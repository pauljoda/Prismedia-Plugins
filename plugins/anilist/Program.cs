using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

var response = await PluginHost.RunAsync(args, AniListPlugin.IdentifyAsync);
Console.Write(JsonSerializer.Serialize(response, PluginHost.JsonOptions));

internal static partial class AniListPlugin {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string Provider = "anilist";
    private const string Api = "https://graphql.anilist.co";

    public static async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        if (!request.Entity.Kind.Equals("video-series", StringComparison.OrdinalIgnoreCase) && !request.Entity.Kind.Equals("video", StringComparison.OrdinalIgnoreCase)) return IdentifyPluginResult.None();
        var id = ExternalId(request) ?? IdFromUrl(request.Query.Url) ?? FirstUrlId(request.Hints.Urls);
        if (id is not null && !IsExplicitSearch(request)) return IdentifyPluginResult.ForProposal(ToProposal(await DetailAsync(id.Value), request.Entity.Kind, request.Entity.Id, "external-id"));
        var title = request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        if (string.IsNullOrWhiteSpace(title)) return IdentifyPluginResult.None();
        var results = await SearchAsync(title);
        return IdentifyPluginResult.ForCandidates(results.Select(media => new EntitySearchCandidate(new Dictionary<string, string> { [Provider] = media.Id.ToString() }, Title(media), Year(media.StartDate), StripHtml(media.Description), media.CoverImage?.Large ?? media.CoverImage?.ExtraLarge, media.Popularity)).ToArray());
    }

    private static EntityMetadataProposal ToProposal(Media media, string requestedKind, Guid targetId, string reason) {
        var kind = requestedKind.Equals("video", StringComparison.OrdinalIgnoreCase) && IsMovieLike(media) ? "video" : "video-series";
        var tags = (media.Genres ?? []).Concat((media.Tags ?? []).Where(t => (t.Rank ?? 0) >= 60).Select(t => t.Name)).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(20).Cast<string>().ToArray();
        var dates = new Dictionary<string, string>(); if (DateString(media.StartDate) is { } start) dates["started"] = start; if (DateString(media.EndDate) is { } end) dates["ended"] = end;
        var stats = new Dictionary<string, int>(); if (media.Episodes is int episodes) stats["episodeCount"] = episodes; if (media.Duration is int duration) stats["runtimeMinutes"] = duration; if (media.Popularity is int popularity) stats["popularity"] = popularity;
        var external = new Dictionary<string, string> { [Provider] = media.Id.ToString() }; if (media.IdMal is int mal) external["mal"] = mal.ToString();
        var images = new List<ImageCandidate>(); if (!string.IsNullOrWhiteSpace(media.CoverImage?.ExtraLarge)) images.Add(new("poster", media.CoverImage!.ExtraLarge!, Provider, 10, null, null, null)); if (!string.IsNullOrWhiteSpace(media.CoverImage?.Large)) images.Add(new("poster", media.CoverImage!.Large!, Provider, 8, null, null, null)); if (!string.IsNullOrWhiteSpace(media.BannerImage)) images.Add(new("backdrop", media.BannerImage!, Provider, 7, null, null, null));
        var children = kind == "video-series" && media.Episodes is > 0 ? Enumerable.Range(1, Math.Min(media.Episodes.Value, 200)).Select(i => EpisodeShell(media, i)).ToArray() : [];
        var relationships = CharacterRelationships(media).Concat(StudioRelationships(media)).ToArray();
        return new EntityMetadataProposal($"anilist:{media.Id}", Provider, kind, 0.9m, reason, new EntityMetadataPatch(Title(media), StripHtml(media.Description), external, [media.SiteUrl ?? $"https://anilist.co/anime/{media.Id}"], tags, PrimaryStudio(media), [], dates, stats, new Dictionary<string, int>(), media.Format), images, children, [], targetId, relationships);
    }

    private static EntityMetadataProposal EpisodeShell(Media media, int number) => new($"anilist:{media.Id}:episode:{number}", Provider, "video", 0.65m, "series-cascade", new EntityMetadataPatch($"Episode {number}", null, new Dictionary<string, string> { [Provider] = media.Id.ToString() }, [], [], null, [], new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int> { ["episodeNumber"] = number, ["sortOrder"] = number }, null), [], [], [], null, []);
    private static IEnumerable<EntityMetadataProposal> CharacterRelationships(Media media) => (media.Characters?.Edges ?? []).Take(20).Select(edge => edge.Node?.Name?.Full).Where(name => !string.IsNullOrWhiteSpace(name)).Select((name, i) => new EntityMetadataProposal($"anilist:character:{Slug(name!)}", Provider, "person", 0.6m, "character", new EntityMetadataPatch(name, null, new Dictionary<string, string>(), [], [], null, [new CreditPatch(name!, "character", null, i)], new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int>(), null), [], [], [], null, []));
    private static IEnumerable<EntityMetadataProposal> StudioRelationships(Media media) => (media.Studios?.Nodes ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Name)).Take(5).Select(studio => new EntityMetadataProposal($"anilist:studio:{Slug(studio.Name!)}", Provider, "studio", 0.7m, "studio", new EntityMetadataPatch(studio.Name, null, new Dictionary<string, string>(), [], [], null, [], new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int>(), null), [], [], [], null, []));

    private static async Task<Media> DetailAsync(int id) { var data = await GraphQlAsync<DetailData>(DetailQuery, new { id }); return data.Media ?? throw new InvalidOperationException("AniList media not found."); }
    private static async Task<IReadOnlyList<Media>> SearchAsync(string search) { var data = await GraphQlAsync<SearchData>(SearchQuery, new { search }); return data.Page?.Media ?? []; }
    private static async Task<T> GraphQlAsync<T>(string query, object variables) {
        using var res = await Http.PostAsJsonAsync(Api, new { query, variables }, PluginHost.JsonOptions);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"AniList API error: {(int)res.StatusCode}");
        var envelope = await res.Content.ReadFromJsonAsync<GraphQlEnvelope<T>>(PluginHost.JsonOptions);
        if (envelope is null) throw new InvalidOperationException("AniList returned no data.");
        if (envelope.Errors is { Length: > 0 }) throw new InvalidOperationException(string.Join("; ", envelope.Errors.Select(e => e.Message)));
        var data = envelope.Data;
        if (data is null) throw new InvalidOperationException("AniList returned no data.");
        return data;
    }
    private static string Title(Media media) => media.Title?.English ?? media.Title?.Romaji ?? media.Title?.Native ?? media.Id.ToString();
    private static bool IsMovieLike(Media media) => string.Equals(media.Format, "MOVIE", StringComparison.OrdinalIgnoreCase) || media.Episodes == 1;
    private static string? PrimaryStudio(Media media) => media.Studios?.Nodes?.FirstOrDefault(s => s.IsAnimationStudio == true)?.Name ?? media.Studios?.Nodes?.FirstOrDefault()?.Name;
    private static string? StripHtml(string? value) => string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value, "<.*?>", string.Empty).Replace("&quot;", "\"").Replace("&amp;", "&").Trim();
    private static int? Year(FuzzyDate? date) => date?.Year;
    private static string? DateString(FuzzyDate? date) => date?.Year is null ? null : $"{date.Year:D4}-{date.Month ?? 1:D2}-{date.Day ?? 1:D2}";
    private static string Slug(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    private static int? ExternalId(IdentifyPluginRequest request) { foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) if (ids is not null && ids.TryGetValue(Provider, out var value) && int.TryParse(value, out var id)) return id; return null; }
    private static int? FirstUrlId(IReadOnlyList<string> urls) => urls.Select(IdFromUrl).FirstOrDefault(id => id is not null);
    private static int? IdFromUrl(string? url) { if (string.IsNullOrWhiteSpace(url)) return null; var match = Regex.Match(url, "anilist\\.co/anime/(\\d+)", RegexOptions.IgnoreCase); return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null; }
    private static bool IsExplicitSearch(IdentifyPluginRequest request) => request.Action.Equals("search", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(request.Query.Title) && string.IsNullOrWhiteSpace(request.Query.Url) && request.Query.ExternalIds is not { Count: > 0 };

    private const string Fields = """
      id idMal description format episodes duration status season seasonYear bannerImage averageScore meanScore popularity siteUrl isAdult genres
      title { romaji english native }
      startDate { year month day }
      endDate { year month day }
      coverImage { extraLarge large medium color }
      tags { name rank }
      studios { nodes { name isAnimationStudio } }
      characters(perPage: 25, sort: [ROLE, RELEVANCE]) { edges { node { name { full } } } }
      """;
    private static readonly string DetailQuery = $"query ($id: Int!) {{ Media(id: $id, type: ANIME) {{ {Fields} }} }}";
    private static readonly string SearchQuery = $"query ($search: String!) {{ Page(perPage: 10) {{ media(search: $search, type: ANIME, isAdult: false, sort: [SEARCH_MATCH, POPULARITY_DESC]) {{ {Fields} }} }} }}";

    private sealed record GraphQlEnvelope<T>(T? Data, GraphQlError[]? Errors);
    private sealed record GraphQlError(string Message);
    private sealed record DetailData(Media? Media);
    private sealed record SearchData(Page? Page);
    private sealed record Page(Media[]? Media);
    private sealed record Media(int Id, int? IdMal, MediaTitle? Title, string? Description, string? Format, int? Episodes, int? Duration, FuzzyDate? StartDate, FuzzyDate? EndDate, Image? CoverImage, string? BannerImage, int? Popularity, string[]? Genres, MediaTag[]? Tags, StudioConnection? Studios, string? SiteUrl, CharacterConnection? Characters);
    private sealed record MediaTitle(string? Romaji, string? English, string? Native);
    private sealed record FuzzyDate(int? Year, int? Month, int? Day);
    private sealed record Image(string? ExtraLarge, string? Large, string? Medium, string? Color);
    private sealed record MediaTag(string? Name, int? Rank);
    private sealed record StudioConnection(Studio[]? Nodes);
    private sealed record Studio(string? Name, bool? IsAnimationStudio);
    private sealed record CharacterConnection(CharacterEdge[]? Edges);
    private sealed record CharacterEdge(Character? Node);
    private sealed record Character(CharacterName? Name);
    private sealed record CharacterName(string? Full);
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
