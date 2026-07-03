namespace Prismedia.Plugin.Tmdb;

internal sealed class TmdbProposalMapper {
    private readonly TmdbApiClient _client;

    public TmdbProposalMapper(TmdbApiClient client) {
        _client = client;
    }

    public async Task<EntityMetadataProposal> MovieToProposalAsync(
        TmdbMovieDetail detail,
        string matchReason,
        string targetKind = "video",
        bool includeRelationshipDetails = true) {
        var genres = detail.Genres?.Select(genre => genre.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray() ?? [];
        var studio = detail.ProductionCompanies?.FirstOrDefault()?.Name;
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(detail.ReleaseDate)) {
            dates["release"] = detail.ReleaseDate;
        }

        var stats = new Dictionary<string, int>();
        if (detail.Runtime is { } runtime) {
            stats["runtimeMinutes"] = runtime;
        }

        var patch = new EntityMetadataPatch(
            detail.Title,
            detail.Overview,
            new Dictionary<string, string> { ["tmdb"] = detail.Id.ToString() },
            [TmdbMetadataHelpers.TmdbUrl("movie", detail.Id)],
            genres,
            studio,
            BuildCredits(detail.Credits),
            dates,
            stats,
            new Dictionary<string, int>(),
            null);

        var relationships = (await BuildPersonRelationshipsAsync(detail.Credits, includeRelationshipDetails)).ToList();
        var studioChild = await BuildStudioChildAsync(detail.ProductionCompanies?.FirstOrDefault(), includeRelationshipDetails);
        if (studioChild is not null) {
            relationships.Add(studioChild);
        }

        return new EntityMetadataProposal(
            $"tmdb:movie:{detail.Id}",
            "tmdb",
            targetKind,
            matchReason is "external-id" or "url" ? 1 : 0.8m,
            matchReason,
            patch,
            BuildImages(detail.PosterPath, detail.BackdropPath, detail.Images),
            [],
            Relationships: relationships);
    }

    public async Task<EntityMetadataProposal> TvToProposalAsync(
        TmdbTvDetail detail,
        string matchReason,
        bool includeRelationshipDetails = true) {
        var genres = detail.Genres?.Select(genre => genre.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray() ?? [];
        var studio = detail.Networks?.FirstOrDefault()?.Name ?? detail.ProductionCompanies?.FirstOrDefault()?.Name;
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(detail.FirstAirDate)) {
            dates["firstAir"] = detail.FirstAirDate;
        }

        if (!string.IsNullOrWhiteSpace(detail.LastAirDate)) {
            dates["lastAir"] = detail.LastAirDate;
        }

        var stats = new Dictionary<string, int>();
        if (detail.NumberOfSeasons is { } seasons) {
            stats["seasonCount"] = seasons;
        }

        if (detail.NumberOfEpisodes is { } episodes) {
            stats["episodeCount"] = episodes;
        }

        var patch = new EntityMetadataPatch(
            detail.Name,
            detail.Overview,
            new Dictionary<string, string> { ["tmdb"] = detail.Id.ToString() },
            [TmdbMetadataHelpers.TmdbUrl("tv", detail.Id)],
            genres,
            studio,
            BuildCredits(detail.Credits),
            dates,
            stats,
            new Dictionary<string, int>(),
            TmdbMetadataHelpers.MapStatus(detail.Status));

        var seasonChildren = (detail.Seasons ?? [])
            .Where(s => s.SeasonNumber >= 0)
            .Select(summary => SeasonShellProposal(detail.Id, summary, "cascade"))
            .ToArray();

        var relationships = (await BuildPersonRelationshipsAsync(detail.Credits, includeRelationshipDetails)).ToList();
        var studioChild = detail.Networks?.FirstOrDefault() is { } network
            ? NetworkStudioChild(network)
            : await BuildStudioChildAsync(detail.ProductionCompanies?.FirstOrDefault(), includeRelationshipDetails);
        if (studioChild is not null) {
            relationships.Add(studioChild);
        }

        return new EntityMetadataProposal(
            $"tmdb:tv:{detail.Id}",
            "tmdb",
            "video-series",
            matchReason is "external-id" or "url" ? 1 : 0.8m,
            matchReason,
            patch,
            BuildImages(detail.PosterPath, detail.BackdropPath, detail.Images),
            seasonChildren,
            Relationships: relationships);
    }

    public async Task<EntityMetadataProposal> SeasonToProposalAsync(
        int seriesId,
        TmdbSeasonSummary season,
        TmdbEpisode[]? episodes,
        string matchReason,
        bool includeRelationshipDetails = true) {
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(season.AirDate)) {
            dates["air"] = season.AirDate;
        }

        var stats = new Dictionary<string, int> { ["episodeCount"] = season.EpisodeCount };
        var positions = new Dictionary<string, int> { ["seasonNumber"] = season.SeasonNumber };
        // The composite id is the canonical season work id: TMDB has no season-by-id endpoint, so only
        // the "{seriesId}_s{seasonNumber}" form can be resolved back on a later lookup.
        var externalId = $"{seriesId}_s{season.SeasonNumber}";
        var seasonUrl = $"https://www.themoviedb.org/tv/{seriesId}/season/{season.SeasonNumber}";
        var patch = new EntityMetadataPatch(
            string.IsNullOrWhiteSpace(season.Name) ? $"Season {season.SeasonNumber}" : season.Name,
            season.Overview,
            new Dictionary<string, string> { ["tmdb"] = externalId },
            [seasonUrl],
            [],
            null,
            [],
            dates,
            stats,
            positions,
            null);

        var episodeChildren = await Task.WhenAll((episodes ?? [])
            .Select(ep => EpisodeToProposalAsync(seriesId, season.SeasonNumber, ep, "cascade", includeRelationshipDetails)));

        return new EntityMetadataProposal(
            $"tmdb:tv:{seriesId}:season:{season.SeasonNumber}",
            "tmdb",
            "video-season",
            0.9m,
            matchReason,
            patch,
            BuildImages(season.PosterPath, null, null),
            episodeChildren);
    }

    public async Task<EntityMetadataProposal> EpisodeToProposalAsync(
        int seriesId,
        int seasonNumber,
        TmdbEpisode episode,
        string matchReason,
        bool includeRelationshipDetails = true) {
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(episode.AirDate)) {
            dates["air"] = episode.AirDate;
        }

        var positions = new Dictionary<string, int> {
            ["episodeNumber"] = episode.EpisodeNumber,
            ["seasonNumber"] = seasonNumber
        };

        var stats = new Dictionary<string, int>();
        if (episode.Runtime is { } runtime) {
            stats["runtimeMinutes"] = runtime;
        }

        var patch = new EntityMetadataPatch(
            episode.Name ?? $"Episode {episode.EpisodeNumber}",
            episode.Overview,
            new Dictionary<string, string> { ["tmdb"] = episode.Id.ToString() },
            [$"https://www.themoviedb.org/tv/{seriesId}/season/{seasonNumber}/episode/{episode.EpisodeNumber}"],
            [],
            null,
            BuildEpisodeCredits(episode),
            dates,
            stats,
            positions,
            null);

        return new EntityMetadataProposal(
            $"tmdb:tv:{seriesId}:s{seasonNumber}:e{episode.EpisodeNumber}",
            "tmdb",
            "video-episode",
            0.9m,
            matchReason,
            patch,
            BuildEpisodeImages(episode),
            [],
            Relationships: await BuildEpisodePersonRelationshipsAsync(episode, includeRelationshipDetails));
    }

    private static EntityMetadataProposal SeasonShellProposal(
        int seriesId,
        TmdbSeasonSummary season,
        string matchReason) {
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(season.AirDate)) {
            dates["air"] = season.AirDate;
        }

        var stats = new Dictionary<string, int> { ["episodeCount"] = season.EpisodeCount };
        var positions = new Dictionary<string, int> { ["seasonNumber"] = season.SeasonNumber };
        // The composite id is the canonical season work id: TMDB has no season-by-id endpoint, so only
        // the "{seriesId}_s{seasonNumber}" form can be resolved back on a later lookup.
        var externalId = $"{seriesId}_s{season.SeasonNumber}";
        var seasonUrl = $"https://www.themoviedb.org/tv/{seriesId}/season/{season.SeasonNumber}";
        var patch = new EntityMetadataPatch(
            string.IsNullOrWhiteSpace(season.Name) ? $"Season {season.SeasonNumber}" : season.Name,
            season.Overview,
            new Dictionary<string, string> { ["tmdb"] = externalId },
            [seasonUrl],
            [],
            null,
            [],
            dates,
            stats,
            positions,
            null);

        return new EntityMetadataProposal(
            $"tmdb:tv:{seriesId}:season:{season.SeasonNumber}",
            "tmdb",
            "video-season",
            0.85m,
            matchReason,
            patch,
            BuildImages(season.PosterPath, null, null),
            []);
    }

    public async Task<EntityMetadataProposal> PersonToProposalAsync(
        int id,
        string matchReason) {
        var detail = await _client.GetPersonAsync(id);
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(detail.Birthday)) {
            dates["birth"] = detail.Birthday;
        }

        if (!string.IsNullOrWhiteSpace(detail.Deathday)) {
            dates["death"] = detail.Deathday;
        }

        // TMDB popularity is a trending score, not person metadata; Prismedia ignores the
        // stat on persistence, so emitting it only clutters the review proposal.
        var stats = new Dictionary<string, int>();

        var externalIds = new Dictionary<string, string> { ["tmdb"] = detail.Id.ToString() };
        if (!string.IsNullOrWhiteSpace(detail.ImdbId)) {
            externalIds["imdb"] = detail.ImdbId;
        }

        var urls = new List<string> { TmdbMetadataHelpers.TmdbUrl("person", detail.Id) };
        if (!string.IsNullOrWhiteSpace(detail.Homepage)) {
            urls.Add(detail.Homepage);
        }

        var patch = new EntityMetadataPatch(
            detail.Name,
            detail.Biography,
            externalIds,
            urls,
            [],
            null,
            [],
            dates,
            stats,
            new Dictionary<string, int>(),
            detail.KnownForDepartment);

        return new EntityMetadataProposal(
            $"tmdb:person:{detail.Id}",
            "tmdb",
            "person",
            matchReason is "external-id" or "url" ? 1 : null,
            matchReason,
            patch,
            BuildPersonImages(detail.ProfilePath, detail.Images),
            []);
    }

    public async Task<EntityMetadataProposal> StudioToProposalAsync(
        int id,
        string matchReason) {
        var detail = await _client.GetCompanyAsync(id);
        var urls = new List<string> { TmdbMetadataHelpers.TmdbUrl("company", detail.Id) };
        if (!string.IsNullOrWhiteSpace(detail.Homepage)) {
            urls.Add(detail.Homepage);
        }

        var patch = new EntityMetadataPatch(
            detail.Name,
            detail.Description,
            new Dictionary<string, string> { ["tmdb"] = detail.Id.ToString() },
            urls,
            [],
            null,
            [],
            new Dictionary<string, string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            detail.OriginCountry);

        return new EntityMetadataProposal(
            $"tmdb:studio:{detail.Id}",
            "tmdb",
            "studio",
            matchReason is "external-id" or "url" ? 1 : null,
            matchReason,
            patch,
            BuildCompanyImages(detail.LogoPath, detail.Images),
            []);
    }

    private static IReadOnlyList<CreditPatch> BuildCredits(TmdbCredits? credits) {
        var cast = (credits?.Cast ?? [])
            .OrderBy(member => member.Order ?? 0)
            .Select(member => new CreditPatch(member.Name, "cast", member.Character, member.Order))
            .ToList();

        var crew = (credits?.Crew ?? [])
            .Where(member => member.Job is "Director" or "Creator" or "Writer")
            .Select((member, index) => new CreditPatch(member.Name, member.Job.ToLowerInvariant(), null, 1000 + index));

        cast.AddRange(crew);
        return cast;
    }

    private static IReadOnlyList<CreditPatch> BuildEpisodeCredits(TmdbEpisode episode) {
        var credits = (episode.GuestStars ?? [])
            .OrderBy(member => member.Order ?? 0)
            .Select(member => new CreditPatch(member.Name, "guest", member.Character, member.Order))
            .ToList();

        credits.AddRange((episode.Crew ?? [])
            .Where(member => member.Job is "Director" or "Writer")
            .Select((member, index) => new CreditPatch(member.Name, member.Job.ToLowerInvariant(), null, 1000 + index)));

        return credits;
    }

    private async Task<IReadOnlyList<EntityMetadataProposal>> BuildPersonRelationshipsAsync(
        TmdbCredits? credits,
        bool includeRelationshipDetails) {
        var seen = new HashSet<int>();
        var children = new List<EntityMetadataProposal>();

        foreach (var member in (credits?.Cast ?? []).OrderBy(m => m.Order ?? 0)) {
            var child = await PersonChildAsync(member.Id, member.Name, member.ProfilePath, seen, includeRelationshipDetails);
            if (child is not null) {
                children.Add(child);
            }
        }

        foreach (var member in (credits?.Crew ?? []).Where(m => m.Job is "Director" or "Creator" or "Writer")) {
            var child = await PersonChildAsync(member.Id, member.Name, member.ProfilePath, seen, includeRelationshipDetails);
            if (child is not null) {
                children.Add(child);
            }
        }

        return children;
    }

    private async Task<IReadOnlyList<EntityMetadataProposal>> BuildEpisodePersonRelationshipsAsync(
        TmdbEpisode episode,
        bool includeRelationshipDetails) {
        var seen = new HashSet<int>();
        var children = new List<EntityMetadataProposal>();

        foreach (var member in (episode.GuestStars ?? []).OrderBy(m => m.Order ?? 0)) {
            var child = await PersonChildAsync(member.Id, member.Name, member.ProfilePath, seen, includeRelationshipDetails);
            if (child is not null) {
                children.Add(child);
            }
        }

        foreach (var member in (episode.Crew ?? []).Where(m => m.Job is "Director" or "Writer")) {
            var child = await PersonChildAsync(member.Id, member.Name, member.ProfilePath, seen, includeRelationshipDetails);
            if (child is not null) {
                children.Add(child);
            }
        }

        return children;
    }

    private async Task<EntityMetadataProposal?> PersonChildAsync(
        int id,
        string name,
        string? profilePath,
        HashSet<int> seen,
        bool includeRelationshipDetails) {
        if (id > 0 && seen.Add(id)) {
            if (!includeRelationshipDetails) {
                return PersonFallback(id, name, profilePath);
            }

            try {
                return await PersonToProposalAsync(id, "cascade");
            } catch {
                return PersonFallback(id, name, profilePath);
            }
        }

        return id <= 0 ? PersonFallback(id, name, profilePath) : null;
    }

    private static EntityMetadataProposal? PersonFallback(int id, string name, string? profilePath) {
        if (string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        var externalIds = id > 0 ? new Dictionary<string, string> { ["tmdb"] = id.ToString() } : new Dictionary<string, string>();
        var urls = id > 0 ? new[] { TmdbMetadataHelpers.TmdbUrl("person", id) } : [];
        var patch = new EntityMetadataPatch(
            name,
            null,
            externalIds,
            urls,
            [],
            null,
            [],
            new Dictionary<string, string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            null);
        var posterUrl = TmdbMetadataHelpers.ImageUrl(profilePath, "original");
        var images = posterUrl is null
            ? []
            : new List<ImageCandidate> { new("poster", posterUrl, "tmdb", 10, null, null, null) };
        return new EntityMetadataProposal(
            id > 0 ? $"tmdb:person:{id}" : $"tmdb:person:{name}",
            "tmdb",
            "person",
            null,
            "cascade",
            patch,
            images,
            []);
    }

    private async Task<EntityMetadataProposal?> BuildStudioChildAsync(TmdbNamed? company, bool includeRelationshipDetails) {
        if (company is null) {
            return null;
        }

        if (!includeRelationshipDetails) {
            return StudioFallback(company);
        }

        if (company.Id > 0) {
            try {
                return await StudioToProposalAsync(company.Id, "cascade");
            } catch {
                return StudioFallback(company);
            }
        }

        return StudioFallback(company);
    }

    private static EntityMetadataProposal? NetworkStudioChild(TmdbNamed network) {
        if (string.IsNullOrWhiteSpace(network.Name)) {
            return null;
        }

        var externalIds = network.Id > 0
            ? new Dictionary<string, string> { ["tmdbNetwork"] = network.Id.ToString() }
            : new Dictionary<string, string>();
        var urls = network.Id > 0 ? new[] { TmdbMetadataHelpers.TmdbUrl("network", network.Id) } : [];
        var patch = new EntityMetadataPatch(
            network.Name,
            null,
            externalIds,
            urls,
            [],
            null,
            [],
            new Dictionary<string, string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            null);
        var logoUrl = TmdbMetadataHelpers.ImageUrl(network.LogoPath, "w500");
        var images = logoUrl is null
            ? []
            : new List<ImageCandidate> { new("logo", logoUrl, "tmdb", 10, null, null, null) };

        return new EntityMetadataProposal(
            network.Id > 0 ? $"tmdb:network:{network.Id}" : $"tmdb:network:{network.Name}",
            "tmdb",
            "studio",
            null,
            "cascade",
            patch,
            images,
            []);
    }

    private static EntityMetadataProposal? StudioFallback(TmdbNamed company) {
        if (string.IsNullOrWhiteSpace(company.Name) || string.IsNullOrWhiteSpace(company.LogoPath)) {
            return null;
        }

        var externalIds = company.Id > 0 ? new Dictionary<string, string> { ["tmdb"] = company.Id.ToString() } : new Dictionary<string, string>();
        var urls = company.Id > 0 ? new[] { TmdbMetadataHelpers.TmdbUrl("company", company.Id) } : [];
        var patch = new EntityMetadataPatch(
            company.Name,
            null,
            externalIds,
            urls,
            [],
            null,
            [],
            new Dictionary<string, string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            null);
        var images = new List<ImageCandidate> {
            new("logo", TmdbMetadataHelpers.ImageUrl(company.LogoPath, "w500")!, "tmdb", 10, null, null, null)
        };
        return new EntityMetadataProposal(
            company.Id > 0 ? $"tmdb:studio:{company.Id}" : $"tmdb:studio:{company.Name}",
            "tmdb",
            "studio",
            null,
            "cascade",
            patch,
            images,
            []);
    }

    private static IReadOnlyList<ImageCandidate> BuildImages(string? posterPath, string? backdropPath, TmdbImagesResponse? images) {
        var result = new List<ImageCandidate>();
        result.AddRange(MapImages("poster", images?.Posters, "original"));
        result.AddRange(MapImages("backdrop", images?.Backdrops, "original"));
        result.AddRange(MapImages("logo", images?.Logos, "w500"));

        AddFallback(result, "poster", TmdbMetadataHelpers.ImageUrl(posterPath, "original"), 10);
        AddFallback(result, "backdrop", TmdbMetadataHelpers.ImageUrl(backdropPath, "original"), 9);
        return result;
    }

    private static IReadOnlyList<ImageCandidate> BuildPersonImages(string? profilePath, TmdbProfileImagesResponse? images) {
        var result = MapImages("poster", images?.Profiles, "original").ToList();
        AddFallback(result, "poster", TmdbMetadataHelpers.ImageUrl(profilePath, "original"), 10);
        return result;
    }

    private static IReadOnlyList<ImageCandidate> BuildCompanyImages(string? logoPath, TmdbImagesResponse? images) {
        var result = MapImages("logo", images?.Logos, "w500").ToList();
        AddFallback(result, "logo", TmdbMetadataHelpers.ImageUrl(logoPath, "w500"), 10);
        return result;
    }

    private static IReadOnlyList<ImageCandidate> BuildEpisodeImages(TmdbEpisode episode) {
        var result = MapImages("still", episode.Images?.Stills, "original").ToList();
        AddFallback(result, "still", TmdbMetadataHelpers.ImageUrl(episode.StillPath, "original"), 10);
        return result;
    }

    private static IEnumerable<ImageCandidate> MapImages(string kind, IReadOnlyList<TmdbImageEntry>? entries, string size) =>
        (entries ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FilePath))
            .Select(entry => new ImageCandidate(
                kind,
                TmdbMetadataHelpers.ImageUrl(entry.FilePath, size)!,
                "tmdb",
                entry.VoteAverage ?? 0,
                entry.Language,
                entry.Width,
                entry.Height));

    private static void AddFallback(List<ImageCandidate> images, string kind, string? url, decimal rank) {
        if (url is null || images.Any(image => image.Url == url)) {
            return;
        }

        images.Add(new ImageCandidate(kind, url, "tmdb", rank, null, null, null));
    }
}
