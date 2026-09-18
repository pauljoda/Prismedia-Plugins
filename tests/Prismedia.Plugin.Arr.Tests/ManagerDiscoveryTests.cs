using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Arr;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr.Tests;

public sealed class ManagerDiscoveryTests {
    [Fact]
    public async Task SearchAndExactReviewReturnRichMetadataWithoutWriting() {
        using var fixture = new Fixture();

        var page = Assert.IsType<ManagedDiscoveryPage>(await fixture.Call(
            ManagerDiscovery.Search,
            new ManagedDiscoveryQuery(ManagerProtocol.Movie, "Metropolis", 10)));
        var result = Assert.Single(page.Items);
        Assert.Equal("19", result.ExternalIds[ManagerProtocol.Tmdb]);
        Assert.Equal("Fritz Lang", result.Metadata!.Studio);
        Assert.Equal("https://images.example.test/poster.jpg", result.Metadata.PosterUrl);

        var lookup = Assert.IsType<ManagedLookupResult>(await fixture.Call(
            ManagerCreation.Lookup,
            new ManagedLookupInput(ManagerProtocol.Movie, new Dictionary<string, string> { [ManagerProtocol.Tmdb] = "19" })));
        Assert.Equal("Fritz Lang", lookup.Candidate.Metadata!.Studio);
        Assert.Equal("1927-01-10", lookup.Candidate.Metadata.Dates![ManagerDiscoveryDates.TheatricalRelease]);
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    private sealed class Fixture : HttpMessageHandler {
        private readonly ConnectionContext connection = new(Guid.NewGuid(), "https://manager.test/", null,
            new Dictionary<string, string>(), new Dictionary<string, string> { [ArrClient.ApiKey] = "secret" });
        private readonly ArrClient client;
        private readonly RadarrLibrary adapter;
        internal List<HttpRequestMessage> Requests { get; } = [];

        internal Fixture() { client = new(connection, this); adapter = new(client); }
        internal Task<object> Call(string operation, object input) => adapter.DispatchAsync(new(
            IntegrationProtocol.Name, IntegrationProtocol.Version, Guid.NewGuid(), operation, connection,
            JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            Requests.Add(request);
            var path = request.RequestUri!.PathAndQuery.Split("/api/v3/")[1];
            object value = path switch {
                "system/status" => new { appName = "Radarr", version = "6.1.1.10360" },
                "movie/lookup?term=Metropolis" => new[] { Movie() },
                "movie?tmdbId=19" => Array.Empty<object>(),
                "movie/lookup/tmdb?tmdbId=19" => Movie(),
                _ => throw new InvalidOperationException("Unexpected request: " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(value, IntegrationProtocol.Json))
            });
        }

        private static object Movie() => new {
            title = "Metropolis", originalTitle = "Metropolis", year = 1927, tmdbId = 19, imdbId = "tt0017136",
            overview = "A city divided.", studio = "Fritz Lang", certification = "PG", runtime = 149,
            genres = new[] { "Science Fiction" }, inCinemas = "1927-01-10T00:00:00Z",
            website = "https://example.test/metropolis", ratings = new { tmdb = new { value = 8.1m } },
            images = new[] { new { coverType = "poster", remoteUrl = "https://images.example.test/poster.jpg" } }
        };

        public new void Dispose() { client.Dispose(); base.Dispose(); }
    }
}
