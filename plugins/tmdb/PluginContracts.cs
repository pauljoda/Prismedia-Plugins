namespace Prismedia.Plugin.Tmdb;

internal sealed record IdentifyPluginRequest(
    string Action,
    IReadOnlyDictionary<string, string> Auth,
    IdentifyEntitySnapshot Entity,
    IdentifyQuery Query,
    IdentifyMatchHints Hints,
    IdentifyStructuralContext? StructuralContext = null,
    bool IncludeNsfw = false);

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
    IReadOnlyDictionary<string, string>? ExternalIds);

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
    string CandidateId,
    IReadOnlyDictionary<string, string> ExternalIds,
    string Title,
    string? Description,
    string? ThumbnailUrl,
    int? Year,
    string? Source,
    decimal? Confidence,
    string? MatchReason);

internal sealed record CreditPatch(
    string Name,
    string Role,
    string? Character,
    int? SortOrder);

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
    string? Classification);

internal sealed record EntityMetadataProposal(
    string ProposalId,
    string Provider,
    string TargetKind,
    decimal? Confidence,
    string? MatchReason,
    EntityMetadataPatch Patch,
    IReadOnlyList<ImageCandidate> Images,
    IReadOnlyList<EntityMetadataProposal> Children,
    Guid? TargetEntityId = null,
    IReadOnlyList<EntityMetadataProposal>? Relationships = null);

internal sealed record IdentifyPluginResult(
    string Type,
    EntityMetadataProposal? Proposal,
    IReadOnlyList<EntitySearchCandidate> Candidates) {
    public const string ProposalType = "proposal";
    public const string CandidatesType = "candidates";
    public const string NoneType = "none";

    public static IdentifyPluginResult ForProposal(EntityMetadataProposal proposal) =>
        new(ProposalType, proposal, []);

    public static IdentifyPluginResult ForCandidates(IReadOnlyList<EntitySearchCandidate> candidates) =>
        new(CandidatesType, null, candidates);

    public static IdentifyPluginResult None() =>
        new(NoneType, null, []);
}

internal sealed record IdentifyPluginResponse(
    bool Ok,
    IdentifyPluginResult? Result,
    string? Error);
