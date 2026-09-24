using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Arr;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr.Tests;

public sealed class ManagerCreationTests {
    private static ManagedLookupInput Work() => new(ManagerProtocol.Movie, new Dictionary<string, string> { [ManagerProtocol.Tmdb] = "1001" });
    private static EnsureManagedInput Intent() => new(Guid.NewGuid(), Work(), "2", "3", "/library");

    [Fact]
    public async Task LookupUsesExactIdentityAndNeverWrites() {
        using var fixture = new Fixture();
        var result = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, Work()));
        Assert.Equal("Film", result.Candidate.Title);
        Assert.Equal("1001", result.Candidate.ExternalIds[ManagerProtocol.Tmdb]);
        Assert.Null(result.Existing);
        Assert.DoesNotContain(fixture.Reads, path => path.StartsWith("credit?", StringComparison.Ordinal));
        Assert.Empty(fixture.Writes);
    }
    [Fact]
    public async Task ExistingLookupUsesLocalHoldingWithoutUpstreamMetadataLookup() {
        using var fixture = new Fixture { Exists = true };
        var result = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, Work()));
        Assert.NotNull(result.Existing);
        Assert.DoesNotContain("movie/lookup/tmdb?tmdbId=1001", fixture.Reads);
        Assert.Contains("credit?movieId=1", fixture.Reads);
        Assert.Empty(fixture.Writes);
    }
    [Fact]
    public async Task ExistingLookupNormalizesPeopleCreditsWithoutImportingUnmappedCrew() {
        using var fixture = new Fixture { Exists = true };

        var result = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, Work()));

        var credits = result.Candidate.Metadata!.Credits!;
        var actor = Assert.Single(credits, credit => credit.Name == "Lead Actor");
        Assert.Equal("Lead Actor", actor.Name);
        Assert.Equal("Hero", actor.Character);
        Assert.Equal("101", actor.ExternalIds![ManagerProtocol.Tmdb]);
        Assert.Equal("https://image.tmdb.org/t/p/original/lead.jpg", actor.ProfileUrl);
        var director = Assert.Single(credits, credit => credit.Role == ManagerCreditRoles.Director);
        Assert.Equal("Director Person", director.Name);
        Assert.True(director.SortOrder >= 1000);
        var unidentified = Assert.Single(credits, credit => credit.Name == "Unidentified Actor");
        Assert.Null(unidentified.ExternalIds);
        Assert.NotNull(unidentified.SortOrder);
        Assert.InRange(unidentified.SortOrder.Value, 0, 1_000_000);
        Assert.DoesNotContain(credits, credit => credit.Name == "Camera Operator");
    }
    [Fact]
    public async Task NewMovieIsAddedUnmonitoredWithoutSearchOrCollectionMonitoring() {
        using var fixture = new Fixture();
        var result = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, Intent()));
        Assert.Equal(ManagerControls.Applied, result.Outcome);
        Assert.True(result.Created);
        Assert.False(result.Holding!.Item.Monitored);
        Assert.Empty(result.Holding.Files);
        var body = Assert.Single(fixture.Writes);
        Assert.Equal(1001, body.GetProperty("tmdbId").GetInt32());
        Assert.Equal(2, body.GetProperty("qualityProfileId").GetInt32());
        Assert.Equal("/library", body.GetProperty("rootFolderPath").GetString());
        Assert.False(body.GetProperty("monitored").GetBoolean());
        Assert.False(body.GetProperty("addOptions").GetProperty("searchForMovie").GetBoolean());
        Assert.Equal(ArrCreation.Unmonitored, body.GetProperty("addOptions").GetProperty("monitor").GetString());
        Assert.Equal(ArrCreation.Released, body.GetProperty("minimumAvailability").GetString());
        Assert.False(body.TryGetProperty("path", out _));
    }
    [Fact]
    public async Task ExistingHoldingRetainsItsPathProfileAndMonitoring() {
        using var fixture = new Fixture { Exists = true, Monitored = true, Profile = 7, MoviePath = "/elsewhere/film" };
        var result = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, Intent()));
        Assert.Equal(ManagerControls.Applied, result.Outcome);
        Assert.False(result.Created);
        Assert.Equal("7", result.Holding!.Item.ProfileId);
        Assert.True(result.Holding.Item.Monitored);
        Assert.Equal("/elsewhere/film", result.Holding.Path);
        Assert.Empty(fixture.Writes);
    }
    [Fact]
    public async Task LostCreationResponseIsRecoveredByReadOnlyExactLookup() {
        using var fixture = new Fixture { LoseResponse = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Call(ManagerCreation.Ensure, Intent()));
        var observed = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, Work()));
        Assert.NotNull(observed.Existing);
        Assert.Single(fixture.Writes);
    }
    [Fact]
    public async Task ReplayingEnsureOnExistingIdentityDoesNotAddTwice() {
        using var fixture = new Fixture();
        var intent = Intent();
        await fixture.Call(ManagerCreation.Ensure, intent);
        var replay = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, intent));
        Assert.False(replay.Created);
        Assert.Single(fixture.Writes);
    }
    [Fact]
    public async Task InvalidProfileRootOrChangedRootPathRejectBeforeCreation() {
        using var fixture = new Fixture();
        foreach (var input in new[] { Intent() with { ProfileId = "999" }, Intent() with { RootId = "999" }, Intent() with { ExpectedRootPath = "/old" }, Intent() with { OperationId = Guid.Empty } }) {
            var result = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, input));
            Assert.Equal(ManagerControls.Rejected, result.Outcome);
        }
        Assert.Empty(fixture.Writes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedMetadataIdentityNeverCreatesOrAdopts(bool existing) {
        using var fixture = new Fixture { Exists = existing, ReturnedTmdb = 999 };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Lookup, Work()));
        Assert.Equal(ManagerControls.Rejected, Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, Intent())).Outcome);
        Assert.Empty(fixture.Writes);
    }
    [Fact]
    public async Task MissingOrUnknownIdentityIsNotTreatedAsATitleSearch() {
        using var fixture = new Fixture();
        foreach (var work in new[] { new ManagedLookupInput(ManagerProtocol.Movie, new Dictionary<string,string>()), Work() with { ExternalIds = new Dictionary<string,string> { [ManagerProtocol.Tvdb] = "1001" } } })
            await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Lookup, work));
        Assert.Empty(fixture.Writes);
    }
    [Fact]
    public async Task UnreadableRootRejectsBeforeCreation() {
        using var fixture = new Fixture { Accessible = false };
        Assert.Equal(ManagerControls.Rejected, Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, Intent())).Outcome);
        Assert.Empty(fixture.Writes);
    }
    [Fact]
    public async Task PreconditionReadFailureBeforeCreationStaysUncertain() {
        using var fixture = new Fixture { FailingRead = "rootfolder" };
        var error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Ensure, Intent()));
        Assert.Equal("The connected application returned HTTP 500.", error.Message);
        Assert.Empty(fixture.Writes);
    }
    [Fact]
    public async Task DefiniteCreationRefusalIsRejectedWithItsProblem() {
        using var fixture = new Fixture { WriteStatus = HttpStatusCode.BadRequest };
        var result = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, Intent()));
        Assert.Equal(ManagerControls.Rejected, result.Outcome);
        Assert.Equal("The connected application rejected the request (HTTP 400).", result.Problem);
        Assert.Single(fixture.Writes);
    }
    [Fact]
    public async Task DifferentAcknowledgedIdentityRemainsUncertainAfterWrite() {
        using var fixture = new Fixture { ChangeIdentityAfterWrite = true };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Ensure, Intent()));
        Assert.Single(fixture.Writes);
    }
    private sealed class Fixture : HttpMessageHandler {
        internal bool Exists, Monitored, LoseResponse, ChangeIdentityAfterWrite;
        internal bool Accessible = true;
        internal int Profile = 2, ReturnedTmdb = 1001;
        internal string MoviePath = "/library/film";
        internal string? FailingRead;
        internal HttpStatusCode? WriteStatus;
        internal List<JsonElement> Writes { get; } = [];
        internal List<string> Reads { get; } = [];
        internal object?[] Credits { get; set; } = [
            null,
            new { personName = "Lead Actor", personTmdbId = 101, type = "cast", character = "Hero", order = 0,
                images = new[] { new { coverType = "headshot", remoteUrl = "https://image.tmdb.org/t/p/original/lead.jpg", url = "/MediaCover/101/headshot.jpg" } } },
            new { personName = "Unidentified Actor", personTmdbId = -1, type = "cast", character = "Extra", order = int.MaxValue, images = Array.Empty<object>() },
            new { personName = "Director Person", personTmdbId = 202, type = "crew", job = "Director", order = 0, images = Array.Empty<object>() },
            new { personName = "Camera Operator", personTmdbId = 303, type = "crew", job = "Camera Operator", order = 1, images = Array.Empty<object>() }
        ];
        private readonly ConnectionContext connection = new(Guid.NewGuid(), "http://manager.test/", null, new Dictionary<string,string>(), new Dictionary<string,string> { [ArrClient.ApiKey] = "fixture-secret" });
        private readonly ArrClient client;
        private readonly RadarrLibrary adapter;
        internal Fixture() { client = new(connection, this); adapter = new(client); }
        internal Task<object> Call(string operation, object input) => adapter.DispatchAsync(new(IntegrationProtocol.Name, IntegrationProtocol.Version, Guid.NewGuid(), operation, connection, JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);
        private object Movie() => new { id = 1, title = "Film", year = 2024, tmdbId = ReturnedTmdb, qualityProfileId = Profile, monitored = Monitored, hasFile = false, movieFileId = 0, path = MoviePath };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            var path = request.RequestUri!.PathAndQuery.Split("/api/v3/")[1];
            if (request.Method == HttpMethod.Get) {
                Reads.Add(path);
                if (path == FailingRead) return new(HttpStatusCode.InternalServerError);
                return path switch {
                "system/status" => Response(new { appName = "Radarr", version = "6.1.1.10360" }),
                "movie?tmdbId=1001" => Response(Exists ? new[] { Movie() } : []),
                "movie/lookup/tmdb?tmdbId=1001" => Response(new { title = "Film", year = 2024, tmdbId = ReturnedTmdb }),
                "movie/1" => Response(Movie()),
                "credit?movieId=1" => Response(Credits),
                "qualityprofile" => Response(new[] { new { id = 2, name = "Chosen" } }),
                "rootfolder" => Response(new[] { new { id = 3, path = "/library", accessible = Accessible } }),
                _ => throw new InvalidOperationException("Unexpected read: " + path)
                };
            }
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("movie", path);
            Writes.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token)));
            if (WriteStatus is { } status) return new(status) { Content = new StringContent("{}") };
            Exists = true;
            if (ChangeIdentityAfterWrite) ReturnedTmdb = 999;
            if (LoseResponse) throw new HttpRequestException("Simulated accepted creation with lost response");
            return Response(new { id = 1 }, HttpStatusCode.Created);
        }
        private static HttpResponseMessage Response(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value, IntegrationProtocol.Json)) };
    }
}
