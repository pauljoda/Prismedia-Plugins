using Prismedia.Plugin.Integrations;
using System.Text.Json.Serialization;

namespace Prismedia.Plugin.Arr;

/// <summary>Radarr movie holdings; file presence is verified through the referenced movie-file endpoint.</summary>
internal sealed partial class RadarrLibrary(ArrClient client) : ArrLibrary(client, "Radarr", 6, ManagerProtocol.Movie) {
    protected override async Task<IReadOnlyList<ManagedLibraryItem>> ListAsync(CancellationToken cancellationToken) =>
        (await Client.GetAsync<Movie[]>("movie", cancellationToken)).Select(Summary).ToArray();
    protected override async Task<ManagedItemSnapshot> GetAsync(int id, CancellationToken cancellationToken) {
        var movie = await Client.GetOptionalAsync<Movie>($"movie/{id}", cancellationToken)
            ?? throw new ArrHoldingNotFoundException();
        if (movie.Id != id) throw new IntegrationFailure("The application returned another movie.");
        var files = new List<ManagedLibraryFile>();
        if (movie.HasFile) {
            var file = await Client.GetAsync<MovieFile>($"moviefile/{Id(movie.MovieFileId)}", cancellationToken);
            if (file.MovieId != id || file.Id != movie.MovieFileId || file.Size <= 0 || string.IsNullOrWhiteSpace(file.Path))
                throw new IntegrationFailure("The movie's file association changed. Refresh its status.");
            files.Add(new(Id(file.Id), file.Path, file.Size, file.DateAdded, [new(Id(id), Kind, movie.Title)]));
        }
        return new(Summary(movie), movie.Path, files, DateTimeOffset.UtcNow);
    }
    private ManagedLibraryItem Summary(Movie movie) => new(Id(movie.Id), Kind, movie.Title, movie.Year,
        Identities(ManagerProtocol.Tmdb, movie.TmdbId, movie.ImdbId), movie.Monitored, Id(movie.QualityProfileId), movie.HasFile ? 1 : 0,
        ArrPresentation.Map(movie.Overview, movie.Images, movie.Genres, movie.Runtime, movie.Certification));
    private sealed record Movie(int Id, string Title, int Year, int TmdbId, string? ImdbId,
        [property: JsonRequired] bool Monitored, int QualityProfileId, [property: JsonRequired] bool HasFile, int MovieFileId, string Path,
        string? Overview = null, ArrImage?[]? Images = null, string[]? Genres = null, int? Runtime = null, string? Certification = null,
        string? OriginalTitle = null, string? Studio = null, string? Website = null, string? InCinemas = null,
        string? DigitalRelease = null, string? PhysicalRelease = null, MovieRatings? Ratings = null);
    private sealed record MovieFile(int Id, int MovieId, string Path, long Size, DateTimeOffset? DateAdded);
}
