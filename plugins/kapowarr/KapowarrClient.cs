using System.Text.Json;
using Prismedia.Plugin.Integrations;
namespace Prismedia.Plugin.Kapowarr;

/// <summary>Bounded read-only transport; query authentication never escapes the server process.</summary>
internal sealed class KapowarrClient : IDisposable {
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Uri root;
    private readonly string credential;

    internal KapowarrClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new IntegrationFailure("Configure Kapowarr's HTTP base address without embedded credentials.");
        root = new(address.AbsoluteUri.TrimEnd('/') + "/api/");
        var key = connection.Auth.GetValueOrDefault(KapowarrCodes.ApiKey);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl)) throw new IntegrationFailure("Configure a valid Kapowarr API key.");
        credential = Uri.EscapeDataString(key);
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<T> GetAsync<T>(string relativePath, CancellationToken token) where T : class {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // Only adapter-owned constant paths and validated positive IDs enter this boundary.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, relativePath + "?" + KapowarrCodes.ApiKeyParameter + "=" + credential));
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new IntegrationFailure($"Kapowarr returned HTTP {(int)response.StatusCode}. Check the connection and API key.");
        if (response.Content.Headers.ContentLength > MaximumBytes) throw Oversized();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[16384];
        while (true) {
            var count = await stream.ReadAsync(buffer, deadline.Token);
            if (count == 0) break;
            if (bytes.Length + count > MaximumBytes) throw Oversized();
            bytes.Write(buffer, 0, count);
        }
        try {
            using var document = JsonDocument.Parse(bytes.ToArray());
            // prism-vocab: external — Kapowarr's single response-envelope decode boundary.
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Null
                || !document.RootElement.TryGetProperty("result", out var result)) throw Invalid();
            return result.Deserialize<T>(IntegrationProtocol.Json) ?? throw Invalid();
        } catch (JsonException) { throw Invalid(); }
    }

    private static IntegrationFailure Oversized() => new("Kapowarr's response exceeds the read limit.");
    private static IntegrationFailure Invalid() => new("Kapowarr returned an invalid response or reported an API error.");
    public void Dispose() => client.Dispose();
}
