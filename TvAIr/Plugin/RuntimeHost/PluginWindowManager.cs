using System.Collections.Concurrent;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Windows;

namespace TvAIr.Plugin.RuntimeHost;

internal sealed class PluginWindowManager : ITvAirPluginWindowsApi, IDisposable
{
    private readonly PluginRuntimeManager _runtime;
    private readonly PluginNativeWindowHost _host;
    private readonly global::TvAIrPlugin.ITvAirWindowsApi _toolWindows;
    private readonly IReadOnlyDictionary<string, PluginWindowDefinition> _definitions;
    private readonly ConcurrentDictionary<string, PluginWindowState> _states = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public PluginWindowManager(PluginRuntimeManager runtime, IReadOnlyList<PluginWindowDefinition> definitions, global::TvAIrPlugin.ITvAirWindowsApi toolWindows)
    {
        _runtime = runtime;
        _toolWindows = toolWindows ?? throw new ArgumentNullException(nameof(toolWindows));
        _host = new PluginNativeWindowHost(OnUserHidden);
        _definitions = definitions
            .GroupBy(x => x.WindowDefinitionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase);
    }

    public TvAirOperationResult<PluginWindowState> Create(CreatePluginWindowRequest request)
    {
        if (!Available()) return Disconnected<PluginWindowState>();
        if (request is null || string.IsNullOrWhiteSpace(request.WindowDefinitionId))
            return TvAirOperationResult<PluginWindowState>.Fail(TvAirErrorCode.InvalidRequest, "Window definition id is required.");
        if (!_definitions.TryGetValue(request.WindowDefinitionId, out var definition))
            return TvAirOperationResult<PluginWindowState>.Fail(TvAirErrorCode.EntityNotFound, "Window definition was not found.");

        var instanceKey = string.IsNullOrWhiteSpace(request.InstanceKey) ? "default" : request.InstanceKey.Trim();
        var existing = _states.Values.FirstOrDefault(x =>
            string.Equals(x.WindowDefinitionId, definition.WindowDefinitionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.InstanceKey, instanceKey, StringComparison.Ordinal));
        if (existing is not null && definition.Multiplicity == PluginWindowMultiplicity.Single)
            return TvAirOperationResult<PluginWindowState>.Ok(existing);

        var state = new PluginWindowState
        {
            WindowDefinitionId = definition.WindowDefinitionId,
            WindowInstanceId = $"{definition.WindowDefinitionId}:{instanceKey}:{Guid.NewGuid():N}",
            InstanceKey = instanceKey,
            Title = string.IsNullOrWhiteSpace(request.TitleOverride) ? definition.Title : request.TitleOverride.Trim(),
            LifecycleState = PluginWindowLifecycleState.Created,
            AttachedSurfaceIds = Array.Empty<string>()
        };
        _states[state.WindowInstanceId] = state;
        if (request.ActivationMode != PluginWindowActivationMode.None)
            Show(state.WindowInstanceId, request.ActivationMode == PluginWindowActivationMode.Activate);
        return TvAirOperationResult<PluginWindowState>.Ok(_states[state.WindowInstanceId]);
    }

    public TvAirOperationResult Show(string windowInstanceId, bool activate = false)
    {
        if (!Available()) return Disconnected();
        if (!_states.TryGetValue(windowInstanceId, out var state)) return Missing();
        var definition = _definitions[state.WindowDefinitionId];
        _host.Show(state, definition, activate);
        _states[windowInstanceId] = state with { LifecycleState = PluginWindowLifecycleState.Shown, IsFocused = activate };
        return TvAirOperationResult.Ok();
    }

    public TvAirOperationResult Hide(string windowInstanceId)
    {
        if (!_states.TryGetValue(windowInstanceId, out var state)) return Missing();
        _host.Hide(windowInstanceId);
        _states[windowInstanceId] = state with { LifecycleState = PluginWindowLifecycleState.Hidden, IsFocused = false };
        return TvAirOperationResult.Ok();
    }

    public TvAirOperationResult Reveal(string windowInstanceId) => Show(windowInstanceId, false);
    public TvAirOperationResult Activate(string windowInstanceId) => Show(windowInstanceId, true);

    public TvAirOperationResult Close(string windowInstanceId)
    {
        if (!_states.TryGetValue(windowInstanceId, out var state)) return Missing();
        if (state.AttachedSurfaceIds.Count != 0)
            return TvAirOperationResult.Fail(TvAirErrorCode.InvalidRequest, "Detach all surfaces before closing the window.");
        _host.Close(windowInstanceId);
        _states.TryRemove(windowInstanceId, out _);
        return TvAirOperationResult.Ok();
    }

    public TvAirOperationResult<PluginWindowState> Get(string windowInstanceId)
        => _states.TryGetValue(windowInstanceId, out var state)
            ? TvAirOperationResult<PluginWindowState>.Ok(state)
            : TvAirOperationResult<PluginWindowState>.Fail(TvAirErrorCode.EntityNotFound, "Window instance was not found.");

    public IReadOnlyList<PluginWindowState> List()
        => _states.Values.OrderBy(x => x.WindowDefinitionId).ThenBy(x => x.InstanceKey).ToArray();

    public TvAirOperationResult RefreshToolWindow(global::TvAIrPlugin.TvAirToolWindowRefreshRequestDto request)
    {
        if (!Available()) return Disconnected();
        return _toolWindows.RefreshToolWindow(request);
    }

    public TvAirOperationResult<global::TvAIrPlugin.TvAirToolWindowStatePatchResultDto> PatchToolWindow(global::TvAIrPlugin.TvAirToolWindowStatePatchRequestDto request)
    {
        if (!Available()) return Disconnected<global::TvAIrPlugin.TvAirToolWindowStatePatchResultDto>();
        return _toolWindows.PatchToolWindow(request);
    }

    public TvAirOperationResult<global::TvAIrPlugin.TvAirToolWindowPlacementPersistenceResultDto> SetToolWindowPlacementPersistence(global::TvAIrPlugin.TvAirToolWindowPlacementPersistenceRequestDto request)
    {
        if (!Available()) return Disconnected<global::TvAIrPlugin.TvAirToolWindowPlacementPersistenceResultDto>();
        return _toolWindows.SetToolWindowPlacementPersistence(request);
    }

    internal bool Exists(string windowInstanceId) => _states.ContainsKey(windowInstanceId);

    internal PluginWindowDefinition? GetDefinitionForInstance(string windowInstanceId)
    {
        if (!_states.TryGetValue(windowInstanceId, out var state)) return null;
        return _definitions.TryGetValue(state.WindowDefinitionId, out var definition) ? definition : null;
    }


    private void OnUserHidden(string windowInstanceId)
    {
        if (_states.TryGetValue(windowInstanceId, out var state))
            _states[windowInstanceId] = state with { LifecycleState = PluginWindowLifecycleState.Hidden, IsFocused = false };
    }

    internal Task InvokeContentAsync(string windowInstanceId, Func<System.Windows.Forms.Control, Task> action)
    {
        if (!_states.TryGetValue(windowInstanceId, out var state))
            throw new InvalidOperationException("Plugin window instance was not found.");
        _host.EnsureCreated(state, _definitions[state.WindowDefinitionId]);
        return _host.InvokeContentAsync(windowInstanceId, action);
    }

    internal TvAirOperationResult AttachSurface(string windowInstanceId, string surfaceInstanceId)
    {
        if (!_states.TryGetValue(windowInstanceId, out var state)) return Missing();
        var ids = state.AttachedSurfaceIds.Append(surfaceInstanceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _states[windowInstanceId] = state with { AttachedSurfaceIds = ids };
        return TvAirOperationResult.Ok();
    }

    internal TvAirOperationResult DetachSurface(string windowInstanceId, string surfaceInstanceId)
    {
        if (!_states.TryGetValue(windowInstanceId, out var state)) return Missing();
        _states[windowInstanceId] = state with
        {
            AttachedSurfaceIds = state.AttachedSurfaceIds
                .Where(x => !string.Equals(x, surfaceInstanceId, StringComparison.OrdinalIgnoreCase))
                .ToArray()
        };
        return TvAirOperationResult.Ok();
    }

    private bool Available() => Volatile.Read(ref _disposed) == 0 && _runtime.IsConnected;
    private static TvAirOperationResult Missing() => TvAirOperationResult.Fail(TvAirErrorCode.EntityNotFound, "Window instance was not found.");
    private static TvAirOperationResult Disconnected() => TvAirOperationResult.Fail(TvAirErrorCode.RuntimeDisconnected, "Plugin runtime is disconnected.");
    private static TvAirOperationResult<T> Disconnected<T>() => TvAirOperationResult<T>.Fail(TvAirErrorCode.RuntimeDisconnected, "Plugin runtime is disconnected.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _host.Dispose();
        _states.Clear();
    }
}
