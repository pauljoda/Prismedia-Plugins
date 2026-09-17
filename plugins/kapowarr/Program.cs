using Prismedia.Plugin.Kapowarr;
using Prismedia.Plugin.Integrations;
await IntegrationProgram.RunAsync(args, async (request, token) => {
    using var client = new KapowarrClient(request.Connection);
    return await new KapowarrLibrary(client).DispatchAsync(request, token);
});
