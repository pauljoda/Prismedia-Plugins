using Prismedia.Plugin.ArchiveOrg;
using Prismedia.Plugin.Integrations;

await IntegrationProgram.RunAsync(args, async (request, cancellationToken) => {
    using var http = new ArchiveOrgHttpClient(request.Connection);
    return await new ArchiveOrgIntegration(http).DispatchAsync(request, cancellationToken);
});
