/* release_contract: 旧TVTest録画起動APIは撤去。TvTestLauncherはEPG取得・プロセス維持用途に限定。 */
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Options;
using TvAIr.Core;

namespace TvAIr.Tuner;

/// <summary>
/// TVTest.exe の起動を担当する。
/// 起動パラメータの組み立てと Process 管理のみに責務を限定する。
///
/// EPGキャプチャ用オプション:
///   /rec           : 録画モード
///   /recfile       : 録画ファイルパス
///   /recduration   : 録画秒数
///   /recdelay 8    : チャンネルロック完了を待つための録画開始遅延（秒）
///   /recexit       : 録画終了時にTVTest自動終了
///   /noview        : 映像表示を無効（DirectShowは有効のまま）
///   /silent        : エラーダイアログ抑止
///   /noplugin      : プラグイン読み込み禁止
///   /min           : 最小化状態で起動（設定で ON/OFF 可能）
///   /nodshow       : DirectShow無効化・CPU負荷軽減（EPG取得側のみ設定で ON/OFF 可能。録画起動では付けない）
///
/// release_contract: TVTest/LIVETest巻き込み確認性を優先し、Windows側の非表示化は行わない。
/// /min により最小化起動し、タスクバー上でTVTestの活動状態を確認できるようにする。
/// </summary>

public sealed record ViewerWindowStateSnapshot(
    bool Captured,
    int ProcessId,
    string State,
    int Left,
    int Top,
    int Width,
    int Height,
    string Reason,
    string Diagnostics)
{
    public static ViewerWindowStateSnapshot Skipped(int processId, string reason, string diagnostics)
        => new(false, processId, "unknown", 0, 0, 0, 0, reason, diagnostics);
}

public sealed class TvTestLauncher
{
    private readonly IniSettingsService _ini;
    private readonly bool _dryRun;
    private readonly LogRepository _log;

    public TvTestLauncher(IniSettingsService ini, IOptions<TvTestSettings> tvTestOpts, LogRepository log)
    {
        _ini    = ini;
        _dryRun = tvTestOpts.Value.DryRun;
        _log = log;
    }

    // release_contract: TVTest起動はEPG取得・活動維持用途に限定。予約録画本線はDirectRecorder側で扱う。
    // このクラスはTVTestプロセス維持・EPG取得用途に限定し、本番録画はDirectRecorderへ集約する。


    /// <summary>TvAIr管理の視聴用TVTest/LIVETestを、AIrCon viewerStart 用の軽量API契約として可視起動する。</summary>
    public LaunchResult StartViewer(string bonDriverFileName, string did, string channelArgument, bool preserveViewerWindowState = false, string? viewerActivation = null, ViewerWindowStateSnapshot? restoreWindowState = null)
    {
        var bonDriverPath = ResolveBonDriverPath(bonDriverFileName);
        var didArg = string.IsNullOrWhiteSpace(did) ? string.Empty : $" /DID {did}";
        var exe = !string.IsNullOrWhiteSpace(_ini.ViewingTvTestExecutablePath) ? _ini.ViewingTvTestExecutablePath : _ini.TvTestExecutablePath;
        var viewerChannelArgument = BuildViewerLaunchChannelArgument(channelArgument, grFallbackFromCh: true);
        if (string.IsNullOrWhiteSpace(viewerChannelArgument))
            viewerChannelArgument = RemoveNonLaunchIdentityAndSilentArguments(channelArgument);
        var args = NormalizeArgumentWhitespace($"/d \"{bonDriverPath}\"{didArg} {viewerChannelArgument}");
        var workingDirectory = Path.GetDirectoryName(exe) ?? string.Empty;

        _log.Add("VIEWER_TVTEST_ARGUMENT", "Viewer",
            $"selected=chspaceChiSid exeName={SafeLog(Path.GetFileName(exe))} workingDirectory=omitted bonDriver={SafeLog(bonDriverFileName)} bonDriverPath=omitted did={SafeLog(did)} finalArguments={SafeLog(CompactViewerCommandLineForAudit(args))} sourceChannelArgument={SafeLog(channelArgument)} silent=False sidInInitialLaunch={(!string.IsNullOrWhiteSpace(GetCommandTokenValue(viewerChannelArgument, "/sid"))).ToString()} identityArgsInInitialLaunch=sid_only copyCommand=omitted preserveViewerWindowState={preserveViewerWindowState} viewerActivation={SafeLog(viewerActivation)} restoreWindowStateRequested={(restoreWindowState?.Captured == true)} rule=release_contract");

        var result = LaunchViewerCore(exe, args, preserveViewerWindowState, viewerActivation, restoreWindowState);
        if (result.Success && result.ProcessId > 0)
            TvAirManagedProcessRegistry.RegisterViewer(result.ProcessId, did, bonDriverFileName);
        return result;
    }

    /// <summary>
    /// 既存のTvAIr管理viewer TVTestを閉じずに、TVTestの単一インスタンスコマンドラインへ
    /// チャンネル指定だけを渡す軽量再選局契約。AIrCon側はWin32/TVTest直接操作をしない。
    /// </summary>
    public LaunchResult RetuneExistingViewer(int existingProcessId, string bonDriverFileName, string did, string channelArgument, bool preserveViewerWindowState = false, string? viewerActivation = null)
    {
        if (existingProcessId <= 0)
            return new LaunchResult(false, existingProcessId, "existing viewer pid is empty");

        try
        {
            using var existing = Process.GetProcessById(existingProcessId);
            if (existing.HasExited)
            {
                _log.Add("VIEWER_RETUNE_EXISTING", "Viewer", $"result=FAILED method=tvtest_single_instance_commandline pid={existingProcessId} reason=existing_process_exited rule=release_contract");
                return new LaunchResult(false, existingProcessId, "existing viewer process exited");
            }
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_RETUNE_EXISTING", "Viewer", $"result=FAILED method=tvtest_single_instance_commandline pid={existingProcessId} reason=existing_process_not_found message={SafeLog(ex.Message)} rule=release_contract");
            return new LaunchResult(false, existingProcessId, "existing viewer process not found");
        }

        var bonDriverPath = ResolveBonDriverPath(bonDriverFileName);
        var didArg = string.IsNullOrWhiteSpace(did) ? string.Empty : $" /DID {did}";
        var exe = !string.IsNullOrWhiteSpace(_ini.ViewingTvTestExecutablePath) ? _ini.ViewingTvTestExecutablePath : _ini.TvTestExecutablePath;
        var viewerChannelArgument = BuildViewerLaunchChannelArgument(channelArgument, grFallbackFromCh: true);
        if (string.IsNullOrWhiteSpace(viewerChannelArgument))
            viewerChannelArgument = RemoveNonLaunchIdentityAndSilentArguments(channelArgument);
        // /s は公式コマンドラインの「既に起動している場合、複数起動しない」。
        // TvAIr本体の安全な再選局入口として使い、既存viewerを閉じない。
        var args = NormalizeArgumentWhitespace($"/s /d \"{bonDriverPath}\"{didArg} {viewerChannelArgument}");
        var workingDirectory = Path.GetDirectoryName(exe) ?? string.Empty;

        _log.Add("VIEWER_RETUNE_EXISTING_COMMAND", "Viewer",
            $"method=tvtest_single_instance_commandline existingPid={existingProcessId} exeName={SafeLog(Path.GetFileName(exe))} workingDirectory=omitted bonDriver={SafeLog(bonDriverFileName)} bonDriverPath=omitted did={SafeLog(did)} arguments={SafeLog(CompactViewerCommandLineForAudit(args))} sourceChannelArgument={SafeLog(channelArgument)} sidInRetuneCommand={(!string.IsNullOrWhiteSpace(GetCommandTokenValue(viewerChannelArgument, "/sid"))).ToString()} preserveViewerWindowState={preserveViewerWindowState} viewerActivation={SafeLog(viewerActivation)} auditNote=paths_omitted_current_state_is_registry_session rule=release_contract");

        if (_dryRun)
        {
            _log.Add("VIEWER_RETUNE_EXISTING", "DryRun", $"result=OK method=tvtest_single_instance_commandline existingPid={existingProcessId} helperPid=0 dryRun=True rule=release_contract");
            return new LaunchResult(true, existingProcessId, $"DryRun retune existing PID={existingProcessId}: {exe} {args}");
        }
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            _log.Add("VIEWER_RETUNE_EXISTING", "Viewer", $"result=FAILED method=tvtest_single_instance_commandline existingPid={existingProcessId} reason=exe_not_found exe={SafeLog(exe)} rule=release_contract");
            return new LaunchResult(false, existingProcessId, $"Viewer executable not found: {exe}");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var tunerDeviceAccess = TunerDeviceAccessGate.Enter("VIEWER_RETUNE", msg => _log.Add("TUNER_DEVICE_LOCK", "Viewer", msg));
            using var helper = Process.Start(psi);
            var helperPid = helper?.Id ?? 0;
            var helperExited = helper is null;
            try { if (helper is not null) helperExited = helper.WaitForExit(2000) || helper.HasExited; } catch { helperExited = false; }

            if (!helperExited && helper is not null)
            {
                var duplicateGuard = "not_needed";
                try
                {
                    duplicateGuard = helper.CloseMainWindow() ? "close_requested" : "close_not_supported";
                    if (!helper.WaitForExit(500))
                    {
                        helper.Kill(entireProcessTree: false);
                        duplicateGuard = "killed_unexited_helper";
                    }
                }
                catch (Exception cleanupEx)
                {
                    duplicateGuard = "cleanup_error_" + cleanupEx.GetType().Name;
                }
                _log.Add("VIEWER_RETUNE_EXISTING", "Viewer", $"result=FAILED method=tvtest_single_instance_commandline existingPid={existingProcessId} helperPid={helperPid} helperExited=False reason=helper_process_did_not_exit duplicateGuard={SafeLog(duplicateGuard)} action=deny_without_restart preserveViewerWindowState={preserveViewerWindowState} normalWindowActivationSuppressed=True rule=release_contract");
                return new LaunchResult(false, existingProcessId, "Retune helper process did not exit; TvAIr prevented duplicate TVTest and kept the existing viewer alive.");
            }

            var existingAliveAfter = false;
            try
            {
                using var existingAfter = Process.GetProcessById(existingProcessId);
                existingAliveAfter = !existingAfter.HasExited;
            }
            catch { existingAliveAfter = false; }
            if (!existingAliveAfter)
            {
                _log.Add("VIEWER_RETUNE_EXISTING", "Viewer", $"result=FAILED method=tvtest_single_instance_commandline existingPid={existingProcessId} helperPid={helperPid} helperExited=True reason=existing_process_lost_after_command action=stale_release_then_restart_recovery preserveViewerWindowState={preserveViewerWindowState} normalWindowActivationSuppressed=True rule=release_contract");
                return new LaunchResult(false, existingProcessId, "Existing viewer process disappeared after retune command.");
            }

            var survivalOk = true;
            const int survivalMonitorMs = 4000;
            const int survivalStepMs = 500;
            for (var waited = 0; waited < survivalMonitorMs; waited += survivalStepMs)
            {
                Thread.Sleep(survivalStepMs);
                try
                {
                    using var existingProbe = Process.GetProcessById(existingProcessId);
                    if (existingProbe.HasExited)
                    {
                        survivalOk = false;
                        break;
                    }
                }
                catch
                {
                    survivalOk = false;
                    break;
                }
            }
            if (!survivalOk)
            {
                _log.Add("VIEWER_RETUNE_SURVIVAL", "Viewer", $"result=FAILED existingPid={existingProcessId} monitorMs={survivalMonitorMs} reason=existing_process_lost_after_retune action=stale_release_then_restart_recovery rule=release_contract");
                return new LaunchResult(false, existingProcessId, "Existing viewer process disappeared during retune survival monitor.");
            }

            TvAirManagedProcessRegistry.RegisterViewer(existingProcessId, did, bonDriverFileName);
            _log.Add("VIEWER_RETUNE_SURVIVAL", "Viewer", $"result=OK existingPid={existingProcessId} monitorMs={survivalMonitorMs} rule=release_contract");
            _log.Add("VIEWER_RETUNE_EXISTING", "Viewer", $"result=OK method=tvtest_single_instance_commandline existingPid={existingProcessId} helperPid={helperPid} helperExited=True existingAliveAfter=True duplicateGuard=not_needed preserveViewerWindowState={preserveViewerWindowState} normalWindowActivationSuppressed=True rule=release_contract");
            return new LaunchResult(true, existingProcessId, $"Retune command sent to existing viewer PID={existingProcessId}: {exe} {args}");
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_RETUNE_EXISTING", "Viewer", $"result=FAILED method=tvtest_single_instance_commandline existingPid={existingProcessId} reason=process_start_exception message={SafeLog(ex.Message)} exe={SafeLog(exe)} arguments={SafeLog(args)} rule=release_contract");
            return new LaunchResult(false, existingProcessId, $"Retune command exception: {ex.Message} / {exe} {args}");
        }
    }

    private static string RemoveNonLaunchIdentityAndSilentArguments(string args)
    {
        // /nid and /tsid are audit identity fields for TvAIr. /sid is a TVTest launch selector and must be preserved.
        return RemoveCommandToken(RemoveCommandToken(RemoveCommandToken(args, "/silent"), "/nid"), "/tsid");
    }

    private static string BuildViewerLaunchChannelArgument(string channelArgument, bool grFallbackFromCh)
    {
        var sid = GetCommandTokenValue(channelArgument, "/sid");
        var sidArg = string.IsNullOrWhiteSpace(sid) ? string.Empty : $" /sid {sid}";
        var chspace = GetCommandTokenValue(channelArgument, "/chspace");
        var chi = GetCommandTokenValue(channelArgument, "/chi");
        if (!string.IsNullOrWhiteSpace(chspace) && !string.IsNullOrWhiteSpace(chi))
            return NormalizeArgumentWhitespace($"/chspace {chspace} /chi {chi}{sidArg}");

        if (grFallbackFromCh)
        {
            var ch = GetCommandTokenValue(channelArgument, "/ch");
            if (!string.IsNullOrWhiteSpace(ch))
                return NormalizeArgumentWhitespace($"/chspace 0 /chi {ch}{sidArg}");
        }

        return string.Empty;
    }

    private static string? GetCommandTokenValue(string args, string token)
    {
        var tokens = SplitCommandLineLoose(args);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], token, StringComparison.OrdinalIgnoreCase))
                return i + 1 < tokens.Count ? tokens[i + 1] : null;
        }
        return null;
    }

    private static string RemoveCommandToken(string args, string token)
    {
        var tokens = SplitCommandLineLoose(args);
        var kept = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], token, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("/", StringComparison.Ordinal)) i++;
                continue;
            }
            kept.Add(QuoteArgumentIfNeeded(tokens[i]));
        }
        return NormalizeArgumentWhitespace(string.Join(" ", kept));
    }

    private static List<string> SplitCommandLineLoose(string args)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return result;
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        foreach (var ch in args)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static string QuoteArgumentIfNeeded(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "\"\"";
        return value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
    }

    private static string NormalizeArgumentWhitespace(string value)
        => string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));



    /// <summary>
    /// release_contract: TVTest の単一インスタンス受け口が「現在アクティブなTVTest」へ吸われる環境向けに、
    /// viewerProfile に紐付いた既存PIDを一時的に前面化してから /s コマンドを投げる。
    /// ここでは exe 名を変えず、ini 複製も作らない。
    /// </summary>
    public bool PrepareViewerProfileCommandTarget(int processId, string viewerProfileId, string reason)
    {
        if (processId <= 0)
        {
            _log.Add("VIEWER_PROFILE_PID_BIND", "Viewer", $"result=FAILED action=prepare_target pid={processId} viewerProfile={SafeLog(viewerProfileId)} reason=empty_pid policy=foreground_target_before_unscoped_command rule=release_contract");
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                _log.Add("VIEWER_PROFILE_PID_BIND", "Viewer", $"result=FAILED action=prepare_target pid={processId} viewerProfile={SafeLog(viewerProfileId)} reason=process_exited policy=foreground_target_before_unscoped_command rule=release_contract");
                return false;
            }

            var hwnd = process.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                process.Refresh();
                hwnd = process.MainWindowHandle;
            }
            if (hwnd == IntPtr.Zero)
            {
                _log.Add("VIEWER_PROFILE_PID_BIND", "Viewer", $"result=FAILED action=prepare_target pid={processId} viewerProfile={SafeLog(viewerProfileId)} reason=main_window_handle_unavailable policy=foreground_target_before_unscoped_command rule=release_contract");
                return false;
            }

            var foregroundApplied = SetForegroundWindow(hwnd);
            _log.Add("VIEWER_PROFILE_PID_BIND", "Viewer", $"result={(foregroundApplied ? "OK" : "WARN")} action=prepare_target pid={processId} viewerProfile={SafeLog(viewerProfileId)} reason={SafeLog(reason)} foregroundApplied={foregroundApplied} policy=foreground_target_before_unscoped_command noExeRename=True noIniClone=True rule=release_contract");
            return true;
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_PROFILE_PID_BIND", "Viewer", $"result=FAILED action=prepare_target pid={processId} viewerProfile={SafeLog(viewerProfileId)} reason={SafeLog(ex.GetType().Name)} message={SafeLog(ex.Message)} policy=foreground_target_before_unscoped_command rule=release_contract");
            return false;
        }
    }

    public ViewerWindowStateSnapshot CaptureViewerWindowState(int processId, string reason)
    {
        if (processId <= 0)
        {
            _log.Add("VIEWER_WINDOW_STATE_CAPTURE", "Viewer", $"result=SKIPPED pid={processId} reason={SafeLog(reason)} detail=empty_pid rule=release_contract");
            return ViewerWindowStateSnapshot.Skipped(processId, reason, "empty_pid");
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                _log.Add("VIEWER_WINDOW_STATE_CAPTURE", "Viewer", $"result=SKIPPED pid={processId} reason={SafeLog(reason)} detail=process_exited rule=release_contract");
                return ViewerWindowStateSnapshot.Skipped(processId, reason, "process_exited");
            }

            var hwnd = process.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                process.Refresh();
                hwnd = process.MainWindowHandle;
            }
            if (hwnd == IntPtr.Zero)
            {
                _log.Add("VIEWER_WINDOW_STATE_CAPTURE", "Viewer", $"result=FAILED pid={processId} reason={SafeLog(reason)} detail=main_window_handle_unavailable rule=release_contract");
                return ViewerWindowStateSnapshot.Skipped(processId, reason, "main_window_handle_unavailable");
            }

            var state = "normal";
            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref placement))
            {
                if (placement.showCmd == ShowWindowCommands.SW_SHOWMAXIMIZED) state = "maximized";
                else if (placement.showCmd == ShowWindowCommands.SW_SHOWMINIMIZED) state = "minimized";
            }

            var left = 0;
            var top = 0;
            var width = 0;
            var height = 0;
            if (GetWindowRect(hwnd, out var rect))
            {
                left = rect.Left;
                top = rect.Top;
                width = Math.Max(0, rect.Right - rect.Left);
                height = Math.Max(0, rect.Bottom - rect.Top);
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var dx = Math.Abs(rect.Left - info.rcMonitor.Left) + Math.Abs(rect.Top - info.rcMonitor.Top) +
                                 Math.Abs(rect.Right - info.rcMonitor.Right) + Math.Abs(rect.Bottom - info.rcMonitor.Bottom);
                        if (dx <= 8 && state != "minimized") state = "fullscreen";
                    }
                }
            }

            _log.Add("VIEWER_WINDOW_STATE_CAPTURE", "Viewer", $"result=OK pid={processId} reason={SafeLog(reason)} windowState={SafeLog(state)} bounds={left},{top},{width}x{height} source=before_restart_fallback rule=release_contract");
            return new ViewerWindowStateSnapshot(true, processId, state, left, top, width, height, reason, "OK");
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_WINDOW_STATE_CAPTURE", "Viewer", $"result=FAILED pid={processId} reason={SafeLog(reason)} detail={SafeLog(ex.GetType().Name)} message={SafeLog(ex.Message)} rule=release_contract");
            return ViewerWindowStateSnapshot.Skipped(processId, reason, ex.GetType().Name);
        }
    }

    private void RestoreViewerWindowStateAfterLaunch(int processId, ViewerWindowStateSnapshot? snapshot, bool restoreRequested)
    {
        if (!restoreRequested)
        {
            _log.Add("VIEWER_WINDOW_STATE_RESTORE", "Viewer", $"result=SKIPPED pid={processId} reason=no_restore_snapshot_requested preserveViewerWindowState=True-or-launch_without_previous_snapshot rule=release_contract");
            return;
        }
        if (snapshot is null || !snapshot.Captured)
        {
            _log.Add("VIEWER_WINDOW_STATE_RESTORE", "Viewer", $"result=SKIPPED pid={processId} requestedState=unknown reason=no_captured_state sourcePid={(snapshot?.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-")} rule=release_contract");
            return;
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            var hwnd = IntPtr.Zero;
            for (var i = 0; i < 20; i++)
            {
                if (process.HasExited) break;
                process.Refresh();
                hwnd = process.MainWindowHandle;
                if (hwnd != IntPtr.Zero) break;
                Thread.Sleep(250);
            }
            if (hwnd == IntPtr.Zero || process.HasExited)
            {
                _log.Add("VIEWER_WINDOW_STATE_RESTORE", "Viewer", $"result=FAILED pid={processId} requestedState={SafeLog(snapshot.State)} sourcePid={snapshot.ProcessId} reason=main_window_handle_unavailable_after_launch rule=release_contract");
                return;
            }

            var state = (snapshot.State ?? "normal").Trim().ToLowerInvariant();
            var method = "showwindow";
            if (state == "fullscreen")
            {
                ShowWindow(hwnd, ShowWindowCommands.SW_RESTORE);
                SetForegroundWindow(hwnd);
                Thread.Sleep(200);
                SendAltEnter();
                method = "alt_enter_restore_fullscreen";
            }
            else if (state == "maximized")
            {
                ShowWindow(hwnd, ShowWindowCommands.SW_SHOWMAXIMIZED);
                method = "showwindow_maximize";
            }
            else if (state == "minimized")
            {
                ShowWindow(hwnd, ShowWindowCommands.SW_SHOWMINIMIZED);
                method = "showwindow_minimize";
            }
            else
            {
                ShowWindow(hwnd, ShowWindowCommands.SW_SHOWNORMAL);
                if (snapshot.Width > 0 && snapshot.Height > 0)
                    SetWindowPos(hwnd, IntPtr.Zero, snapshot.Left, snapshot.Top, snapshot.Width, snapshot.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                method = "showwindow_normal_bounds";
            }
            _log.Add("VIEWER_WINDOW_STATE_RESTORE", "Viewer", $"result=OK pid={processId} requestedState={SafeLog(snapshot.State)} sourcePid={snapshot.ProcessId} method={SafeLog(method)} bounds={snapshot.Left},{snapshot.Top},{snapshot.Width}x{snapshot.Height} rule=release_contract");
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_WINDOW_STATE_RESTORE", "Viewer", $"result=FAILED pid={processId} requestedState={SafeLog(snapshot.State)} sourcePid={snapshot.ProcessId} reason={SafeLog(ex.GetType().Name)} message={SafeLog(ex.Message)} rule=release_contract");
        }
    }

    private static void SendAltEnter()
    {
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public LaunchResult StopManagedViewerProcess(int processId, string reason)
    {
        if (processId <= 0)
            return new LaunchResult(true, 0, "no pid");
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                _log.Add("VIEWER_PROCESS_STOP", "Viewer", $"result=ALREADY_EXITED previousPid={processId} reason={reason} rule=release_contract");
                return new LaunchResult(true, processId, "already exited");
            }

            var closeIssued = false;
            try
            {
                closeIssued = process.CloseMainWindow();
            }
            catch { }

            if (closeIssued)
            {
                try
                {
                    if (process.WaitForExit(3000))
                    {
                        TvAirManagedProcessRegistry.Unregister(processId);
                        _log.Add("VIEWER_PROCESS_STOP", "Viewer", $"result=CLOSED previousPid={processId} closeMainWindow=True reason={reason} rule=release_contract");
                        return new LaunchResult(true, processId, "closed");
                    }
                }
                catch { }
            }

            try
            {
                process.Kill(entireProcessTree: false);
                try { process.WaitForExit(3000); } catch { }
                TvAirManagedProcessRegistry.Unregister(processId);
                _log.Add("VIEWER_PROCESS_STOP", "Viewer", $"result=KILLED_SINGLE_PID previousPid={processId} closeMainWindow={closeIssued} treeKill=False reason={reason} rule=release_contract");
                return new LaunchResult(true, processId, "killed single pid");
            }
            catch (Exception killEx)
            {
                _log.Add("VIEWER_PROCESS_STOP", "Viewer", $"result=FAILED previousPid={processId} closeMainWindow={closeIssued} treeKill=False reason={reason} message={killEx.Message} rule=release_contract");
                return new LaunchResult(false, processId, killEx.Message);
            }
        }
        catch (Exception ex)
        {
            TvAirManagedProcessRegistry.Unregister(processId);
            _log.Add("VIEWER_PROCESS_STOP", "Viewer", $"result=NOT_FOUND previousPid={processId} reason={reason} message={ex.Message} rule=release_contract");
            return new LaunchResult(true, processId, "not found");
        }
    }

    private LaunchResult LaunchViewerCore(string exe, string args, bool preserveViewerWindowState = false, string? viewerActivation = null, ViewerWindowStateSnapshot? restoreWindowState = null)
    {
        var workingDirectory = Path.GetDirectoryName(exe) ?? string.Empty;
        if (_dryRun)
        {
            _log.Add("VIEWER_PROCESS_START_COMMAND", "DryRun", $"exeName={SafeLog(Path.GetFileName(exe))} workingDirectory=omitted arguments={SafeLog(CompactViewerCommandLineForAudit(args))} useShellExecute=False windowStyle=Normal verb=- runAs=False createNoWindow=False environmentDiff=- copyCommand=omitted preserveViewerWindowState={preserveViewerWindowState} viewerActivation={SafeLog(viewerActivation)} restoreWindowStateRequested={(restoreWindowState?.Captured == true)} rule=release_contract");
            _log.Add("VIEWER_PROCESS_START", "DryRun", $"result=OK pid=0 exe={exe} args={args} rule=release_contract");
            return new LaunchResult(true, 0, $"DryRun: {exe} {args}");
        }
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            _log.Add("VIEWER_PROCESS_START_FAILED", "Viewer", $"reason=exe_not_found exe={SafeLog(exe)} workingDirectory={SafeLog(workingDirectory)} rule=release_contract");
            return new LaunchResult(false, 0, $"Viewer executable not found: {exe}");
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };
            _log.Add("VIEWER_PROCESS_START_COMMAND", "Viewer", $"exeName={SafeLog(Path.GetFileName(psi.FileName))} workingDirectory=omitted arguments={SafeLog(CompactViewerCommandLineForAudit(psi.Arguments))} useShellExecute={psi.UseShellExecute} windowStyle={psi.WindowStyle} verb={SafeLog(psi.Verb)} runAs={string.Equals(psi.Verb, "runas", StringComparison.OrdinalIgnoreCase)} createNoWindow={psi.CreateNoWindow} copyCommand=omitted preserveViewerWindowState={preserveViewerWindowState} viewerActivation={SafeLog(viewerActivation)} restoreWindowStateRequested={(restoreWindowState?.Captured == true)} rule=release_contract");
            using var tunerDeviceAccess = TunerDeviceAccessGate.Enter("VIEWER_START", msg => _log.Add("TUNER_DEVICE_LOCK", "Viewer", msg));
            var process = Process.Start(psi);
            if (process == null)
            {
                _log.Add("VIEWER_PROCESS_START_FAILED", "Viewer", "reason=process_start_null rule=release_contract");
                return new LaunchResult(false, 0, "Process.Start returned null.");
            }

            var pid = process.Id;
            try { process.WaitForInputIdle(3000); } catch { }
            _log.Add("VIEWER_PROCESS_START", "Viewer", $"result=OK pid={pid} exe={SafeLog(exe)} state=launched rule=release_contract");
            var activationMethod = preserveViewerWindowState ? "preserve_existing_no_normal_activate" : "normal_window_start";
            _log.Add("VIEWER_WINDOW_ACTIVATE", "Viewer", $"result=REQUESTED pid={pid} method={activationMethod} preserveViewerWindowState={preserveViewerWindowState} viewerActivation={SafeLog(viewerActivation)} normalWindowActivationSuppressed={preserveViewerWindowState} rule=release_contract");
            // release_contract: BonDriver/DID変更時のprofile枠再起動では、既存TVTestへのunscoped再選局を避けつつ、
            // 既存viewerの全画面・最大化・通常位置を可能な範囲で引き継ぐ。
            RestoreViewerWindowStateAfterLaunch(pid, restoreWindowState, preserveViewerWindowState && restoreWindowState is not null);
            return new LaunchResult(true, pid, $"Started PID={pid}: {exe} {args}");
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_PROCESS_START_FAILED", "Viewer", $"reason=process_start_exception message={SafeLog(ex.Message)} exe={SafeLog(exe)} workingDirectory={SafeLog(workingDirectory)} arguments={SafeLog(args)} rule=release_contract");
            return new LaunchResult(false, 0, $"Process.Start exception: {ex.Message} / {exe} {args}");
        }
    }


    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const byte VK_MENU = 0x12;
    private const byte VK_RETURN = 0x0D;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static class ShowWindowCommands
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_SHOWMAXIMIZED = 3;
        public const int SW_RESTORE = 9;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static string SafeLog(object? value)
    {
        if (value is null) return "-";
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return "-";
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string CompactViewerCommandLineForAudit(string? commandLine)
    {
        var text = SafeLog(commandLine);
        if (text == "-") return text;
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var quoted = token.Length >= 2 && token.StartsWith("\"") && token.EndsWith("\"");
            var raw = quoted ? token[1..^1] : token;
            if (raw.Contains(@":\", StringComparison.OrdinalIgnoreCase) &&
                (raw.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(raw);
                tokens[i] = quoted ? "\"" + name + "\"" : name;
            }
        }
        return SafeLog(string.Join(" ", tokens));
    }


    /// <summary>EPGキャプチャ用TSファイル録画モードでTVTestを起動する。</summary>
    /// <param name="bonDriverFileName">BonDriverのDLLファイル名</param>
    /// <param name="did">物理チューナー識別子 (例: A, B …)。空の場合は /DID なし。</param>
    public LaunchResult StartEpgRecording(
        string bonDriverFileName,
        string did,
        string channelArgument,
        string recordingFilePath,
        int durationSeconds)
    {
        var bonDriverPath = ResolveBonDriverPath(bonDriverFileName);
        var didArg = string.IsNullOrWhiteSpace(did) ? "" : $" /DID {did}";
        var opts = BuildCommonOptions();
        var args = $"/d \"{bonDriverPath}\"{didArg} {channelArgument}" +
                   $" /rec /recfile \"{recordingFilePath}\" /recduration {durationSeconds}s" +
                   $" /recdelay 8 /recexit /noview /silent /noplugin{opts}";
        var result = Launch(args);

        // ─── EPG取得用TVTestプロセスの優先度を BelowNormal に下げる ───
        // LIVE視聴TVTestと同優先度で競合するとカクつきの原因になるため、
        // 起動直後にBelowNormalへ降格してCPUリソースをLIVE側に譲る。
        if (result.Success && result.ProcessId > 0 && _ini.EpgUseBelowNormalPriority)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(result.ProcessId);
                if (!p.HasExited)
                    p.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
            }
            catch { /* プロセス即時終了などは無視 */ }
        }
        return result;
    }

    /// <summary>
    /// EPG取得用の共通オプションを設定に応じて組み立てる。
    /// 先頭にスペースが付いた文字列を返す（args への直接連結用）。
    /// </summary>
    private string BuildCommonOptions()
    {
        var sb = new System.Text.StringBuilder();
        if (_ini.UseNodshowOption) sb.Append(" /nodshow");
        if (_ini.UseMinOption)     sb.Append(" /min");
        return sb.ToString();
    }

    private LaunchResult Launch(string args, bool callerHoldsPt3Lock = false)
    {
        var exe = _ini.TvTestExecutablePath;
        var workingDirectory = Path.GetDirectoryName(exe) ?? "";
        var hasSilent = ContainsCommandToken(args, "/silent");
        var hasNoPlugin = ContainsCommandToken(args, "/noplugin");
        var hasNoDShow = ContainsCommandToken(args, "/nodshow");
        _log.Add("TVTEST_LAUNCH_REQUEST", "Launch", $"exe={exe} workingDirectory={workingDirectory} hasSilent={hasSilent} hasNoPlugin={hasNoPlugin} hasNoDShow={hasNoDShow} args={args}");

        if (callerHoldsPt3Lock)
        {
            _log.Add("TUNER_DEVICE_LOCK", "TVTestLauncher", "TUNER_DEVICE_LOCK_REUSE owner=TVTEST_LAUNCH reason=caller_holds_tuner_device_lock");
            return LaunchCore(exe, args);
        }

        using var tunerDeviceAccess = TunerDeviceAccessGate.Enter("TVTEST_LAUNCH", msg => _log.Add("TUNER_DEVICE_LOCK", "TVTestLauncher", msg));
        return LaunchCore(exe, args);
    }

    private LaunchResult LaunchCore(string exe, string args)
    {
        var workingDirectory = Path.GetDirectoryName(exe) ?? "";

        if (_dryRun)
        {
            _log.Add("TVTEST_LAUNCH_RESULT", "DryRun", $"success=True exe={exe} args={args}");
            return new LaunchResult(true, 0, $"DryRun: {exe} {args}");
        }

        if (!File.Exists(exe))
        {
            _log.Add("TVTEST_LAUNCH_RESULT", "Fail", $"success=False reason=exe_not_found exe={exe}");
            return new LaunchResult(false, 0, $"TVTest.exe not found: {exe}");
        }

        var psi = new ProcessStartInfo
        {
            FileName         = exe,
            Arguments        = args,
            UseShellExecute  = false,
            WorkingDirectory = workingDirectory,

            // release_contract: 非表示起動はやめる。/min はTVTest側オプションとして維持し、
            // タスクバー上でTVTestが活動中であることを確認できるようにする。
            CreateNoWindow   = false,
            WindowStyle = ProcessWindowStyle.Minimized
        };

        try
        {
            var process = Process.Start(psi);
            if (process == null)
            {
                _log.Add("TVTEST_LAUNCH_RESULT", "Fail", "success=False reason=process_start_null");
                return new LaunchResult(false, 0, "Process.Start returned null.");
            }

            _log.Add("TVTEST_WINDOW_POLICY", "VisibleMinimized", $"pid={process.Id} createNoWindow=False windowStyle=Minimized hideAfterLaunch=False");

            _log.Add("TVTEST_LAUNCH_RESULT", "OK", $"success=True pid={process.Id} exe={exe}");
            return new LaunchResult(true, process.Id, $"Started PID={process.Id}: {exe} {args}");
        }
        catch (Exception ex)
        {
            _log.Add("TVTEST_LAUNCH_RESULT", "Fail", $"success=False reason=process_start_exception message={ex.Message}");
            return new LaunchResult(false, 0, $"Process.Start 例外: {ex.Message} / {exe} {args}");
        }
    }


    private static bool ContainsCommandToken(string args, string token)
    {
        if (string.IsNullOrWhiteSpace(args) || string.IsNullOrWhiteSpace(token))
            return false;

        return args.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .Any(part => string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveBonDriverPath(string fileName)
    {
        var configuredBonDriverPath = (fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(configuredBonDriverPath))
        {
            _log.Add("TUNER_PROTECT", "BonDriver",
                "ConfiguredBonDriverPath unresolved: value=empty policy=explicit_configuration_only");
            return configuredBonDriverPath;
        }

        if (Path.IsPathRooted(configuredBonDriverPath))
        {
            if (!File.Exists(configuredBonDriverPath))
                _log.Add("TUNER_PROTECT", "BonDriver",
                    $"ConfiguredBonDriverPath not found: {configuredBonDriverPath} policy=explicit_configuration_only");
            return configuredBonDriverPath;
        }

        var configuredBonDriverDirectory = _ini.BonDriverDirectory ?? string.Empty;
        var resolvedConfiguredBonDriverPath = Path.Combine(configuredBonDriverDirectory, configuredBonDriverPath);
        if (!File.Exists(resolvedConfiguredBonDriverPath))
        {
            _log.Add("TUNER_PROTECT", "BonDriver",
                $"ConfiguredBonDriverPath not found: {resolvedConfiguredBonDriverPath} policy=explicit_configuration_only");
        }
        return resolvedConfiguredBonDriverPath;
    }
}


public sealed record LaunchResult(bool Success, int ProcessId, string Message);
