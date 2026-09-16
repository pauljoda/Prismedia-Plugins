using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Opds;

/// <summary>Implements capability negotiation, bounded browsing, advertised search, and revalidated full-content offers.</summary>
internal sealed class OpdsIntegration(OpdsHttpClient http, ConnectionContext connection) {
    private sealed record Cursor(string Url, int Offset);
    private static readonly string[] Kinds = [MediaKinds.Book, MediaKinds.Comic];
    private static readonly XNamespace OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";
    private const string SearchTerms = "{searchTerms}";
    private const string QueryVariable = "{?query}";

    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken cancellationToken) => request.Operation switch {
        IntegrationOperations.Probe => await ProbeAsync(cancellationToken),
        IntegrationOperations.Browse or IntegrationOperations.Search => await DiscoverAsync(Read<DiscoveryInput>(request.Input), request.Operation == IntegrationOperations.Search, cancellationToken),
        IntegrationOperations.Resolve => await ResolveAsync(Read<ResolveOfferInput>(request.Input), cancellationToken),
        _ => throw new IntegrationFailure("This OPDS operation is not supported.")
    };
    private static T Read<T>(JsonElement input) => input.Deserialize<T>(IntegrationProtocol.Json) ?? throw new IntegrationFailure("The operation input is missing.");

    internal async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken) {
        var root = await ReadDocumentAsync(connection.BaseUrl, MediaKinds.Book, cancellationToken);
        var search = await SearchTemplateAsync(root, cancellationToken);
        return new(null, root.Title, null, [
            new(IntegrationCapabilities.Discovery, search is null ? [IntegrationOperations.Browse] : [IntegrationOperations.Browse, IntegrationOperations.Search], Kinds),
            new(IntegrationCapabilities.AcquisitionSource, [IntegrationOperations.Resolve], Kinds)
        ]);
    }

    internal async Task<CatalogPage> DiscoverAsync(DiscoveryInput input, bool searching, CancellationToken cancellationToken) {
        if (!Kinds.Contains(input.EntityKind) || input.Limit is < 1 or > 100 || input.Query?.Length > 512) throw new IntegrationFailure("The catalog request is invalid.");
        string address;
        var offset = 0;
        if (!string.IsNullOrEmpty(input.Cursor)) {
            try {
                var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(input.Cursor), IntegrationProtocol.Json);
                if (cursor is null || cursor.Offset is < 0 or > 10000) throw new FormatException();
                address = cursor.Url;
                offset = cursor.Offset;
            } catch (Exception error) when (error is FormatException or JsonException) { throw new IntegrationFailure("The catalog cursor is invalid."); }
        } else if (searching) {
            if (string.IsNullOrWhiteSpace(input.Query)) throw new IntegrationFailure("Enter a search query.");
            var root = await ReadDocumentAsync(connection.BaseUrl, input.EntityKind, cancellationToken);
            var template = await SearchTemplateAsync(root, cancellationToken) ?? throw new IntegrationFailure("This catalog does not advertise a supported search interface.");
            address = ExpandSearch(template, input.Query);
        } else address = input.Container ?? connection.BaseUrl;
        var page = await ReadDocumentAsync(address, input.EntityKind, cancellationToken);
        if (offset > page.Entries.Count) throw new IntegrationFailure("The catalog page changed. Browse it again.");
        var items = page.Entries.Skip(offset).Take(input.Limit).Select(entry => entry.Item).ToArray();
        var next = offset + items.Length < page.Entries.Count
            ? new Cursor(address, offset + items.Length) : page.Next is null ? null : new Cursor(page.Next.AbsoluteUri, 0);
        return new(page.Title, items, next is null ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(next, IntegrationProtocol.Json)));
    }

    internal async Task<ResolvedSourceOffer> ResolveAsync(ResolveOfferInput input, CancellationToken cancellationToken) {
        if (input.Selection is null || !Kinds.Contains(input.Selection.EntityKind)) throw new IntegrationFailure("Select a valid publication.");
        var page = await ReadDocumentAsync(input.Selection.Locator, input.Selection.EntityKind, cancellationToken);
        var entry = page.Entries.FirstOrDefault(item => item.Item.Selection.ItemId == input.Selection.ItemId && !item.Item.IsContainer)
            ?? throw new IntegrationFailure("The selected publication is no longer in this catalog page.");
        var offer = entry.Item.Offers.FirstOrDefault(item => item.Id == input.OfferId && item.Access == AcquisitionAccess.Download);
        if (offer is null || !entry.Acquisitions.TryGetValue(input.OfferId, out var acquisition)) throw new IntegrationFailure("This offer is not a direct full-publication download.");
        http.RequireScope(acquisition.Url.AbsoluteUri);
        var filename = Path.GetFileName(acquisition.Url.LocalPath);
        if (string.IsNullOrWhiteSpace(filename) || !Path.HasExtension(filename)) filename = "publication" + Extension(acquisition.MediaType);
        return new(input.Selection, offer.Id, entry.Item.Publication, offer, new(acquisition.Url.AbsoluteUri, http.Headers, filename, offer.ByteSize));
    }

    private async Task<OpdsDocument> ReadDocumentAsync(string address, string kind, CancellationToken cancellationToken) {
        var response = await http.ReadAsync(http.RequireScope(address), cancellationToken);
        return OpdsParser.Parse(response.Body, response.Url, kind);
    }

    private async Task<string?> SearchTemplateAsync(OpdsDocument document, CancellationToken cancellationToken) {
        if (document.Search is null) return null;
        string? template;
        if (document.Search.IsDescription) {
            var response = await http.ReadAsync(document.Search.Url, cancellationToken);
            try {
                using var reader = OpdsParser.CreateXmlReader(response.Body);
                var root = XDocument.Load(reader).Root;
                if (root?.Name != OpenSearch + "OpenSearchDescription") return null;
                // prism-vocab: external — OpenSearch template and media-type attributes.
                template = root.Elements(OpenSearch + "Url")
                    .Where(element => OpdsParser.IsCatalog(element.Attribute("type")?.Value))
                    .Select(element => element.Attribute("template")?.Value)
                    .FirstOrDefault(value => value is not null && SupportedTemplate(value));
                if (template is not null) template = new Uri(response.Url, template).AbsoluteUri;
            } catch (System.Xml.XmlException) { return null; }
        } else template = document.Search.Url.AbsoluteUri;
        if (template is null) return null;
        template = template.Replace("%7B", "{", StringComparison.OrdinalIgnoreCase).Replace("%7D", "}", StringComparison.OrdinalIgnoreCase);
        if (!SupportedTemplate(template)) return null;
        http.RequireScope(ExpandSearch(template, "probe"));
        return template;
    }

    private static bool SupportedTemplate(string template) {
        template = template.Replace("%7B", "{", StringComparison.OrdinalIgnoreCase).Replace("%7D", "}", StringComparison.OrdinalIgnoreCase);
        if (!template.Contains(SearchTerms) && !template.Contains(QueryVariable)) return false;
        return !Regex.IsMatch(template.Replace(SearchTerms, "").Replace(QueryVariable, ""), "\\{[^}]*\\}");
    }
    private static string ExpandSearch(string template, string query) => template.Replace(SearchTerms, Uri.EscapeDataString(query), StringComparison.Ordinal)
        .Replace(QueryVariable, "?query=" + Uri.EscapeDataString(query), StringComparison.Ordinal);
    private static string Extension(string? type) => type switch {
        "application/epub+zip" => ".epub", "application/pdf" => ".pdf", "application/vnd.comicbook+zip" or "application/x-cbz" => ".cbz",
        "application/vnd.comicbook-rar" or "application/x-cbr" => ".cbr", "application/x-mobipocket-ebook" => ".mobi", "application/vnd.amazon.ebook" => ".azw", _ => ".bin"
    };
}
