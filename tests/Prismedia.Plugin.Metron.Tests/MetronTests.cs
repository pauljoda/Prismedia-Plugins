using System.Net;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Metadata;

namespace Prismedia.Plugin.Metron.Tests;

public sealed class MetronTests
{
    [Fact]
    public async Task SearchKeepsDifferentRunsAsChoicesAndSendsExactFilters()
    {
        using var http = Client(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
            Assert.Contains("name=Example", request.RequestUri!.Query);
            Assert.Contains("year_began=2020", request.RequestUri.Query);
            Assert.Contains("language=en", request.RequestUri.Query);
            return Json(Page(Series(1), Series(2)));
        });
        var result = await Plugin(http).IdentifyAsync(Request(MetronCodes.SeriesKind, fields: new()
        {
            [MetronCodes.SeriesTitle] = "Example",
            [MetronCodes.Year] = "2020",
            [MetronCodes.Language] = "en"
        }));
        Assert.Null(result.Proposal); Assert.Equal(2, result.Candidates.Count);
        Assert.NotEqual(result.Candidates[0].ExternalIds[MetronCodes.SeriesIdentity], result.Candidates[1].ExternalIds[MetronCodes.SeriesIdentity]);
    }

    [Theory]
    [InlineData("½")]
    [InlineData("12.5")]
    [InlineData("12A")]
    [InlineData("Annual 1")]
    public async Task IssueIdentityAndExactDesignationSurviveLookupAndSerialization(string designation)
    {
        using var http = Client(_ => Json(Issue(50, designation)));
        var result = await Plugin(http).IdentifyAsync(Request(MetronCodes.IssueKind, id: "50"));
        var proposal = Assert.IsType<EntityMetadataProposal>(result.Proposal);
        Assert.Equal("50", proposal.Patch.ExternalIds[MetronCodes.IssueIdentity]);
        Assert.Equal("4000-999", proposal.Patch.ExternalIds[MetronCodes.ComicVineIdentity]);
        Assert.Equal("123", proposal.Patch.ExternalIds[MetronCodes.GcdIssueIdentity]);
        Assert.DoesNotContain(MetronCodes.SeriesIdentity, proposal.Patch.ExternalIds.Keys);
        var position = Assert.Single(proposal.Patch.PositionEntries!);
        Assert.Equal(designation, position.Label); Assert.Equal(1, position.Value);
        Assert.Empty(proposal.Patch.Positions);
        Assert.Equal("2020-09-02", proposal.Patch.Dates[MetronCodes.PublicationDate]);
        Assert.DoesNotContain("2020-11-01", proposal.Patch.Dates.Values);
        Assert.Equal(32, proposal.Patch.Stats[MetronCodes.PageCount]);
        Assert.Contains(proposal.Patch.Credits, credit => credit.Name == "Writer One" && credit.Role == MetronCodes.WriterRole);
        Assert.Contains(proposal.Patch.Credits, credit => credit.Name == "Artist Two" && credit.Role == MetronCodes.ArtistRole);
        Assert.DoesNotContain("<p>", proposal.Patch.Description);
        Assert.Equal("https://static.metron.cloud/media/cover.jpg", Assert.Single(proposal.Images).Url);
        var roundtrip = JsonSerializer.Deserialize<EntityMetadataProposal>(JsonSerializer.Serialize(proposal, MetronClient.ProtocolJson), MetronClient.ProtocolJson)!;
        Assert.Equal(designation, Assert.Single(roundtrip.Patch.PositionEntries!).Label);
    }

    [Fact]
    public async Task IssueSearchRejectsWrongNumbersAndRunsAndHonorsRequestedLimit()
    {
        using var http = Client(request =>
        {
            Assert.Contains("number=12.5", request.RequestUri!.Query);
            Assert.Contains("series_year_began=2020", request.RequestUri.Query);
            return Json(Page(Issue(1, "12"), Issue(2, "12.5"), Issue(3, "12.5").Replace("\"year_began\":2020", "\"year_began\":2000"), Issue(4, "12.5")));
        });
        var request = Request(MetronCodes.IssueKind, fields: new() { [MetronCodes.SeriesTitle] = "Example", [MetronCodes.IssueNumber] = "12.5", [MetronCodes.Year] = "2020" });
        var result = await Plugin(http).IdentifyAsync(request with { Query = request.Query with { Limit = 1 } });
        Assert.Equal("2", Assert.Single(result.Candidates).ExternalIds[MetronCodes.IssueIdentity]);
    }

    [Fact]
    public async Task SeriesChildrenAreCompleteOrderedAndNeverInventVolumeEntitiesOrVariants()
    {
        var calls = 0; var delays = new List<TimeSpan>();
        using var http = Client(request =>
        {
            calls++;
            return request.RequestUri!.AbsolutePath == "/api/series/1/" ? Json(Series(1))
                : request.RequestUri.Query == "?page=1" ? Json(PageWithNext(3, "https://metron.cloud/api/series/1/issue_list/?page=2", Issue(52, "12.5"), Issue(51, "12")))
                : Json(PageWithNext(3, null, Issue(50, "½")));
        });
        var plugin = new MetronPlugin(http, (wait, _) => { delays.Add(wait); return Task.CompletedTask; });
        var proposal = (await plugin.IdentifyAsync(Request(MetronCodes.SeriesKind, id: "1", children: true))).Proposal!;
        Assert.Equal(3, calls); Assert.Equal(2, delays.Count); Assert.All(delays, wait => Assert.True(wait.TotalMilliseconds >= 3100));
        Assert.Equal(["½", "12", "12.5"], proposal.Children.Select(child => Assert.Single(child.Patch.PositionEntries!).Label));
        Assert.Equal([1, 2, 3], proposal.Children.Select(child => Assert.Single(child.Patch.PositionEntries!).Value));
        Assert.All(proposal.Children, child => { Assert.Equal(MetronCodes.IssueKind, child.TargetKind); Assert.Empty(child.Children); });
        Assert.Equal("4050-200", proposal.Patch.ExternalIds[MetronCodes.ComicVineIdentity]);
        Assert.Equal(["Other title"], proposal.Patch.AlternativeTitles);
        Assert.Equal("2020", proposal.Patch.Dates[MetronCodes.PublicationDate]);
    }

    [Fact]
    public async Task SeriesLookupWithoutChildrenDoesNotFetchIssues()
    {
        var calls = 0;
        using var http = Client(_ => { calls++; return Json(Series(1)); });
        Assert.Empty((await Plugin(http).IdentifyAsync(Request(MetronCodes.SeriesKind, id: "1"))).Proposal!.Children);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MissingAlternativeNamesAndPublicationDatesDoNotInventClearingOrCoverDateFallbacks()
    {
        using var seriesHttp = Client(_ => Json(Series(1).Replace("[\"Other title\"]", "[]")));
        var series = (await Plugin(seriesHttp).IdentifyAsync(Request(MetronCodes.SeriesKind, id: "1"))).Proposal!;
        Assert.Null(series.Patch.AlternativeTitles);
        Assert.DoesNotContain("alternativeTitles", JsonSerializer.Serialize(series.Patch, MetronClient.ProtocolJson));
        using var issueHttp = Client(_ => Json(Issue(50, "1").Replace("\"2020-09-02\"", "null")));
        Assert.Empty((await Plugin(issueHttp).IdentifyAsync(Request(MetronCodes.IssueKind, id: "50"))).Proposal!.Patch.Dates);
    }

    [Fact]
    public async Task ExplicitIdAndUrlConflictFailsBeforeNetworkAccess()
    {
        using var http = Client(_ => throw new Exception("Network must not be used"));
        var request = Request(MetronCodes.IssueKind, id: "50");
        await Assert.ThrowsAsync<ArgumentException>(() => Plugin(http).IdentifyAsync(request with
        {
            Query = request.Query with { Url = "https://metron.cloud/api/issue/51/" }
        }));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(302)]
    public async Task FailedExactLookupsNeverFallBackToTitleSearchOrRetry(int status)
    {
        var calls = 0;
        using var http = Client(_ => { calls++; return new((HttpStatusCode)status); });
        var request = Request(MetronCodes.IssueKind, id: "50") with { Query = new("A tempting title", null, new Dictionary<string, string> { { MetronCodes.IssueIdentity, "50" } }) };
        if (status == 404) Assert.Null((await Plugin(http).IdentifyAsync(request)).Proposal);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Plugin(http).IdentifyAsync(request));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("../issue/1")]
    [InlineData("01")]
    [InlineData("1?key=secret")]
    public async Task InvalidIdsAreRejectedBeforeNetworkAccess(string id)
    {
        using var http = Client(_ => throw new Exception("Network must not be used"));
        await Assert.ThrowsAsync<ArgumentException>(() => Plugin(http).IdentifyAsync(Request(MetronCodes.IssueKind, id: id)));
    }

    [Theory]
    [InlineData("http://metron.cloud/api/issue/1/")]
    [InlineData("https://metron.cloud.example/api/issue/1/")]
    [InlineData("https://user@metron.cloud/api/issue/1/")]
    [InlineData("https://metron.cloud/api/series/1/")]
    [InlineData("https://metron.cloud/issue/a-slug/")]
    public async Task UnsupportedUrlsCannotReceiveCredentials(string url)
    {
        using var http = Client(_ => throw new Exception("Network must not be used"));
        var request = Request(MetronCodes.IssueKind) with { Action = MetronCodes.LookupUrl, Query = new(null, url, null) };
        await Assert.ThrowsAsync<ArgumentException>(() => Plugin(http).IdentifyAsync(request));
    }

    [Fact]
    public async Task NumericApiUrlsResolveOnlyTheirExactRecord()
    {
        using var http = Client(request => { Assert.Equal("/api/issue/50/", request.RequestUri!.AbsolutePath); return Json(Issue(50, "1")); });
        var request = Request(MetronCodes.IssueKind) with { Action = MetronCodes.LookupUrl, Query = new(null, "https://metron.cloud/api/issue/50/", null) };
        Assert.Equal("50", (await Plugin(http).IdentifyAsync(request)).Proposal!.Patch.ExternalIds[MetronCodes.IssueIdentity]);
    }

    [Fact]
    public async Task WrongReturnedIdentityAndConflictingParentRunAreRejected()
    {
        using var http = Client(_ => Json(Issue(50, "1")));
        await Assert.ThrowsAsync<InvalidDataException>(() => Plugin(http).IdentifyAsync(Request(MetronCodes.IssueKind, id: "51")));
        var request = Request(MetronCodes.IssueKind, id: "50") with { StructuralContext = new([new(Guid.NewGuid(), MetronCodes.SeriesKind, "Other run", new Dictionary<string, string> { { MetronCodes.SeriesIdentity, "2" } })], new Dictionary<string, int>()) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Plugin(http).IdentifyAsync(request));
    }

    [Theory]
    [InlineData("other-origin")]
    [InlineData("duplicate")]
    [InlineData("wrong-series")]
    [InlineData("incomplete")]
    [InlineData("oversized")]
    public async Task InvalidSeriesPaginationNeverPublishesPartialChildren(string scenario)
    {
        using var http = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/series/1/") return Json(Series(1));
            return scenario switch
            {
                "other-origin" => Json(PageWithNext(2, "https://untrusted.example/api/series/1/issue_list/?page=2", Issue(1, "1"))),
                "duplicate" => Json(Page(Issue(1, "1"), Issue(1, "1"))),
                "wrong-series" => Json(Page(Issue(1, "1").Replace("\"series\":{\"id\":1", "\"series\":{\"id\":2"))),
                "incomplete" => Json(PageWithNext(2, null, Issue(1, "1"))),
                _ => Json(PageWithNext(501, null, Issue(1, "1")))
            };
        });
        if (scenario == "oversized")
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Plugin(http).IdentifyAsync(Request(MetronCodes.SeriesKind, id: "1", children: true)));
            Assert.Contains("review limit", error.Message);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Plugin(http).IdentifyAsync(Request(MetronCodes.SeriesKind, id: "1", children: true)));
        }
    }

    [Theory]
    [InlineData(MetronCodes.BurstRemaining, MetronCodes.BurstReset)]
    [InlineData(MetronCodes.SustainedRemaining, MetronCodes.SustainedReset)]
    public async Task ExhaustedQuotaStopsAdditionalCalls(string remaining, string reset)
    {
        var calls = 0;
        using var http = Client(_ =>
        {
            calls++; var response = Json(Series(1)); response.Headers.Add(remaining, "0");
            response.Headers.Add(reset, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString()); return response;
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Plugin(http).IdentifyAsync(Request(MetronCodes.SeriesKind, id: "1", children: true)));
        Assert.Contains("rate limited", error.Message); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MissingCredentialsFailBeforeNetworkAccess()
    {
        using var http = Client(_ => throw new Exception("Network must not be used"));
        var request = Request(MetronCodes.SeriesKind, id: "1") with { Auth = new Dictionary<string, string>() };
        Assert.Contains("API token", (await Assert.ThrowsAsync<ArgumentException>(() => Plugin(http).IdentifyAsync(request))).Message);
    }

    private static MetronPlugin Plugin(HttpClient http) => new(http, (_, _) => Task.CompletedTask);
    private static IdentifyPluginRequest Request(string kind, string? id = null, Dictionary<string, string>? fields = null, bool children = false) => new(2,
        id is null ? MetronCodes.Search : MetronCodes.LookupId, new Dictionary<string, string> { { MetronCodes.Token, "fixture-token" } }, new(Guid.NewGuid(), kind, "Example"),
        new(null, null, id is null ? null : new Dictionary<string, string> { { kind == MetronCodes.SeriesKind ? MetronCodes.SeriesIdentity : MetronCodes.IssueIdentity, id } }, Fields: fields),
        new(new Dictionary<string, string>(), [], null, null), IncludeStructuralChildren: children);
    private static string Series(int id) => $$"""{"id":{{id}},"name":"Example","series":"Example (2020)","year_began":2020,"volume":1,"issue_count":3,"publisher":{"id":3,"name":"Fixture Press"},"language":"en","alt_names":["Other title"],"cv_id":200,"gcd_id":300,"resource_url":"https://metron.cloud/series/example/"}""";
    private static string Issue(int id, string number) => $$"""{"id":{{id}},"series":{"id":1,"name":"Example","volume":1,"year_began":2020,"language":"en"},"number":{{JsonSerializer.Serialize(number)}},"desc":"<p>A &amp; B</p>","page":32,"store_date":"2020-09-02","cover_date":"2020-11-01","image":"https://static.metron.cloud/media/cover.jpg","cv_id":999,"gcd_id":123,"credits":[{"id":4,"creator":"Writer One","role":[{"id":1,"name":"Writer"}]},{"id":5,"creator":"Artist Two","role":[{"id":2,"name":"Penciller"},{"id":3,"name":"Inker"}]}],"variants":[{"name":"Alternate cover","image":"https://static.metron.cloud/media/alternate.jpg"}]}""";
    private static string Page(params string[] items) => PageWithNext(items.Length, null, items);
    private static string PageWithNext(int count, string? next, params string[] items) => $"{{\"count\":{count},\"next\":{JsonSerializer.Serialize(next)},\"results\":[{string.Join(',', items)}]}}";
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) => new(new Handler(response));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
}
