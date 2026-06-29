namespace TvAIr.Core;

/// <summary>
/// インメモリの循環バッファでログエントリを保持するリポジトリ。
/// 最新N件を保持し、古いものは自動的に破棄する。
/// </summary>
public sealed class LogRepository
{
    // release_contract: 表版/裏版ログプロファイル入口。Developmentは従来診断、Releaseは開発者詳細を強く抑制する。
    private enum RuntimeLogProfile { PublicRelease }

    private readonly object gate = new();
    private readonly Queue<LogEntry> buffer;
    private readonly Dictionary<string, DateTime> recentFingerprints = new();
    private readonly int maxSize;
    private readonly bool verboseLogging;
    private readonly RuntimeLogProfile runtimeProfile;

    // 通常運用ではログ一覧を埋めるだけの詳細トレースを抑制する。
    // 調査時だけ環境変数 TVAIR_VERBOSE_LOG=1、または実行フォルダに verbose-log.flag を置くと詳細ログを復活させる。
    private static readonly TimeSpan DuplicateSuppressWindow = TimeSpan.FromMinutes(10);

    public event Action<LogEntry>? EntryAdded;

    public LogRepository(int maxSize = 10000)
    {
        this.maxSize = maxSize;
        buffer = new Queue<LogEntry>(maxSize);
        verboseLogging = string.Equals(Environment.GetEnvironmentVariable("TVAIR_VERBOSE_LOG"), "1", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(AppContext.BaseDirectory, "verbose-log.flag"));
        runtimeProfile = ResolveRuntimeLogProfile();
    }
    private static RuntimeLogProfile ResolveRuntimeLogProfile()
    {
        return RuntimeLogProfile.PublicRelease;
    }

    public void Add(LogEntry entry)
    {
        if (runtimeProfile == RuntimeLogProfile.PublicRelease)
        {
            // 表版: 開発者ログは画面非表示ではなく非生成・非保持。
            // ユーザー運用ログへの変換も行わず、長期運用でログが蓄積しないようにする。
            return;
        }

        if (ShouldSuppress(entry)) return;

        lock (gate)
        {
            var fp = BuildFingerprint(entry);
            var now = DateTime.Now;
            var suppressWindow = GetDuplicateSuppressWindow(entry);
            if (recentFingerprints.TryGetValue(fp, out var last) && now - last < suppressWindow)
                return;
            recentFingerprints[fp] = now;

            if (recentFingerprints.Count > maxSize * 2)
            {
                foreach (var key in recentFingerprints.Where(kv => now - kv.Value > GetDuplicateSuppressWindowForFingerprintKey(kv.Key)).Select(kv => kv.Key).ToList())
                    recentFingerprints.Remove(key);
            }

            if (buffer.Count >= maxSize)
                buffer.Dequeue();
            buffer.Enqueue(entry);
        }

        try { EntryAdded?.Invoke(entry); } catch { }
    }

    public void Add(string eventName, string title, string message)
        => Add(new LogEntry { Event = eventName, Title = title, Message = message });

    private bool ShouldSuppress(LogEntry entry)
    {
        if (verboseLogging) return false;

        var ev = entry.Event ?? string.Empty;
        var title = entry.Title ?? string.Empty;
        var msg = entry.Message ?? string.Empty;

        // release_contract: 通常運用ログでは、周期監視・keepalive・内部割当トレースを抑制する。
        // 必要な場合は TVAIR_VERBOSE_LOG=1 または verbose-log.flag で詳細診断ログを復活させる。
        if (ev == "ALLOC_TRACE") return true;
        if (ev is "EPG_CH2_TS_SCOPE_AUDIT" or "EPG_TS_SCOPE_AUDIT" or "EPG_EIT_COVERAGE" or "EPG_PROCESS_COVERAGE") return true;
        if (ev == "PLUGIN_RENDER_RESULT") return true;
        if (ev == "PLUGIN_SAFE_EVENT_BIND") return true;
        if (ev == "PLUGIN_SAFE_EVENT_KEEPALIVE" && msg.Contains("result=OK", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "RESERVATION_AUDIT" && string.Equals(title, "TickWindow", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "RESERVATION_AUDIT" && string.Equals(title, "DueForce", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "RESERVATION_AUDIT" && title.EndsWith("_PAST_TERMINAL_SUMMARY", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "RESERVATION_AUDIT" && msg.Contains("rule=release_contract", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "RESERVATION_PIPELINE_AUDIT" && msg.Contains("stage=start_request", StringComparison.OrdinalIgnoreCase) && msg.Contains("conflicted=True", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "REC_DUE_SCAN" && msg.Contains("conflicted=True", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "REC_START_REQUEST" && msg.Contains("conflicted=True", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "REC_START_DECISION" && msg.Contains("conflicted=True", StringComparison.OrdinalIgnoreCase) && msg.Contains("stage=group_resolved", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "REC_START_DECISION" && (msg.Contains("reason=conflict_still_true_before_preempt", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("reason=conflict_due_user_event", StringComparison.OrdinalIgnoreCase))) return true;
        if ((ev == "ALLOC_ROUTE" || ev == "ALLOC_POLICY") && msg.Contains("action=Reevaluate:Tick", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "TUNER_ALLOC_SUMMARY" || ev == "TUNER_PRIORITY_CONTRACT" || ev == "TUNER_SKIP_SUMMARY") return true;
        if (ev == "CHAIN_ALLOC_LOCK" && msg.Contains("result=ALLOCATED", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "TaskScheduler" && string.Equals(title, "Wake", StringComparison.OrdinalIgnoreCase)
            && (msg.Contains("reason=plan-hash-nochange", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("reason=nochange", StringComparison.OrdinalIgnoreCase))) return true;
        if (ev == "TaskScheduler" && string.Equals(title, "WakeDebounce", StringComparison.OrdinalIgnoreCase)
            && !msg.Contains("失敗", StringComparison.OrdinalIgnoreCase)
            && !msg.Contains("FAILED", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "TaskScheduler" && (title.StartsWith("WAKE_CLEANUP_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "WAKE_CURRENT_EXTRA_NONBLOCKING_POLICY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "WAKE_CURRENT_EXTRA_NONBLOCKING_SUMMARY", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "WAKE_CURRENT_GENERATION_EXTRA_AUDIT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, "SYSTEM_EPG_WAKE_NOT_REQUIRED_YET", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(title, "WAKE_PLAN_HASH", StringComparison.OrdinalIgnoreCase) && msg.Contains("changed=False", StringComparison.OrdinalIgnoreCase))
            || (string.Equals(title, "WAKE_PLAN_COVERAGE", StringComparison.OrdinalIgnoreCase) && msg.Contains("result=OK", StringComparison.OrdinalIgnoreCase) && msg.Contains("missingCoverage=0", StringComparison.OrdinalIgnoreCase))
            || (string.Equals(title, "WAKE_TASK_MAINTENANCE", StringComparison.OrdinalIgnoreCase) && msg.Contains("class=diagnostic", StringComparison.OrdinalIgnoreCase) && msg.Contains("result=OK", StringComparison.OrdinalIgnoreCase))
            || (string.Equals(title, "WAKE_RUNTIME_AUDIT", StringComparison.OrdinalIgnoreCase) && msg.Contains("inDesired=False", StringComparison.OrdinalIgnoreCase)))) return true;

        if (ev == "TUNER_GROUP") return true;

        // 全件ALLOCATEDは1分ごとに大量発生するため、失敗・競合以外は詳細モードへ回す。
        if (ev == "TUNER_ALLOC" && msg.Contains("result=ALLOCATED", StringComparison.OrdinalIgnoreCase)) return true;

        // EPG確認予約の除外ログは正常系なので通常時は抑制する。
        if (ev == "TUNER_SKIP" && msg.Contains("reason=epg_source_excluded", StringComparison.OrdinalIgnoreCase)) return true;

        // 成功チェーンは通常時は要約で十分。NG/WARNは残す。

        // 高頻度スキャンログは録画直前だけでなく毎Tick出るため、通常時は最終判断系を優先する。
        if (ev == "REC_DUE_SCAN" && !title.StartsWith("R", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "REC_TUNER_CHECK" && !msg.Contains("FAIL", StringComparison.OrdinalIgnoreCase)) return true;

        // release_contract: 表版前の通常診断ログ整理。ユーザー運用ログ正本とは分離し、
        // 録画・競合・Wakeの実異常以外の内部経路トレースを通常ログから外す。
        if (ev == "ALLOC_POLICY") return true;
        if (ev == "ALLOC_ROUTE"
            && (string.Equals(title, "Enter", StringComparison.OrdinalIgnoreCase)
                || string.Equals(title, "Exit", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(title, "Wake", StringComparison.OrdinalIgnoreCase) && msg.Contains("遅延集約", StringComparison.OrdinalIgnoreCase)))) return true;
        if (ev == "RESERVATION_PIPELINE_AUDIT" && !msg.Contains("result=FAILED", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "TUNER_PIPELINE_AUDIT" && !msg.Contains("result=FAILED", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "RECORD_FILENAME_PIPELINE_AUDIT" && !msg.Contains("result=FAILED", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev.StartsWith("PRE_REC_EPG", StringComparison.OrdinalIgnoreCase)
            || ev.StartsWith("PRE_REC_PRETUNE", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "EPG_TUNER_BUSY" || ev == "EPG_TUNER_WAIT") return true;
        if (ev == "Plugin" && string.Equals(title, "NicoJkPlugin", StringComparison.OrdinalIgnoreCase)
            && msg.Contains("comment受信 count=", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "PLUGIN_LIVE_COMMENT_RECEIVED" || ev == "PLUGIN_LIVE_COMMENT_DISPATCH") return true;

        return false;
    }

    private static TimeSpan GetDuplicateSuppressWindow(LogEntry entry)
    {
        var ev = entry.Event ?? string.Empty;
        var title = entry.Title ?? string.Empty;
        var msg = entry.Message ?? string.Empty;

        if (ev == "TUNER_CONFLICT") return TimeSpan.FromHours(1);
        if (ev == "TaskScheduler" && string.Equals(title, "Wake", StringComparison.OrdinalIgnoreCase)
            && msg.Contains("Wakeタスク実体差異検出", StringComparison.OrdinalIgnoreCase)) return TimeSpan.FromMinutes(30);
        if (ev == "TaskScheduler" && (msg.Contains("registered=0 failed=0 deleted=0 deleteFailed=0 kept=", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("registeredNext=none", StringComparison.OrdinalIgnoreCase))) return TimeSpan.FromMinutes(15);
        return DuplicateSuppressWindow;
    }

    private static TimeSpan GetDuplicateSuppressWindowForFingerprintKey(string key)
    {
        if (key.StartsWith("TUNER_CONFLICT", StringComparison.OrdinalIgnoreCase)) return TimeSpan.FromHours(1);
        if (key.StartsWith("TaskSchedulerWakeWakeタスク実体差異検出", StringComparison.OrdinalIgnoreCase)) return TimeSpan.FromMinutes(30);
        return DuplicateSuppressWindow;
    }

    private static string BuildFingerprint(LogEntry entry)
    {
        // 同じ内容の繰り返しだけを抑える。時刻は含めない。
        return $"{entry.Event}\u001f{entry.Title}\u001f{entry.Message}";
    }

    public IReadOnlyList<LogEntry> GetAll()
    {
        lock (gate)
            return buffer.ToArray();
    }

    public IReadOnlyList<LogEntry> GetRecent(int count)
    {
        lock (gate)
            return buffer.TakeLast(count).ToArray();
    }
}

// CONFLICT_OVERRIDE_APPLIED
