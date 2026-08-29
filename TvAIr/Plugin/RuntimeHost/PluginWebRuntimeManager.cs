using System.Collections.Concurrent;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using TvAIr.Core;
using TvAIrPlugin.Bridge;
using TvAIrPlugin.Events;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Surfaces;
using TvAIrPlugin.WebRuntime;

namespace TvAIr.Plugin.RuntimeHost;

/// <summary>
/// Owns WebView2 runtime instances for Web surfaces. The host uses CoreWebView2Controller directly
/// against the native window content handle, avoiding framework-specific WPF/WinForms facade assemblies.
/// </summary>
internal sealed class PluginWebRuntimeManager : ITvAirPluginWebRuntimeApi, IDisposable
{
    private static readonly JsonSerializerOptions BridgeJsonOptions = CreateBridgeJsonOptions();

    private static JsonSerializerOptions CreateBridgeJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return options;
    }

    private static readonly string[] CapabilitySet =
    [
        "WebRuntime.ModernJavaScript",
        "WebRuntime.EsModules",
        "WebRuntime.Dom",
        "WebRuntime.Canvas",
        "WebRuntime.Svg",
        "WebRuntime.Async",
        "WebRuntime.Timer",
        "WebRuntime.Router",
        "WebRuntime.AssetOrigin"
    ];

    private readonly PluginRuntimeManager _runtime;
    private readonly PluginSurfaceManager _surfaces;
    private readonly HostSurfaceRegistry _hostSurfaces;
    private readonly PluginWindowManager _windows;
    private readonly PluginAssetCatalog _assets;
    private readonly PluginBridgeManager _bridge;
    private readonly PluginEventChannel _events;
    private readonly LogRepository _log;
    private readonly ConcurrentDictionary<string, PluginWebRuntimeState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HostedController> _controllers = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public PluginWebRuntimeManager(
        PluginRuntimeManager runtime,
        PluginSurfaceManager surfaces,
        HostSurfaceRegistry hostSurfaces,
        PluginWindowManager windows,
        PluginAssetCatalog assets,
        PluginBridgeManager bridge,
        PluginEventChannel events,
        LogRepository log)
    {
        _runtime = runtime;
        _surfaces = surfaces;
        _hostSurfaces = hostSurfaces;
        _windows = windows;
        _assets = assets;
        _bridge = bridge;
        _events = events;
        _log = log;
    }

    public IReadOnlyList<string> Capabilities => CapabilitySet;

    public TvAirOperationResult<PluginWebRuntimeState> Start(StartPluginWebRuntimeRequest request)
    {
        if (!Available()) return Disconnected<PluginWebRuntimeState>();
        if (request is null || string.IsNullOrWhiteSpace(request.SurfaceInstanceId) || string.IsNullOrWhiteSpace(request.EntryPoint))
            return TvAirOperationResult<PluginWebRuntimeState>.Fail(TvAirErrorCode.InvalidRequest, "Surface instance id and entry point are required.");

        var surface = _surfaces.Get(request.SurfaceInstanceId);
        if (!surface.Succeeded || surface.Value is null)
            return TvAirOperationResult<PluginWebRuntimeState>.Fail(TvAirErrorCode.EntityNotFound, "Surface instance was not found.");
        if (surface.Value.Kind != PluginSurfaceKind.Web)
            return TvAirOperationResult<PluginWebRuntimeState>.Fail(TvAirErrorCode.InvalidRequest, "Only Web surfaces can start a web runtime.");

        var asset = _assets.Describe(request.EntryPoint);
        if (asset is null || !asset.IsEntryPoint)
            return TvAirOperationResult<PluginWebRuntimeState>.Fail(TvAirErrorCode.EntityNotFound, "Web runtime entry point asset was not found or is not declared as an entry point.");

        var attachedHost = ResolveAttachedWindowHost(surface.Value);
        if (!attachedHost.Succeeded || attachedHost.Value is null)
            return TvAirOperationResult<PluginWebRuntimeState>.Fail(
                attachedHost.Error?.Code ?? TvAirErrorCode.HostSurfaceUnavailable,
                attachedHost.Error?.Message ?? "Window content host surface was not found.");

        var existing = _states.Values.FirstOrDefault(x =>
            string.Equals(x.SurfaceInstanceId, request.SurfaceInstanceId, StringComparison.OrdinalIgnoreCase)
            && x.LifecycleState is not PluginWebRuntimeLifecycleState.Closed and not PluginWebRuntimeLifecycleState.Failed);
        if (existing is not null)
            return TvAirOperationResult<PluginWebRuntimeState>.Ok(existing);

        var id = $"web:{request.SurfaceInstanceId}:{Guid.NewGuid():N}";
        var starting = new PluginWebRuntimeState
        {
            RuntimeSessionId = _runtime.RuntimeSessionId,
            WebRuntimeInstanceId = id,
            SurfaceInstanceId = request.SurfaceInstanceId,
            Origin = _assets.Origin,
            EntryPoint = asset.LogicalPath,
            LifecycleState = PluginWebRuntimeLifecycleState.Starting
        };
        _states[id] = starting;

        try
        {
            var root = _assets.MaterializeRuntimeRoot();
            _windows.InvokeContentAsync(attachedHost.Value.OwnerInstanceId, async contentHost =>
            {
                contentHost.Controls.Clear();
                contentHost.Margin = System.Windows.Forms.Padding.Empty;
                contentHost.Padding = System.Windows.Forms.Padding.Empty;
                _ = contentHost.Handle;

                var environment = await CoreWebView2Environment.CreateAsync();
                var controller = await environment.CreateCoreWebView2ControllerAsync(contentHost.Handle);
                void ApplyContentBounds() => controller.Bounds = contentHost.ClientRectangle;
                ApplyContentBounds();
                controller.IsVisible = true;

                EventHandler? resized = (_, _) => ApplyContentBounds();
                contentHost.Resize += resized;
                contentHost.ClientSizeChanged += resized;

                var windowDefinition = _windows.GetDefinitionForInstance(attachedHost.Value.OwnerInstanceId);
                var formClient = contentHost.FindForm()?.ClientRectangle ?? System.Drawing.Rectangle.Empty;
                _log.Add("PLUGIN_TOOL_WINDOW_CONTENT_HOST", _runtime.PluginId,
                    $"result=OK source=webview2_runtime windowId={attachedHost.Value.OwnerInstanceId} formClientBounds={formClient.Left},{formClient.Top},{formClient.Width}x{formClient.Height} contentControlBounds={controller.Bounds.Left},{controller.Bounds.Top},{controller.Bounds.Width}x{controller.Bounds.Height} contentControlDock=Fill contentControlMargin=0,0,0,0 contentControlPadding=0,0,0,0 descriptorSizeReference={windowDefinition?.SizeReference.ToString() ?? "-"} savedPlacementSizeReference=- appliedSizeReference={windowDefinition?.SizeReference.ToString() ?? "-"} hostKind=corewebview2_controller rule=runtime_tool_window_content_occupancy_contract");

                controller.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    _assets.Origin.Host,
                    root,
                    CoreWebView2HostResourceAccessKind.DenyCors);

                var initialStateJson = JsonSerializer.Serialize(request.InitialState);
                await controller.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    "Object.defineProperty(window, '__tvairInitialState', { value: " + initialStateJson + ", writable: false, configurable: false });");
                await controller.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BuildBridgeBootstrap(_runtime.RuntimeSessionId, _bridge.ProtocolVersion));

                var hosted = new HostedController(contentHost, environment, controller, resized, _bridge, _events, _log, _runtime.PluginId, id);
                _controllers[id] = hosted;
                hosted.AttachBridge();

                var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<CoreWebView2NavigationCompletedEventArgs>? completed = null;
                completed = (_, args) =>
                {
                    controller.CoreWebView2.NavigationCompleted -= completed;
                    if (args.IsSuccess)
                        navigation.TrySetResult();
                    else
                        navigation.TrySetException(new InvalidOperationException($"Web runtime navigation failed: {args.WebErrorStatus}"));
                };
                controller.CoreWebView2.NavigationCompleted += completed;
                controller.CoreWebView2.Navigate(_assets.ResolveUri(asset.LogicalPath).AbsoluteUri);
                await navigation.Task;
                _log.Add("PLUGIN_WEB_RUNTIME", _runtime.PluginId, $"result=NAVIGATION_OK runtimeId={id} origin={_assets.Origin} entry={asset.LogicalPath} bridge=attached rule=plugin_web_runtime_phase3_observability");
            }).GetAwaiter().GetResult();

            _surfaces.Start(request.SurfaceInstanceId);
            var running = starting with { LifecycleState = PluginWebRuntimeLifecycleState.Running, LastError = null };
            _states[id] = running;
            return TvAirOperationResult<PluginWebRuntimeState>.Ok(running);
        }
        catch (Exception ex)
        {
            if (_controllers.TryRemove(id, out var partial))
                partial.Dispose();
            var failed = starting with
            {
                LifecycleState = PluginWebRuntimeLifecycleState.Failed,
                LastError = ex.GetType().Name + ": " + ex.Message
            };
            // 失敗状態は戻り値と通常ログで呼出元へ返しており、Manager 内へ履歴として保持しない。
            // 起動失敗のたびに GUID 単位の state を残すと、長期稼働で再試行回数に比例して増える。
            _states.TryRemove(id, out _);
            return TvAirOperationResult<PluginWebRuntimeState>.Fail(TvAirErrorCode.InternalError, failed.LastError);
        }
    }

    public TvAirOperationResult Suspend(string id)
    {
        if (!Available()) return Disconnected();
        if (!_states.TryGetValue(id, out var state)) return Missing();
        if (_controllers.TryGetValue(id, out var hosted))
            hosted.Post(controller => controller.IsVisible = false);
        _surfaces.Suspend(state.SurfaceInstanceId);
        _states[id] = state with { LifecycleState = PluginWebRuntimeLifecycleState.Suspended };
        return TvAirOperationResult.Ok();
    }

    public TvAirOperationResult Resume(string id)
    {
        if (!Available()) return Disconnected();
        if (!_states.TryGetValue(id, out var state)) return Missing();
        if (_controllers.TryGetValue(id, out var hosted))
            hosted.Post(controller => controller.IsVisible = true);
        _surfaces.Resume(state.SurfaceInstanceId);
        _states[id] = state with { LifecycleState = PluginWebRuntimeLifecycleState.Running };
        return TvAirOperationResult.Ok();
    }

    public TvAirOperationResult Close(string id)
    {
        if (!_states.TryGetValue(id, out _)) return Missing();
        if (_controllers.TryRemove(id, out var hosted))
            hosted.Dispose();

        // 長期連続稼働保護:
        // Closed state は診断履歴ではなく実行中instanceの管理表である。終了済みGUIDを残すと、
        // Web surface の開始・終了回数に比例して _states が増えるため、controller破棄と同時に除去する。
        _states.TryRemove(id, out _);
        return TvAirOperationResult.Ok();
    }

    public TvAirOperationResult<PluginWebRuntimeState> Get(string id)
        => _states.TryGetValue(id, out var state)
            ? TvAirOperationResult<PluginWebRuntimeState>.Ok(state)
            : TvAirOperationResult<PluginWebRuntimeState>.Fail(TvAirErrorCode.EntityNotFound, "Web runtime instance was not found.");

    public IReadOnlyList<PluginWebRuntimeState> List() => _states.Values.OrderBy(x => x.WebRuntimeInstanceId).ToArray();

    private TvAirOperationResult<HostSurfaceState> ResolveAttachedWindowHost(PluginSurfaceState surface)
    {
        foreach (var hostId in surface.AttachedHostSurfaceIds)
        {
            var host = _hostSurfaces.List().FirstOrDefault(x =>
                string.Equals(x.HostSurfaceInstanceId, hostId, StringComparison.OrdinalIgnoreCase));
            if (host is not null && host.Kind == HostSurfaceKind.WindowContent)
                return TvAirOperationResult<HostSurfaceState>.Ok(host);
        }
        return TvAirOperationResult<HostSurfaceState>.Fail(
            TvAirErrorCode.HostSurfaceUnavailable,
            "Web surface must be attached to a WindowContent host surface before starting the web runtime.");
    }

    private static string BuildBridgeBootstrap(string runtimeSessionId, string protocolVersion)
    {
        var session = JsonSerializer.Serialize(runtimeSessionId);
        var protocol = JsonSerializer.Serialize(protocolVersion);
        const string bootstrap = """
(() => {
  const pending = new Map();
  const eventHandlers = new Map();
  const pendingEvents = new Map();
  let nextId = 0;
  window.chrome.webview.addEventListener('message', e => {
    const m = e.data || {};
    if (m.type === 'response') {
      const p = pending.get(m.requestId); if (!p) return;
      pending.delete(m.requestId); m.succeeded ? p.resolve(m.result) : p.reject(m.error);
    } else if (m.type === 'event') {
      const h = eventHandlers.get(m.subscriptionId);
      if (h) h(m.envelope);
      else { const items = pendingEvents.get(m.subscriptionId) || []; items.push(m.envelope); pendingEvents.set(m.subscriptionId, items); }
    } else if (m.type === 'subscription') {
      const p = pending.get(m.requestId); if (!p) return;
      pending.delete(m.requestId); p.resolve(m.state);
    }
  });
  const send = msg => window.chrome.webview.postMessage(msg);
  window.tvair = Object.freeze({
    runtimeSessionId: __TVAIR_SESSION__, protocolVersion: __TVAIR_PROTOCOL__,
    invoke(method, parameters = {}, cancellationId = null) {
      const requestId = `js-${++nextId}`;
      return new Promise((resolve, reject) => { pending.set(requestId, {resolve, reject}); send({type:'invoke', request:{protocolVersion:__TVAIR_PROTOCOL__, runtimeSessionId:__TVAIR_SESSION__, requestId, method, parameters, cancellationId}}); });
    },
    cancel(cancellationId) { send({type:'cancel', cancellationId}); },
    async subscribe(eventType, handler, resumeAfterSequence = null) {
      const requestId = `sub-${++nextId}`;
      const state = await new Promise((resolve, reject) => { pending.set(requestId, {resolve, reject}); send({type:'subscribe', requestId, eventType, resumeAfterSequence}); });
      eventHandlers.set(state.subscriptionId, handler);
      for (const envelope of pendingEvents.get(state.subscriptionId) || []) handler(envelope);
      pendingEvents.delete(state.subscriptionId);
      return state;
    },
    unsubscribe(subscriptionId) {
      const requestId = `unsub-${++nextId}`;
      return new Promise((resolve, reject) => {
        pending.set(requestId, {resolve, reject});
        send({type:'unsubscribe', requestId, subscriptionId});
      }).then(result => {
        if (result === true) { eventHandlers.delete(subscriptionId); pendingEvents.delete(subscriptionId); }
        return result === true;
      });
    }
  });
})();
""";

        return bootstrap
            .Replace("__TVAIR_SESSION__", session, StringComparison.Ordinal)
            .Replace("__TVAIR_PROTOCOL__", protocol, StringComparison.Ordinal);
    }

    private bool Available() => Volatile.Read(ref _disposed) == 0 && _runtime.IsConnected;
    private static TvAirOperationResult Missing() => TvAirOperationResult.Fail(TvAirErrorCode.EntityNotFound, "Web runtime instance was not found.");
    private static TvAirOperationResult Disconnected() => TvAirOperationResult.Fail(TvAirErrorCode.RuntimeDisconnected, "Plugin runtime is disconnected.");
    private static TvAirOperationResult<T> Disconnected<T>() => TvAirOperationResult<T>.Fail(TvAirErrorCode.RuntimeDisconnected, "Plugin runtime is disconnected.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var id in _states.Keys.ToArray()) Close(id);
        _controllers.Clear();
        _states.Clear();
    }

    private sealed class HostedController : IDisposable
    {
        private readonly System.Windows.Forms.Control _host;
        private readonly CoreWebView2Environment _environment;
        private readonly CoreWebView2Controller _controller;
        private readonly EventHandler _resized;
        private readonly PluginBridgeManager _bridge;
        private readonly PluginEventChannel _events;
        private readonly LogRepository _log;
        private readonly string _pluginId;
        private readonly string _runtimeId;
        private readonly ConcurrentDictionary<string, PluginEventSubscription> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
        private int _disposed;

        public HostedController(
            System.Windows.Forms.Control host,
            CoreWebView2Environment environment,
            CoreWebView2Controller controller,
            EventHandler resized,
            PluginBridgeManager bridge,
            PluginEventChannel events,
            LogRepository log,
            string pluginId,
            string runtimeId)
        {
            _host = host;
            _environment = environment;
            _controller = controller;
            _resized = resized;
            _bridge = bridge;
            _events = events;
            _log = log;
            _pluginId = pluginId;
            _runtimeId = runtimeId;
        }

        public void AttachBridge() => _controller.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                _log.Add("PLUGIN_WEB_BRIDGE", _pluginId, $"result=RECEIVED runtimeId={_runtimeId} messageType={type ?? "-"} rule=plugin_web_runtime_phase3_observability");
                if (type == "invoke")
                {
                    var request = root.GetProperty("request").Deserialize<PluginBridgeRequest>(BridgeJsonOptions)!;
                    var response = await _bridge.InvokeAsync(request).ConfigureAwait(false);
                    _log.Add("PLUGIN_WEB_BRIDGE", _pluginId, $"result={(response.Succeeded ? "OK" : "REJECTED")} runtimeId={_runtimeId} method={request.Method} requestId={request.RequestId} errorCode={response.Error?.Code.ToString() ?? "-"} rule=plugin_web_runtime_phase3_observability");
                    PostJson(new { type = "response", response.RequestId, response.Succeeded, response.Result, response.Error });
                }
                else if (type == "cancel")
                {
                    _bridge.Cancel(root.GetProperty("cancellationId").GetString() ?? string.Empty);
                }
                else if (type == "subscribe")
                {
                    var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
                    var eventType = root.GetProperty("eventType").GetString() ?? string.Empty;
                    long? resume = root.TryGetProperty("resumeAfterSequence", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : null;
                    var replay = new List<PluginEventEnvelope>();
                    PluginEventSubscription? subscription = null;
                    subscription = _events.Subscribe(
                        new PluginEventSubscriptionRequest { EventType = eventType, ResumeAfterSequence = resume },
                        envelope =>
                        {
                            if (subscription is null)
                            {
                                lock (replay) replay.Add(envelope);
                                return;
                            }
                            PostJson(new { type = "event", subscriptionId = subscription.State.SubscriptionId, envelope });
                        });
                    _subscriptions[subscription.State.SubscriptionId] = subscription;
                    _log.Add("PLUGIN_WEB_EVENT", _pluginId, $"result=SUBSCRIBED runtimeId={_runtimeId} eventType={eventType} subscriptionId={subscription.State.SubscriptionId} currentSequence={subscription.State.CurrentSequence} replayGap={subscription.State.ReplayGapDetected} bufferedReplay={replay.Count} rule=plugin_web_runtime_phase3_observability");
                    PostJson(new { type = "subscription", requestId, state = subscription.State });
                    PluginEventEnvelope[] replaySnapshot;
                    lock (replay) replaySnapshot = replay.ToArray();
                    foreach (var envelope in replaySnapshot)
                    {
                        _log.Add("PLUGIN_WEB_EVENT", _pluginId, $"result=REPLAY_SENT runtimeId={_runtimeId} eventType={envelope.EventType} sequence={envelope.Sequence} subscriptionId={subscription.State.SubscriptionId} rule=plugin_web_runtime_phase3_observability");
                        PostJson(new { type = "event", subscriptionId = subscription.State.SubscriptionId, envelope });
                    }
                }
                else if (type == "unsubscribe")
                {
                    var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
                    var id = root.GetProperty("subscriptionId").GetString() ?? string.Empty;
                    var unsubscribed = false;
                    if (_subscriptions.TryRemove(id, out var subscription))
                    {
                        subscription.Dispose();
                        unsubscribed = true;
                        _log.Add("PLUGIN_WEB_EVENT", _pluginId, $"result=UNSUBSCRIBED runtimeId={_runtimeId} subscriptionId={id} rule=plugin_web_runtime_phase3_observability");
                    }
                    else
                    {
                        _log.Add("PLUGIN_WEB_EVENT", _pluginId, $"result=NOT_FOUND runtimeId={_runtimeId} subscriptionId={id} rule=plugin_web_runtime_phase3_observability");
                    }
                    PostJson(new { type = "response", requestId, succeeded = true, result = unsubscribed, error = (object?)null });
                }
            }
            catch (Exception ex)
            {
                _log.Add("PLUGIN_WEB_BRIDGE", _pluginId, $"result=ERROR runtimeId={_runtimeId} type={ex.GetType().Name} message={ex.Message.Replace(" ", "_")} rule=plugin_web_runtime_phase3_observability");
                PostJson(new { type = "bridgeError", error = new { code = "InternalError", message = ex.Message } });
            }
        }

        private void PostJson(object value)
        {
            var json = JsonSerializer.Serialize(value, BridgeJsonOptions);
            Post(_ => _controller.CoreWebView2.PostWebMessageAsJson(json));
        }

        public void Post(Action<CoreWebView2Controller> action)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (_host.IsDisposed) return;
            if (_host.InvokeRequired)
                _host.BeginInvoke(new Action(() => action(_controller)));
            else
                action(_controller);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            void CloseCore()
            {
                _host.Resize -= _resized;
                _host.ClientSizeChanged -= _resized;
                _controller.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                foreach (var subscription in _subscriptions.Values) subscription.Dispose();
                _subscriptions.Clear();
                _controller.Close();
                _ = _environment;
            }
            if (_host.IsDisposed)
            {
                _controller.Close();
                _ = _environment;
            }
            else if (_host.InvokeRequired)
            {
                try { _host.Invoke((Action)CloseCore); } catch { _controller.Close(); }
            }
            else
            {
                CloseCore();
            }
        }
    }
}
