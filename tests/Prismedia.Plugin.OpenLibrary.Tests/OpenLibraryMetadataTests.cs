namespace Prismedia.Plugin.OpenLibrary.Tests;

public sealed class OpenLibraryMetadataTests {
    [Fact]
    public void IdParsingAcceptsKeysUrlsAndBareIds() {
        Assert.Equal("OL257943W", OpenLibraryMetadata.WorkIdFromKey("/works/OL257943W"));
        Assert.Equal("OL807276M", OpenLibraryMetadata.EditionIdFromKey("OL807276M"));
        Assert.Equal("OL234664A", OpenLibraryMetadata.AuthorIdFromUrl("https://openlibrary.org/authors/OL234664A/George_R._R._Martin"));
        Assert.Equal("9780553573404", OpenLibraryMetadata.IsbnFromUrl("https://openlibrary.org/isbn/9780553573404"));
    }

    [Fact]
    public void TagsPreserveSeriesAndRemoveCatalogNoise() {
        var tags = OpenLibraryMetadata.Tags(
            ["series:A Song of Ice and Fire", "Fantasy", "nyt:mass-market-paperback=2011-04-10", "Accessible book"],
            ["Westeros"],
            ["Eddard Stark"],
            [],
            "A Song of Ice and Fire",
            "Paperback");

        Assert.Contains("series: A Song of Ice and Fire", tags);
        Assert.Contains("Fantasy", tags);
        Assert.Contains("place: Westeros", tags);
        Assert.Contains("character: Eddard Stark", tags);
        Assert.DoesNotContain(tags, tag => tag.StartsWith("nyt:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Accessible book", tags);
    }

    [Fact]
    public void CleanTextRemovesCommonMarkdownWithoutDroppingContent() {
        var text = OpenLibraryMetadata.CleanText("### Plot\n\n***A Game of Thrones*** is followed by [A Clash of Kings](https://openlibrary.org/works/OL257939W).");

        Assert.Equal("Plot\n\nA Game of Thrones is followed by A Clash of Kings.", text);
    }
}
