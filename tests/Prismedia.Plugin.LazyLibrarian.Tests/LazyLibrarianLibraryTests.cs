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
        Assert.Single(probe.Capabilities);
        var roots = Assert.IsType<ProviderLibraryCatalog>(await library.DispatchAsync(
            Request(connection, ManagerProtocol.ListLibraries, new { }), default));
        Assert.Equal(["/books", "/audio"], roots.Libraries.Select(root => root.RemotePath).ToArray());

        var ebook = Assert.IsType<ManagedItemSnapshot>(await library.DispatchAsync(
            Request(connection, ManagerProtocol.GetLibraryItem, input), default));
        Assert.Equal("/books", ebook.Path);
        Assert.Equal("/books/example.epub", Assert.Single(ebook.Files).Path);
        Assert.Equal(474161, ebook.Files[0].SizeBytes);
        Assert.Equal(MediaKinds.Book, Assert.Single(ebook.Files[0].Targets).EntityKind);

        var audio = Assert.IsType<ManagedItemSnapshot>(await library.DispatchAsync(
            Request(connection, ManagerProtocol.GetLibraryItem,
                input with { BookRendition = LazyLibrarianCodes.AudiobookRendition }), default));
        Assert.Equal("/audio", audio.Path);
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
            var body = query.Contains("cmd=getVersion", StringComparison.Ordinal)
                ? "{\"Success\":true,\"current_version\":\"2b48097a\"}"
                : query.Contains("cmd=getAllBooks", StringComparison.Ordinal)
                ? "[{\"BookID\":\"OL450063W\",\"AuthorID\":\"author-1\",\"AuthorName\":\"Author\",\"BookName\":\"Example\",\"Status\":\"Open\",\"AudioStatus\":\"Open\"}]"
                : "{\"books\":[{\"BookID\":\"OL450063W\",\"AuthorID\":\"author-1\",\"BookName\":\"Example\",\"Status\":\"Open\",\"AudioStatus\":\"Open\",\"BookFile\":\"/books/example.epub\",\"AudioFile\":\""
                    + (EscapingPath ? "/outside/example.m4b" : "/audio/example.m4b") + "\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
