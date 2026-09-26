using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Archiver;

/// <summary>Bounded authenticated control transport. Redirects are rejected; credentials stay on the configured origin.</summary>
internal sealed class ArchiverClient : IDisposable {
    #region Static Variables
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    #endregion

    #region Variables
    private readonly HttpClient client;
    private readonly Uri root;
    internal IReadOnlyDictionary<string, string> Headers { get; }
    #endregion

    #region Constructors
    internal ArchiverClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new IntegrationFailure("Configure the Archiver's HTTP base address without embedded credentials.");
        root = new(address.AbsoluteUri.TrimEnd('/') + "/");
        var token = connection.Auth.GetValueOrDefault(ArchiverWire.TokenKey);
        if (string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n')) throw new IntegrationFailure("Configure a valid integration token.");
        Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    #endregion

    #region Actions - Transport
    internal Uri ArtifactUrl(string contentPath) {
        if (string.IsNullOrWhiteSpace(contentPath) || !contentPath.StartsWith('/') || contentPath.StartsWith("//") || contentPath.Contains('\\'))
            throw new IntegrationFailure("The artifact requires an API-relative content path.");
        var target = new Uri(root, contentPath);
        if (target.Scheme != root.Scheme || target.Host != root.Host || target.Port != root.Port || target.UserInfo.Length != 0 || target.Fragment.Length != 0)
            throw new IntegrationFailure("The artifact delivery address leaves this connection's server.");
        return target;
    }
    internal async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken,
        Guid? operation = null, bool allowMissing = false) where T : class {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(method, new Uri(root, "api/v1/" + path));
        foreach (var header in Headers) request.Headers.Add(header.Key, header.Value);
        request.Headers.Accept.ParseAdd("application/json");
        if (operation is not null) request.Headers.Add("Idempotency-Key", operation.Value.ToString());
        if (body is not null) request.Content = JsonContent.Create(body, options: IntegrationProtocol.Json);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new IntegrationFailure($"The Archiver returned HTTP {(int)response.StatusCode}. Check its job state before retrying.");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new IntegrationFailure("The Archiver response exceeds the control-message limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (true) {
            var count = await stream.ReadAsync(chunk, deadline.Token);
            if (count == 0) break;
            if (buffer.Length + count > MaximumResponseBytes) throw new IntegrationFailure("The Archiver response exceeds the control-message limit.");
            buffer.Write(chunk, 0, count);
        }
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), IntegrationProtocol.Json) ?? throw new IntegrationFailure("The Archiver returned an empty control message.");
    }
    #endregion

    #region Actions - Disposal
    public void Dispose() => client.Dispose();
    #endregion
}
