using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.Kapowarr;

namespace Prismedia.Plugin.Kapowarr.Tests;

public sealed class KapowarrLibraryTests {
    [Fact]
    public async Task ListsProviderLibrariesWithStableRootIdentity() {
        using var fixture = new Fixture();
        fixture.Results["rootfolder"] = new[] {
            new { id = 1, folder = "/comics/main/", size = 1000, free = 500 },
            new { id = 2, folder = "D:\\Comics", size = 2000, free = 1000 },
        };

        var catalog = Assert.IsType<ProviderLibraryCatalog>(await fixture.Call(ManagerProtocol.ListLibraries, new { }));

        Assert.Equal(["1", "2"], catalog.Libraries.Select(library => library.RemoteId));
        Assert.Equal(["main", "Comics"], catalog.Libraries.Select(library => library.Label));
        Assert.All(catalog.Libraries, library => Assert.Equal([KapowarrCodes.ComicSeries], library.EntityKinds));
    }

    [Fact]
    public async Task ProbeDeclaresOnlyExistingLibraryReadsWithoutInventingInstallationIdentity() {
        using var fixture = new Fixture();
        var probe = Assert.IsType<ProbeResult>(await fixture.Call(IntegrationOperations.Probe, new { }));
        Assert.Null(probe.InstanceId);
        Assert.Equal("V1.3.2", probe.Version);
        Assert.Equal([ManagerProtocol.Options], Assert.Single(probe.Capabilities, c => c.Kind == ManagerProtocol.ExternalManager).Operations);
        Assert.All(probe.Capabilities, c => Assert.Equal([KapowarrCodes.ComicSeries], c.EntityKinds));
        Assert.All(fixture.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task PagesAreConnectionAndQueryScopedAndIssueCountsAreNotFileCounts() {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = new[] { Volume(2), Volume(1) };
        var query = new ManagedLibraryQuery(KapowarrCodes.ComicSeries, null, null, 1);
        var page = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, query));
        Assert.Equal("1", Assert.Single(page.Items).RemoteId);
        Assert.Null(page.Items[0].RemoteFileCount);
        var next = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, query with { Cursor = page.NextCursor }));
        Assert.Equal("2", Assert.Single(next.Items).RemoteId);
        Assert.Null(next.NextCursor);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.SearchLibrary, query with { Cursor = page.NextCursor, Query = "changed" }));
        fixture.Connection = fixture.Connection with { Id = Guid.NewGuid() };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.SearchLibrary, query with { Cursor = page.NextCursor }));
    }

    [Fact]
    public async Task ExistingHoldingsSearchMatchesTitlesAndExactComicVineIds() {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = new[] { Volume(1), Volume(2) };
        foreach (var query in new[] { "comic 2", "4050-1002" }) {
            var page = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(KapowarrCodes.ComicSeries, query, null, 25)));
            Assert.Equal("2", Assert.Single(page.Items).RemoteId);
        }
    }

    [Fact]
    public async Task CombinedFilesRetainExactFractionalLabelsAndExcludeGeneralSidecars() {
        using var fixture = new Fixture();
        var file = File(1);
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "½", [file]), Issue(2, "12.5", [file])]);
        var snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        Assert.Equal(1, snapshot.Item.RemoteFileCount);
        var targets = Assert.Single(snapshot.Files).Targets;
        Assert.Equal(2, targets.Count);
        Assert.Equal(["½", "12.5"], targets.Select(t => t.IssueLabel));
        Assert.All(targets, t => { Assert.Equal(MediaKinds.Comic, t.EntityKind); Assert.Null(t.EpisodeNumber); });
    }

    [Fact]
    public async Task ReusedVolumeIdsAndCrossVolumeIssuesAreRejected() {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = Volume(2);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "1", [File(1)], volumeId: 2)]);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        fixture.Results["volumes/1"] = Volume(1);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input with { ExpectedExternalIds = new Dictionary<string, string> { [KapowarrCodes.ComicVine] = "4050-9999" } }));
    }

    [Fact]
    public async Task ConflictingFileEvidenceAndMultipleRenditionsRequireReview() {
        using var fixture = new Fixture();
        foreach (var issues in new[] {
            new[] { Issue(1, "1", [File(1), File(2)]) },
            new[] { Issue(1, "1", [File(1)]), Issue(2, "2", [File(1, 999)]) },
            new[] { Issue(1, "1", [File(1)]), Issue(1, "1", [File(1)]) }
        }) {
            fixture.Results["volumes/1"] = Volume(1, issues);
            await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        }
    }

    [Fact]
    public async Task MissingFilesRemainMissingAndExternalRootsDoNotInventProfilesOrReadability() {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "12.5", [], monitored: true)]);
        var snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        Assert.Empty(snapshot.Files);
        var issue = Assert.Single(snapshot.ComicIssues!);
        Assert.Equal("1", issue.RemoteId);
        Assert.Equal("12.5", issue.IssueLabel);
        Assert.True(issue.Monitored);
        fixture.Results["rootfolder"] = new[] { new { id = 1, folder = "/comics/", size = 1000, free = 500 } };
        var options = Assert.IsType<ManagerOptions>(await fixture.Call(ManagerProtocol.Options, new ManagerOptionsInput(KapowarrCodes.ComicSeries)));
        Assert.Empty(options.Profiles);
        Assert.Null(Assert.Single(options.Roots).Accessible);
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("1.2.0")]
    [InlineData("1.3.2-dev")]
    public async Task UntestedApiFamiliesFailClosed(string version) {
        using var fixture = new Fixture(); fixture.Results["system/about"] = new { version };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
    }

    [Fact]
    public async Task TransportUsesOnlyConfiguredPrefixAndNeverReturnsCredentialBearingErrors() {
        using var fixture = new Fixture();
        fixture.Status = HttpStatusCode.Redirect;
        var error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
        Assert.DoesNotContain(Fixture.Secret, error.Message);
        var uri = Assert.Single(fixture.Requests).RequestUri!;
        Assert.Equal("/kapowarr/api/system/about", uri.AbsolutePath);
        Assert.Equal("?api_key=" + Uri.EscapeDataString(Fixture.Secret), uri.Query);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task ErrorEnvelopesMalformedJsonAndOversizedResponsesAreNotEmptyLibraries() {
        using var fixture = new Fixture();
        foreach (var response in new[] { "{\"error\":\"" + Fixture.Secret + "\",\"result\":[]}", "not json", new string('x', 8 * 1024 * 1024 + 1) }) {
            fixture.Raw = response;
            var error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
            Assert.DoesNotContain(Fixture.Secret, error.Message);
        }
    }

    internal static ManagedItemInput Input => new(KapowarrCodes.ComicSeries, "1", new Dictionary<string, string> { [KapowarrCodes.ComicVine] = "4050-1001" });
    private static object Volume(int id, object[]? issues = null) => new { id, comicvine_id = 1000 + id, title = "Comic " + id, year = 2024,
        monitored = false, folder = "/comics/Comic", issues_downloaded = 20, issues = issues ?? [], general_files = new[] { new { id = 500, filepath = "/comics/Comic/cover.jpg", size = 50 } } };
    private static object File(int id, long size = 128) => new { id, filepath = $"/comics/Comic/issue{id}.cbz", size };
    private static object Issue(int id, string label, object[] files, int volumeId = 1, bool monitored = false) => new { id, volume_id = volumeId, comicvine_id = 2000 + id,
        issue_number = label, calculated_issue_number = 0.5, title = "Chapter " + id, monitored, files };

    private sealed class Fixture : HttpMessageHandler {
        internal const string Secret = "fixture/secret+key";
        internal Dictionary<string, object> Results { get; } = new() { ["system/about"] = new { version = "V1.3.2" } };
        internal List<HttpRequestMessage> Requests { get; } = [];
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal string? Raw { get; set; }
        internal ConnectionContext Connection { get; set; } = new(Guid.NewGuid(), "http://manager.test/kapowarr/", null,
            new Dictionary<string, string>(), new Dictionary<string, string> { [KapowarrCodes.ApiKey] = Secret });
        internal async Task<object> Call(string operation, object input) {
            using var client = new KapowarrClient(Connection, this);
            return await new KapowarrLibrary(client).DispatchAsync(new(IntegrationProtocol.Name, IntegrationProtocol.Version, Guid.NewGuid(),
                operation, Connection, JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath.Split("/api/")[1];
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Raw ?? JsonSerializer.Serialize(new { error = (string?)null, result = Results.GetValueOrDefault(path) })) });
        }
        protected override void Dispose(bool disposing) { }
    }
}
