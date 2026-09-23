using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>Exposes one Book work with independently inspected readable and audio files from a tested LazyLibrarian build.</summary>
internal sealed class LazyLibrarianLibrary(LazyLibrarianClient client, ConnectionContext connection) {
    private readonly LazyLibrarianBooks books = new(client);

    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken token) {
        var version = await client.ReadAsync<LazyLibrarianVersionRow>(LazyLibrarianCodes.GetVersion, null, token);
        if (!version.Success || version.CurrentVersion is null
            || !version.CurrentVersion.StartsWith(LazyLibrarianCodes.TestedVersion, StringComparison.Ordinal))
            throw new IntegrationFailure("This adapter is verified only with the tested LazyLibrarian API build.");
        if (connection.ExpectedInstanceId is not null)
            throw new IntegrationFailure("LazyLibrarian does not report a persistent installation ID. Reconnect it with its current identity policy.");
        var ebookRoot = Root(LazyLibrarianCodes.EbookRoot);
        var audiobookRoot = Root(LazyLibrarianCodes.AudiobookRoot);
        if (request.Operation == IntegrationOperations.Probe) {
            await books.ListAsync(token);
            return new ProbeResult(null, "LazyLibrarian", version.CurrentVersion, [
                new(ManagerProtocol.ConnectedLibrary,
                    [ManagerProtocol.SearchLibrary, ManagerProtocol.GetLibraryItem, ManagerProtocol.ListLibraries],
                    [MediaKinds.Book])
            ]);
        }
        if (request.Operation == ManagerProtocol.SearchLibrary)
            return await SearchAsync(Input<ManagedLibraryQuery>(request), token);
        if (request.Operation == ManagerProtocol.GetLibraryItem)
            return await GetAsync(Input<ManagedItemInput>(request), ebookRoot, audiobookRoot, token);
        if (request.Operation == ManagerProtocol.ListLibraries)
            return new ProviderLibraryCatalog([
                new(LazyLibrarianCodes.EbookRendition, "Ebooks", ebookRoot, [MediaKinds.Book], connection.BaseUrl),
                new(LazyLibrarianCodes.AudiobookRendition, "Audiobooks", audiobookRoot, [MediaKinds.Book], connection.BaseUrl)
            ]);
        throw new IntegrationFailure("This LazyLibrarian operation is not supported.");
    }

    private async Task<ManagedLibraryPage> SearchAsync(ManagedLibraryQuery input, CancellationToken token) {
        if (input.EntityKind != MediaKinds.Book || input.Limit is < 1 or > 100
            || input.Query?.Length > 512 || input.Cursor?.Length > 8192) throw Invalid();
        var query = input.Query?.Trim() ?? "";
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { connection.Id, input.EntityKind, Query = query, input.Limit }))));
        var after = "";
        if (!string.IsNullOrWhiteSpace(input.Cursor)) {
            var parts = input.Cursor.Split(':', 2);
            if (parts.Length != 2 || parts[0] != scope || parts[1].Length is < 1 or > 512) throw Invalid();
            after = parts[1];
        }
        var rows = (await books.ListAsync(token)).Where(row => row.BookID is not null
            && string.CompareOrdinal(row.BookID, after) > 0
            && (query.Length == 0 || row.BookName!.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.AuthorName!.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.BookID.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(row => row.BookID, StringComparer.Ordinal).Take(input.Limit + 1).ToArray();
        var page = rows.Take(input.Limit).Select(row => Item(row, null)).ToArray();
        return new(page, rows.Length > input.Limit ? scope + ":" + page[^1].RemoteId : null);
    }

    private async Task<ManagedItemSnapshot> GetAsync(
        ManagedItemInput input, string ebookRoot, string audiobookRoot, CancellationToken token) {
        if (input.EntityKind != MediaKinds.Book || input.BookRendition is not
                (LazyLibrarianCodes.EbookRendition or LazyLibrarianCodes.AudiobookRendition)
            || input.ExpectedExternalIds is not { Count: > 0 and <= 64 }) throw Invalid();
        var row = await books.GetAsync(input.RemoteId, token);
        var rendition = input.BookRendition == LazyLibrarianCodes.EbookRendition
            ? LazyLibrarianCodes.Ebook : LazyLibrarianCodes.Audiobook;
        var root = rendition == LazyLibrarianCodes.Ebook ? ebookRoot : audiobookRoot;
        var path = rendition == LazyLibrarianCodes.Ebook ? row.BookFile : row.AudioFile;
        if (!string.IsNullOrWhiteSpace(path) && !UnderRoot(root, path))
            throw new IntegrationFailure("The reported book file is outside this rendition's configured library root.");
        var item = Item(row, string.IsNullOrWhiteSpace(path) ? 0 : 1, rendition);
        if (input.ExpectedExternalIds.Any(pair => item.ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
            throw new IntegrationFailure("This book's pinned identity changed. Refresh the connected library.");
        var files = new List<ManagedLibraryFile>();
        if (!string.IsNullOrWhiteSpace(path)) {
            var size = await client.FileSizeAsync(row.BookID!, rendition, path, token);
            var target = rendition == LazyLibrarianCodes.Ebook
                ? new ManagedFileTarget(row.BookID!, MediaKinds.Book, row.BookName!)
                : new ManagedFileTarget(row.BookID! + ":audio-1", ManagerProtocol.AudioTrack, row.BookName!);
            files.Add(new(row.BookID! + ":" + input.BookRendition, path, size, null, [target]));
        }
        return new(item, path is null or "" ? root : Path.GetDirectoryName(path)!, files, DateTimeOffset.UtcNow);
    }

    private string Root(string key) {
        var root = connection.Settings.GetValueOrDefault(key)?.TrimEnd('/');
        if (root is null || root.Length < 2 || root.Length > 8192 || !root.StartsWith('/')
            || root.Any(char.IsControl) || root.Split('/').Any(part => part is "." or ".."))
            throw new IntegrationFailure("Configure absolute ebook and audiobook roots from LazyLibrarian's library settings.");
        return root;
    }

    private static bool UnderRoot(string root, string path) => path.StartsWith(root + "/", StringComparison.Ordinal)
        && path.Length <= 8192 && !path.Any(char.IsControl)
        && !path.Split('/').Any(part => part is "." or "..");

    private static ManagedLibraryItem Item(LazyLibrarianBookRow row, int? fileCount, string? rendition = null) {
        if (row.BookID is null || row.BookName is null) throw Invalid();
        var identities = new Dictionary<string, string> {
            [row.BookID.StartsWith("OL", StringComparison.Ordinal) && row.BookID.EndsWith('W')
                && long.TryParse(row.BookID.AsSpan(2, row.BookID.Length - 3), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number) && number > 0
                ? LazyLibrarianCodes.OpenLibraryWork : LazyLibrarianCodes.LazyLibrarianWork] = row.BookID
        };
        var status = rendition == LazyLibrarianCodes.Audiobook ? row.AudioStatus : row.Status;
        var monitored = rendition is null
            ? row.Status == "Wanted" || row.AudioStatus == "Wanted"
            : status == "Wanted";
        return new(row.BookID, MediaKinds.Book, row.BookName, null, identities,
            monitored, null, fileCount);
    }

    private static T Input<T>(IntegrationRequest request) =>
        request.Input.Deserialize<T>(IntegrationProtocol.Json) ?? throw Invalid();
    private static IntegrationFailure Invalid() =>
        new("LazyLibrarian returned invalid or ambiguous book library evidence.");
}
