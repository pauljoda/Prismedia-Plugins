using System.Net;
using System.Text;

namespace Prismedia.Plugin.MangaDex.Tests;

public sealed class MangaDexIdentityRoundTripTests {
    private const string MangaId = "11111111-2222-3333-4444-555555555555";
    private const string ChapterId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";

    [Fact]
    public void VolumeCompositePreservesOpaqueCaseAndColons() {
        var identity = MangaDexPlugin.FormatVolumeIdentity(MangaId, "Vol:Case");

        Assert.True(MangaDexPlugin.TryParseVolumeIdentity(identity, out var mangaId, out var volume));
        Assert.Equal(MangaId, mangaId);
        Assert.Equal("Vol:Case", volume);
        Assert.False(MangaDexPlugin.TryParseVolumeIdentity(identity.ToLowerInvariant(), out _, out var lowerVolume)
            && lowerVolume == "Vol:Case");
    }

    [Fact]
    public async Task VolumeAndChapterStructuralIdentitiesRoundTripWithoutContext() {
        var previous = MangaDexPlugin.Http;
        using var http = new HttpClient(new StubHandler(ResponseFor));
        MangaDexPlugin.Http = http;
        try {
            var root = Assert.IsType<EntityMetadataProposal>((await MangaDexPlugin.IdentifyAsync(
                Lookup("comic-series", "mangadex", MangaId))).Proposal);
            Assert.Equal("comic-series", root.TargetKind);
            var emittedVolume = Assert.Single(root.Children);
            var volumeIdentity = emittedVolume.Patch.ExternalIds["mangadexvolume"];

            var resolvedVolume = Assert.IsType<EntityMetadataProposal>((await MangaDexPlugin.IdentifyAsync(
                Lookup("comic-volume", "mangadexvolume", volumeIdentity))).Proposal);
            Assert.Equal(emittedVolume.ProposalId, resolvedVolume.ProposalId);
            Assert.Equal("comic-volume", resolvedVolume.TargetKind);
            Assert.Equal(
                new Dictionary<string, string> { ["mangadexvolume"] = volumeIdentity },
                resolvedVolume.Patch.ExternalIds);

            var emittedChapter = Assert.Single(emittedVolume.Children);
            var chapterIdentity = emittedChapter.Patch.ExternalIds["mangadexchapter"];
            var resolvedChapter = Assert.IsType<EntityMetadataProposal>((await MangaDexPlugin.IdentifyAsync(
                Lookup("comic-installment", "mangadexchapter", chapterIdentity))).Proposal);
            Assert.Equal(emittedChapter.ProposalId, resolvedChapter.ProposalId);
            Assert.Equal("comic-installment", resolvedChapter.TargetKind);
            Assert.Equal(
                new Dictionary<string, string> { ["mangadexchapter"] = chapterIdentity },
                resolvedChapter.Patch.ExternalIds);
            Assert.Equal("2024-01-02", resolvedChapter.Patch.Dates["published"]);
            Assert.Equal(20, resolvedChapter.Patch.Stats["pageCount"]);
        } finally {
            MangaDexPlugin.Http = previous;
        }
    }

    [Theory]
    [InlineData("book")]
    [InlineData("book-volume")]
    [InlineData("book-chapter")]
    public async Task ProseBookKindsAreNotClaimed(string kind) {
        var result = await MangaDexPlugin.IdentifyAsync(Lookup(kind, "mangadex", MangaId));

        Assert.Null(result.Proposal);
        Assert.Empty(result.Candidates);
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

    private static (HttpStatusCode Status, string Json) ResponseFor(HttpRequestMessage request) {
        var path = request.RequestUri?.AbsolutePath;
        if (path == $"/chapter/{ChapterId}") return (HttpStatusCode.OK, Chapter(includeManga: true));
        if (path == $"/manga/{MangaId}") return (HttpStatusCode.OK, Manga());
        if (path == "/cover") return (HttpStatusCode.OK, """{"data":[],"total":0}""");
        if (path == $"/manga/{MangaId}/feed") return (HttpStatusCode.OK, $"{{\"data\":[{Chapter(includeManga: false)}],\"total\":1}}");
        if (path == $"/manga/{MangaId}/aggregate") return (HttpStatusCode.OK, """{"volumes":{}}""");
        throw new InvalidOperationException($"Unexpected MangaDex request {request.RequestUri}");
    }

    private static string Manga() => $$"""
        {
          "data": {
            "id": "{{MangaId}}",
            "type": "manga",
            "attributes": {
              "title": { "en": "Case Saga" },
              "description": { "en": "A case-sensitive fixture." },
              "year": 2024,
              "contentRating": "safe",
              "originalLanguage": "en",
              "tags": []
            },
            "relationships": []
          }
        }
        """;

    private static string Chapter(bool includeManga) {
        var resource = $$"""
            {
              "id": "{{ChapterId}}",
              "type": "chapter",
              "attributes": {
                "title": "Opening",
                "volume": "Vol:Case",
                "chapter": "1",
                "translatedLanguage": "en",
                "publishAt": "2024-01-02T00:00:00Z",
                "pages": 20
              },
              "relationships": [
                {{(includeManga ? $"{{\"id\":\"{MangaId}\",\"type\":\"manga\"}}" : "")}}
              ]
            }
            """;
        return includeManga ? $"{{\"data\":{resource}}}" : resource;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Json)> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var (status, json) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
