/* TvTestLauncherはTvAIrから管理するTVTestプロセスの起動・維持に使用し、録画実行はTvAIrEpgRecが担当する。 */
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
/// EPG取得では、現在のEPG worker起動経路が必要な表示・負荷設定だけを組み立てる。
/// 録画用のTVTestコマンドラインオプション（/rec、/recfile、/recduration、/recdelay、/recexit）は
/// 使用しない。録画実行はTvAIrEpgRecが担当する。
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
    int NormalLeft,
    int NormalTop,
    int NormalWidth,
    int NormalHeight,
    int MonitorLeft,
    int MonitorTop,
    int MonitorWidth,
    int MonitorHeight,
    int WorkLeft,
    int WorkTop,
    int WorkWidth,
    int WorkHeight,
    string Reason,
    string Diagnostics)
{
    public static ViewerWindowStateSnapshot Skipped(int processId, string reason, string diagnostics)
        => new(false, processId, "unknown", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, reason, diagnostics);
}


public sealed record ViewerWindowRestoreResult(
    bool Requested,
    bool Applied,
    string ShowState,
    int Left,
    int Top,
    int Width,
    int Height,
    string Method,
    string Diagnostics)
{
    public static ViewerWindowRestoreResult NotRequested(string diagnostics)
        => new(false, false, "unknown", 0, 0, 0, 0, "none", diagnostics);

    public static ViewerWindowRestoreResult Failed(string showState, string method, string diagnostics)
        => new(true, false, showState, 0, 0, 0, 0, method, diagnostics);
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


    /// <summary>TvAIr管理の視聴用TVTest/LIVETestを、汎用Viewer API契約として可視起動する。</summary>
    public LaunchResult StartViewer(string ownershipId, string bonDriverFileName, string did, string tunerGroup, string channelArgument, bool preserveViewerWindowState = false, string? viewerActivation = null, ViewerWindowStateSnapshot? restoreWindowState = null)
    {
        // Viewer must pass the configured BonDriver file name, not an absolute path.
        // TVTest treats the /d token as viewer state; absolute paths can leak a TvAIr-owned
        // launch representation into the shared TVTest tuner selection state.
        var viewerBonDriver = Path.GetFileName((bonDriverFileName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(viewerBonDriver))
            viewerBonDriver = (bonDriverFileName ?? string.Empty).Trim();
        var didArg = string.IsNullOrWhiteSpace(did) ? string.Empty : $" /DID {did}";
        var exe = !string.IsNullOrWhiteSpace(_ini.ViewingTvTestExecutablePath) ? _ini.ViewingTvTestExecutablePath : _ini.TvTestExecutablePath;
        var viewerChannelArgument = BuildViewerLaunchChannelArgument(channelArgument, grFallbackFromCh: true);
        if (string.IsNullOrWhiteSpace(viewerChannelArgument))
            viewerChannelArgument = RemoveNonLaunchIdentityAndSilentArguments(channelArgument);
        var args = NormalizeArgumentWhitespace($"/d \"{viewerBonDriver}\"{didArg} {viewerChannelArgument}");
        var workingDirectory = Path.GetDirectoryName(exe) ?? string.Empty;

        _log.Add("VIEWER_TVTEST_ARGUMENT", "Viewer",
            $"selected=chspaceChiSid exeName={SafeLog(Path.GetFileName(exe))} workingDirectory=omitted bonDriver={SafeLog(bonDriverFileName)} bonDriverPath=omitted did={SafeLog(did)} finalArguments={SafeLog(CompactViewerCommandLineForAudit(args))} sourceChannelArgument={SafeLog(channelArgument)} silent=False sidInInitialLaunch={(!string.IsNullOrWhiteSpace(GetCommandTokenValue(viewerChannelArgument, "/sid"))).ToString()} identityArgsInInitialLaunch=sid_only copyCommand=omitted preserveViewerWindowState={preserveViewerWindowState} viewerActivation={SafeLog(viewerActivation)} restoreWindowStateRequested={(restoreWindowState?.Captured == true)} rule=release_contract");

        var result = LaunchViewerCore(exe, args, tunerGroup, preserveViewerWindowState, viewerActivation, restoreWindowState);
        if (result.Success && result.ProcessId > 0)
            TvAirManagedProcessRegistry.RegisterViewer(result.ProcessId, ownershipId, did, bonDriverFileName);
        return result;
    }

    /// <summary>
    /// TvAIrが所有する既存Viewerへ、TVTest自身の通常single-task契約で選局要求を渡す。
    /// 対象はViewer Profileから解決済みのBonDriver/DIDで一意に決まり、前面化やHWND直送は行わない。
    /// </summary>
    public LaunchResult RetuneExistingViewer(string ownershipId, int existingProcessId, string bonDriverFileName, string did, string channelArgument, bool preserveViewerWindowState = false, string? viewerActivation = null)
    {
        if (existingProcessId <= 0)
        {
            _log.Add("VIEWER_RETUNE_EXISTING", "Viewer",
                $"result=FAILED method=pid_targeted_tvtest_interprocess existingPid={existingProcessId} leaseId={SafeLog(ownershipId)} reason=invalid_process_id foregroundApplied=False processRestarted=False rule=viewer_session_contract");
            return new LaunchResult(false, existingProcessId, "Existing viewer process id is invalid.", ErrorCode: "invalid_process_id");
        }

        try
        {
            using var process = Process.GetProcessById(existingProcessId);
            if (process.HasExited)
            {
                _log.Add("VIEWER_RETUNE_EXISTING", "Viewer",
                    $"result=FAILED method=pid_targeted_tvtest_interprocess existingPid={existingProcessId} leaseId={SafeLog(ownershipId)} reason=process_exited foregroundApplied=False processRestarted=False rule=viewer_session_contract");
                return new LaunchResult(false, existingProcessId, "Existing viewer process has exited.", ErrorCode: "process_exited");
            }

            var hwnd = ResolveOwnedViewerMainWindow(existingProcessId, process);
            if (hwnd == IntPtr.Zero)
            {
                _log.Add("VIEWER_RETUNE_EXISTING", "Viewer",
                    $"result=FAILED method=pid_targeted_tvtest_interprocess existingPid={existingProcessId} leaseId={SafeLog(ownershipId)} reason=main_window_unavailable foregroundApplied=False processRestarted=False rule=viewer_session_contract");
                return new LaunchResult(false, existingProcessId, "Owned TVTest main window is unavailable.", ErrorCode: "main_window_unavailable");
            }

            // Keep retune on the same Viewer contract as initial launch: file name only.
            var viewerBonDriver = Path.GetFileName((bonDriverFileName ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(viewerBonDriver))
                viewerBonDriver = (bonDriverFileName ?? string.Empty).Trim();
            var didArg = string.IsNullOrWhiteSpace(did) ? string.Empty : $" /DID {did}";
            var viewerChannelArgument = BuildViewerLaunchChannelArgument(channelArgument, grFallbackFromCh: true);
            if (string.IsNullOrWhiteSpace(viewerChannelArgument))
                viewerChannelArgument = RemoveNonLaunchIdentityAndSilentArguments(channelArgument);

            // TVTest official single-task receiver consumes the original command-line body.
            // The target HWND is already resolved from the owned PID, so /s and global target discovery are unnecessary.
            var commandLine = NormalizeArgumentWhitespace($"/d \"{viewerBonDriver}\"{didArg} {viewerChannelArgument}");

            // ViewerActivation=preserve means the Host does not request foreground activation during retune.
            // Retune itself only targets the exact owned Viewer PID; foreground is never restored or rewritten here.
            var dispatch = SendTvTestExecuteMessage(hwnd, commandLine, out var receiverResult, out var dispatchError);
            if (!dispatch)
            {
                _log.Add("VIEWER_RETUNE_EXISTING", "Viewer",
                    $"result=FAILED method=pid_targeted_tvtest_interprocess existingPid={existingProcessId} leaseId={SafeLog(ownershipId)} did={SafeLog(did)} bonDriver={SafeLog(bonDriverFileName)} command={SafeLog(CompactViewerCommandLineForAudit(commandLine))} reason={SafeLog(dispatchError)} receiverResult={receiverResult} unscopedSingleTaskSuppressed=True foregroundAppliedByHost=False processRestarted=False viewerActivation={SafeLog(viewerActivation)} rule=viewer_session_contract");
                return new LaunchResult(false, existingProcessId, $"TVTest interprocess retune failed: {dispatchError}", ErrorCode: "interprocess_dispatch_failed");
            }

            _log.Add("VIEWER_RETUNE_EXISTING", "Viewer",
                $"result=OK method=pid_targeted_tvtest_interprocess existingPid={existingProcessId} leaseId={SafeLog(ownershipId)} did={SafeLog(did)} bonDriver={SafeLog(bonDriverFileName)} command={SafeLog(CompactViewerCommandLineForAudit(commandLine))} receiverResult={receiverResult} targetScope=owned_pid_exact unscopedSingleTaskSuppressed=True foregroundAppliedByHost=False processRestarted=False viewerActivation={SafeLog(viewerActivation)} rule=viewer_session_contract");
            return new LaunchResult(true, existingProcessId, "TVTest accepted the PID-targeted retune command.");
        }
        catch (ArgumentException)
        {
            _log.Add("VIEWER_RETUNE_EXISTING", "Viewer",
                $"result=FAILED method=pid_targeted_tvtest_interprocess existingPid={existingProcessId} leaseId={SafeLog(ownershipId)} reason=process_not_found foregroundApplied=False processRestarted=False rule=viewer_session_contract");
            return new LaunchResult(false, existingProcessId, "Existing viewer process was not found.", ErrorCode: "process_not_found");
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_RETUNE_EXISTING", "Viewer",
                $"result=FAILED method=pid_targeted_tvtest_interprocess existingPid={existingProcessId} leaseId={SafeLog(ownershipId)} reason=exception message={SafeLog(ex.Message)} foregroundApplied=False processRestarted=False rule=viewer_session_contract");
            return new LaunchResult(false, existingProcessId, $"PID-targeted retune exception: {ex.Message}", ErrorCode: "retune_exception");
        }
    }


    /// <summary>Activates only the owned viewer window. No tune, session, generation, lease, or process state is changed.</summary>
    public LaunchResult ActivateExistingViewer(string leaseId, int existingProcessId)
    {
        if (existingProcessId <= 0)
            return new LaunchResult(false, existingProcessId, "Existing viewer process id is invalid.", ErrorCode: "invalid_process_id");
        try
        {
            using var process = Process.GetProcessById(existingProcessId);
            if (process.HasExited)
                return new LaunchResult(false, existingProcessId, "Existing viewer process has exited.", ErrorCode: "process_exited");
            var hwnd = IntPtr.Zero;
            // The process may be alive while TVTest is recreating its top-level window.
            // Resolve by the owned PID until the window becomes available; no session/generation state is changed.
            for (var attempt = 0; attempt < 8 && hwnd == IntPtr.Zero; attempt++)
            {
                if (process.HasExited)
                    return new LaunchResult(false, existingProcessId, "viewerProcessExited");
                hwnd = ResolveOwnedViewerMainWindow(existingProcessId, process);
                if (hwnd == IntPtr.Zero)
                {
                    // VIEWER_ACTIVATION_WINDOW_READY_WAIT_INVARIANT:
                    // TVTestの所有PIDとプロセス状態を正本とし、無条件の固定sleepでウィンドウ生成を推測しない。
                    // 入力待機完了または50ms上限のどちらかで直ちに再確認する。探索回数・所有PID限定・前面化動作は変更しない。
                    // この順序・上限・所有PID条件の変更には、着手前に開発者の明示承認が必要。
                    try { _ = process.WaitForInputIdle(50); }
                    catch (InvalidOperationException) { }
                    catch (NotSupportedException) { }
                }
            }
            if (hwnd == IntPtr.Zero)
                return new LaunchResult(false, existingProcessId, "viewerWindowUnavailable");
            ShowWindow(hwnd, ShowWindowCommands.SW_RESTORE);
            var applied = SetForegroundWindow(hwnd);
            _log.Add("VIEWER_WINDOW_ACTIVATE", "Viewer",
                $"result={(applied ? "OK" : "FAILED")} leaseId={SafeLog(leaseId)} pid={existingProcessId} foregroundApplied={applied} retune=False generationChanged=False processRestarted=False rule=viewer_window_activation_contract");
            return new LaunchResult(applied, existingProcessId, applied ? "Owned viewer window activated." : "foregroundActivationRejected");
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_WINDOW_ACTIVATE", "Viewer",
                $"result=FAILED leaseId={SafeLog(leaseId)} pid={existingProcessId} reason=exception message={SafeLog(ex.Message)} foregroundApplied=False retune=False generationChanged=False processRestarted=False rule=viewer_window_activation_contract");
            return new LaunchResult(false, existingProcessId, ex.Message);
        }
    }

    private static IntPtr ResolveOwnedViewerMainWindow(int expectedProcessId, Process process)
    {
        process.Refresh();
        var mainWindow = process.MainWindowHandle;
        if (mainWindow != IntPtr.Zero && IsWindow(mainWindow))
        {
            GetWindowThreadProcessId(mainWindow, out var ownerPid);
            if (ownerPid == (uint)expectedProcessId)
                return mainWindow;
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var ownerPid);
            if (ownerPid != (uint)expectedProcessId)
                return true;

            var className = new System.Text.StringBuilder(128);
            if (GetClassName(hwnd, className, className.Capacity) <= 0)
                return true;
            if (!string.Equals(className.ToString(), "TVTest", StringComparison.OrdinalIgnoreCase))
                return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }


    private static bool SendTvTestExecuteMessage(IntPtr targetWindow, string commandLine, out nuint receiverResult, out string error)
    {
        receiverResult = 0;
        error = string.Empty;
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow))
        {
            error = "invalid_target_window";
            return false;
        }

        var payload = (commandLine ?? string.Empty) + "\0";
        var bytes = System.Text.Encoding.Unicode.GetBytes(payload);
        var payloadPtr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, payloadPtr, bytes.Length);
            var copyData = new COPYDATASTRUCT
            {
                dwData = new UIntPtr(ProcessMessageExecute),
                cbData = bytes.Length,
                lpData = payloadPtr
            };
            var copyDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<COPYDATASTRUCT>());
            try
            {
                Marshal.StructureToPtr(copyData, copyDataPtr, false);
                var sent = SendMessageTimeout(
                    targetWindow,
                    WmCopyData,
                    IntPtr.Zero,
                    copyDataPtr,
                    SendMessageTimeoutFlags.SMTO_BLOCK | SendMessageTimeoutFlags.SMTO_ABORTIFHUNG,
                    5000,
                    out receiverResult);
                if (sent == IntPtr.Zero)
                {
                    error = $"send_message_timeout_win32_{Marshal.GetLastWin32Error()}";
                    return false;
                }
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(copyDataPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(payloadPtr);
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
            var normalLeft = 0;
            var normalTop = 0;
            var normalWidth = 0;
            var normalHeight = 0;
            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref placement))
            {
                if (placement.showCmd == ShowWindowCommands.SW_SHOWMAXIMIZED) state = "maximized";
                else if (placement.showCmd == ShowWindowCommands.SW_SHOWMINIMIZED) state = "minimized";
                normalLeft = placement.rcNormalPosition.Left;
                normalTop = placement.rcNormalPosition.Top;
                normalWidth = Math.Max(0, placement.rcNormalPosition.Right - placement.rcNormalPosition.Left);
                normalHeight = Math.Max(0, placement.rcNormalPosition.Bottom - placement.rcNormalPosition.Top);
            }

            var left = 0;
            var top = 0;
            var width = 0;
            var height = 0;
            var monitorLeft = 0;
            var monitorTop = 0;
            var monitorWidth = 0;
            var monitorHeight = 0;
            var workLeft = 0;
            var workTop = 0;
            var workWidth = 0;
            var workHeight = 0;
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
                        monitorLeft = info.rcMonitor.Left;
                        monitorTop = info.rcMonitor.Top;
                        monitorWidth = Math.Max(0, info.rcMonitor.Right - info.rcMonitor.Left);
                        monitorHeight = Math.Max(0, info.rcMonitor.Bottom - info.rcMonitor.Top);
                        workLeft = info.rcWork.Left;
                        workTop = info.rcWork.Top;
                        workWidth = Math.Max(0, info.rcWork.Right - info.rcWork.Left);
                        workHeight = Math.Max(0, info.rcWork.Bottom - info.rcWork.Top);
                        var dx = Math.Abs(rect.Left - info.rcMonitor.Left) + Math.Abs(rect.Top - info.rcMonitor.Top) +
                                 Math.Abs(rect.Right - info.rcMonitor.Right) + Math.Abs(rect.Bottom - info.rcMonitor.Bottom);
                        if (dx <= 8 && state != "minimized") state = "fullscreen";
                    }
                }
            }

            _log.Add("VIEWER_WINDOW_STATE_CAPTURE", "Viewer", $"result=OK pid={processId} reason={SafeLog(reason)} windowStateCaptured=True capturedShowState={SafeLog(state)} capturedBounds={left},{top},{width}x{height} normalBounds={normalLeft},{normalTop},{normalWidth}x{normalHeight} capturedMonitor={monitorLeft},{monitorTop},{monitorWidth}x{monitorHeight} capturedWorkArea={workLeft},{workTop},{workWidth}x{workHeight} source=before_restart_fallback rule=release_contract");
            return new ViewerWindowStateSnapshot(true, processId, state, left, top, width, height, normalLeft, normalTop, normalWidth, normalHeight, monitorLeft, monitorTop, monitorWidth, monitorHeight, workLeft, workTop, workWidth, workHeight, reason, "OK");
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_WINDOW_STATE_CAPTURE", "Viewer", $"result=FAILED pid={processId} reason={SafeLog(reason)} detail={SafeLog(ex.GetType().Name)} message={SafeLog(ex.Message)} rule=release_contract");
            return ViewerWindowStateSnapshot.Skipped(processId, reason, ex.GetType().Name);
        }
    }

    private ViewerWindowRestoreResult RestoreViewerWindowStateAfterLaunch(int processId, ViewerWindowStateSnapshot? snapshot, bool restoreRequested, bool preserveActivation)
    {
        if (!restoreRequested)
        {
            const string diagnostics = "no_restore_snapshot_requested";
            _log.Add("VIEWER_WINDOW_STATE_RESTORE", "Viewer", $"result=SKIPPED pid={processId} reason={diagnostics} preserveViewerWindowState=True-or-launch_without_previous_snapshot rule=release_contract");
            return ViewerWindowRestoreResult.NotRequested(diagnostics);
        }
        if (snapshot is null || !snapshot.Captured)
        {
            const string diagnostics = "no_captured_state";
            _log.Add("VIEWER_WINDOW_STATE_RESTORE", "Viewer", $"result=SKIPPED pid={processId} requestedState=unknown reason={diagnostics} sourcePid={(snapshot?.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-")} rule=release_contract");
            return ViewerWindowRestoreResult.Failed("unknown", "none", diagnostics);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var hwnd = IntPtr.Zero;
            // VIEWER_WINDOW_READY_WAIT_INVARIANT:
            // TVTest再起動後の復元は、所有PIDのMainWindowHandleが実在した時点で直ちに進む。
            // 固定待機ではなく、各反復でTVTest自身の
            // input-idle成立を最大250msだけ待ち、ウィンドウ実在を再確認する。探索上限40回、
            // 所有PID限定、復元順序を変更する場合は開発者の明示承認を要する。
            for (var i = 0; i < 40; i++)
            {
                if (process.HasExited)
                    break;
                process.Refresh();
                hwnd = ResolveOwnedViewerMainWindow(processId, process);
                if (hwnd != IntPtr.Zero)
                    break;
                try
                {
                    process.WaitForInputIdle(250);
                }
                catch (InvalidOperationException)
                {
                    // Process exit / non-GUI transition is observed by the next loop condition.
                }
            }
            if (hwnd == IntPtr.Zero || process.HasExited)
            {
                const string diagnostics = "main_window_handle_unavailable_after_launch";
                _log.Add("VIEWER_WINDOW_STATE_RESTORE", "Viewer", $"result=FAILED pid={processId} requestedState={SafeLog(snapshot.State)} sourcePid={snapshot.ProcessId} reason={diagnostics} rule=release_contract");
                return ViewerWindowRestoreResult.Failed(snapshot.State, "window_wait", diagnostics);
            }

            var state = (snapshot.State ?? "normal").Trim().ToLowerInvariant();
            var restoreLeft = snapshot.Left;
            var restoreTop = snapshot.Top;
            var restoreWidth = snapshot.Width;
            var restoreHeight = snapshot.Height;
            var method = "showwindow";
            var applied = false;

            if (preserveActivation)
            {
                if (state == "minimized")
                {
                    ShowWindow(hwnd, ShowWindowCommands.SW_SHOWMINNOACTIVE);
                    applied = IsIconic(hwnd);
                    method = "showwindow_minimize_no_activate";
                }
                else
                {
                    ShowWindow(hwnd, ShowWindowCommands.SW_SHOWNA);
                    if (state == "maximized" && snapshot.WorkWidth > 0 && snapshot.WorkHeight > 0)
                    {
                        restoreLeft = snapshot.WorkLeft;
                        restoreTop = snapshot.WorkTop;
                        restoreWidth = snapshot.WorkWidth;
                        restoreHeight = snapshot.WorkHeight;
                        method = "maximized_workarea_bounds_no_activate";
                    }
                    else if (state == "fullscreen" && snapshot.MonitorWidth > 0 && snapshot.MonitorHeight > 0)
                    {
                        restoreLeft = snapshot.MonitorLeft;
                        restoreTop = snapshot.MonitorTop;
                        restoreWidth = snapshot.MonitorWidth;
                        restoreHeight = snapshot.MonitorHeight;
                        method = "fullscreen_monitor_bounds_no_activate";
                    }
                    else
                    {
                        if (snapshot.NormalWidth > 0 && snapshot.NormalHeight > 0)
                        {
                            restoreLeft = snapshot.NormalLeft;
                            restoreTop = snapshot.NormalTop;
                            restoreWidth = snapshot.NormalWidth;
                            restoreHeight = snapshot.NormalHeight;
                        }
                        method = "normal_bounds_no_activate";
                    }

                    applied = restoreWidth > 0 && restoreHeight > 0 &&
                              SetWindowPos(hwnd, IntPtr.Zero, restoreLeft, restoreTop, restoreWidth, restoreHeight, SWP_NOZORDER | SWP_NOACTIVATE);
                }
            }
            else if (state == "fullscreen")
            {
                ShowWindow(hwnd, ShowWindowCommands.SW_RESTORE);
                var foregroundRequested = SetForegroundWindow(hwnd);

                // VIEWER_FULLSCREEN_RESTORE_WAIT_INVARIANT:
                // Alt+Enter は対象TVTestが実際に前面化したことを確認してから送る。
                // 前面化が確認できれば即時に進み、確認できない場合だけ必要な範囲で再確認する。
                // 上限はOSが前面化要求を拒否・遅延した場合に処理を閉じるためだけに使う。
                // この契約または待機方式を変更する場合は、開発者の明示承認を事前に得ること。
                long foregroundWaitMs = 0;
                var foregroundReady = foregroundRequested && WaitForForegroundWindow(hwnd, TimeSpan.FromMilliseconds(200), out foregroundWaitMs);
                if (foregroundReady)
                {
                    SendAltEnter();
                    applied = true;
                }
                _log.Add("VIEWER_FULLSCREEN_RESTORE_WAIT", "Viewer",
                    $"result={(foregroundReady ? "READY" : "NOT_READY")} pid={processId} foregroundRequested={foregroundRequested} foregroundWaitMs={foregroundWaitMs} fixedWaitRemoved=True rule=release_contract");
                method = "alt_enter_restore_fullscreen";
            }
            else if (state == "maximized")
            {
                ShowWindow(hwnd, ShowWindowCommands.SW_SHOWMAXIMIZED);
                applied = IsZoomed(hwnd);
                method = "showwindow_maximize";
            }
            else if (state == "minimized")
            {
                ShowWindow(hwnd, ShowWindowCommands.SW_SHOWMINIMIZED);
                applied = IsIconic(hwnd);
                method = "showwindow_minimize";
            }
            else
            {
                ShowWindow(hwnd, ShowWindowCommands.SW_SHOWNORMAL);
                if (snapshot.NormalWidth > 0 && snapshot.NormalHeight > 0)
                {
                    restoreLeft = snapshot.NormalLeft;
                    restoreTop = snapshot.NormalTop;
                    restoreWidth = snapshot.NormalWidth;
                    restoreHeight = snapshot.NormalHeight;
                }
                applied = restoreWidth > 0 && restoreHeight > 0 &&
                          SetWindowPos(hwnd, IntPtr.Zero, restoreLeft, restoreTop, restoreWidth, restoreHeight, SWP_NOZORDER | SWP_NOACTIVATE);
                method = "showwindow_normal_bounds";
            }

            var applyDiagnostics = applied ? "OK" : $"win32_apply_failed:{Marshal.GetLastWin32Error()}";
            _log.Add("VIEWER_WINDOW_STATE_RESTORE", "Viewer",
                $"result={(applied ? "OK" : "FAILED")} pid={processId} previousPid={snapshot.ProcessId} newPid={processId} restoreWindowStateRequested=True restoreWindowStateApplied={applied} restoredShowState={SafeLog(snapshot.State)} restoredBounds={restoreLeft},{restoreTop},{restoreWidth}x{restoreHeight} method={SafeLog(method)} activationChanged={(preserveActivation ? "False" : state == "fullscreen" ? "True" : "False")} diagnostics={SafeLog(applyDiagnostics)} rule=release_contract");
            return new ViewerWindowRestoreResult(true, applied, state, restoreLeft, restoreTop, restoreWidth, restoreHeight, method, applyDiagnostics);
        }
        catch (Exception ex)
        {
            var diagnostics = $"{ex.GetType().Name}:{ex.Message}";
            _log.Add("VIEWER_WINDOW_STATE_RESTORE", "Viewer", $"result=FAILED pid={processId} requestedState={SafeLog(snapshot.State)} sourcePid={snapshot.ProcessId} reason={SafeLog(ex.GetType().Name)} message={SafeLog(ex.Message)} rule=release_contract");
            return ViewerWindowRestoreResult.Failed(snapshot.State, "exception", diagnostics);
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

    private LaunchResult LaunchViewerCore(string exe, string args, string tunerGroup, bool preserveViewerWindowState = false, string? viewerActivation = null, ViewerWindowStateSnapshot? restoreWindowState = null)
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
            var preserveActivation = !string.Equals(viewerActivation, "activate", StringComparison.OrdinalIgnoreCase);
            _log.Add("VIEWER_PROCESS_START_COMMAND", "Viewer", $"exeName={SafeLog(Path.GetFileName(exe))} workingDirectory=omitted arguments={SafeLog(CompactViewerCommandLineForAudit(args))} useShellExecute=False windowStyle={(preserveActivation ? "ShowNoActivate" : "Normal")} verb=- runAs=False createNoWindow=False copyCommand=omitted preserveViewerWindowState={preserveViewerWindowState} viewerActivation={SafeLog(viewerActivation)} restoreWindowStateRequested={(restoreWindowState?.Captured == true)} rule=release_contract");
            using var tunerDeviceAccess = TunerDeviceAccessGate.Enter("VIEWER_START", tunerGroup, msg => _log.Add("TUNER_DEVICE_LOCK", "Viewer", msg));

            int pid;
            if (preserveActivation)
            {
                var native = StartViewerNoActivate(exe, args, workingDirectory);
                if (!native.Success)
                {
                    _log.Add("VIEWER_PROCESS_START_FAILED", "Viewer", $"reason=create_process_no_activate_failed message={SafeLog(native.Message)} rule=release_contract");
                    return native;
                }
                pid = native.ProcessId;
            }
            else
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
                var process = Process.Start(psi);
                if (process == null)
                {
                    _log.Add("VIEWER_PROCESS_START_FAILED", "Viewer", "reason=process_start_null rule=release_contract");
                    return new LaunchResult(false, 0, "Process.Start returned null.");
                }
                pid = process.Id;
            }

            try
            {
                using var launched = Process.GetProcessById(pid);
                launched.WaitForInputIdle(3000);
            }
            catch { }
            _log.Add("VIEWER_PROCESS_START", "Viewer", $"result=OK pid={pid} exe={SafeLog(exe)} state=launched activation={(preserveActivation ? "preserved" : "default")} rule=release_contract");
            // release_contract: BonDriver/DID変更時のprofile枠再起動では、既存TVTestへのunscoped再選局を避けつつ、
            // 既存viewerの全画面・最大化・通常位置を可能な範囲で引き継ぐ。
            var windowRestore = restoreWindowState is not null
                ? RestoreViewerWindowStateAfterLaunch(pid, restoreWindowState, preserveViewerWindowState, preserveActivation)
                : ViewerWindowRestoreResult.NotRequested("launch_without_restore_snapshot");
            return new LaunchResult(true, pid, $"Started PID={pid}: {exe} {args}", windowRestore);
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_PROCESS_START_FAILED", "Viewer", $"reason=process_start_exception message={SafeLog(ex.Message)} exe={SafeLog(exe)} workingDirectory={SafeLog(workingDirectory)} arguments={SafeLog(args)} rule=release_contract");
            return new LaunchResult(false, 0, $"Process.Start exception: {ex.Message} / {exe} {args}");
        }
    }

    private LaunchResult StartViewerNoActivate(string exe, string args, string workingDirectory)
    {
        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESHOWWINDOW,
            wShowWindow = SW_SHOWNOACTIVATE
        };
        var commandLine = new System.Text.StringBuilder($"\"{exe}\" {args}");
        if (!CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workingDirectory, ref startupInfo, out var processInfo))
            return new LaunchResult(false, 0, $"CreateProcessW failed: {Marshal.GetLastWin32Error()}");

        try
        {
            return new LaunchResult(true, unchecked((int)processInfo.dwProcessId), $"Started PID={processInfo.dwProcessId}: {exe} {args}");
        }
        finally
        {
            if (processInfo.hThread != IntPtr.Zero) CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero) CloseHandle(processInfo.hProcess);
        }
    }

    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_SHOWNOACTIVATE = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        System.Text.StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);


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
        public const int SW_SHOWMINNOACTIVE = 7;
        public const int SW_SHOWNA = 8;
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

    private const uint WmCopyData = 0x004A;
    private const uint ProcessMessageExecute = 0x54565400;

    [Flags]
    private enum SendMessageTimeoutFlags : uint
    {
        SMTO_BLOCK = 0x0001,
        SMTO_ABORTIFHUNG = 0x0002
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public UIntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static bool WaitForForegroundWindow(IntPtr expectedWindow, TimeSpan timeout, out long elapsedMs)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (GetForegroundWindow() == expectedWindow)
            {
                elapsedMs = started.ElapsedMilliseconds;
                return true;
            }

            if (started.Elapsed >= timeout)
            {
                elapsedMs = started.ElapsedMilliseconds;
                return false;
            }

            // 状態確認間隔。固定settleではなく、前面化が成立した時点で次の反復を待たず終了する。
            Thread.Sleep(10);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        SendMessageTimeoutFlags fuFlags,
        uint uTimeout,
        out nuint lpdwResult);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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


    /// <summary>
    /// EPG取得用の共通オプションを設定に応じて組み立てる。
    /// 先頭にスペースが付いた文字列を返す（args への直接連結用）。
    /// </summary>

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


public sealed record LaunchResult(bool Success, int ProcessId, string Message, ViewerWindowRestoreResult? WindowRestore = null, string ErrorCode = "");
