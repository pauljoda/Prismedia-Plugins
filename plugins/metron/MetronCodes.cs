namespace Prismedia.Plugin.Metron;

/// <summary>Host and upstream protocol vocabulary owned by the Metron adapter.</summary>
internal static class MetronCodes {
    #region Static Variables
    public const string Provider = "metron";
    public const string SeriesKind = "comic-series";
    public const string IssueKind = "comic-installment";
    public const string SeriesIdentity = "metronseries";
    public const string IssueIdentity = "metronissue";
    public const string ComicVineIdentity = "comicvine";
    public const string GcdSeriesIdentity = "gcdseries";
    public const string GcdIssueIdentity = "gcdissue";
    public const string Search = "search";
    public const string LookupId = "lookup-id";
    public const string LookupUrl = "lookup-url";
    public const string Token = "apiToken";
    public const string SeriesTitle = "seriesTitle";
    public const string IssueNumber = "issueNumber";
    public const string Year = "year";
    public const string Volume = "volume";
    public const string Language = "language";
    public const string Publisher = "publisher";
    public const string ChapterPosition = "chapter";
    public const string PublicationDate = "publication";
    public const string PageCount = "pageCount";
    public const string IssueCount = "issueCount";
    public const string Cover = "cover";
    public const string WriterRole = "writer";
    public const string ArtistRole = "artist";
    public const string PersonRole = "person";
    // prism-vocab: external Metron resource paths, query keys, and rate-limit headers.
    public const string SeriesPath = "series/";
    public const string IssuePath = "issue/";
    public const string IssueListPath = "issue_list/";
    public const string PageQuery = "page";
    public const string SeriesNameQuery = "series_name";
    public const string NameQuery = "name";
    public const string NumberQuery = "number";
    public const string SeriesYearQuery = "series_year_began";
    public const string YearQuery = "year_began";
    public const string SeriesVolumeQuery = "series_volume";
    public const string PublisherQuery = "publisher_name";
    public const string BurstLimit = "X-RateLimit-Burst-Limit";
    public const string BurstRemaining = "X-RateLimit-Burst-Remaining";
    public const string SustainedRemaining = "X-RateLimit-Sustained-Remaining";
    public const string BurstReset = "X-RateLimit-Burst-Reset";
    public const string SustainedReset = "X-RateLimit-Sustained-Reset";
    #endregion
}
