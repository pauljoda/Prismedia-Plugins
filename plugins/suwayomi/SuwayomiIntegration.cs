using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Suwayomi;

/// <summary>Browses installed Suwayomi sources and prepares one exact chapter without changing library or reading state.</summary>
internal sealed class SuwayomiIntegration(SuwayomiClient client, ConnectionContext connection) {
    private static readonly string[] Kinds = [MediaKinds.Comic];
    private static readonly Capability[] Capabilities = [
        new(IntegrationCapabilities.Discovery, [IntegrationOperations.Browse, IntegrationOperations.Search], Kinds),
        new(IntegrationCapabilities.AcquisitionSource,
            [IntegrationOperations.RequestSource, IntegrationOperations.ObserveSource, IntegrationOperations.Resolve], Kinds)
    ];

    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken cancellationToken) {
        if (request.Operation == IntegrationOperations.Probe) return await ProbeAsync(cancellationToken);
        await RequireSupportedServerAsync(cancellationToken);
        return request.Operation switch {
            IntegrationOperations.Browse => await DiscoverAsync(Read<DiscoveryInput>(request.Input), false, cancellationToken),
            IntegrationOperations.Search => await DiscoverAsync(Read<DiscoveryInput>(request.Input), true, cancellationToken),
            IntegrationOperations.RequestSource => await RequestSourceAsync(Read<RequestSourceInput>(request.Input), cancellationToken),
            IntegrationOperations.ObserveSource => await ObserveSourceAsync(Read<ObserveSourceInput>(request.Input), cancellationToken),
            IntegrationOperations.Resolve => await ResolveAsync(Read<ResolveOfferInput>(request.Input), cancellationToken),
            _ => throw new IntegrationFailure("This Suwayomi operation is unavailable.")
        };
    }

    internal async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken) {
        var about = await RequireSupportedServerAsync(cancellationToken);
        return new(null, about.Name, about.Version, Capabilities);
    }

    internal async Task<CatalogPage> DiscoverAsync(DiscoveryInput input, bool searching, CancellationToken cancellationToken) {
        if (input.EntityKind != MediaKinds.Comic || input.Limit is < 1 or > SuwayomiCodes.MaximumCatalogLimit
            || input.Query?.Length > 512) throw new IntegrationFailure("The Suwayomi catalog request is invalid.");
        if (input.Container is null) {
            if (searching || !string.IsNullOrWhiteSpace(input.Query)) throw new IntegrationFailure("Choose one Suwayomi source before searching.");
            return await BrowseSourcesAsync(input, cancellationToken);
        }
        var kind = LocatorKind(input.Container);
        return kind switch {
            SuwayomiCodes.SourceContainer => await BrowseMangasAsync(input, searching, Decode<SourceLocator>(input.Container), cancellationToken),
            SuwayomiCodes.MangaContainer when !searching && string.IsNullOrWhiteSpace(input.Query) =>
                await BrowseChaptersAsync(input, Decode<MangaLocator>(input.Container), cancellationToken),
            SuwayomiCodes.MangaContainer => throw new IntegrationFailure("Search within a Suwayomi source, then open a manga to browse its chapters."),
            _ => throw new IntegrationFailure("The Suwayomi catalog container is invalid.")
        };
    }

    internal async Task<SourceAcquisitionObservation> ObserveSourceAsync(ObserveSourceInput input, CancellationToken cancellationToken) {
        if (input.Selection is null || input.OfferId != SuwayomiCodes.CbzOffer)
            throw new IntegrationFailure("Select a Suwayomi CBZ chapter offer.");
        var exact = await ReadExactAsync(input.Selection, cancellationToken);
        return await ObserveExactAsync(input.Selection, exact, cancellationToken);
    }

    internal async Task<SourceAcquisitionObservation> RequestSourceAsync(RequestSourceInput input, CancellationToken cancellationToken) {
        if (input.OperationId == Guid.Empty || input.Selection is null || input.OfferId != SuwayomiCodes.CbzOffer)
            throw new IntegrationFailure("The exact Suwayomi chapter request is incomplete.");
        var exact = await ReadExactAsync(input.Selection, cancellationToken);
        var current = await ObserveExactAsync(input.Selection, exact, cancellationToken);
        if (current.State != SourceAcquisitionStates.NotObserved) return current;
        _ = await client.GraphQlAsync<EnqueueData>(SuwayomiQueries.Enqueue,
            new { id = exact.Chapter.Id, clientMutationId = input.OperationId.ToString("D") }, cancellationToken);
        return await ObserveSourceAsync(new(input.Selection, input.OfferId), cancellationToken);
    }

    internal async Task<ResolvedSourceOffer> ResolveAsync(ResolveOfferInput input, CancellationToken cancellationToken) {
        if (input.Selection is null || input.OfferId != SuwayomiCodes.CbzOffer)
            throw new IntegrationFailure("Select a ready Suwayomi CBZ chapter offer.");
        var observed = await ObserveSourceAsync(new(input.Selection, input.OfferId), cancellationToken);
        if (observed.State != SourceAcquisitionStates.Ready || observed.Offer.Access != AcquisitionAccess.Download)
            throw new IntegrationFailure("The selected Suwayomi chapter is not ready to download.");
        var locator = DecodeChapter(input.Selection);
        return new(input.Selection, input.OfferId, observed.Publication, observed.Offer,
            new(client.ChapterDownloadUrl(locator.ChapterId), client.DeliveryHeaders,
                $"suwayomi-{locator.MangaId.ToString(CultureInfo.InvariantCulture)}-{locator.ChapterId.ToString(CultureInfo.InvariantCulture)}.cbz"));
    }

    private async Task<CatalogPage> BrowseSourcesAsync(DiscoveryInput input, CancellationToken cancellationToken) {
        var offset = 0;
        if (input.Cursor is not null) {
            var cursor = Decode<SourceCursor>(input.Cursor);
            if (cursor.ConnectionId != connection.Id || cursor.Offset is < 0 or > SuwayomiCodes.MaximumCursorOffset)
                throw new IntegrationFailure("Start a new Suwayomi source browse after changing the connection.");
            offset = cursor.Offset;
        }
        var data = await client.GraphQlAsync<SourcesData>(SuwayomiQueries.Sources, new { first = input.Limit, offset }, cancellationToken);
        if (data.Sources.Nodes.Count > input.Limit || data.Sources.TotalCount < offset + data.Sources.Nodes.Count)
            throw new IntegrationFailure("Suwayomi returned an inconsistent source page.");
        foreach (var source in data.Sources.Nodes) RequireValidSource(source);
        var items = data.Sources.Nodes.Select(source => new CatalogItem(
            new(SourceItemId(source.Id), Encode(new SourceLocator(SuwayomiCodes.SourceContainer, connection.Id, source.Id, source.Name, source.Lang)), MediaKinds.Comic),
            true, new(SourceTitle(source), null, [], new Dictionary<string, string>(), Language: source.Lang), [])).ToArray();
        var nextOffset = offset + items.Length;
        return new("Suwayomi sources", items, nextOffset < data.Sources.TotalCount
            ? Encode(new SourceCursor(connection.Id, nextOffset)) : null, false);
    }

    private async Task<CatalogPage> BrowseMangasAsync(DiscoveryInput input, bool searching, SourceLocator locator, CancellationToken cancellationToken) {
        ValidateSourceLocator(locator);
        var source = (await client.GraphQlAsync<SourceData>(SuwayomiQueries.Source, new { id = locator.SourceId }, cancellationToken)).Source
            ?? throw new IntegrationFailure("The selected Suwayomi source is unavailable.");
        RequireSameSource(locator.SourceId, locator.SourceName, locator.SourceLanguage, source);
        var query = searching ? input.Query?.Trim() : null;
        if (searching && string.IsNullOrWhiteSpace(query)) throw new IntegrationFailure("Enter a manga title to search this Suwayomi source.");
        var mode = searching ? SuwayomiCodes.Search : SuwayomiCodes.Popular;
        var page = 1;
        var offset = 0;
        if (input.Cursor is not null) {
            var cursor = Decode<MangaCursor>(input.Cursor);
            if (cursor.ConnectionId != connection.Id || cursor.SourceId != locator.SourceId || cursor.SourceName != locator.SourceName
                || cursor.SourceLanguage != locator.SourceLanguage || cursor.Mode != mode || cursor.Query != query || cursor.Limit != input.Limit
                || cursor.Page is < 1 or > SuwayomiCodes.MaximumCursorOffset || cursor.Offset is < 0 or > SuwayomiCodes.MaximumCursorOffset)
                throw new IntegrationFailure("Start a new Suwayomi manga browse after changing its source, query, or page size.");
            page = cursor.Page;
            offset = cursor.Offset;
        }
        var data = await client.GraphQlAsync<SearchMangaData>(SuwayomiQueries.FetchSourceManga,
            new { source = locator.SourceId, type = mode, page, query }, cancellationToken);
        var all = data.FetchSourceManga.Mangas;
        if (offset > all.Count || all.Any(manga => manga.SourceId != locator.SourceId))
            throw new IntegrationFailure("Suwayomi returned an inconsistent manga page.");
        var mangas = all.Skip(offset).Take(input.Limit).ToArray();
        var items = mangas.Select(manga => MangaItem(source, manga)).ToArray();
        MangaCursor? next = null;
        if (offset + mangas.Length < all.Count)
            next = new(connection.Id, source.Id, source.Name, source.Lang, mode, query, page, offset + mangas.Length, input.Limit);
        else if (data.FetchSourceManga.HasNextPage)
            next = new(connection.Id, source.Id, source.Name, source.Lang, mode, query, page + 1, 0, input.Limit);
        return new(searching ? $"{source.Name} search" : source.Name, items, next is null ? null : Encode(next), true);
    }

    private async Task<CatalogPage> BrowseChaptersAsync(DiscoveryInput input, MangaLocator locator, CancellationToken cancellationToken) {
        ValidateMangaLocator(locator);
        var source = (await client.GraphQlAsync<SourceData>(SuwayomiQueries.Source, new { id = locator.SourceId }, cancellationToken)).Source
            ?? throw new IntegrationFailure("The selected Suwayomi source is unavailable.");
        RequireSameSource(locator.SourceId, locator.SourceName, locator.SourceLanguage, source);
        var manga = await ReadMangaAsync(locator.MangaId, cancellationToken);
        RequireSameManga(locator.SourceId, locator.MangaId, locator.MangaUrl, manga);
        var offset = 0;
        if (input.Cursor is not null) {
            var cursor = Decode<ChapterCursor>(input.Cursor);
            if (cursor.ConnectionId != connection.Id || cursor.SourceId != locator.SourceId || cursor.MangaId != locator.MangaId
                || cursor.Limit != input.Limit || cursor.Offset is < 0 or > SuwayomiCodes.MaximumCursorOffset)
                throw new IntegrationFailure("Start a new Suwayomi chapter browse after changing its manga or page size.");
            offset = cursor.Offset;
        }
        var data = await ReadChaptersAsync(manga.Id, input.Limit, offset, cancellationToken);
        if (offset == 0 && data.Chapters.TotalCount == 0 && !manga.InLibrary && manga.ChaptersLastFetchedAt is null) {
            _ = await client.GraphQlAsync<FetchMangaData>(SuwayomiQueries.RefreshNonLibraryManga, new { id = manga.Id }, cancellationToken);
            manga = await ReadMangaAsync(locator.MangaId, cancellationToken);
            RequireSameManga(locator.SourceId, locator.MangaId, locator.MangaUrl, manga);
            data = await ReadChaptersAsync(manga.Id, input.Limit, offset, cancellationToken);
        }
        if (data.Chapters.Nodes.Count > input.Limit || data.Chapters.TotalCount < offset + data.Chapters.Nodes.Count
            || data.Chapters.Nodes.Any(chapter => chapter.MangaId != manga.Id))
            throw new IntegrationFailure("Suwayomi returned an inconsistent chapter page.");
        var items = await Task.WhenAll(data.Chapters.Nodes.Select(async chapter =>
            ChapterItem(source, manga, chapter, chapter.IsDownloaded && await client.IsChapterReadyAsync(chapter.Id, cancellationToken))));
        var nextOffset = offset + items.Length;
        return new(manga.Title, items, nextOffset < data.Chapters.TotalCount
            ? Encode(new ChapterCursor(connection.Id, source.Id, manga.Id, nextOffset, input.Limit)) : null, false);
    }

    private async Task<ExactData> ReadExactAsync(SourceSelection selection, CancellationToken cancellationToken) {
        var locator = DecodeChapter(selection);
        var data = await client.GraphQlAsync<ExactData>(SuwayomiQueries.Exact,
            new { sourceId = locator.SourceId, mangaId = locator.MangaId, chapterId = locator.ChapterId }, cancellationToken);
        if (data.Source is null) throw new IntegrationFailure("The selected Suwayomi source is unavailable.");
        RequireSameSource(locator.SourceId, locator.SourceName, locator.SourceLanguage, data.Source);
        RequireSameManga(locator.SourceId, locator.MangaId, locator.MangaUrl, data.Manga);
        RequireValidChapter(data.Chapter);
        if (data.Chapter.Id != locator.ChapterId || data.Chapter.MangaId != locator.MangaId || data.Chapter.Url != locator.ChapterUrl
            || data.Chapter.SourceOrder != locator.SourceOrder)
            throw new IntegrationFailure("The selected Suwayomi chapter identity changed. Browse the source again.");
        return data;
    }

    private async Task<SourceAcquisitionObservation> ObserveExactAsync(SourceSelection selection, ExactData exact, CancellationToken cancellationToken) {
        var ready = exact.Chapter.IsDownloaded && await client.IsChapterReadyAsync(exact.Chapter.Id, cancellationToken);
        var queued = exact.DownloadStatus.Queue.FirstOrDefault(item => item.Chapter.Id == exact.Chapter.Id && item.Chapter.MangaId == exact.Manga.Id);
        var state = ready ? SourceAcquisitionStates.Ready : queued is null
            ? SourceAcquisitionStates.NotObserved
            : queued.State switch {
                SuwayomiCodes.Queued => SourceAcquisitionStates.Queued,
                SuwayomiCodes.Downloading or SuwayomiCodes.Finished => SourceAcquisitionStates.Downloading,
                SuwayomiCodes.Error => SourceAcquisitionStates.Failed,
                _ => throw new IntegrationFailure("Suwayomi returned an unknown chapter download state.")
            };
        double? progress = queued is null ? null : Math.Clamp((double)queued.Progress, 0, 1);
        var publication = Publication(exact.Source, exact.Manga, exact.Chapter);
        var offer = Offer(state == SourceAcquisitionStates.Ready);
        return new(selection, SuwayomiCodes.CbzOffer, publication, offer, state, progress,
            state is SourceAcquisitionStates.Queued or SourceAcquisitionStates.Downloading ? DateTimeOffset.UtcNow.AddSeconds(2) : null,
            state == SourceAcquisitionStates.Failed ? "Suwayomi reported that this chapter download failed." : null);
    }

    private async Task<AboutServer> RequireSupportedServerAsync(CancellationToken cancellationToken) {
        var about = (await client.GraphQlAsync<AboutData>(SuwayomiQueries.About, null, cancellationToken)).AboutServer;
        if (about.Version != SuwayomiCodes.ReportedVersion)
            throw new IntegrationFailure($"This plugin supports Suwayomi Server {SuwayomiCodes.SupportedVersion}; the configured server reported {about.Version}.");
        return about;
    }

    private async Task<SuwayomiManga> ReadMangaAsync(int id, CancellationToken cancellationToken) {
        var page = (await client.GraphQlAsync<MangasData>(SuwayomiQueries.Manga, new { id }, cancellationToken)).Mangas;
        if (page.TotalCount != 1 || page.Nodes is not { Count: 1 } || page.Nodes[0].Id != id)
            throw new IntegrationFailure("The selected Suwayomi manga is unavailable.");
        return page.Nodes[0];
    }
    private Task<ChaptersData> ReadChaptersAsync(int mangaId, int first, int offset, CancellationToken cancellationToken) =>
        client.GraphQlAsync<ChaptersData>(SuwayomiQueries.Chapters, new { mangaId, first, offset }, cancellationToken);

    private CatalogItem MangaItem(SuwayomiSource source, SuwayomiManga manga) {
        RequireValidManga(manga);
        var locator = new MangaLocator(SuwayomiCodes.MangaContainer, connection.Id, source.Id, source.Name, source.Lang,
            manga.Id, manga.Url, manga.Title);
        return new(new(MangaItemId(source.Id, manga.Url), Encode(locator), MediaKinds.Comic), true,
            new(manga.Title, manga.Description, Authors(manga), new Dictionary<string, string>(), Language: source.Lang), []);
    }
    private CatalogItem ChapterItem(SuwayomiSource source, SuwayomiManga manga, SuwayomiChapter chapter, bool ready) {
        RequireValidChapter(chapter);
        var locator = new ChapterLocator(SuwayomiCodes.ChapterSelection, connection.Id, source.Id, source.Name, source.Lang,
            manga.Id, manga.Url, manga.Title, chapter.Id, chapter.Url, chapter.SourceOrder, chapter.Name, chapter.ChapterNumber, chapter.Scanlator);
        return new(new(ChapterItemId(source.Id, manga.Url, chapter.Url), Encode(locator), MediaKinds.Comic), false,
            Publication(source, manga, chapter), [Offer(ready)]);
    }
    private static CatalogPublication Publication(SuwayomiSource source, SuwayomiManga manga, SuwayomiChapter chapter) =>
        new(string.IsNullOrWhiteSpace(chapter.Name) ? manga.Title : $"{manga.Title} — {chapter.Name}", manga.Description,
            Authors(manga), new Dictionary<string, string>(), Language: source.Lang, Publisher: chapter.Scanlator,
            EditionLabel: source.Name,
            IssueLabel: chapter.ChapterNumber >= 0 ? chapter.ChapterNumber.ToString("0.###", CultureInfo.InvariantCulture) : chapter.Name);
    private static CatalogOffer Offer(bool ready) => new(SuwayomiCodes.CbzOffer,
        ready ? "Download CBZ" : "Request chapter CBZ", ready ? AcquisitionAccess.Download : AcquisitionAccess.Request,
        SuwayomiCodes.CbzMediaType);
    private static string[] Authors(SuwayomiManga manga) => new[] { manga.Author, manga.Artist }
        .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Cast<string>().ToArray();

    private ChapterLocator DecodeChapter(SourceSelection selection) {
        if (selection.EntityKind != MediaKinds.Comic) throw new IntegrationFailure("Select a Suwayomi comic chapter.");
        var locator = Decode<ChapterLocator>(selection.Locator);
        if (locator.Kind != SuwayomiCodes.ChapterSelection || locator.ConnectionId != connection.Id || !ValidSourceId(locator.SourceId)
            || locator.MangaId <= 0 || locator.ChapterId <= 0 || locator.SourceOrder < 0
            || selection.ItemId != ChapterItemId(locator.SourceId, locator.MangaUrl, locator.ChapterUrl))
            throw new IntegrationFailure("The Suwayomi chapter selection belongs to another source or connection.");
        return locator;
    }
    private void ValidateSourceLocator(SourceLocator locator) {
        if (locator.Kind != SuwayomiCodes.SourceContainer || locator.ConnectionId != connection.Id || !ValidSourceId(locator.SourceId)
            || string.IsNullOrWhiteSpace(locator.SourceName) || string.IsNullOrWhiteSpace(locator.SourceLanguage))
            throw new IntegrationFailure("The Suwayomi source selection belongs to another connection.");
    }
    private void ValidateMangaLocator(MangaLocator locator) {
        if (locator.Kind != SuwayomiCodes.MangaContainer || locator.ConnectionId != connection.Id || !ValidSourceId(locator.SourceId)
            || locator.MangaId <= 0 || string.IsNullOrWhiteSpace(locator.MangaUrl))
            throw new IntegrationFailure("The Suwayomi manga selection belongs to another source or connection.");
    }
    private static void RequireSameSource(string id, string name, string language, SuwayomiSource source) {
        if (source.Id != id || source.Name != name || source.Lang != language)
            throw new IntegrationFailure("The selected Suwayomi source identity changed. Browse sources again.");
        RequireValidSource(source);
    }
    private static void RequireSameManga(string sourceId, int id, string url, SuwayomiManga manga) {
        RequireValidManga(manga);
        if (manga.Id != id || manga.SourceId != sourceId || manga.Url != url)
            throw new IntegrationFailure("The selected Suwayomi manga identity changed. Browse the source again.");
    }
    private static void RequireValidManga(SuwayomiManga manga) {
        if (manga.Id <= 0 || !ValidSourceId(manga.SourceId) || manga.Title.Length is < 1 or > 512 || manga.Url.Length is < 1 or > 4096
            || manga.Description?.Length > 8192 || manga.Author?.Length > 512 || manga.Artist?.Length > 512)
            throw new IntegrationFailure("Suwayomi returned invalid manga evidence.");
    }
    private static void RequireValidChapter(SuwayomiChapter chapter) {
        if (chapter.Id <= 0 || chapter.MangaId <= 0 || chapter.SourceOrder < 0 || chapter.Name.Length > 512
            || chapter.Url.Length is < 1 or > 4096 || chapter.RealUrl?.Length > 4096 || chapter.Scanlator?.Length > 512
            || !float.IsFinite(chapter.ChapterNumber) || chapter.PageCount < -1)
            throw new IntegrationFailure("Suwayomi returned invalid chapter evidence.");
    }
    private static void RequireValidSource(SuwayomiSource source) {
        if (!ValidSourceId(source.Id) || source.Name.Length is < 1 or > 512 || source.Lang.Length is < 1 or > 64)
            throw new IntegrationFailure("Suwayomi returned an invalid source identity.");
    }
    private static string SourceTitle(SuwayomiSource source) => $"{source.Name} [{source.Lang}]";
    private static bool ValidSourceId(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && value == parsed.ToString(CultureInfo.InvariantCulture);
    private static string SourceItemId(string sourceId) => $"source:{sourceId}";
    private static string MangaItemId(string sourceId, string mangaUrl) =>
        StableItemId(SuwayomiCodes.MangaContainer, sourceId, mangaUrl);
    private static string ChapterItemId(string sourceId, string mangaUrl, string chapterUrl) =>
        StableItemId(SuwayomiCodes.ChapterSelection, sourceId, mangaUrl, chapterUrl);
    private static string StableItemId(string kind, params string[] identity) {
        var canonicalTuple = new[] { kind }.Concat(identity).ToArray();
        var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonicalTuple, IntegrationProtocol.Json));
        return $"{kind}:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
    private static T Read<T>(JsonElement input) => input.Deserialize<T>(IntegrationProtocol.Json) ?? throw new IntegrationFailure("The Suwayomi request is incomplete.");
    private static string Encode<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, IntegrationProtocol.Json));
    private static string LocatorKind(string value) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16384) throw new IntegrationFailure("The Suwayomi selection or cursor is invalid.");
        try {
            using var document = JsonDocument.Parse(Convert.FromBase64String(value));
            return document.RootElement.GetProperty("kind").GetString() ?? throw new IntegrationFailure("The Suwayomi selection or cursor is invalid.");
        } catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) {
            throw new IntegrationFailure("The Suwayomi selection or cursor is invalid.");
        }
    }
    private static T Decode<T>(string value) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16384) throw new IntegrationFailure("The Suwayomi selection or cursor is invalid.");
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(value), IntegrationProtocol.Json)
            ?? throw new IntegrationFailure("The Suwayomi selection or cursor is invalid."); }
        catch (Exception error) when (error is JsonException or FormatException) { throw new IntegrationFailure("The Suwayomi selection or cursor is invalid."); }
    }
}
