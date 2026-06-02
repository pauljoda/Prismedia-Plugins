using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

var response = await PluginHost.RunAsync(args, MusicBrainzPlugin.IdentifyAsync);
Console.Write(JsonSerializer.Serialize(response, PluginHost.JsonOptions));

internal static partial class MusicBrainzPlugin {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string Provider = "musicbrainz";
    private const string MbBase = "https://musicbrainz.org/ws/2";
    private const string CoverBase = "https://coverartarchive.org";

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
        if (id is not null && !IsExplicitSearch(request)) return IdentifyPluginResult.ForProposal(await ArtistProposalAsync(id, "external-id"));
        var query = request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        if (string.IsNullOrWhiteSpace(query)) return IdentifyPluginResult.None();
        var artists = await SearchAsync<SearchArtistResponse>("artist", $"artist:{Quote(query)}", 10);
        var candidates = (artists?.Artists ?? []).Select(artist => new EntitySearchCandidate(
            new Dictionary<string, string> { [Provider] = artist.Id },
            artist.Name ?? artist.Id,
            Year(artist.LifeSpan?.Begin),
            ArtistOverview(artist),
            null,
            Score(artist.Score))).ToArray();
        return IdentifyPluginResult.ForCandidates(candidates);
    }

    private static async Task<EntityMetadataProposal> ArtistProposalAsync(string id, string reason) {
        var artist = await GetJsonAsync<MbArtist>($"{MbBase}/artist/{id}?inc=artist-rels+genres+tags+url-rels&fmt=json");
        var tags = Tags(artist?.Genres, artist?.Tags);
        // "member of band" relations become person credits; the member's instruments/roles become
        // the credit label so the artist page can list members with roles (e.g. "Drummer").
        var members = (artist?.Relations ?? [])
            .Where(relation => string.Equals(relation.Type, "member of band", StringComparison.OrdinalIgnoreCase) &&
                               !string.IsNullOrWhiteSpace(relation.Artist?.Name))
            .Select((relation, index) => new CreditPatch(relation.Artist!.Name!, "artist", AttributesLabel(relation.Attributes), index))
            .ToArray();
        var urls = new List<string> { $"https://musicbrainz.org/artist/{id}" };
        foreach (var relation in artist?.Relations ?? []) {
            if (relation.Url?.Resource is { Length: > 0 } resource && !urls.Contains(resource)) urls.Add(resource);
        }

        return Proposal("music-artist", $"musicbrainz:artist:{id}", reason, new EntityMetadataPatch(
            artist?.Name ?? id,
            ArtistOverview(artist),
            new Dictionary<string, string> { [Provider] = id, ["musicbrainzArtist"] = id },
            urls,
            tags,
            null,
            members,
            new Dictionary<string, string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            artist?.Type), []);
    }

    private static async Task<IdentifyPluginResult> IdentifyReleaseAsync(IdentifyPluginRequest request) {
        var id = ExternalId(request) ?? ReleaseIdFromUrl(request.Query.Url) ?? FirstUrlId(request.Hints.Urls, ReleaseIdFromUrl);
        if (id is not null && !IsExplicitSearch(request)) return IdentifyPluginResult.ForProposal(await ReleaseProposalAsync(id, "external-id"));
        var query = request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        if (string.IsNullOrWhiteSpace(query)) return IdentifyPluginResult.None();
        var releases = await SearchAsync<SearchReleaseResponse>("release", $"release:{Quote(query)}", 10);
        var candidates = (releases?.Releases ?? []).Select(release => new EntitySearchCandidate(
            new Dictionary<string, string> { [Provider] = release.Id },
            release.Title ?? release.Id,
            Year(release.Date),
            release.Disambiguation,
            null,
            Score(release.Score))).ToArray();
        return IdentifyPluginResult.ForCandidates(candidates);
    }

    private static async Task<IdentifyPluginResult> IdentifyRecordingAsync(IdentifyPluginRequest request) {
        var id = ExternalId(request) ?? RecordingIdFromUrl(request.Query.Url) ?? FirstUrlId(request.Hints.Urls, RecordingIdFromUrl);
        if (id is not null && !IsExplicitSearch(request)) return IdentifyPluginResult.ForProposal(await RecordingProposalAsync(id, "external-id"));
        var query = request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        if (string.IsNullOrWhiteSpace(query)) return IdentifyPluginResult.None();
        var recordings = await SearchAsync<SearchRecordingResponse>("recording", $"recording:{Quote(query)}", 10);
        var candidates = (recordings?.Recordings ?? []).Select(recording => new EntitySearchCandidate(
            new Dictionary<string, string> { [Provider] = recording.Id },
            recording.Title ?? recording.Id,
            Year(recording.FirstReleaseDate),
            ArtistCreditString(recording.ArtistCredit),
            null,
            Score(recording.Score))).ToArray();
        return IdentifyPluginResult.ForCandidates(candidates);
    }

    private static async Task<EntityMetadataProposal> ReleaseProposalAsync(string id, string reason) {
        var release = await GetJsonAsync<Release>($"{MbBase}/release/{id}?inc=artists+labels+tags+genres+release-groups&fmt=json");
        var images = await CoverImagesAsync(id);
        var tags = Tags(release?.Genres, release?.Tags);
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(release?.Date)) dates["released"] = release!.Date!;
        var stats = new Dictionary<string, int>();
        if (release?.Media is { Length: > 0 }) stats["discCount"] = release.Media.Length;
        return Proposal("audio-library", $"musicbrainz:release:{id}", reason, new EntityMetadataPatch(
            release?.Title ?? id,
            release?.Disambiguation,
            new Dictionary<string, string> { [Provider] = id, ["musicbrainzRelease"] = id },
            [$"https://musicbrainz.org/release/{id}"],
            tags,
            LabelName(release?.LabelInfo),
            ArtistCreditString(release?.ArtistCredit) is { Length: > 0 } artist ? [new CreditPatch(artist, "artist", null, 0)] : [],
            dates,
            stats,
            new Dictionary<string, int>(),
            release?.ReleaseGroup?.PrimaryType), images);
    }

    private static async Task<EntityMetadataProposal> RecordingProposalAsync(string id, string reason) {
        var recording = await GetJsonAsync<Recording>($"{MbBase}/recording/{id}?inc=artists+releases&fmt=json");
        var release = PrimaryRelease(recording?.Releases);
        var releaseId = release?.Id;
        var images = releaseId is null ? [] : await CoverImagesAsync(releaseId);
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(recording?.FirstReleaseDate ?? release?.Date)) dates["released"] = recording?.FirstReleaseDate ?? release!.Date!;
        var stats = new Dictionary<string, int>();
        if (recording?.Length is int ms) stats["runtimeSeconds"] = ms / 1000;
        var urls = new List<string> { $"https://musicbrainz.org/recording/{id}" };
        if (releaseId is not null) urls.Add($"https://musicbrainz.org/release/{releaseId}");
        var external = new Dictionary<string, string> { [Provider] = id, ["musicbrainzRecording"] = id };
        if (releaseId is not null) external["musicbrainzRelease"] = releaseId;
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

    private static async Task<IReadOnlyList<ImageCandidate>> CoverImagesAsync(string releaseId) {
        try {
            var data = await GetJsonAsync<CoverArtResponse>($"{CoverBase}/release/{releaseId}");
            return (data?.Images ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Image)).Select((i, index) => new ImageCandidate("cover", i.Image!, "coverartarchive", i.Front == true ? 10 : 4 - index, null, null, null)).ToArray();
        } catch { return []; }
    }

    private static async Task<T?> SearchAsync<T>(string entity, string query, int limit) =>
        await GetJsonAsync<T>($"{MbBase}/{entity}/?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}");

    private static async Task<T?> GetJsonAsync<T>(string url) {
        using var res = await Http.GetAsync(url);
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<T>(PluginHost.JsonOptions) : default;
    }

    private static EntityMetadataProposal Proposal(string kind, string id, string reason, EntityMetadataPatch patch, IReadOnlyList<ImageCandidate> images) => new(id, Provider, kind, 0.9m, reason, patch, images, [], [], null, []);
    private static bool IsExplicitSearch(IdentifyPluginRequest request) => request.Action.Equals("search", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(request.Query.Title) && request.Query.ExternalIds is not { Count: > 0 } && string.IsNullOrWhiteSpace(request.Query.Url);
    private static string? ExternalId(IdentifyPluginRequest request) { foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) if (ids is not null && ids.TryGetValue(Provider, out var value) && IsGuid(value)) return value; return null; }
    private static string? FirstUrlId(IReadOnlyList<string> urls, Func<string?, string?> parser) => urls.Select(parser).FirstOrDefault(id => id is not null);
    private static string? ReleaseIdFromUrl(string? url) => UrlId(url, "release");
    private static string? RecordingIdFromUrl(string? url) => UrlId(url, "recording");
    private static string? ArtistIdFromUrl(string? url) => UrlId(url, "artist");
    private static string? UrlId(string? url, string kind) { if (string.IsNullOrWhiteSpace(url)) return null; var match = Regex.Match(url, $"musicbrainz\\.org/{kind}/([0-9a-f-]{{36}})", RegexOptions.IgnoreCase); return match.Success ? match.Groups[1].Value : null; }
    private static bool IsGuid(string value) => Guid.TryParse(value, out _);
    private static string Quote(string value) => $"\"{value.Replace("\"", string.Empty).Trim()}\"";
    private static int? Year(string? date) => int.TryParse((date ?? string.Empty).Split('-')[0], out var year) ? year : null;
    private static decimal? Score(int? score) => score is null ? null : Math.Clamp(score.Value / 100m, 0m, 1m);
    private static string ArtistCreditString(ArtistCredit[]? credits) => credits is null ? string.Empty : string.Join(", ", credits.Select(c => c.Name ?? c.Artist?.Name).Where(s => !string.IsNullOrWhiteSpace(s)));
    private static string? LabelName(LabelInfo[]? infos) => infos?.Select(i => i.Label?.Name).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
    private static IReadOnlyList<string> Tags(Tag[]? genres, Tag[]? tags) => [.. (genres ?? []).Concat(tags ?? []).Select(t => t.Name).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(20)!];
    private static Release? PrimaryRelease(Release[]? releases) => releases?.OrderByDescending(r => r.ReleaseGroup?.PrimaryType == "Album").ThenBy(r => r.Date ?? "9999").FirstOrDefault();
    private static string? AttributesLabel(string[]? attributes) => attributes is { Length: > 0 } ? string.Join(", ", attributes.Where(a => !string.IsNullOrWhiteSpace(a))) : null;

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
    private sealed record MbArtist(string Id, string? Name, string? Disambiguation, string? Type, string? Country, int? Score, Area? Area, LifeSpan? LifeSpan, Relation[]? Relations, Tag[]? Tags, Tag[]? Genres);
    private sealed record Area(string? Name);
    private sealed record LifeSpan(string? Begin, string? End);
    private sealed record Relation(string? Type, string? Direction, string[]? Attributes, Artist? Artist, RelationUrl? Url);
    private sealed record RelationUrl(string? Resource);
    private sealed record Release(string Id, string? Title, string? Date, string? Disambiguation, int? Score, ArtistCredit[]? ArtistCredit, LabelInfo[]? LabelInfo, Tag[]? Tags, Tag[]? Genres, ReleaseGroup? ReleaseGroup, Medium[]? Media);
    private sealed record Recording(string Id, string? Title, string? FirstReleaseDate, string? Disambiguation, int? Length, int? Score, ArtistCredit[]? ArtistCredit, Release[]? Releases);
    private sealed record ArtistCredit(string? Name, Artist? Artist);
    private sealed record Artist(string? Name);
    private sealed record LabelInfo(Label? Label);
    private sealed record Label(string? Name);
    private sealed record Tag(string? Name);
    private sealed record ReleaseGroup(string? PrimaryType);
    private sealed record Medium(string? Format);
    private sealed record CoverArtResponse(CoverImage[]? Images);
    private sealed record CoverImage(string? Image, bool? Front);
}

internal static class PluginHost { public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = false }; public static async Task<IdentifyPluginResponse> RunAsync(string[] args, Func<IdentifyPluginRequest, Task<IdentifyPluginResult>> identify) { try { if (args.Length == 0) return new(false, null, "Missing request JSON path."); var request = JsonSerializer.Deserialize<IdentifyPluginRequest>(await File.ReadAllTextAsync(args[0]), JsonOptions); if (request is null) return new(false, null, "Request JSON was empty or invalid."); return new(true, await identify(request), null); } catch (Exception ex) { return new(false, null, ex.Message); } } }
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
