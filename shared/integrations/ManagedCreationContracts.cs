namespace Prismedia.Plugin.Integrations;

/// <summary>Canonical operations for exact identity lookup and initial unmonitored creation.</summary>
public static class ManagerCreation {
    public const string Lookup = "lookup-managed";
    public const string Ensure = "ensure-managed";
}
/// <summary>Exact metadata identities, never a title-only creation guess.</summary>
public sealed record ManagedLookupInput(string EntityKind, IReadOnlyDictionary<string, string> ExternalIds);
/// <summary>A work confirmed by the manager's metadata source, independently of existing holdings.</summary>
public sealed record ManagedCandidate(string EntityKind, string Title, int? Year, IReadOnlyDictionary<string, string> ExternalIds);
/// <summary>Existing holding evidence may resolve an uncertain creation without another write.</summary>
public sealed record ManagedLookupResult(ManagedCandidate Candidate, ManagedItemSnapshot? Existing);
/// <summary>Creation is unmonitored and does not search. Existing holdings preserve all their settings.</summary>
public sealed record EnsureManagedInput(Guid OperationId, ManagedLookupInput Work, string ProfileId, string RootId, string ExpectedRootPath);
/// <summary>A definite creation result; response loss is uncertain and must be recovered by identity lookup.</summary>
public sealed record EnsureManagedResult(string Outcome, ManagedItemSnapshot? Holding = null, bool Created = false, string? Problem = null);
