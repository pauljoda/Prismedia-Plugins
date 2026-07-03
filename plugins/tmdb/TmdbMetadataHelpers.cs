using System.Text.RegularExpressions;

namespace Prismedia.Plugin.Tmdb;

internal static class TmdbMetadataHelpers {
    private static readonly Regex TmdbUrlRegex = new(
        @"themoviedb\.org/(movie|tv|person|company)/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ScoredResult> Score(string query, IReadOnlyList<TmdbSearchResult> results, Func<TmdbSearchResult, string> title) =>
        results
            .Take(20)
            .Select((result, index) => new ScoredResult(result, Similarity(query, title(result)), index))
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.Order)
            .ToList();

    public static decimal Similarity(string a, string b) {
        var left = Normalize(a).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var right = Normalize(b).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (left.Count == 0 || right.Count == 0) {
            return 0;
        }

        var intersection = left.Count(right.Contains);
        var union = left.Union(right, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (decimal)intersection / union;
    }

    public static string Normalize(string value) =>
        Regex.Replace(
            Regex.Replace(value.ToLowerInvariant(), @"[!?.""'`]", ""),
            @"[^a-z0-9\s]+",
            " ")
            .Trim();

    public static EntitySearchCandidate ToCandidate(ScoredResult row, string mediaType) {
        var result = row.Result;
        return new(
            $"tmdb:{mediaType}:{result.Id}",
            new Dictionary<string, string> { ["tmdb"] = result.Id.ToString() },
            result.Name ?? result.Title ?? string.Empty,
            result.Overview,
            ImageUrl(result.PosterPath ?? result.ProfilePath ?? result.LogoPath, "w342"),
            ParseYear(result.FirstAirDate ?? result.ReleaseDate),
            "TMDB",
            row.Score,
            "title-search");
    }

    public static EntitySearchCandidate ToCandidate(TmdbSearchResult result, string mediaType) =>
        new(
            $"tmdb:{mediaType}:{result.Id}",
            new Dictionary<string, string> { ["tmdb"] = result.Id.ToString() },
            result.Name ?? result.Title ?? string.Empty,
            result.Overview,
            ImageUrl(result.PosterPath ?? result.ProfilePath ?? result.LogoPath, "w342"),
            ParseYear(result.FirstAirDate ?? result.ReleaseDate),
            "TMDB",
            null,
            "title-search");

    public static int? ExtractTmdbId(IReadOnlyDictionary<string, string>? externalIds) {
        if (externalIds is null || !externalIds.TryGetValue("tmdb", out var value)) {
            return null;
        }

        var match = Regex.Match(value, @"\d+");
        return match.Success && int.TryParse(match.Value, out var id) ? id : null;
    }

    public static bool TryEpisodeContext(IdentifyPluginRequest request, out int seriesId, out int seasonNumber, out int episodeNumber) {
        seriesId = SeriesTmdbIdFromContext(request) ?? 0;
        seasonNumber = PositionValue(request, "seasonNumber", "season") ?? SeasonNumberFromAncestor(request) ?? 0;
        episodeNumber = PositionValue(request, "episodeNumber", "episode", "sortOrder") ?? 0;
        return seriesId > 0 && seasonNumber > 0 && episodeNumber > 0;
    }

    /// <summary>
    /// Parses the composite season work id ("{seriesId}_s{seasonNumber}") from the request's external
    /// ids. This is the canonical season id Prismedia stores — TMDB has no season-by-id endpoint, so
    /// the composite form is the only one a later lookup can resolve.
    /// </summary>
    public static bool TryParseSeasonWorkId(IdentifyPluginRequest request, out int seriesId, out int seasonNumber) {
        seriesId = 0;
        seasonNumber = 0;
        foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) {
            if (ids is null || !ids.TryGetValue("tmdb", out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split("_s", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var series) && int.TryParse(parts[1], out var season)) {
                seriesId = series;
                seasonNumber = season;
                return true;
            }
        }

        return false;
    }

    public static int? SeriesTmdbIdFromContext(IdentifyPluginRequest request) {
        var series = request.StructuralContext?.Ancestors
            .FirstOrDefault(ancestor => ancestor.Kind.Equals("video-series", StringComparison.OrdinalIgnoreCase));
        return ExtractTmdbId(series?.ExternalIds);
    }

    public static int? PositionValue(IdentifyPluginRequest request, params string[] keys) {
        var positions = request.StructuralContext?.Positions;
        if (positions is null) {
            return null;
        }

        foreach (var key in keys) {
            if (positions.TryGetValue(key, out var value)) {
                return value;
            }
        }

        return null;
    }

    public static bool ParseTmdbUrl(string url) => ParseTmdbUrlValue(url) is not null;

    public static TmdbUrl? ParseTmdbUrlValue(string url) {
        var match = TmdbUrlRegex.Match(url);
        return match.Success && int.TryParse(match.Groups[2].Value, out var id)
            ? new TmdbUrl(match.Groups[1].Value.ToLowerInvariant(), id)
            : null;
    }

    public static string TmdbUrl(string mediaType, int id) =>
        $"https://www.themoviedb.org/{mediaType}/{id}";

    public static string? ImageUrl(string? path, string size) =>
        string.IsNullOrWhiteSpace(path) ? null : $"{TmdbConstants.Image}/{size}{path}";

    public static string? MapStatus(string? status) {
        if (string.IsNullOrWhiteSpace(status)) {
            return null;
        }

        var lower = status.ToLowerInvariant();
        if (lower.Contains("cancel")) {
            return "canceled";
        }

        if (lower.Contains("ended")) {
            return "ended";
        }

        return lower.Contains("return") || lower.Contains("production") || lower.Contains("pilot") || lower.Contains("planned")
            ? "returning"
            : "unknown";
    }

    private static int? SeasonNumberFromAncestor(IdentifyPluginRequest request) {
        var season = request.StructuralContext?.Ancestors
            .FirstOrDefault(ancestor => ancestor.Kind.Equals("video-season", StringComparison.OrdinalIgnoreCase));
        return season is null ? null : PositionFromTitle(season.Title, "season");
    }

    private static int? PositionFromTitle(string? title, string label) {
        if (string.IsNullOrWhiteSpace(title)) {
            return null;
        }

        var match = Regex.Match(title, $@"{label}\s+(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static int? ParseYear(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length >= 4 && int.TryParse(value[..4], out var year) ? year : null;
}
