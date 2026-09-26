using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Kapowarr;

internal sealed partial class KapowarrLibrary {
    #region Actions - Controls
    /// <summary>
    /// Observes one exact issue. Monitoring is reported as Kapowarr applies it: an issue counts as
    /// monitored only inside a monitored run. Search is offered only when Kapowarr's automatic issue
    /// search would actually run.
    /// </summary>
    private async Task<ManagedControlState> ReconcileAsync(ReconcileManagedInput input, CancellationToken token) {
        var (run, targets) = await ReadScopeAsync(input.Scope, token);
        var command = input.Command is { } reference
            ? new ManagedCommandSnapshot(reference, ManagerControls.Unknown, "Kapowarr cannot establish this command's original outcome.")
            : null;
        var monitoring = targets.Select(target => new ManagedTargetMonitoring(target.Target, run.Monitors(target.Issue))).ToArray();
        return new(run.Snapshot.Item, run.Snapshot.Path, monitoring, Capabilities(run, targets), command);
    }

    /// <summary>
    /// Turns one issue's monitoring on or off. Turning it on also monitors the parent run, without a
    /// monitoring scheme and without new-issue monitoring, because Kapowarr never searches an issue
    /// in an unmonitored run. It refuses when monitoring the run would widen the search beyond the issue.
    /// </summary>
    private Task<ManagedMutationResult> ConfigureAsync(ConfigureManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureAsync(async () => {
            if (input.OperationId == Guid.Empty || input.Changes is null || input.Changes.ProfileId is not null
                || input.Changes.Monitored is not { } desired || input.ExpectedProfileId is not null
                || input.ExpectedMonitoring is null || input.Scope?.Targets is not { Count: 1 })
                throw new ManagedMutationRejection("Select one exact comic issue, its reviewed monitoring, and a monitoring change. Kapowarr runs have no profiles.");
            var (run, targets) = await ReadScopeAsync(input.Scope, token);
            var (target, issue) = targets[0];
            RequirePath(run, input.ExpectedPath);
            if (input.ExpectedMonitoring.Count != 1 || !input.ExpectedMonitoring.TryGetValue(target.RemoteId, out var expected)
                || expected != run.Monitors(issue))
                throw new ManagedMutationRejection($"Monitoring for issue {target.IssueLabel} changed in Kapowarr since review. Refresh its settings.");
            var monitorRun = desired && !run.Monitored;
            if (monitorRun && run.RunMonitoringBlocker(issue) is { } blocker) throw new ManagedMutationRejection(blocker);
            var issueChanged = issue.Monitored != desired;
            if (issueChanged) {
                var updated = await client.PutAsync<KapowarrIssueEdit, KapowarrIssue>("issues/" + Id(issue.Id), new(desired), token);
                if (updated.Id != issue.Id || updated.VolumeId != run.Id || updated.IssueNumber != issue.IssueNumber || updated.Monitored != desired)
                    throw new IntegrationFailure("Kapowarr did not confirm the selected issue's monitoring change.");
            }
            if (!monitorRun) return new(ManagerControls.Applied);
            if (!issueChanged) return await MonitorRunAsync(input.Scope, issue, input.ExpectedPath, token);
            return await ManagedMutationRejection.AfterWriteAsync(() => MonitorRunAsync(input.Scope, issue, input.ExpectedPath, token),
                $"Kapowarr monitored issue {target.IssueLabel}, but its run is not confirmed as monitored.");
        });

    /// <summary>
    /// Queues Kapowarr's automatic search for one exact issue. It refuses when that search would do
    /// nothing: an unmonitored run, an unmonitored issue, or an issue that already has a file.
    /// </summary>
    private Task<ManagedMutationResult> RequestAsync(RequestManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureAsync(async () => {
            if (input.OperationId == Guid.Empty || input.ExpectedProfileId is not null || input.Scope?.Targets is not { Count: 1 })
                throw new ManagedMutationRejection("Select one exact comic issue and a durable operation ID. Kapowarr runs have no profiles.");
            var (run, targets) = await ReadScopeAsync(input.Scope, token);
            var issue = targets[0].Issue;
            RequirePath(run, input.ExpectedPath);
            if (run.SearchBlocker(issue) is { } blocker) throw new ManagedMutationRejection(blocker);
            var receipt = await client.PostAsync<KapowarrTaskCommand, KapowarrTaskReceipt>("system/tasks",
                KapowarrTaskCommand.SearchIssue(run.Id, issue.Id), token);
            if (receipt.Id <= 0) throw new IntegrationFailure("Kapowarr did not return a search task ID.");
            // Kapowarr's task IDs have no durable timestamp or completed history. Preserve the receipt,
            // but reconciliation must report Unknown instead of inferring that a vanished task succeeded.
            return new(ManagerControls.Accepted, new(new(Id(receipt.Id), DateTimeOffset.UtcNow), ManagerControls.Pending));
        });

    private async Task<ManagedMutationResult> MonitorRunAsync(ManagedControlScope scope, KapowarrIssue issue,
        string expectedPath, CancellationToken token) {
        await client.PutAcknowledgedAsync("volumes/" + Id(issue.VolumeId), new KapowarrVolumeEdit(true), token);
        return await ManagedMutationRejection.AfterWriteAsync(async () => {
            var observed = await ReadRunAsync(scope.Item, token);
            var observedIssue = observed.RequireIssue(scope.Targets[0]);
            if (observed.Snapshot.Path != expectedPath || !observed.Monitored || observed.MonitorsNewIssues != false || !observedIssue.Monitored)
                throw new IntegrationFailure("Kapowarr did not confirm monitoring for the selected issue and its run. Review the run in Kapowarr before another change.");
            return new ManagedMutationResult(ManagerControls.Applied);
        }, "Kapowarr accepted monitoring for the run, but the result could not be confirmed.");
    }

    private async Task<(KapowarrRun Run, IReadOnlyList<(ManagedControlTarget Target, KapowarrIssue Issue)> Targets)> ReadScopeAsync(
        ManagedControlScope? scope, CancellationToken token) {
        if (scope?.Item is null || scope.Targets is not { Count: > 0 and <= 10000 }
            || scope.Targets.Select(target => target.RemoteId).Distinct(StringComparer.Ordinal).Count() != scope.Targets.Count
            || scope.Targets.Any(target => target.EntityKind != MediaKinds.Comic || target.SeasonNumber is not null
                || target.EpisodeNumber is not null || target.AbsoluteNumber is not null
                || string.IsNullOrWhiteSpace(target.IssueLabel) || target.IssueLabel.Length > 128))
            throw new ManagedMutationRejection("Select distinct comic issues of this run with their exact labels.");
        KapowarrRun run;
        try {
            run = await ReadRunOrMissingAsync(scope.Item, token);
        } catch (KapowarrRecordNotFound) {
            throw new ManagedMutationRejection("This comic run is no longer in Kapowarr. Refresh the connected library.");
        }
        return (run, scope.Targets.Select(target => (target, run.RequireIssue(target))).ToArray());
    }

    private static ManagedControlCapabilities Capabilities(KapowarrRun run,
        IReadOnlyList<(ManagedControlTarget Target, KapowarrIssue Issue)> targets) {
        if (targets.Count != 1) return new(false, false, false, "Select one exact issue to change monitoring or search.");
        var issue = targets[0].Issue;
        // Turning monitoring off is always exact. Turning it on may also need the run monitored.
        var monitoringBlocker = run.Monitors(issue) ? null : run.RunMonitoringBlocker(issue);
        return new(run.SearchBlocker(issue) is null, monitoringBlocker is null, false, monitoringBlocker);
    }

    private static void RequirePath(KapowarrRun run, string expectedPath) {
        if (run.Snapshot.Path != expectedPath)
            throw new ManagedMutationRejection("The run's folder changed in Kapowarr since review. Refresh the connected library.");
    }
    #endregion
}
