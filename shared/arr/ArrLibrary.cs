using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

/// <summary>Common manager protocol, with concrete adapters declaring and translating only their supported controls.</summary>
internal abstract class ArrLibrary(ArrClient client, string appName, int supportedMajor, string kind) {
    #region Static Variables
    private const string InvalidIdentityProblem = "Select a valid remote item identity.";
    #endregion

    #region Variables
    protected ArrClient Client => client;
    protected string Kind => kind;
    protected virtual IReadOnlyList<string> ControlOperations => [];
    #endregion

    #region Abstract Methods
    protected abstract Task<IReadOnlyList<ManagedLibraryItem>> ListAsync(CancellationToken cancellationToken);
    protected abstract Task<ManagedItemSnapshot> GetAsync(int id, CancellationToken cancellationToken);
    #endregion

    #region Actions - Dispatch
    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken cancellationToken) {
        var status = await client.GetAsync<ArrStatus>("system/status", cancellationToken);
        if (status.AppName != appName || !Version.TryParse(status.Version, out var version) || version.Major != supportedMajor)
            throw new IntegrationFailure($"This adapter requires {appName} {supportedMajor}.x with API v3.");
        // Neither supported API supplies a persistent installation UUID. Do not invent one from a name or path.
        if (request.Connection.ExpectedInstanceId is not null) throw new IntegrationFailure("This application does not report a persistent installation ID. Reconnect it with its current identity policy.");
        if (request.Operation == IntegrationOperations.Probe) return new ProbeResult(null, appName, status.Version, [
            new(ManagerProtocol.ConnectedLibrary, [ManagerProtocol.SearchLibrary, ManagerProtocol.GetLibraryItem, ManagerProtocol.ListLibraries], [kind]),
            new(ManagerProtocol.ExternalManager, [ManagerProtocol.Options, .. ControlOperations], [kind])
        ]);
        if (request.Operation == ManagerProtocol.SearchLibrary) {
            var input = Input<ManagedLibraryQuery>(request);
            if (input.EntityKind != kind || input.Limit is < 1 or > 100 || input.Query?.Length > 512) throw new IntegrationFailure("Choose a supported library kind and bounded page size.");
            var all = await ListAsync(cancellationToken);
            if (all.Count > 100000 || all.Select(item => item.RemoteId).Distinct().Count() != all.Count) throw new IntegrationFailure("The library returned duplicate or excessive holdings.");
            var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request.Connection.Id, input.EntityKind, input.Query, input.Limit }))));
            var after = 0;
            if (!string.IsNullOrEmpty(input.Cursor)) {
                var parts = input.Cursor.Split(':');
                if (parts.Length != 2 || parts[0] != scope || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 1)
                    throw new IntegrationFailure("The library page belongs to another search. Start the search again.");
            }
            var matching = all.Where(item => ParseId(item.RemoteId) > after && (string.IsNullOrWhiteSpace(input.Query)
                || item.Title.Contains(input.Query.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.ExternalIds.Values.Contains(input.Query.Trim(), StringComparer.OrdinalIgnoreCase)))
                .OrderBy(item => ParseId(item.RemoteId)).Take(input.Limit + 1).ToArray();
            var page = matching.Take(input.Limit).ToArray();
            return new ManagedLibraryPage(page, matching.Length > input.Limit ? scope + ":" + page[^1].RemoteId : null);
        }
        if (request.Operation == ManagerProtocol.GetLibraryItem) {
            var input = Input<ManagedItemInput>(request);
            if (input.EntityKind != kind || input.ExpectedExternalIds is not { Count: > 0 and <= 64 }) throw new IntegrationFailure("Select a holding with its known metadata identities.");
            ManagedItemSnapshot snapshot;
            try {
                snapshot = await GetAsync(ParseId(input.RemoteId), cancellationToken);
            } catch (ArrHoldingNotFoundException) {
                // A single 404 can mean a broken endpoint. A healthy full catalog must also
                // confirm absence before the host may archive the retained local title.
                var all = await ListAsync(cancellationToken);
                if (all.Count > 100000 || all.Select(item => item.RemoteId).Distinct().Count() != all.Count
                    || all.Any(item => item.RemoteId == input.RemoteId))
                    throw new IntegrationFailure("The connected library changed during this check. Its previous state is retained.");
                throw new IntegrationFailure("This title was removed from the connected library.", IntegrationErrorCodes.ManagedItemNotFound);
            }
            if (input.ExpectedExternalIds.Any(pair => snapshot.Item.ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
                throw new IntegrationFailure("The remote item now has different metadata identities. Refresh the library before using it.");
            return snapshot;
        }
        if (request.Operation == ManagerProtocol.ListLibraries) {
            var roots = await client.GetAsync<ArrRoot[]>("rootfolder", cancellationToken);
            if (roots.Length > 1000 || roots.Select(root => root.Id).Distinct().Count() != roots.Length) throw new IntegrationFailure("The application returned duplicate or excessive libraries.");
            return new ProviderLibraryCatalog(roots.Select(root => {
                var path = Required(root.Path, 8192);
                return new ProviderLibraryDescriptor(Id(root.Id), LibraryLabel(path), path, [kind], request.Connection.BaseUrl);
            }).ToArray());
        }
        if (request.Operation == ManagerProtocol.Options) {
            if (Input<ManagerOptionsInput>(request).EntityKind != kind) throw new IntegrationFailure("Unsupported manager kind.");
            var profiles = await client.GetAsync<ArrProfile[]>("qualityprofile", cancellationToken);
            var roots = await client.GetAsync<ArrRoot[]>("rootfolder", cancellationToken);
            return new ManagerOptions(profiles.Select(profile => new ManagerChoice(Id(profile.Id), profile.Name)).ToArray(),
                roots.Select(root => new ManagerRootChoice(Id(root.Id), root.Path, root.Accessible)).ToArray());
        }
        try {
            return await DispatchControlAsync(request, cancellationToken);
        } catch (ManagedMutationRejection rejection) {
            // Mutations capture their refusals as rejected outcomes, so a refusal reaching this point
            // came from a precondition check that a read shares with them. Reads report it as a plain failure.
            throw new IntegrationFailure(rejection.Problem);
        }
    }
    protected virtual Task<object> DispatchControlAsync(IntegrationRequest request, CancellationToken cancellationToken) =>
        throw new IntegrationFailure("This operation is not implemented by the installed adapter.");
    #endregion

    #region Actions - Evidence
    /// <summary>
    /// Requires a complete catalog to exclude both the former manager ID and its canonical metadata
    /// identity. This prevents a delete/re-add race from being mistaken for confirmed absence.
    /// </summary>
    protected async Task ConfirmHoldingAbsentAsync(
        ManagedControlScope scope,
        string canonicalIdentityNamespace,
        CancellationToken cancellationToken) {
        var all = await ListAsync(cancellationToken);
        if (all.Count > 100000 || all.Select(item => item.RemoteId).Distinct(StringComparer.Ordinal).Count() != all.Count)
            throw new IntegrationFailure("The connected library returned duplicate or excessive holdings while confirming removal.");
        var expectedIdentity = scope.Item.ExpectedExternalIds[canonicalIdentityNamespace];
        if (all.Any(item => item.RemoteId == scope.Item.RemoteId
            || item.ExternalIds.GetValueOrDefault(canonicalIdentityNamespace) == expectedIdentity))
            throw new IntegrationFailure("The managed holding is present in the connected library. Refresh its association before releasing ownership.");
    }
    #endregion

    #region Actions - Identities
    protected static string Id(int id) => id > 0 ? id.ToString(CultureInfo.InvariantCulture) : throw new IntegrationFailure("The application returned an invalid item identity.");
    protected static int ParseId(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new IntegrationFailure(InvalidIdentityProblem);
    /// <summary>Parses a host-selected positive ID that a manager mutation relies on.</summary>
    /// <exception cref="ManagedMutationRejection">The selection is not a positive decimal ID.</exception>
    protected static int ParseSelectedId(string? value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new ManagedMutationRejection(InvalidIdentityProblem);
    protected static Dictionary<string, string> Identities(string primaryNamespace, int primaryId, string? imdbId = null) {
        var result = new Dictionary<string, string> { [primaryNamespace] = Id(primaryId) };
        if (!string.IsNullOrWhiteSpace(imdbId)) result[ManagerProtocol.Imdb] = imdbId;
        return result;
    }
    #endregion

    #region Actions - Parsing
    private static string Required(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum
        && !value.Any(char.IsControl) ? value : throw new IntegrationFailure("The application returned an invalid library path.");
    private static string LibraryLabel(string path) {
        var trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return path;
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return separator >= 0 && separator < trimmed.Length - 1 ? trimmed[(separator + 1)..] : trimmed;
    }
    protected static T Input<T>(IntegrationRequest request) => request.Input.Deserialize<T>(IntegrationProtocol.Json) ?? throw new IntegrationFailure("The operation input is missing.");
    #endregion
}

// Typed records below are the single decode boundary for the external API v3 wire vocabulary.
internal sealed record ArrStatus(string AppName, string Version);
internal sealed record ArrProfile(int Id, string Name);
internal sealed record ArrRoot(int Id, string Path, bool? Accessible);
/// <summary>Internal read evidence; only a separately confirmed catalog observation can classify removal.</summary>
internal sealed class ArrHoldingNotFoundException : Exception;
