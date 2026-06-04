using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

var response = await PluginHost.RunAsync(args, YoutubePlugin.IdentifyAsync);
Console.Write(JsonSerializer.Serialize(response, PluginHost.JsonOptions));

internal static partial class YoutubePlugin {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Random Jitter = new();
    private const int MaxAttempts = 4;
    private const string Provider = "youtube";
    private const string OEmbed = "https://www.youtube.com/oembed";
    private const string InnerTube = "https://www.youtube.com/youtubei/v1";
    private const string InnerTubeKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
    private const string ClientVersion = "2.20240726.00.00";

    public static async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        var kind = request.Entity.Kind;
        if (kind.Equals("music-artist", StringComparison.OrdinalIgnoreCase)) return await IdentifyMusicArtistAsync(request);
        if (kind.Equals("audio-library", StringComparison.OrdinalIgnoreCase)) return await IdentifyAlbumAsync(request);
        if (kind.Equals("audio-track", StringComparison.OrdinalIgnoreCase)) return await IdentifyTrackAsync(request);
        if (!kind.Equals("video", StringComparison.OrdinalIgnoreCase)) {
            return IdentifyPluginResult.None();
        }

        var id = ExternalId(request, Provider) ?? IdFromString(request.Query.Url) ?? FirstUrlId(request.Hints.Urls) ?? IdFromString(request.Hints.FilePath) ?? IdFromString(request.Query.Title) ?? IdFromString(request.Hints.Title) ?? IdFromString(request.Entity.Title);
        if (id is not null && IsExplicitSearch(request)) id = null;
        if (id is not null) {
            return IdentifyPluginResult.ForProposal(await ProposalForIdAsync(id, request.Entity.Kind, "youtube-id"));
        }

        var query = CleanQuery(request.Query.Title ?? request.Hints.Title ?? request.Entity.Title);
        if (string.IsNullOrWhiteSpace(query)) return IdentifyPluginResult.None();

        var candidates = await SearchCandidatesAsync(query);
        return IdentifyPluginResult.ForCandidates(candidates);
    }

    private static bool IsExplicitSearch(IdentifyPluginRequest request) =>
        request.Action.Equals("search", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(request.Query.Title) &&
        string.IsNullOrWhiteSpace(request.Query.Url) &&
        request.Query.ExternalIds is not { Count: > 0 };

    // ===== YouTube Music (WEB_REMIX) audio identification =====
    // YouTube Music exposes a separate InnerTube surface (music.youtube.com) under the WEB_REMIX
    // client. Unlike the standard video player it returns square album-art for songs/art-tracks plus
    // canonical title/artist/album/year — the metadata MusicBrainz provides, but sourced from YouTube
    // (where YouTube-native artists like Divide Music actually live). The files we identify carry no
    // YouTube id, so matching is title + ancestor-artist driven and every accepted row is verified to
    // belong to the requested artist before it is proposed.

    private const string MusicBase = "https://music.youtube.com/youtubei/v1";
    private const string MusicClientVersion = "1.20240724.00.00";
    // Stable InnerTube search filter params: songs, albums, artists.
    private const string SongParams = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D";
    private const string AlbumParams = "EgWKAQIYAWoKEAkQChAFEAMQBA%3D%3D";
    private const string ArtistParams = "EgWKAQIgAWoKEAkQBRAKEAMQBA%3D%3D";

    private static object MusicContext() => new { client = new { clientName = "WEB_REMIX", clientVersion = MusicClientVersion } };

    /// <summary>
    /// Sends an HTTP request, retrying transient throttling (HTTP 429) and server (5xx) responses with
    /// capped exponential backoff plus jitter, honoring a <c>Retry-After</c> header when present. A bulk
    /// artist saturation fires the artist search plus every album browse and track search back-to-back
    /// against InnerTube on a shared anonymous key, which YouTube rate-limits aggressively; without this,
    /// the first throttled response silently became "no results" and the child was dropped. Each attempt
    /// builds a fresh request via the factory (a sent request cannot be replayed). Returns the final
    /// response for the caller to dispose, or null if every attempt threw (e.g. a timeout).
    /// </summary>
    private static async Task<HttpResponseMessage?> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory) {
        for (var attempt = 1; ; attempt++) {
            HttpResponseMessage response;
            try {
                using var request = requestFactory();
                response = await Http.SendAsync(request);
            } catch when (attempt < MaxAttempts) {
                await Task.Delay(Backoff(attempt));
                continue;
            } catch {
                return null;
            }

            var status = (int)response.StatusCode;
            if ((status == 429 || status >= 500) && attempt < MaxAttempts) {
                var delay = RetryAfter(response) ?? Backoff(attempt);
                response.Dispose();
                await Task.Delay(delay);
                continue;
            }

            return response;
        }
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1) + Jitter.Next(0, 250));

    private static TimeSpan? RetryAfter(HttpResponseMessage response) {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } date) {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>Posts an InnerTube payload to a YouTube Music endpoint and returns the parsed root, or null on failure.</summary>
    private static async Task<JsonElement?> MusicPostAsync(string endpoint, object payload) {
        using var res = await SendWithRetryAsync(() => {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{MusicBase}/{endpoint}?key={InnerTubeKey}");
            request.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
            request.Content = JsonContent.Create(payload, options: PluginHost.JsonOptions);
            return request;
        });
        return res is { IsSuccessStatusCode: true } ? await res.Content.ReadFromJsonAsync<JsonElement>(PluginHost.JsonOptions) : null;
    }

    /// <summary>Runs a filtered YouTube Music search and returns the flat list of result-row renderers.</summary>
    private static async Task<IReadOnlyList<JsonElement>> MusicSearchAsync(string query, string filterParams) {
        var root = await MusicPostAsync("search", new { context = MusicContext(), query, @params = filterParams });
        if (root is null) return [];
        var rows = new List<JsonElement>();
        CollectObjects(root.Value, "musicResponsiveListItemRenderer", rows);
        return rows;
    }

    /// <summary>Depth-first collects every object that carries the given property key.</summary>
    private static void CollectObjects(JsonElement node, string key, List<JsonElement> sink) {
        if (node.ValueKind == JsonValueKind.Object) {
            if (node.TryGetProperty(key, out var found)) sink.Add(found);
            foreach (var prop in node.EnumerateObject()) CollectObjects(prop.Value, key, sink);
        } else if (node.ValueKind == JsonValueKind.Array) {
            foreach (var item in node.EnumerateArray()) CollectObjects(item, key, sink);
        }
    }

    private static string? RowVideoId(JsonElement row) {
        var endpoints = new List<JsonElement>();
        CollectObjects(row, "watchEndpoint", endpoints);
        foreach (var endpoint in endpoints) {
            if (endpoint.TryGetProperty("videoId", out var value) && value.ValueKind == JsonValueKind.String && IsValidId(value.GetString())) {
                return value.GetString();
            }
        }
        return null;
    }

    private static string? RowBrowseId(JsonElement row, string prefix) {
        var endpoints = new List<JsonElement>();
        CollectObjects(row, "browseEndpoint", endpoints);
        foreach (var endpoint in endpoints) {
            if (endpoint.TryGetProperty("browseId", out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { } id && id.StartsWith(prefix, StringComparison.Ordinal)) {
                return id;
            }
        }
        return null;
    }

    /// <summary>The concatenated text of each flex column (column 0 is the title; column 1 is the bullet-separated byline).</summary>
    private static IReadOnlyList<string> RowTexts(JsonElement row) {
        var result = new List<string>();
        if (!row.TryGetProperty("flexColumns", out var cols) || cols.ValueKind != JsonValueKind.Array) return result;
        foreach (var col in cols.EnumerateArray()) {
            if (col.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var renderer) &&
                renderer.TryGetProperty("text", out var text) &&
                text.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array) {
                result.Add(string.Concat(runs.EnumerateArray().Select(run => run.TryGetProperty("text", out var t) ? t.GetString() : null)));
            }
        }
        return result;
    }

    private static string[] BylineParts(JsonElement row) {
        var texts = RowTexts(row);
        var byline = texts.Count > 1 ? texts[1] : null;
        return string.IsNullOrWhiteSpace(byline)
            ? []
            : byline.Split('•', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Square cover art for a row, taking the largest thumbnail and adding an upsized variant on top.</summary>
    private static IReadOnlyList<ImageCandidate> RowCoverImages(JsonElement row) {
        var thumbnailArrays = new List<JsonElement>();
        CollectObjects(row, "thumbnails", thumbnailArrays);
        YoutubeThumbnail? biggest = null;
        foreach (var array in thumbnailArrays) {
            if (array.ValueKind != JsonValueKind.Array) continue;
            foreach (var thumb in array.EnumerateArray()) {
                var url = StringAt(thumb, "url");
                if (url is null) continue;
                var width = IntAt(thumb, "width");
                if (biggest is null || (width ?? 0) > (biggest.Width ?? 0)) {
                    biggest = new YoutubeThumbnail(NormalizeUrl(url), width, IntAt(thumb, "height"));
                }
            }
        }
        if (biggest?.Url is null) return [];
        return [
            new ImageCandidate("cover", Upsize(biggest.Url, 1000), Provider, 10, null, 1000, 1000),
            new ImageCandidate("cover", biggest.Url, Provider, 8, null, biggest.Width, biggest.Height)
        ];
    }

    /// <summary>Rewrites a googleusercontent size suffix (=wW-hH or =sN) to request a larger square.</summary>
    private static string Upsize(string url, int size) {
        if (WidthHeightRegex().IsMatch(url)) return WidthHeightRegex().Replace(url, $"=w{size}-h{size}");
        if (SquareSizeRegex().IsMatch(url)) return SquareSizeRegex().Replace(url, $"=s{size}");
        return url;
    }

    private static MusicArtistRow? ParseArtistRow(JsonElement row) {
        var channelId = RowBrowseId(row, "UC");
        if (channelId is null) return null;
        var texts = RowTexts(row);
        var name = texts.Count > 0 ? texts[0] : null;
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new MusicArtistRow(channelId, name!, texts.Count > 1 ? texts[1] : null, RowCoverImages(row));
    }

    private static MusicSongRow? ParseSongRow(JsonElement row) {
        var texts = RowTexts(row);
        var title = texts.Count > 0 ? texts[0] : null;
        if (string.IsNullOrWhiteSpace(title)) return null;
        var parts = BylineParts(row); // [artist, album, duration]
        return new MusicSongRow(
            RowVideoId(row),
            title!,
            parts.Length > 0 ? parts[0] : null,
            parts.Length > 1 ? parts[1] : null,
            parts.Length > 0 ? ParseDuration(parts[^1]) : null,
            RowCoverImages(row));
    }

    private static MusicAlbumRow? ParseAlbumRow(JsonElement row) {
        var browseId = RowBrowseId(row, "MPRE");
        if (browseId is null) return null;
        var texts = RowTexts(row);
        var title = texts.Count > 0 ? texts[0] : null;
        if (string.IsNullOrWhiteSpace(title)) return null;
        var parts = BylineParts(row); // [type, artist, year]
        return new MusicAlbumRow(
            browseId,
            title!,
            parts.Length > 1 ? parts[1] : null,
            parts.Length > 0 ? ParseYear(parts[^1]) : null,
            RowCoverImages(row));
    }

    private static async Task<IdentifyPluginResult> IdentifyMusicArtistAsync(IdentifyPluginRequest request) {
        var query = CleanAudioQuery(request.Query.Title ?? request.Hints.Title ?? request.Entity.Title);
        if (query is null) return IdentifyPluginResult.None();
        var artists = (await MusicSearchAsync(query, ArtistParams)).Select(ParseArtistRow).OfType<MusicArtistRow>().ToList();
        if (artists.Count == 0) return IdentifyPluginResult.None();
        if (IsExplicitSearch(request)) {
            return IdentifyPluginResult.ForCandidates(artists.Select(artist => new EntitySearchCandidate(
                new Dictionary<string, string> { [Provider] = artist.ChannelId, ["youtubeChannel"] = artist.ChannelId },
                artist.Name, null, artist.Subtitle, artist.Images.Count > 0 ? artist.Images[0].Url : null, null)).ToList());
        }
        var best = artists.FirstOrDefault(artist => ArtistMatches(artist.Name, query)) ?? artists[0];
        return IdentifyPluginResult.ForProposal(ArtistProposal(best));
    }

    private static EntityMetadataProposal ArtistProposal(MusicArtistRow artist) {
        var external = new Dictionary<string, string> { [Provider] = artist.ChannelId, ["youtubeChannel"] = artist.ChannelId };
        var urls = new[] { $"https://music.youtube.com/channel/{artist.ChannelId}", ChannelUrl(artist.ChannelId) };
        return new EntityMetadataProposal(
            $"youtube:music:artist:{artist.ChannelId}", Provider, "music-artist", 0.9m, "yt-music-artist",
            new EntityMetadataPatch(artist.Name, null, external, urls, [], null, [],
                new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int>(), null),
            artist.Images, [], [], null, []);
    }

    private static async Task<IdentifyPluginResult> IdentifyTrackAsync(IdentifyPluginRequest request) {
        var artist = AncestorTitle(request, "music-artist");
        var album = AncestorTitle(request, "audio-library");
        var title = CleanAudioQuery(request.Query.Title ?? request.Hints.Title ?? request.Entity.Title);
        if (title is null) return IdentifyPluginResult.None();
        var songs = (await MusicSearchAsync(ScopedQuery(artist, title), SongParams)).Select(ParseSongRow).OfType<MusicSongRow>().ToList();
        if (songs.Count == 0) return IdentifyPluginResult.None();
        if (IsExplicitSearch(request)) {
            return IdentifyPluginResult.ForCandidates(songs.Where(song => song.VideoId is not null).Select(song => new EntitySearchCandidate(
                new Dictionary<string, string> { [Provider] = song.VideoId! },
                song.Title, null, SongOverview(song), song.Images.Count > 0 ? song.Images[0].Url : null, null)).ToList());
        }
        var pool = artist is null ? songs : songs.Where(song => ArtistMatches(song.Artist, artist)).ToList();
        if (pool.Count == 0) return IdentifyPluginResult.None();
        var best = (album is null ? null : pool.FirstOrDefault(song => ArtistMatches(song.Album, album)))
            ?? pool.FirstOrDefault(song => NormalizeName(song.Title) == NormalizeName(title))
            ?? pool[0];
        return IdentifyPluginResult.ForProposal(TrackProposal(best));
    }

    private static EntityMetadataProposal TrackProposal(MusicSongRow song) {
        var external = new Dictionary<string, string>();
        var urls = new List<string>();
        if (song.VideoId is not null) {
            external[Provider] = song.VideoId;
            urls.Add(MusicWatchUrl(song.VideoId));
            urls.Add(VideoUrl(song.VideoId));
        }
        var stats = new Dictionary<string, int>();
        if (song.Seconds is int seconds) stats["runtimeSeconds"] = seconds;
        var credits = new List<CreditPatch>();
        if (!string.IsNullOrWhiteSpace(song.Artist)) credits.Add(new CreditPatch(song.Artist!, "artist", null, 0));
        return new EntityMetadataProposal(
            song.VideoId is not null ? $"youtube:music:song:{song.VideoId}" : $"youtube:music:song:{Slug(song.Title)}",
            Provider, "audio-track", 0.9m, "yt-music-song",
            new EntityMetadataPatch(song.Title, null, external, urls, [], EmptyToNull(song.Album), credits,
                new Dictionary<string, string>(), stats, new Dictionary<string, int>(), null),
            song.Images, [], [], null, []);
    }

    private static async Task<IdentifyPluginResult> IdentifyAlbumAsync(IdentifyPluginRequest request) {
        var artist = AncestorTitle(request, "music-artist");
        var title = CleanAudioQuery(request.Query.Title ?? request.Hints.Title ?? request.Entity.Title);
        if (title is null) return IdentifyPluginResult.None();
        var scoped = ScopedQuery(artist, title);

        var albums = (await MusicSearchAsync(scoped, AlbumParams)).Select(ParseAlbumRow).OfType<MusicAlbumRow>().ToList();
        if (IsExplicitSearch(request)) {
            return IdentifyPluginResult.ForCandidates(albums.Select(album => new EntitySearchCandidate(
                new Dictionary<string, string> { [Provider] = album.BrowseId, ["youtubeAlbum"] = album.BrowseId },
                album.Title, album.Year, album.Artist, album.Images.Count > 0 ? album.Images[0].Url : null, null)).ToList());
        }

        var albumPool = artist is null ? albums : albums.Where(album => ArtistMatches(album.Artist, artist)).ToList();
        var bestAlbum = albumPool.FirstOrDefault(album => NormalizeName(album.Title) == NormalizeName(title))
            ?? (artist is null ? null : albumPool.FirstOrDefault());
        if (bestAlbum is not null) return IdentifyPluginResult.ForProposal(await AlbumProposalAsync(bestAlbum));

        // Singles fallback: YouTube-native artists have no album entry, only the song/art-track. Adopt
        // the matching song's square cover so the on-disk "album" folder still gets its art.
        var songs = (await MusicSearchAsync(scoped, SongParams)).Select(ParseSongRow).OfType<MusicSongRow>().ToList();
        var songPool = artist is null ? songs : songs.Where(song => ArtistMatches(song.Artist, artist)).ToList();
        var bestSong = songPool.FirstOrDefault(song => NormalizeName(song.Title) == NormalizeName(title)) ?? songPool.FirstOrDefault();
        return bestSong is null ? IdentifyPluginResult.None() : IdentifyPluginResult.ForProposal(SingleAlbumProposal(bestSong));
    }

    private static async Task<EntityMetadataProposal> AlbumProposalAsync(MusicAlbumRow album) {
        var external = new Dictionary<string, string> { [Provider] = album.BrowseId, ["youtubeAlbum"] = album.BrowseId };
        var urls = new[] { $"https://music.youtube.com/browse/{album.BrowseId}" };
        var dates = new Dictionary<string, string>();
        if (album.Year is int year) dates["released"] = year.ToString();
        var children = await AlbumTrackChildrenAsync(album.BrowseId);
        return new EntityMetadataProposal(
            $"youtube:music:album:{album.BrowseId}", Provider, "audio-library", 0.9m, "yt-music-album",
            new EntityMetadataPatch(album.Title, null, external, urls, [], EmptyToNull(album.Artist), [],
                dates, new Dictionary<string, int>(), new Dictionary<string, int>(), null),
            album.Images, children, [], null, []);
    }

    private static EntityMetadataProposal SingleAlbumProposal(MusicSongRow song) {
        var external = new Dictionary<string, string>();
        var urls = new List<string>();
        if (song.VideoId is not null) {
            external[Provider] = song.VideoId;
            urls.Add(MusicWatchUrl(song.VideoId));
        }
        return new EntityMetadataProposal(
            song.VideoId is not null ? $"youtube:music:single:{song.VideoId}" : $"youtube:music:single:{Slug(song.Title)}",
            Provider, "audio-library", 0.9m, "yt-music-single",
            new EntityMetadataPatch(EmptyToNull(song.Album) ?? song.Title, null, external, urls, [], EmptyToNull(song.Artist), [],
                new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int>(), null),
            song.Images, [], [], null, []);
    }

    /// <summary>The album's track list as positioned child proposals (mirrors the MusicBrainz cascade), each carrying the album art.</summary>
    private static async Task<IReadOnlyList<EntityMetadataProposal>> AlbumTrackChildrenAsync(string browseId) {
        var root = await MusicPostAsync("browse", new { context = MusicContext(), browseId });
        if (root is null) return [];
        var rows = new List<JsonElement>();
        CollectObjects(root.Value, "musicResponsiveListItemRenderer", rows);
        var children = new List<EntityMetadataProposal>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in rows) {
            var texts = RowTexts(row);
            var title = texts.Count > 0 ? texts[0] : null;
            if (string.IsNullOrWhiteSpace(title)) continue;
            var videoId = RowVideoId(row);
            if (videoId is not null && !seen.Add(videoId)) continue;
            var external = new Dictionary<string, string>();
            var urls = new List<string>();
            if (videoId is not null) {
                external[Provider] = videoId;
                urls.Add(MusicWatchUrl(videoId));
            }
            var patch = new EntityMetadataPatch(title!, null, external, urls, [], null, [],
                new Dictionary<string, string>(), new Dictionary<string, int>(),
                new Dictionary<string, int> { ["sortOrder"] = index }, null);
            children.Add(new EntityMetadataProposal(
                videoId is not null ? $"youtube:music:song:{videoId}" : $"youtube:music:song:{browseId}:{index}",
                Provider, "audio-track", 0.85m, "track-list", patch, RowCoverImages(row), [], [], null, []));
            index++;
        }
        return children;
    }

    private static string ScopedQuery(string? artist, string title) => string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
    private static string MusicWatchUrl(string id) => $"https://music.youtube.com/watch?v={id}";
    private static string? CleanAudioQuery(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Title of the nearest ancestor of the given kind (e.g. the album's artist), used to scope and verify a match.</summary>
    private static string? AncestorTitle(IdentifyPluginRequest request, string kind) {
        foreach (var ancestor in request.StructuralContext?.Ancestors ?? []) {
            if (ancestor.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ancestor.Title)) {
                return ancestor.Title;
            }
        }
        return null;
    }

    private static string NormalizeName(string? value) => value is null ? string.Empty : NameNormRegex().Replace(value.ToLowerInvariant(), string.Empty);

    /// <summary>True when a result's byline artist plausibly equals the expected artist (loose, punctuation-insensitive containment).</summary>
    private static bool ArtistMatches(string? candidate, string expected) {
        var normalizedCandidate = NormalizeName(candidate);
        var normalizedExpected = NormalizeName(expected);
        return normalizedExpected.Length > 0 && normalizedCandidate.Length > 0 &&
            (normalizedCandidate.Contains(normalizedExpected) || normalizedExpected.Contains(normalizedCandidate));
    }

    private static int? ParseDuration(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        var total = 0;
        foreach (var part in parts) {
            if (!int.TryParse(part.Trim(), out var component)) return null;
            total = total * 60 + component;
        }
        return total;
    }

    private static int? ParseYear(string? value) {
        value = value?.Trim();
        return value is { Length: 4 } && int.TryParse(value, out var year) && year is > 1900 and < 2200 ? year : null;
    }

    private static string? SongOverview(MusicSongRow song) {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(song.Artist)) parts.Add(song.Artist!);
        if (!string.IsNullOrWhiteSpace(song.Album)) parts.Add(song.Album!);
        return parts.Count > 0 ? string.Join(" — ", parts) : null;
    }

    private sealed record MusicArtistRow(string ChannelId, string Name, string? Subtitle, IReadOnlyList<ImageCandidate> Images);
    private sealed record MusicSongRow(string? VideoId, string Title, string? Artist, string? Album, int? Seconds, IReadOnlyList<ImageCandidate> Images);
    private sealed record MusicAlbumRow(string BrowseId, string Title, string? Artist, int? Year, IReadOnlyList<ImageCandidate> Images);

    private static async Task<EntityMetadataProposal> ProposalForIdAsync(string id, string targetKind, string reason) {
        var details = await FetchPlayerAsync(id);
        if (details?.VideoDetails is null) {
            return FromOEmbed(id, targetKind, await FetchOEmbedAsync(id), reason);
        }

        var video = details.VideoDetails;
        var micro = details.Microformat?.PlayerMicroformatRenderer;
        var title = EmptyToNull(video.Title) ?? id;
        var urls = new[] { VideoUrl(id) };
        var tags = video.Keywords?.Where(static value => !string.IsNullOrWhiteSpace(value)).Take(20).ToArray() ?? [];
        var images = ThumbnailImages(id, video.Thumbnail?.Thumbnails);
        var stats = new Dictionary<string, int>();
        if (int.TryParse(video.LengthSeconds, out var seconds)) stats["runtimeSeconds"] = seconds;
        if (int.TryParse(video.ViewCount, out var views)) stats["viewCount"] = views;
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(micro?.PublishDate)) dates["published"] = micro.PublishDate!;
        if (!string.IsNullOrWhiteSpace(micro?.UploadDate)) dates["uploaded"] = micro.UploadDate!;
        var external = new Dictionary<string, string> { [Provider] = id };
        if (!string.IsNullOrWhiteSpace(video.ChannelId)) external["youtubeChannel"] = video.ChannelId!;
        var channel = await FetchChannelAsync(video.ChannelId);
        var channelName = EmptyToNull(channel?.Title ?? micro?.OwnerChannelName ?? video.Author);
        var relationships = channelName is null ? [] : new[] { StudioRelationship(channelName, video.ChannelId, channel) };

        return Proposal(targetKind, $"youtube:{id}", reason, new EntityMetadataPatch(
            title,
            EmptyToNull(video.ShortDescription),
            external,
            urls,
            tags,
            channelName,
            [],
            dates,
            stats,
            new Dictionary<string, int>(),
            EmptyToNull(micro?.Category)),
            images,
            relationships);
    }

    private static EntityMetadataProposal FromOEmbed(string id, string targetKind, OEmbedResponse? data, string reason) {
        var channelName = EmptyToNull(data?.AuthorName);
        var relationships = channelName is null ? [] : new[] { StudioRelationship(channelName, null, null) };
        return
        Proposal(targetKind, $"youtube:{id}", reason, new EntityMetadataPatch(
            EmptyToNull(data?.Title) ?? id,
            null,
            new Dictionary<string, string> { [Provider] = id },
            [VideoUrl(id)],
            [],
            channelName,
            [],
            new Dictionary<string, string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            null),
            data?.ThumbnailUrl is null ? StaticImages(id) : [new ImageCandidate("poster", data.ThumbnailUrl, Provider, 10, null, data.ThumbnailWidth, data.ThumbnailHeight), .. StaticImages(id)],
            relationships);
    }

    private static async Task<IReadOnlyList<EntitySearchCandidate>> SearchCandidatesAsync(string query) {
        var payload = new {
            context = new { client = new { clientName = "WEB", clientVersion = ClientVersion } },
            query
        };
        using var res = await SendWithRetryAsync(() =>
            new HttpRequestMessage(HttpMethod.Post, $"{InnerTube}/search?key={InnerTubeKey}") {
                Content = JsonContent.Create(payload, options: PluginHost.JsonOptions)
            });
        if (res is not { IsSuccessStatusCode: true }) return [];
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(PluginHost.JsonOptions);
        var candidates = new List<EntitySearchCandidate>();
        Walk(json, renderer => {
            if (candidates.Count >= 10) return;
            if (!renderer.TryGetProperty("videoId", out var idProp)) return;
            var id = idProp.GetString();
            if (!IsValidId(id)) return;
            var title = TextFromRenderer(renderer);
            if (string.IsNullOrWhiteSpace(title)) return;
            candidates.Add(new EntitySearchCandidate(
                new Dictionary<string, string> { [Provider] = id! },
                title!,
                null,
                null,
                $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
                null));
        });
        return candidates;
    }

    private static void Walk(JsonElement node, Action<JsonElement> onVideoRenderer) {
        if (node.ValueKind == JsonValueKind.Object) {
            if (node.TryGetProperty("videoRenderer", out var renderer)) onVideoRenderer(renderer);
            foreach (var prop in node.EnumerateObject()) Walk(prop.Value, onVideoRenderer);
        } else if (node.ValueKind == JsonValueKind.Array) {
            foreach (var item in node.EnumerateArray()) Walk(item, onVideoRenderer);
        }
    }

    private static string? TextFromRenderer(JsonElement renderer) {
        if (!renderer.TryGetProperty("title", out var title)) return null;
        if (title.TryGetProperty("simpleText", out var simple)) return simple.GetString();
        if (title.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array) {
            return string.Concat(runs.EnumerateArray().Select(run => run.TryGetProperty("text", out var text) ? text.GetString() : null));
        }
        return null;
    }

    private static async Task<PlayerResponse?> FetchPlayerAsync(string id) {
        var payload = new { context = new { client = new { clientName = "WEB", clientVersion = ClientVersion } }, videoId = id };
        using var res = await SendWithRetryAsync(() =>
            new HttpRequestMessage(HttpMethod.Post, $"{InnerTube}/player?key={InnerTubeKey}") {
                Content = JsonContent.Create(payload, options: PluginHost.JsonOptions)
            });
        return res is { IsSuccessStatusCode: true } ? await res.Content.ReadFromJsonAsync<PlayerResponse>(PluginHost.JsonOptions) : null;
    }

    private static async Task<OEmbedResponse?> FetchOEmbedAsync(string id) {
        using var res = await SendWithRetryAsync(() =>
            new HttpRequestMessage(HttpMethod.Get, $"{OEmbed}?url={Uri.EscapeDataString(VideoUrl(id))}&format=json"));
        return res is { IsSuccessStatusCode: true } ? await res.Content.ReadFromJsonAsync<OEmbedResponse>(PluginHost.JsonOptions) : null;
    }

    private static async Task<ChannelMetadata?> FetchChannelAsync(string? channelId) {
        if (string.IsNullOrWhiteSpace(channelId)) return null;

        var payload = new {
            context = new { client = new { clientName = "WEB", clientVersion = ClientVersion } },
            browseId = channelId
        };
        using var res = await SendWithRetryAsync(() =>
            new HttpRequestMessage(HttpMethod.Post, $"{InnerTube}/browse?key={InnerTubeKey}") {
                Content = JsonContent.Create(payload, options: PluginHost.JsonOptions)
            });
        if (res is not { IsSuccessStatusCode: true }) return null;
        var root = await res.Content.ReadFromJsonAsync<JsonElement>(PluginHost.JsonOptions);
        var metadata = TryGet(root, "metadata", "channelMetadataRenderer");

        var title = StringAt(metadata, "title");
        var description = EmptyToNull(StringAt(metadata, "description"));
        var externalId = StringAt(metadata, "externalId") ?? channelId;
        var urls = ChannelUrls(metadata, externalId);
        var images = new List<ImageCandidate>();
        images.AddRange(ImageCandidates(
            "logo",
            Provider,
            ThumbnailSources(
                root,
                ["metadata", "channelMetadataRenderer", "avatar", "thumbnails"],
                ["header", "pageHeaderRenderer", "content", "pageHeaderViewModel", "image", "decoratedAvatarViewModel", "avatar", "avatarViewModel", "image", "sources"])));
        images.AddRange(ImageCandidates(
            "backdrop",
            Provider,
            ThumbnailSources(
                root,
                ["header", "pageHeaderRenderer", "content", "pageHeaderViewModel", "banner", "imageBannerViewModel", "image", "sources"])));

        return new ChannelMetadata(title, description, externalId, urls, images);
    }

    private static IReadOnlyList<ImageCandidate> ThumbnailImages(string id, IReadOnlyList<YoutubeThumbnail>? thumbs) {
        var images = (thumbs ?? []).OrderByDescending(t => t.Width ?? 0)
            .Where(t => !string.IsNullOrWhiteSpace(t.Url))
            .Select((t, i) => new ImageCandidate("poster", t.Url!, Provider, 10 - i, null, t.Width, t.Height))
            .Concat(StaticImages(id))
            .GroupBy(i => i.Url)
            .Select(g => g.First())
            .ToArray();
        return images;
    }

    private static IReadOnlyList<ImageCandidate> StaticImages(string id) => [
        new("poster", $"https://i.ytimg.com/vi/{id}/maxresdefault.jpg", Provider, 9, null, null, null),
        new("poster", $"https://i.ytimg.com/vi/{id}/sddefault.jpg", Provider, 7, null, null, null),
        new("poster", $"https://i.ytimg.com/vi/{id}/hqdefault.jpg", Provider, 5, null, null, null)
    ];

    private static EntityMetadataProposal Proposal(
        string kind,
        string id,
        string reason,
        EntityMetadataPatch patch,
        IReadOnlyList<ImageCandidate> images,
        IReadOnlyList<EntityMetadataProposal>? relationships = null) =>
        new(id, Provider, kind, 0.95m, reason, patch, images, [], [], null, relationships ?? []);

    private static EntityMetadataProposal StudioRelationship(string channelName, string? channelId, ChannelMetadata? channel) {
        var externalIds = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(channel?.ExternalId ?? channelId)) {
            externalIds["youtubeChannel"] = (channel?.ExternalId ?? channelId)!;
        }

        return new EntityMetadataProposal(
            channel?.ExternalId is null ? $"youtube:channel:{Slug(channelName)}" : $"youtube:channel:{channel.ExternalId}",
            Provider,
            "studio",
            0.95m,
            "youtube-channel",
            new EntityMetadataPatch(
                channelName,
                channel?.Description,
                externalIds,
                channel?.Urls ?? [],
                [],
                null,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, int>(),
                new Dictionary<string, int>(),
                null),
            channel?.Images ?? [],
            [],
            []);
    }

    private static string? ExternalId(IdentifyPluginRequest request, string key) {
        foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) {
            if (ids is not null && ids.TryGetValue(key, out var value) && IsValidId(value)) return value;
        }
        return null;
    }

    private static string? FirstUrlId(IReadOnlyList<string> urls) => urls.Select(IdFromString).FirstOrDefault(id => id is not null);
    private static string VideoUrl(string id) => $"https://www.youtube.com/watch?v={id}";
    private static string ChannelUrl(string id) => $"https://www.youtube.com/channel/{id}";
    private static bool IsValidId(string? value) => value is not null && IdRegex().IsMatch(value);
    private static string? IdFromString(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();
        if (IsValidId(value)) return value;
        foreach (Match match in UrlRegex().Matches(value)) if (IdFromUrl(match.Value) is { } urlId) return urlId;
        var brackets = BracketRegex().Matches(value);
        for (var i = brackets.Count - 1; i >= 0; i--) {
            var candidate = WhitespaceRegex().Replace(brackets[i].Groups[1].Value, "_");
            if (IsValidId(candidate)) return candidate;
        }
        return null;
    }
    private static string? IdFromUrl(string raw) {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) {
            var segment = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
            return IsValidId(segment) ? segment : null;
        }
        if (!host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)) return null;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var id = query.Get("v");
        if (IsValidId(id)) return id;
        var match = YoutubePathRegex().Match(uri.AbsolutePath);
        return match.Success && IsValidId(match.Groups[2].Value) ? match.Groups[2].Value : null;
    }
    private static string? CleanQuery(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var q = BracketSuffixRegex().Replace(value, "");
        q = Path.GetFileNameWithoutExtension(q);
        return q.Trim().Length >= 2 ? q.Trim() : null;
    }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Slug(string value) => SlugRegex().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');

    private static JsonElement? TryGet(JsonElement element, params string[] path) {
        var current = element;
        foreach (var segment in path) {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) {
                return null;
            }
        }

        return current;
    }

    private static string? StringAt(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } value &&
        value.TryGetProperty(property, out var propertyValue) &&
        propertyValue.ValueKind == JsonValueKind.String
            ? EmptyToNull(propertyValue.GetString())
            : null;

    private static IReadOnlyList<string> ChannelUrls(JsonElement? metadata, string channelId) {
        var urls = new List<string> { ChannelUrl(channelId) };
        if (StringAt(metadata, "channelUrl") is { } channelUrl) urls.Add(channelUrl);
        if (StringAt(metadata, "vanityChannelUrl") is { } vanityUrl) urls.Add(vanityUrl);
        if (metadata is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty("ownerUrls", out var ownerUrls) &&
            ownerUrls.ValueKind == JsonValueKind.Array) {
            urls.AddRange(ownerUrls.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(url => !string.IsNullOrWhiteSpace(url))!);
        }

        return urls
            .Select(NormalizeUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<YoutubeThumbnail> ThumbnailSources(JsonElement root, params string[][] paths) {
        var result = new List<YoutubeThumbnail>();
        foreach (var path in paths) {
            if (TryGet(root, path) is not { ValueKind: JsonValueKind.Array } items) {
                continue;
            }

            foreach (var item in items.EnumerateArray()) {
                var url = StringAt(item, "url");
                if (url is null) continue;
                result.Add(new YoutubeThumbnail(NormalizeUrl(url), IntAt(item, "width"), IntAt(item, "height")));
            }
        }

        return result
            .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => (item.Width ?? 0) * (item.Height ?? 0))
            .ToArray();
    }

    private static IReadOnlyList<ImageCandidate> ImageCandidates(string kind, string source, IReadOnlyList<YoutubeThumbnail> thumbnails) =>
        thumbnails.Select((thumbnail, index) => new ImageCandidate(
                kind,
                thumbnail.Url!,
                source,
                Math.Max(1, 10 - index),
                null,
                thumbnail.Width,
                thumbnail.Height))
            .ToArray();

    private static int? IntAt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var propertyValue) &&
        propertyValue.TryGetInt32(out var value)
            ? value
            : null;

    private static string NormalizeUrl(string url) =>
        url.StartsWith("//", StringComparison.Ordinal) ? $"https:{url}" : url;

    [GeneratedRegex("^[a-zA-Z0-9_-]{11}$")] private static partial Regex IdRegex();
    [GeneratedRegex("https?://[^\\s\"'<>]+", RegexOptions.IgnoreCase)] private static partial Regex UrlRegex();
    [GeneratedRegex("\\[([^\\]]+)\\]")] private static partial Regex BracketRegex();
    [GeneratedRegex("\\s+")] private static partial Regex WhitespaceRegex();
    [GeneratedRegex("/(embed|shorts|live|v)/([a-zA-Z0-9_-]{11})", RegexOptions.IgnoreCase)] private static partial Regex YoutubePathRegex();
    [GeneratedRegex("\\s*\\[[^\\]]+\\]\\s*$")] private static partial Regex BracketSuffixRegex();
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex SlugRegex();
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NameNormRegex();
    [GeneratedRegex("=w\\d+-h\\d+")] private static partial Regex WidthHeightRegex();
    [GeneratedRegex("=s\\d+")] private static partial Regex SquareSizeRegex();

    private sealed record ChannelMetadata(string? Title, string? Description, string ExternalId, IReadOnlyList<string> Urls, IReadOnlyList<ImageCandidate> Images);
    private sealed record PlayerResponse(VideoDetails? VideoDetails, Microformat? Microformat);
    private sealed record VideoDetails(string? VideoId, string? Title, string? LengthSeconds, string[]? Keywords, string? ChannelId, string? ShortDescription, ThumbnailBag? Thumbnail, string? Author, string? ViewCount);
    private sealed record ThumbnailBag(YoutubeThumbnail[]? Thumbnails);
    private sealed record YoutubeThumbnail(string? Url, int? Width, int? Height);
    private sealed record Microformat(PlayerMicroformatRenderer? PlayerMicroformatRenderer);
    private sealed record PlayerMicroformatRenderer(string? PublishDate, string? UploadDate, string? Category, string? OwnerChannelName);
    private sealed record OEmbedResponse(string? Title, string? AuthorName, string? ThumbnailUrl, int? ThumbnailWidth, int? ThumbnailHeight);
}

internal static class PluginHost {
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = false };
    public static async Task<IdentifyPluginResponse> RunAsync(string[] args, Func<IdentifyPluginRequest, Task<IdentifyPluginResult>> identify) {
        try {
            if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0])) return new(false, null, "Missing request JSON path.");
            var json = await File.ReadAllTextAsync(args[0]);
            var request = JsonSerializer.Deserialize<IdentifyPluginRequest>(json, JsonOptions);
            if (request is null) return new(false, null, "Request JSON was empty or invalid.");
            return new(true, await identify(request), null);
        } catch (Exception ex) { return new(false, null, ex.Message); }
    }
}

internal sealed record IdentifyPluginRequest(int ProtocolVersion, string Action, IReadOnlyDictionary<string, string> Auth, IdentifyEntitySnapshot Entity, IdentifyQuery Query, IdentifyMatchHints Hints, IdentifyStructuralContext? StructuralContext = null);
internal sealed record IdentifyStructuralContext(IReadOnlyList<IdentifyEntitySnapshot> Ancestors, IReadOnlyDictionary<string, int> Positions);
internal sealed record IdentifyEntitySnapshot(Guid Id, string Kind, string Title, IReadOnlyDictionary<string, string>? ExternalIds = null, IReadOnlyList<string>? Urls = null);
internal sealed record IdentifyQuery(string? Title, string? Url, IReadOnlyDictionary<string, string>? ExternalIds, bool? RequireChoice = null);
internal sealed record IdentifyMatchHints(IReadOnlyDictionary<string, string> ExternalIds, IReadOnlyList<string> Urls, string? Title, string? FilePath);
internal sealed record ImageCandidate(string Kind, string Url, string Source, decimal? Rank, string? Language, int? Width, int? Height);
internal sealed record EntitySearchCandidate(IReadOnlyDictionary<string, string> ExternalIds, string Title, int? Year, string? Overview, string? PosterUrl, decimal? Popularity);
internal sealed record CreditPatch(string Name, string Role, string? Character, int? SortOrder);
internal sealed record EntityMetadataFlagsPatch(bool? IsFavorite, bool? IsNsfw, bool? IsOrganized);
internal sealed record EntityMetadataPatch(string? Title, string? Description, IReadOnlyDictionary<string, string> ExternalIds, IReadOnlyList<string> Urls, IReadOnlyList<string> Tags, string? Studio, IReadOnlyList<CreditPatch> Credits, IReadOnlyDictionary<string, string> Dates, IReadOnlyDictionary<string, int> Stats, IReadOnlyDictionary<string, int> Positions, string? Classification) { public int? Rating { get; init; } public EntityMetadataFlagsPatch? Flags { get; init; } }
internal sealed record EntityMetadataProposal(string ProposalId, string Provider, string TargetKind, decimal? Confidence, string? MatchReason, EntityMetadataPatch Patch, IReadOnlyList<ImageCandidate> Images, IReadOnlyList<EntityMetadataProposal> Children, IReadOnlyList<EntitySearchCandidate> Candidates, Guid? TargetEntityId = null, IReadOnlyList<EntityMetadataProposal>? Relationships = null);
internal sealed record IdentifyPluginResult(string Type, EntityMetadataProposal? Proposal, IReadOnlyList<EntitySearchCandidate> Candidates) { public static IdentifyPluginResult ForProposal(EntityMetadataProposal proposal) => new("proposal", proposal, []); public static IdentifyPluginResult ForCandidates(IReadOnlyList<EntitySearchCandidate> candidates) => new("candidates", null, candidates); public static IdentifyPluginResult None() => new("none", null, []); }
internal sealed record IdentifyPluginResponse(bool Ok, IdentifyPluginResult? Result, string? Error);
