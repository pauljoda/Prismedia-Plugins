using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Opds;

/// <summary>Decodes bounded OPDS documents into source choices without executing acquisition links.</summary>
internal static class OpdsParser {
    #region Static Variables
    internal const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dublin = "http://purl.org/dc/terms/";
    private const string Acquisition = "http://opds-spec.org/acquisition";
    private const string OpenAccess = Acquisition + "/open-access";
    private const string Borrow = Acquisition + "/borrow";
    private const string Buy = Acquisition + "/buy";
    private const string Sample = Acquisition + "/sample";
    private const string Subscribe = Acquisition + "/subscribe";
    internal const string SearchRelation = "search";
    private const string NextRelation = "next";
    internal const string OpenSearchType = "application/opensearchdescription+xml";
    private const string SourceIdentity = "opds";
    #endregion

    #region Actions - Parsing
    internal static OpdsDocument Parse(string body, Uri source, string kind) {
        if (body.Length > MaximumDocumentBytes) throw new IntegrationFailure("The catalog exceeds the document size limit.");
        try {
            return body.AsSpan().TrimStart().StartsWith("{") ? ParseJson(body, source, kind) : ParseAtom(body, source, kind);
        } catch (Exception error) when (error is XmlException or JsonException or InvalidOperationException or ArgumentException) {
            throw new IntegrationFailure("The server did not return a valid OPDS catalog.");
        }
    }

    internal static XmlReader CreateXmlReader(string body) => XmlReader.Create(new StringReader(body), new XmlReaderSettings {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumDocumentBytes
    });

    internal static bool SameOrigin(Uri origin, Uri candidate) => candidate.IsAbsoluteUri
        && candidate.Scheme is "http" or "https" && candidate.UserInfo.Length == 0
        && origin.Scheme == candidate.Scheme && origin.IdnHost == candidate.IdnHost && origin.Port == candidate.Port;

    private static Uri? Resolve(Link link) => Uri.TryCreate(link.Base, link.Href, out var result)
        && result.Scheme is "http" or "https" && result.UserInfo.Length == 0 ? result : null;
    internal static bool IsCatalog(string? type) => type?.StartsWith("application/atom+xml", StringComparison.OrdinalIgnoreCase) == true
        || type?.StartsWith("application/opds+json", StringComparison.OrdinalIgnoreCase) == true;
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? Clean(string? value, int limit = 512) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(1))).Trim();
        return text.Length <= limit ? text : text[..limit];
    }
    private static string[] Words(string? value) => value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
    #endregion

    #region Actions - Atom
    private static Uri XmlBase(XElement element, Uri source) {
        var result = source;
        foreach (var ancestor in element.AncestorsAndSelf().Reverse()) {
            if (ancestor.Attribute(XNamespace.Xml + "base")?.Value is { } relative && Uri.TryCreate(result, relative, out var resolved)) result = resolved;
        }
        return result;
    }
    private static Link XmlLink(XElement element, Uri source) => new(
        element.Attribute("href")?.Value ?? "", element.Attribute("type")?.Value,
        Words(element.Attribute("rel")?.Value), XmlBase(element, source),
        element.Elements().Any(child => child.Name.LocalName == "indirectAcquisition"),
        long.TryParse(element.Attribute("length")?.Value, out var size) && size >= 0 ? size : null);

    private static OpdsDocument ParseAtom(string body, Uri source, string kind) {
        using var reader = CreateXmlReader(body);
        var root = XDocument.Load(reader).Root;
        if (root?.Name != Atom + "feed" && root?.Name != Atom + "entry") throw new IntegrationFailure("The server did not return an OPDS Atom feed.");
        if (root.Descendants().Any(element => element.Ancestors().Take(65).Count() > 64)) throw new IntegrationFailure("The catalog nesting exceeds the supported limit.");
        var entries = new List<OpdsEntry>();
        foreach (var entry in root.Name == Atom + "entry" ? new[] { root } : root.Elements(Atom + "entry")) {
            var links = entry.Elements(Atom + "link").Select(link => XmlLink(link, source)).ToArray();
            var title = Clean(entry.Element(Atom + "title")?.Value) ?? "Untitled publication";
            var identity = Clean(entry.Element(Atom + "id")?.Value, 2048);
            var publication = new CatalogPublication(title, Clean((entry.Element(Atom + "summary") ?? entry.Element(Atom + "content"))?.Value, 8192),
                entry.Elements(Atom + "author").Select(author => Clean(author.Element(Atom + "name")?.Value, 256)).OfType<string>().Take(64).ToArray(),
                identity is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [SourceIdentity] = identity },
                Clean(entry.Element(Dublin + "language")?.Value), Clean(entry.Element(Dublin + "publisher")?.Value));
            Add(entries, identity, publication, links, source, kind);
        }
        var rootLinks = root.Elements(Atom + "link").Select(link => XmlLink(link, source)).ToArray();
        return Document(Clean(root.Element(Atom + "title")?.Value) ?? "Catalog", entries, rootLinks, source);
    }
    #endregion

    #region Actions - Json
    // prism-vocab: external — OPDS JSON decoding boundary.
    private static OpdsDocument ParseJson(string body, Uri source, string kind) {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (!root.TryGetProperty("publications", out _) && !root.TryGetProperty("navigation", out _) && !root.TryGetProperty("groups", out _))
            throw new IntegrationFailure("The server did not return an OPDS JSON catalog.");
        var entries = new List<OpdsEntry>();
        void ReadGroup(JsonElement group) {
            foreach (var navigation in Array(group, "navigation")) {
                var link = JsonLink(navigation, source);
                var target = Resolve(link);
                if (target is null || !SameOrigin(source, target) || !IsCatalog(link.Type)) continue;
                var title = Clean(String(navigation, "title")) ?? "Catalog";
                entries.Add(new(new(new(Hash(target.AbsoluteUri), target.AbsoluteUri, kind), true, new(title, null, [], new Dictionary<string, string>()), []), new Dictionary<string, OpdsAcquisition>()));
            }
            foreach (var publication in Array(group, "publications")) {
                if (!publication.TryGetProperty("metadata", out var metadata)) continue;
                var identity = Clean(String(metadata, "identifier"), 2048);
                var authors = new List<string>();
                if (metadata.TryGetProperty("author", out var author)) {
                    foreach (var item in author.ValueKind == JsonValueKind.Array ? author.EnumerateArray().ToArray() : new[] { author }) {
                        var name = Clean(item.ValueKind == JsonValueKind.String ? item.GetString() : String(item, "name"), 256);
                        if (name is not null) authors.Add(name);
                    }
                }
                var language = String(metadata, "language") ?? Array(metadata, "language").FirstOrDefault().ToString();
                var publisher = String(metadata, "publisher");
                if (publisher is null && metadata.TryGetProperty("publisher", out var publisherObject)) publisher = String(publisherObject, "name");
                var data = new CatalogPublication(Clean(String(metadata, "title")) ?? "Untitled publication", Clean(String(metadata, "description"), 8192),
                    authors.Take(64).ToArray(), identity is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [SourceIdentity] = identity },
                    Clean(language), Clean(publisher));
                Add(entries, identity, data, Array(publication, "links").Select(link => JsonLink(link, source)).ToArray(), source, kind);
            }
        }
        ReadGroup(root);
        foreach (var group in Array(root, "groups")) ReadGroup(group);
        var title = root.TryGetProperty("metadata", out var rootMetadata) ? Clean(String(rootMetadata, "title")) : null;
        return Document(title ?? "Catalog", entries, Array(root, "links").Select(link => JsonLink(link, source)).ToArray(), source);
    }

    // prism-vocab: external — OPDS JSON link fields are decoded only here.
    private static Link JsonLink(JsonElement link, Uri source) {
        var relations = String(link, "rel") is { } single ? new[] { single } : Array(link, "rel").Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray();
        var indirect = link.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("indirectAcquisition", out _);
        return new(String(link, "href") ?? "", String(link, "type"), relations, source, indirect,
            link.TryGetProperty("length", out var length) && length.TryGetInt64(out var size) && size >= 0 ? size : null);
    }
    private static string? String(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static IEnumerable<JsonElement> Array(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    #endregion

    #region Actions - Entries
    private static void Add(List<OpdsEntry> entries, string? identity, CatalogPublication publication, Link[] links, Uri source, string requestedKind) {
        var acquisitions = links.Where(link => link.Relations.Any(relation => relation == Acquisition || relation.StartsWith(Acquisition + "/", StringComparison.Ordinal))).ToArray();
        if (acquisitions.Length == 0) {
            var navigation = links.FirstOrDefault(link => IsCatalog(link.Type) && Resolve(link) is { } url && SameOrigin(source, url));
            if (navigation is null) return;
            var target = Resolve(navigation)!;
            entries.Add(new(new(new(identity ?? Hash(target.AbsoluteUri), target.AbsoluteUri, requestedKind), true, publication, []), new Dictionary<string, OpdsAcquisition>()));
            return;
        }
        var isComic = acquisitions.Any(link => IsComic(link.Type, Resolve(link)));
        var kind = isComic ? MediaKinds.Comic : MediaKinds.Book;
        if (kind != requestedKind) return;
        var offers = new List<CatalogOffer>();
        var resolved = new Dictionary<string, OpdsAcquisition>();
        foreach (var link in acquisitions) {
            var url = Resolve(link);
            if (url is null) continue;
            var relation = link.Relations.First(value => value == Acquisition || value.StartsWith(Acquisition + "/", StringComparison.Ordinal));
            var access = relation switch {
                Borrow => AcquisitionAccess.Borrow,
                Buy or Subscribe => AcquisitionAccess.Purchase,
                Sample => AcquisitionAccess.Sample,
                Acquisition or OpenAccess when !link.Indirect && IsPublication(link.Type) && SameOrigin(source, url) => AcquisitionAccess.Download,
                _ => AcquisitionAccess.External
            };
            var id = Hash($"{relation}\n{url.AbsoluteUri}\n{link.Type}");
            if (offers.Any(offer => offer.Id == id)) continue;
            var label = access switch {
                AcquisitionAccess.Download => "Download", AcquisitionAccess.Borrow => "Borrow", AcquisitionAccess.Purchase => "Purchase",
                AcquisitionAccess.Sample => "Sample", _ => "External service"
            };
            offers.Add(new(id, label, access, link.Type, link.Length));
            if (access == AcquisitionAccess.Download) resolved[id] = new(url, link.Type);
        }
        if (offers.Count > 32) throw new IntegrationFailure("The publication advertises too many acquisition offers.");
        identity ??= Hash(string.Join('\n', acquisitions.Select(link => Resolve(link)?.AbsoluteUri)));
        entries.Add(new(new(new(identity, source.AbsoluteUri, kind), false, publication, offers), resolved));
    }

    private static bool IsComic(string? type, Uri? url) => type is "application/vnd.comicbook+zip" or "application/vnd.comicbook-rar" or "application/x-cbz" or "application/x-cbr"
        || Path.GetExtension(url?.AbsolutePath ?? "").ToLowerInvariant() is ".cbz" or ".cbr";
    private static bool IsPublication(string? type) => type?.Split(';')[0].Trim().ToLowerInvariant() is "application/epub+zip" or "application/pdf"
        or "application/vnd.comicbook+zip" or "application/vnd.comicbook-rar" or "application/x-cbz" or "application/x-cbr"
        or "application/x-mobipocket-ebook" or "application/vnd.amazon.ebook";

    private static OpdsDocument Document(string title, List<OpdsEntry> entries, Link[] links, Uri source) {
        var next = links.Where(link => link.Relations.Contains(NextRelation)).Select(Resolve).FirstOrDefault(uri => uri is not null && SameOrigin(source, uri));
        var search = links.FirstOrDefault(link => link.Relations.Contains(SearchRelation) && (IsCatalog(link.Type) || link.Type == OpenSearchType)
            && Resolve(link) is { } uri && SameOrigin(source, uri));
        if (entries.Count > 10000) throw new IntegrationFailure("The catalog contains too many entries in one document.");
        return new(title, entries.DistinctBy(entry => entry.Item.Selection.ItemId).ToArray(), next,
            search is null ? null : new(Resolve(search)!, search.Type == OpenSearchType));
    }
    #endregion

    private sealed record Link(string Href, string? Type, string[] Relations, Uri Base, bool Indirect = false, long? Length = null);
}
