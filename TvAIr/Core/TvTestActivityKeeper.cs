using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TvAIr.Core;

/// <summary>
/// 録画/EPG取得/EPG確認中に、SleepGuardとタスクバー識別用のTVTest表示を最小情報で管理する。
/// v0.5.65: 対象ごとのTVTest表示を維持し、TVTest側のタイトル再設定に負けないよう所有PIDだけ低頻度で再適用する。
/// v0.5.73: DirectRecorder録画中は、既存LIVETest/外部TVTestをActivityKeeperの代用にしない。
/// SleepGuard監視用にTvAIr管理のActivityKeeper TVTestを明示起動し、視聴用プロセスとは分離する。
/// </summary>
public sealed class TvTestActivityKeeper
{
    private readonly IniSettingsService _ini;
    private readonly LogRepository _log;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActivityToken> _tokens = new();

    public TvTestActivityKeeper(IniSettingsService ini, LogRepository log)
    {
        _ini = ini;
        _log = log;
    }

    public TvTestActivityHandle AcquireOwned(string reason, string serviceName, string title)
    {
        var id = Guid.NewGuid();
        var token = new ActivityToken(
            id,
            NormalizeReason(reason),
            Trim(serviceName, 32),
            Trim(title, 48),
            AttachedPid: null,
            OwnedProcess: null,
            OwnsProcess: true);

        lock (_gate)
        {
            // v0.5.73:
            // DirectRecorder録画中のSleepGuard監視は、既存LIVETest/外部TVTestを証跡代用しない。
            // 視聴用プロセスはユーザー操作対象であり、TvAIrの録画中アクティビティとは別物として扱う。
            // そのため、録画開始ごとにTvAIr管理のActivityKeeper TVTestを明示起動する。
            token.OwnedProcess = StartOwnedProcess(token);
            _tokens[id] = token;
            var pidValue = token.OwnedProcess?.Id;
            var pid = pidValue?.ToString() ?? "-";
            var mode = token.OwnedProcess is null ? "activitykeeper_not_started" : "activitykeeper_started";
            _log.Add("RECORDER_ACTIVITY", "ACQUIRE", $"mode={mode} reason={token.Reason} service={Safe(token.ServiceName)} title={Safe(token.Title)} pid={pid} activeCount={_tokens.Count} rule=v0.5.73_no_existing_livetest_witness_for_recording");
            return new TvTestActivityHandle(this, id, pidValue);
        }
    }

    public TvTestActivityHandle AttachExisting(string reason, int pid, string serviceName, string title)
    {
        var id = Guid.NewGuid();
        var token = new ActivityToken(
            id,
            NormalizeReason(reason),
            Trim(serviceName, 32),
            Trim(title, 48),
            AttachedPid: pid,
            OwnedProcess: null,
            OwnsProcess: false);

        lock (_gate)
        {
            _tokens[id] = token;
            StartTitleRefresh(token, pid);
            _log.Add("RECORDER_ACTIVITY", "ATTACH", $"mode=existing_per_target reason={token.Reason} service={Safe(token.ServiceName)} title={Safe(token.Title)} pid={pid} activeCount={_tokens.Count} rule=v0.5.65_target_separated_recorder_activity_cleanup");
            return new TvTestActivityHandle(this, id, pid);
        }
    }

    internal void Release(Guid id)
    {
        ActivityToken? token;
        lock (_gate)
        {
            if (!_tokens.Remove(id, out token)) return;
            token.TitleRefreshCts?.Cancel();
            token.DialogSuppressCts?.Cancel();
            _log.Add("RECORDER_ACTIVITY", "RELEASE", $"token={id:N} reason={token.Reason} service={Safe(token.ServiceName)} pid={TokenPid(token)} activeCount={_tokens.Count} rule=v0.5.65_target_separated_recorder_activity_cleanup");
        }

        if (token.OwnsProcess)
            StopOwnedProcess(token);

        try { token.TitleRefreshCts?.Dispose(); } catch { }
        try { token.DialogSuppressCts?.Dispose(); } catch { }
    }

    private Process? StartOwnedProcess(ActivityToken token)
    {
        var exe = ResolveTvTestExecutable();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            _log.Add("RECORDER_ACTIVITY", "NOT_STARTED", $"reason={token.Reason} service={Safe(token.ServiceName)} activityExe=missing rule=v0.5.65_target_separated_recorder_activity_cleanup");
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Minimized
            };
            if (_ini.UseMinOption) psi.ArgumentList.Add("/min");
            if (_ini.UseNodshowOption) psi.ArgumentList.Add("/nodshow");

            // v0.5.73:
            // ActivityKeeperはTVTestプロセスをSleepGuard監視用に維持するだけで、録画・選局は行わない。
            // 実在する非PT系BonDriverがあれば明示し、TVTestの前回終了時BonDriver復元によるPT系チューナーOpenを避ける。
            // 見つからない場合も既存LIVETestへの代用はせず、TVTestを最小オプションで起動する。
            var passiveBonDriver = TryFindPassiveBonDriver();
            if (!string.IsNullOrWhiteSpace(passiveBonDriver))
            {
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add(passiveBonDriver);
            }

            var process = Process.Start(psi);
            if (process is not null)
            {
                TvAirManagedProcessRegistry.RegisterActivity(process.Id, token.Reason, token.ServiceName);
                StartTitleRefresh(token, process.Id);
                StartOwnedDialogSuppression(token, process.Id);
                _log.Add("RECORDER_ACTIVITY", "STARTED", $"pid={process.Id} exe={Safe(exe)} reason={token.Reason} service={Safe(token.ServiceName)} title={Safe(token.Title)} passiveBonDriver={Safe(passiveBonDriver)} rule=v0.5.73_owned_activity_process_for_sleepguard");
            }
            return process;
        }
        catch (Exception ex)
        {
            _log.Add("RECORDER_ACTIVITY", "START_ERROR", $"reason={token.Reason} service={Safe(token.ServiceName)} error={Safe(ex.Message)} rule=v0.5.65_target_separated_recorder_activity_cleanup");
            return null;
        }
    }

    private void StopOwnedProcess(ActivityToken token)
    {
        var process = token.OwnedProcess;
        if (process is null) return;
        var pid = SafeProcessId(process);
        try
        {
            if (!process.HasExited)
            {
                try { process.CloseMainWindow(); } catch { }
                if (!process.WaitForExit(1000))
                {
                    try { process.Kill(entireProcessTree: false); } catch { }
                }
            }
            _log.Add("RECORDER_ACTIVITY", "STOPPED", $"pid={pid} reason={token.Reason} service={Safe(token.ServiceName)} rule=started_by_tvair_only_v0_5_63");
        }
        catch (Exception ex)
        {
            _log.Add("RECORDER_ACTIVITY", "STOP_ERROR", $"pid={pid} reason={token.Reason} service={Safe(token.ServiceName)} error={Safe(ex.Message)} rule=v0.5.65_target_separated_recorder_activity_cleanup");
        }
        finally
        {
            if (int.TryParse(pid, out var registeredPid))
                TvAirManagedProcessRegistry.Unregister(registeredPid);
            try { process.Dispose(); } catch { }
        }
    }

    private void StartTitleRefresh(ActivityToken token, int pid)
    {
        var cts = new CancellationTokenSource();
        token.TitleRefreshCts = cts;
        var desiredTitle = BuildTitle(token);

        _ = Task.Run(async () =>
        {
            var appliedOnce = false;
            var intervalMs = 1000;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var applied = TrySetProcessTitle(pid, desiredTitle);
                    if (applied && !appliedOnce)
                    {
                        appliedOnce = true;
                        _log.Add("RECORDER_ACTIVITY", "TITLE_APPLIED", $"pid={pid} reason={token.Reason} service={Safe(token.ServiceName)} rule=v0.5.94_short_activity_title_without_tvair_prefix");
                    }

                    // 起動直後はTVTestが自前タイトルへ戻しやすいので短く、安定後は負荷を抑える。
                    intervalMs = appliedOnce ? 3000 : 500;
                    await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }


    private void StartOwnedDialogSuppression(ActivityToken token, int pid)
    {
        // 設定画面以外で出るTVTest由来の通知ダイアログは、ユーザー操作を止めるだけで録画本体とは無関係。
        // 外部TVTest/LIVETestへは一切触らず、TvAIrがActivityKeeper用途で起動した所有PIDだけを対象にする。
        var cts = new CancellationTokenSource();
        token.DialogSuppressCts = cts;
        _ = Task.Run(async () =>
        {
            var suppressed = new HashSet<IntPtr>();
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var closed = SuppressOwnedDialogs(pid, token, suppressed);
                    await Task.Delay(closed ? 150 : 500, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    private bool SuppressOwnedDialogs(int pid, ActivityToken token, HashSet<IntPtr> suppressed)
    {
        var anyClosed = false;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != pid) return true;
            if (!IsWindowVisible(hWnd)) return true;
            if (!IsSuppressibleDialogWindow(hWnd)) return true;

            var title = GetWindowTextSafe(hWnd);
            var cls = GetClassNameSafe(hWnd);
            if (!suppressed.Contains(hWnd))
            {
                suppressed.Add(hWnd);
                _log.Add("RECORDER_ACTIVITY_DIALOG_SUPPRESSED", "CLOSE",
                    $"pid={pid} service={Safe(token.ServiceName)} reason={Safe(token.Reason)} class={Safe(cls)} title={Safe(title)} rule=v0.5.65_owned_activity_dialog_suppression");
            }

            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            anyClosed = true;
            return true;
        }, IntPtr.Zero);
        return anyClosed;
    }

    private static bool IsSuppressibleDialogWindow(IntPtr hWnd)
    {
        var cls = GetClassNameSafe(hWnd);
        if (string.Equals(cls, "#32770", StringComparison.OrdinalIgnoreCase)) return true;

        var title = GetWindowTextSafe(hWnd);
        return title.Contains("エラー", StringComparison.OrdinalIgnoreCase)
            || title.Contains("警告", StringComparison.OrdinalIgnoreCase)
            || title.Contains("確認", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Warning", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowTextSafe(IntPtr hWnd)
    {
        try
        {
            var length = GetWindowTextLength(hWnd);
            var buffer = new char[Math.Max(1, length + 1)];
            _ = GetWindowText(hWnd, buffer, buffer.Length);
            return new string(buffer).TrimEnd('\0');
        }
        catch { return string.Empty; }
    }

    private static string GetClassNameSafe(IntPtr hWnd)
    {
        try
        {
            var buffer = new char[256];
            _ = GetClassName(hWnd, buffer, buffer.Length);
            return new string(buffer).TrimEnd('\0');
        }
        catch { return string.Empty; }
    }

    private string ResolveTvTestExecutable()
    {
        // v0.5.73:
        // ActivityKeeperはSleepGuard監視用のTvAIr管理TVTestとして起動する。
        // 視聴用LIVETestはユーザー視聴プロセスなので、録画中アクティビティの代用にも起動対象にも使わない。
        if (!string.IsNullOrWhiteSpace(_ini.TvTestExecutablePath) && File.Exists(_ini.TvTestExecutablePath)) return _ini.TvTestExecutablePath;
        return string.Empty;
    }

    private int? FindReusableTvTestLikeProcessIdForActivity()
    {
        try
        {
            foreach (var p in Process.GetProcesses()
                         .Where(p => IsTvTestLikeProcessName(p.ProcessName))
                         .OrderBy(p => IsPreferredActivityWitnessName(p.ProcessName) ? 0 : 1)
                         .ThenBy(p => p.Id))
            {
                using (p)
                {
                    try
                    {
                        if (p.HasExited) continue;
                        if (TvAirManagedProcessRegistry.TryGet(p.Id, out var managed) && !managed.IsActivityOnly)
                            continue;
                        return p.Id;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return null;
    }

    private static bool IsTvTestLikeProcessName(string name)
    {
        return string.Equals(name, "TVTest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "LIVETest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TVTest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LIVETest", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPreferredActivityWitnessName(string name)
    {
        return string.Equals(name, "LIVETest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LIVETest", StringComparison.OrdinalIgnoreCase);
    }

    private string TryFindPassiveBonDriver()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_ini.BonDriverDirectory) || !Directory.Exists(_ini.BonDriverDirectory)) return string.Empty;
            var preferred = new[]
            {
                "BonDriver_UDP.dll",
                "BonDriver_TCP.dll",
                "BonDriver_File.dll",
                "BonDriver_Pipe.dll"
            };
            foreach (var name in preferred)
            {
                var path = Path.Combine(_ini.BonDriverDirectory, name);
                if (File.Exists(path)) return name;
            }
        }
        catch { }
        return string.Empty;
    }

    private static string NormalizeReason(string reason) => reason switch
    {
        "Recording" or "録画" or "録画中" => "録画中",
        "PreRecEpg" or "EPG確認" or "EPG確認中" => "EPG確認中",
        "FullEpg" or "EPG" or "EPG取得" or "EPG取得中" => "EPG取得中",
        "EpgSleepGuardBridge" or "EPG監視" => "EPG監視",
        _ => Trim(reason, 12)
    };

    private static string BuildTitle(ActivityToken token)
    {
        // v0.5.94:
        // タスクバー上で後半の局名/番組名が欠けないよう、
        // ActivityKeeperのウィンドウタイトルから "TvAIr" 固定接頭辞を外す。
        // EPG取得の代表プロセス化やEPG用TVTestの一律非表示化は行わない。
        var title = string.IsNullOrWhiteSpace(token.Title) ? token.ServiceName : token.Title;
        var reason = token.Reason switch
        {
            "録画中" => "録画",
            "EPG取得中" => "EPG",
            "EPG確認中" => "EPG確認",
            "EPG監視" => "EPG監視",
            _ => token.Reason
        };

        if (string.IsNullOrWhiteSpace(token.ServiceName))
            return $"{reason}: {title}";

        if (string.IsNullOrWhiteSpace(title) || string.Equals(title, token.ServiceName, StringComparison.OrdinalIgnoreCase))
            return $"{reason}: {token.ServiceName}";

        return $"{reason}: {token.ServiceName} - {title}";
    }

    private static bool TrySetProcessTitle(int pid, string title)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            for (var i = 0; i < 20; i++)
            {
                p.Refresh();
                if (p.HasExited) return false;
                var hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                    hwnd = FindWindowByProcessId(pid);
                if (hwnd != IntPtr.Zero)
                    return SetWindowText(hwnd, title);
                Thread.Sleep(100);
            }
        }
        catch { }
        return false;
    }

    private static IntPtr FindWindowByProcessId(int pid)
    {
        var found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid == pid)
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string TokenPid(ActivityToken token)
    {
        if (token.AttachedPid.HasValue) return token.AttachedPid.Value.ToString();
        if (token.OwnedProcess is null) return "-";
        return SafeProcessId(token.OwnedProcess);
    }

    private static string SafeProcessId(Process process)
    {
        try { return process.Id.ToString(); }
        catch { return "-"; }
    }

    private static string Trim(string? value, int max)
    {
        var s = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (s.Length <= max) return s;
        return s[..Math.Max(0, max - 1)] + "…";
    }

    private static string Safe(string? value) => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    private const int WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private sealed class ActivityToken
    {
        public ActivityToken(Guid id, string reason, string serviceName, string title, int? AttachedPid, Process? OwnedProcess, bool OwnsProcess)
        {
            Id = id;
            Reason = reason;
            ServiceName = serviceName;
            Title = title;
            this.AttachedPid = AttachedPid;
            this.OwnedProcess = OwnedProcess;
            this.OwnsProcess = OwnsProcess;
        }

        public Guid Id { get; }
        public string Reason { get; }
        public string ServiceName { get; }
        public string Title { get; }
        public int? AttachedPid { get; }
        public Process? OwnedProcess { get; set; }
        public bool OwnsProcess { get; }
        public CancellationTokenSource? TitleRefreshCts { get; set; }
        public CancellationTokenSource? DialogSuppressCts { get; set; }
    }
}

public sealed class TvTestActivityHandle : IDisposable
{
    private readonly TvTestActivityKeeper _owner;
    private readonly Guid _id;
    private int _disposed;

    internal TvTestActivityHandle(TvTestActivityKeeper owner, Guid id, int? pid)
    {
        _owner = owner;
        _id = id;
        ProcessId = pid;
    }

    public int? ProcessId { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _owner.Release(_id);
    }
}
