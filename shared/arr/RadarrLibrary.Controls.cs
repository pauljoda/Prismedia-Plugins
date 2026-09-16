using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

internal sealed partial class RadarrLibrary {
    protected override IReadOnlyList<string> ControlOperations => [ManagerControls.Reconcile, ManagerControls.Configure, ManagerControls.Request];

    protected override async Task<object> DispatchControlAsync(IntegrationRequest request, CancellationToken token) => request.Operation switch {
        ManagerControls.Reconcile => await ReconcileAsync(Input<ReconcileManagedInput>(request), token),
        ManagerControls.Configure => await ConfigureAsync(Input<ConfigureManagedInput>(request), token),
        ManagerControls.Request => await RequestAsync(Input<RequestManagedInput>(request), token),
        _ => throw new IntegrationFailure("This operation is not implemented by the installed adapter.")
    };

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
        return new(Summary(movie), movie.Path, [new(input.Scope.Targets[0], movie.Monitored)], new(true, true, true), command);
    }

    private async Task<ManagedMutationResult> ConfigureAsync(ConfigureManagedInput input, CancellationToken token) {
        Movie movie;
        try {
            if (input.OperationId == Guid.Empty || input.Changes is null || input.Changes is { ProfileId: null, Monitored: null })
                throw new IntegrationFailure("Select explicit configuration changes and a durable operation ID.");
            movie = await RequireMovieAsync(input.Scope, token);
            RequirePath(movie, input.ExpectedPath);
            if (MatchesChanges(movie, input.Changes)) return new(ManagerControls.Applied);
            if (input.Changes.ProfileId is not null) {
                if (Id(movie.QualityProfileId) != input.ExpectedProfileId) throw new IntegrationFailure("The movie's profile changed since review. Refresh its settings.");
                var desired = ParseId(input.Changes.ProfileId);
                var profiles = await Client.GetAsync<ArrProfile[]>("qualityprofile", token);
                if (!profiles.Any(profile => profile.Id == desired)) throw new IntegrationFailure("The selected external profile no longer exists.");
            }
            if (input.Changes.Monitored is not null && (input.ExpectedMonitoring is null || input.ExpectedMonitoring.Count != 1
                || !input.ExpectedMonitoring.TryGetValue(Id(movie.Id), out var expected) || expected != movie.Monitored))
                throw new IntegrationFailure("The movie's monitoring changed since review. Refresh its settings.");
        } catch (IntegrationFailure error) { return Rejected(error.Message); }

        // The editor endpoint changes only supplied fields. No paths, tags, availability settings,
        // file moves or deletions are included. A response failure after this point is uncertain.
        try {
            var updated = await Client.WriteAsync<MovieEditAcknowledgement[]>(HttpMethod.Put, "movie/editor", new MovieEdit([movie.Id], input.Changes.Monitored,
                input.Changes.ProfileId is null ? null : ParseId(input.Changes.ProfileId)), token);
            if (updated.Length != 1 || updated[0].Id != movie.Id)
                throw new IntegrationFailure("The configuration response did not confirm the selected movie. Reconcile its state before another change.");
        } catch (ArrRequestRejectedException error) { return Rejected(error.Message); }
        var observed = await RequireMovieAsync(input.Scope, token);
        RequirePath(observed, input.ExpectedPath);
        if (!MatchesChanges(observed, input.Changes)) throw new IntegrationFailure("The submitted configuration could not be confirmed. Reconcile it before another change.");
        return new(ManagerControls.Applied);
    }

    private async Task<ManagedMutationResult> RequestAsync(RequestManagedInput input, CancellationToken token) {
        Movie movie;
        try {
            if (input.OperationId == Guid.Empty) throw new IntegrationFailure("A durable operation ID is required.");
            movie = await RequireMovieAsync(input.Scope, token);
            RequirePath(movie, input.ExpectedPath);
            if (Id(movie.QualityProfileId) != input.ExpectedProfileId) throw new IntegrationFailure("The movie's profile changed since review. Refresh it before searching.");
        } catch (IntegrationFailure error) { return Rejected(error.Message); }
        ArrCommand command;
        try {
            command = await Client.WriteAsync<ArrCommand>(HttpMethod.Post, "command", new MoviesSearch(ArrCommands.MoviesSearch, [movie.Id]), token);
        } catch (ArrRequestRejectedException error) { return Rejected(error.Message); }
        if (!MatchesCommand(command, movie.Id)) throw new IntegrationFailure("The returned command did not confirm the submitted work and scope. Reconcile the application before searching again.");
        return new(ManagerControls.Accepted, MapCommand(command));
    }

    private async Task<Movie> RequireMovieAsync(ManagedControlScope scope, CancellationToken token) {
        if (scope?.Item is null || scope.Item.EntityKind != ManagerProtocol.Movie || scope.Targets is not { Count: 1 }
            || scope.Item.ExpectedExternalIds is not { Count: > 0 and <= 64 }
            || !scope.Item.ExpectedExternalIds.ContainsKey(ManagerProtocol.Tmdb))
            throw new IntegrationFailure("Select one movie with its known TMDB identity.");
        var id = ParseId(scope.Item.RemoteId);
        if (Id(id) != scope.Item.RemoteId || scope.Targets[0] is not { EntityKind: ManagerProtocol.Movie, SeasonNumber: null, EpisodeNumber: null, AbsoluteNumber: null } target
            || target.RemoteId != scope.Item.RemoteId) throw new IntegrationFailure("The selected scope must contain exactly this movie.");
        var movie = await Client.GetAsync<Movie>($"movie/{id}", token);
        if (movie.Id != id || scope.Item.ExpectedExternalIds.Any(pair => Summary(movie).ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
            throw new IntegrationFailure("The remote movie now has different metadata identities. Refresh it before using manager controls.");
        if (string.IsNullOrWhiteSpace(movie.Path) || movie.Path.Length > 8192) throw new IntegrationFailure("The remote movie did not supply a valid library path.");
        return movie;
    }

    private static bool MatchesChanges(Movie movie, ManagedConfigurationChange changes) =>
        (changes.ProfileId is null || Id(movie.QualityProfileId) == changes.ProfileId)
        && (changes.Monitored is null || movie.Monitored == changes.Monitored.Value);
    private static void RequirePath(Movie movie, string expectedPath) {
        if (string.IsNullOrWhiteSpace(expectedPath) || movie.Path != expectedPath)
            throw new IntegrationFailure("The movie's managed path changed since review. Refresh its library association.");
    }
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
    private static ManagedMutationResult Rejected(string problem) => new(ManagerControls.Rejected, Problem: problem);

    // Typed records are the single external API v3 encode/decode boundary.
    private sealed record MovieEdit(int[] MovieIds, bool? Monitored, int? QualityProfileId);
    // Editor responses omit read-only fields such as hasFile. Confirm identities here, then read the complete movie again.
    private sealed record MovieEditAcknowledgement(int Id);
    private sealed record MoviesSearch(string Name, int[] MovieIds);
    private sealed record ArrCommand(int Id, string Name, string Status, string Result, DateTimeOffset Queued, MoviesSearch? Body);
}

/// <summary>External command names and states owned by the supported API v3 versions.</summary>
internal static class ArrCommands {
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
}
