using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

var response = await PluginHost.RunAsync(args, AniListPlugin.IdentifyAsync);
Console.Write(JsonSerializer.Serialize(response, PluginHost.JsonOptions));

internal static partial class AniListPlugin {
    internal static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string PluginId = "anilist";
    private const string PrimaryIdentityNamespace = "anilist";
    private const string SeasonIdentityNamespace = "anilistseason";
    private const string EpisodeIdentityNamespace = "anilistepisode";
    private const string MalIdentityNamespace = "mal";
    private const string Api = "https://graphql.anilist.co";

    private static class SearchFields {
        public const string Title = "title";
        public const string SeriesTitle = "seriesTitle";
        public const string Year = "year";
    }

    public static async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        if (!IsSupportedKind(request.Entity.Kind)) return IdentifyPluginResult.None();

        if (IsEpisodeKind(request.Entity.Kind) &&
            TryGetEpisodeIdentity(request, out var episodeMediaId, out var localEpisode, out var identitySeason, out var identitySort)) {
            var episodeMedia = await DetailAsync(episodeMediaId);
            return IdentifyPluginResult.ForProposal(EpisodeShell(
                episodeMedia,
                localEpisode,
                request.Entity.Id,
                identitySeason,
                identitySort,
                "external-id"));
        }

        if (request.Entity.Kind.Equals("video-season", StringComparison.OrdinalIgnoreCase) &&
            TryGetSeasonIdentity(request, out var seasonMediaId, out var identitySeasonNumber)) {
            var seasonMedia = await DetailAsync(seasonMediaId);
            var seasonProposal = identitySeasonNumber == 0
                ? await SeasonFromContextAsync(seasonMedia, 0, request.Entity.Id)
                : SeasonShell(seasonMedia, identitySeasonNumber, request.Entity.Id, "external-id");
            return IdentifyPluginResult.ForProposal(seasonProposal);
        }

        var id = ExternalId(request) ?? IdFromUrl(request.Query.Url) ?? FirstUrlId(request.Hints.Urls) ?? AncestorExternalId(request);
        var seasonNumber = PositionValue(request, "seasonNumber", "season");
        var episodeNumber = PositionValue(request, "episodeNumber", "episode", "sortOrder");
        if (id is not null && !IsExplicitSearch(request)) {
            var media = await DetailAsync(id.Value);
            if (IsEpisodeKind(request.Entity.Kind) && episodeNumber is { } episode) {
                var episodeProposal = await EpisodeFromContextAsync(media, seasonNumber, episode, request.Entity.Id);
                return episodeProposal is null ? IdentifyPluginResult.None() : IdentifyPluginResult.ForProposal(episodeProposal);
            }

            if (request.Entity.Kind.Equals("video-season", StringComparison.OrdinalIgnoreCase)) {
                return IdentifyPluginResult.ForProposal(await SeasonFromContextAsync(media, seasonNumber ?? 1, request.Entity.Id));
            }

            return IdentifyPluginResult.ForProposal(await ToProposalWithChildrenAsync(
                media,
                request.Entity.Kind,
                request.Entity.Id,
                "external-id",
                request.IncludeStructuralChildren));
        }

        var (title, year) = SearchInput(request);
        if (string.IsNullOrWhiteSpace(title)) return IdentifyPluginResult.None();
        var results = await SearchAsync(title, year, SearchLimit(request));
        return IdentifyPluginResult.ForCandidates(results.Select(media => new EntitySearchCandidate(new Dictionary<string, string> { [PrimaryIdentityNamespace] = media.Id.ToString() }, Title(media), Year(media.StartDate), StripHtml(media.Description), media.CoverImage?.Large ?? media.CoverImage?.ExtraLarge, media.Popularity)).ToArray());
    }

    internal static EntityMetadataProposal ToProposal(Media media, string requestedKind, Guid targetId, string reason) {
        var kind = requestedKind.Equals("video", StringComparison.OrdinalIgnoreCase) && IsMovieLike(media) ? "video" : "video-series";
        var tags = (media.Genres ?? []).Concat((media.Tags ?? []).Where(t => (t.Rank ?? 0) >= 60).Select(t => t.Name)).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(20).Cast<string>().ToArray();
        var dates = new Dictionary<string, string>(); if (DateString(media.StartDate) is { } start) dates["started"] = start; if (DateString(media.EndDate) is { } end) dates["ended"] = end;
        var stats = new Dictionary<string, int>(); if (media.Episodes is int episodes) stats["episodeCount"] = episodes; if (media.Duration is int duration) stats["runtimeMinutes"] = duration; if (media.Popularity is int popularity) stats["popularity"] = popularity;
        var external = new Dictionary<string, string> { [PrimaryIdentityNamespace] = media.Id.ToString() }; if (media.IdMal is int mal) external[MalIdentityNamespace] = mal.ToString();
        var images = new List<ImageCandidate>(); if (!string.IsNullOrWhiteSpace(media.CoverImage?.ExtraLarge)) images.Add(new("poster", media.CoverImage!.ExtraLarge!, PluginId, 10, null, null, null)); if (!string.IsNullOrWhiteSpace(media.CoverImage?.Large)) images.Add(new("poster", media.CoverImage!.Large!, PluginId, 8, null, null, null)); if (!string.IsNullOrWhiteSpace(media.BannerImage)) images.Add(new("backdrop", media.BannerImage!, PluginId, 7, null, null, null));
        var relationships = CharacterRelationships(media).Concat(StudioRelationships(media)).ToArray();
        return new EntityMetadataProposal($"anilist:{media.Id}", PluginId, kind, 0.9m, reason, new EntityMetadataPatch(Title(media), StripHtml(media.Description), external, [media.SiteUrl ?? $"https://anilist.co/anime/{media.Id}"], tags, PrimaryStudio(media), [], dates, stats, new Dictionary<string, int>(), media.Format), images, [], [], targetId, relationships);
    }

    internal static async Task<EntityMetadataProposal> ToProposalWithChildrenAsync(
        Media media,
        string requestedKind,
        Guid targetId,
        string reason,
        bool includeStructuralChildren) {
        var proposal = ToProposal(media, requestedKind, targetId, reason);
        if (!includeStructuralChildren || !proposal.TargetKind.Equals("video-series", StringComparison.OrdinalIgnoreCase)) {
            return proposal;
        }

        var parts = await SeasonPartsAsync(media);
        var seasons = parts
            .Select((part, index) => SeasonShell(part, index + 1, null, "series-children"))
            .ToArray();
        return proposal with { Children = seasons };
    }

    private static async Task<EntityMetadataProposal> SeasonFromContextAsync(Media root, int seasonNumber, Guid targetId) {
        var parts = await SeasonPartsAsync(root);
        if (seasonNumber == 0) return FlatSeasonShell(parts, targetId);
        var part = parts.ElementAtOrDefault(seasonNumber - 1) ?? root;
        return SeasonShell(part, seasonNumber, targetId, "parent-context");
    }

    private static async Task<EntityMetadataProposal?> EpisodeFromContextAsync(Media root, int? seasonNumber, int episodeNumber, Guid targetId) {
        var parts = await SeasonPartsAsync(root);
        if (seasonNumber is null or 0) {
            var mapped = AbsoluteEpisode(parts, episodeNumber);
            return mapped is null
                ? EpisodeShell(root, episodeNumber, targetId, 0, episodeNumber, "parent-context")
                : EpisodeShell(mapped.Value.Media, mapped.Value.EpisodeNumber, targetId, 0, episodeNumber, "parent-context");
        }

        var part = parts.ElementAtOrDefault(seasonNumber.Value - 1) ?? root;
        return EpisodeShell(part, episodeNumber, targetId, seasonNumber.Value, episodeNumber, "parent-context");
    }

    internal static EntityMetadataProposal SeasonShell(Media media, int seasonNumber, Guid? targetId, string reason) {
        var episodeCount = Math.Min(media.Episodes ?? 0, 200);
        var stats = episodeCount > 0 ? new Dictionary<string, int> { ["episodeCount"] = episodeCount } : new Dictionary<string, int>();
        var dates = new Dictionary<string, string>(); if (DateString(media.StartDate) is { } start) dates["started"] = start; if (DateString(media.EndDate) is { } end) dates["ended"] = end;
        var images = new List<ImageCandidate>(); if (!string.IsNullOrWhiteSpace(media.CoverImage?.ExtraLarge)) images.Add(new("poster", media.CoverImage!.ExtraLarge!, PluginId, 10, null, null, null)); if (!string.IsNullOrWhiteSpace(media.CoverImage?.Large)) images.Add(new("poster", media.CoverImage!.Large!, PluginId, 8, null, null, null));
        var episodes = episodeCount > 0
            ? Enumerable.Range(1, episodeCount).Select(i => EpisodeShell(media, i, null, seasonNumber, i, "season-cascade")).ToArray()
            : [];
        return new EntityMetadataProposal($"anilist:{media.Id}:season:{seasonNumber}", PluginId, "video-season", 0.8m, reason, new EntityMetadataPatch($"Season {seasonNumber}", StripHtml(media.Description), new Dictionary<string, string> {
            [SeasonIdentityNamespace] = FormatSeasonIdentity(media.Id, seasonNumber),
            [PrimaryIdentityNamespace] = media.Id.ToString()
        }, [media.SiteUrl ?? $"https://anilist.co/anime/{media.Id}"], [], null, [], dates, stats, new Dictionary<string, int> { ["seasonNumber"] = seasonNumber }, null), images, episodes, [], targetId, []);
    }

    internal static EntityMetadataProposal FlatSeasonShell(IReadOnlyList<Media> parts, Guid? targetId) {
        var first = parts.First();
        var episodeCount = parts.Sum(part => Math.Min(part.Episodes ?? 0, 200));
        var episodes = new List<EntityMetadataProposal>();
        var absolute = 1;
        foreach (var part in parts) {
            foreach (var localEpisode in Enumerable.Range(1, Math.Min(part.Episodes ?? 0, 200))) {
                episodes.Add(EpisodeShell(part, localEpisode, null, 0, absolute, "absolute-cascade"));
                absolute++;
            }
        }

        return new EntityMetadataProposal($"anilist:{first.Id}:season:0", PluginId, "video-season", 0.8m, "parent-context", new EntityMetadataPatch("Season 0", null, new Dictionary<string, string> {
            [SeasonIdentityNamespace] = FormatSeasonIdentity(first.Id, 0),
            [PrimaryIdentityNamespace] = first.Id.ToString()
        }, [first.SiteUrl ?? $"https://anilist.co/anime/{first.Id}"], [], null, [], new Dictionary<string, string>(), episodeCount > 0 ? new Dictionary<string, int> { ["episodeCount"] = episodeCount } : new Dictionary<string, int>(), new Dictionary<string, int> { ["seasonNumber"] = 0 }, null), [], episodes, [], targetId, []);
    }

    internal static EntityMetadataProposal EpisodeShell(Media media, int episodeNumber, Guid? targetId, int seasonNumber, int sortOrder, string reason) => new($"anilist:{media.Id}:s{seasonNumber}:e{sortOrder}", PluginId, "video-episode", 0.65m, reason, new EntityMetadataPatch($"Episode {sortOrder}", null, new Dictionary<string, string> {
        [EpisodeIdentityNamespace] = FormatEpisodeIdentity(media.Id, episodeNumber, seasonNumber, sortOrder),
        [PrimaryIdentityNamespace] = media.Id.ToString()
    }, [], [], null, [], new Dictionary<string, string>(), media.Duration is int duration ? new Dictionary<string, int> { ["runtimeMinutes"] = duration } : new Dictionary<string, int>(), new Dictionary<string, int> { ["seasonNumber"] = seasonNumber, ["episodeNumber"] = sortOrder, ["sortOrder"] = sortOrder }, null), [], [], [], targetId, []);
    private static (Media Media, int EpisodeNumber)? AbsoluteEpisode(IReadOnlyList<Media> parts, int absoluteEpisode) {
        var offset = 0;
        foreach (var part in parts) {
            var count = Math.Min(part.Episodes ?? 0, 200);
            if (absoluteEpisode <= offset + count) return (part, absoluteEpisode - offset);
            offset += count;
        }

        return null;
    }

    private static async Task<IReadOnlyList<Media>> SeasonPartsAsync(Media root) {
        var byId = new Dictionary<int, Media> { [root.Id] = root };
        var pending = new Queue<int>((root.Relations?.Edges ?? [])
            .Where(IsSeasonRelation)
            .Select(edge => edge.Node?.Id ?? 0)
            .Where(id => id > 0));
        var visited = new HashSet<int> { root.Id };

        while (pending.Count > 0 && byId.Count < 10) {
            var id = pending.Dequeue();
            if (!visited.Add(id)) continue;
            var related = await DetailAsync(id);
            if (!IsSeasonPart(related)) continue;
            byId[related.Id] = related;
            foreach (var next in (related.Relations?.Edges ?? []).Where(IsSeasonRelation).Select(edge => edge.Node?.Id ?? 0).Where(next => next > 0 && !visited.Contains(next))) pending.Enqueue(next);
        }

        return byId.Values
            .Where(IsSeasonPart)
            .OrderBy(media => media.StartDate?.Year ?? int.MaxValue)
            .ThenBy(media => media.StartDate?.Month ?? 13)
            .ThenBy(media => media.StartDate?.Day ?? 32)
            .ThenBy(media => media.Id)
            .ToArray();
    }

    private static bool IsSeasonRelation(MediaRelationEdge edge) =>
        edge.Node is not null &&
        (edge.RelationType.Equals("PREQUEL", StringComparison.OrdinalIgnoreCase) ||
            edge.RelationType.Equals("SEQUEL", StringComparison.OrdinalIgnoreCase));

    private static bool IsSeasonPart(Media media) =>
        media.Episodes is > 0 &&
        !string.Equals(media.Format, "MOVIE", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(media.Format, "SPECIAL", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<EntityMetadataProposal> CharacterRelationships(Media media) => (media.Characters?.Edges ?? []).Take(20).Select(edge => edge.Node?.Name?.Full).Where(name => !string.IsNullOrWhiteSpace(name)).Select((name, i) => new EntityMetadataProposal($"anilist:character:{Slug(name!)}", PluginId, "person", 0.6m, "character", new EntityMetadataPatch(name, null, new Dictionary<string, string>(), [], [], null, [new CreditPatch(name!, "character", null, i)], new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int>(), null), [], [], [], null, []));
    private static IEnumerable<EntityMetadataProposal> StudioRelationships(Media media) => (media.Studios?.Nodes ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Name)).Take(5).Select(studio => new EntityMetadataProposal($"anilist:studio:{Slug(studio.Name!)}", PluginId, "studio", 0.7m, "studio", new EntityMetadataPatch(studio.Name, null, new Dictionary<string, string>(), [], [], null, [], new Dictionary<string, string>(), new Dictionary<string, int>(), new Dictionary<string, int>(), null), [], [], [], null, []));

    private static async Task<Media> DetailAsync(int id) { var data = await GraphQlAsync<DetailData>(DetailQuery, new { id }); return data.Media ?? throw new InvalidOperationException("AniList media not found."); }
    private static async Task<IReadOnlyList<Media>> SearchAsync(string search, int? year, int limit) { var data = await GraphQlAsync<SearchData>(SearchQuery, new { search, year, perPage = limit }); return data.Page?.Media ?? []; }
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
    private static int? ExternalId(IdentifyPluginRequest request) { foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) if (TryGetIdentity(ids, PrimaryIdentityNamespace, out var value) && int.TryParse(value, out var id)) return id; return null; }
    private static int? AncestorExternalId(IdentifyPluginRequest request) { foreach (var ancestor in request.StructuralContext?.Ancestors ?? []) if (TryGetIdentity(ancestor.ExternalIds, PrimaryIdentityNamespace, out var value) && int.TryParse(value, out var id)) return id; return null; }
    private static int? PositionValue(IdentifyPluginRequest request, params string[] keys) { foreach (var key in keys) if (request.StructuralContext?.Positions.TryGetValue(key, out var value) == true) return value; return null; }
    private static int? FirstUrlId(IReadOnlyList<string> urls) => urls.Select(IdFromUrl).FirstOrDefault(id => id is not null);
    private static int? IdFromUrl(string? url) { if (string.IsNullOrWhiteSpace(url)) return null; var match = Regex.Match(url, "anilist\\.co/anime/(\\d+)", RegexOptions.IgnoreCase); return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null; }
    private static bool IsExplicitSearch(IdentifyPluginRequest request) => request.Action.Equals("search", StringComparison.OrdinalIgnoreCase) && (!string.IsNullOrWhiteSpace(request.Query.Title) || request.Query.Fields?.Values.Any(value => !string.IsNullOrWhiteSpace(value)) == true) && string.IsNullOrWhiteSpace(request.Query.Url) && request.Query.ExternalIds is not { Count: > 0 };
    private static bool IsSupportedKind(string kind) => kind.Equals("video-series", StringComparison.OrdinalIgnoreCase) || kind.Equals("video-season", StringComparison.OrdinalIgnoreCase) || IsEpisodeKind(kind);
    private static bool IsEpisodeKind(string kind) => kind.Equals("video", StringComparison.OrdinalIgnoreCase) || kind.Equals("video-episode", StringComparison.OrdinalIgnoreCase);

    private const string BasicFields = """
      id idMal description format episodes duration status season seasonYear bannerImage averageScore meanScore popularity siteUrl isAdult genres
      title { romaji english native }
      startDate { year month day }
      endDate { year month day }
      coverImage { extraLarge large medium color }
      tags { name rank }
      studios { nodes { name isAnimationStudio } }
      characters(perPage: 25, sort: [ROLE, RELEVANCE]) { edges { node { name { full } } } }
      """;
    private static readonly string DetailQuery = $"query ($id: Int!) {{ Media(id: $id, type: ANIME) {{ {BasicFields} relations {{ edges {{ relationType node {{ {BasicFields} }} }} }} }} }}";
    private static readonly string SearchQuery = $"query ($search: String!, $year: Int, $perPage: Int!) {{ Page(perPage: $perPage) {{ media(search: $search, seasonYear: $year, type: ANIME, isAdult: false, sort: [SEARCH_MATCH, POPULARITY_DESC]) {{ {BasicFields} }} }} }}";

    // AniList caps root Page queries at 50 entries.
    private static int SearchLimit(IdentifyPluginRequest request) => Math.Clamp(request.Query.Limit, 1, 50);

    internal static (string? Title, int? Year) SearchInput(IdentifyPluginRequest request) {
        var title = SearchField(request, request.Entity.Kind.Equals("video-series", StringComparison.OrdinalIgnoreCase) ? SearchFields.SeriesTitle : SearchFields.Title) ??
            SearchField(request, SearchFields.Title, SearchFields.SeriesTitle) ??
            request.Query.Title ??
            request.Hints.Title ??
            request.Entity.Title;
        int? year = int.TryParse(SearchField(request, SearchFields.Year), out var parsed) && parsed is >= 1900 and <= 2200
            ? parsed
            : null;
        return (title, year);
    }

    internal static string FormatSeasonIdentity(int mediaId, int seasonNumber) => $"{mediaId}:{seasonNumber}";

    internal static bool TryParseSeasonIdentity(string? value, out int mediaId, out int seasonNumber) {
        mediaId = 0;
        seasonNumber = 0;
        if (string.IsNullOrEmpty(value)) return false;
        var separator = value.IndexOf(':');
        return separator > 0 && separator == value.LastIndexOf(':') &&
            int.TryParse(value[..separator], out mediaId) && mediaId > 0 &&
            int.TryParse(value[(separator + 1)..], out seasonNumber) && seasonNumber >= 0;
    }

    internal static string FormatEpisodeIdentity(int mediaId, int episodeNumber, int seasonNumber, int sortOrder) =>
        $"{mediaId}:{episodeNumber}:{seasonNumber}:{sortOrder}";

    internal static bool TryParseEpisodeIdentity(
        string? value,
        out int mediaId,
        out int episodeNumber,
        out int seasonNumber,
        out int sortOrder) {
        mediaId = 0;
        episodeNumber = 0;
        seasonNumber = 0;
        sortOrder = 0;
        if (string.IsNullOrEmpty(value)) return false;
        var parts = value.Split(':', StringSplitOptions.None);
        return parts.Length == 4 &&
            int.TryParse(parts[0], out mediaId) && mediaId > 0 &&
            int.TryParse(parts[1], out episodeNumber) && episodeNumber > 0 &&
            int.TryParse(parts[2], out seasonNumber) && seasonNumber >= 0 &&
            int.TryParse(parts[3], out sortOrder) && sortOrder > 0;
    }

    private static bool TryGetSeasonIdentity(IdentifyPluginRequest request, out int mediaId, out int seasonNumber) {
        foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) {
            if (TryGetIdentity(ids, SeasonIdentityNamespace, out var value) &&
                TryParseSeasonIdentity(value, out mediaId, out seasonNumber)) return true;
        }
        mediaId = 0;
        seasonNumber = 0;
        return false;
    }

    private static bool TryGetEpisodeIdentity(
        IdentifyPluginRequest request,
        out int mediaId,
        out int episodeNumber,
        out int seasonNumber,
        out int sortOrder) {
        foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) {
            if (TryGetIdentity(ids, EpisodeIdentityNamespace, out var value) &&
                TryParseEpisodeIdentity(value, out mediaId, out episodeNumber, out seasonNumber, out sortOrder)) return true;
        }
        mediaId = 0;
        episodeNumber = 0;
        seasonNumber = 0;
        sortOrder = 0;
        return false;
    }

    private static string? SearchField(IdentifyPluginRequest request, params string[] keys) {
        foreach (var key in keys) {
            if (TryGetIdentity(request.Query.Fields, key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static bool TryGetIdentity(IReadOnlyDictionary<string, string>? values, string key, out string value) {
        foreach (var pair in values ?? new Dictionary<string, string>()) {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) {
                value = pair.Value;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    internal sealed record GraphQlEnvelope<T>(T? Data, GraphQlError[]? Errors);
    internal sealed record GraphQlError(string Message);
    internal sealed record DetailData(Media? Media);
    internal sealed record SearchData(Page? Page);
    internal sealed record Page(Media[]? Media);
    internal sealed record Media(int Id, int? IdMal, MediaTitle? Title, string? Description, string? Format, int? Episodes, int? Duration, FuzzyDate? StartDate, FuzzyDate? EndDate, Image? CoverImage, string? BannerImage, int? Popularity, string[]? Genres, MediaTag[]? Tags, StudioConnection? Studios, string? SiteUrl, CharacterConnection? Characters, MediaRelationConnection? Relations = null);
    internal sealed record MediaTitle(string? Romaji, string? English, string? Native);
    internal sealed record FuzzyDate(int? Year, int? Month, int? Day);
    internal sealed record Image(string? ExtraLarge, string? Large, string? Medium, string? Color);
    internal sealed record MediaTag(string? Name, int? Rank);
    internal sealed record StudioConnection(Studio[]? Nodes);
    internal sealed record Studio(string? Name, bool? IsAnimationStudio);
    internal sealed record CharacterConnection(CharacterEdge[]? Edges);
    internal sealed record CharacterEdge(Character? Node);
    internal sealed record Character(CharacterName? Name);
    internal sealed record CharacterName(string? Full);
    internal sealed record MediaRelationConnection(MediaRelationEdge[]? Edges);
    internal sealed record MediaRelationEdge(string RelationType, Media? Node);
}

internal static class PluginHost { public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = false }; public static async Task<IdentifyPluginResponse> RunAsync(string[] args, Func<IdentifyPluginRequest, Task<IdentifyPluginResult>> identify) { try { if (args.Length == 0) return new(false, null, "Missing request JSON path."); var request = JsonSerializer.Deserialize<IdentifyPluginRequest>(await File.ReadAllTextAsync(args[0]), JsonOptions); if (request is null) return new(false, null, "Request JSON was empty or invalid."); return new(true, await identify(request), null); } catch (Exception ex) { return new(false, null, ex.Message); } } }
internal sealed record IdentifyPluginRequest(
    int ProtocolVersion,
    string Action,
    IReadOnlyDictionary<string, string> Auth,
    IdentifyEntitySnapshot Entity,
    IdentifyQuery Query,
    IdentifyMatchHints Hints,
    IdentifyStructuralContext? StructuralContext = null,
    bool IncludeNsfw = false,
    bool IncludeRelationshipDetails = true,
    bool IncludeStructuralChildren = false);
internal sealed record IdentifyStructuralContext(IReadOnlyList<IdentifyEntitySnapshot> Ancestors, IReadOnlyDictionary<string, int> Positions);
internal sealed record IdentifyEntitySnapshot(Guid Id, string Kind, string Title, IReadOnlyDictionary<string, string>? ExternalIds = null, IReadOnlyList<string>? Urls = null);
internal sealed record IdentifyQuery(string? Title, string? Url, IReadOnlyDictionary<string, string>? ExternalIds, bool? RequireChoice = null, IReadOnlyDictionary<string, string>? Fields = null, int Limit = 25);
internal sealed record IdentifyMatchHints(IReadOnlyDictionary<string, string> ExternalIds, IReadOnlyList<string> Urls, string? Title, string? FilePath);
internal sealed record ImageCandidate(string Kind, string Url, string Source, decimal? Rank, string? Language, int? Width, int? Height);
internal sealed record EntitySearchCandidate(IReadOnlyDictionary<string, string> ExternalIds, string Title, int? Year, string? Overview, string? PosterUrl, decimal? Popularity);
internal sealed record CreditPatch(string Name, string Role, string? Character, int? SortOrder);
internal sealed record EntityMetadataFlagsPatch(bool? IsFavorite, bool? IsNsfw, bool? IsOrganized);
internal sealed record EntityMetadataPatch(string? Title, string? Description, IReadOnlyDictionary<string, string> ExternalIds, IReadOnlyList<string> Urls, IReadOnlyList<string> Tags, string? Studio, IReadOnlyList<CreditPatch> Credits, IReadOnlyDictionary<string, string> Dates, IReadOnlyDictionary<string, int> Stats, IReadOnlyDictionary<string, int> Positions, string? Classification) { public int? Rating { get; init; } public EntityMetadataFlagsPatch? Flags { get; init; } }
internal sealed record EntityMetadataProposal(string ProposalId, string Provider, string TargetKind, decimal? Confidence, string? MatchReason, EntityMetadataPatch Patch, IReadOnlyList<ImageCandidate> Images, IReadOnlyList<EntityMetadataProposal> Children, IReadOnlyList<EntitySearchCandidate> Candidates, Guid? TargetEntityId = null, IReadOnlyList<EntityMetadataProposal>? Relationships = null);
internal sealed record IdentifyPluginResult(string Type, EntityMetadataProposal? Proposal, IReadOnlyList<EntitySearchCandidate> Candidates) { public static IdentifyPluginResult ForProposal(EntityMetadataProposal proposal) => new("proposal", proposal, []); public static IdentifyPluginResult ForCandidates(IReadOnlyList<EntitySearchCandidate> candidates) => new("candidates", null, candidates); public static IdentifyPluginResult None() => new("none", null, []); }
internal sealed record IdentifyPluginResponse(bool Ok, IdentifyPluginResult? Result, string? Error);
