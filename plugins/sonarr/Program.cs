using Prismedia.Plugin.Arr;
using Prismedia.Plugin.Integrations;
await IntegrationProgram.RunAsync(args, async (request, cancellationToken) => {
    using var client = new ArrClient(request.Connection);
    return await new SonarrLibrary(client).DispatchAsync(request, cancellationToken);
});
