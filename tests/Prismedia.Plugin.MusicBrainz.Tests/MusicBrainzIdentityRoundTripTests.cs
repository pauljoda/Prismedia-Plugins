using System.Net;
using System.Text;

namespace Prismedia.Plugin.MusicBrainz.Tests;

public sealed class MusicBrainzIdentityRoundTripTests {
    private const string ArtistId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string GroupId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string SingleGroupId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
    private const string LaterSingleGroupId = "ffffffff-ffff-ffff-ffff-ffffffffffff";
    private const string ReleaseId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string RecordingId = "dddddddd-dddd-dddd-dddd-dddddddddddd";

    [Fact]
    public async Task ReleaseGroupAndRecordingChildrenRoundTripWithoutContext() {
        var previousHttp = MusicBrainzPlugin.Http;
        var previousInterval = MusicBrainzPlugin.MinRequestInterval;
        using var http = new HttpClient(new StubHandler(ResponseFor));
        MusicBrainzPlugin.Http = http;
        MusicBrainzPlugin.MinRequestInterval = TimeSpan.Zero;
        try {
            var artist = Assert.IsType<EntityMetadataProposal>((await MusicBrainzPlugin.IdentifyAsync(
                Lookup("music-artist", "musicbrainzartist", ArtistId, includeChildren: true))).Proposal);
            Assert.Collection(
                artist.Children,
                album => Assert.Equal("Fixture Album", album.Patch.Title),
                single => Assert.Equal("Fixture Single", single.Patch.Title),
                laterSingle => Assert.Equal("Later Fixture Single", laterSingle.Patch.Title));
            var emittedAlbum = artist.Children[0];
            var groupIdentity = emittedAlbum.Patch.ExternalIds["musicbrainzreleasegroup"];

            var resolvedAlbum = Assert.IsType<EntityMetadataProposal>((await MusicBrainzPlugin.IdentifyAsync(
                Lookup("audio-library", "musicbrainzreleasegroup", groupIdentity))).Proposal);
            Assert.Equal(emittedAlbum.ProposalId, resolvedAlbum.ProposalId);
            Assert.Equal("audio-library", resolvedAlbum.TargetKind);
            Assert.Equal(groupIdentity, resolvedAlbum.Patch.ExternalIds["musicbrainzreleasegroup"]);
            Assert.Equal(groupIdentity, resolvedAlbum.Patch.ExternalIds["musicbrainz"]);
            Assert.Equal(ReleaseId, resolvedAlbum.Patch.ExternalIds["musicbrainzrelease"]);

            var emittedTrack = Assert.Single(resolvedAlbum.Children);
            var recordingIdentity = emittedTrack.Patch.ExternalIds["musicbrainzrecording"];
            var resolvedTrack = Assert.IsType<EntityMetadataProposal>((await MusicBrainzPlugin.IdentifyAsync(
                Lookup("audio-track", "musicbrainzrecording", recordingIdentity))).Proposal);
            Assert.Equal(emittedTrack.ProposalId, resolvedTrack.ProposalId);
            Assert.Equal("audio-track", resolvedTrack.TargetKind);
            Assert.Equal(recordingIdentity, resolvedTrack.Patch.ExternalIds["musicbrainzrecording"]);
        } finally {
            MusicBrainzPlugin.Http = previousHttp;
            MusicBrainzPlugin.MinRequestInterval = previousInterval;
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
        var path = uri.AbsolutePath;
        if (path == $"/ws/2/artist/{ArtistId}") return (HttpStatusCode.OK, $$"""
            { "id": "{{ArtistId}}", "name": "Fixture Artist", "relations": [], "tags": [], "genres": [] }
            """);
        if (path == "/ws/2/release-group" && uri.Query.Contains($"artist={ArtistId}", StringComparison.Ordinal)) {
            if (uri.Query.Contains("offset=100", StringComparison.Ordinal)) return (HttpStatusCode.OK, $$"""
                {
                  "release-group-count": 101,
                  "release-group-offset": 100,
                  "release-groups": [{
                    "id": "{{LaterSingleGroupId}}", "title": "Later Fixture Single", "first-release-date": "2022-01-01",
                    "primary-type": "Single", "secondary-types": []
                  }]
                }
                """);
            return (HttpStatusCode.OK, $$"""
                {
                  "release-group-count": 101,
                  "release-group-offset": 0,
                  "release-groups": [
                  {
                    "id": "{{GroupId}}", "title": "Fixture Album", "first-release-date": "2020-01-01",
                    "primary-type": "Album", "secondary-types": []
                  },
                  {
                    "id": "{{SingleGroupId}}", "title": "Fixture Single", "first-release-date": "2021-01-01",
                    "primary-type": "Single", "secondary-types": []
                  }
                ] }
                """);
        }
        if (path == $"/ws/2/release/{GroupId}") return (HttpStatusCode.NotFound, "{}");
        if (path == $"/ws/2/release-group/{GroupId}") return (HttpStatusCode.OK, $$"""
            { "id": "{{GroupId}}", "title": "Fixture Album", "releases": [
              { "id": "{{ReleaseId}}", "title": "Fixture Album", "date": "2020-01-01" }
            ] }
            """);
        if (path == $"/ws/2/release/{ReleaseId}") return (HttpStatusCode.OK, $$"""
            {
              "id": "{{ReleaseId}}", "title": "Fixture Album", "date": "2020-01-01",
              "release-group": { "id": "{{GroupId}}", "primary-type": "Album" },
              "media": [{ "position": 1, "tracks": [{
                "position": 1, "title": "Fixture Track", "length": 180000,
                "recording": { "id": "{{RecordingId}}", "title": "Fixture Track" }
              }] }]
            }
            """);
        if (path == $"/ws/2/recording/{RecordingId}") return (HttpStatusCode.OK, $$"""
            {
              "id": "{{RecordingId}}", "title": "Fixture Track", "first-release-date": "2020-01-01",
              "length": 180000, "releases": [{
                "id": "{{ReleaseId}}", "title": "Fixture Album", "date": "2020-01-01",
                "release-group": { "id": "{{GroupId}}", "primary-type": "Album" }
              }]
            }
            """);
        if (uri.Host.Equals("coverartarchive.org", StringComparison.OrdinalIgnoreCase)) {
            return (HttpStatusCode.OK, """{"images":[]}""");
        }
        throw new InvalidOperationException($"Unexpected MusicBrainz request {uri}");
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
