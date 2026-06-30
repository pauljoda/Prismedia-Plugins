using System.Net;
using System.Text;

namespace Prismedia.Plugin.OpenLibrary.Tests;

public sealed class OpenLibraryPluginTests {
    [Fact]
    public async Task SearchIncludesSeriesCandidateWhenQueryMatchesSeriesSubject() {
        using var http = new HttpClient(new StubHandler(request => {
            Assert.Equal("/search.json", request.RequestUri?.AbsolutePath);
            Assert.Contains("A%20Song%20of%20Ice%20and%20Fire", request.RequestUri?.Query);
            return """
                {
                  "numFound": 2,
                  "docs": [
                    {
                      "key": "/works/OL257943W",
                      "title": "A Game of Thrones",
                      "author_name": ["George R. R. Martin"],
                      "author_key": ["OL234664A"],
                      "first_publish_year": 1996,
                      "cover_i": 9269962,
                      "subject": ["Fantasy", "series:A Song of Ice and Fire"]
                    },
                    {
                      "key": "/works/OL257939W",
                      "title": "A Clash of Kings",
                      "author_name": ["George R. R. Martin"],
                      "author_key": ["OL234664A"],
                      "first_publish_year": 1998,
                      "cover_i": 8231751,
                      "subject": ["Fantasy", "series:A Song of Ice and Fire"]
                    }
                  ]
                }
                """;
        }));
        var plugin = new OpenLibraryPlugin(new OpenLibraryApiClient(http, TimeSpan.Zero));

        var result = await plugin.IdentifyAsync(BookRequest(
            "search",
            "book",
            "A Song of Ice and Fire",
            new IdentifyQuery("A Song of Ice and Fire", null, null)));

        Assert.Equal("candidates", result.Type);
        Assert.Contains(result.Candidates, candidate =>
            candidate.ExternalIds.TryGetValue(OpenLibraryMetadata.SeriesKey, out var series) &&
            series == "A Song of Ice and Fire");
    }

    [Fact]
    public async Task BookSearchPrefersSeriesCandidateWhenTopVolumeBelongsToSeries() {
        var seriesPath = Path.Combine(Path.GetTempPath(), $"openlibrary-series-{Guid.NewGuid():N}");
        Directory.CreateDirectory(seriesPath);
        using var http = new HttpClient(new StubHandler(request => {
            Assert.Equal("/search.json", request.RequestUri?.AbsolutePath);
            Assert.Contains("Game%20of%20Thrones", request.RequestUri?.Query);
            return """
                {
                  "numFound": 3,
                  "docs": [
                    {
                      "key": "/works/OL257943W",
                      "title": "A Game of Thrones",
                      "author_name": ["George R. R. Martin"],
                      "author_key": ["OL234664A"],
                      "first_publish_year": 1996,
                      "cover_i": 9269962,
                      "subject": ["Fantasy", "series:A Song of Ice and Fire"]
                    },
                    {
                      "key": "/works/OL257939W",
                      "title": "A Clash of Kings",
                      "author_name": ["George R. R. Martin"],
                      "author_key": ["OL234664A"],
                      "first_publish_year": 1998,
                      "cover_i": 8231751,
                      "subject": ["Fantasy", "series:A Song of Ice and Fire"]
                    },
                    {
                      "key": "/works/OL999999W",
                      "title": "Game of Thrones",
                      "author_name": ["Book Of Thrones"],
                      "first_publish_year": 2017,
                      "subject": ["Television"]
                    }
                  ]
                }
                """;
        }));
        var plugin = new OpenLibraryPlugin(new OpenLibraryApiClient(http, TimeSpan.Zero));

        try {
            var result = await plugin.IdentifyAsync(BookRequest(
                "search",
                "book",
                "Game of Thrones",
                new IdentifyQuery("Game of Thrones", null, null),
                seriesPath));

            Assert.Equal("candidates", result.Type);
            var first = result.Candidates[0];
            Assert.Equal("A Song of Ice and Fire", first.Title);
            Assert.Equal("series-subject", first.MatchReason);
            Assert.Equal("series:A Song of Ice and Fire", first.ExternalIds[OpenLibraryMetadata.Provider]);
            Assert.Equal("A Song of Ice and Fire", first.ExternalIds[OpenLibraryMetadata.SeriesKey]);
        } finally {
            Directory.Delete(seriesPath);
        }
    }

    [Fact]
    public async Task SeriesLookupReturnsIndividualBooksAsChildren() {
        using var http = new HttpClient(new StubHandler(request => request.RequestUri?.AbsolutePath switch {
            "/search.json" when request.RequestUri.Query.Contains("subject=series%3AA%20Song%20of%20Ice%20and%20Fire") => """
                {
                  "numFound": 2,
                  "docs": [
                    { "key": "/works/OL257943W", "title": "A Game of Thrones", "first_publish_year": 1996, "cover_i": 9269962, "subject": ["series:A Song of Ice and Fire"] },
                    { "key": "/works/OL257939W", "title": "A Clash of Kings", "first_publish_year": 1998, "cover_i": 8231751, "subject": ["series:A Song of Ice and Fire"] }
                  ]
                }
                """,
            _ => throw new InvalidOperationException($"Unexpected Open Library request {request.RequestUri}")
        }));
        var plugin = new OpenLibraryPlugin(new OpenLibraryApiClient(http, TimeSpan.Zero));

        var result = await plugin.IdentifyAsync(BookRequest(
            "lookup-id",
            "book",
            "Game of Thrones",
            new IdentifyQuery(null, null, new Dictionary<string, string> {
                [OpenLibraryMetadata.Provider] = "series:A Song of Ice and Fire"
            })));

        Assert.Equal("proposal", result.Type);
        Assert.NotNull(result.Proposal);
        var proposal = result.Proposal!;
        Assert.Equal("book", proposal.TargetKind);
        Assert.Equal("Book series", proposal.Patch.Classification);
        Assert.Collection(
            proposal.Children,
            child => {
                Assert.Equal("book", child.TargetKind);
                Assert.Equal("A Game of Thrones", child.Patch.Title);
                Assert.Equal(1, child.Patch.Positions["volumeNumber"]);
                Assert.Equal(0, child.Patch.Positions["sortOrder"]);
            },
            child => {
                Assert.Equal("book", child.TargetKind);
                Assert.Equal("A Clash of Kings", child.Patch.Title);
                Assert.Equal(2, child.Patch.Positions["volumeNumber"]);
                Assert.Equal(1, child.Patch.Positions["sortOrder"]);
            });
    }

    [Fact]
    public async Task SeriesLookupCanReturnRootOnlyWithoutEnumeratingChildren() {
        using var http = new HttpClient(new StubHandler(request =>
            throw new InvalidOperationException($"Unexpected Open Library request {request.RequestUri}")));
        var plugin = new OpenLibraryPlugin(new OpenLibraryApiClient(http, TimeSpan.Zero));

        var result = await plugin.IdentifyAsync(BookRequest(
            "lookup-id",
            "book",
            "Game of Thrones",
            new IdentifyQuery(null, null, new Dictionary<string, string> {
                [OpenLibraryMetadata.Provider] = "series:A Song of Ice and Fire"
            }),
            includeStructuralChildren: false));

        Assert.Equal("proposal", result.Type);
        Assert.NotNull(result.Proposal);
        var proposal = result.Proposal!;
        Assert.Equal("book", proposal.TargetKind);
        Assert.Equal("A Song of Ice and Fire", proposal.Patch.Title);
        Assert.Equal("Book series", proposal.Patch.Classification);
        Assert.Empty(proposal.Children);
        Assert.Empty(proposal.Relationships ?? []);
    }

    [Fact]
    public async Task BookChildUsesAncestorSeriesContextToHydrateProposal() {
        using var http = new HttpClient(new StubHandler(request => request.RequestUri?.AbsolutePath switch {
            "/search.json" when request.RequestUri.Query.Contains("subject=series%3AA%20Song%20of%20Ice%20and%20Fire") => """
                {
                  "numFound": 2,
                  "docs": [
                    { "key": "/works/OL257943W", "title": "A Game of Thrones", "first_publish_year": 1996, "cover_i": 9269962, "number_of_pages_median": 801, "subject": ["series:A Song of Ice and Fire"] },
                    { "key": "/works/OL257939W", "title": "A Clash of Kings", "first_publish_year": 1998, "cover_i": 8231751, "subject": ["series:A Song of Ice and Fire"] }
                  ]
                }
                """,
            "/works/OL257943W.json" => """
                {
                  "key": "/works/OL257943W",
                  "title": "A Game of Thrones",
                  "description": "The first book in A Song of Ice and Fire.",
                  "covers": [9269962],
                  "subjects": ["Fantasy", "series:A Song of Ice and Fire"]
                }
                """,
            "/search.json" when request.RequestUri.Query.Contains("key%3A%2Fworks%2FOL257943W") => """
                {
                  "numFound": 1,
                  "docs": [{
                    "key": "/works/OL257943W",
                    "title": "A Game of Thrones",
                    "first_publish_year": 1996,
                    "cover_i": 9269962,
                    "number_of_pages_median": 801,
                    "subject": ["Fantasy", "series:A Song of Ice and Fire"]
                  }]
                }
                """,
            "/works/OL257943W/editions.json" => """
                { "size": 0, "entries": [] }
                """,
            _ => throw new InvalidOperationException($"Unexpected Open Library request {request.RequestUri}")
        }));
        var plugin = new OpenLibraryPlugin(new OpenLibraryApiClient(http, TimeSpan.Zero));
        var context = new IdentifyStructuralContext(
            [
                new IdentifyEntitySnapshot(
                    Guid.NewGuid(),
                    "book",
                    "A Song of Ice and Fire",
                    new Dictionary<string, string> {
                        [OpenLibraryMetadata.Provider] = "series:A Song of Ice and Fire",
                        [OpenLibraryMetadata.SeriesKey] = "A Song of Ice and Fire"
                    })
            ],
            new Dictionary<string, int> { ["sortOrder"] = 0 });

        var result = await plugin.IdentifyAsync(BookRequest(
            "search",
            "book",
            "Game of Thrones",
            new IdentifyQuery(null, null, null),
            structuralContext: context));

        Assert.Equal("proposal", result.Type);
        Assert.NotNull(result.Proposal);
        var proposal = result.Proposal!;
        Assert.Equal("book", proposal.TargetKind);
        Assert.Equal("series-context", proposal.MatchReason);
        Assert.Equal("A Game of Thrones", proposal.Patch.Title);
        Assert.Equal(801, proposal.Patch.Stats["pageCount"]);
        Assert.Equal(1, proposal.Patch.Positions["volumeNumber"]);
        Assert.Equal(0, proposal.Patch.Positions["sortOrder"]);
    }

    [Fact]
    public async Task WorkLookupHydratesEditionSeriesPositionAndAuthorRelationship() {
        using var http = new HttpClient(new StubHandler(request => request.RequestUri?.AbsolutePath switch {
            "/works/OL257943W.json" => """
                {
                  "key": "/works/OL257943W",
                  "title": "A Game of Thrones",
                  "description": { "value": "***A Game of Thrones*** begins A Song of Ice and Fire." },
                  "authors": [{ "author": { "key": "/authors/OL234664A" } }],
                  "covers": [9269962],
                  "subjects": ["Fantasy", "series:A Song of Ice and Fire"],
                  "subject_places": ["Westeros"],
                  "subject_people": ["Eddard Stark"],
                  "links": [{ "title": "Official", "url": "https://georgerrmartin.com/grrm_book/a-game-of-thrones-a-song-of-ice-and-fire-book-one/" }]
                }
                """,
            "/search.json" when request.RequestUri.Query.Contains("key%3A%2Fworks%2FOL257943W") => """
                {
                  "numFound": 1,
                  "docs": [{
                    "key": "/works/OL257943W",
                    "title": "A Game of Thrones",
                    "author_name": ["George R. R. Martin"],
                    "author_key": ["OL234664A"],
                    "first_publish_year": 1996,
                    "cover_i": 9269962,
                    "number_of_pages_median": 801,
                    "ratings_average": 4.2,
                    "ratings_count": 754,
                    "subject": ["Fantasy", "series:A Song of Ice and Fire"],
                    "isbn": ["9780553573404"]
                  }]
                }
                """,
            "/works/OL257943W/editions.json" => """
                {
                  "size": 1,
                  "entries": [{
                    "key": "/books/OL807276M",
                    "title": "A Game of Thrones",
                    "publishers": ["Bantam"],
                    "publish_date": "August 1996",
                    "number_of_pages": 694,
                    "covers": [9269962],
                    "isbn_10": ["0553573403"],
                    "isbn_13": ["9780553573404"],
                    "physical_format": "Mass Market Paperback",
                    "languages": [{ "key": "/languages/eng" }],
                    "works": [{ "key": "/works/OL257943W" }]
                  }]
                }
                """,
            "/search.json" when request.RequestUri.Query.Contains("subject=series%3AA%20Song%20of%20Ice%20and%20Fire") => """
                {
                  "numFound": 2,
                  "docs": [
                    { "key": "/works/OL257943W", "title": "A Game of Thrones", "first_publish_year": 1996, "subject": ["series:A Song of Ice and Fire"] },
                    { "key": "/works/OL257939W", "title": "A Clash of Kings", "first_publish_year": 1998, "subject": ["series:A Song of Ice and Fire"] }
                  ]
                }
                """,
            "/authors/OL234664A.json" => """
                {
                  "key": "/authors/OL234664A",
                  "name": "George R. R. Martin",
                  "bio": "American author and screenwriter.",
                  "birth_date": "20 September 1948",
                  "photos": [6387401],
                  "remote_ids": { "wikidata": "Q181677", "goodreads": "346732" },
                  "links": [{ "title": "Official Web Site", "url": "http://www.georgerrmartin.com/" }]
                }
                """,
            _ => throw new InvalidOperationException($"Unexpected Open Library request {request.RequestUri}")
        }));
        var plugin = new OpenLibraryPlugin(new OpenLibraryApiClient(http, TimeSpan.Zero));

        var result = await plugin.IdentifyAsync(BookRequest(
            "lookup-id",
            "book-volume",
            "A Game of Thrones",
            new IdentifyQuery(null, null, new Dictionary<string, string> { [OpenLibraryMetadata.Provider] = "OL257943W" })));

        Assert.Equal("proposal", result.Type);
        Assert.NotNull(result.Proposal);
        var proposal = result.Proposal!;
        Assert.Equal("book-volume", proposal.TargetKind);
        Assert.Equal("A Game of Thrones", proposal.Patch.Title);
        Assert.Equal("Bantam", proposal.Patch.Studio);
        Assert.Equal("OL257943W", proposal.Patch.ExternalIds[OpenLibraryMetadata.WorkIdKey]);
        Assert.Equal("9780553573404", proposal.Patch.ExternalIds["isbn13"]);
        Assert.Equal(694, proposal.Patch.Stats["pageCount"]);
        Assert.Equal(1, proposal.Patch.Positions["volumeNumber"]);
        Assert.Contains("series: A Song of Ice and Fire", proposal.Patch.Tags);
        Assert.Contains("place: Westeros", proposal.Patch.Tags);
        Assert.Contains(proposal.Patch.Credits, credit => credit.Name == "George R. R. Martin" && credit.Role == "author");

        var author = Assert.Single(proposal.Relationships ?? []);
        Assert.Equal("person", author.TargetKind);
        Assert.Equal("George R. R. Martin", author.Patch.Title);
        Assert.Equal("Q181677", author.Patch.ExternalIds["wikidata"]);
        Assert.Single(author.Images);
    }

    [Fact]
    public async Task AuthorLookupPagesPastTheFirstHundredWorks() {
        // 150 works total forces a second page (offset 0 then 100), proving the old 50-item cap is gone.
        static string WorksPage(int offset) {
            const int total = 150;
            var docs = Enumerable.Range(offset, Math.Min(100, total - offset))
                .Select(i => $$"""{ "key": "/works/OL{{i}}W", "title": "Work {{i}}", "first_publish_year": {{2000 + (i % 25)}} }""");
            return $$"""{ "numFound": {{total}}, "docs": [ {{string.Join(",", docs)}} ] }""";
        }

        using var http = new HttpClient(new StubHandler(request => request.RequestUri?.AbsolutePath switch {
            "/authors/OL9A.json" => """{ "name": "Prolific Author", "key": "/authors/OL9A" }""",
            "/search.json" => WorksPage((request.RequestUri?.Query ?? "").Contains("offset=100") ? 100 : 0),
            _ => throw new InvalidOperationException($"Unexpected Open Library request {request.RequestUri}")
        }));
        var plugin = new OpenLibraryPlugin(new OpenLibraryApiClient(http, TimeSpan.Zero));

        var result = await plugin.IdentifyAsync(BookRequest(
            "lookup-id",
            "person",
            "",
            new IdentifyQuery(null, null, new Dictionary<string, string> { [OpenLibraryMetadata.Provider] = "OL9A" }),
            includeStructuralChildren: true));

        Assert.Equal("proposal", result.Type);
        Assert.Equal("person", result.Proposal!.TargetKind);
        Assert.Equal(150, result.Proposal!.Children.Count);
    }

    [Fact]
    public async Task EmptyBookSearchReturnsNone() {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No request expected.")));
        var plugin = new OpenLibraryPlugin(new OpenLibraryApiClient(http, TimeSpan.Zero));

        var result = await plugin.IdentifyAsync(BookRequest(
            "search",
            "book",
            "",
            new IdentifyQuery(null, null, null)));

        Assert.Equal("none", result.Type);
    }

    private static IdentifyPluginRequest BookRequest(
        string action,
        string kind,
        string title,
        IdentifyQuery query,
        string? filePath = null,
        IdentifyStructuralContext? structuralContext = null,
        bool includeRelationshipDetails = true,
        bool includeStructuralChildren = true) =>
        new(
            1,
            action,
            new Dictionary<string, string>(),
            new IdentifyEntitySnapshot(Guid.NewGuid(), kind, title),
            query,
            new IdentifyMatchHints(new Dictionary<string, string>(), [], title, filePath),
            structuralContext,
            IncludeRelationshipDetails: includeRelationshipDetails,
            IncludeStructuralChildren: includeStructuralChildren);

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
    }
}
