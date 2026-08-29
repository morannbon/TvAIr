using TvAIr.Epg.Projection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Schedule;
using TvAIr.Tuner;
using TvAIr.Plugin;
using TvAIrPlugin;

namespace TvAIr.Epg;

public sealed record EpgRunState(
    bool IsRunning,
    bool IsDailyRunning,
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
    private readonly IProgramEventSource   programEvents;
    private readonly TunerPool           tunerPool;
    private readonly ApplicationOperationGate applicationGate;
    private readonly LogRepository       log;
    private readonly UserEventLogService userEvents;
    private readonly PluginTypedEventHub typedEvents;
    private readonly ReservationAllocationRouteService allocationRoute;
    private readonly Database database;
    private readonly SystemSleepInhibitionService sleepInhibition;
    private readonly SystemEpgResponsibilityPlanService systemEpgResponsibilityPlan;
    private readonly NormalEpgWaveOccupation normalEpgWaveOccupation;
    private readonly PowerResumeSignalHub powerResumeSignals;
    private readonly DailyEpgRunStateStore dailyRunStateStore;
    private readonly object dailyRunStateGate = new();
    private DailyEpgRunState? currentDailyRunState;

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
    private Task? activeRunTask;
    private long nextRunGeneration;
    private long currentRunGeneration;
    private bool isRunning;
    private volatile bool isStopping;
    private string pendingRunScope = "All";
    private string? pendingRunSource;
    private bool pendingRunSilent;
    private EpgStartBlockInfo? lastStartBlock;

    private sealed record EpgDurationEstimate(
        int RequiredSeconds,
        int CaptureCriticalPathSeconds,
        int SafetyMarginSeconds,
        int TunerCount,
        int TsCount,
        IReadOnlyList<int> LaneLoadsSeconds,
        string Source);

    /// <summary>
    /// Daily ownerのプロセス内executor管理。Task/CTSを停止・待機するためだけの単一実行資源であり、
    /// Daily lifecycleやRunning判定の正本ではない。正式なRunningはDailyEpgRunStateの永続commitで確定する。
    /// </summary>
    private sealed class ActiveDailyExecutor
    {
        public required long Generation { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Task { get; set; }
    }

    private ActiveDailyExecutor? activeDailyExecutor;
    private bool dailyCancelRequested;

    // Scheduler.Dailyの実行Task/CTSは単一executorだけが保持する。
    // lifecycleのRunning正本はDailyEpgRunStateであり、この参照の有無から状態を逆算しない。
    private readonly object gate = new();

    // Daily schedulerの状態変更通知。maxCount=1のbinary signalとして保持し、
    // schedulerが待機へ入る直前/直後のReservation mutationでも通知を失わない。
    // 複数変更は1回の再評価へcoalesceし、別generationや副正本は持たない。
    private readonly SemaphoreSlim schedulerWakeSignal = new(0, 1);

    // Suspend/Resumeの物理owner整合はPower/Tuner層が所有する。Daily側は、Suspend時に
    // 正式Runningだった事実だけを一時保持し、Resume reconciliation完了後に通常の
    // Daily Cancel経路へ収束させる。Suspend専用terminalや永続markerは作らない。
    private readonly object dailyPowerSignalGate = new();
    private string? suspendedDailyCycleId;
    private DateTime? suspendedDailyDate;
    private int externalSchedulerSignalsSubscribed;

    // 定時EPGは設定時刻を正本とする可動枠。録画予約は動かさず、Daily All全体が
    // 完走できる単一の連続占有枠を最大3時間の範囲で後方へ移動する。枠は分割せず、
    // 放送波別の部分実行・部分成功は持たない。実行開始後は明示キャンセルまたは実エラー以外で中断しない。
    private static readonly TimeSpan ScheduledEpgMovementLimit = TimeSpan.FromHours(3);
    private static readonly TimeSpan EpgStartEstimateSafetyMargin = TimeSpan.FromMinutes(2);

    private bool AnyNormalEpgRunActiveLocked()
        => isRunning || activeDailyExecutor is not null;

    private bool IsDailyExecutorRunningLocked()
        => activeDailyExecutor is not null;

    /// <summary>
    /// Wake -> Scheduler.Daily power handoff uses this only to detect that the scheduled Daily All run has
    /// already acquired its own SystemRequired lease.  It does not expose or mutate run state.
    /// </summary>
    public bool HasActiveScheduledDailyPowerOwner()
    {
        lock (gate)
            return activeDailyExecutor is not null;
    }



    public EpgScheduler(
        EpgSettings                    settings,
        IniSettingsService             ini,
        EpgCapture                     capture,
        ReservationStore               reservationStore,
        IReadOnlyList<TunerProfile>    tunerProfiles,
        ChannelFileLoader              channelLoader,
        IProgramEventSource             programEvents,
        TunerPool                      tunerPool,
        LogRepository                  log,
        UserEventLogService            userEvents,
        PluginTypedEventHub             typedEvents,
        ReservationAllocationRouteService allocationRoute,
        Database                         database,
        ApplicationOperationGate          applicationGate,
        SystemSleepInhibitionService       sleepInhibition,
        SystemEpgResponsibilityPlanService systemEpgResponsibilityPlan,
        NormalEpgWaveOccupation             normalEpgWaveOccupation,
        PowerResumeSignalHub                 powerResumeSignals)
    {
        this.capture                  = capture;
        this.reservationStore         = reservationStore;
        this.tunerProfiles            = tunerProfiles;
        this.ini                      = ini;
        this.channelLoader            = channelLoader;
        this.programEvents            = programEvents;
        this.tunerPool                = tunerPool;
        this.log                      = log;
        this.userEvents               = userEvents;
        this.typedEvents              = typedEvents;
        this.allocationRoute          = allocationRoute;
        this.database                 = database;
        this.applicationGate          = applicationGate;
        this.sleepInhibition          = sleepInhibition;
        this.systemEpgResponsibilityPlan = systemEpgResponsibilityPlan;
        this.normalEpgWaveOccupation = normalEpgWaveOccupation;
        this.powerResumeSignals = powerResumeSignals;
        dailyRunStateStore = new DailyEpgRunStateStore(database.DataDirectory);
        _multiServiceExtraSeconds     = Math.Max(0, settings.MultiServiceExtraSeconds);

        // 定時EPGの有効状態・時刻・深度は、設定画面/API/INI保存が共有する
        // IniSettingsService を起動時正本とする。appsettings の EpgSettings は
        // worker実行時間などの非ユーザー設定だけに限定し、定時契約へ逆投影しない。
        _currentDepth = SettingsDefaults.NormalizeEpgDepth(ini.EpgDepth);
        capture.UpdateRuntimeDepth(_currentDepth);
        config = new EpgScheduleConfig(
            ini.EpgEnabled,
            SettingsDefaults.NormalizeEpgHour(ini.EpgHour),
            SettingsDefaults.NormalizeEpgMinute(ini.EpgMinute));
    }

    /// <summary>
    /// Planner結果をDaily stateへ写す唯一の境界。Scheduled Dailyは1日1本のAll計画だけを持つ。
    /// Running以降は計画・設定snapshotを変更せず、意味的に同一のplanなら永続化しない。
    /// </summary>
    private bool TryCommitDailyPlannerResult(
        DateTime targetDate,
        DailyEpgConfigSnapshot configSnapshot,
        DailyEpgPlanResult plan,
        DateTime updatedAt)
    {
        lock (dailyRunStateGate)
        {
            var date = targetDate.Date;
            if (currentDailyRunState is null || currentDailyRunState.Date.Date != date)
            {
                var next = new DailyEpgRunState(
                    date,
                    DailyEpgRunPhase.Planned,
                    configSnapshot,
                    plan.CanonicalStart,
                    plan.PlannedStart,
                    plan.PlannedEnd,
                    plan.MovementDeadline,
                    plan.RequiredSeconds,
                    plan.Placement,
                    updatedAt);
                dailyRunStateStore.Save(next);
                currentDailyRunState = next;
                return true;
            }

            var current = currentDailyRunState;
            if (current.State is DailyEpgRunPhase.Running or DailyEpgRunPhase.Finalizing
                or DailyEpgRunPhase.Succeeded or DailyEpgRunPhase.Failed
                or DailyEpgRunPhase.Expired or DailyEpgRunPhase.Cancelled)
                return false;

            var planChanged = !DailyEpgPlanSemantics.SamePlan(current, plan);
            var configChanged = current.Config != configSnapshot;
            if (!planChanged && !configChanged) return false;

            var nextState = current with
            {
                Config = configSnapshot,
                CanonicalStart = plan.CanonicalStart,
                PlannedStart = plan.PlannedStart,
                PlannedEnd = plan.PlannedEnd,
                MovementDeadline = plan.MovementDeadline,
                RequiredSeconds = plan.RequiredSeconds,
                Placement = plan.Placement,
                UpdatedAt = updatedAt
            };
            dailyRunStateStore.Save(nextState);
            currentDailyRunState = nextState;
            return true;
        }
    }

    /// <summary>Daily Allの正式開始commit。保存成功後は未開始へ巻き戻さない。</summary>
    private bool TryCommitDailyRunning(
        DailyEpgPlanResult expectedPlan,
        DateTime updatedAt,
        out DailyEpgRunState committedState,
        out string rejectReason)
    {
        lock (dailyRunStateGate)
        {
            var current = currentDailyRunState;
            if (current is null)
            {
                committedState = null!;
                rejectReason = "daily_state_missing";
                return false;
            }
            if (current.State != DailyEpgRunPhase.Planned)
            {
                committedState = current;
                rejectReason = $"daily_state_{current.State}";
                return false;
            }
            if (current.Placement != DailyEpgPlacementState.Placed
                || !current.PlannedStart.HasValue || !current.PlannedEnd.HasValue)
            {
                committedState = current;
                rejectReason = "daily_not_placed";
                return false;
            }
            if (!DailyEpgPlanSemantics.SamePlan(current, expectedPlan))
            {
                committedState = current;
                rejectReason = "plan_changed_before_start";
                return false;
            }

            var next = current with { State = DailyEpgRunPhase.Running, UpdatedAt = updatedAt };
            dailyRunStateStore.Save(next);
            currentDailyRunState = next;
            committedState = next;
            rejectReason = string.Empty;
            return true;
        }
    }

    /// <summary>physical convergence後にだけDaily Allの成功をFinalizingへ反映する。</summary>
    private void CommitDailyCompleted(DateTime updatedAt)
    {
        lock (dailyRunStateGate)
        {
            var current = currentDailyRunState;
            if (current is null || current.State != DailyEpgRunPhase.Running) return;
            var next = current with { State = DailyEpgRunPhase.Finalizing, UpdatedAt = updatedAt };
            dailyRunStateStore.Save(next);
            currentDailyRunState = next;
        }
    }

    /// <summary>physical workerとlogical occupationの収束後にだけDaily AllをFailedへ閉じる。</summary>
    private void CommitDailyFailed(DateTime updatedAt)
    {
        lock (dailyRunStateGate)
        {
            var current = currentDailyRunState;
            if (current is null || current.State is DailyEpgRunPhase.Finalizing
                or DailyEpgRunPhase.Succeeded or DailyEpgRunPhase.Failed
                or DailyEpgRunPhase.Expired or DailyEpgRunPhase.Cancelled)
                return;
            var next = current with { State = DailyEpgRunPhase.Failed, UpdatedAt = updatedAt };
            dailyRunStateStore.Save(next);
            currentDailyRunState = next;
        }
    }

    /// <summary>最大3時間内にAllが完走できる連続枠が無い場合だけExpiredへ進める。</summary>
    private void CommitDailyExpired(DateTime updatedAt)
    {
        lock (dailyRunStateGate)
        {
            var current = currentDailyRunState;
            if (current is null || current.State != DailyEpgRunPhase.Planned
                || current.Placement != DailyEpgPlacementState.Unplaced
                || updatedAt < current.MovementDeadline)
                return;
            var next = current with { State = DailyEpgRunPhase.Expired, UpdatedAt = updatedAt };
            dailyRunStateStore.Save(next);
            currentDailyRunState = next;
        }
    }

    private DailyEpgRunState? GetDailyRunStateSnapshot()
    {
        lock (dailyRunStateGate) return currentDailyRunState;
    }

    /// <summary>
    /// SYSTEM_EPG Wakeへ渡す実予定時刻を返す。Wake側はINIのcanonicalやReservation行から
    /// Daily計画を推測せず、Daily ownerが確定した計画だけを投影する。
    /// 当日runがterminalの場合は、EnsureTomorrowと同じPlanner入力で翌日責務を再評価する。
    /// </summary>
    public IReadOnlyList<DateTime> GetScheduledDailyWakeStarts(DateTime now)
    {
        EpgScheduleConfig cfg;
        lock (configGate) cfg = config;
        if (!cfg.Enabled) return Array.Empty<DateTime>();

        var state = GetDailyRunStateSnapshot();
        if (state is not null)
        {
            if (state.Date.Date == now.Date && state.State == DailyEpgRunPhase.Planned
                && state.Placement == DailyEpgPlacementState.Placed
                && state.PlannedStart.HasValue && state.PlannedStart.Value > now)
                return new[] { state.PlannedStart.Value };

            if (state.Date.Date >= now.Date && !IsDailyRunTerminal(state.State))
                return Array.Empty<DateTime>();

            var nextDate = state.Date.Date >= now.Date ? state.Date.Date.AddDays(1) : now.Date;
            var snapshot = new DailyEpgConfigSnapshot(
                cfg.Enabled, cfg.Hour, cfg.Minute, SettingsDefaults.NormalizeEpgDepth(_currentDepth));
            if (!snapshot.Enabled) return Array.Empty<DateTime>();
            var plan = BuildDailyEpgPlanResult(nextDate, snapshot, snapshot.CanonicalStartFor(nextDate));
            return plan.Placement == DailyEpgPlacementState.Placed
                   && plan.PlannedStart.HasValue && plan.PlannedStart.Value > now
                ? new[] { plan.PlannedStart.Value }
                : Array.Empty<DateTime>();
        }

        var currentSnapshot = new DailyEpgConfigSnapshot(
            cfg.Enabled, cfg.Hour, cfg.Minute, SettingsDefaults.NormalizeEpgDepth(_currentDepth));
        var targetDate = now.Date;
        var canonical = currentSnapshot.CanonicalStartFor(targetDate);
        if (canonical.Add(ScheduledEpgMovementLimit) <= now) targetDate = targetDate.AddDays(1);
        var plannerNow = targetDate == now.Date ? now : currentSnapshot.CanonicalStartFor(targetDate);
        var targetPlan = BuildDailyEpgPlanResult(targetDate, currentSnapshot, plannerNow);
        return targetPlan.Placement == DailyEpgPlacementState.Placed
               && targetPlan.PlannedStart.HasValue && targetPlan.PlannedStart.Value > now
            ? new[] { targetPlan.PlannedStart.Value }
            : Array.Empty<DateTime>();
    }

    private static bool IsDailyRunTerminal(DailyEpgRunPhase phase)
        => phase is DailyEpgRunPhase.Succeeded
            or DailyEpgRunPhase.Failed
            or DailyEpgRunPhase.Expired
            or DailyEpgRunPhase.Cancelled;

    /// <summary>
    /// 起動時はProjection/Wakeより先に永続Daily正本を読む。永続Runningは正式開始済みなので
    /// Pendingへ巻き戻さず、中断済みとしてpartial progressを破棄してCancelledへ収束させる。
    /// Finalizingは通知を再送せず、同じEnsureTomorrowだけを再実行する。
    /// </summary>
    private void RestoreDailyRunStateAtStartup(DateTime now)
    {
        DailyEpgRunState? loaded;
        try
        {
            loaded = dailyRunStateStore.Load();
        }
        catch (Exception ex)
        {
            log.Add("EPG_DAILY_STATE", "EPG",
                $"result=LOAD_FAILED error={ex.GetType().Name}:{ex.Message} action=do_not_guess_from_projection rule=daily_epg_single_state_contract");
            lock (dailyRunStateGate) currentDailyRunState = null;
            return;
        }

        lock (dailyRunStateGate) currentDailyRunState = loaded;
        if (loaded is null) return;

        if (loaded.State == DailyEpgRunPhase.Running)
        {
            // 起動直後にlive Daily ownerは存在しない。Runningを未開始へ戻すと同日二重実行になる。
            CommitDailyCancelled(now);
            log.Add("EPG_DAILY_STATE", "EPG",
                $"result=INTERRUPTED_RUNNING_CANCELLED targetDate={loaded.Date:yyyy-MM-dd} action=discard_partial_no_same_day_resume rule=daily_epg_single_state_contract");
            return;
        }

        if (loaded.State == DailyEpgRunPhase.Finalizing)
        {
            // startup復旧では過去のEpgCompleted/User Eventを再送しない。状態・翌日責務だけを収束させる。
            TryFinalizeDailySucceeded(loaded.UpdatedAt);
        }
    }

    /// <summary>
    /// 明示Daily Cancelの対象executorを固定する。Cancel完了判定はこのsnapshotだけで行い、
    /// capture途中からDaily terminalを先行commitしない。
    /// </summary>
    private void BeginDailyCancelRequest()
    {
        lock (gate) dailyCancelRequested = true;
    }

    private bool MarkDailyCancelConverged()
    {
        lock (gate)
        {
            if (!dailyCancelRequested) return false;
            dailyCancelRequested = false;
            return true;
        }
    }

    /// <summary>
    /// DailyのCancelは当日run全体の取消である。部分取得進捗は同日再開材料として保持しない。
    /// </summary>
    private void CommitDailyCancelled(DateTime updatedAt)
    {
        lock (dailyRunStateGate)
        {
            var current = currentDailyRunState;
            if (current is null || current.State is DailyEpgRunPhase.Succeeded
                or DailyEpgRunPhase.Failed or DailyEpgRunPhase.Expired or DailyEpgRunPhase.Cancelled)
                return;
            var next = current with
            {
                State = DailyEpgRunPhase.Cancelled,
                PlannedStart = null,
                PlannedEnd = null,
                Placement = DailyEpgPlacementState.Unplaced,
                UpdatedAt = updatedAt
            };
            dailyRunStateStore.Save(next);
            currentDailyRunState = next;
        }
    }

    /// <summary>
    /// Failed/Expired/Cancelled は当日再実行しないが、翌日は同じDaily契約から新規開始する。
    /// terminal stateを今日の正本として保持したまま、翌日SystemDailyEpg/Wakeだけを一方向投影する。
    /// </summary>
    private void EnsureTomorrowAfterDailyTerminal(DailyEpgRunState? terminalState, string source)
    {
        if (terminalState is null || !IsDailyRunTerminal(terminalState.State) || !terminalState.Config.Enabled)
            return;
        try
        {
            EnsureDailyResponsibility(
                terminalState.Date.Date.AddDays(1),
                terminalState.Config,
                refreshAllocationRoute: false);
            RefreshDailyWakeProjection(source);
            log.Add("EPG_DAILY_STATE", "EPG",
                $"result=TOMORROW_ENSURED terminal={terminalState.State} targetDate={terminalState.Date:yyyy-MM-dd} tomorrow={terminalState.Date.AddDays(1):yyyy-MM-dd} source={source} action=keep_today_terminal_project_fresh_tomorrow rule=daily_epg_single_state_contract");
        }
        catch (Exception ex)
        {
            log.Add("EPG_DAILY_STATE", "WARN",
                $"result=TOMORROW_ENSURE_FAILED terminal={terminalState.State} targetDate={terminalState.Date:yyyy-MM-dd} source={source} error={ex.GetType().Name}:{ex.Message} action=keep_today_terminal_retry_by_startup_or_wake_route rule=daily_epg_single_state_contract");
        }
    }

    /// <summary>
    /// FinalizingからSucceededへ進める唯一の境界。翌日責務のEnsureが成功する前にSucceededを公開しない。
    /// 専用repair stateは作らず、失敗時はFinalizingをそのまま残して同じEnsureを再実行可能にする。
    /// </summary>
    private bool TryFinalizeDailySucceeded(DateTime completedAt)
    {
        DailyEpgRunState snapshot;
        lock (dailyRunStateGate)
        {
            if (currentDailyRunState is not { State: DailyEpgRunPhase.Finalizing } current)
                return false;
            snapshot = current;
        }

        try
        {
            // 翌日責務はDaily lifecycleの成功条件。作成済みでも同じEnsure経路を通し、
            // Projection側の既存upsert差分判定へ任せる。0件=失敗とは解釈しない。
            if (snapshot.Config.Enabled)
            {
                EnsureDailyResponsibility(
                    snapshot.Date.Date.AddDays(1),
                    snapshot.Config,
                    refreshAllocationRoute: false);
            }

            lock (dailyRunStateGate)
            {
                var current = currentDailyRunState;
                if (current is null
                    || current.Date.Date != snapshot.Date.Date
                    || current.State != DailyEpgRunPhase.Finalizing)
                    return false;

                var next = current with
                {
                    State = DailyEpgRunPhase.Succeeded,
                    UpdatedAt = DateTime.Now
                };
                dailyRunStateStore.Save(next);
                currentDailyRunState = next;
            }

            try
            {
                RefreshDailyWakeProjection("DailySucceededWakeProjection");
            }
            catch (Exception ex)
            {
                // Succeededの正本commitは翌日責務Ensureまでで完了している。Wakeは派生投影なので、
                // 失敗してもDaily正本を巻き戻さず、次の共通Wake更新で同じPlannedStartへ収束させる。
                log.Add("EPG_DAILY_STATE", "WARN",
                    $"result=WAKE_PROJECTION_FAILED targetDate={snapshot.Date:yyyy-MM-dd} error={ex.GetType().Name}:{ex.Message} action=keep_succeeded_retry_by_next_wake_route rule=daily_epg_single_state_contract");
            }

            log.Add("EPG_DAILY_STATE", "EPG",
                $"result=SUCCEEDED targetDate={snapshot.Date:yyyy-MM-dd} completedAt={completedAt:yyyy-MM-dd HH:mm:ss} tomorrow={snapshot.Date.AddDays(1):yyyy-MM-dd} action=tomorrow_responsibility_ensured rule=daily_epg_single_state_contract");
            return true;
        }
        catch (Exception ex)
        {
            log.Add("EPG_DAILY_STATE", "EPG",
                $"result=FINALIZING_PENDING targetDate={snapshot.Date:yyyy-MM-dd} completedAt={completedAt:yyyy-MM-dd HH:mm:ss} error={ex.GetType().Name}:{ex.Message} action=retry_same_finalize_without_repair_state rule=daily_epg_single_state_contract");
            return false;
        }
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

    /// <summary>
    /// 予約timelineの確定変更をDaily Plannerへ通知する。
    /// Wake完了を待たず、同じscheduler再評価入口へ集約する。
    /// </summary>
    public void NotifyReservationTimelineChanged(long mutationVersion, string source, string action)
    {
        log.Add("EPG_SCHEDULED_PLAN_INVALIDATE", "EPG",
            $"result=RECEIVED mutationVersion={mutationVersion} source={source} action={action} behavior=wake_and_rebuild_from_current_timeline rule=scheduled_epg_movable_slot_contract");

        // DEVELOPER_APPROVAL_REQUIRED: System EPG Projectionは保護領域。
        // 2026-08-24 APPROVED_SCOPE: 当日Dailyがterminal済みで翌日責務だけが予約一覧へ投影されている場合、
        // 翌日予約timelineの確定変更を受けた時点で、同じDaily All Plannerから翌日Projectionだけを再生成する。
        // currentDailyRunStateを翌日へ進めたり、SystemEpgResponsibilityPlanService/PreRec責務を変更したりしない。
        // Allocation/Wakeの所有権も奪わず、呼出元のReservation mutation routeへ残す。
        RefreshProjectedFutureDailyAfterTimelineMutation(mutationVersion, source, action);
        SignalSchedulerWake();
    }

    private void RefreshProjectedFutureDailyAfterTimelineMutation(long mutationVersion, string source, string action)
    {
        try
        {
            EpgScheduleConfig cfg;
            lock (configGate) cfg = config;
            if (!cfg.Enabled) return;

            var now = DateTime.Now;
            var state = GetDailyRunStateSnapshot();
            if (state is null
                || state.Date.Date != now.Date
                || !IsDailyRunTerminal(state.State))
                return;

            var targetDate = now.Date.AddDays(1);
            var snapshot = new DailyEpgConfigSnapshot(
                cfg.Enabled, cfg.Hour, cfg.Minute, SettingsDefaults.NormalizeEpgDepth(_currentDepth));

            // Projectionの更新だけを行う。SystemDailyEpg行は通常録画の割当対象ではなく、
            // WakeはTaskScheduler/ReservationAllocationRouteの既存single ownerが現在timelineから再構築する。
            // ここでReevaluateAndLogを再入させるとReservation mutation中のallocation routeと二重所有になるため禁止。
            var changed = EnsureDailyResponsibility(targetDate, snapshot, refreshAllocationRoute: false);
            log.Add("EPG_SCHEDULED_FUTURE_PROJECTION_REFRESH", "All",
                $"result={(changed > 0 ? "UPDATED" : "UNCHANGED")} mutationVersion={mutationVersion} source={source} action={action} targetDate={targetDate:yyyy-MM-dd} currentState={state.State} projectionOnly=True allocationRefresh=False rule=scheduled_epg_future_projection_current_timeline_contract");
        }
        catch (Exception ex)
        {
            // 予約mutation本体をProjection補助の失敗で巻き戻さない。scheduler binary wakeは後続で必ず発行し、
            // 日付境界/次回起動の既存EnsureDailyResponsibilityでも再収束できる。
            log.Add("EPG_SCHEDULED_FUTURE_PROJECTION_REFRESH", "All",
                $"result=FAILED mutationVersion={mutationVersion} source={source} action={action} error={ex.GetType().Name}:{ex.Message} actionOnFailure=keep_mutation_and_wake_scheduler rule=scheduled_epg_future_projection_current_timeline_contract");
        }
    }

    private void SignalSchedulerWake()
    {
        if (isStopping) return;
        try
        {
            // maxCount=1なので、既に未消費signalがある場合はその1件へcoalesceする。
            // CTS差替え方式と違い、schedulerがまだWaitを張っていない瞬間の通知も保持される。
            schedulerWakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 未消費signalが既に1件ある。次のscheduler再評価で現在timeline全体を読み直すため追加countは不要。
        }
    }

    /// <summary>Wakeタスク合流シグナルを受けたとき、定時判定ループを即時再評価させる。</summary>
    public void NotifyWakeSignal(string kind, string at, string source)
    {
        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? "UNKNOWN" : kind.Trim();
        var normalizedAt = string.IsNullOrWhiteSpace(at) ? "-" : at.Trim();
        log.Add("WAKE_SIGNAL", "EPG_SCHEDULER",
            $"result=RECEIVED kind={normalizedKind} at={normalizedAt} source={source} action=wake_scheduler_loop rule=release_contract");
        try
        {
            SignalSchedulerWake();
        }
        catch (Exception ex)
        {
            log.Add("WAKE_SIGNAL", "EPG_SCHEDULER",
                $"result=FAILED kind={normalizedKind} at={normalizedAt} source={source} error={ex.GetType().Name}:{ex.Message} action=scheduler_loop_not_signaled rule=release_contract");
        }
    }

    public EpgRunState GetRunState()
    {
        // DailyのRunning判定はactive executor表ではなく永続DailyRunStateを正本にする。
        // executor登録前後の短い境界でも「開始済み」を未実行として公開しない。
        DailyEpgRunState? dailyState;
        lock (dailyRunStateGate)
            dailyState = currentDailyRunState;
        var dailyRunning = dailyState is { State: DailyEpgRunPhase.Running };

        lock (gate)
        {
            if (isRunning)
            {
                return new EpgRunState(
                    IsRunning: true,
                    IsDailyRunning: false,
                    CanStart: false,
                    CanCancel: true,
                    Source: pendingRunSource ?? "",
                    TargetScope: pendingRunScope,
                    Silent: pendingRunSilent,
                    UiMode: pendingRunSilent ? "Silent" : "Visible",
                    CancelRoute: pendingRunSilent ? "SilentTray" : "VisibleWidget");
            }

            if (dailyRunning)
            {
                return new EpgRunState(
                    IsRunning: true,
                    IsDailyRunning: true,
                    CanStart: false,
                    CanCancel: activeDailyExecutor is not null,
                    Source: "Scheduler.Daily",
                    TargetScope: "All",
                    Silent: true,
                    UiMode: "Silent",
                    CancelRoute: "SilentTray");
            }

            return new EpgRunState(false, false, true, false, "", "All", false, "Visible", "VisibleWidget");
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
        return "録画予約があるためEPG取得を開始できません";
    }

    // ─── 手動実行 ────────────────────────────────────────────────

    /// <summary>EPG取得を今すぐ実行する。既に実行中の場合は false を返す。</summary>
    public bool TriggerNow(string requestedBy = "ManualUi", bool silent = false, string targetScope = "All")
    {
        var normalizedSource = string.IsNullOrWhiteSpace(requestedBy) ? "ManualUi" : requestedBy.Trim();
        var normalizedScope = NormalizeTargetScope(targetScope);

        if (!applicationGate.TryAdmit("epg_start", normalizedSource, out var shutdownReason))
        {
            log.Add("EPG_RUN_GUARD", "EPG",
                $"result=REJECTED requestedSource={normalizedSource} requestedSilent={silent} requestedScope={normalizedScope} reason={shutdownReason} action=reject_start rule=shutdown_admission_contract");
            return false;
        }

        var isScheduledDaily = normalizedSource.Equals("Scheduler.Daily", StringComparison.OrdinalIgnoreCase);
        if (!isScheduledDaily)
        {
            // INTERACTIVE_EPG_ADMISSION_INVARIANT:
            // ユーザーが今すぐ開始する通常EPG / サイレントEPGは、開始後に録画予約を避けたり
            // workerを途中停止したりしない。その代わり、開始前に対象波の必要連続枠を一度だけ計算し、
            // 有効な録画占有（開始前/終了後マージン込み）が1件でも重なる場合はrun自体を開始しない。
            // 自動後方移動・分割取得・再試行は行わない。
            if (TryGetInteractiveEpgOccupancyBlock(normalizedScope, DateTime.Now, out var interactiveReason, out var interactiveUntil, out var interactiveGroup, out var interactiveOwner))
            {
                RememberStartBlock(normalizedScope, normalizedSource, silent, interactiveReason, interactiveUntil, interactiveGroup, interactiveOwner);
                var displayMessage = BuildStartBlockDisplayMessage(interactiveReason, interactiveGroup);
                log.Add("EPG_RUN_BLOCKED", "BLOCKED",
                    $"{displayMessage}。targetScope={normalizedScope} blockedGroup={interactiveGroup} blockedUntil={(interactiveUntil.HasValue ? interactiveUntil.Value.ToString("MM/dd HH:mm:ss") : "-")} blockedReason={interactiveReason} owner={interactiveOwner} silent={silent} source={normalizedSource} action=reject_interactive_epg_before_run rule=interactive_epg_occupancy_admission_contract");
                userEvents.AddScheduledEpgCompleted(normalizedScope, normalizedSource, silent, "BLOCKED", 0, 0, 0, 0, $"blockedReason={interactiveReason}; blockedGroup={interactiveGroup}; owner={interactiveOwner}; interactiveAdmission=True");
                return false;
            }
        }
        else
        {
            // Scheduler.Daily は可動枠Plannerだけが開始Admissionを所有する。
            // TriggerNow到達後に録画近接・LifecycleGateを再判定すると、Plannerが確保した連続枠を
            // 実行時の別判断で壊すため禁止する。予約変化は開始前の次回Planner再計算へ反映する。
        }
        CancellationTokenSource cts;
        TaskCompletionSource<bool> runStartGate;
        IDisposable? sleepLease;
        long runGeneration;
        lock (gate)
        {
            if (isStopping)
            {
                log.Add("EPG_RUN_GUARD", "EPG",
                    $"result=STOPPING requestedSource={normalizedSource} requestedSilent={silent} requestedScope={normalizedScope} action=reject_start rule=epg_scheduler_shutdown_contract");
                return false;
            }
            if (AnyNormalEpgRunActiveLocked())
            {
                var runningSource = isRunning ? (pendingRunSource ?? "-") : "Scheduler.Daily";
                var runningScope = isRunning ? pendingRunScope : "All";
                var runningSilent = isRunning ? pendingRunSilent : true;
                log.Add("EPG_RUN_GUARD", "EPG",
                    $"result=BUSY requestedSource={normalizedSource} requestedSilent={silent} requestedScope={normalizedScope} runningSource={runningSource} runningSilent={runningSilent} runningScope={runningScope} action=reject_start rule=release_contract");
                return false;
            }

            // EPG_POWER_LIFECYCLE_INVARIANT:
            // worker生存期間ではなく、EpgSchedulerが所有するrun全体をスリープ抑止の正本とする。
            // capture worker終了後のTS parse/import、DailyRunState commit、typed event、post-import allocationまで
            // RunWithGuardAsyncが終端するまで同一Power Requestを保持する。代表worker/proxy processは禁止。
            runGeneration = nextRunGeneration + 1;
            if (!sleepInhibition.TryAcquire(normalizedSource, $"EPG:{normalizedScope}", runGeneration, out sleepLease, out var powerError))
            {
                log.Add("EPG_RUN_GUARD", "EPG",
                    $"result=REJECTED requestedSource={normalizedSource} requestedSilent={silent} requestedScope={normalizedScope} reason=system_sleep_inhibition_unavailable detail={powerError} action=reject_unprotected_epg_run rule=epg_run_power_lifecycle_contract");
                return false;
            }

            runCts?.Dispose();
            runCts = new CancellationTokenSource();
            cts = runCts;
            pendingRunScope = normalizedScope;
            pendingRunSource = normalizedSource;
            pendingRunSilent = silent;
            runGeneration = ++nextRunGeneration;
            currentRunGeneration = runGeneration;
            isRunning = true;
            lastStartBlock = null;

            // 停止処理が isRunning=true を観測した時点で、必ず待機対象Taskも取得できるよう、
            // 実処理開始前のゲート付きTaskを同じlock内で登録する。
            runStartGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            activeRunTask = Task.Run(async () =>
            {
                await runStartGate.Task.ConfigureAwait(false);
                await RunWithGuardAsync(cts.Token, normalizedScope, normalizedSource, silent, runGeneration, sleepLease!).ConfigureAwait(false);
            });
        }

        try
        {
            // Phase を即座に running にセット。silent=true の場合、ブラウザを開いてもツール表示不可。
            capture.SetRunning(uiVisible: !silent, runSource: normalizedSource, targetScope: normalizedScope);
            log.Add("EPG_RUN_REQUEST", "EPG",
                $"result=START source={normalizedSource} silent={silent} uiMode={(silent ? "Silent" : "Visible")} targetScope={normalizedScope} route=EpgScheduler.TriggerNow samePipeline=True startGuard=reserved rule=release_contract");
            userEvents.AddScheduledEpgStarted(normalizedScope, normalizedSource, silent);
            typedEvents.Publish(new TvAirEventDto
            {
                EventType = TvAirEventType.EpgStarted,
                EntityId = $"epg:{normalizedSource}:{DateTimeOffset.Now:yyyyMMddHHmmssfff}",
                Details = new Dictionary<string, string>
                {
                    ["source"] = normalizedSource, ["targetScope"] = normalizedScope, ["silent"] = silent.ToString(), ["depth"] = _currentDepth
                }
            });
            return true;
        }
        finally
        {
            // 停止要求が同時に入ってもTaskを未開始のまま残さない。
            runStartGate.TrySetResult(true);
        }
    }

    private bool TriggerScheduledDailyNow(DailyEpgRunState expectedState)
    {
        const string scope = "All";
        if (!applicationGate.TryAdmit("epg_start", "Scheduler.Daily", out var shutdownReason))
        {
            log.Add("EPG_RUN_GUARD", "EPG",
                $"result=REJECTED requestedSource=Scheduler.Daily requestedSilent=True requestedScope=All reason={shutdownReason} action=reject_start rule=shutdown_admission_contract");
            return false;
        }

        var expectedPlan = new DailyEpgPlanResult(
            expectedState.CanonicalStart, expectedState.PlannedStart, expectedState.PlannedEnd,
            expectedState.MovementDeadline, expectedState.RequiredSeconds, expectedState.Placement, "committed_daily_plan");

        CancellationTokenSource? cts = null;
        TaskCompletionSource<bool>? runStartGate = null;
        IDisposable? sleepLease = null;
        long runGeneration = 0;
        var runningCommitted = false;
        DailyEpgRunState? committedState = null;
        try
        {
            if (!IsDailyStartStillSafe(expectedState, DateTime.Now, out var startValidationReason))
            {
                log.Add("EPG_RUN_GUARD", "EPG",
                    $"result=REJECTED requestedSource=Scheduler.Daily requestedSilent=True requestedScope=All reason={startValidationReason} action=replan_before_start rule=daily_epg_start_validation_contract");
                SignalSchedulerWake();
                return false;
            }

            lock (gate)
            {
                if (isStopping || isRunning || IsDailyExecutorRunningLocked()) return false;

                runGeneration = nextRunGeneration + 1;
                if (!sleepInhibition.TryAcquire("Scheduler.Daily", "EPG:All", runGeneration, out sleepLease, out var powerError))
                {
                    log.Add("EPG_RUN_GUARD", "EPG",
                        $"result=REJECTED requestedSource=Scheduler.Daily requestedSilent=True requestedScope=All reason=system_sleep_inhibition_unavailable detail={powerError} action=reject_unprotected_epg_run rule=epg_run_power_lifecycle_contract");
                    return false;
                }

                if (!TryCommitDailyRunning(expectedPlan, DateTime.Now, out var committed, out var rejectReason))
                {
                    log.Add("EPG_RUN_GUARD", "EPG",
                        $"result=REJECTED requestedSource=Scheduler.Daily requestedSilent=True requestedScope=All reason={rejectReason} action=reject_before_executor_start rule=daily_epg_running_commit_contract");
                    return false;
                }
                committedState = committed;
                runningCommitted = true;

                cts = new CancellationTokenSource();
                runGeneration = ++nextRunGeneration;
                runStartGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var executor = new ActiveDailyExecutor
                {
                    Generation = runGeneration,
                    Cts = cts,
                    Task = Task.CompletedTask
                };
                executor.Task = Task.Run(async () =>
                {
                    await runStartGate.Task.ConfigureAwait(false);
                    await RunWithGuardAsync(cts.Token, scope, "Scheduler.Daily", true, runGeneration, sleepLease!, committedState, committedState!.Config.Depth).ConfigureAwait(false);
                });
                activeDailyExecutor = executor;
                lastStartBlock = null;
            }

            log.Add("EPG_RUN_REQUEST", "EPG",
                $"result=START source=Scheduler.Daily silent=True uiMode=Silent targetScope=All route=EpgScheduler.DailyRunner samePipeline=True runGeneration={runGeneration} owner=daily_all rule=daily_epg_running_commit_contract");
            userEvents.AddScheduledEpgStarted(scope, "Scheduler.Daily", true);
            typedEvents.Publish(new TvAirEventDto
            {
                EventType = TvAirEventType.EpgStarted,
                EntityId = $"epg:Scheduler.Daily:All:{DateTimeOffset.Now:yyyyMMddHHmmssfff}",
                Details = new Dictionary<string, string>
                {
                    ["source"] = "Scheduler.Daily", ["targetScope"] = scope, ["silent"] = bool.TrueString, ["depth"] = committedState!.Config.Depth
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            bool executorRegistered;
            lock (gate)
                executorRegistered = activeDailyExecutor is not null && activeDailyExecutor.Generation == runGeneration;

            if (executorRegistered)
            {
                log.Add("EPG_RUN_GUARD", "EPG",
                    $"result=START_NOTIFICATION_WARN requestedSource=Scheduler.Daily requestedScope=All error={ex.GetType().Name}:{ex.Message} action=keep_registered_executor rule=daily_epg_running_commit_contract");
                return true;
            }

            log.Add("EPG_RUN_GUARD", "EPG",
                $"result=ERROR requestedSource=Scheduler.Daily requestedScope=All runningCommitted={runningCommitted} error={ex.GetType().Name}:{ex.Message} action=fail_committed_start_without_executor rule=daily_epg_running_commit_contract");
            if (runningCommitted)
            {
                CommitDailyFailed(DateTime.Now);
                runningCommitted = false;
            }
            return false;
        }
        finally
        {
            runStartGate?.TrySetResult(true);
            if (!runningCommitted)
            {
                cts?.Dispose();
                sleepLease?.Dispose();
            }
        }
    }

    /// <summary>Visible 手動EPGを、Visible UIの明示キャンセルから停止する。</summary>
    public bool CancelVisible(string requestedBy = "VisibleUi")
        => CancelCurrentRun(requestedBy, requiredSilent: false, cancelRoute: "VisibleWidget");

    /// <summary>Silent 手動EPG／定時EPGを、Silent系の明示キャンセルから停止する。</summary>
    public bool CancelSilent(string requestedBy = "SilentUi")
        => CancelCurrentRun(requestedBy, requiredSilent: true, cancelRoute: "SilentTray");

    private bool CancelCurrentRun(string requestedBy, bool requiredSilent, string cancelRoute)
    {
        var normalizedSource = string.IsNullOrWhiteSpace(requestedBy) ? cancelRoute : requestedBy.Trim();
        CancellationTokenSource? manualCts = null;
        CancellationTokenSource? dailyCts = null;
        string scope = "All";
        bool currentSilent = false;

        lock (gate)
        {
            if (isRunning)
            {
                manualCts = runCts;
                scope = pendingRunScope;
                currentSilent = pendingRunSilent;
            }
            else if (requiredSilent && activeDailyExecutor is not null)
            {
                dailyCts = activeDailyExecutor.Cts;
                scope = "All";
                currentSilent = true;
                BeginDailyCancelRequest();
            }
        }

        if (manualCts is null && dailyCts is null)
        {
            log.Add("EPG_CANCEL_REQUEST", "EPG",
                $"result=IGNORED source={normalizedSource} cancelRoute={cancelRoute} reason=not_running rule=release_contract");
            return false;
        }
        if (currentSilent != requiredSilent)
        {
            log.Add("EPG_CANCEL_REQUEST", "EPG",
                $"result=REJECTED source={normalizedSource} cancelRoute={cancelRoute} currentUiMode={(currentSilent ? "Silent" : "Visible")} reason=cancel_route_mode_mismatch rule=epg_explicit_cancel_route_contract");
            return false;
        }

        log.Add("EPG_CANCEL_REQUEST", "EPG",
            $"result=ACCEPT source={normalizedSource} targetScope={scope} uiMode={(currentSilent ? "Silent" : "Visible")} cancelRoute={cancelRoute} scheduledDailyOwner={(dailyCts is null ? 0 : 1)} action=cancel_run_cts rule=epg_explicit_cancel_route_contract");
        try { manualCts?.Cancel(); } catch { }
        try { dailyCts?.Cancel(); } catch { }
        return true;
    }

    /// <summary>
    /// Dailyだけを対象にする内部Cancel入口。手動Silent/Visibleを巻き込まず、
    /// 明示Daily Cancelと同じphysical convergence/zero-reset ownerへ合流する。
    /// active executorが既に無いRunningは、Resume reconciliation後の中断runとして直接Cancelledへ収束する。
    /// </summary>
    private bool RequestCancelDailyOnly(string requestedBy, string cancelRoute)
    {
        CancellationTokenSource? dailyCts;
        lock (gate)
        {
            dailyCts = activeDailyExecutor?.Cts;
            if (dailyCts is not null) BeginDailyCancelRequest();
        }

        if (dailyCts is not null)
        {
            log.Add("EPG_CANCEL_REQUEST", "EPG",
                $"result=ACCEPT source={requestedBy} targetScope=All uiMode=Silent cancelRoute={cancelRoute} scheduledDailyOwners=1 action=cancel_daily_run_cts rule=daily_epg_cancel_contract");
            try { dailyCts.Cancel(); } catch { }
            return true;
        }

        var state = GetDailyRunStateSnapshot();
        if (state is not { State: DailyEpgRunPhase.Running })
            return false;

        CommitDailyCancelled(DateTime.Now);
        var cancelledState = GetDailyRunStateSnapshot();
        if (cancelledState is not null)
            EnsureDailyProjection(cancelledState, refreshAllocationRoute: false);
        EnsureTomorrowAfterDailyTerminal(cancelledState, "DailyInterruptedCancelled");
        // live executorが存在する通常Daily Cancelと公開契約を揃える。
        // PowerResume等はcancel理由であり、EPG公開owner自体はScheduler.Dailyのままにする。
        userEvents.AddScheduledEpgCancelled("All", "Scheduler.Daily", silent: true);
        typedEvents.Publish(new TvAirEventDto
        {
            EventType = TvAirEventType.EpgCancelled,
            EntityId = $"epg:Scheduler.Daily:{DateTimeOffset.Now:yyyyMMddHHmmssfff}",
            Details = new Dictionary<string, string>
            {
                ["source"] = "Scheduler.Daily",
                ["targetScope"] = "All",
                ["silent"] = bool.TrueString
            }
        });
        log.Add("EPG_RUN_CANCELLED", "EPG",
            $"Daily EPG interrupted run converged after {cancelRoute}. source={requestedBy} targetScope=All rule=daily_epg_cancel_contract");
        SignalSchedulerWake();
        return true;
    }

    public bool IsRunning
    {
        get
        {
            var dailyRunning = IsDailyRunning;
            lock (gate) return isRunning || activeDailyExecutor is not null || dailyRunning;
        }
    }

    /// <summary>Daily EPGが正式なRunningへcommit済みかを返す。Task/CTS表ではなくDailyRunStateを正本にする。</summary>
    public bool IsDailyRunning
    {
        get
        {
            lock (dailyRunStateGate)
                return currentDailyRunState is { State: DailyEpgRunPhase.Running };
        }
    }

    /// <summary>スケジュール設定を動的に更新する（設定保存時に呼ぶ）。</summary>
    public void UpdateConfig(
        bool enabled,
        int hour,
        int minute,
        string? epgDepth,
        bool refreshAllocationRoute)
    {
        var normalizedHour = SettingsDefaults.NormalizeEpgHour(hour);
        var normalizedMinute = SettingsDefaults.NormalizeEpgMinute(minute);
        var nextDepth = NormalizeDepth(epgDepth);
        var beforeDepth = _currentDepth;
        EpgScheduleConfig previousConfig;
        lock (configGate)
            previousConfig = config;

        var scheduleContractChanged = previousConfig.Enabled != enabled
            || previousConfig.Hour != normalizedHour
            || previousConfig.Minute != normalizedMinute;
        var depthChanged = !string.Equals(beforeDepth, nextDepth, StringComparison.OrdinalIgnoreCase);

        // 呼出元はINI commit前に同じ契約を検証する。ここでも入口を守り、将来別経路から呼ばれても
        // Running snapshotへ設定を後段上塗りしない。実行中変更を保存してからdeferする旧方式は採らない。
        if (IsDailyRunning && (scheduleContractChanged || depthChanged))
            throw new InvalidOperationException("Daily EPG is running; daily settings are immutable until completion.");
        if (depthChanged && IsRunning)
            throw new InvalidOperationException("Normal EPG is running; EPG depth is immutable until completion.");

        lock (configGate)
        {
            config = new EpgScheduleConfig(enabled, normalizedHour, normalizedMinute);
        }
        if (scheduleContractChanged)
        {
            // Daily lifecycleの永続正本はDailyEpgRunStateだけ。設定変更で過去の実行状態を
            // HashSet/補助JSONごと消して再構成しない。未開始のDaily All planだけをscheduler側で再計画する。
            log.Add("EPG_DAILY_STATE", "EPG",
                $"result=CONFIG_CHANGED beforeEnabled={previousConfig.Enabled} beforeTime={previousConfig.Hour:D2}:{previousConfig.Minute:D2} afterEnabled={enabled} afterTime={normalizedHour:D2}:{normalizedMinute:D2} action=invalidate_unstarted_plan rule=daily_epg_single_state_contract");
        }

        _currentDepth = nextDepth;
        log.Add("EPG_DEPTH_POLICY", "Settings",
            $"result={(depthChanged ? "CHANGED" : "UNCHANGED")} before={beforeDepth} after={nextDepth} enabled={enabled} time={normalizedHour:D2}:{normalizedMinute:D2} apply=runtime_now rule=epg_duration_policy_common");
        if (depthChanged)
        {
            capture.UpdateRuntimeDepth(_currentDepth);
            log.Add("EPG_SCHEDULER", "Depth", $"runtimeDepth={_currentDepth} source=Settings action=UpdateConfig rule=epg_depth_runtime_contract");
        }

        if (enabled)
        {
            // Daily設定のcommit後はschedulerと同じPlanner/State経路へ収束させる。
            // 当日がすでにterminalならその日を復活させず、変更後設定は翌日責務へ反映する。
            // Projectionを設定値から直接作ってDaily正本より先行させない。
            var now = DateTime.Now;
            var snapshot = new DailyEpgConfigSnapshot(
                true, normalizedHour, normalizedMinute, SettingsDefaults.NormalizeEpgDepth(_currentDepth));
            var stateBeforeUpdate = GetDailyRunStateSnapshot();
            if (stateBeforeUpdate is not null
                && stateBeforeUpdate.Date.Date == now.Date
                && IsDailyRunTerminal(stateBeforeUpdate.State))
            {
                EnsureDailyResponsibility(now.Date.AddDays(1), snapshot, refreshAllocationRoute);
            }
            else
            {
                var plan = BuildDailyEpgPlanResult(now.Date, snapshot, now);
                var changed = TryCommitDailyPlannerResult(now.Date, snapshot, plan, now);
                var state = GetDailyRunStateSnapshot();
                if (state is not null && state.Date.Date == now.Date && !IsDailyRunTerminal(state.State))
                    EnsureDailyProjection(state, refreshAllocationRoute && changed);
            }
        }
        else
        {
            // 定時EPGをOFFにしても録画前EPG確認は独立して有効なため、定時EPG行だけを削除する。
            try
            {
                var deleted = reservationStore.DeleteScheduledDailyEpgEntries();
                if (deleted > 0)
                    log.Add("EPG_SCHEDULER", "EpgEntry", $"定時EPG設定OFF: 定時EPGエントリ{deleted}件を削除しました。");
            }
            catch (Exception ex) { log.Add("EPG_SCHEDULER", "EpgEntry", $"EPGエントリ削除エラー: {ex.Message}"); }

            // EPG_UPDATE_CONFIG_ROUTE_OWNERSHIP_CONTRACT
            // refreshAllocationRoute=true はON側のUpsertだけでなくOFF側のDaily行削除にも適用する。
            // OFF切替後はFinalAllocation/Wakeを現在の予約状態へ再同期する。
            // 設定保存は後続のSettings単一Routeへ合流するためfalseを明示する。
            if (refreshAllocationRoute)
            {
                try
                {
                    ReevaluateAndLog("EpgEntryDisabled", refreshWakeTask: true);
                }
                catch (Exception ex)
                {
                    log.Add("EPG_SCHEDULER", "EpgEntry", $"定時EPG設定OFF後の競合評価/Wake再構築エラー: {ex.Message}");
                }
            }
        }

        // 現在の待機を中断して次回時刻を再計算させる
        SignalSchedulerWake();
        log.Add("EPG_SCHEDULER_CONFIG", "EPG",
            $"EPGスケジュール設定を更新しました。enabled={enabled} time={hour:D2}:{minute:D2}");
    }

    /// <summary>
    /// 外部時刻/電源通知はDailyロジックを直接実行せず、scheduler再評価または既存Cancel ownerへ合流させる。
    /// SystemEventsのhandler上でPlanner/DB/worker操作を開始しない。
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref externalSchedulerSignalsSubscribed, 1) == 0)
        {
            SystemEvents.TimeChanged += OnSystemTimeChanged;
            powerResumeSignals.SuspendObserved += OnPowerSuspendObserved;
            powerResumeSignals.ResumeReconciled += OnPowerResumeReconciled;
        }
        return base.StartAsync(cancellationToken);
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e)
    {
        log.Add("EPG_SCHEDULER_CLOCK", "EPG",
            $"result=TIME_CHANGED observedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss} action=invalidate_scheduler_only rule=daily_epg_clock_change_contract");
        SignalSchedulerWake();
    }

    private void OnPowerSuspendObserved(string cycleId, DateTime observedAt)
    {
        var state = GetDailyRunStateSnapshot();
        var running = state is { State: DailyEpgRunPhase.Running };

        lock (dailyPowerSignalGate)
        {
            suspendedDailyCycleId = running ? cycleId : null;
            suspendedDailyDate = running ? state!.Date.Date : null;
        }

        log.Add("EPG_DAILY_POWER", "EPG",
            $"result=SUSPEND_OBSERVED cycle={cycleId} dailyRunning={running} targetDate={(running ? state!.Date.ToString("yyyy-MM-dd") : "-")} action=remember_only_no_worker_interrupt rule=daily_epg_suspend_cancel_contract");
    }

    private void OnPowerResumeReconciled(string cycleId, DateTime reconciledAt)
    {
        DateTime? interruptedDate = null;
        lock (dailyPowerSignalGate)
        {
            if (string.Equals(suspendedDailyCycleId, cycleId, StringComparison.Ordinal))
                interruptedDate = suspendedDailyDate;
            suspendedDailyCycleId = null;
            suspendedDailyDate = null;
        }

        // Resumeは時刻・予約状態の再評価契機でもあるため、Daily中断の有無にかかわらず同じscheduler入口を起こす。
        SignalSchedulerWake();
        if (!interruptedDate.HasValue)
            return;

        var cancelled = RequestCancelDailyOnly("PowerResume", "PowerResumeReconciled");
        log.Add("EPG_DAILY_POWER", "EPG",
            $"result={(cancelled ? "CANCEL_REQUESTED" : "NO_ACTIVE_DAILY_TO_CANCEL")} cycle={cycleId} targetDate={interruptedDate.Value:yyyy-MM-dd} reconciledAt={reconciledAt:yyyy-MM-dd HH:mm:ss} action=converge_through_daily_cancel_owner rule=daily_epg_suspend_cancel_contract");
    }

    // ─── BackgroundService ───────────────────────────────────────

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref externalSchedulerSignalsSubscribed, 0) == 1)
        {
            SystemEvents.TimeChanged -= OnSystemTimeChanged;
            powerResumeSignals.SuspendObserved -= OnPowerSuspendObserved;
            powerResumeSignals.ResumeReconciled -= OnPowerResumeReconciled;
        }

        CancellationTokenSource? activeCts;
        Task? runTask;
        CancellationTokenSource? dailyCts;
        Task? dailyTask;
        lock (gate)
        {
            isStopping = true;
            activeCts = runCts;
            runTask = activeRunTask;
            dailyCts = activeDailyExecutor?.Cts;
            dailyTask = activeDailyExecutor?.Task;
        }

        log.Add("EPG_SCHEDULER_STOP", "EPG",
            $"result=BEGIN manualRunning={(runTask is not null)} activeDailyExecutor={(dailyTask is null ? 0 : 1)} action=cancel_scheduler_and_active_runs rule=epg_scheduler_shutdown_contract");
        try { activeCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { dailyCts?.Cancel(); } catch (ObjectDisposedException) { }

        var schedulerQuiesced = false;
        var activeRunQuiesced = runTask is null;
        var scheduledRunsQuiesced = dailyTask is null;

        try
        {
            await base.StopAsync(cancellationToken);
            schedulerQuiesced = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Add("EPG_SCHEDULER_STOP", "WARN",
                "result=SCHEDULER_TIMEOUT action=host_stop_deadline_reached rule=epg_scheduler_shutdown_contract");
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.WaitAsync(cancellationToken);
                activeRunQuiesced = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                log.Add("EPG_SCHEDULER_STOP", "WARN",
                    "result=RUN_TIMEOUT action=host_stop_deadline_reached_before_active_run_quiescence rule=epg_scheduler_shutdown_contract");
            }
            catch (Exception ex)
            {
                activeRunQuiesced = runTask.IsCompleted;
                log.Add("EPG_SCHEDULER_STOP", "WARN",
                    $"result=RUN_WAIT_ERROR error={ex.GetType().Name}:{ex.Message} completed={runTask.IsCompleted} rule=epg_scheduler_shutdown_contract");
            }
        }

        if (dailyTask is not null)
        {
            try
            {
                await dailyTask.WaitAsync(cancellationToken);
                scheduledRunsQuiesced = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                log.Add("EPG_SCHEDULER_STOP", "WARN",
                    "result=SCHEDULED_DAILY_TIMEOUT action=host_stop_deadline_reached_before_daily_quiescence rule=epg_scheduler_shutdown_contract");
            }
            catch (Exception ex)
            {
                scheduledRunsQuiesced = dailyTask.IsCompleted;
                log.Add("EPG_SCHEDULER_STOP", "WARN",
                    $"result=SCHEDULED_DAILY_WAIT_ERROR error={ex.GetType().Name}:{ex.Message} completed={scheduledRunsQuiesced} rule=epg_scheduler_shutdown_contract");
            }
        }

        var fullyQuiesced = schedulerQuiesced && activeRunQuiesced && scheduledRunsQuiesced;
        log.Add("EPG_SCHEDULER_STOP", fullyQuiesced ? "EPG" : "WARN",
            $"result={(fullyQuiesced ? "END" : "INCOMPLETE")} schedulerQuiesced={schedulerQuiesced} manualRunQuiesced={activeRunQuiesced} scheduledRunsQuiesced={scheduledRunsQuiesced} action={(fullyQuiesced ? "scheduler_and_active_runs_quiesced" : "shutdown_deadline_reached_before_full_quiescence")} rule=epg_scheduler_shutdown_contract");
    }

    private DailyEpgPlanResult BuildDailyEpgPlanResult(
        DateTime targetDate,
        DailyEpgConfigSnapshot snapshot,
        DateTime now)
    {
        var canonicalStart = snapshot.CanonicalStartFor(targetDate);
        var movementDeadline = canonicalStart.Add(ScheduledEpgMovementLimit);
        var estimate = EstimateEpgDuration("All", snapshot.Depth);
        var hasTargets = estimate.TsCount > 0;
        var hasTuners = estimate.TunerCount > 0;
        var requiredSeconds = Math.Max(0, estimate.RequiredSeconds);

        log.Add("EPG_SCHEDULED_DURATION_ESTIMATE", "All",
            $"result={(hasTargets && hasTuners && requiredSeconds > 0 ? "OK" : "UNAVAILABLE")} targetScope=All depth={snapshot.Depth} tsCount={estimate.TsCount} recordingTuners={estimate.TunerCount} " +
            $"laneLoadsSec=[{string.Join(',', estimate.LaneLoadsSeconds)}] captureCriticalPathSec={estimate.CaptureCriticalPathSeconds} safetyMarginSec={estimate.SafetyMarginSeconds} " +
            $"handoffSafetySec={(int)EpgRecordingHandoffPolicy.SafetyMargin.TotalSeconds} requiredSeconds={requiredSeconds} model=capture_target_single_source_group_parallel_critical_path source={estimate.Source} rule=epg_duration_single_source_contract");

        if (!hasTuners)
            return new DailyEpgPlanResult(canonicalStart, null, null, movementDeadline, 0, DailyEpgPlacementState.Unplaced, "no_recording_tuner");
        if (!hasTargets || requiredSeconds <= 0)
            return new DailyEpgPlanResult(canonicalStart, null, null, movementDeadline, 0, DailyEpgPlacementState.Unplaced, "target_duration_unavailable");

        var post = TimeSpan.FromSeconds(Math.Max(0, ini.PostEndMarginSeconds));
        var blocks = reservationStore.GetAll()
            .Where(r => r.Source != ReservationSource.Epg
                     && r.IsEnabled && !r.IsConflicted
                     && r.Status is not ReservationStatus.Completed and not ReservationStatus.Cancelled and not ReservationStatus.Failed)
            .Select(r => new DailyEpgRecordingBlock(
                NormalizeEpgScheduleGroup(ResolveReservationGroup(r)),
                EpgRecordingHandoffPolicy.LatestNormalEpgEndBeforeRecording(r.StartTime, ini.PreStartMarginSeconds),
                r.EndTime + post,
                r.Id))
            .Where(x => x.Group is "GR" or "BSCS")
            .ToArray();

        var required = TimeSpan.FromSeconds(requiredSeconds);
        var candidate = canonicalStart < now ? now : canonicalStart;
        var reason = candidate > canonicalStart ? "canonical_time_passed" : "canonical_slot";
        while (candidate.Add(required) <= movementDeadline)
        {
            var candidateEnd = candidate.Add(required);
            // All Dailyはどちらの放送グループの録画占有とも重ならない連続枠だけを採用する。
            var overlap = blocks
                .Where(x => x.OccupyEnd > candidate && x.SafeStart < candidateEnd)
                .OrderBy(x => x.OccupyEnd)
                .ThenBy(x => x.ReservationId)
                .FirstOrDefault();
            if (overlap is null)
                return new DailyEpgPlanResult(canonicalStart, candidate, candidateEnd, movementDeadline, requiredSeconds, DailyEpgPlacementState.Placed, reason);

            candidate = CeilToNextMinuteBoundary(overlap.OccupyEnd);
            reason = $"moved_after_recording_R{overlap.ReservationId}";
        }

        return new DailyEpgPlanResult(canonicalStart, null, null, movementDeadline, requiredSeconds, DailyEpgPlacementState.Unplaced, "no_contiguous_all_slot_within_3h");
    }

    private static DateTime CeilToNextMinuteBoundary(DateTime value)
    {
        var minute = new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);
        return value == minute ? value : minute.AddMinutes(1);
    }

    /// <summary>
    /// due判定からRunning commitまでの予約mutation raceを閉じる最終安全確認。
    /// 既存plan自体を再計算・移動せず、その固定占有区間が現在の録画timelineでも安全かだけを判定する。
    /// </summary>
    private bool IsDailyStartStillSafe(DailyEpgRunState plan, DateTime now, out string reason)
    {
        reason = string.Empty;
        if (plan.State != DailyEpgRunPhase.Planned
            || plan.Placement != DailyEpgPlacementState.Placed
            || !plan.PlannedStart.HasValue || !plan.PlannedEnd.HasValue)
        {
            reason = "daily_not_startable";
            return false;
        }
        if (now >= plan.PlannedEnd.Value || now >= plan.MovementDeadline)
        {
            reason = "planned_slot_elapsed";
            return false;
        }

        var estimate = EstimateEpgDuration("All", plan.Config.Depth);
        if (estimate.TsCount <= 0 || estimate.TunerCount <= 0 || estimate.RequiredSeconds <= 0)
        {
            reason = "target_duration_unavailable";
            return false;
        }
        // Planner確定後に局/TS構成や利用可能録画Tuner数が変わり、現在必要時間が増えた場合は
        // 古い短い枠で開始しない。未開始Plannedのままscheduler再評価へ戻して新しいAll枠を計画する。
        if (estimate.RequiredSeconds > plan.RequiredSeconds)
        {
            reason = "target_duration_increased";
            return false;
        }

        var post = TimeSpan.FromSeconds(Math.Max(0, ini.PostEndMarginSeconds));
        var overlap = reservationStore.GetAll()
            .Where(r => r.Source != ReservationSource.Epg && r.IsEnabled && !r.IsConflicted
                     && r.Status is not ReservationStatus.Completed and not ReservationStatus.Cancelled and not ReservationStatus.Failed)
            .Select(r => new
            {
                SafeStart = EpgRecordingHandoffPolicy.LatestNormalEpgEndBeforeRecording(r.StartTime, ini.PreStartMarginSeconds),
                OccupyEnd = r.EndTime + post
            })
            .Any(x => x.OccupyEnd > plan.PlannedStart.Value && x.SafeStart < plan.PlannedEnd.Value);
        if (overlap)
        {
            reason = "recording_timeline_changed";
            return false;
        }
        return true;
    }

    private IReadOnlyList<TunerProfile> GetScheduledRecordingTuners(string group)
    {
        var normalized = NormalizeEpgScheduleGroup(group);
        return tunerProfiles
            .Where(t => !string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .Where(t => string.Equals(NormalizeEpgScheduleGroup(t.Group), normalized, StringComparison.OrdinalIgnoreCase)
                     || (string.Equals(t.Group, "HYBRID", StringComparison.OrdinalIgnoreCase)
                         && string.Equals(normalized, "BSCS", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>
    /// DEVELOPER_APPROVAL_REQUIRED: System EPG Projectionは保護領域。
    /// 2026-08-24 APPROVED_SCOPE: Scheduled DailyのGR/BSCS wave分裂を廃止し、単一All planを6物理録画Tunerへ同時刻投影する変更のみ明示承認済み。
    /// それ以外の再構築・補正追加・責務変更・既存正常経路の置換は、改めて事前の開発者明示承認を得ること。
    /// 「次」「続けて」「進めて」等は変更承認ではない。他案件の修正に便乗して触らない。
    /// 与えられたDaily lifecycle snapshotをSystemDailyEpg予約行へ投影する。
    /// ProjectionはPlanner・terminal判定・翌日生成を行わず、Daily lifecycleを予約行から逆算しない。
    /// Planned/Runningの単一All予定だけを6物理録画Tuner責務へ同時刻で投影し、terminalをScheduledへ巻き戻さない。
    /// </summary>
    private int EnsureDailyProjection(DailyEpgRunState state, bool refreshAllocationRoute)
    {
        var changed = 0;
        var recordingTunersByGroup = new[] { "GR", "BSCS" }
            .ToDictionary(
                group => group,
                group => GetScheduledRecordingTuners(group),
                StringComparer.OrdinalIgnoreCase);

        if (state.State is not (DailyEpgRunPhase.Planned or DailyEpgRunPhase.Running))
        {
            foreach (var tuner in recordingTunersByGroup.Values
                         .SelectMany(x => x)
                         .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First()))
            {
                changed += reservationStore.DeleteScheduledDailyEpgEntriesForTuner(tuner.Name);
            }
        }
        else
        {
            var start = state.Placement == DailyEpgPlacementState.Placed && state.PlannedStart.HasValue
                ? state.PlannedStart.Value
                : state.CanonicalStart;
            var end = state.Placement == DailyEpgPlacementState.Placed && state.PlannedEnd.HasValue
                ? state.PlannedEnd.Value
                : start.AddSeconds(Math.Max(1, state.RequiredSeconds));

            // System EPGは6物理録画Tuner責務を維持するが、Daily予定時刻は全Tunerで同じ単一All planを投影する。
            foreach (var pair in recordingTunersByGroup)
            {
                foreach (var tuner in pair.Value)
                {
                    if (reservationStore.UpsertEpgScheduleEntry(pair.Key, start, end,
                        $"EPG取得（{tuner.Name}）", tuner.Name))
                        changed++;
                }
            }
        }

        if (refreshAllocationRoute && changed > 0)
            ReevaluateAndLog("EpgDailyProjection", refreshWakeTask: true);
        return changed;
    }

    private static DailyEpgRunState BuildProjectionState(
        DateTime date,
        DailyEpgConfigSnapshot configSnapshot,
        DailyEpgPlanResult plan,
        DateTime updatedAt)
        => new(
            date.Date,
            DailyEpgRunPhase.Planned,
            configSnapshot,
            plan.CanonicalStart,
            plan.PlannedStart,
            plan.PlannedEnd,
            plan.MovementDeadline,
            plan.RequiredSeconds,
            plan.Placement,
            updatedAt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.Add("EPG_SCHEDULER_START", "EPG", "EPGスケジューラーを開始しました。");

        // 起動時に自動検索予約の整合性をとる。
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
                RefreshWakeTask: false,
                EmitConflictLogs: true,
                ConflictLogCategory: "EPG_SCHEDULER",
                ConflictLogTitle: "Conflict(StartupSync)"));
        }
        catch (Exception ex)
        {
            log.Add("KEYWORD_MATCH_ERROR", "StartupRunMatching",
                $"起動時マッチング例外: {ex.Message}");
        }

        // Dailyの正本を最初に復元し、その結果からProjection/Wakeを作る。
        // SystemDailyEpg予約行やWake taskからDaily lifecycleを逆算してはならない。
        RestoreDailyRunStateAtStartup(DateTime.Now);

        // 起動時にEPGスケジュールエントリを登録（有効な場合）または削除（無効な場合）
        EpgScheduleConfig initCfg;
        lock (configGate) initCfg = config;
        var startupDailyState = GetDailyRunStateSnapshot();
        var todayAlreadyTerminal = startupDailyState is not null
            && startupDailyState.Date.Date == DateTime.Now.Date
            && IsDailyRunTerminal(startupDailyState.State);
        if (initCfg.Enabled)
        {
            if (!todayAlreadyTerminal)
            {
                var startupNow = DateTime.Now;
                var startupConfig = new DailyEpgConfigSnapshot(
                    initCfg.Enabled, initCfg.Hour, initCfg.Minute, SettingsDefaults.NormalizeEpgDepth(_currentDepth));
                var startupPlan = BuildDailyEpgPlanResult(startupNow.Date, startupConfig, startupNow);
                TryCommitDailyPlannerResult(startupNow.Date, startupConfig, startupPlan, startupNow);
                var projectionState = GetDailyRunStateSnapshot();
                if (projectionState is not null && projectionState.Date.Date == startupNow.Date)
                    EnsureDailyProjection(projectionState, refreshAllocationRoute: false);
            }
            else
            {
                // terminal当日を再生成しない一方、翌日責務は現在の確定設定で必ず投影し直す。
                // 起動時の全削除でSucceeded時にEnsure済みの翌日行まで失わせない。
                reservationStore.DeleteScheduledDailyEpgEntries();
                var nextSnapshot = new DailyEpgConfigSnapshot(
                    initCfg.Enabled, initCfg.Hour, initCfg.Minute, SettingsDefaults.NormalizeEpgDepth(_currentDepth));
                EnsureDailyResponsibility(DateTime.Now.Date.AddDays(1), nextSnapshot, refreshAllocationRoute: false);
                log.Add("EPG_DAILY_STATE", "EPG",
                    $"result=STARTUP_TERMINAL_PRESERVED targetDate={startupDailyState!.Date:yyyy-MM-dd} state={startupDailyState.State} action=no_same_day_recreate_ensure_tomorrow rule=daily_epg_single_state_contract");
            }
            try { UpsertPreRecordEpgEntries(); }
            catch (Exception ex) { log.Add("EPG_SCHEDULER", "PreRecEpg", $"起動時直前EPGエントリ生成エラー: {ex.Message}"); }
        }
        else
        {
            // 定時EPGをOFFにしても録画前EPG確認は独立して有効なため、定時EPG行だけを削除する。
            try
            {
                var deleted = reservationStore.DeleteScheduledDailyEpgEntries();
                if (deleted > 0)
                    log.Add("EPG_SCHEDULER", "EpgEntry", $"定時EPG設定OFF: 定時EPGエントリ{deleted}件を削除しました。");
            }
            catch (Exception ex) { log.Add("EPG_SCHEDULER", "EpgEntry", $"EPGエントリ削除エラー: {ex.Message}"); }
        }

        // STARTUP_WAKE_SINGLE_OWNER_INVARIANT:
        // Daily正本復元とSystemDailyEpg/PreRec投影を先に確定し、Wakeは全起動Mutation後のこの共通割当1回だけが所有する。

        try
        {
            allocationRoute.Run(new ReservationAllocationRouteRequest(
                Source: "EpgScheduler",
                Action: "StartupFinalize",
                RunKeywordMatcher: false,
                SyncProgramRuleReservations: false,
                ReevaluateAllocations: true,
                RefreshPreRecordEpgEntries: true,
                RefreshWakeTask: true,
                EmitConflictLogs: true,
                ConflictLogCategory: "EPG_SCHEDULER",
                ConflictLogTitle: "Conflict(StartupFinalize)"));
        }
        catch (Exception ex)
        {
            log.Add("EPG_SCHEDULER", "StartupFinalize",
                $"result=FAILED error={ex.GetType().Name}:{ex.Message} action=retry_by_next_regular_route rule=startup_wake_single_owner_contract");
        }

        string lastNextLogKey = string.Empty;
        DateTime lastNextLogUtc = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            EpgScheduleConfig cfg;
            lock (configGate)
                cfg = config;

            var now = DateTime.Now;

            // live runがFinalizingまで進んだ後にEnsureTomorrowだけが失敗した場合も、
            // 専用repair stateは作らず同じFinalizingを再実行する。active executorが残る間は
            // post-importと競合させず、executor収束後だけschedulerが再試行する。
            var stateBeforePlan = GetDailyRunStateSnapshot();
            bool hasActiveDailyExecutor;
            lock (gate) hasActiveDailyExecutor = activeDailyExecutor is not null;
            if (stateBeforePlan is { State: DailyEpgRunPhase.Finalizing } && !hasActiveDailyExecutor)
            {
                TryFinalizeDailySucceeded(stateBeforePlan.UpdatedAt);
                now = DateTime.Now;
            }

            if (!cfg.Enabled)
            {
            }
            else
            {
                var configSnapshot = new DailyEpgConfigSnapshot(
                    cfg.Enabled, cfg.Hour, cfg.Minute, SettingsDefaults.NormalizeEpgDepth(_currentDepth));
                var plan = BuildDailyEpgPlanResult(now.Date, configSnapshot, now);
                var planChanged = TryCommitDailyPlannerResult(now.Date, configSnapshot, plan, now);

                var expiredChanged = false;
                if (plan.Placement == DailyEpgPlacementState.Unplaced && now >= plan.MovementDeadline)
                {
                    var before = GetDailyRunStateSnapshot();
                    CommitDailyExpired(now);
                    var after = GetDailyRunStateSnapshot();
                    expiredChanged = !Equals(before, after);
                    if (expiredChanged)
                    {
                        log.Add("EPG_SCHEDULED_DAILY_SKIP", "All",
                            $"result=SKIPPED targetScope=All canonical={plan.CanonicalStart:yyyy-MM-dd HH:mm:ss} movementDeadline={plan.MovementDeadline:yyyy-MM-dd HH:mm:ss} reason={plan.Reason} action=terminal_after_actual_deadline rule=scheduled_epg_all_movable_slot_contract");
                    }
                }

                var dailyState = GetDailyRunStateSnapshot();
                if (dailyState is not null && dailyState.Date.Date == now.Date && (planChanged || expiredChanged))
                {
                    EnsureDailyProjection(dailyState, refreshAllocationRoute: true);
                    if (expiredChanged)
                        EnsureTomorrowAfterDailyTerminal(dailyState, "DailyExpired");
                    if (dailyState.State == DailyEpgRunPhase.Planned)
                    {
                        var status = dailyState.Placement == DailyEpgPlacementState.Unplaced
                            ? "UNPLACED_PENDING_DEADLINE"
                            : "PLACED";
                        log.Add("EPG_SCHEDULED_DAILY_PLAN", "All",
                            $"result={status} targetScope=All canonical={dailyState.CanonicalStart:yyyy-MM-dd HH:mm:ss} start={(dailyState.PlannedStart?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-")} end={(dailyState.PlannedEnd?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-")} requiredSeconds={dailyState.RequiredSeconds} movementDeadline={dailyState.MovementDeadline:yyyy-MM-dd HH:mm:ss} reason={plan.Reason} rule=scheduled_epg_all_movable_slot_contract");
                    }
                }

                dailyState = GetDailyRunStateSnapshot();
                if (dailyState is { State: DailyEpgRunPhase.Planned }
                    && dailyState.Date.Date == now.Date
                    && dailyState.Placement == DailyEpgPlacementState.Placed
                    && dailyState.PlannedStart.HasValue
                    && dailyState.PlannedEnd.HasValue
                    && now >= dailyState.PlannedStart.Value
                    && now < dailyState.PlannedEnd.Value)
                {
                    bool alreadyRunning;
                    lock (gate) alreadyRunning = IsDailyExecutorRunningLocked();
                    if (!alreadyRunning && TriggerScheduledDailyNow(dailyState))
                    {
                        log.Add("EPG_SCHEDULER_DUE", "All",
                            $"result=START source=Scheduler.Daily targetScope=All slot={dailyState.PlannedStart:HH:mm:ss}〜{dailyState.PlannedEnd:HH:mm:ss} canonical={dailyState.CanonicalStart:yyyy-MM-dd HH:mm:ss} owner=daily_all rule=scheduled_epg_all_execution_owner_contract");
                    }
                }

                dailyState = GetDailyRunStateSnapshot();
                DateTime next;
                string nextKind;
                if (dailyState is { State: DailyEpgRunPhase.Planned } && dailyState.Date.Date == now.Date)
                {
                    if (dailyState.Placement == DailyEpgPlacementState.Placed
                        && dailyState.PlannedStart.HasValue && dailyState.PlannedStart.Value > now)
                    {
                        next = dailyState.PlannedStart.Value;
                        nextKind = "PLANNED_START";
                    }
                    else if (dailyState.Placement == DailyEpgPlacementState.Unplaced && now < dailyState.MovementDeadline)
                    {
                        next = dailyState.MovementDeadline;
                        nextKind = "MOVEMENT_DEADLINE";
                    }
                    else
                    {
                        next = now.Date.AddDays(1).AddHours(cfg.Hour).AddMinutes(cfg.Minute);
                        nextKind = "NEXT_DAY";
                    }
                }
                else
                {
                    next = now.Date.AddDays(1).AddHours(cfg.Hour).AddMinutes(cfg.Minute);
                    nextKind = "NEXT_DAY";
                }

                var nextLogKey = $"{next:yyyyMMddHHmmss}|{nextKind}";
                if (!string.Equals(lastNextLogKey, nextLogKey, StringComparison.Ordinal)
                    || DateTime.UtcNow - lastNextLogUtc >= TimeSpan.FromMinutes(10))
                {
                    lastNextLogKey = nextLogKey;
                    lastNextLogUtc = DateTime.UtcNow;
                    log.Add("EPG_SCHEDULER_NEXT", "EPG",
                        $"次回の自動判定: {next:yyyy-MM-dd HH:mm:ss} targetScope=All kind={nextKind} planner=all_contiguous_slot movementLimit=3h rule=scheduled_epg_all_movable_slot_contract");
                }
            }

            var waitNow = DateTime.Now;
            var nextEvaluation = ComputeNextDailyEvaluationTime(waitNow, cfg);
            var delay = nextEvaluation <= waitNow
                ? TimeSpan.Zero
                : nextEvaluation - waitNow;

            // 定時EPG schedulerは周期pollingしない。時間到来はabsolute timeout、
            // timeline/config/resume等の状態変化はbinary signalで同じ再評価入口へ収束させる。
            // signalはWait開始前に到着してもSemaphoreSlim内に保持されるため、予約Mutation直後の再計算を失わない。
            // 日付境界を最大待機点に含め、日次責務の切替も取りこぼさない。
            try
            {
                await schedulerWakeSignal.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }


    /// <summary>
    /// 次の意味のある絶対時刻をDailyRunStateから求める。30秒周期pollingは行わず、
    /// PlannedStart / 未配置waveのdeadline / 日付境界のいずれか最短まで待機する。
    /// state変更はSignalSchedulerWakeが待機を中断するため、ここで外部状態を推測しない。
    /// </summary>
    private DateTime ComputeNextDailyEvaluationTime(DateTime now, EpgScheduleConfig cfg)
    {
        var nextDayBoundary = now.Date.AddDays(1);
        if (!cfg.Enabled) return nextDayBoundary;

        var state = GetDailyRunStateSnapshot();
        if (state is null || state.Date.Date != now.Date) return now;
        if (state.State == DailyEpgRunPhase.Finalizing) return now.AddMinutes(1);
        if (IsDailyRunTerminal(state.State)) return nextDayBoundary;
        if (state.State == DailyEpgRunPhase.Running) return nextDayBoundary;

        if (state.Placement == DailyEpgPlacementState.Placed && state.PlannedStart.HasValue)
        {
            if (state.PlannedStart.Value > now) return state.PlannedStart.Value;
            lock (gate)
            {
                if (AnyNormalEpgRunActiveLocked()) return nextDayBoundary;
            }
            return now;
        }

        if (state.Placement == DailyEpgPlacementState.Unplaced)
            return state.MovementDeadline > now ? state.MovementDeadline : now;

        return nextDayBoundary;
    }

    private bool TryGetInteractiveEpgOccupancyBlock(string normalizedScope, DateTime now, out string reason, out DateTime? until, out string blockedGroup, out string owner)
    {
        reason = string.Empty;
        until = null;
        blockedGroup = string.Empty;
        owner = string.Empty;

        var post = TimeSpan.FromSeconds(Math.Max(0, ini.PostEndMarginSeconds));
        var reservations = reservationStore.GetAll();
        var blockers = new List<(string Group, Reservation Reservation, DateTime OccupyStart, DateTime OccupyEnd, int RequiredSeconds)>();
        var requiredSeconds = EstimateEpgRequiredSeconds(normalizedScope);
        if (requiredSeconds <= 0)
        {
            reason = "target_duration_unavailable";
            blockedGroup = normalizedScope;
            owner = "duration_estimator";
            log.Add("EPG_INTERACTIVE_ADMISSION", "BLOCKED",
                $"result=BLOCK targetScope={normalizedScope} reason=target_duration_unavailable action=reject_before_worker_queue rule=epg_duration_single_source_contract");
            return true;
        }
        var epgEnd = now.AddSeconds(requiredSeconds);

        foreach (var group in GroupsForScope(normalizedScope).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var r in reservations)
            {
                if (r.Source == ReservationSource.Epg
                    || !r.IsEnabled
                    || r.IsConflicted
                    || r.Status is ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed
                    || !string.Equals(ResolveReservationGroup(r), group, StringComparison.OrdinalIgnoreCase))
                    continue;

                var normalEpgSafeEnd = EpgRecordingHandoffPolicy.LatestNormalEpgEndBeforeRecording(r.StartTime, ini.PreStartMarginSeconds);
                var occupyEnd = r.EndTime + post;
                if (occupyEnd <= now || normalEpgSafeEnd >= epgEnd)
                    continue;

                blockers.Add((group, r, normalEpgSafeEnd, occupyEnd, requiredSeconds));
            }
        }

        if (blockers.Count == 0)
            return false;

        var first = blockers
            .OrderBy(x => x.OccupyStart)
            .ThenBy(x => x.Reservation.StartTime)
            .ThenBy(x => x.Reservation.Id)
            .First();
        reason = "recording_occupancy_overlap";
        until = blockers.Max(x => x.OccupyEnd);
        blockedGroup = string.Join(",", blockers.Select(x => x.Group).Distinct(StringComparer.OrdinalIgnoreCase));
        owner = $"R{first.Reservation.Id}";

        log.Add("EPG_INTERACTIVE_ADMISSION", "BLOCKED",
            $"result=BLOCK targetScope={normalizedScope} blockedGroup={blockedGroup} requiredSeconds={first.RequiredSeconds} epgWindow={now:MM/dd HH:mm:ss}〜{now.AddSeconds(first.RequiredSeconds):MM/dd HH:mm:ss} " +
            $"recording=R{first.Reservation.Id} normalEpgSafeEnd={first.OccupyStart:MM/dd HH:mm:ss} recordingOccupyEnd={first.OccupyEnd:MM/dd HH:mm:ss} source={first.Reservation.Source} status={first.Reservation.Status} service={SafeValue(first.Reservation.ServiceName)} title={ReservationTitleDisplayContract.ForLog(first.Reservation.Title)} " +
            $"preMarginSec={Math.Max(0, ini.PreStartMarginSeconds)} handoffSafetySec={(int)EpgRecordingHandoffPolicy.SafetyMargin.TotalSeconds} postMarginSec={Math.Max(0, ini.PostEndMarginSeconds)} action=reject_before_worker_queue rule=interactive_epg_occupancy_admission_contract");
        return true;
    }

    private string ResolveReservationGroup(Reservation r)
        => ReservationTunerGroupResolver.Resolve(r, tunerProfiles);

    private int EstimateEpgRequiredSeconds(string targetScope, string? depth = null)
        => EstimateEpgDuration(targetScope, SettingsDefaults.NormalizeEpgDepth(depth ?? _currentDepth)).RequiredSeconds;

    /// <summary>
    /// 手動EPGとScheduled Dailyが共有する必要時間正本。実行時EpgCaptureと同じBuildGroups/Scope filterを使い、
    /// 固定局数・固定TS数へfallbackしない。AllはGR/BSCSを内部で同時進行できるため、各放送グループの
    /// lane critical pathの最大値をrun critical pathとする。
    /// </summary>
    private EpgDurationEstimate EstimateEpgDuration(string targetScope, string depth)
    {
        var normalizedScope = NormalizeTargetScope(targetScope);
        var safetyMarginSeconds = (int)EpgStartEstimateSafetyMargin.TotalSeconds;

        static int[] BuildLaneLoads(IReadOnlyList<int> jobs, int lanes)
        {
            var loads = new int[Math.Max(1, lanes)];
            foreach (var seconds in jobs)
            {
                var lane = 0;
                for (var i = 1; i < loads.Length; i++)
                    if (loads[i] < loads[lane]) lane = i;
                loads[lane] += Math.Max(1, seconds);
            }
            return loads;
        }

        try
        {
            var load = channelLoader.Load();
            var groups = EpgCapture.FilterGroupsByScope(capture.BuildGroups(load.Targets, emitDiagnostics: false), EpgCapture.NormalizeTargetScope(normalizedScope));
            if (groups.Count == 0)
                return new EpgDurationEstimate(0, 0, safetyMarginSeconds, 0, 0, Array.Empty<int>(), "no_explicit_channel_targets");

            var perBroadcastGroup = groups
                .GroupBy(g => NormalizeEpgScheduleGroup(g.Group), StringComparer.OrdinalIgnoreCase)
                .Select(bg =>
                {
                    var tunerCount = Math.Max(0, GetScheduledRecordingTuners(bg.Key).Count);
                    if (tunerCount <= 0)
                        return (Group: bg.Key, TunerCount: 0, Jobs: Array.Empty<int>(), Loads: Array.Empty<int>(), Critical: 0);
                    var jobs = bg
                        .Select(g => EpgDurationPolicy.CreateSchedulePlan(depth, _multiServiceExtraSeconds, g.Targets.Count).RecDurationSeconds)
                        .Where(x => x > 0)
                        .ToArray();
                    var loads = BuildLaneLoads(jobs, tunerCount);
                    return (Group: bg.Key, TunerCount: tunerCount, Jobs: jobs, Loads: loads, Critical: loads.Length == 0 ? 0 : loads.Max());
                })
                .ToArray();

            if (perBroadcastGroup.Any(x => x.TunerCount <= 0))
                return new EpgDurationEstimate(0, 0, safetyMarginSeconds, 0, groups.Count, Array.Empty<int>(), "no_recording_tuner");

            var critical = perBroadcastGroup.Max(x => x.Critical);
            var aggregateLoads = perBroadcastGroup.SelectMany(x => x.Loads).ToArray();
            return new EpgDurationEstimate(
                RequiredSeconds: critical + safetyMarginSeconds,
                CaptureCriticalPathSeconds: critical,
                SafetyMarginSeconds: safetyMarginSeconds,
                TunerCount: perBroadcastGroup.Sum(x => x.TunerCount),
                TsCount: groups.Count,
                LaneLoadsSeconds: aggregateLoads,
                Source: "capture_target_resolver_duration_policy");
        }
        catch (Exception ex)
        {
            log.Add("EPG_DURATION_ESTIMATE", normalizedScope,
                $"result=UNAVAILABLE targetScope={normalizedScope} error={ex.GetType().Name}:{SafeValue(ex.Message)} action=do_not_guess_fixed_ts_count rule=epg_duration_single_source_contract");
            return new EpgDurationEstimate(0, 0, safetyMarginSeconds, 0, 0, Array.Empty<int>(), "target_resolution_unavailable");
        }
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
        if (string.IsNullOrWhiteSpace(reason)) return "recording_occupancy_overlap";
        return reason.Trim();
    }

    // ─── 内部処理 ────────────────────────────────────────────────

    private IReadOnlyList<NormalEpgWaveOccupationSnapshot> BeginNormalEpgWaveOccupation(
        string targetScope,
        string requestedBy,
        bool silent,
        long runGeneration,
        DailyEpgRunState? scheduledPlan = null)
    {
        var now = DateTime.Now;
        var groups = GroupsForScope(targetScope).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new List<NormalEpgWaveOccupationSnapshot>(groups.Length);
        var hasScheduledPlan = scheduledPlan is not null
            && scheduledPlan.State == DailyEpgRunPhase.Running
            && scheduledPlan.PlannedEnd.HasValue;
        var runtimeRequiredSeconds = hasScheduledPlan
            ? Math.Max(1, scheduledPlan!.RequiredSeconds)
            : Math.Max(1, EstimateEpgRequiredSeconds(targetScope));
        foreach (var group in groups)
        {
            var requiredSeconds = runtimeRequiredSeconds;
            var plannedEndAt = hasScheduledPlan
                ? scheduledPlan!.PlannedEnd!.Value
                : now.AddSeconds(requiredSeconds);
            var snapshot = normalEpgWaveOccupation.Set(
                group,
                now,
                plannedEndAt,
                requestedBy,
                silent,
                runGeneration);
            result.Add(snapshot);
            log.Add("EPG_WAVE_OCCUPATION", group,
                $"result=ACQUIRED group={group} occupy={snapshot.StartedAt:MM/dd HH:mm:ss}〜{snapshot.PlannedEndAt:MM/dd HH:mm:ss} requiredSeconds={requiredSeconds} source={requestedBy} silent={silent} runGeneration={runGeneration} scope=normal_epg logicalOnly=True physicalLeaseMutation=False planOwner={(hasScheduledPlan ? "daily_run_state" : "runtime_estimate")} rule=normal_epg_wave_occupation_contract");
        }
        return result;
    }

    private async Task WaitForPhysicalTailAndReleaseAsync(
        long runGeneration,
        string captureRunId,
        string requestedBy,
        string targetScope,
        bool silent,
        CancellationToken cancellationToken)
    {
        var lastSignature = string.Empty;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ownedByGroup = capture.GetRunOwnedPhysicalTuners(captureRunId);
            if (ownedByGroup.Count == 0)
                return;

            var signature = string.Join("|", ownedByGroup
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"{x.Key}:{string.Join(",", x.Value)}"));

            if (!string.Equals(signature, lastSignature, StringComparison.Ordinal))
            {
                lastSignature = signature;
                var changed = false;
                foreach (var group in GroupsForScope(targetScope))
                {
                    if (!ownedByGroup.TryGetValue(group, out var tailTuners) || tailTuners.Count == 0)
                    {
                        // This wave has fully converged while another wave in the same run still owns a tail.
                        // Remove only this group; never keep a tail-free wave blocked by the other group.
                        changed |= normalEpgWaveOccupation.Clear(group, runGeneration);
                        continue;
                    }

                    if (normalEpgWaveOccupation.TryTransitionToPhysicalTail(group, runGeneration, tailTuners, out var snapshot))
                    {
                        changed = true;
                        log.Add("EPG_WAVE_OCCUPATION", group,
                            $"result=TAIL_MODE group={group} tuners={string.Join(",", snapshot.PhysicalTailTuners)} runGeneration={runGeneration} revision={snapshot.Revision} source={requestedBy} silent={silent} rule=normal_epg_physical_tail_contract");
                    }
                }

                if (changed)
                {
                    try
                    {
                        allocationRoute.Run(new ReservationAllocationRouteRequest(
                            Source: "EpgScheduler",
                            Action: "NormalEpgPhysicalTailChanged",
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
                            $"result=WARN action=NormalEpgPhysicalTailChanged runGeneration={runGeneration} error={ex.GetType().Name}:{ex.Message} rule=normal_epg_physical_tail_contract");
                    }
                }
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool EndNormalEpgWaveOccupation(long runGeneration, string reason)
    {
        var owned = normalEpgWaveOccupation.Snapshot()
            .Where(x => x.RunGeneration == runGeneration)
            .ToArray();
        if (owned.Length == 0) return false;
        normalEpgWaveOccupation.ClearRun(runGeneration);
        foreach (var snapshot in owned)
        {
            log.Add("EPG_WAVE_OCCUPATION", snapshot.Group,
                $"result=RELEASED group={snapshot.Group} source={snapshot.Source} silent={snapshot.Silent} runGeneration={runGeneration} reason={reason} releasedAt={DateTime.Now:MM/dd HH:mm:ss} rule=normal_epg_wave_occupation_contract");
        }
        return true;
    }

    private void ReevaluateAfterNormalEpgWaveTerminal(string requestedBy, string targetScope, bool silent, string terminalReason)
    {
        // NORMAL_EPG_TERMINAL_REALLOCATION_INVARIANT:
        // normal EPG waveの終了理由は Complete / ExplicitCancel / RealFailure のどれでも、
        // 録画側から見れば「共通の占有要因が消えた」という同じ意味である。
        // Immediate/Manual等の入口や終了理由ごとの復活処理は持たず、
        // wave解放後は必ず共通Allocation正本へ戻して競合を再計算する。
        // 正常完了時だけは直後のEpgCompletePostImportがAllocation/PreRec/Wakeの最終所有者になる。
        // 明示キャンセル・実失敗には後続所有者がないため、このterminal route自身がWakeまで確定する。
        var refreshWake = !string.Equals(terminalReason, "complete", StringComparison.OrdinalIgnoreCase);
        try
        {
            allocationRoute.Run(new ReservationAllocationRouteRequest(
                Source: "EpgScheduler",
                Action: "NormalEpgWaveTerminal",
                RunKeywordMatcher: false,
                SyncProgramRuleReservations: false,
                ReevaluateAllocations: true,
                RefreshPreRecordEpgEntries: false,
                RefreshWakeTask: refreshWake,
                EmitConflictLogs: false,
                ExecutionMode: silent ? "SilentEpg" : "VisibleEpg"));
            log.Add("EPG_ALLOC_ROUTE", "NormalEpgWaveTerminal",
                $"result=OK source={requestedBy} targetScope={targetScope} silent={silent} terminalReason={terminalReason} refreshWake={refreshWake} wakeOwner={(refreshWake ? "terminal_route" : "epg_complete_post_import")} action=common_reallocation_after_wave_release rule=normal_epg_wave_occupation_contract");
        }
        catch (Exception ex)
        {
            log.Add("EPG_ALLOC_ROUTE", "WARN",
                $"result=WARN source={requestedBy} targetScope={targetScope} silent={silent} terminalReason={terminalReason} action=common_reallocation_after_wave_release error={ex.GetType().Name}:{ex.Message} rule=normal_epg_wave_occupation_contract");
        }
    }

    private async Task RunWithGuardAsync(CancellationToken ct, string targetScope, string requestedBy, bool silent, long runGeneration, IDisposable sleepLease, DailyEpgRunState? scheduledPlan = null, string? runDepthOverride = null)
    {
        var waveOccupationActive = false;
        var runDepth = SettingsDefaults.NormalizeEpgDepth(runDepthOverride ?? _currentDepth);
        var captureRunId = $"normal-epg-{runGeneration}-{targetScope}";
        try
        {
            var isDailyRun = string.Equals(requestedBy, "Scheduler.Daily", StringComparison.OrdinalIgnoreCase);

            var routeAction = BuildManualEpgRouteAction(requestedBy, silent);
            BeginNormalEpgWaveOccupation(targetScope, requestedBy, silent, runGeneration, scheduledPlan);
            waveOccupationActive = true;
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
                    $"result=WARN source={requestedBy} action={routeAction} silent={silent} targetScope={targetScope} error={ex.GetType().Name}:{ex.Message} rule=release_contract");
            }

            log.Add("EPG_PIPELINE_AUDIT", "RunWithGuard",
                $"source={requestedBy} silent={silent} uiMode={(silent ? "Silent" : "Visible")} targetScope={targetScope} depth={runDepth} sameCapturePipeline=True commonRouteAction={routeAction} runScopedActivityKeeper=False activityKeeperTvTest=False rule=release_contract");
            var captureResult = await capture.RunAsync(
                ct,
                targetScope,
                runDepth,
                showProgress: !silent,
                runId: captureRunId,
                manageStatus: !isDailyRun).ConfigureAwait(false);
            await WaitForPhysicalTailAndReleaseAsync(
                runGeneration,
                captureRunId,
                requestedBy,
                targetScope,
                silent,
                ct).ConfigureAwait(false);
            var waveReleasedOnComplete = EndNormalEpgWaveOccupation(runGeneration, "capture_terminal");
            waveOccupationActive = false;
            if (waveReleasedOnComplete)
                ReevaluateAfterNormalEpgWaveTerminal(requestedBy, targetScope, silent, "complete");
            ct.ThrowIfCancellationRequested();
            var isFullDailySuccess = isDailyRun
                && string.Equals(targetScope, "All", StringComparison.OrdinalIgnoreCase)
                && captureResult.Success
                && string.Equals(captureResult.RunResult, "OK", StringComparison.OrdinalIgnoreCase);

            var hasUsableImportThisRun = string.Equals(captureResult.RunResult, "OK", StringComparison.OrdinalIgnoreCase)
                || captureResult.CompletedGroups > 0
                || captureResult.ImportedEvents > 0;

            if (isDailyRun)
            {
                if (isFullDailySuccess)
                {
                    CommitDailyCompleted(DateTime.Now);
                }
                else
                {
                    CommitDailyFailed(DateTime.Now);
                    var failedState = GetDailyRunStateSnapshot();
                    if (failedState is not null)
                        EnsureDailyProjection(failedState, refreshAllocationRoute: false);
                    EnsureTomorrowAfterDailyTerminal(failedState, "DailyCaptureFailed");
                    log.Add("EPG_SCHEDULED_DAILY_TERMINAL", "All",
                        $"result=FAILED source={requestedBy} targetScope=All completed={captureResult.CompletedGroups}/{captureResult.TotalGroups} missingGroups={captureResult.MissingGroups} missingScopes={captureResult.MissingScopes} action=no_retry_no_partial_continuation rule=scheduled_epg_all_execution_owner_contract");
                }
            }

            // Daily成功時はFinalizingへ進んだ後、当日SystemDailyEpg行を完了化してから翌日責務Ensureへ進む。
            DateTime? fullDailyCompletedAt = null;
            if (isDailyRun && isFullDailySuccess)
            {
                var completedAt = DateTime.Now;
                try
                {
                    var completedEntries = reservationStore.CompleteDueDailyEpgScheduleEntries(completedAt);
                    fullDailyCompletedAt = completedAt;
                    log.Add("EPG_SCHEDULER", "EpgEntry",
                        $"result=FINALIZING dueDailyEntries={completedEntries} completedAt={completedAt:yyyy-MM-dd HH:mm:ss} source={requestedBy} targetScope=All action=ensure_tomorrow_before_succeeded rule=daily_epg_single_state_contract");
                }
                catch (Exception ex)
                {
                    isFullDailySuccess = false;
                    log.Add("EPG_SCHEDULER", "EpgEntry",
                        $"result=FINALIZING_PROJECTION_FAILED completedAt={completedAt:yyyy-MM-dd HH:mm:ss} error={ex.GetType().Name}:{ex.Message} action=keep_finalizing_no_success_publish rule=daily_epg_single_state_contract");
                }
            }

            // ユーザー向けEPG終端はcapture/import単位の公開通知。Daily全体のSucceededとは別契約であり、
            // Daily All成功時は当日行の完了化を確定してからOKを公開する。失敗時はそのAll capture結果を公開する。
            if (isDailyRun && isFullDailySuccess)
            {
                userEvents.AddScheduledEpgCompleted(
                    targetScope, requestedBy, silent, "OK",
                    captureResult.CompletedGroups, captureResult.TotalGroups, captureResult.ImportedEvents,
                    captureResult.MissingGroups, captureResult.Detail);
            }
            else if (!isDailyRun)
            {
                userEvents.AddScheduledEpgCompleted(
                    targetScope, requestedBy, silent, captureResult.RunResult,
                    captureResult.CompletedGroups, captureResult.TotalGroups, captureResult.ImportedEvents,
                    captureResult.MissingGroups, captureResult.Detail);
            }
            else if (isDailyRun)
            {
                userEvents.AddScheduledEpgCompleted(
                    targetScope, requestedBy, silent, captureResult.RunResult,
                    captureResult.CompletedGroups, captureResult.TotalGroups, captureResult.ImportedEvents,
                    captureResult.MissingGroups, captureResult.Detail);
            }

            // 型付きEPGイベントもcapture/importの公開終端であり、Daily lifecycleのSucceededとは分離する。
            // Daily All成功時だけ当日行完了化を先に確定し、失敗時は当該capture結果をそのまま公開する。
            var typedResult = isDailyRun && isFullDailySuccess
                ? "OK"
                : captureResult.RunResult;
            var typedEventType = string.Equals(typedResult, "OK", StringComparison.OrdinalIgnoreCase)
                ? TvAirEventType.EpgCompleted
                : TvAirEventType.EpgFailed;
            typedEvents.Publish(new TvAirEventDto
            {
                EventType = typedEventType,
                EntityId = $"epg:{requestedBy}:{DateTimeOffset.Now:yyyyMMddHHmmssfff}",
                Details = new Dictionary<string, string>
                {
                    ["source"] = requestedBy,
                    ["targetScope"] = targetScope,
                    ["silent"] = silent.ToString(),
                    ["result"] = typedResult,
                    ["completedGroups"] = captureResult.CompletedGroups.ToString(),
                    ["totalGroups"] = captureResult.TotalGroups.ToString(),
                    ["importedEvents"] = captureResult.ImportedEvents.ToString(),
                    ["completedScopes"] = captureResult.CompletedScopes,
                    ["missingGroups"] = captureResult.MissingGroups.ToString(),
                    ["missingScopes"] = captureResult.MissingScopes,
                    ["detail"] = captureResult.Detail ?? string.Empty
                }
            });

            var postImportRequired = isDailyRun
                ? isFullDailySuccess
                : hasUsableImportThisRun;

            // EPG取り込み後の共通ルートは1回だけ実行する。
            // Scheduler.Daily はDaily All成功時だけ、手動/プラグイン取得は今回ランに有効取込がある場合だけ通す。
            if (!ct.IsCancellationRequested && postImportRequired)
            {
                // マッチング前に期限切れルールと、そのルール由来のScheduled予約を
                // 同一Transactionで物理削除し、EPG取込後の整合処理へ渡す。
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

                // release_contract: EPG取り込み後の縦串順序を固定する。
                // 先に既存EventIdentity予約を最新EITへ追従させ、その後に自動検索/ProgramRule/割当/PreRecEpg/Wakeを
                // 1本の共通割当ルートで通す。古い時刻のまま一度割当評価してから追従する二段評価を避ける。
                var timeFollowUpdated = 0;
                try
                {
                    timeFollowUpdated = ApplyTimeFollowingWithAudit(requestedBy, targetScope, reevaluateOnUpdated: false);
                }
                catch (Exception ex)
                {
                    log.Add("EPG_SCHEDULER", "TimeFollow", $"時間追従エラー: {ex.Message} rule=release_contract");
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
                    log.Add("EPG_SCHEDULER", "PostImportRoute", $"result=OK timeFollowUpdated={timeFollowUpdated} route=ALLOC_ROUTE matcher=True syncProgram=True reevaluate=True preRec=True wake=True rule=release_contract");
                }
                catch (Exception ex)
                {
                    log.Add("EPG_SCHEDULER", "PostImportRoute", $"result=ERROR timeFollowUpdated={timeFollowUpdated} error={ex.Message} rule=release_contract");
                }

            }

            if (isDailyRun && isFullDailySuccess && fullDailyCompletedAt.HasValue)
            {
                // DailyRunState.Succeededは翌日責務のEnsureまで完了した日次ライフサイクル終端。
                // EpgCompleted（capture/import公開終端）とは意味を混同しない。
                TryFinalizeDailySucceeded(fullDailyCompletedAt.Value);
            }
        }
        catch (OperationCanceledException)
        {
            var waveReleasedOnCancel = false;
            bool shutdownCancellation;
            lock (gate) shutdownCancellation = isStopping;
            var msg = $"EPG取得がキャンセルされました。source={requestedBy} silent={silent} targetScope={targetScope}";

            // worker 停止・Activity解放・EPG lease解放が落ち着くまでは、
            // 通常キャンセルでも終了キャンセルでも run を完了扱いにしない。
            try
            {
                await capture.WaitForCancellationQuiescenceAsync(requestedBy, silent, targetScope, captureRunId);
            }
            catch (Exception ex)
            {
                log.Add("EPG_CANCEL_RELEASE_WAIT", "WARN",
                    $"result=WARN source={requestedBy} silent={silent} targetScope={targetScope} shutdown={shutdownCancellation} error={ex.GetType().Name}:{ex.Message} action=continue_before_epg_run_end rule=release_contract");
            }

            if (waveOccupationActive)
            {
                // Explicit cancel keeps logical ownership until the exact run-owned physical tail converges.
                // During host shutdown new admissions are already closed, so teardown owns the residual cleanup;
                // do not publish a false logical release while the physical owner may still exist.
                if (!shutdownCancellation)
                {
                    await WaitForPhysicalTailAndReleaseAsync(
                        runGeneration,
                        captureRunId,
                        requestedBy,
                        targetScope,
                        silent,
                        CancellationToken.None).ConfigureAwait(false);
                    waveReleasedOnCancel = EndNormalEpgWaveOccupation(runGeneration, "cancelled");
                    waveOccupationActive = false;
                }
                else
                {
                    log.Add("EPG_WAVE_OCCUPATION", targetScope,
                        $"result=RETAINED_FOR_HOST_TEARDOWN runGeneration={runGeneration} reason=shutdown_cancel physicalOwnerMayRemain=True rule=normal_epg_wave_occupation_contract");
                }
            }

            log.Add("EPG_RUN_END", "CANCELLED",
                $"result=CANCELLED source={requestedBy} silent={silent} targetScope={targetScope} shutdown={shutdownCancellation} releaseComplete=True rule=release_contract");
            if (!string.Equals(requestedBy, "Scheduler.Daily", StringComparison.OrdinalIgnoreCase))
            {
                capture.SetStatus(st => st with
                {
                    Phase = "cancelled",
                    CompletedGroups = 0,
                    LastRunAt = DateTime.Now,
                    LastRunMessage = shutdownCancellation ? "終了処理でキャンセル" : "キャンセル済み",
                    UiVisible = !silent,
                    UiMode = silent ? "Silent" : "Visible",
                    CancelRoute = silent ? "SilentTray" : "VisibleWidget"
                });
            }

            if (shutdownCancellation)
            {
                // アプリ終了中に予約再評価・Wake更新などの新しい後処理を開始しない。
                log.Add("EPG_RUN_CANCELLED", "EPG",
                    $"{msg} shutdown=True action=skip_events_and_allocation_route rule=epg_scheduler_shutdown_contract");
            }
            else
            {
                var publishCancelTerminal = true;
                var publishedScope = targetScope;
                if (string.Equals(requestedBy, "Scheduler.Daily", StringComparison.OrdinalIgnoreCase))
                {
                    publishCancelTerminal = MarkDailyCancelConverged();
                    if (publishCancelTerminal)
                    {
                        publishedScope = "All";
                        CommitDailyCancelled(DateTime.Now);
                        var cancelledState = GetDailyRunStateSnapshot();
                        if (cancelledState is not null)
                            EnsureDailyProjection(cancelledState, refreshAllocationRoute: false);
                        EnsureTomorrowAfterDailyTerminal(cancelledState, "DailyCancelled");
                        // Daily Cancelは部分終端を持たず、当日全体のterminalとしてAllで一度だけ公開する。
                        log.Add("EPG_SCHEDULED_DAILY_TERMINAL", "All",
                            $"result=CANCELLED source={requestedBy} targetScope=All action=daily_zero_reset_after_physical_convergence rule=daily_epg_cancel_contract");
                    }
                }

                if (publishCancelTerminal)
                {
                    userEvents.AddScheduledEpgCancelled(publishedScope, requestedBy, silent);
                    typedEvents.Publish(new TvAirEventDto { EventType = TvAirEventType.EpgCancelled, EntityId = $"epg:{requestedBy}:{DateTimeOffset.Now:yyyyMMddHHmmssfff}", Details = new Dictionary<string, string> { ["source"] = requestedBy, ["targetScope"] = publishedScope, ["silent"] = silent.ToString() } });
                    log.Add("EPG_RUN_CANCELLED", "EPG", msg);
                    if (waveReleasedOnCancel)
                        ReevaluateAfterNormalEpgWaveTerminal(requestedBy, publishedScope, silent, "explicit_cancel");
                }
            }
        }
        catch (Exception ex)
        {
            var waveReleasedOnFailure = false;
            if (waveOccupationActive)
            {
                bool stopping;
                lock (gate) stopping = isStopping;
                if (!stopping)
                {
                    await WaitForPhysicalTailAndReleaseAsync(
                        runGeneration,
                        captureRunId,
                        requestedBy,
                        targetScope,
                        silent,
                        CancellationToken.None).ConfigureAwait(false);
                    waveReleasedOnFailure = EndNormalEpgWaveOccupation(runGeneration, "failed");
                    waveOccupationActive = false;
                }
                else
                {
                    log.Add("EPG_WAVE_OCCUPATION", targetScope,
                        $"result=RETAINED_FOR_HOST_TEARDOWN runGeneration={runGeneration} reason=shutdown_failure physicalOwnerMayRemain=True rule=normal_epg_wave_occupation_contract");
                }
            }
            if (string.Equals(requestedBy, "Scheduler.Daily", StringComparison.OrdinalIgnoreCase))
            {
                // Daily terminalの公開は必ずphysical convergence後。物理ownerが残る状態を空きとして公開しない。
                CommitDailyFailed(DateTime.Now);
                var failedState = GetDailyRunStateSnapshot();
                if (failedState is not null)
                    EnsureDailyProjection(failedState, refreshAllocationRoute: false);
                EnsureTomorrowAfterDailyTerminal(failedState, "DailyExceptionFailed");
                log.Add("EPG_SCHEDULED_DAILY_TERMINAL", "All",
                    $"result=FAILED source={requestedBy} targetScope=All error={ex.GetType().Name}:{ex.Message} action=no_retry_after_physical_convergence rule=scheduled_epg_all_execution_owner_contract");
            }
            if (waveReleasedOnFailure)
                ReevaluateAfterNormalEpgWaveTerminal(requestedBy, targetScope, silent, "real_failure");
            userEvents.AddScheduledEpgFailed(targetScope, requestedBy, silent, $"{ex.GetType().Name}: {ex.Message}");
            typedEvents.Publish(new TvAirEventDto { EventType = TvAirEventType.EpgFailed, EntityId = $"epg:{requestedBy}:{DateTimeOffset.Now:yyyyMMddHHmmssfff}", Details = new Dictionary<string, string> { ["source"] = requestedBy, ["targetScope"] = targetScope, ["silent"] = silent.ToString(), ["exceptionType"] = ex.GetType().Name, ["message"] = ex.Message } });
            log.Add("EPG_RUN_ERROR", "EPG", $"EPG取得中に例外が発生しました: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (waveOccupationActive)
            {
                bool stopping;
                lock (gate) stopping = isStopping;
                if (!stopping)
                {
                    // finally must never turn an unknown physical tail into logical free capacity.
                    // If an earlier projection/error path escaped before terminal convergence, finish the exact run-owned tail here.
                    try
                    {
                        await WaitForPhysicalTailAndReleaseAsync(
                            runGeneration,
                            captureRunId,
                            requestedBy,
                            targetScope,
                            silent,
                            CancellationToken.None).ConfigureAwait(false);
                        EndNormalEpgWaveOccupation(runGeneration, "finally_after_physical_convergence");
                        waveOccupationActive = false;
                    }
                    catch (Exception ex)
                    {
                        log.Add("EPG_WAVE_OCCUPATION", targetScope,
                            $"result=RETAINED reason=finally_physical_convergence_failed runGeneration={runGeneration} error={ex.GetType().Name}:{ex.Message} physicalOwnerMayRemain=True rule=normal_epg_wave_occupation_contract");
                    }
                }
                else
                {
                    log.Add("EPG_WAVE_OCCUPATION", targetScope,
                        $"result=RETAINED_FOR_HOST_TEARDOWN runGeneration={runGeneration} reason=finally_shutdown physicalOwnerMayRemain=True rule=normal_epg_wave_occupation_contract");
                }
            }
            try
            {
                // キャンセル・例外時もPhaseをidleに戻す（正常完了時はRunAsync内で既にidleにしている）
                if (!string.Equals(requestedBy, "Scheduler.Daily", StringComparison.OrdinalIgnoreCase))
                {
                    capture.SetStatus(s => s.Phase == "running"
                        ? s with { Phase = ct.IsCancellationRequested ? "cancelled" : "idle", UiVisible = !silent, UiMode = silent ? "Silent" : "Visible", CancelRoute = silent ? "SilentTray" : "VisibleWidget" }
                        : s);
                }
                lock (gate)
                {
                    if (string.Equals(requestedBy, "Scheduler.Daily", StringComparison.OrdinalIgnoreCase))
                    {
                        if (activeDailyExecutor is not null && activeDailyExecutor.Generation == runGeneration)
                        {
                            activeDailyExecutor.Cts.Dispose();
                            activeDailyExecutor = null;
                        }
                    }
                    else if (runCts is not null && runCts.Token == ct)
                    {
                        // 同じmanual runだけが自身の状態を片付ける。
                        isRunning = false;
                        pendingRunSource = null;
                        pendingRunSilent = false;
                        activeRunTask = null;
                        currentRunGeneration = 0;
                        runCts.Dispose();
                        runCts = null;
                    }
                }

                // Normal EPG ownerの消滅はDailyの開始可否を変える状態変化。
                // 周期pollingには戻さず、同じscheduler invalidate入口を起こす。
                if (!isStopping)
                    SignalSchedulerWake();
            }
            finally
            {
                // runの公開終端・daily commit・post-import route・状態片付けがすべて終わった後にだけ解放する。
                // TvAIrEpgRecが先に全終了しても、ここへ到達するまではOSのSystemRequiredを保持する。
                sleepLease.Dispose();
            }
        }
    }


    /// <summary>秒数を次の1分境界に切り上げる。40秒→1分、120秒→2分。</summary>
    private static int CeilToMinutes(int seconds)
        => (int)Math.Ceiling(seconds / 60.0);

    /// <summary>
    /// DEVELOPER_APPROVAL_REQUIRED: System EPG / PreRec責務生成は保護領域。
    /// 事前の開発者明示承認なしに、ON/OFF組合せ、Daily fallback、PreRec置換、6物理録画Tuner責務の契約を変更しない。
    /// 「次」「続けて」「進めて」等は変更承認ではない。他案件の修正に便乗して触らない。
    /// 設定された「○分前EPG確認」を、録画用Tunerごとの直近対象予約へ登録する。
    ///
    /// PRE_RECORD_EPG_INDEPENDENT_SCHEDULE_INVARIANT:
    /// - 定時EPG取得と録画前EPG確認は別責務であり、定時EPGの有効状態・時刻・結果で代替、省略、差し替えしない。
    /// - 対象は番組表予約・自動検索予約・キーワード予約。
    /// - ユーザー明示チェーンはroot親だけを対象とし、子は親の確認結果から系列時間追従を受ける。
    /// - 視聴用Tunerは使用しない。実行時に録画用Tunerの空きがなければ明示例外として実行しない。
    /// - 現在のTuner空きや同一局録画中といった再構築時点の一過性状態で、将来の確認予約を生成抑止しない。
    /// </summary>
    public bool RefreshPreRecordEpgEntries()
    {
        try
        {
            return UpsertPreRecordEpgEntries();
        }
        catch (Exception ex)
        {
            log.Add("EPG_SCHEDULER", "PreRecEpgRefresh", $"直前EPG確認エントリ再構築失敗: {ex.Message}");
            throw;
        }
    }

    private bool UpsertPreRecordEpgEntries()
    {
        if (ini.EpgPreRecordMinutes <= 0) return false;

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
                log.Add("TUNER_PROTECT", "Viewing", $"tuner={tuner.Name} role=Viewing skipped_from=PreRecEpg deletedScheduledSystemEpg={deleted}");
            }
        }

        var recordableTuners = tunerProfiles
            .Where(t => !string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var preMin = SettingsDefaults.ResolveEnabledEpgPreRecordMinutes(ini.EpgPreRecordMinutes);
        var waitSec = CurrentWaitSec;
        var durMin  = Math.Max(1, CeilToMinutes(waitSec));
        var now     = DateTime.Now;

        // ユーザー予約のみを開始時刻順に取得。物理Tuner割当はPreRec Intent生成条件にしない。
        // Manual は番組表からの通常予約であり、録画前EPG確認の正式対象。
        // 無効(IsEnabled=false)と競合(IsConflicted=true)予約はEPG確認対象外
        // (録画されない予約のためにEPGセッションを発生させる意味がない)
        var staleCleanup = reservationStore.DeleteInvalidScheduledPreRecordEpgEntries();
        if (staleCleanup.Deleted > 0)
        {
            log.Add("PRE_REC_EPG_PARENT_CLEANUP", "Summary",
                $"result=DELETED deleted={staleCleanup.Deleted} parentMissing={staleCleanup.ParentMissing} parentDisabled={staleCleanup.ParentDisabled} parentTerminal={staleCleanup.ParentTerminal} parentConflicted={staleCleanup.ParentConflicted} parentUserChainChild={staleCleanup.ParentUserChainChild} parents=[{staleCleanup.ParentIds}] action=remove_orphan_or_non_recordable_prerec_epg rule=release_contract");
        }

        var scheduledAll = reservationStore.GetByStatus(ReservationStatus.Scheduled).ToList();
        var userScheduled = scheduledAll
            // SystemEpg/PreRecEpg自身は親候補にしない。Programは時刻枠録画であり対象外。
            // PRE_RECORD_EPG_CHAIN_ROOT_INVARIANT:
            // ユーザー明示チェーンはroot親だけを対象にする。子は親の確認結果から系列時間追従を受け、
            // 子ごとの独立確認workerを作らない。is_user_chainだけで除外するとroot親まで失うため、previous有無で判定する。
            .Where(r => (r.Source == ReservationSource.Manual || r.Source == ReservationSource.KeywordSearch || r.Source == ReservationSource.Keyword)
                     && (!r.IsUserChain || !r.UserChainPreviousId.HasValue)
                     && r.IsEnabled
                     && !r.IsConflicted)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();
        // 監査用の除外件数は、PreRecEpgの親候補になり得る予約Sourceだけを母集団にし、
        // 一つの予約を複数理由へ重複計上しない。生成判定そのものは上のWhere条件を正本とする。
        var preRecordSourceCandidates = scheduledAll
            .Where(r => r.Source == ReservationSource.Manual
                     || r.Source == ReservationSource.KeywordSearch
                     || r.Source == ReservationSource.Keyword)
            .ToList();
        var skippedUserChainChild = preRecordSourceCandidates.Count(r => r.IsUserChain && r.UserChainPreviousId.HasValue);
        var skippedDisabled = preRecordSourceCandidates.Count(r => !r.IsEnabled
            && !(r.IsUserChain && r.UserChainPreviousId.HasValue));
        var skippedConflicted = preRecordSourceCandidates.Count(r => r.IsEnabled
            && (!r.IsUserChain || !r.UserChainPreviousId.HasValue)
            && r.IsConflicted);
        var excludedSource = scheduledAll.Count(r => r.Source != ReservationSource.Epg
            && r.Source != ReservationSource.Manual
            && r.Source != ReservationSource.KeywordSearch
            && r.Source != ReservationSource.Keyword);
        var epgEntryMutationCount = preRecordRouteMutationCount + staleCleanup.Deleted;

        if (userScheduled.Count == 0)
        {
            var deletedObsolete = reservationStore.DeleteScheduledPreRecordEpgEntriesExceptParents(Array.Empty<int>());
            epgEntryMutationCount += deletedObsolete;
            log.Add("EPG_SCHEDULER", "PreRecEpg",
                $"result=NO_TARGETS candidates=0 sourceCandidates={preRecordSourceCandidates.Count} excludedSource={excludedSource} skippedDisabled={skippedDisabled} skippedConflicted={skippedConflicted} manualIncluded=True skippedUserChainChild={skippedUserChainChild} countsExclusive=True deletedObsolete={deletedObsolete} action=remove_prerec_only_daily_epg_untouched rule=pre_record_epg_independent_schedule_contract");
            return epgEntryMutationCount > 0;
        }

        string ResolveGroup(Reservation r)
            => ReservationTunerGroupResolver.Resolve(r, recordableTuners);

        // PreRecの必要性そのものを定時EPG結果で代替しない。
        // System責務の「直近」は二軸で扱う。
        // 1) 現在から3時間以内の予約イベント群。
        // 2) その時間軸候補が一件も無い場合だけ、予約リスト全体の先頭1件（10時間先でも可）。
        // 各物理録画Tunerごとの未来先頭を独立に先取りしてはならない。

        // SYSTEM_EPG_RESPONSIBILITY_PLAN_SINGLE_SOURCE_INVARIANT:
        // PreRec Intent生成も予約一覧投影も同じSystemEpgResponsibilityPlanServiceを正本とする。
        // Daily ON時はDailyを基本状態/収束先とし、上記直近PreRecが載るTunerだけ一時的に置換する。
        // PriorityNameは親予約の現在Allocation結果であり、System側で物理Tunerを独自固定しない。
        var responsibilityPlan = systemEpgResponsibilityPlan.Build(reservationStore.GetAll(), now);
        var selectedParentIdsSet = responsibilityPlan.ParentIds;
        var responsibilityByParent = responsibilityPlan.PreRecordResponsibilities
            .GroupBy(x => x.ParentReservationId)
            .ToDictionary(g => g.Key, g => g.First());
        var responsibilityMap = string.Join(',', responsibilityPlan.PreRecordResponsibilities
            .OrderBy(x => x.PriorityName, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{SafeValue(x.PriorityName)}:R{x.ParentReservationId}"));
        var preRecordPriorityNames = responsibilityPlan.PreRecordResponsibilities
            .Where(x => !string.IsNullOrWhiteSpace(x.PriorityName))
            .Select(x => x.PriorityName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dailyFallbackMap = ini.EpgEnabled
            ? string.Join(',', responsibilityPlan.RecordingTuners
                .Where(t => !preRecordPriorityNames.Contains(t.Name))
                .Select(t => t.Name)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            : string.Empty;
        var intentParents = userScheduled
            .Where(r => selectedParentIdsSet.Contains(r.Id))
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        // 現在の次責務に含まれないScheduled PreRec子だけを削除する。Completed証拠と定時EPG行には触れない。
        var selectedParentIds = intentParents.Select(r => r.Id).ToArray();
        var deletedObsoleteChildren = reservationStore.DeleteScheduledPreRecordEpgEntriesExceptParents(selectedParentIds);
        epgEntryMutationCount += deletedObsoleteChildren;

        var registeredCount = 0;
        var skippedExpiredDeadline = 0;
        var deletedExpiredDeadline = 0;
        var dedupeReuseCount = 0;
        var dedupeKeepScheduledCount = 0;
        var dedupeTunerReboundCount = 0;
        var dedupeCompletedOrTerminalCount = 0;
        var dedupeParentIds = new SortedSet<int>();
        foreach (var r in intentParents)
        {
            var epgStart = r.StartTime.AddMinutes(-preMin);
            var epgEnd   = epgStart.AddMinutes(durMin);

            // release_contract:
            // epgStartを過ぎた後の再構築は「新規PreRecEpgを今から作らない」ための締切であり、
            // すでにScheduled/Recordingとして登録済みの子予約を実行窓の途中で削除する締切ではない。
            // ProgramGuide/ExternalEpg/割当の再投影は実行窓内にも発生するため、既存子予約はepgEndまで保持し、
            // ReservationSchedulerのdue走査と実行証拠判定へ引き渡す。ここで削除するとAdmission前に消失する。
            if (epgStart <= now && epgEnd > now
                && reservationStore.TryFindReusablePreRecordEpgEntry(
                    r.Id,
                    out var activeWindowPreRecId,
                    out var activeWindowPreRecStatus,
                    out var activeWindowPreRecTuner,
                    out var activeWindowPreRecDataVersion)
                && activeWindowPreRecStatus is ReservationStatus.Scheduled or ReservationStatus.Recording)
            {
                var reboundApplied = true;
                var tunerRebound = false;
                if (activeWindowPreRecStatus == ReservationStatus.Scheduled)
                {
                    reboundApplied = reservationStore.RebindScheduledPreRecordEpgEntry(
                        activeWindowPreRecId,
                        activeWindowPreRecDataVersion,
                        r.Id,
                        epgStart,
                        epgEnd);
                    if (reboundApplied)
                    {
                        epgEntryMutationCount++;
                        tunerRebound = !string.IsNullOrWhiteSpace(activeWindowPreRecTuner);
                    }
                }

                if (!reboundApplied)
                {
                    log.Add("PRE_REC_EPG_ACTIVE_WINDOW", $"R{r.Id}",
                        $"result=REBIND_CAS_REJECTED existing=R{activeWindowPreRecId} existingStatus={activeWindowPreRecStatus} parent=R{r.Id} dataVersion={activeWindowPreRecDataVersion} action=keep_latest_child_and_retry_on_next_evaluation rule=pre_record_epg_active_window_preservation_contract");
                    continue;
                }

                dedupeReuseCount++;
                dedupeKeepScheduledCount++;
                if (tunerRebound) dedupeTunerReboundCount++;
                dedupeParentIds.Add(r.Id);
                log.Add("PRE_REC_EPG_ACTIVE_WINDOW", $"R{r.Id}",
                    $"result=PRESERVED existing=R{activeWindowPreRecId} existingStatus={activeWindowPreRecStatus} parent=R{r.Id} tuner=runtime_selection oldTuner={SafeValue(activeWindowPreRecTuner)} window={epgStart:MM/dd HH:mm:ss}〜{epgEnd:MM/dd HH:mm:ss} now={now:MM/dd HH:mm:ss} tunerRebound={tunerRebound} action=leave_due_entry_for_reservation_scheduler rule=pre_record_epg_active_window_preservation_contract");
                continue;
            }

            if (epgStart <= now)
            {
                skippedExpiredDeadline++;
                var deleted = reservationStore.DeleteScheduledPreRecordEpgEntriesForParent(r.Id);
                deletedExpiredDeadline += deleted;
                epgEntryMutationCount += deleted;
                log.Add("PRE_REC_EPG_DEADLINE", $"R{r.Id}",
                    $"result=SKIP_REGISTER reason=deadline_already_passed_no_active_child parent=R{r.Id} service={SafeValue(r.ServiceName)} title={ReservationTitleDisplayContract.ForLog(r.Title)} now={now:MM/dd HH:mm:ss} epgStart={epgStart:MM/dd HH:mm:ss} epgEnd={epgEnd:MM/dd HH:mm:ss} programStart={r.StartTime:MM/dd HH:mm:ss} preMin={preMin} deletedScheduledPreRecEpg={deleted} action=do_not_create_late_child rule=pre_record_epg_active_window_preservation_contract");
                continue;
            }
            if (epgEnd <= now) continue; // 既に過去ならスキップ

            // 生成時点のTuner空き・同一局録画中・定時EPG予定は、一過性または別責務のため判定材料にしない。
            // Tuner空きなしの明示例外は、実際のdue時点でReservationSchedulerが判定する。
            var group = ResolveGroup(r);

            if (reservationStore.TryFindReusablePreRecordEpgEntry(
                    r.Id,
                    out var reusablePreRecId,
                    out var reusablePreRecStatus,
                    out var reusablePreRecTuner,
                    out var reusablePreRecDataVersion))
            {
                // release_contract:
                // 親予約ごとのPreRecEpg Intentは1件だけ。Scheduledなら同じ行を現在の確認窓へ更新し、Completedなら再生成しない。
                var keepScheduledEntry = reusablePreRecStatus is ReservationStatus.Scheduled or ReservationStatus.Recording;
                var tunerRebound = false;
                var metaNormalized = false;
                if (reusablePreRecStatus == ReservationStatus.Scheduled)
                {
                    // release_contract:
                    // 既存PreRecEpg子予約をdedupe再利用する場合も、子予約Titleは内部用途名へ固定し、
                    // 親番組名は parent/source_rule_id 側のメタ属性として分離する。
                    var reboundApplied = reservationStore.RebindScheduledPreRecordEpgEntry(
                        reusablePreRecId, reusablePreRecDataVersion, r.Id, epgStart, epgEnd);
                    if (reboundApplied)
                    {
                        epgEntryMutationCount++;
                        metaNormalized = true;
                        tunerRebound = !string.IsNullOrWhiteSpace(reusablePreRecTuner);
                    }
                    else
                    {
                        log.Add("PRE_REC_EPG_DEDUPE", $"R{r.Id}",
                            $"result=REBIND_CAS_REJECTED existing=R{reusablePreRecId} existingStatus={reusablePreRecStatus} parent=R{r.Id} dataVersion={reusablePreRecDataVersion} action=keep_latest_child_and_retry_on_next_evaluation rule=release_contract");
                        continue;
                    }
                }

                if (reusablePreRecStatus == ReservationStatus.Completed)
                {
                    var deletedStaleScheduled = reservationStore.DeleteScheduledPreRecordEpgEntriesForParent(r.Id);
                    if (deletedStaleScheduled > 0)
                    {
                        epgEntryMutationCount += deletedStaleScheduled;
                        log.Add("PRE_REC_EPG_INTENT_NORMALIZE", $"R{r.Id}",
                            $"result=DELETED_STALE_SCHEDULED_AFTER_COMPLETED parent=R{r.Id} completed=R{reusablePreRecId} deleted={deletedStaleScheduled} action=prevent_second_probe rule=pre_record_epg_single_intent_contract");
                    }
                }

                dedupeReuseCount++;
                dedupeParentIds.Add(r.Id);
                if (keepScheduledEntry) dedupeKeepScheduledCount++;
                if (tunerRebound) dedupeTunerReboundCount++;
                if (!keepScheduledEntry) dedupeCompletedOrTerminalCount++;

                // release_contract: repeated successful PreRecEpg dedupe is expected during startup/re-evaluation.
                // Keep normal logs as a summary; emit per-reservation rows only when a visible action occurred.
                if (tunerRebound || metaNormalized)
                {
                    log.Add("PRE_REC_EPG_DEDUPE", $"R{r.Id}",
                        $"result={(tunerRebound ? "SKIP_REUSE_REBOUND" : "SKIP_REUSE_META_NORMALIZED")} existing=R{reusablePreRecId} existingStatus={reusablePreRecStatus} parent=R{r.Id} " +
                        $"existingTuner={reusablePreRecTuner} tuner=runtime_selection tunerRebound={tunerRebound} metaNormalized={metaNormalized} group={group} eventId={r.EventId} epg={epgStart:MM/dd HH:mm:ss}〜{epgEnd:MM/dd HH:mm:ss} " +
                        $"keepScheduledEntry={keepScheduledEntry} reason=same_parent_prerec_intent_reused " +
                        $"rule=release_contract");
                }
                continue;
            }

            reservationStore.UpsertPreRecordEpgEntry(r.Id, epgStart, epgEnd);
            epgEntryMutationCount++;
            var priorityName = responsibilityByParent.TryGetValue(r.Id, out var responsibility)
                ? responsibility.PriorityName
                : string.Empty;
            var displayTitle = string.IsNullOrWhiteSpace(priorityName) ? "EPG確認" : $"EPG確認（{priorityName}）";
            log.Add("RESERVE_ENTRY", "SystemEpg", $"共通入口要求 source=System parent=R{r.Id} priority=[{SafeValue(priorityName)}] physicalTuner=[実行時選択] group={group} epg={epgStart:MM/dd HH:mm}〜{epgEnd:MM/dd HH:mm} title=[{displayTitle}] parentTitle=[{ReservationTitleDisplayContract.ForLog(r.Title)}]");
            registeredCount++;
        }

        if (dedupeReuseCount > 0)
        {
            log.Add("PRE_REC_EPG_DEDUPE", "Summary",
                $"result=OK reused={dedupeReuseCount} keepScheduled={dedupeKeepScheduledCount} terminalOrCompleted={dedupeCompletedOrTerminalCount} tunerRebound={dedupeTunerReboundCount} parents=[{string.Join(',', dedupeParentIds.Select(x => $"R{x}"))}] rule=release_contract");
        }

        log.Add("EPG_SCHEDULER", "PreRecEpg",
            $"result={(registeredCount > 0 ? "REGISTERED" : "NO_REGISTER")} candidates={userScheduled.Count} registered={registeredCount} dedupeReused={dedupeReuseCount} dedupeParents=[{string.Join(',', dedupeParentIds.Select(x => $"R{x}"))}] staleDeleted={staleCleanup.Deleted} staleParents=[{staleCleanup.ParentIds}] sourceCandidates={preRecordSourceCandidates.Count} excludedSource={excludedSource} skippedDisabled={skippedDisabled} skippedConflicted={skippedConflicted} skippedExpiredDeadline={skippedExpiredDeadline} deletedExpiredDeadline={deletedExpiredDeadline} preMin={preMin} durMin={durMin} systemMode=daily:{ini.EpgEnabled}/prerec:{ini.EpgPreRecordMinutes > 0} responsibilities=[{responsibilityMap}] dailyFallback=[{dailyFallbackMap}] manualIncluded=True skippedUserChainChild={skippedUserChainChild} countsExclusive=True deletedObsolete={deletedObsoleteChildren} routeMutations={epgEntryMutationCount} wakeRefresh=by_caller rule=system_epg_nearest_dual_axis_global_head_contract");
        return epgEntryMutationCount > 0 || registeredCount > 0;
    }

    /// <summary>
    /// 指定日のDaily責務を呼出元が確定したConfigSnapshotから計画し、SystemDailyEpg行へ一方向投影する。
    /// Finalizingからの翌日Ensureは完了runのsnapshot、terminal後の設定変更は新しい確定設定を明示的に渡す。
    /// Projection側で_currentDepth等を再読込せず、terminal・実行状態・翌日生成の判断も行わない。
    /// </summary>
    private int EnsureDailyResponsibility(
        DateTime date,
        DailyEpgConfigSnapshot snapshot,
        bool refreshAllocationRoute)
    {
        var now = DateTime.Now;
        var plannerNow = date.Date == now.Date
            ? now
            : snapshot.CanonicalStartFor(date);
        var plan = BuildDailyEpgPlanResult(date.Date, snapshot, plannerNow);
        var projectionState = BuildProjectionState(date.Date, snapshot, plan, plannerNow);
        var changed = EnsureDailyProjection(projectionState, refreshAllocationRoute);

        var status = plan.Placement == DailyEpgPlacementState.Unplaced
            ? (date.Date == now.Date && now >= plan.MovementDeadline ? "SKIPPED" : "UNPLACED_PENDING_DEADLINE")
            : "PLACED";
        var slot = plan.PlannedStart.HasValue && plan.PlannedEnd.HasValue
            ? $"{plan.PlannedStart:MM/dd HH:mm:ss}〜{plan.PlannedEnd:MM/dd HH:mm:ss}"
            : "-";
        log.Add("EPG_SCHEDULER", "EpgEntry",
            $"定時EPGエントリ: targetScope=All result={status} canonical={plan.CanonicalStart:MM/dd HH:mm:ss} " +
            $"slot={slot} requiredSeconds={plan.RequiredSeconds} movementDeadline={plan.MovementDeadline:MM/dd HH:mm:ss} reason={plan.Reason} source=daily_state_projection rule=scheduled_epg_all_movable_slot_contract");
        return changed;
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


    private int ApplyTimeFollowingWithAudit(string requestedBy, string targetScope, bool reevaluateOnUpdated = true)
    {
        // release_contract: conflicted scheduled reservations are intentionally included.
        // EventIdentity reservations must follow EIT updates before the next allocation pass;
        // otherwise a conflict can remain pinned to an obsolete time range.
        // Program reservations are explicit clock-time slots. They do not opt into EPG/EIT time following.
        // Keep the normal EPG pass limited to event-based/user reservations and never infer eligibility from EventId.
        var scheduled = reservationStore.GetByStatus(ReservationStatus.Scheduled)
            .Where(r => r.Source != ReservationSource.Epg && r.Source != ReservationSource.Program)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        var results = reservationStore.ApplyTimeFollowingDetailed(scheduled, programEvents);
        var updated = results.Where(r => r.Updated).ToList();
        var missing = results.Count(r => r.Reason == "EPG_EVENT_NOT_FOUND" || r.Reason == "NO_SERVICE_OR_EVENT_ID");
        var protectedSkip = results.Count(r => r.Reason.StartsWith("SKIP_", StringComparison.OrdinalIgnoreCase));
        var unchanged = results.Count(r => r.Reason == "UNCHANGED_WITHIN_THRESHOLD");

        log.Add("EPG_SCHEDULER", "TimeFollowSummary",
            $"source={requestedBy} targetScope={targetScope} checked={results.Count} updated={updated.Count} unchanged={unchanged} missing={missing} protectedSkip={protectedSkip} rule=release_contract");

        foreach (var r in results.Where(x => x.Updated || x.Reason == "EPG_EVENT_NOT_FOUND" || x.Reason == "EPG_EVENT_INVALID_RANGE" || x.Reason == "NO_SERVICE_OR_EVENT_ID"))
        {
            var oldRange = $"{r.OldStart:MM/dd HH:mm:ss}〜{r.OldEnd:MM/dd HH:mm:ss}";
            var newRange = r.NewStart.HasValue && r.NewEnd.HasValue
                ? $"{r.NewStart.Value:MM/dd HH:mm:ss}〜{r.NewEnd.Value:MM/dd HH:mm:ss}"
                : "-";
            log.Add("EPG_SCHEDULER", r.Updated ? "TimeFollowUpdated" : "TimeFollowAudit",
                $"service={r.ServiceName} title={r.Title} id=R{r.ReservationId} result={r.Reason} old={oldRange} new={newRange} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} eid={r.EventId} rule=release_contract");
        }

        if (updated.Count > 0)
        {
            var ids = string.Join(",", updated.Select(r => $"R{r.ReservationId}"));
            log.Add("EPG_SCHEDULER", "TimeFollow",
                $"時間追従更新: {updated.Count}件 [{ids}] route=ALLOC_ROUTE wakeRefresh=True preRecRefresh=after_time_follow rule=release_contract");
            if (reevaluateOnUpdated)
            {
                ReevaluateAndLog("TimeFollow", refreshWakeTask: true);
            }
        }

        return updated.Count;
    }

    /// <summary>
    /// DailyRunState/翌日責務のcommit後にSYSTEM_EPG Wakeだけを現在正本へ同期する。
    /// PlannerやallocationをWake側から逆起動せず、既存TaskSchedulerServiceの差分適用へ委譲する。
    /// </summary>
    private void RefreshDailyWakeProjection(string action)
    {
        allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "EpgScheduler",
            Action: action,
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: false,
            ReevaluateAllocations: false,
            RefreshPreRecordEpgEntries: false,
            RefreshWakeTask: true,
            EmitConflictLogs: false,
            ConflictLogCategory: "EPG_SCHEDULER",
            ConflictLogTitle: $"Conflict({action})"));
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
