using System.Security.Cryptography;
using System.Text;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

internal sealed partial class LazyLibrarianLibrary {
    #region Static Variables
    private const int MaximumAudioParts = 999;
    #endregion

    #region Actions - Files
    /// <summary>
    /// Reports the final files of one rendition. An ebook is confirmed through LazyLibrarian's direct
    /// file HEAD. An audiobook is inventoried from its mapped local folder, because LazyLibrarian
    /// answers a direct audiobook request, including HEAD, by building a ZIP of the whole folder.
    /// Prismedia re-checks every mapped path and size before import. A reported file of a type this
    /// adapter does not import, such as a .mobi or .azw3 ebook, is omitted rather than failing the
    /// whole read, so the book's monitoring and holding stay observable.
    /// </summary>
    private async Task<IReadOnlyList<ManagedLibraryFile>> FilesAsync(LazyLibrarianBookRow row, LazyLibrarianRendition rendition,
        string reportedPath, CancellationToken token) {
        if (!rendition.Accepts(reportedPath)) return [];
        var anchorTarget = new ManagedFileTarget(rendition.AnchorTargetId(row.BookID!), rendition.TargetKind, row.BookName!);
        if (rendition.ReadsMappedFolder) return InventoryMappedFolder(row, rendition, reportedPath, anchorTarget, token);
        if (!Help.Supports(LazyLibrarianCommand.GetFileDirect, rendition))
            throw new IntegrationFailure($"LazyLibrarian's API help does not list getFileDirect with type={rendition.WireType}, so the {rendition.Noun} file cannot be confirmed.");
        var size = await client.FileSizeAsync(row.BookID!, rendition, reportedPath, token);
        return [new(rendition.AnchorFileId(row.BookID!), reportedPath, size, null, [anchorTarget])];
    }

    /// <summary>
    /// Reads the reported file and, for a file inside its own book folder, every sibling audio part
    /// from the reviewed local mapping. A file directly in the root is reported alone, because the
    /// root's other files belong to other works.
    /// </summary>
    private IReadOnlyList<ManagedLibraryFile> InventoryMappedFolder(LazyLibrarianBookRow row, LazyLibrarianRendition rendition,
        string reportedPath, ManagedFileTarget anchorTarget, CancellationToken token) {
        var remoteRoot = Root(rendition);
        var mounts = connection.LibraryMounts?.Where(mount =>
            mount.RemoteRootId == rendition.Code && mount.RemotePath == remoteRoot).ToArray() ?? [];
        if (mounts.Length == 0)
            throw new IntegrationFailure($"Map LazyLibrarian's {rendition.Noun} root ({remoteRoot}) to a Prismedia library folder "
                + $"so its {rendition.Noun} files can be inspected. LazyLibrarian serves a multi-file {rendition.Noun} only as a ZIP.");
        if (mounts.Length != 1) throw Invalid();
        var relative = reportedPath[(remoteRoot.Length + 1)..].Split('/');
        if (relative.Any(part => part.Length == 0 || part is "." or "..")) throw Invalid();
        var localRoot = Path.GetFullPath(mounts[0].LocalPath);
        if (!Path.IsPathFullyQualified(localRoot) || !Directory.Exists(localRoot))
            throw new IntegrationFailure($"The mapped {rendition.Noun} folder is not accessible. Review the library mapping.");
        try {
            var folder = localRoot;
            foreach (var part in relative[..^1]) {
                folder = Path.Combine(folder, part);
                if (!Directory.Exists(folder)) throw Missing(rendition, reportedPath);
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    throw new IntegrationFailure($"The mapped {rendition.Noun} folder contains a link. Review this library boundary.");
            }
            var anchorLocal = Path.Combine(folder, relative[^1]);
            if (!File.Exists(anchorLocal)) throw Missing(rendition, reportedPath);
            if ((File.GetAttributes(anchorLocal) & FileAttributes.ReparsePoint) != 0)
                throw new IntegrationFailure($"The mapped {rendition.Noun} file is a link. Review this library boundary.");
            var anchor = new ManagedLibraryFile(rendition.AnchorFileId(row.BookID!), reportedPath,
                Size(anchorLocal, rendition), null, [anchorTarget]);
            if (relative.Length < 2) return [anchor];
            var parts = new List<ManagedLibraryFile>();
            foreach (var candidate in Directory.EnumerateFiles(folder)) {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(candidate);
                if (!rendition.Accepts(candidate) || name == relative[^1]) continue;
                if (parts.Count >= MaximumAudioParts)
                    throw new IntegrationFailure($"The mapped {rendition.Noun} folder has too many parts to review as one work.");
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    throw new IntegrationFailure($"The mapped {rendition.Noun} folder contains a linked part. Review this library boundary.");
                var remotePath = remoteRoot + "/" + string.Join('/', relative[..^1]) + "/" + name;
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remotePath)))[..24];
                var target = new ManagedFileTarget(row.BookID! + ":audio-" + digest, rendition.TargetKind, Path.GetFileNameWithoutExtension(name));
                parts.Add(new(row.BookID! + ":audio-file-" + digest, remotePath, Size(candidate, rendition), null, [target]));
            }
            return [anchor, .. parts.OrderBy(file => file.Path, StringComparer.Ordinal)];
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            throw new IntegrationFailure($"The mapped {rendition.Noun} folder could not be inventoried. Review its access.");
        }
    }

    private static long Size(string localPath, LazyLibrarianRendition rendition) {
        var length = new FileInfo(localPath).Length;
        return length > 0
            ? length
            : throw new IntegrationFailure($"The mapped {rendition.Noun} file {Path.GetFileName(localPath)} is empty.");
    }

    private static IntegrationFailure Missing(LazyLibrarianRendition rendition, string reportedPath) =>
        new($"The {rendition.Noun} file LazyLibrarian reports ({reportedPath}) is not in the mapped folder. Review the library mapping.");
    #endregion
}
