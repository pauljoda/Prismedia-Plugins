using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Commons;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Commons.Tests;

public sealed class CommonsIntegrationTests {
    [Fact]
    public async Task EmptyMediaWikiGeneratorResultIsAnEmptyCatalogPage() {
        using var fixture = new Fixture { ResponseOverride = new { batchcomplete = true } };
        var page = await fixture.Search();
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ApiErrorsAndOversizedBodiesAreRejectedWithoutEchoingTheirContents() {
        using var fixture = new Fixture { ResponseOverride = new { error = new { code = "maxlag", info = "private upstream body" } } };
        var error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Search());
        Assert.DoesNotContain("private upstream body", error.Message);
        fixture.ResponseOverride = new { value = new string('a', 4 * 1024 * 1024) };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Search());
    }

    [Fact]
    public async Task ProbeChecksTheActualWikiAndDeclaresAnonymousImageSearchAndAcquisition() {
        using var fixture = new Fixture();
        var probe = Assert.IsType<ProbeResult>(await fixture.Call(IntegrationOperations.Probe, new { }));
        Assert.Null(probe.InstanceId);
        Assert.Equal([IntegrationOperations.Search], Assert.Single(probe.Capabilities, item => item.Kind == IntegrationCapabilities.Discovery).Operations);
        Assert.All(probe.Capabilities, item => Assert.Equal([MediaKinds.Image], item.EntityKinds));
        fixture.WikiId = "anotherwiki";
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Probe, new { }));
    }

    [Fact]
    public async Task SearchPreservesPlainAttributionAndResolvePinsTheSelectedFileVersion() {
        using var fixture = new Fixture();
        var item = Assert.Single((await fixture.Search()).Items);
        Assert.Equal("Example.jpg", item.Publication.Title);
        Assert.Equal("Artist & illustrator", item.Publication.Attribution!.Creator);
        Assert.Equal("CC BY 4.0", item.Publication.Attribution.LicenseName);
        Assert.Equal("Own work", item.Publication.Attribution.Credit);
        Assert.True(item.Publication.Attribution.AttributionRequired);
        Assert.Equal(AcquisitionAccess.Download, Assert.Single(item.Offers).Access);
        var resolved = Assert.IsType<ResolvedSourceOffer>(await fixture.Call(IntegrationOperations.Resolve, new ResolveOfferInput(item.Selection, item.Offers[0].Id)));
        Assert.Equal(Fixture.Sha1, resolved.Delivery.Sha1);
        Assert.Null(resolved.Delivery.Sha256);
        Assert.Empty(resolved.Delivery.Headers);
        Assert.Equal("https://upload.wikimedia.org/wikipedia/commons/a/a9/Example.jpg", resolved.Delivery.Url);
        Assert.Equal(100, resolved.Delivery.ByteSize);
    }

    [Fact]
    public async Task ReplacedFilesCannotSatisfyAnEarlierSelection() {
        using var fixture = new Fixture();
        var item = Assert.Single((await fixture.Search()).Items);
        fixture.Info["sha1"] = new string('b', 40);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Resolve, new ResolveOfferInput(item.Selection, item.Offers[0].Id)));
    }

    [Fact]
    public async Task SelectionCannotBeReusedForAnotherPageOrConnection() {
        using var fixture = new Fixture();
        var item = Assert.Single((await fixture.Search()).Items);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Resolve, new ResolveOfferInput(item.Selection with { ItemId = "2" }, item.Offers[0].Id)));
        fixture.Connection = fixture.Connection with { Id = Guid.NewGuid() };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Call(IntegrationOperations.Resolve, new ResolveOfferInput(item.Selection, item.Offers[0].Id)));
    }

    [Fact]
    public async Task CursorIsBoundToTheConnectionQueryAndRequestedLimit() {
        using var fixture = new Fixture { NextOffset = 10 };
        var first = await fixture.Search();
        fixture.NextOffset = null;
        await fixture.Search(cursor: first.NextCursor);
        Assert.Contains("gsroffset=10", fixture.Addresses.Last().Query);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Search("another query", first.NextCursor));
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Search(cursor: first.NextCursor, limit: 5));
        fixture.Connection = fixture.Connection with { Id = Guid.NewGuid() };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Search(cursor: first.NextCursor));
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("application/pdf")]
    public async Task UnsupportedFormatsDoNotBecomeImageDownloads(string mime) {
        using var fixture = new Fixture();
        fixture.Info["mime"] = mime;
        Assert.Empty((await fixture.Search()).Items);
    }

    [Theory]
    [InlineData("https://other.test/image.jpg")]
    [InlineData("http://upload.wikimedia.org/image.jpg")]
    [InlineData("https://user@upload.wikimedia.org/image.jpg")]
    public async Task ForeignOrUnsafeFileUrlsAreRejected(string url) {
        using var fixture = new Fixture();
        fixture.Info["url"] = url;
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Search());
    }

    [Fact]
    public async Task OversizedImagesAndMissingFileEvidenceAreNotOffered() {
        using var fixture = new Fixture();
        fixture.Info["size"] = 64L * 1024 * 1024 + 1;
        Assert.Empty((await fixture.Search()).Items);
        fixture.Info["size"] = 100;
        fixture.Info.Remove("sha1");
        Assert.Empty((await fixture.Search()).Items);
    }

    [Theory]
    [InlineData("http://commons.wikimedia.org")]
    [InlineData("https://elsewhere.test")]
    [InlineData("https://commons.wikimedia.org/unexpected")]
    public void ConnectionCannotTurnTheAdapterIntoAnArbitraryHttpClient(string baseUrl) {
        Assert.Throws<IntegrationFailure>(() => new CommonsClient(Fixture.Context with { BaseUrl = baseUrl }));
    }

    [Fact]
    public async Task RedirectsAndUpstreamErrorsDoNotExposeResponseBodies() {
        using var fixture = new Fixture { Status = HttpStatusCode.Redirect };
        var error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Search());
        Assert.DoesNotContain("private upstream body", error.Message);
        Assert.Single(fixture.Addresses);
    }

    private sealed class Fixture : IDisposable {
        internal const string Sha1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal static ConnectionContext Context => new(Guid.NewGuid(), "https://commons.wikimedia.org", null, new Dictionary<string, string>(), new Dictionary<string, string>());
        internal ConnectionContext Connection { get; set; } = Context;
        internal string WikiId { get; set; } = "commonswiki";
        internal int? NextOffset { get; set; }
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal object? ResponseOverride { get; set; }
        internal List<Uri> Addresses { get; } = [];
        internal Dictionary<string, object> Info { get; } = new() {
            ["timestamp"] = "2026-01-01T00:00:00Z", ["size"] = 100, ["width"] = 10, ["height"] = 10, ["mime"] = "image/jpeg", ["sha1"] = Sha1,
            ["url"] = "https://upload.wikimedia.org/wikipedia/commons/a/a9/Example.jpg?utm_source=fixture",
            ["descriptionurl"] = "https://commons.wikimedia.org/wiki/File:Example.jpg",
            ["extmetadata"] = new Dictionary<string, object> {
                ["Artist"] = new { value = "<a href='/wiki/Artist'>Artist &amp; illustrator</a>" }, ["Credit"] = new { value = "<span>Own work</span>" },
                ["LicenseShortName"] = new { value = "CC BY 4.0" }, ["LicenseUrl"] = new { value = "https://creativecommons.org/licenses/by/4.0/" },
                ["AttributionRequired"] = new { value = "true" }, ["UsageTerms"] = new { value = "Attribution" }, ["ImageDescription"] = new { value = "<p>Example image</p>" }
            }
        };
        internal async Task<CatalogPage> Search(string query = "Example", string? cursor = null, int limit = 25) =>
            Assert.IsType<CatalogPage>(await Call(IntegrationOperations.Search, new DiscoveryInput(MediaKinds.Image, query, cursor, null, limit)));
        internal async Task<object> Call(string operation, object input) {
            using var http = new CommonsClient(Connection, new Handler(request => {
                Addresses.Add(request.RequestUri!);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Null(request.Headers.Authorization);
                Assert.NotEmpty(request.Headers.UserAgent);
                object body = request.RequestUri!.Query.Contains("meta=siteinfo", StringComparison.Ordinal)
                    ? new { query = new { general = new { wikiid = WikiId, generator = "MediaWiki fixture" } } }
                    : new Dictionary<string, object> { ["query"] = new { pages = new[] { new { pageid = 1, ns = 6, title = "File:Example.jpg", index = 1, imageinfo = new[] { Info } } } },
                        ["continue"] = new { gsroffset = NextOffset } };
                return new(Status) { Content = new StringContent(Status == HttpStatusCode.OK ? JsonSerializer.Serialize(ResponseOverride ?? body) : "private upstream body") };
            }));
            return await new CommonsIntegration(http, Connection).DispatchAsync(new(IntegrationProtocol.Name, IntegrationProtocol.Version,
                Guid.NewGuid(), operation, Connection, JsonSerializer.SerializeToElement(input, IntegrationProtocol.Json)), default);
        }
        public void Dispose() { }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
