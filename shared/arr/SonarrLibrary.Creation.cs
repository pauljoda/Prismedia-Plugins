using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

internal sealed partial class SonarrLibrary {
    #region Actions - Creation
    /// <summary>
    /// Resolves one exact TVDB or TMDB series, and optionally its exact episodes, without mutating Sonarr:
    /// the existing holding for that identity, or otherwise the metadata lookup's candidate.
    /// </summary>
    /// <exception cref="ManagedMutationRejection">
    /// The identity or episode selection is not exact, several holdings share it, or the manager does not
    /// resolve the series or each episode exactly once.
    /// </exception>
    private async Task<ManagedLookupResult> LookupAsync(ManagedLookupInput input, CancellationToken token) {
        var identity = RequireCreationIdentity(input);
        ValidateTargets(input.Targets, required: false);

        Series? candidate = null;
        if (identity.TvdbId is { } requestedTvdb) {
            var existing = await FindExistingAsync(requestedTvdb, token);
            if (existing is not null) return await ExistingResultAsync(existing, input, token);
        }

        var term = identity.TvdbId is { } tvdb ? $"tvdb:{tvdb}" : $"tmdb:{identity.TmdbId!.Value}";
        var results = await Client.GetAsync<Series[]>($"series/lookup?term={Uri.EscapeDataString(term)}", token);
        if (results.Length > 1000) throw new IntegrationFailure("The manager returned excessive series lookup results.");
        var matches = results.Where(result => MatchesIdentity(result, input)).ToArray();
        if (matches.Length != 1) throw new ManagedMutationRejection("The manager did not resolve the exact TVDB/TMDB series identity uniquely.");
        candidate = matches[0];
        var confirmed = Candidate(candidate, input);

        var holding = await FindExistingAsync(candidate.TvdbId, token);
        return holding is null
            ? new(confirmed, null)
            : await ExistingResultAsync(holding, input, token);
    }

    /// <summary>
    /// Adds one reviewed series unmonitored, without a search, under the reviewed root and profile, then
    /// confirms it and resolves the selected episodes through complete re-reads. An existing holding is
    /// returned unchanged. A lost add response stays uncertain so the host resolves the exact identity
    /// instead of adding again.
    /// </summary>
    private Task<EnsureManagedResult> EnsureAsync(EnsureManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureCreationAsync(async () => {
            if (input.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(input.ExpectedRootPath) || input.ExpectedRootPath.Length > 8192)
                throw new ManagedMutationRejection("Choose reviewed creation settings and a durable operation ID.");
            RequireCreationIdentity(input.Work);
            ValidateTargets(input.Work.Targets, required: true);
            var profile = ParseSelectedId(input.ProfileId);
            var rootId = ParseSelectedId(input.RootId);
            var lookup = await LookupAsync(input.Work, token);
            if (lookup.Existing is { } existing)
                return new(ManagerControls.Applied, existing, Targets: lookup.Targets);

            var profiles = await Client.GetAsync<ArrProfile[]>("qualityprofile", token);
            if (profiles.Count(item => item.Id == profile) != 1)
                throw new ManagedMutationRejection("The selected profile no longer exists uniquely.");
            var roots = await Client.GetAsync<ArrRoot[]>("rootfolder", token);
            var matches = roots.Where(item => item.Id == rootId).ToArray();
            if (matches.Length != 1 || matches[0].Accessible == false || matches[0].Path != input.ExpectedRootPath)
                throw new ManagedMutationRejection("The selected root changed or is not accessible. Refresh the creation settings.");
            var root = matches[0];

            var tvdb = ParseId(lookup.Candidate.ExternalIds[ManagerProtocol.Tvdb]);
            var acknowledgement = await Client.WriteAsync<SeriesCreationAcknowledgement>(HttpMethod.Post, "series",
                new AddSeries(lookup.Candidate.Title, tvdb, profile, root.Path, false, true,
                    new(ArrCreation.Unmonitored, false, false)), token);
            // Any failure after the POST is uncertain. Recovery must use exact lookup, which adopts
            // an existing series and waits for its asynchronous episode catalog without adding again.
            return await ManagedMutationRejection.AfterWriteAsync(() => ConfirmAddedAsync(input, acknowledgement, root, token),
                "Sonarr accepted the series, but it could not be confirmed.");
        });

    private async Task<EnsureManagedResult> ConfirmAddedAsync(EnsureManagedInput input, SeriesCreationAcknowledgement added,
        ArrRoot root, CancellationToken token) {
        var observed = await GetAsync(ParseId(Id(added.Id)), token);
        Candidate(observed.Item.Title, observed.Item.Year, observed.Item.ExternalIds, input.Work);
        if (observed.Item.Monitored || observed.Item.ProfileId != input.ProfileId
            || !RemoteLibraryPath.TryParse(root.Path, out var remoteRoot) || !remoteRoot.IsAncestorOf(observed.Path))
            throw new IntegrationFailure("The added series settings or root could not be confirmed. Reconcile it before any acquisition action.");
        var targets = await ResolveTargetsAsync(added.Id, input.Work.Targets, token);
        return new(ManagerControls.Applied, observed, true, Targets: targets);
    }

    private async Task<ManagedLookupResult> ExistingResultAsync(Series series, ManagedLookupInput input, CancellationToken token) {
        var snapshot = await GetAsync(ParseId(Id(series.Id)), token);
        var candidate = Candidate(series, input);
        var targets = await ResolveTargetsAsync(series.Id, input.Targets, token);
        return new(candidate, snapshot, targets);
    }

    private async Task<Series?> FindExistingAsync(int tvdbId, CancellationToken token) {
        var existing = await Client.GetAsync<Series[]>($"series?tvdbId={tvdbId}", token);
        if (existing.Length > 1 || existing.Any(series => series.TvdbId != tvdbId))
            throw new ManagedMutationRejection("The manager returned ambiguous holdings for this TVDB identity.");
        return existing.SingleOrDefault();
    }

    private async Task<IReadOnlyList<ManagedResolvedTarget>?> ResolveTargetsAsync(
        int seriesId, IReadOnlyList<ManagedLookupTarget>? requested, CancellationToken token) {
        if (requested is not { Count: > 0 }) return null;
        var episodes = await Client.GetAsync<Episode[]>($"episode?seriesId={seriesId}", token);
        if (episodes.Length > 100000 || episodes.Any(episode => episode.Id <= 0 || episode.SeriesId != seriesId
                || episode.TvdbId < 0 || episode.SeasonNumber < 0 || episode.EpisodeNumber < 0
                || episode.AbsoluteEpisodeNumber is < 0)
            || episodes.Select(episode => episode.Id).Distinct().Count() != episodes.Length)
            throw new IntegrationFailure("The series returned inconsistent episode identities.");

        var resolved = new List<ManagedResolvedTarget>(requested.Count);
        var used = new HashSet<int>();
        foreach (var target in requested) {
            var matches = episodes.Where(episode => MatchesTarget(episode, target)).ToArray();
            if (matches.Length != 1 || !used.Add(matches[0].Id))
                throw new ManagedMutationRejection("The manager did not resolve every selected episode exactly once.");
            var episode = matches[0];
            IReadOnlyDictionary<string, string> externalIds = episode.TvdbId > 0
                ? new Dictionary<string, string> { [ManagerProtocol.Tvdb] = Id(episode.TvdbId) }
                : new Dictionary<string, string>();
            resolved.Add(new(Id(episode.Id), ManagerProtocol.Episode, externalIds,
                episode.SeasonNumber, episode.EpisodeNumber, episode.AbsoluteEpisodeNumber));
        }
        return resolved;
    }

    private static CreationIdentity RequireCreationIdentity(ManagedLookupInput input) {
        if (input?.EntityKind != ManagerProtocol.Series || input.ExternalIds is not { Count: > 0 and <= 3 }
            || input.ExternalIds.Keys.Any(key => key is not (ManagerProtocol.Tvdb or ManagerProtocol.Tmdb or ManagerProtocol.Imdb))
            || input.ExternalIds.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            || !input.ExternalIds.ContainsKey(ManagerProtocol.Tvdb) && !input.ExternalIds.ContainsKey(ManagerProtocol.Tmdb))
            throw new ManagedMutationRejection("Select an exact TVDB or TMDB series identity and, optionally, its matching identities.");
        var tvdb = ParseOptionalIdentity(input.ExternalIds, ManagerProtocol.Tvdb);
        var tmdb = ParseOptionalIdentity(input.ExternalIds, ManagerProtocol.Tmdb);
        return new(tvdb, tmdb);
    }

    private static int? ParseOptionalIdentity(IReadOnlyDictionary<string, string> identities, string key) {
        if (!identities.TryGetValue(key, out var value)) return null;
        var parsed = ParseSelectedId(value);
        if (Id(parsed) != value) throw new ManagedMutationRejection("Use canonical numeric TVDB and TMDB identities.");
        return parsed;
    }

    private static void ValidateTargets(IReadOnlyList<ManagedLookupTarget>? targets, bool required) {
        if (targets is null or { Count: 0 }) {
            if (required) throw new ManagedMutationRejection("Select a finite nonempty episode scope.");
            return;
        }
        if (targets.Count > 10000) throw new ManagedMutationRejection("Select a finite nonempty episode scope.");
        foreach (var target in targets) {
            if (target is null || target.EntityKind != ManagerProtocol.Episode || target.ExternalIds is null or { Count: > 1 }
                || target.ExternalIds.Keys.Any(key => key != ManagerProtocol.Tvdb)
                || target.SeasonNumber is null or < 0 || target.EpisodeNumber is null or < 0 || target.AbsoluteNumber is < 0)
                throw new ManagedMutationRejection("Each selected target must be one exact Sonarr episode with canonical coordinates.");
            ParseOptionalIdentity(target.ExternalIds, ManagerProtocol.Tvdb);
        }
    }

    private static bool MatchesIdentity(Series series, ManagedLookupInput input) {
        if (series.TvdbId <= 0) return false;
        var identities = SeriesIdentities(series);
        return input.ExternalIds.All(pair => identities.GetValueOrDefault(pair.Key) == pair.Value);
    }

    private static bool MatchesTarget(Episode episode, ManagedLookupTarget target) {
        if (episode.SeasonNumber != target.SeasonNumber || episode.EpisodeNumber != target.EpisodeNumber) return false;
        if (target.AbsoluteNumber is { } expectedAbsolute && episode.AbsoluteEpisodeNumber is { } actualAbsolute
            && actualAbsolute != expectedAbsolute) return false;
        return !target.ExternalIds.TryGetValue(ManagerProtocol.Tvdb, out var expectedTvdb)
            || episode.TvdbId > 0 && Id(episode.TvdbId) == expectedTvdb;
    }

    private static ManagedCandidate Candidate(Series series, ManagedLookupInput expected) {
        var candidate = Candidate(series.Title, series.Year, SeriesIdentities(series), expected);
        return candidate with { Metadata = DiscoveryMetadata(series) };
    }
    private static ManagedCandidate Candidate(string title, int? year, IReadOnlyDictionary<string, string> ids, ManagedLookupInput expected) {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 512 || year is < 0 or > 9999
            || expected.ExternalIds.Any(pair => ids.GetValueOrDefault(pair.Key) != pair.Value))
            throw new ManagedMutationRejection("The manager returned a different or incomplete metadata identity.");
        return new(ManagerProtocol.Series, title, year, ids);
    }
    #endregion

    private sealed record CreationIdentity(int? TvdbId, int? TmdbId);
    private sealed record SeriesCreationAcknowledgement(int Id);
    private sealed record AddSeries(string Title, int TvdbId, int QualityProfileId, string RootFolderPath,
        bool Monitored, bool SeasonFolder, AddSeriesOptions AddOptions);
    private sealed record AddSeriesOptions(string Monitor, bool SearchForMissingEpisodes, bool SearchForCutoffUnmetEpisodes);
}
