using System.Text.Json.Serialization;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

/// <summary>Conservative, read-only drain evidence; unknown work anywhere in the application blocks release.</summary>
internal static class ArrReleaseInspector {
    #region Actions - Inspection
    internal static async Task<ManagedReleaseObservation> InspectAsync(ArrClient client,
        Func<CancellationToken, Task<ArrReleaseScopeObservation>> readScope, bool television, CancellationToken token) {
        var before = await readScope(token);
        ValidateScopeObservation(before);
        await RequireDownloadVisibilityAsync(client, token);
        var queuePath = television ? "queue?page=1&pageSize=100&includeUnknownSeriesItems=true"
            : "queue?page=1&pageSize=100&includeUnknownMovieItems=true";
        var emptyBefore = await QueueEmptyAsync(client, queuePath, token);
        var commands = await client.GetAsync<ActivityCommand[]>("command", token);
        if (commands.Length > 10000 || commands.Any(command => command is null || command.Id <= 0 || string.IsNullOrWhiteSpace(command.Status))
            || commands.Select(command => command.Id).Distinct().Count() != commands.Length)
            throw new IntegrationFailure("The manager returned incomplete or inconsistent command activity.");
        var idle = commands.All(command => command.Status is ArrCommands.Completed or ArrCommands.Failed or ArrCommands.Aborted or ArrCommands.Cancelled);
        var emptyAfter = await QueueEmptyAsync(client, queuePath, token);
        await RequireDownloadVisibilityAsync(client, token);
        var after = await readScope(token);
        ValidateScopeObservation(after);
        if (before.RemoteItemAbsent != after.RemoteItemAbsent
            || before.State is { } beforeState && after.State is { } afterState
                && (beforeState.Path != afterState.Path || beforeState.Item.ProfileId != afterState.Item.ProfileId
                    || beforeState.Item.Monitored != afterState.Item.Monitored
                    || !beforeState.Targets.SequenceEqual(afterState.Targets)))
            throw new IntegrationFailure("The managed scope changed during release inspection. Review its settings again.");
        return new(after.State, emptyBefore && emptyAfter, idle, after.RemoteItemAbsent);
    }

    private static void ValidateScopeObservation(ArrReleaseScopeObservation observation) {
        if (observation.RemoteItemAbsent != (observation.State is null))
            throw new IntegrationFailure("The manager returned contradictory release scope evidence.");
    }

    private static async Task RequireDownloadVisibilityAsync(ArrClient client, CancellationToken token) {
        var issues = await client.GetAsync<ActivityHealth[]>("health", token);
        if (issues.Length > 10000 || issues.Any(issue => issue is null || string.IsNullOrWhiteSpace(issue.Source)))
            throw new IntegrationFailure("The manager returned incomplete health evidence.");
        if (issues.Any(issue => issue.Source is ArrHealth.DownloadClientCheck or ArrHealth.DownloadClientStatusCheck))
            throw new IntegrationFailure("The manager reports a download client problem. Restore its connection and health before reviewing ownership release.");
    }

    private static async Task<bool> QueueEmptyAsync(ArrClient client, string path, CancellationToken token) {
        var page = await client.GetAsync<ActivityQueuePage>(path, token);
        if (page.Page != 1 || page.PageSize != 100 || page.TotalRecords is < 0 or > 100000 || page.Records is null
            || page.Records.Length != Math.Min(page.TotalRecords, 100) || page.Records.Any(record => record is null || record.Id <= 0)
            || page.Records.Select(record => record.Id).Distinct().Count() != page.Records.Length)
            throw new IntegrationFailure("The manager returned incomplete or inconsistent download activity.");
        return page.TotalRecords == 0;
    }
    #endregion

    // Single API v3 decode boundaries. Required members distinguish empty activity from missing evidence.
    private sealed record ActivityQueuePage([property: JsonRequired] int Page, [property: JsonRequired] int PageSize,
        [property: JsonRequired] int TotalRecords, [property: JsonRequired] ActivityQueueItem[] Records);
    private sealed record ActivityQueueItem([property: JsonRequired] int Id);
    private sealed record ActivityCommand([property: JsonRequired] int Id, [property: JsonRequired] string Status);
    private sealed record ActivityHealth([property: JsonRequired] string Source);
}

/// <summary>One exact holding observation, either current state or independently confirmed absence.</summary>
internal sealed record ArrReleaseScopeObservation(ManagedControlState? State, bool RemoteItemAbsent) {
    #region Constructors
    internal static ArrReleaseScopeObservation Present(ManagedControlState state) => new(state, false);
    internal static ArrReleaseScopeObservation Absent() => new(null, true);
    #endregion
}
