using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prismedia.Plugin.Tmdb;

internal static class TmdbPluginHost {
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static async Task RunAsync(string[] args) {
        try {
            if (args.Length == 0) {
                throw new InvalidOperationException("Expected request JSON path as the first argument.");
            }

            var request = JsonSerializer.Deserialize<IdentifyPluginRequest>(
                await File.ReadAllTextAsync(args[0]),
                JsonOptions) ?? throw new InvalidOperationException("Request JSON was empty.");
            var apiKey = TmdbAuth.ReadApiKey(request.Auth);
            using var http = new HttpClient();
            var plugin = new TmdbPlugin(new TmdbApiClient(http, apiKey));
            var result = await plugin.IdentifyAsync(request);

            Write(new IdentifyPluginResponse(
                true,
                result,
                result.Type == IdentifyPluginResult.NoneType ? "No TMDB match was found." : null));
        } catch (Exception ex) {
            Write(new IdentifyPluginResponse(false, null, ex.Message));
        }
    }

    private static void Write(IdentifyPluginResponse response) =>
        Console.Write(JsonSerializer.Serialize(response, JsonOptions));
}
