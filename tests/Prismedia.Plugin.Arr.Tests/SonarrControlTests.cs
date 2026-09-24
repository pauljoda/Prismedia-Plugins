using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Arr;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr.Tests;

public sealed class SonarrControlTests {
    [Fact] public async Task MonitoringWritesOnlySelectedEpisodeFlagsAndPreservesParentProfileAndOtherEpisodes() {
        using var fixture = new Fixture();
        var result = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, fixture.Configure()));
        Assert.Equal(ManagerControls.Applied, result.Outcome);
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(HttpMethod.Put, write.Method); Assert.Equal("episode/monitor", write.Path);
        Assert.Equal(["episodeIds", "monitored"], write.Body.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal([1, 2], write.Body.GetProperty("episodeIds").EnumerateArray().Select(item => item.GetInt32()));
        Assert.True(fixture.Flags[1]); Assert.True(fixture.Flags[2]); Assert.False(fixture.Flags[3]);
        Assert.True(fixture.ParentMonitored); Assert.Equal(7, fixture.Profile);
        await fixture.Call(ManagerControls.Configure, fixture.Configure()); Assert.Single(fixture.Writes);
    }
    [Fact] public async Task DisabledParentPreventsMonitoringChangesButExplicitEpisodeSearchRemainsAvailable() {
        using var fixture = new Fixture { ParentMonitored = false };
        var view = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(Scope())));
        Assert.False(view.Capabilities.CanChangeMonitoring); Assert.False(view.Capabilities.CanChangeProfile);
        Assert.NotNull(view.Capabilities.MonitoringUnavailableReason); Assert.True(view.Capabilities.CanSearch);
        Assert.Equal(ManagerControls.Rejected, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, fixture.Configure())).Outcome);
        Assert.Empty(fixture.Writes);
        Assert.Equal(ManagerControls.Accepted, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request, fixture.Search())).Outcome);
        var write = Assert.Single(fixture.Writes); Assert.Equal("command", write.Path);
        Assert.Equal(["episodeIds", "name"], write.Body.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(ArrCommands.EpisodeSearch, write.Body.GetProperty("name").GetString());
        Assert.Equal([1, 2], write.Body.GetProperty("episodeIds").EnumerateArray().Select(item => item.GetInt32()));
        Assert.False(fixture.ParentMonitored); Assert.All(fixture.Flags.Values, Assert.False);
    }
    [Fact] public async Task SeriesProfileChangesAreOutsideTheFiniteEpisodeScope() {
        using var fixture = new Fixture();
        var request = fixture.Configure() with { Changes = new(ProfileId: "8", Monitored: true) };
        Assert.Equal(ManagerControls.Rejected, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, request)).Outcome);
        Assert.Empty(fixture.Writes);
    }
    [Fact] public async Task ChangedEpisodeCoordinatesOrProviderIdentityRejectBeforeWriting() {
        using var fixture = new Fixture();
        foreach (var scope in new[] {
            Scope() with { Targets = [Scope().Targets[0] with { EpisodeNumber = 99 }] },
            Scope() with { Targets = [Scope().Targets[0] with { AbsoluteNumber = 99 }] },
            Scope() with { Targets = [Scope().Targets[0], Scope().Targets[0]] },
            Scope() with { Targets = [] },
            Scope() with { Item = Scope().Item with { ExpectedExternalIds = new Dictionary<string, string> { [ManagerProtocol.Tvdb] = "999" } } }
        }) Assert.Equal(ManagerControls.Rejected, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request, fixture.Search() with { Scope = scope })).Outcome);
        Assert.Empty(fixture.Writes);
    }
    [Fact] public async Task InconsistentOrFailedPreconditionReadsStayUncertainBeforeWriting() {
        using var foreign = new Fixture { ForeignEpisode = true };
        var inconsistent = await Assert.ThrowsAsync<IntegrationFailure>(() => foreign.Call(ManagerControls.Request, foreign.Search()));
        Assert.Equal("The series returned inconsistent episode identities.", inconsistent.Message);
        Assert.Empty(foreign.Writes);
        using var offline = new Fixture { FailingRead = "episode?seriesId=1" };
        var failed = await Assert.ThrowsAsync<IntegrationFailure>(() => offline.Call(ManagerControls.Configure, offline.Configure()));
        Assert.Equal("The connected application returned HTTP 500.", failed.Message);
        Assert.Empty(offline.Writes);
    }
    [Fact] public async Task RefusedConfirmationAfterTheWriteIsUncertainRatherThanRejected() {
        using var fixture = new Fixture { MoveFolderAfterWrite = true };
        var error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerControls.Configure, fixture.Configure()));
        Assert.StartsWith("Sonarr accepted the episode monitoring change, but it could not be confirmed.", error.Message);
        Assert.Single(fixture.Writes);
    }
    [Fact] public async Task ChangedProfileFolderOrExpectedFlagsRejectBeforeWrites() {
        using var fixture = new Fixture();
        foreach (var request in new[] {
            fixture.Configure() with { ExpectedPath = "/different" },
            fixture.Configure() with { ExpectedProfileId = "8" },
            fixture.Configure() with { ExpectedMonitoring = new Dictionary<string, bool> { ["1"] = true, ["2"] = false } }
        }) Assert.Equal(ManagerControls.Rejected, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, request)).Outcome);
        Assert.Empty(fixture.Writes);
    }
    [Fact] public async Task ConfigurationResponseLossIsObservedWithoutAnotherWrite() {
        using var fixture = new Fixture { LoseResponse = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Call(ManagerControls.Configure, fixture.Configure()));
        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(Scope())));
        Assert.All(state.Targets, target => Assert.True(target.Monitored)); Assert.Single(fixture.Writes);
    }
    [Fact] public async Task SearchResponseLossNeverFabricatesACommandIdentity() {
        using var fixture = new Fixture { LoseResponse = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Call(ManagerControls.Request, fixture.Search()));
        Assert.Single(fixture.Writes);
        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(Scope())));
        Assert.Null(state.Command); Assert.Single(fixture.Writes);
    }
    [Fact] public async Task CompletedCommandDoesNotIncreaseReportedFileCountAndExpiredHistoryRemainsUnknown() {
        using var fixture = new Fixture();
        var accepted = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request, fixture.Search()));
        fixture.Status = ArrCommands.Completed;
        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(Scope(), accepted.Command!.Reference)));
        Assert.Equal(ManagerControls.Completed, state.Command!.Status); Assert.Equal(0, state.Item.RemoteFileCount);
        fixture.MissingCommand = true;
        state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(Scope(), accepted.Command.Reference)));
        Assert.Equal(ManagerControls.Unknown, state.Command!.Status); Assert.Single(fixture.Writes);
    }
    [Fact] public async Task ReusedCommandAndChangedCoverageRemainUnverified() {
        using var fixture = new Fixture();
        var accepted = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request, fixture.Search()));
        var different = accepted.Command!.Reference with { QueuedAt = accepted.Command.Reference.QueuedAt.AddTicks(1) };
        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(Scope(), different)));
        Assert.Equal(ManagerControls.Unknown, state.Command!.Status);
        fixture.CommandIds = [1, 2, 3];
        state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(Scope(), accepted.Command.Reference)));
        Assert.Equal(ManagerControls.Unknown, state.Command!.Status);
    }

    private static ManagedControlScope Scope() => new(new(ManagerProtocol.Series, "1", new Dictionary<string, string> { [ManagerProtocol.Tvdb] = "42" }),
        [new("1", ManagerProtocol.Episode, 0, 1), new("2", ManagerProtocol.Episode, 0, 2, 2)]);
    private sealed record Write(HttpMethod Method, string Path, JsonElement Body);
    private sealed class Fixture : HttpMessageHandler {
        internal Dictionary<int, bool> Flags = new() { [1] = false, [2] = false, [3] = false };
        internal bool ParentMonitored = true, ForeignEpisode, LoseResponse, MissingCommand, MoveFolderAfterWrite;
        internal string Folder = "/series/show";
        internal string? FailingRead;
        internal int Profile = 7;
        internal string Status = ArrCommands.Queued;
        internal int[] CommandIds = [1, 2];
        internal List<Write> Writes = [];
        private readonly ConnectionContext connection = new(Guid.NewGuid(), "https://manager.test/", null, new Dictionary<string, string>(), new Dictionary<string, string> { [ArrClient.ApiKey] = "test-secret" });
        private readonly SonarrLibrary library;
        internal Fixture() => library = new(new(connection, this));
        internal ConfigureManagedInput Configure() => new(Guid.NewGuid(), Scope(), "/series/show", "7", new Dictionary<string, bool> { ["1"] = false, ["2"] = false }, new(Monitored: true));
        internal RequestManagedInput Search() => new(Guid.NewGuid(), Scope(), "/series/show", "7");
        internal Task<object> Call(string operation, object input) => library.DispatchAsync(new(IntegrationProtocol.Name, IntegrationProtocol.Version, Guid.NewGuid(), operation,
            connection, JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);
        private object Series() => new { id = 1, title = "Show", tvdbId = 42, monitored = ParentMonitored, qualityProfileId = Profile, path = Folder, statistics = new { episodeFileCount = 0 } };
        private object[] Episodes() => Flags.Select(pair => (object)new { id = pair.Key, seriesId = ForeignEpisode ? 2 : 1, title = "Episode", seasonNumber = 0, episodeNumber = pair.Key,
            absoluteEpisodeNumber = pair.Key == 2 ? (int?)2 : null, monitored = pair.Value, hasFile = false, episodeFileId = 0 }).ToArray();
        private object Command() => new { id = 9, name = ArrCommands.EpisodeSearch, status = Status, result = ArrCommands.Successful,
            queued = "2026-09-16T12:00:00.1234567Z", body = new { name = ArrCommands.EpisodeSearch, episodeIds = CommandIds } };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            var path = request.RequestUri!.PathAndQuery.Split("/api/v3/")[1];
            if (request.Method == HttpMethod.Get && path == FailingRead) return new(HttpStatusCode.InternalServerError);
            if (request.Method == HttpMethod.Get) return path switch {
                "system/status" => Response(new { appName = "Sonarr", version = "4.0.17.2952" }),
                "series/1" => Response(Series()), "episode?seriesId=1" => Response(Episodes()),
                "command/9" => MissingCommand ? new(HttpStatusCode.NotFound) : Response(Command()),
                _ => throw new InvalidOperationException("Unexpected read: " + path)
            };
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
            Writes.Add(new(request.Method, path, body));
            if (path == "episode/monitor") foreach (var id in body.GetProperty("episodeIds").EnumerateArray()) Flags[id.GetInt32()] = body.GetProperty("monitored").GetBoolean();
            else Assert.Equal("command", path);
            if (MoveFolderAfterWrite) Folder = "/series/moved";
            if (LoseResponse) throw new HttpRequestException("Response lost after acceptance");
            return Response(path == "episode/monitor" ? new[] { new { id = 1 }, new { id = 2 } } : Command(), HttpStatusCode.Accepted);
        }
        private static HttpResponseMessage Response(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body, IntegrationProtocol.Json)) };
    }
}
