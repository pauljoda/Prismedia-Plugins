using Prismedia.Plugin.Archiver;
using Prismedia.Plugin.Integrations;

await IntegrationProgram.RunAsync(args, async (request, cancellationToken) => {
    using var client = new ArchiverClient(request.Connection);
    return await new ArchiverIntegration(client, request.Connection).DispatchAsync(request, cancellationToken);
});
