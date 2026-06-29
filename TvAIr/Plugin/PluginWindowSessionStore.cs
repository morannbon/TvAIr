namespace TvAIr.Plugin;

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

    public PluginWindowSession Open(string pluginName, string pluginId, string routeSegment, PluginWindowRequest request)
    {
        var normalizedPluginId = NormalizeWindowId(pluginId);
        var normalizedRoute = NormalizeRoute(routeSegment);
        var reuseKey = BuildReuseKey(normalizedPluginId, normalizedRoute);
        var saved = GetSavedStateNoLock(reuseKey);
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
            ResolveDimension(request.Width, saved?.Width, 620, Math.Clamp(request.MinWidth, 160, 2400), 2400),
            ResolveDimension(request.Height, saved?.Height, 760, Math.Clamp(request.MinHeight, 160, 1600), 1600),
            Math.Clamp(request.MinWidth, 160, 2400),
            Math.Clamp(request.MinHeight, 160, 1600),
            request.Resizable,
            request.Movable,
            saved?.AlwaysOnTop ?? request.AlwaysOnTop,
            saved?.Left,
            saved?.Top,
            contentRoute,
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            1,
            false,
            true,
            false,
            false,
            reuseKey);
        lock (_gate)
        {
            _sessions[windowId] = session;
        }
        return session;
    }

    public PluginWindowSession OpenOrReuse(string pluginName, string pluginId, string routeSegment, PluginWindowRequest request, bool reuseExisting, out bool reused)
    {
        var normalizedPluginId = NormalizeWindowId(pluginId);
        var normalizedRoute = NormalizeRoute(routeSegment);
        var reuseKey = BuildReuseKey(normalizedPluginId, normalizedRoute);
        reused = false;

        lock (_gate)
        {
            if (reuseExisting)
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
                    var effectiveMinWidth = request.MinWidth > 0 ? Math.Max(existing.MinWidth, Math.Clamp(request.MinWidth, 160, 2400)) : existing.MinWidth;
                    var effectiveMinHeight = request.MinHeight > 0 ? Math.Max(existing.MinHeight, Math.Clamp(request.MinHeight, 160, 1600)) : existing.MinHeight;
                    var updated = existing with
                    {
                        Title = string.IsNullOrWhiteSpace(request.Title) ? existing.Title : request.Title.Trim(),
                        Width = Math.Max(effectiveMinWidth, HasAnyPayload(request, "width", "Width") && request.Width > 0 ? Math.Clamp(request.Width, 240, 2400) : existing.Width),
                        Height = Math.Max(effectiveMinHeight, HasAnyPayload(request, "height", "Height") && request.Height > 0 ? Math.Clamp(request.Height, 240, 1600) : existing.Height),
                        MinWidth = effectiveMinWidth,
                        MinHeight = effectiveMinHeight,
                        Resizable = HasAnyPayload(request, "resizable", "Resizable") ? request.Resizable : existing.Resizable,
                        Movable = HasAnyPayload(request, "movable", "Movable") ? request.Movable : existing.Movable,
                        AlwaysOnTop = HasAnyPayload(request, "alwaysOnTop", "AlwaysOnTop") ? request.AlwaysOnTop : existing.AlwaysOnTop,
                        ContentRoute = contentRoute,
                        UpdatedAt = DateTimeOffset.Now,
                        Revision = existing.Revision + 1,
                        RefreshRequested = true,
                        PreserveScroll = request.PreserveScroll,
                        RefreshScrollTarget = NormalizeScrollTarget(request.RefreshScrollTarget),
                        RefreshScrollMode = NormalizeScrollMode(request.RefreshScrollMode),
                        IsClosed = false,
                        HostAlive = existing.HostAlive
                    };
                    _sessions[updated.WindowId] = updated;
                    reused = true;
                    return updated;
                }
            }

            var saved = GetSavedStateNoLock(reuseKey);
            var windowId = NormalizeWindowId(request.WindowId);
            if (string.IsNullOrWhiteSpace(windowId))
                windowId = $"{normalizedPluginId}-{Guid.NewGuid():N}";
            var session = new PluginWindowSession(
                windowId,
                normalizedPluginId,
                pluginName,
                normalizedRoute,
                string.IsNullOrWhiteSpace(request.Title) ? pluginName : request.Title.Trim(),
                ResolveDimension(request.Width, saved?.Width, 620, Math.Clamp(request.MinWidth, 160, 2400), 2400),
                ResolveDimension(request.Height, saved?.Height, 760, Math.Clamp(request.MinHeight, 160, 1600), 1600),
                Math.Clamp(request.MinWidth, 160, 2400),
                Math.Clamp(request.MinHeight, 160, 1600),
                request.Resizable,
                request.Movable,
                saved?.AlwaysOnTop ?? request.AlwaysOnTop,
                saved?.Left,
                saved?.Top,
                string.IsNullOrWhiteSpace(request.ContentRoute) ? $"/plugin/{normalizedRoute}" : request.ContentRoute.Trim(),
                DateTimeOffset.Now,
                DateTimeOffset.Now,
                1,
                false,
                true,
                false,
                false,
                reuseKey);
            _sessions[windowId] = session;
            return session;
        }
    }

    public PluginWindowSession? Update(string? windowId, string pluginId, PluginWindowRequest request)
    {
        windowId = NormalizeWindowId(windowId);
        pluginId = NormalizeWindowId(pluginId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing)) return null;
            if (!string.Equals(existing.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return null;
            var effectiveMinWidth = request.MinWidth > 0 ? Math.Max(existing.MinWidth, Math.Clamp(request.MinWidth, 160, 2400)) : existing.MinWidth;
            var effectiveMinHeight = request.MinHeight > 0 ? Math.Max(existing.MinHeight, Math.Clamp(request.MinHeight, 160, 1600)) : existing.MinHeight;
            var updated = existing with
            {
                Title = string.IsNullOrWhiteSpace(request.Title) ? existing.Title : request.Title.Trim(),
                Width = Math.Max(effectiveMinWidth, HasAnyPayload(request, "width", "Width") && request.Width > 0 ? Math.Clamp(request.Width, 240, 2400) : existing.Width),
                Height = Math.Max(effectiveMinHeight, HasAnyPayload(request, "height", "Height") && request.Height > 0 ? Math.Clamp(request.Height, 240, 1600) : existing.Height),
                MinWidth = effectiveMinWidth,
                MinHeight = effectiveMinHeight,
                Resizable = HasAnyPayload(request, "resizable", "Resizable") ? request.Resizable : existing.Resizable,
                Movable = HasAnyPayload(request, "movable", "Movable") ? request.Movable : existing.Movable,
                AlwaysOnTop = HasAnyPayload(request, "alwaysOnTop", "AlwaysOnTop") ? request.AlwaysOnTop : existing.AlwaysOnTop,
                ContentRoute = string.IsNullOrWhiteSpace(request.ContentRoute) ? existing.ContentRoute : request.ContentRoute.Trim(),
                UpdatedAt = DateTimeOffset.Now,
                Revision = existing.Revision + 1,
                RefreshRequested = existing.RefreshRequested,
                PreserveScroll = existing.PreserveScroll,
                RefreshScrollTarget = existing.RefreshScrollTarget,
                RefreshScrollMode = existing.RefreshScrollMode,
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
        pluginId = NormalizeWindowId(pluginId);
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
                PreserveScroll = request.PreserveScroll,
                RefreshScrollTarget = NormalizeScrollTarget(request.RefreshScrollTarget),
                RefreshScrollMode = NormalizeScrollMode(request.RefreshScrollMode)
            };
            _sessions[windowId] = updated;
            return updated;
        }
    }

    public bool Close(string? windowId, string pluginId)
    {
        windowId = NormalizeWindowId(windowId);
        pluginId = NormalizeWindowId(pluginId);
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

    public void MarkHostClosed(string? windowId, PluginWindowHostState? state)
    {
        windowId = NormalizeWindowId(windowId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(windowId) || !_sessions.TryGetValue(windowId, out var existing)) return;
            var persistState = state is not null && !state.IsMinimized;
            var updated = existing with
            {
                Width = persistState && state!.Width > 0 ? Math.Max(existing.MinWidth, Math.Clamp(state.Width, 240, 2400)) : existing.Width,
                Height = persistState && state!.Height > 0 ? Math.Max(existing.MinHeight, Math.Clamp(state.Height, 240, 1600)) : existing.Height,
                Left = persistState ? state!.Left : existing.Left,
                Top = persistState ? state!.Top : existing.Top,
                AlwaysOnTop = state?.AlwaysOnTop ?? existing.AlwaysOnTop,
                HostAlive = false,
                IsClosed = true,
                UpdatedAt = DateTimeOffset.Now
            };
            _sessions[windowId] = updated;
            SaveStateNoLock(updated);
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
                Width = persistState && state.Width > 0 ? Math.Max(existing.MinWidth, Math.Clamp(state.Width, 240, 2400)) : existing.Width,
                Height = persistState && state.Height > 0 ? Math.Max(existing.MinHeight, Math.Clamp(state.Height, 240, 1600)) : existing.Height,
                Left = persistState ? state.Left : existing.Left,
                Top = persistState ? state.Top : existing.Top,
                AlwaysOnTop = state.AlwaysOnTop,
                HostAlive = state.HostAlive,
                UpdatedAt = DateTimeOffset.Now
            };
            _sessions[windowId] = updated;
            if (persistState) SaveStateNoLock(updated);
        }
    }


    public PluginWindowSession? UpdateContentRouteFromRender(string? windowId, string pluginId, string pathAndQuery)
    {
        windowId = NormalizeWindowId(windowId);
        pluginId = NormalizeWindowId(pluginId);
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

    public PluginWindowSavedState? GetSavedState(string pluginId, string routeSegment)
    {
        var reuseKey = BuildReuseKey(NormalizeWindowId(pluginId), NormalizeRoute(routeSegment));
        lock (_gate)
        {
            return GetSavedStateNoLock(reuseKey);
        }
    }

    private PluginWindowSavedState? GetSavedStateNoLock(string reuseKey)
        => _savedStates.TryGetValue(reuseKey, out var state) ? state : null;

    private void SaveStateNoLock(PluginWindowSession session)
    {
        if (string.IsNullOrWhiteSpace(session.ReuseKey)) return;
        _savedStates[session.ReuseKey] = new PluginWindowSavedState(session.ReuseKey, session.Width, session.Height, session.Left, session.Top, session.AlwaysOnTop, DateTimeOffset.Now);
    }


    public PluginWindowSession? DeleteClosed(string? windowId, string pluginId)
    {
        windowId = NormalizeWindowId(windowId);
        pluginId = NormalizeWindowId(pluginId);
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

    public static string BuildReuseKey(string pluginId, string routeSegment) => $"{NormalizeWindowId(pluginId)}+{NormalizeRoute(routeSegment)}";

    private static int ResolveDimension(int requested, int? saved, int fallback, int min, int max)
    {
        var value = saved.GetValueOrDefault(requested > 0 ? requested : fallback);
        return Math.Clamp(value, min, max);
    }

    private static string NormalizeScrollTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim().TrimStart('#');
        return new string(trimmed.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.').ToArray());
    }

    private static string NormalizeScrollMode(string? value)
    {
        var mode = (value ?? string.Empty).Trim().ToLowerInvariant();
        return mode is "top" or "nearest" or "center" ? mode : "center";
    }

    private static string NormalizeRoute(string? value) => (value ?? string.Empty).Trim().Trim('/');

    private static string NormalizeWindowId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
    }
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
    string ReuseKey,
    string RefreshScrollTarget = "",
    string RefreshScrollMode = "center");

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
    bool IsMinimized);
