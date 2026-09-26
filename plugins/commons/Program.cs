using Prismedia.Plugin.Commons;
using Prismedia.Plugin.Integrations;

await IntegrationProgram.RunAsync(args, async (request, cancellationToken) => {
    using var http = new CommonsClient(request.Connection);
    return await new CommonsIntegration(http, request.Connection).DispatchAsync(request, cancellationToken);
});
