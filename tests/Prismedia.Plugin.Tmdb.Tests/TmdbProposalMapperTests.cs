using System.Net;
using System.Text;

namespace Prismedia.Plugin.Tmdb.Tests;

public sealed class TmdbProposalMapperTests {
    [Fact]
    public async Task TvProposalUsesSeasonShellsAndHydratedCreditRelationships() {
        using var http = new HttpClient(new StubHandler(request => {
            if (request.RequestUri?.AbsolutePath == "/3/person/2140873") {
                return """
                    {
                      "id": 2140873,
                      "name": "Quinta Brunson",
                      "biography": "Creator and performer.",
                      "profile_path": "/quinta-full.jpg",
                      "birthday": "1989-12-21",
                      "deathday": null,
                      "homepage": "https://example.test/quinta",
                      "imdb_id": "nm6421259",
                      "place_of_birth": "Philadelphia, Pennsylvania, USA",
                      "known_for_department": "Acting",
                      "popularity": 12.6,
                      "images": { "profiles": [{ "file_path": "/quinta-full.jpg", "width": 500, "height": 750 }] }
                    }
                    """;
            }

            throw new InvalidOperationException($"Unexpected TMDB request {request.RequestUri}");
        }));
        var mapper = new TmdbProposalMapper(new TmdbApiClient(http, "test-key"));
        var detail = new TmdbTvDetail(
            Id: 271267,
            Name: "Abbott Elementary",
            FirstAirDate: "2021-12-07",
            LastAirDate: "2026-05-06",
            Overview: "A workplace comedy.",
            PosterPath: null,
            BackdropPath: null,
            Genres: [],
            NumberOfSeasons: 5,
            NumberOfEpisodes: 72,
            Status: "Returning Series",
            Networks: [],
            ProductionCompanies: [],
            Seasons: [
                new TmdbSeasonSummary(176944, 1, 13, "Season 1", "First year.", "2021-12-07", "/season.jpg")
            ],
            Credits: new TmdbCredits(
                [new TmdbCast(2140873, "Quinta Brunson", "Janine Teagues", 0, "/quinta.jpg")],
                []),
            Images: null);

        var proposal = await mapper.TvToProposalAsync(detail, "external-id");

        var season = Assert.Single(proposal.Children);
        Assert.Equal("video-season", season.TargetKind);
        Assert.Equal(1, season.Patch.Positions["seasonNumber"]);
        Assert.Empty(season.Children);
        var person = Assert.Single(proposal.Relationships ?? [], row => row.TargetKind == "person");
        Assert.Equal("Quinta Brunson", person.Patch.Title);
        Assert.Equal("Creator and performer.", person.Patch.Description);
        Assert.Equal("2140873", person.Patch.ExternalIds["tmdb"]);
        Assert.Equal("nm6421259", person.Patch.ExternalIds["imdb"]);
        Assert.Contains("https://example.test/quinta", person.Patch.Urls);
        Assert.Equal("1989-12-21", person.Patch.Dates["birth"]);
        Assert.Equal(13, person.Patch.Stats["popularity"]);
        Assert.Equal("Acting", person.Patch.Classification);
    }

    [Fact]
    public async Task TvProposalUsesNetworkStudioWithoutHydratingThroughCompanyNamespace() {
        using var http = new HttpClient(new StubHandler(request => {
            if (request.RequestUri?.AbsolutePath == "/3/company/49") {
                return """
                    {
                      "id": 49,
                      "name": "El Deseo",
                      "description": "A different production company.",
                      "logo_path": "/eldeseo.png",
                      "homepage": "https://example.test/el-deseo",
                      "origin_country": "ES",
                      "images": { "logos": [{ "file_path": "/eldeseo.png", "width": 500, "height": 200 }] }
                    }
                    """;
            }

            throw new InvalidOperationException($"Unexpected TMDB request {request.RequestUri}");
        }));
        var mapper = new TmdbProposalMapper(new TmdbApiClient(http, "test-key"));
        var detail = new TmdbTvDetail(
            Id: 271267,
            Name: "The Chair Company",
            FirstAirDate: "2025-10-12",
            LastAirDate: "2025-11-30",
            Overview: "A series.",
            PosterPath: null,
            BackdropPath: null,
            Genres: [],
            NumberOfSeasons: 1,
            NumberOfEpisodes: 8,
            Status: "Returning Series",
            Networks: [new TmdbNamed(49, "HBO", "/hbo.png")],
            ProductionCompanies: [new TmdbNamed(49, "El Deseo", "/eldeseo.png")],
            Seasons: [],
            Credits: null,
            Images: null);

        var proposal = await mapper.TvToProposalAsync(detail, "test");

        Assert.Equal("HBO", proposal.Patch.Studio);
        var studio = Assert.Single(proposal.Relationships ?? [], row => row.TargetKind == "studio");
        Assert.Equal("HBO", studio.Patch.Title);
        Assert.Contains(studio.Patch.Urls, url => url == "https://www.themoviedb.org/network/49");
        Assert.Contains(studio.Images, image => image.Kind == "logo" && image.Url.EndsWith("/hbo.png", StringComparison.Ordinal));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
    }
}
