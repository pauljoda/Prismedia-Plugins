using System.Net;
using System.Text;
using System.Text.Json;

public sealed partial class AniListProposalTests {
    [Fact]
    public async Task FullModeOmitsAdultFilterInsteadOfPassingNull() {
        await WithMangaResponse(new { data = new { Page = new { media = new[] { MangaFixture(), MangaFixture(id: 9, adult: true) } } } }, async wire => {
            var request = MangaRequest("search") with { Query = new("Manga fixture", null, null), IncludeNsfw = true };
            Assert.Equal(2, (await AniListPlugin.IdentifyAsync(request)).Candidates.Count);
            var sent = JsonDocument.Parse(Assert.Single(wire)).RootElement;
            Assert.DoesNotContain("isAdult:", sent.GetProperty("query").GetString()!);
            Assert.False(sent.GetProperty("variables").TryGetProperty("isAdult", out _));
        });
    }

    [Fact]
    public async Task MangaSearchUsesPublicationYearAndFiltersWrongMediaAndAdultResults() {
        var request = MangaRequest("search") with { Query = new("Wrong legacy title", null, null, Fields: new Dictionary<string, string> { ["seriesTitle"] = "Manga fixture", ["year"] = "2020" }, Limit: 500) };
        await WithMangaResponse(new { data = new { Page = new { media = new[] {
            MangaFixture(), MangaFixture(2, format: "NOVEL"), MangaFixture(3, type: "ANIME"), MangaFixture(4, adult: true), MangaFixture(5, year: 2019)
        } } } }, async wire => {
            var result = await AniListPlugin.IdentifyAsync(request);
            var candidate = Assert.Single(result.Candidates);
            Assert.Equal("30002", candidate.ExternalIds["anilist"]);
            Assert.Null(result.Proposal);
            var sent = JsonDocument.Parse(Assert.Single(wire)).RootElement;
            Assert.Equal("Manga fixture", sent.GetProperty("variables").GetProperty("search").GetString());
            Assert.Equal(50, sent.GetProperty("variables").GetProperty("perPage").GetInt32());
            Assert.Equal("2020%", sent.GetProperty("variables").GetProperty("year").GetString());
            var query = sent.GetProperty("query").GetString()!;
            Assert.Contains("type: MANGA", query); Assert.Contains("startDate_like: $year", query);
            Assert.DoesNotContain("seasonYear", query); Assert.Contains("ONE_SHOT", query);
        });
    }

    [Fact]
    public async Task MangaLookupPreservesWorkIdentityAndNeverInventsCountBasedChildren() {
        await WithMangaResponse(new { data = new { Media = MangaFixture() } }, async wire => {
            var request = MangaRequest("lookup-id") with { Query = new(null, null, new Dictionary<string, string> { ["anilist"] = "30002" }), IncludeStructuralChildren = true };
            var result = await AniListPlugin.IdentifyAsync(request);
            var proposal = Assert.IsType<EntityMetadataProposal>(result.Proposal);
            Assert.Equal("comic-series", proposal.TargetKind); Assert.Equal(request.Entity.Id, proposal.TargetEntityId);
            Assert.Equal("30002", proposal.Patch.ExternalIds["anilist"]); Assert.Equal("2", proposal.Patch.ExternalIds["mal"]);
            Assert.Empty(proposal.Children); Assert.Empty(proposal.Relationships!); Assert.Empty(proposal.Patch.Positions);
            Assert.Null(proposal.Patch.Studio);
            Assert.Equal("2020", proposal.Patch.Dates["published"]);
            Assert.DoesNotContain("runtimeMinutes", proposal.Patch.Stats.Keys);
            Assert.Equal(123, proposal.Patch.Stats["chapterCount"]);
            var creator = Assert.Single(proposal.Patch.Credits);
            Assert.Equal("Fixture Author", creator.Name); Assert.Equal("creator", creator.Role);
            Assert.Single(wire);
        });
    }

    [Theory]
    [InlineData("https://anilist.co/manga/30002/Fixture")]
    [InlineData("https://www.anilist.co/manga/30002/")]
    public async Task MangaUrlsRoundTrip(string url) {
        await WithMangaResponse(new { data = new { Media = MangaFixture() } }, async wire => {
            var result = await AniListPlugin.IdentifyAsync(MangaRequest("lookup-url") with { Query = new(null, url, null) });
            Assert.Equal("30002", result.Proposal!.Patch.ExternalIds["anilist"]);
            Assert.Single(wire);
        });
    }

    [Theory]
    [InlineData("https://anilist.co/anime/30002")]
    [InlineData("https://example.test/anilist.co/manga/30002")]
    [InlineData("https://anilist.co.evil.test/manga/30002")]
    [InlineData("https://user@anilist.co/manga/30002")]
    [InlineData("https://anilist.co/manga/30002garbage")]
    public async Task WrongOrMalformedMangaUrlsDoNotFallBackToTitleSearch(string url) {
        await WithMangaResponse(new { data = new { Media = MangaFixture() } }, async wire => {
            var result = await AniListPlugin.IdentifyAsync(MangaRequest("lookup-url") with { Query = new("Fallback", url, null) });
            Assert.Null(result.Proposal); Assert.Empty(result.Candidates); Assert.Empty(wire);
        });
    }

    [Theory]
    [InlineData("ANIME", "MANGA", false)]
    [InlineData("MANGA", "NOVEL", false)]
    [InlineData("MANGA", "MANGA", true)]
    public async Task MangaLookupRejectsWrongTypesAndHiddenAdultMetadata(string type, string format, bool adult) {
        await WithMangaResponse(new { data = new { Media = MangaFixture(type: type, format: format, adult: adult) } }, async _ => {
            var result = await AniListPlugin.IdentifyAsync(MangaRequest("lookup-id") with { Query = new(null, null, new Dictionary<string, string> { ["anilist"] = "30002" }) });
            Assert.Null(result.Proposal); Assert.Empty(result.Candidates);
        });
    }

    [Fact]
    public async Task AnExplicitAdultLookupMarksNsfwAndPreservesItsIdentity() {
        await WithMangaResponse(new { data = new { Media = MangaFixture(adult: true) } }, async _ => {
            var request = MangaRequest("lookup-id") with { Query = new(null, null, new Dictionary<string, string> { ["anilist"] = "30002" }), IncludeNsfw = true };
            var result = await AniListPlugin.IdentifyAsync(request);
            Assert.True(result.Proposal!.Patch.Flags!.IsNsfw);
        });
    }

    [Fact]
    public async Task LookupCannotAcceptAnotherIdentityReturnedByTheServer() {
        await WithMangaResponse(new { data = new { Media = MangaFixture(id: 7) } }, async _ => {
            var result = await AniListPlugin.IdentifyAsync(MangaRequest("lookup-id") with { Query = new(null, null, new Dictionary<string, string> { ["anilist"] = "30002" }) });
            Assert.Null(result.Proposal);
        });
    }

    private static IdentifyPluginRequest MangaRequest(string action) => new(2, action, new Dictionary<string, string>(),
        new(Guid.NewGuid(), "comic-series", "Manga fixture"), new(null, null, null), new(new Dictionary<string, string>(), [], null, null));

    private static object MangaFixture(int id = 30002, string type = "MANGA", string format = "MANGA", bool adult = false, int year = 2020) => new {
        id, idMal = 2, type, format, isAdult = adult, title = new { english = "Fixture", romaji = "Fixture Romaji", native = "原作" },
        description = "<p>A manga fixture.</p>", startDate = new { year }, chapters = 123, volumes = 14,
        genres = new[] { "Fantasy" }, tags = Array.Empty<object>(), coverImage = new { large = "https://example.test/cover.jpg" },
        staff = new { edges = new[] { new { role = "Story & Art", node = new { name = new { full = "Fixture Author" } } }, new { role = "Lettering", node = new { name = new { full = "Unrelated Staff" } } } } }
    };

    private static async Task WithMangaResponse(object response, Func<List<string>, Task> test, bool unknownLength = false) {
        var previous = AniListPlugin.Http; var sent = new List<string>();
        using var http = new HttpClient(new RecordingMangaHandler(JsonSerializer.Serialize(response), sent, unknownLength));
        AniListPlugin.Http = http;
        try { await test(sent); } finally { AniListPlugin.Http = previous; }
    }
    private sealed class RecordingMangaHandler(string response, List<string> sent, bool unknownLength) : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            sent.Add(await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = unknownLength
                ? new StreamContent(new NonSeekableContentStream(Encoding.UTF8.GetBytes(response)))
                : new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class NonSeekableContentStream(byte[] bytes) : MemoryStream(bytes) {
        public override bool CanSeek => false;
    }
}

public sealed partial class AniListProposalTests {
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("030002")]
    [InlineData("30002 ")]
    [InlineData("30002:1")]
    public async Task InvalidWorkIdentifiersCannotBecomeTitleSearches(string id) {
        await WithMangaResponse(new { data = new { Media = MangaFixture() } }, async wire => {
            var request = MangaRequest("lookup-id") with { Query = new("Fallback", null, new Dictionary<string, string> { ["anilist"] = id }) };
            var result = await AniListPlugin.IdentifyAsync(request);
            Assert.Null(result.Proposal); Assert.Empty(result.Candidates); Assert.Empty(wire);
        });
    }

    [Fact]
    public async Task OneShotsRemainWorkLevelProposalsAndKeepFormalAlternativeTitles() {
        await WithMangaResponse(new { data = new { Media = MangaFixture(format: "ONE_SHOT") } }, async _ => {
            var result = await AniListPlugin.IdentifyAsync(MangaRequest("lookup-id") with { Query = new(null, null, new Dictionary<string, string> { ["anilist"] = "30002" }) });
            Assert.Equal("comic-series", result.Proposal!.TargetKind);
            Assert.Equal(new[] { "Fixture Romaji", "原作" }, result.Proposal.Patch.AlternativeTitles);
            Assert.Empty(result.Proposal.Children);
        });
    }

    [Theory]
    [InlineData(2020, null, null, "2020")]
    [InlineData(2020, 5, null, "2020-05")]
    [InlineData(2020, 2, 29, "2020-02-29")]
    [InlineData(2021, 2, 29, "2021-02")]
    public void FuzzyDatesDoNotInventPrecision(int year, int? month, int? day, string expected) =>
        Assert.Equal(expected, AniListPlugin.PublicationDate(new(year, month, day)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOversizedResponseIsRejectedBeforeMetadataIsAccepted(bool unknownLength) {
        await WithMangaResponse(new { data = new { padding = new string('x', 8 * 1024 * 1024 + 1), Media = MangaFixture() } }, async _ => {
            await Assert.ThrowsAsync<InvalidOperationException>(() => AniListPlugin.IdentifyAsync(MangaRequest("lookup-id") with { Query = new(null, null, new Dictionary<string, string> { ["anilist"] = "30002" }) }));
        }, unknownLength);
    }
}
