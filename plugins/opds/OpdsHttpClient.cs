using System.Net;
using System.Text;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Opds;

/// <summary>Catalog HTTP transport with explicit credential scope, redirects, deadlines, and decompressed response limits.</summary>
internal sealed class OpdsHttpClient : IDisposable {
    private const string UsernameKey = "username";
    private const string PasswordKey = "password";
    private const string TokenKey = "token";
    private readonly HttpClient client;
    private readonly Uri origin;
    internal IReadOnlyDictionary<string, string> Headers { get; }

    internal OpdsHttpClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var address) || !OpdsParser.SameOrigin(address, address))
            throw new IntegrationFailure("Configure a valid HTTP or HTTPS catalog address without embedded credentials.");
        origin = address;
        var username = connection.Auth.GetValueOrDefault(UsernameKey);
        var password = connection.Auth.GetValueOrDefault(PasswordKey);
        var token = connection.Auth.GetValueOrDefault(TokenKey);
        if (!string.IsNullOrEmpty(token) && (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password)))
            throw new IntegrationFailure("Configure either a bearer token or a username and password.");
        if (!string.IsNullOrEmpty(password) && string.IsNullOrEmpty(username)) throw new IntegrationFailure("A username is required when configuring a password.");
        if (username?.Contains(':') == true) throw new IntegrationFailure("A Basic authentication username cannot contain a colon.");
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(token)) headers["Authorization"] = "Bearer " + token;
        else if (!string.IsNullOrEmpty(username)) headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        if (headers.Values.Any(value => value.Contains('\r') || value.Contains('\n'))) throw new IntegrationFailure("Credentials cannot contain HTTP line breaks.");
        Headers = headers;
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) }) {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal Uri RequireScope(string address) {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var target) || !OpdsParser.SameOrigin(origin, target))
            throw new IntegrationFailure("The catalog link is outside this connection's configured server.");
        return target;
    }

    internal async Task<(string Body, Uri Url)> ReadAsync(Uri address, CancellationToken cancellationToken) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var current = RequireScope(address.AbsoluteUri);
        for (var redirects = 0; redirects <= 5; redirects++) {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            foreach (var header in Headers) request.Headers.Add(header.Key, header.Value);
            request.Headers.Accept.ParseAdd("application/opds+json, application/atom+xml, application/opensearchdescription+xml");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) {
                if (response.Headers.Location is not { } location || redirects == 5) throw new IntegrationFailure("The catalog returned an invalid redirect chain.");
                current = RequireScope(new Uri(current, location).AbsoluteUri);
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new IntegrationFailure("The catalog rejected the connection credentials.");
            if (!response.IsSuccessStatusCode) throw new IntegrationFailure($"The catalog returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > OpdsParser.MaximumDocumentBytes) throw new IntegrationFailure("The catalog exceeds the document size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            while (true) {
                var count = await stream.ReadAsync(chunk, deadline.Token);
                if (count == 0) break;
                if (buffer.Length + count > OpdsParser.MaximumDocumentBytes) throw new IntegrationFailure("The catalog exceeds the document size limit.");
                buffer.Write(chunk, 0, count);
            }
            // OPDS JSON is UTF-8; StreamReader also recognizes XML UTF BOMs.
            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, true);
            return (await reader.ReadToEndAsync(deadline.Token), current);
        }
        throw new IntegrationFailure("The catalog returned too many redirects.");
    }

    public void Dispose() => client.Dispose();
}
