namespace Prismedia.Plugin.Integrations;

/// <summary>Canonical wire values for finite manager controls; command completion is not file availability.</summary>
public static class ManagerControls {
    public const string Reconcile = "reconcile-managed";
    public const string Configure = "configure-managed";
    public const string Request = "request-managed";
    public const string Applied = "applied";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Unknown = "unknown";
}
/// <summary>Exact target identity and numbering, independently of current bytes.</summary>
public sealed record ManagedControlTarget(string RemoteId, string EntityKind,
    int? SeasonNumber = null, int? EpisodeNumber = null, int? AbsoluteNumber = null);
/// <summary>A pinned work and finite targets, never an implicit entire-series request.</summary>
public sealed record ManagedControlScope(ManagedItemInput Item, IReadOnlyList<ManagedControlTarget> Targets);
/// <summary>Current monitoring of one selected target.</summary>
public sealed record ManagedTargetMonitoring(ManagedControlTarget Target, bool Monitored);
/// <summary>Controls supported faithfully for this exact scope.</summary>
public sealed record ManagedControlCapabilities(bool CanSearch, bool CanChangeMonitoring, bool CanChangeProfile,
    string? MonitoringUnavailableReason = null);
/// <summary>Command history is separate from available source files.</summary>
public sealed record ManagedCommandSnapshot(ManagedCommandReference Reference, string Status, string? Problem = null);
/// <summary>Queue time fences transient numeric command ID reuse across restarts.</summary>
public sealed record ManagedCommandReference(string Id, DateTimeOffset QueuedAt);
/// <summary>Current configuration and optional exact command observation.</summary>
public sealed record ManagedControlState(ManagedLibraryItem Item, string Path,
    IReadOnlyList<ManagedTargetMonitoring> Targets, ManagedControlCapabilities Capabilities, ManagedCommandSnapshot? Command = null);
/// <summary>Observes configuration and an optional known command without writing.</summary>
public sealed record ReconcileManagedInput(ManagedControlScope Scope, ManagedCommandReference? Command = null);
/// <summary>Null fields preserve their existing values.</summary>
public sealed record ManagedConfigurationChange(string? ProfileId = null, bool? Monitored = null);
/// <summary>Reviewed changes; OperationId is host correlation and not an upstream idempotency promise.</summary>
public sealed record ConfigureManagedInput(Guid OperationId, ManagedControlScope Scope, string ExpectedPath,
    string ExpectedProfileId, IReadOnlyDictionary<string, bool> ExpectedMonitoring, ManagedConfigurationChange Changes);
/// <summary>Exactly one scoped search with no implicit monitoring changes.</summary>
public sealed record RequestManagedInput(Guid OperationId, ManagedControlScope Scope, string ExpectedPath, string ExpectedProfileId);
/// <summary>Only definite outcomes may be returned; ambiguous writes fail the invocation.</summary>
public sealed record ManagedMutationResult(string Outcome, ManagedCommandSnapshot? Command = null, string? Problem = null);
