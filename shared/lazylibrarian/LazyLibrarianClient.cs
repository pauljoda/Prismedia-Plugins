using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>Bounded transport for LazyLibrarian's API, whose errors may arrive as HTTP 200 text.</summary>
internal sealed class LazyLibrarianClient : IDisposable {
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string apiKey;

    internal LazyLibrarianClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0
            || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new IntegrationFailure("Configure LazyLibrarian's HTTP base address without embedded credentials.");
        var key = connection.Auth.GetValueOrDefault(LazyLibrarianCodes.ApiKey);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
            throw new IntegrationFailure("Configure a valid LazyLibrarian API key.");
        endpoint = new Uri(address.AbsoluteUri.TrimEnd('/') + "/api");
        apiKey = key;
        client = new(handler ?? new SocketsHttpHandler {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Reads a JSON API command and rejects success-shaped HTTP responses containing API errors.</summary>
    internal async Task<T> ReadAsync<T>(string command, IReadOnlyDictionary<string, string>? arguments, CancellationToken token) {
        if (command is not (LazyLibrarianCodes.GetAllBooks or LazyLibrarianCodes.GetAuthor or LazyLibrarianCodes.GetVersion)) throw Invalid();
        var bytes = await SendAsync(command, arguments, token);
        try {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(LazyLibrarianCodes.Success, out var success)
                && success.ValueKind == JsonValueKind.False)
                throw Invalid();
            return document.RootElement.Deserialize<T>(IntegrationProtocol.Json) ?? throw Invalid();
        } catch (JsonException) { throw Invalid(); }
    }

    /// <summary>Accepts a mutating command only when LazyLibrarian returns its explicit OK acknowledgement.</summary>
    internal async Task AcknowledgeAsync(string command, IReadOnlyDictionary<string, string> arguments, CancellationToken token) {
        if (command is not (LazyLibrarianCodes.QueueBook or LazyLibrarianCodes.UnqueueBook
                or LazyLibrarianCodes.SearchBook)) throw Invalid();
        var bytes = await SendAsync(command, arguments, token);
        if (!string.Equals(System.Text.Encoding.UTF8.GetString(bytes).Trim(), LazyLibrarianCodes.Ok, StringComparison.Ordinal))
            throw Invalid();
    }

    /// <summary>Gets final-file size without downloading bytes; a ZIP response cannot stand in for mapped audio tracks.</summary>
    internal async Task<long> FileSizeAsync(string bookId, string rendition, string reportedPath, CancellationToken token) {
        var extension = Path.GetExtension(reportedPath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(bookId) || bookId.Length > 512
            || rendition == LazyLibrarianCodes.Ebook && extension is not (".epub" or ".pdf")
            || rendition == LazyLibrarianCodes.Audiobook && extension is not (".m4b" or ".m4a" or ".mp3")
            || rendition is not (LazyLibrarianCodes.Ebook or LazyLibrarianCodes.Audiobook)) throw Invalid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Head, Address(LazyLibrarianCodes.GetFileDirect,
            new Dictionary<string, string> { [LazyLibrarianCodes.IdParameter] = bookId,
                [LazyLibrarianCodes.TypeParameter] = rendition }));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is not > 0
            || name is null || !Path.GetExtension(name).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new IntegrationFailure("LazyLibrarian did not confirm the exact supported file for this book rendition. Multi-file audio needs explicit track evidence.");
        return response.Content.Headers.ContentLength.Value;
    }

    private async Task<byte[]> SendAsync(string command, IReadOnlyDictionary<string, string>? arguments, CancellationToken token) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, Address(command, arguments));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new IntegrationFailure($"LazyLibrarian returned HTTP {(int)response.StatusCode}. Check the connection and API key.");
        if (response.Content.Headers.ContentLength > MaximumBytes) throw Oversized();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true) {
            var count = await stream.ReadAsync(buffer, deadline.Token);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaximumBytes) throw Oversized();
            output.Write(buffer, 0, count);
        }
    }

    private Uri Address(string command, IReadOnlyDictionary<string, string>? arguments) {
        if (string.IsNullOrWhiteSpace(command) || command.Any(ch => !char.IsAsciiLetter(ch))) throw Invalid();
        var query = new List<KeyValuePair<string, string>> {
            new(LazyLibrarianCodes.ApiKeyParameter, apiKey), new(LazyLibrarianCodes.CommandParameter, command)
        };
        if (arguments is not null) {
            if (arguments.Count > 8 || arguments.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key.Length > 32 || pair.Value is null || pair.Value.Length > 8192
                || pair.Key is LazyLibrarianCodes.ApiKeyParameter or LazyLibrarianCodes.CommandParameter)) throw Invalid();
            query.AddRange(arguments);
        }
        var builder = new UriBuilder(endpoint) {
            Query = string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))
        };
        return builder.Uri;
    }

    private static IntegrationFailure Invalid() => new("LazyLibrarian returned an invalid or rejected API response.");
    private static IntegrationFailure Oversized() => new("LazyLibrarian's response exceeds the read limit.");
    public void Dispose() => client.Dispose();
}
