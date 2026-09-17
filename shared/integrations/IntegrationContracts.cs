using System.Text.Json;

namespace Prismedia.Plugin.Integrations;

/// <summary>Independent connected-application wire protocol; does not replace metadata identify v2.</summary>
public static class IntegrationProtocol {
    public const string Name = "prismedia-integration";
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, MaxDepth = 64 };
}

/// <summary>Typed integration operations supported by the common executable harness.</summary>
public static class IntegrationOperations {
    public const string Probe = "probe";
    public const string Browse = "browse";
    public const string Search = "search";
    public const string Resolve = "resolve";
    public const string Inspect = "inspect";
    public const string Submit = "submit";
    public const string FindSubmission = "find-submission";
    public const string CancelSubmission = "cancel-submission";
    public const string GetJob = "get-job";
    public const string Cancel = "cancel";
    public const string ListArtifacts = "list-artifacts";
    public const string AuthorizeArtifact = "authorize-artifact";
    public const string RenewRetention = "renew-retention";
    public const string Acknowledge = "acknowledge";
}
/// <summary>Independent connected-application capability families.</summary>
public static class IntegrationCapabilities {
    public const string Discovery = "catalog-discovery";
    public const string AcquisitionSource = "acquisition-source";
    public const string TransferExecutor = "transfer-executor";
}
/// <summary>Prismedia entity kinds supported by publication catalogs.</summary>
public static class MediaKinds {
    public const string Gallery = "gallery";
    public const string Image = "image";
    public const string Book = "book";
    public const string Comic = "comic-installment";
}
/// <summary>Separates full downloads from loans, checkout, previews, and external workflows.</summary>
public static class AcquisitionAccess {
    public const string Download = "download";
    public const string Borrow = "borrow";
    public const string Purchase = "purchase";
    public const string Sample = "sample";
    public const string External = "external";
}
/// <summary>Instance configuration and decrypted credentials supplied only for this invocation.</summary>
public sealed record ConnectionContext(Guid Id, string BaseUrl, string? ExpectedInstanceId, IReadOnlyDictionary<string, string> Settings, IReadOnlyDictionary<string, string> Auth);
/// <summary>Correlated host request with a separately versioned integration protocol.</summary>
public sealed record IntegrationRequest(string Protocol, int ProtocolVersion, Guid InvocationId, string Operation, ConnectionContext Connection, JsonElement Input);
/// <summary>Correlated success or safe failure emitted by an integration executable.</summary>
public sealed record IntegrationResponse(string Protocol, int ProtocolVersion, Guid InvocationId, bool Ok, object? Result, string? Error);
/// <summary>Declared operation and media-kind support for one capability family.</summary>
public sealed record Capability(string Kind, IReadOnlyList<string> Operations, IReadOnlyList<string> EntityKinds);
/// <summary>Observed remote support; InstanceId is null when the upstream has no persistent installation identity.</summary>
public sealed record ProbeResult(string? InstanceId, string DisplayName, string? Version, IReadOnlyList<Capability> Capabilities);
/// <summary>Bounded source browsing or search; opaque cursors remain owned by the adapter.</summary>
public sealed record DiscoveryInput(string EntityKind, string? Query, string? Cursor, string? Container, int Limit);
/// <summary>Source record identity and its resolution location, scoped by the host connection.</summary>
public sealed record SourceSelection(string ItemId, string Locator, string EntityKind);
/// <summary>Non-executable acquisition choice, including its access constraints.</summary>
public sealed record CatalogOffer(string Id, string Label, string Access, string? MediaType = null, long? ByteSize = null);
/// <summary>Descriptive evidence from a catalog; it is not an automatic metadata update.</summary>
public sealed record CatalogPublication(string Title, string? Description, IReadOnlyList<string> Authors, IReadOnlyDictionary<string, string> ExternalIds,
    string? Language = null, string? Publisher = null, string? EditionLabel = null, string? IssueLabel = null, CatalogAttribution? Attribution = null);
/// <summary>Plain-text source attribution and license statements retained by the host with accepted imports.</summary>
public sealed record CatalogAttribution(string SourceUrl, string? Creator, string? Credit, string? LicenseName,
    string? LicenseUrl, string? UsageTerms, bool? AttributionRequired);
/// <summary>A navigable container or a publication with explicitly classified acquisition choices.</summary>
public sealed record CatalogItem(SourceSelection Selection, bool IsContainer, CatalogPublication Publication, IReadOnlyList<CatalogOffer> Offers);
/// <summary>One bounded result page and an optional adapter-owned continuation cursor.</summary>
public sealed record CatalogPage(string Title, IReadOnlyList<CatalogItem> Items, string? NextCursor = null);
/// <summary>Exact source item and offer selected for revalidation before acquisition.</summary>
public sealed record ResolveOfferInput(SourceSelection Selection, string OfferId);
/// <summary>Server-only byte retrieval instructions; credentials must never reach browser responses.</summary>
public sealed record HttpArtifactDelivery(string Url, IReadOnlyDictionary<string, string> Headers, string SuggestedFileName,
    long? ByteSize = null, string? Sha256 = null, DateTimeOffset? ExpiresAt = null, string? Sha1 = null);
/// <summary>Revalidated full-publication offer and its server-only delivery instructions.</summary>
public sealed record ResolvedSourceOffer(SourceSelection Selection, string OfferId, CatalogPublication Publication, CatalogOffer Offer, HttpArtifactDelivery Delivery);

/// <summary>Actionable failure whose text is safe to expose to the connection owner.</summary>
public sealed class IntegrationFailure(string message) : Exception(message);
