namespace Prismedia.Plugin.Tmdb;

internal static class TmdbAuth {
    public static string ReadApiKey(IReadOnlyDictionary<string, string> auth) {
        if (auth.TryGetValue("apiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey)) {
            return apiKey;
        }

        if (auth.TryGetValue("TMDB_API_KEY", out var legacyKey) && !string.IsNullOrWhiteSpace(legacyKey)) {
            return legacyKey;
        }

        var env = Environment.GetEnvironmentVariable("TMDB_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) {
            return env;
        }

        throw new InvalidOperationException("TMDB API key is required.");
    }
}
