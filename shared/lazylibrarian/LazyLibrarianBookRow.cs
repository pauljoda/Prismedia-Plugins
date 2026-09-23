using System.Text.Json.Serialization;

namespace Prismedia.Plugin.LazyLibrarian;

// prism-vocab: external — LazyLibrarian's selected book fields are decoded only at this boundary.
internal sealed record LazyLibrarianBookRow(
    string? BookID,
    string? AuthorID,
    string? AuthorName,
    string? BookName,
    string? Status,
    string? AudioStatus,
    string? BookFile,
    string? AudioFile,
    string? BookDate,
    string? BookImg);

// prism-vocab: external — getAuthor returns the complete rows under books.
internal sealed record LazyLibrarianAuthorRow(
    [property: JsonPropertyName("books")] IReadOnlyList<LazyLibrarianBookRow>? Books);

// prism-vocab: external — getVersion is checked before declaring connected-library support.
internal sealed record LazyLibrarianVersionRow(
    [property: JsonPropertyName("Success")] bool Success,
    [property: JsonPropertyName("current_version")] string? CurrentVersion);
