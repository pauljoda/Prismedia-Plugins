namespace Prismedia.Plugin.Integrations;

/// <summary>Canonical operation for a read-only external-manager catalog search.</summary>
public static class ManagerDiscovery {
    public const string Search = "discover-managed";
}

/// <summary>Canonical semantic date codes carried by normalized manager discovery metadata.</summary>
public static class ManagerDiscoveryDates {
    public const string TheatricalRelease = "theatrical-release";
    public const string DigitalRelease = "digital-release";
    public const string PhysicalRelease = "physical-release";
}

/// <summary>A bounded title search of one external manager's upstream catalog.</summary>
public sealed record ManagedDiscoveryQuery(string EntityKind, string Query, int Limit);

/// <summary>Canonical credit-role codes understood by Prismedia's shared people relationship model.</summary>
public static class ManagerCreditRoles {
    public const string Actor = "actor";
    public const string Director = "director";
    public const string Writer = "writer";
    public const string Producer = "producer";
    public const string Creator = "creator";
    public const string Composer = "composer";
}

/// <summary>A normalized person credit supplied by an external manager during exact metadata review.</summary>
public sealed record ManagedPersonCredit(
    string Name,
    string Role,
    string? Character,
    int? SortOrder,
    IReadOnlyDictionary<string, string>? ExternalIds = null,
    string? ProfileUrl = null);

/// <summary>Optional normalized descriptive metadata supplied by an external manager catalog.</summary>
public sealed record ManagedDiscoveryMetadata(
    string? OriginalTitle = null,
    string? Overview = null,
    string? Studio = null,
    string? Classification = null,
    int? RuntimeMinutes = null,
    decimal? Rating = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyDictionary<string, string>? Dates = null,
    IReadOnlyList<string>? Urls = null,
    string? PosterUrl = null,
    string? BackdropUrl = null,
    IReadOnlyList<ManagedPersonCredit>? Credits = null);

/// <summary>Manager-confirmed catalog candidate in Prismedia integration vocabulary.</summary>
public sealed record ManagedDiscoveryCandidate(string EntityKind, string Title, int? Year,
    IReadOnlyDictionary<string, string> ExternalIds, ManagedDiscoveryMetadata? Metadata = null);

/// <summary>Bounded manager catalog results.</summary>
public sealed record ManagedDiscoveryPage(IReadOnlyList<ManagedDiscoveryCandidate> Items);
