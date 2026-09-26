using System.Net;
using System.Text;
using Prismedia.Plugin.ArchiveOrg;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.ArchiveOrg.Tests;

public sealed class ArchiveOrgIntegrationTests {
    private const string Identifier = "AtomicAttack05195301Ctc_201505";
    private const string FileName = "Atomic_Attack_05_195301_ctc.cbz";
    private const string Sha1 = "35c25cb7d86601a9d06ea4278fabc1b104e8e0e2";
    private const long Size = 27482104;
    private const string License = "http://creativecommons.org/publicdomain/mark/1.0/";
    private const string ItemUrl = "https://archive.org/metadata/" + Identifier;

    [Fact]
    public async Task PublicDomainSearchAndOriginalCbzResolveToPinnedAnonymousHost() {
        var fixture = new Fixture();
        using var http = new ArchiveOrgHttpClient(Connection, fixture);
        var adapter = new ArchiveOrgIntegration(http);

        var results = await adapter.DiscoverAsync(new(MediaKinds.Comic, "Atomic Attack", null, null, 10), true, default);
        var container = Assert.Single(results.Items);
        Assert.True(container.IsContainer);
        Assert.Empty(container.Offers);
        Assert.Equal("5", container.Publication.IssueLabel);
        Assert.Equal(ItemUrl, container.Selection.Locator);

        var page = await adapter.DiscoverAsync(new(MediaKinds.Comic, null, null, ItemUrl, 10), false, default);
        var comic = Assert.Single(page.Items);
        Assert.False(comic.IsContainer);
        Assert.Equal(FileName, Assert.Single(comic.Offers).Id);
        Assert.Equal(Size, comic.Offers[0].ByteSize);

        var resolved = await adapter.ResolveAsync(new(comic.Selection, FileName), default);
        Assert.Equal("https://dn760009.eu.archive.org/0/items/" + Identifier + "/" + FileName, resolved.Delivery.Url);
        Assert.Equal(Size, resolved.Delivery.ByteSize);
        Assert.Equal(Sha1, resolved.Delivery.Sha1);
        Assert.Empty(resolved.Delivery.Headers);
        Assert.Equal(5, fixture.Calls);
    }

    [Fact]
    public async Task LoanOrUnlabeledResultsNeverBecomeDownloadableComics() {
        var fixture = new Fixture { License = "https://creativecommons.org/licenses/by-nc-nd/4.0/" };
        using var http = new ArchiveOrgHttpClient(Connection, fixture);
        var adapter = new ArchiveOrgIntegration(http);
        Assert.Empty((await adapter.DiscoverAsync(new(MediaKinds.Comic, "Atomic Attack", null, null, 10), true, default)).Items);
        await Assert.ThrowsAsync<IntegrationFailure>(() => adapter.DiscoverAsync(new(MediaKinds.Comic, null, null, ItemUrl, 10), false, default));
    }

    [Fact]
    public async Task DerivativeOrPrivateFilesAreNotOffersAndForeignRedirectsFail() {
        var fixture = new Fixture { FileSource = "derivative" };
        using var http = new ArchiveOrgHttpClient(Connection, fixture);
        var adapter = new ArchiveOrgIntegration(http);
        Assert.Empty((await adapter.DiscoverAsync(new(MediaKinds.Comic, null, null, ItemUrl, 10), false, default)).Items);
        fixture.FileSource = "original";
        fixture.RedirectHost = "archive.org.evil.test";
        await Assert.ThrowsAsync<IntegrationFailure>(() => adapter.ResolveAsync(
            new(new(Identifier + "/" + FileName, ItemUrl, MediaKinds.Comic), FileName), default));
    }

    [Theory]
    [InlineData("http://archive.org/")]
    [InlineData("https://other.test/")]
    [InlineData("https://archive.org/metadata/x")]
    public void CatalogOriginMustBeThePublicArchiveRoot(string address) {
        Assert.Throws<IntegrationFailure>(() => new ArchiveOrgHttpClient(Connection with { BaseUrl = address }));
    }

    private static ConnectionContext Connection => new(Guid.NewGuid(), "https://archive.org/", null,
        new Dictionary<string, string>(), new Dictionary<string, string>());

    private sealed class Fixture : HttpMessageHandler {
        internal string License { get; set; } = ArchiveOrgIntegrationTests.License;
        internal string FileSource { get; set; } = "original";
        internal string RedirectHost { get; set; } = "dn760009.eu.archive.org";
        internal int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            Calls++;
            Assert.Contains("Prismedia-ArchiveOrg/1.0.0", request.Headers.UserAgent.ToString());
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response;
            if (path == "/advancedsearch.php") response = Json("""
                {"response":{"numFound":1,"docs":[{"identifier":"AtomicAttack05195301Ctc_201505","title":"ATOMIC ATTACK! No. 5 - Comic Book, 1953","licenseurl":"LICENSE"}]}}
                """.Replace("LICENSE", License));
            else if (path == "/metadata/" + Identifier) response = Json("""
                {"metadata":{"title":"ATOMIC ATTACK! No. 5 - Comic Book, 1953","creator":"Public archive","licenseurl":"LICENSE"},
                 "files":[{"name":"Atomic_Attack_05_195301_ctc.cbz","source":"SOURCE","size":"27482104","sha1":"35c25cb7d86601a9d06ea4278fabc1b104e8e0e2"}]}
                """.Replace("LICENSE", License).Replace("SOURCE", FileSource));
            else if (path.StartsWith("/download/", StringComparison.Ordinal)) {
                response = new(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://" + RedirectHost + "/0/items/" + Identifier + "/" + FileName);
            } else if (request.RequestUri.Host == RedirectHost) {
                response = new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentLength = Size;
            } else response = new(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
