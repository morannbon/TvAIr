namespace TvAIr.Plugin;

using TvAIr.Core;
using TvAIrPlugin;

/// <summary>
/// release_contract: 本体管理のプラグイン独立/ToolWindow契約セッション管理。
/// pluginId + routeSegment を reuseKey とし、host close / closeWindow / state persistence を同じ経路で扱う。
/// </summary>
public sealed class PluginWindowSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginWindowSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginWindowSavedState> _savedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly PluginWindowPlacementStore _placementStore;
    private readonly LogRepository _log;

    public PluginWindowSessionStore(PluginWindowPlacementStore placementStore, LogRepository log)
    {
        _placementStore = placementStore;
        _log = log;
    }

    public PluginWindowSession Open(string pluginName, string pluginId, string routeSegment, PluginWindowRequest request)
    {
        var normalizedPluginId = PluginIdentity.Normalize(pluginId);
        var normalizedRoute = NormalizeRoute(routeSegment);
        var reuseKey = BuildReuseKey(normalizedPluginId, normalizedRoute);
        var definitionId = ResolveWindowDefinitionId(request.WindowDefinitionId, normalizedRoute);
        var policy = _placementStore.GetPolicy(normalizedPluginId, definitionId);
        var rememberPlacement = (policy?.RememberPlacement ?? true) && request.StatePersistence != TvAIrPlugin.Windows.PluginWindowStatePersistence.None;
        var placementResolve = rememberPlacement
            ? _placementStore.GetPlacement(normalizedPluginId, definitionId, request.SizeReference)
            : new PluginWindowPlacementResolve(null, "persistence_disabled");
        var savedPlacement = placementResolve.Placement;
        var windowId = NormalizeWindowId(request.WindowId);
        if (string.IsNullOrWhiteSpace(windowId))
            windowId = $"{normalizedPluginId}-{Guid.NewGuid():N}";

        var contentRoute = string.IsNullOrWhiteSpace(request.ContentRoute) ? $"/plugin/{normalizedRoute}" : request.ContentRoute.Trim();
        var session = new PluginWindowSession(
            windowId,
            normalizedPluginId,
            pluginName,
            normalizedRoute,
            string.IsNullOrWhiteSpace(request.Title) ? pluginName : request.Title.Trim(),
            ResolveDimension(request.Width, savedPlacement?.Width, 620),
            ResolveDimension(request.Height, savedPlacement?.Height, 760),
            NormalizeMinimumDimension(request.MinWidth),
            NormalizeMinimumDimension(request.MinHeight),
            request.Resizable,
            request.Movable,
            request.AlwaysOnTop,
            savedPlacement?.Left,
            savedPlacement?.Top,
            contentRoute,
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            1,
            false,
            true,
            false,
            false,
            definitionId,
            rememberPlacement,
            request.ScrollPolicy,
            request.HorizontalScrollPolicy,
            request.VerticalScrollPolicy,
            request.SizeReference,
            savedPlacement?.SizeReference ?? request.SizeReference,
            placementResolve.SavedSizeReference,
            request.ResizeMode,
            request.RefreshMode,
            request.ContentSizePolicy,
            request.PreserveInteractionState,
            request.ReusePolicy,
            request.ActivationPolicy,
            request.CloseBehavior,
            request.BackgroundExecution,
            request.StatePersistence,
            savedPlacement is not null,
            reuseKey);
        lock (_gate)
        {
            _sessions[windowId] = session;
        }
        LogPlacementResolve(session, savedPlacement is not null ? "RESTORED" : "DEFAULT", placementResolve.Source);
        return session;
    }

    public PluginWindowSession OpenOrReuse(string pluginName, string pluginId, string routeSegment, PluginWindowRequest request, bool reuseExisting, out bool reused)
    {
        var normalizedPluginId = PluginIdentity.Normalize(pluginId);
        var normalizedRoute = NormalizeRoute(routeSegment);
        var reuseKey = BuildReuseKey(normalizedPluginId, normalizedRoute);
        var definitionId = ResolveWindowDefinitionId(request.WindowDefinitionId, normalizedRoute);
        var policy = _placementStore.GetPolicy(normalizedPluginId, definitionId);
        var rememberPlacement = (policy?.RememberPlacement ?? true) && request.StatePersistence != TvAIrPlugin.Windows.PluginWindowStatePersistence.None;
        reused = false;

        lock (_gate)
        {
            if (reuseExisting && request.ReusePolicy != TvAIrPlugin.Windows.PluginWindowReusePolicy.Multiple)
            {
                var existing = _sessions.Values
                    .Where(x => string.Equals(x.PluginId, normalizedPluginId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.RouteSegment, normalizedRoute, StringComparison.OrdinalIgnoreCase)
                        && !x.IsClosed)
                    .OrderByDescending(x => x.UpdatedAt)
                    .FirstOrDefault();
                if (existing is not null)
                {
                    var contentRoute = string.IsNullOrWhiteSpace(request.ContentRoute) ? existing.ContentRoute : request.ContentRoute.Trim();
                    var contentChanged = !string.Equals(contentRoute, existing.ContentRoute, StringComparison.Ordinal);
                    var effectiveMinWidth = request.MinWidth > 0 ? NormalizeMinimumDimension(request.MinWidth) : existing.MinWidth;
                    var effectiveMinHeight = request.MinHeight > 0 ? NormalizeMinimumDimension(request.MinHeight) : existing.MinHeight;
                    var referenceChanged = existing.SizeReference != request.SizeReference;
                    var updated = existing with
                    {
                        Title = string.IsNullOrWhiteSpace(request.Title) ? existing.Title : request.Title.Trim(),
                        Width = referenceChanged
                            ? ResolveDimension(request.Width, null, 620)
                            : HasAnyPayload(request, "width", "Width") && request.Width > 0 ? NormalizeRequestedDimension(request.Width) : existing.Width,
                        Height = referenceChanged
                            ? ResolveDimension(request.Height, null, 760)
                            : HasAnyPayload(request, "height", "Height") && request.Height > 0 ? NormalizeRequestedDimension(request.Height) : existing.Height,
                        MinWidth = effectiveMinWidth,
                        MinHeight = effectiveMinHeight,
                        Resizable = HasAnyPayload(request, "resizable", "Resizable") ? request.Resizable : existing.Resizable,
                        Movable = HasAnyPayload(request, "movable", "Movable") ? request.Movable : existing.Movable,
                        AlwaysOnTop = HasAnyPayload(request, "alwaysOnTop", "AlwaysOnTop") ? request.AlwaysOnTop : existing.AlwaysOnTop,
                        ContentRoute = contentRoute,
                        UpdatedAt = DateTimeOffset.Now,
                        // Reopening an existing window only reveals/activates it. Content revision
                        // belongs to an actual route change or the explicit Refresh() contract.
                        Revision = contentChanged ? existing.Revision + 1 : existing.Revision,
                        RefreshRequested = contentChanged,
                        PreserveScroll = contentChanged && request.PreserveScroll,
                        IsClosed = false,
                        HostAlive = existing.HostAlive,
                        WindowDefinitionId = definitionId,
                        RememberPlacement = rememberPlacement,
                        ScrollPolicy = request.ScrollPolicy,
                        HorizontalScrollPolicy = request.HorizontalScrollPolicy,
                        VerticalScrollPolicy = request.VerticalScrollPolicy,
                        SizeReference = request.SizeReference,
                        AppliedSizeReference = referenceChanged || HasAnyPayload(request, "width", "Width", "height", "Height")
                            ? request.SizeReference
                            : existing.AppliedSizeReference,
                        SavedPlacementSizeReference = referenceChanged ? null : existing.SavedPlacementSizeReference,
                        ResizeMode = request.ResizeMode,
                        RefreshMode = request.RefreshMode,
                        ContentSizePolicy = request.ContentSizePolicy,
                        PreserveInteractionState = request.PreserveInteractionState,
                        ReusePolicy = request.ReusePolicy,
                        ActivationPolicy = request.ActivationPolicy,
                        CloseBehavior = request.CloseBehavior,
                        BackgroundExecution = request.BackgroundExecution,
                        StatePersistence = request.StatePersistence,
                        RestoredPlacement = referenceChanged ? false : existing.RestoredPlacement
                    };
                    _sessions[updated.WindowId] = updated;
                    reused = true;
                    return updated;
                }
            }

            var placementResolve = rememberPlacement
                ? _placementStore.GetPlacement(normalizedPluginId, definitionId, request.SizeReference)
                : new PluginWindowPlacementResolve(null, "persistence_disabled");
            var savedPlacement = placementResolve.Placement;
            var windowId = NormalizeWindowId(request.WindowId);
            if (string.IsNullOrWhiteSpace(windowId))
                windowId = $"{normalizedPluginId}-{Guid.NewGuid():N}";
            var session = new PluginWindowSession(
                windowId,
                normalizedPluginId,
                pluginName,
                normalizedRoute,
                string.IsNullOrWhiteSpace(request.Title) ? pluginName : request.Title.Trim(),
                ResolveDimension(request.Width, savedPlacement?.Width, 620),
                ResolveDimension(request.Height, savedPlacement?.Height, 760),
                NormalizeMinimumDimension(request.MinWidth),
                NormalizeMinimumDimension(request.MinHeight),
                request.Resizable,
                request.Movable,
                request.AlwaysOnTop,
                savedPlacement?.Left,
                savedPlacement?.Top,
                string.IsNullOrWhiteSpace(request.ContentRoute) ? $"/plugin/{normalizedRoute}" : request.ContentRoute.Trim(),
                DateTimeOffset.Now,
                DateTimeOffset.Now,
                1,
                false,
                true,
                false,
                false,
                definitionId,
                rememberPlacement,
                request.ScrollPolicy,
                request.HorizontalScrollPolicy,
                request.VerticalScrollPolicy,
                request.SizeReference,
                savedPlacement?.SizeReference ?? request.SizeReference,
                placementResolve.SavedSizeReference,
                request.ResizeMode,
                request.RefreshMode,
                request.ContentSizePolicy,
                request.PreserveInteractionState,
                request.ReusePolicy,
                request.ActivationPolicy,
                request.CloseBehavior,
                request.BackgroundExecution,
                request.StatePersistence,
                savedPlacement is not null,
                reuseKey);
            _sessions[windowId] = session;
            LogPlacementResolve(session, savedPlacement is not null ? "RESTORED" : "DEFAULT", placementResolve.Source);
            return session;
        }
    }

    public PluginWindowSession? Update(string? windowId, string pluginId, PluginWindowRequest request)
    {
        windowId = NormalizeWindowId(windowId);
        pluginId = PluginIdentity.Normalize(pluginId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing)) return null;
            if (!string.Equals(existing.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return null;
            var effectiveMinWidth = request.MinWidth > 0 ? NormalizeMinimumDimension(request.MinWidth) : existing.MinWidth;
            var effectiveMinHeight = request.MinHeight > 0 ? NormalizeMinimumDimension(request.MinHeight) : existing.MinHeight;
            var updated = existing with
            {
                Title = string.IsNullOrWhiteSpace(request.Title) ? existing.Title : request.Title.Trim(),
                Width = HasAnyPayload(request, "width", "Width") && request.Width > 0 ? NormalizeRequestedDimension(request.Width) : existing.Width,
                Height = HasAnyPayload(request, "height", "Height") && request.Height > 0 ? NormalizeRequestedDimension(request.Height) : existing.Height,
                AppliedSizeReference = HasAnyPayload(request, "width", "Width", "height", "Height")
                    ? request.SizeReference
                    : existing.AppliedSizeReference,
                MinWidth = effectiveMinWidth,
                MinHeight = effectiveMinHeight,
                Resizable = HasAnyPayload(request, "resizable", "Resizable") ? request.Resizable : existing.Resizable,
                Movable = HasAnyPayload(request, "movable", "Movable") ? request.Movable : existing.Movable,
                AlwaysOnTop = HasAnyPayload(request, "alwaysOnTop", "AlwaysOnTop") ? request.AlwaysOnTop : existing.AlwaysOnTop,
                ContentRoute = string.IsNullOrWhiteSpace(request.ContentRoute) ? existing.ContentRoute : request.ContentRoute.Trim(),
                UpdatedAt = DateTimeOffset.Now,
                // content revision is owned only by Refresh(). Window placement, size and
                // always-on-top updates must not make the host shell reload plugin HTML.
                Revision = existing.Revision,
                RefreshRequested = false,
                PreserveScroll = false,
                IsClosed = false
            };
            _sessions[windowId] = updated;
            SaveStateNoLock(updated);
            return updated;
        }
    }

    public PluginWindowSession? Refresh(string? windowId, string pluginId, PluginWindowRequest request)
    {
        windowId = NormalizeWindowId(windowId);
        pluginId = PluginIdentity.Normalize(pluginId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing)) return null;
            if (!string.Equals(existing.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return null;
            if (existing.IsClosed) return null;
            var updated = existing with
            {
                ContentRoute = string.IsNullOrWhiteSpace(request.ContentRoute) ? existing.ContentRoute : request.ContentRoute.Trim(),
                UpdatedAt = DateTimeOffset.Now,
                Revision = existing.Revision + 1,
                RefreshRequested = true,
                PreserveScroll = request.PreserveScroll
            };
            _sessions[windowId] = updated;
            return updated;
        }
    }

    public bool Close(string? windowId, string pluginId)
    {
        windowId = NormalizeWindowId(windowId);
        pluginId = PluginIdentity.Normalize(pluginId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing)) return false;
            if (!string.Equals(existing.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return false;
            _sessions.Remove(windowId);
            if (!string.IsNullOrWhiteSpace(existing.ReuseKey)) _savedStates.Remove(existing.ReuseKey);
            return true;
        }
    }

    public void MarkHostAlive(string? windowId, bool hostAlive)
    {
        windowId = NormalizeWindowId(windowId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing)) return;
            _sessions[windowId] = existing with { HostAlive = hostAlive, IsClosed = !hostAlive && existing.IsClosed, UpdatedAt = DateTimeOffset.Now };
        }
    }

    public PluginWindowCloseTransitionResult MarkHostClosing(string? windowId, PluginWindowHostState? state)
    {
        windowId = NormalizeWindowId(windowId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing))
                return PluginWindowCloseTransitionResult.NotFound(windowId);

            var persistState = state is not null && !state.IsMinimized;
            var updated = existing with
            {
                Width = persistState ? ResolvePersistedWidth(existing, state!) : existing.Width,
                Height = persistState ? ResolvePersistedHeight(existing, state!) : existing.Height,
                AppliedSizeReference = persistState ? existing.SizeReference : existing.AppliedSizeReference,
                Left = persistState ? state!.Left : existing.Left,
                Top = persistState ? state!.Top : existing.Top,
                AlwaysOnTop = state?.AlwaysOnTop ?? existing.AlwaysOnTop,
                HostAlive = false,
                IsClosed = true,
                UpdatedAt = DateTimeOffset.Now
            };

            // closeBehavior=Dispose の正規境界。FormClosedを待たず、閉鎖開始時点で
            // reuse対象から除外する。位置保存はSession再利用とは分離して維持する。
            SaveStateNoLock(updated);
            var removed = _sessions.Remove(windowId);
            return new PluginWindowCloseTransitionResult(
                windowId,
                existing.PluginId,
                existing.PluginName,
                existing.WindowDefinitionId,
                existing.RouteSegment,
                existing.CloseBehavior,
                existing.BackgroundExecution,
                SessionDisposed: removed,
                RemovedFromReuseRegistry: removed);
        }
    }

    public void MarkHostClosed(string? windowId, PluginWindowHostState? state)
    {
        windowId = NormalizeWindowId(windowId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing)) return;
            var persistState = state is not null && !state.IsMinimized;
            var updated = existing with
            {
                Width = persistState ? ResolvePersistedWidth(existing, state!) : existing.Width,
                Height = persistState ? ResolvePersistedHeight(existing, state!) : existing.Height,
                AppliedSizeReference = persistState ? existing.SizeReference : existing.AppliedSizeReference,
                Left = persistState ? state!.Left : existing.Left,
                Top = persistState ? state!.Top : existing.Top,
                AlwaysOnTop = state?.AlwaysOnTop ?? existing.AlwaysOnTop,
                HostAlive = false,
                IsClosed = true,
                UpdatedAt = DateTimeOffset.Now
            };
            // 長期連続稼働保護:
            // ホストを×で閉じたセッションは再利用対象ではなく、位置・サイズだけを saved state へ残す。
            // closed session 本体を保持すると、ToolWindow の開閉回数に比例して _sessions が増え続けるため、
            // 保存完了後に同じ正本経路で必ず解放する。
            SaveStateNoLock(updated);
            _sessions.Remove(windowId);
        }
    }

    public void UpdateHostState(string? windowId, PluginWindowHostState? state)
    {
        windowId = NormalizeWindowId(windowId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing) || state is null) return;
            var persistState = !state.IsMinimized;
            var updated = existing with
            {
                Width = persistState ? ResolvePersistedWidth(existing, state) : existing.Width,
                Height = persistState ? ResolvePersistedHeight(existing, state) : existing.Height,
                AppliedSizeReference = persistState ? existing.SizeReference : existing.AppliedSizeReference,
                Left = persistState ? state.Left : existing.Left,
                Top = persistState ? state.Top : existing.Top,
                AlwaysOnTop = state.AlwaysOnTop,
                HostAlive = state.HostAlive,
                UpdatedAt = DateTimeOffset.Now
            };
            _sessions[windowId] = updated;
        }
    }


    private static int ResolvePersistedWidth(PluginWindowSession session, PluginWindowHostState state)
        => NormalizeRequestedDimension(session.SizeReference == TvAIrPlugin.Windows.PluginWindowSizeReference.ContentArea && state.ClientWidth > 0
            ? state.ClientWidth
            : state.Width);

    private static int ResolvePersistedHeight(PluginWindowSession session, PluginWindowHostState state)
        => NormalizeRequestedDimension(session.SizeReference == TvAIrPlugin.Windows.PluginWindowSizeReference.ContentArea && state.ClientHeight > 0
            ? state.ClientHeight
            : state.Height);


    public PluginWindowSession? UpdateContentRouteFromRender(string? windowId, string pluginId, string pathAndQuery)
    {
        windowId = NormalizeWindowId(windowId);
        pluginId = PluginIdentity.Normalize(pluginId);
        if (string.IsNullOrWhiteSpace(pathAndQuery)) return null;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing)) return null;
            if (!string.Equals(existing.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return null;
            if (existing.IsClosed) return null;
            var updated = existing with
            {
                ContentRoute = pathAndQuery,
                UpdatedAt = DateTimeOffset.Now,
                HostAlive = true
            };
            _sessions[windowId] = updated;
            return updated;
        }
    }

    public PluginWindowSession? Get(string? windowId)
    {
        windowId = NormalizeWindowId(windowId);
        lock (_gate)
        {
            return !string.IsNullOrWhiteSpace(windowId) && _sessions.TryGetValue(windowId, out var session) ? session : null;
        }
    }

    public IReadOnlyList<PluginWindowSession> ListByPluginId(string? pluginId)
    {
        pluginId = PluginIdentity.Normalize(pluginId);
        lock (_gate)
        {
            return _sessions.Values
                .Where(s => string.IsNullOrWhiteSpace(pluginId) || string.Equals(s.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.UpdatedAt)
                .ToList();
        }
    }

    public PluginWindowSavedState? GetSavedState(string pluginId, string routeSegment)
    {
        var reuseKey = BuildReuseKey(PluginIdentity.Normalize(pluginId), NormalizeRoute(routeSegment));
        lock (_gate)
        {
            return GetSavedStateNoLock(reuseKey);
        }
    }

    public PluginWindowPlacementPersistenceUpdateResult SetPlacementPersistence(string pluginId, string windowDefinitionId, bool rememberPlacement, bool clearSavedPlacement)
    {
        var normalizedPluginId = PluginIdentity.Normalize(pluginId);
        var normalizedDefinitionId = NormalizeWindowDefinitionId(windowDefinitionId);
        if (string.IsNullOrWhiteSpace(normalizedDefinitionId))
            return new PluginWindowPlacementPersistenceUpdateResult(false, false, false, "invalid_window_definition_id");

        var storeUpdate = _placementStore.SetPolicy(normalizedPluginId, normalizedDefinitionId, rememberPlacement, clearSavedPlacement);
        if (!storeUpdate.Success)
            return new PluginWindowPlacementPersistenceUpdateResult(false, false, false, storeUpdate.FailureReason);

        lock (_gate)
        {
            foreach (var windowId in _sessions.Values
                .Where(x => string.Equals(x.PluginId, normalizedPluginId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.WindowDefinitionId, normalizedDefinitionId, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.WindowId)
                .ToArray())
            {
                var existing = _sessions[windowId];
                _sessions[windowId] = existing with
                {
                    RememberPlacement = rememberPlacement,
                    UpdatedAt = DateTimeOffset.Now
                };
            }
        }

        return new PluginWindowPlacementPersistenceUpdateResult(
            true,
            storeUpdate.RememberPlacementApplied,
            storeUpdate.SavedPlacementCleared,
            string.Empty);
    }

    public PluginWindowPlacementPersistencePolicy? GetPlacementPersistence(string pluginId, string windowDefinitionId)
    {
        return _placementStore.GetPolicy(PluginIdentity.Normalize(pluginId), NormalizeWindowDefinitionId(windowDefinitionId));
    }

    private PluginWindowSavedState? GetSavedStateNoLock(string reuseKey)
        => _savedStates.TryGetValue(reuseKey, out var state) ? state : null;

    private void LogPlacementResolve(PluginWindowSession session, string result, string source)
    {
        _log.Add("PLUGIN_TOOL_WINDOW_PLACEMENT_RESOLVE", session.PluginName,
            $"result={result} pluginId={session.PluginId} windowDefinitionId={session.WindowDefinitionId} rememberPlacement={session.RememberPlacement} source={source} size={session.Width}x{session.Height} descriptorSizeReference={session.SizeReference} savedPlacementSizeReference={session.SavedPlacementSizeReference?.ToString() ?? "-"} appliedSizeReference={session.AppliedSizeReference} rule=plugin_window_placement_persistence_contract");
    }

    private void SaveStateNoLock(PluginWindowSession session)
    {
        if (!session.RememberPlacement || string.IsNullOrWhiteSpace(session.WindowDefinitionId)) return;
        if (!string.IsNullOrWhiteSpace(session.ReuseKey))
            _savedStates[session.ReuseKey] = new PluginWindowSavedState(session.ReuseKey, session.Width, session.Height, session.Left, session.Top, session.AlwaysOnTop, DateTimeOffset.Now);

        var state = new PluginWindowHostState(
            session.WindowId,
            session.HostAlive,
            session.Width,
            session.Height,
            session.Left,
            session.Top,
            session.AlwaysOnTop,
            "host_managed",
            false,
            "Normal",
            false);
        if (!_placementStore.SavePlacement(session.PluginId, session.WindowDefinitionId, state, session.SizeReference, out var failureReason))
            _log.Add("PLUGIN_WINDOW_PLACEMENT_STORE", session.PluginName, $"result=SAVE_FAILED pluginId={session.PluginId} windowDefinitionId={session.WindowDefinitionId} failureReason={failureReason} rule=plugin_window_placement_persistence_contract");
    }


    public PluginWindowSession? DeleteClosed(string? windowId, string pluginId)
    {
        windowId = NormalizeWindowId(windowId);
        pluginId = PluginIdentity.Normalize(pluginId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing)) return null;
            if (!string.Equals(existing.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return null;
            _sessions.Remove(windowId);
            if (!string.IsNullOrWhiteSpace(existing.ReuseKey)) _savedStates.Remove(existing.ReuseKey);
            return existing with { IsClosed = true, HostAlive = false, UpdatedAt = DateTimeOffset.Now };
        }
    }

    private static bool HasPayload(PluginWindowRequest request, params string[] keys)
        => keys.Any(k => request.Payload.ContainsKey(k));

    private static bool HasAnyPayload(PluginWindowRequest request, params string[] keys)
        => HasPayload(request, keys);

    public static string BuildReuseKey(string pluginId, string routeSegment) => $"{PluginIdentity.Normalize(pluginId)}+{NormalizeRoute(routeSegment)}";
    public static string BuildPlacementPolicyKey(string pluginId, string windowDefinitionId) => $"{PluginIdentity.Normalize(pluginId)}+{NormalizeWindowDefinitionId(windowDefinitionId)}";

    private static int ResolveDimension(int requested, int? saved, int fallback)
    {
        var value = saved.GetValueOrDefault(requested > 0 ? requested : fallback);
        return NormalizeRequestedDimension(value);
    }

    private static int NormalizeRequestedDimension(int value) => value > 0 ? value : 1;

    private static int NormalizeMinimumDimension(int value) => value > 0 ? value : 0;

    private static string NormalizeRoute(string? value) => (value ?? string.Empty).Trim().Trim('/');

    private static string ResolveWindowDefinitionId(string? value, string routeSegment)
    {
        var normalized = NormalizeWindowDefinitionId(value);
        return normalized.Length > 0 ? normalized : NormalizeWindowDefinitionId(routeSegment);
    }

    private static string NormalizeWindowDefinitionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
    }

    private static string NormalizeWindowId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
    }
}

public sealed record PluginWindowCloseTransitionResult(
    string WindowId,
    string PluginId,
    string PluginName,
    string WindowDefinitionId,
    string RouteSegment,
    TvAIrPlugin.Windows.PluginWindowCloseBehavior CloseBehavior,
    TvAIrPlugin.Windows.PluginWindowBackgroundExecution BackgroundExecution,
    bool SessionDisposed,
    bool RemovedFromReuseRegistry)
{
    public static PluginWindowCloseTransitionResult NotFound(string? windowId)
        => new(
            windowId ?? string.Empty,
            string.Empty,
            "ToolWindow",
            string.Empty,
            string.Empty,
            TvAIrPlugin.Windows.PluginWindowCloseBehavior.Dispose,
            TvAIrPlugin.Windows.PluginWindowBackgroundExecution.StopWithWindow,
            false,
            false);
}

public sealed record PluginWindowSession(
    string WindowId,
    string PluginId,
    string PluginName,
    string RouteSegment,
    string Title,
    int Width,
    int Height,
    int MinWidth,
    int MinHeight,
    bool Resizable,
    bool Movable,
    bool AlwaysOnTop,
    int? Left,
    int? Top,
    string ContentRoute,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision,
    bool RefreshRequested,
    bool PreserveScroll,
    bool IsClosed,
    bool HostAlive,
    string WindowDefinitionId,
    bool RememberPlacement,
    TvAIrPlugin.Windows.PluginWindowScrollPolicy ScrollPolicy,
    TvAIrPlugin.Windows.PluginWindowAxisScrollPolicy HorizontalScrollPolicy,
    TvAIrPlugin.Windows.PluginWindowAxisScrollPolicy VerticalScrollPolicy,
    TvAIrPlugin.Windows.PluginWindowSizeReference SizeReference,
    TvAIrPlugin.Windows.PluginWindowSizeReference AppliedSizeReference,
    TvAIrPlugin.Windows.PluginWindowSizeReference? SavedPlacementSizeReference,
    TvAIrPlugin.Windows.PluginWindowResizeMode ResizeMode,
    TvAIrPlugin.Windows.PluginWindowRefreshMode RefreshMode,
    TvAIrPlugin.Windows.PluginWindowContentSizePolicy ContentSizePolicy,
    bool PreserveInteractionState,
    TvAIrPlugin.Windows.PluginWindowReusePolicy ReusePolicy,
    TvAIrPlugin.Windows.PluginWindowActivationPolicy ActivationPolicy,
    TvAIrPlugin.Windows.PluginWindowCloseBehavior CloseBehavior,
    TvAIrPlugin.Windows.PluginWindowBackgroundExecution BackgroundExecution,
    TvAIrPlugin.Windows.PluginWindowStatePersistence StatePersistence,
    bool RestoredPlacement,
    string ReuseKey);

public sealed record PluginWindowSavedState(
    string ReuseKey,
    int Width,
    int Height,
    int? Left,
    int? Top,
    bool AlwaysOnTop,
    DateTimeOffset UpdatedAt);

public sealed record PluginWindowHostState(
    string WindowId,
    bool HostAlive,
    int Width,
    int Height,
    int? Left,
    int? Top,
    bool AlwaysOnTop,
    string HostKind,
    bool WebView2RuntimeAvailable,
    string WindowState,
    bool IsMinimized,
    int ClientWidth = 0,
    int ClientHeight = 0);

public sealed record PluginWindowPlacementPersistencePolicy(
    string PluginId,
    string WindowDefinitionId,
    bool RememberPlacement,
    DateTimeOffset UpdatedAt);

public sealed record PluginWindowPlacementPersistenceUpdateResult(
    bool Success,
    bool RememberPlacementApplied,
    bool SavedPlacementCleared,
    string FailureReason);
