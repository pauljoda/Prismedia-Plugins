using System.Net;
using System.Text;

namespace Prismedia.Plugin.Tmdb.Tests;

public sealed class TmdbPluginTests {
    [Fact]
    public async Task ExplicitSeriesTitleSearchIgnoresStaleEntityHints() {
        using var http = new HttpClient(new StubHandler(request => {
            Assert.Equal("/3/search/tv", request.RequestUri?.AbsolutePath);
            Assert.Contains("query=the%20chair%20company", request.RequestUri?.Query);
            return """
                {
                  "results": [
                    {
                      "id": 271267,
                      "name": "The Chair Company",
                      "first_air_date": "2025-10-12",
                      "overview": "A series.",
                      "poster_path": "/poster.jpg",
                      "media_type": "tv"
                    }
                  ]
                }
                """;
        }));
        var plugin = new TmdbPlugin(new TmdbApiClient(http, "test-key"));

        var result = await plugin.IdentifyAsync(new IdentifyPluginRequest(
            "search",
            new Dictionary<string, string>(),
            new IdentifyEntitySnapshot(
                Guid.NewGuid(),
                "video-series",
                "The Chair Company"),
            new IdentifyQuery("The Chair Company", null, null),
            new IdentifyMatchHints(
                new Dictionary<string, string> { ["tmdb"] = "418214" },
                ["https://www.themoviedb.org/tv/271267"],
                "The Chair Company",
                null)));

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("271267", candidate.ExternalIds["tmdb"]);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
    }
}
