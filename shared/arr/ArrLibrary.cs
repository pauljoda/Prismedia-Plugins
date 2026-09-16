using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

/// <summary>Common manager protocol, with concrete adapters declaring and translating only their supported controls.</summary>
internal abstract class ArrLibrary(ArrClient client, string appName, int supportedMajor, string kind) {
    protected ArrClient Client => client;
    protected string Kind => kind;
    protected virtual IReadOnlyList<string> ControlOperations => [];
    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken cancellationToken) {
        var status = await client.GetAsync<ArrStatus>("system/status", cancellationToken);
        if (status.AppName != appName || !Version.TryParse(status.Version, out var version) || version.Major != supportedMajor)
            throw new IntegrationFailure($"This adapter requires {appName} {supportedMajor}.x with API v3.");
        // Neither supported API supplies a persistent installation UUID. Do not invent one from a name or path.
        if (request.Connection.ExpectedInstanceId is not null) throw new IntegrationFailure("This application does not report a persistent installation ID. Reconnect it with its current identity policy.");
        if (request.Operation == IntegrationOperations.Probe) return new ProbeResult(null, appName, status.Version, [
            new(ManagerProtocol.ConnectedLibrary, [ManagerProtocol.SearchLibrary, ManagerProtocol.GetLibraryItem], [kind]),
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
            var snapshot = await GetAsync(ParseId(input.RemoteId), cancellationToken);
            if (input.ExpectedExternalIds.Any(pair => snapshot.Item.ExternalIds.GetValueOrDefault(pair.Key) != pair.Value))
                throw new IntegrationFailure("The remote item now has different metadata identities. Refresh the library before using it.");
            return snapshot;
        }
        if (request.Operation == ManagerProtocol.Options) {
            if (Input<ManagerOptionsInput>(request).EntityKind != kind) throw new IntegrationFailure("Unsupported manager kind.");
            var profiles = await client.GetAsync<ArrProfile[]>("qualityprofile", cancellationToken);
            var roots = await client.GetAsync<ArrRoot[]>("rootfolder", cancellationToken);
            return new ManagerOptions(profiles.Select(profile => new ManagerChoice(Id(profile.Id), profile.Name)).ToArray(),
                roots.Select(root => new ManagerRootChoice(Id(root.Id), root.Path, root.Accessible)).ToArray());
        }
        return await DispatchControlAsync(request, cancellationToken);
    }
    protected virtual Task<object> DispatchControlAsync(IntegrationRequest request, CancellationToken cancellationToken) =>
        throw new IntegrationFailure("This operation is not implemented by the installed adapter.");
    protected abstract Task<IReadOnlyList<ManagedLibraryItem>> ListAsync(CancellationToken cancellationToken);
    protected abstract Task<ManagedItemSnapshot> GetAsync(int id, CancellationToken cancellationToken);
    protected static string Id(int id) => id > 0 ? id.ToString(CultureInfo.InvariantCulture) : throw new IntegrationFailure("The application returned an invalid item identity.");
    protected static int ParseId(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : throw new IntegrationFailure("Select a valid remote item identity.");
    protected static Dictionary<string, string> Identities(string primaryNamespace, int primaryId, string? imdbId = null) {
        var result = new Dictionary<string, string> { [primaryNamespace] = Id(primaryId) };
        if (!string.IsNullOrWhiteSpace(imdbId)) result[ManagerProtocol.Imdb] = imdbId;
        return result;
    }
    protected static T Input<T>(IntegrationRequest request) => request.Input.Deserialize<T>(IntegrationProtocol.Json) ?? throw new IntegrationFailure("The operation input is missing.");
}

// Typed records below are the single decode boundary for the external API v3 wire vocabulary.
internal sealed record ArrStatus(string AppName, string Version);
internal sealed record ArrProfile(int Id, string Name);
internal sealed record ArrRoot(int Id, string Path, bool? Accessible);
