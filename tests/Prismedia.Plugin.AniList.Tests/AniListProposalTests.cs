using System.Net;
using System.Text;

public sealed class AniListProposalTests {
    [Fact]
    public void SearchInputUsesManifestFieldsWithoutLegacyTitle() {
        var request = new IdentifyPluginRequest(
            2,
            "search",
            new Dictionary<string, string>(),
            new IdentifyEntitySnapshot(Guid.NewGuid(), "video-series", "Stale title"),
            new IdentifyQuery(
                null,
                null,
                null,
                Fields: new Dictionary<string, string> {
                    ["seriesTitle"] = "Frieren",
                    ["year"] = "2023"
                }),
            new IdentifyMatchHints(new Dictionary<string, string>(), [], null, null));

        var input = AniListPlugin.SearchInput(request);

        Assert.Equal("Frieren", input.Title);
        Assert.Equal(2023, input.Year);
    }

    [Fact]
    public void SeriesProposalLeavesStructuralChildrenToLocalContext() {
        var media = MediaFixture(episodes: 24);

        var proposal = AniListPlugin.ToProposal(media, "video-series", Guid.NewGuid(), "external-id");

        Assert.Equal("video-series", proposal.TargetKind);
        Assert.Empty(proposal.Children);
        Assert.Equal("113415", proposal.Patch.ExternalIds["anilist"]);
    }

    [Fact]
    public void SeasonProposalCarriesSeasonPositionAndEpisodeChildren() {
        var media = MediaFixture(episodes: 12);

        var proposal = AniListPlugin.SeasonShell(media, 2, Guid.NewGuid(), "parent-context");

        Assert.Equal("video-season", proposal.TargetKind);
        Assert.Equal("Season 2", proposal.Patch.Title);
        Assert.Equal(2, proposal.Patch.Positions["seasonNumber"]);
        Assert.Equal(12, proposal.Children.Count);
        Assert.Equal("113415:2", proposal.Patch.ExternalIds["anilistseason"]);
        Assert.All(proposal.Children, child => Assert.Equal("video-episode", child.TargetKind));
        Assert.Equal(2, proposal.Children[5].Patch.Positions["seasonNumber"]);
        Assert.Equal(6, proposal.Children[5].Patch.Positions["episodeNumber"]);
    }

    [Fact]
    public void EpisodeProposalUsesEpisodeKindAndRuntime() {
        var media = MediaFixture(episodes: 24, runtime: 23);

        var proposal = AniListPlugin.EpisodeShell(media, 7, Guid.NewGuid(), 1, 7, "parent-context");

        Assert.Equal("video-episode", proposal.TargetKind);
        Assert.Equal("Episode 7", proposal.Patch.Title);
        Assert.Equal(1, proposal.Patch.Positions["seasonNumber"]);
        Assert.Equal(7, proposal.Patch.Positions["episodeNumber"]);
        Assert.Equal(7, proposal.Patch.Positions["sortOrder"]);
        Assert.Equal(23, proposal.Patch.Stats["runtimeMinutes"]);
        Assert.Equal("113415:7:1:7", proposal.Patch.ExternalIds["anilistepisode"]);
        Assert.DoesNotContain("anilist", proposal.Patch.ExternalIds.Keys);
    }

    [Fact]
    public void FlatSeasonProposalUsesAbsoluteEpisodeNumbersAcrossParts() {
        var first = MediaFixture(id: 1, episodes: 24);
        var second = MediaFixture(id: 2, episodes: 23);

        var proposal = AniListPlugin.FlatSeasonShell([first, second], Guid.NewGuid());

        Assert.Equal("video-season", proposal.TargetKind);
        Assert.Equal(0, proposal.Patch.Positions["seasonNumber"]);
        Assert.Equal(47, proposal.Children.Count);
        Assert.DoesNotContain("anilist", proposal.Children[0].Patch.ExternalIds.Keys);
        Assert.Equal(1, proposal.Children[0].Patch.Positions["episodeNumber"]);
        Assert.DoesNotContain("anilist", proposal.Children[24].Patch.ExternalIds.Keys);
        Assert.Equal(25, proposal.Children[24].Patch.Positions["episodeNumber"]);
        Assert.Equal(25, proposal.Children[24].Patch.Positions["sortOrder"]);
    }

    [Fact]
    public async Task SeriesHydratesSeasonsOnlyWhenStructuralChildrenAreRequested() {
        var media = MediaFixture(episodes: 12);

        var withoutChildren = await AniListPlugin.ToProposalWithChildrenAsync(
            media, "video-series", Guid.NewGuid(), "external-id", includeStructuralChildren: false);
        var withChildren = await AniListPlugin.ToProposalWithChildrenAsync(
            media, "video-series", Guid.NewGuid(), "external-id", includeStructuralChildren: true);

        Assert.Empty(withoutChildren.Children);
        var season = Assert.Single(withChildren.Children);
        Assert.Equal("video-season", season.TargetKind);
        Assert.Equal("113415:1", season.Patch.ExternalIds["anilistseason"]);
    }

    [Fact]
    public async Task SeasonAndEpisodeStructuralIdentitiesRoundTripWithoutContext() {
        var previous = AniListPlugin.Http;
        using var http = new HttpClient(new StubHandler("""
            {
              "data": {
                "media": {
                  "id": 113415,
                  "idMal": 40748,
                  "description": "A boy fights curses.",
                  "format": "TV",
                  "episodes": 12,
                  "duration": 24,
                  "popularity": 918115,
                  "siteUrl": "https://anilist.co/anime/113415",
                  "title": { "romaji": "Jujutsu Kaisen", "english": "JUJUTSU KAISEN", "native": "呪術廻戦" },
                  "startDate": { "year": 2020, "month": 10, "day": 3 },
                  "endDate": { "year": 2021, "month": 3, "day": 27 },
                  "coverImage": { "extraLarge": "https://example.test/poster-large.jpg", "large": "https://example.test/poster.jpg" },
                  "genres": ["Action"],
                  "tags": [],
                  "studios": { "nodes": [] },
                  "characters": { "edges": [] },
                  "relations": { "edges": [] }
                }
              }
            }
            """));
        AniListPlugin.Http = http;
        try {
            var emittedSeason = AniListPlugin.SeasonShell(MediaFixture(episodes: 12), 2, null, "series-children");
            var seasonIdentity = emittedSeason.Patch.ExternalIds["anilistseason"];
            var resolvedSeason = Assert.IsType<EntityMetadataProposal>((await AniListPlugin.IdentifyAsync(
                Lookup("video-season", "anilistseason", seasonIdentity))).Proposal);
            Assert.Equal(emittedSeason.ProposalId, resolvedSeason.ProposalId);
            Assert.Equal(seasonIdentity, resolvedSeason.Patch.ExternalIds["anilistseason"]);

            var emittedEpisode = emittedSeason.Children[6];
            var episodeIdentity = emittedEpisode.Patch.ExternalIds["anilistepisode"];
            var resolvedEpisode = Assert.IsType<EntityMetadataProposal>((await AniListPlugin.IdentifyAsync(
                Lookup("video", "anilistepisode", episodeIdentity))).Proposal);
            Assert.Equal(emittedEpisode.ProposalId, resolvedEpisode.ProposalId);
            Assert.Equal("video-episode", resolvedEpisode.TargetKind);
            Assert.Equal(episodeIdentity, resolvedEpisode.Patch.ExternalIds["anilistepisode"]);
        } finally {
            AniListPlugin.Http = previous;
        }
    }

    private static IdentifyPluginRequest Lookup(string kind, string identityNamespace, string value) {
        var ids = new Dictionary<string, string> { [identityNamespace] = value };
        return new IdentifyPluginRequest(
            2,
            "lookup-id",
            new Dictionary<string, string>(),
            new IdentifyEntitySnapshot(Guid.NewGuid(), kind, string.Empty, ids),
            new IdentifyQuery(null, null, ids),
            new IdentifyMatchHints(ids, [], null, null));
    }

    private static AniListPlugin.Media MediaFixture(int id = 113415, int episodes = 24, int runtime = 24) =>
        new(
            Id: id,
            IdMal: 40748,
            Title: new AniListPlugin.MediaTitle("Jujutsu Kaisen", "JUJUTSU KAISEN", "呪術廻戦"),
            Description: "A boy fights curses.",
            Format: "TV",
            Episodes: episodes,
            Duration: runtime,
            StartDate: new AniListPlugin.FuzzyDate(2020, 10, 3),
            EndDate: new AniListPlugin.FuzzyDate(2021, 3, 27),
            CoverImage: new AniListPlugin.Image("https://example.test/poster-large.jpg", "https://example.test/poster.jpg", null, null),
            BannerImage: "https://example.test/banner.jpg",
            Popularity: 918115,
            Genres: ["Action"],
            Tags: [new AniListPlugin.MediaTag("Shounen", 80)],
            Studios: new AniListPlugin.StudioConnection([new AniListPlugin.Studio("MAPPA", true)]),
            SiteUrl: "https://anilist.co/anime/113415",
            Characters: null);

    private sealed class StubHandler(string response) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
    }
}
