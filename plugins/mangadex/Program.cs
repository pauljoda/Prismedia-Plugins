using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

var response = await PluginHost.RunAsync(args, MangaDexPlugin.IdentifyAsync);
Console.Write(JsonSerializer.Serialize(response, PluginHost.JsonOptions));

internal static partial class MangaDexPlugin {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly string[] SfwContentRatings = ["safe", "suggestive"];
    private static readonly string[] AllContentRatings = ["safe", "suggestive", "erotica", "pornographic"];
    private const string Provider = "mangadex";
    private const string Api = "https://api.mangadex.org";
    private const string Web = "https://mangadex.org";
    private const string Uploads = "https://uploads.mangadex.org";
    private const string DefaultLanguage = "en";

    static MangaDexPlugin() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Prismedia-MangaDex-Plugin/1.1");

    public static async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        if (!request.Entity.Kind.Equals("book", StringComparison.OrdinalIgnoreCase) &&
            !request.Entity.Kind.Equals("book-volume", StringComparison.OrdinalIgnoreCase) &&
            !request.Entity.Kind.Equals("book-chapter", StringComparison.OrdinalIgnoreCase)) {
            return IdentifyPluginResult.None();
        }

        var query = request.Query.Title ?? request.Hints.Title ?? request.Entity.Title;
        var mangaId = ExternalId(request, Provider)
            ?? IdFromUrl(request.Query.Url)
            ?? FirstUrlId(request.Hints.Urls)
            ?? AncestorExternalId(request, Provider);

        var chapterId = ExternalId(request, "mangadexChapter")
            ?? ChapterIdFromUrl(request.Query.Url)
            ?? FirstChapterUrlId(request.Hints.Urls);

        if (mangaId is not null && !IsExplicitSearch(request)) {
            return IdentifyPluginResult.ForProposal(await ProposalAsync(mangaId, request, "external-id", query));
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

        var results = await SearchAsync(query, request.IncludeNsfw);
        return IdentifyPluginResult.ForCandidates(results.Select(manga => new EntitySearchCandidate(
            new Dictionary<string, string> { [Provider] = manga.Id },
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
        string? selectedChapterId = null) {
        var manga = await GetMangaAsync(id) ?? throw new InvalidOperationException("MangaDex title not found.");
        if (!request.IncludeNsfw && IsAdult(manga)) {
            throw new InvalidOperationException("MangaDex title is adult-rated and NSFW mode is not enabled.");
        }

        var covers = await GetCoversAsync(manga.Id);
        var chapters = await GetChaptersAsync(manga.Id, request.IncludeNsfw, PreferredLanguage(request));
        var aggregate = await GetAggregateAsync(manga.Id, PreferredLanguage(request));
        var children = BuildChildren(manga, chapters, aggregate, covers, selectedChapterId).ToArray();
        var images = BookImages(manga, covers).ToArray();
        var attrs = manga.Attributes;
        var external = new Dictionary<string, string> { [Provider] = manga.Id };
        var urls = new[] { $"{Web}/title/{manga.Id}" };
        var dates = new Dictionary<string, string>();
        if (attrs?.Year is int year) dates["published"] = year.ToString();

        var proposal = new EntityMetadataProposal(
            $"mangadex:{manga.Id}",
            Provider,
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
        return ScopedProposalForRequest(proposal, request);
    }

    private static EntityMetadataProposal ScopedProposalForRequest(EntityMetadataProposal bookProposal, IdentifyPluginRequest request) {
        if (request.Entity.Kind.Equals("book", StringComparison.OrdinalIgnoreCase)) {
            return bookProposal;
        }

        if (request.Entity.Kind.Equals("book-volume", StringComparison.OrdinalIgnoreCase)) {
            var volume = bookProposal.Children.FirstOrDefault(child => MatchesVolumeRequest(child, request));
            return volume is null
                ? ScopedFallback(bookProposal, request, "book-volume")
                : volume with { TargetEntityId = request.Entity.Id };
        }

        if (request.Entity.Kind.Equals("book-chapter", StringComparison.OrdinalIgnoreCase)) {
            var chapter = StructuralDescendants(bookProposal)
                .FirstOrDefault(child => child.TargetKind.Equals("book-chapter", StringComparison.OrdinalIgnoreCase) && MatchesChapterRequest(child, request));
            return chapter is null
                ? ScopedFallback(bookProposal, request, "book-chapter")
                : chapter with { TargetEntityId = request.Entity.Id };
        }

        return bookProposal;
    }

    private static EntityMetadataProposal ScopedFallback(EntityMetadataProposal bookProposal, IdentifyPluginRequest request, string targetKind) =>
        bookProposal with {
            ProposalId = $"{bookProposal.ProposalId}:{targetKind}:{request.Entity.Id}",
            TargetKind = targetKind,
            TargetEntityId = request.Entity.Id,
            Patch = bookProposal.Patch with {
                Title = request.Entity.Title,
                Description = null,
                Tags = [],
                Credits = [],
                Stats = new Dictionary<string, int>(),
                Positions = request.StructuralContext?.Positions ?? new Dictionary<string, int>()
            },
            Images = [],
            Children = [],
            Candidates = [],
            Relationships = []
        };

    private static IEnumerable<EntityMetadataProposal> StructuralDescendants(EntityMetadataProposal proposal) {
        foreach (var child in proposal.Children) {
            yield return child;
            foreach (var descendant in StructuralDescendants(child)) {
                yield return descendant;
            }
        }
    }

    private static bool MatchesVolumeRequest(EntityMetadataProposal volume, IdentifyPluginRequest request) {
        if (!volume.TargetKind.Equals("book-volume", StringComparison.OrdinalIgnoreCase)) return false;

        var requestedVolume = ExternalId(request, "volume");
        var requestedVolumeNumber = NormalizeNumber(requestedVolume) ?? NumberFromTitle(request.Entity.Title);
        if (requestedVolumeNumber is not null &&
            volume.Patch.ExternalIds.TryGetValue("volume", out var volumeId) &&
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
        var requestedChapterId = ExternalId(request, "mangadexChapter");
        if (!string.IsNullOrWhiteSpace(requestedChapterId) &&
            chapter.Patch.ExternalIds.TryGetValue("mangadexChapter", out var chapterId) &&
            chapterId.Equals(requestedChapterId, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var requestedChapterNumber = ExternalId(request, "chapterNumber");
        if (!string.IsNullOrWhiteSpace(requestedChapterNumber) &&
            chapter.Patch.ExternalIds.TryGetValue("chapterNumber", out var proposalChapterNumber) &&
            NormalizeChapterNumber(proposalChapterNumber) == NormalizeChapterNumber(requestedChapterNumber)) {
            return true;
        }

        var positions = request.StructuralContext?.Positions ?? new Dictionary<string, int>();
        var requestPosition = PositionValue(positions, "chapter", "chapterNumber", "sort", "sortOrder");
        var proposalPosition = PositionValue(chapter.Patch.Positions, "chapter", "chapterNumber", "sort", "sortOrder");
        if (requestPosition is not null && proposalPosition == requestPosition) return true;

        return !string.IsNullOrWhiteSpace(request.Entity.Title) &&
            chapter.Patch.Title?.Equals(request.Entity.Title, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static int? PositionValue(IReadOnlyDictionary<string, int> positions, params string[] keys) {
        foreach (var key in keys) {
            if (positions.TryGetValue(key, out var value)) return value;
        }

        return null;
    }

    private static IReadOnlyList<EntityMetadataProposal> BuildChildren(
        MangaResource manga,
        IReadOnlyList<ChapterResource> chapters,
        AggregateEnvelope? aggregate,
        IReadOnlyList<CoverResource> covers,
        string? selectedChapterId) {
        var volumeByChapter = VolumeByChapter(aggregate);
        var uniqueChapters = UniqueChapters(chapters);
        var volumeNumbers = uniqueChapters
            .Select(chapter => EffectiveVolume(chapter, volumeByChapter))
            .Concat(covers.Select(cover => cover.Attributes?.Volume))
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
            children.Add(VolumeProposal(manga, volume, covers, volumeChapters, selectedChapterId));
        }

        foreach (var chapter in uniqueChapters.Where(chapter => EffectiveVolume(chapter, volumeByChapter) is null).OrderBy(chapter => ChapterSortKey(chapter.Attributes?.Chapter))) {
            children.Add(ChapterProposal(manga, chapter, selectedChapterId));
        }

        return children;
    }

    private static EntityMetadataProposal VolumeProposal(
        MangaResource manga,
        string volume,
        IReadOnlyList<CoverResource> covers,
        IReadOnlyList<ChapterResource> chapters,
        string? selectedChapterId) {
        var coverImages = VolumeImages(manga, covers, volume).ToArray();
        var volumePosition = PositionNumber(volume);
        var positions = new Dictionary<string, int>();
        if (volumePosition is int position) {
            positions["volumeNumber"] = position;
            positions["sortOrder"] = position;
        }

        return new EntityMetadataProposal(
            $"mangadex:{manga.Id}:volume:{volume}",
            Provider,
            "book-volume",
            0.8m,
            "volume-map",
            new EntityMetadataPatch(
                $"Volume {volume}",
                null,
                new Dictionary<string, string> { [Provider] = manga.Id, ["volume"] = volume },
                [$"{Web}/title/{manga.Id}"],
                [],
                null,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, int>(),
                positions,
                null) {
                Flags = AdultFlags(manga)
            },
            coverImages,
            chapters.Select(chapter => ChapterProposal(manga, chapter, selectedChapterId)).ToArray(),
            []);
    }

    private static EntityMetadataProposal ChapterProposal(MangaResource manga, ChapterResource chapter, string? selectedChapterId) {
        var chapterText = chapter.Attributes?.Chapter;
        var chapterNumber = PositionNumber(chapterText);
        var title = string.IsNullOrWhiteSpace(chapter.Attributes?.Title)
            ? $"Chapter {chapterText ?? chapter.Id}"
            : $"Chapter {chapterText}: {chapter.Attributes!.Title}";
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(chapter.Attributes?.PublishAt)) {
            dates["published"] = chapter.Attributes!.PublishAt![..Math.Min(10, chapter.Attributes.PublishAt.Length)];
        }

        var positions = new Dictionary<string, int>();
        if (chapterNumber is int position) {
            positions["chapterNumber"] = position;
            positions["sortOrder"] = position;
        }

        var external = new Dictionary<string, string> {
            [Provider] = manga.Id,
            ["mangadexChapter"] = chapter.Id
        };
        if (!string.IsNullOrWhiteSpace(chapterText)) external["chapterNumber"] = chapterText!;
        if (!string.IsNullOrWhiteSpace(chapter.Attributes?.Volume)) external["volume"] = chapter.Attributes!.Volume!;

        return new EntityMetadataProposal(
            $"mangadex:{manga.Id}:chapter:{chapter.Id}",
            Provider,
            "book-chapter",
            selectedChapterId == chapter.Id ? 0.9m : 0.7m,
            "chapter-feed",
            new EntityMetadataPatch(
                title,
                null,
                external,
                [$"{Web}/chapter/{chapter.Id}"],
                [],
                ScanlationGroup(chapter),
                [],
                dates,
                new Dictionary<string, int>(),
                positions,
                null) {
                Flags = AdultFlags(manga)
            },
            [],
            [],
            []);
    }

    private static async Task<IReadOnlyList<MangaResource>> SearchAsync(string title, bool includeNsfw) {
        var url = $"{Api}/manga?title={Uri.EscapeDataString(title)}&limit=10&includes[]=cover_art&includes[]=author&includes[]=artist&order[relevance]=desc{ContentRatingQuery(includeNsfw)}";
        return (await GetJsonAsync<ListEnvelope<MangaResource>>(url))?.Data ?? [];
    }

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
        var offset = 0;
        while (true) {
            var languageQuery = string.IsNullOrWhiteSpace(language) ? "" : $"&translatedLanguage[]={Uri.EscapeDataString(language)}";
            var url = $"{Api}/manga/{mangaId}/feed?limit=100&offset={offset}&order[volume]=asc&order[chapter]=asc&includes[]=scanlation_group{languageQuery}{ContentRatingQuery(includeNsfw)}";
            var page = await GetJsonAsync<ListEnvelope<ChapterResource>>(url);
            var rows = page?.Data ?? [];
            output.AddRange(rows);
            if (rows.Length == 0 || output.Count >= (page?.Total ?? output.Count)) break;
            offset += rows.Length;
        }

        return output;
    }

    private static async Task<AggregateEnvelope?> GetAggregateAsync(string mangaId, string language) {
        var languageQuery = string.IsNullOrWhiteSpace(language) ? "" : $"?translatedLanguage[]={Uri.EscapeDataString(language)}";
        return await GetJsonAsync<AggregateEnvelope>($"{Api}/manga/{mangaId}/aggregate{languageQuery}");
    }

    private static string ContentRatingQuery(bool includeNsfw) =>
        string.Concat((includeNsfw ? AllContentRatings : SfwContentRatings).Select(rating => $"&contentRating[]={rating}"));

    private static async Task<T?> GetJsonAsync<T>(string url) {
        using var res = await Http.GetAsync(url);
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<T>(PluginHost.JsonOptions) : default;
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
        foreach (var (volumeKey, volume) in aggregate?.Volumes ?? new Dictionary<string, AggregateVolume>()) {
            var volumeNumber = string.IsNullOrWhiteSpace(volume.Volume) ? volumeKey : volume.Volume;
            if (string.IsNullOrWhiteSpace(volumeNumber)) continue;
            foreach (var (chapterKey, chapter) in volume.Chapters ?? new Dictionary<string, AggregateChapter>()) {
                var chapterNumber = NormalizeChapterNumber(chapter.Chapter ?? chapterKey);
                if (chapterNumber is not null && !output.ContainsKey(chapterNumber)) {
                    output[chapterNumber] = volumeNumber.Trim();
                }
            }
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
        if (!string.IsNullOrWhiteSpace(chapter.Attributes?.Volume)) return chapter.Attributes!.Volume!.Trim();
        var chapterNumber = NormalizeChapterNumber(chapter.Attributes?.Chapter);
        return chapterNumber is not null && volumeByChapter.TryGetValue(chapterNumber, out var volume) ? volume : null;
    }

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
        request.Query.ExternalIds?.GetValueOrDefault("language") ??
        request.Entity.ExternalIds?.GetValueOrDefault("language") ??
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
    private static string? NumberFromTitle(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"(?:^|\b)(?:volume|vol\.?|v)?\s*0*(\d+(?:\.\d+)?)(?:\b|$)", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeNumber(match.Groups[1].Value) : null;
    }
    private static string? NormalizeNumber(string? value) => decimal.TryParse(value, out var number) ? number.ToString("0.###") : null;
    private static string? NormalizeChapterNumber(string? value) => decimal.TryParse(value, out var number) ? number.ToString("0.###") : null;

    private static string? ExternalId(IdentifyPluginRequest request, string key) {
        foreach (var ids in new[] { request.Query.ExternalIds, request.Entity.ExternalIds, request.Hints.ExternalIds }) {
            if (ids is not null && ids.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static string? AncestorExternalId(IdentifyPluginRequest request, string key) =>
        request.StructuralContext?.Ancestors.Select(ancestor => ancestor.ExternalIds?.GetValueOrDefault(key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

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
        !string.IsNullOrWhiteSpace(request.Query.Title) &&
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
    private sealed record ChapterAttributes(string? Title, string? Volume, string? Chapter, string? TranslatedLanguage, string? PublishAt, string? ReadableAt);
    private sealed record AggregateEnvelope(Dictionary<string, AggregateVolume>? Volumes);
    private sealed record AggregateVolume(string? Volume, Dictionary<string, AggregateChapter>? Chapters);
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
internal sealed record IdentifyQuery(string? Title, string? Url, IReadOnlyDictionary<string, string>? ExternalIds, bool? RequireChoice = null);
internal sealed record IdentifyMatchHints(IReadOnlyDictionary<string, string> ExternalIds, IReadOnlyList<string> Urls, string? Title, string? FilePath);
internal sealed record ImageCandidate(string Kind, string Url, string Source, decimal? Rank, string? Language, int? Width, int? Height);
internal sealed record EntitySearchCandidate(IReadOnlyDictionary<string, string> ExternalIds, string Title, int? Year, string? Overview, string? PosterUrl, decimal? Popularity);
internal sealed record CreditPatch(string Name, string Role, string? Character, int? SortOrder);
internal sealed record EntityMetadataFlagsPatch(bool? IsFavorite, bool? IsNsfw, bool? IsOrganized);
internal sealed record EntityMetadataPatch(string? Title, string? Description, IReadOnlyDictionary<string, string> ExternalIds, IReadOnlyList<string> Urls, IReadOnlyList<string> Tags, string? Studio, IReadOnlyList<CreditPatch> Credits, IReadOnlyDictionary<string, string> Dates, IReadOnlyDictionary<string, int> Stats, IReadOnlyDictionary<string, int> Positions, string? Classification) { public int? Rating { get; init; } public EntityMetadataFlagsPatch? Flags { get; init; } }
internal sealed record EntityMetadataProposal(string ProposalId, string Provider, string TargetKind, decimal? Confidence, string? MatchReason, EntityMetadataPatch Patch, IReadOnlyList<ImageCandidate> Images, IReadOnlyList<EntityMetadataProposal> Children, IReadOnlyList<EntitySearchCandidate> Candidates, Guid? TargetEntityId = null, IReadOnlyList<EntityMetadataProposal>? Relationships = null);
internal sealed record IdentifyPluginResult(string Type, EntityMetadataProposal? Proposal, IReadOnlyList<EntitySearchCandidate> Candidates) { public static IdentifyPluginResult ForProposal(EntityMetadataProposal proposal) => new("proposal", proposal, []); public static IdentifyPluginResult ForCandidates(IReadOnlyList<EntitySearchCandidate> candidates) => new("candidates", null, candidates); public static IdentifyPluginResult None() => new("none", null, []); }
internal sealed record IdentifyPluginResponse(bool Ok, IdentifyPluginResult? Result, string? Error);
