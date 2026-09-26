using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Arr;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr.Tests;

public sealed class SonarrDiscoveryTests {
    [Fact]
    public async Task SearchAndExactReviewPreserveSeriesIdentitiesArtworkAndMetadataWithoutTargetsOrWrites() {
        using var fixture = new Fixture();

        var probe = Assert.IsType<ProbeResult>(await fixture.Call(IntegrationOperations.Probe, new { }));
        Assert.Contains(ManagerDiscovery.Search,
            probe.Capabilities.Single(capability => capability.Kind == ManagerProtocol.ExternalManager).Operations);

        var page = Assert.IsType<ManagedDiscoveryPage>(await fixture.Call(
            ManagerDiscovery.Search,
            new ManagedDiscoveryQuery(ManagerProtocol.Series, "Breaking Bad", 10)));
        var result = Assert.Single(page.Items);
        Assert.Equal("81189", result.ExternalIds[ManagerProtocol.Tvdb]);
        Assert.Equal("1396", result.ExternalIds[ManagerProtocol.Tmdb]);
        Assert.Equal("tt0903747", result.ExternalIds[ManagerProtocol.Imdb]);
        Assert.Equal("AMC", result.Metadata!.Studio);
        Assert.Equal(9.5m, result.Metadata.Rating);
        Assert.Equal("2008-01-20", result.Metadata.Dates![ManagerDiscoveryDates.FirstAir]);
        Assert.Equal("https://art.example.test/poster.jpg", result.Metadata.PosterUrl);

        var lookup = Assert.IsType<ManagedLookupResult>(await fixture.Call(
            ManagerCreation.Lookup,
            new ManagedLookupInput(ManagerProtocol.Series,
                new Dictionary<string, string> { [ManagerProtocol.Tvdb] = "81189" })));
        Assert.Equal("81189", lookup.Candidate.ExternalIds[ManagerProtocol.Tvdb]);
        Assert.Equal("https://art.example.test/fanart.jpg", lookup.Candidate.Metadata!.BackdropUrl);
        Assert.Null(lookup.Targets);
        Assert.Null(lookup.Existing);
        Assert.Contains(fixture.Requests, request => request.RequestUri!.PathAndQuery.Contains("term=tvdb%3A81189"));
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task SearchDeduplicatesCanonicalTvdbIdentityBeforeApplyingTheLimit() {
        using var fixture = new Fixture { DuplicateLookup = true };

        var page = Assert.IsType<ManagedDiscoveryPage>(await fixture.Call(
            ManagerDiscovery.Search,
            new ManagedDiscoveryQuery(ManagerProtocol.Series, "Breaking Bad", 1)));

        Assert.Single(page.Items);
    }

    private sealed class Fixture : HttpMessageHandler {
        private readonly ConnectionContext connection = new(Guid.NewGuid(), "https://manager.test/", null,
            new Dictionary<string, string>(), new Dictionary<string, string> { [ArrClient.ApiKey] = "secret" });
        private readonly ArrClient client;
        private readonly SonarrLibrary adapter;
        internal bool DuplicateLookup { get; init; }
        internal List<HttpRequestMessage> Requests { get; } = [];

        internal Fixture() { client = new(connection, this); adapter = new(client); }
        internal Task<object> Call(string operation, object input) => adapter.DispatchAsync(new(
            IntegrationProtocol.Name, IntegrationProtocol.Version, Guid.NewGuid(), operation, connection,
            JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            Requests.Add(request);
            var path = request.RequestUri!.PathAndQuery.Split("/api/v3/")[1];
            object value = path switch {
                "system/status" => new { appName = "Sonarr", version = "4.0.17.2952" },
                "series/lookup?term=Breaking%20Bad" => DuplicateLookup ? new[] { Series(), Series() } : [Series()],
                "series/lookup?term=tvdb%3A81189" => new[] { Series() },
                "series?tvdbId=81189" => Array.Empty<object>(),
                _ => throw new InvalidOperationException("Unexpected request: " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(value, IntegrationProtocol.Json))
            });
        }

        private static object Series() => new {
            id = 0, title = "Breaking Bad", year = 2008, tvdbId = 81189, tmdbId = 1396,
            imdbId = "tt0903747", monitored = false, qualityProfileId = 0, path = "",
            overview = "A chemistry teacher builds a criminal empire.", network = "AMC",
            certification = "TV-MA", runtime = 48, genres = new[] { "Crime", "Drama" },
            firstAired = "2008-01-20T00:00:00Z", lastAired = "2013-09-29T00:00:00Z",
            ratings = new { value = 9.5m },
            images = new[] {
                new { coverType = "poster", remoteUrl = "https://art.example.test/poster.jpg" },
                new { coverType = "fanart", remoteUrl = "https://art.example.test/fanart.jpg" },
            }
        };

        public new void Dispose() { client.Dispose(); base.Dispose(); }
    }
}
