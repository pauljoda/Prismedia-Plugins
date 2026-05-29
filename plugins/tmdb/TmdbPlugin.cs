namespace Prismedia.Plugin.Tmdb;

internal sealed class TmdbPlugin {
    private readonly TmdbApiClient _client;
    private readonly TmdbProposalMapper _mapper;
    private readonly IReadOnlyDictionary<string, Func<IdentifyPluginRequest, Task<IdentifyPluginResult>>> _handlers;

    public TmdbPlugin(TmdbApiClient client) {
        _client = client;
        _mapper = new TmdbProposalMapper(client);
        _handlers = new Dictionary<string, Func<IdentifyPluginRequest, Task<IdentifyPluginResult>>>(StringComparer.OrdinalIgnoreCase) {
            ["video-series"] = IdentifySeriesAsync,
            ["video-season"] = IdentifySeasonAsync,
            ["video"] = IdentifyVideoAsync,
            ["video-episode"] = IdentifyVideoAsync,
            ["person"] = IdentifyPersonAsync,
            ["studio"] = IdentifyStudioAsync
        };
    }

    public async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) =>
        _handlers.TryGetValue(request.Entity.Kind, out var handler)
            ? await handler(request)
            : IdentifyPluginResult.None();

    private async Task<IdentifyPluginResult> IdentifySeriesAsync(IdentifyPluginRequest request) {
        var title = request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        if (ResolveTmdbReference(request, "tv") is { } reference) {
            return Proposal(await _mapper.TvToProposalAsync(await _client.GetTvAsync(reference.Id), reference.MatchReason));
        }

        return IdentifyPluginResult.ForCandidates(await SearchSeriesCandidatesAsync(title));
    }

    private async Task<IdentifyPluginResult> IdentifyVideoAsync(IdentifyPluginRequest request) {
        if (TmdbMetadataHelpers.TryEpisodeContext(request, out var seriesId, out var seasonNumber, out var episodeNumber)) {
            return Proposal(await EpisodeFromContextAsync(seriesId, seasonNumber, episodeNumber));
        }

        var title = request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        if (ResolveTmdbReference(request, "movie") is { } reference) {
            return Proposal(await _mapper.MovieToProposalAsync(await _client.GetMovieAsync(reference.Id), reference.MatchReason));
        }

        return IdentifyPluginResult.ForCandidates(await SearchMovieCandidatesAsync(title));
    }

    private async Task<IdentifyPluginResult> IdentifySeasonAsync(IdentifyPluginRequest request) {
        var seriesId = TmdbMetadataHelpers.SeriesTmdbIdFromContext(request);
        var seasonNumber = TmdbMetadataHelpers.PositionValue(request, "seasonNumber", "season", "sortOrder");
        if (seriesId is null || seasonNumber is null) {
            var parentTitle = request.StructuralContext?.Ancestors
                .FirstOrDefault(ancestor => ancestor.Kind.Equals("video-series", StringComparison.OrdinalIgnoreCase))
                ?.Title;
            seriesId = (await SearchSeriesResultsAsync(parentTitle)).FirstOrDefault()?.Result.Id;
        }

        if (seriesId is null || seasonNumber is null) {
            return IdentifyPluginResult.None();
        }

        var detail = await _client.GetSeasonAsync(seriesId.Value, seasonNumber.Value);
        var episodes = await RepairSeasonEpisodesAsync(seriesId.Value, seasonNumber.Value, detail.Episodes);
        var summary = new TmdbSeasonSummary(
            detail.Id,
            detail.SeasonNumber ?? seasonNumber.Value,
            episodes.Length,
            detail.Name,
            detail.Overview,
            detail.AirDate,
            detail.PosterPath);
        return Proposal(await _mapper.SeasonToProposalAsync(seriesId.Value, summary, episodes, "context"));
    }

    private async Task<IdentifyPluginResult> IdentifyPersonAsync(IdentifyPluginRequest request) {
        var title = request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        if (ResolveTmdbReference(request, "person") is { } reference) {
            return Proposal(await _mapper.PersonToProposalAsync(reference.Id, reference.MatchReason));
        }

        return IdentifyPluginResult.ForCandidates(await SearchPersonCandidatesAsync(title));
    }

    private async Task<IdentifyPluginResult> IdentifyStudioAsync(IdentifyPluginRequest request) {
        var title = request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        if (ResolveTmdbReference(request, "company") is { } reference) {
            return Proposal(await _mapper.StudioToProposalAsync(reference.Id, reference.MatchReason));
        }

        return IdentifyPluginResult.ForCandidates(await SearchStudioCandidatesAsync(title));
    }

    private static IdentifyPluginResult Proposal(EntityMetadataProposal? proposal) =>
        proposal is null ? IdentifyPluginResult.None() : IdentifyPluginResult.ForProposal(proposal);

    private static TmdbReference? ResolveTmdbReference(IdentifyPluginRequest request, string mediaType) {
        if (TmdbMetadataHelpers.ExtractTmdbId(request.Query.ExternalIds) is { } queryId) {
            return new TmdbReference(queryId, "external-id");
        }

        if (TryParseUrlForMediaType(request.Query.Url, mediaType, out var queryUrlId)) {
            return new TmdbReference(queryUrlId, "url");
        }

        if (IsExplicitTitleSearch(request)) {
            return null;
        }

        foreach (var url in request.Hints.Urls) {
            if (TryParseUrlForMediaType(url, mediaType, out var hintUrlId)) {
                return new TmdbReference(hintUrlId, "url");
            }
        }

        if (TmdbMetadataHelpers.ExtractTmdbId(request.Hints.ExternalIds) is { } hintId) {
            return new TmdbReference(hintId, "external-id");
        }

        return null;
    }

    private static bool TryParseUrlForMediaType(string? url, string mediaType, out int id) {
        var parsed = string.IsNullOrWhiteSpace(url) ? null : TmdbMetadataHelpers.ParseTmdbUrlValue(url);
        if (parsed is not null && parsed.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase)) {
            id = parsed.Id;
            return true;
        }

        id = 0;
        return false;
    }

    private static bool IsExplicitTitleSearch(IdentifyPluginRequest request) =>
        request.Action.Equals("search", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(request.Query.Title) &&
        string.IsNullOrWhiteSpace(request.Query.Url) &&
        request.Query.ExternalIds is not { Count: > 0 };

    private async Task<IReadOnlyList<EntitySearchCandidate>> SearchMovieCandidatesAsync(string? rawTitle) =>
        (await SearchMovieResultsAsync(rawTitle))
        .Take(10)
        .Select(row => TmdbMetadataHelpers.ToCandidate(row, "movie"))
        .ToArray();

    private async Task<List<ScoredResult>> SearchMovieResultsAsync(string? rawTitle) {
        if (string.IsNullOrWhiteSpace(rawTitle)) {
            return [];
        }

        var clean = TmdbMetadataHelpers.Normalize(rawTitle);
        var data = await _client.FetchAsync<TmdbSearchResponse>("/search/movie", new Dictionary<string, string> {
            ["query"] = clean.Length == 0 ? rawTitle : clean,
            ["include_adult"] = "true"
        });
        return TmdbMetadataHelpers.Score(rawTitle, data.Results ?? [], result => result.Title ?? result.Name ?? string.Empty);
    }

    private async Task<IReadOnlyList<EntitySearchCandidate>> SearchSeriesCandidatesAsync(string? rawTitle) =>
        (await SearchSeriesResultsAsync(rawTitle))
        .Take(10)
        .Select(row => TmdbMetadataHelpers.ToCandidate(row, "tv"))
        .ToArray();

    private async Task<List<ScoredResult>> SearchSeriesResultsAsync(string? rawTitle) {
        if (string.IsNullOrWhiteSpace(rawTitle)) {
            return [];
        }

        var clean = TmdbMetadataHelpers.Normalize(rawTitle);
        var data = await _client.FetchAsync<TmdbSearchResponse>("/search/tv", new Dictionary<string, string> {
            ["query"] = clean.Length == 0 ? rawTitle : clean,
            ["include_adult"] = "true"
        });
        var results = data.Results ?? [];
        if (results.Length == 0) {
            var multi = await _client.FetchAsync<TmdbSearchResponse>("/search/multi", new Dictionary<string, string> {
                ["query"] = clean.Length == 0 ? rawTitle : clean,
                ["include_adult"] = "true"
            });
            results = (multi.Results ?? []).Where(result => result.MediaType == "tv").ToArray();
        }

        return TmdbMetadataHelpers.Score(rawTitle, results, result => result.Name ?? result.Title ?? string.Empty);
    }

    private async Task<IReadOnlyList<EntitySearchCandidate>> SearchPersonCandidatesAsync(string? rawTitle) {
        if (string.IsNullOrWhiteSpace(rawTitle)) {
            return [];
        }

        var data = await _client.FetchAsync<TmdbSearchResponse>("/search/person", new Dictionary<string, string> {
            ["query"] = TmdbMetadataHelpers.Normalize(rawTitle),
            ["include_adult"] = "true"
        });
        return TmdbMetadataHelpers.Score(rawTitle, data.Results ?? [], result => result.Name ?? string.Empty)
            .Take(10)
            .Select(row => TmdbMetadataHelpers.ToCandidate(row, "person"))
            .ToArray();
    }

    private async Task<IReadOnlyList<EntitySearchCandidate>> SearchStudioCandidatesAsync(string? rawTitle) {
        if (string.IsNullOrWhiteSpace(rawTitle)) {
            return [];
        }

        var data = await _client.FetchAsync<TmdbSearchResponse>("/search/company", new Dictionary<string, string> {
            ["query"] = TmdbMetadataHelpers.Normalize(rawTitle)
        });
        return TmdbMetadataHelpers.Score(rawTitle, data.Results ?? [], result => result.Name ?? string.Empty)
            .Take(10)
            .Select(row => TmdbMetadataHelpers.ToCandidate(row, "company"))
            .ToArray();
    }

    private async Task<EntityMetadataProposal?> EpisodeFromContextAsync(int seriesId, int seasonNumber, int episodeNumber) {
        var episode = await _client.GetEpisodeAsync(seriesId, seasonNumber, episodeNumber);
        return await _mapper.EpisodeToProposalAsync(seriesId, seasonNumber, episode, "parent-context");
    }

    private async Task<TmdbEpisode[]> RepairSeasonEpisodesAsync(
        int seriesId,
        int seasonNumber,
        IReadOnlyList<TmdbEpisode> episodes) {
        if (episodes.Count == 0) {
            return [];
        }

        var byNumber = new Dictionary<int, TmdbEpisode>();
        var hasDuplicate = false;
        foreach (var episode in episodes.Where(episode => episode.EpisodeNumber > 0).OrderBy(episode => episode.EpisodeNumber)) {
            if (!byNumber.TryAdd(episode.EpisodeNumber, episode)) {
                hasDuplicate = true;
            }
        }

        var maxEpisodeNumber = Math.Max(episodes.Count, byNumber.Keys.DefaultIfEmpty(0).Max());
        var missing = Enumerable.Range(1, maxEpisodeNumber)
            .Where(number => !byNumber.ContainsKey(number))
            .ToArray();
        if (!hasDuplicate && missing.Length == 0) {
            return episodes
                .OrderBy(episode => episode.EpisodeNumber)
                .ToArray();
        }

        foreach (var episodeNumber in missing) {
            try {
                byNumber[episodeNumber] = await _client.GetEpisodeAsync(seriesId, seasonNumber, episodeNumber);
            } catch (HttpRequestException) {
            } catch (InvalidOperationException) {
            }
        }

        return byNumber.Values
            .OrderBy(episode => episode.EpisodeNumber)
            .ToArray();
    }

    private sealed record TmdbReference(int Id, string MatchReason);
}
