using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.Opds;

namespace Prismedia.Plugin.Opds.Tests;

public sealed class OpdsParserTests {
    [Fact]
    public void AtomPreservesDirectoryLinksAndDistinguishesFullDownloadsFromLendingAndSamples() {
        var result = OpdsParser.Parse("""
          <feed xmlns="http://www.w3.org/2005/Atom" xmlns:dc="http://purl.org/dc/terms/" xml:base="publications/">
            <title>Books</title><id>urn:catalog:books</id>
            <link rel="next" href="page2.xml" />
            <entry><id>urn:isbn:9781234567890</id><title>A Book</title><author><name>A Writer</name></author>
              <dc:language>en</dc:language><dc:publisher>Publisher</dc:publisher>
              <link rel="http://opds-spec.org/acquisition/open-access" type="application/epub+zip" href="book.epub" />
              <link rel="http://opds-spec.org/acquisition/borrow" type="text/html" href="borrow" />
              <link rel="http://opds-spec.org/acquisition/sample" type="application/pdf" href="sample.pdf" />
            </entry>
          </feed>
          """, new Uri("https://catalog.test/opds/"), MediaKinds.Book);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("Books", result.Title);
        Assert.Equal("https://catalog.test/opds/publications/page2.xml", result.Next!.AbsoluteUri);
        Assert.Equal([AcquisitionAccess.Download, AcquisitionAccess.Borrow, AcquisitionAccess.Sample], entry.Item.Offers.Select(offer => offer.Access));
        var download = entry.Item.Offers[0];
        Assert.Equal("https://catalog.test/opds/publications/book.epub", entry.Acquisitions[download.Id].Url.AbsoluteUri);
        Assert.DoesNotContain("https://", download.Id);
        Assert.Equal("en", entry.Item.Publication.Language);
        Assert.Equal("Publisher", entry.Item.Publication.Publisher);
    }

    [Fact]
    public void JsonCatalogParsesPublicationsNavigationAndComicsWithoutTreatingPurchasesAsDownloads() {
        var result = OpdsParser.Parse("""
          {"metadata":{"title":"Comics"},"navigation":[{"title":"Recent","href":"recent","type":"application/opds+json"}],
           "publications":[{"metadata":{"title":"Issue 1½","identifier":"urn:comic:1.5","author":[{"name":"Writer"}],"language":"en"},
             "links":[{"rel":"http://opds-spec.org/acquisition/buy","href":"checkout","type":"text/html"},
                      {"rel":["http://opds-spec.org/acquisition/open-access"],"href":"issue.cbz","type":"application/vnd.comicbook+zip"}]}]}
          """, new Uri("https://catalog.test/opds/"), MediaKinds.Comic);
        Assert.Equal(2, result.Entries.Count);
        Assert.True(result.Entries[0].Item.IsContainer);
        Assert.Equal("https://catalog.test/opds/recent", result.Entries[0].Item.Selection.Locator);
        var comic = result.Entries[1].Item;
        Assert.Equal(MediaKinds.Comic, comic.Selection.EntityKind);
        Assert.Equal("Issue 1½", comic.Publication.Title);
        Assert.Equal([AcquisitionAccess.Purchase, AcquisitionAccess.Download], comic.Offers.Select(offer => offer.Access));
    }

    [Fact]
    public void CrossOriginAndCredentialBearingLinksCannotBecomeExecutableDownloads() {
        var result = OpdsParser.Parse("""
          <feed xmlns="http://www.w3.org/2005/Atom"><title>Books</title><id>urn:catalog</id>
          <entry><id>urn:book:1</id><title>Book</title>
            <link rel="http://opds-spec.org/acquisition/open-access" type="application/epub+zip" href="https://other.test/book.epub" />
            <link rel="http://opds-spec.org/acquisition/open-access" type="application/epub+zip" href="https://user:password@catalog.test/book.epub" />
          </entry></feed>
          """, new Uri("https://catalog.test/opds/"), MediaKinds.Book);
        var entry = Assert.Single(result.Entries);
        Assert.All(entry.Item.Offers, offer => Assert.NotEqual(AcquisitionAccess.Download, offer.Access));
    }

    [Theory]
    [InlineData("<!DOCTYPE feed [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><feed xmlns='http://www.w3.org/2005/Atom'><title>&secret;</title></feed>")]
    [InlineData("<html><title>A login form</title></html>")]
    [InlineData("{\"metadata\":{\"title\":\"Not a catalog\"}}")]
    public void InvalidOrUnsafeDocumentsAreRejected(string document) =>
        Assert.Throws<IntegrationFailure>(() => OpdsParser.Parse(document, new Uri("https://catalog.test/"), MediaKinds.Book));
}
