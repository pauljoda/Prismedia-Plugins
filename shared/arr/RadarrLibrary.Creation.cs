using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

internal sealed partial class RadarrLibrary {
    /// <summary>
    /// Resolves one exact TMDB movie without mutating Radarr: the existing holding for that identity, or
    /// otherwise the metadata lookup's candidate.
    /// </summary>
    /// <exception cref="ManagedMutationRejection">
    /// The identity is not exact, several holdings share it, or the manager answers with another work.
    /// </exception>
    private async Task<ManagedLookupResult> LookupAsync(ManagedLookupInput input, CancellationToken token) {
        var tmdb = RequireCreationIdentity(input);
        var existing = await Client.GetAsync<Movie[]>($"movie?tmdbId={tmdb}", token);
        if (existing.Length > 1) throw new ManagedMutationRejection("The manager returned ambiguous holdings for this identity.");
        if (existing.Length == 1) {
            var snapshot = await GetAsync(ParseId(Id(existing[0].Id)), token);
            var credits = await CreditsAsync(existing[0].Id, token);
            return new(Candidate(existing[0], input, credits), snapshot);
        }
        var found = await Client.GetAsync<MovieLookup>($"movie/lookup/tmdb?tmdbId={tmdb}", token);
        var candidate = Candidate(found, input);
        return new(candidate, null);
    }

    /// <summary>
    /// Adds one reviewed movie unmonitored, without a search, under the reviewed root and profile, then
    /// confirms it through a complete re-read. An existing holding is returned unchanged. A lost add
    /// response stays uncertain so the host resolves the exact identity instead of adding again.
    /// </summary>
    private Task<EnsureManagedResult> EnsureAsync(EnsureManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureCreationAsync(async () => {
            if (input.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(input.ExpectedRootPath) || input.ExpectedRootPath.Length > 8192)
                throw new ManagedMutationRejection("Choose reviewed creation settings and a durable operation ID.");
            var tmdb = RequireCreationIdentity(input.Work);
            var profile = ParseSelectedId(input.ProfileId);
            var rootId = ParseSelectedId(input.RootId);
            var lookup = await LookupAsync(input.Work, token);
            // Existing holdings are evidence, not permission to overwrite their settings or move files.
            if (lookup.Existing is { } existing) return new(ManagerControls.Applied, existing);
            var profiles = await Client.GetAsync<ArrProfile[]>("qualityprofile", token);
            if (profiles.Count(item => item.Id == profile) != 1)
                throw new ManagedMutationRejection("The selected profile no longer exists uniquely.");
            var roots = await Client.GetAsync<ArrRoot[]>("rootfolder", token);
            var matches = roots.Where(item => item.Id == rootId).ToArray();
            if (matches.Length != 1 || matches[0].Accessible == false || matches[0].Path != input.ExpectedRootPath)
                throw new ManagedMutationRejection("The selected root changed or is not accessible. Refresh the creation settings.");
            var root = matches[0];
            var acknowledgement = await Client.WriteAsync<MovieCreationAcknowledgement>(HttpMethod.Post, "movie",
                new AddMovie(tmdb, profile, root.Path, false, ArrCreation.Released, new(false, ArrCreation.Unmonitored)), token);
            // Any failure from this point is uncertain. The host must look up the exact identity, never
            // retry a timed-out POST on the assumption that no movie was added.
            return await ManagedMutationRejection.AfterWriteAsync(() => ConfirmAddedAsync(input, acknowledgement, root, token),
                "Radarr accepted the movie, but it could not be confirmed.");
        });

    private async Task<EnsureManagedResult> ConfirmAddedAsync(EnsureManagedInput input, MovieCreationAcknowledgement added,
        ArrRoot root, CancellationToken token) {
        var observed = await GetAsync(ParseId(Id(added.Id)), token);
        if (input.Work.ExternalIds.Any(pair => observed.Item.ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
            throw new IntegrationFailure("The manager returned a different or incomplete metadata identity.");
        if (observed.Item.Monitored || observed.Item.ProfileId != input.ProfileId || !InsideRemoteRoot(observed.Path, root.Path))
            throw new IntegrationFailure("The added movie's settings or root could not be confirmed. Reconcile it before any acquisition action.");
        return new(ManagerControls.Applied, observed, true);
    }

    private static int RequireCreationIdentity(ManagedLookupInput input) {
        if (input?.EntityKind != ManagerProtocol.Movie || input.ExternalIds is not { Count: > 0 and <= 2 }
            || !input.ExternalIds.TryGetValue(ManagerProtocol.Tmdb, out var tmdb)
            || input.ExternalIds.Keys.Any(key => key is not (ManagerProtocol.Tmdb or ManagerProtocol.Imdb))
            || input.ExternalIds.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)))
            throw new ManagedMutationRejection("Select an exact TMDB movie identity and, optionally, its matching IMDb identity.");
        var id = ParseSelectedId(tmdb);
        if (Id(id) != tmdb) throw new ManagedMutationRejection("Use the canonical numeric TMDB movie identity.");
        return id;
    }
    private static ManagedCandidate Candidate(MovieLookup movie, ManagedLookupInput expected) {
        var ids = Identities(ManagerProtocol.Tmdb, movie.TmdbId, movie.ImdbId);
        var title = movie.Title;
        var year = movie.Year;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 512 || year is < 0 or > 9999
            || expected.ExternalIds.Any(pair => ids.GetValueOrDefault(pair.Key) != pair.Value))
            throw new ManagedMutationRejection("The manager returned a different or incomplete metadata identity.");
        return new(ManagerProtocol.Movie, title, year, ids, DiscoveryMetadata(movie));
    }
    private static ManagedCandidate Candidate(
        Movie movie,
        ManagedLookupInput expected,
        IReadOnlyList<ManagedPersonCredit>? credits = null) {
        var ids = Identities(ManagerProtocol.Tmdb, movie.TmdbId, movie.ImdbId);
        if (string.IsNullOrWhiteSpace(movie.Title) || movie.Title.Length > 512 || movie.Year is < 0 or > 9999
            || expected.ExternalIds.Any(pair => ids.GetValueOrDefault(pair.Key) != pair.Value))
            throw new ManagedMutationRejection("The manager returned a different or incomplete metadata identity.");
        return new(ManagerProtocol.Movie, movie.Title, movie.Year, ids, DiscoveryMetadata(movie, credits));
    }
    private static bool InsideRemoteRoot(string path, string root) {
        // This is the remote path namespace. Local mapping and canonical byte checks remain host-owned.
        var prefix = root.TrimEnd('/', '\\');
        return path.StartsWith(prefix + "/", StringComparison.Ordinal) || path.StartsWith(prefix + "\\", StringComparison.Ordinal);
    }
    private sealed record MovieLookup(string Title, int Year, int TmdbId, string? ImdbId,
        string? OriginalTitle = null, string? Overview = null, string? Studio = null,
        string? Certification = null, int? Runtime = null, string[]? Genres = null,
        ArrImage?[]? Images = null, string? Website = null, string? InCinemas = null,
        string? DigitalRelease = null, string? PhysicalRelease = null, MovieRatings? Ratings = null);
    private sealed record MovieRatings(MovieRating? Tmdb = null);
    private sealed record MovieRating(decimal? Value = null);
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
