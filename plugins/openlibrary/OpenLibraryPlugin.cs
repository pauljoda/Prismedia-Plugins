namespace Prismedia.Plugin.OpenLibrary;

internal sealed class OpenLibraryPlugin {
    private readonly OpenLibraryApiClient _client;

    public OpenLibraryPlugin(OpenLibraryApiClient client) {
        _client = client;
    }

    public async Task<IdentifyPluginResult> IdentifyAsync(IdentifyPluginRequest request) {
        if (request.Entity.Kind.Equals("book", StringComparison.OrdinalIgnoreCase)) {
            return await IdentifyBookAsync(request, "book");
        }

        if (request.Entity.Kind.Equals("book-volume", StringComparison.OrdinalIgnoreCase)) {
            return await IdentifyBookVolumeAsync(request);
        }

        if (request.Entity.Kind.Equals("person", StringComparison.OrdinalIgnoreCase)) {
            return await IdentifyPersonAsync(request);
        }

        return IdentifyPluginResult.None();
    }

    private async Task<IdentifyPluginResult> IdentifyBookAsync(IdentifyPluginRequest request, string targetKind) {
        if (ResolveSeriesName(request) is { } seriesName && !IsExplicitSearch(request)) {
            return IdentifyPluginResult.ForProposal(await SeriesProposalAsync(
                seriesName,
                request.Entity.Id,
                "series-id",
                request.IncludeStructuralChildren,
                request.IncludeRelationshipDetails));
        }

        if (await ResolveWorkLookupAsync(request) is { } lookup && !IsExplicitSearch(request)) {
            return IdentifyPluginResult.ForProposal(await BookProposalAsync(
                lookup.WorkId,
                targetKind,
                request.Entity.Id,
                lookup.MatchReason,
                lookup.Edition,
                request.IncludeRelationshipDetails,
                request.IncludeStructuralChildren));
        }

        if (await SeriesChildProposalAsync(request, targetKind) is { } seriesChildProposal) {
            return IdentifyPluginResult.ForProposal(seriesChildProposal);
        }

        var query = QueryTitle(request);
        if (string.IsNullOrWhiteSpace(query)) {
            return IdentifyPluginResult.None();
        }

        var search = await _client.SearchWorksAsync(BuildWorkSearchQuery(request, query), 10);
        var docs = search?.Docs ?? [];
        var workCandidates = WorkCandidates(docs, query).ToArray();
        var seriesCandidates = SeriesCandidates(docs, query, PreferImpliedSeries(request, targetKind)).ToArray();
        var orderedCandidates = PreferSeriesCandidates(request, targetKind, seriesCandidates)
            ? seriesCandidates.Concat(workCandidates)
            : workCandidates.Concat(seriesCandidates);
        var candidates = orderedCandidates
            .Take(12)
            .ToArray();
        return candidates.Length == 0 ? IdentifyPluginResult.None() : IdentifyPluginResult.ForCandidates(candidates);
    }

    private async Task<IdentifyPluginResult> IdentifyBookVolumeAsync(IdentifyPluginRequest request) {
        if (await ResolveWorkLookupAsync(request) is { } lookup && !IsExplicitSearch(request)) {
            return IdentifyPluginResult.ForProposal(await BookProposalAsync(
                lookup.WorkId,
                "book-volume",
                request.Entity.Id,
                lookup.MatchReason,
                lookup.Edition,
                request.IncludeRelationshipDetails,
                request.IncludeStructuralChildren));
        }

        if (await SeriesChildProposalAsync(request, "book-volume") is { } seriesChildProposal) {
            return IdentifyPluginResult.ForProposal(seriesChildProposal);
        }

        return await IdentifyBookAsync(request, "book-volume");
    }

    private async Task<IdentifyPluginResult> IdentifyPersonAsync(IdentifyPluginRequest request) {
        var authorId = ResolveAuthorId(request);
        if (authorId is not null && !IsExplicitSearch(request)) {
            return IdentifyPluginResult.ForProposal(
                await AuthorProposalAsync(authorId, "external-id", request.IncludeStructuralChildren));
        }

        var query = QueryTitle(request);
        if (string.IsNullOrWhiteSpace(query)) {
            return IdentifyPluginResult.None();
        }

        var birthYear = int.TryParse(SearchField(request, OpenLibraryMetadata.SearchFields.BirthYear), out var parsedBirthYear) ? parsedBirthYear : (int?)null;
        var authors = ((await _client.SearchAuthorsAsync(query, 10))?.Docs ?? [])
            .Where(author => birthYear is null || OpenLibraryMetadata.YearFromDate(author.BirthDate) == birthYear)
            .ToArray();
        var candidates = authors
            .Where(author => OpenLibraryMetadata.AuthorIdFromKey(author.Key) is not null || !string.IsNullOrWhiteSpace(author.Key))
            .Select(author => AuthorCandidate(author))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Title))
            .ToArray();
        return candidates.Length == 0 ? IdentifyPluginResult.None() : IdentifyPluginResult.ForCandidates(candidates);
    }

    private async Task<EntityMetadataProposal> SeriesProposalAsync(
        string seriesName,
        Guid? targetId,
        string reason,
        bool includeStructuralChildren,
        bool includeRelationshipDetails) {
        if (!includeStructuralChildren) {
            var shellPatch = new EntityMetadataPatch(
                seriesName,
                null,
                new Dictionary<string, string> {
                    [OpenLibraryMetadata.PrimaryIdentityNamespace] = $"series:{seriesName}",
                    [OpenLibraryMetadata.SeriesKey] = seriesName
                },
                [OpenLibraryMetadata.SearchUrl($"subject:\"series:{seriesName}\"")],
                [],
                null,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, int>(),
                new Dictionary<string, int>(),
                "Book series");
            return new EntityMetadataProposal(
                $"openlibrary:series:{OpenLibraryMetadata.Normalize(seriesName).Replace(' ', '-')}",
                OpenLibraryMetadata.PluginId,
                "book",
                0.9m,
                reason,
                shellPatch,
                [],
                [],
                [],
                targetId,
                []);
        }

        var docs = ((await _client.SearchSeriesAsync(seriesName, 50))?.Docs ?? [])
            .Where(doc => OpenLibraryMetadata.WorkIdFromKey(doc.Key) is not null)
            .OrderBy(doc => doc.FirstPublishYear ?? int.MaxValue)
            .ThenBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var authors = docs
            .SelectMany(doc => AuthorNames(doc).Select((name, index) => new { name, key = doc.AuthorKey?.ElementAtOrDefault(index) }))
            .GroupBy(row => row.name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var credits = authors
            .Select((author, index) => new CreditPatch(author.name, "author", null, index))
            .ToArray();
        var relationships = new List<EntityMetadataProposal>();
        foreach (var author in authors.Take(5)) {
            if (OpenLibraryMetadata.AuthorIdFromKey(author.key) is { } id) {
                relationships.Add(await AuthorRelationshipAsync(id, author.name, includeRelationshipDetails));
            }
        }

        var children = docs
            .Select((doc, index) => SeriesBookShell(doc, seriesName, index + 1))
            .ToArray();
        var cover = docs.FirstOrDefault(doc => doc.CoverId is not null)?.CoverId;
        var stats = children.Length > 0
            ? new Dictionary<string, int> { ["bookCount"] = children.Length }
            : new Dictionary<string, int>();
        var externalIds = new Dictionary<string, string> {
            [OpenLibraryMetadata.PrimaryIdentityNamespace] = $"series:{seriesName}",
            [OpenLibraryMetadata.SeriesKey] = seriesName
        };
        var patch = new EntityMetadataPatch(
            seriesName,
            SeriesDescription(seriesName, docs),
            externalIds,
            [OpenLibraryMetadata.SearchUrl($"subject:\"series:{seriesName}\"")],
            OpenLibraryMetadata.Tags(docs.SelectMany(doc => doc.Subjects ?? []), [], [], [], seriesName, null),
            null,
            credits,
            SeriesDates(docs),
            stats,
            new Dictionary<string, int>(),
            "Book series");
        var images = cover is int coverId
            ? new[] { new ImageCandidate("cover", OpenLibraryMetadata.CoverUrl(coverId), "Open Library series cover", 10, null, null, null) }
            : [];

        return new EntityMetadataProposal(
            $"openlibrary:series:{OpenLibraryMetadata.Normalize(seriesName).Replace(' ', '-')}",
            OpenLibraryMetadata.PluginId,
            "book",
            0.9m,
            reason,
            patch,
            images,
            children,
            [],
            targetId,
            relationships);
    }

    private async Task<EntityMetadataProposal> BookProposalAsync(
        string workId,
        string targetKind,
        Guid? targetId,
        string reason,
        OpenLibraryEdition? preferredEdition = null,
        bool includeRelationshipDetails = true,
        bool includeStructuralChildren = true) {
        var work = await _client.GetWorkAsync(workId) ?? throw new InvalidOperationException($"Open Library work '{workId}' was not found.");
        var searchDoc = await _client.SearchWorkByIdAsync(workId);
        var editions = (await _client.GetEditionsAsync(workId))?.Entries ?? [];
        var edition = SelectEdition(work, searchDoc, editions, preferredEdition);
        var subjects = (work.Subjects ?? []).Concat(searchDoc?.Subjects ?? []).ToArray();
        var seriesName = OpenLibraryMetadata.SeriesName(subjects.Concat(edition?.Series ?? []));
        var seriesDocs = seriesName is null || !includeStructuralChildren ? [] : (await _client.SearchSeriesAsync(seriesName, 50))?.Docs ?? [];
        var position = OpenLibraryMetadata.SeriesPosition(workId, seriesDocs);
        var title = OpenLibraryMetadata.FirstNonEmpty(work.Title, searchDoc?.Title, edition?.Title, workId)!;
        var authorRefs = AuthorRefs(work, searchDoc).Take(8).ToArray();
        var relationships = new List<EntityMetadataProposal>();
        foreach (var author in authorRefs) {
            relationships.Add(await AuthorRelationshipAsync(author.Id, author.Name, includeRelationshipDetails));
        }

        var patch = new EntityMetadataPatch(
            title,
            BookDescription(work, edition, searchDoc),
            BookExternalIds(workId, edition, searchDoc),
            BookUrls(workId, edition, work),
            OpenLibraryMetadata.Tags(subjects, work.SubjectPlaces ?? [], work.SubjectPeople ?? [], work.SubjectTimes ?? [], seriesName, edition?.PhysicalFormat),
            edition?.Publishers?.FirstOrDefault(),
            BookCredits(authorRefs, edition),
            BookDates(work, searchDoc, edition),
            BookStats(searchDoc, edition),
            BookPositions(position, targetKind, reason),
            seriesName ?? edition?.PhysicalFormat ?? "Book");

        return new EntityMetadataProposal(
            $"openlibrary:work:{workId}:{targetKind}",
            OpenLibraryMetadata.PluginId,
            targetKind,
            reason is "external-id" or "url" or "isbn" ? 1m : 0.85m,
            reason,
            patch,
            BookImages(work, searchDoc, edition),
            [],
            [],
            targetId,
            relationships);
    }

    private async Task<EntityMetadataProposal> AuthorProposalAsync(string authorId, string reason, bool includeStructuralChildren = false) {
        var author = await _client.GetAuthorAsync(authorId) ?? throw new InvalidOperationException($"Open Library author '{authorId}' was not found.");
        var children = includeStructuralChildren ? await AuthorWorkChildrenAsync(authorId) : [];
        return AuthorProposal(authorId, author, reason, children);
    }

    /// <summary>
    /// Enumerates an author's books as structural child proposals so a request can fan each selected work out
    /// into its own acquisition. Mirrors the series-volume children: a rich <c>author_key:</c> search supplies
    /// cover/year/rating fields, results are de-duplicated by title (preferring an edition with a cover) and
    /// ordered newest-first.
    /// </summary>
    // Prolific authors have hundreds of works; page through them (100 at a time) up to a sane ceiling rather
    // than capping at one page, so a request surfaces the full bibliography.
    private const int AuthorWorksPageSize = 100;
    private const int AuthorWorksMax = 500;

    private async Task<IReadOnlyList<EntityMetadataProposal>> AuthorWorkChildrenAsync(string authorId) {
        var collected = new List<OpenLibrarySearchDoc>();
        for (var offset = 0; offset < AuthorWorksMax; offset += AuthorWorksPageSize) {
            var page = await _client.SearchWorksByAuthorAsync(authorId, AuthorWorksPageSize, offset);
            var pageDocs = page?.Docs ?? [];
            collected.AddRange(pageDocs);
            // Stop once this page didn't fill (last page) or we've reached the reported total.
            if (pageDocs.Length < AuthorWorksPageSize || offset + pageDocs.Length >= (page?.NumFound ?? 0)) {
                break;
            }
        }

        var docs = collected
            .Where(doc => OpenLibraryMetadata.WorkIdFromKey(doc.Key) is not null && !string.IsNullOrWhiteSpace(doc.Title))
            .GroupBy(doc => OpenLibraryMetadata.Normalize(doc.Title))
            .Select(group => group.OrderByDescending(doc => doc.CoverId is not null).First())
            .OrderByDescending(doc => doc.FirstPublishYear ?? int.MinValue)
            .ThenBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return docs.Select(AuthorBookShell).ToArray();
    }

    /// <summary>Builds a requestable book child proposal from an author's-works search doc (no series context).</summary>
    private static EntityMetadataProposal AuthorBookShell(OpenLibrarySearchDoc doc) {
        var workId = OpenLibraryMetadata.WorkIdFromKey(doc.Key)!;
        var stats = new Dictionary<string, int>();
        if (doc.NumberOfPagesMedian is int pages) stats["pageCount"] = pages;
        var dates = new Dictionary<string, string>();
        if (doc.FirstPublishYear is int year) dates["published"] = year.ToString();
        var patch = new EntityMetadataPatch(
            doc.Title ?? workId,
            CandidateOverview(doc),
            WorkExternalIds(doc),
            [OpenLibraryMetadata.WorkUrl(workId)],
            OpenLibraryMetadata.Tags(doc.Subjects ?? [], [], [], [], null, null),
            doc.Publishers?.FirstOrDefault(),
            AuthorNames(doc).Select((name, index) => new CreditPatch(name, "author", null, index)).ToArray(),
            dates,
            stats,
            new Dictionary<string, int>(),
            "Book");
        var images = doc.CoverId is int coverId
            ? new[] { new ImageCandidate("cover", OpenLibraryMetadata.CoverUrl(coverId), "Open Library work cover", 10, null, null, null) }
            : [];
        return new EntityMetadataProposal(
            $"openlibrary:author-work:{workId}",
            OpenLibraryMetadata.PluginId,
            "book",
            0.8m,
            "author-works",
            patch,
            images,
            [],
            []);
    }

    private async Task<EntityMetadataProposal> AuthorRelationshipAsync(string authorId, string fallbackName, bool includeDetails) {
        if (includeDetails) {
            try {
                return await AuthorProposalAsync(authorId, "cascade");
            } catch {
            }
        }

        var patch = new EntityMetadataPatch(
            fallbackName,
            null,
            new Dictionary<string, string> {
                [OpenLibraryMetadata.PrimaryIdentityNamespace] = authorId,
                [OpenLibraryMetadata.AuthorIdKey] = authorId
            },
            [OpenLibraryMetadata.AuthorUrl(authorId)],
            [],
            null,
            [],
            new Dictionary<string, string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            "Author");
        return new EntityMetadataProposal(
            $"openlibrary:author:{authorId}",
            OpenLibraryMetadata.PluginId,
            "person",
            null,
            "cascade",
            patch,
            [],
            [],
            []);
    }

    private static EntityMetadataProposal AuthorProposal(string authorId, OpenLibraryAuthor author, string reason, IReadOnlyList<EntityMetadataProposal>? children = null) {
        var name = OpenLibraryMetadata.FirstNonEmpty(author.Name, author.PersonalName, author.Title, authorId)!;
        var dates = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(author.BirthDate)) dates["birth"] = author.BirthDate!;
        if (!string.IsNullOrWhiteSpace(author.DeathDate)) dates["death"] = author.DeathDate!;

        var externalIds = new Dictionary<string, string> {
            [OpenLibraryMetadata.PrimaryIdentityNamespace] = authorId,
            [OpenLibraryMetadata.AuthorIdKey] = authorId
        };
        foreach (var (key, value) in author.RemoteIds ?? []) {
            if (!string.IsNullOrWhiteSpace(value)) externalIds.TryAdd(key, value);
        }

        var urls = new List<string> { OpenLibraryMetadata.AuthorUrl(authorId) };
        urls.AddRange((author.Links ?? []).Select(link => link.Url).Where(url => !string.IsNullOrWhiteSpace(url))!);

        var patch = new EntityMetadataPatch(
            name,
            OpenLibraryMetadata.JsonText(author.Bio),
            externalIds,
            urls.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray(),
            [],
            null,
            [],
            dates,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            "Author");
        var images = (author.Photos ?? [])
            .Where(id => id > 0)
            .Distinct()
            .Select((id, index) => new ImageCandidate("poster", OpenLibraryMetadata.AuthorPhotoUrl(id), "Open Library author photo", 10 - index, null, null, null))
            .Take(5)
            .ToArray();
        return new EntityMetadataProposal(
            $"openlibrary:author:{authorId}",
            OpenLibraryMetadata.PluginId,
            "person",
            reason is "external-id" or "url" ? 1m : 0.8m,
            reason,
            patch,
            images,
            children ?? [],
            []);
    }

    private static IEnumerable<EntitySearchCandidate> WorkCandidates(IEnumerable<OpenLibrarySearchDoc> docs, string query) =>
        docs
            .Where(doc => OpenLibraryMetadata.WorkIdFromKey(doc.Key) is not null)
            .Select(doc => new EntitySearchCandidate(
                WorkExternalIds(doc),
                doc.Title ?? OpenLibraryMetadata.WorkIdFromKey(doc.Key)!,
                doc.FirstPublishYear,
                CandidateOverview(doc),
                doc.CoverId is int coverId ? OpenLibraryMetadata.CoverUrl(coverId) : null,
                doc.RatingsAverage,
                CandidateId: $"openlibrary:work:{OpenLibraryMetadata.WorkIdFromKey(doc.Key)}",
                Source: "Open Library",
                Confidence: CandidateConfidence(query, doc.Title),
                MatchReason: "title-search"));

    private static IEnumerable<EntitySearchCandidate> SeriesCandidates(
        IReadOnlyList<OpenLibrarySearchDoc> docs,
        string query,
        bool includeImpliedVolumeSeries) {
        var normalizedQuery = OpenLibraryMetadata.Normalize(query);
        if (normalizedQuery.Length == 0) yield break;

        foreach (var group in docs
            .SelectMany(doc => (doc.Subjects ?? [])
                .Select(subject => OpenLibraryMetadata.SeriesName([subject]))
                .Where(series => !string.IsNullOrWhiteSpace(series))
                .Select(series => new SeriesSearchEvidence(series!, doc)))
            .GroupBy(evidence => evidence.Series, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())) {
            var series = group.Key;
            var matchedDocs = group.Select(evidence => evidence.Doc).ToArray();
            var normalizedSeries = OpenLibraryMetadata.Normalize(series);
            var queryMatchesSeries = SeriesMatchesQuery(normalizedSeries, normalizedQuery);
            if (!queryMatchesSeries && !(includeImpliedVolumeSeries && matchedDocs.Any(doc => StrongTitleMatch(query, doc)))) {
                continue;
            }

            yield return new EntitySearchCandidate(
                new Dictionary<string, string> {
                    [OpenLibraryMetadata.PrimaryIdentityNamespace] = $"series:{series}",
                    [OpenLibraryMetadata.SeriesKey] = series
                },
                series,
                matchedDocs.Select(doc => doc.FirstPublishYear).Where(year => year is not null).Min(),
                $"Book series with {matchedDocs.Length} matched Open Library works.",
                matchedDocs.FirstOrDefault(doc => doc.CoverId is not null)?.CoverId is int coverId ? OpenLibraryMetadata.CoverUrl(coverId) : null,
                null,
                CandidateId: $"openlibrary:series:{OpenLibraryMetadata.Normalize(series).Replace(' ', '-')}",
                Source: "Open Library",
                Confidence: SeriesCandidateConfidence(normalizedSeries, normalizedQuery, queryMatchesSeries),
                MatchReason: "series-subject");
        }
    }

    private static bool PreferImpliedSeries(IdentifyPluginRequest request, string targetKind) =>
        targetKind.Equals("book", StringComparison.OrdinalIgnoreCase) &&
        IsLikelySeriesContainer(request);

    private static bool PreferSeriesCandidates(
        IdentifyPluginRequest request,
        string targetKind,
        IReadOnlyList<EntitySearchCandidate> seriesCandidates) {
        if (!targetKind.Equals("book", StringComparison.OrdinalIgnoreCase) || seriesCandidates.Count == 0) {
            return false;
        }

        if (IsLikelySeriesContainer(request)) {
            return true;
        }

        var normalizedQuery = OpenLibraryMetadata.Normalize(QueryTitle(request));
        return seriesCandidates.Any(candidate =>
            OpenLibraryMetadata.Normalize(candidate.Title).Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLikelySeriesContainer(IdentifyPluginRequest request) =>
        !string.IsNullOrWhiteSpace(request.Hints.FilePath) &&
        Directory.Exists(request.Hints.FilePath);

    private static bool SeriesMatchesQuery(string normalizedSeries, string normalizedQuery) =>
        normalizedSeries.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
        normalizedQuery.Contains(normalizedSeries, StringComparison.OrdinalIgnoreCase);

    private static bool StrongTitleMatch(string query, OpenLibrarySearchDoc doc) =>
        CandidateConfidence(query, doc.Title) is decimal confidence && confidence >= 0.82m;

    private static decimal SeriesCandidateConfidence(string normalizedSeries, string normalizedQuery, bool queryMatchesSeries) {
        if (normalizedSeries == normalizedQuery) return 0.95m;
        return queryMatchesSeries ? 0.82m : 0.88m;
    }

    private static EntitySearchCandidate AuthorCandidate(OpenLibraryAuthorSearchDoc author) {
        var id = OpenLibraryMetadata.AuthorIdFromKey(author.Key) ?? author.Key?.Trim() ?? string.Empty;
        var overviewParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(author.TopWork)) overviewParts.Add($"Known for {author.TopWork}.");
        if (author.WorkCount is int count) overviewParts.Add($"{count} works in Open Library.");
        var externalIds = new Dictionary<string, string> {
            [OpenLibraryMetadata.PrimaryIdentityNamespace] = id,
            [OpenLibraryMetadata.AuthorIdKey] = id
        };
        return new EntitySearchCandidate(
            externalIds,
            author.Name ?? id,
            OpenLibraryMetadata.YearFromDate(author.BirthDate),
            overviewParts.Count == 0 ? null : string.Join(' ', overviewParts),
            // Portrait addressed by OLID so the search thumbnail shows the author without a per-result fetch;
            // a photo-less author 404s and the client falls back to its placeholder.
            string.IsNullOrWhiteSpace(id) ? null : OpenLibraryMetadata.AuthorPhotoUrlByOlid(id),
            author.RatingsAverage,
            CandidateId: $"openlibrary:author:{id}",
            Source: "Open Library",
            Confidence: null,
            MatchReason: "author-search");
    }

    private async Task<WorkLookup?> ResolveWorkLookupAsync(IdentifyPluginRequest request) {
        if (ResolveWorkId(request) is { } workId) return new WorkLookup(workId, "external-id", null);
        if (ResolveEditionId(request) is { } editionId) {
            var edition = await _client.GetEditionAsync(editionId);
            var editionWorkId = edition?.Works?.Select(work => OpenLibraryMetadata.WorkIdFromKey(work.Key)).FirstOrDefault(id => id is not null);
            if (editionWorkId is not null) return new WorkLookup(editionWorkId, "edition-id", edition);
        }

        if (ResolveIsbn(request) is { } isbn) {
            var edition = await _client.GetEditionByIsbnAsync(isbn);
            var editionWorkId = edition?.Works?.Select(work => OpenLibraryMetadata.WorkIdFromKey(work.Key)).FirstOrDefault(id => id is not null);
            if (editionWorkId is not null) return new WorkLookup(editionWorkId, "isbn", edition);
        }

        return null;
    }

    private static OpenLibraryEdition? SelectEdition(
        OpenLibraryWork work,
        OpenLibrarySearchDoc? searchDoc,
        IReadOnlyList<OpenLibraryEdition> editions,
        OpenLibraryEdition? preferredEdition) {
        if (preferredEdition is not null) return preferredEdition;
        var workTitle = OpenLibraryMetadata.Normalize(OpenLibraryMetadata.FirstNonEmpty(work.Title, searchDoc?.Title));
        return editions
            .Where(edition => !string.IsNullOrWhiteSpace(edition.Title))
            .OrderByDescending(edition => EditionScore(edition, workTitle))
            .FirstOrDefault();
    }

    private static int EditionScore(OpenLibraryEdition edition, string workTitle) {
        var title = OpenLibraryMetadata.Normalize(edition.Title);
        var score = 0;
        if (title == workTitle) score += 30;
        if (title.Contains(workTitle, StringComparison.OrdinalIgnoreCase) || workTitle.Contains(title, StringComparison.OrdinalIgnoreCase)) score += 10;
        if ((edition.Languages ?? []).Any(language => language.Key?.EndsWith("/eng", StringComparison.OrdinalIgnoreCase) == true)) score += 25;
        if (edition.Covers is { Length: > 0 }) score += 8;
        if (edition.NumberOfPages is > 0) score += 6;
        if (edition.Isbn13 is { Length: > 0 }) score += 5;
        if (edition.Publishers is { Length: > 0 }) score += 4;
        if (edition.PublishDate is not null && OpenLibraryMetadata.YearFromDate(edition.PublishDate) is not null) score += 2;
        return score;
    }

    private async Task<EntityMetadataProposal?> SeriesChildProposalAsync(IdentifyPluginRequest request, string targetKind) {
        if (IsExplicitSearch(request) || ResolveAncestorSeriesName(request) is not { } seriesName) {
            return null;
        }

        var docs = (await _client.SearchSeriesAsync(seriesName, 50))?.Docs ?? [];
        var match = MatchSeriesChild(docs, request);
        if (match is null || OpenLibraryMetadata.WorkIdFromKey(match.Key) is not { } workId) {
            return null;
        }

        return await BookProposalAsync(
            workId,
            targetKind,
            request.Entity.Id,
            "series-context",
            includeRelationshipDetails: request.IncludeRelationshipDetails,
            includeStructuralChildren: request.IncludeStructuralChildren);
    }

    private static EntityMetadataProposal SeriesBookShell(OpenLibrarySearchDoc doc, string seriesName, int position) {
        var workId = OpenLibraryMetadata.WorkIdFromKey(doc.Key)!;
        var stats = new Dictionary<string, int>();
        if (doc.NumberOfPagesMedian is int pages) stats["pageCount"] = pages;
        var dates = new Dictionary<string, string>();
        if (doc.FirstPublishYear is int year) dates["published"] = year.ToString();
        var patch = new EntityMetadataPatch(
            doc.Title ?? workId,
            CandidateOverview(doc),
            WorkExternalIds(doc, seriesName),
            [OpenLibraryMetadata.WorkUrl(workId)],
            OpenLibraryMetadata.Tags(doc.Subjects ?? [], [], [], [], seriesName, null),
            doc.Publishers?.FirstOrDefault(),
            AuthorNames(doc).Select((name, index) => new CreditPatch(name, "author", null, index)).ToArray(),
            dates,
            stats,
            new Dictionary<string, int> {
                ["seriesNumber"] = position,
                ["volumeNumber"] = position,
                ["sortOrder"] = position - 1
            },
            seriesName);
        var images = doc.CoverId is int coverId
            ? new[] { new ImageCandidate("cover", OpenLibraryMetadata.CoverUrl(coverId), "Open Library work cover", 10, null, null, null) }
            : [];
        return new EntityMetadataProposal(
            $"openlibrary:series:{OpenLibraryMetadata.Normalize(seriesName).Replace(' ', '-')}:work:{workId}",
            OpenLibraryMetadata.PluginId,
            "book",
            0.8m,
            "series-subject",
            patch,
            images,
            [],
            []);
    }

    private static OpenLibrarySearchDoc? MatchSeriesChild(IReadOnlyList<OpenLibrarySearchDoc> docs, IdentifyPluginRequest request) {
        var title = OpenLibraryMetadata.Normalize(QueryTitle(request) ?? request.Entity.Title);
        if (title.Length > 0) {
            var titleMatch = docs.FirstOrDefault(doc => {
                var candidate = OpenLibraryMetadata.Normalize(doc.Title);
                return candidate == title || candidate.Contains(title, StringComparison.OrdinalIgnoreCase) || title.Contains(candidate, StringComparison.OrdinalIgnoreCase);
            });
            if (titleMatch is not null) return titleMatch;
        }

        var position = PositionValue(request, "volumeNumber", "volume", "sortOrder");
        if (position is null) return null;
        var index = request.StructuralContext?.Positions.ContainsKey("sortOrder") == true ? position.Value : position.Value - 1;
        return index >= 0 && index < docs.Count ? docs[index] : null;
    }

    private static IReadOnlyList<AuthorRef> AuthorRefs(OpenLibraryWork work, OpenLibrarySearchDoc? doc) {
        var refs = new List<AuthorRef>();
        var ids = (work.Authors ?? []).Select(author => OpenLibraryMetadata.AuthorIdFromKey(author.Author?.Key)).Where(id => id is not null).Select(id => id!).ToArray();
        var names = doc?.AuthorName ?? [];
        for (var i = 0; i < Math.Max(ids.Length, names.Length); i++) {
            var id = i < ids.Length ? ids[i] : null;
            var name = i < names.Length ? names[i] : id;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) {
                refs.Add(new AuthorRef(id!, name!));
            }
        }

        return refs
            .GroupBy(author => author.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<CreditPatch> BookCredits(IReadOnlyList<AuthorRef> authors, OpenLibraryEdition? edition) {
        var credits = authors
            .Select((author, index) => new CreditPatch(author.Name, "author", null, index))
            .ToList();
        credits.AddRange((edition?.Contributors ?? [])
            .Where(contributor => !string.IsNullOrWhiteSpace(contributor.Name))
            .Select((contributor, index) => new CreditPatch(
                contributor.Name!.Trim(),
                string.IsNullOrWhiteSpace(contributor.Role) ? "contributor" : contributor.Role!.Trim().ToLowerInvariant(),
                null,
                100 + index)));
        return credits;
    }

    private static string? BookDescription(OpenLibraryWork work, OpenLibraryEdition? edition, OpenLibrarySearchDoc? searchDoc) =>
        OpenLibraryMetadata.FirstNonEmpty(
            OpenLibraryMetadata.JsonText(work.Description),
            OpenLibraryMetadata.JsonText(edition?.Description),
            searchDoc?.FirstSentence?.FirstOrDefault());

    private static IReadOnlyDictionary<string, string> BookExternalIds(string workId, OpenLibraryEdition? edition, OpenLibrarySearchDoc? searchDoc) {
        var ids = new Dictionary<string, string> {
            [OpenLibraryMetadata.PrimaryIdentityNamespace] = workId,
            [OpenLibraryMetadata.WorkIdKey] = workId
        };
        if (OpenLibraryMetadata.EditionIdFromKey(edition?.Key) is { } editionId) {
            ids[OpenLibraryMetadata.EditionIdKey] = editionId;
        }

        var isbn13 = edition?.Isbn13?.FirstOrDefault() ?? searchDoc?.Isbns?.FirstOrDefault(Isbn13);
        var isbn10 = edition?.Isbn10?.FirstOrDefault() ?? searchDoc?.Isbns?.FirstOrDefault(isbn => !Isbn13(isbn));
        if (!string.IsNullOrWhiteSpace(isbn13)) ids["isbn13"] = isbn13!;
        if (!string.IsNullOrWhiteSpace(isbn10)) ids["isbn10"] = isbn10!;
        return ids;
    }

    private static IReadOnlyList<string> BookUrls(string workId, OpenLibraryEdition? edition, OpenLibraryWork work) {
        var urls = new List<string> { OpenLibraryMetadata.WorkUrl(workId) };
        if (OpenLibraryMetadata.EditionIdFromKey(edition?.Key) is { } editionId) {
            urls.Add(OpenLibraryMetadata.EditionUrl(editionId));
        }

        urls.AddRange((work.Links ?? []).Select(link => link.Url).Where(url => !string.IsNullOrWhiteSpace(url))!);
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
    }

    private static IReadOnlyDictionary<string, string> BookDates(OpenLibraryWork work, OpenLibrarySearchDoc? doc, OpenLibraryEdition? edition) {
        var dates = new Dictionary<string, string>();
        if (doc?.FirstPublishYear is int year) dates["published"] = year.ToString();
        else if (!string.IsNullOrWhiteSpace(work.FirstPublishDate)) dates["published"] = work.FirstPublishDate!;
        if (!string.IsNullOrWhiteSpace(edition?.PublishDate)) dates["editionPublished"] = edition!.PublishDate!;
        return dates;
    }

    private static IReadOnlyDictionary<string, int> BookStats(OpenLibrarySearchDoc? doc, OpenLibraryEdition? edition) {
        var stats = new Dictionary<string, int>();
        if (edition?.NumberOfPages is int pages) stats["pageCount"] = pages;
        else if (doc?.NumberOfPagesMedian is int medianPages) stats["pageCount"] = medianPages;
        if (doc?.RatingsCount is int ratingCount) stats["ratingCount"] = ratingCount;
        return stats;
    }

    private static IReadOnlyDictionary<string, int> BookPositions(int? position, string targetKind, string reason) {
        if (position is null) return new Dictionary<string, int>();
        if (targetKind.Equals("book-volume", StringComparison.OrdinalIgnoreCase) ||
            reason.Equals("series-context", StringComparison.OrdinalIgnoreCase)) {
            return new Dictionary<string, int> {
                ["seriesNumber"] = position.Value,
                ["volumeNumber"] = position.Value,
                ["sortOrder"] = position.Value - 1
            };
        }

        return new Dictionary<string, int> { ["seriesNumber"] = position.Value };
    }

    private static IReadOnlyList<ImageCandidate> BookImages(OpenLibraryWork work, OpenLibrarySearchDoc? doc, OpenLibraryEdition? edition) {
        var coverIds = new List<int>();
        coverIds.AddRange(edition?.Covers ?? []);
        if (doc?.CoverId is int docCover) coverIds.Add(docCover);
        coverIds.AddRange(work.Covers ?? []);
        return coverIds
            .Where(id => id > 0)
            .Distinct()
            .Select((id, index) => new ImageCandidate("cover", OpenLibraryMetadata.CoverUrl(id), "Open Library cover", 10 - index, null, null, null))
            .Take(12)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> WorkExternalIds(OpenLibrarySearchDoc doc, string? seriesName = null) {
        var workId = OpenLibraryMetadata.WorkIdFromKey(doc.Key)!;
        var ids = new Dictionary<string, string> {
            [OpenLibraryMetadata.PrimaryIdentityNamespace] = workId,
            [OpenLibraryMetadata.WorkIdKey] = workId
        };
        if (!string.IsNullOrWhiteSpace(seriesName)) ids[OpenLibraryMetadata.SeriesKey] = seriesName!;
        if (doc.Isbns?.FirstOrDefault(Isbn13) is { } isbn13) ids["isbn13"] = isbn13;
        return ids;
    }

    private static IReadOnlyDictionary<string, string> SeriesDates(IEnumerable<OpenLibrarySearchDoc> docs) {
        var years = docs.Select(doc => doc.FirstPublishYear).Where(year => year is not null).Select(year => year!.Value).Order().ToArray();
        if (years.Length == 0) return new Dictionary<string, string>();
        var dates = new Dictionary<string, string> { ["firstPublished"] = years[0].ToString() };
        if (years[^1] != years[0]) dates["latestPublished"] = years[^1].ToString();
        return dates;
    }

    private static string SeriesDescription(string seriesName, IReadOnlyList<OpenLibrarySearchDoc> docs) {
        var titles = docs
            .Select((doc, index) => $"{index + 1}. {doc.Title}")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(10);
        return $"{seriesName} book series. {string.Join("; ", titles)}.";
    }

    private static string? CandidateOverview(OpenLibrarySearchDoc doc) {
        var parts = new List<string>();
        if (AuthorNames(doc).ToArray() is { Length: > 0 } authors) parts.Add($"By {string.Join(", ", authors)}.");
        if (doc.FirstPublishYear is int year) parts.Add($"First published {year}.");
        if (doc.NumberOfPagesMedian is int pages) parts.Add($"{pages} pages.");
        if (doc.RatingsAverage is decimal rating && doc.RatingsCount is int count) parts.Add($"Open Library rating {rating:0.0} from {count} readers.");
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static IEnumerable<string> AuthorNames(OpenLibrarySearchDoc doc) =>
        (doc.AuthorName ?? [])
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name.Trim());

    private static bool HasSeries(OpenLibrarySearchDoc doc, string seriesName) =>
        (doc.Subjects ?? []).Any(subject =>
            OpenLibraryMetadata.SeriesName([subject])?.Equals(seriesName, StringComparison.OrdinalIgnoreCase) == true);

    private static decimal? CandidateConfidence(string query, string? title) {
        var normalizedQuery = OpenLibraryMetadata.Normalize(query);
        var normalizedTitle = OpenLibraryMetadata.Normalize(title);
        if (normalizedQuery.Length == 0 || normalizedTitle.Length == 0) return null;
        if (normalizedQuery == normalizedTitle) return 0.95m;
        if (normalizedTitle.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            normalizedQuery.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase)) return 0.82m;
        return 0.65m;
    }

    private static int? PositionValue(IdentifyPluginRequest request, params string[] keys) {
        var positions = request.StructuralContext?.Positions ?? new Dictionary<string, int>();
        foreach (var key in keys) {
            if (positions.TryGetValue(key, out var value)) return value;
        }

        return null;
    }

    private static string? ResolveSeriesName(IdentifyPluginRequest request) =>
        SeriesFromIds(request.Query.ExternalIds) ??
        SeriesFromIds(request.Entity.ExternalIds) ??
        SeriesFromIds(request.Hints.ExternalIds);

    private static string? ResolveAncestorSeriesName(IdentifyPluginRequest request) =>
        request.StructuralContext?.Ancestors
            .Select(ancestor => SeriesFromIds(ancestor.ExternalIds))
            .FirstOrDefault(series => !string.IsNullOrWhiteSpace(series));

    private static string? SeriesFromIds(IReadOnlyDictionary<string, string>? ids) =>
        OpenLibraryMetadata.OpenLibraryId(ids, OpenLibraryMetadata.SeriesKey) ??
        OpenLibraryMetadata.SeriesFromProviderId(OpenLibraryMetadata.OpenLibraryId(ids, OpenLibraryMetadata.PrimaryIdentityNamespace));

    private static string? ResolveWorkId(IdentifyPluginRequest request) =>
        WorkIdFromIds(request.Query.ExternalIds) ??
        OpenLibraryMetadata.WorkIdFromUrl(request.Query.Url) ??
        request.Hints.Urls.Select(OpenLibraryMetadata.WorkIdFromUrl).FirstOrDefault(id => id is not null) ??
        WorkIdFromIds(request.Entity.ExternalIds) ??
        WorkIdFromIds(request.Hints.ExternalIds);

    private static string? WorkIdFromIds(IReadOnlyDictionary<string, string>? ids) {
        var value = OpenLibraryMetadata.OpenLibraryId(ids, OpenLibraryMetadata.WorkIdKey, OpenLibraryMetadata.PrimaryIdentityNamespace);
        return OpenLibraryMetadata.WorkIdFromKey(value);
    }

    private static string? ResolveEditionId(IdentifyPluginRequest request) =>
        EditionIdFromIds(request.Query.ExternalIds) ??
        OpenLibraryMetadata.EditionIdFromUrl(request.Query.Url) ??
        request.Hints.Urls.Select(OpenLibraryMetadata.EditionIdFromUrl).FirstOrDefault(id => id is not null) ??
        EditionIdFromIds(request.Entity.ExternalIds) ??
        EditionIdFromIds(request.Hints.ExternalIds);

    private static string? EditionIdFromIds(IReadOnlyDictionary<string, string>? ids) =>
        OpenLibraryMetadata.EditionIdFromKey(OpenLibraryMetadata.OpenLibraryId(ids, OpenLibraryMetadata.EditionIdKey));

    private static string? ResolveIsbn(IdentifyPluginRequest request) =>
        OpenLibraryMetadata.Isbn(request.Query.ExternalIds) ??
        OpenLibraryMetadata.IsbnFromUrl(request.Query.Url) ??
        request.Hints.Urls.Select(OpenLibraryMetadata.IsbnFromUrl).FirstOrDefault(id => id is not null) ??
        OpenLibraryMetadata.Isbn(request.Entity.ExternalIds) ??
        OpenLibraryMetadata.Isbn(request.Hints.ExternalIds);

    private static string? ResolveAuthorId(IdentifyPluginRequest request) =>
        AuthorIdFromIds(request.Query.ExternalIds) ??
        OpenLibraryMetadata.AuthorIdFromUrl(request.Query.Url) ??
        request.Hints.Urls.Select(OpenLibraryMetadata.AuthorIdFromUrl).FirstOrDefault(id => id is not null) ??
        AuthorIdFromIds(request.Entity.ExternalIds) ??
        AuthorIdFromIds(request.Hints.ExternalIds);

    private static string? AuthorIdFromIds(IReadOnlyDictionary<string, string>? ids) {
        var value = OpenLibraryMetadata.OpenLibraryId(ids, OpenLibraryMetadata.AuthorIdKey, OpenLibraryMetadata.PrimaryIdentityNamespace);
        return OpenLibraryMetadata.AuthorIdFromKey(value);
    }

    private static string? QueryTitle(IdentifyPluginRequest request) =>
        OpenLibraryMetadata.FirstNonEmpty(
            SearchField(request, OpenLibraryMetadata.SearchFields.Title, OpenLibraryMetadata.SearchFields.SeriesTitle),
            request.Query.Title,
            request.Hints.Title,
            request.Entity.Title);

    internal static string BuildWorkSearchQuery(IdentifyPluginRequest request, string fallbackTitle) {
        var terms = new List<string>();
        var title = SearchField(request, OpenLibraryMetadata.SearchFields.Title);
        var seriesTitle = SearchField(request, OpenLibraryMetadata.SearchFields.SeriesTitle);
        if (!string.IsNullOrWhiteSpace(title)) terms.Add($"title:{QuoteSearch(title)}");
        else if (string.IsNullOrWhiteSpace(seriesTitle)) terms.Add($"title:{QuoteSearch(fallbackTitle)}");
        if (!string.IsNullOrWhiteSpace(seriesTitle)) terms.Add($"subject:{QuoteSearch($"series:{seriesTitle}")}");
        if (SearchField(request, OpenLibraryMetadata.SearchFields.Author) is { } author) terms.Add($"author:{QuoteSearch(author)}");
        if (SearchField(request, OpenLibraryMetadata.SearchFields.Year) is { } year) terms.Add($"first_publish_year:{QuoteSearch(year)}");
        return string.Join(' ', terms);
    }

    private static string QuoteSearch(string value) => $"\"{value.Replace("\"", string.Empty).Trim()}\"";

    private static string? SearchField(IdentifyPluginRequest request, params string[] keys) {
        foreach (var key in keys) {
            var value = OpenLibraryMetadata.OpenLibraryId(request.Query.Fields, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static bool IsExplicitSearch(IdentifyPluginRequest request) =>
        request.Action.Equals("search", StringComparison.OrdinalIgnoreCase) &&
        (!string.IsNullOrWhiteSpace(request.Query.Title) || request.Query.Fields?.Values.Any(value => !string.IsNullOrWhiteSpace(value)) == true) &&
        string.IsNullOrWhiteSpace(request.Query.Url) &&
        request.Query.ExternalIds is not { Count: > 0 };

    private static bool Isbn13(string isbn) => isbn.Count(char.IsDigit) == 13;

    private sealed record WorkLookup(string WorkId, string MatchReason, OpenLibraryEdition? Edition);
    private sealed record AuthorRef(string Id, string Name);
    private sealed record SeriesSearchEvidence(string Series, OpenLibrarySearchDoc Doc);
}
