using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Archiver;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Archiver.Tests;

public sealed class ArchiverContractTests {
    private const string Instance = "original-installation";
    private static ConnectionContext Connection(string? instance = Instance) => new(Guid.NewGuid(), "https://archiver.test/", instance,
        new Dictionary<string, string>(), new Dictionary<string, string> { [ArchiverWire.TokenKey] = "test-credential" });
    [Fact]
    public async Task ChangedInstallationBlocksOperationBeforePosting() {
        var calls = 0;
        using var client = new ArchiverClient(Connection("another-installation"), new Handler(request => {
            calls++; Assert.Equal(HttpMethod.Get, request.Method); return Json(System());
        }));
        await Assert.ThrowsAsync<IntegrationFailure>(() => new ArchiverIntegration(client, Connection("another-installation"))
            .DispatchAsync(Request(IntegrationOperations.Submit, new { }), default));
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task ProbeDoesNotAdvertiseSearchForAUrlOnlyExecutor() {
        using var client = new ArchiverClient(Connection(), new Handler(_ => Json(System())));
        var result = Assert.IsType<ProbeResult>(await new ArchiverIntegration(client, Connection()).DispatchAsync(Request(IntegrationOperations.Probe, new { }), default));
        Assert.DoesNotContain(result.Capabilities.SelectMany(capability => capability.Operations), operation => operation == IntegrationOperations.Search);
        Assert.Contains(result.Capabilities, capability => capability.Kind == IntegrationCapabilities.TransferExecutor);
    }
    [Fact]
    public async Task NotFoundIsAllowedOnlyForOperationRecovery() {
        using var client = new ArchiverClient(Connection(), new Handler(_ => new(HttpStatusCode.NotFound)));
        Assert.Null(await client.SendAsync<JobSnapshot>(HttpMethod.Get, "operations/missing", null, default, allowMissing: true));
        await Assert.ThrowsAsync<IntegrationFailure>(() => client.SendAsync<JobSnapshot>(HttpMethod.Get, "jobs/missing", null, default));
    }
    [Fact]
    public async Task CredentialsNeverFollowControlRedirectsOrArtifactOrigins() {
        using var client = new ArchiverClient(Connection(), new Handler(request => {
            Assert.Equal("archiver.test", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://other.test/");
            return response;
        }));
        await Assert.ThrowsAsync<IntegrationFailure>(() => client.SendAsync<SystemInfo>(HttpMethod.Get, "system", null, default));
        Assert.Throws<IntegrationFailure>(() => client.ArtifactUrl("//other.test/file"));
        Assert.Throws<IntegrationFailure>(() => client.ArtifactUrl("https://other.test/file"));
        Assert.Throws<IntegrationFailure>(() => client.ArtifactUrl("/\\other.test/file"));
        Assert.Equal("archiver.test", client.ArtifactUrl("/api/v1/artifacts/file/content").Host);
    }
    [Fact]
    public async Task SubmissionPinsTheInspectedOutputFormatAndIdempotencyKey() {
        var operation = Guid.NewGuid();
        using var client = new ArchiverClient(Connection(), new Handler(request => {
            if (request.Method == HttpMethod.Get) return Json(System());
            Assert.Equal(operation.ToString(), request.Headers.GetValues("Idempotency-Key").Single());
            var input = JsonSerializer.Deserialize<SubmitJob>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), IntegrationProtocol.Json)!;
            Assert.Equal("selection", input.Selection.Id);
            Assert.Equal(ArchiverWire.Cbz, input.Output.Format);
            Assert.Equal(ArchiverWire.Profile, input.Output.Profile);
            Assert.Equal(operation, input.ClientOperationId);
            return Json(new { instanceId = Instance, jobId = "job", clientOperationId = operation, revision = 1, state = "queued", itemFailures = Array.Empty<object>() });
        }));
        var pinned = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new PinnedSelection("selection", ArchiverWire.Cbz), IntegrationProtocol.Json));
        await new ArchiverIntegration(client, Connection()).DispatchAsync(Request(IntegrationOperations.Submit,
            new SubmitTransferInput(operation, "https://source.test/comic", pinned, "revision", ["issue"], 1, 1000)), default);
    }
    [Fact]
    public async Task ImageProfileIsNegotiatedSeparatelyAndPinsItsOutputFormat() {
        using var legacy = new ArchiverClient(Connection(), new Handler(_ => Json(System())));
        var old = Assert.IsType<ProbeResult>(await new ArchiverIntegration(legacy, Connection()).DispatchAsync(Request(IntegrationOperations.Probe, new { }), default));
        Assert.DoesNotContain(old.Capabilities.SelectMany(c => c.EntityKinds), kind => kind == MediaKinds.Image);
        using var client = new ArchiverClient(Connection(), new Handler(request => {
            if (request.Method == HttpMethod.Get) return Json(System() with { OutputProfiles = [ArchiverWire.Profile, ArchiverWire.ImageProfile] });
            if (request.RequestUri!.AbsolutePath.EndsWith("/inspect")) return Json(new Inspection("selection", "revision", DateTimeOffset.UtcNow.AddMinutes(5),
                "https://source.test/image", "source", [new("image", "An image", ArchiverWire.Image, [ArchiverWire.Png])], []));
            var submitted = JsonSerializer.Deserialize<SubmitJob>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), IntegrationProtocol.Json)!;
            Assert.Equal(ArchiverWire.ImageProfile, submitted.Output.Profile);
            Assert.Equal(ArchiverWire.Png, submitted.Output.Format);
            return Json(new { instanceId = Instance, jobId = "job", clientOperationId = submitted.ClientOperationId, revision = 1, state = "queued", itemFailures = Array.Empty<object>() });
        }));
        var integration = new ArchiverIntegration(client, Connection());
        var probe = Assert.IsType<ProbeResult>(await integration.DispatchAsync(Request(IntegrationOperations.Probe, new { }), default));
        Assert.Contains(probe.Capabilities, c => c.Kind == IntegrationCapabilities.TransferExecutor && c.EntityKinds.Contains(MediaKinds.Image));
        var inspected = Assert.IsType<TransferInspection>(await integration.DispatchAsync(Request(IntegrationOperations.Inspect,
            new InspectTransferInput("https://source.test/image", MediaKinds.Image, 1)), default));
        await integration.DispatchAsync(Request(IntegrationOperations.Submit,
            new SubmitTransferInput(Guid.NewGuid(), inspected.CanonicalUrl, inspected.SelectionId, inspected.Revision, ["image"], 1, 1000)), default);
    }
    private static IntegrationRequest Request<T>(string operation, T input) => new(IntegrationProtocol.Name, 1, Guid.NewGuid(), operation, Connection(), JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json));
    private static SystemInfo System() => new(Instance, "1.0", "fixture", [ArchiverWire.Inspect, ArchiverWire.Submit, ArchiverWire.Cancel, ArchiverWire.CancelOperation, ArchiverWire.Artifacts, ArchiverWire.Retention, ArchiverWire.Receipts], [ArchiverWire.Profile], 1, 1000, 7, 30);
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, IntegrationProtocol.Json)) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(action(request));
    }
}
