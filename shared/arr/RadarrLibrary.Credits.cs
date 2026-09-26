using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Arr;

internal sealed partial class RadarrLibrary {
    #region Static Variables
    private const int MaximumRemoteCredits = 5000;
    private const int MaximumNormalizedCredits = 1000;
    private const int MaximumCreditSortOrder = 1_000_000;
    #endregion

    #region Actions - Credits
    /// <summary>Reads Radarr's movie-scoped credit resource for one exact existing holding.</summary>
    private async Task<IReadOnlyList<ManagedPersonCredit>> CreditsAsync(int movieId, CancellationToken token) {
        var credits = await Client.GetAsync<MovieCredit?[]>($"credit?movieId={Id(movieId)}", token);
        if (credits.Length > MaximumRemoteCredits)
            throw new IntegrationFailure("The manager returned excessive movie credits.");

        var normalized = credits
            .Select(NormalizeCredit)
            .Where(credit => credit is not null)
            .Select(credit => credit!)
            .ToArray();
        if (normalized.Length > MaximumNormalizedCredits)
            throw new IntegrationFailure("The manager returned excessive relevant movie credits.");
        return normalized;
    }

    private static ManagedPersonCredit? NormalizeCredit(MovieCredit? credit, int index) {
        if (credit is null || string.IsNullOrWhiteSpace(credit.PersonName) || credit.PersonName.Length > 512
            || credit.PersonName.Any(char.IsControl)) return null;

        var role = CreditRole(credit);
        if (role is null) return null;

        var isCast = string.Equals(credit.Type, ArrCreditType.Cast, StringComparison.OrdinalIgnoreCase);
        var character = isCast ? CreditText(credit.Character, 512) : null;
        var externalIds = credit.PersonTmdbId > 0
            ? new Dictionary<string, string> { [ManagerProtocol.Tmdb] = Id(credit.PersonTmdbId) }
            : null;
        var order = credit.Order is >= 0 and <= MaximumCreditSortOrder ? credit.Order : index;
        if (!isCast) order = 1000 + Math.Min(order, MaximumCreditSortOrder - 1000);

        return new(
            credit.PersonName.Trim(),
            role,
            character,
            order,
            externalIds,
            ArrPresentation.PublicImage(credit.Images, ArrCoverType.Headshot));
    }

    private static string? CreditRole(MovieCredit credit) {
        if (string.Equals(credit.Type, ArrCreditType.Cast, StringComparison.OrdinalIgnoreCase))
            return ManagerCreditRoles.Actor;
        if (!string.Equals(credit.Type, ArrCreditType.Crew, StringComparison.OrdinalIgnoreCase))
            return null;

        return credit.Job switch {
            ArrCreditJob.Director => ManagerCreditRoles.Director,
            ArrCreditJob.Writer or ArrCreditJob.Screenplay or ArrCreditJob.Story or ArrCreditJob.Teleplay => ManagerCreditRoles.Writer,
            ArrCreditJob.Producer or ArrCreditJob.ExecutiveProducer or ArrCreditJob.CoProducer or ArrCreditJob.AssociateProducer => ManagerCreditRoles.Producer,
            ArrCreditJob.Creator => ManagerCreditRoles.Creator,
            ArrCreditJob.Composer or ArrCreditJob.OriginalMusicComposer => ManagerCreditRoles.Composer,
            _ => null
        };
    }

    private static string? CreditText(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            ? null
            : value.Trim();
    #endregion

    private sealed record MovieCredit(
        string? PersonName,
        int PersonTmdbId,
        ArrImage?[]? Images,
        string? Department,
        string? Job,
        string? Character,
        int Order,
        string? Type);
}

/// <summary>External Radarr credit-type vocabulary at its single protocol boundary.</summary>
internal static class ArrCreditType {
    #region Static Variables
    internal const string Cast = "cast";
    internal const string Crew = "crew";
    #endregion
}

/// <summary>External Radarr/TMDB crew-job vocabulary mapped into Prismedia's bounded roles.</summary>
internal static class ArrCreditJob {
    #region Static Variables
    internal const string Director = "Director";
    internal const string Writer = "Writer";
    internal const string Screenplay = "Screenplay";
    internal const string Story = "Story";
    internal const string Teleplay = "Teleplay";
    internal const string Producer = "Producer";
    internal const string ExecutiveProducer = "Executive Producer";
    internal const string CoProducer = "Co-Producer";
    internal const string AssociateProducer = "Associate Producer";
    internal const string Creator = "Creator";
    internal const string Composer = "Composer";
    internal const string OriginalMusicComposer = "Original Music Composer";
    #endregion
}
