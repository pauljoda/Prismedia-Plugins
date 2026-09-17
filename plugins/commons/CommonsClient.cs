using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Commons;

/// <summary>Anonymous, bounded GET access to the fixed Commons Action API; redirects never expand its scope.</summary>
internal sealed class CommonsClient : IDisposable {
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient client;

    internal CommonsClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var address) || !SameOrigin(address, CommonsCodes.Origin)
            || address.AbsolutePath is not ("/" or "/w/api.php") || address.Query.Length != 0 || address.Fragment.Length != 0
            || connection.Auth.Count != 0 || connection.ExpectedInstanceId is not null)
            throw new IntegrationFailure("Use https://commons.wikimedia.org for this anonymous public catalog.");
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<CommonsResponse> ReadAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) {
        var query = new Dictionary<string, string>(parameters) {
            [CommonsQuery.Action] = CommonsQuery.Query, [CommonsQuery.Format] = CommonsQuery.Json,
            [CommonsQuery.FormatVersion] = "2", [CommonsQuery.MaxLag] = "5"
        };
        var address = CommonsCodes.Origin + "/w/api.php?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("Prismedia (+https://pauljoda.github.io/Prismedia/)");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new IntegrationFailure($"Commons returned HTTP {(int)response.StatusCode}. Try again later.");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new IntegrationFailure("The Commons response exceeds the catalog size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var body = new MemoryStream();
        var buffer = new byte[16384];
        while (true) {
            var count = await stream.ReadAsync(buffer, deadline.Token);
            if (count == 0) break;
            if (body.Length + count > MaximumResponseBytes) throw new IntegrationFailure("The Commons response exceeds the catalog size limit.");
            body.Write(buffer, 0, count);
        }
        body.Position = 0;
        var result = await JsonSerializer.DeserializeAsync<CommonsResponse>(body, IntegrationProtocol.Json, deadline.Token);
        if (result is null || result.Error is not null || result.Query is null && result.BatchComplete != true)
            throw new IntegrationFailure("Commons could not return this catalog request. Try again later.");
        return result;
    }

    internal static bool SameOrigin(Uri address, string origin) => address.Scheme == Uri.UriSchemeHttps && address.UserInfo.Length == 0
        && address.Port == 443 && address.IdnHost.Equals(new Uri(origin).IdnHost, StringComparison.OrdinalIgnoreCase);
    public void Dispose() => client.Dispose();
}
