using System.Net;
using System.Text.Json;

namespace Prismedia.Plugin.GoogleBooks;

/// <summary>Bounded read-only access to the public volume API; errors never include credential-bearing URLs.</summary>
internal sealed class GoogleBooksClient(HttpClient http) {
    private const int MaximumBytes = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private static readonly Uri Origin = new("https://www.googleapis.com/books/v1/");

    public async Task<T?> GetAsync<T>(string relative, string? apiKey) {
        var uri = new Uri(Origin, relative + (string.IsNullOrWhiteSpace(apiKey) ? string.Empty :
            (relative.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(apiKey)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Prismedia-GoogleBooks/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("Google Books is rate limited. Try later or configure an API key with available quota.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Google Books returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Google Books response exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true) {
            var count = await input.ReadAsync(buffer, timeout.Token);
            if (count == 0) break;
            if (output.Length + count > MaximumBytes) throw new InvalidDataException("Google Books response exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(output.ToArray(), JsonOptions);
    }
}

// prism-vocab: external Google Books response fields, decoded only in these boundary models.
internal sealed record GoogleVolume(string Id, GoogleVolumeInfo VolumeInfo);
internal sealed record GoogleVolumeInfo(string? Title, string? Subtitle, string? Description,
    string[]? Authors, string? Publisher, string? PublishedDate, string? Language, string? PrintType,
    string? MaturityRating, GoogleIdentifier[]? IndustryIdentifiers, GoogleImages? ImageLinks, string[]? Categories);
internal sealed record GoogleIdentifier(string Type, string Identifier);
internal sealed record GoogleImages(string? Thumbnail, string? SmallThumbnail);
internal sealed record GoogleVolumePage(GoogleVolume[]? Items, int? TotalItems);
