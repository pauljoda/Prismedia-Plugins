public sealed class AniListProposalTests {
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
    }

    [Fact]
    public void FlatSeasonProposalUsesAbsoluteEpisodeNumbersAcrossParts() {
        var first = MediaFixture(id: 1, episodes: 24);
        var second = MediaFixture(id: 2, episodes: 23);

        var proposal = AniListPlugin.FlatSeasonShell([first, second], Guid.NewGuid());

        Assert.Equal("video-season", proposal.TargetKind);
        Assert.Equal(0, proposal.Patch.Positions["seasonNumber"]);
        Assert.Equal(47, proposal.Children.Count);
        Assert.Equal("1", proposal.Children[0].Patch.ExternalIds["anilist"]);
        Assert.Equal(1, proposal.Children[0].Patch.Positions["episodeNumber"]);
        Assert.Equal("2", proposal.Children[24].Patch.ExternalIds["anilist"]);
        Assert.Equal(25, proposal.Children[24].Patch.Positions["episodeNumber"]);
        Assert.Equal(25, proposal.Children[24].Patch.Positions["sortOrder"]);
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
}
