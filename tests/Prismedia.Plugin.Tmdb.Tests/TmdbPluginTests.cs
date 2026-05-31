using System.Net;
using System.Text;

namespace Prismedia.Plugin.Tmdb.Tests;

public sealed class TmdbPluginTests {
    [Fact]
    public async Task MovieLookupReturnsMovieProposalKind() {
        using var http = new HttpClient(new StubHandler(request => {
            Assert.Equal("/3/movie/1239655", request.RequestUri?.AbsolutePath);
            Assert.Contains("append_to_response=credits%2Cimages", request.RequestUri?.Query);
            return """
                {
                  "id": 1239655,
                  "title": "Friendship",
                  "original_title": "Friendship",
                  "release_date": "2025-05-09",
                  "overview": "A suburban dad falls hard for his charismatic new neighbor.",
                  "poster_path": "/friendship-poster.jpg",
                  "backdrop_path": "/friendship-backdrop.jpg",
                  "genres": [{ "id": 35, "name": "Comedy" }],
                  "runtime": 100,
                  "production_companies": [],
                  "credits": { "cast": [], "crew": [] },
                  "images": { "posters": [], "backdrops": [], "logos": [] }
                }
                """;
        }));
        var plugin = new TmdbPlugin(new TmdbApiClient(http, "test-key"));

        var result = await plugin.IdentifyAsync(new IdentifyPluginRequest(
            "lookup-id",
            new Dictionary<string, string>(),
            new IdentifyEntitySnapshot(
                Guid.NewGuid(),
                "movie",
                "Friendship"),
            new IdentifyQuery(null, null, new Dictionary<string, string> { ["tmdb"] = "1239655" }),
            new IdentifyMatchHints(new Dictionary<string, string>(), [], "Friendship", null)));

        Assert.NotNull(result.Proposal);
        Assert.Equal("movie", result.Proposal.TargetKind);
        Assert.Equal("Friendship", result.Proposal.Patch.Title);
        Assert.Equal("1239655", result.Proposal.Patch.ExternalIds["tmdb"]);
        Assert.Contains("Comedy", result.Proposal.Patch.Tags);
        Assert.Equal("2025-05-09", result.Proposal.Patch.Dates["release"]);
    }

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

    [Fact]
    public async Task SeasonIdentifyRepairsDuplicateEpisodeNumbersAndMapsEpisodeStills() {
        using var http = new HttpClient(new StubHandler(request => {
            if (request.RequestUri?.AbsolutePath == "/3/tv/207/season/1") {
                return """
                    {
                      "id": 668,
                      "season_number": 1,
                      "name": "Season 1",
                      "air_date": "1997-10-20",
                      "poster_path": "/season.jpg",
                      "episodes": [
                        {
                          "id": 12077,
                          "episode_number": 2,
                          "name": "Water, Water Everywhere",
                          "overview": "Episode two.",
                          "air_date": "1997-10-21",
                          "still_path": null,
                          "runtime": 25,
                          "guest_stars": [],
                          "crew": []
                        },
                        {
                          "id": 12077,
                          "episode_number": 2,
                          "name": "Water, Water Everywhere",
                          "overview": "Episode two duplicate.",
                          "air_date": "1997-10-21",
                          "still_path": null,
                          "runtime": 25,
                          "guest_stars": [],
                          "crew": []
                        }
                      ]
                    }
                    """;
            }

            if (request.RequestUri?.AbsolutePath == "/3/tv/207/season/1/episode/1") {
                Assert.Contains("append_to_response=credits%2Cimages", request.RequestUri.Query);
                return """
                    {
                      "id": 1107384,
                      "episode_number": 1,
                      "name": "Home Is Where the Bear Is",
                      "overview": "Episode one.",
                      "air_date": "1997-10-20",
                      "still_path": null,
                      "runtime": 25,
                      "guest_stars": [],
                      "crew": [],
                      "images": {
                        "stills": [
                          {
                            "file_path": "/episode-one.jpg",
                            "width": 1280,
                            "height": 720,
                            "iso_639_1": null,
                            "vote_average": 5.5
                          }
                        ]
                      }
                    }
                    """;
            }

            throw new InvalidOperationException($"Unexpected TMDB request {request.RequestUri}");
        }));
        var plugin = new TmdbPlugin(new TmdbApiClient(http, "test-key"));

        var result = await plugin.IdentifyAsync(new IdentifyPluginRequest(
            "lookup-id",
            new Dictionary<string, string>(),
            new IdentifyEntitySnapshot(
                Guid.NewGuid(),
                "video-season",
                "Season 1"),
            new IdentifyQuery(null, null, null),
            new IdentifyMatchHints(new Dictionary<string, string>(), [], "Season 1", null),
            new IdentifyStructuralContext(
                [
                    new IdentifyEntitySnapshot(
                        Guid.NewGuid(),
                        "video-series",
                        "Bear in the Big Blue House",
                        new Dictionary<string, string> { ["tmdb"] = "207" })
                ],
                new Dictionary<string, int> { ["season"] = 1 })));

        Assert.NotNull(result.Proposal);
        var proposal = result.Proposal;
        Assert.Equal(
            ["Home Is Where the Bear Is", "Water, Water Everywhere"],
            proposal.Children.Select(child => child.Patch.Title ?? string.Empty).ToArray());
        var episodeOneImage = Assert.Single(proposal.Children[0].Images);
        Assert.Equal("still", episodeOneImage.Kind);
        Assert.Equal("https://image.tmdb.org/t/p/original/episode-one.jpg", episodeOneImage.Url);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
    }
}
