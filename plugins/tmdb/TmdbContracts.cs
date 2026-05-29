using System.Text.Json.Serialization;

namespace Prismedia.Plugin.Tmdb;

internal sealed record ScoredResult(TmdbSearchResult Result, decimal Score, int Order);

internal sealed record TmdbUrl(string MediaType, int Id);

internal sealed record TmdbSearchResponse(
    [property: JsonPropertyName("results")] TmdbSearchResult[]? Results);

internal sealed record TmdbSearchResult(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("media_type")] string? MediaType,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("poster_path")] string? PosterPath,
    [property: JsonPropertyName("profile_path")] string? ProfilePath,
    [property: JsonPropertyName("logo_path")] string? LogoPath,
    [property: JsonPropertyName("vote_average")] decimal? VoteAverage);

internal sealed record TmdbMovieDetail(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("original_title")] string? OriginalTitle,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("poster_path")] string? PosterPath,
    [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
    [property: JsonPropertyName("genres")] TmdbGenre[]? Genres,
    [property: JsonPropertyName("runtime")] int? Runtime,
    [property: JsonPropertyName("production_companies")] TmdbNamed[]? ProductionCompanies,
    [property: JsonPropertyName("credits")] TmdbCredits? Credits,
    [property: JsonPropertyName("images")] TmdbImagesResponse? Images);

internal sealed record TmdbTvDetail(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
    [property: JsonPropertyName("last_air_date")] string? LastAirDate,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("poster_path")] string? PosterPath,
    [property: JsonPropertyName("backdrop_path")] string? BackdropPath,
    [property: JsonPropertyName("genres")] TmdbGenre[]? Genres,
    [property: JsonPropertyName("number_of_seasons")] int? NumberOfSeasons,
    [property: JsonPropertyName("number_of_episodes")] int? NumberOfEpisodes,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("networks")] TmdbNamed[]? Networks,
    [property: JsonPropertyName("production_companies")] TmdbNamed[]? ProductionCompanies,
    [property: JsonPropertyName("seasons")] TmdbSeasonSummary[]? Seasons,
    [property: JsonPropertyName("credits")] TmdbCredits? Credits,
    [property: JsonPropertyName("images")] TmdbImagesResponse? Images);

internal sealed record TmdbSeasonSummary(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("season_number")] int SeasonNumber,
    [property: JsonPropertyName("episode_count")] int EpisodeCount,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("air_date")] string? AirDate,
    [property: JsonPropertyName("poster_path")] string? PosterPath);

internal sealed record TmdbSeasonDetail(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("season_number")] int? SeasonNumber,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("air_date")] string? AirDate,
    [property: JsonPropertyName("poster_path")] string? PosterPath,
    [property: JsonPropertyName("episodes")] TmdbEpisode[] Episodes);

internal sealed record TmdbEpisode(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("episode_number")] int EpisodeNumber,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("overview")] string? Overview,
    [property: JsonPropertyName("air_date")] string? AirDate,
    [property: JsonPropertyName("still_path")] string? StillPath,
    [property: JsonPropertyName("runtime")] int? Runtime,
    [property: JsonPropertyName("vote_average")] decimal? VoteAverage,
    [property: JsonPropertyName("guest_stars")] TmdbCast[]? GuestStars,
    [property: JsonPropertyName("crew")] TmdbCrew[]? Crew,
    [property: JsonPropertyName("images")] TmdbEpisodeImagesResponse? Images = null);

internal sealed record TmdbGenre(
    [property: JsonPropertyName("name")] string Name);

internal sealed record TmdbNamed(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("logo_path")] string? LogoPath);

internal sealed record TmdbCredits(
    [property: JsonPropertyName("cast")] TmdbCast[]? Cast,
    [property: JsonPropertyName("crew")] TmdbCrew[]? Crew);

internal sealed record TmdbCast(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("character")] string? Character,
    [property: JsonPropertyName("order")] int? Order,
    [property: JsonPropertyName("profile_path")] string? ProfilePath);

internal sealed record TmdbCrew(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("job")] string Job,
    [property: JsonPropertyName("profile_path")] string? ProfilePath);

internal sealed record TmdbImagesResponse(
    [property: JsonPropertyName("posters")] TmdbImageEntry[]? Posters,
    [property: JsonPropertyName("backdrops")] TmdbImageEntry[]? Backdrops,
    [property: JsonPropertyName("logos")] TmdbImageEntry[]? Logos);

internal sealed record TmdbEpisodeImagesResponse(
    [property: JsonPropertyName("stills")] TmdbImageEntry[]? Stills);

internal sealed record TmdbImageEntry(
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("width")] int? Width,
    [property: JsonPropertyName("height")] int? Height,
    [property: JsonPropertyName("iso_639_1")] string? Language,
    [property: JsonPropertyName("vote_average")] decimal? VoteAverage);

internal sealed record TmdbProfileImagesResponse(
    [property: JsonPropertyName("profiles")] TmdbImageEntry[]? Profiles);

internal sealed record TmdbPersonDetail(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("biography")] string? Biography,
    [property: JsonPropertyName("profile_path")] string? ProfilePath,
    [property: JsonPropertyName("birthday")] string? Birthday,
    [property: JsonPropertyName("deathday")] string? Deathday,
    [property: JsonPropertyName("homepage")] string? Homepage,
    [property: JsonPropertyName("imdb_id")] string? ImdbId,
    [property: JsonPropertyName("place_of_birth")] string? PlaceOfBirth,
    [property: JsonPropertyName("known_for_department")] string? KnownForDepartment,
    [property: JsonPropertyName("popularity")] decimal? Popularity,
    [property: JsonPropertyName("images")] TmdbProfileImagesResponse? Images);

internal sealed record TmdbCompanyDetail(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("logo_path")] string? LogoPath,
    [property: JsonPropertyName("homepage")] string? Homepage,
    [property: JsonPropertyName("origin_country")] string? OriginCountry,
    [property: JsonPropertyName("images")] TmdbImagesResponse? Images);
