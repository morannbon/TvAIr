using TvAIrPlugin.Runtime;

namespace TvAIrPlugin.WebRuntime;

public enum PluginWebRuntimeLifecycleState
{
    Created,
    Starting,
    Running,
    Suspended,
    Closing,
    Closed,
    Failed
}

public sealed record StartPluginWebRuntimeRequest
{
    public required string SurfaceInstanceId { get; init; }
    public required string EntryPoint { get; init; }
    public IReadOnlyDictionary<string, object?> InitialState { get; init; }
        = new Dictionary<string, object?>();
}

public sealed record PluginWebRuntimeState
{
    public required string RuntimeSessionId { get; init; }
    public required string WebRuntimeInstanceId { get; init; }
    public required string SurfaceInstanceId { get; init; }
    public required Uri Origin { get; init; }
    public required string EntryPoint { get; init; }
    public PluginWebRuntimeLifecycleState LifecycleState { get; init; }
    public string? LastError { get; init; }
}

public interface ITvAirPluginWebRuntimeApi
{
    IReadOnlyList<string> Capabilities { get; }
    TvAirOperationResult<PluginWebRuntimeState> Start(StartPluginWebRuntimeRequest request);
    TvAirOperationResult Suspend(string webRuntimeInstanceId);
    TvAirOperationResult Resume(string webRuntimeInstanceId);
    TvAirOperationResult Close(string webRuntimeInstanceId);
    TvAirOperationResult<PluginWebRuntimeState> Get(string webRuntimeInstanceId);
    IReadOnlyList<PluginWebRuntimeState> List();
}
