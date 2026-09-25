using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>
/// Reads LazyLibrarian's book catalog and single books. LazyLibrarian's API has no dispatchable
/// read of one book by BookID, so a book is located once per invocation through the complete
/// <c>getAllBooks</c> catalog and then read, and re-read, through its author's <c>getAuthor</c> rows.
/// </summary>
internal sealed class LazyLibrarianBooks(LazyLibrarianClient client) {
    #region Static Variables
    private const int MaximumCatalogSize = 100000;
    #endregion

    #region Variables
    /// <summary>AuthorID of each book located during this invocation.</summary>
    private readonly Dictionary<string, string> authorsByBook = new(StringComparer.Ordinal);
    #endregion

    #region Actions - Catalog
    /// <summary>Reads and validates the complete catalog.</summary>
    /// <exception cref="IntegrationFailure">The catalog is oversized, malformed, or has duplicate BookIDs.</exception>
    internal async Task<IReadOnlyList<LazyLibrarianBookRow>> ListAsync(CancellationToken token) {
        var rows = await client.ReadAsync<LazyLibrarianBookRow[]>(LazyLibrarianCommand.GetAllBooks, null, token);
        if (rows.Length > MaximumCatalogSize || rows.Any(row => !ValidSummary(row))
            || rows.Select(row => row.BookID).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw Invalid();
        return rows;
    }
    #endregion

    #region Actions - Books
    /// <summary>
    /// Reads one book's complete row, or returns null when the complete, non-empty catalog does not
    /// contain it. A null result is catalog evidence of absence; callers decide whether it means removal.
    /// An empty catalog is inconclusive: one empty reply cannot distinguish a removed book from a
    /// LazyLibrarian that answered without its library, so it fails instead of reporting absence.
    /// </summary>
    /// <param name="bookId">LazyLibrarian BookID.</param>
    /// <param name="token">Invocation deadline.</param>
    /// <exception cref="IntegrationFailure">The catalog was empty, malformed, or ambiguous.</exception>
    internal async Task<LazyLibrarianBookRow?> FindAsync(string bookId, CancellationToken token) {
        if (!Text(bookId, 512)) throw new ManagedMutationRejection("Select a valid LazyLibrarian book identity.");
        if (!authorsByBook.TryGetValue(bookId, out var authorId)) {
            var catalog = await ListAsync(token);
            if (catalog.Count == 0)
                throw new IntegrationFailure("LazyLibrarian returned an empty book catalog, so this book cannot be located. Check LazyLibrarian before treating it as removed.");
            var summary = catalog.SingleOrDefault(row => row.BookID == bookId);
            if (summary is null) return null;
            authorId = summary.AuthorID!;
            authorsByBook[bookId] = authorId;
        }
        var result = await client.ReadAsync<LazyLibrarianAuthorRow>(LazyLibrarianCommand.GetAuthor, authorId, token);
        if (result.Books is not { Count: <= MaximumCatalogSize }
            || result.Books.Count(row => row?.BookID == bookId) != 1) throw Invalid();
        var book = result.Books.Single(row => row?.BookID == bookId);
        if (!ValidIdentity(book) || book.AuthorID != authorId
            || !OptionalPath(book.BookFile) || !OptionalPath(book.AudioFile)
            || book.Status is null || book.AudioStatus is null) throw Invalid();
        return book;
    }
    #endregion

    #region Actions - Evidence
    private static bool ValidSummary(LazyLibrarianBookRow? row) => ValidIdentity(row) && Text(row!.AuthorName, 512);

    private static bool ValidIdentity(LazyLibrarianBookRow? row) => row is not null
        && Text(row.BookID, 512) && Text(row.AuthorID, 512) && Text(row.BookName, 512);

    private static bool OptionalPath(string? path) => path is null or "" || Text(path, 8192);

    private static bool Text(string? text, int limit) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= limit && !text.Any(char.IsControl);

    private static IntegrationFailure Invalid() => new("LazyLibrarian returned incomplete or ambiguous book identity evidence.");
    #endregion
}
