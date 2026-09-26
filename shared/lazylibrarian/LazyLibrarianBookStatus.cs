namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>
/// LazyLibrarian's status of one book format (<c>Status</c> for ebooks, <c>AudioStatus</c> for
/// audiobooks). LazyLibrarian searches only a Wanted format, and <c>queueBook</c> marks any format
/// Wanted, including one it already has. An unrecognized future spelling is kept, never treated as
/// monitored, and blocks changes until it is reviewed.
/// </summary>
internal sealed class LazyLibrarianBookStatus {
    #region Static Variables
    /// <summary>Queued for LazyLibrarian's searches.</summary>
    internal static readonly LazyLibrarianBookStatus Wanted = new("Wanted", monitored: true, owned: false, inProgress: false);

    /// <summary>A release was grabbed and is downloading; LazyLibrarian does not search it again.</summary>
    internal static readonly LazyLibrarianBookStatus Snatched = new("Snatched", monitored: true, owned: false, inProgress: true);

    /// <summary>Marked as owned by the user.</summary>
    internal static readonly LazyLibrarianBookStatus Have = new("Have", monitored: false, owned: true, inProgress: false);

    /// <summary>A final file is in LazyLibrarian's library.</summary>
    internal static readonly LazyLibrarianBookStatus Open = new("Open", monitored: false, owned: true, inProgress: false);

    /// <summary>Not wanted.</summary>
    internal static readonly LazyLibrarianBookStatus Skipped = new("Skipped", monitored: false, owned: false, inProgress: false);

    /// <summary>Excluded by the user or by LazyLibrarian's filters.</summary>
    internal static readonly LazyLibrarianBookStatus Ignored = new("Ignored", monitored: false, owned: false, inProgress: false);

    /// <summary>Every status this adapter recognizes.</summary>
    internal static IReadOnlyList<LazyLibrarianBookStatus> All { get; } = [Wanted, Snatched, Have, Open, Skipped, Ignored];
    #endregion

    #region Variables
    /// <summary>LazyLibrarian's stored spelling.</summary>
    internal string Spelling { get; }

    /// <summary>Whether LazyLibrarian is trying to obtain this format.</summary>
    internal bool IsMonitored { get; }

    /// <summary>Whether LazyLibrarian already has this format, so queueing it would request another copy.</summary>
    internal bool IsOwned { get; }

    /// <summary>Whether a grabbed release is still being downloaded.</summary>
    internal bool IsInProgress { get; }

    /// <summary>Whether the spelling is one this adapter understands.</summary>
    internal bool IsRecognized { get; }

    /// <summary>Whether LazyLibrarian's <c>searchBook</c> would search this format: Wanted and not already snatched.</summary>
    internal bool IsSearchable => IsMonitored && !IsInProgress;
    #endregion

    #region Constructors
    private LazyLibrarianBookStatus(string spelling, bool monitored, bool owned, bool inProgress, bool recognized = true) {
        Spelling = spelling;
        IsMonitored = monitored;
        IsOwned = owned;
        IsInProgress = inProgress;
        IsRecognized = recognized;
    }
    #endregion

    #region Actions - Decoding
    /// <summary>Decodes a stored spelling, keeping an unrecognized one as an unmonitored, unchangeable status.</summary>
    internal static LazyLibrarianBookStatus Named(string? spelling) =>
        All.FirstOrDefault(status => status.Spelling == spelling)
            ?? new(spelling ?? "", monitored: false, owned: false, inProgress: false, recognized: false);
    #endregion

    #region Actions - Controls
    /// <summary>
    /// Explains why monitoring cannot be turned on from this status, or returns null when queueing
    /// the format would only mark a missing format Wanted.
    /// </summary>
    internal string? QueueBlocker(LazyLibrarianRendition rendition) {
        if (!IsRecognized)
            return $"LazyLibrarian reported an unrecognized {rendition.Noun} status \"{Spelling}\". Review it in LazyLibrarian first.";
        return IsOwned
            ? $"LazyLibrarian already has this {rendition.Noun} ({Spelling}). Turning monitoring on would queue another copy."
            : null;
    }

    /// <summary>Explains why LazyLibrarian would not search this format, or returns null when it would.</summary>
    internal string? SearchBlocker(LazyLibrarianRendition rendition) {
        if (IsSearchable) return null;
        if (IsInProgress) return $"LazyLibrarian already snatched this {rendition.Noun} and is downloading it.";
        return IsOwned
            ? $"LazyLibrarian already has this {rendition.Noun} ({Spelling}), so it would not search for it."
            : $"LazyLibrarian searches only Wanted books, and this {rendition.Noun} is {(IsRecognized ? Spelling : "in an unrecognized status")}. Turn its monitoring on first.";
    }
    #endregion
}
