using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Integrations;
namespace Prismedia.Plugin.Kapowarr;

/// <summary>Reads existing comic runs and applies reviewed actions to one exact issue.</summary>
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
            new(ManagerProtocol.ExternalManager, [ManagerProtocol.Options, ManagerControls.Reconcile, ManagerControls.Configure, ManagerControls.Request, ManagerCreation.Lookup], [KapowarrCodes.ComicSeries])
        ]);
        if (request.Operation == ManagerProtocol.SearchLibrary) return await SearchAsync(request, token);
        if (request.Operation == ManagerProtocol.GetLibraryItem) return await GetAsync(Input<ManagedItemInput>(request), token);
        if (request.Operation == ManagerControls.Reconcile) return await ReconcileAsync(Input<ReconcileManagedInput>(request), token);
        if (request.Operation == ManagerControls.Configure) return await ConfigureAsync(Input<ConfigureManagedInput>(request), token);
        if (request.Operation == ManagerControls.Request) return await RequestAsync(Input<RequestManagedInput>(request), token);
        if (request.Operation == ManagerCreation.Lookup) return await LookupAsync(Input<ManagedLookupInput>(request), token);
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
        throw new IntegrationFailure("This Kapowarr operation is not supported.");
    }

    private async Task<ManagedControlState> ReconcileAsync(ReconcileManagedInput input, CancellationToken token) {
        if (input.Scope?.Item is null || input.Scope.Targets is not { Count: > 0 and <= 10000 }
            || input.Scope.Targets.Select(target => target.RemoteId).Distinct(StringComparer.Ordinal).Count() != input.Scope.Targets.Count
            || input.Scope.Targets.Any(target => target.EntityKind != MediaKinds.Comic || target.SeasonNumber is not null
                || target.EpisodeNumber is not null || target.AbsoluteNumber is not null
                || string.IsNullOrWhiteSpace(target.IssueLabel) || target.IssueLabel.Length > 128)) throw Invalid();
        var snapshot = await GetAsync(input.Scope.Item, token);
        var issues = snapshot.ComicIssues!.ToDictionary(issue => issue.RemoteId, StringComparer.Ordinal);
        var targets = input.Scope.Targets.Select(target => {
            if (!issues.TryGetValue(target.RemoteId, out var issue) || issue.IssueLabel != target.IssueLabel)
                throw new IntegrationFailure("The selected comic issue changed. Refresh the connected library before using manager controls.");
            return new ManagedTargetMonitoring(target, issue.Monitored);
        }).ToArray();
        var command = input.Command is { } reference
            ? new ManagedCommandSnapshot(reference, ManagerControls.Unknown, "Kapowarr cannot establish this command's original outcome.")
            : null;
        return new(snapshot.Item, snapshot.Path, targets,
            new(input.Scope.Targets.Count == 1, input.Scope.Targets.Count == 1, false,
                input.Scope.Targets.Count == 1 ? null : "Select one exact issue to change monitoring or search."), command);
    }

    private async Task<ManagedMutationResult> ConfigureAsync(ConfigureManagedInput input, CancellationToken token) {
        if (input.OperationId == Guid.Empty || input.Changes is null || input.Changes.ProfileId is not null
            || input.Changes.Monitored is not { } desired || input.ExpectedProfileId is not null
            || input.ExpectedMonitoring is null || input.Scope?.Targets is not { Count: 1 }) throw Invalid();
        var state = await ReconcileAsync(new(input.Scope), token);
        var target = state.Targets[0];
        if (state.Path != input.ExpectedPath || input.ExpectedMonitoring.Count != 1
            || !input.ExpectedMonitoring.TryGetValue(target.Target.RemoteId, out var expected) || target.Monitored != expected)
            return new(ManagerControls.Rejected);
        if (target.Monitored == desired) return new(ManagerControls.Applied);
        var issue = await client.PutAsync<KapowarrIssue>("issues/" + Id(ParseId(target.Target.RemoteId)),
            new { monitored = desired }, token);
        if (issue.Id != ParseId(target.Target.RemoteId) || issue.VolumeId != ParseId(input.Scope.Item.RemoteId)
            || issue.IssueNumber != target.Target.IssueLabel || issue.Monitored != desired)
            throw new IntegrationFailure("Kapowarr did not confirm the selected issue's monitoring change.");
        return new(ManagerControls.Applied);
    }

    private async Task<ManagedMutationResult> RequestAsync(RequestManagedInput input, CancellationToken token) {
        if (input.OperationId == Guid.Empty || input.ExpectedProfileId is not null
            || input.Scope?.Targets is not { Count: 1 }) throw Invalid();
        var state = await ReconcileAsync(new(input.Scope), token);
        if (state.Path != input.ExpectedPath) return new(ManagerControls.Rejected);
        var issueId = ParseId(state.Targets[0].Target.RemoteId);
        var volumeId = ParseId(input.Scope.Item.RemoteId);
        var receipt = await client.PostAsync<KapowarrTaskReceipt>("system/tasks",
            new { cmd = KapowarrCodes.AutoSearchIssue, volume_id = volumeId, issue_id = issueId }, token);
        if (receipt.Id <= 0) throw new IntegrationFailure("Kapowarr did not return a search task ID.");
        // Kapowarr's task IDs have no durable timestamp or completed history. Preserve the receipt,
        // but reconciliation must report Unknown instead of inferring that a vanished task succeeded.
        return new(ManagerControls.Accepted, new(new(Id(receipt.Id), DateTimeOffset.UtcNow), ManagerControls.Pending));
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

    private async Task<ManagedLookupResult> LookupAsync(ManagedLookupInput input, CancellationToken token) {
        RequireKind(input.EntityKind);
        if (input.ExternalIds is not { Count: 1 }
            || !input.ExternalIds.TryGetValue(KapowarrCodes.ComicVine, out var seriesId)
            || !ComicVineId(seriesId, KapowarrCodes.ComicVineSeriesPrefix)
            || input.Targets is not { Count: 1 }) throw Invalid();
        var target = input.Targets[0];
        if (target.EntityKind != MediaKinds.Comic || target.ExternalIds is not { Count: 1 }
            || !target.ExternalIds.TryGetValue(KapowarrCodes.ComicVine, out var issueId)
            || !ComicVineId(issueId, KapowarrCodes.ComicVineIssuePrefix)
            || string.IsNullOrWhiteSpace(target.IssueLabel) || target.IssueLabel.Length > 128
            || target.SeasonNumber is not null || target.EpisodeNumber is not null || target.AbsoluteNumber is not null)
            throw Invalid();
        var volumes = await client.GetAsync<KapowarrVolume[]>("volumes", token);
        if (volumes.Length > 100000 || volumes.Select(volume => volume.Id).Distinct().Count() != volumes.Length) throw Invalid();
        var matches = volumes.Where(volume => KapowarrCodes.ComicVineSeriesPrefix + Id(volume.ComicVineId) == seriesId).ToArray();
        if (matches.Length != 1)
            throw new IntegrationFailure("The exact Comic Vine run is missing or ambiguous in Kapowarr.");
        var item = Item(matches[0], null);
        var snapshot = await GetAsync(new(item.EntityKind, item.RemoteId, item.ExternalIds), token);
        var issues = snapshot.ComicIssues!.Where(issue => issue.ExternalIds?.GetValueOrDefault(KapowarrCodes.ComicVine) == issueId
            && issue.IssueLabel == target.IssueLabel).ToArray();
        if (issues.Length != 1)
            throw new IntegrationFailure("The exact Comic Vine issue is missing or its label changed in Kapowarr.");
        var issue = issues[0];
        return new(new(item.EntityKind, item.Title, item.Year, item.ExternalIds), snapshot,
            [new(issue.RemoteId, MediaKinds.Comic, issue.ExternalIds!, IssueLabel: issue.IssueLabel)]);
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
                string.IsNullOrWhiteSpace(issue.Title) ? "Issue " + label : Required(issue.Title, 512), issue.Monitored,
                new Dictionary<string, string> { [KapowarrCodes.ComicVine] = KapowarrCodes.ComicVineIssuePrefix + Id(issue.ComicVineId) });
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
            new Dictionary<string, string> { [KapowarrCodes.ComicVine] = KapowarrCodes.ComicVineSeriesPrefix + Id(volume.ComicVineId) }, volume.Monitored, null, fileCount);
    }
    private static void RequireKind(string kind) { if (kind != KapowarrCodes.ComicSeries) throw new IntegrationFailure("Choose a comic series from the connected library."); }
    private static string Id(int value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : throw Invalid();
    private static int ParseId(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw Invalid();
    private static bool ComicVineId(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
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
