namespace Prismedia.Plugin.Integrations;

/// <summary>Requests one pinned publication after the host has durably accepted its operation. Replays must coalesce the same source selection.</summary>
public sealed record RequestSourceInput(Guid OperationId, SourceSelection Selection, string OfferId);

/// <summary>Reads readiness of the exact source selection without enqueuing downloads, updating reading progress, or changing its scope.</summary>
public sealed record ObserveSourceInput(SourceSelection Selection, string OfferId);

/// <summary>Canonical wire states for source-owned preparation that does not claim executor semantics.</summary>
public static class SourceAcquisitionStates {
    public const string NotObserved = "not-observed";
    public const string Queued = "queued";
    public const string Downloading = "downloading";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary>Source-owned preparation evidence. Ready permits resolving a direct offer; it does not prove retained bytes or a committed local import.</summary>
public sealed record SourceAcquisitionObservation(SourceSelection Selection, string OfferId,
    CatalogPublication Publication, CatalogOffer Offer, string State,
    double? Progress = null, DateTimeOffset? NextPollAfter = null, string? Problem = null);
