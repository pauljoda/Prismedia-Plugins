using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>
/// One book format LazyLibrarian manages independently: its Prismedia rendition code, LazyLibrarian's
/// <c>type</c> spelling, the connection setting that holds its library root, the book row columns that
/// hold its status and file, and how its file evidence is read.
/// </summary>
internal sealed class LazyLibrarianRendition {
    #region Static Variables
    /// <summary>Readable ebook files, confirmed through LazyLibrarian's direct file endpoint.</summary>
    internal static readonly LazyLibrarianRendition Ebook = new(
        code: "ebook", wireType: "eBook", rootSetting: "ebookRoot", libraryLabel: "Ebooks", noun: "ebook",
        targetKind: MediaKinds.Book, anchorTargetSuffix: "", extensions: [".epub", ".pdf"],
        readsMappedFolder: false, status: row => row.Status, file: row => row.BookFile);

    /// <summary>
    /// Audiobooks, inventoried from the mapped local folder. LazyLibrarian's direct file endpoint
    /// bundles a multi-file audiobook folder into a ZIP, even for a HEAD request, so it is never used.
    /// </summary>
    internal static readonly LazyLibrarianRendition Audiobook = new(
        code: "audiobook", wireType: "AudioBook", rootSetting: "audiobookRoot", libraryLabel: "Audiobooks", noun: "audiobook",
        targetKind: ManagerProtocol.AudioTrack, anchorTargetSuffix: ":audio-1", extensions: [".m4b", ".m4a", ".mp3"],
        readsMappedFolder: true, status: row => row.AudioStatus, file: row => row.AudioFile);

    /// <summary>Every rendition, in library-listing order.</summary>
    internal static IReadOnlyList<LazyLibrarianRendition> All { get; } = [Ebook, Audiobook];
    #endregion

    #region Variables
    private readonly IReadOnlySet<string> extensions;
    private readonly string anchorTargetSuffix;
    private readonly Func<LazyLibrarianBookRow, string?> status;
    private readonly Func<LazyLibrarianBookRow, string?> file;

    /// <summary>Prismedia's rendition code, also used as the provider library ID.</summary>
    internal string Code { get; }

    /// <summary>LazyLibrarian's <c>type</c> parameter spelling for this format.</summary>
    internal string WireType { get; }

    /// <summary>Connection setting that holds this format's absolute LazyLibrarian library root.</summary>
    internal string RootSetting { get; }

    /// <summary>Provider library label shown when mapping this format's root.</summary>
    internal string LibraryLabel { get; }

    /// <summary>Lowercase noun used in user-facing messages.</summary>
    internal string Noun { get; }

    /// <summary>Entity kind of each file target in this format.</summary>
    internal string TargetKind { get; }

    /// <summary>
    /// Whether file evidence comes from the mapped local folder instead of LazyLibrarian's direct file
    /// endpoint, which cannot describe the separate tracks of a multi-file audiobook.
    /// </summary>
    internal bool ReadsMappedFolder { get; }
    #endregion

    #region Constructors
    private LazyLibrarianRendition(string code, string wireType, string rootSetting, string libraryLabel, string noun,
        string targetKind, string anchorTargetSuffix, IReadOnlyList<string> extensions, bool readsMappedFolder,
        Func<LazyLibrarianBookRow, string?> status, Func<LazyLibrarianBookRow, string?> file) {
        Code = code;
        WireType = wireType;
        RootSetting = rootSetting;
        LibraryLabel = libraryLabel;
        Noun = noun;
        TargetKind = targetKind;
        this.anchorTargetSuffix = anchorTargetSuffix;
        this.extensions = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ReadsMappedFolder = readsMappedFolder;
        this.status = status;
        this.file = file;
    }
    #endregion

    #region Actions - Lookup
    /// <summary>Finds the rendition for a host rendition code.</summary>
    internal static LazyLibrarianRendition? Named(string? code) => All.FirstOrDefault(rendition => rendition.Code == code);

    /// <summary>Requires a Book request that names one supported rendition.</summary>
    /// <exception cref="ManagedMutationRejection">The host asked for another kind or no exact rendition.</exception>
    internal static LazyLibrarianRendition Require(string? entityKind, string? code) =>
        entityKind == MediaKinds.Book && Named(code) is { } rendition
            ? rendition
            : throw new ManagedMutationRejection("Choose the ebook or audiobook rendition of a Book.");
    #endregion

    #region Actions - Book Rows
    /// <summary>This format's status in one book row.</summary>
    internal LazyLibrarianBookStatus StatusOf(LazyLibrarianBookRow row) => LazyLibrarianBookStatus.Named(status(row));

    /// <summary>This format's reported final file in one book row, or null when none is reported.</summary>
    internal string? FileOf(LazyLibrarianBookRow row) => string.IsNullOrWhiteSpace(file(row)) ? null : file(row);

    /// <summary>Whether a reported file has a type this format imports.</summary>
    internal bool Accepts(string path) => extensions.Contains(Path.GetExtension(path));

    /// <summary>Stable remote ID of the file LazyLibrarian reports for this format.</summary>
    internal string AnchorFileId(string bookId) => bookId + ":" + Code;

    /// <summary>Stable target ID of the file LazyLibrarian reports for this format.</summary>
    internal string AnchorTargetId(string bookId) => bookId + anchorTargetSuffix;
    #endregion
}
