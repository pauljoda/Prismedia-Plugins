using System.Text.Json;
using Prismedia.Plugin.Metadata;
using Prismedia.Plugin.GoogleBooks;

IdentifyPluginResponse response;
try {
    if (args.Length != 1) throw new ArgumentException("Provide one request JSON file.");
    var request = JsonSerializer.Deserialize<IdentifyPluginRequest>(await File.ReadAllTextAsync(args[0]), GoogleBooksClient.JsonOptions)
        ?? throw new ArgumentException("The metadata request is empty.");
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var http = new HttpClient(handler);
    response = new(true, await new GoogleBooksPlugin(http).IdentifyAsync(request), null);
} catch (Exception error) {
    response = new(false, null, error is ArgumentException or InvalidOperationException or InvalidDataException
        ? error.Message : "Google Books could not complete the metadata lookup.");
}
Console.Write(JsonSerializer.Serialize(response, GoogleBooksClient.JsonOptions));
