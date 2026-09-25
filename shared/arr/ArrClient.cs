using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

/// <summary>
/// Bounded transport for the configured API origin; never retries writes or follows credential-bearing
/// redirects. A write the application definitely refuses with a 4xx before changing anything is a
/// <see cref="ManagedMutationRejection"/>; every other write failure stays uncertain.
/// </summary>
internal sealed class ArrClient : IDisposable {
    #region Static Variables
    internal const string ApiKey = "apiKey";
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions WriteJson = new(IntegrationProtocol.Json) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    #endregion

    #region Variables
    private readonly HttpClient client;
    private readonly Uri root;
    private readonly string key;
    #endregion

    #region Constructors
    internal ArrClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new IntegrationFailure("Configure the application's HTTP base address without embedded credentials.");
        root = new(address.AbsoluteUri.TrimEnd('/') + "/api/v3/");
        key = connection.Auth.GetValueOrDefault(ApiKey) ?? "";
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new IntegrationFailure("Configure a valid API key.");
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    #endregion

    #region Actions - Transport
    internal async Task<T> GetAsync<T>(string relativePath, CancellationToken cancellationToken) where T : class =>
        (await SendAsync<T>(HttpMethod.Get, relativePath, null, false, cancellationToken))!;
    internal Task<T?> GetOptionalAsync<T>(string relativePath, CancellationToken cancellationToken) where T : class =>
        SendAsync<T>(HttpMethod.Get, relativePath, null, true, cancellationToken);
    /// <summary>
    /// Sends one POST or PUT and decodes its reply. HTTP 400, 401, 403, 404, and 405 mean the application
    /// refused the request before changing anything. Timeouts, redirects, oversized or invalid replies,
    /// and server errors are never classified that way.
    /// </summary>
    /// <exception cref="ManagedMutationRejection">The application definitely refused the write.</exception>
    /// <exception cref="IntegrationFailure">The write's outcome is uncertain.</exception>
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
            throw new ManagedMutationRejection($"The connected application rejected the request (HTTP {(int)response.StatusCode}).");
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
    #endregion

    #region Actions - Disposal
    public void Dispose() => client.Dispose();
    #endregion
}
