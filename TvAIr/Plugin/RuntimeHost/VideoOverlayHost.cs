using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TvAIrPlugin;
using TvAIrPlugin.Overlay;
using TvAIrPlugin.Runtime;

namespace TvAIr.Plugin.RuntimeHost;

internal sealed class VideoOverlayHost : ITvAirVideoOverlayApi, ITvAirVideoOverlayScenesApi, ITvAirVideoOverlayElementsApi, IDisposable
{
    private sealed class SceneEntry
    {
        public required VideoOverlaySceneState State { get; set; }
        public Dictionary<string, VideoOverlayLayerState> Layers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, DateTimeOffset>> ExpirationsByLayer { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset? ClosedAtUtc { get; set; }
    }

    private const int ClosedSceneRetentionLimit = 128;
    private readonly string _pluginId;
    private readonly Func<string, VideoOverlayViewerTarget?> _resolveViewerTarget;
    private readonly GenericVideoOverlayRenderer _renderer;
    private readonly TvAIr.Core.LogRepository _log;
    private readonly HashSet<PluginPermission> _permissions;
    private readonly ConcurrentDictionary<string, SceneEntry> _scenes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public VideoOverlayHost(string pluginId, Func<string, VideoOverlayViewerTarget?> resolveViewerTarget, TvAIr.Core.LogRepository log, IEnumerable<PluginPermission> permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _pluginId = pluginId;
        _resolveViewerTarget = resolveViewerTarget ?? throw new ArgumentNullException(nameof(resolveViewerTarget));
        _renderer = new GenericVideoOverlayRenderer();
        _log = log;
        _permissions = new HashSet<PluginPermission>(permissions ?? Array.Empty<PluginPermission>());
    }

    public ITvAirVideoOverlayScenesApi Scenes => this;
    public ITvAirVideoOverlayElementsApi Elements => this;

    public TvAirOperationResult<VideoOverlaySceneState> Create(CreateVideoOverlaySceneRequest request)
    {
        var permission = RequireWrite<VideoOverlaySceneState>();
        if (permission is not null) return permission;
        if (request is null || string.IsNullOrWhiteSpace(request.SceneDefinitionId) || string.IsNullOrWhiteSpace(request.ViewerSessionId) || request.ExpectedGeneration < 0)
            return TvAirOperationResult<VideoOverlaySceneState>.Fail(TvAirErrorCode.InvalidRequest, "Scene definition, viewer session, and non-negative generation are required.");
        var definition = request.SceneDefinitionId.Trim();
        var viewer = request.ViewerSessionId.Trim();
        var target = _resolveViewerTarget(viewer);
        if (target is null || target.Generation != request.ExpectedGeneration)
            return TvAirOperationResult<VideoOverlaySceneState>.Fail(TvAirErrorCode.StaleViewerGeneration, "Viewer generation is not active.");
        var key = string.IsNullOrWhiteSpace(request.InstanceKey) ? "default" : request.InstanceKey.Trim();
        var id = StableId(_pluginId, definition, viewer, key);
        lock (_gate)
        {
            if (_scenes.TryGetValue(id, out var existing) && !existing.State.IsClosed)
            {
                if (existing.State.Generation != request.ExpectedGeneration)
                    return TvAirOperationResult<VideoOverlaySceneState>.Fail(TvAirErrorCode.StaleViewerGeneration, "Viewer generation does not match the existing overlay scene.");
                existing.State = existing.State with { Attached = true };
                _renderer.Update(target, existing.State, existing.Layers.Values.ToArray());
                return TvAirOperationResult<VideoOverlaySceneState>.Ok(existing.State);
            }

            // A closed scene is a completed lifecycle, not a permanent reservation of InstanceKey.
            // Recreating the same definition/viewer/key starts a fresh scene with the stable instance id.
            var state = new VideoOverlaySceneState(id, definition, viewer, request.ExpectedGeneration,
                true, 1, false, 0);
            var created = new SceneEntry { State = state, ClosedAtUtc = null };
            _scenes[id] = created;
            _renderer.Update(target, state, Array.Empty<VideoOverlayLayerState>());
            Log(existing is null ? "CREATED" : "RECREATED", state);
            return TvAirOperationResult<VideoOverlaySceneState>.Ok(state);
        }
    }


    public TvAirOperationResult Add(AddVideoOverlayElementsRequest request)
    {
        var permission = RequireWrite();
        if (permission is not null) return permission;
        if (request is null || string.IsNullOrWhiteSpace(request.SceneInstanceId) || string.IsNullOrWhiteSpace(request.LayerId) || request.Elements is null)
            return TvAirOperationResult.Fail(TvAirErrorCode.InvalidRequest, "Scene, layer, and elements are required.");
        if (request.Elements.Count > 1000 || request.Elements.Any(x => x is null || string.IsNullOrWhiteSpace(x.ElementId)))
            return TvAirOperationResult.Fail(TvAirErrorCode.PayloadTooLarge, "Overlay layer accepts at most 1000 identified elements.");
        lock (_gate)
        {
            var check = Validate(request.SceneInstanceId, request.ExpectedGeneration, request.ExpectedRevision, out var entry);
            if (check is not null) return check;
            var layerId = request.LayerId.Trim();
            var now = DateTimeOffset.UtcNow;
            var current = entry!.Layers.TryGetValue(layerId, out var layer)
                ? PruneExpiredElements(entry, layer, now)
                : new VideoOverlayLayerState(layerId, 0, Array.Empty<VideoOverlayElement>());

            var mergedById = new Dictionary<string, VideoOverlayElement>(StringComparer.Ordinal);
            foreach (var element in current.Elements)
                mergedById[element.ElementId] = element;

            var expirations = GetOrCreateLayerExpirations(entry, layerId);
            foreach (var element in request.Elements)
            {
                mergedById[element.ElementId] = element;
                if (element is VideoOverlayTextElement { Duration: { } duration } && duration > TimeSpan.Zero)
                    expirations[element.ElementId] = now + duration;
                else
                    expirations.Remove(element.ElementId);
            }

            entry.Layers[layerId] = new VideoOverlayLayerState(layerId, current.Revision + 1, mergedById.Values.ToArray());
            var target = _resolveViewerTarget(entry.State.ViewerSessionId);
            entry.State = entry.State with { Revision = entry.State.Revision + 1, LayerCount = entry.Layers.Count, Attached = target is not null && target.Generation == entry.State.Generation };
            if (target is not null && target.Generation == entry.State.Generation) _renderer.Update(target, entry.State, entry.Layers.Values.ToArray());
            Log("ELEMENTS_ADDED", entry.State, $"layerId={layerId} elements={request.Elements.Count}");
            return TvAirOperationResult.Ok();
        }
    }


    public TvAirOperationResult Clear(ClearVideoOverlayLayerRequest request)
    {
        var permission = RequireWrite();
        if (permission is not null) return permission;
        if (request is null || string.IsNullOrWhiteSpace(request.SceneInstanceId) || string.IsNullOrWhiteSpace(request.LayerId))
            return TvAirOperationResult.Fail(TvAirErrorCode.InvalidRequest, "Scene and layer are required.");
        lock (_gate)
        {
            var check = Validate(request.SceneInstanceId, request.ExpectedGeneration, request.ExpectedRevision, out var entry);
            if (check is not null) return check;
            var layerId = request.LayerId?.Trim() ?? string.Empty;
            entry!.Layers.Remove(layerId);
            entry.ExpirationsByLayer.Remove(layerId);
            var target = _resolveViewerTarget(entry.State.ViewerSessionId);
            entry.State = entry.State with { Revision = entry.State.Revision + 1, LayerCount = entry.Layers.Count, Attached = target is not null && target.Generation == entry.State.Generation };
            if (target is not null && target.Generation == entry.State.Generation) _renderer.Update(target, entry.State, entry.Layers.Values.ToArray());
            Log("LAYER_CLEARED", entry.State, $"layerId={request.LayerId}");
            return TvAirOperationResult.Ok();
        }
    }

    public TvAirOperationResult Close(CloseVideoOverlaySceneRequest request)
    {
        var permission = RequireWrite();
        if (permission is not null) return permission;
        if (request is null || string.IsNullOrWhiteSpace(request.SceneInstanceId))
            return TvAirOperationResult.Fail(TvAirErrorCode.InvalidRequest, "Scene is required.");
        lock (_gate)
        {
            // Close releases an existing Host-owned resource. Unlike Create/Add/Clear, it must not
            // require the viewer generation to still be active: a Plugin learns about a generation
            // advance only after ViewerSessionChanged is delivered, and must then be able to close
            // the scene it created for the previous generation. The scene instance itself remains
            // Plugin-scoped by this VideoOverlayHost, while ExpectedGeneration/Revision protect
            // against accidentally closing a different lifecycle that reused the stable instance id.
            var check = ValidateClose(request.SceneInstanceId, request.ExpectedGeneration, request.ExpectedRevision, out var entry);
            if (check is not null) return check;
            return CloseCore(entry!);
        }
    }

    private TvAirOperationResult CloseCore(SceneEntry entry)
    {
        entry.Layers.Clear();
        entry.ExpirationsByLayer.Clear();
        entry.State = entry.State with { Revision = entry.State.Revision + 1, IsClosed = true, Attached = false, LayerCount = 0 };
        entry.ClosedAtUtc = DateTimeOffset.UtcNow;
        _renderer.Close(entry.State.SceneInstanceId);
        PruneClosedScenesLocked();
        Log("CLOSED", entry.State);
        return TvAirOperationResult.Ok();
    }

    private void PruneClosedScenesLocked()
    {
        var overflow = _scenes.Values
            .Where(x => x.State.IsClosed)
            .OrderByDescending(x => x.ClosedAtUtc ?? DateTimeOffset.MinValue)
            .Skip(ClosedSceneRetentionLimit)
            .Select(x => x.State.SceneInstanceId)
            .ToArray();
        foreach (var id in overflow)
            _scenes.TryRemove(id, out _);
    }

    public IReadOnlyList<VideoOverlaySceneState> List(bool includeClosed = false)
    {
        if (!CanRead()) return Array.Empty<VideoOverlaySceneState>();
        lock (_gate)
            return _scenes.Values.Select(x => Refresh(x)).Where(x => includeClosed || !x.IsClosed).OrderBy(x => x.SceneDefinitionId, StringComparer.Ordinal).ThenBy(x => x.SceneInstanceId, StringComparer.Ordinal).ToArray();
    }

    public TvAirOperationResult<VideoOverlaySceneSnapshot> Get(string sceneInstanceId)
    {
        if (!CanRead())
            return TvAirOperationResult<VideoOverlaySceneSnapshot>.Fail(TvAirErrorCode.PermissionDenied, "ReadVideoOverlay permission is required.");
        lock (_gate)
        {
            if (!_scenes.TryGetValue(sceneInstanceId ?? string.Empty, out var entry))
                return TvAirOperationResult<VideoOverlaySceneSnapshot>.Fail(TvAirErrorCode.EntityNotFound, "Overlay scene was not found.");
            var state = Refresh(entry);
            return TvAirOperationResult<VideoOverlaySceneSnapshot>.Ok(new VideoOverlaySceneSnapshot(state, entry.Layers.Values.OrderBy(x => x.LayerId, StringComparer.Ordinal).ToArray()));
        }
    }


    private static Dictionary<string, DateTimeOffset> GetOrCreateLayerExpirations(SceneEntry entry, string layerId)
    {
        if (!entry.ExpirationsByLayer.TryGetValue(layerId, out var expirations))
            entry.ExpirationsByLayer[layerId] = expirations = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        return expirations;
    }

    private static VideoOverlayLayerState PruneExpiredElements(SceneEntry entry, VideoOverlayLayerState layer, DateTimeOffset now)
    {
        if (!entry.ExpirationsByLayer.TryGetValue(layer.LayerId, out var expirations) || expirations.Count == 0)
            return layer;

        var expiredIds = expirations
            .Where(x => x.Value <= now)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (expiredIds.Count == 0)
            return layer;

        foreach (var elementId in expiredIds)
            expirations.Remove(elementId);
        if (expirations.Count == 0)
            entry.ExpirationsByLayer.Remove(layer.LayerId);

        var retained = layer.Elements.Where(x => !expiredIds.Contains(x.ElementId)).ToArray();
        return new VideoOverlayLayerState(layer.LayerId, layer.Revision, retained);
    }

    private bool CanRead() => _permissions.Contains(PluginPermission.ReadVideoOverlay) || _permissions.Contains(PluginPermission.WriteVideoOverlay);

    private TvAirOperationResult? RequireWrite()
        => _permissions.Contains(PluginPermission.WriteVideoOverlay)
            ? null
            : TvAirOperationResult.Fail(TvAirErrorCode.PermissionDenied, "WriteVideoOverlay permission is required.");

    private TvAirOperationResult<T>? RequireWrite<T>()
        => _permissions.Contains(PluginPermission.WriteVideoOverlay)
            ? null
            : TvAirOperationResult<T>.Fail(TvAirErrorCode.PermissionDenied, "WriteVideoOverlay permission is required.");

    private TvAirOperationResult? Validate(string id, long generation, long? revision, out SceneEntry? entry)
    {
        entry = null;
        if (!_scenes.TryGetValue(id ?? string.Empty, out entry) || entry.State.IsClosed)
            return TvAirOperationResult.Fail(TvAirErrorCode.EntityNotFound, "Overlay scene was not found.");
        if (entry.State.Generation != generation)
            return TvAirOperationResult.Fail(TvAirErrorCode.StaleViewerGeneration, "Viewer generation is stale.");
        var target = _resolveViewerTarget(entry.State.ViewerSessionId);
        if (target is null || target.Generation != generation)
            return TvAirOperationResult.Fail(TvAirErrorCode.StaleViewerGeneration, "Viewer generation is not active.");
        if (revision.HasValue && entry.State.Revision != revision.Value)
            return TvAirOperationResult.Fail(TvAirErrorCode.RevisionConflict, "Overlay scene revision does not match.");
        return null;
    }

    private TvAirOperationResult? ValidateClose(string id, long generation, long? revision, out SceneEntry? entry)
    {
        entry = null;
        if (!_scenes.TryGetValue(id ?? string.Empty, out entry) || entry.State.IsClosed)
            return TvAirOperationResult.Fail(TvAirErrorCode.EntityNotFound, "Overlay scene was not found.");
        if (entry.State.Generation != generation)
            return TvAirOperationResult.Fail(TvAirErrorCode.StaleViewerGeneration, "Viewer generation does not match the overlay scene being closed.");
        if (revision.HasValue && entry.State.Revision != revision.Value)
            return TvAirOperationResult.Fail(TvAirErrorCode.RevisionConflict, "Overlay scene revision does not match.");
        return null;
    }

    private VideoOverlaySceneState Refresh(SceneEntry entry)
    {
        if (!entry.State.IsClosed)
            entry.State = entry.State with { Attached = _resolveViewerTarget(entry.State.ViewerSessionId) is { } target && target.Generation == entry.State.Generation, LayerCount = entry.Layers.Count };
        return entry.State;
    }

    private void Log(string result, VideoOverlaySceneState state, string extra = "")
        => _log.Add("PLUGIN_VIDEO_OVERLAY_HOST", _pluginId, $"result={result} sceneInstanceId={state.SceneInstanceId} definitionId={state.SceneDefinitionId} viewerSessionId={state.ViewerSessionId} generation={state.Generation} revision={state.Revision} attached={state.Attached} closed={state.IsClosed} layers={state.LayerCount} {extra} render=host_owned pointer=passthrough sound=none rule=plugin_video_overlay_host_contract".Trim());

    public void Dispose()
    {
        foreach (var scene in _scenes.Values)
            _renderer.Close(scene.State.SceneInstanceId);
        _renderer.Dispose();
        _scenes.Clear();
    }

    private static string StableId(params string[] parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", parts)))).ToLowerInvariant();
}
