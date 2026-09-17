namespace Prismedia.Plugin.Archiver;

/// <summary>Archiver API v1 scalar vocabulary; translated at the adapter boundary.</summary>
internal static class ArchiverWire {
    internal const string Image = "image";
    internal const string ImageProfile = "single-image";
    internal const string Png = "png";
    internal const string Jpeg = "jpg";
    internal const string Webp = "webp";
    internal const string Book = "book";
    internal const string Comic = "comic";
    internal const string Profile = "single-publication";
    internal const string Epub = "epub";
    internal const string Pdf = "pdf";
    internal const string Cbz = "cbz";
    internal const string TokenKey = "token";
    internal const string Inspect = "inspect";
    internal const string Submit = "submit";
    internal const string Cancel = "cancel";
    internal const string CancelOperation = "cancel-operation";
    internal const string Artifacts = "artifacts";
    internal const string Retention = "retention";
    internal const string Receipts = "receipts";
}
internal sealed record SystemInfo(string InstanceId, string ApiVersion, string ApplicationVersion, IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> OutputProfiles, int MaximumItems, long MaximumBytes, int DefaultRetentionDays, int OperationRetentionDays);
internal sealed record SourceItem(string Id, string Title, string MediaKind, IReadOnlyList<string> Formats);
internal sealed record Inspection(string Id, string Revision, DateTimeOffset ExpiresAt, string CanonicalUrl, string SourceId,
    IReadOnlyList<SourceItem> Items, IReadOnlyList<string> Warnings);
internal sealed record InspectRequest(string Url, string MediaKind, int MaxItems);
internal sealed record JobInput(string Url);
internal sealed record JobSelection(string Id, string Revision, IReadOnlyList<string> ItemIds);
internal sealed record JobOutput(string Profile, string Format);
internal sealed record JobLimits(int MaxItems, long MaxBytes);
internal sealed record SubmitJob(Guid ClientOperationId, JobInput Input, JobSelection Selection, JobOutput Output, JobLimits Limits);
internal sealed record ItemFailure(string ItemId, string Message);
internal sealed record JobSnapshot(string InstanceId, string JobId, Guid ClientOperationId, long Revision, string State,
    double? Progress, string? ManifestRevision, DateTimeOffset? RetainedUntil, bool ArtifactsExpired,
    DateTimeOffset? NextPollAfter, IReadOnlyList<ItemFailure> ItemFailures);
internal sealed record Artifact(string Id, string ItemId, string RelativePath, string MediaType, long SizeBytes, string Sha256,
    string Role, string ContentPath, string? GroupId = null, int? Ordinal = null);
internal sealed record ManifestPage(string JobId, string Revision, bool Sealed, int ArtifactCount, IReadOnlyList<Artifact> Artifacts, string? NextCursor);
internal sealed record LeaseRequest(DateTimeOffset RetainUntil);
internal sealed record LeaseResult(string JobId, DateTimeOffset RetainedUntil);
internal sealed record ImportedArtifact(string ArtifactId, string Sha256, string? ClientReference);
internal sealed record ReceiptRequest(Guid ReceiptId, string ManifestRevision, IReadOnlyList<ImportedArtifact> Artifacts);
internal sealed record ReceiptResult(string JobId, Guid ReceiptId, bool Accepted);
/// <summary>Opaque adapter selection wraps Archiver identity plus the explicit negotiated output format.</summary>
internal sealed record PinnedSelection(string Id, string Format);

internal sealed record OperationCancellation(string InstanceId, Guid ClientOperationId, bool PreventedAcceptance, JobSnapshot? Job);
