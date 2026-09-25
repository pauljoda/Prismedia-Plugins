using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Arr;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr.Tests;

public sealed class SonarrCreationTests {
    private static ManagedLookupTarget Target(int season = 0, int episode = 1, int? absolute = null, int? tvdb = 5001) =>
        new(ManagerProtocol.Episode, tvdb is { } id
            ? new Dictionary<string, string> { [ManagerProtocol.Tvdb] = id.ToString() }
            : new Dictionary<string, string>(), season, episode, absolute);
    private static ManagedLookupInput Work(params ManagedLookupTarget[] targets) =>
        new(ManagerProtocol.Series, new Dictionary<string, string> { [ManagerProtocol.Tvdb] = "1001" }, targets.Length == 0 ? [Target()] : targets);
    private static EnsureManagedInput Intent(params ManagedLookupTarget[] targets) =>
        new(Guid.NewGuid(), Work(targets), "2", "3", "/library");

    [Fact]
    public async Task LookupUsesExactTvdbOrTmdbTermsWithoutTitleFallback() {
        using var fixture = new Fixture();
        var byTvdb = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, Work()));
        Assert.Equal("Series", byTvdb.Candidate.Title);
        Assert.Equal("1001", byTvdb.Candidate.ExternalIds[ManagerProtocol.Tvdb]);
        Assert.Equal("2001", byTvdb.Candidate.ExternalIds[ManagerProtocol.Tmdb]);
        Assert.Null(byTvdb.Existing);
        Assert.Null(byTvdb.Targets);
        Assert.Contains("series/lookup?term=tvdb%3A1001", fixture.Reads, StringComparer.OrdinalIgnoreCase);

        var tmdbWork = Work() with { ExternalIds = new Dictionary<string, string> { [ManagerProtocol.Tmdb] = "2001" } };
        var byTmdb = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, tmdbWork));
        Assert.Equal("1001", byTmdb.Candidate.ExternalIds[ManagerProtocol.Tvdb]);
        Assert.Contains("series/lookup?term=tmdb%3A2001", fixture.Reads, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task ExistingSeriesResolvesCanonicalEpisodeIdsAndPreservesSettings() {
        using var fixture = new Fixture { Exists = true, Monitored = true, Profile = 7, SeriesPath = "/elsewhere/series" };
        fixture.Episodes = [fixture.Episode(11, 0, 1, 5, 5001), fixture.Episode(12, 1, 2, null, 5002)];
        var work = Work(Target(0, 1, absolute: null), Target(1, 2, absolute: 2, tvdb: 5002));

        var lookup = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, work));
        Assert.NotNull(lookup.Existing);
        Assert.Equal("7", lookup.Existing.Item.ProfileId);
        Assert.True(lookup.Existing.Item.Monitored);
        Assert.Equal("/elsewhere/series", lookup.Existing.Path);
        Assert.Equal(["11", "12"], lookup.Targets!.Select(target => target.RemoteId).ToArray());
        Assert.Equal(5, lookup.Targets![0].AbsoluteNumber);
        Assert.Null(lookup.Targets[1].AbsoluteNumber);

        var ensured = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure,
            new EnsureManagedInput(Guid.NewGuid(), work, "2", "3", "/library")));
        Assert.Equal(ManagerControls.Applied, ensured.Outcome);
        Assert.False(ensured.Created);
        Assert.Equal("/elsewhere/series", ensured.Holding!.Path);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task EpisodeIdentityCoordinatesAndUniqueRemoteIdsAreAllRequired() {
        using var mismatch = new Fixture { Exists = true };
        mismatch.Episodes = [mismatch.Episode(11, 0, 1, 1, 9999)];
        await Assert.ThrowsAsync<IntegrationFailure>(() => mismatch.Call(ManagerCreation.Lookup, Work(Target())));

        using var conflict = new Fixture { Exists = true };
        conflict.Episodes = [conflict.Episode(11, 0, 1, 9, 5001)];
        await Assert.ThrowsAsync<IntegrationFailure>(() => conflict.Call(ManagerCreation.Lookup, Work(Target(0, 1, absolute: 8))));

        using var duplicate = new Fixture { Exists = true };
        duplicate.Episodes = [duplicate.Episode(11, 0, 1, null, 5001)];
        await Assert.ThrowsAsync<IntegrationFailure>(() => duplicate.Call(ManagerCreation.Lookup, Work(Target(), Target())));
    }

    [Fact]
    public async Task NewSeriesIsAddedUnmonitoredWithoutSearchThenResolvesExactTargets() {
        using var fixture = new Fixture { CatalogReadyAfterWrite = true };
        var result = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, Intent()));

        Assert.Equal(ManagerControls.Applied, result.Outcome);
        Assert.True(result.Created);
        Assert.False(result.Holding!.Item.Monitored);
        Assert.Equal("1", Assert.Single(result.Targets!).RemoteId);
        var body = Assert.Single(fixture.Writes);
        Assert.Equal("series", body.Path);
        Assert.Equal("Series", body.Body.GetProperty("title").GetString());
        Assert.Equal(1001, body.Body.GetProperty("tvdbId").GetInt32());
        Assert.Equal(2, body.Body.GetProperty("qualityProfileId").GetInt32());
        Assert.Equal("/library", body.Body.GetProperty("rootFolderPath").GetString());
        Assert.False(body.Body.GetProperty("monitored").GetBoolean());
        Assert.Equal(ArrCreation.Unmonitored, body.Body.GetProperty("addOptions").GetProperty("monitor").GetString());
        Assert.False(body.Body.GetProperty("addOptions").GetProperty("searchForMissingEpisodes").GetBoolean());
        Assert.False(body.Body.GetProperty("addOptions").GetProperty("searchForCutoffUnmetEpisodes").GetBoolean());
        Assert.DoesNotContain(fixture.Writes, write => write.Path == "command");
    }

    [Fact]
    public async Task AcceptedAddWithLostResponseIsRecoveredOnlyByReadOnlyLookup() {
        using var fixture = new Fixture { LoseResponse = true, CatalogReadyAfterWrite = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Call(ManagerCreation.Ensure, Intent()));
        var recovered = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, Work()));
        Assert.NotNull(recovered.Existing);
        Assert.Single(recovered.Targets!);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task IncompletePostAddEpisodeCatalogStaysUncertainUntilLookupCanAdoptIt() {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Ensure, Intent()));
        Assert.True(fixture.Exists);
        Assert.Single(fixture.Writes);

        fixture.CatalogReady = true;
        var recovered = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, Work()));
        Assert.NotNull(recovered.Existing);
        Assert.Equal("1", Assert.Single(recovered.Targets!).RemoteId);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task DefiniteValidationFailuresRejectBeforeAnyAdd() {
        using var fixture = new Fixture();
        var invalid = new[] {
            Intent() with { OperationId = Guid.Empty },
            Intent() with { ProfileId = "999" },
            Intent() with { RootId = "999" },
            Intent() with { ExpectedRootPath = "/changed" },
            Intent() with { Work = Work() with { ExternalIds = new Dictionary<string, string> { [ManagerProtocol.Imdb] = "tt123" } } },
            Intent() with { Work = Work() with { Targets = [] } },
        };
        foreach (var intent in invalid) {
            var result = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, intent));
            Assert.Equal(ManagerControls.Rejected, result.Outcome);
        }
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task PreconditionReadFailureBeforeAddStaysUncertain() {
        using var fixture = new Fixture { FailingRead = "qualityprofile" };
        var error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Ensure, Intent()));
        Assert.Equal("The connected application returned HTTP 500.", error.Message);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task DifferentAcknowledgedIdentityOrSettingsRemainUncertainAfterWrite() {
        using var identity = new Fixture { CatalogReadyAfterWrite = true, ChangeIdentityAfterWrite = true };
        await Assert.ThrowsAsync<IntegrationFailure>(() => identity.Call(ManagerCreation.Ensure, Intent()));
        Assert.Single(identity.Writes);

        using var settings = new Fixture { CatalogReadyAfterWrite = true, ChangeSettingsAfterWrite = true };
        await Assert.ThrowsAsync<IntegrationFailure>(() => settings.Call(ManagerCreation.Ensure, Intent()));
        Assert.Single(settings.Writes);
    }

    [Theory]
    [InlineData("/library-archive/Series")]
    [InlineData("/library")]
    [InlineData("/Library/Series")]
    public async Task AddedSeriesOutsideTheReviewedRootStaysUncertainAfterWrite(string seriesPath) {
        using var fixture = new Fixture { CatalogReadyAfterWrite = true, SeriesPath = seriesPath };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Ensure, Intent()));
        Assert.Single(fixture.Writes);
    }

    private sealed class Fixture : HttpMessageHandler {
        internal bool Exists, Monitored, LoseResponse, CatalogReady, CatalogReadyAfterWrite;
        internal bool ChangeIdentityAfterWrite, ChangeSettingsAfterWrite;
        internal bool Accessible = true;
        internal int Profile = 2, ReturnedTvdb = 1001, ReturnedTmdb = 2001;
        internal string SeriesPath = "/library/Series";
        internal string? FailingRead;
        internal object[]? Episodes { get; set; }
        internal List<string> Reads { get; } = [];
        internal List<(string Path, JsonElement Body)> Writes { get; } = [];
        private readonly ConnectionContext connection = new(Guid.NewGuid(), "http://manager.test/", null,
            new Dictionary<string, string>(), new Dictionary<string, string> { [ArrClient.ApiKey] = "test-api-key" });
        private readonly ArrClient client;
        private readonly SonarrLibrary adapter;

        internal Fixture() { client = new(connection, this); adapter = new(client); }
        internal Task<object> Call(string operation, object input) => adapter.DispatchAsync(new(IntegrationProtocol.Name,
            IntegrationProtocol.Version, Guid.NewGuid(), operation, connection,
            JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);
        internal object Episode(int id, int season, int number, int? absolute, int tvdb) => new {
            id, seriesId = 1, tvdbId = tvdb, title = $"Episode {id}", seasonNumber = season, episodeNumber = number,
            absoluteEpisodeNumber = absolute, episodeFileId = 0, hasFile = false, monitored = false,
        };
        private object Series(bool lookup = false) => new {
            id = lookup ? 0 : 1, title = "Series", year = 2024, tvdbId = ReturnedTvdb, tmdbId = ReturnedTmdb,
            imdbId = "tt1001", qualityProfileId = lookup ? 0 : Profile, monitored = lookup || Monitored,
            path = lookup ? "" : SeriesPath, seasonFolder = true,
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            var path = request.RequestUri!.PathAndQuery.Split("/api/v3/")[1];
            if (request.Method == HttpMethod.Get) {
                Reads.Add(path);
                if (path == FailingRead) return new(HttpStatusCode.InternalServerError);
                if (path == "system/status") return Response(new { appName = "Sonarr", version = "4.0.17.2952" });
                if (path.StartsWith("series?tvdbId=", StringComparison.Ordinal))
                    return Response(Exists && path == $"series?tvdbId={ReturnedTvdb}" ? new[] { Series() } : []);
                if (path.StartsWith("series/lookup?term=", StringComparison.Ordinal)) return Response(new[] { Series(lookup: true) });
                if (path == "series/1") return Response(Series());
                if (path == "episodefile?seriesId=1") return Response(Array.Empty<object>());
                if (path == "episode?seriesId=1") return Response(CatalogReady || Exists && Writes.Count == 0
                    ? Episodes ?? [Episode(1, 0, 1, null, 5001)] : []);
                if (path == "qualityprofile") return Response(new[] { new { id = 2, name = "Chosen" } });
                if (path == "rootfolder") return Response(new[] { new { id = 3, path = "/library", accessible = Accessible } });
                throw new InvalidOperationException("Unexpected read: " + path);
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("series", path);
            Writes.Add((path, JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token))));
            Exists = true;
            CatalogReady = CatalogReadyAfterWrite;
            if (ChangeIdentityAfterWrite) ReturnedTvdb = 999;
            if (ChangeSettingsAfterWrite) { Profile = 9; SeriesPath = "/other/Series"; Monitored = true; }
            if (LoseResponse) throw new HttpRequestException("Simulated accepted creation with lost response");
            return Response(new { id = 1 }, HttpStatusCode.Created);
        }

        private static HttpResponseMessage Response(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value, IntegrationProtocol.Json)) };
    }
}
