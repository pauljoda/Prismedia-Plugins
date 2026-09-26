namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>
/// The identity namespace a LazyLibrarian BookID belongs to. With LazyLibrarian's Open Library source,
/// a BookID is an Open Library work ID; any other BookID stays scoped to LazyLibrarian.
/// </summary>
internal sealed class LazyLibrarianBookIdentity {
    #region Static Variables
    /// <summary>An Open Library work ID such as <c>OL450063W</c>.</summary>
    internal static readonly LazyLibrarianBookIdentity OpenLibraryWork = new("openlibrarywork", IsOpenLibraryWorkId);

    /// <summary>Any other BookID, meaningful only inside this LazyLibrarian installation.</summary>
    internal static readonly LazyLibrarianBookIdentity LazyLibrarianWork = new("lazylibrarianwork", _ => true);

    /// <summary>Every namespace, most specific first.</summary>
    internal static IReadOnlyList<LazyLibrarianBookIdentity> All { get; } = [OpenLibraryWork, LazyLibrarianWork];
    #endregion

    #region Variables
    private readonly Func<string, bool> recognizes;

    /// <summary>Prismedia identity namespace spelling.</summary>
    internal string Namespace { get; }
    #endregion

    #region Constructors
    private LazyLibrarianBookIdentity(string @namespace, Func<string, bool> recognizes) {
        Namespace = @namespace;
        this.recognizes = recognizes;
    }
    #endregion

    #region Actions - Identities
    /// <summary>The most specific namespace a BookID belongs to.</summary>
    internal static LazyLibrarianBookIdentity For(string bookId) => All.First(identity => identity.recognizes(bookId));

    /// <summary>Whether a BookID is spelled as an identity of this namespace.</summary>
    internal bool Recognizes(string? bookId) => bookId is not null && recognizes(bookId);

    /// <summary>The identity map for one BookID in its most specific namespace.</summary>
    internal static IReadOnlyDictionary<string, string> Identities(string bookId) =>
        new Dictionary<string, string> { [For(bookId).Namespace] = bookId };

    private static bool IsOpenLibraryWorkId(string value) {
        if (value.Length is < 4 or > 64 || !value.StartsWith("OL", StringComparison.Ordinal) || !value.EndsWith('W')) return false;
        var digits = value.AsSpan(2, value.Length - 3);
        return digits.IndexOfAnyExceptInRange('0', '9') < 0 && digits.IndexOfAnyExcept('0') >= 0;
    }
    #endregion
}
