using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Suwayomi;

/// <summary>Bounded same-origin access to one configured Suwayomi server.</summary>
internal sealed class SuwayomiClient : IDisposable {
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Uri baseAddress;
    private readonly Dictionary<string, string> headers = [];

    internal SuwayomiClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")
            || parsed.UserInfo.Length != 0 || parsed.Query.Length != 0 || parsed.Fragment.Length != 0 || connection.ExpectedInstanceId is not null)
            throw new IntegrationFailure("Enter the HTTP or HTTPS root URL of one Suwayomi server.");
        baseAddress = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/");
        var token = Value(connection.Auth, "token");
        var username = Value(connection.Auth, "username");
        var password = Value(connection.Auth, "password");
        if (token is not null && (username is not null || password is not null) || password is not null && username is null)
            throw new IntegrationFailure("Configure either a bearer token or a complete username and password pair.");
        if (token is not null) headers["Authorization"] = "Bearer " + token;
        else if (username is not null) headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + (password ?? string.Empty)));
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal IReadOnlyDictionary<string, string> DeliveryHeaders => new Dictionary<string, string>(headers);

    internal async Task<T> GraphQlAsync<T>(string query, object? variables, CancellationToken cancellationToken) where T : class {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "api/graphql")) {
            Content = JsonContent.Create(new { query, variables }, options: IntegrationProtocol.Json)
        };
        ApplyHeaders(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new IntegrationFailure($"Suwayomi returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new IntegrationFailure("The Suwayomi response exceeds the catalog size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var bounded = new MemoryStream();
        var buffer = new byte[16384];
        while (true) {
            var count = await stream.ReadAsync(buffer, deadline.Token);
            if (count == 0) break;
            if (bounded.Length + count > MaximumResponseBytes) throw new IntegrationFailure("The Suwayomi response exceeds the catalog size limit.");
            bounded.Write(buffer, 0, count);
        }
        bounded.Position = 0;
        var envelope = await JsonSerializer.DeserializeAsync<GraphQlEnvelope<T>>(bounded, IntegrationProtocol.Json, deadline.Token);
        if (envelope?.Data is null || envelope.Errors is { Count: > 0 })
            throw new IntegrationFailure("Suwayomi could not complete the requested operation.");
        return envelope.Data;
    }

    internal async Task<bool> IsChapterReadyAsync(int chapterId, CancellationToken cancellationToken) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Head, ChapterDownloadUri(chapterId));
        ApplyHeaders(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw new IntegrationFailure($"Suwayomi returned HTTP {(int)response.StatusCode} while checking the chapter file.");
        var size = response.Content.Headers.ContentLength;
        return size is > 0 and <= SuwayomiCodes.MaximumArtifactBytes;
    }

    internal string ChapterDownloadUrl(int chapterId) => ChapterDownloadUri(chapterId).AbsoluteUri;
    private Uri ChapterDownloadUri(int chapterId) => new(baseAddress, $"api/v1/chapter/{chapterId}/download?markAsRead=false");
    private void ApplyHeaders(HttpRequestMessage request) {
        foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        request.Headers.UserAgent.ParseAdd("Prismedia (+https://pauljoda.github.io/Prismedia/)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    public void Dispose() => client.Dispose();
}
