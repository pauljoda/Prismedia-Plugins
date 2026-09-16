namespace Prismedia.Plugin.Integrations;

/// <summary>Host request for finite URL inspection.</summary>
public sealed record InspectTransferInput(string Url, string EntityKind, int MaximumItems);
/// <summary>Inspectable publication choice in the host's vocabulary.</summary>
public sealed record InspectedTransferItem(string Id, string Title, string EntityKind);
/// <summary>Frozen finite source selection, never an instruction to crawl an account.</summary>
public sealed record TransferInspection(string SelectionId, string Revision, DateTimeOffset ExpiresAt, string CanonicalUrl,
    IReadOnlyList<InspectedTransferItem> Items, bool RequiresSelection, IReadOnlyList<string> Warnings);
/// <summary>Accepted finite executor intent, persisted by the host before invocation.</summary>
public sealed record SubmitTransferInput(Guid ClientOperationId, string Url, string SelectionId, string SelectionRevision,
    IReadOnlyList<string> ItemIds, int MaximumItems, long MaximumBytes);
/// <summary>Durable operation-key lookup.</summary>
public sealed record FindTransferInput(Guid ClientOperationId);
/// <summary>Remote identity-scoped job lookup.</summary>
public sealed record RemoteTransferJobInput(string JobId);
/// <summary>Stable manifest pagination request.</summary>
public sealed record ReadTransferManifestInput(string JobId, string Revision, string? Cursor, int Limit);
/// <summary>Exact artifact delivery authorization request.</summary>
public sealed record AuthorizeTransferArtifactInput(string JobId, string Revision, string ArtifactId);
/// <summary>Minimum guaranteed retention horizon.</summary>
public sealed record RenewTransferRetentionInput(string JobId, DateTimeOffset RetainUntil);
/// <summary>Exact imported bytes and local owning entities.</summary>
public sealed record ArtifactImport(string ArtifactId, string Sha256, IReadOnlyList<Guid> EntityIds);
/// <summary>Persisted receipt replayed independently of importing bytes.</summary>
public sealed record AcknowledgeTransferInput(string JobId, string ManifestRevision, Guid ReceiptId, IReadOnlyList<ArtifactImport> Artifacts);
