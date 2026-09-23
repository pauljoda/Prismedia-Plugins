using System.Text.Json.Serialization;
namespace Prismedia.Plugin.Kapowarr;

// prism-vocab: external — the single typed decode boundary for Kapowarr 1.3 API fields.
internal sealed record KapowarrAbout(string Version);
internal sealed record KapowarrRoot(int Id, string? Folder);
internal sealed record KapowarrVolume(int Id, [property: JsonPropertyName("comicvine_id")] int ComicVineId,
    string? Title, int? Year, [property: JsonRequired] bool Monitored, string? Folder, KapowarrIssue[]? Issues);
internal sealed record KapowarrIssue(int Id, [property: JsonPropertyName("volume_id")] int VolumeId,
    [property: JsonPropertyName("comicvine_id")] int ComicVineId,
    [property: JsonPropertyName("issue_number")] string? IssueNumber, string? Title, [property: JsonRequired] bool Monitored, KapowarrFile[]? Files);
internal sealed record KapowarrFile(int Id, [property: JsonPropertyName("filepath")] string? FilePath, long Size);
