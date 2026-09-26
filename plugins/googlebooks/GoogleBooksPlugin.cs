using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Prismedia.Plugin.Metadata;

namespace Prismedia.Plugin.GoogleBooks;

/// <summary>Edition-specific identification. A title search always remains a reviewed candidate choice.</summary>
internal sealed partial class GoogleBooksPlugin(HttpClient http) {
    #region Variables
    private readonly GoogleBooksClient client = new(http);
    #endregion

    #region Actions - Identification
    public async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        if (request.Entity.Kind is not (GoogleBooksCodes.Book or GoogleBooksCodes.BookVolume)) return IdentifyPluginResult.None();
        var id = Value(request.Query.ExternalIds, GoogleBooksCodes.Provider);
        var urlId = VolumeIdFromUrl(request.Query.Url, required: true);
        if (id is not null && urlId is not null && id != urlId)
            throw new ArgumentException("The volume ID and URL identify different editions.");
        id ??= urlId;
        var isbn = RequestedIsbn(request.Query.ExternalIds, Value(request.Query.Fields, GoogleBooksCodes.Isbn));
        var explicitIdentity = id is not null || isbn is not null;
        var title = Value(request.Query.Fields, GoogleBooksCodes.Title) ?? request.Query.Title;
        var search = request.Action.Equals(GoogleBooksCodes.Search, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(title);
        if (!explicitIdentity && !search) {
            id = Value(request.Entity.ExternalIds, GoogleBooksCodes.Provider) ?? Value(request.Hints.ExternalIds, GoogleBooksCodes.Provider)
                ?? request.Hints.Urls.Select(url => VolumeIdFromUrl(url, required: false)).FirstOrDefault(value => value is not null);
            isbn = RequestedIsbn(request.Entity.ExternalIds) ?? RequestedIsbn(request.Hints.ExternalIds);
        }
        var apiKey = Value(request.Auth, GoogleBooksCodes.ApiKey)
            ?? throw new ArgumentException("Configure a Google Books API key before searching.");
        if (id is not null) {
            if (!VolumeIdPattern().IsMatch(id)) throw new ArgumentException("The Google Books volume ID is invalid.");
            var volume = await client.GetAsync<GoogleVolume>("volumes/" + Uri.EscapeDataString(id), apiKey);
            if (volume is null) return IdentifyPluginResult.None();
            if (!string.Equals(volume.Id, id, StringComparison.Ordinal)) throw new InvalidOperationException("Google Books returned a different volume identity.");
            if (!Visible(volume, request.IncludeNsfw)) return IdentifyPluginResult.None();
            if (isbn is not null && !MatchesIsbn(volume, isbn)) throw new InvalidOperationException("The selected volume does not contain the requested ISBN.");
            return request.Query.RequireChoice == true ? IdentifyPluginResult.ForCandidates([Candidate(volume)])
                : IdentifyPluginResult.ForProposal(Proposal(volume, request, "edition-id"));
        }

        title ??= request.Hints.Title ?? request.Entity.Title;
        if (isbn is null && string.IsNullOrWhiteSpace(title)) return IdentifyPluginResult.None();
        var query = isbn is not null ? "isbn:" + isbn.Value : "intitle:" + Quote(title!);
        var author = Value(request.Query.Fields, GoogleBooksCodes.Author);
        if (isbn is null && author is not null) query += " inauthor:" + Quote(author);
        var language = Value(request.Query.Fields, GoogleBooksCodes.Language);
        if (language is not null && !LanguagePattern().IsMatch(language)) throw new ArgumentException("Use a two-letter language code.");
        var limit = Math.Clamp(request.Query.Limit, 1, 100);
        var volumes = new List<GoogleVolume>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < limit; offset += 40) {
            var count = Math.Min(40, limit - offset);
            var page = await client.GetAsync<GoogleVolumePage>("volumes?q=" + Uri.EscapeDataString(query)
                + $"&printType=books&maxResults={count}&startIndex={offset}"
                + (language is null ? string.Empty : "&langRestrict=" + Uri.EscapeDataString(language)), apiKey);
            var items = page?.Items ?? [];
            foreach (var volume in items.Take(count)) {
                if (Visible(volume, request.IncludeNsfw) && seen.Add(volume.Id) && (isbn is null || MatchesIsbn(volume, isbn))) volumes.Add(volume);
            }
            if (items.Length < count) break;
            if (offset + count < limit) await Task.Delay(250);
        }
        // Search pagination and regional catalogs cannot prove that a single result is the only edition.
        return volumes.Count == 0 ? IdentifyPluginResult.None() : IdentifyPluginResult.ForCandidates(volumes.Select(Candidate).ToArray());
    }
    #endregion

    #region Actions - Evidence
    private static bool Visible(GoogleVolume volume, bool includeNsfw) => volume is not null && volume.VolumeInfo is not null
        && VolumeIdPattern().IsMatch(volume.Id ?? string.Empty) && !string.IsNullOrWhiteSpace(volume.VolumeInfo.Title)
        && (volume.VolumeInfo.PrintType is null or GoogleBooksCodes.BookPrintType)
        && (includeNsfw || volume.VolumeInfo.MaturityRating != GoogleBooksCodes.Mature);

    private static bool MatchesIsbn(GoogleVolume volume, BookIsbn isbn) => Identifiers(volume)
        .Select(identifier => BookIsbn.Parse(identifier.Identifier))
        .Any(candidate => candidate?.Canonical13 == isbn.Canonical13);

    private static IEnumerable<GoogleIdentifier> Identifiers(GoogleVolume volume) =>
        (volume.VolumeInfo.IndustryIdentifiers ?? []).Where(id => id.Type is GoogleBooksCodes.Isbn10Type or GoogleBooksCodes.Isbn13Type);

    private static Dictionary<string, string> ExternalIds(GoogleVolume volume) {
        var ids = new Dictionary<string, string> { [GoogleBooksCodes.Provider] = volume.Id };
        foreach (var identifier in Identifiers(volume)) {
            if (BookIsbn.Parse(identifier.Identifier) is not { } isbn) continue;
            var code = isbn.Value.Length == 13 ? GoogleBooksCodes.Isbn13 : GoogleBooksCodes.Isbn10;
            ids.TryAdd(code, isbn.Value);
        }
        return ids;
    }

    private static EntitySearchCandidate Candidate(GoogleVolume volume) {
        var info = volume.VolumeInfo;
        var facts = new[] { info.Language, info.Publisher, info.PublishedDate, string.Join(", ", ExternalIds(volume).Where(pair => pair.Key != GoogleBooksCodes.Provider).Select(pair => pair.Value)) };
        var overview = string.Join(" · ", facts.Where(value => !string.IsNullOrWhiteSpace(value))) + "\n" + PlainText(info.Description, 1600);
        return new(ExternalIds(volume), DisplayTitle(info), Year(info.PublishedDate), overview.Trim(), Cover(info), null,
            CandidateId: GoogleBooksCodes.Provider + ":" + volume.Id, Source: "Google Books", MatchReason: "edition-search");
    }

    private static EntityMetadataProposal Proposal(GoogleVolume volume, IdentifyPluginRequest request, string reason) {
        var info = volume.VolumeInfo;
        var dates = new Dictionary<string, string>();
        if (ValidDate(info.PublishedDate) is { } date) dates[GoogleBooksCodes.EditionPublished] = date;
        var cover = Cover(info);
        var patch = new EntityMetadataPatch(DisplayTitle(info), PlainText(info.Description, 50000), ExternalIds(volume),
            ["https://books.google.com/books?id=" + Uri.EscapeDataString(volume.Id)],
            (info.Categories ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Take(30).ToArray(), info.Publisher,
            (info.Authors ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Take(50)
                .Select((name, index) => new CreditPatch(name, GoogleBooksCodes.AuthorRole, null, index)).ToArray(),
            dates, new Dictionary<string, int>(), new Dictionary<string, int>(), null) {
            Flags = info.MaturityRating == GoogleBooksCodes.Mature ? new(null, true, null) : null
        };
        return new(GoogleBooksCodes.Provider + ":" + volume.Id, GoogleBooksCodes.Provider, request.Entity.Kind, 1m, reason,
            patch, cover is null ? [] : [new(GoogleBooksCodes.Cover, cover, "Google Books", null, info.Language, null, null)],
            [], [], request.Entity.Id, []);
    }

    private static string DisplayTitle(GoogleVolumeInfo info) => string.Join(": ", new[] { info.Title, info.Subtitle }
        .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
    private static int? Year(string? value) => value is { Length: >= 4 } && int.TryParse(value[..4], out var year) ? year : null;
    private static string? ValidDate(string? value) => value is not null && DateTime.TryParseExact(value,
        ["yyyy", "yyyy-MM", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? value : null;
    private static string? PlainText(string? value, int limit) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = WebUtility.HtmlDecode(HtmlTags().Replace(value, " ")).Trim();
        return text[..Math.Min(text.Length, limit)];
    }
    private static string Quote(string value) {
        if (value.Length > 512) throw new ArgumentException("Search terms must be at most 512 characters.");
        return "\"" + value.Replace('"', ' ').Replace('\\', ' ').Trim() + "\"";
    }
    private static string? Cover(GoogleVolumeInfo info) {
        if (!Uri.TryCreate(info.ImageLinks?.Thumbnail ?? info.ImageLinks?.SmallThumbnail, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0
            || !(uri.Host == "books.google.com" || uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase))) return null;
        return new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri;
    }
    private static string? Value(IReadOnlyDictionary<string, string>? values, string key) =>
        values?.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is { } value
            && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    private static BookIsbn? RequestedIsbn(IReadOnlyDictionary<string, string>? values, string? field = null) {
        var supplied = new[] { Value(values, GoogleBooksCodes.Isbn13), Value(values, GoogleBooksCodes.Isbn10),
            Value(values, GoogleBooksCodes.Isbn), field }.Where(value => value is not null)
            .Select(value => BookIsbn.Parse(value) ?? throw new ArgumentException("Enter a valid ISBN-10 or ISBN-13.")).ToArray();
        if (supplied.Select(value => value.Canonical13).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new ArgumentException("The supplied ISBNs identify different editions.");
        return supplied.FirstOrDefault();
    }

    internal static string? VolumeIdFromUrl(string? raw, bool required) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort) {
            if (uri.Host.Equals("books.google.com", StringComparison.OrdinalIgnoreCase)) {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in uri.Query.TrimStart('?').Split('&')) {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2 && parts[0] == "id") ids.Add(Uri.UnescapeDataString(parts[1]));
                }
                if (ids.Count == 1 && VolumeIdPattern().IsMatch(ids.Single())) return ids.Single();
            }
            if (uri.Host.Equals("www.googleapis.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/books/v1/volumes/", StringComparison.Ordinal)) {
                var id = uri.AbsolutePath["/books/v1/volumes/".Length..];
                if (VolumeIdPattern().IsMatch(id)) return id;
            }
        }
        if (required) throw new ArgumentException("Use a Google Books volume URL containing its exact ID.");
        return null;
    }
    #endregion

    #region Actions - Patterns
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,128}\z", RegexOptions.CultureInvariant)] private static partial Regex VolumeIdPattern();
    [GeneratedRegex(@"\A[a-zA-Z]{2}\z", RegexOptions.CultureInvariant)] private static partial Regex LanguagePattern();
    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant, 1000)] private static partial Regex HtmlTags();
    #endregion
}
