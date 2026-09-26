using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

internal sealed partial class LazyLibrarianLibrary {
    #region Actions - Manager
    private ManagerOptions Options(ManagerOptionsInput input) {
        var rendition = LazyLibrarianRendition.Require(input.EntityKind, input.BookRendition);
        return new([], [new(rendition.Code, Root(rendition), null)]);
    }

    /// <summary>
    /// Resolves an Open Library work already in LazyLibrarian's catalog. LazyLibrarian's name search
    /// cannot prove an exact work ID, so a work absent from the catalog is reported as not found
    /// there, never as a removed holding.
    /// </summary>
    private async Task<ManagedLookupResult> LookupAsync(ManagedLookupInput input, CancellationToken token) {
        var rendition = LazyLibrarianRendition.Require(input.EntityKind, input.BookRendition);
        if (input.Targets is { Count: > 0 } || input.ExternalIds is not { Count: 1 }
            || !input.ExternalIds.TryGetValue(LazyLibrarianBookIdentity.OpenLibraryWork.Namespace, out var workId)
            || !LazyLibrarianBookIdentity.OpenLibraryWork.Recognizes(workId))
            throw new IntegrationFailure("Select one exact Open Library work, such as OL450063W, for a LazyLibrarian Book.");
        var row = await books.FindAsync(workId, token)
            ?? throw new IntegrationFailure($"Open Library work {workId} is not in LazyLibrarian's catalog. Add it in LazyLibrarian first; Prismedia does not add books there.");
        var snapshot = await SnapshotAsync(row, rendition, input.ExternalIds, token);
        return new(new(MediaKinds.Book, snapshot.Item.Title, snapshot.Item.Year, snapshot.Item.ExternalIds), snapshot);
    }

    /// <summary>
    /// Observes one rendition's monitoring and what LazyLibrarian would do with each control. A command
    /// reference is always reported Unknown: LazyLibrarian keeps no durable result for one search.
    /// </summary>
    private async Task<ManagedControlState> ReconcileAsync(ReconcileManagedInput input, CancellationToken token) {
        var (row, rendition, item, path) = await ReadScopeAsync(input.Scope, token);
        var status = rendition.StatusOf(row);
        var command = input.Command is { } reference
            ? new ManagedCommandSnapshot(reference, ManagerControls.Unknown,
                "LazyLibrarian does not expose a durable result for this exact search.")
            : null;
        var monitoringBlocker = MonitoringBlocker(status, rendition, !status.IsMonitored);
        var searchBlocker = SearchBlocker(status, rendition);
        return new(item, path, [], new(searchBlocker is null, monitoringBlocker is null, false, monitoringBlocker), command);
    }

    /// <summary>
    /// Turns one rendition's monitoring on (<c>queueBook</c>, Wanted) or off (<c>unqueueBook</c>,
    /// Skipped), then confirms it with a targeted re-read. It never queues a rendition LazyLibrarian
    /// already has.
    /// </summary>
    private Task<ManagedMutationResult> ConfigureAsync(ConfigureManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureAsync(async () => {
            if (input.OperationId == Guid.Empty || input.Changes is null
                || input.Changes.ProfileId is not null || input.Changes.Monitored is not { } desired
                || input.ExpectedProfileId is not null || input.ExpectedMonitoring is not { Count: 0 })
                throw new ManagedMutationRejection("Choose whether to monitor this Book rendition. LazyLibrarian books have no profiles or per-target monitoring.");
            var (row, rendition, _, path) = await ReadScopeAsync(input.Scope, token);
            RequirePath(path, input.ExpectedPath);
            var status = rendition.StatusOf(row);
            if (status.IsMonitored == desired) return new(ManagerControls.Applied);
            if (MonitoringBlocker(status, rendition, desired) is { } blocker) throw new ManagedMutationRejection(blocker);
            await client.AcknowledgeAsync(LazyLibrarianCommand.Monitoring(desired), row.BookID!, rendition, token);
            return await ManagedMutationRejection.AfterWriteAsync(async () => {
                var confirmed = await books.FindAsync(row.BookID!, token);
                if (confirmed is null || rendition.StatusOf(confirmed).IsMonitored != desired)
                    throw new IntegrationFailure($"LazyLibrarian did not confirm this {rendition.Noun}'s monitoring state. Review the book before another change.");
                return new ManagedMutationResult(ManagerControls.Applied);
            }, $"LazyLibrarian accepted the {rendition.Noun} monitoring change, but it could not be confirmed.");
        });

    /// <summary>
    /// Starts LazyLibrarian's background search for one Wanted rendition. It refuses when LazyLibrarian
    /// would not search: the rendition is not Wanted, is already snatched, or is already owned.
    /// </summary>
    private Task<ManagedMutationResult> RequestAsync(RequestManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureAsync(async () => {
            if (input.OperationId == Guid.Empty || input.ExpectedProfileId is not null)
                throw new ManagedMutationRejection("Search one Book rendition with a durable operation ID. LazyLibrarian books have no profiles.");
            var (row, rendition, _, path) = await ReadScopeAsync(input.Scope, token);
            RequirePath(path, input.ExpectedPath);
            if (SearchBlocker(rendition.StatusOf(row), rendition) is { } blocker) throw new ManagedMutationRejection(blocker);
            await client.AcknowledgeAsync(LazyLibrarianCommand.SearchBook, row.BookID!, rendition, token);
            // The API's OK starts background work; it does not prove a release was found.
            // Keep a host correlation ID, then report Unknown on later observation.
            var command = new ManagedCommandSnapshot(new(input.OperationId.ToString("N"), DateTimeOffset.UtcNow), ManagerControls.Pending);
            return new(ManagerControls.Accepted, command);
        });

    private async Task<(LazyLibrarianBookRow Row, LazyLibrarianRendition Rendition, ManagedLibraryItem Item, string Path)> ReadScopeAsync(
        ManagedControlScope? scope, CancellationToken token) {
        if (scope?.Item is null || scope.Targets is not { Count: 0 })
            throw new ManagedMutationRejection("Select one Book rendition without child targets.");
        var rendition = LazyLibrarianRendition.Require(scope.Item.EntityKind, scope.Item.BookRendition);
        var row = await books.FindAsync(scope.Item.RemoteId, token)
            ?? throw new ManagedMutationRejection("This book is no longer in LazyLibrarian's catalog. Refresh the connected library.");
        var (item, path) = Holding(row, rendition, scope.Item.ExpectedExternalIds);
        return (row, rendition, item, path);
    }

    /// <summary>Explains why a monitoring change is unavailable, or returns null when it would be exact.</summary>
    private string? MonitoringBlocker(LazyLibrarianBookStatus status, LazyLibrarianRendition rendition, bool monitored) =>
        Help.WriteBlocker(LazyLibrarianCommand.Monitoring(monitored), rendition) ?? (monitored ? status.QueueBlocker(rendition) : null);

    /// <summary>Explains why a search is unavailable or would not run, or returns null when it would.</summary>
    private string? SearchBlocker(LazyLibrarianBookStatus status, LazyLibrarianRendition rendition) =>
        Help.WriteBlocker(LazyLibrarianCommand.SearchBook, rendition) ?? status.SearchBlocker(rendition);

    private static void RequirePath(string path, string expectedPath) {
        if (path != expectedPath)
            throw new ManagedMutationRejection("This Book rendition's library root changed since review. Refresh the connected library.");
    }
    #endregion
}
