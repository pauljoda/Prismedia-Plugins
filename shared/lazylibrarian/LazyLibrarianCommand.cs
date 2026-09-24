namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>How the adapter reads one command's reply.</summary>
internal enum LazyLibrarianReply {
    /// <summary>A JSON document.</summary>
    Json,

    /// <summary>LazyLibrarian's HTML command list.</summary>
    Help,

    /// <summary>A plain <c>OK</c> acknowledgement, or refusal text.</summary>
    Acknowledgement,

    /// <summary>A file download, read only through HEAD.</summary>
    File
}

/// <summary>
/// One LazyLibrarian API command this adapter sends, with how its reply is read and whether it must
/// accept the book format's <c>type</c> parameter.
/// </summary>
internal sealed class LazyLibrarianCommand {
    #region Static Variables
    /// <summary>Lists the commands, and their parameters, that the configured API key may run.</summary>
    internal static readonly LazyLibrarianCommand Help = new("help", LazyLibrarianReply.Help, takesRendition: false);

    /// <summary>Reports the installed LazyLibrarian version.</summary>
    internal static readonly LazyLibrarianCommand GetVersion = new("getVersion", LazyLibrarianReply.Json, takesRendition: false);

    /// <summary>Lists every book with its author, the only complete catalog read.</summary>
    internal static readonly LazyLibrarianCommand GetAllBooks = new("getAllBooks", LazyLibrarianReply.Json, takesRendition: false);

    /// <summary>Reads one author and every column of each of their books.</summary>
    internal static readonly LazyLibrarianCommand GetAuthor = new("getAuthor", LazyLibrarianReply.Json, takesRendition: false);

    /// <summary>Serves one book file directly.</summary>
    internal static readonly LazyLibrarianCommand GetFileDirect = new("getFileDirect", LazyLibrarianReply.File, takesRendition: true);

    /// <summary>Marks one book format Wanted.</summary>
    internal static readonly LazyLibrarianCommand QueueBook = new("queueBook", LazyLibrarianReply.Acknowledgement, takesRendition: true);

    /// <summary>Marks one book format Skipped.</summary>
    internal static readonly LazyLibrarianCommand UnqueueBook = new("unqueueBook", LazyLibrarianReply.Acknowledgement, takesRendition: true);

    /// <summary>Starts a background search for one Wanted book format.</summary>
    internal static readonly LazyLibrarianCommand SearchBook = new("searchBook", LazyLibrarianReply.Acknowledgement, takesRendition: true);

    /// <summary>Commands every operation needs to read the catalog and one book.</summary>
    internal static IReadOnlyList<LazyLibrarianCommand> CoreReads { get; } = [GetAllBooks, GetAuthor];
    #endregion

    #region Variables
    /// <summary>LazyLibrarian's command spelling.</summary>
    internal string Name { get; }

    /// <summary>How the reply is read.</summary>
    internal LazyLibrarianReply Reply { get; }

    /// <summary>Whether the command must accept the book format through its <c>type</c> parameter.</summary>
    internal bool TakesRendition { get; }
    #endregion

    #region Constructors
    private LazyLibrarianCommand(string name, LazyLibrarianReply reply, bool takesRendition) {
        Name = name;
        Reply = reply;
        TakesRendition = takesRendition;
    }
    #endregion

    #region Actions - Monitoring
    /// <summary>The command that turns a format's monitoring on (queue) or off (unqueue).</summary>
    internal static LazyLibrarianCommand Monitoring(bool monitored) => monitored ? QueueBook : UnqueueBook;
    #endregion
}
