using System.Drawing;
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

    private Thread? _thread;
    private NotifyIcon? _icon;
    private System.Windows.Forms.Timer? _timer;
    private Icon? _idleIcon;
    private Icon? _recordingIcon;
    private bool _blinkOn;
    private bool _isActive;
    private SettingsWindow? _settingsWindow;
    private const string MenuLegacyEntryCleanupContract = "release_contract";
    private const string MenuLabelHelp = "ヘルプ";
    private const string MenuLabelVersion = "バージョン情報";
    private const string MenuLabelExit = "TvAIr終了";
    private const string MenuActionHelpOpen = "help-open";
    private const string MenuActionVersionOpen = "version-open";
    private const string MenuActionAppExit = "app-exit";
    private volatile bool _started;

    public TrayIconService(int port, ReservationStore reservationStore, TunerPool tunerPool, EpgScheduler epgScheduler, LogRepository log, PluginDefaultMenuActionService pluginMenuActions)
    {
        _port = port;
        _reservationStore = reservationStore;
        _tunerPool = tunerPool;
        _epgScheduler = epgScheduler;
        _log = log;
        _pluginMenuActions = pluginMenuActions;
    }

    public void Start()
    {
        if (_started)
        {
            try { _log.Add("TRAY_ICON", "START_SKIPPED", "reason=already_started rule=release_contract"); } catch { }
            return;
        }
        _started = true;
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

            var menu = new ContextMenuStrip();

            var openItem = new ToolStripMenuItem("TvAIrを開く");
            openItem.Click += (_, _) => OpenBrowser($"http://localhost:{_port}");

            var epgMenu = new ToolStripMenuItem("EPG取得");

            var silentEpgItem = new ToolStripMenuItem("全局取得");
            silentEpgItem.Click += (_, _) => StartSilentEpg(silentEpgItem, "All", "全局取得");

            var silentEpgGrItem = new ToolStripMenuItem("地上波のみ取得");
            silentEpgGrItem.Click += (_, _) => StartSilentEpg(silentEpgGrItem, "GR", "地上波のみ取得");

            var silentEpgBsItem = new ToolStripMenuItem("BSのみ取得");
            silentEpgBsItem.Click += (_, _) => StartSilentEpg(silentEpgBsItem, "BS", "BSのみ取得");

            var silentEpgCsItem = new ToolStripMenuItem("CSのみ取得");
            silentEpgCsItem.Click += (_, _) => StartSilentEpg(silentEpgCsItem, "CS", "CSのみ取得");

            var silentEpgBsCsItem = new ToolStripMenuItem("BS/CSのみ取得");
            silentEpgBsCsItem.Click += (_, _) => StartSilentEpg(silentEpgBsCsItem, "BSCS", "BS/CSのみ取得");

            var cancelEpgItem = new ToolStripMenuItem("取得キャンセル");
            cancelEpgItem.Click += (_, _) => CancelSilentEpg(cancelEpgItem);

            epgMenu.DropDownItems.Add(silentEpgItem);
            epgMenu.DropDownItems.Add(silentEpgGrItem);
            epgMenu.DropDownItems.Add(silentEpgBsItem);
            epgMenu.DropDownItems.Add(silentEpgCsItem);
            epgMenu.DropDownItems.Add(silentEpgBsCsItem);
            epgMenu.DropDownItems.Add(cancelEpgItem);

            epgMenu.DropDownOpening += (_, _) =>
            {
                var run = _epgScheduler.GetRunState();
                var canStart = run.CanStart;
                silentEpgItem.Enabled = canStart;
                silentEpgGrItem.Enabled = canStart;
                silentEpgBsItem.Enabled = canStart;
                silentEpgCsItem.Enabled = canStart;
                silentEpgBsCsItem.Enabled = canStart;
                cancelEpgItem.Enabled = run.CanCancel;
                cancelEpgItem.Text = run.CanCancel
                    ? $"取得キャンセル（{run.UiMode}/{run.TargetScope}）"
                    : "取得キャンセル";
            };

            var pluginMenu = new ToolStripMenuItem("プラグイン");
            pluginMenu.DropDownOpening += (_, _) => PopulatePluginMenu(pluginMenu);
            PopulatePluginMenu(pluginMenu);

            var settingsItem = new ToolStripMenuItem("設定")
            {
                Tag = MenuLegacyEntryCleanupContract + ":settings-open:tray"
            };
            settingsItem.Click += (_, _) => OpenSettingsWindowFromEntry("tray");
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(epgMenu);
            menu.Items.Add(pluginMenu);
            menu.Items.Add(settingsItem);
            AddMenuTail(menu.Items, includeExit: true);

            _icon = new NotifyIcon
            {
                Icon = _idleIcon,
                Text = $"TvAIr (:{_port})",
                ContextMenuStrip = menu,
                Visible = true,
            };

            _icon.DoubleClick += (_, _) => openItem.PerformClick();
            _log.Add("TRAY_ICON", "VISIBLE", $"result=OK port={_port} thread={Environment.CurrentManagedThreadId} rule=release_contract");

            _timer = new System.Windows.Forms.Timer
            {
                Interval = 800
            };
            _timer.Tick += (_, _) =>
            {
                EnsureVisible();
                RefreshState();
            };

            // 起動直後は必ず待機中アイコンに固定
            SetIdle();
            _timer.Start();

            Application.Run();
        });

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
    }

    private void AddMenuTail(ToolStripItemCollection items, bool includeExit)
    {
        items.Add(new ToolStripSeparator());
        items.Add(CreateHelpMenuItem("tray"));
        items.Add(CreateVersionMenuItem("tray"));
        if (!includeExit) return;
        items.Add(new ToolStripSeparator());
        items.Add(CreateExitMenuItem("tray"));
    }

    private ToolStripMenuItem CreateHelpMenuItem(string surface)
    {
        var item = new ToolStripMenuItem(MenuLabelHelp)
        {
            Tag = $"{MenuLegacyEntryCleanupContract}:{MenuActionHelpOpen}:{surface}"
        };
        item.Click += (_, _) => OpenBrowser($"http://localhost:{_port}/help.html");
        return item;
    }

    private ToolStripMenuItem CreateVersionMenuItem(string surface)
    {
        var item = new ToolStripMenuItem(MenuLabelVersion)
        {
            Tag = $"{MenuLegacyEntryCleanupContract}:{MenuActionVersionOpen}:{surface}"
        };
        item.Click += (_, _) =>
        {
            var version = Application.ProductVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                version = typeof(TrayIconService).Assembly.GetName().Version?.ToString() ?? "unknown";
            }
            MessageBox.Show($"TvAIr\n\nバージョン: {version}", "TvAIr バージョン情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        return item;
    }

    private ToolStripMenuItem CreateExitMenuItem(string surface)
    {
        var item = new ToolStripMenuItem(MenuLabelExit)
        {
            Tag = $"{MenuLegacyEntryCleanupContract}:{MenuActionAppExit}:{surface}"
        };
        item.Click += (_, _) =>
        {
            DisposeTrayObjects();
            Environment.Exit(0);
        };
        return item;
    }

    private void PopulatePluginMenu(ToolStripMenuItem pluginMenu)
    {
        try
        {
            pluginMenu.DropDownItems.Clear();
            var actions = _pluginMenuActions.ResolveActions("tray");
            foreach (var action in actions)
            {
                if (string.IsNullOrWhiteSpace(action.RouteSegment)) continue;
                var item = new ToolStripMenuItem(string.IsNullOrWhiteSpace(action.Label) ? action.Name : action.Label);
                var route = action.RouteSegment;
                var kind = action.Kind ?? string.Empty;
                item.Click += (_, _) => ExecutePluginMenuAction(route, kind);
                pluginMenu.DropDownItems.Add(item);
            }
            pluginMenu.Visible = pluginMenu.DropDownItems.Count > 0;
        }
        catch
        {
            pluginMenu.DropDownItems.Clear();
            pluginMenu.Visible = false;
        }
    }

    private void ExecutePluginMenuAction(string route, string kind)
    {
        var safeRoute = (route ?? string.Empty).Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(safeRoute)) return;

        if (kind.Equals(PluginMenuActionKinds.Page, StringComparison.OrdinalIgnoreCase)
            || kind.Equals(PluginMenuActionKinds.Settings, StringComparison.OrdinalIgnoreCase))
        {
            OpenBrowser($"http://localhost:{_port}/plugin-menu/{Uri.EscapeDataString(safeRoute)}?source=context");
            return;
        }

        RequestSilentPluginMenu(safeRoute);
    }

    private void StartSilentEpg(ToolStripMenuItem menuItem, string targetScope, string label)
    {
        try
        {
            menuItem.Enabled = false;
            var started = _epgScheduler.TriggerNow($"TrayMenu.SilentEpg.{targetScope}", silent: true, targetScope: targetScope);
            var block = started ? null : _epgScheduler.GetLastStartBlockInfo(targetScope);
            _log.Add("TRAY_EPG_SILENT", "EPG", started
                ? $"タスクトレイ右クリックメニューからEPG取得（サイレント）を開始しました。targetScope={targetScope} label={label} uiMode=Silent cancelRoute=TrayOnly rule=release_contract"
                : block is not null
                    ? $"タスクトレイ右クリックメニューからEPG取得（サイレント）が開始できませんでした。targetScope={targetScope} label={label} reason={block.Reason} action=show_short_dialog rule=release_contract"
                    : $"タスクトレイ右クリックメニューからEPG取得（サイレント）が要求されましたが、既に取得中です。targetScope={targetScope} label={label} action=reject_start_cancel_required rule=release_contract");

            if (!started && block is not null)
            {
                TvAIrNotificationDialog.Show(block.DisplayMessage, "時間をおいてお試しください。");
            }

            // サイレント指定のため、開始できた場合もブラウザ・右下進捗パネルは開かない。
            RefreshState();
        }
        catch (Exception ex)
        {
            try
            {
                _log.Add("TRAY_EPG_SILENT_ERROR", "EPG", $"EPG取得（サイレント）開始時に例外: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }
        finally
        {
            // EPG取得中の開始メニュー無効化は DropDownOpening で一元制御する。
        }
    }

    private void CancelSilentEpg(ToolStripMenuItem menuItem)
    {
        try
        {
            menuItem.Enabled = false;
            var accepted = _epgScheduler.Cancel("TrayMenu.EpgCancel");
            _log.Add("TRAY_EPG_CANCEL", "EPG", accepted
                ? "タスクトレイ右クリックメニューからEPG取得キャンセルを要求しました。cancelRoute=TrayOrWidget commonRoute=CancelCurrentEpgRun rule=release_contract"
                : "タスクトレイ右クリックメニューからEPG取得キャンセルを要求しましたが、実行中ではありません。rule=release_contract");
            RefreshState();
        }
        catch (Exception ex)
        {
            try { _log.Add("TRAY_EPG_CANCEL_ERROR", "EPG", $"EPG取得キャンセル時に例外: {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
        finally
        {
            menuItem.Enabled = true;
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

    private void RefreshState()
    {
        var hasRecording = false;
        var hasEpg = false;
        var hasTunerActive = false;
        var epgRunStateRunning = false;
        var epgRunMode = string.Empty;

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

        try
        {
            var run = _epgScheduler.GetRunState();
            epgRunStateRunning = run.IsRunning;
            epgRunMode = run.UiMode ?? string.Empty;
            hasEpg = hasEpg || epgRunStateRunning;
        }
        catch { }

        _isActive = hasRecording || hasEpg || hasTunerActive;

        if (!_isActive)
        {
            SetIdle();
            return;
        }

        if (_icon is null) return;

        // release_contract: TvAIrEpgRec のタスクバー表示をOFFにできるため、TvAIr本体トレイアイコンを
        // 録画・通常EPG取得・サイレントEPG取得・録画前EPG確認の代表インジケータとして点滅させる。
        _blinkOn = !_blinkOn;
        _icon.Icon = _blinkOn ? (_recordingIcon ?? _idleIcon ?? SystemIcons.Application)
                              : (_idleIcon ?? SystemIcons.Application);
        _icon.Text = hasRecording
            ? $"録画中 (:{_port})"
            : hasEpg
                ? $"EPG取得中{(string.IsNullOrWhiteSpace(epgRunMode) ? string.Empty : $"/{epgRunMode}")} (:{_port})"
                : $"チューナー使用中 (:{_port})";
    }

    private void SetIdle()
    {
        _blinkOn = false;
        if (_icon is null) return;
        _icon.Icon = _idleIcon ?? SystemIcons.Application;
        _icon.Text = $"待機中 (:{_port})";
    }


    private void OpenSettingsWindowFromEntry(string source)
    {
        // MenuLegacyEntryCleanupContract: tray/right-click and Web hamburger/context/page menus share
        // the same command identity and dispatch semantics. Do not add a tray-only settings tree here;
        // page-local menu fallbacks are removed; EPG silent handling is the only intentional surface split.
        try
        {
            if (_settingsWindow is not null && !_settingsWindow.IsDisposed)
            {
                if (_settingsWindow.WindowState == FormWindowState.Minimized)
                {
                    _settingsWindow.WindowState = FormWindowState.Normal;
                }
                _settingsWindow.BringToFront();
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_port);
            _settingsWindow.Tag = MenuLegacyEntryCleanupContract + ":settings-open:" + source;
            _settingsWindow.FormClosed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.BringToFront();
            _settingsWindow.Activate();
        }
        catch { }
    }
    private void DisposeTrayObjects()
    {
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
        _started = false;
    }

    public void Dispose()
    {
        DisposeTrayObjects();
    }
}
