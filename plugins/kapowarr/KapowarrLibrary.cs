using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Integrations;
namespace Prismedia.Plugin.Kapowarr;

/// <summary>Reads existing comic runs and exact issue-to-file associations without changing remote state.</summary>
internal sealed class KapowarrLibrary(KapowarrClient client) {
    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken token) {
        var about = await client.GetAsync<KapowarrAbout>("system/about", token);
        var versionText = about.Version?.StartsWith('V') == true ? about.Version[1..] : about.Version;
        if (!Version.TryParse(versionText, out var version) || version.Major != 1 || version.Minor != 3)
            throw new IntegrationFailure("This adapter requires Kapowarr 1.3.x.");
        if (request.Connection.ExpectedInstanceId is not null)
            throw new IntegrationFailure("Kapowarr does not report a persistent installation ID. Reconnect it with its current identity policy.");
        if (request.Operation == IntegrationOperations.Probe) return new ProbeResult(null, "Kapowarr", about.Version, [
            new(ManagerProtocol.ConnectedLibrary, [ManagerProtocol.SearchLibrary, ManagerProtocol.GetLibraryItem, ManagerProtocol.ListLibraries], [KapowarrCodes.ComicSeries]),
            new(ManagerProtocol.ExternalManager, [ManagerProtocol.Options], [KapowarrCodes.ComicSeries])
        ]);
        if (request.Operation == ManagerProtocol.SearchLibrary) return await SearchAsync(request, token);
        if (request.Operation == ManagerProtocol.GetLibraryItem) return await GetAsync(Input<ManagedItemInput>(request), token);
        if (request.Operation == ManagerProtocol.ListLibraries) {
            var roots = await client.GetAsync<KapowarrRoot[]>("rootfolder", token);
            if (roots.Length > 1000 || roots.Select(root => root.Id).Distinct().Count() != roots.Length) throw Invalid();
            return new ProviderLibraryCatalog(roots.Select(root => {
                var path = Required(root.Folder, 8192);
                return new ProviderLibraryDescriptor(Id(root.Id), LibraryLabel(path), path, [KapowarrCodes.ComicSeries], request.Connection.BaseUrl);
            }).ToArray());
        }
        if (request.Operation == ManagerProtocol.Options) {
            RequireKind(Input<ManagerOptionsInput>(request).EntityKind);
            var roots = await client.GetAsync<KapowarrRoot[]>("rootfolder", token);
            if (roots.Length > 1000 || roots.Select(root => root.Id).Distinct().Count() != roots.Length) throw Invalid();
            return new ManagerOptions([], roots.Select(root => new ManagerRootChoice(Id(root.Id), Required(root.Folder, 8192), null)).ToArray());
        }
        throw new IntegrationFailure("This Kapowarr connection supports existing library reads only.");
    }

    private async Task<ManagedLibraryPage> SearchAsync(IntegrationRequest request, CancellationToken token) {
        var input = Input<ManagedLibraryQuery>(request);
        RequireKind(input.EntityKind);
        if (input.Limit is < 1 or > 100 || input.Query?.Length > 512 || input.Cursor?.Length > 8192) throw Invalid();
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request.Connection.Id, input.EntityKind, input.Query, input.Limit }))));
        var after = 0;
        if (!string.IsNullOrEmpty(input.Cursor)) {
            var parts = input.Cursor.Split(':');
            if (parts.Length != 2 || parts[0] != scope || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 1)
                throw new IntegrationFailure("This library page belongs to another search. Start the search again.");
        }
        var volumes = await client.GetAsync<KapowarrVolume[]>("volumes", token);
        if (volumes.Length > 100000 || volumes.Select(volume => volume.Id).Distinct().Count() != volumes.Length) throw Invalid();
        var items = volumes.Select(volume => Item(volume, null)).Where(item => ParseId(item.RemoteId) > after
            && (string.IsNullOrWhiteSpace(input.Query) || item.Title.Contains(input.Query.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.ExternalIds.Values.Contains(input.Query.Trim(), StringComparer.OrdinalIgnoreCase)))
            .OrderBy(item => ParseId(item.RemoteId)).Take(input.Limit + 1).ToArray();
        var page = items.Take(input.Limit).ToArray();
        return new(page, items.Length > input.Limit ? scope + ":" + page[^1].RemoteId : null);
    }

    private async Task<ManagedItemSnapshot> GetAsync(ManagedItemInput input, CancellationToken token) {
        RequireKind(input.EntityKind);
        if (input.ExpectedExternalIds is not { Count: > 0 and <= 64 }) throw Invalid();
        var volume = await client.GetAsync<KapowarrVolume>("volumes/" + Id(ParseId(input.RemoteId)), token);
        var item = Item(volume, null);
        if (item.RemoteId != input.RemoteId || input.ExpectedExternalIds.Any(pair => item.ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
            throw new IntegrationFailure("The remote run now has different identities. Refresh the connected library.");
        if (volume.Issues is null || volume.Issues.Length > 10000 || volume.Issues.Select(issue => issue.Id).Distinct().Count() != volume.Issues.Length) throw Invalid();
        var issues = volume.Issues.Select(issue => {
            if (issue.VolumeId != volume.Id || issue.ComicVineId <= 0) throw Invalid();
            var label = Required(issue.IssueNumber, 128);
            return new ManagedComicIssue(Id(issue.Id), label,
                string.IsNullOrWhiteSpace(issue.Title) ? "Issue " + label : Required(issue.Title, 512), issue.Monitored);
        }).ToArray();
        var files = Files(volume);
        return new(item with { RemoteFileCount = files.Count }, Required(volume.Folder, 8192), files, DateTimeOffset.UtcNow, issues);
    }

    private static IReadOnlyList<ManagedLibraryFile> Files(KapowarrVolume volume) {
        var evidence = new Dictionary<int, (KapowarrFile File, List<ManagedFileTarget> Targets)>();
        foreach (var issue in volume.Issues!) {
            if (issue.VolumeId != volume.Id || issue.ComicVineId <= 0 || issue.Files is null) throw Invalid();
            _ = Id(issue.Id);
            var label = Required(issue.IssueNumber, 128);
            if (issue.Files.Length > 1) throw new IntegrationFailure("This run has multiple files for one issue. Review its renditions in Kapowarr before inspecting exact file associations.");
            foreach (var file in issue.Files) {
                if (file.Size <= 0 || file.Id <= 0) throw Invalid();
                _ = Required(file.FilePath, 8192);
                if (!evidence.TryGetValue(file.Id, out var entry)) entry = (file, []);
                if (entry.File != file || entry.Targets.Count >= 1000) throw Invalid();
                var title = string.IsNullOrWhiteSpace(issue.Title) ? "Issue " + label : Required(issue.Title, 512);
                entry.Targets.Add(new(Id(issue.Id), MediaKinds.Comic, title, IssueLabel: label));
                evidence[file.Id] = entry;
            }
        }
        if (evidence.Count > 10000 || evidence.Values.Select(value => value.File.FilePath).Distinct(StringComparer.Ordinal).Count() != evidence.Count) throw Invalid();
        return evidence.OrderBy(pair => pair.Key).Select(pair => new ManagedLibraryFile(Id(pair.Key), pair.Value.File.FilePath!, pair.Value.File.Size, null, pair.Value.Targets)).ToArray();
    }

    private static ManagedLibraryItem Item(KapowarrVolume volume, int? fileCount) {
        if (volume.Year is < 0 or > 9999) throw Invalid();
        return new(Id(volume.Id), KapowarrCodes.ComicSeries, Required(volume.Title, 512), volume.Year,
            new Dictionary<string, string> { [KapowarrCodes.ComicVine] = "4050-" + Id(volume.ComicVineId) }, volume.Monitored, null, fileCount);
    }
    private static void RequireKind(string kind) { if (kind != KapowarrCodes.ComicSeries) throw new IntegrationFailure("Choose a comic series from the connected library."); }
    private static string Id(int value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : throw Invalid();
    private static int ParseId(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw Invalid();
    private static string Required(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl) ? value : throw Invalid();
    private static string LibraryLabel(string path) {
        var trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return path;
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return separator >= 0 && separator < trimmed.Length - 1 ? trimmed[(separator + 1)..] : trimmed;
    }
    private static T Input<T>(IntegrationRequest request) => request.Input.Deserialize<T>(IntegrationProtocol.Json) ?? throw Invalid();
    private static IntegrationFailure Invalid() => new("Kapowarr returned invalid, ambiguous, or oversized comic library evidence.");
}
