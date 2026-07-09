using System.Net;
using System.Text;

namespace Prismedia.Plugin.Youtube.Tests;

public sealed class YoutubeIdentityRoundTripTests {
    private const string ChannelId = "UCabcdefghijk";
    private const string AlbumId = "MPREb_fixture_album";
    private const string VideoId = "abcdefghijk";

    [Fact]
    public async Task ArtistAlbumAndTrackStructuralIdentitiesRoundTripWithoutContext() {
        var previous = YoutubePlugin.Http;
        using var http = new HttpClient(new StubHandler(ResponseFor));
        YoutubePlugin.Http = http;
        try {
            var artist = Assert.IsType<EntityMetadataProposal>((await YoutubePlugin.IdentifyAsync(
                Lookup("music-artist", "youtubechannel", ChannelId, includeChildren: true))).Proposal);
            var emittedAlbum = Assert.Single(artist.Children);
            var albumIdentity = emittedAlbum.Patch.ExternalIds["youtubealbum"];

            var resolvedAlbum = Assert.IsType<EntityMetadataProposal>((await YoutubePlugin.IdentifyAsync(
                Lookup("audio-library", "youtubealbum", albumIdentity))).Proposal);
            Assert.Equal(emittedAlbum.ProposalId, resolvedAlbum.ProposalId);
            Assert.Equal("audio-library", resolvedAlbum.TargetKind);
            Assert.Equal(albumIdentity, resolvedAlbum.Patch.ExternalIds["youtubealbum"]);

            var emittedTrack = Assert.Single(resolvedAlbum.Children);
            var trackIdentity = emittedTrack.Patch.ExternalIds["youtube"];
            var resolvedTrack = Assert.IsType<EntityMetadataProposal>((await YoutubePlugin.IdentifyAsync(
                Lookup("audio-track", "youtube", trackIdentity))).Proposal);
            Assert.Equal(emittedTrack.ProposalId, resolvedTrack.ProposalId);
            Assert.Equal("audio-track", resolvedTrack.TargetKind);
            Assert.Equal(trackIdentity, resolvedTrack.Patch.ExternalIds["youtube"]);
        } finally {
            YoutubePlugin.Http = previous;
        }
    }

    [Fact]
    public async Task VideoBackedSingleAlbumIdentityRoundTripsWithoutBecomingATrack() {
        var previous = YoutubePlugin.Http;
        using var http = new HttpClient(new StubHandler(ResponseFor));
        YoutubePlugin.Http = http;
        try {
            var identity = YoutubePlugin.FormatVideoAlbumIdentity(VideoId);
            var proposal = Assert.IsType<EntityMetadataProposal>((await YoutubePlugin.IdentifyAsync(
                Lookup("audio-library", "youtubealbum", identity))).Proposal);

            Assert.Equal("youtube:music:single:abcdefghijk", proposal.ProposalId);
            Assert.Equal("audio-library", proposal.TargetKind);
            Assert.Equal(identity, proposal.Patch.ExternalIds["youtubealbum"]);
            Assert.Equal(VideoId, proposal.Patch.ExternalIds["youtube"]);
        } finally {
            YoutubePlugin.Http = previous;
        }
    }

    private static IdentifyPluginRequest Lookup(
        string kind,
        string identityNamespace,
        string value,
        bool includeChildren = false) {
        var ids = new Dictionary<string, string> { [identityNamespace] = value };
        return new IdentifyPluginRequest(
            2,
            "lookup-id",
            new Dictionary<string, string>(),
            new IdentifyEntitySnapshot(Guid.NewGuid(), kind, string.Empty, ids),
            new IdentifyQuery(null, null, ids),
            new IdentifyMatchHints(ids, [], null, null),
            IncludeStructuralChildren: includeChildren);
    }

    private static (HttpStatusCode Status, string Json) ResponseFor(HttpRequestMessage request) {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Missing URI");
        if (uri.Host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)) {
            return (HttpStatusCode.OK, AlbumSearchResponse());
        }
        if (uri.Host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.EndsWith("/browse", StringComparison.Ordinal)) {
            return (HttpStatusCode.OK, BrowseResponse());
        }
        if (uri.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.EndsWith("/player", StringComparison.Ordinal)) {
            return (HttpStatusCode.OK, PlayerResponse());
        }
        throw new InvalidOperationException($"Unexpected YouTube request {uri}");
    }

    private static string AlbumSearchResponse() => $$"""
        {
          "contents": [{ "musicResponsiveListItemRenderer": {
            "flexColumns": [
              { "musicResponsiveListItemFlexColumnRenderer": { "text": { "runs": [{ "text": "Fixture Album" }] } } },
              { "musicResponsiveListItemFlexColumnRenderer": { "text": { "runs": [
                { "text": "Album" }, { "text": " • " }, { "text": "Fixture Artist" },
                { "text": " • " }, { "text": "2020" }
              ] } } }
            ],
            "navigationEndpoint": { "browseEndpoint": { "browseId": "{{AlbumId}}" } }
          } }]
        }
        """;

    private static string BrowseResponse() => $$"""
        {
          "header": {
            "musicImmersiveHeaderRenderer": { "title": { "runs": [{ "text": "Fixture Artist" }] } },
            "musicDetailHeaderRenderer": {
              "title": { "runs": [{ "text": "Fixture Album" }] },
              "subtitle": { "runs": [{ "text": "Album • Fixture Artist • 2020" }] }
            }
          },
          "contents": [{ "musicResponsiveListItemRenderer": {
            "flexColumns": [
              { "musicResponsiveListItemFlexColumnRenderer": { "text": { "runs": [{ "text": "Fixture Track" }] } } },
              { "musicResponsiveListItemFlexColumnRenderer": { "text": { "runs": [
                { "text": "Fixture Artist" }, { "text": " • " }, { "text": "Fixture Album" },
                { "text": " • " }, { "text": "3:00" }
              ] } } }
            ],
            "navigationEndpoint": { "watchEndpoint": { "videoId": "{{VideoId}}" } }
          } }]
        }
        """;

    private static string PlayerResponse() => $$"""
        {
          "videoDetails": {
            "videoId": "{{VideoId}}", "title": "Fixture Track", "lengthSeconds": "180",
            "keywords": [], "channelId": null, "shortDescription": "A fixture track.",
            "thumbnail": { "thumbnails": [] }, "author": "Fixture Artist", "viewCount": "10"
          },
          "microformat": { "playerMicroformatRenderer": {
            "publishDate": "2020-01-01", "uploadDate": "2020-01-01", "category": "Music",
            "ownerChannelName": "Fixture Artist"
          } }
        }
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Json)> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var (status, json) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
