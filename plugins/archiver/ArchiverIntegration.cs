using System.Text.Json;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Archiver;

/// <summary>Maps the independent Archiver executor API to Prismedia's declared integration capabilities.</summary>
internal sealed class ArchiverIntegration(ArchiverClient client, ConnectionContext connection) {
    private static readonly Capability[] Capabilities = [
        new(IntegrationCapabilities.Discovery, [IntegrationOperations.Inspect], [MediaKinds.Book, MediaKinds.Comic, MediaKinds.Image, MediaKinds.Gallery]),
        new(IntegrationCapabilities.TransferExecutor, [IntegrationOperations.Submit, IntegrationOperations.FindSubmission,
            IntegrationOperations.GetJob, IntegrationOperations.Cancel, IntegrationOperations.CancelSubmission, IntegrationOperations.ListArtifacts,
            IntegrationOperations.AuthorizeArtifact, IntegrationOperations.RenewRetention, IntegrationOperations.Acknowledge], [MediaKinds.Book, MediaKinds.Comic, MediaKinds.Image, MediaKinds.Gallery])
    ];
    internal async Task<object> DispatchAsync(IntegrationRequest request, CancellationToken cancellationToken) {
        var system = await client.SendAsync<SystemInfo>(HttpMethod.Get, "system", null, cancellationToken)
            ?? throw new IntegrationFailure("The Archiver did not return its identity.");
        if (string.IsNullOrWhiteSpace(system.InstanceId) || system.InstanceId.Length > 512 || !Version.TryParse(system.ApiVersion, out var version) || version.Major != 1
            || !(system.OutputProfiles?.Contains(ArchiverWire.Profile) == true || system.OutputProfiles?.Contains(ArchiverWire.ImageProfile) == true || system.OutputProfiles?.Contains(ArchiverWire.GalleryProfile) == true) || system.MaximumItems < 1 || system.MaximumBytes < 1
            || new[] { ArchiverWire.Inspect, ArchiverWire.Submit, ArchiverWire.Cancel, ArchiverWire.CancelOperation, ArchiverWire.Artifacts, ArchiverWire.Retention, ArchiverWire.Receipts }
                .Any(capability => system.Capabilities?.Contains(capability) != true))
            throw new IntegrationFailure("This server does not support the required Archiver executor API v1 output profiles.");
        if (connection.ExpectedInstanceId is not null && connection.ExpectedInstanceId != system.InstanceId)
            throw new IntegrationFailure("The Archiver installation identity changed. Reconcile the saved connection before creating work.");
        switch (request.Operation) {
            case IntegrationOperations.Probe:
                return new ProbeResult(system.InstanceId, "The Archiver", system.ApplicationVersion, Capabilities.Select(capability => capability with {
                    EntityKinds = capability.EntityKinds.Where(kind => system.OutputProfiles.Contains(ProfileForKind(kind))).ToArray()
                }).ToArray());
            case IntegrationOperations.Inspect: {
                var input = Input<InspectTransferInput>(request);
                var kind = input.EntityKind switch { MediaKinds.Book => ArchiverWire.Book, MediaKinds.Comic => ArchiverWire.Comic, MediaKinds.Image => ArchiverWire.Image, MediaKinds.Gallery => ArchiverWire.Gallery, _ => throw new IntegrationFailure("This executor profile supports books, comics, still images, and ordered galleries.") };
                if (!system.OutputProfiles.Contains(ProfileForKind(input.EntityKind)))
                    throw new IntegrationFailure("The server does not support the selected output profile.");
                var inspected = await client.SendAsync<Inspection>(HttpMethod.Post, "inspect", new InspectRequest(input.Url, kind, input.MaximumItems), cancellationToken)
                    ?? throw new IntegrationFailure("The source returned no inspection.");
                if (inspected.Items is not { Count: > 0 } || inspected.Items.Count > input.MaximumItems || inspected.Items.Any(item => item.MediaKind != kind || item.Formats is null))
                    throw new IntegrationFailure("The source returned unsupported or unbounded publication choices.");
                var formats = kind switch {
                    ArchiverWire.Book => new[] { ArchiverWire.Epub, ArchiverWire.Pdf },
                    ArchiverWire.Image => [ArchiverWire.Png, ArchiverWire.Jpeg, ArchiverWire.Webp],
                    ArchiverWire.Gallery => [ArchiverWire.ImageSet],
                    _ => [ArchiverWire.Cbz]
                };
                var format = formats.FirstOrDefault(candidate => inspected.Items.All(item => item.Formats.Contains(candidate)))
                    ?? throw new IntegrationFailure("The selection does not offer a consistent supported publication format.");
                var pinned = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new PinnedSelection(inspected.Id, format), IntegrationProtocol.Json));
                return new TransferInspection(pinned, inspected.Revision, inspected.ExpiresAt, inspected.CanonicalUrl,
                    inspected.Items.Select(item => new InspectedTransferItem(item.Id, item.Title, input.EntityKind)).ToArray(), inspected.Items.Count > 1, inspected.Warnings);
            }
            case IntegrationOperations.Submit: {
                var input = Input<SubmitTransferInput>(request);
                PinnedSelection pinned;
                try { pinned = JsonSerializer.Deserialize<PinnedSelection>(Convert.FromBase64String(input.SelectionId), IntegrationProtocol.Json)!; }
                catch (Exception error) when (error is FormatException or JsonException) { throw new IntegrationFailure("The inspected selection is invalid."); }
                if (pinned is null || pinned.Format is not (ArchiverWire.Epub or ArchiverWire.Pdf or ArchiverWire.Cbz or ArchiverWire.Png or ArchiverWire.Jpeg or ArchiverWire.Webp or ArchiverWire.ImageSet) || input.ItemIds.Count != 1 || input.MaximumItems != 1)
                    throw new IntegrationFailure("Select one supported publication from a current inspection.");
                var profile = pinned.Format switch {
                    ArchiverWire.Png or ArchiverWire.Jpeg or ArchiverWire.Webp => ArchiverWire.ImageProfile,
                    ArchiverWire.ImageSet => ArchiverWire.GalleryProfile,
                    _ => ArchiverWire.Profile
                };
                if (!system.OutputProfiles.Contains(profile)) throw new IntegrationFailure("The server no longer supports the inspected output profile.");
                return await client.SendAsync<JobSnapshot>(HttpMethod.Post, "jobs", new SubmitJob(input.ClientOperationId,
                    new(input.Url), new(pinned.Id, input.SelectionRevision, input.ItemIds), new(profile, pinned.Format), new(input.MaximumItems, input.MaximumBytes)),
                    cancellationToken, input.ClientOperationId) ?? throw new IntegrationFailure("The job was not durably accepted.");
            }
            case IntegrationOperations.FindSubmission: {
                var input = Input<FindTransferInput>(request);
                return new { job = await client.SendAsync<JobSnapshot>(HttpMethod.Get, $"operations/{input.ClientOperationId}", null, cancellationToken, allowMissing: true) };
            }
            case IntegrationOperations.CancelSubmission: {
                var input = Input<FindTransferInput>(request);
                return await client.SendAsync<OperationCancellation>(HttpMethod.Post, $"operations/{input.ClientOperationId}/cancel", null, cancellationToken)
                    ?? throw new IntegrationFailure("The executor did not confirm its operation cancellation fence.");
            }
            case IntegrationOperations.GetJob:
            case IntegrationOperations.Cancel: {
                var input = Input<RemoteTransferJobInput>(request);
                var cancel = request.Operation == IntegrationOperations.Cancel;
                return await client.SendAsync<JobSnapshot>(cancel ? HttpMethod.Post : HttpMethod.Get, $"jobs/{Escape(input.JobId)}" + (cancel ? "/cancel" : ""), null, cancellationToken)
                    ?? throw new IntegrationFailure("The job is unavailable.");
            }
            case IntegrationOperations.ListArtifacts: {
                var input = Input<ReadTransferManifestInput>(request);
                return await ReadManifestAsync(input, cancellationToken);
            }
            case IntegrationOperations.AuthorizeArtifact: {
                var input = Input<AuthorizeTransferArtifactInput>(request);
                string? cursor = null;
                var seen = new HashSet<string>();
                for (var pageCount = 0; pageCount < 100; pageCount++) {
                    var page = await ReadManifestAsync(new(input.JobId, input.Revision, cursor, 100), cancellationToken);
                    if (page.Artifacts.FirstOrDefault(file => file.Id == input.ArtifactId) is { } artifact)
                        return new HttpArtifactDelivery(client.ArtifactUrl(artifact.ContentPath).AbsoluteUri, client.Headers,
                            Path.GetFileName(artifact.RelativePath), artifact.SizeBytes, artifact.Sha256);
                    cursor = page.NextCursor;
                    if (string.IsNullOrEmpty(cursor)) break;
                    if (!seen.Add(cursor)) throw new IntegrationFailure("The artifact manifest repeated a continuation cursor.");
                }
                throw new IntegrationFailure("The requested artifact is absent from the sealed manifest.");
            }
            case IntegrationOperations.RenewRetention: {
                var input = Input<RenewTransferRetentionInput>(request);
                return await client.SendAsync<LeaseResult>(HttpMethod.Post, $"jobs/{Escape(input.JobId)}/lease", new LeaseRequest(input.RetainUntil), cancellationToken)
                    ?? throw new IntegrationFailure("The executor did not confirm output retention.");
            }
            case IntegrationOperations.Acknowledge: {
                var input = Input<AcknowledgeTransferInput>(request);
                var receipt = new ReceiptRequest(input.ReceiptId, input.ManifestRevision, input.Artifacts.Select(artifact =>
                    new ImportedArtifact(artifact.ArtifactId, artifact.Sha256, string.Join(",", artifact.EntityIds.Order()))).ToArray());
                return await client.SendAsync<ReceiptResult>(HttpMethod.Post, $"jobs/{Escape(input.JobId)}/receipts", receipt, cancellationToken)
                    ?? throw new IntegrationFailure("The executor did not confirm the import receipt.");
            }
            default: throw new IntegrationFailure("This Archiver operation is not supported.");
        }
    }
    private async Task<ManifestPage> ReadManifestAsync(ReadTransferManifestInput input, CancellationToken cancellationToken) {
        var path = $"jobs/{Escape(input.JobId)}/artifacts?revision={Escape(input.Revision)}&limit={input.Limit}";
        if (input.Cursor is not null) path += "&cursor=" + Escape(input.Cursor);
        var page = await client.SendAsync<ManifestPage>(HttpMethod.Get, path, null, cancellationToken)
            ?? throw new IntegrationFailure("The manifest is unavailable.");
        if (page.JobId != input.JobId || page.Revision != input.Revision || !page.Sealed || page.Artifacts is null || page.Artifacts.Count > input.Limit)
            throw new IntegrationFailure("The returned manifest does not match its requested sealed revision.");
        return page;
    }
    private static string ProfileForKind(string kind) => kind switch {
        MediaKinds.Image => ArchiverWire.ImageProfile,
        MediaKinds.Gallery => ArchiverWire.GalleryProfile,
        _ => ArchiverWire.Profile
    };
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static T Input<T>(IntegrationRequest request) => request.Input.Deserialize<T>(IntegrationProtocol.Json) ?? throw new IntegrationFailure("The operation input is missing.");
}
