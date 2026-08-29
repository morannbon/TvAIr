using System.Collections.Concurrent;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Surfaces;

namespace TvAIr.Plugin.RuntimeHost;

internal sealed class HostSurfaceRegistry : ITvAirHostSurfacesApi, IDisposable
{
    private static readonly IReadOnlyDictionary<HostSurfaceKind, string[]> CapabilityMap =
        new Dictionary<HostSurfaceKind, string[]>
        {
            [HostSurfaceKind.WindowContent] = ["Surface.Attach", "Surface.Resize", "Surface.Dpi", "Surface.Focus", "WebRuntime.ModernJavaScript", "WebRuntime.EsModules", "WebRuntime.Dom", "WebRuntime.Canvas", "WebRuntime.Svg", "WebRuntime.Async", "WebRuntime.Timer", "WebRuntime.Router", "WebRuntime.AssetOrigin"],
            [HostSurfaceKind.ApplicationPage] = ["Surface.Attach", "Surface.Resize", "Surface.Dpi", "Surface.Focus"],
            [HostSurfaceKind.ApplicationPanel] = ["Surface.Attach", "Surface.Resize", "Surface.Dpi", "Surface.Focus"],
            [HostSurfaceKind.ViewerOverlay] = ["Surface.Attach", "Overlay.Alpha", "Overlay.ZOrder", "Overlay.Animation", "Overlay.PointerPassthrough", "Overlay.DpiTracking", "Overlay.MonitorTracking"],
            [HostSurfaceKind.ViewerCompanion] = ["Surface.Attach", "Surface.Resize", "Surface.Dpi", "Surface.Focus"],
            [HostSurfaceKind.Background] = ["Surface.Attach", "Runtime.Background", "Bridge.Rpc", "Bridge.Events"]
        };

    private readonly PluginRuntimeManager _runtime;
    private readonly PluginSurfaceManager _surfaces;
    private readonly PluginWindowManager _windows;
    private readonly Func<string, VideoOverlayViewerTarget?> _resolveViewerTarget;
    private readonly ConcurrentDictionary<string, HostSurfaceState> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PluginSurfaceAttachment> _attachments = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public HostSurfaceRegistry(PluginRuntimeManager runtime, PluginSurfaceManager surfaces, PluginWindowManager windows, Func<string, VideoOverlayViewerTarget?> resolveViewerTarget)
    {
        _runtime = runtime;
        _surfaces = surfaces;
        _windows = windows;
        _resolveViewerTarget = resolveViewerTarget ?? throw new ArgumentNullException(nameof(resolveViewerTarget));
    }

    public TvAirOperationResult<HostSurfaceState> Resolve(HostSurfaceKind kind, string ownerInstanceId, string slotId = "main")
    {
        if (!Available()) return TvAirOperationResult<HostSurfaceState>.Fail(TvAirErrorCode.RuntimeDisconnected, "Plugin runtime is disconnected.");
        if (string.IsNullOrWhiteSpace(ownerInstanceId))
            return TvAirOperationResult<HostSurfaceState>.Fail(TvAirErrorCode.InvalidRequest, "Host surface owner id is required.");
        if (kind == HostSurfaceKind.WindowContent && !_windows.Exists(ownerInstanceId))
            return TvAirOperationResult<HostSurfaceState>.Fail(TvAirErrorCode.EntityNotFound, "Window owner was not found.");
        if (kind == HostSurfaceKind.ViewerOverlay && _resolveViewerTarget(ownerInstanceId) is null)
            return TvAirOperationResult<HostSurfaceState>.Fail(TvAirErrorCode.EntityNotFound, "Viewer session owner was not found.");

        var normalizedSlot = string.IsNullOrWhiteSpace(slotId) ? "main" : slotId.Trim();
        var id = $"{kind}:{ownerInstanceId}:{normalizedSlot}";
        var host = _hosts.GetOrAdd(id, _ => new HostSurfaceState
        {
            HostSurfaceInstanceId = id,
            Kind = kind,
            OwnerInstanceId = ownerInstanceId,
            SlotId = normalizedSlot,
            Capabilities = CapabilityMap[kind],
            AttachedSurfaceIds = Array.Empty<string>()
        });
        return TvAirOperationResult<HostSurfaceState>.Ok(host);
    }

    public TvAirOperationResult<PluginSurfaceAttachment> Attach(AttachPluginSurfaceRequest request)
    {
        if (!Available()) return TvAirOperationResult<PluginSurfaceAttachment>.Fail(TvAirErrorCode.RuntimeDisconnected, "Plugin runtime is disconnected.");
        if (request is null)
            return TvAirOperationResult<PluginSurfaceAttachment>.Fail(TvAirErrorCode.InvalidRequest, "Attachment request is required.");
        if (!_hosts.TryGetValue(request.HostSurfaceInstanceId, out var host))
            return TvAirOperationResult<PluginSurfaceAttachment>.Fail(TvAirErrorCode.HostSurfaceUnavailable, "Host surface was not found.");
        if (!_surfaces.TryGetDefinition(request.SurfaceInstanceId, out var definition))
            return TvAirOperationResult<PluginSurfaceAttachment>.Fail(TvAirErrorCode.EntityNotFound, "Surface instance was not found.");
        if (!Supports(host.Kind, definition.Kind))
            return TvAirOperationResult<PluginSurfaceAttachment>.Fail(TvAirErrorCode.CapabilityUnavailable, "Surface kind is not supported by the selected host surface.");

        var missing = definition.RequiredCapabilities
            .Where(x => !host.Capabilities.Contains(x, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length != 0)
            return TvAirOperationResult<PluginSurfaceAttachment>.Fail(TvAirErrorCode.CapabilityUnavailable, $"Host surface is missing required capabilities: {string.Join(",", missing)}");

        var existing = _attachments.Values.FirstOrDefault(x =>
            string.Equals(x.SurfaceInstanceId, request.SurfaceInstanceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.HostSurfaceInstanceId, request.HostSurfaceInstanceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.SlotId, request.SlotId, StringComparison.Ordinal));
        if (existing is not null)
            return TvAirOperationResult<PluginSurfaceAttachment>.Ok(existing);

        var attachment = new PluginSurfaceAttachment
        {
            AttachmentId = Guid.NewGuid().ToString("N"),
            SurfaceInstanceId = request.SurfaceInstanceId,
            HostSurfaceInstanceId = request.HostSurfaceInstanceId,
            SlotId = string.IsNullOrWhiteSpace(request.SlotId) ? host.SlotId : request.SlotId.Trim()
        };

        _attachments[attachment.AttachmentId] = attachment;
        _hosts[host.HostSurfaceInstanceId] = host with
        {
            AttachedSurfaceIds = host.AttachedSurfaceIds
                .Append(request.SurfaceInstanceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        _surfaces.AttachHost(request.SurfaceInstanceId, host.HostSurfaceInstanceId);
        if (host.Kind == HostSurfaceKind.WindowContent)
            _windows.AttachSurface(host.OwnerInstanceId, request.SurfaceInstanceId);
        return TvAirOperationResult<PluginSurfaceAttachment>.Ok(attachment);
    }

    public TvAirOperationResult Detach(string attachmentId)
    {
        if (!Available()) return TvAirOperationResult.Fail(TvAirErrorCode.RuntimeDisconnected, "Plugin runtime is disconnected.");
        if (!_attachments.TryRemove(attachmentId, out var attachment))
            return TvAirOperationResult.Fail(TvAirErrorCode.EntityNotFound, "Attachment was not found.");
        if (_hosts.TryGetValue(attachment.HostSurfaceInstanceId, out var host))
        {
            _hosts[host.HostSurfaceInstanceId] = host with
            {
                AttachedSurfaceIds = host.AttachedSurfaceIds
                    .Where(x => !string.Equals(x, attachment.SurfaceInstanceId, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            };
            if (host.Kind == HostSurfaceKind.WindowContent)
                _windows.DetachSurface(host.OwnerInstanceId, attachment.SurfaceInstanceId);
        }
        _surfaces.DetachHost(attachment.SurfaceInstanceId, attachment.HostSurfaceInstanceId);
        return TvAirOperationResult.Ok();
    }

    public IReadOnlyList<HostSurfaceState> List()
        => _hosts.Values.OrderBy(x => x.Kind).ThenBy(x => x.OwnerInstanceId).ThenBy(x => x.SlotId).ToArray();

    private bool Available() => Volatile.Read(ref _disposed) == 0 && _runtime.IsConnected;

    private static bool Supports(HostSurfaceKind host, PluginSurfaceKind surface)
        => host switch
        {
            HostSurfaceKind.ViewerOverlay => surface == PluginSurfaceKind.OverlayScene,
            HostSurfaceKind.Background => surface is PluginSurfaceKind.Web or PluginSurfaceKind.Native or PluginSurfaceKind.Document or PluginSurfaceKind.Canvas,
            _ => surface != PluginSurfaceKind.OverlayScene
        };

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        foreach (var id in _attachments.Keys.ToArray()) Detach(id);
        Interlocked.Exchange(ref _disposed, 1);
        _hosts.Clear();
    }
}
