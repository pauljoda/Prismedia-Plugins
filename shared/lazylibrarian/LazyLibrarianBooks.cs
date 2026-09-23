using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>Resolves one book work across LazyLibrarian's summary and author-detail reads.</summary>
internal sealed class LazyLibrarianBooks(LazyLibrarianClient client) {
    internal async Task<IReadOnlyList<LazyLibrarianBookRow>> ListAsync(CancellationToken token) {
        var rows = await client.ReadAsync<LazyLibrarianBookRow[]>(LazyLibrarianCodes.GetAllBooks, null, token);
        if (rows.Length > 100000 || rows.Any(row => !ValidSummary(row))
            || rows.Select(row => row.BookID).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw Invalid();
        return rows;
    }

    internal async Task<LazyLibrarianBookRow> GetAsync(string bookId, CancellationToken token) {
        if (!Text(bookId, 512)) throw Invalid();
        var summaries = await ListAsync(token);
        var summary = summaries.SingleOrDefault(row => row.BookID == bookId)
            ?? throw new IntegrationFailure("This book is no longer in the connected LazyLibrarian library.", IntegrationErrorCodes.ManagedItemNotFound);
        var result = await client.ReadAsync<LazyLibrarianAuthorRow>(LazyLibrarianCodes.GetAuthor,
            new Dictionary<string, string> { [LazyLibrarianCodes.IdParameter] = summary.AuthorID! }, token);
        if (result.Books is not { Count: <= 100000 }
            || result.Books.Count(row => row?.BookID == bookId) != 1) throw Invalid();
        var book = result.Books.Single(row => row?.BookID == bookId);
        if (!ValidIdentity(book) || book.AuthorID != summary.AuthorID
            || !OptionalPath(book.BookFile) || !OptionalPath(book.AudioFile)
            || book.Status is null || book.AudioStatus is null) throw Invalid();
        return book;
    }

    private static bool ValidSummary(LazyLibrarianBookRow? row) => ValidIdentity(row) && Text(row!.AuthorName, 512);
    private static bool ValidIdentity(LazyLibrarianBookRow? row) => row is not null
        && Text(row.BookID, 512) && Text(row.AuthorID, 512) && Text(row.BookName, 512);
    private static bool OptionalPath(string? path) => path is null or "" || Text(path, 8192);
    private static bool Text(string? text, int limit) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= limit && !text.Any(char.IsControl);
    private static IntegrationFailure Invalid() => new("LazyLibrarian returned incomplete or ambiguous book identity evidence.");
}
