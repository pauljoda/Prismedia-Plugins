using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.LazyLibrarian;

namespace Prismedia.Plugin.LazyLibrarian.Tests;

public sealed class LazyLibrarianLibraryTests {
    [Fact]
    public async Task OneWorkExposesSeparateRenditionFilesAndRoots() {
        using var handler = new StubHandler();
        var connection = Connection();
        using var client = new LazyLibrarianClient(connection, handler);
        var library = new LazyLibrarianLibrary(client, connection);
        var input = new ManagedItemInput(MediaKinds.Book, "OL450063W",
            new Dictionary<string, string> { [LazyLibrarianCodes.OpenLibraryWork] = "OL450063W" },
            LazyLibrarianCodes.EbookRendition);

        var probe = Assert.IsType<ProbeResult>(await library.DispatchAsync(Request(connection, IntegrationOperations.Probe, new { }), default));
        Assert.Equal(2, probe.Capabilities.Count);
        Assert.Contains(probe.Capabilities, capability => capability.Kind == ManagerProtocol.ExternalManager
            && capability.Operations.Contains(ManagerControls.Request));
        var roots = Assert.IsType<ProviderLibraryCatalog>(await library.DispatchAsync(
            Request(connection, ManagerProtocol.ListLibraries, new { }), default));
        Assert.Equal(["/books", "/audio"], roots.Libraries.Select(root => root.RemotePath).ToArray());

        var ebook = Assert.IsType<ManagedItemSnapshot>(await library.DispatchAsync(
            Request(connection, ManagerProtocol.GetLibraryItem, input), default));
        Assert.Equal("/books/OL450063W", ebook.Path);
        Assert.Equal("/books/example.epub", Assert.Single(ebook.Files).Path);
        Assert.Equal(474161, ebook.Files[0].SizeBytes);
        Assert.Equal(MediaKinds.Book, Assert.Single(ebook.Files[0].Targets).EntityKind);

        var audio = Assert.IsType<ManagedItemSnapshot>(await library.DispatchAsync(
            Request(connection, ManagerProtocol.GetLibraryItem,
                input with { BookRendition = LazyLibrarianCodes.AudiobookRendition }), default));
        Assert.Equal("/audio/OL450063W", audio.Path);
        Assert.Equal("/audio/example.m4b", Assert.Single(audio.Files).Path);
        Assert.Equal(34080, audio.Files[0].SizeBytes);
        Assert.Equal(ManagerProtocol.AudioTrack, Assert.Single(audio.Files[0].Targets).EntityKind);
        Assert.NotEqual(ebook.Files[0].RemoteId, audio.Files[0].RemoteId);
    }

    [Fact]
    public async Task RejectsMultipartAudioZipAndEscapingPath() {
        using var handler = new StubHandler { ZipAudio = true };
        var connection = Connection();
        using var client = new LazyLibrarianClient(connection, handler);
        var library = new LazyLibrarianLibrary(client, connection);
        var input = new ManagedItemInput(MediaKinds.Book, "OL450063W",
            new Dictionary<string, string> { [LazyLibrarianCodes.OpenLibraryWork] = "OL450063W" },
            LazyLibrarianCodes.AudiobookRendition);
        await Assert.ThrowsAsync<IntegrationFailure>(() => library.DispatchAsync(
            Request(connection, ManagerProtocol.GetLibraryItem, input), default));

        handler.ZipAudio = false;
        handler.EscapingPath = true;
        await Assert.ThrowsAsync<IntegrationFailure>(() => library.DispatchAsync(
            Request(connection, ManagerProtocol.GetLibraryItem, input), default));
    }

    [Fact]
    public async Task ExistingWorkCanBeReviewedAndControlledPerRendition() {
        using var handler = new StubHandler();
        var connection = Connection();
        using var client = new LazyLibrarianClient(connection, handler);
        var library = new LazyLibrarianLibrary(client, connection);
        var identities = new Dictionary<string, string> { [LazyLibrarianCodes.OpenLibraryWork] = "OL450063W" };
        var input = new ManagedItemInput(MediaKinds.Book, "OL450063W", identities,
            LazyLibrarianCodes.AudiobookRendition);
        var lookup = Assert.IsType<ManagedLookupResult>(await library.DispatchAsync(Request(connection,
            ManagerCreation.Lookup, new ManagedLookupInput(MediaKinds.Book, identities, null,
                LazyLibrarianCodes.AudiobookRendition)), default));
        Assert.Equal("OL450063W", lookup.Candidate.ExternalIds[LazyLibrarianCodes.OpenLibraryWork]);
        Assert.Equal("/audio/OL450063W", lookup.Existing?.Path);
        var options = Assert.IsType<ManagerOptions>(await library.DispatchAsync(Request(connection,
            ManagerProtocol.Options, new ManagerOptionsInput(MediaKinds.Book,
                LazyLibrarianCodes.AudiobookRendition)), default));
        Assert.Empty(options.Profiles);
        Assert.Equal("/audio", Assert.Single(options.Roots).Path);

        var scope = new ManagedControlScope(input, []);
        var state = Assert.IsType<ManagedControlState>(await library.DispatchAsync(Request(connection,
            ManagerControls.Reconcile, new ReconcileManagedInput(scope)), default));
        Assert.False(state.Item.Monitored);
        Assert.True(state.Capabilities.CanSearch);
        var applied = Assert.IsType<ManagedMutationResult>(await library.DispatchAsync(Request(connection,
            ManagerControls.Configure, new ConfigureManagedInput(Guid.NewGuid(), scope, state.Path,
                null, new Dictionary<string, bool>(), new(Monitored: true))), default));
        Assert.Equal(ManagerControls.Applied, applied.Outcome);
        Assert.Equal(LazyLibrarianCodes.QueueBook, handler.LastMutation);
        Assert.Equal(LazyLibrarianCodes.Audiobook, handler.LastType);
        Assert.Equal("Open", handler.EbookStatus);
        Assert.Equal("Wanted", handler.AudioStatus);

        handler.SearchReply = "No search methods set, check config";
        await Assert.ThrowsAsync<IntegrationFailure>(() => library.DispatchAsync(Request(connection,
            ManagerControls.Request, new RequestManagedInput(Guid.NewGuid(), scope, state.Path, null)), default));
        handler.SearchReply = LazyLibrarianCodes.Ok;
        var requested = Assert.IsType<ManagedMutationResult>(await library.DispatchAsync(Request(connection,
            ManagerControls.Request, new RequestManagedInput(Guid.NewGuid(), scope, state.Path, null)), default));
        Assert.Equal(ManagerControls.Accepted, requested.Outcome);
        Assert.Equal(ManagerControls.Pending, requested.Command?.Status);
        var observed = Assert.IsType<ManagedControlState>(await library.DispatchAsync(Request(connection,
            ManagerControls.Reconcile, new ReconcileManagedInput(scope, requested.Command?.Reference)), default));
        Assert.Equal(ManagerControls.Unknown, observed.Command?.Status);
    }

    private static ConnectionContext Connection() => new(Guid.NewGuid(), "http://manager.test/", null,
        new Dictionary<string, string> { [LazyLibrarianCodes.EbookRoot] = "/books",
            [LazyLibrarianCodes.AudiobookRoot] = "/audio" },
        new Dictionary<string, string> { [LazyLibrarianCodes.ApiKey] = "secret" });

    private static IntegrationRequest Request(ConnectionContext connection, string operation, object input) =>
        new(IntegrationProtocol.Name, IntegrationProtocol.Version, Guid.NewGuid(), operation, connection,
            JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json));

    private sealed class StubHandler : HttpMessageHandler {
        public bool ZipAudio { get; set; }
        public bool EscapingPath { get; set; }
        public string EbookStatus { get; private set; } = "Open";
        public string AudioStatus { get; private set; } = "Open";
        public string SearchReply { get; set; } = LazyLibrarianCodes.Ok;
        public string? LastMutation { get; private set; }
        public string? LastType { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var query = request.RequestUri!.Query;
            if (request.Method == HttpMethod.Head) {
                var audio = query.Contains("type=AudioBook", StringComparison.Ordinal);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentLength = audio ? 34080 : 474161;
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") {
                    FileName = audio ? ZipAudio ? "bundle.zip" : "example.m4b" : "example.epub"
                };
                return Task.FromResult(response);
            }
            var mutation = new[] { LazyLibrarianCodes.QueueBook, LazyLibrarianCodes.UnqueueBook,
                LazyLibrarianCodes.SearchBook }.FirstOrDefault(command =>
                query.Contains("cmd=" + command, StringComparison.Ordinal));
            if (mutation is not null) {
                LastMutation = mutation;
                LastType = query.Contains("type=AudioBook", StringComparison.Ordinal)
                    ? LazyLibrarianCodes.Audiobook : LazyLibrarianCodes.Ebook;
                if (mutation == LazyLibrarianCodes.QueueBook) {
                    if (LastType == LazyLibrarianCodes.Audiobook) AudioStatus = "Wanted";
                    else EbookStatus = "Wanted";
                } else if (mutation == LazyLibrarianCodes.UnqueueBook) {
                    if (LastType == LazyLibrarianCodes.Audiobook) AudioStatus = "Skipped";
                    else EbookStatus = "Skipped";
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(mutation == LazyLibrarianCodes.SearchBook ? SearchReply : LazyLibrarianCodes.Ok)
                });
            }
            var body = query.Contains("cmd=getVersion", StringComparison.Ordinal)
                ? "{\"Success\":true,\"current_version\":\"2b48097a\"}"
                : query.Contains("cmd=getAllBooks", StringComparison.Ordinal)
                ? "[{\"BookID\":\"OL450063W\",\"AuthorID\":\"author-1\",\"AuthorName\":\"Author\",\"BookName\":\"Example\",\"Status\":\"" + EbookStatus + "\",\"AudioStatus\":\"" + AudioStatus + "\"}]"
                : "{\"books\":[{\"BookID\":\"OL450063W\",\"AuthorID\":\"author-1\",\"BookName\":\"Example\",\"Status\":\"" + EbookStatus + "\",\"AudioStatus\":\"" + AudioStatus + "\",\"BookFile\":\"/books/example.epub\",\"AudioFile\":\""
                    + (EscapingPath ? "/outside/example.m4b" : "/audio/example.m4b") + "\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
