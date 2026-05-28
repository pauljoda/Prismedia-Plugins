using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

var response = await PluginHost.RunAsync(args, YoutubePlugin.IdentifyAsync);
Console.Write(JsonSerializer.Serialize(response, PluginHost.JsonOptions));

internal static partial class YoutubePlugin {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string Provider = "youtube";
    private const string OEmbed = "https://www.youtube.com/oembed";
    private const string InnerTube = "https://www.youtube.com/youtubei/v1";
    private const string InnerTubeKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
    private const string ClientVersion = "2.20240726.00.00";

    public static async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        if (!request.Entity.Kind.Equals("video", StringComparison.OrdinalIgnoreCase)) {
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
        using var res = await Http.PostAsJsonAsync($"{InnerTube}/search?key={InnerTubeKey}", payload, PluginHost.JsonOptions);
        if (!res.IsSuccessStatusCode) return [];
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
        using var res = await Http.PostAsJsonAsync($"{InnerTube}/player?key={InnerTubeKey}", payload, PluginHost.JsonOptions);
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<PlayerResponse>(PluginHost.JsonOptions) : null;
    }

    private static async Task<OEmbedResponse?> FetchOEmbedAsync(string id) {
        using var res = await Http.GetAsync($"{OEmbed}?url={Uri.EscapeDataString(VideoUrl(id))}&format=json");
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<OEmbedResponse>(PluginHost.JsonOptions) : null;
    }

    private static async Task<ChannelMetadata?> FetchChannelAsync(string? channelId) {
        if (string.IsNullOrWhiteSpace(channelId)) return null;

        var payload = new {
            context = new { client = new { clientName = "WEB", clientVersion = ClientVersion } },
            browseId = channelId
        };
        using var res = await Http.PostAsJsonAsync($"{InnerTube}/browse?key={InnerTubeKey}", payload, PluginHost.JsonOptions);
        if (!res.IsSuccessStatusCode) return null;
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
