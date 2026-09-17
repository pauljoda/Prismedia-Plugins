using System.Text.Json.Serialization;

namespace Prismedia.Plugin.Commons;

/// <summary>Fixed public service identities and MediaWiki wire vocabulary used by the adapter.</summary>
internal static class CommonsCodes {
    internal const string Origin = "https://commons.wikimedia.org";
    internal const string FileOrigin = "https://upload.wikimedia.org";
    internal const string WikiId = "commonswiki";
    internal const string IdentityNamespace = "commons";
    internal const string OriginalOffer = "original";
    internal const string TitlePrefix = "File:";
    internal const int FileNamespace = 6;
    internal const long MaximumBytes = 64L * 1024 * 1024;
    internal const long MaximumPixels = 100_000_000;
    internal const string Artist = "Artist";
    internal const string Credit = "Credit";
    internal const string LicenseName = "LicenseShortName";
    internal const string LicenseUrl = "LicenseUrl";
    internal const string UsageTerms = "UsageTerms";
    internal const string AttributionRequired = "AttributionRequired";
    internal const string Description = "ImageDescription";
}

/// <summary>MediaWiki query parameters, owned at this protocol boundary.</summary>
internal static class CommonsQuery {
    internal const string Action = "action";
    internal const string Query = "query";
    internal const string Format = "format";
    internal const string Json = "json";
    internal const string FormatVersion = "formatversion";
    internal const string MaxLag = "maxlag";
    internal const string Meta = "meta";
    internal const string SiteInfo = "siteinfo";
    internal const string SiteInfoProperty = "siprop";
    internal const string General = "general";
    internal const string Generator = "generator";
    internal const string Search = "search";
    internal const string SearchText = "gsrsearch";
    internal const string SearchNamespace = "gsrnamespace";
    internal const string SearchLimit = "gsrlimit";
    internal const string SearchOffset = "gsroffset";
    internal const string Properties = "prop";
    internal const string ImageInfo = "imageinfo";
    internal const string ImageProperties = "iiprop";
    internal const string ImageMetadataFilter = "iiextmetadatafilter";
    internal const string ImageLocalOnly = "iilocalonly";
    internal const string PageIds = "pageids";
    internal const string ImageFields = "url|size|mime|sha1|timestamp|extmetadata";
}

// prism-vocab: external — the single decode boundary for MediaWiki JSON field names.
internal sealed record CommonsResponse(
    [property: JsonPropertyName(CommonsQuery.Query)] CommonsQueryResult? Query,
    [property: JsonPropertyName("continue")] CommonsContinuation? Continue,
    [property: JsonPropertyName("error")] object? Error,
    [property: JsonPropertyName("batchcomplete")] bool? BatchComplete);
internal sealed record CommonsQueryResult(
    [property: JsonPropertyName(CommonsQuery.General)] CommonsSite? General,
    [property: JsonPropertyName("pages")] IReadOnlyList<CommonsPage>? Pages);
internal sealed record CommonsSite(
    [property: JsonPropertyName("wikiid")] string? WikiId,
    [property: JsonPropertyName(CommonsQuery.Generator)] string? Generator);
internal sealed record CommonsContinuation([property: JsonPropertyName(CommonsQuery.SearchOffset)] int? Offset);
internal sealed record CommonsPage(
    [property: JsonPropertyName("pageid")] long PageId,
    [property: JsonPropertyName("ns")] int Namespace,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("index")] int? Index,
    [property: JsonPropertyName(CommonsQuery.ImageInfo)] IReadOnlyList<CommonsImage>? Images);
internal sealed record CommonsImage(
    [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("width")] long Width,
    [property: JsonPropertyName("height")] long Height,
    [property: JsonPropertyName("mime")] string? Mime,
    [property: JsonPropertyName("sha1")] string? Sha1,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("descriptionurl")] string? DescriptionUrl,
    [property: JsonPropertyName("extmetadata")] IReadOnlyDictionary<string, CommonsMetadata>? Metadata);
internal sealed record CommonsMetadata([property: JsonPropertyName("value")] string? Value);
