using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Kapowarr;

internal sealed partial class KapowarrLibrary {
    #region Actions - Discovery
    private async Task<ManagedDiscoveryPage> DiscoverAsync(ManagedDiscoveryQuery input, CancellationToken token) {
        RequireKind(input.EntityKind);
        if (string.IsNullOrWhiteSpace(input.Query) || input.Query.Length > 512 || input.Query.Any(char.IsControl)
            || input.Limit is < 1 or > 100)
            throw new IntegrationFailure("Enter a catalog search of up to 512 characters and a page size from 1 to 100.");
        var found = await SearchCatalogAsync(input.Query.Trim(), token);
        return new(found.Take(input.Limit).Select(volume => new ManagedDiscoveryCandidate(
            ManagerProtocol.ComicSeries, Required(volume.Title, 512), volume.Year,
            ComicVineIdentity.Series.Identities(volume.ComicVineId))).ToArray());
    }

    private async Task<KapowarrSearchVolume[]> SearchCatalogAsync(string query, CancellationToken token) {
        var found = await client.GetAsync<KapowarrSearchVolume[]>("volumes/search?query=" + Uri.EscapeDataString(query), token);
        if (found.Length > 50 || found.Any(volume => volume.ComicVineId <= 0 || volume.Year is < 0 or > 9999 || volume.AlreadyAdded is < 0)
            || found.Select(volume => volume.ComicVineId).Distinct().Count() != found.Length) throw Invalid();
        return found;
    }
    #endregion

    #region Actions - Creation
    /// <summary>
    /// Resolves one exact Comic Vine run, and optionally one exact issue, without mutating Kapowarr.
    /// A run absent from the library is confirmed through Kapowarr's catalog search instead.
    /// </summary>
    private async Task<ManagedLookupResult> LookupAsync(ManagedLookupInput input, CancellationToken token) {
        var work = RequireWork(input);
        var matches = (await ReadCatalogAsync(token)).Where(volume => volume.ComicVineId == work.SeriesId).ToArray();
        if (matches.Length > 1)
            throw new ManagedMutationRejection("The exact Comic Vine run is ambiguous in Kapowarr.");
        if (matches.Length == 0) {
            var exact = (await SearchCatalogAsync(ComicVineIdentity.Series.Format(work.SeriesId), token))
                .Where(volume => volume.ComicVineId == work.SeriesId).ToArray();
            if (exact.Length != 1 || exact[0].AlreadyAdded is > 0)
                throw new ManagedMutationRejection("Kapowarr did not confirm one unadded Comic Vine run. Refresh its catalog.");
            return new(new(ManagerProtocol.ComicSeries, Required(exact[0].Title, 512), exact[0].Year,
                ComicVineIdentity.Series.Identities(work.SeriesId)), null);
        }
        var item = Item(matches[0]);
        var run = await ReadRunAsync(new(item.EntityKind, item.RemoteId, item.ExternalIds), token);
        var candidate = new ManagedCandidate(item.EntityKind, item.Title, item.Year, item.ExternalIds);
        if (work.Target is not { } target) return new(candidate, run.Snapshot);
        return new(candidate, run.Snapshot, [KapowarrRun.Target(run.RequireIssue(target.ComicVineId, target.Label))]);
    }

    /// <summary>
    /// Adds one reviewed run with run, issue, and new-issue monitoring and search off, then re-reads it.
    /// An existing run is returned unchanged. A lost add response stays uncertain so the host resolves
    /// the exact identity instead of repeating an add that may already have succeeded.
    /// </summary>
    private Task<EnsureManagedResult> EnsureAsync(EnsureManagedInput input, CancellationToken token) =>
        ManagedMutationRejection.CaptureCreationAsync(async () => {
            if (input.OperationId == Guid.Empty || input.ProfileId is not null
                || string.IsNullOrWhiteSpace(input.ExpectedRootPath) || input.ExpectedRootPath.Length > 8192)
                throw new ManagedMutationRejection("Choose a mapped comic root and durable operation ID. Kapowarr runs have no profiles.");
            var work = RequireWork(input.Work);
            var rootId = ParseId(input.RootId);
            var lookup = await LookupAsync(input.Work, token);
            if (lookup.Existing is { } existing)
                return new(ManagerControls.Applied, existing, Targets: lookup.Targets);
            if ((await ReadRootsAsync(token)).Count(root => root.Id == rootId && root.Folder == input.ExpectedRootPath) != 1)
                throw new ManagedMutationRejection("The selected Kapowarr root changed. Review its mapped library again.");
            var added = await client.PostAsync<KapowarrAddVolume, KapowarrVolumeReceipt>("volumes",
                KapowarrAddVolume.Unmonitored(work.SeriesId, rootId), token);
            return await ManagedMutationRejection.AfterWriteAsync(() => ConfirmAddedAsync(input, work, added, token),
                "Kapowarr accepted the run, but it could not be confirmed.");
        });

    private async Task<EnsureManagedResult> ConfirmAddedAsync(EnsureManagedInput input, KapowarrWork work,
        KapowarrVolumeReceipt added, CancellationToken token) {
        var observed = await ReadRunAsync(new(ManagerProtocol.ComicSeries, Id(added.Id), input.Work.ExternalIds), token);
        if (observed.Monitored || !RemoteLibraryPath.TryParse(input.ExpectedRootPath, out var root) || !root.IsAncestorOf(observed.Snapshot.Path))
            throw new IntegrationFailure("The added comic run's monitoring or root could not be confirmed.");
        if (work.Target is not { } target) return new(ManagerControls.Applied, observed.Snapshot, true);
        var issue = observed.RequireIssue(target.ComicVineId, target.Label);
        if (issue.Monitored)
            throw new IntegrationFailure("The added comic issue was monitored before its reviewed request.");
        return new(ManagerControls.Applied, observed.Snapshot, true, Targets: [KapowarrRun.Target(issue)]);
    }

    private static KapowarrWork RequireWork(ManagedLookupInput input) {
        RequireKind(input.EntityKind);
        if (input.ExternalIds is not { Count: 1 }
            || !ComicVineIdentity.Series.TryParse(input.ExternalIds.GetValueOrDefault(ComicVineIdentity.Namespace), out var seriesId)
            || input.Targets is { Count: not 1 })
            throw new ManagedMutationRejection("Select one exact Comic Vine run and at most one exact issue.");
        if (input.Targets is not { Count: 1 }) return new(seriesId, null);
        var target = input.Targets[0];
        if (target.EntityKind != MediaKinds.Comic || target.ExternalIds is not { Count: 1 }
            || !ComicVineIdentity.Issue.TryParse(target.ExternalIds.GetValueOrDefault(ComicVineIdentity.Namespace), out var issueId)
            || string.IsNullOrWhiteSpace(target.IssueLabel) || target.IssueLabel.Length > 128
            || target.SeasonNumber is not null || target.EpisodeNumber is not null || target.AbsoluteNumber is not null)
            throw new ManagedMutationRejection("Select one exact Comic Vine issue with its label.");
        return new(seriesId, new(issueId, target.IssueLabel));
    }
    #endregion

    /// <summary>A validated lookup: one Comic Vine run and at most one exact issue.</summary>
    private sealed record KapowarrWork(int SeriesId, KapowarrIssueTarget? Target);

    /// <summary>One exact Comic Vine issue identity and label.</summary>
    private sealed record KapowarrIssueTarget(int ComicVineId, string Label);
}
