using System.Text.Json.Serialization;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

/// <summary>Sonarr series holdings with exact episode-to-file associations, including specials and combined episodes.</summary>
internal sealed partial class SonarrLibrary(ArrClient client) : ArrLibrary(client, "Sonarr", 4, ManagerProtocol.Series) {
    protected override async Task<IReadOnlyList<ManagedLibraryItem>> ListAsync(CancellationToken cancellationToken) =>
        (await Client.GetAsync<Series[]>("series", cancellationToken)).Select(Summary).ToArray();
    protected override async Task<ManagedItemSnapshot> GetAsync(int id, CancellationToken cancellationToken) {
        var series = await Client.GetOptionalAsync<Series>($"series/{id}", cancellationToken)
            ?? throw new ArrHoldingNotFoundException();
        if (series.Id != id) throw new IntegrationFailure("The application returned another series.");
        var files = await Client.GetAsync<EpisodeFile[]>($"episodefile?seriesId={id}", cancellationToken);
        var episodes = await Client.GetAsync<Episode[]>($"episode?seriesId={id}", cancellationToken);
        if (files.Length > 10000 || episodes.Length > 100000 || files.Any(file => file.SeriesId != id || file.Size <= 0)
            || episodes.Any(episode => episode.SeriesId != id) || episodes.Select(episode => episode.Id).Distinct().Count() != episodes.Length)
            throw new IntegrationFailure("The series returned inconsistent or excessive file associations.");
        var byFile = episodes.Where(episode => episode.EpisodeFileId > 0).ToLookup(episode => episode.EpisodeFileId);
        var fileIds = files.Select(file => file.Id).ToHashSet();
        if (files.Any(file => !byFile.Contains(file.Id)) || episodes.Any(episode => episode.HasFile != (episode.EpisodeFileId > 0)
            || episode.HasFile && !fileIds.Contains(episode.EpisodeFileId)))
            throw new IntegrationFailure("Episode file associations changed during the read. Refresh the series.");
        var mapped = files.Select(file => new ManagedLibraryFile(Id(file.Id), file.Path, file.Size, file.DateAdded,
            byFile[file.Id].OrderBy(episode => episode.SeasonNumber).ThenBy(episode => episode.EpisodeNumber).Select(episode =>
                new ManagedFileTarget(Id(episode.Id), ManagerProtocol.Episode, episode.Title, episode.SeasonNumber, episode.EpisodeNumber, episode.AbsoluteEpisodeNumber)).ToArray())).ToArray();
        return new(Summary(series) with { RemoteFileCount = files.Length }, series.Path, mapped, DateTimeOffset.UtcNow);
    }
    private ManagedLibraryItem Summary(Series series) => new(Id(series.Id), Kind, series.Title, series.Year,
        SeriesIdentities(series), series.Monitored, Id(series.QualityProfileId), series.Statistics?.EpisodeFileCount,
        ArrPresentation.Map(series.Overview, series.Images, series.Genres, series.Runtime, series.Certification));
    private static Dictionary<string, string> SeriesIdentities(Series series) {
        var identities = Identities(ManagerProtocol.Tvdb, series.TvdbId, series.ImdbId);
        if (series.TmdbId > 0) identities[ManagerProtocol.Tmdb] = Id(series.TmdbId);
        return identities;
    }
    private sealed record Series(int Id, string Title, int Year, int TvdbId, int TmdbId, string? ImdbId, [property: JsonRequired] bool Monitored, int QualityProfileId, string Path, SeriesStatistics? Statistics,
        string? Overview = null, ArrImage?[]? Images = null, string[]? Genres = null, int? Runtime = null, string? Certification = null);
    private sealed record SeriesStatistics(int EpisodeFileCount);
    private sealed record EpisodeFile(int Id, int SeriesId, string Path, long Size, DateTimeOffset? DateAdded);
    private sealed record Episode(int Id, int SeriesId, int TvdbId, string Title, int SeasonNumber, int EpisodeNumber, int? AbsoluteEpisodeNumber, int EpisodeFileId, bool HasFile,
        [property: JsonRequired] bool Monitored);
}
