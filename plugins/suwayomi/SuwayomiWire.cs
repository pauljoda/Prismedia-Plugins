using System.Text.Json.Serialization;

namespace Prismedia.Plugin.Suwayomi;

// prism-vocab: external — the single decode boundary for Suwayomi GraphQL response fields.
internal sealed record GraphQlEnvelope<T>(T? Data, IReadOnlyList<GraphQlError>? Errors) where T : class;
internal sealed record GraphQlError(string? Message);
internal sealed record AboutData(AboutServer AboutServer);
internal sealed record AboutServer(string Name, string Version);
internal sealed record SourcesData(SuwayomiNodePage<SuwayomiSource> Sources);
internal sealed record SourceData(SuwayomiSource? Source);
internal sealed record MangasData(SuwayomiNodePage<SuwayomiManga> Mangas);
internal sealed record ChaptersData(SuwayomiNodePage<SuwayomiChapter> Chapters);
internal sealed record SearchMangaData(SearchMangaPayload FetchSourceManga);
internal sealed record SearchMangaPayload(IReadOnlyList<SuwayomiManga> Mangas, bool HasNextPage);
internal sealed record FetchMangaData(FetchMangaPayload FetchMangaAndChapters);
internal sealed record FetchMangaPayload(SuwayomiManga Manga, IReadOnlyList<SuwayomiChapter> Chapters);
internal sealed record ExactData(SuwayomiSource Source, SuwayomiManga Manga, SuwayomiChapter Chapter, SuwayomiDownloadStatus DownloadStatus);
internal sealed record EnqueueData(EnqueuePayload EnqueueChapterDownload);
internal sealed record EnqueuePayload(string? ClientMutationId, SuwayomiDownloadStatus DownloadStatus);
internal sealed record SuwayomiNodePage<T>(IReadOnlyList<T> Nodes, int TotalCount);
internal sealed record SuwayomiSource(string Id, string Name, string Lang, string ContentWarning);
internal sealed record SuwayomiManga(int Id, string SourceId, string Url, string Title, string? Author, string? Artist,
    string? Description, bool Initialized, bool InLibrary, string? ChaptersLastFetchedAt);
internal sealed record SuwayomiChapter(int Id, string Url, string Name, string UploadDate, float ChapterNumber, string? Scanlator,
    int MangaId, int SourceOrder, string? RealUrl, bool IsDownloaded, int PageCount);
internal sealed record SuwayomiDownloadStatus(IReadOnlyList<SuwayomiDownload> Queue);
internal sealed record SuwayomiDownload(string State, float Progress, int Tries, int Position, SuwayomiChapter Chapter);

internal sealed record SourceLocator(string Kind, Guid ConnectionId, string SourceId, string SourceName, string SourceLanguage);
internal sealed record MangaLocator(string Kind, Guid ConnectionId, string SourceId, string SourceName, string SourceLanguage,
    int MangaId, string MangaUrl, string MangaTitle);
internal sealed record ChapterLocator(string Kind, Guid ConnectionId, string SourceId, string SourceName, string SourceLanguage,
    int MangaId, string MangaUrl, string MangaTitle, int ChapterId, string ChapterUrl, int SourceOrder,
    string ChapterName, float ChapterNumber, string? Scanlator);
internal sealed record SourceCursor(Guid ConnectionId, int Offset);
internal sealed record MangaCursor(Guid ConnectionId, string SourceId, string SourceName, string SourceLanguage,
    string Mode, string? Query, int Page, int Offset, int Limit);
internal sealed record ChapterCursor(Guid ConnectionId, string SourceId, int MangaId, int Offset, int Limit);
