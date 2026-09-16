using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Arr;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr.Tests;

public sealed class ManagerLibraryTests {
    [Fact]
    public async Task RadarrPagesExistingHoldingsWithoutClaimsOfPersistentIdentityOrMutations() {
        using var fixture = new Fixture();
        fixture.Responses["movie"] = new[] { Movie(2), Movie(1) };
        var probe = Assert.IsType<ProbeResult>(await fixture.Call(IntegrationOperations.Probe, new { }));
        Assert.Null(probe.InstanceId);
        var page = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.Movie, null, null, 1)));
        Assert.Equal("1", Assert.Single(page.Items).RemoteId);
        Assert.Equal("1001", page.Items[0].ExternalIds[ManagerProtocol.Tmdb]);
        var second = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.Movie, null, page.NextCursor, 1)));
        Assert.Equal("2", Assert.Single(second.Items).RemoteId);
        Assert.Null(second.NextCursor);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.Movie, "changed query", page.NextCursor, 1)));
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task RemoteItemNumberCannotSubstituteAnotherMetadataIdentity() {
        using var fixture = new Fixture();
        fixture.Responses["movie/1"] = Movie(1);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem,
            new ManagedItemInput(ManagerProtocol.Movie, "1", new Dictionary<string, string> { [ManagerProtocol.Tmdb] = "9999" })));
    }

    [Fact]
    public async Task MissingMovieFileRemainsMissingAndExactFileAssociationsAreRequired() {
        using var fixture = new Fixture();
        fixture.Responses["movie/1"] = Movie(1);
        var input = new ManagedItemInput(ManagerProtocol.Movie, "1", new Dictionary<string, string> { [ManagerProtocol.Tmdb] = "1001" });
        var snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, input));
        Assert.Empty(snapshot.Files);
        fixture.Responses["movie/1"] = Movie(1, true);
        fixture.Responses["moviefile/11"] = new { id = 11, movieId = 2, path = "/library/movie.mkv", size = 128 };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, input));
        fixture.Responses["moviefile/11"] = new { id = 11, movieId = 1, path = "/library/movie.mkv", size = 128 };
        snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, input));
        Assert.Equal("1", Assert.Single(Assert.Single(snapshot.Files).Targets).RemoteId);
    }

    [Fact]
    public async Task SonarrRetainsCombinedEpisodesSpecialsAndAbsoluteNumbers() {
        using var fixture = new Fixture(sonarr: true);
        fixture.Responses["series/1"] = new { id = 1, title = "Series", year = 2000, tvdbId = 1001, qualityProfileId = 1, monitored = true, path = "/library/series" };
        fixture.Responses["episodefile?seriesId=1"] = new[] { new { id = 10, seriesId = 1, path = "/library/series/combined.mkv", size = 128 } };
        fixture.Responses["episode?seriesId=1"] = new[] { Episode(1, 0, 1, 10), Episode(2, 0, 2, 10) };
        var input = new ManagedItemInput(ManagerProtocol.Series, "1", new Dictionary<string, string> { [ManagerProtocol.Tvdb] = "1001" });
        var snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, input));
        var file = Assert.Single(snapshot.Files);
        Assert.Equal(2, file.Targets.Count);
        Assert.All(file.Targets, target => Assert.Equal(0, target.SeasonNumber));
        Assert.Equal(2, file.Targets[1].AbsoluteNumber);
        fixture.Responses["episodefile?seriesId=1"] = Array.Empty<object>();
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, input));
    }

    [Fact]
    public async Task SonarrDoesNotAdvertiseOrExecuteUnimplementedControls() {
        using var fixture = new Fixture(sonarr: true);
        var probe = Assert.IsType<ProbeResult>(await fixture.Call(IntegrationOperations.Probe, new { }));
        var manager = Assert.Single(probe.Capabilities, capability => capability.Kind == ManagerProtocol.ExternalManager);
        Assert.Equal(ManagerProtocol.Options, Assert.Single(manager.Operations));
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerControls.Configure, new { }));
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task WrongApplicationOrUnsupportedServerVersionIsRejected() {
        using var fixture = new Fixture();
        fixture.Responses["system/status"] = new { appName = "Sonarr", version = "4.0.17.2952" };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
        fixture.Responses["system/status"] = new { appName = "Radarr", version = "7.0.0.0" };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
    }

    [Fact]
    public async Task TransportPreservesProxyPrefixAndRejectsRedirectsWithoutLeakingAuth() {
        using var fixture = new Fixture();
        fixture.Status = HttpStatusCode.Redirect;
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
        var request = Assert.Single(fixture.Requests);
        Assert.Equal("http://manager.test/radarr/api/v3/system/status", request.RequestUri!.AbsoluteUri);
        Assert.Equal("test-secret", Assert.Single(request.Headers.GetValues("X-Api-Key")));
    }

    private static object Movie(int id, bool hasFile = false) => new { id, title = "Movie " + id, year = 2024, tmdbId = 1000 + id, qualityProfileId = 1, monitored = false, hasFile, movieFileId = hasFile ? 11 : 0, path = "/library/movie" };
    private static object Episode(int id, int season, int number, int fileId) => new { id, seriesId = 1, title = "Episode " + id, seasonNumber = season, episodeNumber = number, absoluteEpisodeNumber = number, episodeFileId = fileId, hasFile = true };

    private sealed class Fixture : HttpMessageHandler {
        internal Dictionary<string, object> Responses { get; } = [];
        internal List<HttpRequestMessage> Requests { get; } = [];
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        private readonly ConnectionContext connection = new(Guid.NewGuid(), "http://manager.test/radarr/", null, new Dictionary<string, string>(), new Dictionary<string, string> { [ArrClient.ApiKey] = "test-secret" });
        private readonly ArrClient client;
        private readonly ArrLibrary library;
        internal Fixture(bool sonarr = false) {
            Responses["system/status"] = new { appName = sonarr ? "Sonarr" : "Radarr", version = sonarr ? "4.0.17.2952" : "6.1.1.10360" };
            client = new(connection, this);
            library = sonarr ? new SonarrLibrary(client) : new RadarrLibrary(client);
        }
        internal Task<object> Call(string operation, object input) => library.DispatchAsync(new(IntegrationProtocol.Name, IntegrationProtocol.Version,
            Guid.NewGuid(), operation, connection, JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(request);
            var path = request.RequestUri!.PathAndQuery.Split("/api/v3/")[1];
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(JsonSerializer.Serialize(Responses[path], IntegrationProtocol.Json)) });
        }
    }
}
