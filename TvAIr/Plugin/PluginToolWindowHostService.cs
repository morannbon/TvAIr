namespace TvAIr.Plugin;

using System.Collections.Concurrent;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Text.Json;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;
using Microsoft.Win32;
using TvAIr.Core;
using TvAIrPlugin.Windows;

/// <summary>
/// release_contract: TvAIr本体管理Plugin Tool Window direct content表示修正。
/// pluginId+routeSegment reuse、host close同期、状態保存、alwaysOnTop/size反映、JSON画面抑止fallbackを同じ境界へ集約する。
/// </summary>
public sealed class PluginToolWindowHostService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HostedWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly PluginWindowSessionStore _sessions;
    private readonly PluginTypedEventHub _typedEvents;
    private readonly LogRepository _log;

    public PluginToolWindowHostService(PluginWindowSessionStore sessions, PluginTypedEventHub typedEvents, LogRepository log)
    {
        _sessions = sessions;
        _typedEvents = typedEvents;
        _log = log;
        TryEnsureWebBrowserFeatureControl();
    }

    public PluginToolWindowOpenResult OpenOrActivate(PluginWindowSession session, string absoluteUrl, PluginToolWindowIconSpec? iconSpec = null, bool activate = true)
    {
        if (session is null) return new PluginToolWindowOpenResult("FAILED", "none", IsWebView2RuntimeAvailable(), false, false, false, "missing_session", iconSpec?.Source ?? "none", false, iconSpec?.Diagnostics ?? "missing_session");
        var windowId = session.WindowId;
        lock (_gate)
        {
            if (_windows.TryGetValue(windowId, out var existing) && existing.IsAlive)
            {
                existing.PostShow(absoluteUrl, session, iconSpec, activate);
                _sessions.MarkHostAlive(windowId, true);
                var existingIcon = existing.ApplyIconWithAudit(iconSpec);
                return new PluginToolWindowOpenResult(activate ? "ACTIVATED" : "REVEALED", existing.HostKind, IsWebView2RuntimeAvailable(), true, true, activate, activate ? "existing_window_reused_and_activated" : "existing_window_reused_without_activation", existingIcon.Source, existingIcon.Applied, existingIcon.Diagnostics);
            }

            var host = new HostedWindow(session, absoluteUrl, iconSpec, IsWebView2RuntimeAvailable(), activate, OnHostedWindowClosed, OnHostedWindowClosing, OnHostedWindowStateChanged, _log);
            _windows[windowId] = host;
            _sessions.MarkHostAlive(windowId, true);
            host.Start();
            return new PluginToolWindowOpenResult("ISSUED", host.HostKind, host.WebView2RuntimeAvailable, false, true, false, "new_window_started_activate_requested", iconSpec?.Source ?? "default", iconSpec is not null, iconSpec?.Diagnostics ?? "default_icon_contract");
        }
    }

    public PluginToolWindowOpenResult RefreshExisting(PluginWindowSession session, string absoluteUrl)
    {
        if (session is null) return new PluginToolWindowOpenResult("FAILED", "none", IsWebView2RuntimeAvailable(), false, false, false, "missing_session", "none", false, "missing_session");
        lock (_gate)
        {
            if (!_windows.TryGetValue(session.WindowId, out var existing) || !existing.IsAlive)
                return new PluginToolWindowOpenResult("NOT_FOUND", "none", IsWebView2RuntimeAvailable(), false, false, false, "existing_window_not_found", "none", false, "existing_window_not_found");

            // runtime_tool_window_interaction_state_contract:
            // shell+iframe mode は Session.Revision を正本として iframe だけを更新する。
            // outer shell 自体を Navigate すると、refresh対象と無関係な表示コンテキストの
            // scroll/center状態までdocument再生成に巻き込むため禁止する。
            // WebBrowser direct-content fallback だけは shell poll が無いので、同一documentを
            // Navigateしつつ Host がviewport scrollを保存/復元する。
            var directContent = IsDirectContentNavigation(absoluteUrl);
            existing.PostRefreshExisting(absoluteUrl, session, directContent);
            _sessions.MarkHostAlive(session.WindowId, true);
            return new PluginToolWindowOpenResult(
                "REFRESHED",
                existing.HostKind,
                IsWebView2RuntimeAvailable(),
                true,
                true,
                false,
                directContent
                    ? "existing_direct_content_refresh_preserve_interaction"
                    : "existing_shell_kept_iframe_refresh_by_revision",
                "none",
                false,
                "no_activation");
        }
    }

    private static bool IsDirectContentNavigation(string? absoluteUrl)
        => (absoluteUrl ?? string.Empty).Contains("__tvairToolHostContent=1", StringComparison.OrdinalIgnoreCase);

    public PluginToolWindowStatePatchHostResult ApplyStatePatch(string? windowId, IReadOnlyList<RuntimeUiPatch>? patches, long stateRevision)
    {
        if (string.IsNullOrWhiteSpace(windowId))
            return PluginToolWindowStatePatchHostResult.CreateSkipped(windowId, 0, stateRevision, "window_closed");

        HostedWindow? target;
        lock (_gate)
        {
            if (!_windows.TryGetValue(windowId.Trim(), out target) || !target.IsAlive)
                return PluginToolWindowStatePatchHostResult.CreateSkipped(windowId, patches?.Count ?? 0, stateRevision, "window_closed");
        }

        return target.ApplyStatePatchWithAudit(patches, stateRevision);
    }

    internal bool TryExecuteOwnedDialog<T>(string? windowId, Func<IWin32Window, T> dialog, out T? result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(windowId) || dialog is null) return false;
        HostedWindow? target;
        lock (_gate)
        {
            if (!_windows.TryGetValue(windowId.Trim(), out target) || !target.IsAlive) return false;
        }
        return target.TryExecuteOwnedDialog(dialog, out result);
    }

    public int RefreshAllForThemeChange(long themeRevision, string? selectedTheme)
    {
        HostedWindow[] targets;
        lock (_gate)
        {
            targets = _windows.Values.Where(window => window.IsAlive).ToArray();
        }

        var selected = NormalizeTheme(selectedTheme);
        var effective = ResolveEffectiveTheme(selected);
        var queued = 0;
        var failed = 0;
        Exception? firstFailure = null;

        // PLUGIN_THEME_REFRESH_FANOUT_CONTRACT
        // 1つのToolWindowへの通知失敗で、後続の正常なWindowまで更新対象から脱落させない。
        // 全Windowへの通知を個別に試行した後、失敗が1件でもあれば呼出元へ部分失敗を通知する。
        foreach (var target in targets)
        {
            try
            {
                target.PostThemeRefresh(themeRevision, selected, effective);
                queued++;
            }
            catch (Exception ex)
            {
                failed++;
                firstFailure ??= ex;
                _log.Add("PLUGIN_TOOL_WINDOW_THEME_REFRESH", "Theme",
                    $"result=FAILED revision={themeRevision} windowId={target.WindowId} exception={ex.GetType().Name} reason={ex.Message} activation=False focus=False positionChange=False sizeChange=False rule=release_contract");
            }
        }

        _log.Add("PLUGIN_TOOL_WINDOW_THEME_REFRESH", "Theme",
            $"result={(failed == 0 ? "QUEUED" : "PARTIAL")} revision={themeRevision} openWindows={targets.Length} queued={queued} failed={failed} activation=False focus=False positionChange=False sizeChange=False minimizedRestore=False scrollPreserve=True rule=release_contract");

        if (failed > 0)
            throw new InvalidOperationException(
                $"ToolWindow theme refresh failed for {failed} of {targets.Length} windows.",
                firstFailure);

        return queued;
    }


    private static string NormalizeTheme(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "light" or "dark" ? normalized : "current";
    }

    private static string ResolveEffectiveTheme(string selectedTheme)
    {
        if (selectedTheme is "light" or "dark") return selectedTheme;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            var light = value is int i ? i != 0 : value?.ToString() != "0";
            return light ? "light" : "dark";
        }
        catch
        {
            return "light";
        }
    }

    public PluginToolWindowHostCapabilities GetCapabilities()
    {
        var webView2 = IsWebView2RuntimeAvailable();
        return new PluginToolWindowHostCapabilities(
            ToolWindowSupported: true,
            HostWindowSupported: true,
            WebView2RuntimeAvailable: webView2,
            HostKind: webView2 ? "winforms_webbrowser_fallback_direct_content_webview2_runtime_detected" : "winforms_webbrowser_fallback_direct_content",
            FallbackHostKind: "winforms_webbrowser_fallback_direct_content",
            FallbackToBrowserRedirectSupported: true,
            JsonScreenSuppressed: true,
            SupportsAlwaysOnTop: true,
            SupportsSize: true,
            SupportsMinSize: true,
            SupportsPositionPersistence: true,
            SupportsStatePersistence: true,
            SupportsReuseExisting: true,
            SupportsActivateExisting: true,
            ReuseKey: "pluginId+routeSegment",
            RefreshTarget: "content",
            RefreshReloadScope: "toolwindow-content-document|iframe-content-only",
            ScriptExecutionAllowed: false,
            SupportsManifestFormIcon: true,
            FormIconSourcePriority: "EmbeddedResource>plugin_file>default_TvAIr_icon",
            ContractVersion: TvAIrVersionContract.PluginHostContractVersion);
    }

    public bool IsHostAlive(string? windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return false;
        lock (_gate)
        {
            return _windows.TryGetValue(windowId, out var existing) && existing.IsAlive;
        }
    }

    public PluginWindowHostState? GetHostState(string? windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return null;
        lock (_gate)
        {
            if (!_windows.TryGetValue(windowId, out var existing) || !existing.IsAlive) return null;
            return existing.Snapshot();
        }
    }

    public PluginToolWindowApplyResult ApplySession(string? windowId, PluginWindowSession session)
    {
        if (string.IsNullOrWhiteSpace(windowId) || session is null)
            return PluginToolWindowApplyResult.NotFound(windowId);

        HostedWindow? existing;
        lock (_gate)
        {
            if (!_windows.TryGetValue(windowId, out existing) || !existing.IsAlive)
                return PluginToolWindowApplyResult.NotFound(windowId);
        }

        return existing.ApplySessionWithAudit(session);
    }

    public bool Close(string? windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return false;
        HostedWindow? existing;
        lock (_gate)
        {
            if (!_windows.TryGetValue(windowId, out existing)) return false;
        }
        existing.PostClose();
        return true;
    }

    private void OnHostedWindowClosing(
        string windowId,
        string hostInstanceId,
        PluginWindowHostState? state,
        string source,
        PluginWindowCloseBehavior closeBehavior,
        PluginWindowBackgroundExecution backgroundExecution)
    {
        var removedFromHostReuseRegistry = false;
        lock (_gate)
        {
            if (_windows.TryGetValue(windowId, out var current)
                && string.Equals(current.HostInstanceId, hostInstanceId, StringComparison.Ordinal))
            {
                removedFromHostReuseRegistry = _windows.Remove(windowId);
            }
        }

        // Dispose確定時点をSessionの再利用不能境界とする。FormClosedまで残すと、
        // ×閉鎖と再openが競合した場合にOpenOrReuseが旧Sessionを拾える。
        // Minimize/Hide/PreserveSessionはこの経路へ入らず、同一Sessionを維持する。
        var closeTransition = _sessions.MarkHostClosing(windowId, state);
        var backgroundStopped = backgroundExecution == PluginWindowBackgroundExecution.StopWithWindow
            && closeTransition.SessionDisposed;
        var backgroundContractSatisfied = backgroundExecution != PluginWindowBackgroundExecution.StopWithWindow || backgroundStopped;
        var reuseRemoved = closeTransition.RemovedFromReuseRegistry && removedFromHostReuseRegistry;

        _log.Add("PLUGIN_TOOL_WINDOW_CLOSE", closeTransition.PluginName,
            $"pluginId={closeTransition.PluginId} windowId={windowId} source={source} closeBehavior={closeBehavior} backgroundExecution={backgroundExecution} sessionDisposed={closeTransition.SessionDisposed} removedFromReuseRegistry={reuseRemoved} backgroundStopped={backgroundStopped} result={(closeTransition.SessionDisposed && reuseRemoved && backgroundContractSatisfied ? "OK" : "PARTIAL")} rule=runtime_descriptor_window_lifetime_contract");

        PublishRuntimeWindowLifecycle(
            closeTransition.PluginId,
            windowId,
            closeTransition.WindowDefinitionId,
            closeTransition.RouteSegment,
            PluginWindowLifecycleState.Closing,
            closeTransition.CloseBehavior,
            closeTransition.BackgroundExecution,
            source,
            closeTransition.SessionDisposed);
    }

    private void OnHostedWindowClosed(PluginWindowSession session, string hostInstanceId, PluginWindowHostState? finalState, bool sessionDetachedAtClosing, PluginWindowCloseBehavior closeBehavior, PluginWindowBackgroundExecution backgroundExecution)
    {
        var windowId = session.WindowId;
        lock (_gate)
        {
            if (_windows.TryGetValue(windowId, out var current)
                && string.Equals(current.HostInstanceId, hostInstanceId, StringComparison.Ordinal))
                _windows.Remove(windowId);
        }
        // 通常のDispose/X閉鎖ではFormClosingで既にSessionを正本から除外済み。
        // 例外終了などFormClosingを通らなかった場合だけ最終回収する。
        if (!sessionDetachedAtClosing)
            _sessions.MarkHostClosed(windowId, finalState);

        PublishRuntimeWindowLifecycle(
            session.PluginId,
            windowId,
            session.WindowDefinitionId,
            session.RouteSegment,
            PluginWindowLifecycleState.Closed,
            closeBehavior,
            backgroundExecution,
            sessionDetachedAtClosing ? "form_closed_after_closing" : "form_closed_without_closing_callback",
            sessionDetachedAtClosing || closeBehavior == PluginWindowCloseBehavior.Dispose);
    }

    private void PublishRuntimeWindowLifecycle(
        string pluginId,
        string windowId,
        string windowDefinitionId,
        string routeSegment,
        PluginWindowLifecycleState state,
        PluginWindowCloseBehavior closeBehavior,
        PluginWindowBackgroundExecution backgroundExecution,
        string source,
        bool sessionDisposed)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(windowId))
            return;

        try
        {
            _typedEvents.Publish(new TvAirEventDto
            {
                EventType = TvAirEventType.RuntimeWindowLifecycleChanged,
                EntityId = $"runtime-window:{windowId}",
                SourceOwnerId = "plugin_tool_window_host",
                ChangeKind = state.ToString(),
                RuntimeWindowLifecycle = new TvAirRuntimeWindowLifecycleDto
                {
                    PluginId = pluginId,
                    WindowInstanceId = windowId,
                    WindowDefinitionId = windowDefinitionId ?? string.Empty,
                    RouteSegment = routeSegment ?? string.Empty,
                    State = state,
                    CloseBehavior = closeBehavior,
                    BackgroundExecution = backgroundExecution,
                    Source = source ?? string.Empty,
                    SessionDisposed = sessionDisposed
                },
                Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pluginId"] = pluginId,
                    ["windowInstanceId"] = windowId,
                    ["windowDefinitionId"] = windowDefinitionId ?? string.Empty,
                    ["routeSegment"] = routeSegment ?? string.Empty,
                    ["state"] = state.ToString(),
                    ["closeBehavior"] = closeBehavior.ToString(),
                    ["backgroundExecution"] = backgroundExecution.ToString(),
                    ["source"] = source ?? string.Empty,
                    ["sessionDisposed"] = sessionDisposed.ToString()
                }
            });
            _log.Add("PLUGIN_RUNTIME_WINDOW_LIFECYCLE_EVENT", pluginId,
                $"result=QUEUED windowId={windowId} windowDefinitionId={windowDefinitionId} routeSegment={routeSegment} state={state} closeBehavior={closeBehavior} backgroundExecution={backgroundExecution} source={source} sessionDisposed={sessionDisposed} rule=runtime_window_lifecycle_event_contract");
        }
        catch (ObjectDisposedException)
        {
            _log.Add("PLUGIN_RUNTIME_WINDOW_LIFECYCLE_EVENT", pluginId,
                $"result=SKIPPED reason=typed_event_hub_disposed windowId={windowId} state={state} rule=runtime_window_lifecycle_event_contract");
        }
    }

    private void OnHostedWindowStateChanged(string windowId, PluginWindowHostState? state)
    {
        _sessions.UpdateHostState(windowId, state);
    }

    private static void TryEnsureWebBrowserFeatureControl()
    {
        try
        {
            var exeName = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName);
            if (string.IsNullOrWhiteSpace(exeName)) exeName = "TvAIr.exe";
            using var emulation = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
            emulation?.SetValue(exeName, 11001, RegistryValueKind.DWord);
            using var gpu = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_GPU_RENDERING");
            gpu?.SetValue(exeName, 1, RegistryValueKind.DWord);
        }
        catch
        {
            // 設定失敗時もhost起動は継続する。状態はcapabilities/logで切り分ける。
        }
    }

    private static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            using var key1 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F1A2F1D8-0BB1-4B8F-9C74-9A03C4F1B9F2}");
            using var key2 = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F1A2F1D8-0BB1-4B8F-9C74-9A03C4F1B9F2}");
            return key1 is not null || key2 is not null;
        }
        catch
        {
            return false;
        }
    }

    private sealed class HostedWindow
    {
        private readonly PluginWindowSession _initialSession;
        private readonly string _initialUrl;
        private readonly PluginToolWindowIconSpec? _initialIconSpec;
        private readonly bool _initialActivateRequested;
        private readonly Action<PluginWindowSession, string, PluginWindowHostState?, bool, PluginWindowCloseBehavior, PluginWindowBackgroundExecution> _onClosed;
        private readonly Action<string, string, PluginWindowHostState?, string, PluginWindowCloseBehavior, PluginWindowBackgroundExecution> _onClosing;
        private readonly Action<string, PluginWindowHostState?> _onStateChanged;
        private readonly BlockingCollection<QueuedToolWindowAction> _actions = new();
        private Thread? _thread;
        private volatile bool _alive;
        private PluginWindowCloseBehavior _currentCloseBehavior;
        private PluginWindowBackgroundExecution _currentBackgroundExecution;
        private int _sessionDetachedAtClosing;
        private ToolWindowForm? _form;
        private readonly LogRepository _log;

        public HostedWindow(PluginWindowSession session, string initialUrl, PluginToolWindowIconSpec? initialIconSpec, bool webView2RuntimeAvailable, bool initialActivateRequested, Action<PluginWindowSession, string, PluginWindowHostState?, bool, PluginWindowCloseBehavior, PluginWindowBackgroundExecution> onClosed, Action<string, string, PluginWindowHostState?, string, PluginWindowCloseBehavior, PluginWindowBackgroundExecution> onClosing, Action<string, PluginWindowHostState?> onStateChanged, LogRepository log)
        {
            _initialSession = session;
            _initialUrl = initialUrl;
            _initialIconSpec = initialIconSpec;
            _initialActivateRequested = initialActivateRequested;
            WebView2RuntimeAvailable = webView2RuntimeAvailable;
            _onClosed = onClosed;
            _onClosing = onClosing;
            _onStateChanged = onStateChanged;
            _currentCloseBehavior = session.CloseBehavior;
            _currentBackgroundExecution = session.BackgroundExecution;
            HostKind = webView2RuntimeAvailable ? "winforms_webbrowser_fallback_direct_content_webview2_runtime_detected" : "winforms_webbrowser_fallback_direct_content";
            _log = log;
        }

        public string HostKind { get; }
        public bool WebView2RuntimeAvailable { get; }
        public bool IsAlive => _alive;
        private bool IsClosingOrClosed => !_alive || Volatile.Read(ref _sessionDetachedAtClosing) != 0 || _actions.IsAddingCompleted;
        public string WindowId => _initialSession.WindowId;
        public string HostInstanceId { get; } = Guid.NewGuid().ToString("N");

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = $"TvAIr.PluginToolWindow.{_initialSession.WindowId}" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void PostShow(string url, PluginWindowSession session, PluginToolWindowIconSpec? iconSpec, bool activate)
        {
            UpdateLifetimeContract(session);
            TryQueueAction("show_and_navigate", form => form.ShowAndNavigate(url, session, iconSpec, activate));
        }

        public void PostNavigateOnly(string url, PluginWindowSession session)
        {
            UpdateLifetimeContract(session);
            TryQueueAction("navigate_only", form => form.NavigateOnly(url, session));
        }

        public void PostRefreshExisting(string url, PluginWindowSession session, bool directContent)
        {
            UpdateLifetimeContract(session);
            TryQueueAction(
                directContent ? "refresh_direct_content" : "refresh_shell_content",
                form => form.RefreshExistingContent(url, session, directContent));
        }

        public void PostThemeRefresh(long themeRevision, string selectedTheme, string effectiveTheme)
            => TryQueueAction("theme_refresh", form => form.RefreshThemeSilently(themeRevision, selectedTheme, effectiveTheme));

        public PluginToolWindowIconApplyResult ApplyIconWithAudit(PluginToolWindowIconSpec? iconSpec)
        {
            if (iconSpec is null) return new PluginToolWindowIconApplyResult("default", false, "no_icon_spec");
            try
            {
                using var applied = new ManualResetEventSlim(false);
                PluginToolWindowIconApplyResult result = new(iconSpec.Source, false, "accepted_pending");
                _actions.Add(new QueuedToolWindowAction("apply_icon_with_audit", form =>
                {
                    try { result = form.ApplyIconWithAudit(iconSpec); }
                    catch (Exception ex) { result = new PluginToolWindowIconApplyResult(iconSpec.Source, false, "icon_apply_exception_" + ex.GetType().Name); }
                    finally { try { applied.Set(); } catch { } }
                }));
                return applied.Wait(TimeSpan.FromMilliseconds(1200)) ? result : new PluginToolWindowIconApplyResult(iconSpec.Source, false, "icon_apply_timeout");
            }
            catch (Exception ex)
            {
                return new PluginToolWindowIconApplyResult(iconSpec.Source, false, "icon_queue_exception_" + ex.GetType().Name);
            }
        }

        public PluginToolWindowStatePatchHostResult ApplyStatePatchWithAudit(IReadOnlyList<RuntimeUiPatch>? patches, long stateRevision)
        {
            if (!_alive)
                return PluginToolWindowStatePatchHostResult.CreateSkipped(_initialSession.WindowId, patches?.Count ?? 0, stateRevision, "window_closed");
            try
            {
                using var applied = new ManualResetEventSlim(false);
                PluginToolWindowStatePatchHostResult result = PluginToolWindowStatePatchHostResult.CreateSkipped(
                    _initialSession.WindowId, patches?.Count ?? 0, stateRevision, "accepted_pending");
                _actions.Add(new QueuedToolWindowAction("apply_statepatch_with_audit", form =>
                {
                    try { result = form.ApplyStatePatchWithAudit(patches, stateRevision); }
                    catch (Exception ex)
                    {
                        result = PluginToolWindowStatePatchHostResult.Failed(
                            _initialSession.WindowId, patches?.Count ?? 0, stateRevision, "apply_exception_" + ex.GetType().Name);
                    }
                    finally { try { applied.Set(); } catch { } }
                }));
                if (applied.Wait(TimeSpan.FromMilliseconds(1200)))
                    return result;

                return IsClosingOrClosed
                    ? PluginToolWindowStatePatchHostResult.CreateSkipped(
                        _initialSession.WindowId, patches?.Count ?? 0, stateRevision, "window_closed")
                    : PluginToolWindowStatePatchHostResult.Failed(
                        _initialSession.WindowId, patches?.Count ?? 0, stateRevision, "statepatch_timeout");
            }
            catch (ObjectDisposedException)
            {
                return PluginToolWindowStatePatchHostResult.CreateSkipped(
                    _initialSession.WindowId, patches?.Count ?? 0, stateRevision, "window_closed");
            }
            catch (InvalidOperationException) when (IsClosingOrClosed)
            {
                return PluginToolWindowStatePatchHostResult.CreateSkipped(
                    _initialSession.WindowId, patches?.Count ?? 0, stateRevision, "window_closed");
            }
            catch (Exception ex)
            {
                return IsClosingOrClosed
                    ? PluginToolWindowStatePatchHostResult.CreateSkipped(
                        _initialSession.WindowId, patches?.Count ?? 0, stateRevision, "window_closed")
                    : PluginToolWindowStatePatchHostResult.Failed(
                        _initialSession.WindowId, patches?.Count ?? 0, stateRevision, "queue_exception_" + ex.GetType().Name);
            }
        }

        public PluginToolWindowApplyResult ApplySessionWithAudit(PluginWindowSession session)
        {
            if (session is null) return PluginToolWindowApplyResult.NotFound(_initialSession.WindowId);
            UpdateLifetimeContract(session);
            try
            {
                using var applied = new ManualResetEventSlim(false);
                PluginToolWindowApplyResult result = PluginToolWindowApplyResult.Pending(_initialSession.WindowId);
                _actions.Add(new QueuedToolWindowAction("apply_session_with_audit", form =>
                {
                    try
                    {
                        result = form.ApplySessionWithAudit(session);
                    }
                    catch (Exception ex)
                    {
                        result = PluginToolWindowApplyResult.Failed(_initialSession.WindowId, "apply_exception_" + ex.GetType().Name);
                    }
                    finally
                    {
                        try { applied.Set(); } catch { }
                    }
                }));

                return applied.Wait(TimeSpan.FromMilliseconds(1200))
                    ? result
                    : PluginToolWindowApplyResult.Timeout(_initialSession.WindowId);
            }
            catch (Exception ex)
            {
                return PluginToolWindowApplyResult.Failed(_initialSession.WindowId, "queue_exception_" + ex.GetType().Name);
            }
        }

        public bool TryExecuteOwnedDialog<T>(Func<IWin32Window, T> dialog, out T? result)
        {
            result = default;
            if (dialog is null || !_alive) return false;
            try
            {
                var form = _form;
                if (form is null || form.IsDisposed || form.Disposing) return false;
                T? captured = default;
                void Execute() => captured = dialog(form);
                if (form.InvokeRequired) form.Invoke(new Action(Execute));
                else Execute();
                result = captured;
                return true;
            }
            catch (Exception ex)
            {
                TryLogActionFailure("owned_dialog", "runtime_path_picker", ex);
                return false;
            }
        }

        public void PostClose()
        {
            // API/Capability closeも×閉鎖と同じDispose正本へ通す。
            // action queueの250ms待ち中に旧Sessionがreuseされないよう、Disposeなら要求時点で先にdetachする。
            if (_currentCloseBehavior == PluginWindowCloseBehavior.Dispose)
                SignalClosing(Snapshot(), "host_close_request", _currentCloseBehavior, _currentBackgroundExecution);
            TryQueueAction("request_close", form => form.RequestClose());
        }

        private void UpdateLifetimeContract(PluginWindowSession session)
        {
            _currentCloseBehavior = session.CloseBehavior;
            _currentBackgroundExecution = session.BackgroundExecution;
        }

        private void SignalClosing(PluginWindowHostState? state, string source, PluginWindowCloseBehavior closeBehavior, PluginWindowBackgroundExecution backgroundExecution)
        {
            if (Interlocked.Exchange(ref _sessionDetachedAtClosing, 1) != 0) return;
            _onClosing(_initialSession.WindowId, HostInstanceId, state, source, closeBehavior, backgroundExecution);
        }


        private bool TryQueueAction(string source, Action<ToolWindowForm> action)
        {
            try
            {
                _actions.Add(new QueuedToolWindowAction(source, action));
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException) when (IsClosingOrClosed)
            {
                return false;
            }
            catch (Exception ex)
            {
                TryLogActionFailure("queue", source, ex);
                return false;
            }
        }

        private void ExecuteQueuedActionSafely(ToolWindowForm form, QueuedToolWindowAction queuedAction)
        {
            try
            {
                queuedAction.Action(form);
            }
            catch (ObjectDisposedException)
            {
                // Form終了と非同期action到着の競合は正常終了として扱う。
            }
            catch (InvalidOperationException) when (form.IsDisposed || form.Disposing || !_alive)
            {
                // Handle破棄後のactionは再実行せず終了する。
            }
            catch (Exception ex)
            {
                TryLogActionFailure("execute", queuedAction.Source, ex);
            }
        }

        private void TryLogActionFailure(string stage, string source, Exception ex)
        {
            try
            {
                _log.Add("PLUGIN_TOOL_WINDOW_ACTION_FAILURE", _initialSession.WindowId,
                    $"stage={stage} source={source} exceptionType={ex.GetType().Name} error={ex.Message} hostAlive={_alive} rule=runtime_toolwindow_action_queue_contract");
            }
            catch
            {
                // ログ基盤障害でToolWindow threadを停止させない。
            }
        }

        private sealed record QueuedToolWindowAction(string Source, Action<ToolWindowForm> Action);

        public PluginWindowHostState? Snapshot()
        {
            try
            {
                var form = _form;
                if (form is null) return null;
                return form.Snapshot();
            }
            catch
            {
                return null;
            }
        }

        private void Run()
        {
            _alive = true;
            PluginWindowHostState? finalState = null;
            try
            {
                Application.EnableVisualStyles();
                using var form = new ToolWindowForm(
                    _initialSession,
                    _initialUrl,
                    _initialIconSpec,
                    _initialActivateRequested,
                    HostKind,
                    WebView2RuntimeAvailable,
                    state => _onStateChanged(_initialSession.WindowId, state),
                    (state, source, closeBehavior, backgroundExecution) =>
                        SignalClosing(state, source, closeBehavior, backgroundExecution),
                    _log);
                _form = form;
                var timer = new System.Windows.Forms.Timer { Interval = 250 };
                timer.Tick += (_, _) =>
                {
                    while (_actions.TryTake(out var queuedAction))
                        ExecuteQueuedActionSafely(form, queuedAction);
                };
                timer.Start();
                form.FormClosed += (_, _) =>
                {
                    finalState = form.Snapshot(hostAliveOverride: false);
                    timer.Stop();
                    _alive = false;
                    _onClosed(_initialSession, HostInstanceId, finalState, Volatile.Read(ref _sessionDetachedAtClosing) != 0, _currentCloseBehavior, _currentBackgroundExecution);
                };
                Application.Run(form);
            }
            catch
            {
                _alive = false;
                _onClosed(_initialSession, HostInstanceId, finalState, Volatile.Read(ref _sessionDetachedAtClosing) != 0, _currentCloseBehavior, _currentBackgroundExecution);
            }
            finally
            {
                _form = null;
            }
        }
    }

    private sealed class ToolWindowForm : Form
    {
        private readonly WebBrowser _browser;
        private long _latestAcceptedStatePatchRevision;
        private long _lastAppliedStatePatchRevision;
        private readonly string _windowId;
        private readonly string _hostKind;
        private readonly bool _webView2RuntimeAvailable;
        private readonly Action<PluginWindowHostState> _onStateChanged;
        private readonly Action<PluginWindowHostState, string, PluginWindowCloseBehavior, PluginWindowBackgroundExecution> _onClosing;
        private readonly LogRepository _log;
        private string _pluginName;
        private PluginWindowScrollPolicy _scrollPolicy;
        private PluginWindowAxisScrollPolicy _horizontalScrollPolicy;
        private PluginWindowAxisScrollPolicy _verticalScrollPolicy;
        private PluginWindowSizeReference _sizeReference;
        private PluginWindowResizeMode _resizeMode;
        private PluginWindowContentSizePolicy _contentSizePolicy;
        private PluginWindowCloseBehavior _closeBehavior;
        private PluginWindowBackgroundExecution _backgroundExecution;
        private PluginWindowStatePersistence _statePersistence;
        private PluginWindowActivationPolicy _activationPolicy;
        private bool _explicitDispose;
        private bool _loaded;
        private bool _applyingSizeContract;
        private bool _userResized;
        private bool _initialContentFitApplied;
        private int _axisLockedOuterWidth;
        private int _axisLockedOuterHeight;
        private string _currentRequestedUrl = string.Empty;
        private Icon? _ownedIcon;
        private FormWindowState _lastObservedWindowState = FormWindowState.Normal;
        private bool _lastObservedIsIconic;
        private int? _pendingThemeScrollLeft;
        private int? _pendingThemeScrollTop;
        private int? _pendingRefreshScrollLeft;
        private int? _pendingRefreshScrollTop;
        private long? _pendingRefreshRevision;
        private long? _pendingThemeRevision;
        private string _pendingThemeSelected = string.Empty;
        private string _pendingThemeEffective = string.Empty;
        private readonly bool _initialActivateRequested;

        protected override bool ShowWithoutActivation => !_initialActivateRequested || base.ShowWithoutActivation;

        public ToolWindowForm(PluginWindowSession session, string url, PluginToolWindowIconSpec? iconSpec, bool initialActivateRequested, string hostKind, bool webView2RuntimeAvailable, Action<PluginWindowHostState> onStateChanged, Action<PluginWindowHostState, string, PluginWindowCloseBehavior, PluginWindowBackgroundExecution> onClosing, LogRepository log)
        {
            _windowId = session.WindowId;
            _initialActivateRequested = initialActivateRequested;
            _hostKind = hostKind;
            _webView2RuntimeAvailable = webView2RuntimeAvailable;
            _onStateChanged = onStateChanged;
            _onClosing = onClosing;
            _log = log;
            _pluginName = string.IsNullOrWhiteSpace(session.PluginName) ? session.RouteSegment : session.PluginName;
            _scrollPolicy = session.ScrollPolicy;
            _horizontalScrollPolicy = session.HorizontalScrollPolicy;
            _verticalScrollPolicy = session.VerticalScrollPolicy;
            _sizeReference = session.SizeReference;
            _resizeMode = session.ResizeMode;
            _contentSizePolicy = session.ContentSizePolicy;
            _closeBehavior = session.CloseBehavior;
            _backgroundExecution = session.BackgroundExecution;
            _statePersistence = session.StatePersistence;
            _activationPolicy = session.ActivationPolicy;
            Text = string.IsNullOrWhiteSpace(session.Title) ? session.PluginName : session.Title;
            StartPosition = session.Left.HasValue && session.Top.HasValue
                ? FormStartPosition.Manual
                : FormStartPosition.CenterScreen;

            // OuterWindow placement has one owner and one application point. Finalize non-client
            // policy and requested outer/content size before applying the persisted location.
            // Applying Location before FormBorderStyle/MinimumSize allows WinForms to recalculate
            // non-client geometry after the saved coordinate was already set, which breaks the
            // zero-drift round-trip contract.
            ApplyResizeMode(session);
            ApplyRequestedSize(session, "constructor");
            MinimumSize = BuildMinimumSize(session);
            ApplyPositiveDimensionGuard("constructor");
            _axisLockedOuterWidth = Width;
            _axisLockedOuterHeight = Height;
            if (session.Left.HasValue && session.Top.HasValue)
            {
                SetBounds(session.Left.Value, session.Top.Value, Width, Height, BoundsSpecified.All);
            }
            LogPlacementTrace(session, "constructor_applied");
            MinimizeBox = true;
            ShowInTaskbar = true;
            TopMost = session.AlwaysOnTop;
            ApplyIconWithAudit(iconSpec);
            BackColor = System.Drawing.SystemColors.Window;
            Padding = Padding.Empty;
            Margin = Padding.Empty;
            AutoScroll = false;
            // ApplyRequestedSize already resolved outer/content semantics.
            _lastObservedWindowState = WindowState;
            _lastObservedIsIconic = IsActuallyMinimized();

            _browser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScriptErrorsSuppressed = true,
                AllowWebBrowserDrop = false,
                WebBrowserShortcutsEnabled = true,
                IsWebBrowserContextMenuEnabled = false,
                ScrollBarsEnabled = ResolveAxisPolicy(session.HorizontalScrollPolicy, session.ScrollPolicy, horizontal: true) != PluginWindowAxisScrollPolicy.Hidden
                    || ResolveAxisPolicy(session.VerticalScrollPolicy, session.ScrollPolicy, horizontal: false) != PluginWindowAxisScrollPolicy.Hidden,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                MinimumSize = System.Drawing.Size.Empty,
                BackColor = System.Drawing.SystemColors.Window
            };
            Controls.Add(_browser);
            _browser.Navigating += (_, _) => FitBrowserToClient();

            _browser.DocumentCompleted += (_, _) =>
            {
                FitBrowserToClient();
                ApplyScrollPolicyToDocument("document_completed");
                ApplyContentSizePolicy("document_completed");
                RestoreThemeScrollAfterRerender();
                RestoreRefreshScrollAfterRerender();
                PublishStateIfLoaded();
            };
            Shown += (_, _) => LogPlacementTrace(session, "shown_actual");
            Load += (_, _) =>
            {
                _loaded = true;
                EnsureContentOccupancy("load");
                Navigate(url, force: true);
                LogContentHostingContract(session, "load");
                if (_initialActivateRequested && _activationPolicy != PluginWindowActivationPolicy.Never) ForceForeground();
                PublishState();
            };
            Layout += (_, _) => EnsureContentOccupancy("layout");
            ClientSizeChanged += (_, _) => EnsureContentOccupancy("client_size_changed");
            Move += (_, _) => PublishStateIfLoaded();
            Resize += (_, _) =>
            {
                EnforceResizeAxisContract("resize");
                if (_loaded && !_applyingSizeContract && MouseButtons != MouseButtons.None)
                    _userResized = true;
                FitBrowserToClient();
                HandleWindowStateChanged("resize");
                PublishStateIfLoaded();
            };
            SizeChanged += (_, _) => FitBrowserToClient();
            FormClosing += (_, e) =>
            {
                if (!_explicitDispose && _closeBehavior is PluginWindowCloseBehavior.Hide or PluginWindowCloseBehavior.PreserveSession)
                {
                    e.Cancel = true;
                    Hide();
                    PublishState(hostAliveOverride: true);
                    _log.Add("PLUGIN_TOOL_WINDOW_LIFETIME", _pluginName, $"result=HIDDEN_BY_HOST_X windowId={_windowId} closeBehavior={_closeBehavior} backgroundExecution={_backgroundExecution} statePersistence={_statePersistence} hostAlive=True rule=runtime_descriptor_window_lifetime_contract");
                    return;
                }

                var finalState = Snapshot(hostAliveOverride: false);
                var closeSource = !_explicitDispose && e.CloseReason == CloseReason.UserClosing
                    ? "host_x_button"
                    : _explicitDispose
                        ? "host_close_request"
                        : "host_window_close";
                _onClosing(finalState, closeSource, _closeBehavior, _backgroundExecution);
                PublishState(hostAliveOverride: false);
            };
        }

        public void RequestClose()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(RequestClose));
                return;
            }
            if (_closeBehavior is PluginWindowCloseBehavior.Hide or PluginWindowCloseBehavior.PreserveSession)
            {
                Hide();
                PublishState(hostAliveOverride: true);
                _log.Add("PLUGIN_TOOL_WINDOW_LIFETIME", _pluginName, $"result=HIDDEN windowId={_windowId} closeBehavior={_closeBehavior} backgroundExecution={_backgroundExecution} statePersistence={_statePersistence} hostAlive=True rule=runtime_descriptor_window_lifetime_contract");
                return;
            }
            _explicitDispose = true;
            Close();
        }

        public void ShowAndNavigate(string url, PluginWindowSession session, PluginToolWindowIconSpec? iconSpec, bool activate)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowAndNavigate(url, session, iconSpec, activate)));
                return;
            }
            _activationPolicy = session.ActivationPolicy;
            _closeBehavior = session.CloseBehavior;
            _backgroundExecution = session.BackgroundExecution;
            _statePersistence = session.StatePersistence;
            var activateRequested = activate && _activationPolicy != PluginWindowActivationPolicy.Never;
            var minimizedBefore = WindowState == FormWindowState.Minimized && IsActuallyMinimized();
            // A non-activating reuse/content update must preserve the user's minimized state.
            // Restoring a minimized ToolWindow changes the Win32 foreground window and can close
            // unrelated host UI such as the NotifyIcon ContextMenuStrip via AppFocusChange.
            // Only an explicit activation request may restore and foreground an existing window.
            ApplySession(session, restoreFromMinimized: activateRequested);
            ApplyIconWithAudit(iconSpec);
            if (!Visible)
            {
                if (activateRequested) Show();
                else ShowWindowWithoutActivation();
            }
            if (activateRequested)
                ForceForeground();
            else if (minimizedBefore)
            {
                _log.Add("PLUGIN_TOOL_WINDOW_ACTIVATION",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=PRESERVED_MINIMIZED windowId={SafeLogValue(_windowId)} activationRequested=False minimizedBefore=True minimizedAfter={(WindowState == FormWindowState.Minimized && IsActuallyMinimized())} foregroundChange=none rule=runtime_descriptor_window_activation_contract");
            }
            Navigate(url, force: false);
            PublishState();
        }

        public void NavigateOnly(string url, PluginWindowSession session)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => NavigateOnly(url, session)));
                return;
            }

            ApplySession(session, restoreFromMinimized: false);
            Navigate(url, force: true);
            PublishState();
        }

        public void RefreshExistingContent(string url, PluginWindowSession session, bool directContent)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => RefreshExistingContent(url, session, directContent)));
                return;
            }

            var preserveInteraction = session.PreserveInteractionState && session.PreserveScroll;
            ApplySession(session, restoreFromMinimized: false);

            if (!directContent)
            {
                // shell+iframe mode: outer document/Windowは維持し、revision pollだけを即時wakeする。
                // poll側がiframeのscrollを保存してcontentだけ再読込する。
                var wakeResult = "poll_timer_fallback";
                try
                {
                    if (_browser.Document is not null)
                    {
                        _browser.Document.InvokeScript("__tvairRefreshWindowState");
                        wakeResult = "script_wake";
                    }
                }
                catch
                {
                    // shell自身の1秒pollが正本fallback。outer shell navigationには戻さない。
                }
                _log.Add("PLUGIN_TOOL_WINDOW_INTERACTION_STATE",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=PRESERVED mode=shell_iframe windowId={SafeLogValue(_windowId)} revision={session.Revision} preserveInteractionState={session.PreserveInteractionState} preserveScroll={session.PreserveScroll} outerNavigation=False refreshWake={SafeLogValue(wakeResult)} activation=False focus=False rule=runtime_tool_window_interaction_state_contract");
                PublishState();
                return;
            }

            // direct-content fallbackにはiframe pollが無い。実documentのviewportだけHostで保存する。
            if (preserveInteraction)
                CaptureRefreshScroll(session.Revision);
            else
                ClearPendingRefreshScroll();

            Navigate(url, force: true);
            _log.Add("PLUGIN_TOOL_WINDOW_INTERACTION_STATE",
                string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                $"result={(preserveInteraction ? "PRESERVE_QUEUED" : "REFRESH_WITHOUT_PRESERVE")} mode=direct_content windowId={SafeLogValue(_windowId)} revision={session.Revision} preserveInteractionState={session.PreserveInteractionState} preserveScroll={session.PreserveScroll} outerNavigation=True activation=False focus=False rule=runtime_tool_window_interaction_state_contract");
            PublishState();
        }

        private void CaptureRefreshScroll(long revision)
        {
            try
            {
                var root = _browser.Document?.GetElementsByTagName("html").Cast<HtmlElement>().FirstOrDefault();
                var body = _browser.Document?.Body;
                _pendingRefreshScrollLeft = body?.ScrollLeft ?? root?.ScrollLeft ?? 0;
                _pendingRefreshScrollTop = body?.ScrollTop ?? root?.ScrollTop ?? 0;
                _pendingRefreshRevision = revision;
            }
            catch
            {
                ClearPendingRefreshScroll();
            }
        }

        private void RestoreRefreshScrollAfterRerender()
        {
            if (!_pendingRefreshRevision.HasValue || _browser.Document is null) return;

            var revision = _pendingRefreshRevision.Value;
            var left = _pendingRefreshScrollLeft ?? 0;
            var top = _pendingRefreshScrollTop ?? 0;
            try
            {
                _browser.Document.Window?.ScrollTo(left, top);
                _log.Add("PLUGIN_TOOL_WINDOW_INTERACTION_STATE",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=RESTORED mode=direct_content windowId={SafeLogValue(_windowId)} revision={revision} scroll={left},{top} activation=False focus=False rule=runtime_tool_window_interaction_state_contract");
            }
            catch { }
            finally
            {
                ClearPendingRefreshScroll();
            }
        }

        private void ClearPendingRefreshScroll()
        {
            _pendingRefreshScrollLeft = null;
            _pendingRefreshScrollTop = null;
            _pendingRefreshRevision = null;
        }

        public void RefreshThemeSilently(long themeRevision, string selectedTheme, string effectiveTheme)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => RefreshThemeSilently(themeRevision, selectedTheme, effectiveTheme)));
                return;
            }

            try
            {
                if (_browser.Document is null)
                    throw new InvalidOperationException("browser_document_unavailable");
                if (string.IsNullOrWhiteSpace(_currentRequestedUrl))
                    throw new InvalidOperationException("theme_content_url_unavailable");

                var root = _browser.Document.GetElementsByTagName("html").Cast<HtmlElement>().FirstOrDefault();
                var body = _browser.Document.Body;
                _pendingThemeScrollLeft = body?.ScrollLeft ?? root?.ScrollLeft ?? 0;
                _pendingThemeScrollTop = body?.ScrollTop ?? root?.ScrollTop ?? 0;
                _pendingThemeRevision = themeRevision;
                _pendingThemeSelected = selectedTheme;
                _pendingThemeEffective = effectiveTheme;

                // Runtime plugin HTML may resolve Host.Theme values into rendered CSS.
                // Root attributes alone cannot update those resolved values. Re-render the current
                // content document through the same generic Runtime UI route while preserving
                // window activation, placement, size and scroll state.
                Navigate(_currentRequestedUrl, force: true);

                _log.Add("PLUGIN_TOOL_WINDOW_THEME_REFRESH",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=RERENDER_QUEUED revision={themeRevision} windowId={SafeLogValue(_windowId)} mode=runtime_content_rerender selected={SafeLogValue(selectedTheme)} effective={SafeLogValue(effectiveTheme)} documentReload=True scrollPreserve=True activation=False focus=False positionChange=False sizeChange=False minimizedRestore=False rule=release_contract");
            }
            catch (Exception ex)
            {
                ClearPendingThemeRerender();
                _log.Add("PLUGIN_TOOL_WINDOW_THEME_REFRESH",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=FAILED revision={themeRevision} windowId={SafeLogValue(_windowId)} mode=runtime_content_rerender reason={SafeLogValue(ex.Message)} exception={SafeLogValue(ex.GetType().Name)} rule=release_contract");
            }
        }

        private void RestoreThemeScrollAfterRerender()
        {
            if (!_pendingThemeRevision.HasValue || _browser.Document is null) return;

            var revision = _pendingThemeRevision.Value;
            var selected = _pendingThemeSelected;
            var effective = _pendingThemeEffective;
            var left = _pendingThemeScrollLeft ?? 0;
            var top = _pendingThemeScrollTop ?? 0;
            try
            {
                _browser.Document.Window?.ScrollTo(left, top);
                _log.Add("PLUGIN_TOOL_WINDOW_THEME_REFRESH",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=APPLIED revision={revision} windowId={SafeLogValue(_windowId)} mode=runtime_content_rerender selected={SafeLogValue(selected)} effective={SafeLogValue(effective)} documentReload=True scrollRestored=True activation=False focus=False positionChange=False sizeChange=False minimizedRestore=False rule=release_contract");
            }
            catch (Exception ex)
            {
                _log.Add("PLUGIN_TOOL_WINDOW_THEME_REFRESH",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=APPLIED_WITH_SCROLL_WARNING revision={revision} windowId={SafeLogValue(_windowId)} mode=runtime_content_rerender selected={SafeLogValue(selected)} effective={SafeLogValue(effective)} documentReload=True scrollRestored=False reason={SafeLogValue(ex.Message)} exception={SafeLogValue(ex.GetType().Name)} activation=False focus=False positionChange=False sizeChange=False minimizedRestore=False rule=release_contract");
            }
            finally
            {
                ClearPendingThemeRerender();
            }
        }

        private void ClearPendingThemeRerender()
        {
            _pendingThemeScrollLeft = null;
            _pendingThemeScrollTop = null;
            _pendingThemeRevision = null;
            _pendingThemeSelected = string.Empty;
            _pendingThemeEffective = string.Empty;
        }



        private void ShowWindowWithoutActivation()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ShowWindowWithoutActivation));
                return;
            }
            if (!IsHandleCreated) CreateControl();
            ShowWindow(Handle, SwShowNoActivate);
        }

        private const int SwShowNoActivate = 4;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private void ForceForeground()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ForceForeground));
                return;
            }
            EnsureNotMinimized("force_foreground");
            Show();
            Activate();
            BringToFront();
            Focus();
            if (!TopMost)
            {
                TopMost = true;
                TopMost = false;
            }
        }

        public PluginToolWindowStatePatchHostResult ApplyStatePatchWithAudit(IReadOnlyList<RuntimeUiPatch>? patches, long stateRevision)
        {
            if (InvokeRequired)
            {
                try
                {
                    PluginToolWindowStatePatchHostResult? result = null;
                    Invoke(new Action(() => result = ApplyStatePatchWithAudit(patches, stateRevision)));
                    return result ?? PluginToolWindowStatePatchHostResult.Failed(_windowId, patches?.Count ?? 0, stateRevision, "invoke_no_result");
                }
                catch (Exception ex)
                {
                    return PluginToolWindowStatePatchHostResult.Failed(_windowId, patches?.Count ?? 0, stateRevision, "invoke_exception_" + ex.GetType().Name);
                }
            }

            var requestedCount = patches?.Count ?? 0;
            if (IsDisposed || Disposing || !IsHandleCreated)
                return PluginToolWindowStatePatchHostResult.CreateSkipped(_windowId, requestedCount, stateRevision, "window_closed");
            if (stateRevision <= 0)
                return PluginToolWindowStatePatchHostResult.Failed(_windowId, requestedCount, stateRevision, "invalid_state_revision");
            if (stateRevision < _latestAcceptedStatePatchRevision
                || stateRevision <= _lastAppliedStatePatchRevision)
                return PluginToolWindowStatePatchHostResult.CreateSkipped(_windowId, requestedCount, stateRevision, "stale_revision");

            // Accept the newest authoritative generation before touching the document.
            // If the DOM is temporarily unavailable, an older callback must never be able to
            // overtake this generation. The same revision remains retryable until actually applied.
            if (stateRevision > _latestAcceptedStatePatchRevision)
                _latestAcceptedStatePatchRevision = stateRevision;

            var normalized = RuntimeUiPatchContract.Normalize(patches);
            if (normalized.Count == 0)
            {
                _lastAppliedStatePatchRevision = stateRevision;
                return PluginToolWindowStatePatchHostResult.CreateApplied(_windowId, requestedCount, 0, stateRevision, "no_valid_patches");
            }

            var document = _browser.Document;
            if (document is null || document.Body is null)
                return PluginToolWindowStatePatchHostResult.CreateSkipped(_windowId, requestedCount, stateRevision, "document_not_ready");

            try
            {
                var json = JsonSerializer.Serialize(normalized);
                var invokeResult = document.InvokeScript("tvairApplyUiPatchesJson", new object[] { json });
                if (invokeResult is null)
                    return PluginToolWindowStatePatchHostResult.CreateSkipped(_windowId, requestedCount, stateRevision, "statepatch_bridge_unavailable");
                var appliedCount = Convert.ToInt32(invokeResult, System.Globalization.CultureInfo.InvariantCulture);
                if (appliedCount < 0)
                    return PluginToolWindowStatePatchHostResult.Failed(_windowId, requestedCount, stateRevision, "statepatch_bridge_parse_failed");

                _lastAppliedStatePatchRevision = stateRevision;
                if (appliedCount == 0)
                    return PluginToolWindowStatePatchHostResult.CreateSkipped(_windowId, requestedCount, stateRevision, "target_not_found");
                return PluginToolWindowStatePatchHostResult.CreateApplied(_windowId, requestedCount, Math.Min(appliedCount, normalized.Count), stateRevision, "host_statepatch_applied");
            }
            catch (Exception ex)
            {
                return PluginToolWindowStatePatchHostResult.Failed(_windowId, requestedCount, stateRevision, "statepatch_exception_" + ex.GetType().Name);
            }
        }

        public PluginToolWindowApplyResult ApplySessionWithAudit(PluginWindowSession session)
        {
            if (InvokeRequired)
            {
                try
                {
                    PluginToolWindowApplyResult? result = null;
                    Invoke(new Action(() => result = ApplySessionWithAudit(session)));
                    return result ?? PluginToolWindowApplyResult.Failed(_windowId, "invoke_no_result");
                }
                catch (Exception ex)
                {
                    return PluginToolWindowApplyResult.Failed(_windowId, "invoke_exception_" + ex.GetType().Name);
                }
            }

            var before = BuildSnapshot();
            // Background/runtime session updates are content and policy updates only.
            // They must not restore a minimized window or replace the running placement.
            ApplySession(session, restoreFromMinimized: false);
            PublishState();
            var after = BuildSnapshot();
            return PluginToolWindowApplyResult.FromStates(_windowId, before, after);
        }


        public PluginToolWindowIconApplyResult ApplyIconWithAudit(PluginToolWindowIconSpec? iconSpec)
        {
            if (InvokeRequired)
            {
                try
                {
                    PluginToolWindowIconApplyResult? result = null;
                    Invoke(new Action(() => result = ApplyIconWithAudit(iconSpec)));
                    return result ?? new PluginToolWindowIconApplyResult(iconSpec?.Source ?? "default", false, "invoke_no_result");
                }
                catch (Exception ex)
                {
                    return new PluginToolWindowIconApplyResult(iconSpec?.Source ?? "default", false, "invoke_exception_" + ex.GetType().Name);
                }
            }
            if (iconSpec is null) return new PluginToolWindowIconApplyResult("default", false, "no_icon_spec");
            try
            {
                Icon? nextIcon = null;
                if (iconSpec.IconBytes is { Length: > 0 })
                {
                    using var ms = new MemoryStream(iconSpec.IconBytes);
                    using var loaded = new Icon(ms);
                    nextIcon = (Icon)loaded.Clone();
                }
                else if (!string.IsNullOrWhiteSpace(iconSpec.FilePath) && File.Exists(iconSpec.FilePath))
                {
                    using var loaded = new Icon(iconSpec.FilePath);
                    nextIcon = (Icon)loaded.Clone();
                }
                if (nextIcon is null) return new PluginToolWindowIconApplyResult(iconSpec.Source, false, "icon_not_found_or_empty");
                var old = _ownedIcon;
                _ownedIcon = nextIcon;
                Icon = nextIcon;
                try { old?.Dispose(); } catch { }
                return new PluginToolWindowIconApplyResult(iconSpec.Source, true, iconSpec.Diagnostics);
            }
            catch (Exception ex)
            {
                return new PluginToolWindowIconApplyResult(iconSpec.Source, false, "icon_apply_exception_" + ex.GetType().Name);
            }
        }

        public PluginWindowHostState Snapshot(bool? hostAliveOverride = null)
        {
            if (InvokeRequired)
            {
                try
                {
                    PluginWindowHostState? result = null;
                    Invoke(new Action(() => result = Snapshot(hostAliveOverride)));
                    return result ?? BuildSnapshot(hostAliveOverride);
                }
                catch
                {
                    return BuildSnapshot(hostAliveOverride);
                }
            }
            return BuildSnapshot(hostAliveOverride);
        }

        private void ApplySession(PluginWindowSession session, bool restoreFromMinimized)
        {
            // The running Form owns the current placement. Descriptor defaults and saved placement
            // are creation-time inputs only. Existing-host session application may update content
            // and live policies, but it must not turn transient WinForms bounds into the new placement.
            if (restoreFromMinimized)
            {
                EnsureNotMinimized("apply_session_explicit_show");
            }

            var placementBefore = CaptureRuntimePlacement();
            Text = string.IsNullOrWhiteSpace(session.Title) ? Text : session.Title;
            if (!string.IsNullOrWhiteSpace(session.PluginName)) _pluginName = session.PluginName;
            _sizeReference = session.SizeReference;
            _contentSizePolicy = session.ContentSizePolicy;
            _closeBehavior = session.CloseBehavior;
            _backgroundExecution = session.BackgroundExecution;
            _statePersistence = session.StatePersistence;
            _activationPolicy = session.ActivationPolicy;

            // FormBorderStyle and MinimumSize are live policy properties, but assigning them can
            // synchronously raise Resize/SizeChanged and can alter Bounds while WinForms is handling
            // activation, visibility or non-client recalculation. Apply only actual policy changes and
            // suppress resize-contract feedback while the policy transaction is in progress.
            _applyingSizeContract = true;
            try
            {
                ApplyResizeModeIfChanged(session);
                var requestedMinimum = BuildMinimumSize(session);
                if (MinimumSize != requestedMinimum)
                    MinimumSize = requestedMinimum;
            }
            finally
            {
                _applyingSizeContract = false;
            }

            PreserveRuntimePlacement(placementBefore, "apply_session");
            _axisLockedOuterWidth = placementBefore.NormalBounds.Width;
            _axisLockedOuterHeight = placementBefore.NormalBounds.Height;

            try
            {
                var placementAfter = CaptureRuntimePlacement();
                _log.Add("PLUGIN_TOOL_WINDOW_SIZE_REFERENCE",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=PRESERVED source=apply_session sizeAction=PRESERVE_CURRENT windowId={SafeLogValue(_windowId)} contractReference={session.SizeReference} appliedReference={session.AppliedSizeReference} restoredPlacement={session.RestoredPlacement} currentOuterBefore={placementBefore.NormalBounds.Width}x{placementBefore.NormalBounds.Height} currentOuter={placementAfter.NormalBounds.Width}x{placementAfter.NormalBounds.Height} currentLocationBefore={placementBefore.NormalBounds.Left},{placementBefore.NormalBounds.Top} currentLocation={placementAfter.NormalBounds.Left},{placementAfter.NormalBounds.Top} windowStateBefore={placementBefore.EffectiveState} windowState={placementAfter.EffectiveState} descriptorSize={ResolveWindowDimension(session.Width)}x{ResolveWindowDimension(session.Height)} descriptorSizeApplied=False minimumSize={MinimumSize.Width}x{MinimumSize.Height} savedPlacementApplied=False rule=runtime_descriptor_content_size_contract");
            }
            catch { }

            TopMost = session.AlwaysOnTop;
            _scrollPolicy = session.ScrollPolicy;
            _horizontalScrollPolicy = session.HorizontalScrollPolicy;
            _verticalScrollPolicy = session.VerticalScrollPolicy;
            ApplyScrollPolicyToHost("apply_session");
            FitBrowserToClient();
        }

        private void ApplyResizeModeIfChanged(PluginWindowSession session)
        {
            var requestedMode = session.ResizeMode;
            var resizable = session.Resizable && requestedMode != PluginWindowResizeMode.Fixed;
            var requestedBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedSingle;
            var requestedMaximizeBox = resizable && requestedMode == PluginWindowResizeMode.Both;

            _resizeMode = requestedMode;
            if (FormBorderStyle != requestedBorderStyle)
                FormBorderStyle = requestedBorderStyle;
            if (MaximizeBox != requestedMaximizeBox)
                MaximizeBox = requestedMaximizeBox;
        }

        private RuntimePlacementSnapshot CaptureRuntimePlacement()
        {
            var rawState = WindowState;
            var actualMinimized = rawState == FormWindowState.Minimized && IsActuallyMinimized();
            var effectiveState = actualMinimized
                ? FormWindowState.Minimized
                : rawState == FormWindowState.Minimized ? FormWindowState.Normal : rawState;
            var normalBounds = effectiveState == FormWindowState.Normal
                ? Bounds
                : !RestoreBounds.IsEmpty ? RestoreBounds : Bounds;
            return new RuntimePlacementSnapshot(normalBounds, effectiveState, actualMinimized);
        }

        private void PreserveRuntimePlacement(RuntimePlacementSnapshot expected, string source)
        {
            var observed = CaptureRuntimePlacement();
            var boundsChanged = observed.NormalBounds != expected.NormalBounds;
            var stateChanged = observed.EffectiveState != expected.EffectiveState;
            if (!boundsChanged && !stateChanged) return;

            _applyingSizeContract = true;
            try
            {
                if (expected.EffectiveState == FormWindowState.Normal)
                {
                    if (WindowState != FormWindowState.Normal)
                        WindowState = FormWindowState.Normal;
                    Bounds = expected.NormalBounds;
                }
                else
                {
                    // RestoreBounds cannot be assigned directly. Restore the normal bounds first,
                    // then return to the pre-update maximized/minimized state without activation.
                    if (WindowState != FormWindowState.Normal)
                        WindowState = FormWindowState.Normal;
                    Bounds = expected.NormalBounds;
                    WindowState = expected.EffectiveState;
                }
            }
            finally
            {
                _applyingSizeContract = false;
            }

            var restored = CaptureRuntimePlacement();
            try
            {
                _log.Add("PLUGIN_TOOL_WINDOW_PLACEMENT_MUTATION",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"windowId={SafeLogValue(_windowId)} source={SafeLogValue(source)} reason=non_user_session_policy_side_effect_suppressed oldBounds={expected.NormalBounds.Left},{expected.NormalBounds.Top},{expected.NormalBounds.Width}x{expected.NormalBounds.Height} observedBounds={observed.NormalBounds.Left},{observed.NormalBounds.Top},{observed.NormalBounds.Width}x{observed.NormalBounds.Height} newBounds={restored.NormalBounds.Left},{restored.NormalBounds.Top},{restored.NormalBounds.Width}x{restored.NormalBounds.Height} oldWindowState={expected.EffectiveState} observedWindowState={observed.EffectiveState} newWindowState={restored.EffectiveState} userInitiated=False descriptorSizeApplied=False minimumSizeApplied=False savedPlacementApplied=False rule=runtime_tool_window_running_placement_contract");
            }
            catch { }
        }

        private readonly record struct RuntimePlacementSnapshot(
            System.Drawing.Rectangle NormalBounds,
            FormWindowState EffectiveState,
            bool IsActuallyMinimized);

        private void LogPlacementTrace(PluginWindowSession session, string phase)
        {
            try
            {
                var actual = Bounds;
                var screen = Screen.FromRectangle(actual);
                var area = screen.WorkingArea;
                _log.Add("PLUGIN_TOOL_WINDOW_PLACEMENT_TRACE",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"phase={SafeLogValue(phase)} windowId={SafeLogValue(_windowId)} windowDefinitionId={SafeLogValue(session.WindowDefinitionId)} sizeReference={session.SizeReference} requestedX={session.Left?.ToString() ?? "-"} requestedY={session.Top?.ToString() ?? "-"} requestedWidth={ResolveWindowDimension(session.Width)} requestedHeight={ResolveWindowDimension(session.Height)} appliedX={actual.Left} appliedY={actual.Top} appliedWidth={actual.Width} appliedHeight={actual.Height} actualX={actual.Left} actualY={actual.Top} actualWidth={actual.Width} actualHeight={actual.Height} workingArea={area.Left},{area.Top},{area.Width}x{area.Height} dpi={DeviceDpi} monitor={SafeLogValue(screen.DeviceName)} windowState={WindowState} result={(session.Left.HasValue && session.Top.HasValue && actual.Left == session.Left.Value && actual.Top == session.Top.Value && actual.Width == ResolveWindowDimension(session.Width) && actual.Height == ResolveWindowDimension(session.Height) ? "EXACT" : "OBSERVED")} rule=plugin_window_placement_roundtrip_contract");
            }
            catch { }
        }

        private void ApplyPositiveDimensionGuard(string source)
        {
            var oldWidth = Width;
            var oldHeight = Height;
            var minimum = MinimumSize;
            var newWidth = Math.Max(oldWidth, Math.Max(1, minimum.Width));
            var newHeight = Math.Max(oldHeight, Math.Max(1, minimum.Height));
            if (newWidth != oldWidth || newHeight != oldHeight)
            {
                Width = newWidth;
                Height = newHeight;
                try
                {
                    _log.Add("PLUGIN_TOOL_WINDOW_SIZE_GUARD",
                        string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                        $"result=APPLIED source={SafeLogValue(source)} windowId={SafeLogValue(_windowId)} oldSize={oldWidth}x{oldHeight} newSize={newWidth}x{newHeight} minimumSize={minimum.Width}x{minimum.Height} guard=runtime_descriptor_minimum rule=release_contract");
                }
                catch { }
            }
        }

        private System.Drawing.Size BuildMinimumSize(PluginWindowSession session)
        {
            var requested = new System.Drawing.Size(Math.Max(1, session.MinWidth), Math.Max(1, session.MinHeight));
            return session.SizeReference == PluginWindowSizeReference.ContentArea
                ? SizeFromClientSize(requested)
                : requested;
        }

        private void ApplyRequestedSize(PluginWindowSession session, string source)
        {
            var requested = new System.Drawing.Size(ResolveWindowDimension(session.Width), ResolveWindowDimension(session.Height));
            _applyingSizeContract = true;
            try
            {
                if (session.AppliedSizeReference == PluginWindowSizeReference.ContentArea)
                    ClientSize = requested;
                else
                    Size = requested;
                _axisLockedOuterWidth = Width;
                _axisLockedOuterHeight = Height;
            }
            finally
            {
                _applyingSizeContract = false;
            }
            try
            {
                _log.Add("PLUGIN_TOOL_WINDOW_SIZE_REFERENCE", string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=APPLIED source={SafeLogValue(source)} windowId={SafeLogValue(_windowId)} contractReference={session.SizeReference} appliedReference={session.AppliedSizeReference} restoredPlacement={session.RestoredPlacement} requested={requested.Width}x{requested.Height} outer={Width}x{Height} client={ClientSize.Width}x{ClientSize.Height} rule=runtime_descriptor_content_size_contract");
            }
            catch { }
        }

        private void ApplyResizeMode(PluginWindowSession session)
        {
            _resizeMode = session.ResizeMode;
            var resizable = session.Resizable && _resizeMode != PluginWindowResizeMode.Fixed;
            FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedSingle;
            MaximizeBox = resizable && _resizeMode == PluginWindowResizeMode.Both;
        }

        private void EnforceResizeAxisContract(string source)
        {
            if (_applyingSizeContract || WindowState != FormWindowState.Normal) return;
            var targetWidth = Width;
            var targetHeight = Height;
            switch (_resizeMode)
            {
                case PluginWindowResizeMode.Fixed:
                    targetWidth = _axisLockedOuterWidth;
                    targetHeight = _axisLockedOuterHeight;
                    break;
                case PluginWindowResizeMode.Vertical:
                    targetWidth = _axisLockedOuterWidth;
                    break;
                case PluginWindowResizeMode.Horizontal:
                    targetHeight = _axisLockedOuterHeight;
                    break;
            }
            if (targetWidth == Width && targetHeight == Height) return;
            _applyingSizeContract = true;
            try { Size = new System.Drawing.Size(Math.Max(MinimumSize.Width, targetWidth), Math.Max(MinimumSize.Height, targetHeight)); }
            finally { _applyingSizeContract = false; }
            try
            {
                _log.Add("PLUGIN_TOOL_WINDOW_RESIZE_MODE",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=CONSTRAINED source={SafeLogValue(source)} windowId={SafeLogValue(_windowId)} resizeMode={_resizeMode} outer={Width}x{Height} rule=runtime_descriptor_resize_mode_contract");
            }
            catch { }
        }

        private void ApplyContentSizePolicy(string source)
        {
            if (_contentSizePolicy == PluginWindowContentSizePolicy.Ignore || _resizeMode == PluginWindowResizeMode.Fixed) return;
            if (_contentSizePolicy == PluginWindowContentSizePolicy.InitialOnly && _initialContentFitApplied) return;
            if (_contentSizePolicy == PluginWindowContentSizePolicy.FitUntilUserResize && _userResized) return;
            var document = _browser.Document;
            var body = document?.Body;
            if (body is null) return;
            var root = document!.GetElementsByTagName("html").Cast<HtmlElement>().FirstOrDefault();
            var bodyScroll = body.ScrollRectangle;
            var rootScroll = root?.ScrollRectangle ?? System.Drawing.Rectangle.Empty;
            var desiredWidth = Math.Max(bodyScroll.Width, rootScroll.Width);
            var desiredHeight = Math.Max(bodyScroll.Height, rootScroll.Height);
            if (desiredWidth <= 0 || desiredHeight <= 0) return;

            var currentClient = ClientSize;
            var targetWidth = currentClient.Width;
            var targetHeight = currentClient.Height;
            var allowHorizontal = _resizeMode is PluginWindowResizeMode.Horizontal or PluginWindowResizeMode.Both;
            var allowVertical = _resizeMode is PluginWindowResizeMode.Vertical or PluginWindowResizeMode.Both;
            if (allowHorizontal)
                targetWidth = _contentSizePolicy == PluginWindowContentSizePolicy.GrowOnly
                    ? Math.Max(currentClient.Width, desiredWidth)
                    : desiredWidth;
            if (allowVertical)
                targetHeight = _contentSizePolicy == PluginWindowContentSizePolicy.GrowOnly
                    ? Math.Max(currentClient.Height, desiredHeight)
                    : desiredHeight;

            var working = Screen.FromControl(this).WorkingArea;
            targetWidth = Math.Min(Math.Max(1, targetWidth), Math.Max(1, working.Width));
            targetHeight = Math.Min(Math.Max(1, targetHeight), Math.Max(1, working.Height));
            if (targetWidth == currentClient.Width && targetHeight == currentClient.Height)
            {
                _initialContentFitApplied = true;
                return;
            }

            _applyingSizeContract = true;
            try
            {
                ClientSize = new System.Drawing.Size(targetWidth, targetHeight);
                _axisLockedOuterWidth = Width;
                _axisLockedOuterHeight = Height;
                _initialContentFitApplied = true;
            }
            finally
            {
                _applyingSizeContract = false;
            }
            try
            {
                _log.Add("PLUGIN_TOOL_WINDOW_CONTENT_SIZE_POLICY",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=APPLIED source={SafeLogValue(source)} windowId={SafeLogValue(_windowId)} policy={_contentSizePolicy} resizeMode={_resizeMode} desiredClient={desiredWidth}x{desiredHeight} appliedClient={ClientSize.Width}x{ClientSize.Height} outer={Width}x{Height} userResized={_userResized} rule=runtime_descriptor_content_size_policy_contract");
            }
            catch { }
        }

        private static int ResolveWindowDimension(int value)
            => value > 0 ? value : 1;

        private void EnsureNotMinimized(string source)
        {
            if (WindowState != FormWindowState.Minimized || !IsActuallyMinimized()) return;
            var old = FormWindowState.Minimized;
            WindowState = FormWindowState.Normal;
            LogWindowStateChange(old, WindowState, source, statePersisted: false, actualMinimized: false);
            _lastObservedWindowState = WindowState;
            _lastObservedIsIconic = false;
        }

        private void HandleWindowStateChanged(string source)
        {
            var rawState = WindowState;
            var actualMinimized = rawState == FormWindowState.Minimized && IsActuallyMinimized();
            if (rawState == FormWindowState.Minimized && !actualMinimized)
            {
                LogWindowStateGuard(source, rawState, "winforms_minimized_without_isiconic");
                if (_lastObservedWindowState == FormWindowState.Minimized)
                {
                    _lastObservedWindowState = FormWindowState.Normal;
                    _lastObservedIsIconic = false;
                }
                return;
            }

            var current = actualMinimized ? FormWindowState.Minimized : rawState;
            if (current == _lastObservedWindowState && actualMinimized == _lastObservedIsIconic) return;

            var old = _lastObservedWindowState;
            _lastObservedWindowState = current;
            _lastObservedIsIconic = actualMinimized;
            var persist = current != FormWindowState.Minimized;
            var auditSource = source;
            if (string.Equals(source, "resize", StringComparison.OrdinalIgnoreCase))
            {
                auditSource = current == FormWindowState.Minimized
                    ? "window_state_minimized"
                    : old == FormWindowState.Minimized ? "window_state_restored" : source;
            }
            LogWindowStateChange(old, current, auditSource, persist, actualMinimized);
        }

        private bool IsActuallyMinimized()
        {
            try
            {
                return IsHandleCreated && NativeMethods.IsIconic(Handle);
            }
            catch
            {
                return WindowState == FormWindowState.Minimized;
            }
        }

        private void LogWindowStateGuard(string source, FormWindowState rawState, string reason)
        {
            try
            {
                _log.Add("PLUGIN_TOOL_WINDOW_STATE_GUARD",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=IGNORED source={SafeLogValue(source)} rawState={rawState} effectiveState={_lastObservedWindowState} windowId={SafeLogValue(_windowId)} reason={SafeLogValue(reason)} bounds={Bounds.Left},{Bounds.Top},{Bounds.Width}x{Bounds.Height} showInTaskbar={ShowInTaskbar} topMost={TopMost} rule=release_contract");
            }
            catch { }
        }

        private void LogWindowStateChange(FormWindowState oldState, FormWindowState newState, string source, bool statePersisted, bool actualMinimized)
        {
            try
            {
                var b = actualMinimized && !RestoreBounds.IsEmpty ? RestoreBounds : Bounds;
                _log.Add("PLUGIN_TOOL_WINDOW_STATE",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"oldState={oldState} newState={newState} source={SafeLogValue(source)} windowId={SafeLogValue(_windowId)} bounds={b.Left},{b.Top},{b.Width}x{b.Height} showInTaskbar={ShowInTaskbar} topMost={TopMost} actualMinimized={actualMinimized} statePersisted={statePersisted} minimizedPersistenceSuppressed={actualMinimized} rule=release_contract");
            }
            catch { }
        }

        private void ApplyScrollPolicyToHost(string source)
        {
            try
            {
                var horizontal = ResolveAxisPolicy(_horizontalScrollPolicy, _scrollPolicy, horizontal: true);
                var vertical = ResolveAxisPolicy(_verticalScrollPolicy, _scrollPolicy, horizontal: false);
                _browser.ScrollBarsEnabled = horizontal != PluginWindowAxisScrollPolicy.Hidden || vertical != PluginWindowAxisScrollPolicy.Hidden;
                ApplyScrollPolicyToDocument(source);
            }
            catch { }
        }

        private void ApplyScrollPolicyToDocument(string source)
        {
            try
            {
                var document = _browser.Document;
                if (document is null) return;
                var root = document.GetElementsByTagName("html").Cast<HtmlElement>().FirstOrDefault();
                var body = document.Body;
                var horizontal = ResolveAxisPolicy(_horizontalScrollPolicy, _scrollPolicy, horizontal: true);
                var vertical = ResolveAxisPolicy(_verticalScrollPolicy, _scrollPolicy, horizontal: false);
                var overflowX = ToOverflow(horizontal);
                var overflowY = ToOverflow(vertical);
                ApplyOverflow(root, overflowX, overflowY);
                ApplyOverflow(body, overflowX, overflowY);
                _log.Add("PLUGIN_TOOL_WINDOW_SCROLL_POLICY",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=APPLIED source={SafeLogValue(source)} windowId={SafeLogValue(_windowId)} legacyPolicy={_scrollPolicy} horizontalPolicy={horizontal} verticalPolicy={vertical} hostScrollBarsEnabled={_browser.ScrollBarsEnabled} overflowX={SafeLogValue(overflowX)} overflowY={SafeLogValue(overflowY)} hostKind={SafeLogValue(_hostKind)} rule=runtime_descriptor_scroll_policy_contract");
            }
            catch (Exception ex)
            {
                try
                {
                    _log.Add("PLUGIN_TOOL_WINDOW_SCROLL_POLICY",
                        string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                        $"result=FAILED source={SafeLogValue(source)} windowId={SafeLogValue(_windowId)} policy={_scrollPolicy} exception={SafeLogValue(ex.GetType().Name)} rule=runtime_descriptor_scroll_policy_contract");
                }
                catch { }
            }
        }


        private static PluginWindowAxisScrollPolicy ResolveAxisPolicy(PluginWindowAxisScrollPolicy axis, PluginWindowScrollPolicy legacy, bool horizontal)
        {
            if (axis != PluginWindowAxisScrollPolicy.Auto) return axis;
            return legacy switch
            {
                PluginWindowScrollPolicy.Hidden => PluginWindowAxisScrollPolicy.Hidden,
                PluginWindowScrollPolicy.Vertical => horizontal ? PluginWindowAxisScrollPolicy.Hidden : PluginWindowAxisScrollPolicy.Visible,
                PluginWindowScrollPolicy.Horizontal => horizontal ? PluginWindowAxisScrollPolicy.Visible : PluginWindowAxisScrollPolicy.Hidden,
                PluginWindowScrollPolicy.Both => PluginWindowAxisScrollPolicy.Visible,
                _ => PluginWindowAxisScrollPolicy.Auto
            };
        }

        private static string ToOverflow(PluginWindowAxisScrollPolicy policy)
            => policy switch
            {
                PluginWindowAxisScrollPolicy.Hidden => "hidden",
                PluginWindowAxisScrollPolicy.Visible => "auto",
                _ => string.Empty
            };

        private static void ApplyOverflow(HtmlElement? element, string overflowX, string overflowY)
        {
            if (element is null) return;
            if (string.IsNullOrEmpty(overflowX) && string.IsNullOrEmpty(overflowY))
            {
                element.Style = RemoveStyleProperty(RemoveStyleProperty(element.Style, "overflow-x"), "overflow-y");
                return;
            }
            var style = RemoveStyleProperty(RemoveStyleProperty(element.Style, "overflow-x"), "overflow-y");
            element.Style = $"{style};overflow-x:{overflowX};overflow-y:{overflowY};";
        }

        private static string RemoveStyleProperty(string? style, string property)
        {
            if (string.IsNullOrWhiteSpace(style)) return string.Empty;
            return string.Join(";", style.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.TrimStart().StartsWith(property + ":", StringComparison.OrdinalIgnoreCase)));
        }

        private void FitBrowserToClient() => EnsureContentOccupancy("fit_browser");

        private void EnsureContentOccupancy(string source)
        {
            try
            {
                Padding = Padding.Empty;
                Margin = Padding.Empty;
                _browser.Dock = DockStyle.Fill;
                _browser.Margin = Padding.Empty;
                _browser.Padding = Padding.Empty;
                _browser.Bounds = ClientRectangle;
            }
            catch (Exception ex)
            {
                try
                {
                    _log.Add("PLUGIN_TOOL_WINDOW_CONTENT_HOST", string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                        $"result=FAILED source={SafeLogValue(source)} windowId={SafeLogValue(_windowId)} reason={SafeLogValue(ex.GetType().Name)} rule=runtime_tool_window_content_occupancy_contract");
                }
                catch { }
            }
        }

        private void LogContentHostingContract(PluginWindowSession session, string source)
        {
            try
            {
                var formClient = ClientRectangle;
                var content = _browser.Bounds;
                _log.Add("PLUGIN_TOOL_WINDOW_CONTENT_HOST", string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result=OK source={SafeLogValue(source)} windowId={SafeLogValue(_windowId)} formClientBounds={formClient.Left},{formClient.Top},{formClient.Width}x{formClient.Height} contentControlBounds={content.Left},{content.Top},{content.Width}x{content.Height} contentControlDock={_browser.Dock} contentControlMargin={_browser.Margin.Left},{_browser.Margin.Top},{_browser.Margin.Right},{_browser.Margin.Bottom} contentControlPadding={_browser.Padding.Left},{_browser.Padding.Top},{_browser.Padding.Right},{_browser.Padding.Bottom} descriptorSizeReference={session.SizeReference} savedPlacementSizeReference={session.SavedPlacementSizeReference?.ToString() ?? "-"} appliedSizeReference={session.AppliedSizeReference} hostKind={SafeLogValue(_hostKind)} directContent={(_currentRequestedUrl.Contains("__tvairToolHostContent=1", StringComparison.OrdinalIgnoreCase) ? "True" : "Pending")} rule=runtime_tool_window_content_occupancy_contract");
            }
            catch { }
        }

        private void Navigate(string url, bool force)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
                var requestedUrl = uri.AbsoluteUri;
                if (!force && string.Equals(_currentRequestedUrl, requestedUrl, StringComparison.OrdinalIgnoreCase))
                    return;
                _currentRequestedUrl = requestedUrl;
                _browser.Navigate(uri);
            }
            catch { }
        }

        private void PublishStateIfLoaded()
        {
            if (_loaded) PublishState();
        }

        private void PublishState(bool? hostAliveOverride = null)
        {
            try { _onStateChanged(BuildSnapshot(hostAliveOverride)); } catch { }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            internal static extern bool IsIconic(IntPtr hWnd);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _ownedIcon?.Dispose(); } catch { }
                _ownedIcon = null;
            }
            base.Dispose(disposing);
        }

        private PluginWindowHostState BuildSnapshot(bool? hostAliveOverride = null)
        {
            var rawState = WindowState;
            var minimized = rawState == FormWindowState.Minimized && IsActuallyMinimized();
            var state = minimized ? FormWindowState.Minimized : rawState == FormWindowState.Minimized ? FormWindowState.Normal : rawState;
            var b = minimized && !RestoreBounds.IsEmpty ? RestoreBounds : Bounds;
            return new(
                _windowId,
                hostAliveOverride ?? !IsDisposed,
                Math.Max(0, b.Width),
                Math.Max(0, b.Height),
                b.Left,
                b.Top,
                TopMost,
                _hostKind,
                _webView2RuntimeAvailable,
                state.ToString(),
                minimized,
                Math.Max(0, ClientSize.Width),
                Math.Max(0, ClientSize.Height));
        }
        private static string SafeLogValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "-";
            var s = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length > 160) s = s[..160] + "…";
            return s;
        }

    }
}

public sealed record PluginToolWindowStatePatchHostResult(
    bool HostAccepted,
    bool Applied,
    bool Skipped,
    string WindowId,
    int RequestedPatchCount,
    int AppliedPatchCount,
    long StateRevision,
    string Diagnostics)
{
    public static PluginToolWindowStatePatchHostResult CreateApplied(string? windowId, int requested, int applied, long revision, string diagnostics)
        => new(true, true, false, windowId ?? string.Empty, requested, applied, revision, diagnostics);

    public static PluginToolWindowStatePatchHostResult CreateSkipped(string? windowId, int requested, long revision, string diagnostics)
        => new(true, false, true, windowId ?? string.Empty, requested, 0, revision, diagnostics);

    public static PluginToolWindowStatePatchHostResult Failed(string? windowId, int requested, long revision, string diagnostics)
        => new(true, false, false, windowId ?? string.Empty, requested, 0, revision, diagnostics);
}

public sealed record PluginToolWindowApplyResult(
    bool HostAccepted,
    bool Applied,
    string WindowId,
    bool? BeforeTopMost,
    bool? AfterTopMost,
    int? BeforeWidth,
    int? BeforeHeight,
    int? AfterWidth,
    int? AfterHeight,
    string Diagnostics)
{
    public static PluginToolWindowApplyResult Pending(string? windowId)
        => new(true, false, windowId ?? string.Empty, null, null, null, null, null, null, "accepted_pending");

    public static PluginToolWindowApplyResult NotFound(string? windowId)
        => new(false, false, windowId ?? string.Empty, null, null, null, null, null, null, "host_window_not_found");

    public static PluginToolWindowApplyResult Timeout(string? windowId)
        => new(true, false, windowId ?? string.Empty, null, null, null, null, null, null, "host_apply_timeout");

    public static PluginToolWindowApplyResult Failed(string? windowId, string diagnostics)
        => new(true, false, windowId ?? string.Empty, null, null, null, null, null, null, diagnostics);

    public static PluginToolWindowApplyResult FromStates(string windowId, PluginWindowHostState before, PluginWindowHostState after)
        => new(true, true, windowId, before.AlwaysOnTop, after.AlwaysOnTop, before.Width, before.Height, after.Width, after.Height, "host_form_applied");
}

public sealed record PluginToolWindowIconSpec(string ManifestIcon, string Source, string Diagnostics, byte[]? IconBytes, string? FilePath);

public sealed record PluginToolWindowIconApplyResult(string Source, bool Applied, string Diagnostics);

public sealed record PluginToolWindowOpenResult(
    string Result,
    string HostKind,
    bool WebView2RuntimeAvailable,
    bool Reused,
    bool Activated,
    bool StateRestored,
    string Diagnostics,
    string IconSource,
    bool IconApplied,
    string IconDiagnostics);

public sealed record PluginToolWindowHostCapabilities(
    bool ToolWindowSupported,
    bool HostWindowSupported,
    bool WebView2RuntimeAvailable,
    string HostKind,
    string FallbackHostKind,
    bool FallbackToBrowserRedirectSupported,
    bool JsonScreenSuppressed,
    bool SupportsAlwaysOnTop,
    bool SupportsSize,
    bool SupportsMinSize,
    bool SupportsPositionPersistence,
    bool SupportsStatePersistence,
    bool SupportsReuseExisting,
    bool SupportsActivateExisting,
    string ReuseKey,
    string RefreshTarget,
    string RefreshReloadScope,
    bool ScriptExecutionAllowed,
    bool SupportsManifestFormIcon,
    string FormIconSourcePriority,
    string ContractVersion);
