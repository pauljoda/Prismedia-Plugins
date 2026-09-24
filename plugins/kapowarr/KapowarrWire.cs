using System.Text.Json.Serialization;

namespace Prismedia.Plugin.Kapowarr;

// prism-vocab: external — the single typed decode and encode boundary for Kapowarr 1.3 API fields.

/// <summary>
/// Kapowarr's response envelope. Both fields must be present; a non-null error is never an empty
/// success, and a null result is valid only for acknowledgement-only writes.
/// </summary>
internal sealed record KapowarrEnvelope<TResult>(
    [property: JsonRequired] string? Error,
    [property: JsonRequired] TResult? Result);

/// <summary>Kapowarr's reported release, such as <c>V1.3.2</c>.</summary>
internal sealed record KapowarrAbout(string Version);

/// <summary>One configured Kapowarr root folder.</summary>
internal sealed record KapowarrRoot(int Id, string? Folder);

/// <summary>One Comic Vine catalog result; <see cref="AlreadyAdded"/> is the existing run ID when added.</summary>
internal sealed record KapowarrSearchVolume([property: JsonPropertyName("comicvine_id")] int ComicVineId,
    string? Title, int? Year, [property: JsonPropertyName("already_added")] int? AlreadyAdded);

/// <summary>The run Kapowarr created for an add request.</summary>
internal sealed record KapowarrVolumeReceipt(int Id);

/// <summary>An add-run request body.</summary>
internal sealed record KapowarrAddVolume(
    [property: JsonPropertyName("comicvine_id")] int ComicVineId,
    [property: JsonPropertyName("root_folder_id")] int RootFolderId,
    bool Monitor,
    [property: JsonPropertyName("monitoring_scheme")] string MonitoringScheme,
    [property: JsonPropertyName("monitor_new_issues")] bool MonitorNewIssues,
    [property: JsonPropertyName("auto_search")] bool AutoSearch) {
    #region Actions - Requests
    /// <summary>Adds one run with run monitoring, every issue's monitoring, new-issue monitoring, and search off.</summary>
    /// <param name="comicVineId">Numeric Comic Vine volume ID of the reviewed run.</param>
    /// <param name="rootFolderId">Kapowarr root folder that receives the run.</param>
    internal static KapowarrAddVolume Unmonitored(int comicVineId, int rootFolderId) =>
        new(comicVineId, rootFolderId, false, "none", false, false);
    #endregion
}

/// <summary>
/// One run with its complete issue list. <see cref="MonitorNewIssues"/> is null when Kapowarr did not
/// report it, which never counts as off.
/// </summary>
internal sealed record KapowarrVolume(int Id, [property: JsonPropertyName("comicvine_id")] int ComicVineId,
    string? Title, int? Year, [property: JsonRequired] bool Monitored, string? Folder, KapowarrIssue[]? Issues,
    [property: JsonPropertyName("monitor_new_issues")] bool? MonitorNewIssues = null);

/// <summary>One issue of a run and every file Kapowarr matched to it.</summary>
internal sealed record KapowarrIssue(int Id, [property: JsonPropertyName("volume_id")] int VolumeId,
    [property: JsonPropertyName("comicvine_id")] int ComicVineId,
    [property: JsonPropertyName("issue_number")] string? IssueNumber, string? Title, [property: JsonRequired] bool Monitored, KapowarrFile[]? Files);

/// <summary>One final file in Kapowarr's path namespace.</summary>
internal sealed record KapowarrFile(int Id, [property: JsonPropertyName("filepath")] string? FilePath, long Size);

/// <summary>An issue edit; Kapowarr changes only the monitoring flag through this route.</summary>
internal sealed record KapowarrIssueEdit(bool Monitored);

/// <summary>
/// A run edit that sets only the run's own monitoring flag. It carries no monitoring scheme, so
/// Kapowarr leaves every issue's monitoring and the run's new-issue monitoring unchanged.
/// </summary>
internal sealed record KapowarrVolumeEdit(bool Monitored);

/// <summary>A task request body for Kapowarr's task queue.</summary>
internal sealed record KapowarrTaskCommand(string Cmd,
    [property: JsonPropertyName("volume_id")] int VolumeId,
    [property: JsonPropertyName("issue_id")] int IssueId) {
    #region Actions - Requests
    /// <summary>Queues Kapowarr's automatic search for one issue of one run.</summary>
    /// <param name="volumeId">Kapowarr run ID.</param>
    /// <param name="issueId">Kapowarr issue ID within that run.</param>
    internal static KapowarrTaskCommand SearchIssue(int volumeId, int issueId) =>
        new("auto_search_issue", volumeId, issueId);
    #endregion
}

/// <summary>The queued task Kapowarr acknowledged.</summary>
internal sealed record KapowarrTaskReceipt(int Id);
