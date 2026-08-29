using System.Text.Json;
using TvAIrPlugin.Runtime;

namespace TvAIrPlugin.Bridge;

public sealed record PluginBridgeRequest
{
    public required string ProtocolVersion { get; init; }
    public required string RuntimeSessionId { get; init; }
    public required string RequestId { get; init; }
    public required string Method { get; init; }
    public JsonElement Parameters { get; init; }
    public string? CancellationId { get; init; }
}

public sealed record PluginBridgeResponse
{
    public required string RequestId { get; init; }
    public bool Succeeded { get; init; }
    public object? Result { get; init; }
    public TvAirError? Error { get; init; }
}

public sealed record PluginBridgeCapabilityDescriptor(
    string Method,
    string RequestSchemaVersion,
    string ResponseSchemaVersion);

public interface ITvAirPluginBridgeApi
{
    string ProtocolVersion { get; }
    IReadOnlyList<PluginBridgeCapabilityDescriptor> ListMethods();
    Task<PluginBridgeResponse> InvokeAsync(PluginBridgeRequest request, CancellationToken cancellationToken = default);
    TvAirOperationResult Cancel(string cancellationId);
}
