using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr.Tests;

public sealed class RemoteLibraryPathTests {
    [Theory]
    [InlineData("/library", "/library/Film (2024)/film.mkv")]
    [InlineData("/library/", "/library/Film")]
    [InlineData("C:\\Library", "c:\\library\\Film")]
    [InlineData("C:/Library", "C:\\LIBRARY\\Film\\film.mkv")]
    [InlineData("\\\\server\\share\\library", "//SERVER/Share/Library/Film")]
    public void WholeSegmentsBeneathTheRootAreContained(string root, string path) =>
        Assert.True(RemoteLibraryPath.Parse(root).IsAncestorOf(path));

    [Theory]
    [InlineData("/library", "/library")]
    [InlineData("/library/", "/library/")]
    [InlineData("C:\\Library", "c:/library")]
    [InlineData("\\\\server\\share", "//server/share")]
    public void TheRootItselfIsNotInsideTheRoot(string root, string path) =>
        Assert.False(RemoteLibraryPath.Parse(root).IsAncestorOf(path));

    [Theory]
    [InlineData("/library", "/library2/Film")]
    [InlineData("/library", "/librarian/Film")]
    [InlineData("/library", "/Library/Film")]
    [InlineData("/library", "/other/library/Film")]
    [InlineData("/library", "C:/library/Film")]
    [InlineData("C:\\Library", "D:\\Library\\Film")]
    [InlineData("C:\\Library", "/Library/Film")]
    [InlineData("\\\\server\\share", "//server/other/Film")]
    [InlineData("\\\\server\\share", "//server/share2/Film")]
    public void PrefixesOtherRootsAndOtherSyntaxesAreOutside(string root, string path) =>
        Assert.False(RemoteLibraryPath.Parse(root).IsAncestorOf(path));

    [Theory]
    [InlineData("/library/../etc/passwd")]
    [InlineData("/library/./Film")]
    [InlineData("C:\\Library\\..\\Windows")]
    [InlineData("library/Film")]
    [InlineData("Film")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/library/back\\slash")]
    [InlineData("//server")]
    [InlineData("/library/\u0007bell")]
    public void TraversingRelativeAndAmbiguousPathsAreRefused(string path) {
        Assert.False(RemoteLibraryPath.TryParse(path, out var parsed));
        Assert.Null(parsed);
        Assert.Throws<ArgumentException>(() => RemoteLibraryPath.Parse(path));
        Assert.False(RemoteLibraryPath.Parse("/library").IsAncestorOf(path));
    }

    [Fact]
    public void OverlongPathsAreRefused() =>
        Assert.False(RemoteLibraryPath.TryParse("/" + new string('a', 8192), out _));

    [Theory]
    [InlineData("/library/", "/library", false)]
    [InlineData("/library//Film/", "/library/Film", false)]
    [InlineData("C:\\", "C:/", true)]
    [InlineData("C:\\Library\\Film\\", "C:/Library/Film", true)]
    [InlineData("\\\\server\\share\\", "//server/share", true)]
    public void ValuesNormalizeSeparatorsAndTrailingSlashes(string path, string expected, bool windows) {
        var parsed = RemoteLibraryPath.Parse(path);
        Assert.Equal(expected, parsed.Value);
        Assert.Equal(windows, parsed.IsWindows);
    }

    [Fact]
    public void RelativeSegmentsAreWholeSegmentsBelowTheRoot() {
        var root = RemoteLibraryPath.Parse("C:\\Library");
        Assert.Equal(["Film (2024)", "film.mkv"], root.RelativeSegments(RemoteLibraryPath.Parse("c:/library/Film (2024)/film.mkv")));
        Assert.Empty(root.RelativeSegments(RemoteLibraryPath.Parse("c:/library"))!);
        Assert.Null(root.RelativeSegments(RemoteLibraryPath.Parse("c:/library2/film.mkv")));
        Assert.Null(RemoteLibraryPath.Parse("/library").RelativeSegments(RemoteLibraryPath.Parse("/Library/film.mkv")));
    }
}
