using Microsoft.Extensions.Hosting;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Schedule;
using TvAIr.Tuner;

namespace TvAIr.Epg;

public sealed record EpgRunState(
    bool IsRunning,
    bool CanStart,
    bool CanCancel,
    string Source,
    string TargetScope,
    bool Silent,
    string UiMode,
    string CancelRoute);

public sealed record EpgStartBlockInfo(
    string TargetScope,
    string Source,
    bool Silent,
    string Reason,
    string DisplayMessage,
    DateTime? BlockedUntil,
    string BlockedGroup,
    string Owner,
    DateTime CreatedAt);

/// <summary>
/// 毎日指定時刻に EPG 取得を自動実行するバックグラウンドサービス。
/// 手動実行もサポートする。
/// EPG取得完了後にキーワード自動予約エンジンを実行する。
/// </summary>
public sealed class EpgScheduler : BackgroundService
{
    private readonly EpgCapture          capture;
    private readonly ReservationStore    reservationStore;
    private readonly IReadOnlyList<TunerProfile> tunerProfiles;
    private readonly IniSettingsService  ini;
    private readonly ChannelFileLoader   channelLoader;
    private readonly EpgStore            epgStore;
    private readonly TunerPool           tunerPool;
    private readonly LogRepository       log;
    private readonly UserEventLogService userEvents;
    private readonly ReservationAllocationRouteService allocationRoute;

    // appsettings.json由来の静的設定（iniに存在しない項目）
    private readonly int _multiServiceExtraSeconds;

    // EpgDepthは設定保存時に動的更新されるため独立フィールドで管理
    private string _currentDepth;

    /// <summary>現在のEPG深度に対応する1TSあたりの待機秒数</summary>
    private int CurrentWaitSec => EpgDurationPolicy.BaseSecondsForDepth(_currentDepth);

    // 現在有効なEpg設定（動的更新可能）
    private EpgScheduleConfig config;
    private readonly object configGate = new();

    // 実行制御
    private CancellationTokenSource? runCts;
    private bool isRunning;
    private string pendingRunScope = "All";
    private string? pendingRunSource;
    private bool pendingRunSilent;
    private EpgStartBlockInfo? lastStartBlock;
    private readonly object gate = new();

    // 自動スケジューラーの待機を中断するためのトークン（設定変更時にリセット）
    private CancellationTokenSource schedulerWakeCts = new();

    // v0.9.33: 定時EPGは Wake 起動契約で定刻実行させる。
    // 以前の2時間catchupは「7時に始まらない」問題を遅延実行で隠していたため、
    // 起動遅延救済は最小限に制限する。
    private static readonly TimeSpan ScheduledEpgCatchupGrace = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ScheduledEpgRetryDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EpgStartEstimateSafetyMargin = TimeSpan.FromMinutes(2);
    private DateTime? deferredScheduledEpgAt;
    private string deferredScheduledEpgScope = "All";
    private DateTime? lastPartialEpgRepairDate;

    private sealed record EpgStartProtectionBlock(string Group, string Reason, DateTime Until, string Owner, string Label, int RequiredSeconds);

    public EpgScheduler(
        EpgSettings                    settings,
        IniSettingsService             ini,
        EpgCapture                     capture,
        ReservationStore               reservationStore,
        IReadOnlyList<TunerProfile>    tunerProfiles,
        ChannelFileLoader              channelLoader,
        EpgStore                       epgStore,
        TunerPool                      tunerPool,
        LogRepository                  log,
        UserEventLogService            userEvents,
        ReservationAllocationRouteService allocationRoute)
    {
        this.capture                  = capture;
        this.reservationStore         = reservationStore;
        this.tunerProfiles            = tunerProfiles;
        this.ini                      = ini;
        this.channelLoader            = channelLoader;
        this.epgStore                 = epgStore;
        this.tunerPool                = tunerPool;
        this.log                      = log;
        this.userEvents               = userEvents;
        this.allocationRoute          = allocationRoute;
        _multiServiceExtraSeconds     = Math.Max(0, settings.MultiServiceExtraSeconds);
        _currentDepth                 = NormalizeDepth(settings.EpgDepth);
        capture.UpdateRuntimeDepth(_currentDepth);
        config = new EpgScheduleConfig(settings.Enabled, settings.DailyRefreshHour, settings.DailyRefreshMinute);
    }

    private static string NormalizeDepth(string? value) => EpgDurationPolicy.NormalizeDepth(value);

    private static string NormalizeTargetScope(string? value)
    {
        var v = (value ?? "All").Trim().ToUpperInvariant();
        return v switch
        {
            "GR" or "TERRESTRIAL" or "地上波" => "GR",
            "BS" => "BS",
            "CS" => "CS",
            "BSCS" or "BS/CS" or "BSC S" => "BSCS",
            _ => "All"
        };
    }

    private static string BuildManualEpgRouteAction(string source, bool silent)
    {
        if (source.StartsWith("TrayMenu.", StringComparison.OrdinalIgnoreCase))
            return silent ? "TraySilentEpgStart" : "TrayVisibleEpgStart";
        if (source.StartsWith("Scheduler", StringComparison.OrdinalIgnoreCase))
            return "ScheduledSilentEpgStart";
        return silent ? "ManualSilentEpgStart" : "ManualVisibleEpgStart";
    }

    /// <summary>Wakeタスク合流シグナルを受けたとき、定時判定ループを即時再評価させる。</summary>
    public void NotifyWakeSignal(string kind, string at, string source)
    {
        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? "UNKNOWN" : kind.Trim();
        var normalizedAt = string.IsNullOrWhiteSpace(at) ? "-" : at.Trim();
        log.Add("WAKE_SIGNAL", "EPG_SCHEDULER",
            $"result=RECEIVED kind={normalizedKind} at={normalizedAt} source={source} action=wake_scheduler_loop rule=v0.11.393_libisdb_style_eit_reader_rebuild");
        try
        {
            var old = schedulerWakeCts;
            schedulerWakeCts = new CancellationTokenSource();
            old.Cancel();
            old.Dispose();
        }
        catch { }
    }

    public EpgRunState GetRunState()
    {
        lock (gate)
        {
            return new EpgRunState(
                IsRunning: isRunning,
                CanStart: !isRunning,
                CanCancel: isRunning,
                Source: pendingRunSource ?? "",
                TargetScope: pendingRunScope,
                Silent: pendingRunSilent,
                UiMode: pendingRunSilent ? "Silent" : "Visible",
                CancelRoute: pendingRunSilent ? "TrayOnly" : "WidgetOrTray");
        }
    }

    public EpgStartBlockInfo? GetLastStartBlockInfo(string? targetScope = null)
    {
        lock (gate)
        {
            if (lastStartBlock is null) return null;
            if ((DateTime.Now - lastStartBlock.CreatedAt).TotalSeconds > 15) return null;
            var normalizedScope = string.IsNullOrWhiteSpace(targetScope) ? string.Empty : NormalizeTargetScope(targetScope);
            if (!string.IsNullOrWhiteSpace(normalizedScope)
                && !string.Equals(lastStartBlock.TargetScope, normalizedScope, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return lastStartBlock;
        }
    }

    private void RememberStartBlock(string targetScope, string source, bool silent, string reason, DateTime? until, string blockedGroup, string owner)
    {
        var normalizedReason = NormalizeBlockedReason(reason);
        lock (gate)
        {
            lastStartBlock = new EpgStartBlockInfo(
                TargetScope: targetScope,
                Source: source,
                Silent: silent,
                Reason: normalizedReason,
                DisplayMessage: BuildStartBlockDisplayMessage(normalizedReason, string.IsNullOrWhiteSpace(blockedGroup) ? targetScope : blockedGroup),
                BlockedUntil: until,
                BlockedGroup: string.IsNullOrWhiteSpace(blockedGroup) ? targetScope : blockedGroup,
                Owner: string.IsNullOrWhiteSpace(owner) ? "-" : owner,
                CreatedAt: DateTime.Now);
        }
    }

    private void ClearStartBlock()
    {
        lock (gate)
        {
            lastStartBlock = null;
        }
    }

    private static string BuildStartBlockDisplayMessage(string? reason, string? group = null)
    {
        var r = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        // User-facing blocked messages stay short. The affected scope is shown separately as Target.
        if (r.Contains("no_free", StringComparison.OrdinalIgnoreCase)
            || r.Contains("no_tuner", StringComparison.OrdinalIgnoreCase)
            || r.Contains("tuner", StringComparison.OrdinalIgnoreCase) && !r.Contains("recording", StringComparison.OrdinalIgnoreCase))
            return "空きチューナーがないためEPG取得を開始できません";
        if (r.Contains("warmup", StringComparison.OrdinalIgnoreCase))
            return "録画開始直後のためEPG取得を開始できません";
        return "録画予約が近いためEPG取得を開始できません";
    }

    private static string UserScopeLabel(string? group)
    {
        var g = (group ?? string.Empty).Trim().ToUpperInvariant();
        return g switch
        {
            "GR" or "TERRESTRIAL" or "地上波" => "地上波",
            "BS" or "CS" or "BSCS" or "BS/CS" => "BS/CS",
            _ => "全体"
        };
    }

    // ─── 手動実行 ────────────────────────────────────────────────

    /// <summary>EPG取得を今すぐ実行する。既に実行中の場合は false を返す。</summary>
    public bool TriggerNow(string requestedBy = "ManualUi", bool silent = false, string targetScope = "All")
    {
        var normalizedSource = string.IsNullOrWhiteSpace(requestedBy) ? "ManualUi" : requestedBy.Trim();
        var normalizedScope = NormalizeTargetScope(targetScope);

        // v0.11.147:
        // 通常EPGは録画中でも空き録画チューナーがあれば実行してよい。
        // ただし、録画開始待ち / 今すぐ録画 / 開始時刻超過 / dueStart到達済みの予約がある波では、
        // EPGを新規投入してからプリエンプトするのではなく、EPG入口で録画を先に通す。
        if (TryGetRecordingPriorityBlockReason(normalizedScope, out var dueReason, out var dueUntil, out var dueGroup, out var dueOwner))
        {
            RememberStartBlock(normalizedScope, normalizedSource, silent, dueReason, dueUntil, dueGroup, dueOwner);
            var dueDisplayMessage = BuildStartBlockDisplayMessage(dueReason, dueGroup);
            log.Add("EPG_RUN_BLOCKED", "BLOCKED",
                $"{dueDisplayMessage}。targetScope={normalizedScope} blockedGroup={dueGroup} blockedUntil={(dueUntil.HasValue ? dueUntil.Value.ToString("MM/dd HH:mm:ss") : "-")} blockedReason={dueReason} owner={dueOwner} silent={silent} source={normalizedSource} action=recording_due_first rule=v0.11.175_notification_crosscut_cleanup");
            userEvents.AddScheduledEpgCompleted(normalizedScope, normalizedSource, silent, "BLOCKED", 0, 0, 0, 0, $"blockedReason={dueReason}; blockedGroup={dueGroup}; owner={dueOwner}; recordingDue=True");
            return false;
        }

        if (TryGetManualEpgBlockedReason(normalizedScope, out var blockedReason, out var blockedUntil, out var blockedGroup))
        {
            RememberStartBlock(normalizedScope, normalizedSource, silent, blockedReason, blockedUntil, blockedGroup, string.Empty);
            var blockedDisplayMessage = BuildStartBlockDisplayMessage(blockedReason, blockedGroup);
            log.Add("EPG_RUN_BLOCKED", "BLOCKED",
                $"{blockedDisplayMessage}。targetScope={normalizedScope} blockedGroup={blockedGroup} blockedUntil={(blockedUntil.HasValue ? blockedUntil.Value.ToString("MM/dd HH:mm:ss") : "-")} blockedReason={blockedReason} silent={silent} source={normalizedSource} action=recording_lifecycle_gate rule=v0.11.175_notification_crosscut_cleanup");
            userEvents.AddScheduledEpgCompleted(normalizedScope, normalizedSource, silent, "BLOCKED", 0, 0, 0, 0, $"blockedReason={blockedReason}; blockedGroup={blockedGroup}; recordingConcurrent=True");
            return false;
        }
        CancellationTokenSource cts;
        lock (gate)
        {
            if (isRunning)
            {
                log.Add("EPG_RUN_GUARD", "EPG",
                    $"result=BUSY requestedSource={normalizedSource} requestedSilent={silent} requestedScope={normalizedScope} runningSource={(pendingRunSource ?? "-")} runningSilent={pendingRunSilent} runningScope={pendingRunScope} action=reject_start rule=v0.8.78_epg_run_contract");
                return false;
            }
            runCts?.Dispose();
            runCts = new CancellationTokenSource();
            cts = runCts;
            pendingRunScope = normalizedScope;
            pendingRunSource = normalizedSource;
            pendingRunSilent = silent;
            isRunning = true;
            lastStartBlock = null;
        }

        // Phase を即座に running にセット。silent=true の場合、ブラウザを開いてもツール表示不可。
        capture.SetRunning(uiVisible: !silent, runSource: normalizedSource, targetScope: normalizedScope);
        log.Add("EPG_RUN_REQUEST", "EPG",
            $"result=START source={normalizedSource} silent={silent} uiMode={(silent ? "Silent" : "Visible")} targetScope={normalizedScope} route=EpgScheduler.TriggerNow samePipeline=True startGuard=reserved rule=v0.8.78_epg_run_contract");
        userEvents.AddScheduledEpgStarted(normalizedScope, normalizedSource, silent);

        _ = Task.Run(() => RunWithGuardAsync(cts.Token, normalizedScope, normalizedSource, silent));
        return true;
    }

    /// <summary>実行中の EPG 取得をキャンセルする。</summary>
    public bool Cancel(string requestedBy = "ManualUi")
    {
        var normalizedSource = string.IsNullOrWhiteSpace(requestedBy) ? "ManualUi" : requestedBy.Trim();
        CancellationTokenSource? cts;
        string scope;
        lock (gate)
        {
            cts = isRunning ? runCts : null;
            scope = pendingRunScope;
        }
        if (cts is null)
        {
            log.Add("EPG_CANCEL_REQUEST", "EPG",
                $"result=IGNORED source={normalizedSource} reason=not_running rule=v0.5.97_endgap_cancel_genre_color_cleanup");
            return false;
        }
        log.Add("EPG_CANCEL_REQUEST", "EPG",
            $"result=ACCEPT source={normalizedSource} targetScope={scope} action=cancel_run_cts rule=v0.5.97_endgap_cancel_genre_color_cleanup");
        try { cts.Cancel(); } catch { }
        return true;
    }

    public bool IsRunning { get { lock (gate) return isRunning; } }

    /// <summary>スケジュール設定を動的に更新する（設定保存時に呼ぶ）。</summary>
    public void UpdateConfig(bool enabled, int hour, int minute, string epgDepth = "medium")
    {
        lock (configGate)
        {
            config = new EpgScheduleConfig(enabled, Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59));
        }
        // v0.5.95: EPG取得中の深度変更は現在のrunには混ぜず、次回runから反映する。
        var nextDepth = NormalizeDepth(epgDepth);
        var beforeDepth = _currentDepth;
        var depthChanged = !string.Equals(beforeDepth, nextDepth, StringComparison.OrdinalIgnoreCase);
        _currentDepth = nextDepth;
        log.Add("EPG_DEPTH_POLICY", "Settings",
            $"result={(depthChanged ? "CHANGED" : "UNCHANGED")} before={beforeDepth} after={nextDepth} enabled={enabled} time={Math.Clamp(hour, 0, 23):D2}:{Math.Clamp(minute, 0, 59):D2} apply={(IsRunning ? "defer_next_run" : "runtime_now")} rule=epg_duration_policy_common");
        if (IsRunning)
        {
            log.Add("EPG_DEPTH_CHANGE_DEFERRED", "EPG",
                $"currentRunDepth={capture.GetStatus().RunDepth} nextRunDepth={nextDepth} beforeSchedulerDepth={beforeDepth} rule=epg_depth_runtime_contract");
        }
        else
        {
            capture.UpdateRuntimeDepth(_currentDepth);
            log.Add("EPG_SCHEDULER", "Depth", $"runtimeDepth={_currentDepth} source=Settings action=UpdateConfig rule=epg_depth_runtime_contract");
        }

        if (enabled)
        {
            // EPGエントリを新しいスケジュールで更新
            UpsertEpgEntries(hour, minute);
        }
        else
        {
            // EPG設定をOFFにした場合はscheduledなEPGエントリを削除
            try
            {
                var deleted = reservationStore.DeleteAllEpgEntries();
                if (deleted > 0)
                    log.Add("EPG_SCHEDULER", "EpgEntry", $"EPG設定OFF: EPGエントリ{deleted}件を削除しました。");
            }
            catch (Exception ex) { log.Add("EPG_SCHEDULER", "EpgEntry", $"EPGエントリ削除エラー: {ex.Message}"); }
        }

        // 現在の待機を中断して次回時刻を再計算させる
        var old = schedulerWakeCts;
        schedulerWakeCts = new CancellationTokenSource();
        old.Cancel();
        old.Dispose();
        log.Add("EPG_SCHEDULER_CONFIG", "EPG",
            $"EPGスケジュール設定を更新しました。enabled={enabled} time={hour:D2}:{minute:D2}");
    }

    private static string ResolveChannelTargetGroup(ChannelTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.Group))
        {
            var g = target.Group.Trim().ToUpperInvariant();
            if (g is "BS" or "CS" or "BSCS" or "BS/CS") return "BSCS";
            if (g is "GR" or "TERRESTRIAL" or "地上波") return "GR";
        }

        return !string.IsNullOrWhiteSpace(target.ChannelArgument)
            && target.ChannelArgument.Contains("/chspace", StringComparison.OrdinalIgnoreCase)
            ? "BSCS"
            : "GR";
    }

    private static string BuildEpgServiceKey(ushort nid, ushort tsid, ushort sid)
        => $"{nid}:{tsid}:{sid}";

    private bool TryResolvePartialDayEpgRepairScope(DateTime now, out string repairScope, out string detail)
    {
        repairScope = string.Empty;
        detail = string.Empty;

        try
        {
            var dayStart = now.TimeOfDay < TimeSpan.FromHours(4)
                ? now.Date.AddDays(-1).AddHours(4)
                : now.Date.AddHours(4);
            var dayEnd = dayStart.AddDays(1);

            var targets = channelLoader.Load().Targets.ToList();
            if (targets.Count == 0)
            {
                detail = "no_channel_targets";
                return false;
            }

            var dayEvents = epgStore.GetAllRaw()
                .Where(e => e.End > dayStart && e.Start < dayEnd)
                .ToList();
            if (dayEvents.Count == 0)
            {
                detail = $"dayEvents=0 channels={targets.Count} policy=no_auto_full_epg_from_empty_db";
                return false;
            }

            var eventKeys = dayEvents
                .Select(e => BuildEpgServiceKey(e.NetworkId, e.TransportStreamId, e.ServiceId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var channelGroups = targets
                .Select(t => new
                {
                    Target = t,
                    Group = ResolveChannelTargetGroup(t),
                    Key = BuildEpgServiceKey(t.OriginalNetworkId, t.TransportStreamId, t.ServiceId)
                })
                .ToList();

            var totalByGroup = channelGroups
                .GroupBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var missingByGroup = channelGroups
                .Where(x => !eventKeys.Contains(x.Key))
                .GroupBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            if (missingByGroup.Count == 0)
            {
                detail = $"dayEvents={dayEvents.Count} missing=0";
                return false;
            }

            // 部分欠損だけを自動修復対象にする。DBが丸ごと空、または対象波が丸ごと空の場合は
            // 表示/起動だけで全局EPGを勝手に開始せず、通常の手動/定時EPGに任せる。
            var repairGroups = new List<string>();
            var samples = new List<string>();
            foreach (var kv in missingByGroup.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                totalByGroup.TryGetValue(kv.Key, out var total);
                var missing = kv.Value.Count;
                if (total <= 0 || missing <= 0 || missing >= total)
                    continue;

                repairGroups.Add(kv.Key);
                foreach (var item in kv.Value.Take(6))
                    samples.Add($"{item.Group}:{item.Target.Name}:{item.Key}");
            }

            if (repairGroups.Count == 0)
            {
                var rawSummary = string.Join(",", missingByGroup.Select(kv => $"{kv.Key}:{kv.Value.Count}/{totalByGroup.GetValueOrDefault(kv.Key)}"));
                detail = $"dayEvents={dayEvents.Count} missing={rawSummary} policy=no_auto_full_group_epg";
                return false;
            }

            repairScope = repairGroups.Count == 1
                ? repairGroups[0]
                : "All";
            detail = $"date={dayStart:yyyy-MM-dd} dayEvents={dayEvents.Count} repairScope={repairScope} missingSummary={string.Join(",", repairGroups.Select(g => $"{g}:{missingByGroup[g].Count}/{totalByGroup.GetValueOrDefault(g)}"))} missingSample={SafeValue(string.Join("/", samples))} policy=partial_visible_service_repair";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"error={SafeValue(ex.Message)}";
            return false;
        }
    }

    // ─── BackgroundService ───────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.Add("EPG_SCHEDULER_START", "EPG", "EPGスケジューラーを開始しました。");

        // v32.66: 起動時に自動検索予約の整合性をとる。
        // 前回終了後にDBだけ残っているルール・予約の状態が最新のEPGと一致していない
        // 可能性があるため、起動直後に一度 Purge → RunMatching を走らせて予約リストを
        // 最新状態に揃える。これで「ルール作成後に再起動 → 朝まで何も反映されない」
        // という問題を解消する(TvAIr は毎朝6:54に自動再起動される前提のため影響大)。
        try
        {
            var purged = reservationStore.PurgeExpiredKeywordRules();
            if (purged > 0)
                log.Add("KEYWORD_RULE_PURGE", "StartupPurge",
                    $"起動時 期限切れルール掃除: {purged}件 (関連予約も物理削除)");
        }
        catch (Exception ex)
        {
            log.Add("KEYWORD_RULE_PURGE", "StartupPurge", $"起動時ルール掃除エラー: {ex.Message}");
        }

        try
        {
            allocationRoute.Run(new ReservationAllocationRouteRequest(
                Source: "EpgScheduler",
                Action: "StartupSync",
                RunKeywordMatcher: true,
                SyncProgramRuleReservations: true,
                ReevaluateAllocations: true,
                RefreshPreRecordEpgEntries: true,
                RefreshWakeTask: true,
                EmitConflictLogs: true,
                ConflictLogCategory: "EPG_SCHEDULER",
                ConflictLogTitle: "Conflict(StartupSync)"));
        }
        catch (Exception ex)
        {
            log.Add("KEYWORD_MATCH_ERROR", "StartupRunMatching",
                $"起動時マッチング例外: {ex.Message}");
        }

        // 起動時にEPGスケジュールエントリを登録（有効な場合）または削除（無効な場合）
        EpgScheduleConfig initCfg;
        lock (configGate) initCfg = config;
        if (initCfg.Enabled)
        {
            UpsertEpgEntries(initCfg.Hour, initCfg.Minute);
            try { UpsertPreRecordEpgEntries(); }
            catch (Exception ex) { log.Add("EPG_SCHEDULER", "PreRecEpg", $"起動時直前EPGエントリ生成エラー: {ex.Message}"); }
        }
        else
        {
            // EPG設定がOFFの場合はscheduledなEPGエントリを削除
            try
            {
                var deleted = reservationStore.DeleteAllEpgEntries();
                if (deleted > 0)
                    log.Add("EPG_SCHEDULER", "EpgEntry", $"EPG設定OFF: EPGエントリ{deleted}件を削除しました。");
            }
            catch (Exception ex) { log.Add("EPG_SCHEDULER", "EpgEntry", $"EPGエントリ削除エラー: {ex.Message}"); }
        }

        DateTime? lastScheduledRunDate = null;
        string lastNextLogKey = string.Empty;
        DateTime lastNextLogUtc = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            EpgScheduleConfig cfg;
            lock (configGate) cfg = config;

            if (!cfg.Enabled)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch { break; }
                continue;
            }

            var now = DateTime.Now;
            var todayDue = now.Date.AddHours(cfg.Hour).AddMinutes(cfg.Minute);
            var next = now <= todayDue ? todayDue : todayDue.AddDays(1);
            var catchupLimit = todayDue.Add(ScheduledEpgCatchupGrace);
            var alreadyRanToday = lastScheduledRunDate.HasValue && lastScheduledRunDate.Value.Date == now.Date;
            var deferredDue = deferredScheduledEpgAt.HasValue && now >= deferredScheduledEpgAt.Value && !alreadyRanToday;
            var dueNow = (now >= todayDue && now <= catchupLimit && !alreadyRanToday) || deferredDue;

            if (dueNow)
            {
                var scheduledAt = deferredDue ? deferredScheduledEpgAt!.Value : todayDue;
                var delaySec = Math.Max(0, (int)(now - scheduledAt).TotalSeconds);
                var retryScope = string.IsNullOrWhiteSpace(deferredScheduledEpgScope) ? "All" : deferredScheduledEpgScope;
                DateTime? retryAt = null;
                var blockedGroup = string.Empty;
                var blockedReason = string.Empty;
                var scopeToRun = deferredDue ? retryScope : ResolveScheduledEpgScopeOrDefer(now, out retryAt, out blockedGroup, out blockedReason);

                log.Add("EPG_SCHEDULER_DUE", "EPG",
                    $"result=DUE source=Scheduler.Daily scheduled={scheduledAt:yyyy-MM-dd HH:mm:ss} now={now:yyyy-MM-dd HH:mm:ss} catchup={(delaySec > 0)} delaySec={delaySec} windowEnd={catchupLimit:yyyy-MM-dd HH:mm:ss} requestedScope={(deferredDue ? retryScope : "All")} runnableScope={(string.IsNullOrWhiteSpace(scopeToRun) ? "-" : scopeToRun)} rule=v0.11.175_simple_epg_start_guard");

                if (string.IsNullOrWhiteSpace(scopeToRun))
                {
                    deferredScheduledEpgAt = (retryAt ?? now.AddMinutes(10));
                    deferredScheduledEpgScope = "All";
                    log.Add("EPG_SCHEDULER_DEFER", "EPG",
                        $"result=DEFER source=Scheduler.Daily targetScope=All blockedGroup={blockedGroup} reason={blockedReason} retryAt={deferredScheduledEpgAt.Value:yyyy-MM-dd HH:mm:ss} action=move_to_next_safe_slot rule=v0.11.175_simple_epg_start_guard");
                }
                else
                {
                    if (!string.Equals(scopeToRun, "All", StringComparison.OrdinalIgnoreCase) && !deferredDue)
                    {
                        deferredScheduledEpgAt = (retryAt ?? now.AddMinutes(30));
                        deferredScheduledEpgScope = "All";
                        log.Add("EPG_SCHEDULER_DEFER", "EPG",
                            $"result=PARTIAL_RUN_DEFER_REST source=Scheduler.Daily runScope={scopeToRun} blockedGroup={blockedGroup} reason={blockedReason} retryAt={deferredScheduledEpgAt.Value:yyyy-MM-dd HH:mm:ss} action=run_safe_scope_then_retry_all rule=v0.11.175_simple_epg_start_guard");
                    }
                    else if (deferredDue)
                    {
                        deferredScheduledEpgAt = null;
                        deferredScheduledEpgScope = "All";
                    }

                    var started = TriggerNow("Scheduler.Daily", silent: true, targetScope: scopeToRun);
                    if (started)
                    {
                        if (string.Equals(scopeToRun, "All", StringComparison.OrdinalIgnoreCase))
                        {
                            lastScheduledRunDate = now.Date;
                            deferredScheduledEpgAt = null;
                            deferredScheduledEpgScope = "All";
                        }
                        _ = Task.Run(async () =>
                        {
                            while (!stoppingToken.IsCancellationRequested && IsRunning)
                            {
                                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch { break; }
                            }
                            EpgScheduleConfig nextCfg;
                            lock (configGate) nextCfg = config;
                            if (nextCfg.Enabled)
                            {
                                try { UpsertEpgEntries(nextCfg.Hour, nextCfg.Minute); }
                                catch (Exception ex) { log.Add("EPG_SCHEDULER", "EpgEntry", $"定時EPG完了後の次回エントリ再生成エラー: {ex.Message}"); }
                            }
                        }, stoppingToken);
                    }
                    else
                    {
                        if (!deferredScheduledEpgAt.HasValue)
                        {
                            deferredScheduledEpgAt = now.AddMinutes(10);
                            deferredScheduledEpgScope = scopeToRun;
                        }
                        log.Add("EPG_SCHEDULER_DUE", "EPG",
                            $"result=BUSY_OR_BLOCKED source=Scheduler.Daily scheduled={scheduledAt:yyyy-MM-dd HH:mm:ss} now={now:yyyy-MM-dd HH:mm:ss} targetScope={scopeToRun} retryAt={(deferredScheduledEpgAt.HasValue ? deferredScheduledEpgAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-")} rule=v0.11.175_simple_epg_start_guard");
                    }
                }
            }
            else if (now > catchupLimit && !alreadyRanToday && !deferredScheduledEpgAt.HasValue)
            {
                // v0.9.33: 大幅遅延catchupで全体EPGを勝手に開始しない。
                // v0.11.622: ただし、DBが部分的に埋まっていて可視チャンネルだけが欠けている場合は、
                // 全局catchupではなく欠損波だけを論理リソース経路で補修する。
                var delaySec = Math.Max(0, (int)(now - todayDue).TotalSeconds);
                if (lastPartialEpgRepairDate?.Date != now.Date
                    && TryResolvePartialDayEpgRepairScope(now, out var repairScope, out var repairDetail))
                {
                    deferredScheduledEpgAt = now.AddSeconds(10);
                    deferredScheduledEpgScope = repairScope;
                    lastPartialEpgRepairDate = now.Date;
                    log.Add("EPG_SCHEDULER_PARTIAL_REPAIR", "EPG",
                        $"result=DEFER_PARTIAL_REPAIR source=Scheduler.Daily scheduled={todayDue:yyyy-MM-dd HH:mm:ss} now={now:yyyy-MM-dd HH:mm:ss} delaySec={delaySec} windowEnd={catchupLimit:yyyy-MM-dd HH:mm:ss} repairAt={deferredScheduledEpgAt.Value:yyyy-MM-dd HH:mm:ss} targetScope={repairScope} {repairDetail} rule=v0.11.622_partial_epg_repair_catchup");
                }
                else
                {
                    lastScheduledRunDate = now.Date;
                    log.Add("EPG_SCHEDULER_DUE", "EPG",
                        $"result=MISSED_WAKE_CATCHUP_SUPPRESSED source=Scheduler.Daily scheduled={todayDue:yyyy-MM-dd HH:mm:ss} now={now:yyyy-MM-dd HH:mm:ss} delaySec={delaySec} windowEnd={catchupLimit:yyyy-MM-dd HH:mm:ss} action=do_not_start_all_epg reason=late_start_outside_catchup_window rule=epg_scheduler_catchup_contract");
                }
            }

            var minutes = Math.Max(0, (int)(next - now).TotalMinutes);
            var nextLogKey = $"{next:yyyyMMddHHmm}|enabled={cfg.Enabled}|hour={cfg.Hour}|minute={cfg.Minute}";
            var shouldLogNext = !string.Equals(lastNextLogKey, nextLogKey, StringComparison.Ordinal)
                                || DateTime.UtcNow - lastNextLogUtc >= TimeSpan.FromMinutes(10)
                                || minutes <= 10;
            if (shouldLogNext)
            {
                lastNextLogKey = nextLogKey;
                lastNextLogUtc = DateTime.UtcNow;
                log.Add("EPG_SCHEDULER_NEXT", "EPG",
                    $"次回の自動取得: {next:yyyy-MM-dd HH:mm} (約 {minutes} 分後) poll=30s log=throttled_10min dueContract=Scheduler.Daily/SilentEpg catchupGraceMin=5 rule=wake_log_polish_quality_wording_noise_reduce");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, schedulerWakeCts.Token);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), linked.Token);
            }
            catch (OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested) break;
                continue;
            }
        }
    }

    private bool TryGetRecordingPriorityBlockReason(string normalizedScope, out string reason, out DateTime? until, out string blockedGroup, out string owner)
    {
        reason = string.Empty;
        until = null;
        blockedGroup = string.Empty;
        owner = string.Empty;

        var groups = GroupsForScope(normalizedScope).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groups.Count == 0) return false;

        var now = DateTime.Now;
        var preStartSeconds = Math.Max(0, ini.PreStartMarginSeconds);

        var due = reservationStore.GetAll()
            .Where(r => r.Source != ReservationSource.Epg
                     && r.IsEnabled
                     && r.Status == ReservationStatus.Scheduled
                     && r.EndTime > now
                     && groups.Contains(ResolveReservationGroup(r))
                     && IsRecordingDueForEpgPriority(r, now, preStartSeconds))
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        if (due.Count == 0) return false;

        var first = due[0];
        var dueStart = first.StartTime.AddSeconds(-preStartSeconds);
        blockedGroup = string.Join(",", due.Select(ResolveReservationGroup).Distinct(StringComparer.OrdinalIgnoreCase));
        reason = first.Source == ReservationSource.Immediate
            ? "immediate_recording_due"
            : now >= first.StartTime
                ? "recording_start_time_passed"
                : "recording_due_start_reached";
        until = new[] { first.EndTime, now.AddMinutes(3) }.Min();
        owner = $"R{first.Id}";

        log.Add("EPG_RECORDING_PRIORITY_GATE", "EPG",
            $"result=BLOCK_EPG targetScope={normalizedScope} blockedGroup={blockedGroup} owner=R{first.Id} reason={reason} dueStart={dueStart:MM/dd HH:mm:ss} now={now:MM/dd HH:mm:ss} start={first.StartTime:MM/dd HH:mm:ss} end={first.EndTime:MM/dd HH:mm:ss} source={first.Source} service={SafeValue(first.ServiceName)} title={ReservationTitleDisplayContract.ForLog(first.Title)} action=recording_first rule=v0.11.147_recording_lifecycle_epg_gate_state_split");
        return true;
    }

    private static bool IsRecordingDueForEpgPriority(Reservation r, DateTime now, int preStartSeconds)
    {
        var dueStart = r.StartTime.AddSeconds(-preStartSeconds);
        if (r.Source == ReservationSource.Immediate)
            return now >= dueStart || now >= r.StartTime || r.CreatedAt >= r.StartTime;
        return now >= dueStart || now >= r.StartTime;
    }

    private static string ResolveReservationGroup(Reservation r)
        => (!string.IsNullOrWhiteSpace(r.ChannelArgument) &&
            r.ChannelArgument.Contains("/chspace", StringComparison.OrdinalIgnoreCase))
            ? "BSCS"
            : "GR";

    private bool TryGetManualEpgBlockedReason(string normalizedScope, out string reason, out DateTime? until, out string blockedGroup)
    {
        reason = string.Empty;
        until = null;
        blockedGroup = string.Empty;

        var blocked = GetEpgStartProtectionBlocks(normalizedScope, DateTime.Now);
        if (blocked.Count == 0) return false;

        reason = string.Join(",", blocked.Select(x => x.Reason).Distinct(StringComparer.OrdinalIgnoreCase));
        until = blocked.Max(x => x.Until);
        blockedGroup = string.Join(",", blocked.Select(x => x.Group).Distinct(StringComparer.OrdinalIgnoreCase));
        log.Add("EPG_START_PROTECTION_GATE", "EPG",
            $"result=BLOCK targetScope={normalizedScope} blockedGroup={blockedGroup} reason={reason} until={(until.HasValue ? until.Value.ToString("MM/dd HH:mm:ss") : "-")} " +
            $"details=[{string.Join("|", blocked.Select(x => $"{x.Group}:owner={x.Owner}:requiredSec={x.RequiredSeconds}:until={x.Until:MM/dd HH:mm:ss}:reason={x.Reason}"))}] " +
            "action=do_not_enter_epg_worker_queue rule=v0.11.175_simple_epg_start_guard");
        return true;
    }

    private IReadOnlyList<EpgStartProtectionBlock> GetEpgStartProtectionBlocks(string normalizedScope, DateTime now)
    {
        var result = new List<EpgStartProtectionBlock>();
        foreach (var group in GroupsForScope(normalizedScope).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var requiredSec = EstimateScheduledEpgRequiredSeconds(group);
            var requiredUntil = now.AddSeconds(requiredSec);

            if (RecordingLifecycleGate.IsNormalEpgSuppressed(group, requiredUntil, out var suppressUntil, out var suppressReason, out var suppressOwner, out var suppressLabel))
            {
                result.Add(new EpgStartProtectionBlock(
                    group,
                    NormalizeBlockedReason(suppressReason),
                    suppressUntil,
                    string.IsNullOrWhiteSpace(suppressOwner) ? "-" : suppressOwner,
                    string.IsNullOrWhiteSpace(suppressLabel) ? "-" : suppressLabel,
                    requiredSec));
                continue;
            }

            var reservation = reservationStore.GetAll()
                .Where(r => r.Source != ReservationSource.Epg
                         && r.IsEnabled
                         && r.Status == ReservationStatus.Scheduled
                         && r.EndTime > now
                         && string.Equals(ResolveReservationGroup(r), group, StringComparison.OrdinalIgnoreCase)
                         && r.StartTime < requiredUntil)
                .OrderBy(r => r.StartTime)
                .ThenBy(r => r.Id)
                .FirstOrDefault();

            if (reservation is not null)
            {
                result.Add(new EpgStartProtectionBlock(
                    group,
                    "recording_window_insufficient",
                    reservation.EndTime.Add(ScheduledEpgRetryDelay),
                    $"R{reservation.Id}",
                    $"service={SafeValue(reservation.ServiceName)} title={ReservationTitleDisplayContract.ForLog(reservation.Title)}",
                    requiredSec));
            }
        }
        return result;
    }

    private int EstimateScheduledEpgRequiredSeconds(string group)
    {
        var normalizedGroup = string.Equals(group, "BSCS", StringComparison.OrdinalIgnoreCase) ? "BSCS" : "GR";
        var tunerCount = tunerProfiles.Count(t =>
            !string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(t.Group, normalizedGroup, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Group, "HYBRID", StringComparison.OrdinalIgnoreCase)));
        tunerCount = Math.Max(1, tunerCount);

        try
        {
            var targets = channelLoader.Load().Targets;
            var totalSec = targets
                .Where(t => string.Equals(t.ChannelArgument.Contains("/chspace", StringComparison.OrdinalIgnoreCase) ? "BSCS" : "GR", normalizedGroup, StringComparison.OrdinalIgnoreCase))
                .GroupBy(t => new { t.OriginalNetworkId, t.TransportStreamId })
                .Select(g => EpgDurationPolicy.CreateSchedulePlan(_currentDepth, _multiServiceExtraSeconds, g.Count()).RecDurationSeconds)
                .Sum();
            if (totalSec > 0)
                return (int)Math.Ceiling((double)totalSec / tunerCount) + (int)EpgStartEstimateSafetyMargin.TotalSeconds;
        }
        catch { }

        var defaultTsCount = string.Equals(normalizedGroup, "BSCS", StringComparison.OrdinalIgnoreCase) ? 36 : 6;
        return (int)Math.Ceiling((double)(CurrentWaitSec * defaultTsCount) / tunerCount) + (int)EpgStartEstimateSafetyMargin.TotalSeconds;
    }

    private string ResolveScheduledEpgScopeOrDefer(DateTime now, out DateTime? retryAt, out string blockedGroup, out string reason)
    {
        retryAt = null;
        blockedGroup = string.Empty;
        reason = string.Empty;
        var blocks = GetEpgStartProtectionBlocks("All", now);
        if (blocks.Count == 0) return "All";

        var blocked = blocks.Select(x => x.Group).ToHashSet(StringComparer.OrdinalIgnoreCase);
        blockedGroup = string.Join(",", blocked);
        reason = string.Join(",", blocks.Select(x => x.Reason).Distinct(StringComparer.OrdinalIgnoreCase));
        retryAt = blocks.Max(x => x.Until);

        var grBlocked = blocked.Contains("GR");
        var bscsBlocked = blocked.Contains("BSCS");
        if (grBlocked && bscsBlocked) return string.Empty;
        if (grBlocked) return "BSCS";
        if (bscsBlocked) return "GR";
        return "All";
    }

    private static IEnumerable<string> GroupsForScope(string normalizedScope)
    {
        var scope = string.IsNullOrWhiteSpace(normalizedScope) ? "ALL" : normalizedScope.Trim().ToUpperInvariant();
        if (scope is "GR") return new[] { "GR" };
        if (scope is "BS" or "CS" or "BSCS") return new[] { "BSCS" };
        return new[] { "GR", "BSCS" };
    }

    private static string NormalizeBlockedReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "recording_preempt_lock";
        var normalized = reason.Trim();
        if (normalized.Contains("window_insufficient", StringComparison.OrdinalIgnoreCase)) return "recording_window_insufficient";
        if (normalized.Contains("warmup", StringComparison.OrdinalIgnoreCase)) return "recording_warmup";
        if (normalized.Contains("stable", StringComparison.OrdinalIgnoreCase)) return "recording_stable_free_tuner_allowed";
        return normalized.Contains("recording", StringComparison.OrdinalIgnoreCase)
            ? "recording_preempt_lock"
            : normalized;
    }

    // ─── 内部処理 ────────────────────────────────────────────────

    private async Task RunWithGuardAsync(CancellationToken ct, string targetScope, string requestedBy, bool silent)
    {
        try
        {
            var routeAction = BuildManualEpgRouteAction(requestedBy, silent);
            try
            {
                allocationRoute.Run(new ReservationAllocationRouteRequest(
                    Source: "EpgScheduler",
                    Action: routeAction,
                    RunKeywordMatcher: false,
                    SyncProgramRuleReservations: false,
                    ReevaluateAllocations: true,
                    RefreshPreRecordEpgEntries: false,
                    RefreshWakeTask: false,
                    EmitConflictLogs: false,
                    ExecutionMode: silent ? "SilentEpg" : "VisibleEpg"));
            }
            catch (Exception ex)
            {
                log.Add("EPG_ALLOC_ROUTE", "WARN",
                    $"result=WARN source={requestedBy} action={routeAction} silent={silent} targetScope={targetScope} error={ex.GetType().Name}:{ex.Message} rule=v0.8.78_epg_run_contract");
            }

            log.Add("EPG_PIPELINE_AUDIT", "RunWithGuard",
                $"source={requestedBy} silent={silent} uiMode={(silent ? "Silent" : "Visible")} targetScope={targetScope} depth={_currentDepth} sameCapturePipeline=True commonRouteAction={routeAction} runScopedActivityKeeper=False activityKeeperTvTest=False rule=v0.8.78_epg_run_contract");
            var captureResult = await capture.RunAsync(ct, targetScope, _currentDepth, showProgress: !silent);
            userEvents.AddScheduledEpgCompleted(targetScope, requestedBy, silent, captureResult.RunResult, captureResult.CompletedGroups, captureResult.TotalGroups, captureResult.ImportedEvents, captureResult.MissingGroups, captureResult.Detail);

            // EPG取得完了: エントリをcompletedに変更
            try { reservationStore.CompleteEpgScheduleEntries(); }
            catch (Exception ex) { log.Add("EPG_SCHEDULER", "EpgEntry", $"EPGエントリ完了処理エラー: {ex.Message}"); }

            // EPG取得完了後にキーワード自動予約を実行
            if (!ct.IsCancellationRequested)
            {
                // v32.65: マッチング前に期限切れルールを掃除(期限切れルール由来の予約も物理削除)。
                // これで PurgeExpiredKeywordRules のデッドコード状態を解消し、
                // EPG取得というメンテナンスタイミングに合わせて整合性を保つ。
                try
                {
                    var purged = reservationStore.PurgeExpiredKeywordRules();
                    if (purged > 0)
                        log.Add("KEYWORD_RULE_PURGE", "ExpiredRules",
                            $"期限切れルールを掃除: {purged}件 (関連予約も物理削除)");
                }
                catch (Exception ex)
                {
                    log.Add("KEYWORD_RULE_PURGE", "ExpiredRules", $"期限切れルール掃除エラー: {ex.Message}");
                }

                // v0.11.380: EPG取り込み後の縦串順序を固定する。
                // 先に既存EventIdentity予約を最新EITへ追従させ、その後に自動検索/ProgramRule/割当/PreRecEpg/Wakeを
                // 1本の共通割当ルートで通す。古い時刻のまま一度割当評価してから追従する二段評価を避ける。
                var timeFollowUpdated = 0;
                try
                {
                    timeFollowUpdated = ApplyTimeFollowingWithAudit(requestedBy, targetScope, reevaluateOnUpdated: false);
                }
                catch (Exception ex)
                {
                    log.Add("EPG_SCHEDULER", "TimeFollow", $"時間追従エラー: {ex.Message} rule=v0.11.380_epg_update_route_order_batch");
                }

                try
                {
                    allocationRoute.Run(new ReservationAllocationRouteRequest(
                        Source: "EpgScheduler",
                        Action: "EpgCompletePostImport",
                        RunKeywordMatcher: true,
                        SyncProgramRuleReservations: true,
                        ReevaluateAllocations: true,
                        RefreshPreRecordEpgEntries: true,
                        RefreshWakeTask: true,
                        EmitConflictLogs: true,
                        ConflictLogCategory: "EPG_SCHEDULER",
                        ConflictLogTitle: "Conflict(EpgCompletePostImport)"));
                    log.Add("EPG_SCHEDULER", "PostImportRoute", $"result=OK timeFollowUpdated={timeFollowUpdated} route=ALLOC_ROUTE matcher=True syncProgram=True reevaluate=True preRec=True wake=True rule=v0.11.380_epg_update_route_order_batch");
                }
                catch (Exception ex)
                {
                    log.Add("EPG_SCHEDULER", "PostImportRoute", $"result=ERROR timeFollowUpdated={timeFollowUpdated} error={ex.Message} rule=v0.11.380_epg_update_route_order_batch");
                }

                // 翌日のEPGスケジュールエントリを登録
                try
                {
                    EpgScheduleConfig cfg2;
                    lock (configGate) cfg2 = config;
                    if (cfg2.Enabled)
                        UpsertEpgEntries(cfg2.Hour, cfg2.Minute);
                }
                catch (Exception ex)
                {
                    log.Add("EPG_SCHEDULER", "EpgEntry", $"翌日EPGエントリ登録エラー: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            var msg = $"EPG取得がキャンセルされました。source={requestedBy} silent={silent} targetScope={targetScope}";

            // v0.8.78: EPG_RUN_END と ALLOC_ROUTE:EpgCancelled は、停止要求を出した直後ではなく、
            // TvAIrEpgRec worker 停止・Activity解放・EPG lease解放が落ち着いた後に出す。
            // これにより、キャンセル直後の再評価が Epg/R- 残存状態を見てしまう順序不整合を避ける。
            try
            {
                await capture.WaitForCancellationQuiescenceAsync(requestedBy, silent, targetScope);
            }
            catch (Exception ex)
            {
                log.Add("EPG_CANCEL_RELEASE_WAIT", "WARN",
                    $"result=WARN source={requestedBy} silent={silent} targetScope={targetScope} error={ex.GetType().Name}:{ex.Message} action=continue_before_epg_run_end rule=v0.8.78_epg_cancel_quiescence_contract");
            }

            log.Add("EPG_RUN_END", "CANCELLED", $"result=CANCELLED source={requestedBy} silent={silent} targetScope={targetScope} releaseComplete=True rule=v0.8.78_epg_cancel_quiescence_contract");
            userEvents.AddScheduledEpgCancelled(targetScope, requestedBy, silent);
            log.Add("EPG_RUN_CANCELLED", "EPG", msg);
            capture.SetStatus(st => st with { Phase = "cancelled", CompletedGroups = 0, LastRunAt = DateTime.Now, LastRunMessage = "キャンセル済み", UiVisible = !silent, UiMode = silent ? "Silent" : "Visible", CancelRoute = silent ? "TrayOnly" : "WidgetOrTray" });
            try
            {
                allocationRoute.Run(new ReservationAllocationRouteRequest(
                    Source: "EpgScheduler",
                    Action: "EpgCancelled",
                    RunKeywordMatcher: false,
                    SyncProgramRuleReservations: false,
                    ReevaluateAllocations: true,
                    RefreshPreRecordEpgEntries: false,
                    RefreshWakeTask: false,
                    EmitConflictLogs: false,
                    ExecutionMode: silent ? "SilentEpg" : "VisibleEpg"));
            }
            catch (Exception ex)
            {
                log.Add("EPG_ALLOC_ROUTE", "WARN",
                    $"result=WARN source={requestedBy} action=EpgCancelled silent={silent} targetScope={targetScope} error={ex.GetType().Name}:{ex.Message} rule=v0.8.78_epg_cancel_quiescence_contract");
            }
        }
        catch (Exception ex)
        {
            userEvents.AddScheduledEpgFailed(targetScope, requestedBy, silent, $"{ex.GetType().Name}: {ex.Message}");
            log.Add("EPG_RUN_ERROR", "EPG", $"EPG取得中に例外が発生しました: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // キャンセル・例外時もPhaseをidleに戻す（正常完了時はRunAsync内で既にidleにしている）
            capture.SetStatus(s => s.Phase == "running"
                ? s with { Phase = ct.IsCancellationRequested ? "cancelled" : "idle", UiVisible = !silent, UiMode = silent ? "Silent" : "Visible", CancelRoute = silent ? "TrayOnly" : "WidgetOrTray" }
                : s);
            lock (gate)
            {
                isRunning = false;
                pendingRunSource = null;
                pendingRunSilent = false;
            }
        }
    }

    /// <summary>
    /// 時刻を次の30分境界（:00 or :30）に切り上げる。
    /// </summary>
    private static DateTime CeilTo30Min(DateTime dt)
    {
        var totalMin = dt.Hour * 60 + dt.Minute;
        var ceiled   = (int)Math.Ceiling(totalMin / 30.0) * 30;
        return dt.Date.AddMinutes(ceiled);
    }

    /// <summary>秒数を次の1分境界に切り上げる。40秒→1分、120秒→2分。</summary>
    private static int CeilToMinutes(int seconds)
        => (int)Math.Ceiling(seconds / 60.0);

    /// <summary>
    /// チューナー個別に直近のユーザー予約1件のみ録画直前EPG確認エントリを登録する。
    ///
    /// 【仕様】
    /// - 対象: source が manual(番組表予約)/keyword のユーザー予約のみ（system epg は除外）
    /// - チューナー名でグループ化し、各チューナーの直近1件を対象とする
    /// - 以下の全条件を満たす場合のみEPG確認エントリを登録：
    ///   1. 直近ユーザー予約の開始時刻が定時EPGエントリの開始時刻より前
    ///   2. その予約のchannel_argumentと一致するチューナーが現在録画中でない
    ///      （録画中なら時間追従が機能するため新たなEPGセッション不要）
    ///   3. 空きチューナーが存在する（空きなしならユーザー予約を優先してEPG確認セッションを発生させない）
    /// - 結果として予約リストのシステムEPG関連エントリは常時最大6件に収まる
    /// </summary>
    public void RefreshPreRecordEpgEntries()
    {
        try
        {
            UpsertPreRecordEpgEntries();
        }
        catch (Exception ex)
        {
            log.Add("EPG_SCHEDULER", "PreRecEpgRefresh", $"直前EPG確認エントリ再構築失敗: {ex.Message}");
            throw;
        }
    }

    private void UpsertPreRecordEpgEntries()
    {
        if (ini.EpgPreRecordMinutes <= 0) return;

        var preRecordRouteMutationCount = 0;
        var protectedViewingTuners = tunerProfiles
            .Where(t => string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var tuner in protectedViewingTuners)
        {
            var deleted = reservationStore.DeleteScheduledEpgEntriesForTuner(tuner.Name);
            if (deleted > 0)
            {
                preRecordRouteMutationCount += deleted;
                log.Add("TUNER_PROTECT", "Viewing", $"tuner={tuner.Name} role=Viewing skipped_from=EPG deletedScheduledEpg={deleted}");
            }
        }

        var recordableTuners = tunerProfiles
            .Where(t => !string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var preMin  = (ini.EpgPreRecordMinutes is 5 or 10 or 15 or 20 ? ini.EpgPreRecordMinutes : 15);
        var waitSec = CurrentWaitSec;
        var durMin  = Math.Max(1, CeilToMinutes(waitSec));
        var now     = DateTime.Now;

        // ユーザー予約のみ・チューナー割り当て済み・直近順に取得。
        // Manual は番組表からの通常予約であり、録画前EPG確認の正式対象。
        // v32.85: 無効(IsEnabled=false)と競合(IsConflicted=true)予約はEPG確認対象外
        // (録画されない予約のためにEPGセッションを発生させる意味がない)
        var staleCleanup = reservationStore.DeleteInvalidScheduledPreRecordEpgEntries();
        if (staleCleanup.Deleted > 0)
        {
            log.Add("PRE_REC_EPG_PARENT_CLEANUP", "Summary",
                $"result=DELETED deleted={staleCleanup.Deleted} parentMissing={staleCleanup.ParentMissing} parentDisabled={staleCleanup.ParentDisabled} parentTerminal={staleCleanup.ParentTerminal} parentConflicted={staleCleanup.ParentConflicted} parentUserChain={staleCleanup.ParentUserChain} parents=[{staleCleanup.ParentIds}] action=remove_orphan_or_non_recordable_prerec_epg rule=v0.11.254_prerec_parent_state_cleanup");
        }

        var scheduledAll = reservationStore.GetByStatus(ReservationStatus.Scheduled).ToList();
        var userScheduled = scheduledAll
            // システム予約(source=Epg)には確認EPGを走らせない。
            // Program はプログラム録画系であり、番組表予約(Manual)とは別扱い。
            // v0.9.70: ユーザー明示チェーン後続は独立したPreRecEpg/時間追従対象にしない。
            // 一挙放送の大量チェーン指定で、後続セグメントごとのEPG確認が
            // 連続契約を崩すことを防ぐ。チェーン境界はTvAIr本体の契約で扱う。
            .Where(r => (r.Source == ReservationSource.Manual || r.Source == ReservationSource.KeywordSearch || r.Source == ReservationSource.Keyword)
                     && !r.IsUserChain
                     && !string.IsNullOrWhiteSpace(r.TunerName)
                     && r.IsEnabled
                     && !r.IsConflicted)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();
        var skippedDisabled = scheduledAll.Count(r => r.Source != ReservationSource.Epg && !r.IsEnabled);
        var skippedConflicted = scheduledAll.Count(r => r.Source != ReservationSource.Epg && r.IsConflicted);
        var skippedNoTuner = scheduledAll.Count(r => r.Source != ReservationSource.Epg && string.IsNullOrWhiteSpace(r.TunerName));
        var skippedUserChain = scheduledAll.Count(r => r.Source != ReservationSource.Epg && r.IsUserChain);
        var epgEntryMutationCount = preRecordRouteMutationCount + staleCleanup.Deleted;

        if (userScheduled.Count == 0)
        {
            log.Add("EPG_SCHEDULER", "PreRecEpg",
                $"result=NO_TARGETS candidates=0 skippedDisabled={skippedDisabled} skippedConflicted={skippedConflicted} skippedNoTuner={skippedNoTuner} manualIncluded=True skippedUserChain={skippedUserChain} action=restore_daily_epg rule=v0.9.71_chain_follow_preserve_tail");
            // 予約イベントが無い状態では直前EPG確認を残さない。
            // 既存のEPG確認エントリを各チューナーごとに掃除し、必要なら定時EPGへ戻す。
            EpgScheduleConfig emptyCfg;
            lock (configGate) emptyCfg = config;
            if (emptyCfg.Enabled)
            {
                foreach (var tuner in recordableTuners)
                    epgEntryMutationCount += reservationStore.RestoreDailyEpgEntry(tuner, recordableTuners);
            }
            ReevaluateAndLog("PreRecEpgEmpty");
            return;
        }

        static string ResolveGroup(Reservation r)
            => (!string.IsNullOrWhiteSpace(r.ChannelArgument) &&
                r.ChannelArgument.Contains("/chspace", StringComparison.OrdinalIgnoreCase))
                ? "BSCS" : "GR";

        // 定時EPGエントリの開始時刻をグループ別に取得
        var epgScheduled = reservationStore.GetByStatus(ReservationStatus.Scheduled)
            .Where(r => r.Source == ReservationSource.Epg)
            .ToList();

        var dailyEpgStartByTuner = epgScheduled
            .Where(r => !string.IsNullOrWhiteSpace(r.TunerName)
                     && r.Title.StartsWith("EPG取得"))
            .GroupBy(r => r.TunerName)
            .ToDictionary(g => g.Key, g => g.Min(r => r.StartTime));

        // 現在録画中のchannel_argumentセット
        var recordingChannelArgs = tunerPool.GetStatus()
            .Where(s => s.UsageKind == TunerUsageKind.Recording
                     && s.ReservationId.HasValue)
            .Select(s => s.ReservationId!.Value)
            .ToHashSet();

        // 録画中予約のchannel_argumentを取得
        var recordingArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rid in recordingChannelArgs)
        {
            var rec = reservationStore.GetById(rid);
            if (rec != null && !string.IsNullOrWhiteSpace(rec.ChannelArgument))
                recordingArgs.Add(rec.ChannelArgument);
        }

        // チューナー名ごとに直近1件を評価
        var perTuner = userScheduled
            .GroupBy(r => r.TunerName)
            .Select(g => g.First()); // OrderBy済みなので先頭が直近

        // EPG確認エントリを登録するチューナー名セット
        var confirmTuners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var registeredCount = 0;
        var skippedExpiredDeadline = 0;
        var deletedExpiredDeadline = 0;
        var dedupeReuseCount = 0;
        var dedupeKeepScheduledCount = 0;
        var dedupeTunerReboundCount = 0;
        var dedupeCompletedOrTerminalCount = 0;
        var dedupeParentIds = new SortedSet<int>();
        foreach (var r in perTuner)
        {
            if (tunerPool.IsViewingReservedTuner(r.TunerName))
            {
                var deleted = reservationStore.DeleteScheduledEpgEntriesForTuner(r.TunerName);
                epgEntryMutationCount += deleted;
                log.Add("TUNER_PROTECT", "Viewing", $"tuner={r.TunerName} role=Viewing skipped_from=PreRecEpg parent=R{r.Id} deletedScheduledEpg={deleted}");
                continue;
            }

            var epgStart = r.StartTime.AddMinutes(-preMin);
            var epgEnd   = epgStart.AddMinutes(durMin);
            if (epgStart <= now)
            {
                skippedExpiredDeadline++;
                var deleted = reservationStore.DeleteScheduledPreRecordEpgEntriesForParent(r.Id);
                deletedExpiredDeadline += deleted;
                epgEntryMutationCount += deleted;
                log.Add("PRE_REC_EPG_DEADLINE", $"R{r.Id}",
                    $"result=SKIP_REGISTER reason=deadline_already_passed parent=R{r.Id} service={SafeValue(r.ServiceName)} title={ReservationTitleDisplayContract.ForLog(r.Title)} now={now:MM/dd HH:mm:ss} epgStart={epgStart:MM/dd HH:mm:ss} programStart={r.StartTime:MM/dd HH:mm:ss} preMin={preMin} deletedScheduledEpg={deleted} action=recording_priority rule=v0.11.141_prerec_deadline_epg_blocked_classification");
                continue;
            }
            if (epgEnd <= now) continue; // 既に過去ならスキップ

            // 条件1: 直近ユーザー予約が定時EPGより前にある場合のみ
            if (dailyEpgStartByTuner.TryGetValue(r.TunerName, out var dailyStart)
                && r.StartTime >= dailyStart)
                continue; // 定時EPGより後 → 定時EPGが代替するためスキップ

            // 条件2: 同一channel_argumentが録画中なら時間追従が機能するためスキップ
            if (!string.IsNullOrWhiteSpace(r.ChannelArgument)
                && recordingArgs.Contains(r.ChannelArgument))
                continue;

            // 条件3: 空きチューナーがない場合はユーザー予約を優先してスキップ
            var group = ResolveGroup(r);
            if (!tunerPool.HasFreeSlot(group))
                continue;

            if (reservationStore.TryFindReusablePreRecordEpgEntry(
                    r.Id,
                    r.TunerName,
                    epgStart,
                    epgEnd,
                    r.EventId,
                    out var reusablePreRecId,
                    out var reusablePreRecStatus,
                    out var reusablePreRecTuner))
            {
                // v0.10.09:
                // 同一親予約・同一イベント・同一時間窓のPreRecEpgは、チューナー再割当だけでは再生成しない。
                // Scheduledの既存子予約だけは最新の録画予定チューナーへ付け替え、Wakeタスクは分離したまま維持する。
                var keepScheduledEntry = reusablePreRecStatus is ReservationStatus.Scheduled or ReservationStatus.Recording;
                var tunerRebound = false;
                var metaNormalized = false;
                if (reusablePreRecStatus == ReservationStatus.Scheduled)
                {
                    // v0.11.197:
                    // 既存PreRecEpg子予約をdedupe再利用する場合も、子予約Titleは内部用途名へ固定し、
                    // 親番組名は parent/source_rule_id 側のメタ属性として分離する。
                    reservationStore.RebindScheduledPreRecordEpgEntry(reusablePreRecId, r.Id, epgStart, epgEnd, r.TunerName);
                    epgEntryMutationCount++;
                    metaNormalized = true;
                    tunerRebound = !string.Equals(reusablePreRecTuner, r.TunerName, StringComparison.OrdinalIgnoreCase);
                }

                if (keepScheduledEntry)
                    confirmTuners.Add(r.TunerName);

                dedupeReuseCount++;
                dedupeParentIds.Add(r.Id);
                if (keepScheduledEntry) dedupeKeepScheduledCount++;
                if (tunerRebound) dedupeTunerReboundCount++;
                if (!keepScheduledEntry) dedupeCompletedOrTerminalCount++;

                // v0.10.56: repeated successful PreRecEpg dedupe is expected during startup/re-evaluation.
                // Keep normal logs as a summary; emit per-reservation rows only when a visible action occurred.
                if (tunerRebound || metaNormalized)
                {
                    log.Add("PRE_REC_EPG_DEDUPE", $"R{r.Id}",
                        $"result={(tunerRebound ? "SKIP_REUSE_REBOUND" : "SKIP_REUSE_META_NORMALIZED")} existing=R{reusablePreRecId} existingStatus={reusablePreRecStatus} parent=R{r.Id} " +
                        $"existingTuner={reusablePreRecTuner} tuner={r.TunerName} tunerRebound={tunerRebound} metaNormalized={metaNormalized} group={group} eventId={r.EventId} epg={epgStart:MM/dd HH:mm:ss}〜{epgEnd:MM/dd HH:mm:ss} " +
                        $"keepScheduledEntry={keepScheduledEntry} reason=same_parent_event_window_already_checked_ignoring_tuner " +
                        $"rule=v0.11.408_epg_db_cache_cleanup");
                }
                continue;
            }

            reservationStore.UpsertPreRecordEpgEntry(r.Id, group, epgStart, epgEnd, r.TunerName);
            epgEntryMutationCount++;
            log.Add("RESERVE_ENTRY", "SystemEpg", $"共通入口要求 source=System parent=R{r.Id} tuner=[{r.TunerName}] group={group} epg={epgStart:MM/dd HH:mm}〜{epgEnd:MM/dd HH:mm} title=[録画前EPG確認（{r.TunerName}）] parentTitle=[{ReservationTitleDisplayContract.ForLog(r.Title)}]");
            confirmTuners.Add(r.TunerName);
            registeredCount++;
        }

        // EPG確認エントリを登録しなかったチューナーについて、
        // 既存のEPG確認エントリを削除し定時EPGエントリを復元する
        EpgScheduleConfig cfg;
        lock (configGate) cfg = config;
        if (cfg.Enabled)
        {
            foreach (var tuner in recordableTuners)
            {
                if (confirmTuners.Contains(tuner.Name)) continue;
                epgEntryMutationCount += reservationStore.RestoreDailyEpgEntry(tuner, recordableTuners);
            }
        }

        if (dedupeReuseCount > 0)
        {
            log.Add("PRE_REC_EPG_DEDUPE", "Summary",
                $"result=OK reused={dedupeReuseCount} keepScheduled={dedupeKeepScheduledCount} terminalOrCompleted={dedupeCompletedOrTerminalCount} tunerRebound={dedupeTunerReboundCount} parents=[{string.Join(',', dedupeParentIds.Select(x => $"R{x}"))}] rule=v0.11.408_epg_db_cache_cleanup");
        }

        log.Add("EPG_SCHEDULER", "PreRecEpg",
            $"result={(registeredCount > 0 ? "REGISTERED" : "NO_REGISTER")} candidates={userScheduled.Count} registered={registeredCount} dedupeReused={dedupeReuseCount} dedupeParents=[{string.Join(',', dedupeParentIds.Select(x => $"R{x}"))}] staleDeleted={staleCleanup.Deleted} staleParents=[{staleCleanup.ParentIds}] skippedDisabled={skippedDisabled} skippedConflicted={skippedConflicted} skippedNoTuner={skippedNoTuner} skippedExpiredDeadline={skippedExpiredDeadline} deletedExpiredDeadline={deletedExpiredDeadline} preMin={preMin} durMin={durMin} manualIncluded=True skippedUserChain={skippedUserChain} routeMutations={epgEntryMutationCount} wakeRefresh=by_caller rule=v0.11.378_prerec_epg_route_mutation_guard");
        if (epgEntryMutationCount > 0 || registeredCount > 0)
        {
            ReevaluateAndLog("PreRecEpg", refreshWakeTask: false);
        }
    }

    /// <summary>
    /// 定時EPGエントリをチューナー1本1行で生成・更新する。
    /// 終了時刻 = CeilTo30Min(開始 + 各TSの所要秒数の合計 / チューナー本数)
    /// 各TSの所要秒数 = waitSec。v0.8.01では、EPG不足を秒数延長で隠さず、チャンネル/TS/SID束ねを優先して監査する。
    /// </summary>
    private void UpsertEpgEntries(int hour, int minute)
    {
        var nextBase = CalcNextRun(DateTime.Now, new EpgScheduleConfig(true, hour, minute));
        var start    = nextBase.AddMinutes(-1);
        var scheduleDuration = EpgDurationPolicy.CreateSchedulePlan(_currentDepth, _multiServiceExtraSeconds);
        var waitSec = scheduleDuration.RecDurationSeconds;
        log.Add("EPG_DURATION_POLICY", "Schedule",
            $"depth={scheduleDuration.Depth} configuredBase={scheduleDuration.ConfiguredBaseSeconds}s effectiveBase={scheduleDuration.EffectiveBaseSeconds}s configuredExtraPerService={scheduleDuration.ConfiguredExtraPerServiceSeconds}s effectiveExtraPerService={scheduleDuration.EffectiveExtraPerServiceSeconds}s reason={scheduleDuration.Reason} rule={scheduleDuration.Rule}");

        var protectedViewingTuners = tunerProfiles
            .Where(t => string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var tuner in protectedViewingTuners)
        {
            var deleted = reservationStore.DeleteScheduledEpgEntriesForTuner(tuner.Name);
            if (deleted > 0)
                log.Add("TUNER_PROTECT", "Viewing", $"tuner={tuner.Name} role=Viewing skipped_from=DailyEpg deletedScheduledEpg={deleted}");
        }

        var recordableTuners = tunerProfiles
            .Where(t => !string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var explicitTuners = recordableTuners
            .Where(p => !string.Equals(p.Group, "HYBRID", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Group.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        var hybridTuners = recordableTuners
            .Where(p => string.Equals(p.Group, "HYBRID", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tunersByGroup = new Dictionary<string, List<Core.TunerProfile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in explicitTuners)
            tunersByGroup[kv.Key] = kv.Value;

        if (hybridTuners.Count > 0)
        {
            // 地/BS/CS は定時EPGでは BS/CS 側へ寄せる。
            // 1本の物理チューナーに同時に2系統の定時EPGエントリを作らないため。
            if (!tunersByGroup.TryGetValue("BSCS", out var list))
            {
                list = new List<Core.TunerProfile>();
                tunersByGroup["BSCS"] = list;
            }
            list.AddRange(hybridTuners);
        }

        // ─── v32.82: TS単位の所要時間集計に変更 ───
        // 旧式は「局数 × waitSec / チューナー数」だったが、
        // 実際の取得は TS単位なので、各TSのサービス数を考慮した所要秒の合計をベースに計算。
        // groupKey("GR" or "BSCS") → そのグループの全TSの所要秒合計
        var totalSecByGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var channelCountByGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var channelLoad = channelLoader.Load();
        foreach (var warning in channelLoad.Warnings)
            log.Add("EPG_CHANNEL_CONFIG", "Settings", warning);

        // v0.11.678: ch2/ChSet明示設定が成立しない場合に、チューナー本数を局数へ読み替える旧式fallbackを禁止する。
        // 設定画面の入力値だけを正本にし、チャンネル正本が0件なら定時EPGエントリも作らない。
        if (channelLoad.Targets.Count == 0)
        {
            log.Add("EPG_SCHEDULER", "EpgEntry",
                "result=SKIPPED reason=channel_settings_incomplete targets=0 action=do_not_create_daily_epg_entries source=explicit_settings rule=v0.11.678_explicit_channel_contract");
            return;
        }

        // (group, nid, tsId) でグループ化してサービス数を求める。
        // TSIDだけで束ねると、将来のネットワーク違い衝突を見落とすためNIDもキーに含める。
        var tsGroups = channelLoad.Targets
            .GroupBy(t => new
            {
                Group = NormalizeEpgScheduleGroup(t.Group),
                Nid   = t.OriginalNetworkId,
                Ts    = t.TransportStreamId
            })
            .Select(g => new { g.Key.Group, g.Key.Nid, g.Key.Ts, ServiceCount = g.Count() })
            .ToList();

        foreach (var tg in tsGroups)
        {
            var perTsPlan = EpgDurationPolicy.CreateSchedulePlan(
                _currentDepth,
                _multiServiceExtraSeconds,
                tg.ServiceCount);
            var perTs = perTsPlan.RecDurationSeconds;
            totalSecByGroup[tg.Group] = totalSecByGroup.GetValueOrDefault(tg.Group) + perTs;
        }
        channelCountByGroup = channelLoad.Targets
            .GroupBy(t => NormalizeEpgScheduleGroup(t.Group))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var (group, tuners) in tunersByGroup)
        {
            var scheduleGroup = NormalizeEpgScheduleGroup(group);
            if (!channelCountByGroup.TryGetValue(scheduleGroup, out var channelCount) || channelCount <= 0 ||
                !totalSecByGroup.TryGetValue(scheduleGroup, out var totalSec) || totalSec <= 0)
            {
                log.Add("EPG_SCHEDULER", "EpgEntry",
                    $"result=SKIPPED group={group} reason=no_explicit_channel_targets action=do_not_create_daily_epg_entry source=explicit_settings rule=v0.11.678_explicit_channel_contract");
                continue;
            }

            // チューナー本数で並列処理されるので、所要時間は totalSec / 並列数（小数切り上げ）
            var durationSec  = (int)Math.Ceiling((double)totalSec / tuners.Count);
            var end          = CeilTo30Min(start.AddSeconds(durationSec));

            foreach (var tuner in tuners)
            {
                reservationStore.UpsertEpgScheduleEntry(
                    group, start, end,
                    $"EPG取得（{tuner.Name}）",
                    tuner.Name);
            }

            log.Add("EPG_SCHEDULER", "EpgEntry",
                $"定時EPGエントリ: group={group} 局数={channelCount} " +
                $"チューナー={tuners.Count}本 {start:MM/dd HH:mm}〜{end:HH:mm} source=explicit_settings rule=v0.11.678_explicit_channel_contract");
        }

        try
        {
            // v0.8.09:
            // 定時EPGエントリは予約DBへ直接Upsertするだけで終わらせず、必ず共通割り当てルートへ戻す。
            // v0.8.08 では TaskSchedulerService 側だけを直したため、EpgScheduler 起点の EpgEntry 作成時に
            // RefreshWakeTask=false のまま通過し、07:00用Wake生成が他イベント依存になる余地が残っていた。
            // EpgEntry の作成/更新は ALLOC_ROUTE/TUNER_ALLOC → Wake再構築までを正式な一連処理にする。
            ReevaluateAndLog("EpgEntry", refreshWakeTask: true);
        }
        catch (Exception ex) { log.Add("EPG_SCHEDULER", "EpgEntry", $"EPGエントリ競合評価/Wake再構築エラー: {ex.Message}"); }
    }


    private static string NormalizeEpgScheduleGroup(string? group)
    {
        var g = (group ?? string.Empty).Trim().ToUpperInvariant();
        return g switch
        {
            "BS" or "CS" or "BSCS" or "BS/CS" => "BSCS",
            "GR" => "GR",
            var raw => raw
        };
    }

    private static string SafeValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("\r", " ").Replace("\n", " ").Trim();

    private static DateTime CalcNextRun(DateTime now, EpgScheduleConfig cfg)
    {
        var today = now.Date.AddHours(cfg.Hour).AddMinutes(cfg.Minute);
        return today > now ? today : today.AddDays(1);
    }

    private int ApplyTimeFollowingWithAudit(string requestedBy, string targetScope, bool reevaluateOnUpdated = true)
    {
        // v0.11.378: conflicted scheduled reservations are intentionally included.
        // EventIdentity reservations must follow EIT updates before the next allocation pass;
        // otherwise a conflict can remain pinned to an obsolete time range.
        var scheduled = reservationStore.GetByStatus(ReservationStatus.Scheduled)
            .Where(r => r.Source != ReservationSource.Epg)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        var results = reservationStore.ApplyTimeFollowingDetailed(scheduled, epgStore);
        var updated = results.Where(r => r.Updated).ToList();
        var missing = results.Count(r => r.Reason == "EPG_EVENT_NOT_FOUND" || r.Reason == "NO_SERVICE_OR_EVENT_ID");
        var protectedSkip = results.Count(r => r.Reason.StartsWith("SKIP_", StringComparison.OrdinalIgnoreCase));
        var unchanged = results.Count(r => r.Reason == "UNCHANGED_WITHIN_THRESHOLD");

        log.Add("EPG_SCHEDULER", "TimeFollowSummary",
            $"source={requestedBy} targetScope={targetScope} checked={results.Count} updated={updated.Count} unchanged={unchanged} missing={missing} protectedSkip={protectedSkip} rule=v0.11.380_epg_update_route_order_batch");

        foreach (var r in results.Where(x => x.Updated || x.Reason == "EPG_EVENT_NOT_FOUND" || x.Reason == "EPG_EVENT_INVALID_RANGE" || x.Reason == "NO_SERVICE_OR_EVENT_ID"))
        {
            var oldRange = $"{r.OldStart:MM/dd HH:mm:ss}〜{r.OldEnd:MM/dd HH:mm:ss}";
            var newRange = r.NewStart.HasValue && r.NewEnd.HasValue
                ? $"{r.NewStart.Value:MM/dd HH:mm:ss}〜{r.NewEnd.Value:MM/dd HH:mm:ss}"
                : "-";
            log.Add("EPG_SCHEDULER", r.Updated ? "TimeFollowUpdated" : "TimeFollowAudit",
                $"service={r.ServiceName} title={r.Title} id=R{r.ReservationId} result={r.Reason} old={oldRange} new={newRange} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} eid={r.EventId} rule=v0.11.380_epg_update_route_order_batch");
        }

        if (updated.Count > 0)
        {
            var ids = string.Join(",", updated.Select(r => $"R{r.ReservationId}"));
            log.Add("EPG_SCHEDULER", "TimeFollow",
                $"時間追従更新: {updated.Count}件 [{ids}] route=ALLOC_ROUTE wakeRefresh=True preRecRefresh=after_time_follow rule=v0.11.380_epg_update_route_order_batch");
            if (reevaluateOnUpdated)
            {
                ReevaluateAndLog("TimeFollow", refreshWakeTask: true);
            }
        }

        return updated.Count;
    }

    private void ReevaluateAndLog(string context, bool refreshWakeTask = false)
    {
        allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "EpgScheduler",
            Action: $"Reevaluate:{context}",
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: false,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: false,
            RefreshWakeTask: refreshWakeTask,
            EmitConflictLogs: true,
            ConflictLogCategory: "EPG_SCHEDULER",
            ConflictLogTitle: $"Conflict({context})"));
    }
}

/// <summary>EPGスケジュール設定（動的更新用）</summary>
internal sealed record EpgScheduleConfig(bool Enabled, int Hour, int Minute);
