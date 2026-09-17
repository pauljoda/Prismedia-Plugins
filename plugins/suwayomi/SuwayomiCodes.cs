namespace Prismedia.Plugin.Suwayomi;

/// <summary>Fixed Suwayomi v2.3.2243 protocol vocabulary and safe adapter bounds.</summary>
internal static class SuwayomiCodes {
    internal const string SupportedVersion = "2.3.2243";
    internal const string ReportedVersion = "v" + SupportedVersion;
    internal const string CbzOffer = "cbz";
    internal const string CbzMediaType = "application/vnd.comicbook+zip";
    internal const string SourceContainer = "source";
    internal const string MangaContainer = "manga";
    internal const string ChapterSelection = "chapter";
    internal const string Search = "SEARCH";
    internal const string Popular = "POPULAR";
    internal const string Queued = "QUEUED";
    internal const string Downloading = "DOWNLOADING";
    internal const string Finished = "FINISHED";
    internal const string Error = "ERROR";
    internal const int MaximumCatalogLimit = 100;
    internal const int MaximumCursorOffset = 10_000;
    internal const long MaximumArtifactBytes = 2L * 1024 * 1024 * 1024;
}
