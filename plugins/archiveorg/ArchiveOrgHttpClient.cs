using System.Net;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.ArchiveOrg;

/// <summary>Reads bounded public Archive metadata and resolves one anonymous storage-host URL.</summary>
internal sealed class ArchiveOrgHttpClient : IDisposable {
    private const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private const string UserAgent = "Prismedia-ArchiveOrg/1.0.0 (+https://pauljoda.github.io/Prismedia/)";
    private readonly HttpClient client;

    internal ArchiveOrgHttpClient(ConnectionContext connection, HttpMessageHandler? handler = null) {
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps || address.IdnHost != ArchiveOrgProtocol.Host
            || !address.IsDefaultPort || address.UserInfo.Length != 0 || address.AbsolutePath != "/"
            || address.Query.Length != 0 || address.Fragment.Length != 0 || connection.Auth.Count != 0)
            throw new IntegrationFailure("Use the public https://archive.org/ catalog address without credentials.");
        client = new(handler ?? new SocketsHttpHandler {
            AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<JsonDocument> ReadAsync(string path, CancellationToken cancellationToken) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = CreateRequest(HttpMethod.Get, new Uri("https://" + ArchiveOrgProtocol.Host + path));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new IntegrationFailure($"Internet Archive returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaximumDocumentBytes) throw new IntegrationFailure("The Archive metadata exceeds the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (true) {
            var count = await stream.ReadAsync(chunk, deadline.Token);
            if (count == 0) break;
            if (buffer.Length + count > MaximumDocumentBytes) throw new IntegrationFailure("The Archive metadata exceeds the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: deadline.Token); }
        catch (JsonException) { throw new IntegrationFailure("Internet Archive returned invalid metadata."); }
    }

    internal async Task<Uri> ResolveFileAsync(string identifier, string filename, long size, CancellationToken cancellationToken) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var address = new Uri("https://" + ArchiveOrgProtocol.Host + "/download/"
            + Uri.EscapeDataString(identifier) + "/" + Uri.EscapeDataString(filename));
        using var request = CreateRequest(HttpMethod.Head, address);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            || response.Headers.Location is not { } location)
            throw new IntegrationFailure("The Archive file has no direct public storage location.");
        var target = new Uri(address, location);
        if (target.Scheme != Uri.UriSchemeHttps || !target.IsDefaultPort || target.UserInfo.Length != 0
            || target.Fragment.Length != 0 || !target.IdnHost.EndsWith("." + ArchiveOrgProtocol.Host, StringComparison.OrdinalIgnoreCase))
            throw new IntegrationFailure("The Archive file redirected outside its public storage hosts.");
        using var verifyRequest = CreateRequest(HttpMethod.Head, target);
        using var verified = await client.SendAsync(verifyRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (verified.StatusCode != HttpStatusCode.OK || verified.Content.Headers.ContentLength != size)
            throw new IntegrationFailure("The Archive file changed or is no longer directly downloadable.");
        return target;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri address) {
        var request = new HttpRequestMessage(method, address);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        return request;
    }

    public void Dispose() => client.Dispose();
}
