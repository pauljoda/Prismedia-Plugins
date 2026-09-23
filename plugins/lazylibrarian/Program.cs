using Prismedia.Plugin.Integrations;
using Prismedia.Plugin.LazyLibrarian;

await IntegrationProgram.RunAsync(args, async (request, token) => {
    using var client = new LazyLibrarianClient(request.Connection);
    return await new LazyLibrarianLibrary(client, request.Connection).DispatchAsync(request, token);
});
