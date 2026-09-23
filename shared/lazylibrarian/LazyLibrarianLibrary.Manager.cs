using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

internal sealed partial class LazyLibrarianLibrary {
    private static ManagerOptions Options(ManagerOptionsInput input, string ebookRoot, string audiobookRoot) {
        RequireBookRendition(input.EntityKind, input.BookRendition);
        var root = input.BookRendition == LazyLibrarianCodes.EbookRendition ? ebookRoot : audiobookRoot;
        return new([], [new(input.BookRendition!, root, null)]);
    }

    private async Task<ManagedLookupResult> LookupAsync(ManagedLookupInput input,
        string ebookRoot, string audiobookRoot, CancellationToken token) {
        RequireBookRendition(input.EntityKind, input.BookRendition);
        if (input.Targets is { Count: > 0 }
            || input.ExternalIds is not { Count: 1 }
            || !input.ExternalIds.TryGetValue(LazyLibrarianCodes.OpenLibraryWork, out var workId)
            || !ValidWorkId(workId)) throw Invalid();
        // LazyLibrarian's findBook searches names, not exact work IDs. Only an already
        // catalogued work can be proved by its complete BookID and author-detail read.
        var snapshot = await GetAsync(new(MediaKinds.Book, workId, input.ExternalIds,
            input.BookRendition), ebookRoot, audiobookRoot, token);
        return new(new(MediaKinds.Book, snapshot.Item.Title, snapshot.Item.Year,
            snapshot.Item.ExternalIds), snapshot);
    }

    private async Task<ManagedControlState> ReconcileAsync(ReconcileManagedInput input,
        string ebookRoot, string audiobookRoot, CancellationToken token) {
        var snapshot = await ScopeSnapshotAsync(input.Scope, ebookRoot, audiobookRoot, token);
        var command = input.Command is { } reference
            ? new ManagedCommandSnapshot(reference, ManagerControls.Unknown,
                "LazyLibrarian does not expose a durable result for this exact search.")
            : null;
        return new(snapshot.Item, snapshot.Path, [], new(true, true, false), command);
    }

    private async Task<ManagedMutationResult> ConfigureAsync(ConfigureManagedInput input,
        string ebookRoot, string audiobookRoot, CancellationToken token) {
        if (input.OperationId == Guid.Empty || input.Changes is null
            || input.Changes.ProfileId is not null || input.Changes.Monitored is not { } desired
            || input.ExpectedProfileId is not null || input.ExpectedMonitoring is not { Count: 0 })
            throw Invalid();
        var snapshot = await ScopeSnapshotAsync(input.Scope, ebookRoot, audiobookRoot, token);
        if (snapshot.Path != input.ExpectedPath) return new(ManagerControls.Rejected);
        if (snapshot.Item.Monitored == desired) return new(ManagerControls.Applied);
        var rendition = Type(input.Scope.Item.BookRendition!);
        await client.AcknowledgeAsync(desired ? LazyLibrarianCodes.QueueBook : LazyLibrarianCodes.UnqueueBook,
            Arguments(input.Scope.Item.RemoteId, rendition), token);
        var confirmed = await ScopeSnapshotAsync(input.Scope, ebookRoot, audiobookRoot, token);
        if (confirmed.Path != input.ExpectedPath || confirmed.Item.Monitored != desired)
            throw new IntegrationFailure("LazyLibrarian did not confirm this rendition's monitoring state. Review the remote work before another change.");
        return new(ManagerControls.Applied);
    }

    private async Task<ManagedMutationResult> RequestAsync(RequestManagedInput input,
        string ebookRoot, string audiobookRoot, CancellationToken token) {
        if (input.OperationId == Guid.Empty || input.ExpectedProfileId is not null) throw Invalid();
        var snapshot = await ScopeSnapshotAsync(input.Scope, ebookRoot, audiobookRoot, token);
        if (snapshot.Path != input.ExpectedPath || !snapshot.Item.Monitored)
            return new(ManagerControls.Rejected);
        await client.AcknowledgeAsync(LazyLibrarianCodes.SearchBook,
            Arguments(input.Scope.Item.RemoteId, Type(input.Scope.Item.BookRendition!)), token);
        // The API's OK starts background work; it does not prove a release was found.
        // Keep a host correlation ID, then report Unknown on later observation.
        var command = new ManagedCommandSnapshot(
            new(input.OperationId.ToString("N"), DateTimeOffset.UtcNow), ManagerControls.Pending);
        return new(ManagerControls.Accepted, command);
    }

    private async Task<ManagedItemSnapshot> ScopeSnapshotAsync(ManagedControlScope? scope,
        string ebookRoot, string audiobookRoot, CancellationToken token) {
        if (scope?.Item is null || scope.Targets is not { Count: 0 }) throw Invalid();
        RequireBookRendition(scope.Item.EntityKind, scope.Item.BookRendition);
        return await GetAsync(scope.Item, ebookRoot, audiobookRoot, token);
    }

    private static IReadOnlyDictionary<string, string> Arguments(string bookId, string type) =>
        new Dictionary<string, string> {
            [LazyLibrarianCodes.IdParameter] = bookId,
            [LazyLibrarianCodes.TypeParameter] = type
        };

    private static string Type(string rendition) => rendition switch {
        LazyLibrarianCodes.EbookRendition => LazyLibrarianCodes.Ebook,
        LazyLibrarianCodes.AudiobookRendition => LazyLibrarianCodes.Audiobook,
        _ => throw Invalid()
    };

    private static void RequireBookRendition(string kind, string? rendition) {
        if (kind != MediaKinds.Book || rendition is not
                (LazyLibrarianCodes.EbookRendition or LazyLibrarianCodes.AudiobookRendition))
            throw Invalid();
    }

    private static bool ValidWorkId(string? value) => value is { Length: >= 4 and <= 512 }
        && value.StartsWith(LazyLibrarianCodes.OpenLibraryPrefix, StringComparison.Ordinal)
        && value.EndsWith(LazyLibrarianCodes.OpenLibraryWorkSuffix)
        && value.AsSpan(2, value.Length - 3).IndexOfAnyExceptInRange('0', '9') < 0;
}
