using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Commons;

/// <summary>Searches Commons images and resolves exact selected file versions with source attribution.</summary>
internal sealed class CommonsIntegration(CommonsClient client, ConnectionContext connection) {
    #region Static Variables
    private static readonly Capability[] Capabilities = [
        new(IntegrationCapabilities.Discovery, [IntegrationOperations.Search], [MediaKinds.Image]),
        new(IntegrationCapabilities.AcquisitionSource, [IntegrationOperations.Resolve], [MediaKinds.Image])
    ];
    #endregion

    #region Actions - Dispatch
    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken cancellationToken) => request.Operation switch {
        IntegrationOperations.Probe => await Probe(cancellationToken),
        IntegrationOperations.Search => await Search(Read<DiscoveryInput>(request.Input), cancellationToken),
        IntegrationOperations.Resolve => await Resolve(Read<ResolveOfferInput>(request.Input), cancellationToken),
        _ => throw new IntegrationFailure("This Commons operation is unavailable.")
    };
    #endregion

    #region Actions - Catalog
    private async Task<ProbeResult> Probe(CancellationToken cancellationToken) {
        var result = await client.ReadAsync(new Dictionary<string, string> { [CommonsQuery.Meta] = CommonsQuery.SiteInfo,
            [CommonsQuery.SiteInfoProperty] = CommonsQuery.General }, cancellationToken);
        if (result.Query?.General?.WikiId != CommonsCodes.WikiId) throw new IntegrationFailure("The endpoint did not identify itself as Wikimedia Commons.");
        return new(null, "Wikimedia Commons", result.Query.General.Generator, Capabilities);
    }

    private async Task<CatalogPage> Search(DiscoveryInput input, CancellationToken cancellationToken) {
        if (input.EntityKind != MediaKinds.Image || input.Limit is < 1 or > 100 || string.IsNullOrWhiteSpace(input.Query)
            || input.Query.Length > 512 || input.Container is not null) throw new IntegrationFailure("Search Commons for a still image using up to 512 characters.");
        var query = input.Query.Trim();
        var offset = 0;
        if (input.Cursor is not null) {
            var cursor = Decode<Cursor>(input.Cursor);
            if (cursor.ConnectionId != connection.Id || cursor.Query != query || cursor.Limit != input.Limit || cursor.Offset is < 0 or > 10000)
                throw new IntegrationFailure("Start a new Commons search after changing its connection or query.");
            offset = cursor.Offset;
        }
        var parameters = ImageParameters();
        parameters[CommonsQuery.Generator] = CommonsQuery.Search;
        parameters[CommonsQuery.SearchText] = query;
        parameters[CommonsQuery.SearchNamespace] = CommonsCodes.FileNamespace.ToString(CultureInfo.InvariantCulture);
        parameters[CommonsQuery.SearchLimit] = Math.Min(10, input.Limit).ToString(CultureInfo.InvariantCulture);
        parameters[CommonsQuery.SearchOffset] = offset.ToString(CultureInfo.InvariantCulture);
        var result = await client.ReadAsync(parameters, cancellationToken);
        var pages = result.Query?.Pages ?? [];
        if (pages.Count > Math.Min(10, input.Limit) || pages.Select(page => page.PageId).Distinct().Count() != pages.Count)
            throw new IntegrationFailure("Commons returned an inconsistent image page.");
        var items = pages.OrderBy(page => page.Index ?? int.MaxValue).Select(ToItem).OfType<CatalogItem>().ToArray();
        var next = result.Continue?.Offset;
        if (next is not null && (next <= offset || next > 10000)) throw new IntegrationFailure("Commons returned an invalid continuation.");
        return new("Wikimedia Commons", items, next is { } value ? Encode(new Cursor(connection.Id, query, input.Limit, value)) : null);
    }

    private async Task<ResolvedSourceOffer> Resolve(ResolveOfferInput input, CancellationToken cancellationToken) {
        if (input.Selection?.EntityKind != MediaKinds.Image || input.OfferId != CommonsCodes.OriginalOffer)
            throw new IntegrationFailure("Select an original Commons image offer.");
        var selection = Decode<Selection>(input.Selection.Locator);
        if (selection.ConnectionId != connection.Id || selection.PageId <= 0 || input.Selection.ItemId != selection.PageId.ToString(CultureInfo.InvariantCulture))
            throw new IntegrationFailure("The image selection belongs to another source or connection.");
        var parameters = ImageParameters();
        parameters[CommonsQuery.PageIds] = selection.PageId.ToString(CultureInfo.InvariantCulture);
        var result = await client.ReadAsync(parameters, cancellationToken);
        var pages = result.Query?.Pages;
        if (pages is not { Count: 1 } || pages[0].PageId != selection.PageId || ToItem(pages[0]) is not { } item
            || Decode<Selection>(item.Selection.Locator) != selection) throw new IntegrationFailure("The Commons file changed or is unavailable. Search again to select its current version.");
        var image = pages[0].Images![0];
        var url = FileUrl(image.Url!);
        var fileName = $"commons-{selection.PageId.ToString(CultureInfo.InvariantCulture)}{Extension(image.Mime!)}";
        return new(input.Selection, input.OfferId, item.Publication, item.Offers[0],
            new(url, new Dictionary<string, string>(), fileName, image.Size, Sha1: selection.Sha1));
    }
    #endregion

    #region Actions - Evidence
    private CatalogItem? ToItem(CommonsPage page) {
        if (page.Namespace != CommonsCodes.FileNamespace || page.PageId <= 0 || string.IsNullOrWhiteSpace(page.Title) || page.Title.Length > 512
            || page.Images is not { Count: 1 } || page.Images[0] is not { } image || Extension(image.Mime) is null
            || image.Size is <= 0 or > CommonsCodes.MaximumBytes || image.Width <= 0 || image.Height <= 0
            || image.Width > CommonsCodes.MaximumPixels / image.Height || image.Timestamp is null
            || image.Sha1 is not { Length: 40 } || !image.Sha1.All(Uri.IsHexDigit) || image.Url is null) return null;
        _ = FileUrl(image.Url);
        if (!Uri.TryCreate(image.DescriptionUrl, UriKind.Absolute, out var source) || !CommonsClient.SameOrigin(source, CommonsCodes.Origin))
            throw new IntegrationFailure("Commons returned an invalid source attribution link.");
        var creator = Text(image, CommonsCodes.Artist, 2048);
        var licenseUrl = Text(image, CommonsCodes.LicenseUrl, 2048);
        if (licenseUrl is not null && (!Uri.TryCreate(licenseUrl, UriKind.Absolute, out var license) || license.UserInfo.Length != 0
            || license.Scheme is not ("http" or "https"))) throw new IntegrationFailure("Commons returned an invalid license link.");
        var attribution = new CatalogAttribution(source.AbsoluteUri, creator, Text(image, CommonsCodes.Credit, 8192),
            Text(image, CommonsCodes.LicenseName, 256), licenseUrl, Text(image, CommonsCodes.UsageTerms, 2048),
            bool.TryParse(Text(image, CommonsCodes.AttributionRequired, 16), out var required) ? required : null);
        var title = page.Title.StartsWith(CommonsCodes.TitlePrefix, StringComparison.Ordinal) ? page.Title[CommonsCodes.TitlePrefix.Length..] : page.Title;
        var id = page.PageId.ToString(CultureInfo.InvariantCulture);
        return new(new(id, Encode(new Selection(connection.Id, page.PageId, image.Sha1.ToLowerInvariant(), image.Timestamp.Value)), MediaKinds.Image), false,
            new(title, Text(image, CommonsCodes.Description, 8192), creator is { Length: <= 256 } ? [creator] : [],
                new Dictionary<string, string> { [CommonsCodes.IdentityNamespace] = id }, Attribution: attribution),
            [new(CommonsCodes.OriginalOffer, "Original image", AcquisitionAccess.Download, image.Mime, image.Size)]);
    }

    private static string FileUrl(string value) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || !CommonsClient.SameOrigin(url, CommonsCodes.FileOrigin)
            || url.Fragment.Length != 0 || !url.AbsolutePath.StartsWith("/wikipedia/commons/", StringComparison.Ordinal))
            throw new IntegrationFailure("Commons returned an image outside its public file host.");
        return new UriBuilder(url) { Query = string.Empty }.Uri.AbsoluteUri;
    }
    private static string? Extension(string? mime) => mime switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", _ => null };
    private static string? Text(CommonsImage image, string key, int limit) {
        var value = image.Metadata?.GetValueOrDefault(key)?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 65536) throw new IntegrationFailure("Commons returned oversized attribution text.");
        var plain = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]*>", " ", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)));
        plain = Regex.Replace(plain, @"\s+", " ", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)).Trim();
        if (plain.Length > limit) throw new IntegrationFailure("Commons returned oversized attribution text.");
        return plain.Length == 0 ? null : plain;
    }
    private static Dictionary<string, string> ImageParameters() => new() {
        [CommonsQuery.Properties] = CommonsQuery.ImageInfo, [CommonsQuery.ImageProperties] = CommonsQuery.ImageFields,
        [CommonsQuery.ImageLocalOnly] = "1", [CommonsQuery.ImageMetadataFilter] = string.Join('|', CommonsCodes.Artist, CommonsCodes.Credit,
            CommonsCodes.LicenseName, CommonsCodes.LicenseUrl, CommonsCodes.UsageTerms, CommonsCodes.AttributionRequired, CommonsCodes.Description)
    };
    private static T Read<T>(JsonElement input) => input.Deserialize<T>(IntegrationProtocol.Json) ?? throw new IntegrationFailure("The Commons request is incomplete.");
    private static string Encode<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, IntegrationProtocol.Json));
    private static T Decode<T>(string value) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192) throw new IntegrationFailure("The Commons selection or cursor is invalid.");
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(value), IntegrationProtocol.Json) ?? throw new IntegrationFailure("The Commons selection or cursor is invalid."); }
        catch (Exception error) when (error is JsonException or FormatException) { throw new IntegrationFailure("The Commons selection or cursor is invalid."); }
    }
    #endregion

    private sealed record Cursor(Guid ConnectionId, string Query, int Limit, int Offset);
    private sealed record Selection(Guid ConnectionId, long PageId, string Sha1, DateTimeOffset Timestamp);
}
