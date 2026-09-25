using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Web;
using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.LazyLibrarian;

namespace Prismedia.Plugin.LazyLibrarian.Tests;

public sealed class LazyLibrarianLibraryTests : IDisposable {
    private readonly string audioRoot = Path.Combine(Path.GetTempPath(), "prismedia-lazy-audio-" + Guid.NewGuid().ToString("N"));

    public LazyLibrarianLibraryTests() => Directory.CreateDirectory(audioRoot);

    public void Dispose() => Directory.Delete(audioRoot, recursive: true);

    [Fact]
    public async Task OneWorkExposesSeparateRenditionFilesAndRoots() {
        await File.WriteAllBytesAsync(Path.Combine(audioRoot, "example.m4b"), new byte[34080]);
        var fixture = Fixture(audioPath: "/audio/example.m4b");

        var probe = Assert.IsType<ProbeResult>(await fixture.Call(IntegrationOperations.Probe, new { }));
        Assert.Equal("2b48097a", probe.Version);
        Assert.Contains(probe.Capabilities, capability => capability.Kind == ManagerProtocol.ExternalManager
            && capability.Operations.Contains(ManagerControls.Request) && capability.Operations.Contains(ManagerControls.Configure));
        var roots = Assert.IsType<ProviderLibraryCatalog>(await fixture.Call(ManagerProtocol.ListLibraries, new { }));
        Assert.Equal(["/books", "/audio"], roots.Libraries.Select(root => root.RemotePath).ToArray());

        var ebook = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Ebook)));
        Assert.Equal("/books/OL450063W", ebook.Path);
        Assert.Equal("/books/example.epub", Assert.Single(ebook.Files).Path);
        Assert.Equal(474161, ebook.Files[0].SizeBytes);
        Assert.Equal(MediaKinds.Book, Assert.Single(ebook.Files[0].Targets).EntityKind);

        var audio = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Audiobook)));
        Assert.Equal("/audio/OL450063W", audio.Path);
        Assert.Equal("/audio/example.m4b", Assert.Single(audio.Files).Path);
        Assert.Equal(34080, audio.Files[0].SizeBytes);
        Assert.Equal(ManagerProtocol.AudioTrack, Assert.Single(audio.Files[0].Targets).EntityKind);
        Assert.NotEqual(ebook.Files[0].RemoteId, audio.Files[0].RemoteId);
        Assert.Equal(["eBook"], fixture.Handler.HeadTypes);
    }

    [Fact]
    public async Task MultiPartAudiobookIsInventoriedFromTheMappedFolderWithoutRequestingItsZip() {
        var folder = Path.Combine(audioRoot, "example");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "example.m4b"), new byte[34080]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "part-two.mp3"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "cover.jpg"), [9]);
        await File.WriteAllBytesAsync(Path.Combine(audioRoot, "unrelated.mp3"), [4]);
        var fixture = Fixture(audioPath: "/audio/example/example.m4b");

        var first = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Audiobook)));
        var second = Assert.IsType<ManagedItemSnapshot>(await fixture.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Audiobook)));

        Assert.Equal(2, first.Item.RemoteFileCount);
        Assert.Equal(["/audio/example/example.m4b", "/audio/example/part-two.mp3"], first.Files.Select(file => file.Path).ToArray());
        Assert.Equal(first.Files.Select(file => file.RemoteId), second.Files.Select(file => file.RemoteId));
        Assert.Equal([34080L, 3L], first.Files.Select(file => file.SizeBytes).ToArray());
        Assert.Equal(2, first.Files.SelectMany(file => file.Targets).Select(target => target.RemoteId).Distinct().Count());
        Assert.Empty(fixture.Handler.HeadTypes);
    }

    [Fact]
    public async Task UnmappedAudiobookRootAndEscapingPathsFailWithSpecificReasons() {
        var unmapped = Fixture(audioPath: "/audio/example/example.m4b", mapped: false);
        var failure = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => unmapped.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Audiobook)));
        Assert.Contains("Map LazyLibrarian's audiobook root (/audio)", failure.Message);
        Assert.IsType<ManagedItemSnapshot>(await unmapped.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Ebook)));

        var missing = Fixture(audioPath: "/audio/example/example.m4b");
        Assert.Contains("is not in the mapped folder", (await Assert.ThrowsAnyAsync<IntegrationFailure>(() =>
            missing.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Audiobook)))).Message);

        foreach (var outside in new[] { "/outside/example.m4b", "/audio-archive/example.m4b", "/audio/../outside/example.m4b", "/Audio/example.m4b", "/audio" }) {
            var escaping = Fixture(audioPath: outside);
            Assert.Contains("outside this rendition's configured library root", (await Assert.ThrowsAnyAsync<IntegrationFailure>(() =>
                escaping.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Audiobook)))).Message);
        }
        Assert.DoesNotContain("AudioBook", unmapped.Handler.HeadTypes.Concat(missing.Handler.HeadTypes));
    }

    [Fact]
    public async Task ExistingWorkCanBeReviewedAndControlledPerRendition() {
        var fixture = Fixture(audioPath: null);
        var identities = Identities();
        var lookup = Assert.IsType<ManagedLookupResult>(await fixture.Call(ManagerCreation.Lookup,
            new ManagedLookupInput(MediaKinds.Book, identities, null, LazyLibrarianRendition.Audiobook.Code)));
        Assert.Equal("OL450063W", lookup.Candidate.ExternalIds[LazyLibrarianBookIdentity.OpenLibraryWork.Namespace]);
        Assert.Equal("/audio/OL450063W", lookup.Existing?.Path);
        var options = Assert.IsType<ManagerOptions>(await fixture.Call(ManagerProtocol.Options,
            new ManagerOptionsInput(MediaKinds.Book, LazyLibrarianRendition.Audiobook.Code)));
        Assert.Empty(options.Profiles);
        Assert.Equal("/audio", Assert.Single(options.Roots).Path);

        var scope = Scope(LazyLibrarianRendition.Audiobook);
        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(scope)));
        Assert.False(state.Item.Monitored);
        Assert.True(state.Capabilities.CanChangeMonitoring);
        Assert.False(state.Capabilities.CanSearch);
        var applied = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure,
            new ConfigureManagedInput(Guid.NewGuid(), scope, state.Path, null, new Dictionary<string, bool>(), new(Monitored: true))));
        Assert.Equal(ManagerControls.Applied, applied.Outcome);
        Assert.Equal(("queueBook", "OL450063W", "AudioBook"), Assert.Single(fixture.Handler.Writes));
        Assert.Equal("Open", fixture.Handler.EbookStatus);
        Assert.Equal("Wanted", fixture.Handler.AudioStatus);
        Assert.True(Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile,
            new ReconcileManagedInput(scope))).Capabilities.CanSearch);

        fixture.Handler.SearchReply = "No search methods set, check config";
        var refused = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request,
            new RequestManagedInput(Guid.NewGuid(), scope, state.Path, null)));
        Assert.Equal(ManagerControls.Rejected, refused.Outcome);
        Assert.Equal("LazyLibrarian refused searchBook: No search methods set, check config", refused.Problem);
        fixture.Handler.SearchReply = "OK";
        var requested = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request,
            new RequestManagedInput(Guid.NewGuid(), scope, state.Path, null)));
        Assert.Equal(ManagerControls.Accepted, requested.Outcome);
        Assert.Equal(ManagerControls.Pending, requested.Command?.Status);
        Assert.Equal(("searchBook", "OL450063W", "AudioBook"), fixture.Handler.Writes[^1]);
        var observed = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile,
            new ReconcileManagedInput(scope, requested.Command?.Reference)));
        Assert.Equal(ManagerControls.Unknown, observed.Command?.Status);
    }

    [Fact]
    public async Task TurningMonitoringOffUnqueuesOnlyThatFormatAndConfirmsWithoutRereadingTheCatalog() {
        var fixture = Fixture(audioPath: null, ebookStatus: "Wanted", audioStatus: "Wanted");
        var scope = Scope(LazyLibrarianRendition.Ebook);

        var result = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure,
            new ConfigureManagedInput(Guid.NewGuid(), scope, "/books/OL450063W", null, new Dictionary<string, bool>(), new(Monitored: false))));

        Assert.Equal(ManagerControls.Applied, result.Outcome);
        Assert.Equal(("unqueueBook", "OL450063W", "eBook"), Assert.Single(fixture.Handler.Writes));
        Assert.Equal("Skipped", fixture.Handler.EbookStatus);
        Assert.Equal("Wanted", fixture.Handler.AudioStatus);
        Assert.Equal(1, fixture.Handler.Commands.Count(command => command == "getAllBooks"));
        Assert.Equal(2, fixture.Handler.Commands.Count(command => command == "getAuthor"));
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("Have")]
    public async Task OwnedRenditionsAreNeverQueuedOrSearched(string owned) {
        var fixture = Fixture(audioPath: null, ebookStatus: owned);
        var scope = Scope(LazyLibrarianRendition.Ebook);

        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(scope)));
        var configured = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure,
            new ConfigureManagedInput(Guid.NewGuid(), scope, state.Path, null, new Dictionary<string, bool>(), new(Monitored: true))));
        var requested = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request,
            new RequestManagedInput(Guid.NewGuid(), scope, state.Path, null)));

        Assert.False(state.Item.Monitored);
        Assert.False(state.Capabilities.CanChangeMonitoring);
        Assert.False(state.Capabilities.CanSearch);
        Assert.Contains($"already has this ebook ({owned})", state.Capabilities.MonitoringUnavailableReason);
        Assert.Equal(ManagerControls.Rejected, configured.Outcome);
        Assert.Equal(state.Capabilities.MonitoringUnavailableReason, configured.Problem);
        Assert.Equal(ManagerControls.Rejected, requested.Outcome);
        Assert.Contains("would not search for it", requested.Problem);
        Assert.Empty(fixture.Handler.Writes);
    }

    [Theory]
    [InlineData("Skipped", "searches only Wanted books")]
    [InlineData("Snatched", "already snatched")]
    [InlineData("Seeding", "unrecognized status")]
    public async Task SearchIsRejectedWhenLazyLibrarianWouldNotSearch(string status, string problem) {
        var fixture = Fixture(audioPath: null, audioStatus: status);
        var scope = Scope(LazyLibrarianRendition.Audiobook);

        var state = Assert.IsType<ManagedControlState>(await fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(scope)));
        var result = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request,
            new RequestManagedInput(Guid.NewGuid(), scope, state.Path, null)));

        Assert.False(state.Capabilities.CanSearch);
        Assert.Equal(ManagerControls.Rejected, result.Outcome);
        Assert.Contains(problem, result.Problem);
        Assert.Empty(fixture.Handler.Writes);
    }

    [Fact]
    public async Task LibrarySearchPagesByBookIdAndScopesItsCursor() {
        var fixture = Fixture(audioPath: null);
        fixture.Handler.ExtraBooks = [("OL1W", "Alpha", "Wanted", "Skipped"), ("OL9W", "Omega", "Skipped", "Skipped")];
        var query = new ManagedLibraryQuery(MediaKinds.Book, null, null, 2);

        var first = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, query));
        var second = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, query with { Cursor = first.NextCursor }));
        var byAuthor = Assert.IsType<ManagedLibraryPage>(await fixture.Call(ManagerProtocol.SearchLibrary, query with { Query = "omega" }));

        Assert.Equal(["OL1W", "OL450063W"], first.Items.Select(item => item.RemoteId).ToArray());
        Assert.True(first.Items[0].Monitored);
        Assert.Equal("OL9W", Assert.Single(second.Items).RemoteId);
        Assert.Null(second.NextCursor);
        Assert.Equal("OL9W", Assert.Single(byAuthor.Items).RemoteId);
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.SearchLibrary,
            query with { Cursor = first.NextCursor, Query = "changed" }));
    }

    [Fact]
    public async Task OnlyALibraryReadReportsAnAbsentBookAsRemoved() {
        var fixture = Fixture(audioPath: null);
        fixture.Handler.IncludeBook = false;
        fixture.Handler.ExtraBooks = [("OL9W", "Omega", "Skipped", "Skipped")];
        var scope = Scope(LazyLibrarianRendition.Ebook);

        var removed = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Ebook)));
        var lookup = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Lookup,
            new ManagedLookupInput(MediaKinds.Book, Identities(), null, LazyLibrarianRendition.Ebook.Code)));
        var reconcile = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerControls.Reconcile, new ReconcileManagedInput(scope)));
        var configure = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Configure,
            new ConfigureManagedInput(Guid.NewGuid(), scope, "/books/OL450063W", null, new Dictionary<string, bool>(), new(Monitored: true))));
        var request = Assert.IsType<ManagedMutationResult>(await fixture.Call(ManagerControls.Request,
            new RequestManagedInput(Guid.NewGuid(), scope, "/books/OL450063W", null)));

        Assert.Equal(IntegrationErrorCodes.ManagedItemNotFound, removed.Code);
        Assert.Null(lookup.Code);
        Assert.Contains("is not in LazyLibrarian's catalog", lookup.Message);
        Assert.Null(reconcile.Code);
        Assert.Equal(ManagerControls.Rejected, configure.Outcome);
        Assert.Contains("no longer in LazyLibrarian's catalog", configure.Problem);
        Assert.Equal(ManagerControls.Rejected, request.Outcome);
        Assert.Empty(fixture.Handler.Writes);
    }

    [Fact]
    public async Task AnEmptyCatalogIsInconclusiveRatherThanProofOfRemoval() {
        var fixture = Fixture(audioPath: null);
        fixture.Handler.IncludeBook = false;
        var scope = Scope(LazyLibrarianRendition.Ebook);

        var read = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Ebook)));
        var lookup = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerCreation.Lookup,
            new ManagedLookupInput(MediaKinds.Book, Identities(), null, LazyLibrarianRendition.Ebook.Code)));
        var configure = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => fixture.Call(ManagerControls.Configure,
            new ConfigureManagedInput(Guid.NewGuid(), scope, "/books/OL450063W", null, new Dictionary<string, bool>(), new(Monitored: true))));

        Assert.Null(read.Code);
        Assert.Contains("empty book catalog", read.Message);
        Assert.Null(lookup.Code);
        Assert.IsNotType<ManagedMutationRejection>(configure);
        Assert.Empty(fixture.Handler.Writes);
    }

    [Fact]
    public async Task ReadOnlyOrOlderHelpLimitsControlsInsteadOfPinningABuild() {
        var readOnly = Fixture(audioPath: null, help: ReadOnlyHelp);
        var probe = Assert.IsType<ProbeResult>(await readOnly.Call(IntegrationOperations.Probe, new { }));
        var manager = Assert.Single(probe.Capabilities, capability => capability.Kind == ManagerProtocol.ExternalManager);
        Assert.DoesNotContain(ManagerControls.Configure, manager.Operations);
        Assert.DoesNotContain(ManagerControls.Request, manager.Operations);
        var scope = Scope(LazyLibrarianRendition.Audiobook);
        var state = Assert.IsType<ManagedControlState>(await readOnly.Call(ManagerControls.Reconcile, new ReconcileManagedInput(scope)));
        Assert.False(state.Capabilities.CanChangeMonitoring);
        Assert.Contains("does not list queueBook with type=AudioBook", state.Capabilities.MonitoringUnavailableReason);
        var refused = Assert.IsType<ManagedMutationResult>(await readOnly.Call(ManagerControls.Configure,
            new ConfigureManagedInput(Guid.NewGuid(), scope, state.Path, null, new Dictionary<string, bool>(), new(Monitored: true))));
        Assert.Equal(ManagerControls.Rejected, refused.Outcome);
        Assert.Contains("read-only", refused.Problem);
        Assert.Empty(readOnly.Handler.Writes);

        var earlier = Fixture(audioPath: null, help: EarlierHelp, version: "{\"Success\":true,\"current_version\":\"f066b525\"}");
        var earlierProbe = Assert.IsType<ProbeResult>(await earlier.Call(IntegrationOperations.Probe, new { }));
        Assert.Null(earlierProbe.Version);
        Assert.Contains(ManagerControls.Configure, Assert.Single(earlierProbe.Capabilities,
            capability => capability.Kind == ManagerProtocol.ExternalManager).Operations);

        var incomplete = Fixture(audioPath: null, help: "<tr><td>getAllBooks</td><td>list all books</td></tr>");
        var failure = await Assert.ThrowsAnyAsync<IntegrationFailure>(() => incomplete.Call(ManagerProtocol.GetLibraryItem, Item(LazyLibrarianRendition.Ebook)));
        Assert.Contains("does not list getAuthor", failure.Message);
    }

    private static readonly string FullHelp = "<html><p>Sample use</p><table><tr><th style=\"text-align: left;\">Command</th>"
        + "<th style=\"text-align: left;\">Parameters</th></tr>"
        + "<tr><td>getAllBooks</td><td>[&sort=] [&limit=] [&status=] [&audiostatus=] list all books in the database</td></tr>"
        + "<tr><td>getAuthor</td><td>&id= get author by AuthorID and list their books</td></tr>"
        + "<tr><td>getFileDirect</td><td>&id= [&type=eBook/AudioBook/Comic/Issue] download file directly</td></tr>"
        + "<tr><td>getVersion</td><td>show lazylibrarian current/git version</td></tr>"
        + "<tr><td>queueBook</td><td>&id= [&type=eBook/AudioBook] mark book as Wanted, default eBook</td></tr>"
        + "<tr><td>searchBook</td><td>&id= [&wait] [&type=eBook/AudioBook] search for one book by BookID</td></tr>"
        + "<tr><td>unqueueBook</td><td>&id= [&type=eBook/AudioBook] mark book as Skipped, default eBook</td></tr>"
        + "</table></html>";

    private static readonly string ReadOnlyHelp = "<html><table>"
        + "<tr><td>getAllBooks</td><td>[&sort=] [&limit=] list all books in the database</td></tr>"
        + "<tr><td>getAuthor</td><td>&id= get author by AuthorID and list their books</td></tr>"
        + "<tr><td>getFileDirect</td><td>&id= [&type=eBook/AudioBook/Comic/Issue] download file directly</td></tr>"
        + "</table></html>";

    private static readonly string EarlierHelp = "<html><ul>"
        + "<li>getAllBooks: list all books in the database</li>\n"
        + "<li>getAuthor: &id= get author by AuthorID and list their books</li>\n"
        + "<li>queueBook: &id= [&type=eBook/AudioBook] mark book as Wanted, default eBook</li>\n"
        + "<li>unqueueBook: &id= [&type=eBook/AudioBook] mark book as Skipped, default eBook</li>\n"
        + "<li>searchBook: &id= [&wait] [&type=eBook/AudioBook] search for one book by BookID</li>\n"
        + "</ul></html>";

    private LibraryFixture Fixture(string? audioPath, string ebookStatus = "Open", string audioStatus = "Skipped",
        bool mapped = true, string? help = null, string? version = null) {
        var connection = new ConnectionContext(Guid.NewGuid(), "http://manager.test/", null,
            new Dictionary<string, string> {
                [LazyLibrarianRendition.Ebook.RootSetting] = "/books",
                [LazyLibrarianRendition.Audiobook.RootSetting] = "/audio"
            },
            new Dictionary<string, string> { [LazyLibrarianClient.ApiKey] = "secret" },
            mapped ? [new(LazyLibrarianRendition.Audiobook.Code, "/audio", audioRoot)] : null);
        return new(connection, new StubHandler(help ?? FullHelp, version ?? "{\"Success\":true,\"current_version\":\"2b48097a\"}") {
            EbookStatus = ebookStatus, AudioStatus = audioStatus, AudioPath = audioPath
        });
    }

    private static Dictionary<string, string> Identities() =>
        new() { [LazyLibrarianBookIdentity.OpenLibraryWork.Namespace] = "OL450063W" };

    private static ManagedItemInput Item(LazyLibrarianRendition rendition) =>
        new(MediaKinds.Book, "OL450063W", Identities(), rendition.Code);

    private static ManagedControlScope Scope(LazyLibrarianRendition rendition) => new(Item(rendition), []);

    private sealed record LibraryFixture(ConnectionContext Connection, StubHandler Handler) {
        internal async Task<object> Call(string operation, object input) {
            using var client = new LazyLibrarianClient(Connection, Handler);
            return await new LazyLibrarianLibrary(client, Connection).DispatchAsync(new(IntegrationProtocol.Name,
                IntegrationProtocol.Version, Guid.NewGuid(), operation, Connection,
                JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);
        }
    }

    /// <summary>A LazyLibrarian API double that routes on parsed query parameters.</summary>
    private sealed class StubHandler(string help, string version) : HttpMessageHandler {
        public string EbookStatus { get; set; } = "Open";
        public string AudioStatus { get; set; } = "Skipped";
        public string? AudioPath { get; set; }
        public bool IncludeBook { get; set; } = true;
        public string SearchReply { get; set; } = "OK";
        public IReadOnlyList<(string Id, string Name, string Status, string AudioStatus)> ExtraBooks { get; set; } = [];
        public List<string> Commands { get; } = [];
        public List<string> HeadTypes { get; } = [];
        public List<(string Command, string? Id, string? Type)> Writes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.Equal("secret", query["apikey"]);
            var command = query["cmd"]!;
            if (request.Method == HttpMethod.Head) {
                Assert.Equal("getFileDirect", command);
                HeadTypes.Add(query["type"]!);
                // LazyLibrarian zips a multi-file audiobook folder even for HEAD; the adapter must not ask.
                var audio = query["type"] == "AudioBook";
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentLength = audio ? 9_999_999 : 474161;
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") {
                    FileName = audio ? "Example.zip" : "example.epub"
                };
                return Task.FromResult(response);
            }
            Commands.Add(command);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(command switch {
                "help" => help,
                "getVersion" => version,
                "getAllBooks" => JsonSerializer.Serialize(Rows().Select(row => new {
                    row.BookID, row.AuthorID, row.AuthorName, row.BookName, row.Status, audiostatus = row.AudioStatus
                })),
                "getAuthor" => JsonSerializer.Serialize(new { author = Array.Empty<object>(),
                    books = Rows().Where(row => row.AuthorID == query["id"]) }),
                "queueBook" or "unqueueBook" or "searchBook" => Write(command, query["id"], query["type"]),
                _ => "{\"Success\":false,\"Data\":\"\",\"Error\":{\"Code\":405,\"Message\":\"Unknown command\"}}"
            }) });
        }

        private string Write(string command, string? id, string? type) {
            Writes.Add((command, id, type));
            if (command == "searchBook") return SearchReply;
            var status = command == "queueBook" ? "Wanted" : "Skipped";
            if (type == "AudioBook") AudioStatus = status;
            else EbookStatus = status;
            return "OK";
        }

        private IEnumerable<LazyLibrarianBookRow> Rows() {
            if (IncludeBook)
                yield return new("OL450063W", "author-1", "Author", "Example", EbookStatus, AudioStatus, "/books/example.epub", AudioPath, null, null);
            foreach (var extra in ExtraBooks)
                yield return new(extra.Id, "author-2", extra.Name == "Omega" ? "Omega Writer" : "Writer", extra.Name,
                    extra.Status, extra.AudioStatus, null, null, null, null);
        }
    }
}
