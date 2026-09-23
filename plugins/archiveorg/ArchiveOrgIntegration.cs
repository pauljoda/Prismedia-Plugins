using System.Text.Json;
using System.Text.RegularExpressions;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.ArchiveOrg;

internal static class ArchiveOrgProtocol {
    internal const string Host = "archive.org";
    internal const string ExternalIdProvider = "archive.org";
    internal const string ComicMediaType = "application/vnd.comicbook+zip";
    internal const string PublicDomainMark = "Public Domain Mark 1.0";
    internal const string CcZero = "CC0 1.0";
    internal const string PublicDomainQuery = "subject:(comic books) AND mediatype:texts AND licenseurl:*publicdomain*";
}

/// <summary>Discovers explicitly public-domain-labeled Archive items and revalidates original CBZ files.</summary>
internal sealed class ArchiveOrgIntegration(ArchiveOrgHttpClient http) {
    private sealed record Cursor(string? Query, string? Container, int Offset, int Limit);
    private sealed record ArchiveFile(string Name, long Size, string Sha1);
    private static readonly Regex IssuePattern = new(@"(?:#|\bNo\.?\s*)(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IdentifierPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$", RegexOptions.Compiled);

    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken cancellationToken) => request.Operation switch {
        IntegrationOperations.Probe => await ProbeAsync(cancellationToken),
        IntegrationOperations.Browse or IntegrationOperations.Search => await DiscoverAsync(
            Read<DiscoveryInput>(request.Input), request.Operation == IntegrationOperations.Search, cancellationToken),
        IntegrationOperations.Resolve => await ResolveAsync(Read<ResolveOfferInput>(request.Input), cancellationToken),
        _ => throw new IntegrationFailure("This Archive operation is not supported.")
    };

    private static T Read<T>(JsonElement input) => input.Deserialize<T>(IntegrationProtocol.Json)
        ?? throw new IntegrationFailure("The operation input is missing.");

    internal async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken) {
        using var result = await http.ReadAsync("/advancedsearch.php?q=mediatype%3Atexts&rows=0&output=json", cancellationToken);
        if (!result.RootElement.TryGetProperty("response", out _))
            throw new IntegrationFailure("Internet Archive did not return a search response.");
        return new(null, "Internet Archive public-domain comics", null, [
            new(IntegrationCapabilities.Discovery, [IntegrationOperations.Browse, IntegrationOperations.Search], [MediaKinds.Comic]),
            new(IntegrationCapabilities.AcquisitionSource, [IntegrationOperations.Resolve], [MediaKinds.Comic])
        ]);
    }

    internal async Task<CatalogPage> DiscoverAsync(DiscoveryInput input, bool searching, CancellationToken cancellationToken) {
        if (input.EntityKind != MediaKinds.Comic || input.Limit is < 1 or > 50)
            throw new IntegrationFailure("Choose a valid bounded comic catalog request.");
        Cursor cursor;
        if (input.Cursor is { Length: > 0 }) {
            try { cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(input.Cursor), IntegrationProtocol.Json)
                ?? throw new FormatException(); }
            catch (Exception error) when (error is FormatException or JsonException) {
                throw new IntegrationFailure("The Archive cursor is invalid.");
            }
            if (cursor.Limit != input.Limit || cursor.Offset is < 0 or > 5000
                || cursor.Container != input.Container || cursor.Query != (searching ? input.Query : null))
                throw new IntegrationFailure("The Archive cursor does not match this search.");
        } else cursor = new(searching ? input.Query : null, input.Container, 0, input.Limit);
        if (input.Container is { Length: > 0 }) {
            if (searching || !TryIdentifier(input.Container, out var identifier))
                throw new IntegrationFailure("The selected Archive item is invalid.");
            using var document = await ReadItemAsync(identifier, cancellationToken);
            var root = document.RootElement;
            if (!TryPublication(root, identifier, out var publication))
                throw new IntegrationFailure("This Archive item no longer has a supported public-domain label.");
            var files = Files(root);
            var fileItems = files.Skip(cursor.Offset).Take(input.Limit)
                .Select(file => FileItem(identifier, input.Container, publication, file)).ToArray();
            var next = cursor.Offset + fileItems.Length < files.Count ? Encode(cursor with { Offset = cursor.Offset + fileItems.Length }) : null;
            return new(publication.Title, fileItems, next, true);
        }
        if (searching && string.IsNullOrWhiteSpace(cursor.Query)) throw new IntegrationFailure("Enter a comic title to search.");
        if (cursor.Query is { Length: > 120 } || cursor.Query?.Any(char.IsControl) == true)
            throw new IntegrationFailure("The comic search is too long or contains invalid characters.");
        var query = ArchiveOrgProtocol.PublicDomainQuery;
        if (cursor.Query is { Length: > 0 } title) query += " AND title:\"" + title.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var pageNumber = cursor.Offset / input.Limit + 1;
        if (pageNumber > 100) throw new IntegrationFailure("The Archive result window is exhausted. Narrow the title search.");
        var path = "/advancedsearch.php?q=" + Uri.EscapeDataString(query)
            + "&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=creator&fl%5B%5D=licenseurl"
            + "&rows=" + input.Limit + "&page=" + pageNumber + "&output=json";
        using var results = await http.ReadAsync(path, cancellationToken);
        if (!results.RootElement.TryGetProperty("response", out var response)
            || !response.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
            throw new IntegrationFailure("Internet Archive returned an invalid search page.");
        var items = new List<CatalogItem>();
        foreach (var item in docs.EnumerateArray()) {
            var id = Text(item, "identifier");
            if (id is null || !IdentifierPattern.IsMatch(id) || !TryPublication(item, id, out var publication)) continue;
            var locator = ItemUrl(id);
            items.Add(new(new(id, locator, MediaKinds.Comic), true, publication, []));
        }
        var total = response.TryGetProperty("numFound", out var count) && count.TryGetInt32(out var found) ? found : 0;
        var nextCursor = cursor.Offset + input.Limit < total && pageNumber < 100
            ? Encode(cursor with { Offset = cursor.Offset + input.Limit }) : null;
        return new(searching ? "Public-domain comic results" : "Public-domain comics", items, nextCursor, true);
    }

    internal async Task<ResolvedSourceOffer> ResolveAsync(ResolveOfferInput input, CancellationToken cancellationToken) {
        if (input.Selection?.EntityKind != MediaKinds.Comic || !TryIdentifier(input.Selection.Locator, out var identifier))
            throw new IntegrationFailure("Select an Archive comic item.");
        using var document = await ReadItemAsync(identifier, cancellationToken);
        if (!TryPublication(document.RootElement, identifier, out var publication))
            throw new IntegrationFailure("This Archive item no longer has a supported public-domain label.");
        var file = Files(document.RootElement).FirstOrDefault(file =>
            input.Selection.ItemId == identifier + "/" + file.Name && input.OfferId == file.Name);
        if (file is null) throw new IntegrationFailure("The selected original CBZ is no longer available.");
        var item = FileItem(identifier, input.Selection.Locator, publication, file);
        var address = await http.ResolveFileAsync(identifier, file.Name, file.Size, cancellationToken);
        return new(input.Selection, input.OfferId, item.Publication, item.Offers[0],
            new(address.AbsoluteUri, new Dictionary<string, string>(), file.Name, file.Size, Sha1: file.Sha1));
    }

    private async Task<JsonDocument> ReadItemAsync(string identifier, CancellationToken cancellationToken) =>
        await http.ReadAsync("/metadata/" + Uri.EscapeDataString(identifier), cancellationToken);

    private static CatalogItem FileItem(string identifier, string locator, CatalogPublication publication, ArchiveFile file) {
        var itemId = identifier + "/" + file.Name;
        var externalIds = new Dictionary<string, string> { [ArchiveOrgProtocol.ExternalIdProvider] = itemId };
        var label = Path.GetFileNameWithoutExtension(file.Name).Replace('_', ' ');
        return new(new(itemId, locator, MediaKinds.Comic), false,
            publication with { ExternalIds = externalIds, EditionLabel = label },
            [new(file.Name, "Original CBZ", AcquisitionAccess.Download, ArchiveOrgProtocol.ComicMediaType, file.Size)]);
    }

    private static bool TryPublication(JsonElement item, string identifier, out CatalogPublication publication) {
        publication = null!;
        var metadata = item.TryGetProperty("metadata", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : item;
        var title = Text(metadata, "title");
        var license = Text(metadata, "licenseurl");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 512 || !TryPublicDomainLabel(license, out var licenseName)) return false;
        var creator = Text(metadata, "creator");
        var authors = creator is { Length: > 0 and <= 256 } ? new[] { creator } : [];
        var attribution = new CatalogAttribution("https://" + ArchiveOrgProtocol.Host + "/details/" + Uri.EscapeDataString(identifier),
            creator, null, licenseName, license,
            "The uploader labeled this item public domain. Check the item's rights information for your jurisdiction.", null);
        publication = new(title, null, authors,
            new Dictionary<string, string> { [ArchiveOrgProtocol.ExternalIdProvider] = identifier },
            Language: Text(metadata, "language"), IssueLabel: IssuePattern.Match(title) is { Success: true } match ? match.Groups[1].Value : null,
            Attribution: attribution);
        return true;
    }

    private static bool TryPublicDomainLabel(string? value, out string name) {
        name = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https") || address.IdnHost != "creativecommons.org"
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0) return false;
        name = address.AbsolutePath.TrimEnd('/') switch {
            "/publicdomain/mark/1.0" => ArchiveOrgProtocol.PublicDomainMark,
            "/publicdomain/zero/1.0" => ArchiveOrgProtocol.CcZero,
            _ => string.Empty
        };
        return name.Length > 0;
    }

    private static IReadOnlyList<ArchiveFile> Files(JsonElement item) {
        if (!item.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return [];
        return files.EnumerateArray().Where(file => file.ValueKind == JsonValueKind.Object
                && Text(file, "source") == "original" && !IsPrivate(file))
            .Select(file => new ArchiveFile(Text(file, "name") ?? string.Empty,
                long.TryParse(Text(file, "size"), out var size) ? size : 0, Text(file, "sha1") ?? string.Empty))
            .Where(file => file.Name.Length is > 4 and <= 255 && file.Name.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase)
                && !file.Name.Contains('/') && !file.Name.Contains('\\') && file.Size > 0
                && file.Sha1.Length == 40 && file.Sha1.All(Uri.IsHexDigit))
            .DistinctBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? Text(JsonElement item, string key) {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array) {
            foreach (var element in value.EnumerateArray()) if (element.ValueKind == JsonValueKind.String) return element.GetString();
        }
        return null;
    }

    private static bool IsPrivate(JsonElement file) => file.TryGetProperty("private", out var value)
        && value.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetString() is "true" or "1",
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            _ => false
        };

    private static string ItemUrl(string identifier) => "https://" + ArchiveOrgProtocol.Host + "/metadata/" + Uri.EscapeDataString(identifier);
    private static string Encode(Cursor cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, IntegrationProtocol.Json));
    private static bool TryIdentifier(string locator, out string identifier) {
        identifier = string.Empty;
        if (!Uri.TryCreate(locator, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps
            || address.IdnHost != ArchiveOrgProtocol.Host || !address.IsDefaultPort || address.UserInfo.Length != 0
            || address.Query.Length != 0 || address.Fragment.Length != 0 || !address.AbsolutePath.StartsWith("/metadata/", StringComparison.Ordinal)) return false;
        identifier = Uri.UnescapeDataString(address.AbsolutePath["/metadata/".Length..]);
        return IdentifierPattern.IsMatch(identifier) && address.AbsoluteUri == ItemUrl(identifier);
    }
}
