using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using TvAIr.Schedule;
using TvAIr.Tuner;
using TvAIr.Epg;
using TvAIr.Plugin;

namespace TvAIr.Core;

/// <summary>
/// タスクトレイアイコンを管理するサービス。
/// STAスレッドでWindowsメッセージループを回し、アイコン右クリック→終了を提供する。
/// Idle時は固定、録画中・EPG取得中・録画前EPG確認中はゆっくり点滅する。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly int _port;
    private readonly ReservationStore _reservationStore;
    private readonly TunerPool _tunerPool;
    private readonly EpgScheduler _epgScheduler;
    private readonly LogRepository _log;
    private readonly PluginDefaultMenuActionService _pluginMenuActions;
    private readonly IniSettingsService _settings;

    private Thread? _thread;
    private NotifyIcon? _icon;
    private System.Windows.Forms.Timer? _timer;
    private Icon? _idleIcon;
    private Icon? _recordingIcon;
    private bool _blinkOn;
    private volatile TrayVisualState _visualState = TrayVisualState.Idle;
    private int _stateRefreshInProgress;
#if TVAIR_DEVELOPER_DIAGNOSTICS
    // Developer Diagnostics only. Public builds do not allocate or retain the tray diagnostic queue.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string EventName, string Title, string Message)> _trayDiagnostics = new();
    private int _trayDiagnosticDrainInProgress;
#endif
    private int _trayMenuOpen;
    private readonly object _trayPopupSync = new();
    private TrayPopupSession? _activeTrayPopup;
    private TrayPopupRequest? _pendingTrayPopup;
    private long _trayPopupSequence;
    private int _trayPopupShuttingDown;
    private int _trayTimerTickCount;
    private long _lastTrayHeartbeatTick;
#if TVAIR_DEVELOPER_DIAGNOSTICS
    private long _lastProcessAllocatedBytes;
#endif
    private long _lastManagedMouseUpTick;
    private long _lastNativeTrayRightTick;
    private IntPtr _notifyIconSinkHandle;
    private IntPtr _trayNativeSubclassHandle;
    private SubclassProc? _trayNativeSubclassProc;
    private string _notifyIconSinkResolution = "not_attempted";
    private Icon? _appliedTrayIcon;
    private string _appliedTrayText = string.Empty;
    private const string MenuLabelHelp = "ヘルプ";
    private const string MenuLabelVersion = "バージョン情報";
    private const string MenuLabelExit = "TvAIr終了";
    private const uint TrayCommandOpen = 1001;
    private const uint TrayCommandEpgAll = 1101;
    private const uint TrayCommandEpgGr = 1102;
    private const uint TrayCommandEpgBs = 1103;
    private const uint TrayCommandEpgCs = 1104;
    private const uint TrayCommandEpgBscs = 1105;
    private const uint TrayCommandEpgCancel = 1106;
    private const uint TrayCommandSettings = 1201;
    private const uint TrayCommandHelp = 1202;
    private const uint TrayCommandVersion = 1203;
    private const uint TrayCommandExit = 1204;
    private const uint TrayCommandPluginFirst = 2000;
    private const uint TrayCommandPluginLast = 2999;
    private const uint MfString = 0x0000;
    private const uint MfGrayed = 0x0001;
    private const uint MfSeparator = 0x0800;
    private const uint MfPopup = 0x0010;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
    private const uint WmNull = 0x0000;
    private const uint WmCancelMode = 0x001F;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private volatile bool _started;

    public TrayIconService(int port, ReservationStore reservationStore, TunerPool tunerPool, EpgScheduler epgScheduler, LogRepository log, PluginDefaultMenuActionService pluginMenuActions, IniSettingsService settings)
    {
        _port = port;
        _reservationStore = reservationStore;
        _tunerPool = tunerPool;
        _epgScheduler = epgScheduler;
        _log = log;
        _pluginMenuActions = pluginMenuActions;
        _settings = settings;
    }

    public void Start()
    {
        if (_started)
        {
            try { _log.Add("TRAY_ICON", "START_SKIPPED", "reason=already_started rule=release_contract"); } catch { }
            return;
        }
        _started = true;
        Volatile.Write(ref _trayPopupShuttingDown, 0);
        _thread = new Thread(() =>
        {
            var idleIconPath = Path.Combine(AppContext.BaseDirectory, "TvAIr_Idle.ico");
            var recordingIconPath = Path.Combine(AppContext.BaseDirectory, "TvAIr_Recording.ico");

            _idleIcon = File.Exists(idleIconPath)
                ? new Icon(idleIconPath)
                : SystemIcons.Application;
            _recordingIcon = File.Exists(recordingIconPath)
                ? new Icon(recordingIconPath)
                : _idleIcon;

            _icon = new NotifyIcon
            {
                Icon = _idleIcon,
                Text = $"TvAIr (:{_port})",
                // The tray menu is intentionally NOT assigned to NotifyIcon.ContextMenuStrip.
                // Each right-click is handed to an independent popup STA/HWND. NotifyIcon never owns
                // the modal TrackPopupMenuEx loop and no popup state survives a completed session.
                Visible = true,
            };
            _appliedTrayIcon = _idleIcon;
            _appliedTrayText = $"TvAIr (:{_port})";

            _icon.MouseUp += (_, e) =>
            {
                Interlocked.Exchange(ref _lastManagedMouseUpTick, Environment.TickCount64);
                QueueTrayDiagnostic("TRAY_MOUSE_EVENT", e.Button.ToString(),
                    $"result=RECEIVED button={e.Button} clicks={e.Clicks} x={e.X} y={e.Y} thread={Environment.CurrentManagedThreadId} owner=tray_sta_popup_session_separated rule=tray_popup_session_separation_contract");

                if (e.Button == MouseButtons.Right)
                {
                    QueueNativeTrayMenu();
                }
            };
            _icon.DoubleClick += (_, _) => OpenBrowser($"http://localhost:{_port}");

            // Diagnostic only: subclass the NotifyIcon native sink with the documented comctl32 subclass chain
            // so the Shell callback can be observed at the actual sink boundary. The callback always forwards to
            // DefSubclassProc without consuming, rewriting, retrying, or redispatching the message.
            RefreshNotifyIconSinkHandle();
            EnsureTrayNativeWindowObserver();

            _log.Add("TRAY_ICON", "VISIBLE", $"result=OK port={_port} thread={Environment.CurrentManagedThreadId} owner=tray_sta_popup_session_separated visualProjection=tray_timer_only nativeSink={FormatHwnd(_notifyIconSinkHandle)} nativeSinkResolution={_notifyIconSinkResolution} rule=release_contract");

            _timer = new System.Windows.Forms.Timer
            {
                Interval = 800
            };
            _timer.Tick += (_, _) =>
            {
                _trayTimerTickCount++;
                EnsureVisible();
                QueueStateRefresh();
                ApplyCachedVisualState();
                EmitTrayStaHeartbeatIfDue();
            };

            // 起動直後は必ず待機中アイコンに固定
            SetIdle();
            _timer.Start();

            try
            {
                Application.Run();
            }
            finally
            {
                ReleaseTrayNativeWindowObserver();
            }
        });

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
    }

    private void RequestAppExit(string source)
    {
        // 終了処理はWeb/トレイで分岐させず /api/app/exit を単一出口とする。
        // トレイ側からプロセス終了を直接行わず、Hostの共通終了契約へ要求する。
        var safeSource = string.IsNullOrWhiteSpace(source) ? "TrayMenu" : source.Trim();
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var url = $"http://localhost:{_port}/api/app/exit?source={Uri.EscapeDataString(safeSource)}";
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url);
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                _log.Add("TRAY_APP_EXIT", "Exit",
                    $"result={(response.IsSuccessStatusCode ? "ACCEPTED" : "FAILED")} source={safeSource} status={(int)response.StatusCode} commonRoute=/api/app/exit rule=tray_menu_command_contract");
            }
            catch (Exception ex)
            {
                try { _log.Add("TRAY_APP_EXIT", "Exit", $"result=FAILED source={safeSource} error={ex.GetType().Name} commonRoute=/api/app/exit rule=tray_menu_command_contract"); } catch { }
            }
        });
    }

    private void ExecutePluginMenuAction(string route, string kind)
    {
        var safeRoute = (route ?? string.Empty).Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(safeRoute)) return;

        if (kind.Equals(PluginMenuActionKinds.Page, StringComparison.OrdinalIgnoreCase)
            || kind.Equals(PluginMenuActionKinds.Settings, StringComparison.OrdinalIgnoreCase))
        {
            OpenBrowser($"http://localhost:{_port}/plugin-menu/{Uri.EscapeDataString(safeRoute)}?source=tray");
            return;
        }

        RequestSilentPluginMenu(safeRoute);
    }

    private void QueueNativeTrayMenu()
    {
        if (Volatile.Read(ref _trayPopupShuttingDown) != 0) return;

        if (!GetCursorPos(out var point))
        {
            QueueTrayDiagnostic("TRAY_CONTEXT_MENU", "OPEN_FAILED",
                $"result=FAILED reason=get_cursor_pos_failed win32Error={Marshal.GetLastWin32Error()} thread={Environment.CurrentManagedThreadId} rule=tray_popup_session_separation_contract");
            return;
        }

        var request = CreateTrayPopupRequest(point);
        TrayPopupSession? startSession = null;
        IntPtr cancelOwner = IntPtr.Zero;
        long replacedSequence = 0;
        long pendingSequence = 0;
        lock (_trayPopupSync)
        {
            if (_activeTrayPopup is null)
            {
                startSession = new TrayPopupSession(request.Sequence, request);
                _activeTrayPopup = startSession;
                Volatile.Write(ref _trayMenuOpen, 1);
            }
            else
            {
                replacedSequence = _pendingTrayPopup?.Sequence ?? 0;
                _pendingTrayPopup = request;
                pendingSequence = request.Sequence;
                _activeTrayPopup.CancelRequested = true;
                cancelOwner = Interlocked.CompareExchange(ref _activeTrayPopup.OwnerHwnd, IntPtr.Zero, IntPtr.Zero);
            }
        }

        if (startSession is not null)
        {
            StartTrayPopupSession(startSession);
            return;
        }

        QueueTrayDiagnostic("TRAY_CONTEXT_MENU", "REPLACE_REQUESTED",
            $"result=QUEUED activeSequence={GetActivePopupSequence()} pendingSequence={pendingSequence} replacedPendingSequence={replacedSequence} cancelOwner={FormatHwnd(cancelOwner)} trayThread={Environment.CurrentManagedThreadId} policy=latest_only_bounded rule=tray_popup_session_separation_contract");

        if (cancelOwner != IntPtr.Zero && IsWindow(cancelOwner))
        {
            _ = PostMessage(cancelOwner, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
            _ = PostMessage(cancelOwner, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private TrayPopupRequest CreateTrayPopupRequest(NativePoint point)
    {
        var plugins = new List<TrayPopupPluginAction>();
        try
        {
            var actions = _pluginMenuActions.ResolveActions("tray");
            foreach (var action in actions)
            {
                if (string.IsNullOrWhiteSpace(action.RouteSegment)) continue;
                var label = string.IsNullOrWhiteSpace(action.Label) ? action.Name : action.Label;
                if (string.IsNullOrWhiteSpace(label)) continue;
                plugins.Add(new TrayPopupPluginAction(label, action.RouteSegment, action.Kind ?? string.Empty));
                if (plugins.Count >= (TrayCommandPluginLast - TrayCommandPluginFirst + 1)) break;
            }
        }
        catch { }

        return new TrayPopupRequest(
            Sequence: Interlocked.Increment(ref _trayPopupSequence),
            X: point.X,
            Y: point.Y,
            VisualState: _visualState,
            Plugins: plugins.ToArray(),
            RequestedAtTick: Environment.TickCount64);
    }

    private long GetActivePopupSequence()
    {
        lock (_trayPopupSync) return _activeTrayPopup?.Sequence ?? 0;
    }

    private void StartTrayPopupSession(TrayPopupSession session)
    {
        var thread = new Thread(() => RunTrayPopupSession(session))
        {
            IsBackground = true,
            Name = $"TvAIr.TrayPopup.{session.Sequence}"
        };
        thread.SetApartmentState(ApartmentState.STA);
        session.Thread = thread;
        thread.Start();
    }

    private void RunTrayPopupSession(TrayPopupSession session)
    {
        IntPtr root = IntPtr.Zero;
        uint command = 0;
        var pluginCommands = new Dictionary<uint, (string Route, string Kind)>();
        PopupOwnerWindow? owner = null;
        try
        {
            owner = new PopupOwnerWindow();
            owner.Create();
            Interlocked.Exchange(ref session.OwnerHwnd, owner.Handle);

            if (session.CancelRequested || Volatile.Read(ref _trayPopupShuttingDown) != 0)
            {
                QueueTrayDiagnostic("TRAY_CONTEXT_MENU", "OPEN_CANCELLED",
                    $"result=CANCELLED sequence={session.Sequence} reason=cancel_before_tracking popupThread={Environment.CurrentManagedThreadId} owner={FormatHwnd(owner.Handle)} rule=tray_popup_session_separation_contract");
            }
            else
            {
                root = BuildNativeTrayMenu(session.Request.VisualState, session.Request.Plugins, pluginCommands);
                if (root == IntPtr.Zero)
                {
                    QueueTrayDiagnostic("TRAY_CONTEXT_MENU", "OPEN_FAILED",
                        $"result=FAILED sequence={session.Sequence} reason=create_popup_menu_failed win32Error={Marshal.GetLastWin32Error()} popupThread={Environment.CurrentManagedThreadId} rule=tray_popup_session_separation_contract");
                }
                else
                {
                    var before = CaptureForegroundWindowSnapshot();
                    QueueTrayDiagnostic("TRAY_CONTEXT_MENU", "OPENING",
                        $"result=ENTER sequence={session.Sequence} owner=popup_dedicated_sta lifecycle=per_right_click popupThread={Environment.CurrentManagedThreadId} rootHandle={FormatHwnd(root)} popupOwner={FormatHwnd(owner.Handle)} x={session.Request.X} y={session.Request.Y} foreground={before.ToLogFields()} rule=tray_popup_session_separation_contract");

                    _ = ShowWindow(owner.Handle, SwShowNoActivate);
                    var foregroundAccepted = SetForegroundWindow(owner.Handle);
                    QueueTrayDiagnostic("TRAY_CONTEXT_MENU", "OPENED",
                        $"result=TRACKING sequence={session.Sequence} owner=popup_dedicated_sta lifecycle=per_right_click popupThread={Environment.CurrentManagedThreadId} rootHandle={FormatHwnd(root)} popupOwner={FormatHwnd(owner.Handle)} foregroundAccepted={foregroundAccepted} trayStaIndependent=True rule=tray_popup_session_separation_contract");

                    if (session.CancelRequested)
                    {
                        _ = PostMessage(owner.Handle, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
                    }

                    command = TrackPopupMenuEx(
                        root,
                        TpmRightButton | TpmReturnCmd | TpmNonotify,
                        session.Request.X,
                        session.Request.Y,
                        owner.Handle,
                        IntPtr.Zero);

                    _ = PostMessage(owner.Handle, WmNull, IntPtr.Zero, IntPtr.Zero);
                    var after = CaptureForegroundWindowSnapshot();
                    QueueTrayDiagnostic("TRAY_CONTEXT_MENU", "CLOSED",
                        $"result=OK sequence={session.Sequence} owner=popup_dedicated_sta lifecycle=per_right_click command={command} popupThread={Environment.CurrentManagedThreadId} rootHandle={FormatHwnd(root)} popupOwner={FormatHwnd(owner.Handle)} elapsedMs={Math.Max(0, Environment.TickCount64 - session.Request.RequestedAtTick)} foreground={after.ToLogFields()} rule=tray_popup_session_separation_contract");
                }
            }
        }
        catch (Exception ex)
        {
            QueueTrayDiagnostic("TRAY_CONTEXT_MENU", "OPEN_FAILED",
                $"result=FAILED sequence={session.Sequence} error={ex.GetType().Name} message={SanitizeWindowField(ex.Message)} popupThread={Environment.CurrentManagedThreadId} owner=popup_dedicated_sta rule=tray_popup_session_separation_contract");
            command = 0;
        }
        finally
        {
            if (root != IntPtr.Zero) DestroyNativeMenu(root, $"popup_terminal_sequence_{session.Sequence}");
            if (owner is not null)
            {
                try { _ = ShowWindow(owner.Handle, SwHide); } catch { }
                try { owner.Destroy(); } catch { }
            }
            Interlocked.Exchange(ref session.OwnerHwnd, IntPtr.Zero);
        }

        CompleteTrayPopupSession(session, command, pluginCommands);
    }

    private void CompleteTrayPopupSession(TrayPopupSession session, uint command, IReadOnlyDictionary<uint, (string Route, string Kind)> pluginCommands)
    {
        TrayPopupSession? next = null;
        lock (_trayPopupSync)
        {
            if (ReferenceEquals(_activeTrayPopup, session))
            {
                _activeTrayPopup = null;
                if (Volatile.Read(ref _trayPopupShuttingDown) == 0 && _pendingTrayPopup is { } pending)
                {
                    _pendingTrayPopup = null;
                    next = new TrayPopupSession(pending.Sequence, pending);
                    _activeTrayPopup = next;
                }
                else
                {
                    _pendingTrayPopup = null;
                    Volatile.Write(ref _trayMenuOpen, 0);
                }
            }
        }

        if (next is not null)
        {
            QueueTrayDiagnostic("TRAY_CONTEXT_MENU", "REPLACEMENT_START",
                $"result=START sequence={next.Sequence} previousSequence={session.Sequence} policy=latest_only_bounded rule=tray_popup_session_separation_contract");
            StartTrayPopupSession(next);
        }

        if (command != 0)
        {
            DispatchNativeTrayCommand(command, pluginCommands);
        }
    }

    private IntPtr BuildNativeTrayMenu(TrayVisualState state, IReadOnlyList<TrayPopupPluginAction> pluginActions, Dictionary<uint, (string Route, string Kind)> pluginCommands)
    {
        var root = CreatePopupMenu();
        if (root == IntPtr.Zero) return IntPtr.Zero;

        IntPtr epg = IntPtr.Zero;
        IntPtr plugins = IntPtr.Zero;
        var epgAttached = false;
        var pluginsAttached = false;
        try
        {
            AppendNativeCommand(root, TrayCommandOpen, "TvAIrを開く", enabled: true);
            AppendNativeSeparator(root);

            epg = CreatePopupMenu();
            if (epg == IntPtr.Zero) throw new InvalidOperationException("CreatePopupMenu(EPG) failed.");
            AppendNativeCommand(epg, TrayCommandEpgAll, "全局取得", state.EpgCanStart);
            AppendNativeCommand(epg, TrayCommandEpgGr, "地上波のみ取得", state.EpgCanStart);
            AppendNativeCommand(epg, TrayCommandEpgBs, "BSのみ取得", state.EpgCanStart);
            AppendNativeCommand(epg, TrayCommandEpgCs, "CSのみ取得", state.EpgCanStart);
            AppendNativeCommand(epg, TrayCommandEpgBscs, "BS/CSのみ取得", state.EpgCanStart);
            AppendNativeCommand(epg, TrayCommandEpgCancel, "取得キャンセル", state.EpgCanCancel && state.EpgRunSilent);
            AppendNativeSubmenu(root, epg, "EPG取得");
            epgAttached = true;

            plugins = CreatePopupMenu();
            if (plugins == IntPtr.Zero) throw new InvalidOperationException("CreatePopupMenu(Plugins) failed.");
            uint pluginId = TrayCommandPluginFirst;
            foreach (var action in pluginActions)
            {
                if (pluginId > TrayCommandPluginLast) break;
                AppendNativeCommand(plugins, pluginId, action.Label, enabled: true);
                pluginCommands[pluginId] = (action.Route, action.Kind);
                pluginId++;
            }

            if (pluginCommands.Count > 0)
            {
                AppendNativeSubmenu(root, plugins, "プラグイン");
                pluginsAttached = true;
            }

            AppendNativeCommand(root, TrayCommandSettings, "設定", enabled: true);
            AppendNativeSeparator(root);
            AppendNativeCommand(root, TrayCommandHelp, MenuLabelHelp, enabled: true);
            AppendNativeCommand(root, TrayCommandVersion, MenuLabelVersion, enabled: true);
            AppendNativeSeparator(root);
            AppendNativeCommand(root, TrayCommandExit, MenuLabelExit, enabled: true);
            return root;
        }
        catch
        {
            // Build failure is terminal for this invocation. Destroy the root (and every already
            // attached submenu) here, then propagate the exact failure to the outer diagnostic.
            if (root != IntPtr.Zero)
            {
                DestroyNativeMenu(root, "build_failure_root");
                root = IntPtr.Zero;
            }
            throw;
        }
        finally
        {
            // Once attached, DestroyMenu(root) owns the submenu tree recursively. Only unattached
            // temporary menus are destroyed here.
            if (!epgAttached && epg != IntPtr.Zero) DestroyNativeMenu(epg, "build_cleanup_epg");
            if (!pluginsAttached && plugins != IntPtr.Zero) DestroyNativeMenu(plugins, "build_cleanup_plugins");
        }
    }

    private void DestroyNativeMenu(IntPtr menu, string phase)
    {
        if (menu == IntPtr.Zero) return;
        if (DestroyMenu(menu)) return;

        var error = Marshal.GetLastWin32Error();
        QueueTrayDiagnostic("TRAY_NATIVE_MENU_RESOURCE", "DESTROY_FAILED",
            $"result=FAILED phase={phase} handle={FormatHwnd(menu)} win32Error={error} thread={Environment.CurrentManagedThreadId} rule=tray_native_popup_resource_contract");
    }

    private static void AppendNativeCommand(IntPtr menu, uint id, string text, bool enabled)
    {
        var flags = MfString | (enabled ? 0u : MfGrayed);
        if (!AppendMenu(menu, flags, new UIntPtr(id), text))
            throw new InvalidOperationException($"AppendMenu command {id} failed.");
    }

    private static void AppendNativeSeparator(IntPtr menu)
    {
        if (!AppendMenu(menu, MfSeparator, UIntPtr.Zero, null))
            throw new InvalidOperationException("AppendMenu separator failed.");
    }

    private static void AppendNativeSubmenu(IntPtr menu, IntPtr submenu, string text)
    {
        if (!AppendMenu(menu, MfPopup | MfString, new UIntPtr(unchecked((ulong)submenu.ToInt64())), text))
            throw new InvalidOperationException("AppendMenu submenu failed.");
    }

    private void DispatchNativeTrayCommand(uint command, IReadOnlyDictionary<uint, (string Route, string Kind)> pluginCommands)
    {
        if (command == 0) return;
        switch (command)
        {
            case TrayCommandOpen:
                OpenBrowser($"http://localhost:{_port}");
                break;
            case TrayCommandEpgAll:
                StartSilentEpg("All", "全局取得");
                break;
            case TrayCommandEpgGr:
                StartSilentEpg("GR", "地上波のみ取得");
                break;
            case TrayCommandEpgBs:
                StartSilentEpg("BS", "BSのみ取得");
                break;
            case TrayCommandEpgCs:
                StartSilentEpg("CS", "CSのみ取得");
                break;
            case TrayCommandEpgBscs:
                StartSilentEpg("BSCS", "BS/CSのみ取得");
                break;
            case TrayCommandEpgCancel:
                CancelSilentEpg();
                break;
            case TrayCommandSettings:
                OpenSettingsWindowFromEntry("tray");
                break;
            case TrayCommandHelp:
                OpenBrowser($"http://localhost:{_port}/help.html");
                break;
            case TrayCommandVersion:
                ShowVersionDialog();
                break;
            case TrayCommandExit:
                RequestAppExit("TrayMenu");
                break;
            default:
                if (pluginCommands.TryGetValue(command, out var plugin))
                    ExecutePluginMenuAction(plugin.Route, plugin.Kind);
                break;
        }
    }

    private static void ShowVersionDialog()
    {
        var version = Application.ProductVersion;
        if (string.IsNullOrWhiteSpace(version))
            version = typeof(TrayIconService).Assembly.GetName().Version?.ToString() ?? "unknown";
        MessageBox.Show($"TvAIr\n\nバージョン: {version}", "TvAIr バージョン情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void StartSilentEpg(string targetScope, string label)
    {
        try
        {
            var started = _epgScheduler.TriggerNow($"TrayMenu.SilentEpg.{targetScope}", silent: true, targetScope: targetScope);
            var block = started ? null : _epgScheduler.GetLastStartBlockInfo(targetScope);
            _log.Add("TRAY_EPG_SILENT", "EPG", started
                ? $"タスクトレイ右クリックメニューからEPG取得（サイレント）を開始しました。targetScope={targetScope} label={label} uiMode=Silent cancelRoute=SilentTray rule=epg_explicit_cancel_route_contract"
                : block is not null
                    ? $"タスクトレイ右クリックメニューからEPG取得（サイレント）が開始できませんでした。targetScope={targetScope} label={label} reason={block.Reason} action=show_short_dialog rule=release_contract"
                    : $"タスクトレイ右クリックメニューからEPG取得（サイレント）が要求されましたが、既に取得中です。targetScope={targetScope} label={label} action=reject_start_cancel_required rule=release_contract");

            if (!started && block is not null)
            {
                TvAIrNotificationDialog.Show(block.DisplayMessage, "時間をおいてお試しください。", _settings.SystemTheme);
            }

            // サイレント指定のため、開始できた場合もブラウザ・右下進捗パネルは開かない。
            // 状態取得はトレイSTAスレッドを塞がないようバックグラウンドで更新する。
            QueueStateRefresh();
            ApplyCachedVisualState();
        }
        catch (Exception ex)
        {
            try
            {
                _log.Add("TRAY_EPG_SILENT_ERROR", "EPG", $"EPG取得（サイレント）開始時に例外: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }
    }

    private void CancelSilentEpg()
    {
        try
        {
            var accepted = _epgScheduler.CancelSilent("TrayMenu.SilentEpgCancel");
            _log.Add("TRAY_EPG_CANCEL", "EPG", accepted
                ? "タスクトレイ右クリックメニューからSilent EPG取得キャンセルを要求しました。cancelRoute=SilentTray rule=epg_explicit_cancel_route_contract"
                : "タスクトレイ右クリックメニューからSilent EPG取得キャンセルを要求しましたが、対象となるSilent EPGは実行中ではありません。rule=epg_explicit_cancel_route_contract");
            QueueStateRefresh();
            ApplyCachedVisualState();
        }
        catch (Exception ex)
        {
            try { _log.Add("TRAY_EPG_CANCEL_ERROR", "EPG", $"EPG取得キャンセル時に例外: {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    private void RequestSilentPluginMenu(string route)
    {
        var safeRoute = (route ?? string.Empty).Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(safeRoute)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new System.Net.Http.HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(5)
                };
                var url = $"http://localhost:{_port}/plugin-menu/{Uri.EscapeDataString(safeRoute)}?source=tray";
                using var response = await client.GetAsync(url).ConfigureAwait(false);
                _log.Add("TRAY_PLUGIN_MENU", safeRoute, $"result={(response.IsSuccessStatusCode ? "OK" : "WARN")} route={safeRoute} status={(int)response.StatusCode} browserOpened=False programGuideOpened=False rule=release_contract");
            }
            catch (Exception ex)
            {
                try { _log.Add("TRAY_PLUGIN_MENU", safeRoute, $"result=FAILED route={safeRoute} error={ex.GetType().Name} browserOpened=False rule=release_contract"); } catch { }
            }
        });
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }


    private void EnsureVisible()
    {
        try
        {
            if (_icon is null) return;
            if (!_icon.Visible)
            {
                _icon.Visible = true;
                _log.Add("TRAY_ICON", "RECOVER_VISIBLE", "result=OK reason=notifyicon_hidden rule=release_contract");
            }
        }
        catch (Exception ex)
        {
            try
            {
                var message = (ex.Message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                _log.Add("TRAY_ICON", "RECOVER_FAILED", $"error={ex.GetType().Name} message={message} rule=release_contract");
            }
            catch { }
        }
    }

    private void QueueStateRefresh()
    {
        // NotifyIconはTray STA、native popupは専用STA/HWNDで動く。
        // 録画開始・終了時は ReservationStore / TunerPool 側の更新ロックと重なるため、
        // 状態取得をSTAスレッドで同期実行すると右クリックメニューまで停止する。
        // 取得は単一のバックグラウンド処理へ逃がし、STA側はキャッシュ済み状態の描画だけを行う。
        if (Interlocked.CompareExchange(ref _stateRefreshInProgress, 1, 0) != 0) return;

        _ = Task.Run(() =>
        {
            try
            {
                _visualState = ReadVisualStateSnapshot();
            }
            finally
            {
                Volatile.Write(ref _stateRefreshInProgress, 0);
            }
        });
    }

    private TrayVisualState ReadVisualStateSnapshot()
    {
        var hasRecording = false;
        var hasEpg = false;
        var hasTunerActive = false;

        try
        {
            hasRecording = _reservationStore.GetByStatus(ReservationStatus.Recording).Count > 0;
        }
        catch { }

        try
        {
            var tunerStatus = _tunerPool.GetStatus();
            hasRecording = hasRecording || tunerStatus.Any(s => s.UsageKind == TunerUsageKind.Recording);
            hasEpg = tunerStatus.Any(s => s.UsageKind == TunerUsageKind.Epg);
            hasTunerActive = tunerStatus.Any(s => s.UsageKind == TunerUsageKind.Recording || s.UsageKind == TunerUsageKind.Epg);
        }
        catch { }

        var epgRunMode = string.Empty;
        var epgTargetScope = string.Empty;
        var epgCanStart = true;
        var epgCanCancel = false;
        var epgRunSilent = false;
        try
        {
            var runState = _epgScheduler.GetRunState();
            hasEpg = hasEpg || runState.IsRunning;
            epgRunMode = runState.IsRunning ? runState.UiMode ?? string.Empty : string.Empty;
            epgTargetScope = runState.TargetScope ?? string.Empty;
            epgCanStart = runState.CanStart;
            epgCanCancel = runState.CanCancel;
            epgRunSilent = runState.Silent;
        }
        catch { }

        return new TrayVisualState(
            hasRecording,
            hasEpg,
            hasTunerActive,
            epgRunMode,
            epgTargetScope,
            epgCanStart,
            epgCanCancel,
            epgRunSilent);
    }

    private bool IsTrayMenuActive()
        => Volatile.Read(ref _trayMenuOpen) != 0;

    private void ApplyCachedVisualState()
    {
        // Popup tracking is owned by an independent dedicated STA/HWND. NotifyIcon projection is
        // therefore never paused by an open tray menu.
        var state = _visualState;
        if (!state.IsActive)
        {
            SetIdle();
            return;
        }

        if (_icon is null) return;

        // release_contract: TvAIrEpgRec のタスクバー表示をOFFにできるため、TvAIr本体トレイアイコンを
        // 録画・通常EPG取得・サイレントEPG取得・録画前EPG確認の代表インジケータとして点滅させる。
        _blinkOn = !_blinkOn;
        var nextIcon = _blinkOn ? (_recordingIcon ?? _idleIcon ?? SystemIcons.Application)
                                : (_idleIcon ?? SystemIcons.Application);
        var nextText = state.HasRecording
            ? $"録画中 (:{_port})"
            : state.HasEpg
                ? $"EPG取得中{(string.IsNullOrWhiteSpace(state.EpgRunMode) ? string.Empty : $"/{state.EpgRunMode}")} (:{_port})"
                : $"チューナー使用中 (:{_port})";
        ApplyNotifyIconVisual(nextIcon, nextText);
    }



    private void EmitTrayStaHeartbeatIfDue()
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastTrayHeartbeatTick);
        if (previous != 0 && now - previous < 30_000) return;
        if (Interlocked.CompareExchange(ref _lastTrayHeartbeatTick, now, previous) != previous) return;

        var managed = Interlocked.Read(ref _lastManagedMouseUpTick);
        var nativeRight = Interlocked.Read(ref _lastNativeTrayRightTick);
        var sink = RefreshNotifyIconSinkHandle();
        var sinkAlive = sink != IntPtr.Zero && IsWindow(sink);
        EnsureTrayNativeWindowObserver();
        var focus = CaptureForegroundWindowSnapshot();
        long activePopupSequence;
        long pendingPopupSequence;
        IntPtr popupOwner;
        bool popupCancelRequested;
        lock (_trayPopupSync)
        {
            activePopupSequence = _activeTrayPopup?.Sequence ?? 0;
            pendingPopupSequence = _pendingTrayPopup?.Sequence ?? 0;
            popupOwner = _activeTrayPopup is null
                ? IntPtr.Zero
                : Interlocked.CompareExchange(ref _activeTrayPopup.OwnerHwnd, IntPtr.Zero, IntPtr.Zero);
            popupCancelRequested = _activeTrayPopup?.CancelRequested ?? false;
        }
        QueueTrayDiagnostic("TRAY_STA_HEARTBEAT", "ALIVE",
            $"result=OK thread={Environment.CurrentManagedThreadId} timerTicks={_trayTimerTickCount} timerEnabled={_timer?.Enabled.ToString() ?? "-"} iconExists={(_icon is not null)} iconVisible={_icon?.Visible.ToString() ?? "-"} menuActive={IsTrayMenuActive()} activePopupSequence={activePopupSequence} pendingPopupSequence={pendingPopupSequence} popupOwner={FormatHwnd(popupOwner)} popupCancelRequested={popupCancelRequested} popupOwnership=dedicated_sta stateRefreshInProgress={Volatile.Read(ref _stateRefreshInProgress)} lastNativeRightAgoMs={(nativeRight == 0 ? -1 : now - nativeRight)} lastManagedMouseUpAgoMs={(managed == 0 ? -1 : now - managed)} nativeSink={FormatHwnd(sink)} nativeSinkAlive={sinkAlive} nativeSinkResolution={_notifyIconSinkResolution} foreground={focus.ToLogFields()} rule=tray_sta_message_pump_contract");
        EmitProcessMemoryDiagnostic();
    }

    private IntPtr RefreshNotifyIconSinkHandle()
    {
        var icon = _icon;
        if (icon is null)
        {
            _notifyIconSinkHandle = IntPtr.Zero;
            _notifyIconSinkResolution = "icon_null";
            return IntPtr.Zero;
        }

        try
        {
            var fields = icon.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                object? value;
                try { value = field.GetValue(icon); } catch { continue; }
                if (value is not NativeWindow nativeWindow) continue;
                var handle = nativeWindow.Handle;
                if (handle == IntPtr.Zero) continue;
                _notifyIconSinkHandle = handle;
                _notifyIconSinkResolution = $"nativewindow_field:{field.Name}";
                return handle;
            }

            _notifyIconSinkHandle = IntPtr.Zero;
            _notifyIconSinkResolution = "nativewindow_field_not_found";
        }
        catch (Exception ex)
        {
            _notifyIconSinkHandle = IntPtr.Zero;
            _notifyIconSinkResolution = $"failed:{ex.GetType().Name}";
        }

        return _notifyIconSinkHandle;
    }

    private static readonly UIntPtr TrayNativeSubclassId = new(0x54564149); // "TVAI"

    private void EnsureTrayNativeWindowObserver()
    {
        var sink = _notifyIconSinkHandle;
        if (sink == IntPtr.Zero || !IsWindow(sink)) return;

        if (_trayNativeSubclassHandle == sink && _trayNativeSubclassProc is not null) return;

        ReleaseTrayNativeWindowObserver();
        try
        {
            _trayNativeSubclassProc = TrayNativeSubclassProc;
            if (!SetWindowSubclass(sink, _trayNativeSubclassProc, TrayNativeSubclassId, UIntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                _trayNativeSubclassProc = null;
                QueueTrayDiagnostic("TRAY_NATIVE_OBSERVER", "ATTACH_FAILED",
                    $"result=FAILED sinkHwnd={FormatHwnd(sink)} win32Error={error} thread={Environment.CurrentManagedThreadId} observation=comctl32_subclass_forward_only rule=tray_notifyicon_native_message_diagnostic");
                return;
            }

            _trayNativeSubclassHandle = sink;
            QueueTrayDiagnostic("TRAY_NATIVE_OBSERVER", "ATTACHED",
                $"result=OK sinkHwnd={FormatHwnd(sink)} thread={Environment.CurrentManagedThreadId} observation=comctl32_subclass_forward_only rule=tray_notifyicon_native_message_diagnostic");
        }
        catch (Exception ex)
        {
            _trayNativeSubclassHandle = IntPtr.Zero;
            _trayNativeSubclassProc = null;
            QueueTrayDiagnostic("TRAY_NATIVE_OBSERVER", "ATTACH_FAILED",
                $"result=FAILED sinkHwnd={FormatHwnd(sink)} error={ex.GetType().Name} thread={Environment.CurrentManagedThreadId} observation=comctl32_subclass_forward_only rule=tray_notifyicon_native_message_diagnostic");
        }
    }

    private void ReleaseTrayNativeWindowObserver()
    {
        var handle = _trayNativeSubclassHandle;
        var proc = _trayNativeSubclassProc;
        _trayNativeSubclassHandle = IntPtr.Zero;
        _trayNativeSubclassProc = null;
        if (handle == IntPtr.Zero || proc is null) return;
        try { _ = RemoveWindowSubclass(handle, proc, TrayNativeSubclassId); } catch { }
    }

    private IntPtr TrayNativeSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        try
        {
            var message = Message.Create(hWnd, unchecked((int)uMsg), new IntPtr(unchecked((long)wParam.ToUInt64())), lParam);
            ObserveTrayNativeMessage(ref message);
        }
        catch { }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ObserveTrayNativeMessage(ref Message message)
    {
        var sink = _notifyIconSinkHandle;
        if (sink == IntPtr.Zero || message.HWnd != sink) return;

        // NotifyIcon v4 carries the Shell notification code in LOWORD(lParam); older behavior
        // uses the mouse message value directly. LOWORD therefore covers both forms here.
        var notification = unchecked((int)((long)message.LParam & 0xFFFF));
        const int WmRButtonDown = 0x0204;
        const int WmRButtonUp = 0x0205;
        const int WmContextMenu = 0x007B;
        if (notification != WmRButtonDown && notification != WmRButtonUp && notification != WmContextMenu) return;

        var now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastNativeTrayRightTick, now);
        var managed = Interlocked.Read(ref _lastManagedMouseUpTick);
        QueueTrayDiagnostic("TRAY_NATIVE_MESSAGE", "Right",
            $"result=RECEIVED sinkHwnd={FormatHwnd(sink)} msg=0x{message.Msg:X} notification=0x{notification:X} wParam=0x{message.WParam.ToInt64():X} lParam=0x{message.LParam.ToInt64():X} thread={Environment.CurrentManagedThreadId} lastManagedMouseUpAgoMs={(managed == 0 ? -1 : now - managed)} observation=comctl32_subclass_forward_only consumed=False rule=tray_notifyicon_native_message_diagnostic");
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
    private void EmitProcessMemoryDiagnostic()
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        // longrun_memory_rootcause_diagnostic:
        // 既存30秒Tray STA heartbeatのcadenceへ相乗りし、新しいTimer/Taskを作らずに
        // TvAIr processのWorking Setと.NET managed heapを同一時点で観測する。
        // Working Set増加がmanaged heap由来か、WebBrowser/COM等native側かを切り分ける診断専用ログ。
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var gc = GC.GetGCMemoryInfo();
            var managed = GC.GetTotalMemory(forceFullCollection: false);
            var allocatedTotal = GC.GetTotalAllocatedBytes(precise: false);
            var previousAllocatedTotal = Interlocked.Exchange(ref _lastProcessAllocatedBytes, allocatedTotal);
            var allocatedDelta = previousAllocatedTotal == 0
                ? -1
                : Math.Max(0, allocatedTotal - previousAllocatedTotal);
            QueueTrayDiagnostic("APP_MEMORY_DIAGNOSTIC", "PROCESS",
                $"result=OK workingSetBytes={process.WorkingSet64} privateBytes={process.PrivateMemorySize64} virtualBytes={process.VirtualMemorySize64} managedBytes={managed} gcHeapBytes={gc.HeapSizeBytes} gcFragmentedBytes={gc.FragmentedBytes} gcMemoryLoadBytes={gc.MemoryLoadBytes} gcHighMemoryLoadThresholdBytes={gc.HighMemoryLoadThresholdBytes} gcTotalAvailableMemoryBytes={gc.TotalAvailableMemoryBytes} allocatedTotalBytes={allocatedTotal} allocatedDeltaBytes={allocatedDelta} allocatedDeltaWindowSeconds=30 pinnedObjects={gc.PinnedObjectsCount} finalizationPending={gc.FinalizationPendingCount} gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} threads={process.Threads.Count} handles={process.HandleCount} rule=longrun_memory_rootcause_diagnostic");
        }
        catch (Exception ex)
        {
            QueueTrayDiagnostic("APP_MEMORY_DIAGNOSTIC", "PROCESS",
                $"result=FAILED exception={ex.GetType().Name} reason={ex.Message} rule=longrun_memory_rootcause_diagnostic");
        }
#endif
    }

    [System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
    private void QueueTrayDiagnostic(string eventName, string title, string message)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        // Tray diagnostics are development-only. They stay asynchronous so developer LogAdded
        // subscribers never run synchronously on the tray STA thread.
        _trayDiagnostics.Enqueue((eventName, title, message));
        StartTrayDiagnosticDrain();
#endif
    }

#if TVAIR_DEVELOPER_DIAGNOSTICS
    private void StartTrayDiagnosticDrain()
    {
        if (Interlocked.CompareExchange(ref _trayDiagnosticDrainInProgress, 1, 0) != 0) return;
        _ = Task.Run(DrainTrayDiagnostics);
    }

    private void DrainTrayDiagnostics()
    {
        while (true)
        {
            while (_trayDiagnostics.TryDequeue(out var item))
            {
                try { _log.Add(item.EventName, item.Title, item.Message); }
                catch { }
            }

            Volatile.Write(ref _trayDiagnosticDrainInProgress, 0);
            if (_trayDiagnostics.IsEmpty ||
                Interlocked.CompareExchange(ref _trayDiagnosticDrainInProgress, 1, 0) != 0)
            {
                return;
            }
        }
    }
#endif

    private sealed record TrayVisualState(
        bool HasRecording,
        bool HasEpg,
        bool HasTunerActive,
        string EpgRunMode,
        string EpgTargetScope,
        bool EpgCanStart,
        bool EpgCanCancel,
        bool EpgRunSilent)
    {
        public static TrayVisualState Idle { get; } = new(false, false, false, string.Empty, string.Empty, true, false, false);
        public bool IsActive => HasRecording || HasEpg || HasTunerActive;
    }

    private void SetIdle()
    {
        _blinkOn = false;
        if (_icon is null) return;
        ApplyNotifyIconVisual(_idleIcon ?? SystemIcons.Application, $"待機中 (:{_port})");
    }

    private void ApplyNotifyIconVisual(Icon icon, string text)
    {
        if (_icon is null) return;

        // NotifyIconはshell側の実体を持つため、同値の再投影も行わない。
        // 状態変化または点滅で実際に値が変わる時だけ更新する。
        if (!ReferenceEquals(_appliedTrayIcon, icon))
        {
            _icon.Icon = icon;
            _appliedTrayIcon = icon;
        }

        if (!string.Equals(_appliedTrayText, text, StringComparison.Ordinal))
        {
            _icon.Text = text;
            _appliedTrayText = text;
        }
    }



    private static ForegroundWindowSnapshot CaptureForegroundWindowSnapshot()
        => CaptureWindowSnapshot(GetForegroundWindow());

    private static ForegroundWindowSnapshot CaptureWindowSnapshot(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return new ForegroundWindowSnapshot(IntPtr.Zero, 0, 0, "-", "-", "-");
        }

        uint pid = 0;
        var tid = GetWindowThreadProcessId(hwnd, out pid);
        var processName = "-";
        if (pid != 0)
        {
            try { processName = Process.GetProcessById(unchecked((int)pid)).ProcessName; }
            catch { }
        }

        var className = ReadWindowString(hwnd, static (h, sb, cap) => GetClassName(h, sb, cap));
        var title = ReadWindowString(hwnd, static (h, sb, cap) => GetWindowText(h, sb, cap));
        return new ForegroundWindowSnapshot(hwnd, pid, tid, processName, className, title);
    }

    private static string ReadWindowString(IntPtr hwnd, Func<IntPtr, StringBuilder, int, int> reader)
    {
        try
        {
            var sb = new StringBuilder(512);
            _ = reader(hwnd, sb, sb.Capacity);
            return SanitizeWindowField(sb.ToString());
        }
        catch
        {
            return "-";
        }
    }

    private static string SanitizeWindowField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var normalized = value.Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }

    private static string FormatHwnd(IntPtr hwnd)
        => hwnd == IntPtr.Zero ? "0x0" : $"0x{hwnd.ToInt64():X}";

    private sealed record ForegroundWindowSnapshot(
        IntPtr Hwnd,
        uint ProcessId,
        uint ThreadId,
        string ProcessName,
        string ClassName,
        string Title)
    {
        public string ToLogFields()
            => $"hwnd={FormatHwnd(Hwnd)},pid={ProcessId},tid={ThreadId},process={SanitizeWindowField(ProcessName)},class={SanitizeWindowField(ClassName)},title={SanitizeWindowField(Title)}";
    }

    private sealed record TrayPopupPluginAction(string Label, string Route, string Kind);

    private sealed record TrayPopupRequest(
        long Sequence,
        int X,
        int Y,
        TrayVisualState VisualState,
        IReadOnlyList<TrayPopupPluginAction> Plugins,
        long RequestedAtTick);

    private sealed class TrayPopupSession
    {
        public TrayPopupSession(long sequence, TrayPopupRequest request)
        {
            Sequence = sequence;
            Request = request;
        }

        public long Sequence { get; }
        public TrayPopupRequest Request { get; }
        public volatile bool CancelRequested;
        public IntPtr OwnerHwnd;
        public Thread? Thread;
    }

    private sealed class PopupOwnerWindow : NativeWindow
    {
        public void Create()
        {
            var cp = new CreateParams
            {
                Caption = "TvAIr.TrayPopupOwner",
                X = -32000,
                Y = -32000,
                Width = 1,
                Height = 1,
                Style = WsPopup,
                ExStyle = WsExToolWindow,
            };
            CreateHandle(cp);
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("Popup owner HWND creation failed.");
        }

        public void Destroy()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private void OpenSettingsWindowFromEntry(string source)
    {
        // トレイ入口は本体画面を複製せず、共通設定UIだけを表示する専用表示モードを直接開く。
        // 設定項目・保存・検証・テーマはWeb設定正本を共有し、トレイ専用実装は持たない。
        try
        {
            OpenBrowser($"http://localhost:{_port}/?settingsHost=1&entry={Uri.EscapeDataString(source)}");
        }
        catch { }
    }
    private void DisposeTrayObjects()
    {
        // NotifyIcon and popup tracking have separate STA ownership. Shutdown only requests the
        // bounded active popup session to cancel; the popup thread owns its HWND/HMENU cleanup.
        Volatile.Write(ref _trayPopupShuttingDown, 1);
        IntPtr popupOwner = IntPtr.Zero;
        lock (_trayPopupSync)
        {
            _pendingTrayPopup = null;
            if (_activeTrayPopup is not null)
            {
                _activeTrayPopup.CancelRequested = true;
                popupOwner = Interlocked.CompareExchange(ref _activeTrayPopup.OwnerHwnd, IntPtr.Zero, IntPtr.Zero);
            }
        }
        if (popupOwner != IntPtr.Zero && IsWindow(popupOwner))
        {
            _ = PostMessage(popupOwner, WmCancelMode, IntPtr.Zero, IntPtr.Zero);
            _ = PostMessage(popupOwner, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        try
        {
            if (_timer is not null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }
        catch { }

        ReleaseTrayNativeWindowObserver();

        try
        {
            if (_icon is not null)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }
        }
        catch { }

        try { _idleIcon?.Dispose(); } catch { }
        try { _recordingIcon?.Dispose(); } catch { }
        _idleIcon = null;
        _recordingIcon = null;
        _appliedTrayIcon = null;
        _appliedTrayText = string.Empty;
        Volatile.Write(ref _trayMenuOpen, 0);
        _started = false;
    }

    public void Dispose()
    {
        DisposeTrayObjects();
    }
}
