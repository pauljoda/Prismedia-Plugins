using System.Net;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>
/// Bounded transport for LazyLibrarian's API, whose refusals usually arrive as HTTP 200 text or JSON.
/// A write that LazyLibrarian refuses before changing anything is a <see cref="ManagedMutationRejection"/>;
/// every other unexpected write reply stays uncertain.
/// </summary>
internal sealed class LazyLibrarianClient : IDisposable {
    #region Static Variables
    /// <summary>Connection credential key that holds LazyLibrarian's API key.</summary>
    internal const string ApiKey = "apiKey";

    private const string ApiKeyParameter = "apikey";
    private const string CommandParameter = "cmd";
    private const string IdParameter = "id";
    private const string TypeParameter = "type";
    private const string Acknowledgement = "OK";
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumRefusalLength = 512;
    #endregion

    #region Variables
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string apiKey;
    #endregion

    #region Constructors
    /// <summary>Validates the connection's base address and API key before any request.</summary>
    /// <param name="connection">The connection whose base URL and API key identify one LazyLibrarian installation.</param>
    /// <param name="handler">Optional transport handler; tests replace the network with it.</param>
    internal LazyLibrarianClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0
            || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new IntegrationFailure("Configure LazyLibrarian's HTTP base address without embedded credentials.");
        var key = connection.Auth.GetValueOrDefault(ApiKey);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
            throw new IntegrationFailure("Configure a valid LazyLibrarian API key.");
        endpoint = new Uri(address.AbsoluteUri.TrimEnd('/') + "/api");
        apiKey = key;
        client = new(handler ?? new SocketsHttpHandler {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    #endregion

    #region Actions - Reads
    /// <summary>Reads the commands and parameters the configured key may use.</summary>
    /// <exception cref="IntegrationFailure">LazyLibrarian refused the key or listed no commands.</exception>
    internal async Task<LazyLibrarianApiHelp> ReadHelpAsync(CancellationToken token) {
        var text = Encoding.UTF8.GetString(await SendAsync(LazyLibrarianCommand.Help, [], false, token));
        if (Refusal(text) is { } refusal) throw new IntegrationFailure($"LazyLibrarian refused its API help: {refusal}.");
        return LazyLibrarianApiHelp.Parse(text);
    }

    /// <summary>Reads one JSON command and rejects success-shaped HTTP responses that carry API errors.</summary>
    /// <param name="command">A command whose reply is JSON.</param>
    /// <param name="id">Optional <c>id</c> argument, such as an AuthorID.</param>
    /// <param name="token">Invocation deadline.</param>
    internal async Task<T> ReadAsync<T>(LazyLibrarianCommand command, string? id, CancellationToken token) {
        if (command.Reply != LazyLibrarianReply.Json)
            throw new ArgumentException($"{command.Name} does not reply with JSON.", nameof(command));
        var text = Encoding.UTF8.GetString(await SendAsync(command, id is null ? [] : [new(IdParameter, id)], false, token));
        if (Refusal(text) is { } refusal) throw new IntegrationFailure($"LazyLibrarian refused {command.Name}: {refusal}.");
        try {
            return JsonSerializer.Deserialize<T>(text, IntegrationProtocol.Json) ?? throw Invalid();
        } catch (JsonException) {
            throw Invalid();
        }
    }
    #endregion

    #region Actions - Writes
    /// <summary>
    /// Sends one format-scoped write and requires LazyLibrarian's explicit <c>OK</c>. LazyLibrarian's
    /// handlers answer with refusal text or a JSON refusal before changing anything, so those replies
    /// and HTTP 4xx are definite refusals. Server errors and unrecognized replies stay uncertain.
    /// </summary>
    /// <exception cref="ManagedMutationRejection">LazyLibrarian definitely refused the write.</exception>
    /// <exception cref="IntegrationFailure">The write's outcome is uncertain.</exception>
    internal async Task AcknowledgeAsync(LazyLibrarianCommand command, string bookId, LazyLibrarianRendition rendition,
        CancellationToken token) {
        if (command.Reply != LazyLibrarianReply.Acknowledgement)
            throw new ArgumentException($"{command.Name} is not an acknowledged write.", nameof(command));
        var text = Encoding.UTF8.GetString(await SendAsync(command, Arguments(bookId, rendition), true, token)).Trim();
        if (string.Equals(text, Acknowledgement, StringComparison.Ordinal)) return;
        if (Refusal(text) is { } refusal) throw new ManagedMutationRejection($"LazyLibrarian refused {command.Name}: {refusal}.");
        if (text.Length is > 0 and <= MaximumRefusalLength && !text.Contains('<') && !text.Any(char.IsControl))
            throw new ManagedMutationRejection($"LazyLibrarian refused {command.Name}: {text}");
        throw new IntegrationFailure($"LazyLibrarian did not acknowledge {command.Name}. Review the book in LazyLibrarian before another change.");
    }
    #endregion

    #region Actions - Files
    /// <summary>
    /// Gets a directly served file's size through HEAD without downloading bytes. Only formats whose
    /// direct download is the reported file itself may use it; LazyLibrarian zips multi-file audiobooks.
    /// </summary>
    /// <exception cref="IntegrationFailure">LazyLibrarian did not confirm the exact reported file.</exception>
    internal async Task<long> FileSizeAsync(string bookId, LazyLibrarianRendition rendition, string reportedPath, CancellationToken token) {
        if (rendition.ReadsMappedFolder)
            throw new ArgumentException($"LazyLibrarian cannot serve {rendition.Noun} files directly without bundling them.", nameof(rendition));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Head, Address(LazyLibrarianCommand.GetFileDirect, Arguments(bookId, rendition)));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is not > 0
            || name is null || !Path.GetExtension(name).Equals(Path.GetExtension(reportedPath), StringComparison.OrdinalIgnoreCase))
            throw new IntegrationFailure($"LazyLibrarian did not confirm the exact {rendition.Noun} file it reports for this book.");
        return response.Content.Headers.ContentLength.Value;
    }
    #endregion

    #region Actions - Transport
    private async Task<byte[]> SendAsync(LazyLibrarianCommand command, IReadOnlyList<KeyValuePair<string, string>> arguments,
        bool write, CancellationToken token) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, Address(command, arguments));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != HttpStatusCode.OK) {
            // A 4xx write never reached a LazyLibrarian handler that changes state.
            if (write && (int)response.StatusCode is >= 400 and < 500)
                throw new ManagedMutationRejection($"LazyLibrarian refused {command.Name} (HTTP {(int)response.StatusCode}).");
            throw new IntegrationFailure($"LazyLibrarian returned HTTP {(int)response.StatusCode}. Check the connection and API key.");
        }
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

    private Uri Address(LazyLibrarianCommand command, IReadOnlyList<KeyValuePair<string, string>> arguments) {
        if (arguments.Any(pair => pair.Value.Length is 0 or > 8192 || pair.Value.Any(char.IsControl)))
            throw new IntegrationFailure("Select a valid LazyLibrarian book identity.");
        IReadOnlyList<KeyValuePair<string, string>> query = [new(ApiKeyParameter, apiKey), new(CommandParameter, command.Name), .. arguments];
        var builder = new UriBuilder(endpoint) {
            Query = string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))
        };
        return builder.Uri;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Arguments(string bookId, LazyLibrarianRendition rendition) =>
        [new(IdParameter, bookId), new(TypeParameter, rendition.WireType)];

    /// <summary>Describes LazyLibrarian's JSON refusal envelope, or returns null for any other reply.</summary>
    private static string? Refusal(string text) {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{')) return null;
        try {
            var error = JsonSerializer.Deserialize<LazyLibrarianApiError>(trimmed, IntegrationProtocol.Json);
            if (error?.Success != false) return null;
            var message = error.Error?.Message is { Length: > 0 and <= 256 } reported && !reported.Any(char.IsControl)
                ? reported
                : "an unspecified API error";
            return error.Error?.Code is { } code ? $"{message} (code {code})" : message;
        } catch (JsonException) {
            return null;
        }
    }

    private static IntegrationFailure Invalid() => new("LazyLibrarian returned an invalid API response.");
    private static IntegrationFailure Oversized() => new("LazyLibrarian's response exceeds the read limit.");
    #endregion

    #region Actions - Lifetime
    /// <summary>Releases the owned HTTP client.</summary>
    public void Dispose() => client.Dispose();
    #endregion
}
