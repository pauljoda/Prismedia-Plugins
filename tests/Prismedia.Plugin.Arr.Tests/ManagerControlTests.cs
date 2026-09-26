using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Arr;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr.Tests;

public sealed class ManagerControlTests {
    [Fact]
    public async Task ConfigurationWritesOnlyRequestedFieldsAndVerifiesTheObservedResult() {
        using var fixture = new Fixture();
        var result = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure,
            fixture.Configure(new(ProfileId: "2"))));
        Assert.Equal(ManagerControls.Applied, result.Outcome);
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(HttpMethod.Put, write.Method);
        Assert.Equal("movie/editor", write.Path);
        Assert.Equal(["movieIds", "qualityProfileId"], write.Body.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(1, Assert.Single(write.Body.GetProperty("movieIds").EnumerateArray()).GetInt32());
        Assert.False(fixture.Monitored);
        Assert.Equal(2, fixture.Profile);
        Assert.Equal("/library/film", fixture.Path);
    }

    [Fact]
    public async Task MonitoringUpdatesPreserveProfileAndAlreadySatisfiedIntentDoesNotWrite() {
        using var fixture = new Fixture();
        var request = fixture.Configure(new(Monitored: true));
        Assert.Equal(ManagerControls.Applied, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, request)).Outcome);
        Assert.Equal(["monitored", "movieIds"], Assert.Single(fixture.Writes).Body.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(1, fixture.Profile);
        Assert.Equal(ManagerControls.Applied, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, request)).Outcome);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task StaleProfilePathOrProviderIdentityRejectsBeforeMutation() {
        using var fixture = new Fixture();
        var request = fixture.Configure(new(ProfileId: "2"));
        foreach (var invalid in new[] {
            request with { ExpectedProfileId = "3" },
            request with { ExpectedPath = "/library/other" },
            request with { Scope = Scope() with { Item = Scope().Item with { ExpectedExternalIds = new Dictionary<string, string> { [ManagerProtocol.Tmdb] = "9999" } } } },
            request with { Scope = Scope() with { Targets = [new("2", ManagerProtocol.Movie)] } },
            request with { Changes = new(ProfileId: "9999") }
        }) Assert.Equal(ManagerControls.Rejected, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, invalid)).Outcome);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task ChangedMonitoringRequiresFreshReviewWhenDesiredStateIsNotSatisfied() {
        using var fixture = new Fixture();
        var input = fixture.Configure(new(Monitored: true)) with { ExpectedMonitoring = new Dictionary<string, bool> { ["1"] = true } };
        Assert.Equal(ManagerControls.Rejected, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, input)).Outcome);
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("movie/1")]
    [InlineData("qualityprofile")]
    public async Task PreconditionReadFailureStaysUncertainInsteadOfRejected(string failingRead) {
        using var fixture = new Fixture { FailingRead = failingRead };
        var error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerControls.Configure, fixture.Configure(new(ProfileId: "2"))));
        Assert.Equal("The connected application returned HTTP 500.", error.Message);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task RefusedConfirmationAfterTheWriteIsUncertainRatherThanRejected() {
        using var fixture = new Fixture { MovePathAfterWrite = true };
        var error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerControls.Configure, fixture.Configure(new(Monitored: true))));
        Assert.StartsWith("Radarr accepted the movie's configuration change, but it could not be confirmed.", error.Message);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task LostConfigurationResponseIsUncertainAndReadOnlyReconciliationCanObserveItsEffect() {
        using var fixture = new Fixture { LoseResponse = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Call(ManagerControls.Configure, fixture.Configure(new(Monitored: true))));
        Assert.Single(fixture.Writes);
        var observed = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(Scope())));
        Assert.True(Assert.Single(observed.Targets).Monitored);
        Assert.Null(observed.Command);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Redirect, false)]
    public async Task OnlyDefiniteWriteRejectionsAreReportedAsRejected(HttpStatusCode status, bool rejected) {
        using var fixture = new Fixture { WriteStatus = status };
        var action = () => fixture.Call(ManagerControls.Request, fixture.Search());
        if (rejected) {
            var result = Assert.IsType<ManagedMutationResult>(await action());
            Assert.Equal(ManagerControls.Rejected, result.Outcome);
            Assert.Equal($"The connected application rejected the request (HTTP {(int)status}).", result.Problem);
        } else await Assert.ThrowsAsync<IntegrationFailure>(action);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task SearchSubmitsExactlyOneMovieAndDoesNotChangeMonitoringOrProfile() {
        using var fixture = new Fixture();
        var result = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request, fixture.Search()));
        Assert.Equal(ManagerControls.Accepted, result.Outcome);
        Assert.Equal(new ManagedCommandReference("7", Fixture.QueuedAt), result.Command!.Reference);
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(HttpMethod.Post, write.Method);
        Assert.Equal("command", write.Path);
        Assert.Equal(ArrCommands.MoviesSearch, write.Body.GetProperty("name").GetString());
        Assert.Equal(1, Assert.Single(write.Body.GetProperty("movieIds").EnumerateArray()).GetInt32());
        Assert.False(fixture.Monitored);
        Assert.Equal(1, fixture.Profile);
    }

    [Fact]
    public async Task LostSearchResponseNeverCausesAnAutomaticSecondPost() {
        using var fixture = new Fixture { LoseResponse = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Call(ManagerControls.Request, fixture.Search()));
        Assert.Single(fixture.Writes);
        await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(Scope()));
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData(ArrCommands.Queued, ArrCommands.Unknown, ManagerControls.Pending)]
    [InlineData(ArrCommands.Started, ArrCommands.Unknown, ManagerControls.Running)]
    [InlineData(ArrCommands.Completed, ArrCommands.Successful, ManagerControls.Completed)]
    [InlineData(ArrCommands.Completed, ArrCommands.Unsuccessful, ManagerControls.Failed)]
    [InlineData(ArrCommands.Completed, ArrCommands.Unknown, ManagerControls.Unknown)]
    [InlineData(ArrCommands.Failed, ArrCommands.Unsuccessful, ManagerControls.Failed)]
    [InlineData(ArrCommands.Cancelled, ArrCommands.Unknown, ManagerControls.Cancelled)]
    [InlineData(ArrCommands.Orphaned, ArrCommands.Unknown, ManagerControls.Unknown)]
    public async Task CommandStateDoesNotPromoteUnavailableFiles(string status, string result, string expected) {
        using var fixture = new Fixture { CommandStatus = status, CommandResult = result };
        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile,
            new ReconcileManagedInput(Scope(), new("7", Fixture.QueuedAt))));
        Assert.Equal(expected, state.Command!.Status);
        Assert.Equal(0, state.Item.RemoteFileCount);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task MissingReusedOrWrongScopeCommandHistoryRemainsUnknown() {
        using var fixture = new Fixture();
        var input = new ReconcileManagedInput(Scope(), new("7", Fixture.QueuedAt));
        fixture.CommandMissing = true;
        Assert.Equal(ManagerControls.Unknown, Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, input)).Command!.Status);
        fixture.CommandMissing = false;
        Assert.Equal(ManagerControls.Unknown, Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, input with { Command = new("7", Fixture.QueuedAt.AddSeconds(-1)) })).Command!.Status);
        fixture.CommandMovieId = 2;
        Assert.Equal(ManagerControls.Unknown, Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, input)).Command!.Status);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerControls.Request, fixture.Search()));
        Assert.Single(fixture.Writes);
    }

    private static ManagedControlScope Scope() => new(new(ManagerProtocol.Movie, "1", new Dictionary<string, string> { [ManagerProtocol.Tmdb] = "1001" }), [new("1", ManagerProtocol.Movie)]);
    private sealed record CapturedWrite(HttpMethod Method, string Path, JsonElement Body);
    private sealed class Fixture : HttpMessageHandler {
        internal static readonly DateTimeOffset QueuedAt = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        internal bool Monitored { get; set; }
        internal int Profile { get; set; } = 1;
        internal string Path { get; set; } = "/library/film";
        internal bool LoseResponse { get; set; }
        internal HttpStatusCode WriteStatus { get; set; } = HttpStatusCode.Accepted;
        internal string CommandStatus { get; set; } = ArrCommands.Queued;
        internal string CommandResult { get; set; } = ArrCommands.Unknown;
        internal int CommandMovieId { get; set; } = 1;
        internal bool CommandMissing { get; set; }
        internal string? FailingRead { get; set; }
        internal bool MovePathAfterWrite { get; set; }
        internal List<CapturedWrite> Writes { get; } = [];
        private readonly ConnectionContext connection = new(Guid.NewGuid(), "http://manager.test/radarr/", null, new Dictionary<string, string>(), new Dictionary<string, string> { [ArrClient.ApiKey] = "test-secret" });
        private readonly ArrClient client;
        private readonly RadarrLibrary library;
        internal Fixture() { client = new(connection, this); library = new(client); }
        internal ConfigureManagedInput Configure(ManagedConfigurationChange changes) => new(Guid.NewGuid(), Scope(), Path, "1", new Dictionary<string, bool> { ["1"] = false }, changes);
        internal RequestManagedInput Search() => new(Guid.NewGuid(), Scope(), Path, "1");
        internal Task<object> Call(string operation, object input) => library.DispatchAsync(new(IntegrationProtocol.Name, IntegrationProtocol.Version,
            Guid.NewGuid(), operation, connection, JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);
        private object Movie() => new { id = 1, title = "Film", year = 2024, tmdbId = 1001, qualityProfileId = Profile, monitored = Monitored, hasFile = false, movieFileId = 0, path = Path };
        private object Command() => new { id = 7, name = ArrCommands.MoviesSearch, status = CommandStatus, result = CommandResult, queued = QueuedAt,
            body = new { name = ArrCommands.MoviesSearch, movieIds = new[] { CommandMovieId } } };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            var path = request.RequestUri!.PathAndQuery.Split("/api/v3/")[1];
            if (request.Method == HttpMethod.Get && path == FailingRead) return new(HttpStatusCode.InternalServerError);
            if (request.Method == HttpMethod.Get) return path switch {
                "system/status" => Response(new { appName = "Radarr", version = "6.1.1.10360" }),
                "movie/1" => Response(Movie()),
                "qualityprofile" => Response(new[] { new { id = 1, name = "First" }, new { id = 2, name = "Second" } }),
                "command/7" => CommandMissing ? new(HttpStatusCode.NotFound) : Response(Command()),
                _ => throw new InvalidOperationException("Unexpected read: " + path)
            };
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
            Writes.Add(new(request.Method, path, body));
            if (WriteStatus != HttpStatusCode.Accepted) return new(WriteStatus) { Content = new StringContent("{}") };
            if (path == "movie/editor") {
                if (body.TryGetProperty("monitored", out var monitored)) Monitored = monitored.GetBoolean();
                if (body.TryGetProperty("qualityProfileId", out var profile)) Profile = profile.GetInt32();
                if (MovePathAfterWrite) Path = "/library/moved";
            } else Assert.Equal("command", path);
            if (LoseResponse) throw new HttpRequestException("Simulated response loss after acceptance");
            // Radarr 6 editor replies omit hasFile; only the subsequent authoritative GET has that field.
            return Response(path == "movie/editor" ? new[] { new { id = 1, monitored = Monitored, qualityProfileId = Profile, movieFileId = 0 } } : Command(), HttpStatusCode.Accepted);
        }
        private static HttpResponseMessage Response(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value, IntegrationProtocol.Json)) };
    }
}
