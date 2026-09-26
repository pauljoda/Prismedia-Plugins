using System.Text.Json;

namespace Prismedia.Plugin.Integrations;

/// <summary>Bounded one-request/one-response entry point shared by integration executables.</summary>
public static class IntegrationProgram {
    #region Actions - Invocation
    /// <summary>Reads a bounded invocation file, applies a deadline, and emits exactly one correlated JSON envelope.</summary>
    public static async Task RunAsync(string[] args, Func<IntegrationRequest, CancellationToken, Task<object>> dispatch) {
        IntegrationRequest? request = null;
        IntegrationResponse response;
        try {
            if (args.Length != 1 || new FileInfo(args[0]).Length > 16 * 1024 * 1024) throw new IntegrationFailure("Invalid invocation request.");
            request = JsonSerializer.Deserialize<IntegrationRequest>(await File.ReadAllTextAsync(args[0]), IntegrationProtocol.Json)
                ?? throw new IntegrationFailure("The invocation request is empty.");
            if (request.Protocol != IntegrationProtocol.Name || request.ProtocolVersion != IntegrationProtocol.Version
                || request.InvocationId == Guid.Empty || request.Connection?.Auth is null || request.Connection.Settings is null)
                throw new IntegrationFailure("Unsupported integration protocol or incomplete connection.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            var result = await dispatch(request, deadline.Token);
            response = new(IntegrationProtocol.Name, IntegrationProtocol.Version, request.InvocationId, true, result, null);
        } catch (Exception error) {
            var message = error switch {
                IntegrationFailure => error.Message,
                OperationCanceledException => "The remote operation timed out.",
                HttpRequestException => "The remote HTTP request failed.",
                JsonException => "The remote application returned invalid JSON.",
                _ => "The integration operation failed."
            };
            foreach (var secret in (request?.Connection?.Auth?.Values ?? []).Where(value => !string.IsNullOrEmpty(value)).OrderByDescending(value => value.Length)) {
                message = message.Replace(secret, "[redacted]", StringComparison.Ordinal)
                    .Replace(Uri.EscapeDataString(secret), "[redacted]", StringComparison.Ordinal);
            }
            response = new(IntegrationProtocol.Name, IntegrationProtocol.Version, request?.InvocationId ?? Guid.Empty, false, null,
                message.Length <= 4096 ? message : message[..4096], (error as IntegrationFailure)?.Code);
        }
        Console.WriteLine(JsonSerializer.Serialize(response, IntegrationProtocol.Json));
    }
    #endregion
}
