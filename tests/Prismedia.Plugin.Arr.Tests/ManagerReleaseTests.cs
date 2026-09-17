using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Arr;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr.Tests;

public sealed class ManagerReleaseTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyCompleteQueueAndTerminalCommandsAreObservedWithoutWrites(bool television) {
        using var fixture = new Fixture(television);
        var observed = await fixture.Inspect();
        Assert.True(observed.QueueEmpty);
        Assert.True(observed.CommandsIdle);
        Assert.All(observed.State.Targets, target => Assert.False(target.Monitored));
        Assert.Equal(2, fixture.QueueReads);
        Assert.Equal(2, fixture.HoldingReads);
        Assert.All(fixture.Methods, method => Assert.Equal(HttpMethod.Get, method));
        Assert.Contains(television ? "includeUnknownSeriesItems=true" : "includeUnknownMovieItems=true", fixture.QueuePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnyQueueItemIncludingUnknownOrOtherWorksPreventsAnEmptyObservation(bool television) {
        using var fixture = new Fixture(television) { QueueCount = 1 };
        Assert.False((await fixture.Inspect()).QueueEmpty);
    }

    [Theory]
    [InlineData(ArrCommands.Queued)]
    [InlineData(ArrCommands.Started)]
    [InlineData(ArrCommands.Orphaned)]
    [InlineData(ArrCommands.Unknown)]
    public async Task RunningQueuedAndUnrecognizedCommandsNeverEstablishIdle(string status) {
        using var fixture = new Fixture(false) { CommandStatus = status };
        Assert.False((await fixture.Inspect()).CommandsIdle);
    }

    [Fact]
    public async Task QueueGrowthDuringInspectionDoesNotReturnEarlierEmptyEvidence() {
        using var fixture = new Fixture(false) { GrowQueue = true };
        Assert.False((await fixture.Inspect()).QueueEmpty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingQueueCountOrRecordArrayIsNotEvidenceOfAnEmptyQueue(bool omitCount) {
        using var fixture = new Fixture(false) { OmitQueueCount = omitCount, OmitQueueRecords = !omitCount };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Inspect());
    }

    [Fact]
    public async Task InconsistentQueueTotalsAndDuplicateCommandIdsAreRejected() {
        using var fixture = new Fixture(false) { InconsistentQueue = true };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Inspect());
        fixture.InconsistentQueue = false; fixture.DuplicateCommands = true;
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Inspect());
    }

    [Fact]
    public async Task ConfigurationChangesDuringObservationRequireFreshReview() {
        using var fixture = new Fixture(false) { ChangeMonitoring = true };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Inspect());
    }

    [Fact]
    public async Task DisabledSonarrParentDoesNotHideEnabledEpisodeMonitoring() {
        using var fixture = new Fixture(true) { Monitored = true };
        Assert.True(Assert.Single((await fixture.Inspect()).State.Targets).Monitored);
    }

    private sealed class Fixture(bool television) : HttpMessageHandler {
        internal int QueueCount, QueueReads, HoldingReads;
        internal bool GrowQueue, OmitQueueCount, OmitQueueRecords, InconsistentQueue, DuplicateCommands, ChangeMonitoring, Monitored;
        internal string CommandStatus = ArrCommands.Completed, QueuePath = "";
        internal List<HttpMethod> Methods = [];
        internal async Task<ManagedReleaseObservation> Inspect() {
            var connection = new ConnectionContext(Guid.NewGuid(), "http://manager.test/", null,
                new Dictionary<string, string>(), new Dictionary<string, string> { [ArrClient.ApiKey] = "fixture-key" });
            using var client = new ArrClient(connection, this);
            ArrLibrary library = television ? new SonarrLibrary(client) : new RadarrLibrary(client);
            var scope = new ManagedControlScope(new(television ? ManagerProtocol.Series : ManagerProtocol.Movie, "1",
                new Dictionary<string, string> { [television ? ManagerProtocol.Tvdb : ManagerProtocol.Tmdb] = "42" }),
                television ? [new("10", ManagerProtocol.Episode, 0, 1)] : [new("1", ManagerProtocol.Movie)]);
            return Assert.IsType<ManagedReleaseObservation>(await library.DispatchAsync(new(IntegrationProtocol.Name,
                IntegrationProtocol.Version, Guid.NewGuid(), ManagerRelease.Inspect, connection,
                JsonSerializer.SerializeToElement(new InspectManagedReleaseInput(scope), IntegrationProtocol.Json)), default));
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            Methods.Add(request.Method);
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.PathAndQuery.Split("/api/v3/")[1];
            object value;
            if (path == "system/status") value = new { appName = television ? "Sonarr" : "Radarr", version = television ? "4.0.17.2952" : "6.1.1.10360" };
            else if (path is "movie/1" or "series/1") {
                HoldingReads++;
                value = new { id = 1, title = "Holding", year = 2024, tmdbId = 42, tvdbId = 42,
                    qualityProfileId = 1, monitored = television ? false : Monitored || ChangeMonitoring && HoldingReads > 1,
                    hasFile = false, movieFileId = 0, path = "/library/holding", statistics = new { episodeFileCount = 0 } };
            } else if (path == "episode?seriesId=1") value = new[] { new { id = 10, seriesId = 1, title = "Special", seasonNumber = 0,
                episodeNumber = 1, monitored = Monitored, hasFile = false, episodeFileId = 0 } };
            else if (path.StartsWith("queue?", StringComparison.Ordinal)) {
                QueuePath = path; QueueReads++;
                var count = GrowQueue && QueueReads > 1 ? 1 : QueueCount;
                var page = new Dictionary<string, object> { ["page"] = 1, ["pageSize"] = 100 };
                if (!OmitQueueCount) page["totalRecords"] = count;
                if (!OmitQueueRecords) page["records"] = count > 0 || InconsistentQueue ? new[] { new { id = 8 } } : [];
                value = page;
            } else if (path == "command") {
                var command = new { id = 5, status = CommandStatus };
                value = DuplicateCommands ? new[] { command, command } : [command];
            } else throw new InvalidOperationException(path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, IntegrationProtocol.Json)) });
        }
    }
}
