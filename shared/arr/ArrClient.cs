using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

/// <summary>Bounded transport for the configured API origin; never retries writes or follows credential-bearing redirects.</summary>
internal sealed class ArrClient : IDisposable {
    internal const string ApiKey = "apiKey";
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions WriteJson = new(IntegrationProtocol.Json) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient client;
    private readonly Uri root;
    private readonly string key;
    internal ArrClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new IntegrationFailure("Configure the application's HTTP base address without embedded credentials.");
        root = new(address.AbsoluteUri.TrimEnd('/') + "/api/v3/");
        key = connection.Auth.GetValueOrDefault(ApiKey) ?? "";
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new IntegrationFailure("Configure a valid API key.");
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    internal async Task<T> GetAsync<T>(string relativePath, CancellationToken cancellationToken) where T : class =>
        (await SendAsync<T>(HttpMethod.Get, relativePath, null, false, cancellationToken))!;
    internal Task<T?> GetOptionalAsync<T>(string relativePath, CancellationToken cancellationToken) where T : class =>
        SendAsync<T>(HttpMethod.Get, relativePath, null, true, cancellationToken);
    internal async Task<T> WriteAsync<T>(HttpMethod method, string relativePath, object body, CancellationToken cancellationToken) where T : class {
        if (method != HttpMethod.Post && method != HttpMethod.Put) throw new IntegrationFailure("Unsupported manager mutation.");
        return (await SendAsync<T>(method, relativePath, body, false, cancellationToken))!;
    }
    private async Task<T?> SendAsync<T>(HttpMethod method, string relativePath, object? body, bool allowMissing, CancellationToken cancellationToken) where T : class {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(method, new Uri(root, relativePath));
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, WriteJson), System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-Api-Key", key);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (method != HttpMethod.Get && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new ArrRequestRejectedException(response.StatusCode);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IntegrationFailure("The remote holding or API endpoint no longer exists. Refresh the connected library.");
        if (!response.IsSuccessStatusCode) throw new IntegrationFailure($"The connected application returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new IntegrationFailure("The connected application's response exceeds the read limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[16384];
        while (true) {
            var count = await stream.ReadAsync(buffer, deadline.Token);
            if (count == 0) break;
            if (bytes.Length + count > MaximumBytes) throw new IntegrationFailure("The connected application's response exceeds the read limit.");
            bytes.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(bytes.ToArray(), IntegrationProtocol.Json) ?? throw new IntegrationFailure("The connected application returned an empty response.");
    }
    public void Dispose() => client.Dispose();
}

/// <summary>A definitive API rejection; timeouts, redirects, oversized replies and server failures are never classified this way.</summary>
internal sealed class ArrRequestRejectedException(HttpStatusCode status)
    : Exception($"The connected application rejected the request (HTTP {(int)status}).");
