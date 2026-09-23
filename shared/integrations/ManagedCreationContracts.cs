namespace Prismedia.Plugin.Integrations;

/// <summary>Canonical operations for exact identity lookup and initial unmonitored creation.</summary>
public static class ManagerCreation {
    public const string Lookup = "lookup-managed";
    public const string Ensure = "ensure-managed";
}
/// <summary>One finite child target requested beneath a managed container, before remote IDs are resolved.</summary>
public sealed record ManagedLookupTarget(string EntityKind, IReadOnlyDictionary<string, string> ExternalIds,
    int? SeasonNumber = null, int? EpisodeNumber = null, int? AbsoluteNumber = null, string? IssueLabel = null);
/// <summary>Exact metadata identities, independent of an external application's local item IDs.</summary>
public sealed record ManagedLookupInput(string EntityKind, IReadOnlyDictionary<string, string> ExternalIds,
    IReadOnlyList<ManagedLookupTarget>? Targets = null);
/// <summary>A work confirmed by the manager's metadata source; it is not evidence of owned files.</summary>
public sealed record ManagedCandidate(string EntityKind, string Title, int? Year,
    IReadOnlyDictionary<string, string> ExternalIds, ManagedDiscoveryMetadata? Metadata = null);
/// <summary>One manager-resolved stable child identity corresponding to an exact requested target.</summary>
public sealed record ManagedResolvedTarget(string RemoteId, string EntityKind,
    IReadOnlyDictionary<string, string> ExternalIds, int? SeasonNumber = null, int? EpisodeNumber = null,
    int? AbsoluteNumber = null, string? IssueLabel = null);
/// <summary>Read-only lookup can reconcile an uncertain creation without repeating its mutation.</summary>
public sealed record ManagedLookupResult(ManagedCandidate Candidate, ManagedItemSnapshot? Existing,
    IReadOnlyList<ManagedResolvedTarget>? Targets = null);
/// <summary>Initial creation is unmonitored and does not search. Existing holdings retain all settings.</summary>
public sealed record EnsureManagedInput(Guid OperationId, ManagedLookupInput Work, string ProfileId, string RootId, string ExpectedRootPath);
/// <summary>Only definite outcomes may be returned; lost responses must leave the host's creation intent uncertain.</summary>
public sealed record EnsureManagedResult(string Outcome, ManagedItemSnapshot? Holding = null, bool Created = false, string? Problem = null,
    IReadOnlyList<ManagedResolvedTarget>? Targets = null);
