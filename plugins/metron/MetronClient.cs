using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Prismedia.Plugin.Metron;

/// <summary>Read-only fixed-origin transport with bounded bodies, quota observation, and no automatic retries.</summary>
internal sealed class MetronClient(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null) {
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal static readonly Uri Origin = new("https://metron.cloud/api/");
    internal static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, MaxDepth = 32 };
    internal static readonly JsonSerializerOptions ProtocolJson = new(JsonSerializerDefaults.Web) { MaxDepth = 48 };
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private TimeSpan _interval = TimeSpan.FromMilliseconds(3100);
    private bool _sent;
    private DateTimeOffset? _quotaReset;
    private long _totalBytes;

    internal async Task<T?> GetAsync<T>(string relative, string apiToken, CancellationToken token) where T : class {
        var uri = new Uri(Origin, relative);
        if (!SameApiOrigin(uri)) throw new ArgumentException("Metron requests must stay on its API origin.");
        if (_quotaReset is { } reset && reset > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Metron is rate limited. Wait for the account quota to reset before retrying.");
        if (_sent) await _delay(_interval, token);
        _sent = true;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        request.Headers.UserAgent.ParseAdd("Prismedia-Metron/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        ObserveQuota(response);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("Metron is rate limited. Try again after " + RetryDelay(response) + ".");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Metron rejected the API token. Check the token in plugin settings.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Metron returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/vnd.api+json"))
            throw new InvalidDataException("Metron did not return a JSON API response.");
        if (response.Content.Headers.ContentLength > MaximumBytes) throw Oversized();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true) {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) break;
            _totalBytes += read;
            if (output.Length + read > MaximumBytes || _totalBytes > 2L * MaximumBytes) throw Oversized();
            output.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<T>(output.ToArray(), ApiJson)
            ?? throw new InvalidDataException("Metron returned an empty API document.");
    }

    internal static void ValidateNext(string? next, string path) {
        if (next is null) return;
        if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || !SameApiOrigin(uri)
            || uri.AbsolutePath != new Uri(Origin, path).AbsolutePath || uri.Fragment.Length != 0)
            throw new InvalidDataException("Metron pagination left the selected resource.");
    }
    private static bool SameApiOrigin(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.Host == Origin.Host
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.AbsolutePath.StartsWith(Origin.AbsolutePath, StringComparison.Ordinal);
    private static InvalidDataException Oversized() => new("Metron response exceeds its size limit.");
    private static string RetryDelay(HttpResponseMessage response) => response.Headers.RetryAfter?.Delta is { } delta
        ? Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds)) + " seconds"
        : response.Headers.RetryAfter?.Date is { } date ? date.ToString("u") : "the account quota resets";
    private void ObserveQuota(HttpResponseMessage response) {
        if (HeaderNumber(response, MetronCodes.BurstLimit) is > 0 and < 20)
            _interval = TimeSpan.FromMilliseconds(Math.Ceiling(60000d / HeaderNumber(response, MetronCodes.BurstLimit)!.Value) + 100);
        foreach (var (remaining, reset) in new[] { (MetronCodes.BurstRemaining, MetronCodes.BurstReset), (MetronCodes.SustainedRemaining, MetronCodes.SustainedReset) }) {
            if (HeaderNumber(response, remaining) is not 0) continue;
            var seconds = HeaderNumber(response, reset);
            var until = seconds is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : DateTimeOffset.UtcNow.AddDays(1);
            if (_quotaReset is null || until > _quotaReset) _quotaReset = until;
        }
    }
    private static long? HeaderNumber(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values)
        && long.TryParse(values.FirstOrDefault(), out var number) ? number : null;
}

// prism-vocab: external Metron response fields are decoded only by these boundary records.
internal sealed record MetronPage<T>(int Count, string? Next, T[]? Results);
internal sealed record MetronNamed(long Id, string? Name);
internal sealed record MetronSeries(long Id, string? Name, string? Series, string? Desc, int? YearBegan, int? YearEnd,
    int? Volume, int? IssueCount, string? Language, string[]? AltNames, MetronNamed? Publisher, MetronNamed[]? Genres,
    long? CvId, long? GcdId, string? ResourceUrl);
internal sealed record MetronIssue(long Id, MetronSeries? Series, string? Number, string? Issue, string? Desc,
    string? StoreDate, string? CoverDate, string? Image, int? Page, MetronNamed? Publisher, MetronCredit[]? Credits,
    long? CvId, long? GcdId, string? ResourceUrl);
internal sealed record MetronCredit(long Id, string? Creator, MetronNamed[]? Role);
