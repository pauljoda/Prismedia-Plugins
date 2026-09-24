using System.Text.Json.Serialization;
namespace Prismedia.Plugin.Kapowarr;

// prism-vocab: external — the single typed decode boundary for Kapowarr 1.3 API fields.
internal sealed record KapowarrAbout(string Version);
internal sealed record KapowarrRoot(int Id, string? Folder);
internal sealed record KapowarrSearchVolume([property: JsonPropertyName("comicvine_id")] int ComicVineId,
    string? Title, int? Year, [property: JsonPropertyName("already_added")] int? AlreadyAdded);
internal sealed record KapowarrVolumeReceipt(int Id);
internal sealed record KapowarrAddVolume(
    [property: JsonPropertyName("comicvine_id")] int ComicVineId,
    [property: JsonPropertyName("root_folder_id")] int RootFolderId,
    bool Monitor,
    [property: JsonPropertyName("monitoring_scheme")] string MonitoringScheme,
    [property: JsonPropertyName("monitor_new_issues")] bool MonitorNewIssues,
    [property: JsonPropertyName("auto_search")] bool AutoSearch);
internal sealed record KapowarrVolume(int Id, [property: JsonPropertyName("comicvine_id")] int ComicVineId,
    string? Title, int? Year, [property: JsonRequired] bool Monitored, string? Folder, KapowarrIssue[]? Issues);
internal sealed record KapowarrIssue(int Id, [property: JsonPropertyName("volume_id")] int VolumeId,
    [property: JsonPropertyName("comicvine_id")] int ComicVineId,
    [property: JsonPropertyName("issue_number")] string? IssueNumber, string? Title, [property: JsonRequired] bool Monitored, KapowarrFile[]? Files);
internal sealed record KapowarrFile(int Id, [property: JsonPropertyName("filepath")] string? FilePath, long Size);
internal sealed record KapowarrTaskReceipt(int Id);
