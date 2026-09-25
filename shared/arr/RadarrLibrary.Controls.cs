using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

internal sealed partial class RadarrLibrary {
    #region Variables
    protected override IReadOnlyList<string> ControlOperations => [ManagerDiscovery.Search, ManagerControls.Reconcile, ManagerControls.Configure, ManagerControls.Request, ManagerCreation.Lookup, ManagerCreation.Ensure, ManagerRelease.Inspect];
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
            ct => ObserveReleaseScopeAsync(Input<InspectManagedReleaseInput>(request).Scope, ct), false, token),
        _ => throw new IntegrationFailure("This operation is not implemented by the installed adapter.")
    };
    #endregion

    #region Actions - Controls
    private async Task<ManagedControlState> ReconcileAsync(ReconcileManagedInput input, CancellationToken token) {
        var movie = await RequireMovieAsync(input.Scope, token);
        ManagedCommandSnapshot? command = null;
        if (input.Command is { } reference) {
            var observed = await Client.GetOptionalAsync<ArrCommand>($"command/{ParseId(reference.Id)}", token);
            command = observed is null ? Unknown(reference, "The connected application no longer retains this command.")
                : observed.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) != reference.Id || observed.Queued != reference.QueuedAt
                    ? Unknown(reference, "The connected application reused this command identifier.")
                    : MatchesCommand(observed, movie.Id) ? MapCommand(observed)
                    : Unknown(reference, "This command no longer belongs to the selected work and scope.");
        }
        return ControlState(input.Scope, movie, command);
    }

    /// <summary>
    /// Changes only the reviewed profile and monitoring fields of one movie, then confirms them through a
    /// complete re-read. An intent that already holds is applied without a write.
    /// </summary>
    private Task<ManagedMutationResult> ConfigureAsync(ConfigureManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureAsync(async () => {
            if (input.OperationId == Guid.Empty || input.Changes is null || input.Changes is { ProfileId: null, Monitored: null })
                throw new ManagedMutationRejection("Select explicit configuration changes and a durable operation ID.");
            var movie = await RequireMovieAsync(input.Scope, token);
            RequirePath(movie, input.ExpectedPath);
            if (MatchesChanges(movie, input.Changes)) return new(ManagerControls.Applied);
            int? desiredProfile = null;
            if (input.Changes.ProfileId is not null) {
                if (Id(movie.QualityProfileId) != input.ExpectedProfileId)
                    throw new ManagedMutationRejection("The movie's profile changed since review. Refresh its settings.");
                desiredProfile = ParseSelectedId(input.Changes.ProfileId);
                var profiles = await Client.GetAsync<ArrProfile[]>("qualityprofile", token);
                if (!profiles.Any(profile => profile.Id == desiredProfile))
                    throw new ManagedMutationRejection("The selected external profile no longer exists.");
            }
            if (input.Changes.Monitored is not null && (input.ExpectedMonitoring is null || input.ExpectedMonitoring.Count != 1
                || !input.ExpectedMonitoring.TryGetValue(Id(movie.Id), out var expected) || expected != movie.Monitored))
                throw new ManagedMutationRejection("The movie's monitoring changed since review. Refresh its settings.");
            // The editor endpoint changes only supplied fields. No paths, tags, availability settings,
            // file moves or deletions are included. A response failure after this point is uncertain.
            var updated = await Client.WriteAsync<MovieEditAcknowledgement[]>(HttpMethod.Put, "movie/editor",
                new MovieEdit([movie.Id], input.Changes.Monitored, desiredProfile), token);
            if (updated.Length != 1 || updated[0].Id != movie.Id)
                throw new IntegrationFailure("The configuration response did not confirm the selected movie. Reconcile its state before another change.");
            return await ManagedMutationRejection.AfterWriteAsync(async () => {
                var observed = await RequireMovieAsync(input.Scope, token);
                RequirePath(observed, input.ExpectedPath);
                if (!MatchesChanges(observed, input.Changes))
                    throw new IntegrationFailure("The submitted configuration could not be confirmed. Reconcile it before another change.");
                return new ManagedMutationResult(ManagerControls.Applied);
            }, "Radarr accepted the movie's configuration change, but it could not be confirmed.");
        });

    /// <summary>
    /// Queues one search for exactly the reviewed movie without changing its monitoring or profile.
    /// </summary>
    private Task<ManagedMutationResult> RequestAsync(RequestManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureAsync(async () => {
            if (input.OperationId == Guid.Empty) throw new ManagedMutationRejection("A durable operation ID is required.");
            var movie = await RequireMovieAsync(input.Scope, token);
            RequirePath(movie, input.ExpectedPath);
            if (Id(movie.QualityProfileId) != input.ExpectedProfileId)
                throw new ManagedMutationRejection("The movie's profile changed since review. Refresh it before searching.");
            var command = await Client.WriteAsync<ArrCommand>(HttpMethod.Post, "command",
                new MoviesSearch(ArrCommands.MoviesSearch, [movie.Id]), token);
            if (!MatchesCommand(command, movie.Id))
                throw new IntegrationFailure("The returned command did not confirm the submitted work and scope. Reconcile the application before searching again.");
            return new(ManagerControls.Accepted, MapCommand(command));
        });

    /// <summary>Reads the one movie a control scope pins and requires it to still match that scope.</summary>
    /// <exception cref="ManagedMutationRejection">The scope is not exactly one pinned movie, or the movie's identity or path no longer matches it.</exception>
    /// <exception cref="IntegrationFailure">The movie could not be read.</exception>
    private async Task<Movie> RequireMovieAsync(ManagedControlScope scope, CancellationToken token) {
        var id = RequireMovieScope(scope);
        var movie = await Client.GetAsync<Movie>($"movie/{id}", token);
        return RequireMovieMatch(scope, id, movie);
    }

    private async Task<ArrReleaseScopeObservation> ObserveReleaseScopeAsync(
        ManagedControlScope scope,
        CancellationToken token) {
        var id = RequireMovieScope(scope);
        var movie = await Client.GetOptionalAsync<Movie>($"movie/{id}", token);
        if (movie is null) {
            await ConfirmHoldingAbsentAsync(scope, ManagerProtocol.Tmdb, token);
            return ArrReleaseScopeObservation.Absent();
        }
        return ArrReleaseScopeObservation.Present(ControlState(scope, RequireMovieMatch(scope, id, movie)));
    }

    /// <summary>Requires a control scope to pin exactly one movie by its canonical manager and TMDB IDs.</summary>
    /// <exception cref="ManagedMutationRejection">The scope names anything other than exactly this movie.</exception>
    private static int RequireMovieScope(ManagedControlScope scope) {
        if (scope?.Item is null || scope.Item.EntityKind != ManagerProtocol.Movie || scope.Targets is not { Count: 1 }
            || scope.Item.ExpectedExternalIds is not { Count: > 0 and <= 64 }
            || !scope.Item.ExpectedExternalIds.ContainsKey(ManagerProtocol.Tmdb))
            throw new ManagedMutationRejection("Select one movie with its known TMDB identity.");
        var id = ParseSelectedId(scope.Item.RemoteId);
        var tmdbId = ParseSelectedId(scope.Item.ExpectedExternalIds[ManagerProtocol.Tmdb]);
        if (Id(id) != scope.Item.RemoteId || scope.Targets[0] is not { EntityKind: ManagerProtocol.Movie, SeasonNumber: null, EpisodeNumber: null, AbsoluteNumber: null } target
            || target.RemoteId != scope.Item.RemoteId || Id(tmdbId) != scope.Item.ExpectedExternalIds[ManagerProtocol.Tmdb])
            throw new ManagedMutationRejection("The selected scope must contain exactly this movie.");
        return id;
    }

    /// <summary>Requires the read movie to keep the scope's pinned identities and a usable library path.</summary>
    /// <exception cref="ManagedMutationRejection">The movie's identity changed or it has no valid library path.</exception>
    private Movie RequireMovieMatch(ManagedControlScope scope, int id, Movie movie) {
        if (movie.Id != id || scope.Item.ExpectedExternalIds.Any(pair => Summary(movie).ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
            throw new ManagedMutationRejection("The remote movie now has different metadata identities. Refresh it before using manager controls.");
        if (string.IsNullOrWhiteSpace(movie.Path) || movie.Path.Length > 8192)
            throw new ManagedMutationRejection("The remote movie did not supply a valid library path.");
        return movie;
    }

    private ManagedControlState ControlState(
        ManagedControlScope scope,
        Movie movie,
        ManagedCommandSnapshot? command = null) =>
        new(Summary(movie), movie.Path, [new(scope.Targets[0], movie.Monitored)], new(true, true, true), command);

    private static bool MatchesChanges(Movie movie, ManagedConfigurationChange changes) =>
        (changes.ProfileId is null || Id(movie.QualityProfileId) == changes.ProfileId)
        && (changes.Monitored is null || movie.Monitored == changes.Monitored.Value);
    private static void RequirePath(Movie movie, string expectedPath) {
        if (string.IsNullOrWhiteSpace(expectedPath) || movie.Path != expectedPath)
            throw new ManagedMutationRejection("The movie's managed path changed since review. Refresh its library association.");
    }
    #endregion

    #region Actions - Commands
    private static bool MatchesCommand(ArrCommand command, int movieId) => command.Id > 0 && command.Queued.Year >= 1970
        && command.Name == ArrCommands.MoviesSearch && command.Body?.Name == ArrCommands.MoviesSearch
        && command.Body.MovieIds is { Length: 1 } && command.Body.MovieIds[0] == movieId;
    private static ManagedCommandSnapshot MapCommand(ArrCommand command) {
        var reference = new ManagedCommandReference(Id(command.Id), command.Queued);
        return command.Status switch {
            ArrCommands.Queued => new(reference, ManagerControls.Pending),
            ArrCommands.Started => new(reference, ManagerControls.Running),
            ArrCommands.Completed when command.Result == ArrCommands.Successful => new(reference, ManagerControls.Completed),
            ArrCommands.Completed when command.Result == ArrCommands.Unsuccessful => new(reference, ManagerControls.Failed, "The connected search completed unsuccessfully."),
            ArrCommands.Failed => new(reference, ManagerControls.Failed, "The connected search failed. Inspect its history in the connected application."),
            ArrCommands.Aborted or ArrCommands.Cancelled => new(reference, ManagerControls.Cancelled),
            _ => Unknown(reference, "The connected application cannot establish the outcome of this command.")
        };
    }
    private static ManagedCommandSnapshot Unknown(ManagedCommandReference reference, string problem) => new(reference, ManagerControls.Unknown, problem);
    #endregion

    // Typed records are the single external API v3 encode/decode boundary.
    private sealed record MovieEdit(int[] MovieIds, bool? Monitored, int? QualityProfileId);
    // Editor responses omit read-only fields such as hasFile. Confirm identities here, then read the complete movie again.
    private sealed record MovieEditAcknowledgement(int Id);
    private sealed record MoviesSearch(string Name, int[] MovieIds);
    private sealed record ArrCommand(int Id, string Name, string Status, string Result, DateTimeOffset Queued, MoviesSearch? Body);
}

/// <summary>External command names and states owned by the supported API v3 versions.</summary>
internal static class ArrCommands {
    #region Static Variables
    internal const string MoviesSearch = "MoviesSearch";
    internal const string EpisodeSearch = "EpisodeSearch";
    internal const string Queued = "queued";
    internal const string Started = "started";
    internal const string Completed = "completed";
    internal const string Failed = "failed";
    internal const string Aborted = "aborted";
    internal const string Cancelled = "cancelled";
    internal const string Successful = "successful";
    internal const string Unsuccessful = "unsuccessful";
    internal const string Unknown = "unknown";
    internal const string Orphaned = "orphaned";
    #endregion
}
