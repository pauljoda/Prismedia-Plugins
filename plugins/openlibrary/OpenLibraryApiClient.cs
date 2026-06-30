using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Prismedia.Plugin.OpenLibrary;

internal sealed class OpenLibraryApiClient {
    private const string BaseUrl = "https://openlibrary.org";
    private const int MaxRetries = 3;
    private static readonly string RateLimitPath = Path.Combine(Path.GetTempPath(), "prismedia-openlibrary.ratelimit");
    private static readonly TimeSpan DefaultMinRequestInterval = TimeSpan.FromMilliseconds(1100);
    private static readonly string WorkFields = string.Join(',', [
        "key",
        "title",
        "author_name",
        "author_key",
        "first_publish_year",
        "cover_i",
        "number_of_pages_median",
        "ratings_average",
        "ratings_count",
        "subject",
        "first_sentence",
        "isbn",
        "publisher",
        "publish_date",
        "edition_key"
    ]);

    private readonly HttpClient _http;
    private readonly TimeSpan _minRequestInterval;

    public OpenLibraryApiClient(HttpClient http, TimeSpan? minRequestInterval = null) {
        _http = http;
        _minRequestInterval = minRequestInterval ?? DefaultMinRequestInterval;
        _http.BaseAddress ??= new Uri(BaseUrl);
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Prismedia-OpenLibrary-Plugin/0.1 (https://github.com/pauljoda/Prismedia-Plugins)");
        }
    }

    public Task<OpenLibrarySearchResponse?> SearchWorksAsync(string query, int limit) =>
        GetJsonAsync<OpenLibrarySearchResponse>($"/search.json?q={Escape(query)}&limit={limit}&fields={WorkFields}");

    public async Task<OpenLibrarySearchDoc?> SearchWorkByIdAsync(string workId) =>
        (await GetJsonAsync<OpenLibrarySearchResponse>($"/search.json?q={Escape($"key:/works/{workId}")}&limit=1&fields={WorkFields}"))
        ?.Docs?.FirstOrDefault();

    public Task<OpenLibrarySearchResponse?> SearchSeriesAsync(string seriesName, int limit) =>
        GetJsonAsync<OpenLibrarySearchResponse>($"/search.json?subject={Escape($"series:{seriesName}")}&sort=old&limit={limit}&fields={WorkFields}");

    public Task<OpenLibrarySearchResponse?> SearchWorksByAuthorAsync(string authorId, int limit, int offset = 0) =>
        GetJsonAsync<OpenLibrarySearchResponse>($"/search.json?q={Escape($"author_key:{authorId}")}&sort=new&limit={limit}&offset={offset}&fields={WorkFields}");

    public Task<OpenLibraryAuthorSearchResponse?> SearchAuthorsAsync(string query, int limit) =>
        GetJsonAsync<OpenLibraryAuthorSearchResponse>($"/search/authors.json?q={Escape(query)}&limit={limit}");

    public Task<OpenLibraryWork?> GetWorkAsync(string workId) =>
        GetJsonAsync<OpenLibraryWork>($"/works/{EscapeSegment(workId)}.json");

    public Task<OpenLibraryEditionResponse?> GetEditionsAsync(string workId, int limit = 50) =>
        GetJsonAsync<OpenLibraryEditionResponse>($"/works/{EscapeSegment(workId)}/editions.json?limit={limit}");

    public Task<OpenLibraryEdition?> GetEditionAsync(string editionId) =>
        GetJsonAsync<OpenLibraryEdition>($"/books/{EscapeSegment(editionId)}.json");

    public Task<OpenLibraryEdition?> GetEditionByIsbnAsync(string isbn) =>
        GetJsonAsync<OpenLibraryEdition>($"/isbn/{EscapeSegment(isbn)}.json");

    public Task<OpenLibraryAuthor?> GetAuthorAsync(string authorId) =>
        GetJsonAsync<OpenLibraryAuthor>($"/authors/{EscapeSegment(authorId)}.json");

    private async Task<T?> GetJsonAsync<T>(string path) {
        for (var attempt = 0; ; attempt++) {
            await ThrottleAsync();
            try {
                using var response = await _http.GetAsync(path);
                if (response.StatusCode == HttpStatusCode.NotFound) return default;
                if (response.IsSuccessStatusCode) {
                    return await response.Content.ReadFromJsonAsync<T>(OpenLibraryPluginHost.JsonOptions);
                }

                if (attempt >= MaxRetries || !IsTransientStatus(response.StatusCode)) {
                    return default;
                }
            } catch (TaskCanceledException) when (attempt < MaxRetries) {
            }

            await Task.Delay(TimeSpan.FromSeconds(1 + attempt));
        }
    }

    private async Task ThrottleAsync() {
        if (_minRequestInterval <= TimeSpan.Zero) return;

        long slotTicks = DateTime.UtcNow.Ticks;
        for (var attempt = 0; ; attempt++) {
            try {
                using (var fs = new FileStream(RateLimitPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                    using var reader = new StreamReader(fs, Encoding.UTF8, false, 64, leaveOpen: true);
                    var lastTicks = long.TryParse((await reader.ReadToEndAsync()).Trim(), out var parsed) ? parsed : 0L;
                    slotTicks = Math.Max(DateTime.UtcNow.Ticks, lastTicks + _minRequestInterval.Ticks);
                    fs.SetLength(0);
                    fs.Position = 0;
                    await fs.WriteAsync(Encoding.UTF8.GetBytes(slotTicks.ToString()));
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

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string EscapeSegment(string value) => Uri.EscapeDataString(value.Trim().Trim('/'));
}
