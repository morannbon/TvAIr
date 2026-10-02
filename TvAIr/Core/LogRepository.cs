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
    private readonly Queue<LogEntry> auditBuffer;
    private readonly Dictionary<FingerprintKey, FingerprintState> recentFingerprints = new();
    private readonly int maxSize;
    private readonly int auditMaxSize;
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

    public LogRepository(int maxSize = 10000, int auditMaxSize = 20000)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        this.maxSize = Math.Max(1000, maxSize);
        this.auditMaxSize = Math.Max(2000, auditMaxSize);
        buffer = new Queue<LogEntry>(this.maxSize);
        auditBuffer = new Queue<LogEntry>(this.auditMaxSize);
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
                if (recentFingerprints.TryGetValue(fp, out var state) && now - state.LastSeen < suppressWindow)
                    return;
                recentFingerprints[fp] = new FingerprintState(now, suppressWindow);

                if (recentFingerprints.Count > maxSize * 2)
                {
                    foreach (var key in recentFingerprints.Where(kv => now - kv.Value.LastSeen > kv.Value.SuppressWindow).Select(kv => kv.Key).ToList())
                        recentFingerprints.Remove(key);
                }
            }

            // DIAGNOSTIC_RETENTION_SSOT:
            // High-value lifecycle evidence is retained in its own bounded lane so periodic/UI noise
            // cannot evict PreRec/recording/EPG/ownership/terminal evidence before a trouble export.
            // Ordinary diagnostics remain independently bounded. GetAll/GetRecent merge both lanes by time.
            var destination = IsAuditEvidence(entry) ? auditBuffer : buffer;
            var capacity = ReferenceEquals(destination, auditBuffer) ? auditMaxSize : maxSize;
            if (destination.Count >= capacity)
                destination.Dequeue();
            destination.Enqueue(entry);
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

    private readonly record struct FingerprintKey(ulong Hash1, ulong Hash2);
    private readonly record struct FingerprintState(DateTime LastSeen, TimeSpan SuppressWindow);

    private static FingerprintKey BuildFingerprint(LogEntry entry)
    {
        // MEMORY_SSOT: the ring buffer already owns the diagnostic strings.  The dedupe index must
        // never retain a second copy of Event/Title/Message.  Hash the UTF-16 content in-place into
        // a fixed 128-bit key; lengths/separators keep field boundaries unambiguous.
        const ulong offset1 = 14695981039346656037UL;
        const ulong offset2 = 7809847782465536322UL;
        var hash1 = offset1;
        var hash2 = offset2;

        static void Mix(ref ulong h1, ref ulong h2, string? value)
        {
            const ulong prime1 = 1099511628211UL;
            const ulong prime2 = 14029467366897019727UL;
            value ??= string.Empty;
            foreach (var c in value)
            {
                h1 ^= c;
                h1 *= prime1;
                h2 ^= (ulong)c + 0x9E3779B97F4A7C15UL;
                h2 *= prime2;
            }
            h1 ^= 0x1FUL;
            h1 *= prime1;
            h2 ^= 0xA5UL;
            h2 *= prime2;
            h1 ^= (ulong)value.Length;
            h1 *= prime1;
            h2 ^= (ulong)value.Length << 1;
            h2 *= prime2;
        }

        Mix(ref hash1, ref hash2, entry.Event);
        Mix(ref hash1, ref hash2, entry.Title);
        Mix(ref hash1, ref hash2, entry.Message);
        return new FingerprintKey(hash1, hash2);
    }

    private static bool IsAuditEvidence(LogEntry entry)
    {
        var ev = entry.Event ?? string.Empty;
        var msg = entry.Message ?? string.Empty;
        if (ev.StartsWith("PRE_REC", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev.StartsWith("REC_", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev.StartsWith("RECORDING_", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev.StartsWith("EPG_RUN", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev.StartsWith("EPG_SECTION_COMPLETENESS", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev.StartsWith("EPG_ACTUAL_COMMIT_CONTINUITY", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev is "EPG_CAPTURE_START" or "EPG_CAPTURE_END" or "EPG_WORKER_TASK_CONVERGED") return true;
        if (ev.StartsWith("TUNER_OWNERSHIP", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev.StartsWith("TUNER_RECONCILE", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev.StartsWith("Reservation", StringComparison.OrdinalIgnoreCase) || ev.StartsWith("RESERVATION_", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev.StartsWith("CHAIN_", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev.StartsWith("FINAL_ALLOCATION", StringComparison.OrdinalIgnoreCase)) return true;
        if (ev == "PLUGIN_TYPED_EVENT_DISPATCH" && msg.Contains("EpgCompleted", StringComparison.OrdinalIgnoreCase)) return true;
        return msg.Contains("result=FAILED", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("result=FALLBACK", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("failureReason=", StringComparison.OrdinalIgnoreCase);
    }

    private LogEntry[] SnapshotMergedUnsafe()
    {
        return auditBuffer.Concat(buffer)
            .OrderBy(x => x.CreatedAt)
            .ToArray();
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
            var removed = buffer.Count + auditBuffer.Count;
            buffer.Clear();
            auditBuffer.Clear();
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
            var body = SnapshotMergedUnsafe();
            buffer.Clear();
            auditBuffer.Clear();
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
            var body = SnapshotMergedUnsafe();
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

    public IReadOnlyList<LogEntry> GetRecent(int count)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        lock (gate)
        {
            if (count <= 0) return Array.Empty<LogEntry>();
            var merged = SnapshotMergedUnsafe();
            if (pinnedHeader is null) return merged.TakeLast(count).ToArray();

            var bodyCount = Math.Max(0, count - 1);
            var body = merged.TakeLast(bodyCount).ToArray();
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

