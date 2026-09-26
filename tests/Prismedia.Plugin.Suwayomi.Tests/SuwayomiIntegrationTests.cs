using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.Suwayomi;

namespace Prismedia.Plugin.Suwayomi.Tests;

public sealed class SuwayomiIntegrationTests {
    [Fact]
    public async Task RootListsInstalledSourcesAndSearchIsScopedToTheSelectedSource() {
        using var fixture = new Fixture();
        var root = await fixture.Browse();
        var source = Assert.Single(root.Items);
        Assert.False(root.CanSearch);
        Assert.True(source.IsContainer);
        Assert.Equal("English Source [en]", source.Publication.Title);
        var results = await fixture.Browse(source.Selection.Locator, searching: true, query: "Example");
        Assert.True(results.CanSearch);
        Assert.Single(results.Items);
        Assert.Contains(fixture.GraphQlBodies, body => body.Contains("\"type\":\"SEARCH\"", StringComparison.Ordinal)
            && body.Contains("\"query\":\"Example\"", StringComparison.Ordinal)
            && body.Contains("\"source\":\"-100\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Browse(searching: true, query: "unscoped"));
    }

    [Fact]
    public async Task ExistingLibraryMangaUsesCachedChaptersWithoutRefreshing() {
        using var fixture = new Fixture { InLibrary = true };
        var manga = await fixture.MangaSelection();
        var chapters = await fixture.Browse(manga.Locator);
        Assert.False(chapters.CanSearch);
        Assert.Empty(chapters.Items);
        Assert.Equal(0, fixture.RefreshCalls);
    }

    [Fact]
    public async Task NewlyDiscoveredNonLibraryMangaFetchesItsOwnChaptersOnceWhenCacheIsEmpty() {
        using var fixture = new Fixture { AddChapterOnRefresh = true };
        var manga = await fixture.MangaSelection();
        var chapters = await fixture.Browse(manga.Locator);
        Assert.Single(chapters.Items);
        Assert.Equal(1, fixture.RefreshCalls);
    }

    [Fact]
    public async Task ExactRequestReplayObservesQueueAndDoesNotEnqueueAgain() {
        using var fixture = new Fixture { Chapters = { Fixture.Chapter() } };
        var selection = await fixture.ChapterSelection();
        var operationId = Guid.NewGuid();
        var first = await fixture.Integration.RequestSourceAsync(new(operationId, selection, SuwayomiCodes.CbzOffer), default);
        var second = await fixture.Integration.RequestSourceAsync(new(operationId, selection, SuwayomiCodes.CbzOffer), default);
        Assert.Equal(SourceAcquisitionStates.Queued, first.State);
        Assert.Equal(SourceAcquisitionStates.Queued, second.State);
        Assert.Equal(AcquisitionAccess.Request, first.Offer.Access);
        Assert.Equal(1, fixture.EnqueueCalls);
        Assert.Equal([1], fixture.EnqueuedChapterIds);
    }

    [Fact]
    public async Task FailedOrUnknownQueueEvidenceNeverStartsAnotherRequest() {
        using var failed = new Fixture { QueueState = SuwayomiCodes.Error, Chapters = { Fixture.Chapter() } };
        var failedSelection = await failed.ChapterSelection();
        var observation = await failed.Integration.RequestSourceAsync(
            new(Guid.NewGuid(), failedSelection, SuwayomiCodes.CbzOffer), default);
        Assert.Equal(SourceAcquisitionStates.Failed, observation.State);
        Assert.Equal(0, failed.EnqueueCalls);

        using var unknown = new Fixture { QueueState = "PAUSED", Chapters = { Fixture.Chapter() } };
        var unknownSelection = await unknown.ChapterSelection();
        await Assert.ThrowsAsync<IntegrationFailure>(() => unknown.Integration.RequestSourceAsync(
            new(Guid.NewGuid(), unknownSelection, SuwayomiCodes.CbzOffer), default));
        Assert.Equal(0, unknown.EnqueueCalls);
    }

    [Fact]
    public async Task ChangedPinnedChapterIdentityIsRejectedBeforeRequestMutation() {
        using var fixture = new Fixture { Chapters = { Fixture.Chapter() } };
        var selection = await fixture.ChapterSelection();
        fixture.Chapters[0] = fixture.Chapters[0] with { Url = "/changed" };
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Integration.RequestSourceAsync(
            new(Guid.NewGuid(), selection, SuwayomiCodes.CbzOffer), default));
        Assert.Equal(0, fixture.EnqueueCalls);
    }

    [Fact]
    public async Task StableItemIdentityUsesSourceUrlsWhileLocatorsStillPinNumericIds() {
        using var fixture = new Fixture { Chapters = { Fixture.Chapter() } };
        var source = Assert.Single((await fixture.Browse()).Items);
        var originalManga = Assert.Single((await fixture.Browse(source.Selection.Locator)).Items);
        var originalChapter = Assert.Single((await fixture.Browse(originalManga.Selection.Locator)).Items);

        fixture.MangaId = 20;
        fixture.Chapters[0] = Fixture.Chapter(id: 2, mangaId: 20);
        var rebuiltManga = Assert.Single((await fixture.Browse(source.Selection.Locator)).Items);
        var rebuiltChapter = Assert.Single((await fixture.Browse(rebuiltManga.Selection.Locator)).Items);

        Assert.Equal(originalManga.Selection.ItemId, rebuiltManga.Selection.ItemId);
        Assert.Equal(originalChapter.Selection.ItemId, rebuiltChapter.Selection.ItemId);
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Integration.ObserveSourceAsync(
            new(originalChapter.Selection, SuwayomiCodes.CbzOffer), default));

        fixture.MangaUrl = "/manga/changed";
        fixture.Chapters[0] = Fixture.Chapter(id: 2, mangaId: 20, url: "/manga/changed/chapter-1");
        var changedManga = Assert.Single((await fixture.Browse(source.Selection.Locator)).Items);
        var changedChapter = Assert.Single((await fixture.Browse(changedManga.Selection.Locator)).Items);

        Assert.NotEqual(originalManga.Selection.ItemId, changedManga.Selection.ItemId);
        Assert.NotEqual(originalChapter.Selection.ItemId, changedChapter.Selection.ItemId);
    }

    [Fact]
    public async Task ReadyChapterResolvesExplicitNoProgressCbzWithoutTrustingHeadSize() {
        using var fixture = new Fixture { HeadReady = true, Chapters = { Fixture.Chapter() with { IsDownloaded = true, PageCount = 12 } } };
        var selection = await fixture.ChapterSelection();
        var observation = await fixture.Integration.ObserveSourceAsync(new(selection, SuwayomiCodes.CbzOffer), default);
        Assert.Equal(SourceAcquisitionStates.Ready, observation.State);
        Assert.Equal(AcquisitionAccess.Download, observation.Offer.Access);
        Assert.Equal("English Source", observation.Publication.EditionLabel);
        var resolved = await fixture.Integration.ResolveAsync(new(selection, SuwayomiCodes.CbzOffer), default);
        Assert.Equal("https://suwayomi.test/api/v1/chapter/1/download?markAsRead=false", resolved.Delivery.Url);
        Assert.Equal("Bearer secret", resolved.Delivery.Headers["Authorization"]);
        Assert.Null(resolved.Delivery.ByteSize);
        Assert.Null(resolved.Delivery.Sha256);
        Assert.True(fixture.HeadCalls >= 2);
    }

    [Fact]
    public async Task StaleDownloadedFlagDoesNotAdvertiseADirectOfferWithoutTheCbzFile() {
        using var fixture = new Fixture { HeadReady = false, Chapters = { Fixture.Chapter() with { IsDownloaded = true } } };
        var manga = await fixture.MangaSelection();
        var chapter = Assert.Single((await fixture.Browse(manga.Locator)).Items);
        Assert.Equal(AcquisitionAccess.Request, Assert.Single(chapter.Offers).Access);
        Assert.Equal(1, fixture.HeadCalls);
    }

    [Fact]
    public async Task ProbeRejectsServerVersionsOutsideTheReviewedContract() {
        using var fixture = new Fixture();
        Assert.Null((await fixture.Integration.ProbeAsync(default)).InstanceId);
        fixture.ServerVersion = "2.4.0";
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Integration.ProbeAsync(default));
    }

    [Fact]
    public void ConnectionCannotClaimAStableSuwayomiInstanceIdentity() {
        Assert.Throws<IntegrationFailure>(() => new SuwayomiClient(Fixture.Context with { ExpectedInstanceId = "invented" }));
    }

    private sealed class Fixture : IDisposable {
        internal static readonly ConnectionContext Context = new(Guid.NewGuid(), "https://suwayomi.test", null,
            new Dictionary<string, string>(), new Dictionary<string, string> { ["token"] = "secret" });
        internal string ServerVersion { get; set; } = SuwayomiCodes.ReportedVersion;
        internal bool InLibrary { get; set; }
        internal bool AddChapterOnRefresh { get; set; }
        internal bool HeadReady { get; set; }
        internal int MangaId { get; set; } = 10;
        internal string MangaUrl { get; set; } = "/manga/example";
        internal string? QueueState { get; set; }
        internal int RefreshCalls { get; set; }
        internal int EnqueueCalls { get; set; }
        internal int HeadCalls { get; set; }
        internal List<int> EnqueuedChapterIds { get; } = [];
        internal List<string> GraphQlBodies { get; } = [];
        internal List<SuwayomiChapter> Chapters { get; } = [];
        private readonly SuwayomiClient client;
        internal SuwayomiIntegration Integration { get; }

        internal Fixture() {
            client = new(Context, new Handler(Respond));
            Integration = new(client, Context);
        }

        internal static SuwayomiChapter Chapter(int id = 1, int mangaId = 10, string url = "/manga/chapter-1") =>
            new(id, url, "Chapter 1", "0", 1, "Scanlator", mangaId, 0, $"https://source.test{url}", false, -1);
        private SuwayomiSource Source() => new("-100", "English Source", "en", "SAFE");
        private SuwayomiManga Manga() => new(MangaId, "-100", MangaUrl, "Example Manga", "Author", "Artist",
            "Description", true, InLibrary, null);

        internal async Task<CatalogPage> Browse(string? container = null, bool searching = false, string? query = null) =>
            await Integration.DiscoverAsync(new(MediaKinds.Comic, query, null, container, 25), searching, default);
        internal async Task<SourceSelection> MangaSelection() {
            var source = Assert.Single((await Browse()).Items);
            return Assert.Single((await Browse(source.Selection.Locator)).Items).Selection;
        }
        internal async Task<SourceSelection> ChapterSelection() {
            var manga = await MangaSelection();
            return Assert.Single((await Browse(manga.Locator)).Items).Selection;
        }

        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request) {
            if (request.Method == HttpMethod.Head) {
                HeadCalls++;
                Assert.Equal("?markAsRead=false", request.RequestUri!.Query);
                return HeadReady
                    ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) { Headers = { ContentLength = 987654 } } }
                    : new(HttpStatusCode.NotFound);
            }
            var body = await request.Content!.ReadAsStringAsync();
            GraphQlBodies.Add(body);
            using var document = JsonDocument.Parse(body);
            var query = document.RootElement.GetProperty("query").GetString()!;
            var variables = document.RootElement.GetProperty("variables");
            object data;
            if (query.Contains("PrismediaAbout", StringComparison.Ordinal)) data = new { aboutServer = new { name = "Suwayomi Server", version = ServerVersion } };
            else if (query.Contains("PrismediaSources", StringComparison.Ordinal)) data = new { sources = new { nodes = new[] { Source() }, totalCount = 1 } };
            else if (query.Contains("PrismediaSourceManga", StringComparison.Ordinal)) data = new { fetchSourceManga = new { mangas = new[] { Manga() }, hasNextPage = false } };
            else if (query.Contains("PrismediaSource(", StringComparison.Ordinal)) data = new { source = Source() };
            else if (query.Contains("PrismediaManga(", StringComparison.Ordinal)) data = new { mangas = new { nodes = new[] { Manga() }, totalCount = 1 } };
            else if (query.Contains("PrismediaChapters", StringComparison.Ordinal)) data = new { chapters = new { nodes = Chapters, totalCount = Chapters.Count } };
            else if (query.Contains("PrismediaRefreshManga", StringComparison.Ordinal)) {
                RefreshCalls++;
                if (AddChapterOnRefresh && Chapters.Count == 0) Chapters.Add(Chapter());
                data = new { fetchMangaAndChapters = new { manga = Manga(), chapters = Chapters.Select(chapter => new { id = chapter.Id }) } };
            } else if (query.Contains("PrismediaExact", StringComparison.Ordinal)) data = ExactData();
            else if (query.Contains("PrismediaEnqueue", StringComparison.Ordinal)) {
                EnqueueCalls++;
                var id = variables.GetProperty("id").GetInt32();
                EnqueuedChapterIds.Add(id);
                data = new { enqueueChapterDownload = new { clientMutationId = variables.GetProperty("clientMutationId").GetString(), downloadStatus = Status(true) } };
            } else throw new InvalidOperationException(query);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data }, options: IntegrationProtocol.Json) };
        }
        private object ExactData() => new { source = Source(), manga = Manga(), chapter = Chapters.Single(), downloadStatus = Status(EnqueueCalls > 0) };
        private object Status(bool queued) => new { queue = queued || QueueState is not null
            ? new[] { new { state = QueueState ?? SuwayomiCodes.Queued, progress = 0.0, tries = 0, position = 0, chapter = Chapters.Single() } }
            : [] };
        public void Dispose() => client.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
