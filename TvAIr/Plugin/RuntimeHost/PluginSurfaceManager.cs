using System.Collections.Concurrent;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Surfaces;

namespace TvAIr.Plugin.RuntimeHost;

internal sealed class PluginSurfaceManager : ITvAirPluginSurfacesApi, IDisposable
{
    private readonly PluginRuntimeManager _runtime;
    private readonly IReadOnlyDictionary<string, PluginSurfaceDefinition> _definitions;
    private readonly ConcurrentDictionary<string, PluginSurfaceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public PluginSurfaceManager(PluginRuntimeManager runtime, IReadOnlyList<PluginSurfaceDefinition> definitions)
    {
        _runtime = runtime;
        _definitions = definitions
            .GroupBy(x => x.SurfaceDefinitionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase);
    }

    public TvAirOperationResult<PluginSurfaceState> Create(CreatePluginSurfaceRequest request)
    {
        if (!Available(out var unavailable)) return unavailable!;
        if (request is null || string.IsNullOrWhiteSpace(request.SurfaceDefinitionId))
            return TvAirOperationResult<PluginSurfaceState>.Fail(TvAirErrorCode.InvalidRequest, "Surface definition id is required.");
        if (!_definitions.TryGetValue(request.SurfaceDefinitionId, out var definition))
            return TvAirOperationResult<PluginSurfaceState>.Fail(TvAirErrorCode.EntityNotFound, "Surface definition was not found.");

        var instanceKey = string.IsNullOrWhiteSpace(request.InstanceKey) ? "default" : request.InstanceKey.Trim();
        var existing = _states.Values.FirstOrDefault(x =>
            string.Equals(x.SurfaceDefinitionId, definition.SurfaceDefinitionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.InstanceKey, instanceKey, StringComparison.Ordinal));
        if (existing is not null)
            return TvAirOperationResult<PluginSurfaceState>.Ok(existing);

        var id = $"{definition.SurfaceDefinitionId}:{instanceKey}:{Guid.NewGuid():N}";
        var state = new PluginSurfaceState
        {
            SurfaceDefinitionId = definition.SurfaceDefinitionId,
            SurfaceInstanceId = id,
            InstanceKey = instanceKey,
            Kind = definition.Kind,
            LifecycleState = PluginSurfaceLifecycleState.Created,
            AttachedHostSurfaceIds = Array.Empty<string>()
        };
        _states[id] = state;
        return TvAirOperationResult<PluginSurfaceState>.Ok(state);
    }

    public TvAirOperationResult Start(string surfaceInstanceId) => Transition(surfaceInstanceId, PluginSurfaceLifecycleState.Started);
    public TvAirOperationResult Suspend(string surfaceInstanceId) => Transition(surfaceInstanceId, PluginSurfaceLifecycleState.Suspended);
    public TvAirOperationResult Resume(string surfaceInstanceId) => Transition(surfaceInstanceId, PluginSurfaceLifecycleState.Started);

    public TvAirOperationResult Close(string surfaceInstanceId)
    {
        if (!Available(out var unavailable)) return unavailable!;
        if (!_states.TryGetValue(surfaceInstanceId, out var state)) return Missing();
        if (state.AttachedHostSurfaceIds.Count != 0)
            return TvAirOperationResult.Fail(TvAirErrorCode.InvalidRequest, "Detach the surface from all host surfaces before closing it.");
        _states.TryRemove(surfaceInstanceId, out _);
        return TvAirOperationResult.Ok();
    }

    public TvAirOperationResult<PluginSurfaceState> Get(string surfaceInstanceId)
        => _states.TryGetValue(surfaceInstanceId, out var state)
            ? TvAirOperationResult<PluginSurfaceState>.Ok(state)
            : TvAirOperationResult<PluginSurfaceState>.Fail(TvAirErrorCode.EntityNotFound, "Surface instance was not found.");

    public IReadOnlyList<PluginSurfaceState> List()
        => _states.Values.OrderBy(x => x.SurfaceDefinitionId).ThenBy(x => x.InstanceKey).ToArray();

    internal bool TryGetDefinition(string surfaceInstanceId, out PluginSurfaceDefinition definition)
    {
        definition = default!;
        return _states.TryGetValue(surfaceInstanceId, out var state)
            && _definitions.TryGetValue(state.SurfaceDefinitionId, out definition!);
    }

    internal bool Exists(string surfaceInstanceId) => _states.ContainsKey(surfaceInstanceId);

    internal TvAirOperationResult AttachHost(string surfaceInstanceId, string hostSurfaceInstanceId)
    {
        if (!_states.TryGetValue(surfaceInstanceId, out var state)) return Missing();
        var ids = state.AttachedHostSurfaceIds
            .Append(hostSurfaceInstanceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _states[surfaceInstanceId] = state with { AttachedHostSurfaceIds = ids };
        return TvAirOperationResult.Ok();
    }

    internal TvAirOperationResult DetachHost(string surfaceInstanceId, string hostSurfaceInstanceId)
    {
        if (!_states.TryGetValue(surfaceInstanceId, out var state)) return Missing();
        var ids = state.AttachedHostSurfaceIds
            .Where(x => !string.Equals(x, hostSurfaceInstanceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _states[surfaceInstanceId] = state with { AttachedHostSurfaceIds = ids };
        return TvAirOperationResult.Ok();
    }

    private TvAirOperationResult Transition(string id, PluginSurfaceLifecycleState next)
    {
        if (!Available(out var unavailable)) return unavailable!;
        if (!_states.TryGetValue(id, out var state)) return Missing();
        if (state.LifecycleState == PluginSurfaceLifecycleState.Closed)
            return TvAirOperationResult.Fail(TvAirErrorCode.InvalidRequest, "Closed surface cannot transition.");
        _states[id] = state with { LifecycleState = next };
        return TvAirOperationResult.Ok();
    }

    private bool Available(out TvAirOperationResult<PluginSurfaceState>? unavailable)
    {
        unavailable = null;
        if (Volatile.Read(ref _disposed) == 0 && _runtime.IsConnected) return true;
        unavailable = TvAirOperationResult<PluginSurfaceState>.Fail(TvAirErrorCode.RuntimeDisconnected, "Plugin runtime is disconnected.");
        return false;
    }

    private static TvAirOperationResult Missing()
        => TvAirOperationResult.Fail(TvAirErrorCode.EntityNotFound, "Surface instance was not found.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _states.Clear();
    }
}
