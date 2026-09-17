using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

internal sealed partial class RadarrLibrary {
    private async Task<ManagedLookupResult> LookupAsync(ManagedLookupInput input, CancellationToken token) {
        var tmdb = RequireCreationIdentity(input);
        var existing = await Client.GetAsync<Movie[]>($"movie?tmdbId={tmdb}", token);
        if (existing.Length > 1) throw new IntegrationFailure("The manager returned ambiguous holdings for this identity.");
        if (existing.Length == 1) {
            var snapshot = await GetAsync(ParseId(Id(existing[0].Id)), token);
            var candidate = Candidate(snapshot.Item.Title, snapshot.Item.Year, snapshot.Item.ExternalIds, input);
            return new(candidate, snapshot);
        }
        var found = await Client.GetAsync<MovieLookup>($"movie/lookup/tmdb?tmdbId={tmdb}", token);
        return new(Candidate(found.Title, found.Year, Identities(ManagerProtocol.Tmdb, found.TmdbId, found.ImdbId), input), null);
    }

    private async Task<EnsureManagedResult> EnsureAsync(EnsureManagedInput input, CancellationToken token) {
        int tmdb, profile;
        ArrRoot root;
        try {
            if (input.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(input.ExpectedRootPath) || input.ExpectedRootPath.Length > 8192)
                throw new IntegrationFailure("Choose reviewed creation settings and a durable operation ID.");
            tmdb = RequireCreationIdentity(input.Work);
            profile = ParseId(input.ProfileId);
            var rootId = ParseId(input.RootId);
            var lookup = await LookupAsync(input.Work, token);
            // Existing holdings are evidence, not permission to overwrite their settings or move files.
            if (lookup.Existing is { } existing) return new(ManagerControls.Applied, existing);
            var profiles = await Client.GetAsync<ArrProfile[]>("qualityprofile", token);
            if (profiles.Count(item => item.Id == profile) != 1) throw new IntegrationFailure("The selected profile no longer exists uniquely.");
            var roots = await Client.GetAsync<ArrRoot[]>("rootfolder", token);
            var matches = roots.Where(item => item.Id == rootId).ToArray();
            if (matches.Length != 1 || matches[0].Accessible == false || matches[0].Path != input.ExpectedRootPath)
                throw new IntegrationFailure("The selected root changed or is not accessible. Refresh the creation settings.");
            root = matches[0];
        } catch (IntegrationFailure error) { return new(ManagerControls.Rejected, Problem: error.Message); }

        MovieCreationAcknowledgement acknowledgement;
        try {
            acknowledgement = await Client.WriteAsync<MovieCreationAcknowledgement>(HttpMethod.Post, "movie",
                new AddMovie(tmdb, profile, root.Path, false, ArrCreation.Released, new(false, ArrCreation.Unmonitored)), token);
        } catch (ArrRequestRejectedException error) { return new(ManagerControls.Rejected, Problem: error.Message); }
        // Any failure from this point is uncertain. The host must look up the exact identity, never
        // retry a timed-out POST on the assumption that no movie was added.
        var observed = await GetAsync(ParseId(Id(acknowledgement.Id)), token);
        Candidate(observed.Item.Title, observed.Item.Year, observed.Item.ExternalIds, input.Work);
        if (observed.Item.Monitored || observed.Item.ProfileId != input.ProfileId || !InsideRemoteRoot(observed.Path, root.Path))
            throw new IntegrationFailure("The added movie's settings or root could not be confirmed. Reconcile it before any acquisition action.");
        return new(ManagerControls.Applied, observed, true);
    }

    private static int RequireCreationIdentity(ManagedLookupInput input) {
        if (input?.EntityKind != ManagerProtocol.Movie || input.ExternalIds is not { Count: > 0 and <= 2 }
            || !input.ExternalIds.TryGetValue(ManagerProtocol.Tmdb, out var tmdb)
            || input.ExternalIds.Keys.Any(key => key is not (ManagerProtocol.Tmdb or ManagerProtocol.Imdb))
            || input.ExternalIds.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)))
            throw new IntegrationFailure("Select an exact TMDB movie identity and, optionally, its matching IMDb identity.");
        var id = ParseId(tmdb);
        if (Id(id) != tmdb) throw new IntegrationFailure("Use the canonical numeric TMDB movie identity.");
        return id;
    }
    private static ManagedCandidate Candidate(string title, int? year, IReadOnlyDictionary<string, string> ids, ManagedLookupInput expected) {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 512 || year is < 0 or > 9999
            || expected.ExternalIds.Any(pair => ids.GetValueOrDefault(pair.Key) != pair.Value))
            throw new IntegrationFailure("The manager returned a different or incomplete metadata identity.");
        return new(ManagerProtocol.Movie, title, year, ids);
    }
    private static bool InsideRemoteRoot(string path, string root) {
        // This is the remote path namespace. Local mapping and canonical byte checks remain host-owned.
        var prefix = root.TrimEnd('/', '\\');
        return path.StartsWith(prefix + "/", StringComparison.Ordinal) || path.StartsWith(prefix + "\\", StringComparison.Ordinal);
    }
    private sealed record MovieLookup(string Title, int Year, int TmdbId, string? ImdbId);
    private sealed record MovieCreationAcknowledgement(int Id);
    private sealed record AddMovie(int TmdbId, int QualityProfileId, string RootFolderPath, bool Monitored,
        string MinimumAvailability, AddMovieOptions AddOptions);
    private sealed record AddMovieOptions(bool SearchForMovie, string Monitor);
}

/// <summary>External Radarr creation vocabulary at its single protocol boundary.</summary>
internal static class ArrCreation {
    internal const string Unmonitored = "none";
    internal const string Released = "released";
}
