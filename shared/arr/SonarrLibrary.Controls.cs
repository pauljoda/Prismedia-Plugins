using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

internal sealed partial class SonarrLibrary {
    #region Variables
    protected override IReadOnlyList<string> ControlOperations => [ManagerDiscovery.Search, ManagerControls.Reconcile, ManagerControls.Configure, ManagerControls.Request,
        ManagerCreation.Lookup, ManagerCreation.Ensure, ManagerRelease.Inspect];
    #endregion

    #region Actions - Dispatch
    protected override async Task<object> DispatchControlAsync(IntegrationRequest request, CancellationToken token) => request.Operation switch {
        ManagerDiscovery.Search => await DiscoverAsync(Input<ManagedDiscoveryQuery>(request), token),
        ManagerControls.Reconcile => await ReconcileAsync(Input<ReconcileManagedInput>(request), token),
        ManagerControls.Configure => await ConfigureAsync(Input<ConfigureManagedInput>(request), token),
        ManagerControls.Request => await RequestAsync(Input<RequestManagedInput>(request), token),
        ManagerCreation.Lookup => await LookupAsync(Input<ManagedLookupInput>(request), token),
        ManagerCreation.Ensure => await EnsureAsync(Input<EnsureManagedInput>(request), token),
        ManagerRelease.Inspect => await ArrReleaseInspector.InspectAsync(Client,
            ct => ObserveReleaseScopeAsync(Input<InspectManagedReleaseInput>(request).Scope, ct), true, token),
        _ => throw new IntegrationFailure("This operation is not implemented by the installed adapter.")
    };
    #endregion

    #region Actions - Controls
    private async Task<ManagedControlState> ReconcileAsync(ReconcileManagedInput input, CancellationToken token) {
        var (series, episodes) = await RequireScopeAsync(input.Scope, token);
        ManagedCommandSnapshot? command = null;
        if (input.Command is { } reference) {
            var observed = await Client.GetOptionalAsync<EpisodeCommand>($"command/{ParseId(reference.Id)}", token);
            command = observed is not null && Id(observed.Id) == reference.Id && observed.Queued == reference.QueuedAt
                && MatchesCommand(observed, episodes) ? MapCommand(observed)
                : new(reference, ManagerControls.Unknown, "The original command is absent, its identity changed, or it no longer matches the selected episodes.");
        }
        return ControlState(input.Scope, series, episodes, command);
    }

    /// <summary>
    /// Changes only the selected episodes' monitoring flags, then confirms them through a complete
    /// re-read. It never changes the series' profile or parent monitoring.
    /// </summary>
    private Task<ManagedMutationResult> ConfigureAsync(ConfigureManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureAsync(async () => {
            if (input.OperationId == Guid.Empty || input.Changes is not { ProfileId: null, Monitored: { } desired })
                throw new ManagedMutationRejection("Choose an episode monitoring change. A series-wide profile cannot be changed through a finite episode scope.");
            var (series, episodes) = await RequireScopeAsync(input.Scope, token);
            RequireConfiguration(series, input.ExpectedPath, input.ExpectedProfileId);
            if (episodes.All(episode => episode.Monitored == desired)) return new(ManagerControls.Applied);
            if (!series.Monitored)
                throw new ManagedMutationRejection("Series monitoring is disabled. This scope does not authorize changing its parent monitoring setting.");
            if (input.ExpectedMonitoring is null || input.ExpectedMonitoring.Count != episodes.Length
                || episodes.Any(episode => !input.ExpectedMonitoring.TryGetValue(Id(episode.Id), out var expected) || expected != episode.Monitored))
                throw new ManagedMutationRejection("Episode monitoring changed since review. Refresh the selected episodes.");
            var ids = episodes.Select(episode => episode.Id).Order().ToArray();
            var accepted = await Client.WriteAsync<EpisodeAcknowledgement[]>(HttpMethod.Put, "episode/monitor", new EpisodeMonitor(ids, desired), token);
            if (accepted.Length != ids.Length || !accepted.Select(episode => episode.Id).Order().SequenceEqual(ids))
                throw new IntegrationFailure("The monitoring reply did not confirm the exact selected episodes. Reconcile before another change.");
            return await ManagedMutationRejection.AfterWriteAsync(async () => {
                var (after, updated) = await RequireScopeAsync(input.Scope, token);
                RequireConfiguration(after, input.ExpectedPath, input.ExpectedProfileId);
                if (updated.Any(episode => episode.Monitored != desired))
                    throw new IntegrationFailure("The requested episode flags could not be confirmed. Reconcile their state before another change.");
                return new ManagedMutationResult(ManagerControls.Applied);
            }, "Sonarr accepted the episode monitoring change, but it could not be confirmed.");
        });

    /// <summary>
    /// Queues one search for exactly the selected episodes without changing any monitoring or profile.
    /// </summary>
    private Task<ManagedMutationResult> RequestAsync(RequestManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureAsync(async () => {
            if (input.OperationId == Guid.Empty) throw new ManagedMutationRejection("A durable operation ID is required.");
            var (series, episodes) = await RequireScopeAsync(input.Scope, token);
            RequireConfiguration(series, input.ExpectedPath, input.ExpectedProfileId);
            var command = await Client.WriteAsync<EpisodeCommand>(HttpMethod.Post, "command",
                new EpisodeSearch(ArrCommands.EpisodeSearch, episodes.Select(episode => episode.Id).Order().ToArray()), token);
            if (!MatchesCommand(command, episodes))
                throw new IntegrationFailure("The accepted command did not confirm the exact selected episodes. Inspect the application before another search.");
            return new(ManagerControls.Accepted, MapCommand(command));
        });

    /// <summary>Reads the series and episodes a control scope pins and requires them to still match that scope.</summary>
    /// <exception cref="ManagedMutationRejection">The scope is invalid, or the series or an episode no longer matches it.</exception>
    /// <exception cref="IntegrationFailure">The series or its episodes could not be read or were inconsistent.</exception>
    private async Task<(Series Series, Episode[] Episodes)> RequireScopeAsync(ManagedControlScope scope, CancellationToken token) {
        var id = RequireScopeIdentity(scope);
        var series = await Client.GetAsync<Series>($"series/{id}", token);
        return await RequireScopeMatchAsync(scope, id, series, token);
    }

    private async Task<ArrReleaseScopeObservation> ObserveReleaseScopeAsync(
        ManagedControlScope scope,
        CancellationToken token) {
        var id = RequireScopeIdentity(scope);
        var series = await Client.GetOptionalAsync<Series>($"series/{id}", token);
        if (series is null) {
            await ConfirmHoldingAbsentAsync(scope, ManagerProtocol.Tvdb, token);
            return ArrReleaseScopeObservation.Absent();
        }
        var matched = await RequireScopeMatchAsync(scope, id, series, token);
        return ArrReleaseScopeObservation.Present(ControlState(scope, matched.Series, matched.Episodes));
    }

    /// <summary>Requires a control scope to pin one TVDB series and unique, exactly numbered episodes.</summary>
    /// <exception cref="ManagedMutationRejection">The scope is not a finite, canonical episode selection.</exception>
    private static int RequireScopeIdentity(ManagedControlScope scope) {
        if (scope?.Item is null || scope.Item.EntityKind != ManagerProtocol.Series || scope.Targets is not { Count: > 0 and <= 10000 }
            || scope.Item.ExpectedExternalIds is not { Count: > 0 and <= 64 } || !scope.Item.ExpectedExternalIds.ContainsKey(ManagerProtocol.Tvdb))
            throw new ManagedMutationRejection("Select a pinned TVDB series and explicit episode targets.");
        var id = ParseSelectedId(scope.Item.RemoteId);
        var tvdbId = ParseSelectedId(scope.Item.ExpectedExternalIds[ManagerProtocol.Tvdb]);
        if (Id(id) != scope.Item.RemoteId || scope.Targets.Any(target => target is null || target.EntityKind != ManagerProtocol.Episode
            || target.SeasonNumber is null or < 0 || target.EpisodeNumber is null or < 0 || Id(ParseSelectedId(target.RemoteId)) != target.RemoteId)
            || scope.Targets.Select(target => target.RemoteId).Distinct(StringComparer.Ordinal).Count() != scope.Targets.Count
            || Id(tvdbId) != scope.Item.ExpectedExternalIds[ManagerProtocol.Tvdb])
            throw new ManagedMutationRejection("The scope must contain unique canonical episode IDs and exact numbering.");
        return id;
    }

    /// <summary>
    /// Requires the read series to keep the scope's pinned identity and a complete configuration, then
    /// resolves each selected episode by its exact coordinates.
    /// </summary>
    /// <exception cref="ManagedMutationRejection">The series' identity or configuration, or an episode's identity or coordinates, changed.</exception>
    /// <exception cref="IntegrationFailure">The episodes could not be read or were inconsistent.</exception>
    private async Task<(Series Series, Episode[] Episodes)> RequireScopeMatchAsync(
        ManagedControlScope scope,
        int id,
        Series series,
        CancellationToken token) {
        if (series.Id != id || series.QualityProfileId <= 0 || string.IsNullOrWhiteSpace(series.Path) || series.Path.Length > 8192
            || scope.Item.ExpectedExternalIds.Any(pair => Summary(series).ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
            throw new ManagedMutationRejection("The remote series no longer has the pinned metadata identity or a complete configuration.");
        var episodes = await Client.GetAsync<Episode[]>($"episode?seriesId={id}", token);
        if (episodes.Length > 100000 || episodes.Any(episode => episode.Id <= 0 || episode.SeriesId != id)
            || episodes.Select(episode => episode.Id).Distinct().Count() != episodes.Length)
            throw new IntegrationFailure("The series returned inconsistent episode identities.");
        var byId = episodes.ToDictionary(episode => Id(episode.Id), StringComparer.Ordinal);
        // Absolute numbering is compared only when both the scope and Sonarr carry one: Sonarr omits it for
        // most non-anime series, and a scope pinned from another source may not know it.
        if (scope.Targets.Any(target => !byId.TryGetValue(target.RemoteId, out var episode) || target.SeasonNumber != episode.SeasonNumber
            || target.EpisodeNumber != episode.EpisodeNumber
            || (target.AbsoluteNumber is { } expectedAbsolute && episode.AbsoluteEpisodeNumber is { } actualAbsolute && expectedAbsolute != actualAbsolute)))
            throw new ManagedMutationRejection("An episode identity or coordinate changed. Review its saved association before changing this scope.");
        return (series, scope.Targets.Select(target => byId[target.RemoteId]).ToArray());
    }

    private ManagedControlState ControlState(
        ManagedControlScope scope,
        Series series,
        Episode[] episodes,
        ManagedCommandSnapshot? command = null) {
        var byId = episodes.ToDictionary(episode => Id(episode.Id), StringComparer.Ordinal);
        return new(Summary(series), series.Path,
            scope.Targets.Select(target => new ManagedTargetMonitoring(target, byId[target.RemoteId].Monitored)).ToArray(),
            new(true, series.Monitored, false, series.Monitored ? null
                : "Series monitoring is disabled in Sonarr. Enable it there before changing episode monitoring here; Prismedia will not change that series-wide setting."),
            command);
    }
    private static void RequireConfiguration(Series series, string path, string? profile) {
        if (string.IsNullOrWhiteSpace(path) || series.Path != path || Id(series.QualityProfileId) != profile)
            throw new ManagedMutationRejection("The series folder or profile changed since review. Refresh before continuing.");
    }
    #endregion

    #region Actions - Commands
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
    #endregion

    // Exact API v3 encode/decode boundaries. No series editor or parent monitoring requests exist here.
    private sealed record EpisodeMonitor(int[] EpisodeIds, bool Monitored);
    private sealed record EpisodeAcknowledgement(int Id);
    private sealed record EpisodeSearch(string Name, int[] EpisodeIds);
    private sealed record EpisodeCommand(int Id, string Name, string Status, string Result, DateTimeOffset Queued, EpisodeSearch? Body);
}
