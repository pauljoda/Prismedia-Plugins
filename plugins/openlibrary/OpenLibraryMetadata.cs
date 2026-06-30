using System.Text.Json;
using System.Text.RegularExpressions;

namespace Prismedia.Plugin.OpenLibrary;

internal static partial class OpenLibraryMetadata {
    public const string Provider = "openlibrary";
    public const string WorkIdKey = "openlibraryWork";
    public const string EditionIdKey = "openlibraryEdition";
    public const string AuthorIdKey = "openlibraryAuthor";
    public const string SeriesKey = "openlibrarySeries";

    private static readonly HashSet<string> NoisySubjects = new(StringComparer.OrdinalIgnoreCase) {
        "accessible book",
        "protected daisy",
        "overdrive",
        "general",
        "fiction",
        "novela"
    };

    public static string? WorkIdFromKey(string? key) => IdFromKey(key, "works", "W");
    public static string? EditionIdFromKey(string? key) => IdFromKey(key, "books", "M");
    public static string? AuthorIdFromKey(string? key) => IdFromKey(key, "authors", "A");

    public static string? OpenLibraryId(IReadOnlyDictionary<string, string>? ids, params string[] keys) {
        if (ids is null) return null;
        foreach (var key in keys) {
            if (ids.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) {
                return value.Trim();
            }
        }

        return null;
    }

    public static string? Isbn(IReadOnlyDictionary<string, string>? ids) =>
        OpenLibraryId(ids, "isbn", "isbn13", "isbn10");

    public static string? WorkIdFromUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = WorkUrlRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string? EditionIdFromUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = EditionUrlRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string? IsbnFromUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = IsbnUrlRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string? AuthorIdFromUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = AuthorUrlRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string? SeriesFromProviderId(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.StartsWith("series:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["series:".Length..].Trim()
            : null;
    }

    public static string WorkUrl(string workId) => $"https://openlibrary.org/works/{workId}";
    public static string EditionUrl(string editionId) => $"https://openlibrary.org/books/{editionId}";
    public static string AuthorUrl(string authorId) => $"https://openlibrary.org/authors/{authorId}";
    public static string SearchUrl(string query) => $"https://openlibrary.org/search?q={Uri.EscapeDataString(query)}";
    public static string CoverUrl(int coverId) => $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg?default=false";
    public static string AuthorPhotoUrl(int photoId) => $"https://covers.openlibrary.org/a/id/{photoId}-L.jpg?default=false";

    /// <summary>
    /// Author photo addressed by Open Library id (OLID) rather than a numeric photo id, so a search candidate can
    /// carry a portrait without a second author fetch. <c>default=false</c> makes a photo-less author 404 so the
    /// client falls back to its placeholder instead of a blank image.
    /// </summary>
    public static string AuthorPhotoUrlByOlid(string authorId) => $"https://covers.openlibrary.org/a/olid/{authorId}-M.jpg?default=false";

    public static string? JsonText(JsonElement? value) {
        if (value is not JsonElement element) return null;
        return element.ValueKind switch {
            JsonValueKind.String => CleanText(element.GetString()),
            JsonValueKind.Object when element.TryGetProperty("value", out var nested) => CleanText(nested.GetString()),
            _ => null
        };
    }

    public static string? CleanText(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        text = MarkdownLinkRegex().Replace(text, "$1");
        text = MarkdownMarkerRegex().Replace(text, "");
        text = HeadingRegex().Replace(text, "");
        text = MultiBlankLineRegex().Replace(text, "\n\n");
        return text.Length <= 5000 ? text : $"{text[..4997].Trim()}...";
    }

    public static string Normalize(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return NormalizeRegex().Replace(value.ToLowerInvariant(), " ").Trim();
    }

    public static string? SeriesName(IEnumerable<string?> subjects) =>
        subjects
            .Where(subject => !string.IsNullOrWhiteSpace(subject))
            .Select(subject => subject!.Trim())
            .Where(subject => subject.StartsWith("series:", StringComparison.OrdinalIgnoreCase))
            .Select(subject => subject["series:".Length..].Trim())
            .FirstOrDefault(subject => subject.Length > 0);

    public static IReadOnlyList<string> Tags(
        IEnumerable<string?> subjects,
        IEnumerable<string?> places,
        IEnumerable<string?> people,
        IEnumerable<string?> times,
        string? seriesName,
        string? physicalFormat) {
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(seriesName)) tags.Add($"series: {seriesName.Trim()}");
        if (!string.IsNullOrWhiteSpace(physicalFormat)) tags.Add($"format: {physicalFormat.Trim()}");

        tags.AddRange(subjects
            .Where(subject => !string.IsNullOrWhiteSpace(subject))
            .Select(subject => NormalizeSubject(subject!))
            .Where(subject => subject is { Length: > 0 } && !NoisySubjects.Contains(subject))
            .Take(40));

        tags.AddRange(places
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => $"place: {value!.Trim()}")
            .Take(10));

        tags.AddRange(people
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => $"character: {value!.Trim()}")
            .Take(20));

        tags.AddRange(times
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => $"period: {value!.Trim()}")
            .Take(10));

        return tags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToArray();
    }

    public static int? YearFromDate(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = YearRegex().Match(value);
        return match.Success && int.TryParse(match.Value, out var year) ? year : null;
    }

    public static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    public static int? SeriesPosition(string workId, IReadOnlyList<OpenLibrarySearchDoc> seriesDocs) {
        var index = seriesDocs
            .Select((doc, i) => new { Id = WorkIdFromKey(doc.Key), Index = i })
            .FirstOrDefault(row => row.Id?.Equals(workId, StringComparison.OrdinalIgnoreCase) == true)
            ?.Index;
        return index is null ? null : index + 1;
    }

    private static string NormalizeSubject(string subject) {
        var value = subject.Trim();
        if (value.StartsWith("series:", StringComparison.OrdinalIgnoreCase)) {
            return $"series: {value["series:".Length..].Trim()}";
        }

        if (value.StartsWith("nyt:", StringComparison.OrdinalIgnoreCase)) {
            return string.Empty;
        }

        return value;
    }

    private static string? IdFromKey(string? key, string segment, string suffix) {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var trimmed = key.Trim();
        var match = Regex.Match(trimmed, $@"(?:^|/){Regex.Escape(segment)}/(OL[0-9]+{suffix})$", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        return Regex.IsMatch(trimmed, $@"^OL[0-9]+{suffix}$", RegexOptions.IgnoreCase) ? trimmed : null;
    }

    [GeneratedRegex(@"openlibrary\.org/works/(OL[0-9]+W)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkUrlRegex();

    [GeneratedRegex(@"openlibrary\.org/(?:books|isbn)/(OL[0-9]+M)", RegexOptions.IgnoreCase)]
    private static partial Regex EditionUrlRegex();

    [GeneratedRegex(@"openlibrary\.org/isbn/([0-9Xx-]{10,17})", RegexOptions.IgnoreCase)]
    private static partial Regex IsbnUrlRegex();

    [GeneratedRegex(@"openlibrary\.org/authors/(OL[0-9]+A)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorUrlRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"[*_`]+")]
    private static partial Regex MarkdownMarkerRegex();

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s*")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiBlankLineRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NormalizeRegex();

    [GeneratedRegex(@"\b\d{4}\b")]
    private static partial Regex YearRegex();
}
