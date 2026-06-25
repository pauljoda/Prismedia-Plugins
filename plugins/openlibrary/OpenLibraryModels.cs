using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prismedia.Plugin.OpenLibrary;

internal sealed record OpenLibrarySearchResponse(
    [property: JsonPropertyName("numFound")] int NumFound,
    [property: JsonPropertyName("docs")] OpenLibrarySearchDoc[]? Docs);

internal sealed record OpenLibrarySearchDoc(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("author_name")] string[]? AuthorName,
    [property: JsonPropertyName("author_key")] string[]? AuthorKey,
    [property: JsonPropertyName("first_publish_year")] int? FirstPublishYear,
    [property: JsonPropertyName("cover_i")] int? CoverId,
    [property: JsonPropertyName("number_of_pages_median")] int? NumberOfPagesMedian,
    [property: JsonPropertyName("ratings_average")] decimal? RatingsAverage,
    [property: JsonPropertyName("ratings_count")] int? RatingsCount,
    [property: JsonPropertyName("subject")] string[]? Subjects,
    [property: JsonPropertyName("first_sentence")] string[]? FirstSentence,
    [property: JsonPropertyName("isbn")] string[]? Isbns,
    [property: JsonPropertyName("publisher")] string[]? Publishers,
    [property: JsonPropertyName("publish_date")] string[]? PublishDates,
    [property: JsonPropertyName("edition_key")] string[]? EditionKeys);

internal sealed record OpenLibraryWork(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("description")] JsonElement? Description,
    [property: JsonPropertyName("first_publish_date")] string? FirstPublishDate,
    [property: JsonPropertyName("authors")] OpenLibraryWorkAuthor[]? Authors,
    [property: JsonPropertyName("covers")] int[]? Covers,
    [property: JsonPropertyName("subjects")] string[]? Subjects,
    [property: JsonPropertyName("subject_places")] string[]? SubjectPlaces,
    [property: JsonPropertyName("subject_people")] string[]? SubjectPeople,
    [property: JsonPropertyName("subject_times")] string[]? SubjectTimes,
    [property: JsonPropertyName("links")] OpenLibraryLink[]? Links);

internal sealed record OpenLibraryWorkAuthor(
    [property: JsonPropertyName("author")] OpenLibraryKeyRef? Author);

internal sealed record OpenLibraryEditionResponse(
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("entries")] OpenLibraryEdition[]? Entries);

internal sealed record OpenLibraryEdition(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("description")] JsonElement? Description,
    [property: JsonPropertyName("publish_date")] string? PublishDate,
    [property: JsonPropertyName("publishers")] string[]? Publishers,
    [property: JsonPropertyName("number_of_pages")] int? NumberOfPages,
    [property: JsonPropertyName("covers")] int[]? Covers,
    [property: JsonPropertyName("isbn_10")] string[]? Isbn10,
    [property: JsonPropertyName("isbn_13")] string[]? Isbn13,
    [property: JsonPropertyName("physical_format")] string? PhysicalFormat,
    [property: JsonPropertyName("series")] string[]? Series,
    [property: JsonPropertyName("languages")] OpenLibraryKeyRef[]? Languages,
    [property: JsonPropertyName("contributors")] OpenLibraryContributor[]? Contributors,
    [property: JsonPropertyName("works")] OpenLibraryKeyRef[]? Works);

internal sealed record OpenLibraryContributor(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record OpenLibraryAuthorSearchResponse(
    [property: JsonPropertyName("numFound")] int NumFound,
    [property: JsonPropertyName("docs")] OpenLibraryAuthorSearchDoc[]? Docs);

internal sealed record OpenLibraryAuthorSearchDoc(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("birth_date")] string? BirthDate,
    [property: JsonPropertyName("top_work")] string? TopWork,
    [property: JsonPropertyName("top_subjects")] string[]? TopSubjects,
    [property: JsonPropertyName("work_count")] int? WorkCount,
    [property: JsonPropertyName("ratings_average")] decimal? RatingsAverage,
    [property: JsonPropertyName("ratings_count")] int? RatingsCount);

internal sealed record OpenLibraryAuthor(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("personal_name")] string? PersonalName,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("bio")] JsonElement? Bio,
    [property: JsonPropertyName("birth_date")] string? BirthDate,
    [property: JsonPropertyName("death_date")] string? DeathDate,
    [property: JsonPropertyName("photos")] int[]? Photos,
    [property: JsonPropertyName("links")] OpenLibraryLink[]? Links,
    [property: JsonPropertyName("remote_ids")] Dictionary<string, string>? RemoteIds);

internal sealed record OpenLibraryLink(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? Url);

internal sealed record OpenLibraryKeyRef(
    [property: JsonPropertyName("key")] string? Key);
