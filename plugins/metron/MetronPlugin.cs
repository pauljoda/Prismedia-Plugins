using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Prismedia.Plugin.Metadata;

namespace Prismedia.Plugin.Metron;

/// <summary>Identifies concrete Metron runs and issues; title searches remain explicit candidate choices.</summary>
internal sealed partial class MetronPlugin(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null) {
    internal const int MaximumIssues = 500;
    private readonly MetronClient _client = new(http, delay);

    internal async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        if (request.ProtocolVersion != 2) throw new ArgumentException("Metron requires metadata protocol version 2.");
        var series = request.Entity.Kind == MetronCodes.SeriesKind;
        if (!series && request.Entity.Kind != MetronCodes.IssueKind) return IdentifyPluginResult.None();
        if (request.Action is not (MetronCodes.Search or MetronCodes.LookupId or MetronCodes.LookupUrl))
            throw new ArgumentException("Metron does not support this metadata action.");
        var identity = series ? MetronCodes.SeriesIdentity : MetronCodes.IssueIdentity;
        var id = Value(request.Query.ExternalIds, identity);
        var urlId = UrlIdentity(request.Query.Url, series);
        if (id is not null && urlId is not null && id != urlId) throw new ArgumentException("The Metron ID and URL identify different records.");
        id ??= urlId;
        if (id is null && request.Action != MetronCodes.Search) {
            id = Value(request.Entity.ExternalIds, identity) ?? Value(request.Hints.ExternalIds, identity);
        }
        if (request.Action == MetronCodes.LookupUrl && urlId is null) throw new ArgumentException("Provide a Metron API record URL with a numeric ID.");
        if (id is not null && !PositiveId().IsMatch(id)) throw new ArgumentException("The Metron record ID must be a positive integer.");
        var apiToken = Value(request.Auth, MetronCodes.Token) ?? throw new ArgumentException("Configure a Metron API token before searching.");
        if (apiToken.Length > 4096 || apiToken.Any(char.IsControl)) throw new ArgumentException("The Metron API token is invalid.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        if (id is not null) {
            if (series) {
                var item = await _client.GetAsync<MetronSeries>(MetronCodes.SeriesPath + id + "/", apiToken, token);
                if (item is null) return IdentifyPluginResult.None();
                RequireIdentity(item.Id, id);
                var candidate = SeriesCandidate(item);
                if (request.Query.RequireChoice == true) return IdentifyPluginResult.ForCandidates([candidate]);
                var issues = request.IncludeStructuralChildren ? await ReadIssuesAsync(item.Id, apiToken, token) : [];
                return IdentifyPluginResult.ForProposal(SeriesProposal(item, issues, request.Entity.Id));
            }
            var issue = await _client.GetAsync<MetronIssue>(MetronCodes.IssuePath + id + "/", apiToken, token);
            if (issue is null) return IdentifyPluginResult.None();
            RequireIdentity(issue.Id, id);
            ValidateIssue(issue);
            ValidateSeriesContext(request, issue);
            return request.Query.RequireChoice == true ? IdentifyPluginResult.ForCandidates([IssueCandidate(issue)])
                : IdentifyPluginResult.ForProposal(IssueProposal(issue, 1, request.Entity.Id));
        }
        if (request.Action != MetronCodes.Search) return IdentifyPluginResult.None();
        return await SearchAsync(request, series, apiToken, token);
    }

    private async Task<IdentifyPluginResult> SearchAsync(IdentifyPluginRequest request, bool series, string apiToken, CancellationToken token) {
        var title = Field(request, MetronCodes.SeriesTitle, 300) ?? request.Query.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 300) throw new ArgumentException("Enter a series title of at most 300 characters.");
        var number = Field(request, MetronCodes.IssueNumber, 64);
        if (!series && number is null) throw new ArgumentException("Enter the exact issue designation, such as 12.5 or Annual 1.");
        var year = IntegerField(request, MetronCodes.Year, 1, 9999);
        var volume = IntegerField(request, MetronCodes.Volume, 1, 10000);
        var query = new Dictionary<string, string> { [series ? MetronCodes.NameQuery : MetronCodes.SeriesNameQuery] = title };
        if (number is not null && !series) query[MetronCodes.NumberQuery] = number;
        if (year is not null) query[series ? MetronCodes.YearQuery : MetronCodes.SeriesYearQuery] = year.Value.ToString(CultureInfo.InvariantCulture);
        if (volume is not null) query[series ? MetronCodes.Volume : MetronCodes.SeriesVolumeQuery] = volume.Value.ToString(CultureInfo.InvariantCulture);
        if (Field(request, MetronCodes.Publisher, 200) is { } publisher) query[MetronCodes.PublisherQuery] = publisher;
        if (series && Field(request, MetronCodes.Language, 8) is { } language) {
            if (!LanguageCode().IsMatch(language)) throw new ArgumentException("Use a two-letter language code.");
            query[MetronCodes.Language] = language.ToLowerInvariant();
        }
        var path = (series ? MetronCodes.SeriesPath : MetronCodes.IssuePath) + "?" + QueryString(query);
        var limit = Math.Clamp(request.Query.Limit, 1, 100);
        if (series) {
            var page = await _client.GetAsync<MetronPage<MetronSeries>>(path, apiToken, token) ?? throw InvalidPage();
            var items = page.Results ?? throw InvalidPage();
            return IdentifyPluginResult.ForCandidates(items.Where(item => item is not null && item.Id > 0
                && (year is null || item.YearBegan == year) && (volume is null || item.Volume == volume))
                .DistinctBy(item => item.Id).Take(limit).Select(SeriesCandidate).ToArray());
        }
        var issuePage = await _client.GetAsync<MetronPage<MetronIssue>>(path, apiToken, token) ?? throw InvalidPage();
        return IdentifyPluginResult.ForCandidates((issuePage.Results ?? throw InvalidPage())
            .Where(item => item is not null && item.Id > 0 && item.Series is { Id: > 0 }
                && string.Equals(item.Number?.Trim(), number, StringComparison.OrdinalIgnoreCase)
                && (year is null || item.Series.YearBegan == year) && (volume is null || item.Series.Volume == volume))
            .DistinctBy(item => item.Id).Take(limit).Select(IssueCandidate).ToArray());
    }

    private async Task<IReadOnlyList<MetronIssue>> ReadIssuesAsync(long seriesId, string apiToken, CancellationToken token) {
        var path = MetronCodes.SeriesPath + seriesId.ToString(CultureInfo.InvariantCulture) + "/" + MetronCodes.IssueListPath;
        var issues = new List<MetronIssue>(); var seen = new HashSet<long>(); int? expected = null;
        for (var pageNumber = 1; pageNumber <= 20; pageNumber++) {
            var page = await _client.GetAsync<MetronPage<MetronIssue>>(path + "?" + MetronCodes.PageQuery + "=" + pageNumber, apiToken, token)
                ?? throw InvalidPage();
            if (page.Count < 0 || page.Results is null || page.Results.Length > MaximumIssues) throw InvalidPage();
            if (page.Count > MaximumIssues) throw new InvalidOperationException("This Metron series exceeds the 500-issue review limit. Search for an individual issue instead.");
            if (expected is not null && expected != page.Count) throw ChangedPage();
            expected = page.Count;
            foreach (var issue in page.Results) {
                ValidateIssue(issue);
                if (issue.Series!.Id != seriesId || !seen.Add(issue.Id)) throw ChangedPage();
                issues.Add(issue);
            }
            if (issues.Count > MaximumIssues || issues.Count > expected) throw ChangedPage();
            if (page.Next is null) {
                if (issues.Count != expected) throw ChangedPage();
                return issues.OrderBy(issue => NumericOrder(issue.Number)).ThenBy(issue => issue.Number, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(issue => issue.StoreDate, StringComparer.Ordinal).ThenBy(issue => issue.Id).ToArray();
            }
            if (page.Results.Length == 0) throw ChangedPage();
            MetronClient.ValidateNext(page.Next, path);
        }
        throw new InvalidOperationException("Metron issue pagination exceeded its review limit. Search for an individual issue instead.");
    }

    private static EntityMetadataProposal SeriesProposal(MetronSeries item, IReadOnlyList<MetronIssue> issues, Guid entityId) {
        var dates = new Dictionary<string, string>();
        if (item.YearBegan is > 0 and <= 9999) dates[MetronCodes.PublicationDate] = item.YearBegan.Value.ToString("D4", CultureInfo.InvariantCulture);
        var stats = new Dictionary<string, int>();
        if (item.IssueCount is >= 0) stats[MetronCodes.IssueCount] = item.IssueCount.Value;
        var patch = new EntityMetadataPatch(SeriesTitle(item), PlainText(item.Desc), SeriesIds(item), Urls(item.ResourceUrl),
            Tags(item.Genres), item.Publisher?.Name, [], dates, stats, new Dictionary<string, int>(), null) {
            AlternativeTitles = AlternativeTitles(item.AltNames)
        };
        return new(MetronCodes.SeriesIdentity + ":" + item.Id, MetronCodes.Provider, MetronCodes.SeriesKind, 1, "Exact Metron series identity",
            patch, [], issues.Select((issue, index) => IssueProposal(issue, index + 1)).ToArray(), [], entityId, []);
    }

    private static EntityMetadataProposal IssueProposal(MetronIssue item, int order, Guid? entityId = null) {
        var dates = new Dictionary<string, string>();
        if (DateOnly.TryParseExact(item.StoreDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            dates[MetronCodes.PublicationDate] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var stats = new Dictionary<string, int>();
        if (item.Page is > 0) stats[MetronCodes.PageCount] = item.Page.Value;
        var cover = Cover(item.Image);
        var patch = new EntityMetadataPatch(IssueTitle(item), PlainText(item.Desc), IssueIds(item), Urls(item.ResourceUrl),
            Tags(item.Series?.Genres), item.Publisher?.Name, Credits(item.Credits), dates, stats, new Dictionary<string, int>(), null) {
            PositionEntries = [new(MetronCodes.ChapterPosition, order, item.Number!.Trim())]
        };
        return new(MetronCodes.IssueIdentity + ":" + item.Id, MetronCodes.Provider, MetronCodes.IssueKind, 1, "Exact Metron issue identity; cover variants remain part of this issue",
            patch, cover is null ? [] : [new(MetronCodes.Cover, cover, "Metron", null, item.Series?.Language, null, null)], [], [], entityId, []);
    }

    private static EntitySearchCandidate SeriesCandidate(MetronSeries item) => new(SeriesIds(item), SeriesTitle(item), item.YearBegan,
        string.Join(" · ", new[] { item.Publisher?.Name, item.Volume is { } volume ? $"Run volume {volume}" : null, item.Language }.Where(value => value is not null)),
        null, null, MetronCodes.SeriesIdentity + ":" + item.Id, "Metron", MatchReason: "Review the publisher, start year, and run volume");
    private static EntitySearchCandidate IssueCandidate(MetronIssue item) => new(IssueIds(item), IssueTitle(item), item.Series?.YearBegan,
        $"{SeriesTitle(item.Series!)} · Run volume {item.Series?.Volume} · Issue {item.Number}", Cover(item.Image), null,
        MetronCodes.IssueIdentity + ":" + item.Id, "Metron", MatchReason: "Exact issue designation; choose the intended series run");
    private static Dictionary<string, string> SeriesIds(MetronSeries item) => Ids(MetronCodes.SeriesIdentity, item.Id, item.CvId, "4050-", MetronCodes.GcdSeriesIdentity, item.GcdId);
    private static Dictionary<string, string> IssueIds(MetronIssue item) => Ids(MetronCodes.IssueIdentity, item.Id, item.CvId, "4000-", MetronCodes.GcdIssueIdentity, item.GcdId);
    private static Dictionary<string, string> Ids(string identity, long id, long? comicVine, string comicVinePrefix, string gcdNamespace, long? gcd) {
        var ids = new Dictionary<string, string> { [identity] = id.ToString(CultureInfo.InvariantCulture) };
        if (comicVine is > 0) ids[MetronCodes.ComicVineIdentity] = comicVinePrefix + comicVine.Value.ToString(CultureInfo.InvariantCulture);
        if (gcd is > 0) ids[gcdNamespace] = gcd.Value.ToString(CultureInfo.InvariantCulture);
        return ids;
    }
    private static IReadOnlyList<CreditPatch> Credits(MetronCredit[]? credits) => (credits ?? []).Where(credit => !string.IsNullOrWhiteSpace(credit.Creator))
        .SelectMany(credit => (credit.Role is { Length: > 0 } ? credit.Role.Select(role => CreditRole(role.Name)) : [MetronCodes.PersonRole])
            .Distinct(StringComparer.Ordinal).Select(role => (Name: credit.Creator!.Trim(), Role: role)))
        .Distinct().Take(200).Select((credit, index) => new CreditPatch(credit.Name, credit.Role, null, index)).ToArray();
    private static string CreditRole(string? role) => role?.Trim().ToLowerInvariant() switch {
        // prism-vocab: external Metron credit-role names mapped at this boundary.
        "writer" or "script" or "plot" => MetronCodes.WriterRole,
        "artist" or "penciller" or "penciler" or "inker" or "colorist" or "cover" or "cover artist" => MetronCodes.ArtistRole,
        _ => MetronCodes.PersonRole
    };
    private static string SeriesTitle(MetronSeries item) => !string.IsNullOrWhiteSpace(item.Name) ? item.Name.Trim()
        : !string.IsNullOrWhiteSpace(item.Series) ? item.Series.Trim() : throw new InvalidDataException("Metron returned a series without a title.");
    private static string IssueTitle(MetronIssue item) => SeriesTitle(item.Series!) + " #" + item.Number!.Trim();
    private static string[] Tags(MetronNamed[]? genres) => (genres ?? []).Select(genre => genre.Name?.Trim()).OfType<string>()
        .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
    private static IReadOnlyList<string>? AlternativeTitles(string[]? names) {
        var values = (names ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
        return values.Length == 0 ? null : values;
    }
    private static string[] Urls(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == MetronClient.Origin.Host && uri.IsDefaultPort && uri.UserInfo.Length == 0 ? [uri.AbsoluteUri] : [];
    private static string? Cover(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host is "static.metron.cloud" or "metron.cloud" && uri.IsDefaultPort && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    private static string? PlainText(string? value) => string.IsNullOrWhiteSpace(value) ? null
        : WebUtility.HtmlDecode(HtmlTags().Replace(value[..Math.Min(value.Length, 100000)], " ")).Trim();
    private static decimal NumericOrder(string? label) => decimal.TryParse(label, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture, out var value) ? value : label == "½" ? 0.5m : decimal.MaxValue;
    private static void RequireIdentity(long actual, string expected) {
        if (actual <= 0 || actual.ToString(CultureInfo.InvariantCulture) != expected) throw new InvalidDataException("Metron returned a different record identity.");
    }
    private static void ValidateIssue(MetronIssue? issue) {
        if (issue is null || issue.Id <= 0 || issue.Series is not { Id: > 0 } || string.IsNullOrWhiteSpace(issue.Series.Name)
            || string.IsNullOrWhiteSpace(issue.Number) || issue.Number.Length > 64) throw new InvalidDataException("Metron returned an issue without a usable series identity or designation.");
    }
    private static void ValidateSeriesContext(IdentifyPluginRequest request, MetronIssue issue) {
        foreach (var ancestor in request.StructuralContext?.Ancestors ?? []) {
            if (Value(ancestor.ExternalIds, MetronCodes.SeriesIdentity) is { } expected && expected != issue.Series!.Id.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("This Metron issue belongs to a different series run than the selected parent.");
        }
    }
    private static string? UrlIdentity(string? value, bool series) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = series ? MetronCodes.SeriesPath : MetronCodes.IssuePath;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != MetronClient.Origin.Host
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use a Metron API record URL with a numeric ID.");
        var prefix = MetronClient.Origin.AbsolutePath + path;
        var id = uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) ? uri.AbsolutePath[prefix.Length..].TrimEnd('/') : "";
        return PositiveId().IsMatch(id) ? id : throw new ArgumentException("The Metron API URL does not identify this kind of record.");
    }
    private static string? Value(IReadOnlyDictionary<string, string>? values, string key) => values is not null && values.TryGetValue(key, out var value)
        && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    private static string? Field(IdentifyPluginRequest request, string key, int maximum) {
        var value = Value(request.Query.Fields, key);
        return value?.Length > maximum ? throw new ArgumentException($"{key} exceeds its search limit.") : value;
    }
    private static int? IntegerField(IdentifyPluginRequest request, string key, int minimum, int maximum) {
        if (Field(request, key, 12) is not { } value) return null;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= minimum && number <= maximum
            ? number : throw new ArgumentException($"{key} must be between {minimum} and {maximum}.");
    }
    private static string QueryString(Dictionary<string, string> values) => string.Join('&', values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    private static InvalidDataException InvalidPage() => new("Metron returned an invalid catalog page.");
    private static InvalidDataException ChangedPage() => new("Metron's issue list changed or contains conflicting identities. Retry before reviewing this series.");
    [GeneratedRegex("^[1-9][0-9]{0,17}$", RegexOptions.CultureInvariant)] private static partial Regex PositiveId();
    [GeneratedRegex("^[A-Za-z]{2}$", RegexOptions.CultureInvariant)] private static partial Regex LanguageCode();
    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)] private static partial Regex HtmlTags();
}
