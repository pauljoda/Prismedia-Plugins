using System.Net;
using System.Text;

namespace Prismedia.Plugin.OpenLibrary.Tests;

public sealed class OpenLibraryEditionIdentityTests {
    [Fact]
    public async Task ExactEditionIsRetainedWhenTheRequestAlsoContainsItsWork() {
        var proposal = await LookupAsync(new Dictionary<string, string> {
            [OpenLibraryMetadata.WorkIdKey] = "OL1W", [OpenLibraryMetadata.EditionIdKey] = "OL2M"
        });
        Assert.Equal("OL2M", proposal.Patch.ExternalIds[OpenLibraryMetadata.EditionIdKey]);
        Assert.Equal("9780140328721", proposal.Patch.ExternalIds["isbn13"]);
        Assert.Equal("Requested publisher", proposal.Patch.Studio);
    }

    [Fact]
    public async Task WorkLookupDoesNotClaimAnArbitrarilyRankedEditionOrItsIsbn() {
        var proposal = await LookupAsync(new Dictionary<string, string> { [OpenLibraryMetadata.WorkIdKey] = "OL1W" });
        Assert.False(proposal.Patch.ExternalIds.ContainsKey(OpenLibraryMetadata.EditionIdKey));
        Assert.False(proposal.Patch.ExternalIds.ContainsKey("isbn13"));
        Assert.False(proposal.Patch.ExternalIds.ContainsKey("isbn10"));
        Assert.Null(proposal.Patch.Studio);
        Assert.False(proposal.Patch.Dates.ContainsKey("editionPublished"));
    }

    [Fact]
    public async Task AConflictingEditionAndWorkCannotBeSilentlyCombined() {
        await Assert.ThrowsAsync<InvalidOperationException>(() => LookupAsync(new Dictionary<string, string> {
            [OpenLibraryMetadata.WorkIdKey] = "OL9W", [OpenLibraryMetadata.EditionIdKey] = "OL2M"
        }));
    }

    [Fact]
    public async Task MissingExplicitEditionDoesNotFallBackToTheWorkOrTitleSearch() {
        await Assert.ThrowsAsync<InvalidOperationException>(() => LookupAsync(new Dictionary<string, string> {
            [OpenLibraryMetadata.WorkIdKey] = "OL1W", [OpenLibraryMetadata.EditionIdKey] = "OL404M"
        }));
    }

    [Fact]
    public async Task ExplicitWorkSelectionDoesNotReuseAnOldStoredEdition() {
        var proposal = await LookupAsync(new Dictionary<string, string> { [OpenLibraryMetadata.WorkIdKey] = "OL9W" },
            new Dictionary<string, string> {
                [OpenLibraryMetadata.WorkIdKey] = "OL1W",
                [OpenLibraryMetadata.EditionIdKey] = "OL2M",
                ["isbn13"] = "9780140328721"
            });
        Assert.Equal("OL9W", proposal.Patch.ExternalIds[OpenLibraryMetadata.WorkIdKey]);
        Assert.False(proposal.Patch.ExternalIds.ContainsKey(OpenLibraryMetadata.EditionIdKey));
        var retired = Assert.Single(proposal.Patch.RetiredExternalIds);
        Assert.Equal(OpenLibraryMetadata.EditionIdKey, retired.Namespace);
        Assert.Equal("OL2M", retired.Value);
        Assert.DoesNotContain(proposal.Patch.RetiredExternalIds, identity => identity.Namespace == "isbn13");
    }

    [Fact]
    public async Task AcceptedWorkIdentityWinsOverAStaleEditionUrlAndSharedIsbn() {
        var proposal = await LookupAsync(
            new Dictionary<string, string>(),
            new Dictionary<string, string> {
                [OpenLibraryMetadata.PrimaryIdentityNamespace] = "OL9W",
                [OpenLibraryMetadata.WorkIdKey] = "OL9W",
                ["isbn13"] = "9780140328721"
            },
            ["https://openlibrary.org/books/OL2M"]);

        Assert.Equal("OL9W", proposal.Patch.ExternalIds[OpenLibraryMetadata.WorkIdKey]);
        Assert.False(proposal.Patch.ExternalIds.ContainsKey(OpenLibraryMetadata.EditionIdKey));
        Assert.False(proposal.Patch.ExternalIds.ContainsKey("isbn13"));
        Assert.Empty(proposal.Patch.RetiredExternalIds);
    }

    [Fact]
    public async Task IsbnLookupRetainsOnlyIdentifiersFromThatExactEdition() {
        var proposal = await LookupAsync(new Dictionary<string, string> { ["isbn13"] = "978-0-14-032872-1" });
        Assert.Equal("OL2M", proposal.Patch.ExternalIds[OpenLibraryMetadata.EditionIdKey]);
        Assert.Equal("9780140328721", proposal.Patch.ExternalIds["isbn13"]);
    }

    [Fact]
    public async Task EditionCannotClaimAConflictingRequestedIsbn() {
        await Assert.ThrowsAsync<InvalidOperationException>(() => LookupAsync(new Dictionary<string, string> {
            [OpenLibraryMetadata.EditionIdKey] = "OL2M", ["isbn13"] = "9780553573404"
        }));
    }

    private static async Task<EntityMetadataProposal> LookupAsync(IReadOnlyDictionary<string, string> requested,
        IReadOnlyDictionary<string, string>? stored = null,
        IReadOnlyList<string>? storedUrls = null) {
        using var http = new HttpClient(new Handler());
        var plugin = new OpenLibraryPlugin(new OpenLibraryApiClient(http, TimeSpan.Zero));
        var request = new IdentifyPluginRequest(2, "lookup-id", new Dictionary<string, string>(),
            new IdentifyEntitySnapshot(Guid.NewGuid(), "book", "Example", stored, storedUrls),
            new IdentifyQuery(null, null, requested), new IdentifyMatchHints(stored ?? new Dictionary<string, string>(), storedUrls ?? [], "Example", null),
            IncludeRelationshipDetails: false, IncludeStructuralChildren: false);
        return Assert.IsType<EntityMetadataProposal>((await plugin.IdentifyAsync(request)).Proposal);
    }

    private sealed class Handler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var path = request.RequestUri!.AbsolutePath;
            var status = path == "/books/OL404M.json" ? HttpStatusCode.NotFound : HttpStatusCode.OK;
            var json = path switch {
                "/books/OL2M.json" or "/isbn/9780140328721.json" => """{"key":"/books/OL2M","title":"Example","works":[{"key":"/works/OL1W"}],"publishers":["Requested publisher"],"isbn_13":["9780140328721"]}""",
                "/works/OL1W.json" or "/works/OL9W.json" => """{"title":"Example","authors":[]}""",
                "/search.json" => """{"docs":[{"title":"Example","isbn":["9780553573404"]}]}""",
                "/works/OL1W/editions.json" or "/works/OL9W/editions.json" => """{"entries":[{"key":"/books/OL3M","title":"Example","publishers":["Other publisher"],"isbn_13":["9780553573404"],"languages":[{"key":"/languages/eng"}]}]}""",
                "/books/OL404M.json" => "{}",
                _ => throw new InvalidOperationException($"Unexpected request {path}")
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
