using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Kapowarr;

/// <summary>
/// One validated Kapowarr run: its identity, complete issue list, and exact issue-to-file associations.
/// An issue matched to more than one file cannot be associated exactly, so its files, and every issue
/// sharing one of those files, are withheld from the snapshot. Only operations that target those
/// issues fail; the rest of the run stays usable.
/// </summary>
internal sealed class KapowarrRun {
    #region Variables
    private readonly KapowarrVolume volume;
    private readonly IReadOnlyList<KapowarrIssue> issues;
    private readonly IReadOnlyDictionary<int, string> withheldProblems;

    /// <summary>Connected-library evidence for the run, excluding withheld file associations.</summary>
    internal ManagedItemSnapshot Snapshot { get; }

    /// <summary>Kapowarr's run ID.</summary>
    internal int Id => volume.Id;

    /// <summary>Whether Kapowarr's automatic and RSS searches consider this run at all.</summary>
    internal bool Monitored => volume.Monitored;

    /// <summary>Kapowarr's new-issue monitoring flag, or null when it was not reported.</summary>
    internal bool? MonitorsNewIssues => volume.MonitorNewIssues;
    #endregion

    #region Constructors
    private KapowarrRun(KapowarrVolume volume, IReadOnlyList<KapowarrIssue> issues,
        IReadOnlyDictionary<int, string> withheldProblems, ManagedItemSnapshot snapshot) {
        this.volume = volume;
        this.issues = issues;
        this.withheldProblems = withheldProblems;
        Snapshot = snapshot;
    }
    #endregion

    #region Actions - Decoding
    /// <summary>Validates one run read and builds its connected-library snapshot.</summary>
    /// <param name="volume">Kapowarr's complete run read, including issues and their files.</param>
    /// <exception cref="IntegrationFailure">Kapowarr returned incomplete, conflicting, or oversized evidence.</exception>
    internal static KapowarrRun Decode(KapowarrVolume volume) {
        var item = KapowarrLibrary.Item(volume);
        if (volume.Issues is null || volume.Issues.Length > 10000
            || volume.Issues.Select(issue => issue.Id).Distinct().Count() != volume.Issues.Length)
            throw KapowarrLibrary.Invalid();
        var issues = volume.Issues;
        foreach (var issue in issues) {
            if (issue.VolumeId != volume.Id || issue.ComicVineId <= 0 || issue.Files is null) throw KapowarrLibrary.Invalid();
            _ = KapowarrLibrary.Id(issue.Id);
            _ = Label(issue);
            _ = Title(issue);
        }
        var withheld = Withheld(issues);
        var files = Files(issues, withheld.Files);
        var comicIssues = issues.Select(issue => new ManagedComicIssue(KapowarrLibrary.Id(issue.Id), Label(issue), Title(issue),
            issue.Monitored, ComicVineIdentity.Issue.Identities(issue.ComicVineId))).ToArray();
        var snapshot = new ManagedItemSnapshot(item with { RemoteFileCount = files.Count },
            KapowarrLibrary.Required(volume.Folder, 8192), files, DateTimeOffset.UtcNow, comicIssues);
        return new(volume, issues, withheld.Problems, snapshot);
    }

    private static (IReadOnlySet<int> Files, IReadOnlyDictionary<int, string> Problems) Withheld(IReadOnlyList<KapowarrIssue> issues) {
        var ambiguous = issues.Where(issue => issue.Files!.Length > 1).ToArray();
        var files = ambiguous.SelectMany(issue => issue.Files!).Select(file => file.Id).ToHashSet();
        var problems = new Dictionary<int, string>();
        foreach (var issue in ambiguous)
            problems[issue.Id] = $"Kapowarr matched {issue.Files!.Length} files to issue {Label(issue)}. "
                + "Keep one file for that issue in Kapowarr before using it in Prismedia.";
        foreach (var issue in issues.Where(issue => !problems.ContainsKey(issue.Id)
            && issue.Files!.Any(file => files.Contains(file.Id))))
            problems[issue.Id] = $"Issue {Label(issue)} shares a file with an issue that has several files in Kapowarr. "
                + "Keep one file per issue in Kapowarr before using it in Prismedia.";
        return (files, problems);
    }

    private static IReadOnlyList<ManagedLibraryFile> Files(IReadOnlyList<KapowarrIssue> issues, IReadOnlySet<int> withheldFiles) {
        var evidence = new Dictionary<int, (KapowarrFile File, List<ManagedFileTarget> Targets)>();
        foreach (var issue in issues) {
            foreach (var file in issue.Files!) {
                if (file.Size <= 0 || file.Id <= 0) throw KapowarrLibrary.Invalid();
                _ = KapowarrLibrary.Required(file.FilePath, 8192);
                if (!evidence.TryGetValue(file.Id, out var entry)) entry = (file, []);
                if (entry.File != file || entry.Targets.Count >= 1000) throw KapowarrLibrary.Invalid();
                entry.Targets.Add(new(KapowarrLibrary.Id(issue.Id), MediaKinds.Comic, Title(issue), IssueLabel: Label(issue)));
                evidence[file.Id] = entry;
            }
        }
        if (evidence.Count > 10000
            || evidence.Values.Select(value => value.File.FilePath).Distinct(StringComparer.Ordinal).Count() != evidence.Count)
            throw KapowarrLibrary.Invalid();
        return evidence.Where(pair => !withheldFiles.Contains(pair.Key)).OrderBy(pair => pair.Key)
            .Select(pair => new ManagedLibraryFile(KapowarrLibrary.Id(pair.Key), pair.Value.File.FilePath!,
                pair.Value.File.Size, null, pair.Value.Targets)).ToArray();
    }

    private static string Label(KapowarrIssue issue) => KapowarrLibrary.Required(issue.IssueNumber, 128);

    private static string Title(KapowarrIssue issue) => string.IsNullOrWhiteSpace(issue.Title)
        ? "Issue " + Label(issue)
        : KapowarrLibrary.Required(issue.Title, 512);
    #endregion

    #region Actions - Issues
    /// <summary>Resolves the exact issue a manager control targets.</summary>
    /// <exception cref="ManagedMutationRejection">The issue is gone, its label changed, or its file evidence is withheld.</exception>
    internal KapowarrIssue RequireIssue(ManagedControlTarget target) {
        var issue = issues.SingleOrDefault(issue => KapowarrLibrary.Id(issue.Id) == target.RemoteId)
            ?? throw new ManagedMutationRejection("The selected comic issue is no longer in this Kapowarr run. Refresh the connected library.");
        if (issue.IssueNumber != target.IssueLabel)
            throw new ManagedMutationRejection($"The selected issue's label changed in Kapowarr from {target.IssueLabel} to {issue.IssueNumber}. Refresh the connected library.");
        return Resolved(issue);
    }

    /// <summary>Resolves the exact issue a lookup names by Comic Vine issue identity and label.</summary>
    /// <param name="comicVineId">Numeric Comic Vine issue ID.</param>
    /// <param name="label">Exact issue label, including fractional and special labels.</param>
    /// <exception cref="ManagedMutationRejection">The issue is missing, relabelled, or its file evidence is withheld.</exception>
    internal KapowarrIssue RequireIssue(int comicVineId, string label) {
        var matches = issues.Where(issue => issue.ComicVineId == comicVineId && issue.IssueNumber == label).ToArray();
        if (matches.Length != 1)
            throw new ManagedMutationRejection("The exact Comic Vine issue is missing or its label changed in Kapowarr.");
        return Resolved(matches[0]);
    }

    /// <summary>The host's stable identity for one issue of this run.</summary>
    internal static ManagedResolvedTarget Target(KapowarrIssue issue) =>
        new(KapowarrLibrary.Id(issue.Id), MediaKinds.Comic, ComicVineIdentity.Issue.Identities(issue.ComicVineId), IssueLabel: Label(issue));

    private KapowarrIssue Resolved(KapowarrIssue issue) =>
        withheldProblems.TryGetValue(issue.Id, out var problem) ? throw new ManagedMutationRejection(problem) : issue;
    #endregion

    #region Actions - Monitoring
    /// <summary>
    /// Whether Kapowarr will actually search for this issue. Kapowarr ignores issue monitoring in an
    /// unmonitored run, so only a monitored issue in a monitored run counts as monitored.
    /// </summary>
    internal bool Monitors(KapowarrIssue issue) => volume.Monitored && issue.Monitored;

    /// <summary>
    /// Explains why monitoring this run for one issue would widen Kapowarr's search beyond that issue,
    /// or returns null when the run is already monitored or monitoring it is exact.
    /// </summary>
    internal string? RunMonitoringBlocker(KapowarrIssue issue) {
        if (volume.Monitored) return null;
        if (volume.MonitorNewIssues != false)
            return "This Kapowarr run monitors new issues. Monitoring the run would also fetch future issues, "
                + "so turn off its new-issue monitoring in Kapowarr first.";
        var others = issues.Count(other => other.Id != issue.Id && other.Monitored && other.Files!.Length == 0);
        return others == 0 ? null
            : $"Monitoring this Kapowarr run would also search {others} other monitored issue(s) without files. "
                + "Review the run's issue monitoring in Kapowarr first.";
    }

    /// <summary>
    /// Explains why Kapowarr's automatic search for this issue would do nothing, or returns null when it
    /// would run. Kapowarr searches only an issue with no file in a monitored run whose own flag is on.
    /// </summary>
    internal string? SearchBlocker(KapowarrIssue issue) {
        if (!volume.Monitored)
            return $"Kapowarr searches only monitored runs. Turn on monitoring for issue {Label(issue)} first; that also monitors its run.";
        if (!issue.Monitored)
            return $"Kapowarr searches only monitored issues. Turn on monitoring for issue {Label(issue)} first.";
        return issue.Files!.Length > 0
            ? $"Issue {Label(issue)} already has a file in Kapowarr, so its search would not run."
            : null;
    }
    #endregion
}
