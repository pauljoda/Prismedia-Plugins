namespace Prismedia.Plugin.OpenLibrary;

internal sealed record IdentifyPluginRequest(
    int ProtocolVersion,
    string Action,
    IReadOnlyDictionary<string, string> Auth,
    IdentifyEntitySnapshot Entity,
    IdentifyQuery Query,
    IdentifyMatchHints Hints,
    IdentifyStructuralContext? StructuralContext = null,
    bool IncludeNsfw = false,
    bool IncludeRelationshipDetails = true,
    bool IncludeStructuralChildren = true);

internal sealed record IdentifyStructuralContext(
    IReadOnlyList<IdentifyEntitySnapshot> Ancestors,
    IReadOnlyDictionary<string, int> Positions);

internal sealed record IdentifyEntitySnapshot(
    Guid Id,
    string Kind,
    string Title,
    IReadOnlyDictionary<string, string>? ExternalIds = null,
    IReadOnlyList<string>? Urls = null);

internal sealed record IdentifyQuery(
    string? Title,
    string? Url,
    IReadOnlyDictionary<string, string>? ExternalIds,
    bool? RequireChoice = null,
    IReadOnlyDictionary<string, string>? Fields = null,
    int? Limit = null);

internal sealed record IdentifyMatchHints(
    IReadOnlyDictionary<string, string> ExternalIds,
    IReadOnlyList<string> Urls,
    string? Title,
    string? FilePath);

internal sealed record ImageCandidate(
    string Kind,
    string Url,
    string Source,
    decimal? Rank,
    string? Language,
    int? Width,
    int? Height);

internal sealed record EntitySearchCandidate(
    IReadOnlyDictionary<string, string> ExternalIds,
    string Title,
    int? Year,
    string? Overview,
    string? PosterUrl,
    decimal? Popularity,
    string? CandidateId = null,
    string? Source = null,
    decimal? Confidence = null,
    string? MatchReason = null);

internal sealed record CreditPatch(string Name, string Role, string? Character, int? SortOrder);

internal sealed record EntityMetadataFlagsPatch(bool? IsFavorite, bool? IsNsfw, bool? IsOrganized);

internal sealed record EntityMetadataPatch(
    string? Title,
    string? Description,
    IReadOnlyDictionary<string, string> ExternalIds,
    IReadOnlyList<string> Urls,
    IReadOnlyList<string> Tags,
    string? Studio,
    IReadOnlyList<CreditPatch> Credits,
    IReadOnlyDictionary<string, string> Dates,
    IReadOnlyDictionary<string, int> Stats,
    IReadOnlyDictionary<string, int> Positions,
    string? Classification) {
    public int? Rating { get; init; }
    public EntityMetadataFlagsPatch? Flags { get; init; }
}

internal sealed record EntityMetadataProposal(
    string ProposalId,
    string Provider,
    string TargetKind,
    decimal? Confidence,
    string? MatchReason,
    EntityMetadataPatch Patch,
    IReadOnlyList<ImageCandidate> Images,
    IReadOnlyList<EntityMetadataProposal> Children,
    IReadOnlyList<EntitySearchCandidate> Candidates,
    Guid? TargetEntityId = null,
    IReadOnlyList<EntityMetadataProposal>? Relationships = null);

internal sealed record IdentifyPluginResult(
    string Type,
    EntityMetadataProposal? Proposal,
    IReadOnlyList<EntitySearchCandidate> Candidates) {
    public static IdentifyPluginResult ForProposal(EntityMetadataProposal proposal) => new("proposal", proposal, []);
    public static IdentifyPluginResult ForCandidates(IReadOnlyList<EntitySearchCandidate> candidates) => new("candidates", null, candidates);
    public static IdentifyPluginResult None() => new("none", null, []);
}

internal sealed record IdentifyPluginResponse(
    bool Ok,
    IdentifyPluginResult? Result,
    string? Error);
