namespace Prismedia.Plugin.Integrations;

/// <summary>Read-only evidence for relinquishing an externally managed acquisition scope.</summary>
public static class ManagerRelease {
    #region Static Variables
    public const string Inspect = "inspect-managed-release";
    #endregion
}
/// <summary>The exact reviewed holding and finite targets; no remote mutation is authorized.</summary>
public sealed record InspectManagedReleaseInput(ManagedControlScope Scope);
/// <summary>
/// A current configuration or explicitly confirmed remote absence, plus complete application-wide
/// activity observation. Empty queues do not establish availability, cancel downloads, or guarantee
/// that another user cannot start new work.
/// </summary>
public sealed record ManagedReleaseObservation(
    ManagedControlState? State,
    bool QueueEmpty,
    bool CommandsIdle,
    bool RemoteItemAbsent = false);
