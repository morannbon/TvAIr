namespace TvAIr.Core;

/// <summary>
/// Developer Diagnostics のインメモリ循環ログ正本。
///
/// 開発者版では解析用ログを保持する。一般公開版では TVAIR_DEVELOPER_DIAGNOSTICS を定義せず、
/// Add/SetPinnedHeader 呼び出しをコンパイル時に除去して、ログ文字列の生成・バッファ保持・
/// fingerprint管理・EntryAdded配送を発生させない。
///
/// UserEventLogService は一般ユーザー向け運用ログの別正本であり、このクラスの無効化対象ではない。
/// </summary>
public sealed class LogRepository
{
#if TVAIR_DEVELOPER_DIAGNOSTICS
    private readonly object gate = new();
    private readonly Queue<LogEntry> buffer;
    private readonly Dictionary<string, DateTime> recentFingerprints = new();
    private readonly int maxSize;
    private readonly bool verboseLogging;
    private LogEntry? pinnedHeader;
    private static readonly TimeSpan DuplicateSuppressWindow = TimeSpan.FromMinutes(10);
#endif

#if TVAIR_DEVELOPER_DIAGNOSTICS
    public event Action<LogEntry>? EntryAdded;
#else
    // Public build: keep the host contract callable without retaining subscriber delegates.
    // Developer log delivery is absent, so subscribing here must not create a long-lived root.
    public event Action<LogEntry>? EntryAdded
    {
        add { }
        remove { }
    }
#endif

    /// <summary>
    /// 開発者ログ保守が利用可能なのは Developer Diagnostics 有効ビルドだけ。
    /// 公開版から環境変数やflagで復活させる経路は持たない。
    /// </summary>
    public bool DeveloperMaintenanceEnabled => DeveloperDiagnostics.Enabled;

    public LogRepository(int maxSize = 10000)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        this.maxSize = maxSize;
        buffer = new Queue<LogEntry>(maxSize);
        verboseLogging = string.Equals(Environment.GetEnvironmentVariable("TVAIR_VERBOSE_LOG"), "1", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(AppContext.BaseDirectory, "verbose-log.flag"));
#endif
    }

    /// <summary>
    /// 開発者診断の共通入口。公開版では呼び出しと引数評価そのものをコンパイル時に除去する。
    /// </summary>
    [System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
    public void Add(LogEntry entry)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        if (ShouldSuppress(entry)) return;

        lock (gate)
        {
            // ExternalLookup success is a per-request contract trace. Different queries can legitimately
            // produce the same provider/operation/status/body-size message, so exact-message dedupe must
            // not collapse distinct successful requests. The ring buffer remains bounded by maxSize.
            var bypassDuplicateSuppression = string.Equals(entry.Event, "PLUGIN_EXTERNAL_LOOKUP", StringComparison.Ordinal);
            var now = DateTime.Now;
            if (!bypassDuplicateSuppression)
            {
                var fp = BuildFingerprint(entry);
                var suppressWindow = GetDuplicateSuppressWindow(entry);
                if (recentFingerprints.TryGetValue(fp, out var last) && now - last < suppressWindow)
                    return;
                recentFingerprints[fp] = now;

                if (recentFingerprints.Count > maxSize * 2)
                {
                    foreach (var key in recentFingerprints.Where(kv => now - kv.Value > GetDuplicateSuppressWindowForFingerprintKey(kv.Key)).Select(kv => kv.Key).ToList())
                        recentFingerprints.Remove(key);
                }
            }

            if (buffer.Count >= maxSize)
                buffer.Dequeue();
            buffer.Enqueue(entry);
        }

        try { EntryAdded?.Invoke(entry); } catch { }
#endif
    }

    [System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
    public void Add(string eventName, string title, string message)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        Add(new LogEntry { Event = eventName, Title = title, Message = message });
#endif
    }

#if TVAIR_DEVELOPER_DIAGNOSTICS
    private bool ShouldSuppress(LogEntry entry)
    {
        if (verboseLogging) return false;

        var ev = entry.Event ?? string.Empty;
        var title = entry.Title ?? string.Empty;
        var msg = entry.Message ?? string.Empty;

        // 開発者版の通常診断では、高頻度ノイズだけを抑制する。
        // verbose指定は開発者版の中だけで有効。一般公開版ではAdd呼び出し自体がコンパイル時に除去される。
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
        if (ev == "TaskScheduler"
            && string.Equals(title, "WakeCoalesce", StringComparison.OrdinalIgnoreCase)
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

        // 開発診断ログはユーザー運用ログの正本と分離し、
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


#endif

    /// <summary>
    /// 開発者ログ固定ヘッダ。公開版では呼び出しとヘッダ構築をコンパイル時に除去する。
    /// </summary>
    [System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
    public void SetPinnedHeader(LogEntry header)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        if (header is null) throw new ArgumentNullException(nameof(header));
        lock (gate)
        {
            pinnedHeader = Clone(header);
        }
#endif
    }

    public int ClearDeveloperEntries()
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        lock (gate)
        {
            var removed = buffer.Count;
            buffer.Clear();
            recentFingerprints.Clear();
            return removed;
        }
#else
        return 0;
#endif
    }

    public IReadOnlyList<LogEntry> ConsumeAll()
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        lock (gate)
        {
            var body = buffer.ToArray();
            buffer.Clear();
            recentFingerprints.Clear();

            if (pinnedHeader is null) return body;
            var rows = new LogEntry[body.Length + 1];
            rows[0] = Clone(pinnedHeader);
            Array.Copy(body, 0, rows, 1, body.Length);
            return rows;
        }
#else
        return Array.Empty<LogEntry>();
#endif
    }

    public IReadOnlyList<LogEntry> GetAll()
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        lock (gate)
        {
            if (pinnedHeader is null) return buffer.ToArray();
            var rows = new LogEntry[buffer.Count + 1];
            rows[0] = Clone(pinnedHeader);
            var i = 1;
            foreach (var entry in buffer) rows[i++] = entry;
            return rows;
        }
#else
        return Array.Empty<LogEntry>();
#endif
    }

    public IReadOnlyList<LogEntry> GetRecent(int count)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        lock (gate)
        {
            if (count <= 0) return Array.Empty<LogEntry>();
            if (pinnedHeader is null) return buffer.TakeLast(count).ToArray();

            var bodyCount = Math.Max(0, count - 1);
            var body = buffer.TakeLast(bodyCount).ToArray();
            var rows = new LogEntry[body.Length + 1];
            rows[0] = Clone(pinnedHeader);
            Array.Copy(body, 0, rows, 1, body.Length);
            return rows;
        }
#else
        return Array.Empty<LogEntry>();
#endif
    }

#if TVAIR_DEVELOPER_DIAGNOSTICS
    private static LogEntry Clone(LogEntry entry) => new()
    {
        Event = entry.Event,
        Title = entry.Title,
        Message = entry.Message,
        CreatedAt = entry.CreatedAt
    };
#endif
}

