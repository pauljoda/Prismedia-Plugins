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
        Assert.All(Assert.IsType<ManagedControlState>(observed.State).Targets, target => Assert.False(target.Monitored));
        Assert.Equal(2, fixture.QueueReads);
        Assert.Equal(2, fixture.HoldingReads);
        Assert.Equal(2, fixture.HealthReads);
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
        Assert.True(Assert.Single(Assert.IsType<ManagedControlState>((await fixture.Inspect()).State).Targets).Monitored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedRemovedHoldingReturnsExplicitAbsenceWithoutFabricatingState(bool television) {
        using var fixture = new Fixture(television) { HoldingAbsent = true };

        var observed = await fixture.Inspect();

        Assert.Null(observed.State);
        Assert.True(observed.RemoteItemAbsent);
        Assert.True(observed.QueueEmpty);
        Assert.True(observed.CommandsIdle);
        Assert.Equal(2, fixture.HoldingReads);
        Assert.Equal(2, fixture.CatalogReads);
        Assert.All(fixture.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task RemovedHoldingStillReportsBusyApplicationActivity(bool television, bool queueBusy) {
        using var fixture = new Fixture(television) {
            HoldingAbsent = true,
            QueueCount = queueBusy ? 1 : 0,
            CommandStatus = queueBusy ? ArrCommands.Completed : ArrCommands.Started
        };

        var observed = await fixture.Inspect();

        Assert.True(observed.RemoteItemAbsent);
        Assert.Equal(!queueBusy, observed.QueueEmpty);
        Assert.Equal(queueBusy, observed.CommandsIdle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReappearanceDuringInspectionInvalidatesEarlierAbsence(bool television) {
        using var fixture = new Fixture(television) { HoldingAbsent = true, ReappearOnSecondHoldingRead = true };

        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Inspect());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CatalogPresenceUnderSameOrNewManagerIdBlocksAbsence(bool television, bool newRemoteId) {
        using var fixture = new Fixture(television) {
            HoldingAbsent = true,
            ReaddedInCatalog = true,
            ReaddedWithNewRemoteId = newRemoteId
        };

        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Inspect());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task MissingHoldingCannotBeConfirmedThroughAnEndpointOrCatalogFailure(bool television, bool exactFailure) {
        using var fixture = new Fixture(television) {
            HoldingAbsent = true,
            FailHoldingRead = exactFailure,
            FailCatalogRead = !exactFailure
        };

        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Inspect());
    }

    [Theory]
    [InlineData(false, ArrHealth.DownloadClientCheck)]
    [InlineData(true, ArrHealth.DownloadClientCheck)]
    [InlineData(false, ArrHealth.DownloadClientStatusCheck)]
    [InlineData(true, ArrHealth.DownloadClientStatusCheck)]
    public async Task DownloadClientHealthProblemsInvalidateAnOtherwiseEmptyQueue(bool television, string source) {
        using var fixture = new Fixture(television) { HealthSource = source };
        var error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Inspect());
        Assert.Contains("download client", error.Message);
        Assert.All(fixture.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Fact]
    public async Task DownloadClientFailureDuringInspectionInvalidatesEarlierEvidence() {
        using var fixture = new Fixture(false) { FailHealthAfterFirstRead = true };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Inspect());
        Assert.Equal(2, fixture.HealthReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingHealthSourceIsIncompleteEvidence(bool nullEntry) {
        using var fixture = new Fixture(false) { InvalidHealth = true, NullHealthEntry = nullEntry };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Inspect());
    }

    private sealed class Fixture(bool television) : HttpMessageHandler {
        internal int QueueCount, QueueReads, HoldingReads, CatalogReads, HealthReads;
        internal bool GrowQueue, OmitQueueCount, OmitQueueRecords, InconsistentQueue, DuplicateCommands, ChangeMonitoring, Monitored;
        internal bool FailHealthAfterFirstRead, InvalidHealth, NullHealthEntry;
        internal bool HoldingAbsent, ReappearOnSecondHoldingRead, ReaddedInCatalog, ReaddedWithNewRemoteId, FailHoldingRead, FailCatalogRead;
        internal string? HealthSource;
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
                if (FailHoldingRead) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                if (HoldingAbsent && !(ReappearOnSecondHoldingRead && HoldingReads > 1))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                value = new { id = 1, title = "Holding", year = 2024, tmdbId = 42, tvdbId = 42,
                    qualityProfileId = 1, monitored = television ? false : Monitored || ChangeMonitoring && HoldingReads > 1,
                    hasFile = false, movieFileId = 0, path = "/library/holding", statistics = new { episodeFileCount = 0 } };
            } else if (path is "movie" or "series") {
                CatalogReads++;
                if (FailCatalogRead) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                value = ReaddedInCatalog
                    ? new[] { new { id = ReaddedWithNewRemoteId ? 2 : 1, title = "Replacement", year = 2024, tmdbId = 42, tvdbId = 42,
                        qualityProfileId = 1, monitored = false, hasFile = false, movieFileId = 0,
                        path = "/library/replacement", statistics = new { episodeFileCount = 0 } } }
                    : [];
            } else if (path == "episode?seriesId=1") value = new[] { new { id = 10, seriesId = 1, title = "Special", seasonNumber = 0,
                episodeNumber = 1, monitored = Monitored, hasFile = false, episodeFileId = 0 } };
            else if (path.StartsWith("queue?", StringComparison.Ordinal)) {
                QueuePath = path; QueueReads++;
                var count = GrowQueue && QueueReads > 1 ? 1 : QueueCount;
                var page = new Dictionary<string, object> { ["page"] = 1, ["pageSize"] = 100 };
                if (!OmitQueueCount) page["totalRecords"] = count;
                if (!OmitQueueRecords) page["records"] = count > 0 || InconsistentQueue ? new[] { new { id = 8 } } : [];
                value = page;
            } else if (path == "health") {
                HealthReads++;
                var source = FailHealthAfterFirstRead && HealthReads > 1 ? ArrHealth.DownloadClientStatusCheck : HealthSource;
                value = InvalidHealth ? (NullHealthEntry ? new object?[] { null } : new object[] { new { } })
                    : source is null ? Array.Empty<object>() : new object[] { new { source } };
            } else if (path == "command") {
                var command = new { id = 5, status = CommandStatus };
                value = DuplicateCommands ? new[] { command, command } : [command];
            } else throw new InvalidOperationException(path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, IntegrationProtocol.Json)) });
        }
    }
}
