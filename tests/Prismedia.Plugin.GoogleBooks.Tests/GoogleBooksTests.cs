using System.Net;
using System.Text;
using Prismedia.Plugin.Metadata;

namespace Prismedia.Plugin.GoogleBooks.Tests;

public sealed class GoogleBooksTests {
    [Fact]
    public async Task SearchKeepsEditionsSeparateAndExplainsEditionFactsInTheChoice() {
        using var client = Client(request => {
            Assert.Contains("intitle", Uri.UnescapeDataString(request.RequestUri!.Query));
            Assert.Contains("inauthor", Uri.UnescapeDataString(request.RequestUri.Query));
            return Response("{\"items\":[" + Volume("first", "en") + "," + Volume("second", "fr") + "]}");
        });
        var result = await new GoogleBooksPlugin(client).IdentifyAsync(Request(fields: new() { ["title"] = "Example", ["author"] = "Writer" }));
        Assert.Equal(2, result.Candidates.Count);
        Assert.NotEqual(result.Candidates[0].ExternalIds["googlebooks"], result.Candidates[1].ExternalIds["googlebooks"]);
        Assert.Contains("en", result.Candidates[0].Overview);
        Assert.Contains("Publisher", result.Candidates[0].Overview);
        Assert.Null(result.Proposal);
    }

    [Fact]
    public async Task ExactIdCarriesEditionIdentifiersWithoutInventingASeriesOrFirstPublicationDate() {
        using var client = Client(request => {
            Assert.Equal("/books/v1/volumes/Ab_C-12", request.RequestUri!.AbsolutePath);
            return Response(Volume("Ab_C-12"));
        });
        var result = await new GoogleBooksPlugin(client).IdentifyAsync(Request(ids: new() { ["googlebooks"] = "Ab_C-12" }));
        var proposal = Assert.IsType<EntityMetadataProposal>(result.Proposal);
        Assert.Equal("Ab_C-12", proposal.Patch.ExternalIds["googlebooks"]);
        Assert.Equal("9780140328721", proposal.Patch.ExternalIds["isbn13"]);
        Assert.DoesNotContain("publication", proposal.Patch.Dates.Keys);
        Assert.Equal("2001-03", proposal.Patch.Dates["editionPublished"]);
        Assert.Empty(proposal.Children);
        Assert.DoesNotContain("<b>", proposal.Patch.Description);
        Assert.StartsWith("https://", Assert.Single(proposal.Images).Url);
    }

    [Fact]
    public async Task MissingExactIdNeverFallsBackToTitleSearch() {
        var calls = 0;
        using var client = Client(_ => { calls++; return new(HttpStatusCode.NotFound); });
        Assert.Null((await new GoogleBooksPlugin(client).IdentifyAsync(Request(ids: new() { ["googlebooks"] = "missing" }))).Proposal);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task IsbnSearchRejectsUnrelatedResultsEvenWhenTheirTitleMatches() {
        using var client = Client(_ => Response("{\"items\":[" + Volume("wrong").Replace("9780140328721", "9780553573404") + "]}"));
        var result = await new GoogleBooksPlugin(client).IdentifyAsync(Request(ids: new() { ["isbn13"] = "9780140328721" }));
        Assert.Null(result.Proposal);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task ConflictingExactIdAndIsbnRequiresReviewInsteadOfReplacingIdentity() {
        using var client = Client(_ => Response(Volume("chosen")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GoogleBooksPlugin(client).IdentifyAsync(
            Request(ids: new() { ["googlebooks"] = "chosen", ["isbn13"] = "9780553573404" })));
    }

    [Fact]
    public async Task SharedIsbnStillRequiresChoosingBetweenDistinctCatalogEditions() {
        using var client = Client(_ => Response("{\"items\":[" + Volume("first") + "," + Volume("second") + "]}"));
        var result = await new GoogleBooksPlugin(client).IdentifyAsync(Request(ids: new() { ["isbn13"] = "9780140328721" }));
        Assert.Equal(2, result.Candidates.Count);
        Assert.Null(result.Proposal);
    }

    [Fact]
    public async Task AdultMetadataIsExcludedWhenNsfwIsDisabled() {
        using var client = Client(_ => Response(Volume("adult").Replace("NOT_MATURE", "MATURE")));
        var result = await new GoogleBooksPlugin(client).IdentifyAsync(Request(ids: new() { ["googlebooks"] = "adult" }));
        Assert.Null(result.Proposal);
    }

    [Fact]
    public async Task ForeignLookupUrlsAndMalformedIsbnsDoNotMakeRequests() {
        using var client = Client(_ => throw new InvalidOperationException("No HTTP call expected."));
        var plugin = new GoogleBooksPlugin(client);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.IdentifyAsync(Request() with { Query = new(null, "https://example.invalid/?id=abc", null) }));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.IdentifyAsync(Request(ids: new() { ["isbn13"] = "9780140328720" })));
    }

    [Fact]
    public async Task ContradictorySelectorsAreRejectedBeforeAnyHttpCall() {
        using var client = Client(_ => throw new InvalidOperationException("No HTTP call expected."));
        var plugin = new GoogleBooksPlugin(client);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.IdentifyAsync(Request(ids: new() {
            ["isbn13"] = "9780140328721", ["isbn10"] = "0553573403"
        })));
        var request = Request(ids: new() { ["googlebooks"] = "first" });
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.IdentifyAsync(request with {
            Query = request.Query with { Url = "https://books.google.com/books?id=second" }
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.IdentifyAsync(request with {
            Query = request.Query with { Url = "https://books.google.com/books?id=first&id=second" }
        }));
    }

    [Fact]
    public async Task IsbnSearchWithOneLimitedResultStillRequiresChoosingTheCatalogEdition() {
        using var client = Client(_ => Response("{\"totalItems\":30,\"items\":[" + Volume("first") + "]}"));
        var request = Request(ids: new() { ["isbn10"] = "0140328726", ["isbn13"] = "9780140328721" });
        var result = await new GoogleBooksPlugin(client).IdentifyAsync(request with { Query = request.Query with { Limit = 1 } });
        Assert.Single(result.Candidates);
        Assert.Null(result.Proposal);
    }

    [Fact]
    public async Task ExplicitSearchDoesNotReuseStoredIdentityAndHonorsProviderPageSize() {
        var calls = 0;
        using var client = Client(request => {
            var query = request.RequestUri!.Query;
            Assert.Contains(calls == 0 ? "maxResults=40&startIndex=0" : "maxResults=5&startIndex=40", query);
            var offset = calls++ * 40;
            return Response("{\"items\":[" + string.Join(',', Enumerable.Range(offset, offset == 0 ? 40 : 5).Select(i => Volume("id" + i))) + "]}");
        });
        var request = Request(fields: new() { ["title"] = "A new search" });
        var result = await new GoogleBooksPlugin(client).IdentifyAsync(request with {
            Query = request.Query with { Limit = 45 },
            Entity = request.Entity with { ExternalIds = new Dictionary<string, string> { ["googlebooks"] = "old" } }
        });
        Assert.Equal(45, result.Candidates.Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExactUrlUsesOnlyTheKnownApiAndKeepsEquivalentIsbnIdentity() {
        using var client = Client(request => {
            Assert.Equal("www.googleapis.com", request.RequestUri!.Host);
            Assert.EndsWith("/Ab_C-12", request.RequestUri.AbsolutePath);
            return Response(Volume("Ab_C-12"));
        });
        var request = Request(ids: new() { ["isbn10"] = "0-140-32872-6" });
        var result = await new GoogleBooksPlugin(client).IdentifyAsync(request with {
            Query = request.Query with { Url = "https://books.google.com/books?id=Ab_C-12" }
        });
        Assert.NotNull(result.Proposal);
    }

    [Fact]
    public async Task MissingApiKeyIsReportedWithoutRequestingGoogle() {
        using var client = Client(_ => throw new InvalidOperationException("No HTTP call expected."));
        await Assert.ThrowsAsync<ArgumentException>(() => new GoogleBooksPlugin(client).IdentifyAsync(Request() with {
            Auth = new Dictionary<string, string>()
        }));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task UpstreamFailureNeverEchoesTheApiKeyOrBody(HttpStatusCode status) {
        using var client = Client(_ => new(status) { Content = new StringContent("private-api-key") });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new GoogleBooksClient(client)
            .GetAsync<GoogleVolume>("volumes/id", "private-api-key"));
        Assert.DoesNotContain("private-api-key", error.Message);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeDeserialization() {
        using var client = Client(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new GoogleBooksClient(client).GetAsync<GoogleVolume>("volumes/id", "key"));
    }

    private static IdentifyPluginRequest Request(Dictionary<string, string>? ids = null, Dictionary<string, string>? fields = null) => new(
        2, ids is null ? "search" : "lookup-id", new Dictionary<string, string> { ["apiKey"] = "test-key" },
        new IdentifyEntitySnapshot(Guid.NewGuid(), "book", "Example"), new IdentifyQuery(null, null, ids, Fields: fields),
        new IdentifyMatchHints(new Dictionary<string, string>(), [], "Example", null));

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler(send));
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static string Volume(string id, string language = "en") => $$$$"""
        {"id":"{{{{id}}}}","volumeInfo":{"title":"Example","authors":["Writer"],"publisher":"Publisher","publishedDate":"2001-03",
        "description":"<b>A story</b>","language":"{{{{language}}}}","printType":"BOOK","maturityRating":"NOT_MATURE",
        "industryIdentifiers":[{"type":"ISBN_13","identifier":"9780140328721"}],
        "imageLinks":{"thumbnail":"http://books.google.com/books/content?id={{{{id}}}}"}}}
        """;
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
