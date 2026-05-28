using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

var response = await PluginHost.RunAsync(args, TvdbPlugin.IdentifyAsync);
Console.Write(JsonSerializer.Serialize(response, PluginHost.JsonOptions));

internal static partial class TvdbPlugin {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string Provider = "tvdb";
    private const string Api = "https://api4.thetvdb.com/v4";

    public static async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        if (!request.Entity.Kind.Equals("video-series", StringComparison.OrdinalIgnoreCase) &&
            !request.Entity.Kind.Equals("video-season", StringComparison.OrdinalIgnoreCase) &&
            !request.Entity.Kind.Equals("video", StringComparison.OrdinalIgnoreCase)) {
            return IdentifyPluginResult.None();
        }

        var key = ApiKey(request.Auth);
        if (key is null) throw new InvalidOperationException("TVDB API key is required.");
        return request.Entity.Kind.ToLowerInvariant() switch {
            "video-series" => await IdentifySeriesAsync(request, key),
            "video-season" => await IdentifySeasonAsync(request, key),
            "video" => await IdentifyEpisodeAsync(request, key),
            _ => IdentifyPluginResult.None()
        };
    }

    private static async Task<IdentifyPluginResult> IdentifySeriesAsync(IdentifyPluginRequest request, string apiKey) {
        var id = ExternalId(request) ?? SeriesIdFromUrl(request.Query.Url) ?? FirstUrlId(request.Hints.Urls, SeriesIdFromUrl);
        if (id is not null && !IsExplicitSearch(request)) return IdentifyPluginResult.ForProposal(await SeriesProposalAsync(id.Value, apiKey, "external-id"));
        var title = request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        if (string.IsNullOrWhiteSpace(title)) return IdentifyPluginResult.None();
        var hits = await SearchAsync(title, apiKey);
        return IdentifyPluginResult.ForCandidates(hits.Select(hit => new EntitySearchCandidate(
            new Dictionary<string, string> { [Provider] = TvdbNumericId(hit) },
            hit.Name ?? hit.Translations?.Values.FirstOrDefault() ?? TvdbNumericId(hit),
            Year(hit.Year ?? hit.FirstAirTime),
            hit.Overview ?? hit.Overviews?.Values.FirstOrDefault(),
            hit.ImageUrl ?? hit.Thumbnail,
            hit.Score is null ? null : hit.Score / 100m)).ToArray());
    }

    private static async Task<IdentifyPluginResult> IdentifySeasonAsync(IdentifyPluginRequest request, string apiKey) {
        var seriesId = AncestorSeriesId(request) ?? ExternalId(request);
        var season = Position(request, "seasonNumber", "season", "sortOrder");
        if (seriesId is null || season is null) return IdentifyPluginResult.None();
        var series = await FetchAsync<SeriesExtended>($"/series/{seriesId.Value}/extended", apiKey);
        var tvdbSeason = series?.Seasons?.FirstOrDefault(s => s.Number == season.Value && s.Type?.Type?.Equals("official", StringComparison.OrdinalIgnoreCase) != false);
        if (tvdbSeason is null) return IdentifyPluginResult.None();
        var detail = await FetchAsync<SeasonExtended>($"/seasons/{tvdbSeason.Id}/extended", apiKey);
        return IdentifyPluginResult.ForProposal(SeasonProposal(seriesId.Value, detail ?? new SeasonExtended(tvdbSeason.Id, season.Value, tvdbSeason.Name, tvdbSeason.Image, seriesId.Value, []), request.Entity.Id));
    }

    private static async Task<IdentifyPluginResult> IdentifyEpisodeAsync(IdentifyPluginRequest request, string apiKey) {
        var episodeId = EpisodeIdFromUrl(request.Query.Url) ?? FirstUrlId(request.Hints.Urls, EpisodeIdFromUrl);
        if (episodeId is not null && !IsExplicitSearch(request)) {
            var directEpisode = await FetchAsync<Episode>($"/episodes/{episodeId.Value}/extended", apiKey);
            return directEpisode is null ? IdentifyPluginResult.None() : IdentifyPluginResult.ForProposal(EpisodeProposal(directEpisode, request.Entity.Id, "url"));
        }
        var seriesId = AncestorSeriesId(request);
        var season = Position(request, "seasonNumber", "season");
        var episodeNumber = Position(request, "episodeNumber", "episode", "sortOrder");
        if (seriesId is null || season is null || episodeNumber is null) return IdentifyPluginResult.None();
        var series = await FetchAsync<SeriesExtended>($"/series/{seriesId.Value}/extended", apiKey);
        var tvdbSeason = series?.Seasons?.FirstOrDefault(s => s.Number == season.Value);
        if (tvdbSeason is null) return IdentifyPluginResult.None();
        var detail = await FetchAsync<SeasonExtended>($"/seasons/{tvdbSeason.Id}/extended", apiKey);
        var episode = detail?.Episodes?.FirstOrDefault(e => e.Number == episodeNumber.Value);
        return episode is null ? IdentifyPluginResult.None() : IdentifyPluginResult.ForProposal(EpisodeProposal(episode, request.Entity.Id, "context"));
    }

    private static async Task<EntityMetadataProposal> SeriesProposalAsync(int id, string apiKey, string reason) {
        var series = await FetchAsync<SeriesExtended>($"/series/{id}/extended", apiKey) ?? throw new InvalidOperationException("TVDB series not found.");
        var images = Artwork(series.Artworks, [2], "poster", series.Image).Concat(Artwork(series.Artworks, [3], "backdrop", null)).ToArray();
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(series.FirstAired)) dates["firstAired"] = series.FirstAired!;
        if (!string.IsNullOrWhiteSpace(series.LastAired)) dates["lastAired"] = series.LastAired!;
        var stats = new Dictionary<string, int>();
        if (series.AverageRuntime is int runtime) stats["runtimeMinutes"] = runtime;
        var children = (series.Seasons ?? []).Where(s => s.Number > 0).OrderBy(s => s.Number).Select(s => SeasonShellProposal(id, s)).ToArray();
        var relationships = (series.Characters ?? []).Where(c => !string.IsNullOrWhiteSpace(c.PersonName)).Take(25).Select(c => PersonRelationship(c)).ToArray();
        return new EntityMetadataProposal($"tvdb:series:{id}", Provider, "video-series", 0.92m, reason, new EntityMetadataPatch(
            series.Name,
            series.Overview,
            new Dictionary<string, string> { [Provider] = id.ToString(), ["tvdbSeries"] = id.ToString() },
            [$"https://thetvdb.com/series/{series.Slug ?? id.ToString()}"],
            series.Genres?.Select(g => g.Name).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToArray() ?? [],
            series.OriginalNetwork?.Name ?? series.LatestNetwork?.Name,
            [], dates, stats, new Dictionary<string, int>(), series.Status?.Name), images, children, [], null, relationships);
    }

    private static EntityMetadataProposal SeasonShellProposal(int seriesId, SeasonSummary season) => new(
        $"tvdb:series:{seriesId}:season:{season.Number}", Provider, "video-season", 0.85m, "series-cascade",
        new EntityMetadataPatch(season.Name ?? $"Season {season.Number}", null, new Dictionary<string, string> { [Provider] = season.Id.ToString(), ["tvdbSeason"] = season.Id.ToString(), ["tvdbSeries"] = seriesId.ToString() }, [], [], null, [], new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int> { ["seasonNumber"] = season.Number, ["sortOrder"] = season.Number }, null),
        string.IsNullOrWhiteSpace(season.Image) ? [] : [new ImageCandidate("poster", season.Image!, Provider, 8, null, null, null)], [], [], null, []);

    private static EntityMetadataProposal SeasonProposal(int seriesId, SeasonExtended season, Guid targetId) => new(
        $"tvdb:series:{seriesId}:season:{season.Number}", Provider, "video-season", 0.9m, "context",
        new EntityMetadataPatch(season.Name ?? $"Season {season.Number}", season.Overview, new Dictionary<string, string> { [Provider] = season.Id.ToString(), ["tvdbSeason"] = season.Id.ToString(), ["tvdbSeries"] = seriesId.ToString() }, [], [], null, [], new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int> { ["seasonNumber"] = season.Number, ["sortOrder"] = season.Number }, null),
        string.IsNullOrWhiteSpace(season.Image) ? [] : [new ImageCandidate("poster", season.Image!, Provider, 8, null, null, null)],
        (season.Episodes ?? []).OrderBy(e => e.Number).Select(e => EpisodeProposal(e, null, "season-cascade")).ToArray(), [], targetId, []);

    private static EntityMetadataProposal EpisodeProposal(Episode episode, Guid? targetId, string reason) {
        var dates = new Dictionary<string, string>(); if (!string.IsNullOrWhiteSpace(episode.Aired)) dates["aired"] = episode.Aired!;
        var positions = new Dictionary<string, int>(); if (episode.SeasonNumber is int s) positions["seasonNumber"] = s; if (episode.Number is int e) { positions["episodeNumber"] = e; positions["sortOrder"] = e; }
        var stats = new Dictionary<string, int>(); if (episode.Runtime is int runtime) stats["runtimeMinutes"] = runtime;
        return new EntityMetadataProposal($"tvdb:episode:{episode.Id}", Provider, "video", 0.9m, reason,
            new EntityMetadataPatch(episode.Name ?? $"Episode {episode.Number}", episode.Overview, new Dictionary<string, string> { [Provider] = episode.Id.ToString(), ["tvdbEpisode"] = episode.Id.ToString() }, [], [], null, [], dates, stats, positions, null),
            string.IsNullOrWhiteSpace(episode.Image) ? [] : [new ImageCandidate("still", episode.Image!, Provider, 8, null, null, null)], [], [], targetId, []);
    }

    private static EntityMetadataProposal PersonRelationship(Character character) => new($"tvdb:person:{character.PeopleId ?? character.Id}", Provider, "person", 0.75m, "series-credit", new EntityMetadataPatch(character.PersonName, null, new Dictionary<string, string> { ["tvdbPerson"] = (character.PeopleId ?? character.Id).ToString() }, [], [], null, [new CreditPatch(character.PersonName!, "actor", character.Name, character.Sort)], new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int>(), null), string.IsNullOrWhiteSpace(character.PersonImgUrl) ? [] : [new ImageCandidate("poster", character.PersonImgUrl!, Provider, 5, null, null, null)], [], [], null, []);

    private static async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, string apiKey) => await FetchAsync<SearchHit[]>($"/search?query={Uri.EscapeDataString(query)}&type=series", apiKey) ?? [];
    private static async Task<T?> FetchAsync<T>(string pathAndQuery, string apiKey) { var token = await TokenAsync(apiKey); using var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}{pathAndQuery}"); req.Headers.Authorization = new("Bearer", token); using var res = await Http.SendAsync(req); if (!res.IsSuccessStatusCode) return default; var wrapped = await res.Content.ReadFromJsonAsync<ApiEnvelope<T>>(PluginHost.JsonOptions); return wrapped is null ? default : wrapped.Data; }
    private static async Task<string> TokenAsync(string apiKey) { using var res = await Http.PostAsJsonAsync($"{Api}/login", new { apikey = apiKey }, PluginHost.JsonOptions); if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"TVDB login failed: {(int)res.StatusCode}"); var data = await res.Content.ReadFromJsonAsync<ApiEnvelope<LoginData>>(PluginHost.JsonOptions); return data?.Data?.Token ?? throw new InvalidOperationException("TVDB login returned no token."); }
    private static IReadOnlyList<ImageCandidate> Artwork(IReadOnlyList<ArtworkItem>? items, int[] types, string kind, string? fallback) { var images = (items ?? []).Where(a => types.Contains(a.Type) && !string.IsNullOrWhiteSpace(a.Image)).OrderByDescending(a => a.Score ?? 0).Select(a => new ImageCandidate(kind, a.Image!, Provider, a.Score is null ? 5 : Math.Min(10, a.Score.Value / 1000m), a.Language, a.Width, a.Height)).ToList(); if (!string.IsNullOrWhiteSpace(fallback) && images.All(i => i.Url != fallback)) images.Add(new(kind, fallback!, Provider, 4, null, null, null)); return images; }
    private static string? ApiKey(IReadOnlyDictionary<string, string> auth) => auth.TryGetValue("apiKey", out var a) && !string.IsNullOrWhiteSpace(a) ? a : auth.TryGetValue("TVDB_API_KEY", out var b) && !string.IsNullOrWhiteSpace(b) ? b : Environment.GetEnvironmentVariable("TVDB_API_KEY");
    private static bool IsExplicitSearch(IdentifyPluginRequest request) => request.Action.Equals("search", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(request.Query.Title) && string.IsNullOrWhiteSpace(request.Query.Url) && request.Query.ExternalIds is not { Count: > 0 };
    private static int? ExternalId(IdentifyPluginRequest request) { foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) if (ids is not null && ids.TryGetValue(Provider, out var value) && int.TryParse(value, out var id)) return id; return null; }
    private static int? AncestorSeriesId(IdentifyPluginRequest request) => request.StructuralContext?.Ancestors.Select(a => a.ExternalIds).Where(ids => ids is not null).Select(ids => ids!.TryGetValue("tvdbSeries", out var v) || ids.TryGetValue(Provider, out v) ? int.TryParse(v, out var id) ? id : (int?)null : null).FirstOrDefault(id => id is not null);
    private static int? Position(IdentifyPluginRequest request, params string[] keys) { foreach (var key in keys) { if (request.StructuralContext?.Positions.TryGetValue(key, out var contextValue) == true) return contextValue; } return null; }
    private static string TvdbNumericId(SearchHit hit) => Regex.Match(hit.TvdbId ?? hit.Id ?? hit.ObjectId ?? string.Empty, "(\\d+)$").Groups[1].Value is { Length: > 0 } id ? id : hit.Id ?? string.Empty;
    private static int? SeriesIdFromUrl(string? url) => UrlId(url, "series");
    private static int? EpisodeIdFromUrl(string? url) => UrlId(url, "episodes");
    private static int? FirstUrlId(IReadOnlyList<string> urls, Func<string?, int?> parser) => urls.Select(parser).FirstOrDefault(id => id is not null);
    private static int? UrlId(string? url, string kind) { if (string.IsNullOrWhiteSpace(url)) return null; var m = Regex.Match(url, $"thetvdb\\.com/(?:{kind}|dereferrer/{kind})/(\\d+)", RegexOptions.IgnoreCase); return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null; }
    private static int? Year(string? value) => int.TryParse((value ?? string.Empty).Split('-')[0], out var year) ? year : null;

    private sealed record ApiEnvelope<T>(T? Data);
    private sealed record LoginData(string Token);
    private sealed record SearchHit(string? Id, string? TvdbId, string? ObjectId, string? Name, Dictionary<string, string>? Translations, string? Overview, Dictionary<string, string>? Overviews, string? ImageUrl, string? Thumbnail, string? FirstAirTime, string? Year, int? Score);
    private sealed record SeriesExtended(int Id, string Name, string? Slug, string? Image, string? FirstAired, string? LastAired, Status? Status, int? AverageRuntime, string? Overview, Network? OriginalNetwork, Network? LatestNetwork, SeasonSummary[]? Seasons, ArtworkItem[]? Artworks, Genre[]? Genres, Character[]? Characters);
    private sealed record Status(string? Name);
    private sealed record Network(string? Name);
    private sealed record Genre(string? Name);
    private sealed record SeasonSummary(int Id, int Number, string? Name, string? Image, SeasonType? Type);
    private sealed record SeasonType(string? Type);
    private sealed record ArtworkItem(string? Image, string? Language, int Type, int? Score, int? Width, int? Height);
    private sealed record Character(int Id, string? Name, string? PersonName, int? PeopleId, string? PersonImgUrl, int? Sort);
    private sealed record SeasonExtended(int Id, int Number, string? Name, string? Image, int SeriesId, Episode[]? Episodes, string? Overview = null);
    private sealed record Episode(int Id, int? SeriesId, int? SeasonNumber, int? Number, string? Name, string? Overview, string? Aired, int? Runtime, string? Image);
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
