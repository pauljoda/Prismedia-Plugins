using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>
/// Exposes one Book work with independently inspected ebook and audiobook files. Supported commands
/// are probed from LazyLibrarian's API help on every invocation instead of pinning one build: reads
/// need the core catalog commands, and each write needs its command declared for the book format.
/// </summary>
internal sealed partial class LazyLibrarianLibrary(LazyLibrarianClient client, ConnectionContext connection) {
    #region Variables
    private readonly LazyLibrarianBooks books = new(client);
    private LazyLibrarianApiHelp? help;

    /// <summary>The commands probed for this invocation.</summary>
    private LazyLibrarianApiHelp Help => help
        ?? throw new InvalidOperationException("LazyLibrarian's API help is read at the start of every dispatch.");
    #endregion

    #region Actions - Dispatch
    /// <summary>Probes LazyLibrarian's commands, then runs one integration operation.</summary>
    /// <param name="request">The correlated host request.</param>
    /// <param name="token">Invocation deadline.</param>
    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken token) {
        help = await client.ReadHelpAsync(token);
        Help.RequireReads();
        if (connection.ExpectedInstanceId is not null)
            throw new IntegrationFailure("LazyLibrarian does not report a persistent installation ID. Reconnect it with its current identity policy.");
        foreach (var rendition in LazyLibrarianRendition.All) _ = Root(rendition);
        if (request.Operation == IntegrationOperations.Probe) return await ProbeAsync(token);
        if (request.Operation == ManagerProtocol.SearchLibrary)
            return await SearchAsync(Input<ManagedLibraryQuery>(request), token);
        if (request.Operation == ManagerProtocol.GetLibraryItem)
            return await GetLibraryItemAsync(Input<ManagedItemInput>(request), token);
        if (request.Operation == ManagerProtocol.ListLibraries)
            return new ProviderLibraryCatalog(LazyLibrarianRendition.All.Select(rendition =>
                new ProviderLibraryDescriptor(rendition.Code, rendition.LibraryLabel, Root(rendition), [MediaKinds.Book], connection.BaseUrl)).ToArray());
        if (request.Operation == ManagerProtocol.Options)
            return Options(Input<ManagerOptionsInput>(request));
        if (request.Operation == ManagerCreation.Lookup)
            return await LookupAsync(Input<ManagedLookupInput>(request), token);
        if (request.Operation == ManagerControls.Reconcile)
            return await ReconcileAsync(Input<ReconcileManagedInput>(request), token);
        if (request.Operation == ManagerControls.Configure)
            return await ConfigureAsync(Input<ConfigureManagedInput>(request), token);
        if (request.Operation == ManagerControls.Request)
            return await RequestAsync(Input<RequestManagedInput>(request), token);
        throw new IntegrationFailure("This LazyLibrarian operation is not supported.");
    }

    /// <summary>Declares only the controls this installation's API help lists for both book formats.</summary>
    private async Task<ProbeResult> ProbeAsync(CancellationToken token) {
        await books.ListAsync(token);
        var version = Help.Supports(LazyLibrarianCommand.GetVersion)
            ? await client.ReadAsync<LazyLibrarianVersionRow>(LazyLibrarianCommand.GetVersion, null, token)
            : null;
        bool SupportsEveryFormat(params LazyLibrarianCommand[] commands) => commands.All(command =>
            LazyLibrarianRendition.All.All(rendition => Help.Supports(command, rendition)));
        List<string> managerOperations = [ManagerProtocol.Options, ManagerCreation.Lookup, ManagerControls.Reconcile];
        if (SupportsEveryFormat(LazyLibrarianCommand.QueueBook, LazyLibrarianCommand.UnqueueBook))
            managerOperations.Add(ManagerControls.Configure);
        if (SupportsEveryFormat(LazyLibrarianCommand.SearchBook)) managerOperations.Add(ManagerControls.Request);
        return new ProbeResult(null, "LazyLibrarian", version is { Success: true } ? version.CurrentVersion : null, [
            new(ManagerProtocol.ConnectedLibrary,
                [ManagerProtocol.SearchLibrary, ManagerProtocol.GetLibraryItem, ManagerProtocol.ListLibraries], [MediaKinds.Book]),
            new(ManagerProtocol.ExternalManager, managerOperations, [MediaKinds.Book])
        ]);
    }
    #endregion

    #region Actions - Library
    private async Task<ManagedLibraryPage> SearchAsync(ManagedLibraryQuery input, CancellationToken token) {
        if (input.EntityKind != MediaKinds.Book || input.Limit is < 1 or > 100
            || input.Query?.Length > 512 || input.Cursor?.Length > 8192)
            throw new IntegrationFailure("Search Books with a page size from 1 to 100 and a query of up to 512 characters.");
        var query = input.Query?.Trim() ?? "";
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { connection.Id, input.EntityKind, Query = query, input.Limit }))));
        var after = "";
        if (!string.IsNullOrWhiteSpace(input.Cursor)) {
            var parts = input.Cursor.Split(':', 2);
            if (parts.Length != 2 || parts[0] != scope || parts[1].Length is < 1 or > 512)
                throw new IntegrationFailure("This library page belongs to another search. Start the search again.");
            after = parts[1];
        }
        var rows = (await books.ListAsync(token)).Where(row => string.CompareOrdinal(row.BookID, after) > 0
            && (query.Length == 0 || row.BookName!.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.AuthorName!.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.BookID!.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(row => row.BookID, StringComparer.Ordinal).Take(input.Limit + 1).ToArray();
        var page = rows.Take(input.Limit).Select(row => Item(row, null)).ToArray();
        return new(page, rows.Length > input.Limit ? scope + ":" + page[^1].RemoteId : null);
    }

    /// <summary>
    /// Reads one rendition with its file evidence. Only this read may report confirmed removal, and
    /// only when the complete, validated catalog omits the book.
    /// </summary>
    private async Task<ManagedItemSnapshot> GetLibraryItemAsync(ManagedItemInput input, CancellationToken token) {
        var rendition = LazyLibrarianRendition.Require(input.EntityKind, input.BookRendition);
        var row = await books.FindAsync(input.RemoteId, token)
            ?? throw new IntegrationFailure("This book was removed from LazyLibrarian's catalog.", IntegrationErrorCodes.ManagedItemNotFound);
        return await SnapshotAsync(row, rendition, input.ExpectedExternalIds, token);
    }

    private async Task<ManagedItemSnapshot> SnapshotAsync(LazyLibrarianBookRow row, LazyLibrarianRendition rendition,
        IReadOnlyDictionary<string, string>? expectedIds, CancellationToken token) {
        var (item, path) = Holding(row, rendition, expectedIds);
        var files = rendition.FileOf(row) is { } reported ? await FilesAsync(row, rendition, reported, token) : [];
        return new(item with { RemoteFileCount = files.Count }, path, files, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The rendition's item and stable holding path, without file evidence. LazyLibrarian has no
    /// work-level folder identity when no file exists, so the path is a logical holding inside the
    /// mapped root that stays the same across file arrival.
    /// </summary>
    private (ManagedLibraryItem Item, string Path) Holding(LazyLibrarianBookRow row, LazyLibrarianRendition rendition,
        IReadOnlyDictionary<string, string>? expectedIds) {
        if (expectedIds is not { Count: > 0 and <= 64 })
            throw new ManagedMutationRejection("Select a book with its known identities.");
        var root = Root(rendition);
        if (rendition.FileOf(row) is { } reported && !RemoteLibraryPath.Parse(root).IsAncestorOf(reported))
            throw new IntegrationFailure($"The reported {rendition.Noun} file is outside this rendition's configured library root.");
        var item = Item(row, rendition);
        if (expectedIds.Any(pair => item.ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
            throw new ManagedMutationRejection("This book's pinned identity changed. Refresh the connected library.");
        var pathPart = Uri.EscapeDataString(row.BookID!);
        if (pathPart is "." or "..") pathPart = "work-" + pathPart;
        return (item, root + "/" + pathPart);
    }

    /// <summary>The configured root of one rendition: an absolute, traversal-free remote path with no trailing separator.</summary>
    private string Root(LazyLibrarianRendition rendition) {
        var root = connection.Settings.GetValueOrDefault(rendition.RootSetting)?.TrimEnd('/');
        if (root is null || root.Length < 2 || !root.StartsWith('/') || !RemoteLibraryPath.TryParse(root, out _))
            throw new IntegrationFailure("Configure absolute ebook and audiobook roots from LazyLibrarian's library settings.");
        return root;
    }

    /// <summary>
    /// The host item for one book. With a rendition, monitoring is that format's; without one, a book
    /// counts as monitored when either format is.
    /// </summary>
    private static ManagedLibraryItem Item(LazyLibrarianBookRow row, LazyLibrarianRendition? rendition) {
        var monitored = rendition is null
            ? LazyLibrarianRendition.All.Any(format => format.StatusOf(row).IsMonitored)
            : rendition.StatusOf(row).IsMonitored;
        return new(row.BookID!, MediaKinds.Book, row.BookName!, null, LazyLibrarianBookIdentity.Identities(row.BookID!),
            monitored, null, null);
    }

    private static T Input<T>(IntegrationRequest request) =>
        request.Input.Deserialize<T>(IntegrationProtocol.Json) ?? throw new IntegrationFailure("The operation input is missing.");

    private static IntegrationFailure Invalid() =>
        new("LazyLibrarian returned invalid or ambiguous book library evidence.");
    #endregion
}
