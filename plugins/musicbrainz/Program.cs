using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var response = await PluginHost.RunAsync(args, MusicBrainzPlugin.IdentifyAsync);
Console.Write(JsonSerializer.Serialize(response, PluginHost.JsonOptions));

internal static partial class MusicBrainzPlugin {
    internal static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string PluginId = "musicbrainz";
    private const string PrimaryIdentityNamespace = "musicbrainz";
    private const string ArtistIdentityNamespace = "musicbrainzartist";
    private const string ReleaseIdentityNamespace = "musicbrainzrelease";
    private const string ReleaseGroupIdentityNamespace = "musicbrainzreleasegroup";
    private const string RecordingIdentityNamespace = "musicbrainzrecording";
    private const string MbBase = "https://musicbrainz.org/ws/2";
    private const string CoverBase = "https://coverartarchive.org";
    private const string CoverThumb = "front-250";
    private static class SearchFields {
        public const string Title = "title";
        public const string Artist = "artist";
        public const string Country = "country";
        public const string StartYear = "startYear";
        public const string Year = "year";
        public const string Album = "album";
    }
    // MusicBrainz asks for roughly one request per second; pace a little slower to be safe.
    internal static TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromMilliseconds(1100);
    private static readonly string RateLimitPath = Path.Combine(Path.GetTempPath(), "prismedia-musicbrainz.ratelimit");

    static MusicBrainzPlugin() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Prismedia-MusicBrainz-Plugin/1.0 (https://github.com/pauljoda/prismedia)");

    public static async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        var kind = request.Entity.Kind;
        if (kind.Equals("music-artist", StringComparison.OrdinalIgnoreCase)) return await IdentifyArtistAsync(request);
        if (kind.Equals("audio-library", StringComparison.OrdinalIgnoreCase)) return await IdentifyReleaseAsync(request);
        if (kind.Equals("audio-track", StringComparison.OrdinalIgnoreCase)) return await IdentifyRecordingAsync(request);
        return IdentifyPluginResult.None();
    }

    private static async Task<IdentifyPluginResult> IdentifyArtistAsync(IdentifyPluginRequest request) {
        var id = ExternalId(request) ?? ArtistIdFromUrl(request.Query.Url) ?? FirstUrlId(request.Hints.Urls, ArtistIdFromUrl);
        if (id is not null && !IsExplicitSearch(request)) return IdentifyPluginResult.ForProposal(await ArtistProposalAsync(id, "external-id", request.IncludeStructuralChildren));
        var query = SearchTitle(request);
        if (string.IsNullOrWhiteSpace(query)) return IdentifyPluginResult.None();
        var artists = (await SearchAsync<SearchArtistResponse>("artist", BuildArtistSearch(request, query), SearchLimit(request)))?.Artists ?? [];
        // The artist search response carries no images, so enrich each candidate's thumbnail from its
        // url-rels (the same direct-image source the full proposal uses). Bound to the top results so a
        // broad search does not fan out into many extra rate-limited lookups.
        var candidates = new EntitySearchCandidate[artists.Length];
        for (var i = 0; i < artists.Length; i++) {
            var artist = artists[i];
            var thumb = i < ArtistThumbnailLimit ? await ArtistThumbnailAsync(artist.Id) : null;
            candidates[i] = new EntitySearchCandidate(
                new Dictionary<string, string> {
                    [ArtistIdentityNamespace] = artist.Id,
                    [PrimaryIdentityNamespace] = artist.Id
                },
                artist.Name ?? artist.Id,
                Year(artist.LifeSpan?.Begin),
                ArtistOverview(artist),
                thumb,
                Score(artist.Score));
        }

        return IdentifyPluginResult.ForCandidates(candidates);
    }

    /// <summary>Number of top artist candidates enriched with a thumbnail (each costs one extra lookup).</summary>
    private const int ArtistThumbnailLimit = 5;

    /// <summary>Resolves a band/artist thumbnail by reading the artist's direct-image url-rels.</summary>
    private static async Task<string?> ArtistThumbnailAsync(string id) {
        var artist = await GetJsonAsync<MbArtist>($"{MbBase}/artist/{id}?inc=url-rels&fmt=json");
        return DirectImageUrls(artist?.Relations).FirstOrDefault();
    }

    private static async Task<EntityMetadataProposal> ArtistProposalAsync(string id, string reason, bool includeChildren = false) {
        var artist = await GetJsonAsync<MbArtist>($"{MbBase}/artist/{id}?inc=artist-rels+genres+tags+url-rels&fmt=json");
        var tags = Tags(artist?.Genres, artist?.Tags);
        // "member of band" relations describe the band's people. Each becomes both a credit on the
        // band patch (so apply links the person with their instruments/role as the relationship role)
        // and a reviewable person relationship proposal (so the review page shows a Performers section
        // with photos, exactly like a series lists its cast). One entry per distinct member.
        var memberRelations = (artist?.Relations ?? [])
            .Where(relation => string.Equals(relation.Type, "member of band", StringComparison.OrdinalIgnoreCase) &&
                               !string.IsNullOrWhiteSpace(relation.Artist?.Name))
            .GroupBy(relation => relation.Artist!.Name!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var members = memberRelations
            .Select((relation, index) => new CreditPatch(relation.Artist!.Name!, MemberRole(relation.Attributes), null, index))
            .ToArray();
        var memberProposals = memberRelations
            .Select(MemberRelationship)
            .Where(member => member is not null)
            .Select(member => member!)
            .ToArray();

        var urls = new List<string> { $"https://musicbrainz.org/artist/{id}" };
        foreach (var relation in artist?.Relations ?? []) {
            if (relation.Url?.Resource is { Length: > 0 } resource && !urls.Contains(resource)) urls.Add(resource);
        }

        var images = DirectImageUrls(artist?.Relations)
            .Select((url, index) => new ImageCandidate("cover", url, PluginId, 10 - index, null, null, null))
            .ToArray();

        // Structural children (the artist's albums) are enumerated only on request: the host asks for
        // them when a container is being reviewed (an artist request surfacing its discography), and
        // skips the extra rate-limited call for plain identify lookups.
        var albumChildren = includeChildren ? await AlbumChildrenAsync(id) : [];

        return Proposal("music-artist", $"musicbrainz:artist:{id}", reason, new EntityMetadataPatch(
            artist?.Name ?? id,
            ArtistOverview(artist),
            new Dictionary<string, string> { [PrimaryIdentityNamespace] = id, [ArtistIdentityNamespace] = id },
            urls,
            tags,
            null,
            members,
            new Dictionary<string, string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            artist?.Type), images, memberProposals, albumChildren);
    }

    /// <summary>
    /// Browses the artist's studio discography as album child proposals, keyed by release-group id —
    /// the stable "the album" identity across its many per-country/per-edition releases. Secondary-typed
    /// groups (compilations, live, remixes) are skipped so the list reads like the artist's core
    /// discography. Release lookups accept a release-group id and resolve it to a concrete release.
    /// </summary>
    /// <summary>Resolves a release-group id to its earliest release id, or null when the id isn't a release group either.</summary>
    private static async Task<string?> ResolveReleaseGroupToReleaseAsync(string id) {
        var group = await GetJsonAsync<ReleaseGroupDetail>($"{MbBase}/release-group/{id}?inc=releases&fmt=json");
        return (group?.Releases ?? [])
            .Where(release => !string.IsNullOrWhiteSpace(release.Id))
            .OrderBy(release => string.IsNullOrWhiteSpace(release.Date) ? "9999" : release.Date, StringComparer.Ordinal)
            .Select(release => release.Id)
            .FirstOrDefault();
    }

    private static async Task<IReadOnlyList<EntityMetadataProposal>> AlbumChildrenAsync(string artistId) {
        var browse = await GetJsonAsync<BrowseReleaseGroupsResponse>(
            $"{MbBase}/release-group?artist={artistId}&limit=100&fmt=json");
        var groups = (browse?.ReleaseGroups ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group.Id))
            .Where(group => group.PrimaryType is { } type &&
                (type.Equals("Album", StringComparison.OrdinalIgnoreCase) || type.Equals("EP", StringComparison.OrdinalIgnoreCase)))
            .Where(group => group.SecondaryTypes is not { Length: > 0 })
            .OrderBy(group => group.FirstReleaseDate ?? "9999", StringComparer.Ordinal)
            .ToArray();

        var children = new List<EntityMetadataProposal>(groups.Length);
        foreach (var group in groups) {
            var dates = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(group.FirstReleaseDate)) dates["released"] = group.FirstReleaseDate!;
            var patch = new EntityMetadataPatch(
                group.Title ?? group.Id,
                null,
                new Dictionary<string, string> { [PrimaryIdentityNamespace] = group.Id, [ReleaseGroupIdentityNamespace] = group.Id },
                [$"https://musicbrainz.org/release-group/{group.Id}"],
                [], null, [], dates,
                new Dictionary<string, int>(), new Dictionary<string, int>(),
                group.PrimaryType);
            // Cover Art Archive serves release-group front art directly; a missing cover 404s and the
            // host renders its placeholder, so no per-album existence check is spent here.
            var cover = new ImageCandidate("cover", $"{CoverBase}/release-group/{group.Id}/{CoverThumb}", PluginId, 5, null, null, null);
            children.Add(new EntityMetadataProposal(
                $"musicbrainz:release-group:{group.Id}", PluginId, "audio-library", 0.9m, "album-list",
                patch, [cover], [], [], null, []));
        }

        return children;
    }

    /// <summary>The member's instruments/roles form the relationship role label (e.g. "lead vocals, guitar").</summary>
    private static string MemberRole(string[]? attributes) => AttributesLabel(attributes) ?? "member";

    /// <summary>
    /// Builds a reviewable person relationship proposal for one band member, mirroring how the TMDB
    /// plugin surfaces cast (name + provider id/url). No portrait lookup is performed: MusicBrainz
    /// almost never carries a person image, so a per-member lookup would add a rate-limited request
    /// each for no payoff. A member's photo is filled in if the user later identifies that person.
    /// </summary>
    private static EntityMetadataProposal? MemberRelationship(Relation relation) {
        var name = relation.Artist?.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var memberId = relation.Artist?.Id;
        var externalIds = new Dictionary<string, string>();
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(memberId) && IsGuid(memberId!)) {
            externalIds[PrimaryIdentityNamespace] = memberId!;
            externalIds[ArtistIdentityNamespace] = memberId!;
            urls.Add($"https://musicbrainz.org/artist/{memberId}");
        }

        var patch = new EntityMetadataPatch(
            name, null, externalIds, urls, [], null, [],
            new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int>(), null);
        return new EntityMetadataProposal(
            $"musicbrainz:artist:{memberId ?? name}", PluginId, "person", null, "cascade", patch, [], [], [], null, []);
    }

    private static async Task<IdentifyPluginResult> IdentifyReleaseAsync(IdentifyPluginRequest request) {
        var id = ExternalId(request) ?? ReleaseIdFromUrl(request.Query.Url) ?? FirstUrlId(request.Hints.Urls, ReleaseIdFromUrl);
        if (id is not null && !IsExplicitSearch(request)) return IdentifyPluginResult.ForProposal(await ReleaseProposalAsync(id, "external-id"));
        var query = SearchTitle(request);
        if (string.IsNullOrWhiteSpace(query)) return IdentifyPluginResult.None();

        // Parent-context auto-match: resolve the album within the identified artist's releases.
        if (!IsExplicitSearch(request) && AncestorMusicBrainzId(request, "music-artist") is { } artistId) {
            var scoped = await SearchAsync<SearchReleaseResponse>("release", $"arid:{artistId} AND release:{Quote(CleanTitle(query))}", 5);
            if (scoped?.Releases?.FirstOrDefault() is { } match) return IdentifyPluginResult.ForProposal(await ReleaseProposalAsync(match.Id, "parent-context"));
        }

        var releases = await SearchAsync<SearchReleaseResponse>("release", BuildReleaseSearch(request, query), SearchLimit(request));
        var candidates = (releases?.Releases ?? []).Select(release => new EntitySearchCandidate(
            new Dictionary<string, string> {
                [ReleaseIdentityNamespace] = release.Id,
                [PrimaryIdentityNamespace] = release.Id
            },
            release.Title ?? release.Id,
            Year(release.Date),
            ReleaseOverview(release),
            CoverThumbUrl(release),
            Score(release.Score))).ToArray();
        return IdentifyPluginResult.ForCandidates(candidates);
    }

    private static async Task<IdentifyPluginResult> IdentifyRecordingAsync(IdentifyPluginRequest request) {
        var id = ExternalId(request) ?? RecordingIdFromUrl(request.Query.Url) ?? FirstUrlId(request.Hints.Urls, RecordingIdFromUrl);
        if (id is not null && !IsExplicitSearch(request)) return IdentifyPluginResult.ForProposal(await RecordingProposalAsync(id, "external-id"));
        var query = SearchTitle(request);
        if (string.IsNullOrWhiteSpace(query)) return IdentifyPluginResult.None();

        // Parent-context auto-match: resolve the track within the identified album's recordings.
        if (!IsExplicitSearch(request) && AncestorMusicBrainzId(request, "audio-library") is { } releaseId) {
            var scoped = await SearchAsync<SearchRecordingResponse>("recording", $"reid:{releaseId} AND recording:{Quote(CleanTitle(query))}", 5);
            if (scoped?.Recordings?.FirstOrDefault() is { } match) return IdentifyPluginResult.ForProposal(await RecordingProposalAsync(match.Id, "parent-context"));
        }

        var recordings = await SearchAsync<SearchRecordingResponse>("recording", BuildRecordingSearch(request, query), SearchLimit(request));
        var candidates = (recordings?.Recordings ?? []).Select(recording => new EntitySearchCandidate(
            new Dictionary<string, string> {
                [RecordingIdentityNamespace] = recording.Id,
                [PrimaryIdentityNamespace] = recording.Id
            },
            recording.Title ?? recording.Id,
            Year(recording.FirstReleaseDate),
            RecordingOverview(recording),
            PrimaryRelease(recording.Releases)?.Id is { Length: > 0 } releaseId ? $"{CoverBase}/release/{releaseId}/{CoverThumb}" : null,
            Score(recording.Score))).ToArray();
        return IdentifyPluginResult.ForCandidates(candidates);
    }

    private static async Task<EntityMetadataProposal> ReleaseProposalAsync(
        string id,
        string reason,
        string? requestedReleaseGroupId = null) {
        var release = await GetJsonAsync<Release>($"{MbBase}/release/{id}?inc=artists+labels+tags+genres+release-groups+recordings&fmt=json");
        if (release is null && await ResolveReleaseGroupToReleaseAsync(id) is { } releaseId) {
            // The id was a release-group (the stable album identity an artist's discography is keyed
            // by); resolve it to its earliest concrete release and look that up instead.
            return await ReleaseProposalAsync(releaseId, reason, id);
        }
        var images = await CoverImagesAsync(release?.ReleaseGroup?.Id, id);
        var tags = Tags(release?.Genres, release?.Tags);
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(release?.Date)) dates["released"] = release!.Date!;
        var stats = new Dictionary<string, int>();
        if (release?.Media is { Length: > 0 }) stats["discCount"] = release.Media.Length;
        var externalIds = new Dictionary<string, string> {
            [PrimaryIdentityNamespace] = requestedReleaseGroupId ?? id,
            [ReleaseIdentityNamespace] = id
        };
        if (requestedReleaseGroupId is not null) {
            externalIds[ReleaseGroupIdentityNamespace] = requestedReleaseGroupId;
        } else if (!string.IsNullOrWhiteSpace(release?.ReleaseGroup?.Id)) {
            externalIds[ReleaseGroupIdentityNamespace] = release!.ReleaseGroup!.Id!;
        }
        var urls = new List<string>();
        if (requestedReleaseGroupId is not null) urls.Add($"https://musicbrainz.org/release-group/{requestedReleaseGroupId}");
        urls.Add($"https://musicbrainz.org/release/{id}");
        var proposalId = requestedReleaseGroupId is null
            ? $"musicbrainz:release:{id}"
            : $"musicbrainz:release-group:{requestedReleaseGroupId}";
        return Proposal("audio-library", proposalId, reason, new EntityMetadataPatch(
            release?.Title ?? id,
            release?.Disambiguation,
            externalIds,
            urls,
            tags,
            LabelName(release?.LabelInfo),
            ArtistCreditString(release?.ArtistCredit) is { Length: > 0 } artist ? [new CreditPatch(artist, "artist", null, 0)] : [],
            dates,
            stats,
            new Dictionary<string, int>(),
            release?.ReleaseGroup?.PrimaryType), images, children: TrackChildren(release));
    }

    /// <summary>
    /// Builds the album's track list as child proposals so the host can bind each local track to its
    /// release track <em>by position</em> and adopt the canonical title — matching by track order
    /// rather than the (often messy) on-disk filename. Tracks are ordered the same way the audio scan
    /// orders them: media in disc order, then tracks in position order, with a single album-global
    /// 0-based index that lines up with the local track's sort order. Tracks with no local file are
    /// dropped by the host (this reason is not "provider-tree"), so no phantom tracks are invented.
    /// </summary>
    private static IReadOnlyList<EntityMetadataProposal> TrackChildren(Release? release) {
        var children = new List<EntityMetadataProposal>();
        var globalIndex = 0;
        foreach (var medium in (release?.Media ?? []).OrderBy(medium => medium.Position ?? 0)) {
            foreach (var track in (medium.Tracks ?? []).OrderBy(track => track.Position ?? 0)) {
                var title = string.IsNullOrWhiteSpace(track.Title) ? track.Recording?.Title : track.Title;
                if (string.IsNullOrWhiteSpace(title)) { globalIndex++; continue; }

                var recordingId = track.Recording?.Id;
                var externalIds = new Dictionary<string, string>();
                var urls = new List<string>();
                if (!string.IsNullOrWhiteSpace(recordingId)) {
                    externalIds[PrimaryIdentityNamespace] = recordingId!;
                    externalIds[RecordingIdentityNamespace] = recordingId!;
                    urls.Add($"https://musicbrainz.org/recording/{recordingId}");
                }

                var runtime = track.Length is int ms
                    ? new Dictionary<string, int> { ["runtimeSeconds"] = ms / 1000 }
                    : new Dictionary<string, int>();
                var patch = new EntityMetadataPatch(
                    title, null, externalIds, urls, [], null, [],
                    new Dictionary<string, string>(), runtime,
                    new Dictionary<string, int> { ["sortOrder"] = globalIndex }, null);
                children.Add(new EntityMetadataProposal(
                    recordingId is { Length: > 0 } ? $"musicbrainz:recording:{recordingId}" : $"musicbrainz:track:{release!.Id}:{globalIndex}",
                    PluginId, "audio-track", 0.9m, "track-list", patch, [], [], [], null, []));
                globalIndex++;
            }
        }

        return children;
    }

    private static async Task<EntityMetadataProposal> RecordingProposalAsync(string id, string reason) {
        var recording = await GetJsonAsync<Recording>($"{MbBase}/recording/{id}?inc=artists+releases+release-groups&fmt=json");
        var release = PrimaryRelease(recording?.Releases);
        var releaseId = release?.Id;
        var images = releaseId is null ? [] : await CoverImagesAsync(release?.ReleaseGroup?.Id, releaseId);
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(recording?.FirstReleaseDate ?? release?.Date)) dates["released"] = recording?.FirstReleaseDate ?? release!.Date!;
        var stats = new Dictionary<string, int>();
        if (recording?.Length is int ms) stats["runtimeSeconds"] = ms / 1000;
        var urls = new List<string> { $"https://musicbrainz.org/recording/{id}" };
        if (releaseId is not null) urls.Add($"https://musicbrainz.org/release/{releaseId}");
        var external = new Dictionary<string, string> { [PrimaryIdentityNamespace] = id, [RecordingIdentityNamespace] = id };
        if (releaseId is not null) external[ReleaseIdentityNamespace] = releaseId;
        return Proposal("audio-track", $"musicbrainz:recording:{id}", reason, new EntityMetadataPatch(
            recording?.Title ?? id,
            recording?.Disambiguation,
            external,
            urls,
            [],
            release?.Title,
            ArtistCreditString(recording?.ArtistCredit) is { Length: > 0 } artist ? [new CreditPatch(artist, "artist", null, 0)] : [],
            dates,
            stats,
            new Dictionary<string, int>(),
            null), images);
    }

    /// <summary>
    /// Resolves cover art for an album. The Cover Art Archive frequently stores art only at the
    /// release-group level (or on a different release within the group), so a lookup keyed on a
    /// specific release id can come back empty even when the album clearly has a cover. Prefer the
    /// release group and fall back to the individual release.
    /// </summary>
    private static async Task<IReadOnlyList<ImageCandidate>> CoverImagesAsync(string? releaseGroupId, string releaseId) {
        if (!string.IsNullOrWhiteSpace(releaseGroupId)) {
            var groupImages = await CoverImagesFromAsync($"{CoverBase}/release-group/{releaseGroupId}");
            if (groupImages.Count > 0) return groupImages;
        }

        return await CoverImagesFromAsync($"{CoverBase}/release/{releaseId}");
    }

    private static async Task<IReadOnlyList<ImageCandidate>> CoverImagesFromAsync(string url) {
        try {
            var data = await GetJsonAsync<CoverArtResponse>(url);
            return (data?.Images ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Image)).Select((i, index) => new ImageCandidate("cover", i.Image!, "coverartarchive", i.Front == true ? 10 : 4 - index, null, null, null)).ToArray();
        } catch { return []; }
    }

    private static async Task<T?> SearchAsync<T>(string entity, string query, int limit) =>
        await GetJsonAsync<T>($"{MbBase}/{entity}/?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}");

    internal static int SearchLimit(IdentifyPluginRequest request) => Math.Clamp(request.Query.Limit ?? 10, 1, 25);

    private static async Task<T?> GetJsonAsync<T>(string url) {
        var isMusicBrainz = url.StartsWith(MbBase, StringComparison.OrdinalIgnoreCase);
        // MusicBrainz answers with 503 (and occasionally 429) when its rate limit is brushed, and the
        // network itself can hiccup. Retry transient failures rather than degrading the lookup to an
        // empty result — an empty release/recording would otherwise fall back to the raw id for the
        // title and drop the rest of the metadata. Each attempt re-throttles, so retries are paced.
        for (var attempt = 0; ; attempt++) {
            if (isMusicBrainz) {
                await ThrottleAsync();
            }

            try {
                using var res = await Http.GetAsync(url);
                if (res.IsSuccessStatusCode) {
                    return await res.Content.ReadFromJsonAsync<T>(PluginHost.JsonOptions);
                }

                if (!IsTransientStatus(res.StatusCode) || attempt >= MaxRetries) {
                    return default;
                }
            } catch (HttpRequestException) when (attempt < MaxRetries) {
            } catch (TaskCanceledException) when (attempt < MaxRetries) {
            }
        }
    }

    private const int MaxRetries = 3;

    private static bool IsTransientStatus(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.GatewayTimeout
            or System.Net.HttpStatusCode.RequestTimeout;

    /// <summary>
    /// Paces MusicBrainz requests to stay within the provider's rate limit. Identify cascades spawn
    /// a separate plugin process per entity, so pacing is coordinated <em>across</em> processes via
    /// an exclusively-locked timestamp file: each call reserves the next free time slot (at least
    /// <see cref="MinRequestInterval"/> after the previous reservation), then waits for it. This
    /// keeps a whole artist→albums→tracks cascade under the limit without any host-side coupling.
    /// </summary>
    private static async Task ThrottleAsync() {
        if (MinRequestInterval <= TimeSpan.Zero) return;
        long slotTicks = DateTime.UtcNow.Ticks;
        for (var attempt = 0; ; attempt++) {
            try {
                using (var fs = new FileStream(RateLimitPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                    using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, false, 64, leaveOpen: true);
                    var lastTicks = long.TryParse((await reader.ReadToEndAsync()).Trim(), out var parsed) ? parsed : 0L;
                    slotTicks = Math.Max(DateTime.UtcNow.Ticks, lastTicks + MinRequestInterval.Ticks);
                    fs.SetLength(0);
                    fs.Position = 0;
                    await fs.WriteAsync(System.Text.Encoding.UTF8.GetBytes(slotTicks.ToString()));
                }

                break;
            } catch (IOException) {
                if (attempt >= 300) {
                    slotTicks = DateTime.UtcNow.Ticks;
                    break;
                }

                await Task.Delay(20);
            }
        }

        var wait = slotTicks - DateTime.UtcNow.Ticks;
        if (wait > 0) {
            await Task.Delay(TimeSpan.FromTicks(wait));
        }
    }

    private static EntityMetadataProposal Proposal(string kind, string id, string reason, EntityMetadataPatch patch, IReadOnlyList<ImageCandidate> images, IReadOnlyList<EntityMetadataProposal>? relationships = null, IReadOnlyList<EntityMetadataProposal>? children = null) => new(id, PluginId, kind, 0.9m, reason, patch, images, children ?? [], [], null, relationships ?? []);
    private static bool IsExplicitSearch(IdentifyPluginRequest request) => request.Action.Equals("search", StringComparison.OrdinalIgnoreCase) && (!string.IsNullOrWhiteSpace(request.Query.Title) || request.Query.Fields?.Values.Any(value => !string.IsNullOrWhiteSpace(value)) == true) && request.Query.ExternalIds is not { Count: > 0 } && string.IsNullOrWhiteSpace(request.Query.Url);
    private static string? ExternalId(IdentifyPluginRequest request) {
        foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) {
            foreach (var identityNamespace in IdentityNamespacesForKind(request.Entity.Kind)) {
                if (TryGetValue(ids, identityNamespace, out var value) && IsGuid(value)) return value;
            }
        }
        return null;
    }
    private static string? FirstUrlId(IReadOnlyList<string> urls, Func<string?, string?> parser) => urls.Select(parser).FirstOrDefault(id => id is not null);
    private static string? ReleaseIdFromUrl(string? url) => UrlId(url, "release") ?? UrlId(url, "release-group");
    private static string? RecordingIdFromUrl(string? url) => UrlId(url, "recording");
    private static string? ArtistIdFromUrl(string? url) => UrlId(url, "artist");
    private static string? UrlId(string? url, string kind) { if (string.IsNullOrWhiteSpace(url)) return null; var match = Regex.Match(url, $"musicbrainz\\.org/{kind}/([0-9a-f-]{{36}})", RegexOptions.IgnoreCase); return match.Success ? match.Groups[1].Value : null; }
    private static bool IsGuid(string value) => Guid.TryParse(value, out _);
    private static string Quote(string value) => $"\"{value.Replace("\"", string.Empty).Trim()}\"";

    internal static string BuildArtistSearch(IdentifyPluginRequest request, string title) =>
        JoinSearchTerms(
            $"artist:{Quote(title)}",
            SearchTerm("country", SearchField(request, SearchFields.Country)),
            SearchTerm("begin", SearchField(request, SearchFields.StartYear)));

    internal static string BuildReleaseSearch(IdentifyPluginRequest request, string title) =>
        JoinSearchTerms(
            $"release:{Quote(title)}",
            SearchTerm("artist", SearchField(request, SearchFields.Artist)),
            SearchTerm("date", SearchField(request, SearchFields.Year)));

    internal static string BuildRecordingSearch(IdentifyPluginRequest request, string title) =>
        JoinSearchTerms(
            $"recording:{Quote(title)}",
            SearchTerm("artist", SearchField(request, SearchFields.Artist)),
            SearchTerm("release", SearchField(request, SearchFields.Album)),
            SearchTerm("firstreleasedate", SearchField(request, SearchFields.Year)));

    private static string? SearchTitle(IdentifyPluginRequest request) =>
        SearchField(request, SearchFields.Title, SearchFields.Artist) ?? request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;

    private static string? SearchField(IdentifyPluginRequest request, params string[] keys) {
        foreach (var key in keys) {
            if (TryGetValue(request.Query.Fields, key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static string? SearchTerm(string field, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"{field}:{Quote(value)}";

    private static string JoinSearchTerms(params string?[] terms) =>
        string.Join(" AND ", terms.Where(term => !string.IsNullOrWhiteSpace(term)));

    private static IReadOnlyList<string> IdentityNamespacesForKind(string kind) =>
        kind.Equals("music-artist", StringComparison.OrdinalIgnoreCase)
            ? [PrimaryIdentityNamespace, ArtistIdentityNamespace]
            : kind.Equals("audio-library", StringComparison.OrdinalIgnoreCase)
                ? [PrimaryIdentityNamespace, ReleaseIdentityNamespace, ReleaseGroupIdentityNamespace]
                : [PrimaryIdentityNamespace, RecordingIdentityNamespace];

    private static bool TryGetValue(IReadOnlyDictionary<string, string>? values, string key, out string value) {
        foreach (var pair in values ?? new Dictionary<string, string>()) {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) {
                value = pair.Value;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    /// <summary>Finds the MusicBrainz id of the nearest ancestor of the given Prismedia kind, when one was supplied.</summary>
    private static string? AncestorMusicBrainzId(IdentifyPluginRequest request, string kind) {
        foreach (var ancestor in request.StructuralContext?.Ancestors ?? []) {
            if (ancestor.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
                ancestor.ExternalIds is not null &&
                IdentityNamespacesForKind(kind)
                    .Select(identityNamespace => TryGetValue(ancestor.ExternalIds, identityNamespace, out var value) ? value : null)
                    .FirstOrDefault(value => value is not null && IsGuid(value)) is { } identity) {
                return identity;
            }
        }

        return null;
    }

    /// <summary>Strips a trailing release year suffix (e.g. "Evolve (2017)") so the title matches MusicBrainz.</summary>
    private static string CleanTitle(string title) {
        var cleaned = Regex.Replace(title, @"\s*[\(\[]\s*\d{4}\s*[\)\]]\s*$", string.Empty).Trim();
        return cleaned.Length > 0 ? cleaned : title.Trim();
    }
    private static int? Year(string? date) => int.TryParse((date ?? string.Empty).Split('-')[0], out var year) ? year : null;
    private static decimal? Score(int? score) => score is null ? null : Math.Clamp(score.Value / 100m, 0m, 1m);
    private static string ArtistCreditString(ArtistCredit[]? credits) => credits is null ? string.Empty : string.Join(", ", credits.Select(c => c.Name ?? c.Artist?.Name).Where(s => !string.IsNullOrWhiteSpace(s)));
    private static string? LabelName(LabelInfo[]? infos) => infos?.Select(i => i.Label?.Name).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
    private static IReadOnlyList<string> Tags(Tag[]? genres, Tag[]? tags) => [.. (genres ?? []).Concat(tags ?? []).Select(t => t.Name).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(20)!];
    private static Release? PrimaryRelease(Release[]? releases) => releases?.OrderByDescending(r => r.ReleaseGroup?.PrimaryType == "Album").ThenBy(r => r.Date ?? "9999").FirstOrDefault();
    private static string? AttributesLabel(string[]? attributes) => attributes is { Length: > 0 } ? string.Join(", ", attributes.Where(a => !string.IsNullOrWhiteSpace(a))) : null;

    private static string? CoverThumbUrl(Release release) =>
        release.ReleaseGroup?.Id is { Length: > 0 } releaseGroup
            ? $"{CoverBase}/release-group/{releaseGroup}/{CoverThumb}"
            : $"{CoverBase}/release/{release.Id}/{CoverThumb}";

    private static string? ReleaseOverview(Release release) {
        var parts = new List<string>();
        if (ArtistCreditString(release.ArtistCredit) is { Length: > 0 } artist) parts.Add(artist);
        var facets = new List<string>();
        if (!string.IsNullOrWhiteSpace(release.ReleaseGroup?.PrimaryType)) facets.Add(release.ReleaseGroup!.PrimaryType!);
        if (release.TrackCount is > 0 and var count) facets.Add($"{count} tracks");
        if (!string.IsNullOrWhiteSpace(release.Country)) facets.Add(release.Country!);
        if (!string.IsNullOrWhiteSpace(release.Disambiguation)) facets.Add(release.Disambiguation!);
        if (facets.Count > 0) parts.Add(string.Join(" · ", facets));
        return parts.Count > 0 ? string.Join(" — ", parts) : null;
    }

    private static string? RecordingOverview(Recording recording) {
        var parts = new List<string>();
        if (ArtistCreditString(recording.ArtistCredit) is { Length: > 0 } artist) parts.Add(artist);
        if (PrimaryRelease(recording.Releases)?.Title is { Length: > 0 } album) parts.Add(album);
        return parts.Count > 0 ? string.Join(" — ", parts) : null;
    }

    internal static IEnumerable<string> DirectImageUrls(Relation[]? relations) =>
        (relations ?? [])
            .Where(relation => string.Equals(relation.Type, "image", StringComparison.OrdinalIgnoreCase))
            .Select(relation => ImageDownloadUrl(relation.Url?.Resource))
            .Where(url => url is not null)
            .Select(url => url!)
            .Distinct();

    private static string? ImageDownloadUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (IsDirectImageUrl(url)) return url;

        var commonsFileMatch = Regex.Match(
            url,
            @"^https?://commons\.wikimedia\.org/wiki/File:(?<file>[^#?]+)",
            RegexOptions.IgnoreCase);
        if (commonsFileMatch.Success) {
            var file = commonsFileMatch.Groups["file"].Value;
            if (IsImagePath(file)) {
                return $"https://commons.wikimedia.org/wiki/Special:Redirect/file/{file}";
            }
        }

        return null;
    }

    private static bool IsDirectImageUrl(string url) =>
        url.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase) < 0 &&
        IsImagePath(url);

    private static bool IsImagePath(string value) =>
        new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" }.Any(ext => value.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static string? ArtistOverview(MbArtist? artist) {
        if (artist is null) return null;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist.Disambiguation)) parts.Add(artist.Disambiguation!);
        var facet = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist.Type)) facet.Add(artist.Type!);
        if (artist.Area?.Name is { Length: > 0 } area) facet.Add(area);
        else if (!string.IsNullOrWhiteSpace(artist.Country)) facet.Add(artist.Country!);
        if (Year(artist.LifeSpan?.Begin) is { } begin) facet.Add(begin.ToString());
        if (facet.Count > 0) parts.Add(string.Join(" · ", facet));
        return parts.Count > 0 ? string.Join(" — ", parts) : null;
    }

    private sealed record SearchReleaseResponse(Release[]? Releases);
    private sealed record SearchRecordingResponse(Recording[]? Recordings);
    private sealed record SearchArtistResponse(MbArtist[]? Artists);
    private sealed record MbArtist(
        string Id, string? Name, string? Disambiguation, string? Type, string? Country, int? Score, Area? Area,
        [property: JsonPropertyName("life-span")] LifeSpan? LifeSpan,
        Relation[]? Relations, Tag[]? Tags, Tag[]? Genres);
    private sealed record Area(string? Name);
    private sealed record LifeSpan(string? Begin, string? End);
    internal sealed record Relation(string? Type, string? Direction, string[]? Attributes, Artist? Artist, RelationUrl? Url);
    internal sealed record RelationUrl(string? Resource);
    private sealed record Release(
        string Id, string? Title, string? Date, string? Disambiguation, int? Score,
        [property: JsonPropertyName("artist-credit")] ArtistCredit[]? ArtistCredit,
        [property: JsonPropertyName("label-info")] LabelInfo[]? LabelInfo,
        Tag[]? Tags, Tag[]? Genres,
        [property: JsonPropertyName("release-group")] ReleaseGroup? ReleaseGroup,
        Medium[]? Media, string? Country,
        [property: JsonPropertyName("track-count")] int? TrackCount);
    private sealed record Recording(
        string Id, string? Title,
        [property: JsonPropertyName("first-release-date")] string? FirstReleaseDate,
        string? Disambiguation, int? Length, int? Score,
        [property: JsonPropertyName("artist-credit")] ArtistCredit[]? ArtistCredit,
        Release[]? Releases);
    private sealed record ArtistCredit(string? Name, Artist? Artist);
    internal sealed record Artist(string? Name, string? Id = null);
    private sealed record LabelInfo(Label? Label);
    private sealed record Label(string? Name);
    private sealed record Tag(string? Name);
    private sealed record ReleaseGroup([property: JsonPropertyName("primary-type")] string? PrimaryType, string? Id);
    private sealed record BrowseReleaseGroupsResponse([property: JsonPropertyName("release-groups")] ReleaseGroupItem[]? ReleaseGroups);
    private sealed record ReleaseGroupItem(
        string Id, string? Title,
        [property: JsonPropertyName("first-release-date")] string? FirstReleaseDate,
        [property: JsonPropertyName("primary-type")] string? PrimaryType,
        [property: JsonPropertyName("secondary-types")] string[]? SecondaryTypes);
    private sealed record ReleaseGroupDetail(string Id, string? Title, Release[]? Releases);
    private sealed record Medium(string? Format, int? Position, Track[]? Tracks);
    private sealed record Track(int? Position, string? Number, string? Title, int? Length, Recording? Recording);
    private sealed record CoverArtResponse(CoverImage[]? Images);
    private sealed record CoverImage(string? Image, bool? Front);
}

internal static class PluginHost { public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = false }; public static async Task<IdentifyPluginResponse> RunAsync(string[] args, Func<IdentifyPluginRequest, Task<IdentifyPluginResult>> identify) { try { if (args.Length == 0) return new(false, null, "Missing request JSON path."); var request = JsonSerializer.Deserialize<IdentifyPluginRequest>(await File.ReadAllTextAsync(args[0]), JsonOptions); if (request is null) return new(false, null, "Request JSON was empty or invalid."); return new(true, await identify(request), null); } catch (Exception ex) { return new(false, null, ex.Message); } } }
internal sealed record IdentifyPluginRequest(int ProtocolVersion, string Action, IReadOnlyDictionary<string, string> Auth, IdentifyEntitySnapshot Entity, IdentifyQuery Query, IdentifyMatchHints Hints, IdentifyStructuralContext? StructuralContext = null, bool IncludeStructuralChildren = false);
internal sealed record IdentifyStructuralContext(IReadOnlyList<IdentifyEntitySnapshot> Ancestors, IReadOnlyDictionary<string, int> Positions);
internal sealed record IdentifyEntitySnapshot(Guid Id, string Kind, string Title, IReadOnlyDictionary<string, string>? ExternalIds = null, IReadOnlyList<string>? Urls = null);
internal sealed record IdentifyQuery(string? Title, string? Url, IReadOnlyDictionary<string, string>? ExternalIds, bool? RequireChoice = null, IReadOnlyDictionary<string, string>? Fields = null, int? Limit = null);
internal sealed record IdentifyMatchHints(IReadOnlyDictionary<string, string> ExternalIds, IReadOnlyList<string> Urls, string? Title, string? FilePath);
internal sealed record ImageCandidate(string Kind, string Url, string Source, decimal? Rank, string? Language, int? Width, int? Height);
internal sealed record EntitySearchCandidate(IReadOnlyDictionary<string, string> ExternalIds, string Title, int? Year, string? Overview, string? PosterUrl, decimal? Popularity);
internal sealed record CreditPatch(string Name, string Role, string? Character, int? SortOrder);
internal sealed record EntityMetadataFlagsPatch(bool? IsFavorite, bool? IsNsfw, bool? IsOrganized);
internal sealed record EntityMetadataPatch(string? Title, string? Description, IReadOnlyDictionary<string, string> ExternalIds, IReadOnlyList<string> Urls, IReadOnlyList<string> Tags, string? Studio, IReadOnlyList<CreditPatch> Credits, IReadOnlyDictionary<string, string> Dates, IReadOnlyDictionary<string, int> Stats, IReadOnlyDictionary<string, int> Positions, string? Classification) { public int? Rating { get; init; } public EntityMetadataFlagsPatch? Flags { get; init; } }
internal sealed record EntityMetadataProposal(string ProposalId, string Provider, string TargetKind, decimal? Confidence, string? MatchReason, EntityMetadataPatch Patch, IReadOnlyList<ImageCandidate> Images, IReadOnlyList<EntityMetadataProposal> Children, IReadOnlyList<EntitySearchCandidate> Candidates, Guid? TargetEntityId = null, IReadOnlyList<EntityMetadataProposal>? Relationships = null);
internal sealed record IdentifyPluginResult(string Type, EntityMetadataProposal? Proposal, IReadOnlyList<EntitySearchCandidate> Candidates) { public static IdentifyPluginResult ForProposal(EntityMetadataProposal proposal) => new("proposal", proposal, []); public static IdentifyPluginResult ForCandidates(IReadOnlyList<EntitySearchCandidate> candidates) => new("candidates", null, candidates); public static IdentifyPluginResult None() => new("none", null, []); }
internal sealed record IdentifyPluginResponse(bool Ok, IdentifyPluginResult? Result, string? Error);
