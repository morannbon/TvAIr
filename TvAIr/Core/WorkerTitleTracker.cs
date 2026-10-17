using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TvAIr.Core;

/// <summary>
/// 呼出元が指定した管理対象PIDの処理タイトルを低頻度で維持する。
/// 現在のEPG経路はAttachExistingでワーカーへ接続する。予約録画スケジューラーは利用しない。
/// </summary>
public sealed class WorkerTitleTracker
{
    private readonly LogRepository _log;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActivityToken> _tokens = new();

    public WorkerTitleTracker(LogRepository log)
    {
        _log = log;
    }

    public WorkerTitleHandle AttachExisting(string reason, int pid, string serviceName, string title)
    {
        var id = Guid.NewGuid();
        var token = new ActivityToken(
            id,
            NormalizeReason(reason),
            Trim(serviceName, 32),
            Trim(title, 48),
            AttachedPid: pid);

        lock (_gate)
        {
            _tokens[id] = token;
            StartTitleRefresh(token, pid);
            _log.Add("RECORDER_ACTIVITY", "ATTACH", $"mode=existing_per_target reason={token.Reason} service={Safe(token.ServiceName)} title={Safe(token.Title)} pid={pid} activeCount={_tokens.Count} rule=release_contract");
            return new WorkerTitleHandle(this, id, pid);
        }
    }

    internal void Release(Guid id)
    {
        ActivityToken? token;
        lock (_gate)
        {
            if (!_tokens.Remove(id, out token)) return;
            token.TitleRefreshCts?.Cancel();
            _log.Add("RECORDER_ACTIVITY", "RELEASE", $"token={id:N} reason={token.Reason} service={Safe(token.ServiceName)} pid={token.AttachedPid} activeCount={_tokens.Count} rule=release_contract");
        }

        try { token.TitleRefreshCts?.Dispose(); } catch { }
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
                    var applied = await TrySetProcessTitleAsync(pid, desiredTitle, cts.Token).ConfigureAwait(false);
                    if (applied && !appliedOnce)
                    {
                        appliedOnce = true;
                        _log.Add("RECORDER_ACTIVITY", "TITLE_APPLIED", $"pid={pid} reason={token.Reason} service={Safe(token.ServiceName)} rule=release_contract");
                    }

                    // 起動直後は短い間隔で確認し、タイトル適用後は間隔を延ばす。
                    intervalMs = appliedOnce ? 3000 : 500;
                    await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }


    private static string NormalizeReason(string reason) => reason switch
    {
        "Recording" or "録画" or "録画中" => "録画中",
        "PreRecEpg" or "EPG確認" or "EPG確認中" => "EPG確認中",
        "FullEpg" or "EPG" or "EPG取得" or "EPG取得中" => "EPG取得中",
        _ => Trim(reason, 12)
    };

    private static string BuildTitle(ActivityToken token)
    {
        // release_contract:
        // タスクバー上で後半の局名/番組名が欠けないよう、
        // ワーカーのウィンドウタイトルから "TvAIr" 固定接頭辞を外す。
        var title = string.IsNullOrWhiteSpace(token.Title) ? token.ServiceName : token.Title;
        var reason = token.Reason switch
        {
            "録画中" => "録画",
            "EPG取得中" => "EPG",
            "EPG確認中" => "EPG確認",
            _ => token.Reason
        };

        if (string.IsNullOrWhiteSpace(token.ServiceName))
            return $"{reason}: {title}";

        if (string.IsNullOrWhiteSpace(title) || string.Equals(title, token.ServiceName, StringComparison.OrdinalIgnoreCase))
            return $"{reason}: {token.ServiceName}";

        return $"{reason}: {token.ServiceName} - {title}";
    }

    private static async Task<bool> TrySetProcessTitleAsync(int pid, string title, CancellationToken cancellationToken)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            for (var i = 0; i < 20; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                p.Refresh();
                if (p.HasExited) return false;
                var hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                    hwnd = FindWindowByProcessId(pid);
                if (hwnd != IntPtr.Zero)
                    return SetWindowText(hwnd, title);

                // VIEWER_TITLE_WINDOW_WAIT_INVARIANT:
                // 指定PIDのウィンドウ生成完了だけを待つ。同期Sleepでworker threadを占有しない。
                // ウィンドウを検出した時点で即時終了し、探索上限は最大2秒とする。
                // 待機条件・上限・所有PID限定はタイトル更新の不変条件として維持する。
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    private sealed class ActivityToken
    {
        public ActivityToken(Guid id, string reason, string serviceName, string title, int AttachedPid)
        {
            Id = id;
            Reason = reason;
            ServiceName = serviceName;
            Title = title;
            this.AttachedPid = AttachedPid;
        }

        public Guid Id { get; }
        public string Reason { get; }
        public string ServiceName { get; }
        public string Title { get; }
        public int AttachedPid { get; }
        public CancellationTokenSource? TitleRefreshCts { get; set; }
    }
}

public sealed class WorkerTitleHandle : IDisposable
{
    private readonly WorkerTitleTracker _owner;
    private readonly Guid _id;
    private int _disposed;

    internal WorkerTitleHandle(WorkerTitleTracker owner, Guid id, int? pid)
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
