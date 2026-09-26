using System.Net;
using System.Web;
using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.LazyLibrarian;

namespace Prismedia.Plugin.LazyLibrarian.Tests;

public sealed class LazyLibrarianClientTests {
    [Fact]
    public async Task PreservesProxyPrefixAndEncodesCommandParameters() {
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var client = new LazyLibrarianClient(Connection(), handler);

        var books = await client.ReadAsync<string[]>(LazyLibrarianCommand.GetAuthor, "A & B", CancellationToken.None);

        Assert.Empty(books);
        Assert.Equal("https://example.test/prismedia/api", handler.LastRequest?.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(handler.LastRequest!.Query);
        Assert.Equal("secret key", query["apikey"]);
        Assert.Equal("getAuthor", query["cmd"]);
        Assert.Equal("A & B", query["id"]);
    }

    [Theory]
    [InlineData("No search methods set, check config")]
    [InlineData("{\"Success\":false,\"Data\":\"\",\"Error\":{\"Code\":405}}")]
    public async Task RejectsApplicationErrorsEvenWhenHttpStatusIsOk(string response) {
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(response) });
        using var client = new LazyLibrarianClient(Connection(), handler);

        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => client.ReadAsync<string[]>(LazyLibrarianCommand.GetAllBooks, null, CancellationToken.None));
    }

    [Fact]
    public async Task WritesRequireExplicitAcknowledgementAndClassifyRefusals() {
        var status = HttpStatusCode.OK;
        var reply = "OK";
        using var handler = new StubHandler(_ => new(status) { Content = new StringContent(reply) });
        using var client = new LazyLibrarianClient(Connection(), handler);
        Task Queue() => client.AcknowledgeAsync(LazyLibrarianCommand.QueueBook, "OL85892W", LazyLibrarianRendition.Audiobook, CancellationToken.None);

        await Queue();
        var query = HttpUtility.ParseQueryString(handler.LastRequest!.Query);
        Assert.Equal("queueBook", query["cmd"]);
        Assert.Equal("OL85892W", query["id"]);
        Assert.Equal("AudioBook", query["type"]);

        reply = "Invalid id: OL85892W";
        var refused = await Assert.ThrowsAsync<ManagedMutationRejection>(Queue);
        Assert.Equal("LazyLibrarian refused queueBook: Invalid id: OL85892W", refused.Problem);
        reply = "{\"Success\":false,\"Data\":\"\",\"Error\":{\"Code\":405,\"Message\":\"Command: queueBook not available with read-only api access key, try cmd=help\"}}";
        Assert.Contains("read-only", (await Assert.ThrowsAsync<ManagedMutationRejection>(Queue)).Problem);
        status = HttpStatusCode.Unauthorized;
        await Assert.ThrowsAsync<ManagedMutationRejection>(Queue);

        status = HttpStatusCode.InternalServerError;
        Assert.IsNotType<ManagedMutationRejection>(await Assert.ThrowsAnyAsync<IntegrationFailure>(Queue));
        status = HttpStatusCode.OK;
        reply = "<html><body>Sign in</body></html>";
        Assert.IsNotType<ManagedMutationRejection>(await Assert.ThrowsAnyAsync<IntegrationFailure>(Queue));
    }

    [Fact]
    public async Task RefusesRedirectsAndOversizedReplies() {
        using var redirect = new StubHandler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.test/") } });
        using var redirectClient = new LazyLibrarianClient(Connection(), redirect);
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => redirectClient.ReadAsync<string[]>(LazyLibrarianCommand.GetAllBooks, null, CancellationToken.None));

        using var large = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]) });
        using var largeClient = new LazyLibrarianClient(Connection(), large);
        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => largeClient.ReadAsync<string[]>(LazyLibrarianCommand.GetAllBooks, null, CancellationToken.None));
    }

    [Fact]
    public async Task ResolvesOneWorkWithIndependentBookAndAudioPaths() {
        using var handler = new StubHandler(request => new(HttpStatusCode.OK) { Content = new StringContent(
            HttpUtility.ParseQueryString(request.RequestUri!.Query)["cmd"] == "getAllBooks"
                ? "[{\"BookID\":\"OL450063W\",\"AuthorID\":\"author-1\",\"AuthorName\":\"Mary Shelley\",\"BookName\":\"Frankenstein\",\"Status\":\"Open\",\"AudioStatus\":\"Open\"}]"
                : "{\"books\":[{\"BookID\":\"OL450063W\",\"AuthorID\":\"author-1\",\"BookName\":\"Frankenstein\",\"Status\":\"Open\",\"AudioStatus\":\"Open\",\"BookFile\":\"/books/Frankenstein.epub\",\"AudioFile\":\"/audio/Frankenstein.m4b\"}]}"
        ) });
        using var client = new LazyLibrarianClient(Connection(), handler);
        var catalog = new LazyLibrarianBooks(client);

        var book = await catalog.FindAsync("OL450063W", CancellationToken.None);

        Assert.NotNull(book);
        Assert.Equal("OL450063W", book.BookID);
        Assert.Same(LazyLibrarianBookStatus.Open, LazyLibrarianRendition.Ebook.StatusOf(book));
        Assert.Same(LazyLibrarianBookStatus.Open, LazyLibrarianRendition.Audiobook.StatusOf(book));
        Assert.Equal("/books/Frankenstein.epub", LazyLibrarianRendition.Ebook.FileOf(book));
        Assert.Equal("/audio/Frankenstein.m4b", LazyLibrarianRendition.Audiobook.FileOf(book));
        Assert.Null(await catalog.FindAsync("OL1W", CancellationToken.None));
    }

    [Fact]
    public async Task RejectsDuplicateBookIdentityBeforeAuthorLookup() {
        const string row = "{\"BookID\":\"OL450063W\",\"AuthorID\":\"author-1\",\"AuthorName\":\"Mary Shelley\",\"BookName\":\"Frankenstein\"}";
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[" + row + "," + row + "]") });
        using var client = new LazyLibrarianClient(Connection(), handler);

        await Assert.ThrowsAnyAsync<IntegrationFailure>(() => new LazyLibrarianBooks(client).FindAsync("OL450063W", CancellationToken.None));
        Assert.Equal("getAllBooks", HttpUtility.ParseQueryString(handler.LastRequest!.Query)["cmd"]);
    }

    [Fact]
    public void HelpDeclaresFormatScopedCommandsInCurrentAndEarlierLayouts() {
        var current = LazyLibrarianApiHelp.Parse(
            "<html><table><tr><th>Command</th><th>Parameters</th></tr>"
            + "<tr><td>getAllBooks</td><td>[&sort=] [&limit=] list all books in the database</td></tr>"
            + "<tr><td>getAuthor</td><td>&id= get author by AuthorID and list their books</td></tr>"
            + "<tr><td>searchBook</td><td>&id= [&wait] [&type=eBook/AudioBook] search for one book by BookID</td></tr>"
            + "<tr><td>queueBook</td><td>&id= mark book as Wanted</td></tr></table></html>");
        var earlier = LazyLibrarianApiHelp.Parse(
            "<html><ul><li>getAllBooks: list all books in the database</li>\n"
            + "<li>getAuthor: &id= get author by AuthorID and list their books</li>\n"
            + "<li>queueBook: &id= [&type=eBook/AudioBook] mark book as Wanted, default eBook</li></ul></html>");

        current.RequireReads();
        earlier.RequireReads();
        Assert.True(current.Supports(LazyLibrarianCommand.SearchBook, LazyLibrarianRendition.Audiobook));
        Assert.Null(current.WriteBlocker(LazyLibrarianCommand.SearchBook, LazyLibrarianRendition.Ebook));
        Assert.Contains("queueBook with type=AudioBook", current.WriteBlocker(LazyLibrarianCommand.QueueBook, LazyLibrarianRendition.Audiobook));
        Assert.Null(earlier.WriteBlocker(LazyLibrarianCommand.QueueBook, LazyLibrarianRendition.Audiobook));
        Assert.NotNull(earlier.WriteBlocker(LazyLibrarianCommand.UnqueueBook, LazyLibrarianRendition.Audiobook));
        Assert.False(earlier.Supports(LazyLibrarianCommand.GetVersion));
        var missing = Assert.ThrowsAny<IntegrationFailure>(() => LazyLibrarianApiHelp.Parse(
            "<tr><td>getAllBooks</td><td>list all books</td></tr>").RequireReads());
        Assert.Contains("getAuthor", missing.Message);
        Assert.ThrowsAny<IntegrationFailure>(() => LazyLibrarianApiHelp.Parse("<html>Not Found</html>"));
    }

    private static ConnectionContext Connection() => new(Guid.NewGuid(), "https://example.test/prismedia", null,
        new Dictionary<string, string>(), new Dictionary<string, string> { [LazyLibrarianClient.ApiKey] = "secret key" });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        public Uri? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            LastRequest = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }
}
