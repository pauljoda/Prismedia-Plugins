using System.Text.Json;
using Prismedia.Plugin.Metadata;
using Prismedia.Plugin.Metron;

IdentifyPluginResponse response;
try {
    if (args.Length != 1) throw new ArgumentException("Provide one request JSON file.");
    var request = JsonSerializer.Deserialize<IdentifyPluginRequest>(await File.ReadAllTextAsync(args[0]), MetronClient.ProtocolJson)
        ?? throw new ArgumentException("The metadata request is empty.");
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var http = new HttpClient(handler);
    response = new(true, await new MetronPlugin(http).IdentifyAsync(request), null);
} catch (Exception error) {
    response = new(false, null, error is ArgumentException or InvalidOperationException or InvalidDataException
        ? error.Message : error is OperationCanceledException ? "Metron metadata lookup timed out. Try a smaller issue scope."
        : "Metron could not complete the metadata lookup.");
}
Console.Write(JsonSerializer.Serialize(response, MetronClient.ProtocolJson));
