using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

var response = await PluginHost.RunAsync(args, MangaDexPlugin.IdentifyAsync);
Console.Write(JsonSerializer.Serialize(response, PluginHost.JsonOptions));

internal static partial class MangaDexPlugin {
    internal static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly string[] SfwContentRatings = ["safe", "suggestive"];
    private static readonly string[] AllContentRatings = ["safe", "suggestive", "erotica", "pornographic"];
    private const string PluginId = "mangadex";
    private const string PrimaryIdentityNamespace = "mangadex";
    private const string VolumeIdentityNamespace = "mangadexvolume";
    private const string ChapterIdentityNamespace = "mangadexchapter";
    private const string ChapterNumberLocator = "chapternumber";
    private const string VolumeLocator = "volume";
    private const string LanguageField = "language";
    private static readonly string RateLimitPath = Path.Combine(Path.GetTempPath(), "prismedia-mangadex.ratelimit");
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(250);
    private const string Api = "https://api.mangadex.org";
    private const string Web = "https://mangadex.org";
    private const string Uploads = "https://uploads.mangadex.org";
    private const string DefaultLanguage = "en";

    private static class SearchFields {
        public const string Title = "title";
        public const string SeriesTitle = "seriesTitle";
        public const string Creator = "creator";
        public const string Year = "year";
    }

    static MangaDexPlugin() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Prismedia-MangaDex-Plugin/1.1");

    public static async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        if (!request.Entity.Kind.Equals("book", StringComparison.OrdinalIgnoreCase) &&
            !request.Entity.Kind.Equals("book-volume", StringComparison.OrdinalIgnoreCase) &&
            !request.Entity.Kind.Equals("book-chapter", StringComparison.OrdinalIgnoreCase)) {
            return IdentifyPluginResult.None();
        }

        var query = SearchField(request, SearchFields.Title, SearchFields.SeriesTitle) ?? request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        var hasVolumeIdentity = TryGetVolumeIdentity(request, out var volumeMangaId, out var selectedVolume);
        var mangaId = (hasVolumeIdentity ? volumeMangaId : null)
            ?? ExternalId(request, PrimaryIdentityNamespace)
            ?? IdFromUrl(request.Query.Url)
            ?? FirstUrlId(request.Hints.Urls)
            ?? AncestorExternalId(request, PrimaryIdentityNamespace);

        var chapterId = ExternalId(request, ChapterIdentityNamespace)
            ?? ChapterIdFromUrl(request.Query.Url)
            ?? FirstChapterUrlId(request.Hints.Urls);

        if (mangaId is not null && !IsExplicitSearch(request)) {
            return IdentifyPluginResult.ForProposal(await ProposalAsync(
                mangaId,
                request,
                "external-id",
                query,
                selectedVolume: hasVolumeIdentity ? selectedVolume : null));
        }

        if (chapterId is not null && !IsExplicitSearch(request)) {
            var chapter = await GetChapterAsync(chapterId);
            var id = chapter?.Relationships?.FirstOrDefault(rel => rel.Type == "manga")?.Id;
            if (id is not null) {
                return IdentifyPluginResult.ForProposal(await ProposalAsync(id, request, "chapter-url", query, chapterId));
            }
        }

        if (string.IsNullOrWhiteSpace(query)) {
            return IdentifyPluginResult.None();
        }

        var results = await SearchAsync(
            query,
            request.IncludeNsfw,
            SearchYear(request),
            SearchField(request, SearchFields.Creator),
            SearchLimit(request));
        return IdentifyPluginResult.ForCandidates(results.Select(manga => new EntitySearchCandidate(
            new Dictionary<string, string> { [PrimaryIdentityNamespace] = manga.Id },
            Title(manga, query) ?? manga.Id,
            manga.Attributes?.Year,
            DescriptionText(manga),
            CoverUrl(manga),
            null)).ToArray());
    }

    private static async Task<EntityMetadataProposal> ProposalAsync(
        string id,
        IdentifyPluginRequest request,
        string reason,
        string? preferredTitle,
        string? selectedChapterId = null,
        string? selectedVolume = null) {
        var manga = await GetMangaAsync(id) ?? throw new InvalidOperationException("MangaDex title not found.");
        if (!request.IncludeNsfw && IsAdult(manga)) {
            throw new InvalidOperationException("MangaDex title is adult-rated and NSFW mode is not enabled.");
        }

        var covers = await GetCoversAsync(manga.Id);
        var chapters = await GetChaptersAsync(manga.Id, request.IncludeNsfw, PreferredLanguage(request));
        var aggregate = await GetAggregateAsync(manga.Id, PreferredLanguage(request));
        var children = BuildChildren(manga, chapters, aggregate, covers, selectedChapterId, PreferredLanguage(request)).ToArray();
        var images = BookImages(manga, covers).ToArray();
        var attrs = manga.Attributes;
        var external = new Dictionary<string, string> { [PrimaryIdentityNamespace] = manga.Id };
        var urls = new[] { $"{Web}/title/{manga.Id}" };
        var dates = new Dictionary<string, string>();
        if (attrs?.Year is int year) dates["published"] = year.ToString();

        var proposal = new EntityMetadataProposal(
            $"mangadex:{manga.Id}",
            PluginId,
            "book",
            0.9m,
            reason,
            new EntityMetadataPatch(
                Title(manga, preferredTitle) ?? manga.Id,
                DescriptionText(manga),
                external,
                urls,
                Tags(manga),
                null,
                Credits(manga),
                dates,
                new Dictionary<string, int>(),
                new Dictionary<string, int>(),
                null) {
                Flags = AdultFlags(manga)
            },
            images,
            children,
            [],
            request.Entity.Kind.Equals("book", StringComparison.OrdinalIgnoreCase) ? request.Entity.Id : null,
            []);
        return ScopedProposalForRequest(proposal, request, selectedVolume);
    }

    private static EntityMetadataProposal ScopedProposalForRequest(
        EntityMetadataProposal bookProposal,
        IdentifyPluginRequest request,
        string? selectedVolume = null) {
        if (request.Entity.Kind.Equals("book", StringComparison.OrdinalIgnoreCase)) {
            return bookProposal;
        }

        if (request.Entity.Kind.Equals("book-volume", StringComparison.OrdinalIgnoreCase)) {
            var volume = bookProposal.Children.FirstOrDefault(child => MatchesVolumeRequest(child, request, selectedVolume));
            return volume is null
                ? ScopedFallback(bookProposal, request, "book-volume")
                : volume with { TargetEntityId = request.Entity.Id };
        }

        if (request.Entity.Kind.Equals("book-chapter", StringComparison.OrdinalIgnoreCase)) {
            // Chapters are matched inside their parent volume whenever the ancestors identify
            // one; chapter numbers restart per volume on many titles, so a global search would
            // bind "the volume's first chapter" to the series-wide chapter 1.
            var volumeScope = VolumeNodeForChapterRequest(bookProposal, request);
            var candidates = (volumeScope?.Children ?? StructuralDescendants(bookProposal).ToArray())
                .Where(child => child.TargetKind.Equals("book-chapter", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var chapter = candidates.FirstOrDefault(child => MatchesChapterRequest(child, request))
                ?? (volumeScope is null ? null : RelativeChapterInVolume(candidates, request));
            return chapter is null
                ? ScopedFallback(bookProposal, request, "book-chapter")
                : chapter with { TargetEntityId = request.Entity.Id };
        }

        return bookProposal;
    }

    private static EntityMetadataProposal? VolumeNodeForChapterRequest(EntityMetadataProposal bookProposal, IdentifyPluginRequest request) {
        var ancestor = request.StructuralContext?.Ancestors
            .FirstOrDefault(snapshot => snapshot.Kind.Equals("book-volume", StringComparison.OrdinalIgnoreCase));
        if (ancestor is null) return null;
        var volumeNumber = NormalizeNumber(
            TryGetValue(ancestor.ExternalIds, VolumeLocator, out var ancestorVolume) ? ancestorVolume : null) ??
            NumberFromTitle(ancestor.Title);
        if (volumeNumber is null) return null;
        return bookProposal.Children.FirstOrDefault(child =>
            child.TargetKind.Equals("book-volume", StringComparison.OrdinalIgnoreCase) &&
            TryGetValue(child.Patch.ExternalIds, VolumeLocator, out var volume) &&
            NormalizeNumber(volume) == volumeNumber);
    }

    // Last resort inside a known volume: line the volume's ordered chapters up against the
    // local zero-based sort position. Only valid within a volume scope — applied globally it
    // would bind every volume's first chapter to the same upstream chapter.
    private static EntityMetadataProposal? RelativeChapterInVolume(IReadOnlyList<EntityMetadataProposal> chapters, IdentifyPluginRequest request) {
        var positions = request.StructuralContext?.Positions ?? new Dictionary<string, int>();
        var sort = PositionValue(positions, "sort", "sortOrder");
        return sort is int index && index >= 0 && index < chapters.Count ? chapters[index] : null;
    }

    // No upstream node matched this volume/chapter, so the proposal only identifies the
    // container title: it keeps the local entity's own name and the manga id, but none of the
    // book's dates, urls, or text — those describe the title, not this child — and a low
    // confidence so review surfaces it and auto-identify never applies it.
    private static EntityMetadataProposal ScopedFallback(EntityMetadataProposal bookProposal, IdentifyPluginRequest request, string targetKind) =>
        bookProposal with {
            ProposalId = $"{bookProposal.ProposalId}:{targetKind}:{request.Entity.Id}",
            TargetKind = targetKind,
            TargetEntityId = request.Entity.Id,
            Confidence = 0.3m,
            MatchReason = "scoped-fallback",
            Patch = bookProposal.Patch with {
                Title = request.Entity.Title,
                Description = null,
                ExternalIds = MangaOnlyExternalIds(bookProposal.Patch.ExternalIds),
                Urls = [],
                Tags = [],
                Credits = [],
                Dates = new Dictionary<string, string>(),
                Stats = new Dictionary<string, int>(),
                Positions = request.StructuralContext?.Positions ?? new Dictionary<string, int>()
            },
            Images = [],
            Children = [],
            Candidates = [],
            Relationships = []
        };

    private static IReadOnlyDictionary<string, string> MangaOnlyExternalIds(IReadOnlyDictionary<string, string> externalIds) =>
        TryGetValue(externalIds, PrimaryIdentityNamespace, out var mangaId)
            ? new Dictionary<string, string> { [PrimaryIdentityNamespace] = mangaId }
            : new Dictionary<string, string>();

    private static IEnumerable<EntityMetadataProposal> StructuralDescendants(EntityMetadataProposal proposal) {
        foreach (var child in proposal.Children) {
            yield return child;
            foreach (var descendant in StructuralDescendants(child)) {
                yield return descendant;
            }
        }
    }

    private static bool MatchesVolumeRequest(
        EntityMetadataProposal volume,
        IdentifyPluginRequest request,
        string? selectedVolume = null) {
        if (!volume.TargetKind.Equals("book-volume", StringComparison.OrdinalIgnoreCase)) return false;

        if (selectedVolume is not null &&
            TryGetValue(volume.Patch.ExternalIds, VolumeLocator, out var exactVolume)) {
            return exactVolume.Equals(selectedVolume, StringComparison.Ordinal);
        }

        var requestedVolume = SearchField(request, "volumeNumber") ?? ExternalId(request, VolumeLocator);
        var requestedVolumeNumber = NormalizeNumber(requestedVolume) ?? NumberFromTitle(request.Entity.Title);
        if (requestedVolumeNumber is not null &&
            TryGetValue(volume.Patch.ExternalIds, VolumeLocator, out var volumeId) &&
            NormalizeNumber(volumeId) == requestedVolumeNumber) {
            return true;
        }

        var positions = request.StructuralContext?.Positions ?? new Dictionary<string, int>();
        var requestPosition = PositionValue(positions, "volume", "volumeNumber");
        var proposalPosition = PositionValue(volume.Patch.Positions, "volume", "volumeNumber");
        if (requestPosition is not null && proposalPosition == requestPosition) return true;

        var requestSort = PositionValue(positions, "sort", "sortOrder");
        var proposalVolumeNumber = PositionValue(volume.Patch.Positions, "volume", "volumeNumber");
        if (requestSort is not null && proposalVolumeNumber == requestSort + 1) return true;

        var proposalTitleNumber = NumberFromTitle(volume.Patch.Title);
        var requestTitleNumber = NumberFromTitle(request.Entity.Title);
        return !string.IsNullOrWhiteSpace(request.Entity.Title) &&
            (volume.Patch.Title?.Equals(request.Entity.Title, StringComparison.OrdinalIgnoreCase) == true ||
             (proposalTitleNumber is not null && proposalTitleNumber == requestTitleNumber));
    }

    private static bool MatchesChapterRequest(EntityMetadataProposal chapter, IdentifyPluginRequest request) {
        var requestedChapterId = ExternalId(request, ChapterIdentityNamespace);
        if (!string.IsNullOrWhiteSpace(requestedChapterId) &&
            TryGetValue(chapter.Patch.ExternalIds, ChapterIdentityNamespace, out var chapterId) &&
            chapterId.Equals(requestedChapterId, StringComparison.Ordinal)) {
            return true;
        }

        var requestedChapterNumber = SearchField(request, "chapterNumber") ?? ExternalId(request, ChapterNumberLocator);
        if (!string.IsNullOrWhiteSpace(requestedChapterNumber) &&
            TryGetValue(chapter.Patch.ExternalIds, ChapterNumberLocator, out var proposalChapterNumber) &&
            NormalizeChapterNumber(proposalChapterNumber) == NormalizeChapterNumber(requestedChapterNumber)) {
            return true;
        }

        // Local chapter files usually carry their feed-global chapter number in the name
        // ("... Ch.39"); an explicit number in the title is a stronger signal than any
        // positional alignment.
        var titleChapterNumber = ChapterNumberFromTitle(request.Entity.Title);
        if (titleChapterNumber is not null &&
            TryGetValue(chapter.Patch.ExternalIds, ChapterNumberLocator, out var candidateNumber) &&
            NormalizeChapterNumber(candidateNumber) == titleChapterNumber) {
            return true;
        }

        var positions = request.StructuralContext?.Positions ?? new Dictionary<string, int>();
        var requestChapterPosition = PositionValue(positions, "chapter", "chapterNumber");
        var proposalChapterPosition = ProposalChapterNumber(chapter);
        if (requestChapterPosition is not null) return proposalChapterPosition == requestChapterPosition;

        return !string.IsNullOrWhiteSpace(request.Entity.Title) &&
            chapter.Patch.Title?.Equals(request.Entity.Title, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static int? PositionValue(IReadOnlyDictionary<string, int> positions, params string[] keys) {
        foreach (var key in keys) {
            if (positions.TryGetValue(key, out var value)) return value;
        }

        return null;
    }

    private static int? ProposalChapterNumber(EntityMetadataProposal chapter) {
        if (TryGetValue(chapter.Patch.ExternalIds, ChapterNumberLocator, out var value)) {
            return PositionNumber(value);
        }

        return PositionValue(chapter.Patch.Positions, "chapter", "chapterNumber");
    }

    private static IReadOnlyList<EntityMetadataProposal> BuildChildren(
        MangaResource manga,
        IReadOnlyList<ChapterResource> chapters,
        AggregateEnvelope? aggregate,
        IReadOnlyList<CoverResource> covers,
        string? selectedChapterId,
        string preferredLanguage) {
        var uniqueChapters = UniqueChapters(chapters);
        var volumeByChapter = VolumeByChapter(aggregate)
            .Concat(CoverVolumeByChapter(uniqueChapters, covers))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        var volumeNumbers = uniqueChapters
            .Select(chapter => EffectiveVolume(chapter, volumeByChapter))
            .Concat(covers.Select(cover => NormalizeVolumeValue(cover.Attributes?.Volume)))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(VolumeSortKey)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var children = new List<EntityMetadataProposal>();
        foreach (var volume in volumeNumbers) {
            var volumeChapters = uniqueChapters
                .Where(chapter => EffectiveVolume(chapter, volumeByChapter)?.Equals(volume, StringComparison.OrdinalIgnoreCase) == true)
                .OrderBy(chapter => ChapterSortKey(chapter.Attributes?.Chapter))
                .ToArray();
            children.Add(VolumeProposal(manga, volume, covers, volumeChapters, selectedChapterId, preferredLanguage));
        }

        foreach (var chapter in uniqueChapters.Where(chapter => EffectiveVolume(chapter, volumeByChapter) is null).OrderBy(chapter => ChapterSortKey(chapter.Attributes?.Chapter))) {
            children.Add(ChapterProposal(manga, chapter, selectedChapterId, [], preferredLanguage));
        }

        return children;
    }

    private static EntityMetadataProposal VolumeProposal(
        MangaResource manga,
        string volume,
        IReadOnlyList<CoverResource> covers,
        IReadOnlyList<ChapterResource> chapters,
        string? selectedChapterId,
        string preferredLanguage) {
        var coverImages = VolumeImages(manga, covers, volume).ToArray();
        var volumePosition = PositionNumber(volume);
        var positions = new Dictionary<string, int>();
        if (volumePosition is int position) {
            positions["volumeNumber"] = position;
            positions["sortOrder"] = position;
        }

        var stats = VolumeStats(chapters);
        var dates = VolumeDates(chapters);
        return new EntityMetadataProposal(
            $"mangadex:{manga.Id}:volume:{volume}",
            PluginId,
            "book-volume",
            0.8m,
            "volume-map",
            new EntityMetadataPatch(
                $"Volume {volume}",
                VolumeDescription(chapters),
                new Dictionary<string, string> {
                    [VolumeIdentityNamespace] = FormatVolumeIdentity(manga.Id, volume),
                    [PrimaryIdentityNamespace] = manga.Id,
                    [VolumeLocator] = volume
                },
                [$"{Web}/title/{manga.Id}"],
                [],
                null,
                [],
                dates,
                stats,
                positions,
                null) {
                Flags = AdultFlags(manga)
            },
            coverImages,
            chapters.Select(chapter => ChapterProposal(manga, chapter, selectedChapterId, ChapterCoverImages(coverImages), preferredLanguage)).ToArray(),
            []);
    }

    private static EntityMetadataProposal ChapterProposal(
        MangaResource manga,
        ChapterResource chapter,
        string? selectedChapterId,
        IReadOnlyList<ImageCandidate> images,
        string preferredLanguage) {
        var chapterText = chapter.Attributes?.Chapter;
        var sortPosition = ZeroBasedSortPosition(chapterText);
        // The chapter list can come from a fallback translation when the preferred language
        // has no hosted chapters; keep the structural data but do not put another language's
        // chapter title onto the user's library entries.
        var matchesPreferredLanguage = string.IsNullOrWhiteSpace(chapter.Attributes?.TranslatedLanguage) ||
            chapter.Attributes!.TranslatedLanguage!.Equals(preferredLanguage, StringComparison.OrdinalIgnoreCase);
        var title = string.IsNullOrWhiteSpace(chapter.Attributes?.Title) || !matchesPreferredLanguage
            ? $"Chapter {chapterText ?? chapter.Id}"
            : $"Chapter {chapterText}: {chapter.Attributes!.Title}";
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(chapter.Attributes?.PublishAt)) {
            dates["published"] = chapter.Attributes!.PublishAt![..Math.Min(10, chapter.Attributes.PublishAt.Length)];
        }

        var positions = new Dictionary<string, int>();
        if (sortPosition is int position) {
            positions["sortOrder"] = position;
        }

        var stats = new Dictionary<string, int>();
        if (chapter.Attributes?.Pages is int pages && pages > 0) {
            stats["pageCount"] = pages;
        }

        var external = new Dictionary<string, string> {
            [PrimaryIdentityNamespace] = manga.Id,
            [ChapterIdentityNamespace] = chapter.Id
        };
        if (!string.IsNullOrWhiteSpace(chapterText)) external[ChapterNumberLocator] = chapterText!;
        if (NormalizeVolumeValue(chapter.Attributes?.Volume) is string chapterVolume) external[VolumeLocator] = chapterVolume;

        return new EntityMetadataProposal(
            $"mangadex:{manga.Id}:chapter:{chapter.Id}",
            PluginId,
            "book-chapter",
            selectedChapterId == chapter.Id ? 0.9m : 0.7m,
            "chapter-feed",
            new EntityMetadataPatch(
                title,
                ChapterDescription(chapter),
                external,
                [$"{Web}/chapter/{chapter.Id}"],
                [],
                ScanlationGroup(chapter),
                [],
                dates,
                stats,
                positions,
                null) {
                Flags = AdultFlags(manga)
            },
            images,
            [],
            []);
    }

    private static async Task<IReadOnlyList<MangaResource>> SearchAsync(string title, bool includeNsfw, int? year, string? creator, int limit) {
        var yearQuery = year is null ? string.Empty : $"&year={year.Value}";
        var url = $"{Api}/manga?title={Uri.EscapeDataString(title)}&limit={limit}&includes[]=cover_art&includes[]=author&includes[]=artist&order[relevance]=desc{yearQuery}{ContentRatingQuery(includeNsfw)}";
        var results = (await GetJsonAsync<ListEnvelope<MangaResource>>(url))?.Data ?? [];
        return string.IsNullOrWhiteSpace(creator)
            ? results
            : results.Where(manga => RelationshipNames(manga, "author", "artist")
                .Any(name => name.Contains(creator, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
    }

    internal static int SearchLimit(IdentifyPluginRequest request) => Math.Clamp(request.Query.Limit ?? 10, 1, 25);

    private static async Task<MangaResource?> GetMangaAsync(string id) =>
        (await GetJsonAsync<SingleEnvelope<MangaResource>>($"{Api}/manga/{id}?includes[]=cover_art&includes[]=author&includes[]=artist"))?.Data;

    private static async Task<ChapterResource?> GetChapterAsync(string id) =>
        (await GetJsonAsync<SingleEnvelope<ChapterResource>>($"{Api}/chapter/{id}?includes[]=manga&includes[]=scanlation_group"))?.Data;

    private static async Task<IReadOnlyList<CoverResource>> GetCoversAsync(string mangaId) {
        var output = new List<CoverResource>();
        var offset = 0;
        while (true) {
            var url = $"{Api}/cover?manga[]={mangaId}&limit=100&offset={offset}&order[volume]=asc";
            var page = await GetJsonAsync<ListEnvelope<CoverResource>>(url);
            var rows = page?.Data ?? [];
            output.AddRange(rows);
            if (rows.Length == 0 || output.Count >= (page?.Total ?? output.Count)) break;
            offset += rows.Length;
        }

        return output;
    }

    private static async Task<IReadOnlyList<ChapterResource>> GetChaptersAsync(string mangaId, bool includeNsfw, string language) {
        foreach (var candidateLanguage in new[] { language, DefaultLanguage, string.Empty }.Distinct(StringComparer.OrdinalIgnoreCase)) {
            var chapters = await GetChaptersPageSetAsync(mangaId, includeNsfw, candidateLanguage);
            if (chapters.Count > 0) return chapters;
        }

        return [];
    }

    private static async Task<IReadOnlyList<ChapterResource>> GetChaptersPageSetAsync(string mangaId, bool includeNsfw, string language) {
        var output = new List<ChapterResource>();
        var fetched = 0;
        var offset = 0;
        while (true) {
            var languageQuery = string.IsNullOrWhiteSpace(language) ? "" : $"&translatedLanguage[]={Uri.EscapeDataString(language)}";
            var url = $"{Api}/manga/{mangaId}/feed?limit=100&offset={offset}&order[volume]=asc&order[chapter]=asc&includes[]=scanlation_group{languageQuery}{ContentRatingQuery(includeNsfw)}";
            var page = await GetJsonAsync<ListEnvelope<ChapterResource>>(url);
            var rows = page?.Data ?? [];
            fetched += rows.Length;
            output.AddRange(rows.Where(IsHostedChapter));
            if (rows.Length == 0 || fetched >= (page?.Total ?? fetched)) break;
            offset += rows.Length;
        }

        return output;
    }

    // Licensed titles keep placeholder chapters in the feed (external-reader stubs with no
    // hosted pages) and removed chapters stay flagged unavailable. Both carry real chapter
    // numbers, so letting them through binds local files to chapters that have no content —
    // for fully licensed titles the stubs are the ONLY feed entries and would rename real
    // chapters on every identify.
    private static bool IsHostedChapter(ChapterResource chapter) =>
        chapter.Attributes?.IsUnavailable != true &&
        (string.IsNullOrWhiteSpace(chapter.Attributes?.ExternalUrl) || (chapter.Attributes?.Pages ?? 0) > 0);

    private static async Task<AggregateEnvelope?> GetAggregateAsync(string mangaId, string language) {
        var languageQuery = string.IsNullOrWhiteSpace(language) ? "" : $"?translatedLanguage[]={Uri.EscapeDataString(language)}";
        return await GetJsonAsync<AggregateEnvelope>($"{Api}/manga/{mangaId}/aggregate{languageQuery}");
    }

    private static string ContentRatingQuery(bool includeNsfw) =>
        string.Concat((includeNsfw ? AllContentRatings : SfwContentRatings).Select(rating => $"&contentRating[]={rating}"));

    private static async Task<T?> GetJsonAsync<T>(string url) {
        for (var attempt = 0; ; attempt++) {
            await ThrottleAsync();
            try {
                using var res = await Http.GetAsync(url);
                if (res.IsSuccessStatusCode) return await res.Content.ReadFromJsonAsync<T>(PluginHost.JsonOptions);
                if (attempt >= MaxRetries || !IsTransientStatus(res.StatusCode)) return default;
            } catch (TaskCanceledException) when (attempt < MaxRetries) {
            }

            await Task.Delay(TimeSpan.FromSeconds(1 + attempt));
        }
    }

    private const int MaxRetries = 3;

    private static bool IsTransientStatus(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.GatewayTimeout
            or System.Net.HttpStatusCode.RequestTimeout;

    /// <summary>
    /// Paces MangaDex requests to stay within the provider's 5-requests-per-second limit.
    /// Identify cascades spawn a separate plugin process per entity, so pacing is coordinated
    /// across processes via an exclusively-locked timestamp file: each call reserves the next
    /// free time slot (at least <see cref="MinRequestInterval"/> after the previous
    /// reservation), then waits for it. Unpaced cascades trip MangaDex's edge protection,
    /// which blocks the IP outright.
    /// </summary>
    private static async Task ThrottleAsync() {
        long slotTicks = DateTime.UtcNow.Ticks;
        for (var attempt = 0; ; attempt++) {
            try {
                using (var fs = new FileStream(RateLimitPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                    using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, false, 64, leaveOpen: true);
                    var lastTicks = long.TryParse((await reader.ReadToEndAsync()).Trim(), out var parsed) ? parsed : 0L;
                    slotTicks = Math.Max(DateTime.UtcNow.Ticks, lastTicks + MinRequestInterval.Ticks);
                    fs.SetLength(0);
                    fs.Position = 0;
                    await fs.WriteAsync(System.Text.Encoding.UTF8.GetBytes(slotTicks.ToString()));
                }

                break;
            } catch (IOException) {
                if (attempt >= 300) {
                    slotTicks = DateTime.UtcNow.Ticks;
                    break;
                }

                await Task.Delay(20);
            }
        }

        var wait = slotTicks - DateTime.UtcNow.Ticks;
        if (wait > 0) {
            await Task.Delay(TimeSpan.FromTicks(wait));
        }
    }

    private static IEnumerable<ImageCandidate> BookImages(MangaResource manga, IReadOnlyList<CoverResource> covers) {
        var ordered = OrderedCovers(manga, covers).ToArray();
        var primary = ordered.FirstOrDefault();
        if (primary is not null) {
            yield return new ImageCandidate("cover", CoverResourceUrl(manga.Id, primary)!, "MangaDex title cover", 10, primary.Attributes?.Locale, null, null);
            yield return new ImageCandidate("backdrop", CoverResourceUrl(manga.Id, primary)!, "MangaDex title header", 6, primary.Attributes?.Locale, null, null);
        }

        foreach (var (cover, index) in ordered.Skip(primary is null ? 0 : 1).Select((cover, index) => (cover, index))) {
            var url = CoverResourceUrl(manga.Id, cover);
            if (url is not null) {
                yield return new ImageCandidate("cover", url, CoverSource(cover), Math.Max(1, 8 - index), cover.Attributes?.Locale, null, null);
            }
        }
    }

    private static IEnumerable<ImageCandidate> VolumeImages(MangaResource manga, IReadOnlyList<CoverResource> covers, string volume) =>
        OrderedCovers(manga, covers)
            .Where(cover => cover.Attributes?.Volume?.Equals(volume, StringComparison.OrdinalIgnoreCase) == true)
            .Select((cover, index) => new ImageCandidate("cover", CoverResourceUrl(manga.Id, cover)!, $"MangaDex volume {volume}", Math.Max(1, 10 - index), cover.Attributes?.Locale, null, null));

    private static IEnumerable<CoverResource> OrderedCovers(MangaResource manga, IReadOnlyList<CoverResource> covers) {
        var relationshipCoverId = manga.Relationships?.FirstOrDefault(rel => rel.Type == "cover_art")?.Id;
        return covers
            .Where(cover => CoverResourceUrl(manga.Id, cover) is not null)
            .OrderBy(cover => relationshipCoverId is not null && cover.Id == relationshipCoverId ? 0 : 1)
            .ThenBy(cover => string.IsNullOrWhiteSpace(cover.Attributes?.Volume) ? 0 : 1)
            .ThenBy(cover => VolumeSortKey(cover.Attributes?.Volume))
            .ThenBy(cover => cover.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static string? CoverUrl(MangaResource manga) {
        var cover = manga.Relationships?.FirstOrDefault(rel => rel.Type == "cover_art");
        return CoverUrlFromFileName(manga.Id, cover?.Attributes?.FileName);
    }

    private static string? CoverResourceUrl(string mangaId, CoverResource cover) =>
        CoverUrlFromFileName(mangaId, cover.Attributes?.FileName);

    private static string? CoverUrlFromFileName(string mangaId, string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : $"{Uploads}/covers/{mangaId}/{fileName}.512.jpg";

    private static string CoverSource(CoverResource cover) =>
        string.IsNullOrWhiteSpace(cover.Attributes?.Volume)
            ? "MangaDex title cover"
            : $"MangaDex volume {cover.Attributes!.Volume}";

    private static IReadOnlyDictionary<string, string> VolumeByChapter(AggregateEnvelope? aggregate) {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (volumeKey, volume) in AggregateVolumes(aggregate)) {
            var volumeNumber = NormalizeVolumeValue(volume.Volume) ?? NormalizeVolumeValue(volumeKey);
            if (volumeNumber is null) continue;
            foreach (var (chapterKey, chapter) in AggregateChapters(volume)) {
                var chapterNumber = NormalizeChapterNumber(chapter.Chapter ?? chapterKey);
                if (chapterNumber is not null && !output.ContainsKey(chapterNumber)) {
                    output[chapterNumber] = volumeNumber;
                }
            }
        }

        return output;
    }

    private static IReadOnlyDictionary<string, string> CoverVolumeByChapter(
        IReadOnlyList<ChapterResource> chapters,
        IReadOnlyList<CoverResource> covers) {
        var unvolumedChapterNumbers = chapters
            .Where(chapter => NormalizeVolumeValue(chapter.Attributes?.Volume) is null)
            .Select(chapter => NormalizeChapterNumber(chapter.Attributes?.Chapter))
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var coverVolumesByNumber = covers
            .Select(cover => NormalizeVolumeValue(cover.Attributes?.Volume))
            .Where(value => value is not null)
            .Select(value => value!)
            .GroupBy(value => NormalizeNumber(value) ?? value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (unvolumedChapterNumbers.Length == 0 || coverVolumesByNumber.Count == 0) {
            return new Dictionary<string, string>();
        }

        if (coverVolumesByNumber.Count == 1) {
            var onlyVolume = coverVolumesByNumber.Values.Single();
            return unvolumedChapterNumbers.ToDictionary(chapter => chapter, _ => onlyVolume, StringComparer.OrdinalIgnoreCase);
        }

        if (unvolumedChapterNumbers.Length == coverVolumesByNumber.Count &&
            unvolumedChapterNumbers.All(chapter => coverVolumesByNumber.ContainsKey(chapter))) {
            return unvolumedChapterNumbers.ToDictionary(chapter => chapter, chapter => coverVolumesByNumber[chapter], StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, string>();
    }

    private static IReadOnlyDictionary<string, AggregateVolume> AggregateVolumes(AggregateEnvelope? aggregate) =>
        JsonObjectDictionary<AggregateVolume>(aggregate?.Volumes);

    private static IReadOnlyDictionary<string, AggregateChapter> AggregateChapters(AggregateVolume volume) =>
        JsonObjectDictionary<AggregateChapter>(volume.Chapters);

    private static IReadOnlyDictionary<string, T> JsonObjectDictionary<T>(JsonElement? value) where T : class {
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.Object) {
            return new Dictionary<string, T>();
        }

        var output = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject()) {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var parsed = property.Value.Deserialize<T>(PluginHost.JsonOptions);
            if (parsed is not null) output[property.Name] = parsed;
        }

        return output;
    }

    private static IReadOnlyList<ChapterResource> UniqueChapters(IReadOnlyList<ChapterResource> chapters) {
        var output = new List<ChapterResource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chapter in chapters.OrderBy(chapter => VolumeSortKey(chapter.Attributes?.Volume)).ThenBy(chapter => ChapterSortKey(chapter.Attributes?.Chapter))) {
            var key = NormalizeChapterNumber(chapter.Attributes?.Chapter) ?? chapter.Id;
            if (seen.Add(key)) output.Add(chapter);
        }

        return output;
    }

    private static string? EffectiveVolume(ChapterResource chapter, IReadOnlyDictionary<string, string> volumeByChapter) {
        if (NormalizeVolumeValue(chapter.Attributes?.Volume) is string explicitVolume) return explicitVolume;
        var chapterNumber = NormalizeChapterNumber(chapter.Attributes?.Chapter);
        return chapterNumber is not null && volumeByChapter.TryGetValue(chapterNumber, out var volume)
            ? NormalizeVolumeValue(volume)
            : null;
    }

    private static string? VolumeDescription(IReadOnlyList<ChapterResource> chapters) {
        if (chapters.Count == 0) return null;
        var parts = new List<string> { $"Includes {ChapterRange(chapters)}." };
        var pageCount = chapters
            .Select(chapter => chapter.Attributes?.Pages)
            .Where(pages => pages is > 0)
            .Sum(pages => pages!.Value);
        if (pageCount > 0) parts.Add($"{pageCount} pages.");
        return string.Join(' ', parts);
    }

    private static string ChapterRange(IReadOnlyList<ChapterResource> chapters) {
        var ordered = chapters
            .OrderBy(chapter => ChapterSortKey(chapter.Attributes?.Chapter))
            .Select(chapter => chapter.Attributes?.Chapter)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
        if (ordered.Length == 0) return $"{chapters.Count} chapter{(chapters.Count == 1 ? "" : "s")}";
        if (ordered.Length == 1) return $"Chapter {ordered[0]}";
        return $"Chapters {ordered[0]}-{ordered[^1]}";
    }

    private static IReadOnlyDictionary<string, int> VolumeStats(IReadOnlyList<ChapterResource> chapters) {
        var stats = new Dictionary<string, int>();
        if (chapters.Count > 0) stats["chapterCount"] = chapters.Count;
        var pages = chapters
            .Select(chapter => chapter.Attributes?.Pages)
            .Where(pageCount => pageCount is > 0)
            .Sum(pageCount => pageCount!.Value);
        if (pages > 0) stats["pageCount"] = pages;
        return stats;
    }

    private static IReadOnlyDictionary<string, string> VolumeDates(IReadOnlyList<ChapterResource> chapters) {
        var dates = chapters
            .Select(chapter => PublishedDate(chapter))
            .Where(value => value is not null)
            .Select(value => value!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (dates.Length == 0) return new Dictionary<string, string>();
        var output = new Dictionary<string, string> { ["published"] = dates[0] };
        if (!dates[^1].Equals(dates[0], StringComparison.Ordinal)) output["completed"] = dates[^1];
        return output;
    }

    private static string? ChapterDescription(ChapterResource chapter) {
        var parts = new List<string>();
        if (chapter.Attributes?.Pages is int pages && pages > 0) parts.Add($"{pages} pages");
        var language = LanguageName(chapter.Attributes?.TranslatedLanguage);
        if (language is not null) parts.Add($"{language} translation");
        return parts.Count == 0 ? null : string.Join(" - ", parts);
    }

    private static string? PublishedDate(ChapterResource chapter) {
        if (string.IsNullOrWhiteSpace(chapter.Attributes?.PublishAt)) return null;
        var value = chapter.Attributes!.PublishAt!;
        return value[..Math.Min(10, value.Length)];
    }

    private static IReadOnlyList<ImageCandidate> ChapterCoverImages(IReadOnlyList<ImageCandidate> volumeImages) =>
        volumeImages
            .Take(1)
            .Select(image => image with {
                Source = image.Source.Contains("cover", StringComparison.OrdinalIgnoreCase) ? image.Source : $"{image.Source} cover",
                Rank = image.Rank is decimal rank ? Math.Max(1, rank - 3) : 5
            })
            .ToArray();

    private static string? LanguageName(string? language) =>
        language?.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase) == true
            ? "English"
            : string.IsNullOrWhiteSpace(language) ? null : language;

    private static string[] Tags(MangaResource manga) {
        var attrs = manga.Attributes;
        var tags = (attrs?.Tags ?? [])
            .Select(tag => Localized(tag.Attributes?.Name) ?? tag.Attributes?.Group)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();
        var parsed = ParseDescriptionTags(RawDescription(manga));
        tags.AddRange(parsed.Tags);
        if (!string.IsNullOrWhiteSpace(attrs?.PublicationDemographic)) tags.Add($"demographic: {attrs.PublicationDemographic}");
        if (!string.IsNullOrWhiteSpace(attrs?.Status)) tags.Add($"status: {attrs.Status}");
        if (!string.IsNullOrWhiteSpace(attrs?.OriginalLanguage)) tags.Add($"original language: {attrs.OriginalLanguage}");
        return tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToArray();
    }

    private static CreditPatch[] Credits(MangaResource manga) {
        var parsed = ParseDescriptionTags(RawDescription(manga));
        var names = RelationshipNames(manga, "author", "artist").Concat(parsed.Artists).Distinct(StringComparer.OrdinalIgnoreCase);
        return names.Select((name, index) => new CreditPatch(name, "creator", null, index)).ToArray();
    }

    private static ParsedDescription ParseDescriptionTags(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return new ParsedDescription([], []);
        var tagsIndex = Array.FindIndex(text.Split('\n'), line => line.Contains("Namespace", StringComparison.OrdinalIgnoreCase) && line.Contains("Tags", StringComparison.OrdinalIgnoreCase));
        if (tagsIndex < 0) return new ParsedDescription([], []);
        var artists = new List<string>();
        var tags = new List<string>();
        foreach (var line in text.Split('\n').Skip(tagsIndex + 1)) {
            var cells = line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length < 2 || cells.All(cell => Regex.IsMatch(cell, "^:?-+:?$"))) continue;
            var key = cells[0].ToLowerInvariant();
            var values = cells.Skip(1).SelectMany(cell => cell.Split(',')).Select(cell => cell.Trim()).Where(cell => cell.Length > 0);
            if (key == "artist") artists.AddRange(values);
            else if (!key.Contains("--", StringComparison.Ordinal)) tags.AddRange(values.Select(value => $"{char.ToUpperInvariant(key[0])}{key[1..]}: {value}"));
        }

        return new ParsedDescription(artists.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<string> RelationshipNames(MangaResource manga, params string[] types) =>
        (manga.Relationships ?? [])
            .Where(rel => types.Contains(rel.Type, StringComparer.OrdinalIgnoreCase))
            .Select(rel => rel.Attributes?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim());

    private static string? ScanlationGroup(ChapterResource chapter) =>
        chapter.Relationships?.FirstOrDefault(rel => rel.Type == "scanlation_group")?.Attributes?.Name;

    private static EntityMetadataFlagsPatch? AdultFlags(MangaResource manga) =>
        IsAdult(manga) ? new EntityMetadataFlagsPatch(null, true, null) : null;

    private static bool IsAdult(MangaResource manga) =>
        manga.Attributes?.ContentRating is "erotica" or "pornographic";

    private static string PreferredLanguage(IdentifyPluginRequest request) =>
        SearchField(request, LanguageField) ??
        (TryGetValue(request.Query.ExternalIds, LanguageField, out var queryLanguage) ? queryLanguage : null) ??
        (TryGetValue(request.Entity.ExternalIds, LanguageField, out var entityLanguage) ? entityLanguage : null) ??
        DefaultLanguage;

    private static string? Title(MangaResource manga, string? preferredTitle = null) {
        var titles = AllTitles(manga).ToArray();
        if (!string.IsNullOrWhiteSpace(preferredTitle)) {
            var normalized = Normalize(preferredTitle);
            var exact = titles.FirstOrDefault(title => Normalize(title).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact)) return exact;
            var contains = titles.FirstOrDefault(title => {
                var candidate = Normalize(title);
                return candidate.Length > 0 && (candidate.Contains(normalized, StringComparison.OrdinalIgnoreCase) || normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            });
            if (!string.IsNullOrWhiteSpace(contains)) return contains;
        }

        return titles.FirstOrDefault() ?? manga.Id;
    }

    private static IEnumerable<string> AllTitles(MangaResource manga) {
        foreach (var title in Values(manga.Attributes?.Title)) yield return title;
        foreach (var altTitle in manga.Attributes?.AltTitles ?? []) {
            foreach (var title in Values(altTitle)) yield return title;
        }
    }

    private static IEnumerable<string> Values(Dictionary<string, string>? values) {
        if (values is null) yield break;
        if (values.TryGetValue(DefaultLanguage, out var english) && !string.IsNullOrWhiteSpace(english)) yield return english;
        foreach (var value in values.Values) {
            if (!string.IsNullOrWhiteSpace(value) && value != english) yield return value;
        }
    }

    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();

    private static string? DescriptionText(MangaResource manga) {
        var raw = RawDescription(manga);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var lines = raw.Split('\n');
        var tagsIndex = Array.FindIndex(lines, line => line.Contains("Namespace", StringComparison.OrdinalIgnoreCase) && line.Contains("Tags", StringComparison.OrdinalIgnoreCase));
        return tagsIndex < 0 ? raw.Trim() : string.Join('\n', lines.Take(tagsIndex)).Trim();
    }

    private static string? RawDescription(MangaResource manga) => Localized(manga.Attributes?.Description);

    private static string? Localized(Dictionary<string, string>? values) {
        if (values is null) return null;
        if (values.TryGetValue(DefaultLanguage, out var en) && !string.IsNullOrWhiteSpace(en)) return en;
        return values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static decimal VolumeSortKey(string? value) => decimal.TryParse(value, out var number) ? number : decimal.MaxValue;
    private static decimal ChapterSortKey(string? value) => decimal.TryParse(value, out var number) ? number : decimal.MaxValue;
    private static int? PositionNumber(string? value) {
        if (!decimal.TryParse(value, out var number)) return null;
        return decimal.Remainder(number, 1) == 0
            ? (int)number
            : (int)Math.Round(number * 1000);
    }

    private static int? ZeroBasedSortPosition(string? value) {
        if (!decimal.TryParse(value, out var number)) return null;
        if (number < 0 || decimal.Remainder(number, 1) != 0) return null;
        return Math.Max(0, (int)number - 1);
    }
    private static string? NumberFromTitle(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"(?:^|\b)(?:volume|vol\.?|v)?\s*0*(\d+(?:\.\d+)?)(?:\b|$)", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeNumber(match.Groups[1].Value) : null;
    }

    private static string? ChapterNumberFromTitle(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"\bch(?:apter)?\.?\s*0*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeChapterNumber(match.Groups[1].Value) : null;
    }
    private static string? NormalizeNumber(string? value) => decimal.TryParse(value, out var number) ? number.ToString("0.###") : null;
    private static string? NormalizeChapterNumber(string? value) => decimal.TryParse(value, out var number) ? number.ToString("0.###") : null;
    private static string? NormalizeVolumeValue(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static string? ExternalId(IdentifyPluginRequest request, string key) {
        foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) {
            if (TryGetValue(ids, key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static string? AncestorExternalId(IdentifyPluginRequest request, string key) =>
        request.StructuralContext?.Ancestors
            .Select(ancestor => TryGetValue(ancestor.ExternalIds, key, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    internal static string? SearchField(IdentifyPluginRequest request, params string[] keys) {
        foreach (var key in keys) {
            if (TryGetValue(request.Query.Fields, key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static int? SearchYear(IdentifyPluginRequest request) =>
        int.TryParse(SearchField(request, SearchFields.Year), out var year) && year is >= 1900 and <= 2200
            ? year
            : null;

    internal static string FormatVolumeIdentity(string mangaId, string volume) =>
        $"{mangaId}:{volume.Length}:{volume}";

    internal static bool TryParseVolumeIdentity(string? value, out string mangaId, out string volume) {
        mangaId = string.Empty;
        volume = string.Empty;
        if (string.IsNullOrEmpty(value)) return false;

        var idSeparator = value.IndexOf(':');
        if (idSeparator <= 0 || !Guid.TryParse(value[..idSeparator], out _)) return false;
        var lengthSeparator = value.IndexOf(':', idSeparator + 1);
        if (lengthSeparator <= idSeparator + 1 ||
            !int.TryParse(value[(idSeparator + 1)..lengthSeparator], out var expectedLength) ||
            expectedLength < 1) {
            return false;
        }

        var encodedVolume = value[(lengthSeparator + 1)..];
        if (encodedVolume.Length != expectedLength) return false;
        mangaId = value[..idSeparator];
        volume = encodedVolume;
        return true;
    }

    private static bool TryGetVolumeIdentity(
        IdentifyPluginRequest request,
        out string mangaId,
        out string volume) {
        foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) {
            if (TryGetValue(ids, VolumeIdentityNamespace, out var value) &&
                TryParseVolumeIdentity(value, out mangaId, out volume)) {
                return true;
            }
        }

        mangaId = string.Empty;
        volume = string.Empty;
        return false;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, string>? values, string key, out string value) {
        foreach (var pair in values ?? new Dictionary<string, string>()) {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) {
                value = pair.Value;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    private static string? FirstUrlId(IReadOnlyList<string> urls) => urls.Select(IdFromUrl).FirstOrDefault(id => id is not null);
    private static string? FirstChapterUrlId(IReadOnlyList<string> urls) => urls.Select(ChapterIdFromUrl).FirstOrDefault(id => id is not null);

    private static string? IdFromUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = Regex.Match(url, "mangadex\\.org/(?:title|manga)/([0-9a-f-]{36})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ChapterIdFromUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = Regex.Match(url, "mangadex\\.org/chapter/([0-9a-f-]{36})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsExplicitSearch(IdentifyPluginRequest request) =>
        request.Action.Equals("search", StringComparison.OrdinalIgnoreCase) &&
        (!string.IsNullOrWhiteSpace(request.Query.Title) || request.Query.Fields?.Values.Any(value => !string.IsNullOrWhiteSpace(value)) == true) &&
        string.IsNullOrWhiteSpace(request.Query.Url) &&
        request.Query.ExternalIds is not { Count: > 0 };

    private sealed record SingleEnvelope<T>(T? Data);
    private sealed record ListEnvelope<T>(T[]? Data, int? Total);
    private sealed record MangaResource(string Id, string Type, MangaAttributes? Attributes, Relationship[]? Relationships);
    private sealed record MangaAttributes(Dictionary<string, string>? Title, Dictionary<string, string>[]? AltTitles, Dictionary<string, string>? Description, int? Year, string? ContentRating, string? PublicationDemographic, string? Status, string? OriginalLanguage, Tag[]? Tags);
    private sealed record Tag(TagAttributes? Attributes);
    private sealed record TagAttributes(Dictionary<string, string>? Name, string? Group);
    private sealed record Relationship(string Id, string Type, RelationshipAttributes? Attributes);
    private sealed record RelationshipAttributes(string? FileName, string? Locale, string? Name);
    private sealed record CoverResource(string Id, string Type, CoverAttributes? Attributes);
    private sealed record CoverAttributes(string? FileName, string? Volume, string? Locale);
    private sealed record ChapterResource(string Id, ChapterAttributes? Attributes, Relationship[]? Relationships);
    private sealed record ChapterAttributes(string? Title, string? Volume, string? Chapter, string? TranslatedLanguage, string? PublishAt, string? ReadableAt, int? Pages, string? ExternalUrl, bool? IsUnavailable);
    private sealed record AggregateEnvelope(JsonElement? Volumes);
    private sealed record AggregateVolume(string? Volume, JsonElement? Chapters);
    private sealed record AggregateChapter(string? Chapter);
    private sealed record ParsedDescription(string[] Artists, string[] Tags);
}

internal static class PluginHost {
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = false };
    public static async Task<IdentifyPluginResponse> RunAsync(string[] args, Func<IdentifyPluginRequest, Task<IdentifyPluginResult>> identify) {
        try {
            if (args.Length == 0) return new(false, null, "Missing request JSON path.");
            var request = JsonSerializer.Deserialize<IdentifyPluginRequest>(await File.ReadAllTextAsync(args[0]), JsonOptions);
            if (request is null) return new(false, null, "Request JSON was empty or invalid.");
            return new(true, await identify(request), null);
        } catch (Exception ex) {
            return new(false, null, ex.Message);
        }
    }
}

internal sealed record IdentifyPluginRequest(int ProtocolVersion, string Action, IReadOnlyDictionary<string, string> Auth, IdentifyEntitySnapshot Entity, IdentifyQuery Query, IdentifyMatchHints Hints, IdentifyStructuralContext? StructuralContext = null, bool IncludeNsfw = false);
internal sealed record IdentifyStructuralContext(IReadOnlyList<IdentifyEntitySnapshot> Ancestors, IReadOnlyDictionary<string, int> Positions);
internal sealed record IdentifyEntitySnapshot(Guid Id, string Kind, string Title, IReadOnlyDictionary<string, string>? ExternalIds = null, IReadOnlyList<string>? Urls = null);
internal sealed record IdentifyQuery(string? Title, string? Url, IReadOnlyDictionary<string, string>? ExternalIds, bool? RequireChoice = null, IReadOnlyDictionary<string, string>? Fields = null, int? Limit = null);
internal sealed record IdentifyMatchHints(IReadOnlyDictionary<string, string> ExternalIds, IReadOnlyList<string> Urls, string? Title, string? FilePath);
internal sealed record ImageCandidate(string Kind, string Url, string Source, decimal? Rank, string? Language, int? Width, int? Height);
internal sealed record EntitySearchCandidate(IReadOnlyDictionary<string, string> ExternalIds, string Title, int? Year, string? Overview, string? PosterUrl, decimal? Popularity);
internal sealed record CreditPatch(string Name, string Role, string? Character, int? SortOrder);
internal sealed record EntityMetadataFlagsPatch(bool? IsFavorite, bool? IsNsfw, bool? IsOrganized);
internal sealed record EntityMetadataPatch(string? Title, string? Description, IReadOnlyDictionary<string, string> ExternalIds, IReadOnlyList<string> Urls, IReadOnlyList<string> Tags, string? Studio, IReadOnlyList<CreditPatch> Credits, IReadOnlyDictionary<string, string> Dates, IReadOnlyDictionary<string, int> Stats, IReadOnlyDictionary<string, int> Positions, string? Classification) { public int? Rating { get; init; } public EntityMetadataFlagsPatch? Flags { get; init; } }
internal sealed record EntityMetadataProposal(string ProposalId, string Provider, string TargetKind, decimal? Confidence, string? MatchReason, EntityMetadataPatch Patch, IReadOnlyList<ImageCandidate> Images, IReadOnlyList<EntityMetadataProposal> Children, IReadOnlyList<EntitySearchCandidate> Candidates, Guid? TargetEntityId = null, IReadOnlyList<EntityMetadataProposal>? Relationships = null);
internal sealed record IdentifyPluginResult(string Type, EntityMetadataProposal? Proposal, IReadOnlyList<EntitySearchCandidate> Candidates) { public static IdentifyPluginResult ForProposal(EntityMetadataProposal proposal) => new("proposal", proposal, []); public static IdentifyPluginResult ForCandidates(IReadOnlyList<EntitySearchCandidate> candidates) => new("candidates", null, candidates); public static IdentifyPluginResult None() => new("none", null, []); }
internal sealed record IdentifyPluginResponse(bool Ok, IdentifyPluginResult? Result, string? Error);
