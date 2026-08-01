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
            ["movie"] = IdentifyMovieAsync,
            ["video"] = IdentifyMovieAsync,
            ["video-episode"] = IdentifyEpisodeAsync,
            ["person"] = IdentifyPersonAsync,
            ["studio"] = IdentifyStudioAsync
        };
    }

    public async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) =>
        _handlers.TryGetValue(request.Entity.Kind, out var handler)
            ? await handler(request)
            : IdentifyPluginResult.None();

    private async Task<IdentifyPluginResult> IdentifySeriesAsync(IdentifyPluginRequest request) {
        var title = SearchTitle(request, TmdbConstants.SearchFields.SeriesTitle, TmdbConstants.SearchFields.Title);
        if (ResolveTmdbReference(request, "tv") is { } reference) {
            return Proposal(await _mapper.TvToProposalAsync(
                await _client.GetTvAsync(reference.Id),
                reference.MatchReason,
                request.IncludeRelationshipDetails));
        }

        return IdentifyPluginResult.ForCandidates(await SearchSeriesCandidatesAsync(
            title,
            request.IncludeNsfw,
            TmdbMetadataHelpers.SearchYear(request),
            SearchLimit(request)));
    }

    private async Task<IdentifyPluginResult> IdentifyMovieAsync(IdentifyPluginRequest request) {
        var title = SearchTitle(request, TmdbConstants.SearchFields.Title);
        if (ResolveTmdbReference(request, "movie") is { } reference) {
            return Proposal(await _mapper.MovieToProposalAsync(
                await _client.GetMovieAsync(reference.Id),
                reference.MatchReason,
                MovieTargetKind(request),
                request.IncludeRelationshipDetails));
        }

        return IdentifyPluginResult.ForCandidates(await SearchMovieCandidatesAsync(
            title,
            request.IncludeNsfw,
            TmdbMetadataHelpers.SearchYear(request),
            SearchLimit(request)));
    }

    private async Task<IdentifyPluginResult> IdentifyEpisodeAsync(IdentifyPluginRequest request) =>
        TmdbMetadataHelpers.TryEpisodeContext(request, out var seriesId, out var seasonNumber, out var episodeNumber)
            ? Proposal(await EpisodeFromContextAsync(seriesId, seasonNumber, episodeNumber))
            : IdentifyPluginResult.None();

    private async Task<IdentifyPluginResult> IdentifySeasonAsync(IdentifyPluginRequest request) {
        var seriesId = TmdbMetadataHelpers.SeriesTmdbIdFromContext(request);
        var seasonNumber = TmdbMetadataHelpers.PositionValue(request, "seasonNumber", "season", "sortOrder");
        // A bare id lookup carries the composite season work id ("{seriesId}_s{seasonNumber}") and no
        // structural context — the id itself is the context.
        if ((seriesId is null || seasonNumber is null) &&
            TmdbMetadataHelpers.TryParseSeasonWorkId(request, out var idSeriesId, out var idSeasonNumber)) {
            seriesId = idSeriesId;
            seasonNumber = idSeasonNumber;
        }

        if (seriesId is null || seasonNumber is null) {
            var parentTitle = request.StructuralContext?.Ancestors
                .FirstOrDefault(ancestor => ancestor.Kind.Equals("video-series", StringComparison.OrdinalIgnoreCase))
                ?.Title;
            seriesId = (await SearchSeriesResultsAsync(parentTitle, request.IncludeNsfw)).FirstOrDefault()?.Result.Id;
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
        return Proposal(await _mapper.SeasonToProposalAsync(
            seriesId.Value,
            summary,
            episodes,
            "context",
            request.IncludeRelationshipDetails));
    }

    private async Task<IdentifyPluginResult> IdentifyPersonAsync(IdentifyPluginRequest request) {
        var title = SearchTitle(request, TmdbConstants.SearchFields.Title);
        if (ResolveTmdbReference(request, "person") is { } reference) {
            return Proposal(await _mapper.PersonToProposalAsync(reference.Id, reference.MatchReason));
        }

        return IdentifyPluginResult.ForCandidates(await SearchPersonCandidatesAsync(title, request.IncludeNsfw, SearchLimit(request)));
    }

    private async Task<IdentifyPluginResult> IdentifyStudioAsync(IdentifyPluginRequest request) {
        var title = SearchTitle(request, TmdbConstants.SearchFields.Title);
        if (ResolveTmdbReference(request, "company") is { } reference) {
            return Proposal(await _mapper.StudioToProposalAsync(reference.Id, reference.MatchReason));
        }

        return IdentifyPluginResult.ForCandidates(await SearchStudioCandidatesAsync(title, SearchLimit(request)));
    }

    private static IdentifyPluginResult Proposal(EntityMetadataProposal? proposal) =>
        proposal is null ? IdentifyPluginResult.None() : IdentifyPluginResult.ForProposal(proposal);

    private static string MovieTargetKind(IdentifyPluginRequest request) =>
        request.Entity.Kind.Equals("movie", StringComparison.OrdinalIgnoreCase) ? "movie" : "video";

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
        (!string.IsNullOrWhiteSpace(request.Query.Title) || TmdbMetadataHelpers.HasSearchFields(request)) &&
        string.IsNullOrWhiteSpace(request.Query.Url) &&
        request.Query.ExternalIds is not { Count: > 0 };

    private static string? SearchTitle(IdentifyPluginRequest request, params string[] fieldKeys) =>
        TmdbMetadataHelpers.SearchField(request, fieldKeys) ??
        request.Query.Title ??
        request.Hints.Title ??
        request.Entity.Title;

    // TMDB returns explicit adult titles whenever include_adult is set. Honor the request's
    // NSFW gate: only ask for adult results in NSFW mode, and defensively drop any result the
    // provider still flags as adult when in SFW mode (multi-search can surface them regardless).
    private static string IncludeAdultParam(bool includeNsfw) => includeNsfw ? "true" : "false";

    private static int SearchLimit(IdentifyPluginRequest request) => Math.Clamp(request.Query.Limit, 1, 100);

    private static IReadOnlyList<TmdbSearchResult> FilterAdult(TmdbSearchResult[]? results, bool includeNsfw) =>
        (results ?? []).Where(result => includeNsfw || result.Adult != true).ToArray();

    private async Task<IReadOnlyList<EntitySearchCandidate>> SearchMovieCandidatesAsync(string? rawTitle, bool includeNsfw, int? year, int limit) =>
        (await SearchMovieResultsAsync(rawTitle, includeNsfw, year, limit))
        .Take(limit)
        .Select(row => TmdbMetadataHelpers.ToCandidate(row, "movie"))
        .ToArray();

    private async Task<List<ScoredResult>> SearchMovieResultsAsync(string? rawTitle, bool includeNsfw, int? year = null, int limit = 20) {
        if (string.IsNullOrWhiteSpace(rawTitle)) {
            return [];
        }

        var clean = TmdbMetadataHelpers.Normalize(rawTitle);
        var parameters = new Dictionary<string, string> {
            ["query"] = clean.Length == 0 ? rawTitle : clean,
            ["include_adult"] = IncludeAdultParam(includeNsfw)
        };
        if (year is not null) parameters["year"] = year.Value.ToString();
        var results = await FetchSearchResultsAsync("/search/movie", parameters, limit);
        return TmdbMetadataHelpers.Score(rawTitle, FilterAdult(results, includeNsfw), result => result.Title ?? result.Name ?? string.Empty);
    }

    private async Task<IReadOnlyList<EntitySearchCandidate>> SearchSeriesCandidatesAsync(string? rawTitle, bool includeNsfw, int? year, int limit) =>
        (await SearchSeriesResultsAsync(rawTitle, includeNsfw, year, limit))
        .Take(limit)
        .Select(row => TmdbMetadataHelpers.ToCandidate(row, "tv"))
        .ToArray();

    private async Task<List<ScoredResult>> SearchSeriesResultsAsync(string? rawTitle, bool includeNsfw, int? year = null, int limit = 20) {
        if (string.IsNullOrWhiteSpace(rawTitle)) {
            return [];
        }

        var clean = TmdbMetadataHelpers.Normalize(rawTitle);
        var includeAdult = IncludeAdultParam(includeNsfw);
        var parameters = new Dictionary<string, string> {
            ["query"] = clean.Length == 0 ? rawTitle : clean,
            ["include_adult"] = includeAdult
        };
        if (year is not null) parameters["first_air_date_year"] = year.Value.ToString();
        var results = await FetchSearchResultsAsync("/search/tv", parameters, limit);
        if (results.Length == 0) {
            var multi = await FetchSearchResultsAsync("/search/multi", new Dictionary<string, string> {
                ["query"] = clean.Length == 0 ? rawTitle : clean,
                ["include_adult"] = includeAdult
            }, limit);
            results = multi
                .Where(result => result.MediaType == "tv")
                .Where(result => year is null || result.FirstAirDate?.StartsWith(year.Value.ToString(), StringComparison.Ordinal) == true)
                .ToArray();
        }

        return TmdbMetadataHelpers.Score(rawTitle, FilterAdult(results, includeNsfw), result => result.Name ?? result.Title ?? string.Empty);
    }

    private async Task<IReadOnlyList<EntitySearchCandidate>> SearchPersonCandidatesAsync(string? rawTitle, bool includeNsfw, int limit) {
        if (string.IsNullOrWhiteSpace(rawTitle)) {
            return [];
        }

        var results = await FetchSearchResultsAsync("/search/person", new Dictionary<string, string> {
            ["query"] = TmdbMetadataHelpers.Normalize(rawTitle),
            ["include_adult"] = IncludeAdultParam(includeNsfw)
        }, limit);
        return TmdbMetadataHelpers.Score(rawTitle, FilterAdult(results, includeNsfw), result => result.Name ?? string.Empty)
            .Take(limit)
            .Select(row => TmdbMetadataHelpers.ToCandidate(row, "person"))
            .ToArray();
    }

    private async Task<IReadOnlyList<EntitySearchCandidate>> SearchStudioCandidatesAsync(string? rawTitle, int limit) {
        if (string.IsNullOrWhiteSpace(rawTitle)) {
            return [];
        }

        var results = await FetchSearchResultsAsync("/search/company", new Dictionary<string, string> {
            ["query"] = TmdbMetadataHelpers.Normalize(rawTitle)
        }, limit);
        return TmdbMetadataHelpers.Score(rawTitle, results, result => result.Name ?? string.Empty)
            .Take(limit)
            .Select(row => TmdbMetadataHelpers.ToCandidate(row, "company"))
            .ToArray();
    }

    private async Task<TmdbSearchResult[]> FetchSearchResultsAsync(
        string path,
        IReadOnlyDictionary<string, string> parameters,
        int limit) {
        const int providerPageSize = 20;
        var output = new List<TmdbSearchResult>(limit);
        var pageCount = (int)Math.Ceiling(limit / (double)providerPageSize);
        for (var page = 1; page <= pageCount && output.Count < limit; page++) {
            var pageParameters = parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
            pageParameters["page"] = page.ToString();
            var response = await _client.FetchAsync<TmdbSearchResponse>(path, pageParameters);
            var rows = response.Results ?? [];
            output.AddRange(rows);
            if (rows.Length < providerPageSize || response.TotalPages is { } totalPages && page >= totalPages) {
                break;
            }
        }

        return output.Take(limit).ToArray();
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
