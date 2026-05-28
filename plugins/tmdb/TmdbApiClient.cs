using System.Net.Http.Json;

namespace Prismedia.Plugin.Tmdb;

internal sealed class TmdbApiClient {
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public TmdbApiClient(HttpClient http, string apiKey) {
        _http = http;
        _apiKey = apiKey;
    }

    public async Task<TmdbMovieDetail> GetMovieAsync(int id) =>
        await FetchAsync<TmdbMovieDetail>($"/movie/{id}", new Dictionary<string, string> {
            ["append_to_response"] = "credits,images",
            ["include_image_language"] = "en,null"
        });

    public async Task<TmdbTvDetail> GetTvAsync(int id) =>
        await FetchAsync<TmdbTvDetail>($"/tv/{id}", new Dictionary<string, string> {
            ["append_to_response"] = "credits,images",
            ["include_image_language"] = "en,null"
        });

    public async Task<TmdbSeasonDetail> GetSeasonAsync(int seriesId, int seasonNumber) =>
        await FetchAsync<TmdbSeasonDetail>($"/tv/{seriesId}/season/{seasonNumber}", new Dictionary<string, string> {
            ["append_to_response"] = "images",
            ["include_image_language"] = "en,null"
        });

    public async Task<TmdbEpisode> GetEpisodeAsync(int seriesId, int seasonNumber, int episodeNumber) =>
        await FetchAsync<TmdbEpisode>($"/tv/{seriesId}/season/{seasonNumber}/episode/{episodeNumber}", new Dictionary<string, string> {
            ["append_to_response"] = "credits,images",
            ["include_image_language"] = "en,null"
        });

    public async Task<TmdbPersonDetail> GetPersonAsync(int id) =>
        await FetchAsync<TmdbPersonDetail>($"/person/{id}", new Dictionary<string, string> {
            ["append_to_response"] = "images",
            ["include_image_language"] = "en,null"
        });

    public async Task<TmdbCompanyDetail> GetCompanyAsync(int id) =>
        await FetchAsync<TmdbCompanyDetail>($"/company/{id}", new Dictionary<string, string> {
            ["append_to_response"] = "images",
            ["include_image_language"] = "en,null"
        });

    public async Task<T> FetchAsync<T>(string path, IReadOnlyDictionary<string, string>? parameters = null) {
        var query = new Dictionary<string, string> {
            ["api_key"] = _apiKey
        };
        foreach (var (key, value) in parameters ?? new Dictionary<string, string>()) {
            query[key] = value;
        }

        var queryString = string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return await _http.GetFromJsonAsync<T>($"{TmdbConstants.Api}{path}?{queryString}") ??
            throw new InvalidOperationException($"TMDB returned an empty response for {path}.");
    }
}
