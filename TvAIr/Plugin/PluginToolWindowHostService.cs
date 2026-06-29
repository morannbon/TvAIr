namespace TvAIr.Plugin;

using System.Collections.Concurrent;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TvAIr.Core;

/// <summary>
/// release_contract: TvAIr本体管理Plugin Tool Window direct content表示修正。
/// pluginId+routeSegment reuse、host close同期、状態保存、alwaysOnTop/size反映、JSON画面抑止fallbackを同じ境界へ集約する。
/// </summary>
public sealed class PluginToolWindowHostService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HostedWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly PluginWindowSessionStore _sessions;
    private readonly LogRepository _log;

    public PluginToolWindowHostService(PluginWindowSessionStore sessions, LogRepository log)
    {
        _sessions = sessions;
        _log = log;
        TryEnsureWebBrowserFeatureControl();
    }

    public PluginToolWindowOpenResult OpenOrActivate(PluginWindowSession session, string absoluteUrl, PluginToolWindowIconSpec? iconSpec = null)
    {
        if (session is null) return new PluginToolWindowOpenResult("FAILED", "none", IsWebView2RuntimeAvailable(), false, false, false, "missing_session", iconSpec?.Source ?? "none", false, iconSpec?.Diagnostics ?? "missing_session");
        var windowId = session.WindowId;
        lock (_gate)
        {
            if (_windows.TryGetValue(windowId, out var existing) && existing.IsAlive)
            {
                existing.PostActivate(absoluteUrl, session, iconSpec);
                _sessions.MarkHostAlive(windowId, true);
                var existingIcon = existing.ApplyIconWithAudit(iconSpec);
                return new PluginToolWindowOpenResult("ACTIVATED", existing.HostKind, IsWebView2RuntimeAvailable(), true, true, true, "existing_window_reused", existingIcon.Source, existingIcon.Applied, existingIcon.Diagnostics);
            }

            var host = new HostedWindow(session, absoluteUrl, iconSpec, IsWebView2RuntimeAvailable(), OnHostedWindowClosed, OnHostedWindowStateChanged, _log);
            _windows[windowId] = host;
            _sessions.MarkHostAlive(windowId, true);
            host.Start();
            return new PluginToolWindowOpenResult("ISSUED", host.HostKind, host.WebView2RuntimeAvailable, false, true, false, "new_window_started_activate_requested", iconSpec?.Source ?? "default", iconSpec is not null, iconSpec?.Diagnostics ?? "default_icon_contract");
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
            SupportsRefreshScrollTarget: true,
            RefreshScrollModes: "center|nearest|top",
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

    private void OnHostedWindowClosed(string windowId, PluginWindowHostState? finalState)
    {
        lock (_gate)
        {
            _windows.Remove(windowId);
        }
        _sessions.MarkHostClosed(windowId, finalState);
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
        private readonly Action<string, PluginWindowHostState?> _onClosed;
        private readonly Action<string, PluginWindowHostState?> _onStateChanged;
        private readonly BlockingCollection<Action<ToolWindowForm>> _actions = new();
        private Thread? _thread;
        private volatile bool _alive;
        private ToolWindowForm? _form;
        private readonly LogRepository _log;

        public HostedWindow(PluginWindowSession session, string initialUrl, PluginToolWindowIconSpec? initialIconSpec, bool webView2RuntimeAvailable, Action<string, PluginWindowHostState?> onClosed, Action<string, PluginWindowHostState?> onStateChanged, LogRepository log)
        {
            _initialSession = session;
            _initialUrl = initialUrl;
            _initialIconSpec = initialIconSpec;
            WebView2RuntimeAvailable = webView2RuntimeAvailable;
            _onClosed = onClosed;
            _onStateChanged = onStateChanged;
            HostKind = webView2RuntimeAvailable ? "winforms_webbrowser_fallback_direct_content_webview2_runtime_detected" : "winforms_webbrowser_fallback_direct_content";
            _log = log;
        }

        public string HostKind { get; }
        public bool WebView2RuntimeAvailable { get; }
        public bool IsAlive => _alive;

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = $"TvAIr.PluginToolWindow.{_initialSession.WindowId}" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void PostActivate(string url, PluginWindowSession session, PluginToolWindowIconSpec? iconSpec)
        {
            try
            {
                _actions.Add(form => form.ActivateAndNavigate(url, session, iconSpec));
            }
            catch { }
        }

        public PluginToolWindowIconApplyResult ApplyIconWithAudit(PluginToolWindowIconSpec? iconSpec)
        {
            if (iconSpec is null) return new PluginToolWindowIconApplyResult("default", false, "no_icon_spec");
            try
            {
                using var applied = new ManualResetEventSlim(false);
                PluginToolWindowIconApplyResult result = new(iconSpec.Source, false, "accepted_pending");
                _actions.Add(form =>
                {
                    try { result = form.ApplyIconWithAudit(iconSpec); }
                    catch (Exception ex) { result = new PluginToolWindowIconApplyResult(iconSpec.Source, false, "icon_apply_exception_" + ex.GetType().Name); }
                    finally { try { applied.Set(); } catch { } }
                });
                return applied.Wait(TimeSpan.FromMilliseconds(1200)) ? result : new PluginToolWindowIconApplyResult(iconSpec.Source, false, "icon_apply_timeout");
            }
            catch (Exception ex)
            {
                return new PluginToolWindowIconApplyResult(iconSpec.Source, false, "icon_queue_exception_" + ex.GetType().Name);
            }
        }

        public PluginToolWindowApplyResult ApplySessionWithAudit(PluginWindowSession session)
        {
            if (session is null) return PluginToolWindowApplyResult.NotFound(_initialSession.WindowId);
            try
            {
                using var applied = new ManualResetEventSlim(false);
                PluginToolWindowApplyResult result = PluginToolWindowApplyResult.Pending(_initialSession.WindowId);
                _actions.Add(form =>
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
                });

                return applied.Wait(TimeSpan.FromMilliseconds(1200))
                    ? result
                    : PluginToolWindowApplyResult.Timeout(_initialSession.WindowId);
            }
            catch (Exception ex)
            {
                return PluginToolWindowApplyResult.Failed(_initialSession.WindowId, "queue_exception_" + ex.GetType().Name);
            }
        }

        public void PostClose()
        {
            try
            {
                _actions.Add(form => form.RequestClose());
            }
            catch { }
        }

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
                using var form = new ToolWindowForm(_initialSession, _initialUrl, _initialIconSpec, HostKind, WebView2RuntimeAvailable, state => _onStateChanged(_initialSession.WindowId, state), _log);
                _form = form;
                var timer = new System.Windows.Forms.Timer { Interval = 250 };
                timer.Tick += (_, _) =>
                {
                    while (_actions.TryTake(out var action))
                    {
                        try { action(form); } catch { }
                    }
                };
                timer.Start();
                form.FormClosed += (_, _) =>
                {
                    finalState = form.Snapshot(hostAliveOverride: false);
                    timer.Stop();
                    _alive = false;
                    _onClosed(_initialSession.WindowId, finalState);
                };
                Application.Run(form);
            }
            catch
            {
                _alive = false;
                _onClosed(_initialSession.WindowId, finalState);
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
        private readonly string _windowId;
        private readonly string _hostKind;
        private readonly bool _webView2RuntimeAvailable;
        private readonly Action<PluginWindowHostState> _onStateChanged;
        private readonly LogRepository _log;
        private string _pluginName;
        private bool _loaded;
        private Icon? _ownedIcon;
        private string _pendingRefreshScrollTarget = string.Empty;
        private string _pendingRefreshScrollMode = "center";
        private FormWindowState _lastObservedWindowState = FormWindowState.Normal;
        private bool _lastObservedIsIconic;

        public ToolWindowForm(PluginWindowSession session, string url, PluginToolWindowIconSpec? iconSpec, string hostKind, bool webView2RuntimeAvailable, Action<PluginWindowHostState> onStateChanged, LogRepository log)
        {
            _windowId = session.WindowId;
            _hostKind = hostKind;
            _webView2RuntimeAvailable = webView2RuntimeAvailable;
            _onStateChanged = onStateChanged;
            _log = log;
            _pluginName = string.IsNullOrWhiteSpace(session.PluginName) ? session.RouteSegment : session.PluginName;
            Text = string.IsNullOrWhiteSpace(session.Title) ? session.PluginName : session.Title;
            Width = Math.Max(session.MinWidth, session.Width);
            Height = Math.Max(session.MinHeight, session.Height);
            if (session.Left.HasValue && session.Top.HasValue)
            {
                StartPosition = FormStartPosition.Manual;
                Left = session.Left.Value;
                Top = session.Top.Value;
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
            }
            MinimumSize = new System.Drawing.Size(Math.Max(160, session.MinWidth), Math.Max(120, session.MinHeight));
            ClampToSessionMinimum(session, "constructor");
            FormBorderStyle = session.Resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedSingle;
            MaximizeBox = session.Resizable;
            MinimizeBox = true;
            ShowInTaskbar = true;
            TopMost = session.AlwaysOnTop;
            ApplyIconWithAudit(iconSpec);
            BackColor = System.Drawing.SystemColors.Window;
            Padding = Padding.Empty;
            Margin = Padding.Empty;
            AutoScroll = false;
            ClientSize = new System.Drawing.Size(Math.Max(session.MinWidth, session.Width), Math.Max(session.MinHeight, session.Height));
            _lastObservedWindowState = WindowState;
            _lastObservedIsIconic = IsActuallyMinimized();

            _browser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScriptErrorsSuppressed = true,
                AllowWebBrowserDrop = false,
                WebBrowserShortcutsEnabled = true,
                IsWebBrowserContextMenuEnabled = false,
                ScrollBarsEnabled = true,
                Margin = Padding.Empty,
                MinimumSize = System.Drawing.Size.Empty,
                BackColor = System.Drawing.SystemColors.Window
            };
            Controls.Add(_browser);
            _browser.Navigating += (_, _) => FitBrowserToClient();
            _browser.DocumentCompleted += (_, _) => { FitBrowserToClient(); TryApplyPendingRefreshScroll(); PublishStateIfLoaded(); };
            Load += (_, _) => { _loaded = true; FitBrowserToClient(); Navigate(url); ForceForeground(); PublishState(); };
            Move += (_, _) => PublishStateIfLoaded();
            Resize += (_, _) => { FitBrowserToClient(); HandleWindowStateChanged("resize"); PublishStateIfLoaded(); };
            SizeChanged += (_, _) => FitBrowserToClient();
            FormClosing += (_, _) => PublishState(hostAliveOverride: false);
        }

        public void RequestClose()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(RequestClose));
                return;
            }
            Close();
        }

        public void ActivateAndNavigate(string url, PluginWindowSession session, PluginToolWindowIconSpec? iconSpec)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ActivateAndNavigate(url, session, iconSpec)));
                return;
            }
            EnsureNotMinimized("activate_before_apply");
            ApplySession(session);
            ApplyIconWithAudit(iconSpec);
            ForceForeground();
            Navigate(url);
            PublishState();
        }

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
            ApplySession(session);
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

        private void ApplySession(PluginWindowSession session)
        {
            EnsureNotMinimized("apply_session");
            Text = string.IsNullOrWhiteSpace(session.Title) ? Text : session.Title;
            if (!string.IsNullOrWhiteSpace(session.PluginName)) _pluginName = session.PluginName;
            MinimumSize = new System.Drawing.Size(Math.Max(160, session.MinWidth), Math.Max(120, session.MinHeight));
            if (session.Width > 0 && session.Height > 0)
            {
                Width = Math.Max(session.MinWidth, session.Width);
                Height = Math.Max(session.MinHeight, session.Height);
            }
            ClampToSessionMinimum(session, "apply_session");
            if (session.Left.HasValue && session.Top.HasValue)
            {
                Left = session.Left.Value;
                Top = session.Top.Value;
            }
            TopMost = session.AlwaysOnTop;
            QueueRefreshScroll(session.RefreshScrollTarget, session.RefreshScrollMode);
            FitBrowserToClient();
        }

        private void ClampToSessionMinimum(PluginWindowSession session, string source)
        {
            var minWidth = Math.Max(160, session.MinWidth);
            var minHeight = Math.Max(120, session.MinHeight);
            var oldWidth = Width;
            var oldHeight = Height;
            var newWidth = Math.Max(oldWidth, minWidth);
            var newHeight = Math.Max(oldHeight, minHeight);
            if (newWidth != oldWidth || newHeight != oldHeight)
            {
                Width = newWidth;
                Height = newHeight;
                try
                {
                    _log.Add("PLUGIN_TOOL_WINDOW_MIN_SIZE",
                        string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                        $"result=CLAMPED source={SafeLogValue(source)} windowId={SafeLogValue(_windowId)} oldSize={oldWidth}x{oldHeight} newSize={newWidth}x{newHeight} minSize={minWidth}x{minHeight} rule=release_contract");
                }
                catch { }
            }
        }

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

        private void FitBrowserToClient()
        {
            try
            {
                _browser.Bounds = ClientRectangle;
                _browser.Width = Math.Max(0, ClientSize.Width);
                _browser.Height = Math.Max(0, ClientSize.Height);
            }
            catch { }
        }

        private void Navigate(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) _browser.Navigate(uri);
            }
            catch { }
        }

        private void QueueRefreshScroll(string? target, string? mode)
        {
            _pendingRefreshScrollTarget = NormalizeElementId(target);
            _pendingRefreshScrollMode = NormalizeScrollMode(mode);
        }

        private void TryApplyPendingRefreshScroll()
        {
            var target = _pendingRefreshScrollTarget;
            var mode = NormalizeScrollMode(_pendingRefreshScrollMode);
            _pendingRefreshScrollTarget = string.Empty;

            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            if (_browser.Document is null)
            {
                AddRefreshScrollFinalLog("SKIPPED", target, mode, "document_not_ready");
                return;
            }

            try
            {
                var escapedTarget = target.Replace("\\", "\\\\").Replace("'", "\\'");
                var escapedMode = mode.Replace("\\", "\\\\").Replace("'", "\\'");
                var script =
                    "(function(id,mode){" +
                    "var e=document.getElementById(id);if(!e)return 'TARGET_NOT_FOUND';" +
                    "var p=e.parentElement;" +
                    "while(p&&p!==document.body){var st=p.currentStyle||{};var oy=(st.overflowY||st.overflow||'');if(p.scrollHeight>p.clientHeight&&(oy==='auto'||oy==='scroll'))break;p=p.parentElement;}" +
                    "if(!p||p===document.body)p=document.documentElement||document.body;" +
                    "var y=0,n=e;while(n&&n!==p){y+=n.offsetTop||0;n=n.offsetParent;}" +
                    "var eh=e.offsetHeight||0,ph=p.clientHeight||window.innerHeight||0,stp=p.scrollTop||0;" +
                    "if(mode==='nearest'){if(y>=stp&&(y+eh)<=stp+ph)return 'OK_ALREADY_VISIBLE';if(y>stp)y=y-ph+eh;}" +
                    "else if(mode==='center'){y=y-(ph/2)+(eh/2);}" +
                    "p.scrollTop=Math.max(0,Math.floor(y));return 'OK';" +
                    "})('" + escapedTarget + "','" + escapedMode + "');";
                var rawResult = _browser.Document.InvokeScript("eval", new object[] { script })?.ToString() ?? string.Empty;
                var result = rawResult.Equals("TARGET_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ? "TARGET_NOT_FOUND" : "OK";
                var reason = rawResult.Equals("OK_ALREADY_VISIBLE", StringComparison.OrdinalIgnoreCase) ? "already_visible" : "directcontent_document_completed";
                AddRefreshScrollFinalLog(result, target, mode, reason);
            }
            catch (Exception ex)
            {
                try
                {
                    var element = _browser.Document.GetElementById(target);
                    if (element is null)
                    {
                        AddRefreshScrollFinalLog("TARGET_NOT_FOUND", target, mode, "fallback_getelement_null");
                        return;
                    }
                    element.ScrollIntoView(mode.Equals("top", StringComparison.OrdinalIgnoreCase));
                    AddRefreshScrollFinalLog("OK", target, mode, "fallback_scrollintoview_after_" + ex.GetType().Name);
                }
                catch (Exception fallbackEx)
                {
                    AddRefreshScrollFinalLog("SKIPPED", target, mode, "exception_" + fallbackEx.GetType().Name);
                }
            }
        }

        private void AddRefreshScrollFinalLog(string result, string target, string mode, string reason)
        {
            try
            {
                _log.Add("PLUGIN_WINDOW_REFRESH_SCROLL",
                    string.IsNullOrWhiteSpace(_pluginName) ? "ToolWindow" : _pluginName,
                    $"result={result} action=after_directcontent_refresh windowId={SafeLogValue(_windowId)} target={SafeLogValue(target)} mode={SafeLogValue(mode)} hostKind={SafeLogValue(_hostKind)} reason={SafeLogValue(reason)} rule=release_contract");
            }
            catch { }
        }

        private static string SafeLogValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "-";
            var s = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length > 160) s = s[..160] + "…";
            return s;
        }

        private static string NormalizeElementId(string? value)
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
                minimized);
        }
    }
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
    bool SupportsRefreshScrollTarget,
    string RefreshScrollModes,
    bool ScriptExecutionAllowed,
    bool SupportsManifestFormIcon,
    string FormIconSourcePriority,
    string ContractVersion);
