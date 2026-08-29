using TvAIrPlugin.Runtime;

namespace TvAIrPlugin.Surfaces;

public enum PluginSurfaceKind { Web, Native, Document, Canvas, OverlayScene, HostProvided }
public enum PluginSurfaceLifecycleState { Created, Started, Suspended, Closed, Failed }
public enum HostSurfaceKind { WindowContent, ApplicationPage, ApplicationPanel, ViewerOverlay, ViewerCompanion, Background }

public sealed record PluginSurfaceDefinition
{
    public required string SurfaceDefinitionId { get; init; }
    public required PluginSurfaceKind Kind { get; init; }
    public required string EntryPoint { get; init; }
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();
}
public sealed record CreatePluginSurfaceRequest
{
    public required string SurfaceDefinitionId { get; init; }
    public string InstanceKey { get; init; } = "default";
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
}
public sealed record PluginSurfaceState
{
    public required string SurfaceDefinitionId { get; init; }
    public required string SurfaceInstanceId { get; init; }
    public required string InstanceKey { get; init; }
    public PluginSurfaceKind Kind { get; init; }
    public PluginSurfaceLifecycleState LifecycleState { get; init; }
    public IReadOnlyList<string> AttachedHostSurfaceIds { get; init; } = Array.Empty<string>();
}
public sealed record HostSurfaceState
{
    public required string HostSurfaceInstanceId { get; init; }
    public required HostSurfaceKind Kind { get; init; }
    public required string OwnerInstanceId { get; init; }
    public string SlotId { get; init; } = "main";
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AttachedSurfaceIds { get; init; } = Array.Empty<string>();
}
public sealed record AttachPluginSurfaceRequest
{
    public required string SurfaceInstanceId { get; init; }
    public required string HostSurfaceInstanceId { get; init; }
    public string SlotId { get; init; } = "main";
}
public sealed record PluginSurfaceAttachment
{
    public required string AttachmentId { get; init; }
    public required string SurfaceInstanceId { get; init; }
    public required string HostSurfaceInstanceId { get; init; }
    public string SlotId { get; init; } = "main";
}
public interface ITvAirPluginSurfacesApi
{
    TvAirOperationResult<PluginSurfaceState> Create(CreatePluginSurfaceRequest request);
    TvAirOperationResult Start(string surfaceInstanceId);
    TvAirOperationResult Suspend(string surfaceInstanceId);
    TvAirOperationResult Resume(string surfaceInstanceId);
    TvAirOperationResult Close(string surfaceInstanceId);
    TvAirOperationResult<PluginSurfaceState> Get(string surfaceInstanceId);
    IReadOnlyList<PluginSurfaceState> List();
}
public interface ITvAirHostSurfacesApi
{
    TvAirOperationResult<HostSurfaceState> Resolve(HostSurfaceKind kind, string ownerInstanceId, string slotId = "main");
    TvAirOperationResult<PluginSurfaceAttachment> Attach(AttachPluginSurfaceRequest request);
    TvAirOperationResult Detach(string attachmentId);
    IReadOnlyList<HostSurfaceState> List();
}
