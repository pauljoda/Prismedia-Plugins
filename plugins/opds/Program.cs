using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.Opds;

await IntegrationProgram.RunAsync(args, async (request, cancellationToken) => {
    using var http = new OpdsHttpClient(request.Connection);
    return await new OpdsIntegration(http, request.Connection).DispatchAsync(request, cancellationToken);
});
