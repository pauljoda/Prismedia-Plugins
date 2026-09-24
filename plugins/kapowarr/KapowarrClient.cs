using System.Net;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Kapowarr;

/// <summary>
/// Bounded Kapowarr transport. Query authentication never leaves this process, every reply is decoded
/// through Kapowarr's typed envelope, and a definite 4xx on a write is reported as a
/// <see cref="ManagedMutationRejection"/>; every other write failure stays uncertain.
/// </summary>
internal sealed class KapowarrClient : IDisposable {
    #region Static Variables
    /// <summary>Connection credential key that holds Kapowarr's API key.</summary>
    internal const string ApiKey = "apiKey";

    private const string ApiKeyParameter = "api_key";
    private const string CatalogSearchPath = "volumes/search?";
    private const int MaximumBytes = 8 * 1024 * 1024;
    #endregion

    #region Variables
    private readonly HttpClient client;
    private readonly Uri root;
    private readonly string credential;
    #endregion

    #region Constructors
    /// <summary>Validates the connection's base address and API key before any request.</summary>
    /// <param name="connection">The connection whose base URL and API key identify one Kapowarr installation.</param>
    /// <param name="handler">Optional transport handler; tests replace the network with it.</param>
    internal KapowarrClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new IntegrationFailure("Configure Kapowarr's HTTP base address without embedded credentials.");
        root = new(address.AbsoluteUri.TrimEnd('/') + "/api/");
        var key = connection.Auth.GetValueOrDefault(ApiKey);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
            throw new IntegrationFailure("Configure a valid Kapowarr API key.");
        credential = Uri.EscapeDataString(key);
        client = new(handler ?? new SocketsHttpHandler {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    #endregion

    #region Actions - Reads
    /// <summary>Reads one resource.</summary>
    /// <exception cref="KapowarrRecordNotFound">Kapowarr answered 404 with its own error envelope.</exception>
    internal async Task<TResult> GetAsync<TResult>(string relativePath, CancellationToken token) where TResult : class =>
        await SendAsync<TResult>(HttpMethod.Get, relativePath, null, HttpStatusCode.OK, token) ?? throw Invalid();
    #endregion

    #region Actions - Writes
    /// <summary>Replaces fields of one resource and returns Kapowarr's updated record.</summary>
    internal async Task<TResult> PutAsync<TBody, TResult>(string relativePath, TBody body, CancellationToken token)
        where TResult : class =>
        await SendAsync<TResult>(HttpMethod.Put, relativePath, Content(body), HttpStatusCode.OK, token) ?? throw Invalid();

    /// <summary>Replaces fields of one resource whose route acknowledges with an empty result.</summary>
    internal async Task PutAcknowledgedAsync<TBody>(string relativePath, TBody body, CancellationToken token) =>
        await SendAsync<object>(HttpMethod.Put, relativePath, Content(body), HttpStatusCode.OK, token);

    /// <summary>Creates one resource or queues one task and returns Kapowarr's receipt.</summary>
    internal async Task<TResult> PostAsync<TBody, TResult>(string relativePath, TBody body, CancellationToken token)
        where TResult : class =>
        await SendAsync<TResult>(HttpMethod.Post, relativePath, Content(body), HttpStatusCode.Created, token) ?? throw Invalid();
    #endregion

    #region Actions - Transport
    private async Task<TResult?> SendAsync<TResult>(HttpMethod method, string relativePath, HttpContent? content,
        HttpStatusCode expectedStatus, CancellationToken token) where TResult : class {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // Only adapter-owned constant paths and validated positive IDs enter this boundary.
        var separator = relativePath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        using var request = new HttpRequestMessage(method, new Uri(root, relativePath + separator + ApiKeyParameter + "=" + credential));
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = content;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == expectedStatus) {
            var envelope = Decode<TResult>(await ReadAsync(response, deadline.Token));
            if (envelope.Error is not null) throw Invalid();
            return envelope.Result;
        }
        if (method == HttpMethod.Get && response.StatusCode == HttpStatusCode.NotFound
            && TryDecodeError(await ReadAsync(response, deadline.Token)) is not null)
            throw new KapowarrRecordNotFound();
        if (method == HttpMethod.Get && response.StatusCode == HttpStatusCode.BadRequest
            && relativePath.StartsWith(CatalogSearchPath, StringComparison.Ordinal))
            throw new IntegrationFailure("Kapowarr could not search Comic Vine. Check its Comic Vine API key in Kapowarr settings.");
        if (method != HttpMethod.Get && (int)response.StatusCode is >= 400 and < 500) {
            var error = TryDecodeError(await ReadAsync(response, deadline.Token));
            throw new ManagedMutationRejection(error is null
                ? $"Kapowarr refused this change (HTTP {(int)response.StatusCode})."
                : $"Kapowarr refused this change (HTTP {(int)response.StatusCode}, {error}).");
        }
        throw new IntegrationFailure($"Kapowarr returned HTTP {(int)response.StatusCode}. Check the connection and API key.");
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken token) {
        if (response.Content.Headers.ContentLength > MaximumBytes) throw Oversized();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[16384];
        while (true) {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) return bytes.ToArray();
            if (bytes.Length + count > MaximumBytes) throw Oversized();
            bytes.Write(buffer, 0, count);
        }
    }

    private static KapowarrEnvelope<TResult> Decode<TResult>(byte[] bytes) {
        try {
            return JsonSerializer.Deserialize<KapowarrEnvelope<TResult>>(bytes, IntegrationProtocol.Json) ?? throw Invalid();
        } catch (JsonException) {
            throw Invalid();
        }
    }

    /// <summary>Returns Kapowarr's error class name when the body is its error envelope, otherwise null.</summary>
    private static string? TryDecodeError(byte[] bytes) {
        try {
            var error = JsonSerializer.Deserialize<KapowarrEnvelope<JsonElement>>(bytes, IntegrationProtocol.Json)?.Error;
            return error is { Length: > 0 and <= 64 } && error.All(char.IsAsciiLetter) ? error : null;
        } catch (JsonException) {
            return null;
        }
    }

    private static StringContent Content<TBody>(TBody body) =>
        new(JsonSerializer.Serialize(body, IntegrationProtocol.Json), Encoding.UTF8, "application/json");

    private static IntegrationFailure Oversized() => new("Kapowarr's response exceeds the read limit.");
    private static IntegrationFailure Invalid() => new("Kapowarr returned an invalid response or reported an API error.");
    #endregion

    #region Actions - Lifetime
    /// <summary>Releases the owned HTTP client.</summary>
    public void Dispose() => client.Dispose();
    #endregion
}
