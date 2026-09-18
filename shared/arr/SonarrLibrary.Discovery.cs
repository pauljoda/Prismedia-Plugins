using System.Globalization;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

internal sealed partial class SonarrLibrary {
    private async Task<ManagedDiscoveryPage> DiscoverAsync(ManagedDiscoveryQuery input, CancellationToken token) {
        if (input?.EntityKind != ManagerProtocol.Series || string.IsNullOrWhiteSpace(input.Query)
            || input.Query.Length > 512 || input.Query.Any(char.IsControl) || input.Limit is < 1 or > 100)
            throw new IntegrationFailure("Enter a series title up to 512 characters and a result limit from 1 to 100.");
        var results = await Client.GetAsync<Series[]>($"series/lookup?term={Uri.EscapeDataString(input.Query.Trim())}", token);
        if (results.Length > 1000) throw new IntegrationFailure("The manager returned excessive series lookup results.");
        var candidates = results
            .Select(DiscoveryCandidate)
            .GroupBy(item => item.ExternalIds[ManagerProtocol.Tvdb], StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(input.Limit)
            .ToArray();
        return new(candidates);
    }

    private static ManagedDiscoveryCandidate DiscoveryCandidate(Series series) {
        if (string.IsNullOrWhiteSpace(series.Title) || series.Title.Length > 512 || series.Year is < 0 or > 9999
            || series.TvdbId <= 0) throw new IntegrationFailure("The manager returned invalid series discovery metadata.");
        return new(ManagerProtocol.Series, series.Title, series.Year, SeriesIdentities(series), DiscoveryMetadata(series));
    }

    private static ManagedDiscoveryMetadata DiscoveryMetadata(Series series) {
        var presentation = ArrPresentation.Map(
            series.Overview,
            series.Images,
            series.Genres,
            series.Runtime,
            series.Certification);
        var dates = new Dictionary<string, string>();
        AddDate(dates, ManagerDiscoveryDates.FirstAir, series.FirstAired);
        AddDate(dates, ManagerDiscoveryDates.LastAir, series.LastAired);
        return new(
            Overview: presentation?.Overview,
            Studio: Text(series.Network, 512),
            Classification: presentation?.ContentRating,
            RuntimeMinutes: presentation?.RuntimeMinutes,
            Rating: series.Ratings?.Value is >= 0 and <= 10 ? series.Ratings.Value : null,
            Tags: presentation?.Genres,
            Dates: dates.Count == 0 ? null : dates,
            PosterUrl: presentation?.PosterUrl,
            BackdropUrl: presentation?.BackdropUrl);
    }

    private static void AddDate(IDictionary<string, string> dates, string kind, string? value) {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            dates[kind] = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string? Text(string? value, int limit) => string.IsNullOrWhiteSpace(value) || value.Length > limit
        || value.Any(char.IsControl) ? null : value.Trim();
}
