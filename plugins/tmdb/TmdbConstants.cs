namespace Prismedia.Plugin.Tmdb;

internal static class TmdbConstants {
    public const string PluginId = "tmdb";
    public const string IdentityNamespace = "tmdb";
    public const string SeasonIdentityNamespace = "tmdbseason";
    public const string EpisodeIdentityNamespace = "tmdbepisode";
    public const string Api = "https://api.themoviedb.org/3";
    public const string Image = "https://image.tmdb.org/t/p";

    internal static class DateTypes {
        public const string Release = "release";
        public const string Premiere = "premiere";
        public const string TheatricalRelease = "theatrical-release";
        public const string DigitalRelease = "digital-release";
        public const string PhysicalRelease = "physical-release";
        public const string Air = "air";
    }

    internal static class ReleaseRegions {
        public const string UnitedStates = "US";
    }

    internal static class MovieReleaseTypes {
        public const int Premiere = 1;
        public const int LimitedTheatrical = 2;
        public const int Theatrical = 3;
        public const int Digital = 4;
        public const int Physical = 5;
        public const int Television = 6;
    }

    internal static class SearchFields {
        public const string Title = "title";
        public const string SeriesTitle = "seriesTitle";
        public const string Year = "year";
    }
}
