using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.Suwayomi;

await IntegrationProgram.RunAsync(args, async (request, cancellationToken) => {
    using var http = new SuwayomiClient(request.Connection);
    return await new SuwayomiIntegration(http, request.Connection).DispatchAsync(request, cancellationToken);
});
