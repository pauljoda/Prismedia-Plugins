using System.Net;
using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.Opds;

namespace Prismedia.Plugin.Opds.Tests;

public sealed class OpdsIntegrationTests {
    private static ConnectionContext Connection => new(Guid.NewGuid(), "https://catalog.test/opds/", null, new Dictionary<string, string>(), new Dictionary<string, string> { ["token"] = "secret" });
    private static string Feed(params string[] ids) => "<feed xmlns='http://www.w3.org/2005/Atom'><title>Books</title>" + string.Join("", ids.Select(id => $"<entry><id>{id}</id><title>{id}</title><link rel='http://opds-spec.org/acquisition/open-access' type='application/epub+zip' href='{id}.epub'/></entry>")) + "</feed>";

    [Fact]
    public async Task PaginationKeepsEveryPublicationAndResolutionRefetchesTheSelectedOffer() {
        var calls = 0;
        using var http = new OpdsHttpClient(Connection, new Handler(request => {
            calls++;
            Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
            return Ok(Feed("one", "two", "three"));
        }));
        var integration = new OpdsIntegration(http, Connection);
        var first = await integration.DiscoverAsync(new(MediaKinds.Book, null, null, null, 2), false, default);
        var second = await integration.DiscoverAsync(new(MediaKinds.Book, null, first.NextCursor, null, 2), false, default);
        Assert.Equal(["one", "two", "three"], first.Items.Concat(second.Items).Select(item => item.Selection.ItemId));
        Assert.Null(second.NextCursor);
        var choice = first.Items[0];
        var resolved = await integration.ResolveAsync(new(choice.Selection, choice.Offers[0].Id), default);
        Assert.Equal("https://catalog.test/opds/one.epub", resolved.Delivery.Url);
        Assert.Equal("Bearer secret", resolved.Delivery.Headers["Authorization"]);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ChangedCatalogCannotExecuteAStaleOffer() {
        var calls = 0;
        using var http = new OpdsHttpClient(Connection, new Handler(_ => Ok(++calls == 1 ? Feed("one") : Feed("different"))));
        var integration = new OpdsIntegration(http, Connection);
        var choice = Assert.Single((await integration.DiscoverAsync(new(MediaKinds.Book, null, null, null, 25), false, default)).Items);
        await Assert.ThrowsAsync<IntegrationFailure>(() => integration.ResolveAsync(new(choice.Selection, choice.Offers[0].Id), default));
    }

    [Fact]
    public async Task CrossOriginRedirectIsRejectedBeforeSendingCredentialsToAnotherServer() {
        var calls = 0;
        using var http = new OpdsHttpClient(Connection, new Handler(_ => {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://other.test/collect");
            return response;
        }));
        await Assert.ThrowsAsync<IntegrationFailure>(() => http.ReadAsync(new Uri(Connection.BaseUrl), default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DocumentsOverTheTransportLimitAreRejected() {
        using var http = new OpdsHttpClient(Connection, new Handler(_ => Ok(new string('x', OpdsParser.MaximumDocumentBytes + 1))));
        await Assert.ThrowsAsync<IntegrationFailure>(() => http.ReadAsync(new Uri(Connection.BaseUrl), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvertisedSearchIsNegotiatedAndEncodesUserInput(bool description) {
        using var http = new OpdsHttpClient(Connection, new Handler(request => {
            if (request.RequestUri!.AbsolutePath.EndsWith("description")) return Ok("""
              <OpenSearchDescription xmlns="http://a9.com/-/spec/opensearch/1.1/"><Url type="application/atom+xml;profile=opds-catalog;kind=acquisition" template="search?q={searchTerms}" /></OpenSearchDescription>
              """);
            if (request.RequestUri.AbsolutePath.EndsWith("search")) {
                Assert.Equal("?q=one%20%26%20two", request.RequestUri.Query);
                return Ok(Feed("result"));
            }
            return Ok($"<feed xmlns='http://www.w3.org/2005/Atom'><title>Books</title><link rel='search' type='{(description ? "application/opensearchdescription+xml" : "application/atom+xml")}' href='{(description ? "description" : "search?q={searchTerms}")}'/></feed>");
        }));
        var integration = new OpdsIntegration(http, Connection);
        var probe = await integration.ProbeAsync(default);
        Assert.Null(probe.InstanceId);
        Assert.Contains(IntegrationOperations.Search, probe.Capabilities[0].Operations);
        var result = await integration.DiscoverAsync(new(MediaKinds.Book, "one & two", null, null, 25), true, default);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task CatalogWithoutSearchOnlyNegotiatesBrowsing() {
        using var http = new OpdsHttpClient(Connection, new Handler(_ => Ok(Feed("one"))));
        var integration = new OpdsIntegration(http, Connection);
        Assert.DoesNotContain(IntegrationOperations.Search, (await integration.ProbeAsync(default)).Capabilities[0].Operations);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
