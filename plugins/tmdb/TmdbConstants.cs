namespace Prismedia.Plugin.Tmdb;

internal static class TmdbConstants {
    public const string PluginId = "tmdb";
    public const string IdentityNamespace = "tmdb";
    public const string SeasonIdentityNamespace = "tmdbseason";
    public const string EpisodeIdentityNamespace = "tmdbepisode";
    public const string Api = "https://api.themoviedb.org/3";
    public const string Image = "https://image.tmdb.org/t/p";

    internal static class SearchFields {
        public const string Title = "title";
        public const string SeriesTitle = "seriesTitle";
        public const string Year = "year";
    }
}
