namespace Prismedia.Plugin.MusicBrainz.Tests;

public sealed class MusicBrainzImageTests {
    [Fact]
    public void DirectImageUrlsConvertsCommonsFilePagesToDownloadRedirects() {
        var urls = MusicBrainzPlugin.DirectImageUrls([
            new MusicBrainzPlugin.Relation(
                "image",
                null,
                null,
                null,
                new MusicBrainzPlugin.RelationUrl("https://commons.wikimedia.org/wiki/File:Nirvana_around_1992.jpg"))
        ]).ToArray();

        var url = Assert.Single(urls);
        Assert.Equal("https://commons.wikimedia.org/wiki/Special:Redirect/file/Nirvana_around_1992.jpg", url);
    }

    [Fact]
    public void DirectImageUrlsKeepsDownloadableImagesAndSkipsHtmlPages() {
        var urls = MusicBrainzPlugin.DirectImageUrls([
            new MusicBrainzPlugin.Relation("image", null, null, null, new MusicBrainzPlugin.RelationUrl("https://example.test/artist.webp")),
            new MusicBrainzPlugin.Relation("image", null, null, null, new MusicBrainzPlugin.RelationUrl("https://example.test/artist")),
            new MusicBrainzPlugin.Relation("official homepage", null, null, null, new MusicBrainzPlugin.RelationUrl("https://example.test/not-artwork.jpg")),
            new MusicBrainzPlugin.Relation("image", null, null, null, new MusicBrainzPlugin.RelationUrl("https://en.wikipedia.org/wiki/File:Artist.jpg"))
        ]).ToArray();

        var url = Assert.Single(urls);
        Assert.Equal("https://example.test/artist.webp", url);
    }
}
