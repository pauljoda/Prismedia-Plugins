using System.Text.Json;

await Prismedia.Plugin.OpenLibrary.OpenLibraryPluginHost.RunAsync(args);

namespace Prismedia.Plugin.OpenLibrary {

internal static class OpenLibraryPluginHost {
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static async Task RunAsync(string[] args) {
        try {
            if (args.Length == 0) {
                Write(new IdentifyPluginResponse(false, null, "Missing request JSON path."));
                return;
            }

            var request = JsonSerializer.Deserialize<IdentifyPluginRequest>(
                await File.ReadAllTextAsync(args[0]),
                JsonOptions);
            if (request is null) {
                Write(new IdentifyPluginResponse(false, null, "Request JSON was empty or invalid."));
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var plugin = new OpenLibraryPlugin(new OpenLibraryApiClient(http));
            Write(new IdentifyPluginResponse(true, await plugin.IdentifyAsync(request), null));
        } catch (Exception ex) {
            Write(new IdentifyPluginResponse(false, null, ex.Message));
        }
    }

    private static void Write(IdentifyPluginResponse response) =>
        Console.Write(JsonSerializer.Serialize(response, JsonOptions));
}
}
