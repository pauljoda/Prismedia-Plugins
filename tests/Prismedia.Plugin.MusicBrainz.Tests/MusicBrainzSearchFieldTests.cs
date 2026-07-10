namespace Prismedia.Plugin.MusicBrainz.Tests;

public sealed class MusicBrainzSearchFieldTests {
    [Fact]
    public void AlbumSearchUsesFieldOnlyTitleArtistAndYear() {
        var request = Request(
            "audio-library",
            new Dictionary<string, string> {
                ["title"] = "Blue",
                ["artist"] = "Joni Mitchell",
                ["year"] = "1971"
            });

        var query = MusicBrainzPlugin.BuildReleaseSearch(request, "Blue");

        Assert.Equal("release:\"Blue\" AND artist:\"Joni Mitchell\" AND date:\"1971\"", query);
    }

    [Fact]
    public void TrackSearchUsesAlbumContext() {
        var request = Request(
            "audio-track",
            new Dictionary<string, string> {
                ["artist"] = "Massive Attack",
                ["album"] = "Mezzanine"
            });

        var query = MusicBrainzPlugin.BuildRecordingSearch(request, "Teardrop");

        Assert.Equal("recording:\"Teardrop\" AND artist:\"Massive Attack\" AND release:\"Mezzanine\"", query);
    }

    private static IdentifyPluginRequest Request(string kind, IReadOnlyDictionary<string, string> fields) =>
        new(
            2,
            "search",
            new Dictionary<string, string>(),
            new IdentifyEntitySnapshot(Guid.NewGuid(), kind, string.Empty),
            new IdentifyQuery(null, null, null, Fields: fields),
            new IdentifyMatchHints(new Dictionary<string, string>(), [], null, null));
}
