using System.Text.Json.Serialization;

namespace Prismedia.Plugin.LazyLibrarian;

// prism-vocab: external — LazyLibrarian's selected book fields are decoded only at this boundary.
/// <summary>One book row from <c>getAllBooks</c> (summary columns) or <c>getAuthor</c> (every column).</summary>
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
/// <summary>The <c>getAuthor</c> reply: every book row of one author.</summary>
internal sealed record LazyLibrarianAuthorRow(
    [property: JsonPropertyName("books")] IReadOnlyList<LazyLibrarianBookRow>? Books);

// prism-vocab: external — getVersion reports the running build for display.
/// <summary>The <c>getVersion</c> reply.</summary>
internal sealed record LazyLibrarianVersionRow(
    [property: JsonPropertyName("Success")] bool Success,
    [property: JsonPropertyName("current_version")] string? CurrentVersion);

// prism-vocab: external — LazyLibrarian reports API refusals as HTTP 200 JSON with Success false.
/// <summary>LazyLibrarian's refusal envelope, such as an incorrect key or a command the key may not run.</summary>
internal sealed record LazyLibrarianApiError(
    [property: JsonPropertyName("Success")] bool? Success,
    [property: JsonPropertyName("Error")] LazyLibrarianApiErrorDetail? Error);

/// <summary>The code and message of one LazyLibrarian API refusal.</summary>
internal sealed record LazyLibrarianApiErrorDetail(
    [property: JsonPropertyName("Code")] int? Code,
    [property: JsonPropertyName("Message")] string? Message);
