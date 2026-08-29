using System.Text.Json.Serialization;
using TvAIrPlugin.Runtime;
namespace TvAIrPlugin.Overlay;

public sealed record CreateVideoOverlaySceneRequest(string SceneDefinitionId, string ViewerSessionId, long ExpectedGeneration, string InstanceKey = "default");
public sealed record VideoOverlaySceneState(
    string SceneInstanceId,
    string SceneDefinitionId,
    string ViewerSessionId,
    long Generation,
    bool Attached,
    long Revision = 1,
    bool IsClosed = false,
    int LayerCount = 0);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "elementType")]
[JsonDerivedType(typeof(VideoOverlayTextElement), typeDiscriminator: "text")]
public abstract record VideoOverlayElement(string ElementId);
public sealed record VideoOverlayTextElement(string ElementId, string Text, double FontSize, string Color, string Placement, TimeSpan? Duration = null) : VideoOverlayElement(ElementId);
public sealed record VideoOverlayLayerState(string LayerId, long Revision, IReadOnlyList<VideoOverlayElement> Elements);
public sealed record AddVideoOverlayElementsRequest(string SceneInstanceId, string LayerId, IReadOnlyList<VideoOverlayElement> Elements, long ExpectedGeneration, long? ExpectedRevision = null);
public sealed record ClearVideoOverlayLayerRequest(string SceneInstanceId, string LayerId, long ExpectedGeneration, long? ExpectedRevision = null);
// Close is resource release, not drawing. ExpectedGeneration identifies the scene lifecycle
// being closed, but the Host must not require that generation to remain the viewer's active one.
// Create/Add/Clear continue to require the active viewer generation.
public sealed record CloseVideoOverlaySceneRequest(string SceneInstanceId, long ExpectedGeneration, long? ExpectedRevision = null);
public sealed record VideoOverlaySceneListRequest(bool IncludeClosed = false);
public sealed record VideoOverlaySceneSnapshot(VideoOverlaySceneState Scene, IReadOnlyList<VideoOverlayLayerState> Layers);

public interface ITvAirVideoOverlayScenesApi
{
    TvAirOperationResult<VideoOverlaySceneState> Create(CreateVideoOverlaySceneRequest request);
    TvAirOperationResult Close(CloseVideoOverlaySceneRequest request);
    IReadOnlyList<VideoOverlaySceneState> List(bool includeClosed = false);
    TvAirOperationResult<VideoOverlaySceneSnapshot> Get(string sceneInstanceId);
}
public interface ITvAirVideoOverlayElementsApi
{
    TvAirOperationResult Add(AddVideoOverlayElementsRequest request);
    TvAirOperationResult Clear(ClearVideoOverlayLayerRequest request);
}
public interface ITvAirVideoOverlayApi { ITvAirVideoOverlayScenesApi Scenes { get; } ITvAirVideoOverlayElementsApi Elements { get; } }
