using System.Globalization;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

internal sealed partial class RadarrLibrary {
    private async Task<ManagedDiscoveryPage> DiscoverAsync(ManagedDiscoveryQuery input, CancellationToken token) {
        if (input?.EntityKind != ManagerProtocol.Movie || string.IsNullOrWhiteSpace(input.Query)
            || input.Query.Length > 512 || input.Query.Any(char.IsControl) || input.Limit is < 1 or > 100)
            throw new IntegrationFailure("Enter a movie title up to 512 characters and a result limit from 1 to 100.");
        var results = await Client.GetAsync<MovieLookup[]>($"movie/lookup?term={Uri.EscapeDataString(input.Query.Trim())}", token);
        if (results.Length > 1000) throw new IntegrationFailure("The manager returned excessive movie lookup results.");
        var candidates = results
            .Select(Candidate)
            .GroupBy(item => item.ExternalIds[ManagerProtocol.Tmdb], StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(input.Limit)
            .ToArray();
        return new(candidates);
    }

    private static ManagedDiscoveryCandidate Candidate(MovieLookup movie) {
        if (string.IsNullOrWhiteSpace(movie.Title) || movie.Title.Length > 512 || movie.Year is < 0 or > 9999
            || movie.TmdbId <= 0) throw new IntegrationFailure("The manager returned invalid movie discovery metadata.");
        return new(ManagerProtocol.Movie, movie.Title, movie.Year,
            Identities(ManagerProtocol.Tmdb, movie.TmdbId, movie.ImdbId), DiscoveryMetadata(movie));
    }

    private static ManagedDiscoveryMetadata DiscoveryMetadata(MovieLookup movie) {
        return DiscoveryMetadata(movie.OriginalTitle, movie.Overview, movie.Studio, movie.Certification,
            movie.Runtime, movie.Genres, movie.Images, movie.Website, movie.InCinemas, movie.DigitalRelease,
            movie.PhysicalRelease, movie.Ratings);
    }

    private static ManagedDiscoveryMetadata DiscoveryMetadata(
        Movie movie,
        IReadOnlyList<ManagedPersonCredit>? credits = null) {
        return DiscoveryMetadata(movie.OriginalTitle, movie.Overview, movie.Studio, movie.Certification,
            movie.Runtime, movie.Genres, movie.Images, movie.Website, movie.InCinemas, movie.DigitalRelease,
            movie.PhysicalRelease, movie.Ratings, credits);
    }

    private static ManagedDiscoveryMetadata DiscoveryMetadata(
        string? originalTitle, string? overview, string? studio, string? certification, int? runtime,
        string[]? genres, ArrImage?[]? images, string? website, string? inCinemas,
        string? digitalRelease, string? physicalRelease, MovieRatings? ratings,
        IReadOnlyList<ManagedPersonCredit>? credits = null) {
        var presentation = ArrPresentation.Map(overview, images, genres, runtime, certification);
        var dates = new Dictionary<string, string>();
        AddDate(dates, ManagerDiscoveryDates.TheatricalRelease, inCinemas);
        AddDate(dates, ManagerDiscoveryDates.DigitalRelease, digitalRelease);
        AddDate(dates, ManagerDiscoveryDates.PhysicalRelease, physicalRelease);
        var urls = PublicUrl(website) is { } publicWebsite ? new[] { publicWebsite } : [];
        return new(
            OriginalTitle: Text(originalTitle, 512),
            Overview: presentation?.Overview,
            Studio: Text(studio, 512),
            Classification: presentation?.ContentRating,
            RuntimeMinutes: presentation?.RuntimeMinutes,
            Rating: ratings?.Tmdb?.Value is >= 0 and <= 10 ? ratings.Tmdb.Value : null,
            Tags: presentation?.Genres,
            Dates: dates.Count == 0 ? null : dates,
            Urls: urls.Length == 0 ? null : urls,
            PosterUrl: presentation?.PosterUrl,
            BackdropUrl: presentation?.BackdropUrl,
            Credits: credits is { Count: > 0 } ? credits : null);
    }

    private static void AddDate(IDictionary<string, string> dates, string kind, string? value) {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            dates[kind] = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string? Text(string? value, int limit) => string.IsNullOrWhiteSpace(value) || value.Length > limit
        || value.Any(char.IsControl) ? null : value.Trim();

    private static string? PublicUrl(string? value) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return null;
        return uri.AbsoluteUri;
    }
}
