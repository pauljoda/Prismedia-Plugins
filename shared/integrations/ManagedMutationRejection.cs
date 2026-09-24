namespace Prismedia.Plugin.Integrations;

/// <summary>
/// A definite refusal of one manager mutation: the host supplied invalid or stale input, a reviewed
/// precondition no longer holds, or the remote application definitively refused the write before
/// changing anything. Raise it only before a write is sent or from the write's own definite refusal.
/// Transport errors, timeouts, server errors, and unrecognized replies stay ordinary
/// <see cref="IntegrationFailure"/>s, which the host treats as uncertain outcomes that must not be
/// repeated automatically.
/// </summary>
/// <param name="problem">Specific, user-facing reason the change was not made.</param>
public sealed class ManagedMutationRejection(string problem) : IntegrationFailure(problem) {
    #region Variables
    /// <summary>Specific, user-facing reason the change was not made.</summary>
    public string Problem => Message;
    #endregion

    #region Actions - Outcomes
    /// <summary>
    /// Runs one control mutation and reports a definite refusal as a rejected outcome that carries its
    /// problem. Every other failure propagates so the host keeps the outcome uncertain.
    /// </summary>
    /// <param name="mutation">The validation, precondition checks, write, and confirmation to run.</param>
    /// <returns>The mutation's own outcome, or a rejection naming why nothing was changed.</returns>
    public static async Task<ManagedMutationResult> CaptureAsync(Func<Task<ManagedMutationResult>> mutation) {
        try {
            return await mutation().ConfigureAwait(false);
        } catch (ManagedMutationRejection rejection) {
            return new(ManagerControls.Rejected, Problem: rejection.Problem);
        }
    }

    /// <summary>
    /// Runs one initial creation and reports a definite refusal as a rejected creation that carries its
    /// problem. Every other failure propagates so the host keeps the creation uncertain.
    /// </summary>
    /// <param name="creation">The validation, precondition checks, write, and confirmation to run.</param>
    /// <returns>The creation's own result, or a rejection naming why nothing was created.</returns>
    public static async Task<EnsureManagedResult> CaptureCreationAsync(Func<Task<EnsureManagedResult>> creation) {
        try {
            return await creation().ConfigureAwait(false);
        } catch (ManagedMutationRejection rejection) {
            return new(ManagerControls.Rejected, Problem: rejection.Problem);
        }
    }
    #endregion
}
