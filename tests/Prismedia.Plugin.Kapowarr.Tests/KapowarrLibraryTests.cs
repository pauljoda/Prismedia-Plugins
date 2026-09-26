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
        Assert.All(catalog.Libraries, library => Assert.Equal([ManagerProtocol.ComicSeries], library.EntityKinds));
    }

    [Fact]
    public async Task ProbeDeclaresExactIssueControlsWithoutInventingInstallationIdentity() {
        using var fixture = new Fixture();
        var probe = Assert.IsType<ProbeResult>(await fixture.Call(IntegrationOperations.Probe, new { }));
        Assert.Null(probe.InstanceId);
        Assert.Equal("V1.3.2", probe.Version);
        Assert.Equal([ManagerDiscovery.Search, ManagerProtocol.Options, ManagerControls.Reconcile, ManagerControls.Configure, ManagerControls.Request, ManagerCreation.Lookup, ManagerCreation.Ensure],
            Assert.Single(probe.Capabilities, c => c.Kind == ManagerProtocol.ExternalManager).Operations);
        Assert.All(probe.Capabilities, c => Assert.Equal([ManagerProtocol.ComicSeries], c.EntityKinds));
        Assert.All(fixture.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task PagesAreConnectionAndQueryScopedAndIssueCountsAreNotFileCounts() {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = new[] { Volume(2), Volume(1) };
        var query = new ManagedLibraryQuery(ManagerProtocol.ComicSeries, null, null, 1);
        var page = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, query));
        Assert.Equal("1", Assert.Single(page.Items).RemoteId);
        Assert.Null(page.Items[0].RemoteFileCount);
        var next = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, query with { Cursor = page.NextCursor }));
        Assert.Equal("2", Assert.Single(next.Items).RemoteId);
        Assert.Null(next.NextCursor);
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.SearchLibrary, query with { Cursor = page.NextCursor, Query = "changed" }));
        fixture.Connection = fixture.Connection with { Id = Guid.NewGuid() };
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.SearchLibrary, query with { Cursor = page.NextCursor }));
    }

    [Fact]
    public async Task ExistingHoldingsSearchMatchesTitlesAndExactComicVineIds() {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = new[] { Volume(1), Volume(2) };
        foreach (var query in new[] { "comic 2", "4050-1002" }) {
            var page = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, new ManagedLibraryQuery(ManagerProtocol.ComicSeries, query, null, 25)));
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
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "1", [File(1)], volumeId: 2)]);
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        fixture.Results["volumes/1"] = Volume(1);
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input with { ExpectedExternalIds = new Dictionary<string, string> { [ComicVineIdentity.Namespace] = "4050-9999" } }));
    }

    [Fact]
    public async Task ConflictingFileEvidenceIsRejected() {
        using var fixture = new Fixture();
        foreach (var issues in new[] {
            new[] { Issue(1, "1", [File(1)]), Issue(2, "2", [File(1, 999)]) },
            new[] { Issue(1, "1", [File(1)]), Issue(1, "1", [File(1)]) }
        }) {
            fixture.Results["volumes/1"] = Volume(1, issues);
            await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        }
    }

    [Fact]
    public async Task MultipleFilesForOneIssueFailOnlyOperationsTargetingThatIssue() {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = Volume(1, [
            Issue(1, "1", [File(1), File(2)]), Issue(2, "2", [File(2)]), Issue(3, "3", [File(3)]), Issue(4, "4", [])
        ], monitored: true);

        var snapshot = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        Assert.Equal(4, snapshot.ComicIssues!.Count);
        var file = Assert.Single(snapshot.Files);
        Assert.Equal("3", Assert.Single(file.Targets).RemoteId);
        Assert.Equal(1, snapshot.Item.RemoteFileCount);

        var ambiguous = await Assert.ThrowsAsync<ManagedMutationRejection>(() => fixture.Call(ManagerControls.Reconcile,
            new ReconcileManagedInput(new(Input, [new("1", MediaKinds.Comic, IssueLabel: "1")]))));
        Assert.Contains("2 files to issue 1", ambiguous.Message);
        var sharing = await Assert.ThrowsAsync<ManagedMutationRejection>(() => fixture.Call(ManagerControls.Reconcile,
            new ReconcileManagedInput(new(Input, [new("2", MediaKinds.Comic, IssueLabel: "2")]))));
        Assert.Contains("Issue 2 shares a file", sharing.Message);
        var request = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request, new RequestManagedInput(Guid.NewGuid(),
            new(Input, [new("1", MediaKinds.Comic, IssueLabel: "1")]), "/comics/Comic", null)));
        Assert.Equal(ManagerControls.Rejected, request.Outcome);
        Assert.Contains("Keep one file for that issue", request.Problem);

        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile,
            new ReconcileManagedInput(new(Input, [new("4", MediaKinds.Comic, IssueLabel: "4")]))));
        Assert.False(Assert.Single(state.Targets).Monitored);
        Assert.DoesNotContain(fixture.Requests, request => request.Method != HttpMethod.Get);
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
        Assert.Equal("4000-2001", issue.ExternalIds![ComicVineIdentity.Namespace]);
        Assert.True(issue.Monitored);
        fixture.Results["rootfolder"] = new[] { new { id = 1, folder = "/comics/", size = 1000, free = 500 } };
        var options = Assert.IsType<ManagerOptions>(await fixture.Call(ManagerProtocol.Options, new ManagerOptionsInput(ManagerProtocol.ComicSeries)));
        Assert.Empty(options.Profiles);
        Assert.Null(Assert.Single(options.Roots).Accessible);
    }

    [Fact]
    public async Task LookupPinsOneExistingIssueByComicVineIdentityAndExactLabelWithoutMutation() {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = new[] { Volume(1), Volume(2) };
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "½", []), Issue(2, "0.5", [])]);
        var work = new ManagedLookupInput(ManagerProtocol.ComicSeries,
            new Dictionary<string, string> { [ComicVineIdentity.Namespace] = "4050-1001" },
            [new(MediaKinds.Comic, new Dictionary<string, string> { [ComicVineIdentity.Namespace] = "4000-2001" }, IssueLabel: "½")]);
        var result = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, work));
        Assert.Equal("1", result.Existing!.Item.RemoteId);
        var resolved = Assert.Single(result.Targets!);
        Assert.Equal("1", resolved.RemoteId);
        Assert.Equal("½", resolved.IssueLabel);
        Assert.Equal("4000-2001", resolved.ExternalIds[ComicVineIdentity.Namespace]);
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));

        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Lookup,
            work with { Targets = [work.Targets![0] with { IssueLabel = "0.5" }] }));
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Lookup,
            work with { Targets = [work.Targets![0] with { ExternalIds = new Dictionary<string, string> { [ComicVineIdentity.Namespace] = "4000-9999" } }] }));
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Lookup,
            work with { ExternalIds = new Dictionary<string, string> { [ComicVineIdentity.Namespace] = "4050-9999" } }));
    }

    [Fact]
    public async Task MissingRunLookupConfirmsExactComicVineCandidateWithoutMutation() {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = Array.Empty<object>();
        fixture.Results["volumes/search"] = new[] { new { comicvine_id = 1001, title = "Comic 1", year = 2024, already_added = (int?)null } };

        var result = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup, Work()));

        Assert.Null(result.Existing);
        Assert.Null(result.Targets);
        Assert.Equal("4050-1001", result.Candidate.ExternalIds[ComicVineIdentity.Namespace]);
        Assert.Contains(fixture.Requests, request => request.RequestUri!.Query.Contains("query=4050-1001", StringComparison.Ordinal));
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task CatalogDiscoveryCarriesExactRunIdsWithoutMutatingKapowarr() {
        using var fixture = new Fixture();
        fixture.Results["volumes/search"] = new[] {
            new { comicvine_id = 1001, title = "Comic 1", year = 2024, already_added = (int?)null },
            new { comicvine_id = 1002, title = "Comic 2", year = 2025, already_added = (int?)2 }
        };

        var page = Assert.IsType<ManagedDiscoveryPage>(await fixture.Call(ManagerDiscovery.Search,
            new ManagedDiscoveryQuery(ManagerProtocol.ComicSeries, "Comic", 1)));

        var candidate = Assert.Single(page.Items);
        Assert.Equal("4050-1001", candidate.ExternalIds[ComicVineIdentity.Namespace]);
        Assert.Equal("Comic 1", candidate.Title);
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains("query=Comic", Assert.Single(fixture.Requests, request => request.RequestUri!.AbsolutePath.EndsWith("/volumes/search", StringComparison.Ordinal)).RequestUri!.Query);
    }

    [Fact]
    public async Task CatalogBadRequestNamesTheComicVineSettingWithoutLeakingItsResponse() {
        using var fixture = new Fixture { SearchStatus = HttpStatusCode.BadRequest };
        var error = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerDiscovery.Search,
            new ManagedDiscoveryQuery(ManagerProtocol.ComicSeries, "Comic", 1)));

        Assert.Contains("Comic Vine API key", error.Message);
        Assert.DoesNotContain(Fixture.Secret, error.Message);
    }

    [Fact]
    public async Task EnsureAddsUnmonitoredRunWithoutSearchingAndPinsTheSelectedIssue() {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = Array.Empty<object>();
        fixture.Results["volumes/search"] = new[] { new { comicvine_id = 1001, title = "Comic 1", year = 2024, already_added = (int?)null } };
        fixture.Results["rootfolder"] = new[] { new { id = 7, folder = "/comics/" } };
        fixture.PostResults["volumes"] = new { id = 17 };
        fixture.Results["volumes/17"] = new { id = 17, comicvine_id = 1001, title = "Comic 1", year = 2024,
            monitored = false, folder = "/comics/Comic 1", issues = new[] {
                new { id = 24, volume_id = 17, comicvine_id = 2001, issue_number = "½", title = "Half",
                    monitored = false, files = Array.Empty<object>() }
            } };

        var result = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, Intent()));

        Assert.Equal(ManagerControls.Applied, result.Outcome);
        Assert.True(result.Created);
        Assert.Equal("17", result.Holding!.Item.RemoteId);
        Assert.Equal("24", Assert.Single(result.Targets!).RemoteId);
        using var body = JsonDocument.Parse(Assert.Single(fixture.Bodies));
        Assert.Equal(1001, body.RootElement.GetProperty("comicvine_id").GetInt32());
        Assert.Equal(7, body.RootElement.GetProperty("root_folder_id").GetInt32());
        Assert.False(body.RootElement.GetProperty("monitor").GetBoolean());
        Assert.Equal("none", body.RootElement.GetProperty("monitoring_scheme").GetString());
        Assert.False(body.RootElement.GetProperty("monitor_new_issues").GetBoolean());
        Assert.False(body.RootElement.GetProperty("auto_search").GetBoolean());
    }

    [Fact]
    public async Task EnsureCanAddOnlyTheRunBeforeAnIssueIsSelected() {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = Array.Empty<object>();
        fixture.Results["volumes/search"] = new[] { new { comicvine_id = 1001, title = "Comic 1", year = 2024, already_added = (int?)null } };
        fixture.Results["rootfolder"] = new[] { new { id = 7, folder = "/comics/" } };
        fixture.PostResults["volumes"] = new { id = 17 };
        fixture.Results["volumes/17"] = new { id = 17, comicvine_id = 1001, title = "Comic 1", year = 2024,
            monitored = false, folder = "/comics/Comic 1", issues = Array.Empty<object>() };

        var result = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure,
            Intent() with { Work = RunWork() }));

        Assert.Equal(ManagerControls.Applied, result.Outcome);
        Assert.True(result.Created);
        Assert.Empty(result.Holding!.Files);
        Assert.Null(result.Targets);
    }

    [Fact]
    public async Task EnsureRejectsChangedRootBeforeMutation() {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = Array.Empty<object>();
        fixture.Results["volumes/search"] = new[] { new { comicvine_id = 1001, title = "Comic 1", year = 2024, already_added = (int?)null } };
        fixture.Results["rootfolder"] = new[] { new { id = 7, folder = "/other/" } };

        var result = Assert.IsType<EnsureManagedResult>(await fixture.Call(ManagerCreation.Ensure, Intent()));

        Assert.Equal(ManagerControls.Rejected, result.Outcome);
        Assert.Empty(fixture.Bodies);
    }

    [Theory]
    [InlineData("/comics-archive/Comic 1")]
    [InlineData("/comics")]
    [InlineData("/Comics/Comic 1")]
    public async Task EnsureTreatsARunOutsideTheReviewedRootAsUncertain(string folder) {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = Array.Empty<object>();
        fixture.Results["volumes/search"] = new[] { new { comicvine_id = 1001, title = "Comic 1", year = 2024, already_added = (int?)null } };
        fixture.Results["rootfolder"] = new[] { new { id = 7, folder = "/comics/" } };
        fixture.PostResults["volumes"] = new { id = 17 };
        fixture.Results["volumes/17"] = new { id = 17, comicvine_id = 1001, title = "Comic 1", year = 2024,
            monitored = false, folder, issues = Array.Empty<object>() };

        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Ensure, Intent() with { Work = RunWork() }));

        Assert.Single(fixture.Bodies);
    }

    [Fact]
    public async Task EnsureTreatsAnUnconfirmedIssueAfterAddAsUncertain() {
        using var fixture = new Fixture();
        fixture.Results["volumes"] = Array.Empty<object>();
        fixture.Results["volumes/search"] = new[] { new { comicvine_id = 1001, title = "Comic 1", year = 2024, already_added = (int?)null } };
        fixture.Results["rootfolder"] = new[] { new { id = 7, folder = "/comics/" } };
        fixture.PostResults["volumes"] = new { id = 17 };
        fixture.Results["volumes/17"] = new { id = 17, comicvine_id = 1001, title = "Comic 1", year = 2024,
            monitored = false, folder = "/comics/Comic 1", issues = Array.Empty<object>() };

        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Ensure, Intent()));

        Assert.Single(fixture.Bodies);
    }

    [Fact]
    public async Task MissingMonitorFlagsAreRejectedInsteadOfReportedAsOff() {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = new { id = 1, comicvine_id = 1001, title = "Comic 1", year = 2024,
            folder = "/comics/Comic", issues = new[] { Issue(1, "1", []) } };
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));

        fixture.Results["volumes/1"] = Volume(1, [new { id = 1, volume_id = 1, comicvine_id = 2001,
            issue_number = "1", title = "Issue 1", files = Array.Empty<object>() }]);
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));
    }

    [Fact]
    public async Task ReconcileRequiresTheExactIssueLabelAndExposesSingleIssueControls() {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "12.5", [], monitored: true), Issue(2, "13", [])], monitored: true);
        var target = new ManagedControlTarget("1", MediaKinds.Comic, IssueLabel: "12.5");
        var scope = new ManagedControlScope(Input, [target]);
        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(scope)));
        Assert.Null(state.Item.ProfileId);
        Assert.True(Assert.Single(state.Targets).Monitored);
        Assert.Equal(target, state.Targets[0].Target);
        Assert.True(state.Capabilities.CanSearch);
        Assert.True(state.Capabilities.CanChangeMonitoring);
        Assert.False(state.Capabilities.CanChangeProfile);
        Assert.All(fixture.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));

        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerControls.Reconcile,
            new ReconcileManagedInput(scope with { Targets = [target with { IssueLabel = "12" }] })));
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerControls.Reconcile,
            new ReconcileManagedInput(scope with { Targets = [target with { RemoteId = "2" }] })));
    }

    [Fact]
    public async Task MonitoringWritesOnlyTheReviewedIssueAndRejectsChangedState() {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "½", [], monitored: false), Issue(2, "12.5", [], monitored: true)], monitored: true);
        fixture.Results["issues/1"] = Issue(1, "½", [], monitored: true);
        var scope = new ManagedControlScope(Input, [new("1", MediaKinds.Comic, IssueLabel: "½")]);
        var input = new ConfigureManagedInput(Guid.NewGuid(), scope, "/comics/Comic", null,
            new Dictionary<string, bool> { ["1"] = false }, new(Monitored: true));
        Assert.Equal(ManagerControls.Applied, Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, input)).Outcome);
        var write = Assert.Single(fixture.Requests, request => request.Method == HttpMethod.Put);
        Assert.Equal("/kapowarr/api/issues/1", write.RequestUri!.AbsolutePath);
        Assert.Equal("{\"monitored\":true}", Assert.Single(fixture.Bodies));
        fixture.Requests.Clear();
        fixture.Bodies.Clear();
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "½", [], monitored: true), Issue(2, "12.5", [], monitored: true)], monitored: true);
        var changed = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, input));
        Assert.Equal(ManagerControls.Rejected, changed.Outcome);
        Assert.Contains("changed in Kapowarr since review", changed.Problem);
        Assert.DoesNotContain(fixture.Requests, request => request.Method == HttpMethod.Put);
        var moved = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, input with { ExpectedPath = "/comics/Other" }));
        Assert.Equal(ManagerControls.Rejected, moved.Outcome);
        Assert.Contains("folder changed", moved.Problem);
    }

    [Fact]
    public async Task MonitoringAnIssueInAnUnmonitoredRunAlsoMonitorsOnlyTheRun() {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "½", []), Issue(2, "12.5", [File(2)], monitored: true)]);
        fixture.Results["issues/1"] = Issue(1, "½", [], monitored: true);
        fixture.OnWrite = (path, _) => {
            if (path == "volumes/1") fixture.Results["volumes/1"] = Volume(1, [Issue(1, "½", [], monitored: true),
                Issue(2, "12.5", [File(2)], monitored: true)], monitored: true);
        };
        var scope = new ManagedControlScope(Input, [new("1", MediaKinds.Comic, IssueLabel: "½")]);
        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(scope)));
        Assert.False(state.Capabilities.CanSearch);
        Assert.True(state.Capabilities.CanChangeMonitoring);

        var result = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, new ConfigureManagedInput(Guid.NewGuid(),
            scope, "/comics/Comic", null, new Dictionary<string, bool> { ["1"] = false }, new(Monitored: true))));

        Assert.Equal(ManagerControls.Applied, result.Outcome);
        var writes = fixture.Requests.Where(request => request.Method == HttpMethod.Put).Select(request => request.RequestUri!.AbsolutePath).ToArray();
        Assert.Equal(["/kapowarr/api/issues/1", "/kapowarr/api/volumes/1"], writes);
        Assert.Equal(["{\"monitored\":true}", "{\"monitored\":true}"], fixture.Bodies);
        var searchable = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(scope)));
        Assert.True(Assert.Single(searchable.Targets).Monitored);
        Assert.True(searchable.Capabilities.CanSearch);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MonitoringRefusesToWidenAnUnmonitoredRunsSearch(bool monitorNewIssues, bool otherOpenIssueMonitored) {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "½", []), Issue(2, "12.5", [], monitored: otherOpenIssueMonitored)],
            monitorNewIssues: monitorNewIssues);
        var scope = new ManagedControlScope(Input, [new("1", MediaKinds.Comic, IssueLabel: "½")]);

        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(scope)));
        var result = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, new ConfigureManagedInput(Guid.NewGuid(),
            scope, "/comics/Comic", null, new Dictionary<string, bool> { ["1"] = false }, new(Monitored: true))));

        Assert.False(state.Capabilities.CanChangeMonitoring);
        Assert.Equal(state.Capabilities.MonitoringUnavailableReason, result.Problem);
        Assert.Equal(ManagerControls.Rejected, result.Outcome);
        Assert.Empty(fixture.Bodies);
    }

    [Fact]
    public async Task DefiniteWriteRefusalIsRejectedWithItsProblem() {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "½", [])], monitored: true);
        fixture.Statuses["issues/1"] = (HttpStatusCode.NotFound, "IssueNotFound");
        var scope = new ManagedControlScope(Input, [new("1", MediaKinds.Comic, IssueLabel: "½")]);

        var result = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure, new ConfigureManagedInput(Guid.NewGuid(),
            scope, "/comics/Comic", null, new Dictionary<string, bool> { ["1"] = false }, new(Monitored: true))));

        Assert.Equal(ManagerControls.Rejected, result.Outcome);
        Assert.Equal("Kapowarr refused this change (HTTP 404, IssueNotFound).", result.Problem);
        fixture.Statuses["issues/1"] = (HttpStatusCode.InternalServerError, "Unknown");
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerControls.Configure, new ConfigureManagedInput(Guid.NewGuid(),
            scope, "/comics/Comic", null, new Dictionary<string, bool> { ["1"] = false }, new(Monitored: true))));
    }

    [Fact]
    public async Task SearchQueuesOnlyOneReviewedIssueAndNeverClaimsTaskCompletion() {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = Volume(1, [Issue(1, "½", []), Issue(2, "12.5", [], monitored: true)], monitored: true);
        fixture.Results["system/tasks"] = new { id = 17 };
        var scope = new ManagedControlScope(Input, [new("2", MediaKinds.Comic, IssueLabel: "12.5")]);
        var input = new RequestManagedInput(Guid.NewGuid(), scope, "/comics/Comic", null);
        var result = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request, input));
        Assert.Equal(ManagerControls.Accepted, result.Outcome);
        Assert.Equal("17", result.Command!.Reference.Id);
        Assert.Equal(ManagerControls.Pending, result.Command.Status);
        var write = Assert.Single(fixture.Requests, request => request.Method == HttpMethod.Post);
        Assert.Equal("/kapowarr/api/system/tasks", write.RequestUri!.AbsolutePath);
        Assert.Equal("{\"cmd\":\"auto_search_issue\",\"volume_id\":1,\"issue_id\":2}", Assert.Single(fixture.Bodies));
        fixture.Requests.Clear();
        fixture.Bodies.Clear();
        var observed = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile,
            new ReconcileManagedInput(scope, result.Command.Reference)));
        Assert.Equal(ManagerControls.Unknown, observed.Command!.Status);
        Assert.DoesNotContain(fixture.Requests, request => request.Method != HttpMethod.Get);
    }

    [Theory]
    [InlineData(false, true, false, "only monitored runs")]
    [InlineData(true, false, false, "only monitored issues")]
    [InlineData(true, true, true, "already has a file")]
    public async Task SearchIsRejectedWhenKapowarrWouldNotRunIt(bool runMonitored, bool issueMonitored, bool hasFile, string problem) {
        using var fixture = new Fixture();
        fixture.Results["volumes/1"] = Volume(1, [Issue(2, "12.5", hasFile ? [File(2)] : [], monitored: issueMonitored)], monitored: runMonitored);
        var scope = new ManagedControlScope(Input, [new("2", MediaKinds.Comic, IssueLabel: "12.5")]);

        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(scope)));
        var result = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request,
            new RequestManagedInput(Guid.NewGuid(), scope, "/comics/Comic", null)));

        Assert.False(state.Capabilities.CanSearch);
        Assert.Equal(ManagerControls.Rejected, result.Outcome);
        Assert.Contains(problem, result.Problem);
        Assert.DoesNotContain(fixture.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task RemovalIsConfirmedOnlyWhenTheCompleteCatalogOmitsTheRun() {
        using var fixture = new Fixture();
        fixture.Statuses["volumes/1"] = (HttpStatusCode.NotFound, "VolumeNotFound");
        fixture.Results["volumes"] = new[] { Volume(2) };
        var removed = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        Assert.Equal(IntegrationErrorCodes.ManagedItemNotFound, removed.Code);

        fixture.Results["volumes"] = new[] { Volume(1), Volume(2) };
        var raced = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        Assert.Null(raced.Code);

        fixture.Statuses["volumes/1"] = (HttpStatusCode.NotFound, null);
        fixture.Results["volumes"] = new[] { Volume(2) };
        var proxy = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Input));
        Assert.Null(proxy.Code);

        fixture.Statuses["volumes/1"] = (HttpStatusCode.NotFound, "VolumeNotFound");
        var reconcile = await Assert.ThrowsAsync<ManagedMutationRejection>(() => fixture.Call(ManagerControls.Reconcile,
            new ReconcileManagedInput(new(Input, [new("1", MediaKinds.Comic, IssueLabel: "1")]))));
        Assert.Null(reconcile.Code);
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("1.2.0")]
    [InlineData("1.3.2-dev")]
    public async Task UntestedApiFamiliesFailClosed(string version) {
        using var fixture = new Fixture(); fixture.Results["system/about"] = new { version };
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
    }

    [Fact]
    public async Task TransportUsesOnlyConfiguredPrefixAndNeverReturnsCredentialBearingErrors() {
        using var fixture = new Fixture();
        fixture.Status = HttpStatusCode.Redirect;
        var error = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
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
            var error = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
            Assert.DoesNotContain(Fixture.Secret, error.Message);
        }
    }

    internal static ManagedItemInput Input => new(ManagerProtocol.ComicSeries, "1", new Dictionary<string, string> { [ComicVineIdentity.Namespace] = "4050-1001" });
    private static ManagedLookupInput Work() => new(ManagerProtocol.ComicSeries,
        new Dictionary<string, string> { [ComicVineIdentity.Namespace] = "4050-1001" },
        [new(MediaKinds.Comic, new Dictionary<string, string> { [ComicVineIdentity.Namespace] = "4000-2001" }, IssueLabel: "½")]);
    private static ManagedLookupInput RunWork() => Work() with { Targets = null };
    private static EnsureManagedInput Intent() => new(Guid.NewGuid(), Work(), null!, "7", "/comics/");
    private static object Volume(int id, object[]? issues = null, bool monitored = false, bool monitorNewIssues = false) => new {
        id, comicvine_id = 1000 + id, title = "Comic " + id, year = 2024, monitored, monitor_new_issues = monitorNewIssues,
        folder = "/comics/Comic", issues_downloaded = 20, issues = issues ?? [],
        general_files = new[] { new { id = 500, filepath = "/comics/Comic/cover.jpg", size = 50 } } };
    private static object File(int id, long size = 128) => new { id, filepath = $"/comics/Comic/issue{id}.cbz", size };
    private static object Issue(int id, string label, object[] files, int volumeId = 1, bool monitored = false) => new { id, volume_id = volumeId, comicvine_id = 2000 + id,
        issue_number = label, calculated_issue_number = 0.5, title = "Chapter " + id, monitored, files };

    private sealed class Fixture : HttpMessageHandler {
        internal const string Secret = "fixture/secret+key";
        internal Dictionary<string, object> Results { get; } = new() { ["system/about"] = new { version = "V1.3.2" } };
        internal Dictionary<string, object> PostResults { get; } = new();
        internal List<HttpRequestMessage> Requests { get; } = [];
        internal List<string> Bodies { get; } = [];
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal HttpStatusCode? SearchStatus { get; set; }
        internal Dictionary<string, (HttpStatusCode Status, string? Error)> Statuses { get; } = new();
        internal Action<string, string>? OnWrite { get; set; }
        internal string? Raw { get; set; }
        internal ConnectionContext Connection { get; set; } = new(Guid.NewGuid(), "http://manager.test/kapowarr/", null,
            new Dictionary<string, string>(), new Dictionary<string, string> { [KapowarrClient.ApiKey] = Secret });
        internal async Task<object> Call(string operation, object input) {
            using var client = new KapowarrClient(Connection, this);
            return await new KapowarrLibrary(client).DispatchAsync(new(IntegrationProtocol.Name, IntegrationProtocol.Version, Guid.NewGuid(),
                operation, Connection, JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath.Split("/api/")[1];
            if (request.Content is not null) {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                Bodies.Add(body);
                if (!Statuses.ContainsKey(path)) OnWrite?.Invoke(path, body);
            }
            if (Statuses.TryGetValue(path, out var failure))
                return new HttpResponseMessage(failure.Status) {
                    Content = new StringContent(failure.Error is null ? "<html>Not Found</html>"
                        : JsonSerializer.Serialize(new { error = failure.Error, result = new { } })) };
            var status = path == "volumes/search" ? SearchStatus ?? Status : Status;
            return new HttpResponseMessage(request.Method == HttpMethod.Post && status == HttpStatusCode.OK ? HttpStatusCode.Created : status) {
                Content = new StringContent(Raw ?? JsonSerializer.Serialize(new { error = (string?)null,
                    result = request.Method == HttpMethod.Post && PostResults.TryGetValue(path, out var posted)
                        ? posted : Results.GetValueOrDefault(path) })) };
        }
        protected override void Dispose(bool disposing) { }
    }
}
