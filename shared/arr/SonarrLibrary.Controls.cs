using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

internal sealed partial class SonarrLibrary {
    protected override IReadOnlyList<string> ControlOperations => [ManagerControls.Reconcile, ManagerControls.Configure, ManagerControls.Request, ManagerRelease.Inspect];
    protected override async Task<object> DispatchControlAsync(IntegrationRequest request, CancellationToken token) => request.Operation switch {
        ManagerControls.Reconcile => await ReconcileAsync(Input<ReconcileManagedInput>(request), token),
        ManagerControls.Configure => await ConfigureAsync(Input<ConfigureManagedInput>(request), token),
        ManagerControls.Request => await RequestAsync(Input<RequestManagedInput>(request), token),
        ManagerRelease.Inspect => await ArrReleaseInspector.InspectAsync(Client,
            ct => ReconcileAsync(new(Input<InspectManagedReleaseInput>(request).Scope), ct), true, token),
        _ => throw new IntegrationFailure("This operation is not implemented by the installed adapter.")
    };

    private async Task<ManagedControlState> ReconcileAsync(ReconcileManagedInput input, CancellationToken token) {
        var (series, episodes) = await RequireScopeAsync(input.Scope, token);
        ManagedCommandSnapshot? command = null;
        if (input.Command is { } reference) {
            var observed = await Client.GetOptionalAsync<EpisodeCommand>($"command/{ParseId(reference.Id)}", token);
            command = observed is not null && Id(observed.Id) == reference.Id && observed.Queued == reference.QueuedAt
                && MatchesCommand(observed, episodes) ? MapCommand(observed)
                : new(reference, ManagerControls.Unknown, "The original command is absent, its identity changed, or it no longer matches the selected episodes.");
        }
        var byId = episodes.ToDictionary(episode => Id(episode.Id), StringComparer.Ordinal);
        return new(Summary(series), series.Path, input.Scope.Targets.Select(target => new ManagedTargetMonitoring(target, byId[target.RemoteId].Monitored)).ToArray(),
            new(true, series.Monitored, false, series.Monitored ? null
                : "Series monitoring is disabled in Sonarr. Enable it there before changing episode monitoring here; Prismedia will not change that series-wide setting."), command);
    }

    private async Task<ManagedMutationResult> ConfigureAsync(ConfigureManagedInput input, CancellationToken token) {
        Series series; Episode[] episodes;
        try {
            if (input.OperationId == Guid.Empty || input.Changes is not { ProfileId: null, Monitored: not null })
                throw new IntegrationFailure("Choose an episode monitoring change. A series-wide profile cannot be changed through a finite episode scope.");
            (series, episodes) = await RequireScopeAsync(input.Scope, token);
            RequireConfiguration(series, input.ExpectedPath, input.ExpectedProfileId);
            if (episodes.All(episode => episode.Monitored == input.Changes.Monitored)) return new(ManagerControls.Applied);
            if (!series.Monitored) throw new IntegrationFailure("Series monitoring is disabled. This scope does not authorize changing its parent monitoring setting.");
            if (input.ExpectedMonitoring is null || input.ExpectedMonitoring.Count != episodes.Length
                || episodes.Any(episode => !input.ExpectedMonitoring.TryGetValue(Id(episode.Id), out var expected) || expected != episode.Monitored))
                throw new IntegrationFailure("Episode monitoring changed since review. Refresh the selected episodes.");
        } catch (IntegrationFailure error) { return new(ManagerControls.Rejected, Problem: error.Message); }

        var ids = episodes.Select(episode => episode.Id).Order().ToArray();
        try {
            var accepted = await Client.WriteAsync<EpisodeAcknowledgement[]>(HttpMethod.Put, "episode/monitor", new EpisodeMonitor(ids, input.Changes.Monitored!.Value), token);
            if (accepted.Length != ids.Length || !accepted.Select(episode => episode.Id).Order().SequenceEqual(ids))
                throw new IntegrationFailure("The monitoring reply did not confirm the exact selected episodes. Reconcile before another change.");
        } catch (ArrRequestRejectedException error) { return new(ManagerControls.Rejected, Problem: error.Message); }
        var (after, updated) = await RequireScopeAsync(input.Scope, token);
        RequireConfiguration(after, input.ExpectedPath, input.ExpectedProfileId);
        if (updated.Any(episode => episode.Monitored != input.Changes.Monitored))
            throw new IntegrationFailure("The requested episode flags could not be confirmed. Reconcile their state before another change.");
        return new(ManagerControls.Applied);
    }

    private async Task<ManagedMutationResult> RequestAsync(RequestManagedInput input, CancellationToken token) {
        Episode[] episodes;
        try {
            if (input.OperationId == Guid.Empty) throw new IntegrationFailure("A durable operation ID is required.");
            var scope = await RequireScopeAsync(input.Scope, token); episodes = scope.Episodes;
            RequireConfiguration(scope.Series, input.ExpectedPath, input.ExpectedProfileId);
        } catch (IntegrationFailure error) { return new(ManagerControls.Rejected, Problem: error.Message); }
        EpisodeCommand command;
        try {
            command = await Client.WriteAsync<EpisodeCommand>(HttpMethod.Post, "command",
                new EpisodeSearch(ArrCommands.EpisodeSearch, episodes.Select(episode => episode.Id).Order().ToArray()), token);
        } catch (ArrRequestRejectedException error) { return new(ManagerControls.Rejected, Problem: error.Message); }
        if (!MatchesCommand(command, episodes)) throw new IntegrationFailure("The accepted command did not confirm the exact selected episodes. Inspect the application before another search.");
        return new(ManagerControls.Accepted, MapCommand(command));
    }

    private async Task<(Series Series, Episode[] Episodes)> RequireScopeAsync(ManagedControlScope scope, CancellationToken token) {
        if (scope?.Item is null || scope.Item.EntityKind != ManagerProtocol.Series || scope.Targets is not { Count: > 0 and <= 10000 }
            || scope.Item.ExpectedExternalIds is not { Count: > 0 and <= 64 } || !scope.Item.ExpectedExternalIds.ContainsKey(ManagerProtocol.Tvdb))
            throw new IntegrationFailure("Select a pinned TVDB series and explicit episode targets.");
        var id = ParseId(scope.Item.RemoteId);
        if (Id(id) != scope.Item.RemoteId || scope.Targets.Any(target => target is null || target.EntityKind != ManagerProtocol.Episode
            || target.SeasonNumber is null or < 0 || target.EpisodeNumber is null or < 0 || Id(ParseId(target.RemoteId)) != target.RemoteId)
            || scope.Targets.Select(target => target.RemoteId).Distinct(StringComparer.Ordinal).Count() != scope.Targets.Count)
            throw new IntegrationFailure("The scope must contain unique canonical episode IDs and exact numbering.");
        var series = await Client.GetAsync<Series>($"series/{id}", token);
        if (series.Id != id || series.QualityProfileId <= 0 || string.IsNullOrWhiteSpace(series.Path) || series.Path.Length > 8192
            || scope.Item.ExpectedExternalIds.Any(pair => Summary(series).ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
            throw new IntegrationFailure("The remote series no longer has the pinned metadata identity or a complete configuration.");
        var episodes = await Client.GetAsync<Episode[]>($"episode?seriesId={id}", token);
        if (episodes.Length > 100000 || episodes.Any(episode => episode.Id <= 0 || episode.SeriesId != id)
            || episodes.Select(episode => episode.Id).Distinct().Count() != episodes.Length)
            throw new IntegrationFailure("The series returned inconsistent episode identities.");
        var byId = episodes.ToDictionary(episode => Id(episode.Id), StringComparer.Ordinal);
        if (scope.Targets.Any(target => !byId.TryGetValue(target.RemoteId, out var episode) || target.SeasonNumber != episode.SeasonNumber
            || target.EpisodeNumber != episode.EpisodeNumber || target.AbsoluteNumber != episode.AbsoluteEpisodeNumber))
            throw new IntegrationFailure("An episode identity or coordinate changed. Review its saved association before changing this scope.");
        return (series, scope.Targets.Select(target => byId[target.RemoteId]).ToArray());
    }
    private static void RequireConfiguration(Series series, string path, string profile) {
        if (string.IsNullOrWhiteSpace(path) || series.Path != path || Id(series.QualityProfileId) != profile)
            throw new IntegrationFailure("The series folder or profile changed since review. Refresh before continuing.");
    }
    private static bool MatchesCommand(EpisodeCommand command, Episode[] episodes) => command.Id > 0 && command.Queued.Year >= 1970
        && command.Name == ArrCommands.EpisodeSearch && command.Body?.Name == ArrCommands.EpisodeSearch
        && command.Body.EpisodeIds is { } ids && ids.Order().SequenceEqual(episodes.Select(episode => episode.Id).Order());
    private static ManagedCommandSnapshot MapCommand(EpisodeCommand command) {
        var reference = new ManagedCommandReference(Id(command.Id), command.Queued);
        return command.Status switch {
            ArrCommands.Queued => new(reference, ManagerControls.Pending),
            ArrCommands.Started => new(reference, ManagerControls.Running),
            ArrCommands.Completed when command.Result == ArrCommands.Successful => new(reference, ManagerControls.Completed),
            ArrCommands.Completed when command.Result == ArrCommands.Unsuccessful => new(reference, ManagerControls.Failed),
            ArrCommands.Failed => new(reference, ManagerControls.Failed),
            ArrCommands.Aborted or ArrCommands.Cancelled => new(reference, ManagerControls.Cancelled),
            _ => new(reference, ManagerControls.Unknown, "The manager cannot establish this command's outcome.")
        };
    }
    // Exact API v3 encode/decode boundaries. No series editor or parent monitoring requests exist here.
    private sealed record EpisodeMonitor(int[] EpisodeIds, bool Monitored);
    private sealed record EpisodeAcknowledgement(int Id);
    private sealed record EpisodeSearch(string Name, int[] EpisodeIds);
    private sealed record EpisodeCommand(int Id, string Name, string Status, string Result, DateTimeOffset Queued, EpisodeSearch? Body);
}
