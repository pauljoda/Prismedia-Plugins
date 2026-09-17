using System.Net;
using System.Text;

namespace Prismedia.Plugin.Metron.Tests;

public sealed class MetronClientTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedResponsesAreRejectedWithAndWithoutContentLength(bool knownLength)
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var bytes = new byte[MetronClient.MaximumBytes + 1]; Array.Fill(bytes, (byte)' ');
            HttpContent content = knownLength ? new ByteArrayContent(bytes) : new UnknownLengthContent(bytes);
            content.Headers.ContentType = new("application/json");
            return new(HttpStatusCode.OK) { Content = content };
        }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new MetronClient(http).GetAsync<MetronSeries>("series/1/", "synthetic-secret", default));
        Assert.DoesNotContain("synthetic-secret", error.Message); Assert.Contains("size limit", error.Message);
    }

    [Fact]
    public async Task HtmlAndMalformedJsonAreNotMissingMetadata()
    {
        using var html = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>Log in</html>", Encoding.UTF8, "text/html") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new MetronClient(html).GetAsync<MetronSeries>("series/1/", "secret", default));
        using var invalid = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("not-json", Encoding.UTF8, "application/json") }));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new MetronClient(invalid).GetAsync<MetronSeries>("series/1/", "secret", default));
    }

    [Fact]
    public async Task HeaderAndUnknownLengthBodyReadsShareTheDeadline()
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var content = new StreamContent(new WaitingStream()); content.Headers.ContentType = new("application/json");
            return new(HttpStatusCode.OK) { Content = content };
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MetronClient(http).GetAsync<MetronSeries>("series/1/", "secret", cancellation.Token));
    }

    [Theory]
    [InlineData("https://attacker.example/api/series/1/")]
    [InlineData("http://metron.cloud/api/series/1/")]
    [InlineData("https://user@metron.cloud/api/series/1/")]
    [InlineData("../account/")]
    public async Task TransportRejectsOtherOriginsAndNonApiPaths(string path)
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("Network must not be used")));
        await Assert.ThrowsAsync<ArgumentException>(() => new MetronClient(http).GetAsync<MetronSeries>(path, "secret", default));
    }

    [Fact]
    public async Task UpstreamErrorsDoNotExposeBodyOrCredentialsAndCarryRetryDelay()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("upstream body with synthetic-secret") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(42)); return response;
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MetronClient(http).GetAsync<MetronSeries>("series/1/", "synthetic-secret", default));
        Assert.Equal(1, calls); Assert.Contains("42 seconds", error.Message); Assert.DoesNotContain("synthetic-secret", error.Message);
    }

    [Fact]
    public async Task LowerAccountBurstQuotaAdjustsRequestSpacing()
    {
        var waits = new List<TimeSpan>();
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":1}", Encoding.UTF8, "application/json") };
            response.Headers.Add(MetronCodes.BurstLimit, "10"); return response;
        }));
        var client = new MetronClient(http, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        await client.GetAsync<MetronSeries>("series/1/", "secret", default);
        await client.GetAsync<MetronSeries>("series/2/", "secret", default);
        Assert.True(Assert.Single(waits).TotalSeconds >= 6);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
