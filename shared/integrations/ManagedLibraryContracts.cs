namespace Prismedia.Plugin.Integrations;

/// <summary>Canonical wire vocabulary for connected holdings and manager reads.</summary>
public static class ManagerProtocol {
    public const string SearchLibrary = "search-library";
    public const string GetLibraryItem = "get-library-item";
    public const string Options = "manager-options";
    public const string ConnectedLibrary = "connected-library";
    public const string ExternalManager = "external-manager";
    public const string Movie = "movie";
    public const string Series = "video-series";
    public const string Episode = "video-episode";
    public const string Tmdb = "tmdb";
    public const string Tvdb = "tvdb";
    public const string Imdb = "imdb";
}
/// <summary>Read-only holdings query; pagination remains connection-scoped.</summary>
public sealed record ManagedLibraryQuery(string EntityKind, string? Query, string? Cursor, int Limit);
/// <summary>Remote file counts do not establish local availability.</summary>
public sealed record ManagedLibraryItem(string RemoteId, string EntityKind, string Title, int? Year,
    IReadOnlyDictionary<string, string> ExternalIds, bool Monitored, string? ProfileId, int? RemoteFileCount);
/// <summary>One bounded page of existing holdings.</summary>
public sealed record ManagedLibraryPage(IReadOnlyList<ManagedLibraryItem> Items, string? NextCursor = null);
/// <summary>Stable metadata identities fence reuse of an application's numeric item ID.</summary>
public sealed record ManagedItemInput(string EntityKind, string RemoteId, IReadOnlyDictionary<string, string> ExpectedExternalIds);
/// <summary>Exact content targets represented by a remote file, including combined episodes.</summary>
public sealed record ManagedFileTarget(string RemoteId, string EntityKind, string Title,
    int? SeasonNumber = null, int? EpisodeNumber = null, int? AbsoluteNumber = null, string? IssueLabel = null);
/// <summary>File evidence uses the external server's path namespace and is not a byte-transfer authorization.</summary>
public sealed record ManagedLibraryFile(string RemoteId, string Path, long SizeBytes, DateTimeOffset? AddedAt, IReadOnlyList<ManagedFileTarget> Targets);
/// <summary>Current remote item and its exact final file associations.</summary>
public sealed record ManagedItemSnapshot(ManagedLibraryItem Item, string Path, IReadOnlyList<ManagedLibraryFile> Files, DateTimeOffset ObservedAt);
/// <summary>Kind whose manager choices are requested.</summary>
public sealed record ManagerOptionsInput(string EntityKind);
/// <summary>Opaque external profile identity and display name.</summary>
public sealed record ManagerChoice(string Id, string Label);
/// <summary>External root evidence; never used directly as a local destination.</summary>
public sealed record ManagerRootChoice(string Id, string Path, bool? Accessible);
/// <summary>Existing external profiles and folders.</summary>
public sealed record ManagerOptions(IReadOnlyList<ManagerChoice> Profiles, IReadOnlyList<ManagerRootChoice> Roots);
