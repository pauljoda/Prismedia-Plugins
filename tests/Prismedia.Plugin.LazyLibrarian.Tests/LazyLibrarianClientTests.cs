using System.Net;
using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.LazyLibrarian;

namespace Prismedia.Plugin.LazyLibrarian.Tests;

public sealed class LazyLibrarianClientTests {
    [Fact]
    public async Task PreservesProxyPrefixAndEncodesCommandParameters() {
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var client = new LazyLibrarianClient(Connection(), handler);

        var books = await client.ReadAsync<string[]>(LazyLibrarianCodes.GetAllBooks,
            new Dictionary<string, string> { [LazyLibrarianCodes.IdParameter] = "A & B" }, CancellationToken.None);

        Assert.Empty(books);
        Assert.Equal("https://example.test/prismedia/api", handler.LastRequest?.GetLeftPart(UriPartial.Path));
        Assert.Contains("apikey=secret%20key", handler.LastRequest?.Query);
        Assert.Contains("cmd=getAllBooks", handler.LastRequest?.Query);
        Assert.Contains("id=A%20%26%20B", handler.LastRequest?.Query);
    }

    [Theory]
    [InlineData("No search methods set, check config")]
    [InlineData("{\"Success\":false,\"Data\":\"\",\"Error\":{\"Code\":405}}")]
    public async Task RejectsApplicationErrorsEvenWhenHttpStatusIsOk(string response) {
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(response) });
        using var client = new LazyLibrarianClient(Connection(), handler);

        await Assert.ThrowsAsync<IntegrationFailure>(() => client.ReadAsync<string[]>(LazyLibrarianCodes.GetAllBooks, null, CancellationToken.None));
    }

    [Fact]
    public async Task RequiresExplicitAcknowledgementForMutation() {
        var reply = "OK";
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(reply) });
        using var client = new LazyLibrarianClient(Connection(), handler);
        var arguments = new Dictionary<string, string> { [LazyLibrarianCodes.IdParameter] = "OL85892W", [LazyLibrarianCodes.TypeParameter] = LazyLibrarianCodes.Audiobook };

        await client.AcknowledgeAsync(LazyLibrarianCodes.QueueBook, arguments, CancellationToken.None);
        Assert.Contains("type=AudioBook", handler.LastRequest?.Query);
        reply = "No search methods set";
        await Assert.ThrowsAsync<IntegrationFailure>(() => client.AcknowledgeAsync(LazyLibrarianCodes.QueueBook, arguments, CancellationToken.None));
    }

    [Fact]
    public async Task RefusesRedirectsAndOversizedReplies() {
        using var redirect = new StubHandler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.test/") } });
        using var redirectClient = new LazyLibrarianClient(Connection(), redirect);
        await Assert.ThrowsAsync<IntegrationFailure>(() => redirectClient.ReadAsync<string[]>(LazyLibrarianCodes.GetAllBooks, null, CancellationToken.None));

        using var large = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]) });
        using var largeClient = new LazyLibrarianClient(Connection(), large);
        await Assert.ThrowsAsync<IntegrationFailure>(() => largeClient.ReadAsync<string[]>(LazyLibrarianCodes.GetAllBooks, null, CancellationToken.None));
    }

    [Fact]
    public async Task ResolvesOneWorkWithIndependentBookAndAudioPaths() {
        using var handler = new StubHandler(request => new(HttpStatusCode.OK) { Content = new StringContent(
            request.RequestUri!.Query.Contains("cmd=getAllBooks", StringComparison.Ordinal)
                ? "[{\"BookID\":\"OL450063W\",\"AuthorID\":\"author-1\",\"AuthorName\":\"Mary Shelley\",\"BookName\":\"Frankenstein\",\"Status\":\"Open\",\"AudioStatus\":\"Open\"}]"
                : "{\"books\":[{\"BookID\":\"OL450063W\",\"AuthorID\":\"author-1\",\"BookName\":\"Frankenstein\",\"Status\":\"Open\",\"AudioStatus\":\"Open\",\"BookFile\":\"/books/Frankenstein.epub\",\"AudioFile\":\"/audio/Frankenstein.m4b\"}]}"
        ) });
        using var client = new LazyLibrarianClient(Connection(), handler);
        var catalog = new LazyLibrarianBooks(client);

        var book = await catalog.GetAsync("OL450063W", CancellationToken.None);

        Assert.Equal("OL450063W", book.BookID);
        Assert.Equal("Open", book.Status);
        Assert.Equal("Open", book.AudioStatus);
        Assert.Equal("/books/Frankenstein.epub", book.BookFile);
        Assert.Equal("/audio/Frankenstein.m4b", book.AudioFile);
    }

    [Fact]
    public async Task RejectsDuplicateBookIdentityBeforeAuthorLookup() {
        const string row = "{\"BookID\":\"OL450063W\",\"AuthorID\":\"author-1\",\"AuthorName\":\"Mary Shelley\",\"BookName\":\"Frankenstein\"}";
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[" + row + "," + row + "]") });
        using var client = new LazyLibrarianClient(Connection(), handler);

        await Assert.ThrowsAsync<IntegrationFailure>(() => new LazyLibrarianBooks(client).GetAsync("OL450063W", CancellationToken.None));
    }

    private static ConnectionContext Connection() => new(Guid.NewGuid(), "https://example.test/prismedia", null,
        new Dictionary<string, string>(), new Dictionary<string, string> { [LazyLibrarianCodes.ApiKey] = "secret key" });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        public Uri? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            LastRequest = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }
}
