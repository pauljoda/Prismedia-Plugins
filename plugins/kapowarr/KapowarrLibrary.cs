using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Kapowarr;

/// <summary>
/// Reads existing comic runs and applies reviewed actions to one exact issue of a Kapowarr 1.3.x
/// installation. Reads report confirmed removal only after a complete catalog corroborates a 404.
/// </summary>
internal sealed partial class KapowarrLibrary(KapowarrClient client) {
    #region Static Variables
    private const int MaximumCatalogSize = 100000;
    private const int MaximumRootCount = 1000;
    #endregion

    #region Actions - Dispatch
    /// <summary>Checks the Kapowarr release, then runs one integration operation.</summary>
    /// <param name="request">The correlated host request.</param>
    /// <param name="token">Invocation deadline.</param>
    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken token) {
        var about = await client.GetAsync<KapowarrAbout>("system/about", token);
        var versionText = about.Version?.StartsWith('V') == true ? about.Version[1..] : about.Version;
        if (!Version.TryParse(versionText, out var version) || version.Major != 1 || version.Minor != 3)
            throw new IntegrationFailure("This adapter requires Kapowarr 1.3.x.");
        if (request.Connection.ExpectedInstanceId is not null)
            throw new IntegrationFailure("Kapowarr does not report a persistent installation ID. Reconnect it with its current identity policy.");
        if (request.Operation == IntegrationOperations.Probe) return new ProbeResult(null, "Kapowarr", about.Version, [
            new(ManagerProtocol.ConnectedLibrary, [ManagerProtocol.SearchLibrary, ManagerProtocol.GetLibraryItem, ManagerProtocol.ListLibraries], [ManagerProtocol.ComicSeries]),
            new(ManagerProtocol.ExternalManager, [ManagerDiscovery.Search, ManagerProtocol.Options, ManagerControls.Reconcile, ManagerControls.Configure, ManagerControls.Request, ManagerCreation.Lookup, ManagerCreation.Ensure], [ManagerProtocol.ComicSeries])
        ]);
        if (request.Operation == ManagerProtocol.SearchLibrary) return await SearchAsync(request, token);
        if (request.Operation == ManagerProtocol.GetLibraryItem) return await GetLibraryItemAsync(Input<ManagedItemInput>(request), token);
        if (request.Operation == ManagerControls.Reconcile) return await ReconcileAsync(Input<ReconcileManagedInput>(request), token);
        if (request.Operation == ManagerControls.Configure) return await ConfigureAsync(Input<ConfigureManagedInput>(request), token);
        if (request.Operation == ManagerControls.Request) return await RequestAsync(Input<RequestManagedInput>(request), token);
        if (request.Operation == ManagerCreation.Lookup) return await LookupAsync(Input<ManagedLookupInput>(request), token);
        if (request.Operation == ManagerCreation.Ensure) return await EnsureAsync(Input<EnsureManagedInput>(request), token);
        if (request.Operation == ManagerDiscovery.Search) return await DiscoverAsync(Input<ManagedDiscoveryQuery>(request), token);
        if (request.Operation == ManagerProtocol.ListLibraries) {
            var roots = await ReadRootsAsync(token);
            return new ProviderLibraryCatalog(roots.Select(root => {
                var path = Required(root.Folder, 8192);
                return new ProviderLibraryDescriptor(Id(root.Id), LibraryLabel(path), path, [ManagerProtocol.ComicSeries], request.Connection.BaseUrl);
            }).ToArray());
        }
        if (request.Operation == ManagerProtocol.Options) {
            RequireKind(Input<ManagerOptionsInput>(request).EntityKind);
            var roots = await ReadRootsAsync(token);
            return new ManagerOptions([], roots.Select(root => new ManagerRootChoice(Id(root.Id), Required(root.Folder, 8192), null)).ToArray());
        }
        throw new IntegrationFailure("This Kapowarr operation is not supported.");
    }
    #endregion

    #region Actions - Library
    private async Task<ManagedLibraryPage> SearchAsync(IntegrationRequest request, CancellationToken token) {
        var input = Input<ManagedLibraryQuery>(request);
        RequireKind(input.EntityKind);
        if (input.Limit is < 1 or > 100 || input.Query?.Length > 512 || input.Cursor?.Length > 8192)
            throw new IntegrationFailure("Choose a page size from 1 to 100 and a query of up to 512 characters.");
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request.Connection.Id, input.EntityKind, input.Query, input.Limit }))));
        var after = 0;
        if (!string.IsNullOrEmpty(input.Cursor)) {
            var parts = input.Cursor.Split(':');
            if (parts.Length != 2 || parts[0] != scope || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 1)
                throw new IntegrationFailure("This library page belongs to another search. Start the search again.");
        }
        var items = (await ReadCatalogAsync(token)).Select(Item).Where(item => ParseId(item.RemoteId) > after
            && (string.IsNullOrWhiteSpace(input.Query) || item.Title.Contains(input.Query.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.ExternalIds.Values.Contains(input.Query.Trim(), StringComparer.OrdinalIgnoreCase)))
            .OrderBy(item => ParseId(item.RemoteId)).Take(input.Limit + 1).ToArray();
        var page = items.Take(input.Limit).ToArray();
        return new(page, items.Length > input.Limit ? scope + ":" + page[^1].RemoteId : null);
    }

    /// <summary>
    /// Reads one run. A 404 alone can mean a broken route; the complete catalog must also omit the run
    /// before this read reports confirmed removal to the host.
    /// </summary>
    private async Task<ManagedItemSnapshot> GetLibraryItemAsync(ManagedItemInput input, CancellationToken token) {
        try {
            return (await ReadRunOrMissingAsync(input, token)).Snapshot;
        } catch (KapowarrRecordNotFound) {
            var catalog = await ReadCatalogAsync(token);
            if (catalog.Any(volume => Id(volume.Id) == input.RemoteId))
                throw new IntegrationFailure("The connected Kapowarr library changed during this check. Its previous state is retained.");
            throw new IntegrationFailure("This comic run was removed from Kapowarr.", IntegrationErrorCodes.ManagedItemNotFound);
        }
    }

    private async Task<KapowarrRun> ReadRunAsync(ManagedItemInput input, CancellationToken token) {
        try {
            return await ReadRunOrMissingAsync(input, token);
        } catch (KapowarrRecordNotFound) {
            throw new IntegrationFailure("This comic run is no longer in Kapowarr. Refresh the connected library.");
        }
    }

    private async Task<KapowarrRun> ReadRunOrMissingAsync(ManagedItemInput input, CancellationToken token) {
        RequireKind(input.EntityKind);
        if (input.ExpectedExternalIds is not { Count: > 0 and <= 64 })
            throw new ManagedMutationRejection("Select a comic run with its known Comic Vine identity.");
        var run = KapowarrRun.Decode(await client.GetAsync<KapowarrVolume>("volumes/" + Id(ParseId(input.RemoteId)), token));
        var item = run.Snapshot.Item;
        if (item.RemoteId != input.RemoteId || input.ExpectedExternalIds.Any(pair => item.ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
            throw new ManagedMutationRejection("The remote run now has different identities. Refresh the connected library.");
        return run;
    }

    private async Task<KapowarrVolume[]> ReadCatalogAsync(CancellationToken token) {
        var volumes = await client.GetAsync<KapowarrVolume[]>("volumes", token);
        if (volumes.Length > MaximumCatalogSize || volumes.Select(volume => volume.Id).Distinct().Count() != volumes.Length)
            throw Invalid();
        foreach (var volume in volumes) _ = Item(volume);
        return volumes;
    }

    private async Task<KapowarrRoot[]> ReadRootsAsync(CancellationToken token) {
        var roots = await client.GetAsync<KapowarrRoot[]>("rootfolder", token);
        if (roots.Length > MaximumRootCount || roots.Select(root => root.Id).Distinct().Count() != roots.Length) throw Invalid();
        return roots;
    }
    #endregion

    #region Actions - Evidence
    /// <summary>The host item for one validated run, without file counts.</summary>
    /// <exception cref="IntegrationFailure">The run's title, year, or Comic Vine identity is invalid.</exception>
    internal static ManagedLibraryItem Item(KapowarrVolume volume) {
        if (volume.Year is < 0 or > 9999) throw Invalid();
        return new(Id(volume.Id), ManagerProtocol.ComicSeries, Required(volume.Title, 512), volume.Year,
            ComicVineIdentity.Series.Identities(volume.ComicVineId), volume.Monitored, null, null);
    }

    /// <summary>Spells one positive Kapowarr ID from remote evidence.</summary>
    internal static string Id(int value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : throw Invalid();

    /// <summary>Requires bounded, printable remote text.</summary>
    internal static string Required(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl) ? value : throw Invalid();

    /// <summary>The failure for invalid, ambiguous, or oversized remote evidence.</summary>
    internal static IntegrationFailure Invalid() => new("Kapowarr returned invalid, ambiguous, or oversized comic library evidence.");

    private static void RequireKind(string kind) {
        if (kind != ManagerProtocol.ComicSeries)
            throw new ManagedMutationRejection("Choose a comic series from the connected library.");
    }

    private static int ParseId(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new ManagedMutationRejection("Select a valid Kapowarr run, issue, or root identity.");

    private static string LibraryLabel(string path) {
        var trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return path;
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return separator >= 0 && separator < trimmed.Length - 1 ? trimmed[(separator + 1)..] : trimmed;
    }

    private static T Input<T>(IntegrationRequest request) =>
        request.Input.Deserialize<T>(IntegrationProtocol.Json) ?? throw new IntegrationFailure("The operation input is missing.");
    #endregion
}
