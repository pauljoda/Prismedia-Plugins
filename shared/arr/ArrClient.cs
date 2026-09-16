using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

/// <summary>Bounded read transport for the configured API origin; never follows credential-bearing redirects.</summary>
internal sealed class ArrClient : IDisposable {
    internal const string ApiKey = "apiKey";
    private const int MaximumBytes = 8 * 1024 * 1024;
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
    internal async Task<T> GetAsync<T>(string relativePath, CancellationToken cancellationToken) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, relativePath));
        request.Headers.Add("X-Api-Key", key);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
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
