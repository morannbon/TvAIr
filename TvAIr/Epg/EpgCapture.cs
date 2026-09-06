using Microsoft.Extensions.Options;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Tuner;
using TvAIr.Schedule;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TvAIr.Epg;

/// <summary>
/// EPGキャプチャの全体制御。
///
/// 処理フロー:
///   1. ch2 からチャンネル一覧を取得
///   2. TransportStreamId 単位にグループ化（同一TS内の複数サービスを1回の録画でまとめて取得）
///   3. TunerPool からチューナーを確保して TVTest を起動（EPG取得用の短時間TS取得）
///   4. TvAIrEpgRec終了・チューナー解放後、次局workerを直ちに投入
///   5. 前局TSの解析・DB保存は次局取得と並行して実行
///   6. 通常EPGは対象TS/SIDを今回TSから読めた結果だけでDBへUPSERT
///      非チェーンの録画前EPG確認は目的EventIdentityの観測snapshotを呼出元へ返し、通常EPG DBへは書き込まない
///      明示チェーンrootの既存DB-backed追従経路は、開発者承認なしに変更しない
///
/// 並列数は TunerPool の空きスロット数で自動決定する（GrConcurrentCaptures/BsCsConcurrentCaptures 廃止）。
/// </summary>
public sealed class EpgCapture
{
    private readonly IOptionsMonitor<EpgSettings> settingsMonitor;

    // release_contract: IOptionsMonitor に切り替えたため CurrentValue は読み取り専用。
    // UpdateRuntimeDepth() で設定された EpgDepth だけ別フィールドでオーバーライドする。
    private string? runtimeEpgDepthOverride;
    private EpgSettings settings => settingsMonitor.CurrentValue;
    // UpdateRuntimeDepth() によるオーバーライドを優先し、なければ CurrentValue の EpgDepth を使う。
    private string effectiveEpgDepth => runtimeEpgDepthOverride ?? settings.EpgDepth;
    private readonly IReadOnlyList<TunerProfile> tunerProfiles;
    private readonly ChannelFileLoader channelLoader;
    private readonly TvTestLauncher launcher;
    private readonly EpgStore store;
    private readonly LogRepository log;
    private readonly TunerPool tunerPool;
    private readonly ReservationStore reservationStore;
    private readonly IniSettingsService ini;
    private readonly Database database;
    private readonly TvTestActivityKeeper tvTestActivity;
    private readonly ServiceLogoStore serviceLogoStore;
    private readonly EpgLogoExtractor logoExtractor;
    private readonly ReservationProjectionPromotionService projectionPromotion;
    private readonly KeywordMatcher keywordMatcher;

    // キャプチャ状態（UIへの進捗通知用）
    private EpgCaptureStatus status = new();
    private readonly object statusGate = new();

    // 管理外TVTestはEPG割当判断の対象外。EPGはTvAIrのTunerPoolだけを正本にする。

    // Process.Start 自体だけは短時間シリアル化する。BonDriver/OpenTuner/SetChannel の
    // 物理デバイス開始境界は TunerDeviceAccessGate の GR/BSCS 単位契約で保護する。
    // このSemaphoreをworkerの起動完了待ちに使うとGRとBSCSまで不要に直列化されるため、
    // 子プロセス生成が終わった時点で必ず解放する。
    private readonly SemaphoreSlim epgLaunchStartGate = new(1, 1);

    // TS解析は局ごとに並行できるが、SQLite書込み・stale retire・投影昇格は一つの確定単位として直列化する。
    private readonly SemaphoreSlim epgImportCommitGate = new(1, 1);

    // EPG workerのTask・lease・process・終端状態は activeEpgWorkerTasks を単一正本とする。
    // 表示、停止、復帰照合、カバレッジ監視も同じ状態から投影する。
    // PreRecの開始安全領域により起動できなかったTSだけをrun単位で保持する。
    // normal EPGは開始Admission後に録画timelineを理由としてblocked/deferredへ移行しない。
    private readonly ConcurrentDictionary<string, string> currentRunBlockedGroups = new(StringComparer.OrdinalIgnoreCase);
    // EPG取得失敗はrun単位で保持する。
    private readonly ConcurrentDictionary<string, EpgCaptureFailureState> currentRunCaptureFailures = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastEpgWorkerCoverageLogUtc = DateTime.MinValue;
    private DateTime lastEpgWorkerGapLogUtc = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, byte> epgRunsAcceptingNewWorkers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> activeNormalEpgRuns = new(StringComparer.Ordinal);
    private long epgRunSequence = 0;
    // UI status projection has one visible owner. Scheduled Daily runs use manageStatus=false and do not overwrite it.
    private string currentEpgRunId = "none";
    // release_contract: EPG worker task state is tracked by actual task registration, not by increment/decrement counters.
    // Cancellation can finish the run before worker finally blocks drain; a global counter can underflow in that case.
    private readonly ConcurrentDictionary<string, ActiveEpgWorkerTask> activeEpgWorkerTasks = new(StringComparer.Ordinal);


    public EpgCapture(
        IOptionsMonitor<EpgSettings> settingsMonitor,
        IReadOnlyList<TunerProfile> tunerProfiles,
        ChannelFileLoader channelLoader,
        TvTestLauncher launcher,
        EpgStore store,
        LogRepository log,
        TunerPool tunerPool,
        ReservationStore reservationStore,
        IniSettingsService ini,
        Database database,
        TvTestActivityKeeper tvTestActivity,
        ServiceLogoStore serviceLogoStore,
        EpgLogoExtractor logoExtractor,
        ReservationProjectionPromotionService projectionPromotion,
        KeywordMatcher keywordMatcher)
    {
        this.settingsMonitor = settingsMonitor;
        this.tunerProfiles = tunerProfiles;
        this.channelLoader = channelLoader;
        this.launcher      = launcher;
        this.store         = store;
        this.log           = log;
        this.tunerPool     = tunerPool;
        this.reservationStore = reservationStore;
        this.ini           = ini;
        this.database      = database;
        this.tvTestActivity = tvTestActivity;
        this.serviceLogoStore = serviceLogoStore;
        this.logoExtractor = logoExtractor;
        this.projectionPromotion = projectionPromotion;
        this.keywordMatcher = keywordMatcher;
    }

    private static EpgWorkerProcessIdentityState GetOwnedWorkerProcessIdentity(ActiveEpgWorkerSnapshot snapshot)
    {
        if (snapshot.ProcessId <= 0) return EpgWorkerProcessIdentityState.Missing;

        try
        {
            using var process = Process.GetProcessById(snapshot.ProcessId);
            if (process.HasExited) return EpgWorkerProcessIdentityState.Missing;

            if (snapshot.ProcessStartedAtUtc.HasValue)
            {
                try
                {
                    var actualStartedAtUtc = process.StartTime.ToUniversalTime();
                    return Math.Abs((actualStartedAtUtc - snapshot.ProcessStartedAtUtc.Value).TotalMilliseconds) <= 1000
                        ? EpgWorkerProcessIdentityState.Match
                        : EpgWorkerProcessIdentityState.ReusedPid;
                }
                catch
                {
                    return EpgWorkerProcessIdentityState.IdentityUnknown;
                }
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ProcessExecutablePath))
            {
                try
                {
                    var actualPath = process.MainModule?.FileName ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(actualPath))
                        return EpgWorkerProcessIdentityState.IdentityUnknown;
                    return string.Equals(
                        Path.GetFullPath(actualPath),
                        Path.GetFullPath(snapshot.ProcessExecutablePath),
                        StringComparison.OrdinalIgnoreCase)
                        ? EpgWorkerProcessIdentityState.Match
                        : EpgWorkerProcessIdentityState.ReusedPid;
                }
                catch
                {
                    return EpgWorkerProcessIdentityState.IdentityUnknown;
                }
            }

            return EpgWorkerProcessIdentityState.IdentityUnknown;
        }
        catch (ArgumentException)
        {
            return EpgWorkerProcessIdentityState.Missing;
        }
        catch
        {
            return EpgWorkerProcessIdentityState.IdentityUnknown;
        }
    }

    private static bool IsOwnedWorkerProcessAlive(ActiveEpgWorkerSnapshot snapshot)
        => GetOwnedWorkerProcessIdentity(snapshot) is EpgWorkerProcessIdentityState.Match
            or EpgWorkerProcessIdentityState.IdentityUnknown;

    public int CapturePowerSuspendSnapshot(TunerOwnershipReconcileContext context)
    {
        var active = activeEpgWorkerTasks.Values
            .Select(x => x.Snapshot())
            .Count(x => !x.IsTerminal && x.ProcessId > 0 && x.PoolLeaseCurrent);

        log.Add("EPG_POWER_SUSPEND_SNAPSHOT", context.CycleId,
            $"result=OBSERVED source={context.Source} activeWorkers={activeEpgWorkerTasks.Count} ownedRunning={active} " +
            "action=observe_only_run_continues_unless_worker_process_actually_disappears rule=power_notification_observation_only_contract");
        return active;
    }

    public async Task<TunerOwnershipReconcileResult> ReconcileOwnedWorkersAfterResumeAsync(
        TunerOwnershipReconcileContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workers = activeEpgWorkerTasks.Values.ToList();
        var alive = 0;
        var missing = 0;
        var unavailable = 0;
        var missingCandidates = new List<ActiveEpgWorkerTask>();

        // POWER_NOTIFICATION_OBSERVATION_ONLY_INVARIANT:
        // Suspend/Resume通知だけではEPG workerを中断しない。開始済みrunは明示Cancel以外で止めない。
        // ReconcileはPID/leaseの実状態だけを観測し、実process消失時だけ既存ProcessMissing経路へ収束する。
        foreach (var worker in workers)
        {
            var snapshot = worker.Snapshot();
            if (!snapshot.PoolLeaseCurrent)
            {
                unavailable++;
                log.Add("EPG_RECONCILE_STALE_POOL_LEASE", context.CycleId,
                    $"result=OWNERSHIP_UNAVAILABLE source={context.Source} runId={snapshot.RunId} pid={snapshot.ProcessId} group={snapshot.Group} " +
                    $"poolLeaseId={(snapshot.PoolLeaseId.HasValue ? snapshot.PoolLeaseId.Value.ToString("D") : "-")} occupancyGeneration={snapshot.OccupancyGeneration} " +
                    "action=preserve_owner_task_reject_stale_cleanup rule=release_contract");
                continue;
            }

            if (snapshot.ProcessId <= 0)
            {
                unavailable++;
                continue;
            }

            if (snapshot.IsTerminal)
                continue;

            var identity = GetOwnedWorkerProcessIdentity(snapshot);
            if (identity == EpgWorkerProcessIdentityState.Match)
            {
                alive++;
                continue;
            }
            if (identity == EpgWorkerProcessIdentityState.IdentityUnknown)
            {
                unavailable++;
                continue;
            }

            missingCandidates.Add(worker);
        }

        // Prefer the owner task's actual completion signal. The timeout is only an upper bound
        // for workers whose owner has not converged yet; it is not a mandatory settle delay.
        if (missingCandidates.Count > 0)
        {
            const int ownerCompletionUpperBoundMs = 600;
            await Task.WhenAll(missingCandidates.Select(worker =>
                worker.WaitForOwnerTaskCompletionAsync(
                    TimeSpan.FromMilliseconds(ownerCompletionUpperBoundMs),
                    cancellationToken))).ConfigureAwait(false);
        }

        foreach (var worker in missingCandidates)
        {
            var snapshot = worker.Snapshot();
            if (snapshot.IsTerminal)
                continue;

            var identity = GetOwnedWorkerProcessIdentity(snapshot);
            if (identity is EpgWorkerProcessIdentityState.Match or EpgWorkerProcessIdentityState.IdentityUnknown)
                continue;

            if (snapshot.OwnerTaskCompleted)
            {
                if (TryConvergeExitedEpgWorker(worker, EpgWorkerTerminalReason.Completed, "resume_owner_completed", expectedAttemptGeneration: snapshot.AttemptGeneration))
                {
                    missing++;
                    log.Add("EPG_RESUME_RECONCILE", context.CycleId,
                        $"result=OWNER_COMPLETED_PROCESS_EXITED source={context.Source} pid={snapshot.ProcessId} group={snapshot.Group} tsid={snapshot.TsId} " +
                        $"service={snapshot.ServiceName} action=lease_released_task_removed rule=release_contract");
                }
                else
                {
                    unavailable++;
                    StartResidualEpgWorkerExitMonitor(worker);
                    log.Add("EPG_RESUME_RECONCILE", context.CycleId,
                        $"result=OWNER_COMPLETED_CONVERGENCE_DEFERRED source={context.Source} pid={snapshot.ProcessId} group={snapshot.Group} tsid={snapshot.TsId} " +
                        $"service={snapshot.ServiceName} action=keep_task_and_lease_monitor rule=release_contract");
                }
                continue;
            }

            var newlyReportedMissing = worker.ReportProcessMissing(context.CycleId, snapshot.AttemptGeneration);
            if (newlyReportedMissing)
            {
                missing++;
                log.Add("EPG_RESUME_RECONCILE", context.CycleId,
                    $"result=PROCESS_MISSING source={context.Source} pid={snapshot.ProcessId} group={snapshot.Group} tsid={snapshot.TsId} " +
                    $"service={snapshot.ServiceName} action=signal_owner_task_start_bounded_convergence rule=release_contract");
            }

            // ReportProcessMissing is intentionally single-shot because the owner signal uses a
            // TaskCompletionSource. Convergence monitoring is not single-shot: if process identity
            // was temporarily unavailable, the next reconciliation must be able to retry.
            StartMissingEpgWorkerConvergenceMonitor(worker);
        }

        return new TunerOwnershipReconcileResult(
            "EPG",
            workers.Count,
            alive,
            0,
            unavailable,
            missing,
            $"cycle={context.CycleId} source={context.Source} missing={missing} unavailable={unavailable}");
    }


    internal IReadOnlyList<ActiveEpgWorkerSnapshot> GetActiveEpgWorkerSnapshots()
        => activeEpgWorkerTasks.Values.Select(worker => worker.Snapshot()).ToArray();

    internal IReadOnlyDictionary<string, IReadOnlyList<string>> GetRunOwnedPhysicalTuners(string runId)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var grouped = SnapshotActiveEpgWorkerTasks(runId)
            .Select(x => x.Snapshot())
            .Where(x => x.PoolLeaseId.HasValue && x.PoolLeaseCurrent && !string.IsNullOrWhiteSpace(x.TunerName))
            .GroupBy(x => string.Equals(x.Group, "BSCS", StringComparison.OrdinalIgnoreCase) ? "BSCS" : "GR", StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            result[group.Key] = group
                .Select(x => x.TunerName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return result;
    }

    public EpgCaptureStatus GetStatus()
    {
        EpgCaptureStatus snapshot;
        lock (statusGate) snapshot = status with { };

        if (!string.Equals(snapshot.Phase, "running", StringComparison.OrdinalIgnoreCase))
            return snapshot;

        var now = DateTime.Now;
        var alive = SnapshotActiveEpgWorkerTasks(currentEpgRunId)
            .Select(x => x.Snapshot())
            .Where(x => !x.IsTerminal && x.ProcessId > 0
                && (GetOwnedWorkerProcessIdentity(x) is EpgWorkerProcessIdentityState.Match or EpgWorkerProcessIdentityState.IdentityUnknown))
            .OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.TsId)
            .ThenBy(x => x.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var plannedSeconds = Math.Max(1, WaitSecondsForDepth(snapshot.RunDepth));
        var runningProgress = 0.0;
        var maxElapsedSeconds = 0;
        foreach (var worker in alive)
        {
            var elapsedSeconds = Math.Max(0, (int)(now - worker.StartedAt).TotalSeconds);
            maxElapsedSeconds = Math.Max(maxElapsedSeconds, elapsedSeconds);
            runningProgress += Math.Min(0.95, elapsedSeconds / (double)plannedSeconds);
        }

        var total = Math.Max(0, snapshot.TotalGroups);
        var completed = Math.Clamp(snapshot.CompletedGroups, 0, total == 0 ? int.MaxValue : total);
        var estimatedPercent = total > 0
            ? (int)Math.Round(Math.Clamp((completed + runningProgress) / total, 0.0, 1.0) * 100.0)
            : 0;

        var names = alive.Count == 0
            ? ""
            : string.Join(" / ", alive.Take(3).Select(x => x.ServiceName));
        if (alive.Count > 3) names += $" / 他{alive.Count - 3}件";

        return snapshot with
        {
            RunningGroups = alive.Count,
            RunningGroupNames = names,
            ActiveWorkerElapsedSeconds = maxElapsedSeconds,
            ActiveWorkerPlannedSeconds = plannedSeconds,
            EstimatedProgressPercent = estimatedPercent
        };
    }

    /// <summary>
    /// 設定画面から保存されたEPG深度を、再起動なしで実取得秒数へ反映する。
    /// EpgCapture は singleton のため、IOptions の起動時値だけに依存すると
    /// UI表示上の深度と /recduration が乖離する。
    /// </summary>
    public void UpdateRuntimeDepth(string? epgDepth)
    {
        var normalized = NormalizeDepth(epgDepth);
        var before = runtimeEpgDepthOverride ?? settings.EpgDepth;
        runtimeEpgDepthOverride = normalized;
        Log("EPG_DEPTH_APPLIED", "EPG",
            $"before={before} after={normalized} perTsSeconds={WaitSecondsForDepth(normalized)} source=runtime_depth rule=epg_duration_policy_common");
    }

    private static string NormalizeDepth(string? value) => EpgDurationPolicy.NormalizeDepth(value);

    private static int WaitSecondsForDepth(string? value) => EpgDurationPolicy.BaseSecondsForDepth(value);

    /// <summary>EpgSchedulerからTriggerNow直後に呼び、Phaseを即座にrunningにセットする。
    /// uiVisible=false の run は、ブラウザを開いてもEPG取得ツールを一切表示しない契約。
    /// </summary>
    public void SetRunning(bool uiVisible = true, string runSource = "ManualUi", string targetScope = "All", string runPurpose = "normal_epg_capture")
    {
        var mode = uiVisible ? "Visible" : "Silent";
        SetStatus(s => s with
        {
            Phase = "running",
            RunStartedAt = DateTime.Now,
            RunningGroups = 0,
            RunningGroupNames = "",
            ActiveWorkerElapsedSeconds = 0,
            ActiveWorkerPlannedSeconds = 0,
            EstimatedProgressPercent = 0,
            UiVisible = uiVisible,
            RunPurpose = runPurpose,
            RunSource = string.IsNullOrWhiteSpace(runSource) ? "ManualUi" : runSource,
            UiMode = mode,
            CancelRoute = uiVisible ? "VisibleWidget" : "SilentTray",
            TargetScope = string.IsNullOrWhiteSpace(targetScope) ? "All" : targetScope,
            LastRunMessage = uiVisible ? "取得開始準備中" : "サイレントEPG取得中"
        });
    }

    /// <summary>
    /// EPGキャンセル完了を、停止要求ではなく「worker停止・activity解放・EPG lease解放」後として扱う。
    /// EpgScheduler はこの完了待ちの後で論理wave占有を解放し、EPG_RUN_END と
    /// 共通 ALLOC_ROUTE:NormalEpgWaveTerminal を実行する。
    /// </summary>
    public async Task WaitForCancellationQuiescenceAsync(string source, bool silent, string targetScope, string runId, TimeSpan? timeout = null)
    {
        var waitLimit = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + waitLimit;
        var loggedWait = false;

        // Cancellation quiescence belongs to the normal EPG run that accepted this cancel.
        // Independent PreRec probes also use TunerUsageKind.Epg, but have their own runId/lease owner
        // and must never keep an unrelated normal EPG cancellation waiting.
        while (true)
        {
            var activeTasks = SnapshotActiveEpgWorkerTasks(runId);
            var activeSnapshots = activeTasks
                .Select(x => x.Snapshot())
                .ToList();
            var alive = activeSnapshots
                .Where(x => !x.IsTerminal && x.ProcessId > 0
                && (GetOwnedWorkerProcessIdentity(x) is EpgWorkerProcessIdentityState.Match or EpgWorkerProcessIdentityState.IdentityUnknown))
                .OrderBy(x => x.Group)
                .ThenBy(x => x.TsId)
                .ThenBy(x => x.ProcessId)
                .ToList();

            var ownedLeaseIdentities = activeSnapshots
                .Where(x => x.PoolLeaseId.HasValue)
                .Select(x => new TunerLeaseIdentity(x.PoolLeaseId.GetValueOrDefault(), x.OccupancyGeneration))
                .ToHashSet();
            var epgSlots = tunerPool.GetStatus()
                .Where(s => s.UsageKind == TunerUsageKind.Epg
                    && s.PoolLeaseId is Guid poolLeaseId
                    && ownedLeaseIdentities.Contains(new TunerLeaseIdentity(poolLeaseId, s.OccupancyGeneration)))
                .OrderBy(s => s.SlotIndex)
                .ToList();

            if (alive.Count == 0 && activeTasks.Count == 0 && epgSlots.Count == 0)
            {
                LogEpgWorkerCoverage(runId, "cancel_quiescence_complete", force: true);
                Log("EPG_CANCEL_RELEASE_COMPLETE", "EPG",
                    $"result=OK source={SafeLog(source)} silent={silent} uiMode={(silent ? "Silent" : "Visible")} targetScope={SafeLog(targetScope)} runId={SafeLog(runId)} aliveWorkers=0 activeWorkerTasks=0 epgSlots=0 action=allow_epg_run_end_and_allocation_reevaluate rule=release_contract");
                return;
            }

            if (!loggedWait)
            {
                loggedWait = true;
                LogEpgWorkerCoverage(runId, "cancel_quiescence_wait", force: true);
                Log("EPG_CANCEL_RELEASE_WAIT", "EPG",
                    $"source={SafeLog(source)} silent={silent} uiMode={(silent ? "Silent" : "Visible")} targetScope={SafeLog(targetScope)} runId={SafeLog(runId)} aliveWorkers={alive.Count} activeWorkerTasks={activeTasks.Count} epgSlots={epgSlots.Count} action=wait_before_epg_run_end_and_allocation_reevaluate rule=release_contract");
            }

            if (DateTime.UtcNow >= deadline)
            {
                LogEpgWorkerCoverage(runId, "cancel_quiescence_timeout", force: true);
                var activeTaskSummary = FormatActiveEpgWorkerTaskSummary(activeTasks);
                Log("EPG_CANCEL_RELEASE_COMPLETE", "WARN",
                    $"result=TIMEOUT source={SafeLog(source)} silent={silent} uiMode={(silent ? "Silent" : "Visible")} targetScope={SafeLog(targetScope)} runId={SafeLog(runId)} aliveWorkers={alive.Count} activeWorkerTasks={activeTasks.Count} activeTaskSummary={SafeLog(activeTaskSummary)} epgSlots={epgSlots.Count} action=continue_with_warn_before_allocation_reevaluate rule=release_contract");
                return;
            }

            await Task.Delay(120);
        }
    }

    // ─── 実行エントリポイント ─────────────────────────────────────

    public async Task<EpgCaptureResult> RunAsync(
        CancellationToken ct = default,
        string targetScope = "All",
        string? runDepth = null,
        bool showProgress = true,
        string? runId = null,
        bool manageStatus = true)
    {
        var started = DateTime.Now;
        var normalizedScope = NormalizeTargetScope(targetScope);
        var normalizedDepth = NormalizeDepth(runDepth ?? effectiveEpgDepth);
        const string runPurpose = "normal_epg_capture";
        runId = string.IsNullOrWhiteSpace(runId)
            ? $"epg-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Interlocked.Increment(ref epgRunSequence)}"
            : runId.Trim();
        if (manageStatus) currentEpgRunId = runId;
        activeNormalEpgRuns[runId] = 0;
        PruneCompletedEpgWorkerTasksForOldRuns(runId);
        runtimeEpgDepthOverride = normalizedDepth;
        Log("EPG_RUN_START", "EPG", $"EPG取得を開始します。targetScope={normalizedScope} runDepth={normalizedDepth} purpose={runPurpose} uiVisible={showProgress} uiMode={(showProgress ? "Visible" : "Silent")} rule=release_contract");
        epgRunsAcceptingNewWorkers[runId] = 0;
        CancellationTokenSource? coverageCts = null;
        Task? coverageMonitor = null;

        try
        {
            // 管理外TVTestは監視・保護・EPG除外の対象外。
            // EPG割当はTvAIr自身のTunerPool状態だけを使用する。
            // 物理PT3チューナーの選択・包含的利用・競合処理・再配置はBonDriver_PTx/PT3側へ委ねる。
            // 管理外TVTestの存在を推測してHost側で別Tunerへ逃がす、待つ、譲る、失敗扱いにする処理を追加しない。
            Log("EPG_TUNER_SCOPE", "EPG",
                "result=OK source=tvair_tuner_pool_only unmanagedExternalTvTest=out_of_scope externalProcessScan=False rule=release_contract");

            var load = channelLoader.Load();
            Log("EPG_CH2_LOADED", "EPG", load.Message);
            foreach (var warning in load.Warnings)
                Log("EPG_CHANNEL_CONFIG", "Settings", warning);

            if (load.Targets.Count == 0)
            {
                Log("EPG_RUN_FAIL", "EPG", "有効なチャンネルがありません。ch2/ChSetを設定画面で明示してください。source=explicit_settings rule=release_contract");
                return EpgCaptureResult.Failed("有効なチャンネルがありません。ch2/ChSetを設定画面で明示してください。");
            }

            var allGroups = BuildGroups(load.Targets);
            var groups = FilterGroupsByScope(allGroups, normalizedScope);
            if (groups.Count == 0)
            {
                var noTarget = $"EPG取得対象がありません。targetScope={normalizedScope}";
                Log("EPG_RUN_FAIL", "EPG", noTarget);
                if (manageStatus)
                    SetStatus(st => st with { Phase = "idle", TotalGroups = 0, CompletedGroups = 0, RunningGroups = 0, RunningGroupNames = "", ActiveWorkerElapsedSeconds = 0, ActiveWorkerPlannedSeconds = 0, EstimatedProgressPercent = 0, RunStartedAt = null, LastRunAt = DateTime.Now, LastRunMessage = noTarget, RunDepth = normalizedDepth, TargetScope = normalizedScope, UiVisible = showProgress, RunPurpose = runPurpose, UiMode = showProgress ? "Visible" : "Silent", CancelRoute = showProgress ? "VisibleWidget" : "SilentTray" });
                epgRunsAcceptingNewWorkers.TryRemove(runId, out _);
                return EpgCaptureResult.Failed(noTarget);
            }

            if (manageStatus)
                SetStatus(st => st with
            {
                Phase = "running",
                TotalGroups = groups.Count,
                CompletedGroups = 0,
                RunningGroups = 0,
                RunningGroupNames = "",
                ActiveWorkerElapsedSeconds = 0,
                ActiveWorkerPlannedSeconds = WaitSecondsForDepth(normalizedDepth),
                EstimatedProgressPercent = 0,
                RunStartedAt = started,
                LastRunMessage = "EPG取得中",
                RunDepth = normalizedDepth,
                TargetScope = normalizedScope,
                UiVisible = showProgress,
                RunPurpose = runPurpose,
                UiMode = showProgress ? "Visible" : "Silent",
                CancelRoute = showProgress ? "VisibleWidget" : "SilentTray"
            });
            Log("EPG_PLAN", "EPG",
                $"TS単位巡回取得を開始します。targetScope={normalizedScope} 対象 {groups.SelectMany(g => g.Targets).Count()} 局 / {groups.Count} グループ。allGroups={allGroups.Count} rule=epg_plan_contract");

            using var limitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limitCts.CancelAfter(TimeSpan.FromMinutes(settings.TotalTimeLimitMinutes));
            var execToken = limitCts.Token;

            coverageCts = CancellationTokenSource.CreateLinkedTokenSource(execToken);
            coverageMonitor = StartEpgWorkerCoverageMonitorAsync(started, runId, coverageCts.Token);

            var totalImported = 0;
            var completedGroups = new HashSet<string>();
            totalImported += await RunPassAsync(groups, completedGroups, pass: 1, execToken, runId, manageStatus);
            execToken.ThrowIfCancellationRequested();
            epgRunsAcceptingNewWorkers.TryRemove(runId, out _);

            Log("EPG_ENDING_PHASE", "EPG",
                $"enter completed={completedGroups.Count}/{groups.Count} imported={totalImported} note=serialize_exit_release_cleanup");

            var elapsed = (int)(DateTime.Now - started).TotalSeconds;
            var totalGroups = groups.Count;
            var completedCount = completedGroups.Count;
            var missingGroups = Math.Max(0, totalGroups - completedCount);
            var missingGroupDetails = FormatMissingGroupDetails(groups, completedGroups);
            var missingScopes = string.Join(",", groups
                .Where(g => !completedGroups.Contains(g.Key))
                .Select(g => g.Group)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            var completedScopes = string.Join(",", groups
                .GroupBy(g => g.Group, StringComparer.OrdinalIgnoreCase)
                .Where(scopeGroups => scopeGroups.Any()
                    && scopeGroups.All(g => completedGroups.Contains(g.Key)))
                .Select(scopeGroups => scopeGroups.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            var failureSummary = FormatCaptureFailureSummary(groups, completedGroups, runId);
            var blockedReason = ResolveEpgRunBlockedReason(groups, completedCount, totalGroups, runId);
            var runResult = !string.IsNullOrWhiteSpace(blockedReason)
                ? "BLOCKED"
                : completedCount == totalGroups && totalGroups > 0
                    ? "OK"
                    : "FAILED";
            var detailCore = $"result={runResult} targetScope={normalizedScope} runDepth={normalizedDepth} completed={completedCount}/{totalGroups} imported={totalImported} completedScopes={completedScopes} missingGroups={missingGroups} missingScopes={missingScopes} missingGroupDetails=[{missingGroupDetails}] failureSummary=[{failureSummary}]";
            var detailExtra = $"blockedReason={SafeLogValue(blockedReason)}";
            var detail = detailCore + " " + detailExtra;
            var captureResultDetail = $"blockedReason={SafeLogValue(blockedReason)}; completedScopes={completedScopes}; missingScopes={missingScopes}; missingGroupDetails={missingGroupDetails}; failureSummary={failureSummary}";
            var msg = runResult == "OK"
                ? $"EPG取得を完了しました\n{completedCount}/{totalGroups} グループ完了\n{totalImported} 件取得しました"
                : runResult == "BLOCKED"
                    ? "EPG取得を開始できません"
                    : $"EPG取得に失敗しました\n{completedCount}/{totalGroups} グループ完了\n{totalImported} 件取得しました";

            var runEvent = runResult == "OK" ? "EPG_RUN_OK" : runResult == "BLOCKED" ? "EPG_RUN_BLOCKED" : "EPG_RUN_FAILED";
            var runTitle = runResult == "OK" ? "EPG" : runResult == "BLOCKED" ? "BLOCKED" : "FAILED";
            Log(runEvent, runTitle, msg.Replace("\n", " / ") + " " + detail + " rule=release_contract");
            Log("EPG_RUN_END", runResult, detail + " rule=release_contract");
            var grGroupKeys = groups.Where(g => g.Group == "GR").Select(g => g.Key).ToList();
            if (grGroupKeys.Count > 0 && !grGroupKeys.Any(k => completedGroups.Contains(k)))
                Log("EPG_GR_ALL_EMPTY", "WARN", $"result=WARN grGroups={grGroupKeys.Count} grCompleted=0 rule=release_contract");

            var finalPhase = runResult == "OK" ? "completed" : runResult == "BLOCKED" ? "blocked" : "failed";
            var finalProgress = totalGroups <= 0
                ? 0
                : Math.Clamp((int)Math.Round((double)completedCount * 100 / totalGroups), 0, 100);
            if (manageStatus)
                SetStatus(st => st with { Phase = finalPhase, CompletedGroups = completedCount, RunningGroups = 0, RunningGroupNames = "", ActiveWorkerElapsedSeconds = 0, ActiveWorkerPlannedSeconds = WaitSecondsForDepth(normalizedDepth), EstimatedProgressPercent = finalProgress, LastRunAt = DateTime.Now, LastRunMessage = msg, RunDepth = normalizedDepth, TargetScope = normalizedScope, UiVisible = showProgress, RunPurpose = runPurpose, UiMode = showProgress ? "Visible" : "Silent", CancelRoute = showProgress ? "VisibleWidget" : "SilentTray" });

            var deleted = store.DeleteExpired(DateTime.Now.Date.AddDays(-1));
            if (deleted > 0)
                Log("EPG_CLEANUP", "EPG", $"期限切れ {deleted} 件を削除しました。");

            await StopEpgWorkerCoverageMonitorIfStartedAsync(coverageCts, coverageMonitor);
            coverageCts = null;
            coverageMonitor = null;
            LogEpgWorkerCoverage(runId, "run_end", force: true);

            return new EpgCaptureResult(string.Equals(runResult, "OK", StringComparison.OrdinalIgnoreCase), completedCount, totalGroups, totalImported, runResult, missingGroups, msg, captureResultDetail)
            {
                MissingScopes = missingScopes,
                CompletedScopes = completedScopes,
                PreRecordEvents = Array.Empty<EpgEvent>()
            };
        }
        finally
        {
            epgRunsAcceptingNewWorkers.TryRemove(runId, out _);
            activeNormalEpgRuns.TryRemove(runId, out _);
            foreach (var key in currentRunBlockedGroups.Keys.Where(k => k.StartsWith(runId + "|", StringComparison.Ordinal)))
                currentRunBlockedGroups.TryRemove(key, out _);
            foreach (var key in currentRunCaptureFailures.Keys.Where(k => k.StartsWith(runId + "|", StringComparison.Ordinal)))
                currentRunCaptureFailures.TryRemove(key, out _);
            await StopEpgWorkerCoverageMonitorIfStartedAsync(coverageCts, coverageMonitor);
        }
    }

    // PRE_RECORD_EPG_INDEPENDENT_PROBE_CONTRACT:
    // Every PreRec probe is an independent physical-tuner job and must not share the normal EPG
    // run/status owner. Non-chain probes return an observed snapshot without DB mutation. The protected
    // user-chain root keeps its established DB-backed series-follow input, but still owns only this
    // probe worker/lease and never mutates the normal EPG run/status owner.
    public async Task<EpgCaptureResult> RunIndependentPreRecordProbeAsync(
        CancellationToken ct,
        string targetScope,
        ushort? expectedNetworkId,
        ushort? expectedTransportStreamId,
        ushort? expectedServiceId,
        string? expectedServiceName,
        ushort? expectedEventId,
        DateTime? expectedStartTime,
        DateTime? expectedEndTime,
        int maxCaptureSeconds,
        string preferredRecordingTunerName,
        bool preserveUserChainDbSeriesFollow = false)
    {
        var started = DateTime.Now;
        var normalizedScope = NormalizeTargetScope(targetScope);
        var runId = $"prerec-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Interlocked.Increment(ref epgRunSequence)}";
        var load = channelLoader.Load();
        if (load.Targets.Count == 0)
            return EpgCaptureResult.Failed("録画前EPG確認対象チャンネルがありません。");

        var groups = FilterGroupsForExpectedService(
            FilterGroupsByScope(BuildGroups(load.Targets), normalizedScope),
            expectedNetworkId,
            expectedTransportStreamId,
            expectedServiceId,
            expectedServiceName);
        if (groups.Count != 1)
        {
            Log("PRE_REC_EPG_INDEPENDENT_PROBE", "EPG確認",
                $"result=NO_UNIQUE_TARGET runId={runId} targetScope={normalizedScope} matchedGroups={groups.Count} expectedNid={(expectedNetworkId?.ToString() ?? "-")} expectedTsid={(expectedTransportStreamId?.ToString() ?? "-")} expectedSid={(expectedServiceId?.ToString() ?? "-")} expectedEventId={(expectedEventId?.ToString() ?? "-")} action=keep_original_recording_time rule=pre_record_epg_independent_probe_contract");
            return EpgCaptureResult.Failed("録画前EPG確認対象TSを一意に解決できませんでした。");
        }

        var group = groups[0];
        var snapshotOnly = !preserveUserChainDbSeriesFollow;
        var events = new ConcurrentBag<EpgEvent>();
        var workerState = RegisterActiveEpgWorkerTask(group, 1, runId);
        var imported = 0;
        try
        {
            Log("PRE_REC_EPG_INDEPENDENT_PROBE", $"TS{group.TsId}",
                $"result=START runId={runId} group={group.Group} tuner={SafeLogValue(preferredRecordingTunerName)} safetyCeilingSec={maxCaptureSeconds} expectedEventId={(expectedEventId?.ToString() ?? "-")} chainDbSeriesFollow={preserveUserChainDbSeriesFollow} policy=parallel_by_physical_recording_tuner_no_normal_epg_status_owner rule=pre_record_epg_independent_probe_contract");

            imported = await CaptureGroupAsync(
                group: group,
                pass: 1,
                ct: ct,
                isPreRecordCheck: true,
                workerTaskState: workerState,
                maxCaptureSeconds: maxCaptureSeconds,
                expectedNetworkId: expectedNetworkId,
                expectedTransportStreamId: expectedTransportStreamId,
                expectedServiceId: expectedServiceId,
                expectedEventId: expectedEventId,
                expectedStartTime: expectedStartTime,
                expectedEndTime: expectedEndTime,
                preferredRecordingTunerName: preferredRecordingTunerName,
                preTuneChainPosition: null,
                preTuneAction: null,
                preTuneKeepWorkerUntilSafetyCeiling: false,
                preRecordProbeSnapshotOnly: snapshotOnly,
                preRecordEvents: events,
                releaseWorkerAdmission: null,
                reacquireWorkerAdmissionAsync: null).ConfigureAwait(false);
        }
        finally
        {
            workerState.MarkOwnerTaskCompleted();
            var ownerSnapshot = workerState.Snapshot();
            if (!TryConvergeExitedEpgWorker(workerState, EpgWorkerTerminalReason.Completed, "independent_prerec_owner_finally", ownerSnapshot.AttemptGeneration))
                StartResidualEpgWorkerExitMonitor(workerState);
        }

        var snapshot = events.OrderBy(e => e.Start).ThenBy(e => e.EventId).ToArray();
        var success = preserveUserChainDbSeriesFollow ? imported > 0 : imported > 0 && snapshot.Length > 0;
        var runResult = success ? "OK" : "FAILED";
        Log("PRE_REC_EPG_INDEPENDENT_PROBE", $"TS{group.TsId}",
            $"result={runResult} runId={runId} group={group.Group} tuner={SafeLogValue(preferredRecordingTunerName)} observedEvents={snapshot.Length} importedEvents={(preserveUserChainDbSeriesFollow ? imported : 0)} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} dbWrite={(preserveUserChainDbSeriesFollow ? "epg_store_user_chain_protected" : "none")} normalEpgStatusOwner=untouched rule=pre_record_epg_independent_probe_contract");
        return new EpgCaptureResult(success, success ? 1 : 0, 1, preserveUserChainDbSeriesFollow ? imported : 0, runResult, success ? 0 : 1,
            "録画前時刻確認を終了しました",
            $"result={runResult}; independentPreRec=true; tuner={SafeLogValue(preferredRecordingTunerName)}")
        {
            CompletedScopes = success ? normalizedScope : string.Empty,
            MissingScopes = success ? string.Empty : normalizedScope,
            PreRecordEvents = snapshot
        };
    }

    private static string RunDiagnosticKey(string runId, string groupKey) => $"{runId}|{groupKey}";

    private string FormatMissingGroupDetails(IReadOnlyList<TsGroup> groups, HashSet<string> completedGroups)
    {
        var missing = groups
            .Where(g => !completedGroups.Contains(g.Key))
            .Select(g => $"{g.Group}:TS{g.TsId}:{SafeLogValue(g.Targets.FirstOrDefault()?.Name)}")
            .ToList();
        return missing.Count == 0 ? "-" : string.Join(",", missing);
    }

    private string FormatCaptureFailureSummary(IReadOnlyList<TsGroup> groups, HashSet<string> completedGroups, string runId)
    {
        var failures = groups
            .Where(g => !completedGroups.Contains(g.Key))
            .Select(g => currentRunCaptureFailures.TryGetValue(RunDiagnosticKey(runId, g.Key), out var f)
                ? $"{g.Group}:TS{g.TsId}:{SafeLogValue(g.Targets.FirstOrDefault()?.Name)}:{SafeLogValue(f.Reason)}:{SafeLogValue(f.Detail)}"
                : currentRunBlockedGroups.TryGetValue(RunDiagnosticKey(runId, g.Key), out var blocked) && !string.IsNullOrWhiteSpace(blocked)
                    ? $"{g.Group}:TS{g.TsId}:{SafeLogValue(g.Targets.FirstOrDefault()?.Name)}:Blocked:{SafeLogValue(blocked)}"
                    : $"{g.Group}:TS{g.TsId}:{SafeLogValue(g.Targets.FirstOrDefault()?.Name)}:unknown")
            .ToList();
        return failures.Count == 0 ? "-" : string.Join("|", failures);
    }

    private void RecordCaptureFailure(TsGroup group, string reason, string detail, int attempt, int maxAttempts, string runId)
    {
        var state = new EpgCaptureFailureState(group.Group, group.TsId, reason, detail, attempt, maxAttempts, DateTime.Now);
        currentRunCaptureFailures[RunDiagnosticKey(runId, group.Key)] = state;
        Log("EPG_CAPTURE_FAILURE_CLASSIFIED", $"TS{group.TsId}",
            $"epgGroup={group.Group} ts={group.TsId} service={SafeLogValue(group.Targets.FirstOrDefault()?.Name)} reason={SafeLogValue(reason)} detail={SafeLogValue(detail)} attempt={attempt}/{maxAttempts} rule=release_contract");
    }

    private string ClassifyImportFailure(TsGroup group, string tsFile)
    {
        try
        {
            if (!File.Exists(tsFile)) return "no_ts_file";
            var length = new FileInfo(tsFile).Length;
            if (length <= 0) return "empty_ts_file";
            return "import_failed_with_ts_file";
        }
        catch
        {
            return "import_failed_unknown_file_state";
        }
    }

    private string ResolveEpgRunBlockedReason(IReadOnlyList<TsGroup> groups, int completedCount, int totalGroups, string runId)
    {
        if (totalGroups <= 0 || completedCount > 0) return string.Empty;

        // BLOCKED is used only by PreRec when every requested TS group was rejected by its
        // recording-timeline safety boundary. Normal/定時EPGは開始後に録画timelineでBLOCKEDへ移行しない。
        var blockedReasons = new List<string>(groups.Count);
        foreach (var group in groups)
        {
            if (!currentRunBlockedGroups.TryGetValue(RunDiagnosticKey(runId, group.Key), out var reason)
                || string.IsNullOrWhiteSpace(reason))
            {
                return string.Empty;
            }
            blockedReasons.Add(NormalizeBlockedReason(reason));
        }

        return blockedReasons
            .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
            ?? "pre_record_boundary";
    }



    private static IReadOnlyList<TsGroup> BuildEpgTargetGroupFairQueue(IReadOnlyList<TsGroup> groups, IReadOnlyList<string> targetGroups)
    {
        if (groups.Count == 0) return Array.Empty<TsGroup>();

        var buckets = groups
            .GroupBy(g => NormalizeEpgTargetGroup(g.Group), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new Queue<TsGroup>(g.OrderBy(x => x.TsId).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

        var groupOrder = targetGroups
            .Select(NormalizeEpgTargetGroup)
            .Where(buckets.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var fallbackGroup in buckets.Keys.OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
        {
            if (!groupOrder.Contains(fallbackGroup, StringComparer.OrdinalIgnoreCase))
                groupOrder.Add(fallbackGroup);
        }

        if (groupOrder.Count <= 1)
            return groupOrder.Count == 0
                ? groups.OrderBy(g => g.TsId).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList()
                : buckets[groupOrder[0]].ToList();

        var ordered = new List<TsGroup>(groups.Count);
        while (ordered.Count < groups.Count)
        {
            var advanced = false;
            foreach (var group in groupOrder)
            {
                if (!buckets.TryGetValue(group, out var queue) || queue.Count == 0) continue;
                ordered.Add(queue.Dequeue());
                advanced = true;
            }

            if (!advanced) break;
        }

        return ordered;
    }

    private static string NormalizeEpgTargetGroup(string? group)
    {
        var raw = (group ?? string.Empty).Trim();
        var g = raw.ToUpperInvariant();
        return g switch
        {
            "GR" or "地上波" or "地デジ" => "GR",
            "BS" or "CS" or "BSCS" or "BS/CS" => "BSCS",
            "HYBRID" or "GRBSCS" or "GR/BSCS" or "GR/BS/CS" => "HYBRID",
            _ => raw
        };
    }

    private static string NormalizeBlockedReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "pre_record_boundary";
        var normalized = reason.Trim();
        if (normalized.Contains("window_insufficient", StringComparison.OrdinalIgnoreCase)) return "recording_window_insufficient";
        return normalized;
    }

    // ─── 1パス処理 ───────────────────────────────────────────────

    // NORMAL_EPG_RUN_PASS_CONTRACT:
    // This queue/admission layer belongs only to normal EPG runs. PreRec owns a separate run entry
    // and invokes CaptureGroupAsync directly so it cannot re-enter normal EPG run/status/queue state.
    private async Task<int> RunPassAsync(
        IReadOnlyList<TsGroup> groups,
        HashSet<string> completedGroups,
        int pass,
        CancellationToken ct,
        string runId,
        bool manageStatus)
    {
        if (groups.Count == 0) return 0;

        // release_contract: GlobalEpgJobQueue は維持しつつ、投入許可は LogicalTunerGroup 別の GroupEpgCapacity で制御する。
        // QueueOrder と CapacityControl を分離し、GR/BSCS の RecordingRoleUsableSlots を EpgWorkerAdmissionPolicy の正本にする。
        var status = tunerPool.GetStatus();
        var targetGroups = groups
            .Select(g => NormalizeEpgTargetGroup(g.Group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groupRecordableSlots = targetGroups.ToDictionary(
            g => g,
            g => status.Count(s => string.Equals(s.Group, g, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(s.Role, "Viewing", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        var groupLogicalUsableSlots = targetGroups.ToDictionary(
            g => g,
            g => Math.Max(0, tunerPool.CountEpgUsableFreeSlots(g)),
            StringComparer.OrdinalIgnoreCase);
        var groupEpgCapacity = targetGroups.ToDictionary(
            g => g,
            g => groupLogicalUsableSlots.TryGetValue(g, out var slots) ? slots : 0,
            StringComparer.OrdinalIgnoreCase);
        var totalRecordableSlots = groupRecordableSlots.Values.Sum();
        var totalLogicalEpgUsableSlots = groupLogicalUsableSlots.Values.Sum();
        var totalGroupEpgAdmissionCapacity = groupEpgCapacity.Values.Sum();
        var groupRecordableSummary = string.Join(",", targetGroups.Select(g => $"{g}:{(groupRecordableSlots.TryGetValue(g, out var recordableValue) ? recordableValue : 0)}"));
        var groupLogicalSummary = string.Join(",", targetGroups.Select(g => $"{g}:{(groupLogicalUsableSlots.TryGetValue(g, out var logicalValue) ? logicalValue : 0)}"));
        var groupCapacitySummary = string.Join(",", targetGroups.Select(g => $"{g}:{(groupEpgCapacity.TryGetValue(g, out var capacityValue) ? capacityValue : 0)}"));

        Log("EPG_GLOBAL_JOB_QUEUE_PLAN", "EPG",
            $"pass={pass} groups={groups.Count} targetGroups=[{string.Join(",", targetGroups)}] recordableSlots={totalRecordableSlots} logicalEpgUsableSlots={totalLogicalEpgUsableSlots} groupRecordable=[{groupRecordableSummary}] groupLogical=[{groupLogicalSummary}] groupCapacity=[{groupCapacitySummary}] admissionCapacity={totalGroupEpgAdmissionCapacity} unmanagedExternalTvTest=out_of_scope policy=group_capacity_epg_job_queue rule=release_contract");

        var orderedGroups = BuildEpgTargetGroupFairQueue(groups, targetGroups);
        var admissibleHeadCount = Math.Max(1, Math.Min(totalGroupEpgAdmissionCapacity <= 0 ? targetGroups.Count : totalGroupEpgAdmissionCapacity, orderedGroups.Count));
        var queueHeadSummary = string.Join(",", orderedGroups
            .Take(admissibleHeadCount)
            .Select(g => $"{NormalizeEpgTargetGroup(g.Group)}:{g.TsId}"));
        Log("EPG_TARGET_GROUP_QUEUE_PLAN", "EPG",
            $"pass={pass} queuePolicy=EpgTargetGroupFairQueue admissionPolicy=GroupEpgCapacity targetGroups=[{string.Join(",", targetGroups)}] queueHead=[{queueHeadSummary}] groupCapacity=[{groupCapacitySummary}] admissionCapacity={totalGroupEpgAdmissionCapacity} rule=release_contract");

        var groupSemaphores = groupEpgCapacity
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => new SemaphoreSlim(kv.Value), StringComparer.OrdinalIgnoreCase);
        using var workerAdmissionReleased = new SemaphoreSlim(0);
        if (groupSemaphores.Count == 0)
        {
            Log("EPG_GROUP_CAPACITY_ADMISSION", "EPG",
                $"result=NO_CAPACITY pass={pass} targetGroups=[{string.Join(",", targetGroups)}] groupCapacity=[{groupCapacitySummary}] unmanagedExternalTvTest=out_of_scope action=skip_new_workers rule=release_contract");
            return 0;
        }

        var imported = 0;
        var tasks = new List<Task>();
        var pendingGroups = orderedGroups.ToList();
        var gate = new object();

        Task LaunchAsync(TsGroup group, SemaphoreSlim groupSemaphore)
        {
            var g = group;
            var releaseSemaphore = groupSemaphore;
            var admissionReleased = 0;
            void ReleaseWorkerAdmission()
            {
                if (Interlocked.Exchange(ref admissionReleased, 1) == 0)
                {
                    releaseSemaphore.Release();
                    workerAdmissionReleased.Release();
                }
            }
            var workerTaskState = RegisterActiveEpgWorkerTask(g, pass, runId);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    async Task ReacquireWorkerAdmissionAsync(CancellationToken token)
                    {
                        await releaseSemaphore.WaitAsync(token).ConfigureAwait(false);
                        Interlocked.Exchange(ref admissionReleased, 0);
                    }

                    var count = await CaptureGroupAsync(
                        g,
                        pass,
                        ct,
                        isPreRecordCheck: false,
                        workerTaskState,
                        maxCaptureSeconds: null,
                        expectedNetworkId: null,
                        expectedTransportStreamId: null,
                        expectedServiceId: null,
                        expectedEventId: null,
                        expectedStartTime: null,
                        expectedEndTime: null,
                        preferredRecordingTunerName: null,
                        preTuneChainPosition: null,
                        preTuneAction: null,
                        preTuneKeepWorkerUntilSafetyCeiling: false,
                        preRecordProbeSnapshotOnly: false,
                        preRecordEvents: null,
                        releaseWorkerAdmission: ReleaseWorkerAdmission,
                        reacquireWorkerAdmissionAsync: ReacquireWorkerAdmissionAsync);

                    if (HasUsableCapturedEpgEvents(g, count))
                    {
                        int completedGroupCount;
                        lock (gate)
                        {
                            completedGroups.Add(g.Key);
                            imported += count;
                            completedGroupCount = completedGroups.Count;
                        }
                        if (manageStatus) SetStatus(s => s with { CompletedGroups = completedGroupCount });
                    }
                    else if (count > 0)
                    {
                        lock (gate)
                        {
                            imported += count;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    workerTaskState.TryFinalize(EpgWorkerTerminalReason.Cancelled);
                    // RunPassAsync側で新規投入を止め、全worker収束後に上位token確認へ戻す。
                }
                catch (EpgWorkerProcessMissingException ex)
                {
                    workerTaskState.TryFinalize(EpgWorkerTerminalReason.ProcessMissing);
                    RecordCaptureFailure(
                        g,
                        "worker_process_missing",
                        $"cycle={SafeLogValue(ex.CycleId)}",
                        attempt: 1,
                        maxAttempts: 2,
                        runId: workerTaskState.RunId);
                    Log("EPG_WORKER_TASK_FAIL", $"TS{g.TsId}",
                        $"result=ERROR pass={pass} group={g.Group} worker={SafeLogValue($"{g.Group}-{g.TsId}")} reason=process_missing cycle={SafeLogValue(ex.CycleId)} action=classify_group_failed_continue_other_workers rule=release_contract");
                }
                catch (Exception ex)
                {
                    workerTaskState.TryFinalize(EpgWorkerTerminalReason.Failed);
                    RecordCaptureFailure(
                        g,
                        "worker_task_exception",
                        $"type={ex.GetType().Name} message={SafeLogValue(ex.Message)}",
                        attempt: 1,
                        maxAttempts: 2,
                        runId: workerTaskState.RunId);
                    Log("EPG_WORKER_TASK_FAIL", $"TS{g.TsId}",
                        $"result=ERROR pass={pass} group={g.Group} worker={SafeLogValue($"{g.Group}-{g.TsId}")} error={ex.GetType().Name}:{SafeLogValue(ex.Message)} action=classify_group_failed_continue_other_workers rule=release_contract");
                }
                finally
                {
                    workerTaskState.MarkOwnerTaskCompleted();
                    var ownerSnapshot = workerTaskState.Snapshot();
                    var converged = TryConvergeExitedEpgWorker(workerTaskState, EpgWorkerTerminalReason.Completed, "owner_task_finally", ownerSnapshot.AttemptGeneration);
                    if (!converged)
                    {
                        workerTaskState.DeferAdmissionRelease(ReleaseWorkerAdmission);
                        StartResidualEpgWorkerExitMonitor(workerTaskState);
                    }
                    else
                    {
                        ReleaseWorkerAdmission();
                    }
                }
            }, CancellationToken.None));
            return Task.CompletedTask;
        }

        try
        {
            try
            {
                while (pendingGroups.Count > 0 && !ct.IsCancellationRequested)
                {
                    var launchedInThisScan = false;

                    for (var i = 0; i < pendingGroups.Count; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        var group = pendingGroups[i];
                        lock (gate)
                        {
                            if (completedGroups.Contains(group.Key))
                            {
                                pendingGroups.RemoveAt(i--);
                                continue;
                            }
                        }

                        var groupKey = NormalizeEpgTargetGroup(group.Group);
                        if (!groupSemaphores.TryGetValue(groupKey, out var groupSemaphore))
                        {
                            Log("EPG_GROUP_CAPACITY_ADMISSION", $"TS{group.TsId}",
                                $"result=SKIP_NO_GROUP_CAPACITY pass={pass} group={groupKey} groupCapacity=[{groupCapacitySummary}] rule=release_contract");
                            pendingGroups.RemoveAt(i--);
                            continue;
                        }

                        if (!groupSemaphore.Wait(0))
                            continue;

                        pendingGroups.RemoveAt(i--);
                        launchedInThisScan = true;
                        await LaunchAsync(group, groupSemaphore);
                    }

                    if (launchedInThisScan)
                        continue;

                    if (tasks.Count == 0)
                        break;

                    // CaptureGroupAsync全体（TS解析・DB保存）ではなく、worker終了とチューナー解放を待つ。
                    // これにより前局TS解析中でも、空いた取得枠へ次局workerを直ちに投入できる。
                    await workerAdmissionReleased.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 通知Semaphoreを先に破棄しない。新規投入だけを止め、起動済みworkerの
                // finallyが取得枠と通知を返し終えるまで下のWhenAllで収束させる。
            }

            if (ct.IsCancellationRequested)
            {
                epgRunsAcceptingNewWorkers.TryRemove(runId, out _);
                Log("EPG_CANCEL_NEW_WORKERS_STOPPED", "EPG", $"pass={pass} reason=ct_cancelled action=await_started_workers_before_dispose rule=release_contract");
            }

            await Task.WhenAll(tasks);
            return imported;
        }
        finally
        {
            foreach (var semaphore in groupSemaphores.Values)
                semaphore.Dispose();
        }
    }


    private bool HasUsableCapturedEpgEvents(TsGroup group, int imported)
    {
        if (imported <= 0) return false;

        // EIT schedule section completeness is diagnostic. A short/shallow normal EPG capture can
        // legitimately end with missing schedule segments while still yielding usable events.
        // PreRec completion is evaluated by RunIndependentPreRecordProbeAsync and does not enter here.
        Log("EPG_COMPLETION", $"TS{group.TsId}",
            $"result=USABLE_IMPORT imported={imported} purpose=normal_epg_capture group={group.Group} services={group.Targets.Count} completionBasis=imported_events_present sectionCompleteness=diagnostic_only rule=release_contract");

        return true;
    }

    private async Task<EpgWorkerLaunchResult> StartEpgRecordingWithLaunchGateAsync(
        string workerName,
        TsGroup group,
        string bonDriverFileName,
        string did,
        string channelArgument,
        string tsFile,
        int effectiveWait,
        CancellationToken ct,
        bool isPreRecordCheck,
        string? displayServiceName = null,
        string? preferredRecordingTunerName = null,
        string? preTuneChainPosition = null,
        string? preTuneAction = null,
        bool preTuneKeepWorkerUntilSafetyCeiling = false,
        ushort? expectedEventId = null,
        DateTime? expectedStartTime = null,
        DateTime? expectedEndTime = null,
        Func<EpgWorkerLaunchResult, bool>? bindStartedProcessOwnership = null,
        Func<EpgWorkerLaunchResult, string, Task<bool>>? retireUnreadyStartupAsync = null)
    {
        // EPGも録画/視聴と同じ物理デバイス開始契約へ統合する。
        // lease管理やDID選択はTunerPoolの責務のまま変更せず、ここでは
        // BonDriver Open -> SetChannel -> TS-read開始までの短いstartup境界だけを保護する。
        // GRとBSCSは別gateなので両波の同時起動能力は維持される。
        using var tunerDeviceAccess = await TunerDeviceAccessGate.EnterAsync(
            $"EPG_START {workerName} TS{group.TsId}",
            group.Group,
            msg => Log("TUNER_DEVICE_LOCK", $"TS{group.TsId}", msg),
            ct).ConfigureAwait(false);

        EpgWorkerLaunchResult launch;
        await epgLaunchStartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Log("EPG_LAUNCH_GATE", $"TS{group.TsId}",
                $"enter worker={workerName} group={group.Group} bonDriver={bonDriverFileName} did={did} processStartOnly=true route=TvAIrEpgRec mode=epg-ts rule=epg_worker_route_contract");

            // EXTERNAL_TVTEST_PT3_RESPONSIBILITY_BOUNDARY:
            // ここでのViewing-role保護はTvAIr自身の設定契約だけを対象とする。管理外TVTestは観測しない。
            // 物理PT3側の競合・再配置はBonDriver_PTx/PT3に任せ、Host独自の外部利用回避を入れてはならない。
            var viewingAudit = TvTestProcessAuditor.EmitViewingProtectionAudit(
                log,
                "EPG_START_BEFORE_LAUNCH",
                $"TS{group.TsId}",
                did,
                bonDriverFileName,
                tunerPool.GetProtectedViewingDids(),
                blockOnSameDid: true);
            if (viewingAudit.ShouldBlock || tunerPool.IsViewingReservedDid(did))
            {
                Log("EPG_VIEWING_PROTECTION_BLOCK", $"TS{group.TsId}",
                    $"worker={workerName} targetDid={did} bonDriver={bonDriverFileName} targetIsViewingRole={viewingAudit.TargetIsViewingRole} unmanagedExternalTvTest=out_of_scope");
                return new EpgWorkerLaunchResult(false, 0, "EPG blocked by configured viewing-role protection", null, null, null);
            }

            launch = StartTvAIrEpgRecForEpg(
                workerName,
                group,
                bonDriverFileName,
                did,
                channelArgument,
                tsFile,
                effectiveWait,
                isPreRecordCheck,
                displayServiceName,
                preferredRecordingTunerName,
                preTuneChainPosition,
                preTuneAction,
                preTuneKeepWorkerUntilSafetyCeiling,
                expectedEventId,
                expectedStartTime,
                expectedEndTime);

            Log("EPG_LAUNCH_GATE", $"TS{group.TsId}",
                $"exit worker={workerName} success={launch.Success} pid={launch.ProcessId} processStartOnly=true route=TvAIrEpgRec mode=epg-ts rule=epg_worker_route_contract");
        }
        finally
        {
            epgLaunchStartGate.Release();
        }

        if (!launch.Success || launch.ProcessId <= 0)
            return launch;

        // Process identity becomes part of the exact worker-attempt ownership immediately after
        // CreateProcess succeeds. Startup-boundary observation can take up to 15 seconds, so deferring
        // this binding until after OpenTuner/SetChannel/TS-read evidence would expose a live process
        // with stale/empty owner identity to reconciliation.
        if (bindStartedProcessOwnership is not null && !bindStartedProcessOwnership(launch))
        {
            RequestTvAIrEpgRecStopSignalOnly(launch, workerName, group, "process_ownership_bind_rejected");
            return launch with
            {
                Success = false,
                Message = "EPG worker process ownership binding was rejected before startup observation"
            };
        }

        var startup = await WaitForEpgWorkerStartupBoundaryAsync(launch, workerName, group, ct).ConfigureAwait(false);
        if (!string.Equals(startup.Result, "READY", StringComparison.OrdinalIgnoreCase))
        {
            // PHYSICAL_EPG_DEVICE_START_INVARIANT:
            // startupがREADYへ収束していないworkerを残したままgroup device gateを解放すると、
            // 同一BonDriver系で次workerがCreate/Openへ入り、未収束startupと競合して失敗連鎖を起こす。
            // FAILED/PROCESS_EXIT/TIMEOUTでは、このstartup ownerが同波device gateを保持したまま
            // exact attemptの停止・exit確認を行う。killが必要でも既保持gateを再取得せず、
            // owner taskを有限収束させてからこのusing scopeを抜ける。安全にkill対象を証明できない場合だけ
            // Process drain barrierへ引き渡し、次Enterを実process exitまで待たせる。固定settle delayは使わない。
            var retired = retireUnreadyStartupAsync is not null
                && await retireUnreadyStartupAsync(launch, startup.Result).ConfigureAwait(false);
            if (!retired && launch.ProcessId > 0)
            {
                // owner identityが確定できずその場でkillできない場合でも、Process handleをdrain barrierへ登録する。
                // gate自体を解放した後の次Enterは、この実processのexitまで進まない。
                TunerDeviceAccessGate.RegisterProcessDrain(
                    group.Group,
                    launch.ProcessId,
                    msg => Log("TUNER_DEVICE_LOCK", $"TS{group.TsId}", msg));
            }
            Log("EPG_DEVICE_START_BOUNDARY", $"TS{group.TsId}",
                $"worker={workerName} pid={launch.ProcessId} group={group.Group} result={startup.Result} stage={SafeLogValue(startup.Stage)} elapsedMs={startup.ElapsedMs} " +
                $"retiredBeforeGateRelease={retired} drainBarrierRegistered={!retired && launch.ProcessId > 0} action={(retired ? "release_gate_after_failed_worker_exit" : "release_gate_with_process_drain_barrier")} rule=physical_epg_device_start_lifecycle_contract");
            return launch with
            {
                Success = false,
                Message = $"EPG worker startup did not reach READY: result={startup.Result} stage={startup.Stage}"
            };
        }

        Log("EPG_DEVICE_START_BOUNDARY", $"TS{group.TsId}",
            $"worker={workerName} pid={launch.ProcessId} group={group.Group} result=READY stage={SafeLogValue(startup.Stage)} elapsedMs={startup.ElapsedMs} " +
            "action=release_group_device_gate_after_ready rule=physical_epg_device_start_lifecycle_contract");
        return launch;
    }

    private async Task<(string Result, string Stage, int ElapsedMs)> WaitForEpgWorkerStartupBoundaryAsync(
        EpgWorkerLaunchResult launch,
        string workerName,
        TsGroup group,
        CancellationToken ct)
    {
        const int startupTimeoutMs = 15000;
        const int pollMs = 25;
        var started = Stopwatch.StartNew();
        string lastStage = string.Empty;

        while (started.ElapsedMilliseconds < startupTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            if (TryReadWorkerProgressSnapshot(launch.ProgressPath, out var stages))
            {
                foreach (var (stage, message) in stages)
                {
                    if (!string.IsNullOrWhiteSpace(stage))
                        lastStage = stage;

                    // TvAIrEpgRec routes BonDriver/TS runtime progress through the common route and
                    // prefixes those stages with "common_route_". Normalize only for boundary
                    // classification so the host observes the actual TS-read start/failure contract
                    // without depending on whether the progress came directly from the TS runtime
                    // or through the common-route facade.
                    var boundaryStage = NormalizeEpgWorkerProgressStage(stage);

                    if (string.Equals(boundaryStage, "tsvariant_begin", StringComparison.OrdinalIgnoreCase))
                        return ("READY", stage, (int)started.ElapsedMilliseconds);

                    if (IsTerminalEpgStartupFailureStage(boundaryStage, message))
                        return ("FAILED", stage, (int)started.ElapsedMilliseconds);
                }
            }

            try
            {
                using var process = Process.GetProcessById(launch.ProcessId);
                if (process.HasExited)
                    return ("PROCESS_EXIT", lastStage, (int)started.ElapsedMilliseconds);
            }
            catch (ArgumentException)
            {
                return ("PROCESS_EXIT", lastStage, (int)started.ElapsedMilliseconds);
            }

            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }

        Log("EPG_DEVICE_START_BOUNDARY", $"TS{group.TsId}",
            $"worker={workerName} pid={launch.ProcessId} group={group.Group} result=TIMEOUT lastStage={SafeLogValue(lastStage)} timeoutMs={startupTimeoutMs} " +
            "action=release_gate_without_fixed_settle_delay rule=epg_worker_device_start_contract");
        return ("TIMEOUT", lastStage, startupTimeoutMs);
    }

    private static bool TryReadWorkerProgressSnapshot(
        string? progressPath,
        out List<(string Stage, string Message)> stages)
    {
        stages = new List<(string Stage, string Message)>();
        if (string.IsNullOrWhiteSpace(progressPath) || !File.Exists(progressPath)) return false;

        try
        {
            using var stream = new FileStream(
                progressPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var stage = root.TryGetProperty("Stage", out var stageElement)
                        ? stageElement.GetString() ?? string.Empty
                        : root.TryGetProperty("stage", out stageElement)
                            ? stageElement.GetString() ?? string.Empty
                            : string.Empty;
                    var message = root.TryGetProperty("Message", out var messageElement)
                        ? messageElement.GetString() ?? string.Empty
                        : root.TryGetProperty("message", out messageElement)
                            ? messageElement.GetString() ?? string.Empty
                            : string.Empty;

                    if (!string.IsNullOrWhiteSpace(stage))
                        stages.Add((stage, message));
                }
                catch (JsonException)
                {
                    // The worker may be appending the final line while this snapshot is read.
                    // Ignore only that incomplete observation and retry on the next poll.
                }
            }

            return stages.Count > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }


    private static string NormalizeEpgWorkerProgressStage(string stage)
    {
        const string commonRoutePrefix = "common_route_";
        if (stage.StartsWith(commonRoutePrefix, StringComparison.OrdinalIgnoreCase))
            return stage[commonRoutePrefix.Length..];
        return stage;
    }

    private static bool IsTerminalEpgStartupFailureStage(string stage, string message)
    {
        if (string.Equals(stage, "integrated_tsread_failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stage, "integrated_tsread_exception_finalized", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stage, "common_ts_route_ready_gate_blocked", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(stage, "tsvariant_open_tuner", StringComparison.OrdinalIgnoreCase)
            && message.Contains("result=NG", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(stage, "tsvariant_summary", StringComparison.OrdinalIgnoreCase)
            && message.Contains("result=NG", StringComparison.OrdinalIgnoreCase);
    }

    private EpgWorkerLaunchResult StartTvAIrEpgRecForEpg(
        string workerName,
        TsGroup group,
        string bonDriverFileName,
        string did,
        string channelArgument,
        string tsFile,
        int effectiveWait,
        bool isPreRecordCheck,
        string? displayServiceName,
        string? preferredRecordingTunerName = null,
        string? preTuneChainPosition = null,
        string? preTuneAction = null,
        bool preTuneKeepWorkerUntilSafetyCeiling = false,
        ushort? expectedEventId = null,
        DateTime? expectedStartTime = null,
        DateTime? expectedEndTime = null)
    {
        var exe = ResolveTvAIrEpgRecPath();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return new EpgWorkerLaunchResult(false, 0, "TvAIrEpgRec.exe が見つかりません。", null, null, null);
        }

        var runtimeDir = Path.Combine(AppContext.BaseDirectory, "runtime", "tvairepgrec-production-epg");
        Directory.CreateDirectory(runtimeDir);

        var now = DateTime.Now;
        var stamp = now.ToString("yyyyMMdd_HHmmss_fff");
        var jobId = $"epg_{workerName}_{stamp}_{Guid.NewGuid():N}";
        var jobPath = Path.Combine(runtimeDir, $"epg_job_{workerName}_{stamp}_{Guid.NewGuid():N}.json");
        var resultPath = Path.Combine(runtimeDir, $"epg_result_{workerName}_{stamp}_{Guid.NewGuid():N}.json");
        var progressPath = Path.Combine(runtimeDir, $"epg_progress_{workerName}_{stamp}_{Guid.NewGuid():N}.jsonl");
        var stopSignalPath = Path.Combine(runtimeDir, $"epg_stop_{workerName}_{stamp}_{Guid.NewGuid():N}.signal");
        var runtimeStatsPath = tsFile + ".runtime.jsonl";
        var target = group.Targets.FirstOrDefault();
        var bonDriverPath = ResolveBonDriverPathForEpg(bonDriverFileName);
        var displayLogoPaths = target is null
            ? ServiceLogoDisplayPaths.Empty
            : serviceLogoStore.ResolveDisplayLogoPaths(target.OriginalNetworkId, target.TransportStreamId, target.ServiceId, displayServiceName ?? target.Name);
        var displayLogoPath = displayLogoPaths.TitleBarLogoPath;
        var centerLogoPath = displayLogoPaths.CenterLogoPath;

        var isTerrestrialLogoScope = string.Equals(group.Group, "GR", StringComparison.OrdinalIgnoreCase);
        var getStreamVariant = isTerrestrialLogoScope
            ? "pointer-vtable6-epg-normalize-gr-logo-opportunistic"
            : "pointer-vtable6-epg-normalize";
        var logoPolicy = isTerrestrialLogoScope
            ? "gr_opportunistic_accept_cdt_like_sections"
            : "bscs_opportunistic_no_dedicated_logo_deep_scan";

        var job = new Dictionary<string, object?>
        {
            ["jobId"] = jobId,
            ["mode"] = isPreRecordCheck ? "epg-check" : "epg",
            ["group"] = group.Group,
            ["tuner"] = workerName,
            ["did"] = did,
            ["bonDriver"] = bonDriverFileName,
            ["bonDriverPath"] = bonDriverPath,
            ["tvTestExecutablePath"] = ini.TvTestExecutablePath,
            ["outputPath"] = tsFile,
            ["resultPath"] = resultPath,
            ["progressPath"] = progressPath,
            ["runtimeStatsPath"] = runtimeStatsPath,
            ["cancelSignalPath"] = stopSignalPath,
            ["tsReadSeconds"] = effectiveWait,
            ["channels"] = group.Targets.Select(t => new Dictionary<string, object?>
            {
                ["serviceName"] = t.Name,
                ["networkId"] = (int)t.OriginalNetworkId,
                ["transportStreamId"] = (int)t.TransportStreamId,
                ["serviceId"] = (int)t.ServiceId,
                ["channelSpace"] = t.ResolvedSpace,
                ["channelIndex"] = t.ResolvedChannelIndex,
                ["channelArgument"] = t.ChannelArgument
            }).ToList(),
            ["metadata"] = new Dictionary<string, string>
            {
                ["purpose"] = "normal_epg_capture_ts_file",
                ["owner"] = "EpgCapture",
                ["worker"] = workerName,
                ["displayServiceName"] = displayServiceName ?? target?.Name ?? $"TS{group.TsId}",
                ["displayTitle"] = displayServiceName ?? target?.Name ?? $"TS{group.TsId}",
                ["displayLogoPath"] = displayLogoPath ?? string.Empty,
                ["titleBarLogoPath"] = displayLogoPath ?? string.Empty,
                ["centerLogoPath"] = centerLogoPath ?? string.Empty,
                ["displayLogoTitleBarType"] = displayLogoPaths.TitleBarLogoType?.ToString() ?? string.Empty,
                ["displayLogoCenterType"] = displayLogoPaths.CenterLogoType?.ToString() ?? string.Empty,
                ["displayLogoTarget"] = "worker_titlebar_center_only",
                ["channelArgument"] = channelArgument,
                // TvAIrEpgRec EPG modeでは短い ready-only/continuous デフォルト秒数を使わない。
                // The default capped the read to 15s/200 chunks, which produced 51,200-packet TS files and
                // low EPG imports.  Use the EPG-normalize route so the worker honors the requested EPG seconds
                // and keeps the TS reader open long enough for schedule EIT to arrive.
                ["getStreamVariant"] = getStreamVariant,
                ["logoAcquisitionPolicy"] = logoPolicy,
                ["targetEventsMin"] = "1",
                ["readyThreshold"] = "50",
                ["modeScope"] = isPreRecordCheck ? "target_event_pre_record_check" : "transport_stream_schedule_epg",
                ["recordServiceScopeShared"] = "false",
                ["allocationRouteContract"] = isPreRecordCheck ? "pre_record_epg_check_plan" : "epg_entry_transport_stream_plan",
                ["targetSidCount"] = group.Targets.Count.ToString(),
                ["targetSids"] = string.Join(",", group.Targets.Select(t => t.ServiceId).OrderBy(x => x)),
                ["expectedEventId"] = expectedEventId?.ToString() ?? string.Empty,
                ["expectedEventStart"] = expectedStartTime?.ToString("O") ?? string.Empty,
                ["expectedEventEnd"] = expectedEndTime?.ToString("O") ?? string.Empty,
                ["rule"] = "release_contract",
                // TvAIrEpgRec process icon visibility follows the user setting for active EPG workers.
                ["taskbarIconVisible"] = ini.ShowTvAIrEpgRecTaskbarIcon ? "true" : "false",
                ["didSelectionTransport"] = "process_command_line_/DID",
                ["preTuneEnabled"] = isPreRecordCheck && preTuneKeepWorkerUntilSafetyCeiling && !string.IsNullOrWhiteSpace(preferredRecordingTunerName) ? "true" : "false",
                ["preTuneChainPosition"] = preTuneChainPosition ?? string.Empty,
                ["preTuneAction"] = preTuneAction ?? string.Empty,
                ["preTuneDisplayLabel"] = string.Empty,
                ["preTuneTargetDisplayLabel"] = isPreRecordCheck && preTuneKeepWorkerUntilSafetyCeiling && !string.IsNullOrWhiteSpace(preferredRecordingTunerName) ? "録画準備" : string.Empty,
                ["keepWorkerUntilSafetyCeiling"] = preTuneKeepWorkerUntilSafetyCeiling ? "true" : "false"
            }
        };

        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, EpgJobJsonOptions));

        var launchKind = isPreRecordCheck ? TvAIrEpgRecLaunchKind.PreRecordEpgCheckWorker : TvAIrEpgRecLaunchKind.EpgTransportStreamWorker;
        var showWorkerTaskbarIcon = WorkerProcessStartInfoFactory.IsTaskbarIconVisible(launchKind, ini.ShowTvAIrEpgRecTaskbarIcon);
        var windowPolicy = WorkerProcessStartInfoFactory.GetWindowPolicy(launchKind, ini.ShowTvAIrEpgRecTaskbarIcon);
        var psi = WorkerProcessStartInfoFactory.CreateTvAIrEpgRec(exe, launchKind, ini.ShowTvAIrEpgRecTaskbarIcon);
        WorkerProcessStartInfoFactory.AppendPhysicalTunerDidArgument(psi, did);
        psi.ArgumentList.Add("--job");
        psi.ArgumentList.Add(jobPath);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(isPreRecordCheck ? "epg-check" : "epg");
        psi.ArgumentList.Add("--result");
        psi.ArgumentList.Add(resultPath);
        psi.ArgumentList.Add("--progress");
        psi.ArgumentList.Add(progressPath);

        try
        {
            var process = Process.Start(psi);
            if (process is null)
                return new EpgWorkerLaunchResult(false, 0, "TvAIrEpgRec.exe の起動に失敗しました。", stopSignalPath, resultPath, progressPath);

            Log("TVAIREPGREC_EPG_ROUTE", $"TS{group.TsId}",
                $"result=SELECTED pid={process.Id} exe={SafeLogValue(exe)} job={SafeLogValue(jobPath)} result={SafeLogValue(resultPath)} progress={SafeLogValue(progressPath)} stopSignal={SafeLogValue(stopSignalPath)} service={SafeLogValue(displayServiceName ?? target?.Name)} nid={(target?.OriginalNetworkId.ToString() ?? "-")} tsid={group.TsId} sid={(target?.ServiceId.ToString() ?? "-")} targetSidCount={group.Targets.Count} targetSids=[{string.Join(",", group.Targets.Select(t => t.ServiceId).OrderBy(x => x))}] scope={(isPreRecordCheck ? "target_event_pre_record_check" : "transport_stream_schedule_epg")} recordServiceScopeShared=false channelArgument={SafeLogValue(channelArgument)} bonDriverSetChannelSpace={(target?.ResolvedSpace.ToString() ?? "-")} bonDriverSetChannelIndex={(target?.ResolvedChannelIndex.ToString() ?? "-")} did={did} seconds={effectiveWait} getStreamVariant={SafeLogValue(getStreamVariant)} logoPolicy={SafeLogValue(logoPolicy)} output={SafeLogValue(tsFile)} route=TvAIrEpgRec mode={(isPreRecordCheck ? "epg-check" : "epg")} launchKind={launchKind} taskbarIconVisible={showWorkerTaskbarIcon} windowPolicy={windowPolicy} preTuneChainPosition={SafeLogValue(preTuneChainPosition)} preTuneAction={SafeLogValue(preTuneAction)} keepWorkerUntilSafetyCeiling={preTuneKeepWorkerUntilSafetyCeiling} titleBarLogoPath={SafeLogValue(displayLogoPath)} centerLogoPath={SafeLogValue(centerLogoPath)} logoTarget=worker_titlebar_center_only rule=epg_worker_display_contract");

            return new EpgWorkerLaunchResult(true, process.Id,
                $"Started PID={process.Id}: {exe} --job \"{jobPath}\" --mode {(isPreRecordCheck ? "epg-check" : "epg")} output={tsFile}",
                stopSignalPath,
                resultPath,
                progressPath,
                jobPath);
        }
        catch (Exception ex)
        {
            return new EpgWorkerLaunchResult(false, 0, $"TvAIrEpgRec起動例外: {ex.GetType().Name} {ex.Message}", stopSignalPath, resultPath, progressPath);
        }
    }

    private static readonly JsonSerializerOptions EpgJobJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private string ResolveBonDriverPathForEpg(string bonDriverFileName)
    {
        if (Path.IsPathRooted(bonDriverFileName)) return bonDriverFileName;
        var dir = ini.BonDriverDirectory;
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var candidate = Path.Combine(dir, bonDriverFileName);
            if (File.Exists(candidate)) return candidate;
        }
        return bonDriverFileName;
    }

    private static string? ResolveTvAIrEpgRecPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "TvAIrEpgRec.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "TvAIrEpgRec", "TvAIrEpgRec.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TvAIrEpgRec", "bin", "Release", "net8.0-windows", "TvAIrEpgRec.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TvAIrEpgRec", "bin", "Debug", "net8.0-windows", "TvAIrEpgRec.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void RequestTvAIrEpgRecStopSignalOnly(EpgWorkerLaunchResult launch, string workerName, TsGroup group, string reason)
    {
        if (string.IsNullOrWhiteSpace(launch.StopSignalPath)) return;
        try
        {
            File.WriteAllText(launch.StopSignalPath, DateTimeOffset.Now.ToString("O"));
            Log("TVAIREPGREC_EPG_STOP_SIGNAL", $"TS{group.TsId}",
                $"result=SENT pid={launch.ProcessId} worker={workerName} reason={SafeLog(reason)} stopSignal={SafeLogValue(launch.StopSignalPath)} rule=epg_worker_identity_contract");
        }
        catch (Exception ex)
        {
            Log("TVAIREPGREC_EPG_STOP_SIGNAL", $"TS{group.TsId}",
                $"result=NG pid={launch.ProcessId} worker={workerName} reason={SafeLog(reason)} error={SafeLog(ex.Message)} rule=epg_worker_identity_contract");
        }
    }

    private void RequestTvAIrEpgRecStopOrKill(EpgWorkerLaunchResult launch, string workerName, TsGroup group, string reason)
    {
        if (!string.IsNullOrWhiteSpace(launch.StopSignalPath))
        {
            try
            {
                File.WriteAllText(launch.StopSignalPath, DateTimeOffset.Now.ToString("O"));
                Log("TVAIREPGREC_EPG_STOP_SIGNAL", $"TS{group.TsId}",
                    $"result=SENT pid={launch.ProcessId} worker={workerName} reason={SafeLog(reason)} stopSignal={SafeLogValue(launch.StopSignalPath)} rule=epg_worker_route_contract");
                return;
            }
            catch (Exception ex)
            {
                Log("TVAIREPGREC_EPG_STOP_SIGNAL", $"TS{group.TsId}",
                    $"result=NG pid={launch.ProcessId} worker={workerName} reason={SafeLog(reason)} error={SafeLog(ex.Message)} action=defer_kill_until_identity_recheck rule=epg_worker_identity_contract");
            }
        }

        // Do not kill here. The caller waits, re-checks PID/start-time/path ownership,
        // and only then may kill a still-matching worker. This prevents a stop-signal
        // write failure from turning a stale PID into an unrelated-process kill.
    }

    private async Task<bool> StopAndRetireActiveEpgWorkerAsync(
        EpgWorkerLaunchResult launch,
        string workerName,
        TsGroup group,
        string phase,
        string reason,
        ActiveEpgWorkerTask ownerState,
        long expectedAttemptGeneration,
        TimeSpan? gracefulTimeout = null,
        bool deviceGateAlreadyHeld = false)
    {
        if (!launch.Success || launch.ProcessId <= 0) return false;

        var pid = launch.ProcessId;
        var timeout = gracefulTimeout ?? TimeSpan.FromSeconds(4);
        var initialSnapshot = ownerState.Snapshot();
        if (initialSnapshot.AttemptGeneration != expectedAttemptGeneration)
        {
            Log("EPG_CANCEL_PROCESS_IDENTITY", $"TS{group.TsId}",
                $"result=STALE_ATTEMPT pid={pid} worker={workerName} phase={SafeLog(phase)} expectedAttemptGeneration={expectedAttemptGeneration} currentAttemptGeneration={initialSnapshot.AttemptGeneration} action=do_not_touch_current_attempt rule=epg_worker_attempt_ownership_contract");
            return false;
        }
        var identity = GetOwnedWorkerProcessIdentity(initialSnapshot);

        if (identity == EpgWorkerProcessIdentityState.ReusedPid)
        {
            ownerState.MarkProcessExitObserved(expectedAttemptGeneration);
            Log("EPG_CANCEL_PROCESS_IDENTITY", $"TS{group.TsId}",
                $"result=REUSED_PID pid={pid} worker={workerName} phase={SafeLog(phase)} action=do_not_stop_other_process rule=epg_worker_identity_contract");
            return true;
        }

        if (identity == EpgWorkerProcessIdentityState.IdentityUnknown)
        {
            RequestTvAIrEpgRecStopSignalOnly(launch, workerName, group, reason);
            Log("EPG_CANCEL_PROCESS_IDENTITY", $"TS{group.TsId}",
                $"result=IDENTITY_UNKNOWN pid={pid} worker={workerName} phase={SafeLog(phase)} action=signal_only_keep_tracked rule=epg_worker_identity_contract");
            return false;
        }

        if (identity == EpgWorkerProcessIdentityState.Match)
        {
            Log("EPG_CANCEL_PROCESS_STOP_REQUEST", $"TS{group.TsId}",
                $"pid={pid} worker={workerName} phase={SafeLog(phase)} route=TvAIrEpgRec reason={SafeLog(reason)} rule=epg_worker_route_contract");
            RequestTvAIrEpgRecStopOrKill(launch, workerName, group, reason);

            var waitResult = await WaitForOwnedWorkerExitAsync(pid, timeout, ownerState, expectedAttemptGeneration, CancellationToken.None).ConfigureAwait(false);
            if (waitResult == EpgProcessExitWaitResult.Timeout)
            {
                var currentSnapshot = ownerState.Snapshot();
                if (currentSnapshot.AttemptGeneration != expectedAttemptGeneration) return false;
                identity = GetOwnedWorkerProcessIdentity(currentSnapshot);
                if (identity == EpgWorkerProcessIdentityState.Match)
                {
                    var killResult = TryKillOwnedWorkerProcess(currentSnapshot, deviceGateAlreadyHeld);
                    Log("EPG_CANCEL_PROCESS_STOP_WAIT", $"TS{group.TsId}",
                        $"result=TIMEOUT pid={pid} worker={workerName} phase={SafeLog(phase)} waitMs={(int)timeout.TotalMilliseconds} " +
                        $"killResult={killResult} action={GetOwnedWorkerKillAction(killResult)} deviceGateAlreadyHeld={deviceGateAlreadyHeld} rule=epg_worker_identity_contract");
                }
                else
                {
                    Log("EPG_CANCEL_PROCESS_STOP_WAIT", $"TS{group.TsId}",
                        $"result={identity} pid={pid} worker={workerName} phase={SafeLog(phase)} action=do_not_kill rule=epg_worker_identity_contract");
                }
                waitResult = await WaitForOwnedWorkerExitAsync(pid, TimeSpan.FromSeconds(2), ownerState, expectedAttemptGeneration, CancellationToken.None).ConfigureAwait(false);
            }

            Log("EPG_CANCEL_PROCESS_WAIT_RESULT", $"TS{group.TsId}",
                $"result={waitResult} pid={pid} worker={workerName} phase={SafeLog(phase)} reason={SafeLog(reason)} rule=epg_worker_route_contract");
        }

        var finalSnapshot = ownerState.Snapshot();
        if (finalSnapshot.AttemptGeneration != expectedAttemptGeneration) return false;
        identity = GetOwnedWorkerProcessIdentity(finalSnapshot);
        if (identity is EpgWorkerProcessIdentityState.Missing or EpgWorkerProcessIdentityState.ReusedPid)
        {
            ownerState.MarkProcessExitObserved(expectedAttemptGeneration);
            Log("EPG_CANCEL_PROCESS_EXIT_OK", $"TS{group.TsId}",
                $"pid={pid} worker={workerName} phase={SafeLog(phase)} action=stopped_owner_task_will_release_tuner rule=epg_worker_route_contract");
            return true;
        }

        Log("EPG_CANCEL_PROCESS_EXIT_WARN", $"TS{group.TsId}",
            $"pid={pid} worker={workerName} phase={SafeLog(phase)} action=still_alive_keep_tracked rule=epg_worker_route_contract");
        return false;
    }

    // ─── 1グループのキャプチャ ────────────────────────────────────

    private async Task<int> CaptureGroupAsync(TsGroup group, int pass, CancellationToken ct, bool isPreRecordCheck, ActiveEpgWorkerTask workerTaskState, int? maxCaptureSeconds, ushort? expectedNetworkId, ushort? expectedTransportStreamId, ushort? expectedServiceId, ushort? expectedEventId, DateTime? expectedStartTime, DateTime? expectedEndTime, string? preferredRecordingTunerName = null, string? preTuneChainPosition = null, string? preTuneAction = null, bool preTuneKeepWorkerUntilSafetyCeiling = false, bool preRecordProbeSnapshotOnly = false, ConcurrentBag<EpgEvent>? preRecordEvents = null, Action? releaseWorkerAdmission = null, Func<CancellationToken, Task>? reacquireWorkerAdmissionAsync = null)
    {
        var workerName  = $"{group.Group}-{group.TsId}";
        var tsFile      = BuildTsFilePath(group);
        runtimeEpgDepthOverride = NormalizeDepth(effectiveEpgDepth);
        var serviceCount = group.Targets.Count;
        var durationPlan = EpgDurationPolicy.Create(
            runtimeEpgDepthOverride,
            isPreRecordCheck,
            maxCaptureSeconds,
            serviceCount,
            pass,
            settings.MultiServiceExtraSeconds);

        // release_contract: EPG深度/取得秒数は EpgDurationPolicy に集約。
        // 待機秒→分の数値変換だけを共有する。定時EPG取得と録画前EPG確認の生成・実行契約は共有しない。
        var baseWaitSec = durationPlan.ConfiguredBaseSeconds;
        var effectiveBaseWaitSec = durationPlan.EffectiveBaseSeconds;
        var configuredExtraPerService = durationPlan.ConfiguredExtraPerServiceSeconds;
        var extraPerService = durationPlan.EffectiveExtraPerServiceSeconds;

        Log("EPG_DURATION_POLICY", $"TS{group.TsId}",
            $"mode={(isPreRecordCheck ? "epg-check" : "normal-epg")} group={group.Group} depth={durationPlan.Depth} configuredBase={durationPlan.ConfiguredBaseSeconds}s effectiveBase={durationPlan.EffectiveBaseSeconds}s " +
            $"configuredExtraPerService={durationPlan.ConfiguredExtraPerServiceSeconds}s effectiveExtraPerService={durationPlan.EffectiveExtraPerServiceSeconds}s services={durationPlan.ServiceCount} " +
            $"reason={durationPlan.Reason} rule={durationPlan.Rule}");

        var normalWaitSec = durationPlan.NormalDurationSeconds;
        var waitSec = durationPlan.RecDurationSeconds;
        // release_contract: worker起動成功だけを取得成功とみなさない。
        // TS未生成/no_ts_file は局所再試行し、最終的に output lifecycle reason を確定させる。
        var maxAttempts = isPreRecordCheck ? 1 : 2;

        if (isPreRecordCheck)
        {
            Log("PRE_REC_EPG_PROBE_WORK_START", $"TS{group.TsId}",
                $"{workerName} 開始: TS={group.TsId} pass={pass} services={serviceCount}" +
                $" [{string.Join(",", group.Targets.Select(t => t.ServiceId))}]" +
                $" safetyCeilingSeconds={waitSec} normalEpgSeconds={normalWaitSec}" +
                $" expectedNid={(expectedNetworkId?.ToString() ?? "-")} expectedTsid={(expectedTransportStreamId?.ToString() ?? "-")} expectedSid={(expectedServiceId?.ToString() ?? "-")} expectedEventId={(expectedEventId?.ToString() ?? "-")} expectedStart={(expectedStartTime?.ToString("MM/dd HH:mm:ss") ?? "-")} runtimeTuner={SafeLogValue(preferredRecordingTunerName)} policy=stop_as_soon_as_target_event_seen_runtime_tuner rule=pre_record_epg_runtime_tuner_contract");
        }
        else
        {
            Log("EPG_WORK_START", $"TS{group.TsId}",
                $"{workerName} 開始: TS={group.TsId} pass={pass} services={serviceCount}" +
                $" [{string.Join(",", group.Targets.Select(t => t.ServiceId))}]" +
                $" depth={durationPlan.Depth} recduration={waitSec}s base={effectiveBaseWaitSec}s configuredBase={baseWaitSec}s" +
                (waitSec != effectiveBaseWaitSec ? $" ext={waitSec - effectiveBaseWaitSec}s" : "") +
                $" purpose=normal scope=transport_stream_schedule_epg targetSidCount={group.Targets.Count} targetSids=[{string.Join(",", group.Targets.Select(t => t.ServiceId).OrderBy(x => x))}] allocationRouteAware=True rule={durationPlan.Rule}");
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 1 && reacquireWorkerAdmissionAsync is not null)
            {
                await reacquireWorkerAdmissionAsync(ct).ConfigureAwait(false);
                Log("EPG_WORKER_ADMISSION", $"TS{group.TsId}",
                    $"result=REACQUIRED worker={workerName} attempt={attempt}/{maxAttempts} group={group.Group} action=retry_within_group_capacity rule=epg_worker_transition_contract");
            }

            var effectiveWait = waitSec;

            // TunerPool からチューナーを確保
            // TvAIr管理TunerPoolのみから確保
            var plannedEnd = DateTime.Now.AddSeconds(effectiveWait + (isPreRecordCheck ? 0 : 30));
            var lease = await AcquireEpgLeaseWithShortWaitAsync(group, plannedEnd, workerName, ct, isPreRecordCheck, workerTaskState.RunId, isPreRecordCheck ? preferredRecordingTunerName : null);
            if (lease is null)
            {
                if (!isPreRecordCheck)
                {
                    // normal EPG開始後のTuner確保失敗は録画都合のDeferredへ変換しない。
                    // 実際に必要な論理資源を取得できなかったworker failureとして記録する。
                    RecordCaptureFailure(group, "epg_tuner_unavailable", $"waitMs={ResolveEpgLogicalAcquireWaitMs(plannedEnd, isPreRecordCheck)} unmanagedExternalTvTest=out_of_scope policy=logical_resource_queue", attempt, maxAttempts, workerTaskState.RunId);
                }
                return 0;
            }

            if (!workerTaskState.TryAttachLease(lease, out var attemptGeneration, out var attemptRejectReason))
            {
                try { lease.Dispose(); } catch { }
                Log("EPG_WORKER_ATTEMPT_ATTACH", $"TS{group.TsId}",
                    $"result=REJECTED worker={workerName} attempt={attempt}/{maxAttempts} group={group.Group} reason={attemptRejectReason} action=release_new_lease_stop_retry rule=epg_worker_attempt_ownership_contract");
                return 0;
            }

            // PHYSICAL_EPG_DEVICE_SESSION_INVARIANT:
            // 次局へ進める正本は「論理leaseがFreeになった」だけではない。
            // startupはREADY、失敗startupは当該workerのexit確認までTUNER_DEVICE_LOCK内で収束させる。
            // 正常captureもowned process exit確認後にleaseを解放する。固定時間待機は設けない。

            int imported;
            EpgWorkerLaunchResult? activeLaunch = null;
            var activeLaunchRetired = false;
            try
            {
                // TSファイルのディレクトリ確保
                Directory.CreateDirectory(Path.GetDirectoryName(tsFile)!);

                // TvAIrEpgRec 起動
                // EPG/EPG確認の実TS取得は TvAIrEpgRec 経由で実行する。
                // 取得開始はグローバルゲートでシリアル化する。
                // 録画前EPG確認は同一TS代表サービスで起動する場合があるため、
                // ActivityKeeper/タスクバー識別は目的番組の expectedService を優先する。
                var displayServiceName = ResolveActivityDisplayServiceName(group, isPreRecordCheck, expectedServiceId);
                bool BindStartedProcessOwnership(EpgWorkerLaunchResult startedLaunch)
                {
                    // The just-created process belongs to this exact attempt before any OpenTuner/SetChannel/TS-read
                    // observation begins. This removes the startup window where reconciliation could pair a new
                    // lease with stale/empty process identity.
                    activeLaunch = startedLaunch;
                    // The logical attempt owner is canonical. Bind the process there first, then project the
                    // same PID to the already-owned TunerPool lease. If the projection fails, the process is
                    // still owned and can be stopped/converged without releasing an untracked live worker.
                    if (!workerTaskState.AttachProcess(attemptGeneration, startedLaunch.ProcessId, workerName, startedLaunch.StopSignalPath))
                    {
                        Log("EPG_WORKER_ATTEMPT_ATTACH", $"TS{group.TsId}",
                            $"result=STALE_PROCESS_ATTACH worker={workerName} pid={startedLaunch.ProcessId} attempt={attempt}/{maxAttempts} attemptGeneration={attemptGeneration} action=stop_old_launch_without_touching_current_attempt rule=epg_worker_attempt_ownership_contract");
                        return false;
                    }

                    try
                    {
                        lease.SetProcessId(startedLaunch.ProcessId);
                    }
                    catch (Exception ex)
                    {
                        Log("EPG_WORKER_ATTEMPT_ATTACH", $"TS{group.TsId}",
                            $"result=LEASE_PROCESS_BIND_FAILED worker={workerName} pid={startedLaunch.ProcessId} attempt={attempt}/{maxAttempts} attemptGeneration={attemptGeneration} error={SafeLog(ex.Message)} action=stop_owned_launch_and_converge_attempt rule=epg_worker_attempt_ownership_contract");
                        return false;
                    }

                    LogEpgWorkerCoverage(workerTaskState.RunId, "attach", force: true);
                    return true;
                }

                var launch = await StartEpgRecordingWithLaunchGateAsync(
                    workerName,
                    group,
                    lease.BonDriverFileName,
                    lease.Did,
                    group.Targets[0].ChannelArgument,
                    tsFile,
                    effectiveWait,
                    ct,
                    isPreRecordCheck,
                    displayServiceName,
                    preferredRecordingTunerName,
                    preTuneChainPosition,
                    preTuneAction,
                    preTuneKeepWorkerUntilSafetyCeiling,
                    preRecordProbeSnapshotOnly ? expectedEventId : null,
                    preRecordProbeSnapshotOnly ? expectedStartTime : null,
                    preRecordProbeSnapshotOnly ? expectedEndTime : null,
                    BindStartedProcessOwnership,
                    async (unreadyLaunch, startupResult) => await StopAndRetireActiveEpgWorkerAsync(
                        unreadyLaunch,
                        workerName,
                        group,
                        "device_start_boundary",
                        $"startup_{startupResult}",
                        workerTaskState,
                        attemptGeneration,
                        TimeSpan.FromSeconds(2),
                        deviceGateAlreadyHeld: true).ConfigureAwait(false));
                activeLaunch ??= launch;

                if (!launch.Success)
                {
                    Log("EPG_CAPTURE_FAIL", $"TS{group.TsId}", launch.Message);

                    // Normal EPGのstartup失敗は、未収束workerを残したまま同一capacityで即再起動しない。
                    // failed workerがexit済みであることをowner正本から確認した場合だけlease/admissionを返し、
                    // queueの後続と同じcapacity gateを取り直して次attemptへ進む。PreRecは従来どおり単発。
                    var startupProcessConverged = workerTaskState.TrySnapshotAttempt(attemptGeneration, out var startupFailureSnapshot)
                        && !IsOwnedWorkerProcessAlive(startupFailureSnapshot);
                    if (!isPreRecordCheck && attempt < maxAttempts && startupProcessConverged)
                    {
                        if (!workerTaskState.ReleaseLeaseForAttempt(attemptGeneration))
                        {
                            workerTaskState.DeferAdmissionRelease(releaseWorkerAdmission);
                            Log("EPG_CAPTURE_STARTUP_RETRY", $"TS{group.TsId}",
                                $"result=SUPPRESSED worker={workerName} attempt={attempt}/{maxAttempts} group={group.Group} processConverged=True leaseConverged=False action=keep_owner_for_residual_convergence rule=physical_epg_device_session_contract");
                            return 0;
                        }
                        releaseWorkerAdmission?.Invoke();
                        RecordCaptureFailure(
                            group,
                            "worker_startup_not_ready",
                            $"attempt={attempt}/{maxAttempts} message={SafeLogValue(launch.Message)}",
                            attempt,
                            maxAttempts,
                            workerTaskState.RunId);
                        Log("EPG_CAPTURE_STARTUP_RETRY", $"TS{group.TsId}",
                            $"result=REQUEUE worker={workerName} attempt={attempt}/{maxAttempts} group={group.Group} processConverged=True " +
                            "action=reacquire_group_capacity_before_retry rule=physical_epg_device_session_contract");
                        continue;
                    }

                    if (!startupProcessConverged)
                    {
                        Log("EPG_CAPTURE_STARTUP_RETRY", $"TS{group.TsId}",
                            $"result=SUPPRESSED worker={workerName} attempt={attempt}/{maxAttempts} group={group.Group} processConverged=False " +
                            "action=keep_owned_until_residual_monitor rule=physical_epg_device_session_contract");
                    }
                    return 0;
                }

                var primaryServiceName = displayServiceName;
                using var activityHandle = tvTestActivity.AttachExisting(isPreRecordCheck ? "EPG確認中" : "EPG取得中", launch.ProcessId, primaryServiceName, workerName);
                if (isPreRecordCheck)
                    Log("PRE_REC_EPG_PROBE_TVAIREPGREC_START", $"TS{group.TsId}", launch.Message);
                else
                    Log("EPG_CAPTURE_TVAIREPGREC_START", $"TS{group.TsId}", launch.Message);

                EpgOutputLifecycleState? outputLifecycle = null;
                if (!isPreRecordCheck || !preRecordProbeSnapshotOnly)
                {
                    outputLifecycle = await ObserveEpgOutputLifecycleAsync(
                        group,
                        workerName,
                        tsFile,
                        launch.ProcessId,
                        workerTaskState,
                        attemptGeneration,
                        TimeSpan.FromSeconds(25),
                        ct);
                }

                if (isPreRecordCheck)
                {
                    var probe = await WaitForPreRecordTargetEventAsync(
                        group,
                        tsFile,
                        launch.ProcessId,
                        workerName,
                        workerTaskState,
                        attemptGeneration,
                        TimeSpan.FromSeconds(effectiveWait),
                        expectedNetworkId,
                        expectedTransportStreamId,
                        expectedServiceId,
                        expectedEventId,
                        expectedStartTime,
                        expectedEndTime,
                        !preRecordProbeSnapshotOnly,
                        ct,
                        preferredRecordingTunerName,
                        preTuneKeepWorkerUntilSafetyCeiling);


                    if (!workerTaskState.TrySnapshotAttempt(attemptGeneration, out var probeSnapshot))
                        return 0;
                    var probeIdentity = GetOwnedWorkerProcessIdentity(probeSnapshot);
                    if (probeIdentity == EpgWorkerProcessIdentityState.Match)
                    {
                        Log("PRE_REC_EPG_PROBE_STOP_REQUEST", $"TS{group.TsId}",
                            $"pid={launch.ProcessId} worker={workerName} reason={(probe.TargetFound ? "target_event_seen" : "safety_ceiling_reached")} rule=epg_probe_runtime_contract");
                        RequestTvAIrEpgRecStopOrKill(launch, workerName, group, "pre_record_target_probe_done");
                    }
                    else if (probeIdentity == EpgWorkerProcessIdentityState.IdentityUnknown)
                    {
                        RequestTvAIrEpgRecStopSignalOnly(launch, workerName, group, "pre_record_target_probe_done");
                    }
                    var probeWaitResult = await WaitForOwnedWorkerExitAsync(launch.ProcessId, TimeSpan.FromSeconds(5), workerTaskState, attemptGeneration, CancellationToken.None).ConfigureAwait(false);
                    if (probeWaitResult == EpgProcessExitWaitResult.Timeout)
                    {
                        if (!workerTaskState.TrySnapshotAttempt(attemptGeneration, out probeSnapshot))
                            return 0;
                        probeIdentity = GetOwnedWorkerProcessIdentity(probeSnapshot);
                        if (probeIdentity == EpgWorkerProcessIdentityState.Match)
                        {
                            var killResult = TryKillOwnedWorkerProcess(probeSnapshot);
                            Log("PRE_REC_EPG_PROBE_STOP_TIMEOUT", $"TS{group.TsId}",
                                $"result=TIMEOUT pid={launch.ProcessId} worker={workerName} " +
                                $"killResult={killResult} action={GetOwnedWorkerKillAction(killResult)} " +
                                "reason=pre_record_target_probe_done rule=epg_worker_identity_contract");
                            probeWaitResult = await WaitForOwnedWorkerExitAsync(launch.ProcessId, TimeSpan.FromSeconds(2), workerTaskState, attemptGeneration, CancellationToken.None).ConfigureAwait(false);
                        }
                        else
                        {
                            Log("PRE_REC_EPG_PROBE_STOP_TIMEOUT", $"TS{group.TsId}",
                                $"result={probeIdentity} pid={launch.ProcessId} worker={workerName} action=do_not_kill rule=epg_worker_identity_contract");
                        }
                    }
                    Log("PRE_REC_EPG_PROBE_STOP_RESULT", $"TS{group.TsId}",
                        $"result={probeWaitResult} pid={launch.ProcessId} worker={workerName} rule=epg_probe_runtime_contract");
                    // FINAL_OUTPUT_STAT_INVARIANT: probe-first flowでもworker停止後に最終ファイル状態を再観測し、
                    // release/cleanupより前に出力証拠を確定する。
                    _ = await ObserveEpgOutputLifecycleAsync(
                        group, workerName, tsFile, launch.ProcessId, workerTaskState, attemptGeneration, TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                    if (!workerTaskState.TrySnapshotAttempt(attemptGeneration, out probeSnapshot))
                        return 0;
                    probeIdentity = GetOwnedWorkerProcessIdentity(probeSnapshot);
                    if (probeIdentity is EpgWorkerProcessIdentityState.Missing or EpgWorkerProcessIdentityState.ReusedPid)
                    {
                        await HandleEpgProcessEndAsync(group, workerName, launch.ProcessId, ct).ConfigureAwait(false);
                        MarkActiveEpgWorkerProcessExitObserved(workerTaskState, attemptGeneration, launch.ProcessId, "process_end");
                        activeLaunchRetired = true;
                        if (!workerTaskState.ReleaseLeaseForAttempt(attemptGeneration))
                        {
                            Log("PRE_REC_EPG_PROBE_STOP_WARN", $"TS{group.TsId}",
                                $"result=LEASE_RELEASE_DEFERRED pid={launch.ProcessId} worker={workerName} action=keep_tracked rule=epg_worker_attempt_ownership_contract");
                            return 0;
                        }
                    }
                    else
                    {
                        Log("PRE_REC_EPG_PROBE_STOP_WARN", $"TS{group.TsId}",
                            $"result=STILL_ALIVE pid={launch.ProcessId} worker={workerName} action=skip_parse_keep_tracked rule=epg_probe_runtime_contract");
                        return 0;
                    }

                    if (preRecordProbeSnapshotOnly)
                    {
                        if (probe.TargetFound)
                        {
                            foreach (var observedEvent in probe.Events)
                                preRecordEvents?.Add(observedEvent);
                            imported = probe.Events.Count; // completion evidence only; no normal EPG DB mutation.
                            Log("PRE_REC_EPG_SNAPSHOT", $"TS{group.TsId}",
                                $"result=TARGET_FOUND observedEvents={probe.Events.Count} dbWrite=none event={SafeLogValue(probe.EventSummary)} action=return_probe_snapshot_to_scheduler rule=pre_record_epg_probe_snapshot_contract");
                        }
                        else
                        {
                            imported = 0;
                            Log("PRE_REC_EPG_SNAPSHOT", $"TS{group.TsId}",
                                $"result=TARGET_NOT_FOUND observedEvents={probe.Events.Count} dbWrite=none expectedEventId={(expectedEventId?.ToString() ?? "-")} action=fail_probe_keep_original_reservation_time rule=pre_record_epg_probe_snapshot_contract");
                        }
                        try { File.Delete(tsFile); } catch { }
                        CleanupTvAIrEpgRecRuntimeFiles(launch, workerName, group, reason: probe.TargetFound ? "pre_record_probe_target_found" : "pre_record_probe_target_not_found");
                    }
                    else
                    {
                        // USER_CHAIN_PRE_REC_OBSERVED_SNAPSHOT_INVARIANT:
                        // chain-rootも今回のworkerが実観測したsnapshotをschedulerへ返す。
                        // 既存DB保存は互換投影用に維持するが、時間追従の正本はこの観測snapshotとする。
                        imported = probe.Events.Count > 0
                            ? probe.Events.Count
                            : await ParseAndStoreAsync(group, tsFile, attempt, maxAttempts, ct, isPreRecordCheck);
                        if (probe.Events.Count > 0)
                        {
                            foreach (var observedEvent in probe.Events)
                                preRecordEvents?.Add(observedEvent);
                            try { File.Delete(tsFile); } catch { }
                            CleanupTvAIrEpgRecRuntimeFiles(launch, workerName, group, reason: "pre_record_probe_user_chain_target_seen");
                        }
                        else if (imported > 0)
                        {
                            CleanupTvAIrEpgRecRuntimeFiles(launch, workerName, group, reason: "pre_record_probe_user_chain_parse_ok");
                        }
                    }
                }
                else
                {
                    // TvAIrEpgRec の終了を待つ
                    var processTimeout = TimeSpan.FromSeconds(effectiveWait + 8 + 30);
                    var waitResult = await WaitForExitOrExternalFailureAsync(
                        launch.ProcessId,
                        processTimeout,
                        workerTaskState,
                        attemptGeneration,
                        ct).ConfigureAwait(false);

                    if (waitResult is EpgProcessExitWaitResult.Exited or EpgProcessExitWaitResult.NotFound)
                        workerTaskState.MarkProcessExitObserved(attemptGeneration);

                    if (waitResult == EpgProcessExitWaitResult.ProcessMissing)
                    {
                        throw new EpgWorkerProcessMissingException(
                            workerTaskState.Snapshot().ExternalFailureCycleId);
                    }

                    // キャンセル時はTvAIrEpgRecへ停止要求を送る
                    if (waitResult == EpgProcessExitWaitResult.Cancelled)
                    {
                        activeLaunchRetired = await StopAndRetireActiveEpgWorkerAsync(
                            launch,
                            workerName,
                            group,
                            "wait_for_exit",
                            "wait_for_exit_cancelled",
                            workerTaskState,
                            attemptGeneration).ConfigureAwait(false);
                        Log("EPG_CAPTURE_CANCELLED", $"TS{group.TsId}",
                            $"EPG取得がキャンセルされました。TvAIrEpgRec(PID={launch.ProcessId})を終了しました。");
                        ct.ThrowIfCancellationRequested();
                        return 0;
                    }

                    if (!workerTaskState.TrySnapshotAttempt(attemptGeneration, out var postWaitSnapshot))
                        return 0;
                    var postWaitIdentity = GetOwnedWorkerProcessIdentity(postWaitSnapshot);
                    if (waitResult == EpgProcessExitWaitResult.Timeout
                        && (postWaitIdentity is EpgWorkerProcessIdentityState.Match or EpgWorkerProcessIdentityState.IdentityUnknown))
                    {
                        Log("EPG_CAPTURE_WAIT_TIMEOUT", $"TS{group.TsId}",
                            $"result=TIMEOUT pid={launch.ProcessId} worker={workerName} waitSec={(int)processTimeout.TotalSeconds} action=stop_then_kill_if_needed rule=epg_worker_route_contract");
                        activeLaunchRetired = await StopAndRetireActiveEpgWorkerAsync(
                            launch,
                            workerName,
                            group,
                            "wait_for_exit_timeout",
                            "wait_for_exit_timeout",
                            workerTaskState,
                            attemptGeneration).ConfigureAwait(false);
                    }

                    if (!workerTaskState.TrySnapshotAttempt(attemptGeneration, out var finalOwnedSnapshot))
                        return 0;
                    var finalOwnedIdentity = GetOwnedWorkerProcessIdentity(finalOwnedSnapshot);
                    if (finalOwnedIdentity is EpgWorkerProcessIdentityState.Match or EpgWorkerProcessIdentityState.IdentityUnknown)
                    {
                        Log("EPG_CAPTURE_PROCESS_STILL_ALIVE", $"TS{group.TsId}",
                            $"result={finalOwnedIdentity} pid={launch.ProcessId} worker={workerName} waitResult={waitResult} action=skip_parse_keep_tracked rule=epg_worker_identity_contract");
                        return 0;
                    }

                    await HandleEpgProcessEndAsync(group, workerName, launch.ProcessId, ct).ConfigureAwait(false);
                    if (!activeLaunchRetired)
                    {
                        MarkActiveEpgWorkerProcessExitObserved(workerTaskState, attemptGeneration, launch.ProcessId, "process_end");
                        activeLaunchRetired = true;
                    }
                    if (!workerTaskState.ReleaseLeaseForAttempt(attemptGeneration))
                    {
                        workerTaskState.DeferAdmissionRelease(releaseWorkerAdmission);
                        Log("EPG_WORKER_TRANSITION", $"TS{group.TsId}",
                            $"result=LEASE_RELEASE_DEFERRED worker={workerName} pid={launch.ProcessId} group={group.Group} action=keep_owner_no_next_worker rule=epg_worker_attempt_ownership_contract");
                        return 0;
                    }
                    releaseWorkerAdmission?.Invoke();
                    Log("EPG_WORKER_TRANSITION", $"TS{group.TsId}",
                        $"result=RELEASED worker={workerName} pid={launch.ProcessId} group={group.Group} action=launch_next_before_parse fixedDelayMs=0 rule=epg_worker_transition_contract");


                    // TSパース。取得完了判定はRunPassAsync側で一度だけ行う。
                    imported = await ParseAndStoreAsync(group, tsFile, attempt, maxAttempts, ct, isPreRecordCheck);
                    // EPG_WORKER_TERMINAL_EVIDENCE_INVARIANT:
                    // Parsing a partially written TS and the worker process completing successfully are
                    // independent facts. Always capture the worker-owned terminal result before cleanup;
                    // otherwise a failed worker can be hidden by a usable partial TS and its evidence lost.
                    var terminalEvidence = LogTvAIrEpgRecTerminalEvidence(
                        launch, workerName, group, tsFile, attempt, maxAttempts, imported);

                    if (terminalEvidence.PreserveRuntimeArtifacts)
                    {
                        Log("TVAIREPGREC_RUNTIME_CLEANUP", $"TS{group.TsId}",
                            $"result=PRESERVED worker={workerName} pid={launch.ProcessId} reason=worker_terminal_not_proven_success " +
                            $"workerSuccess={terminalEvidence.WorkerSuccess} exitCode={terminalEvidence.ExitCode} tsReadOk={terminalEvidence.TsReadOk} " +
                            "action=keep_job_result_progress_for_root_cause rule=epg_worker_terminal_evidence_contract");
                    }
                    else
                    {
                        CleanupTvAIrEpgRecRuntimeFiles(
                            launch,
                            workerName,
                            group,
                            reason: imported > 0 ? "epg_parse_done_worker_terminal_ok" : "epg_parse_empty_worker_terminal_ok");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (activeLaunch is not null && activeLaunch.Success && activeLaunch.ProcessId > 0 && !activeLaunchRetired)
                {
                    activeLaunchRetired = await StopAndRetireActiveEpgWorkerAsync(
                        activeLaunch,
                        workerName,
                        group,
                        "capture_scope",
                        "capture_scope_cancelled",
                        workerTaskState,
                        attemptGeneration).ConfigureAwait(false);
                }
                throw;
            }
            finally
            {
                var processStillOwnedAndAlive = false;
                {
                    if (workerTaskState.TrySnapshotAttempt(attemptGeneration, out var snapshot))
                    {
                        // ProcessExitObserved is historical evidence only. The exact attempt's
                        // owned-process identity decides whether that attempt's lease must remain held.
                        processStillOwnedAndAlive = snapshot.ProcessId > 0
                            && IsOwnedWorkerProcessAlive(snapshot);
                    }
                }

                if (!processStillOwnedAndAlive)
                    workerTaskState.ReleaseLeaseForAttempt(attemptGeneration);
            }

            if (imported > 0)
            {
                currentRunCaptureFailures.TryRemove(group.Key, out _);
                return imported;
            }

            if (!isPreRecordCheck)
            {
                var failureReason = ClassifyImportFailure(group, tsFile);
                RecordCaptureFailure(group, failureReason, $"file={SafeLogValue(tsFile)}", attempt, maxAttempts, workerTaskState.RunId);
                if (attempt < maxAttempts)
                {
                    Log("EPG_CAPTURE_RETRY", $"TS{group.TsId}",
                        $"reason={SafeLogValue(failureReason)} nextAttempt={attempt + 1}/{maxAttempts} group={group.Group} service={SafeLogValue(group.Targets.FirstOrDefault()?.Name)} policy=output_lifecycle_retry rule=release_contract");
                }
            }
        }

        return 0;
    }


    private async Task<EpgOutputLifecycleState> ObserveEpgOutputLifecycleAsync(
        TsGroup group,
        string workerName,
        string tsFile,
        int processId,
        ActiveEpgWorkerTask workerTaskState,
        long expectedAttemptGeneration,
        TimeSpan waitLimit,
        CancellationToken ct)
    {
        var started = DateTime.Now;
        var observed = false;
        var nonZero = false;
        var growing = false;
        var workerExitObserved = false;
        long firstSize = 0;
        long lastSize = 0;
        var firstObservedAt = DateTime.MinValue;
        var lastObservedAt = DateTime.MinValue;

        Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
            $"phase=worker_started worker={workerName} pid={processId} group={group.Group} output={SafeLogValue(tsFile)} waitLimitSec={(int)waitLimit.TotalSeconds} rule=release_contract");

        while ((DateTime.Now - started) < waitLimit)
        {
            ct.ThrowIfCancellationRequested();
            var attemptSnapshot = workerTaskState.Snapshot();
            if (attemptSnapshot.AttemptGeneration != expectedAttemptGeneration)
                break;
            var identity = GetOwnedWorkerProcessIdentity(attemptSnapshot);
            if (identity is EpgWorkerProcessIdentityState.Missing or EpgWorkerProcessIdentityState.ReusedPid)
            {
                workerExitObserved = true;
                // Worker終了と最終ファイルflushは同じ観測周期内で競合し得る。
                // 直前pollの0 byteをそのまま確定すると、その後のparserが実ファイルを正常に読めても
                // EPG_OUTPUT_LIFECYCLEだけがts_file_zeroと誤報するため、終了検出時に必ず最終statを取り直す。
                try
                {
                    if (File.Exists(tsFile))
                    {
                        var finalInfo = new FileInfo(tsFile);
                        finalInfo.Refresh();
                        var finalSize = finalInfo.Length;
                        var finalObservedAt = DateTime.Now;
                        var observedBeforeFinalStat = observed;
                        if (!observed)
                        {
                            observed = true;
                            firstSize = finalSize;
                            firstObservedAt = finalObservedAt;
                        }
                        if (finalSize > 0)
                            nonZero = true;
                        if (observedBeforeFinalStat && finalSize > lastSize)
                            growing = true;
                        lastSize = finalSize;
                        lastObservedAt = finalObservedAt;

                        Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
                            $"phase=worker_exit_final_stat worker={workerName} pid={processId} group={group.Group} size={finalSize} observed={observed} nonZero={nonZero} growing={growing} elapsedMs={(int)(DateTime.Now - started).TotalMilliseconds} output={SafeLogValue(tsFile)} rule=epg_output_final_stat_contract");
                    }
                }
                catch (IOException ex)
                {
                    Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
                        $"phase=worker_exit_final_stat_io worker={workerName} pid={processId} group={group.Group} error={SafeLog(ex.Message)} rule=epg_output_final_stat_contract");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
                        $"phase=worker_exit_final_stat_access worker={workerName} pid={processId} group={group.Group} error={SafeLog(ex.Message)} rule=epg_output_final_stat_contract");
                }

                Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
                    $"phase=worker_exited worker={workerName} pid={processId} group={group.Group} elapsedMs={(int)(DateTime.Now - started).TotalMilliseconds} action=stop_output_wait_and_release_tuner rule=epg_worker_transition_contract");
                break;
            }

            try
            {
                if (File.Exists(tsFile))
                {
                    var info = new FileInfo(tsFile);
                    var size = info.Length;
                    if (!observed)
                    {
                        observed = true;
                        firstSize = size;
                        firstObservedAt = DateTime.Now;
                        Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
                            $"phase=ts_file_observed worker={workerName} pid={processId} group={group.Group} size={size} elapsedMs={(int)(DateTime.Now - started).TotalMilliseconds} output={SafeLogValue(tsFile)} rule=release_contract");
                    }
                    if (size > 0 && !nonZero)
                    {
                        nonZero = true;
                        Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
                            $"phase=ts_file_nonzero worker={workerName} pid={processId} group={group.Group} size={size} elapsedMs={(int)(DateTime.Now - started).TotalMilliseconds} rule=release_contract");
                    }
                    if (observed && lastSize > 0 && size > lastSize)
                    {
                        growing = true;
                        Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
                            $"phase=ts_file_growing worker={workerName} pid={processId} group={group.Group} firstSize={firstSize} previousSize={lastSize} size={size} elapsedMs={(int)(DateTime.Now - started).TotalMilliseconds} rule=release_contract");
                        return new EpgOutputLifecycleState(observed, nonZero, growing, firstSize, size, firstObservedAt, DateTime.Now, "growing");
                    }
                    lastSize = size;
                    lastObservedAt = DateTime.Now;
                }
            }
            catch (IOException ex)
            {
                Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
                    $"phase=io_wait worker={workerName} pid={processId} group={group.Group} error={SafeLog(ex.Message)} rule=release_contract");
            }
            catch (UnauthorizedAccessException ex)
            {
                Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
                    $"phase=access_wait worker={workerName} pid={processId} group={group.Group} error={SafeLog(ex.Message)} rule=release_contract");
            }

            await Task.Delay(1000, ct);
        }

        var phase = !observed
            ? "ts_file_not_created"
            : !nonZero
                ? "ts_file_zero"
                : growing
                    ? "ts_file_growing"
                    : workerExitObserved
                        ? "ts_file_nonzero_at_worker_exit"
                        : "ts_file_not_growing";
        Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
            $"phase={phase} worker={workerName} pid={processId} group={group.Group} observed={observed} nonZero={nonZero} growing={growing} firstSize={firstSize} lastSize={lastSize} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} output={SafeLogValue(tsFile)} action=continue_to_worker_exit_then_parse_or_retry rule=release_contract");
        return new EpgOutputLifecycleState(observed, nonZero, growing, firstSize, lastSize, firstObservedAt, lastObservedAt, phase);
    }



    private EpgWorkerTerminalEvidence LogTvAIrEpgRecTerminalEvidence(
        EpgWorkerLaunchResult launch,
        string workerName,
        TsGroup group,
        string tsFile,
        int attempt,
        int maxAttempts,
        int imported)
    {
        static string ReadCompactFile(string? path, bool lastNonEmptyLine)
        {
            if (string.IsNullOrWhiteSpace(path)) return "path_missing";
            try
            {
                if (!File.Exists(path)) return "file_missing";
                string text;
                if (lastNonEmptyLine)
                {
                    text = File.ReadLines(path)
                        .Reverse()
                        .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
                }
                else
                {
                    text = File.ReadAllText(path);
                }
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                return text.Length <= 1800 ? text : text[^1800..];
            }
            catch (Exception ex)
            {
                return $"read_error:{ex.GetType().Name}:{ex.Message}";
            }
        }

        static long FileLengthOrMinusOne(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                    ? new FileInfo(path).Length
                    : -1;
            }
            catch
            {
                return -2;
            }
        }

        static string JsonScalar(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value)) return "-";
            return value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString() ?? "-",
                JsonValueKind.Null => "-",
                _ => value.GetRawText()
            };
        }

        bool resultExists = false;
        bool? workerSuccess = null;
        int? exitCode = null;
        bool? tsReadOk = null;
        string workerSummary;
        try
        {
            if (string.IsNullOrWhiteSpace(launch.ResultPath) || !File.Exists(launch.ResultPath))
            {
                workerSummary = "file_missing";
            }
            else
            {
                resultExists = true;
                using var doc = JsonDocument.Parse(File.ReadAllText(launch.ResultPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("success", out var successValue)
                    && successValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    workerSuccess = successValue.GetBoolean();
                if (root.TryGetProperty("exitCode", out var exitValue)
                    && exitValue.ValueKind == JsonValueKind.Number
                    && exitValue.TryGetInt32(out var parsedExitCode))
                    exitCode = parsedExitCode;

                var ts = root.TryGetProperty("tsReadProbe", out var tsReadProbe) && tsReadProbe.ValueKind == JsonValueKind.Object
                    ? tsReadProbe
                    : default;
                var tsAvailable = ts.ValueKind == JsonValueKind.Object;
                string TsValue(string name) => tsAvailable ? JsonScalar(ts, name) : "-";
                if (tsAvailable && ts.TryGetProperty("tsReadOk", out var tsReadOkValue)
                    && tsReadOkValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    tsReadOk = tsReadOkValue.GetBoolean();

                workerSummary =
                    $"success={JsonScalar(root, "success")} cancelled={JsonScalar(root, "cancelled")} exitCode={JsonScalar(root, "exitCode")} " +
                    $"message={SafeLogValue(JsonScalar(root, "message"))} errorType={SafeLogValue(JsonScalar(root, "errorType"))} error={SafeLogValue(JsonScalar(root, "error"))} " +
                    $"loadLibrary={TsValue("loadLibraryOk")} create={TsValue("createBonDriverOk")} open={TsValue("openTunerOk")} " +
                    $"setChannel2Called={TsValue("setChannel2Called")} setChannel2={TsValue("setChannel2Ok")} setChannel1Called={TsValue("setChannel1Called")} setChannel1={TsValue("setChannel1Ok")} setChannel={TsValue("setChannelOk")} " +
                    $"tsReadStarted={TsValue("tsReadStarted")} tsReadOk={TsValue("tsReadOk")} getTsCalls={TsValue("getTsCalls")} emptyReads={TsValue("emptyReads")} bytesRead={TsValue("bytesRead")} chunksRead={TsValue("chunksRead")} packetsRead={TsValue("packetsRead")} " +
                    $"readySamples={TsValue("readyCountSamples")} nonZeroReadySamples={TsValue("nonZeroReadyCountSamples")} lastReady={TsValue("lastReadyCount")} lastRemain={TsValue("lastRemain")} win32={TsValue("lastWin32Error")} tsError={SafeLogValue(TsValue("error"))}";
            }
        }
        catch (Exception ex)
        {
            workerSummary = $"parse_error:{ex.GetType().Name}:{SafeLogValue(ex.Message)}";
        }

        var resultTailEvidence = ReadCompactFile(launch.ResultPath, lastNonEmptyLine: false);
        var progressEvidence = ReadCompactFile(launch.ProgressPath, lastNonEmptyLine: true);
        var provenSuccess = resultExists && workerSuccess == true && exitCode == 0 && tsReadOk == true;
        var preserveRuntimeArtifacts = !provenSuccess;

        Log("TVAIREPGREC_EPG_TERMINAL_EVIDENCE", $"TS{group.TsId}",
            $"result={(provenSuccess ? "WORKER_OK" : "WORKER_NOT_PROVEN_OK")} worker={SafeLogValue(workerName)} pid={launch.ProcessId} group={SafeLogValue(group.Group)} attempt={attempt}/{maxAttempts} imported={imported} " +
            $"tsExists={File.Exists(tsFile)} tsBytes={FileLengthOrMinusOne(tsFile)} resultBytes={FileLengthOrMinusOne(launch.ResultPath)} progressBytes={FileLengthOrMinusOne(launch.ProgressPath)} " +
            $"workerSummary={workerSummary} workerResultTail={SafeLogValue(resultTailEvidence)} progressLast={SafeLogValue(progressEvidence)} " +
            $"runtimeArtifacts={(preserveRuntimeArtifacts ? "preserve" : "cleanup_allowed")} rule=epg_worker_terminal_evidence_contract");

        return new EpgWorkerTerminalEvidence(
            resultExists,
            workerSuccess,
            exitCode,
            tsReadOk,
            preserveRuntimeArtifacts);
    }

    private void CleanupTvAIrEpgRecRuntimeFiles(EpgWorkerLaunchResult launch, string workerName, TsGroup group, string reason)
    {
        var targets = new[] { launch.JobPath, launch.ResultPath, launch.ProgressPath, launch.StopSignalPath }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deleted = 0;
        var failed = 0;
        foreach (var path in targets)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch
            {
                failed++;
            }
        }
        Log("TVAIREPGREC_RUNTIME_CLEANUP", $"TS{group.TsId}",
            $"result=OK worker={workerName} pid={launch.ProcessId} reason={SafeLog(reason)} deleted={deleted} failed={failed} rule=release_contract");
    }

    private Task StartEpgWorkerCoverageMonitorAsync(DateTime runStarted, string runId, CancellationToken ct)
    {
        LogEpgWorkerCoverage(runId, "run_start", force: true);
        return Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    LogEpgWorkerCoverage(runId, "periodic", force: false);
                    await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("EPG_WORKER_COVERAGE", "WARN",
                    $"monitor_error={SafeLog(ex.Message)} rule=epg_worker_coverage_contract");
            }
        }, CancellationToken.None);
    }

    private static async Task StopEpgWorkerCoverageMonitorIfStartedAsync(CancellationTokenSource? cts, Task? monitor)
    {
        if (cts is null || monitor is null) return;
        try { cts.Cancel(); } catch { }
        try { await monitor.ConfigureAwait(false); } catch { }
        try { cts.Dispose(); } catch { }
    }

    private static string ResolveActivityDisplayServiceName(TsGroup group, bool isPreRecordCheck, ushort? expectedServiceId)
    {
        if (isPreRecordCheck && expectedServiceId.HasValue)
        {
            var expected = group.Targets.FirstOrDefault(t => t.ServiceId == expectedServiceId.Value);
            if (!string.IsNullOrWhiteSpace(expected?.Name))
                return expected.Name;
        }

        return group.Targets.FirstOrDefault()?.Name ?? $"TS{group.TsId}";
    }

    private void MarkActiveEpgWorkerProcessExitObserved(
        ActiveEpgWorkerTask state,
        long expectedAttemptGeneration,
        int pid,
        string reason)
    {
        var snapshot = state.Snapshot();
        if (snapshot.AttemptGeneration != expectedAttemptGeneration || snapshot.ProcessId != pid)
            return;
        var identity = GetOwnedWorkerProcessIdentity(snapshot);
        if (identity is not (EpgWorkerProcessIdentityState.Missing or EpgWorkerProcessIdentityState.ReusedPid))
            return;
        if (state.MarkProcessExitObserved(expectedAttemptGeneration))
            LogEpgWorkerCoverage(state.RunId, $"release:{reason}", force: true);
    }

    private void LogEpgWorkerCoverage(string runId, string phase, bool force)
    {
        var nowUtc = DateTime.UtcNow;
        if (!force && nowUtc - lastEpgWorkerCoverageLogUtc < TimeSpan.FromSeconds(30))
            return;
        lastEpgWorkerCoverageLogUtc = nowUtc;

        var activeTasks = SnapshotActiveEpgWorkerTasks(runId);
        var snapshots = activeTasks.Select(x => x.Snapshot()).ToList();
        var alive = snapshots
            .Where(x => !x.IsTerminal && x.ProcessId > 0
                && (GetOwnedWorkerProcessIdentity(x) is EpgWorkerProcessIdentityState.Match or EpgWorkerProcessIdentityState.IdentityUnknown))
            .OrderBy(x => x.Group)
            .ThenBy(x => x.TsId)
            .ThenBy(x => x.ProcessId)
            .ToList();

        var summary = alive.Count == 0
            ? "-"
            : string.Join(",", alive.Take(8).Select(e => $"{e.Group}:{e.TsId}/{e.ServiceName}/pid={e.ProcessId}"));
        if (alive.Count > 8) summary += $",...+{alive.Count - 8}";

        var activeWorkers = activeTasks.Count;
        // release_contract EpgProcessGapClassificationContract:
        // activeEpgWorkerTasks also contains owner tasks that have already observed their worker
        // exit and are parsing/cleaning the completed TS. Those tasks do not imply that a
        // TvAIrEpgRec process should still be visible. A WARN is valid only when at least one
        // non-terminal owner still expects its attached worker to be alive but none is visible.
        var expectedLiveWorkers = snapshots.Count(x =>
            !x.IsTerminal
            && x.ProcessId > 0
            && !x.ProcessExitObserved
            && !x.OwnerTaskCompleted);
        var activeTaskSummary = FormatActiveEpgWorkerTaskSummary(activeTasks);
        var tvTestIconActive = alive.Count > 0;

        Log("EPG_PROCESS_COVERAGE", "EPG",
            $"phase={phase} activeTrackedIndividual={snapshots.Count(x => x.ProcessId > 0)} aliveIndividualTvAIrEpgRec={alive.Count} activeWorkers={activeWorkers} expectedLiveWorkers={expectedLiveWorkers} " +
            $"processVisible={tvTestIconActive} individualPids={FormatPidList(alive.Select(e => e.ProcessId))} " +
            $"individualSummary={SafeLog(summary)} activeTaskSummary={SafeLog(activeTaskSummary)} " +
            "mode=epg_worker_task_owner rule=epg_worker_transition_contract");

        if (!tvTestIconActive && phase != "run_start" && phase != "run_end")
        {
            if (!epgRunsAcceptingNewWorkers.ContainsKey(runId) || expectedLiveWorkers == 0)
            {
                if (force || nowUtc - lastEpgWorkerGapLogUtc >= TimeSpan.FromSeconds(30))
                {
                    lastEpgWorkerGapLogUtc = nowUtc;
                    Log("EPG_PROCESS_ENDING_GAP", "INFO",
                        $"phase={phase} activeTrackedIndividual={snapshots.Count(x => x.ProcessId > 0)} aliveIndividualTvAIrEpgRec=0 activeWorkers={activeWorkers} expectedLiveWorkers={expectedLiveWorkers} " +
                        "note=no_live_worker_expected_during_epg_transition_or_tail processVisible=False action=continue_transition rule=epg_worker_transition_contract");
                }
            }
            else if (force || nowUtc - lastEpgWorkerGapLogUtc >= TimeSpan.FromSeconds(30))
            {
                lastEpgWorkerGapLogUtc = nowUtc;
                Log("EPG_PROCESS_GAP", "WARN",
                    $"phase={phase} activeTrackedIndividual={snapshots.Count(x => x.ProcessId > 0)} aliveIndividualTvAIrEpgRec=0 activeWorkers={activeWorkers} expectedLiveWorkers={expectedLiveWorkers} note=epg_owner_expects_live_worker_but_none_is_visible processVisible=False " +
                    "action=inspect_worker_transition rule=epg_worker_transition_contract");
            }
        }
    }

    private ActiveEpgWorkerTask RegisterActiveEpgWorkerTask(TsGroup group, int pass, string runId)
    {
        var normalizedGroup = NormalizeEpgTargetGroup(group.Group);
        var key = $"{runId}:{pass}:{normalizedGroup}:{group.TsId}:{Guid.NewGuid():N}";
        var state = new ActiveEpgWorkerTask(
            key,
            runId,
            pass,
            normalizedGroup,
            group.TsId,
            group.Targets.FirstOrDefault()?.Name ?? "-",
            DateTime.Now);
        activeEpgWorkerTasks[key] = state;
        return state;
    }

    private void CompleteActiveEpgWorkerTask(ActiveEpgWorkerTask state)
    {
        if (activeEpgWorkerTasks.TryGetValue(state.Key, out var current) && ReferenceEquals(current, state))
            activeEpgWorkerTasks.TryRemove(state.Key, out _);
    }

    private bool TryConvergeExitedEpgWorker(
        ActiveEpgWorkerTask state,
        EpgWorkerTerminalReason fallbackReason,
        string source,
        long expectedAttemptGeneration)
    {
        var snapshot = state.Snapshot();
        if (snapshot.AttemptGeneration != expectedAttemptGeneration)
            return false;
        // LOGICAL_WORKER_OWNER_INVARIANT:
        // logical worker taskの終端・active辞書からの除去はowner task完了後だけ。
        // missing monitorは個々のattempt資源を収束できるが、retryを含むlogical taskを終端しない。
        if (!snapshot.OwnerTaskCompleted)
            return false;

        var identity = snapshot.ProcessId <= 0
            ? EpgWorkerProcessIdentityState.Missing
            : GetOwnedWorkerProcessIdentity(snapshot);
        // A previous exit observation is only a hint. Always re-check the current
        // owned-process identity before releasing the lease. IdentityUnknown must
        // never be treated as process exit, and Match must never converge.
        if (identity is EpgWorkerProcessIdentityState.Match or EpgWorkerProcessIdentityState.IdentityUnknown)
            return false;

        const string finalizerOwner = "owner_task";
        if (!state.TryBeginFinalization(finalizerOwner, expectedAttemptGeneration))
            return false;

        try
        {
            if (!state.IsCurrentAttempt(expectedAttemptGeneration))
            {
                state.ResetFinalization();
                return false;
            }
            state.MarkProcessExitObserved(expectedAttemptGeneration);
            if (!state.ReleaseLeaseForAttempt(expectedAttemptGeneration))
            {
                state.ResetFinalization();
                return false;
            }
            if (!state.IsCurrentAttempt(expectedAttemptGeneration))
            {
                state.ResetFinalization();
                return false;
            }
            state.FinalizeRequestedOr(fallbackReason);
            CompleteActiveEpgWorkerTask(state);
            state.MarkFinalized();
        }
        catch
        {
            state.ResetFinalization();
            throw;
        }

        var completed = !activeEpgWorkerTasks.TryGetValue(state.Key, out var current) || !ReferenceEquals(current, state);
        if (completed)
        {
            Log("EPG_WORKER_TASK_CONVERGED", $"TS{snapshot.TsId}",
                $"result=COMPLETED source={SafeLog(source)} pid={snapshot.ProcessId} worker={SafeLog(snapshot.WorkerName)} group={SafeLog(snapshot.Group)} identity={identity} " +
                $"finalizerOwner={finalizerOwner} attemptGeneration={snapshot.AttemptGeneration} action=lease_released_task_removed rule=epg_worker_task_owner");
        }
        return completed;
    }

    private bool TryConvergeMissingAttemptResources(
        ActiveEpgWorkerTask state,
        long expectedAttemptGeneration,
        string source)
    {
        var snapshot = state.Snapshot();
        if (snapshot.AttemptGeneration != expectedAttemptGeneration || snapshot.OwnerTaskCompleted)
            return false;
        var identity = snapshot.ProcessId <= 0
            ? EpgWorkerProcessIdentityState.Missing
            : GetOwnedWorkerProcessIdentity(snapshot);
        if (identity is EpgWorkerProcessIdentityState.Match or EpgWorkerProcessIdentityState.IdentityUnknown)
            return false;

        state.MarkProcessExitObserved(expectedAttemptGeneration);
        var released = state.ReleaseLeaseForAttempt(expectedAttemptGeneration);
        Log("EPG_WORKER_ATTEMPT_CONVERGED", $"TS{snapshot.TsId}",
            $"result={(released ? "RESOURCES_CONVERGED" : "LEASE_RELEASE_DEFERRED")} source={SafeLog(source)} pid={snapshot.ProcessId} worker={SafeLog(snapshot.WorkerName)} group={SafeLog(snapshot.Group)} identity={identity} attemptGeneration={expectedAttemptGeneration} leaseReleased={released} ownerTaskCompleted=False action={(released ? "keep_logical_worker_owner_for_retry_or_owner_completion" : "keep_attempt_resources_for_next_reconcile")} rule=epg_worker_attempt_ownership_contract");
        return released;
    }

    private void StartMissingEpgWorkerConvergenceMonitor(ActiveEpgWorkerTask state)
    {
        if (!state.TryStartMissingConvergenceMonitor()) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var snapshot = state.Snapshot();
                var monitoredAttemptGeneration = snapshot.AttemptGeneration;
                var deadlineUtc = snapshot.MissingConvergenceDeadlineUtc ?? DateTime.UtcNow;
                await state.WaitForOwnerTaskOrDeadlineAsync(deadlineUtc, CancellationToken.None).ConfigureAwait(false);

                if (!activeEpgWorkerTasks.TryGetValue(state.Key, out var tracked) || !ReferenceEquals(tracked, state))
                    return;

                snapshot = state.Snapshot();
                if (snapshot.AttemptGeneration != monitoredAttemptGeneration)
                    return;
                var ownerCompleted = snapshot.OwnerTaskCompleted;
                var deadlineReached = snapshot.MissingConvergenceDeadlineUtc.HasValue
                    && DateTime.UtcNow >= snapshot.MissingConvergenceDeadlineUtc.Value;
                if (!ownerCompleted && !deadlineReached)
                    return;

                var converged = ownerCompleted
                    ? TryConvergeExitedEpgWorker(
                        state,
                        EpgWorkerTerminalReason.ProcessMissing,
                        "missing_monitor_owner_completed",
                        expectedAttemptGeneration: monitoredAttemptGeneration)
                    : deadlineReached && TryConvergeMissingAttemptResources(
                        state,
                        monitoredAttemptGeneration,
                        "missing_monitor_deadline");
                if (!converged)
                {
                    var currentSnapshot = state.Snapshot();
                    var identity = currentSnapshot.AttemptGeneration == monitoredAttemptGeneration
                        ? GetOwnedWorkerProcessIdentity(currentSnapshot)
                        : EpgWorkerProcessIdentityState.ReusedPid;
                    Log("EPG_MISSING_WORKER_CONVERGENCE", $"TS{snapshot.TsId}",
                        $"result=DEFERRED pid={snapshot.ProcessId} worker={SafeLog(snapshot.WorkerName)} identity={identity} " +
                        $"ownerCompleted={ownerCompleted} deadlineReached={deadlineReached} attemptGeneration={monitoredAttemptGeneration} action=keep_logical_owner_retry_on_next_reconcile rule=epg_worker_attempt_ownership_contract");
                    state.ResetMissingConvergenceMonitor(monitoredAttemptGeneration);
                }
            }
            catch (Exception ex)
            {
                var snapshot = state.Snapshot();
                Log("EPG_MISSING_WORKER_CONVERGENCE", $"TS{snapshot.TsId}",
                    $"result=ERROR pid={snapshot.ProcessId} worker={SafeLog(snapshot.WorkerName)} error={SafeLog(ex.Message)} " +
                    "action=keep_attempt_resources_for_next_reconcile rule=epg_worker_attempt_ownership_contract");
            }
        }, CancellationToken.None);
    }

    private void StartResidualEpgWorkerExitMonitor(ActiveEpgWorkerTask state)
    {
        if (!state.TryStartExitMonitor()) return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    if (!activeEpgWorkerTasks.TryGetValue(state.Key, out var tracked) || !ReferenceEquals(tracked, state))
                        return;

                    var snapshot = state.Snapshot();
                    var identity = snapshot.ProcessId <= 0
                        ? EpgWorkerProcessIdentityState.Missing
                        : GetOwnedWorkerProcessIdentity(snapshot);
                    if (identity is EpgWorkerProcessIdentityState.Missing or EpgWorkerProcessIdentityState.ReusedPid)
                    {
                        if (TryConvergeExitedEpgWorker(state, EpgWorkerTerminalReason.Completed, "residual_exit_monitor", snapshot.AttemptGeneration))
                            return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                state.ResetExitMonitor();
                var snapshot = state.Snapshot();
                Log("EPG_RESIDUAL_WORKER_MONITOR", $"TS{snapshot.TsId}",
                    $"result=ERROR pid={snapshot.ProcessId} worker={SafeLog(snapshot.WorkerName)} error={SafeLog(ex.Message)} action=keep_task_and_lease rule=epg_worker_task_owner");
            }
        }, CancellationToken.None);
    }

    private List<ActiveEpgWorkerTask> SnapshotActiveEpgWorkerTasks(string runId)
    {
        return activeEpgWorkerTasks.Values
            .Where(x => string.Equals(x.RunId, runId, StringComparison.Ordinal))
            .OrderBy(x => x.Group)
            .ThenBy(x => x.TsId)
            .ThenBy(x => x.Pass)
            .ThenBy(x => x.StartedAt)
            .ToList();
    }

    private void PruneCompletedEpgWorkerTasksForOldRuns(string currentRunId)
    {
        foreach (var item in activeEpgWorkerTasks.ToArray())
        {
            if (string.Equals(item.Value.RunId, currentRunId, StringComparison.Ordinal)
                || activeNormalEpgRuns.ContainsKey(item.Value.RunId))
                continue;

            var snapshot = item.Value.Snapshot();
            var identity = snapshot.ProcessId <= 0
                ? EpgWorkerProcessIdentityState.Missing
                : GetOwnedWorkerProcessIdentity(snapshot);
            if (identity is EpgWorkerProcessIdentityState.Match or EpgWorkerProcessIdentityState.IdentityUnknown)
            {
                StartResidualEpgWorkerExitMonitor(item.Value);
                continue;
            }

            if (!TryConvergeExitedEpgWorker(item.Value, EpgWorkerTerminalReason.Completed, "old_run_prune", snapshot.AttemptGeneration))
                StartResidualEpgWorkerExitMonitor(item.Value);
        }
    }

    private static string FormatActiveEpgWorkerTaskSummary(IReadOnlyList<ActiveEpgWorkerTask> tasks)
    {
        if (tasks.Count == 0) return "-";
        var summary = string.Join(",", tasks.Take(8).Select(t => $"{t.Group}:{t.TsId}/pass={t.Pass}/service={t.ServiceName}"));
        if (tasks.Count > 8) summary += $",...+{tasks.Count - 8}";
        return summary;
    }

    private static string FormatPidList(IEnumerable<int> pids)
    {
        var list = pids.ToList();
        return list.Count == 0 ? "-" : string.Join(",", list);
    }

    private static string SafeLog(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();


    private static async Task WaitForWorkerExitOrDeadlineAsync(int processId, DateTime deadline, CancellationToken cancellationToken)
    {
        var remaining = deadline - DateTime.Now;
        if (remaining <= TimeSpan.Zero) return;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited) return;

            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadlineCts.CancelAfter(remaining);
            try
            {
                await process.WaitForExitAsync(deadlineCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The safety deadline is the normal completion condition for a healthy pre-tune hold.
            }
        }
        catch (ArgumentException)
        {
            // The owned worker already exited before the process handle was acquired.
        }
    }

    private async Task<PreRecordProbeResult> WaitForPreRecordTargetEventAsync(
        TsGroup group,
        string tsFile,
        int processId,
        string workerName,
        ActiveEpgWorkerTask workerTaskState,
        long expectedAttemptGeneration,
        TimeSpan safetyCeiling,
        ushort? expectedNetworkId,
        ushort? expectedTransportStreamId,
        ushort? expectedServiceId,
        ushort? expectedEventId,
        DateTime? expectedStartTime,
        DateTime? expectedEndTime,
        bool persistProbeEventsToDb,
        CancellationToken ct,
        string? preferredRecordingTunerName = null,
        bool preTuneKeepWorkerUntilSafetyCeiling = false)
    {
        var started = DateTime.Now;
        var deadline = started.Add(safetyCeiling);
        var pollNo = 0;
        var lastLength = 0L;
        IReadOnlyList<EpgEvent> lastEvents = Array.Empty<EpgEvent>();

        Log("PRE_REC_EPG_PROBE_WAIT", $"TS{group.TsId}",
            $"start worker={workerName} pid={processId} safetyCeilingSec={(int)safetyCeiling.TotalSeconds} expectedNid={(expectedNetworkId?.ToString() ?? "-")} expectedTsid={(expectedTransportStreamId?.ToString() ?? "-")} expectedSid={(expectedServiceId?.ToString() ?? "-")} expectedEventId={(expectedEventId?.ToString() ?? "-")} expectedStart={(expectedStartTime?.ToString("MM/dd HH:mm:ss") ?? "-")} runtimeTuner={SafeLogValue(preferredRecordingTunerName)} policy=stop_as_soon_as_target_event_seen_runtime_tuner rule=pre_record_epg_runtime_tuner_contract");

        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var attemptSnapshot = workerTaskState.Snapshot();
            if (attemptSnapshot.AttemptGeneration != expectedAttemptGeneration) break;
            var identity = GetOwnedWorkerProcessIdentity(attemptSnapshot);
            if (identity is EpgWorkerProcessIdentityState.Missing or EpgWorkerProcessIdentityState.ReusedPid) break;

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { throw; }

            pollNo++;
            var fi = new FileInfo(tsFile);
            if (!fi.Exists || fi.Length < 188 * 256 || fi.Length == lastLength)
                continue;
            lastLength = fi.Length;

            var probe = await TryParsePreRecordProbeAsync(
                group,
                tsFile,
                expectedNetworkId,
                expectedTransportStreamId,
                expectedServiceId,
                expectedEventId,
                expectedStartTime,
                expectedEndTime,
                persistProbeEventsToDb,
                ct);
            if (probe.Events.Count > 0) lastEvents = probe.Events;

            if (probe.TargetFound)
            {
                if (preTuneKeepWorkerUntilSafetyCeiling && !string.IsNullOrWhiteSpace(preferredRecordingTunerName))
                {
                    Log("PRE_REC_EPG_PROBE_EVENT_FOUND", $"TS{group.TsId}",
                        $"result=FOUND worker={workerName} pid={processId} poll={pollNo} fileBytes={fi.Length} observedEvents={probe.Events.Count} dbWrite={(persistProbeEventsToDb ? "epg_store_user_chain_protected" : "none")} event={probe.EventSummary} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} action=transition_to_record_pretune_hold preferredTuner={SafeLogValue(preferredRecordingTunerName)} rule=release_contract");
                    Log("PRE_REC_PRETUNE_STATE", $"TS{group.TsId}",
                        $"state=record_prepare display=録画準備中 worker={workerName} pid={processId} preferredTuner={SafeLogValue(preferredRecordingTunerName)} holdUntil={deadline:MM/dd HH:mm:ss} reason=target_event_seen_same_worker_no_relaunch rule=release_contract");
                    var foundProbe = probe with { TargetFound = true };
                    // PRE_RECORD_PRETUNE_HOLD_INVARIANT:
                    // Once the target event is found, this worker already owns the planned recording tuner.
                    // Hold it until the safety deadline, cancellation, or actual process exit. Do not poll every
                    // two seconds or add an environment-dependent settle; process exit/deadline are the evidence.
                    await WaitForWorkerExitOrDeadlineAsync(processId, deadline, ct).ConfigureAwait(false);
                    return foundProbe;
                }

                Log("PRE_REC_EPG_PROBE_EVENT_FOUND", $"TS{group.TsId}",
                    $"result=FOUND worker={workerName} pid={processId} poll={pollNo} fileBytes={fi.Length} observedEvents={probe.Events.Count} dbWrite={(persistProbeEventsToDb ? "epg_store_user_chain_protected" : "none")} event={probe.EventSummary} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} action=stop_epg_worker_now rule=epg_probe_runtime_contract");
                return probe with { TargetFound = true };
            }
        }

        // Non-chain snapshot mode only: worker may flush and exit between polls, so parse the final file
        // once before declaring the target missing. The protected user-chain path intentionally keeps
        // the established DB-backed behavior unchanged.
        if (!persistProbeEventsToDb)
        try
        {
            if (File.Exists(tsFile))
            {
                var finalInfo = new FileInfo(tsFile);
                finalInfo.Refresh();
                Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
                    $"phase=worker_exit_final_stat worker={workerName} pid={processId} group={group.Group} size={finalInfo.Length} observed={finalInfo.Exists} nonZero={finalInfo.Length > 0} growing={finalInfo.Length > lastLength} elapsedMs={(int)(DateTime.Now - started).TotalMilliseconds} output={SafeLogValue(tsFile)} rule=epg_output_final_stat_contract");
                if (finalInfo.Length >= 188 * 256)
                {
                    var finalProbe = await TryParsePreRecordProbeAsync(
                        group, tsFile, expectedNetworkId, expectedTransportStreamId, expectedServiceId, expectedEventId, expectedStartTime, expectedEndTime, persistProbeEventsToDb, ct).ConfigureAwait(false);
                    if (finalProbe.Events.Count > 0) lastEvents = finalProbe.Events;
                    if (finalProbe.TargetFound)
                    {
                        Log("PRE_REC_EPG_PROBE_EVENT_FOUND", $"TS{group.TsId}",
                            $"result=FOUND_FINAL_STAT worker={workerName} pid={processId} fileBytes={finalInfo.Length} observedEvents={finalProbe.Events.Count} dbWrite={(persistProbeEventsToDb ? "epg_store_user_chain_protected" : "none")} event={finalProbe.EventSummary} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} action=accept_final_probe_snapshot rule=pre_record_epg_probe_snapshot_contract");
                        return finalProbe;
                    }
                }
            }
        }
        catch (IOException ex)
        {
            Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}", $"phase=worker_exit_final_stat_io worker={workerName} pid={processId} group={group.Group} error={SafeLog(ex.Message)} rule=epg_output_final_stat_contract");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}", $"phase=worker_exit_final_stat_access worker={workerName} pid={processId} group={group.Group} error={SafeLog(ex.Message)} rule=epg_output_final_stat_contract");
        }

        Log("PRE_REC_EPG_PROBE_EVENT_NOT_FOUND", $"TS{group.TsId}",
            $"result=NOT_FOUND worker={workerName} pid={processId} observedEvents={lastEvents.Count} dbWrite={(persistProbeEventsToDb ? "epg_store_user_chain_protected" : "none")} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} action=fall_back_to_original_reservation_time rule=pre_record_epg_probe_snapshot_contract");
        return new PreRecordProbeResult(false, lastEvents, "-");
    }

    private async Task<PreRecordProbeResult> TryParsePreRecordProbeAsync(
        TsGroup group,
        string tsFile,
        ushort? expectedNetworkId,
        ushort? expectedTransportStreamId,
        ushort? expectedServiceId,
        ushort? expectedEventId,
        DateTime? expectedStartTime,
        DateTime? expectedEndTime,
        bool persistProbeEventsToDb,
        CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetDirectoryName(tsFile) ?? Path.GetTempPath(), Path.GetFileNameWithoutExtension(tsFile) + ".probe_" + Guid.NewGuid().ToString("N") + ".ts");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            using (var src = new FileStream(tsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            var epg = await new EpgAnalyzer(settings.MaxPacketsToScan).AnalyzeAsync(tempFile, ct).ConfigureAwait(false);
            var allowedSids = group.Targets.Select(t => t.ServiceId).ToHashSet();
            var events = epg.Events
                .Where(e => allowedSids.Count == 0 || allowedSids.Contains(e.ServiceId))
                .Select(e => new EpgEvent
                {
                    NetworkId = e.NetworkId,
                    TransportStreamId = e.TransportStreamId,
                    ServiceId = e.ServiceId,
                    EventId = e.EventId,
                    ServiceName = group.Targets.FirstOrDefault(t => t.ServiceId == e.ServiceId)?.Name ?? string.Empty,
                    Title = string.Empty,
                    Description = string.Empty,
                    Genre = EpgProjection.GenreLabel(null, e.GenreCodes),
                    GenreCodes = e.GenreCodes ?? string.Empty,
                    TableId = e.BestTableId,
                    SectionNumber = e.SectionNumber,
                    VersionNumber = e.VersionNumber,
                    RawDescriptorLoopHex = e.RawDescriptorLoopHex ?? string.Empty,
                    RawShortEventDescriptorHex = e.RawShortEventDescriptorHex ?? string.Empty,
                    RawExtendedEventDescriptorHex = e.RawExtendedEventDescriptorHex ?? string.Empty,
                    RawContentDescriptorHex = e.RawContentDescriptorHex ?? string.Empty,
                    DurationSeconds = e.DurationSeconds,
                    Start = e.Start,
                    End = e.End,
                    UpdatedAt = DateTime.Now
                })
                .ToList();

            if (persistProbeEventsToDb && events.Count > 0)
            {
                try
                {
                    _ = store.Upsert(events);
                }
                catch (Exception ex)
                {
                    Log("PRE_REC_EPG_STORE", $"TS{group.TsId}",
                        $"result=FAILED events={events.Count} error={ex.GetType().Name}:{TrimLog(ex.Message)} action=preserve_probe_result rule=release_contract");
                }
            }

            Log("PRE_REC_EPG_PARSE", $"TS{group.TsId}",
                $"result=OK events={events.Count} dbWrite={(persistProbeEventsToDb ? "epg_store_user_chain_protected" : "none")} {epg.StatsLine} rule={(persistProbeEventsToDb ? "release_contract" : "pre_record_epg_probe_snapshot_contract")}");

            var target = events.FirstOrDefault(ev => IsExpectedPreRecordEvent(
                ev,
                expectedNetworkId,
                expectedTransportStreamId,
                expectedServiceId,
                expectedEventId,
                expectedStartTime,
                expectedEndTime));
            if (target is not null)
            {
                return new PreRecordProbeResult(true, events,
                    $"{target.NetworkId}/{target.TransportStreamId}/{target.ServiceId}/{target.EventId} {target.Start:MM/dd HH:mm:ss}〜{target.End:MM/dd HH:mm:ss} {SafeLog(target.Title)}");
            }
            return new PreRecordProbeResult(false, events, "-");
        }
        catch
        {
            return new PreRecordProbeResult(false, Array.Empty<EpgEvent>(), "-");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }


    private static bool IsExpectedPreRecordEvent(
        EpgEvent ev,
        ushort? expectedNetworkId,
        ushort? expectedTransportStreamId,
        ushort? expectedServiceId,
        ushort? expectedEventId,
        DateTime? expectedStartTime,
        DateTime? expectedEndTime)
    {
        if (expectedNetworkId.HasValue && ev.NetworkId != expectedNetworkId.Value) return false;
        if (expectedTransportStreamId.HasValue && ev.TransportStreamId != expectedTransportStreamId.Value) return false;
        if (expectedServiceId.HasValue && ev.ServiceId != expectedServiceId.Value) return false;
        if (expectedEventId.HasValue && expectedEventId.Value != 0)
            return ev.EventId == expectedEventId.Value;

        if (expectedStartTime.HasValue)
        {
            var startDelta = Math.Abs((ev.Start - expectedStartTime.Value).TotalMinutes);
            if (startDelta <= 180 && (!expectedEndTime.HasValue || Math.Abs((ev.End - expectedEndTime.Value).TotalMinutes) <= 240))
                return true;
        }
        return false;
    }

    private Task HandleEpgProcessEndAsync(TsGroup group, string workerName, int processId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Log("EPG_CAPTURE_END", $"TS{group.TsId}",
            $"TvAIrEpgRec終了確認。PID={processId} worker={workerName} action=release_after_owned_process_exit fixedDelayMs=0 rule=physical_epg_device_session_contract");
        return Task.CompletedTask;
    }

    private static int ResolveEpgLogicalAcquireWaitMs(DateTime plannedEnd, bool isPreRecordCheck)
    {
        if (isPreRecordCheck) return 3000;

        // 通常EPGは「空きがなければすぐ諦める」ではなく、取得runの残り予算内で論理スロットを待つ。
        // 物理DID構成・BonDriver終了速度への依存を減らすため、
        // ただし録画本線を妨げない上限として最大120秒に丸める。
        var remainingMs = (int)Math.Floor((plannedEnd - DateTime.Now).TotalMilliseconds - 30000);
        if (remainingMs <= 0) return 15000;
        return Math.Clamp(remainingMs, 15000, 120000);
    }

    /// <summary>
    /// EPG用チューナー確保。1.0.0: 空きなしを即スキップにせず、短時間だけ待って再投入する。
    /// ただし録画・STOPフェーズの安全性を優先し、長時間アイドルや無限待ちは行わない。
    /// </summary>
    private async Task<TunerLease?> AcquireEpgLeaseWithShortWaitAsync(
        TsGroup group,
        DateTime plannedEnd,
        string workerName,
        CancellationToken ct,
        bool isPreRecordCheck,
        string workerRunId,
        string? preferredRecordingTunerName = null)
    {
        var maxWaitMs = ResolveEpgLogicalAcquireWaitMs(plannedEnd, isPreRecordCheck);
        var waitStartedAt = DateTime.UtcNow;
        var waitedMs = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // 録画境界の正本はReservationSchedulerの500ms高優先監視。
            // EpgCapture側では共有境界を書き換えず、最新スナップショットを読むだけにする。

            // PRE_RECORD_EPG_RUNTIME_BOUNDARY_INVARIANT:
            // PreRecだけは親録画の開始安全領域へ食い込まないことをここで確認する。
            // normal EPGは開始Admissionを通過した後、録画timelineを理由にworker投入を止めない。
            if (isPreRecordCheck
                && RecordingLifecycleGate.IsPreRecordEpgSuppressed(
                    group.Group, plannedEnd, out var suppressUntil, out var suppressReason, out var suppressOwner, out var suppressLabel))
            {
                currentRunBlockedGroups[RunDiagnosticKey(workerRunId, group.Key)] = string.IsNullOrWhiteSpace(suppressReason) ? "pre_record_boundary" : suppressReason;
                if (RecordingLifecycleGate.ShouldLogPreRecordEpgSuppression(group.Group, suppressUntil, suppressReason, suppressOwner))
                {
                    Log("EPG_SUPPRESSED_BY_REC_DUE", $"TS{group.TsId}",
                        $"{workerName}: 録画前EPG確認の新規起動を抑止 group={group.Group} until={suppressUntil:MM/dd HH:mm:ss} " +
                        $"owner={suppressOwner} reason={suppressReason} label={suppressLabel} pass={group.Group}-{group.TsId} " +
                        "rule=pre_record_epg_runtime_tuner_contract");
                }
                return null;
            }

            TunerLease? lease = null;
            if (isPreRecordCheck && !string.IsNullOrWhiteSpace(preferredRecordingTunerName))
            {
                // PRE_RECORD_EPG_RUNTIME_TUNER_INVARIANT:
                // ReservationSchedulerが現在の空き状態と各Tunerの次録画境界から選んだ物理Tunerだけを取得する。
                // ここで別Tunerへfallbackすると安全判定を迂回するため、選択Tunerが競争で埋まった場合は単発失敗とする。
                lease = tunerPool.AcquireForEpgByName(preferredRecordingTunerName, group.Group, plannedEnd, "pre_record_epg_runtime_selected_tuner");
                if (lease is null)
                {
                    Log("PRE_REC_EPG_RUNTIME_TUNER_ACQUIRE", $"TS{group.TsId}",
                        $"result=SELECTED_TUNER_BUSY worker={workerName} selectedTuner={preferredRecordingTunerName} group={group.Group} action=no_fallback_single_attempt rule=pre_record_epg_runtime_tuner_contract");
                    return null;
                }

                Log("PRE_REC_EPG_RUNTIME_TUNER_ACQUIRE", $"TS{group.TsId}",
                    $"result=OK worker={workerName} selectedTuner={preferredRecordingTunerName} actualTuner={lease.Name} did={lease.Did} group={group.Group} targetSids=[{string.Join(',', group.Targets.Select(t => t.ServiceId).OrderBy(x => x))}] rule=pre_record_epg_runtime_tuner_contract");
            }
            else
            {
                lease = tunerPool.AcquireForEpg(group.Group, plannedEnd);
            }
            if (lease is not null)
            {
                if (waitedMs > 0)
                {
                    Log("EPG_TUNER_ACQUIRE_WAIT_OK", $"TS{group.TsId}",
                        $"{workerName}: チューナー空き待ち後に確保しました。waitMs={waitedMs} tuner={lease.Name} preferredTuner={SafeLogValue(preferredRecordingTunerName)}");
                }
                return lease;
            }

            if (waitedMs >= maxWaitMs)
            {
                Log("EPG_TUNER_BUSY", $"TS{group.TsId}",
                    $"{workerName}: EPG用途の論理チューナーが確保できませんでした（録画/視聴のTvAIr管理リソースを優先）。waitMs={waitedMs} policy=logical_resource_queue");
                return null;
            }

            if (waitedMs == 0)
            {
                Log("EPG_TUNER_WAIT", $"TS{group.TsId}",
                    $"{workerName}: EPG用途の論理チューナー待機を開始します。maxWaitMs={maxWaitMs} policy=logical_resource_queue");
            }

            var remainingMs = Math.Max(0, maxWaitMs - waitedMs);
            if (remainingMs <= 0) continue;
            await tunerPool.WaitForEpgSlotAsync(group.Group, TimeSpan.FromMilliseconds(remainingMs), ct).ConfigureAwait(false);
            waitedMs = (int)Math.Min(maxWaitMs, Math.Max(0, (DateTime.UtcNow - waitStartedAt).TotalMilliseconds));
        }
    }

    private async Task<int> ParseAndStoreAsync(
        TsGroup group, string tsFile, int attempt, int maxAttempts, CancellationToken ct, bool isPreRecordCheck)
    {
        var fi = new FileInfo(tsFile);
        if (!fi.Exists || fi.Length == 0)
        {
            Log("EPG_TS_EMPTY", $"TS{group.TsId}",
                $"result=EMPTY purpose={(isPreRecordCheck ? "pre_record_check" : "normal_epg_capture")} file={SafeLogValue(tsFile)} cleanup=delete_empty_ts rule=release_contract");
            try { if (fi.Exists) File.Delete(tsFile); } catch { }
            return 0;
        }

        try
        {
            var epg = await new EpgAnalyzer(settings.MaxPacketsToScan).AnalyzeAsync(tsFile, ct);
            var targetServiceIds = group.Targets.Select(t => t.ServiceId).Distinct().OrderBy(x => x).ToArray();
            var targetSidSet = targetServiceIds.ToHashSet();
            var targetNetworkId = group.Targets.FirstOrDefault()?.OriginalNetworkId
                ?? epg.Events.FirstOrDefault()?.NetworkId
                ?? (ushort)0;
            var serviceNameBySid = group.Targets
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .GroupBy(t => t.ServiceId)
                .ToDictionary(g => g.Key, g => g.First().Name);
            var purpose = isPreRecordCheck ? "pre_record_check" : "normal_epg_capture";

            Log("EPG_IMPORT_SCOPE", $"TS{group.TsId}",
                $"result=OK purpose={purpose} preRecord={isPreRecordCheck} rule=release_contract");

            Log("EPG_PARSE", $"TS{group.TsId}",
                $"purpose={purpose} source=ts_file group={group.Group} {epg.StatsLine} rule=release_contract");

            if (epg.IgnoredOtherTransportStreamEitSectionCount > 0)
            {
                Log("EPG_OTHER_TS_EIT_IGNORED", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} ignoredSections={epg.IgnoredOtherTransportStreamEitSectionCount} " +
                    $"acceptedTables=0x4E,0x50-0x5F ignoredTables=0x4F,0x60-0x6F action=drop_before_section_tracking_accumulator_projection_db_import " +
                    $"rule=actual_transport_stream_eit_scope");
            }

            if (epg.InvalidEitSectionCount > 0)
            {
                Log("EPG_INVALID_EIT_SECTION_DROPPED", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} invalidSections={epg.InvalidEitSectionCount} " +
                    $"validation=section_syntax_length_crc32_header_consistency action=drop_before_section_tracking_event_header_accumulator_projection_db_import " +
                    $"rule=eit_section_integrity_contract");
            }

            if (epg.IgnoredNonCurrentEitSectionCount > 0)
            {
                Log("EPG_NON_CURRENT_SECTION_IGNORED", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} ignoredSections={epg.IgnoredNonCurrentEitSectionCount} " +
                    $"currentNextIndicator=0 action=drop_before_section_tracking_accumulator_projection_db_import " +
                    $"rule=eit_current_next_contract");
            }

            if (epg.IgnoredDuplicateEitSectionCount > 0)
            {
                Log("EPG_DUPLICATE_SECTION_IGNORED", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} ignoredSections={epg.IgnoredDuplicateEitSectionCount} " +
                    $"identity=service_table_version_section action=parse_first_occurrence_only rule=eit_section_repetition_contract");
            }

            if (epg.IgnoredVersionSwitchEitSectionCount > 0)
            {
                Log("EPG_VERSION_SWITCH_SECTION_IGNORED", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} ignoredSections={epg.IgnoredVersionSwitchEitSectionCount} ignoredBasicSchedule={epg.IgnoredBasicScheduleVersionSwitchEitSectionCount} " +
                    $"snapshot=first_current_version_per_service_table action=defer_new_version_to_next_capture " +
                    $"rule=eit_capture_snapshot_version_contract");
            }

            if (epg.RejectedEventHeaderCount > 0)
            {
                foreach (var rejected in epg.RejectedEventHeaders)
                {
                    Log("EPG_EVENT_HEADER_REJECTED", $"TS{group.TsId}",
                        $"purpose={purpose} group={group.Group} nid={rejected.NetworkId} tsid={rejected.TransportStreamId} sid={rejected.ServiceId} eid={rejected.EventId} " +
                        $"tableId=0x{rejected.TableId:X2} section={rejected.SectionNumber} start={(rejected.Start == DateTime.MinValue ? "-" : rejected.Start.ToString("O"))} durationSec={rejected.DurationSeconds} " +
                        $"descriptorLoopLength={rejected.DescriptorLoopLength} eventHeaderHex={SafeLogValue(rejected.EventHeaderHex)} reason={rejected.Reason} " +
                        $"action=drop_before_accumulator_projection_db_import_and_stale_scope rule=eit_event_header_structure_contract");
                }
                Log("EPG_EVENT_HEADER_REJECTED_SUMMARY", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} rejected={epg.RejectedEventHeaderCount} rejectedBasicSchedule={epg.RejectedBasicScheduleEventHeaderCount} emitted={epg.RejectedEventHeaders.Count} " +
                    $"truncated={epg.RejectedEventHeaderCount > epg.RejectedEventHeaders.Count} " +
                    $"action=drop_before_accumulator_projection_db_import_and_stale_scope rule=eit_event_header_structure_contract");
            }

            var titleLogCount = 0;
            foreach (var a in epg.TitleDecodes
                .Where(a => targetSidSet.Count == 0 || targetSidSet.Contains(a.ServiceId))
                .OrderBy(a => a.ServiceId).ThenBy(a => a.EventId).ThenBy(a => a.TableId).ThenBy(a => a.SectionNumber)
                .Take(80))
            {
                titleLogCount++;
                Log("EPG_ARIB_TITLE", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} nid={a.NetworkId} tsid={a.TransportStreamId} sid={a.ServiceId} eid={a.EventId} " +
                    $"tableId=0x{a.TableId:X2} section={a.SectionNumber}/{a.LastSectionNumber} descriptorLoopLength={a.DescriptorLoopLength} descriptorOffset={a.DescriptorOffset} descriptorLength={a.DescriptorLength} " +
                    $"boundaryStatus={SafeLogValue(a.BoundaryStatus)} lang={SafeLogValue(a.Iso639LanguageCode)} eventNameLength={a.EventNameLength} eventNameBytesLen={a.EventNameBytesLength} eventNameBytesHex={SafeLogValue(a.EventNameBytesHex)} eventNameTrace={SafeLogValue(a.EventNameTrace)} " +
                    $"decodeRoute={SafeLogValue(a.DecodeRoute)} decodeStatus={SafeLogValue(a.DecodeStatus)} decodedTitle={SafeLogValue(TrimLog(a.DecodedTitle))} decodedTitleLength={a.DecodedTitleLength} " +
                    $"textLength={a.TextLength} textBytesLen={a.TextBytesLength} textBytesHexHead={SafeLogValue(a.TextBytesHexHead)} decodedTextHead={SafeLogValue(TrimLog(a.DecodedTextHead))} " +
                    $"emptyReason={SafeLogValue(a.EmptyReason)} rule=release_contract");
            }

            var targetSectionStatuses = epg.SectionStatuses
                .Where(st => targetSidSet.Count == 0 || targetSidSet.Contains(st.ServiceId))
                .OrderBy(st => st.ServiceId).ThenBy(st => st.TableId).ThenBy(st => st.VersionNumber)
                .ToArray();
            // stale row retirement is authorized only by the basic schedule EIT
            // (0x50-0x57), which owns event existence and timing. Present/following
            // (0x4E) and extended schedule (0x58-0x5F) may enrich the same event but
            // must not grant or revoke deletion authority.
            var staleAuthoritySectionStatuses = targetSectionStatuses
                .Where(st => st.TableId >= 0x50 && st.TableId <= 0x57)
                .ToArray();
            var incompleteSectionStatuses = staleAuthoritySectionStatuses
                .Where(st => !st.IsComplete)
                .ToArray();
            var scheduleCoverageIssues = BuildBasicScheduleTableCoverageIssues(staleAuthoritySectionStatuses, targetSidSet);
            var completenessLines = targetSectionStatuses
                .Take(40)
                .Select(st => $"SID={st.ServiceId} table=0x{st.TableId:X2} version={st.VersionNumber} lastTable=0x{st.LastTableId:X2} sec={st.SeenSectionCount}/{st.ExpectedSectionCount} seg={st.SegmentSeenTotal}/{st.SegmentExpectedTotal} complete={st.IsComplete} missingSegs=[{string.Join(",", st.MissingSegments)}]")
                .ToArray();
            if (completenessLines.Length > 0)
            {
                Log("EPG_SECTION_COMPLETENESS", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} loggedTitleDecodes={titleLogCount} " + string.Join(" / ", completenessLines) + " rule=release_contract");
            }

            var rawEvents = BuildRawEventsFromAccumulatorProjection(epg.Events, targetSidSet, serviceNameBySid);
            LogAccumulatorRawEventProjectionContract(group, purpose, targetServiceIds, epg.EventAccumulatorAudits, epg.Events, rawEvents);

            LogDbInsertPrecheck(group, purpose, targetServiceIds, rawEvents);
            var strictTitleBodyMergeStats = ApplyStrictTitleBodyCanonicalMerge(group, purpose, targetServiceIds, epg.EventObservations, rawEvents);
            LogStrictTitleBodyCanonicalMergeContract(group, purpose, targetServiceIds, strictTitleBodyMergeStats);
            rawEvents = rawEvents
                .OrderBy(e => e.ServiceId)
                .ThenBy(e => e.Start)
                .ThenBy(e => e.EventId)
                .ThenBy(e => e.TableId)
                .ToList();

            var basicScheduleEventKeys = epg.EventAccumulatorAudits
                .Where(a => a.HasBasicScheduleObservation)
                .Select(a => (a.NetworkId, a.TransportStreamId, a.ServiceId, a.EventId, a.Start, a.DurationSeconds))
                .ToHashSet();
            var staleAuthorityEvents = rawEvents
                .Where(e => basicScheduleEventKeys.Contains((e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId, e.Start, e.DurationSeconds)))
                .ToList();

            var staleRetireBlockReasons = new List<string>();
            if (staleAuthorityEvents.Count == 0) staleRetireBlockReasons.Add("no_captured_basic_schedule_events");
            if (incompleteSectionStatuses.Length > 0) staleRetireBlockReasons.Add("incomplete_section_snapshot");
            if (scheduleCoverageIssues.Length > 0) staleRetireBlockReasons.Add("incomplete_schedule_table_coverage");
            if (epg.RejectedBasicScheduleEventHeaderCount > 0) staleRetireBlockReasons.Add("rejected_basic_schedule_event_header");
            if (epg.IgnoredBasicScheduleVersionSwitchEitSectionCount > 0) staleRetireBlockReasons.Add("basic_schedule_version_switch_during_capture");
            var staleRetireEligible = staleAuthorityEvents.Count > 0 && staleRetireBlockReasons.Count == 0;

            var importGateWaitStartedAt = DateTime.Now;
            await epgImportCommitGate.WaitAsync(ct).ConfigureAwait(false);
            int upsertedCount;
            int promotedReservations;
            EpgUpsertStorageStats storageStats;
            EpgStaleRetireStats staleStats;
            try
            {
                var importGateWaitMs = (int)Math.Max(0, (DateTime.Now - importGateWaitStartedAt).TotalMilliseconds);
                Log("EPG_IMPORT_COMMIT_GATE", $"TS{group.TsId}",
                    $"result=ENTER purpose={purpose} group={group.Group} waitMs={importGateWaitMs} action=serialize_sqlite_write_keep_ts_analysis_parallel rule=epg_import_commit_contract");

                var commitResult = store.CommitCapture(
                    rawEvents,
                    staleRetireEligible ? staleAuthorityEvents : null);
                upsertedCount = commitResult.Upsert.Count;
                storageStats = commitResult.Upsert.Stats;
                staleStats = commitResult.StaleRetire;
                try
                {
                    // DB identity promotion belongs to the same SQLite commit lane, but the common
                    // allocation route must not hold this gate. Normal EPG already runs one complete
                    // post-import route at the EpgScheduler boundary.
                    promotedReservations = projectionPromotion.PromotePending(
                        $"EpgCapture:{purpose}:{group.Group}:TS{group.TsId}",
                        runAllocationRoute: false);
                }
                catch (Exception ex)
                {
                    promotedReservations = 0;
                    Log("RESERVATION_PROJECTION_PROMOTE", $"TS{group.TsId}",
                        $"result=ERROR source=EpgCapture error={SafeLogValue(ex.Message)} rule=release_contract");
                }
            }
            finally
            {
                epgImportCommitGate.Release();
            }

            // release_contract IncrementalKeywordMatchAfterCommittedEpgImport:
            // A normal EPG run commits each transport stream independently, and the programme
            // guide can expose those committed rows immediately. Waiting until the entire
            // multi-TS run completes before running KeywordMatcher creates a dangerous interval
            // where a visible matching programme is still unreserved. If the run is cancelled or
            // fails later, that interval becomes permanent. Match immediately after each
            // successful DB commit, outside the SQLite commit gate. The existing final
            // EpgCompletePostImport pass remains as an idempotent convergence pass.
            if (!isPreRecordCheck && upsertedCount > 0)
            {
                try
                {
                    var keywordAdded = keywordMatcher.RunMatching(rawEvents);
                    if (keywordAdded > 0)
                    {
                        projectionPromotion.RunAllocationRoute(
                            $"EpgCapture:{purpose}:{group.Group}:TS{group.TsId}:KeywordMatch",
                            ReservationAllocationWakeRefreshMode.BoundedCoalesce);
                    }
                    Log("EPG_INCREMENTAL_KEYWORD_MATCH", $"TS{group.TsId}",
                        $"result=OK purpose={purpose} group={group.Group} imported={upsertedCount} keywordAdded={keywordAdded} allocationRun={keywordAdded > 0} timing=after_committed_ts_import_before_full_epg_completion rule=incremental_keyword_match_contract");
                }
                catch (Exception ex)
                {
                    Log("EPG_INCREMENTAL_KEYWORD_MATCH", $"TS{group.TsId}",
                        $"result=ERROR purpose={purpose} group={group.Group} imported={upsertedCount} error={SafeLogValue(ex.Message)} action=retry_by_final_epg_complete_pass rule=incremental_keyword_match_contract");
                }
            }

            // Pre-record probes do not pass through EpgScheduler's post-import route. If this
            // narrowly scoped import promoted a reservation, run its route only after releasing
            // the SQLite import gate. Normal EPG defers all allocation work to the single
            // EpgCompletePostImport route.
            if (isPreRecordCheck && promotedReservations > 0)
            {
                try
                {
                    projectionPromotion.RunAllocationRoute(
                        $"EpgCapture:{purpose}:{group.Group}:TS{group.TsId}");
                }
                catch (Exception ex)
                {
                    Log("RESERVATION_PROJECTION_PROMOTE", $"TS{group.TsId}",
                        $"result=ERROR phase=post_commit_allocation source=EpgCapture promoted={promotedReservations} error={SafeLogValue(ex.Message)} rule=release_contract");
                }
            }

            Log("EPG_EVENT_DESCRIPTOR_STORAGE_CONTRACT", $"TS{group.TsId}",
                $"result=OK purpose={purpose} group={group.Group} incoming={storageStats.Incoming} " +
                $"incomingRawShortPresent={storageStats.IncomingRawShortPresent} incomingRawExtendedPresent={storageStats.IncomingRawExtendedPresent} incomingRawContentPresent={storageStats.IncomingRawContentPresent} " +
                $"existingRowRead=none policy=current_capture_raw_descriptor_is_storage_authority " +
                $"titleSynthesis=none bodyToTitlePromotion=none dbSchemaMutation=none rule=release_contract");

            var staleScopeStart = staleStats.ScopeStart.HasValue ? staleStats.ScopeStart.Value.ToString("O") : "-";
            var staleScopeEnd = staleStats.ScopeEnd.HasValue ? staleStats.ScopeEnd.Value.ToString("O") : "-";
            var incompleteSectionSample = incompleteSectionStatuses
                .Take(8)
                .Select(st => $"{st.ServiceId}:0x{st.TableId:X2}:v{st.VersionNumber}:{st.SeenSectionCount}/{st.ExpectedSectionCount}")
                .ToArray();
            Log("EPG_STALE_EVENT_RETIRE_CONTRACT", $"TS{group.TsId}",
                $"result={(staleRetireEligible ? "OK" : "SKIPPED_INCOMPLETE_CAPTURE")} purpose={purpose} group={group.Group} services={staleStats.Services} capturedIncoming={rawEvents.Count} basicScheduleAuthorityIncoming={staleAuthorityEvents.Count} retireIncoming={staleStats.IncomingEvents} deleted={staleStats.DeletedRows} " +
                $"scopeStart={staleScopeStart} scopeEnd={staleScopeEnd} blockReasons=[{string.Join(",", staleRetireBlockReasons)}] incompleteSectionSample=[{string.Join(",", incompleteSectionSample)}] " +
                $"policy=delete_only_from_complete_basic_schedule_snapshot authorityTables=0x50-0x57 supplementaryTables=0x4E,0x58-0x5F upsertPartialCapture=allowed titleSynthesis=none bodyToTitlePromotion=none reservationTitleBorrow=none startupDbClear=none dbSchemaMutation=none rule=release_contract");

            var rawStoredTitleBlankCount = rawEvents.Count(e => string.IsNullOrEmpty(e.Title));
            var importedSids = rawEvents.Select(e => e.ServiceId).Distinct().OrderBy(x => x).ToArray();
            Log("EPG_IMPORT", $"TS{group.TsId}",
                $"result=OK purpose={purpose} " +
                $"imported={upsertedCount} rawEvents={rawEvents.Count} rawStoredTitleBlank={rawStoredTitleBlankCount} displayTitleSource=cellText_decoder " +
                $"targetSids=[{string.Join(",", targetServiceIds)}] importSids=[{string.Join(",", importedSids)}] " +
                $"rule=release_contract");

            try { File.Delete(tsFile); } catch { }
            return upsertedCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log("EPG_PARSE_CANCELLED", $"TS{group.TsId}",
                $"result=CANCELLED purpose={(isPreRecordCheck ? "pre_record_check" : "normal_epg_capture")} action=propagate_without_retry rule=epg_import_commit_contract");
            throw;
        }
        catch (Exception ex)
        {
            Log("EPG_PARSE_FAIL", $"TS{group.TsId}",
                $"result=ERROR purpose={(isPreRecordCheck ? "pre_record_check" : "normal_epg_capture")} error={ex.GetType().Name}:{SafeLogValue(ex.Message)} rule=release_contract");
            return 0;
        }
    }


    // ─── TS グループ構築 ──────────────────────────────────────────

    internal static string NormalizeTargetScope(string? value)
    {
        var v = (value ?? "All").Trim().ToUpperInvariant();
        return v switch
        {
            "GR" or "TERRESTRIAL" or "地上波" => "GR",
            "BS" => "BS",
            "CS" => "CS",
            "BSCS" or "BS/CS" => "BSCS",
            _ => "All"
        };
    }

    internal static List<TsGroup> FilterGroupsByScope(IReadOnlyList<TsGroup> groups, string scope)
    {
        return scope switch
        {
            "GR" => groups.Where(g => g.Group == "GR").ToList(),
            "BS" => groups.Where(g => g.Group == "BSCS" && g.Targets.Any(t => t.OriginalNetworkId == 4)).ToList(),
            "CS" => groups.Where(g => g.Group == "BSCS" && g.Targets.All(t => t.OriginalNetworkId != 4)).ToList(),
            "BSCS" => groups.Where(g => g.Group == "BSCS").ToList(),
            _ => groups.ToList()
        };
    }


    private static List<TsGroup> FilterGroupsForExpectedService(
        IReadOnlyList<TsGroup> groups,
        ushort? expectedNetworkId,
        ushort? expectedTransportStreamId,
        ushort? expectedServiceId,
        string? expectedServiceName)
    {
        var serviceName = (expectedServiceName ?? string.Empty).Trim();
        return groups
            .Where(g => g.Targets.Any(t =>
                (!expectedNetworkId.HasValue || t.OriginalNetworkId == expectedNetworkId.Value) &&
                (!expectedTransportStreamId.HasValue || t.TransportStreamId == expectedTransportStreamId.Value) &&
                (!expectedServiceId.HasValue || t.ServiceId == expectedServiceId.Value) &&
                (string.IsNullOrWhiteSpace(serviceName) || string.Equals(t.Name, serviceName, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    private static string[] BuildBasicScheduleTableCoverageIssues(
        IReadOnlyList<EpgSectionStatus> sectionStatuses,
        IReadOnlySet<ushort> targetSidSet)
    {
        var serviceIds = targetSidSet.Count > 0
            ? targetSidSet.OrderBy(x => x).ToArray()
            : sectionStatuses.Select(x => x.ServiceId).Distinct().OrderBy(x => x).ToArray();
        var issues = new List<string>();
        foreach (var sid in serviceIds)
        {
            var schedule = sectionStatuses
                .Where(x => x.ServiceId == sid && x.TableId >= 0x50 && x.TableId <= 0x57)
                .OrderBy(x => x.TableId)
                .ToArray();
            if (schedule.Length == 0)
            {
                issues.Add($"SID={sid}:schedule_tables=none");
                continue;
            }

            var lastTableIds = schedule.Select(x => x.LastTableId).Distinct().OrderBy(x => x).ToArray();
            if (lastTableIds.Length != 1)
            {
                issues.Add($"SID={sid}:basic:last_table_inconsistent=[{string.Join('.', lastTableIds.Select(x => $"0x{x:X2}"))}]");
                continue;
            }

            var lastTableId = lastTableIds[0];
            if (lastTableId < 0x50 || lastTableId > 0x57)
            {
                issues.Add($"SID={sid}:basic:last_table_out_of_family=0x{lastTableId:X2}");
                continue;
            }

            var seen = schedule.Select(x => x.TableId).ToHashSet();
            var missing = Enumerable.Range(0x50, lastTableId - 0x50 + 1)
                .Select(x => (byte)x)
                .Where(x => !seen.Contains(x))
                .ToArray();
            if (missing.Length > 0)
                issues.Add($"SID={sid}:basic:expected=0x50-0x{lastTableId:X2}:missing=[{string.Join('.', missing.Select(x => $"0x{x:X2}"))}]");
        }
        return issues.ToArray();
    }

    private static string SafeLogValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("\r", " ").Replace("\n", " ").Trim();




    private static string EventIdentityKeyForAudit(ushort nid, ushort tsid, ushort sid, ushort eid)
        => $"{nid}:{tsid}:{sid}:{eid}";

    private static string EventIdentityKeyForAudit(EpgEvent e)
        => EventIdentityKeyForAudit(e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId);

    private static string EventIdentityKeyForAudit(ParsedEpgEvent e)
        => EventIdentityKeyForAudit(e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId);

    private static string EventIdentityKeyForAudit(EpgEventObservation e)
        => EventIdentityKeyForAudit(e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId);

    private static string ServiceTimeKeyForAudit(ushort nid, ushort tsid, ushort sid, DateTime start, int durationSeconds)
        => $"{nid}:{tsid}:{sid}:{start:O}:{durationSeconds}";

    private static string ServiceTimeKeyForAudit(EpgEvent e)
        => ServiceTimeKeyForAudit(e.NetworkId, e.TransportStreamId, e.ServiceId, e.Start, e.DurationSeconds);

    private static string ServiceTimeKeyForAudit(ParsedEpgEvent e)
        => ServiceTimeKeyForAudit(e.NetworkId, e.TransportStreamId, e.ServiceId, e.Start, e.DurationSeconds);

    private static string ServiceTimeKeyForAudit(EpgEventObservation e)
        => ServiceTimeKeyForAudit(e.NetworkId, e.TransportStreamId, e.ServiceId, e.Start, e.DurationSeconds);

    private static bool IsScheduleBodyTable(byte tableId) => tableId is >= 0x58 and <= 0x5F;
    private static bool HasRawShort(EpgEvent e) => !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex);
    private static bool HasRawShort(ParsedEpgEvent e) => !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex);
    private static bool HasRawShort(EpgEventObservation e) => !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex);
    private static bool HasRawExtended(EpgEvent e) => !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex);
    private static bool HasRawExtended(ParsedEpgEvent e) => !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex);
    private static bool HasRawExtended(EpgEventObservation e) => !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex);











    private static bool IsScheduleShortCarrierCandidateTable(byte tableId) => tableId is >= 0x50 and <= 0x57;









    private const int EitAlignmentNearbyTitleStartWindowMinutes = 240;







    private List<EpgEvent> BuildRawEventsFromAccumulatorProjection(
        IReadOnlyList<ParsedEpgEvent> accumulatorEvents,
        HashSet<ushort> targetSidSet,
        IReadOnlyDictionary<ushort, string> serviceNameBySid)
    {
        return accumulatorEvents
            .Where(e => targetSidSet.Count == 0 || targetSidSet.Contains(e.ServiceId))
            .Where(e => e.Start != DateTime.MinValue && e.End > e.Start)
            .OrderBy(e => e.ServiceId)
            .ThenBy(e => e.Start)
            .ThenBy(e => e.EventId)
            .Select(e => new EpgEvent
            {
                NetworkId = e.NetworkId,
                TransportStreamId = e.TransportStreamId,
                ServiceId = e.ServiceId,
                EventId = e.EventId,
                ServiceName = serviceNameBySid.TryGetValue(e.ServiceId, out var svcName) ? svcName : string.Empty,
                // UI title/description are intentionally not written here. The DB stores raw ARIB descriptors,
                // and the program guide derives cellText from the common raw descriptor decoder.
                Title = string.Empty,
                Description = string.Empty,
                Genre = EpgProjection.GenreLabel(null, e.GenreCodes),
                GenreCodes = e.GenreCodes ?? string.Empty,
                TableId = e.BestTableId,
                SectionNumber = e.SectionNumber,
                VersionNumber = e.VersionNumber,
                RawDescriptorLoopHex = e.RawDescriptorLoopHex ?? string.Empty,
                RawShortEventDescriptorHex = e.RawShortEventDescriptorHex ?? string.Empty,
                RawExtendedEventDescriptorHex = e.RawExtendedEventDescriptorHex ?? string.Empty,
                RawContentDescriptorHex = e.RawContentDescriptorHex ?? string.Empty,
                DurationSeconds = e.DurationSeconds,
                Start = e.Start,
                End = e.End,
                UpdatedAt = DateTime.Now
            })
            .ToList();
    }


    private void LogAccumulatorRawEventProjectionContract(
        TsGroup group,
        string purpose,
        IReadOnlyList<ushort> targetServiceIds,
        IReadOnlyList<EpgEventAccumulatorAudit> accumulatorAudits,
        IReadOnlyList<ParsedEpgEvent> accumulatorEvents,
        IReadOnlyList<EpgEvent> rawEvents)
    {
        var targetSidSet = targetServiceIds.ToHashSet();
        var scopedAccumulators = accumulatorAudits
            .Where(e => targetSidSet.Count == 0 || targetSidSet.Contains(e.ServiceId))
            .Where(e => e.Start != DateTime.MinValue && e.DurationSeconds > 0)
            .ToList();
        var scopedAccumulatorEvents = accumulatorEvents
            .Where(e => targetSidSet.Count == 0 || targetSidSet.Contains(e.ServiceId))
            .Where(e => e.Start != DateTime.MinValue && e.End > e.Start)
            .ToList();
        var scopedRawEvents = rawEvents
            .Where(e => targetSidSet.Count == 0 || targetSidSet.Contains(e.ServiceId))
            .Where(e => e.Start != DateTime.MinValue && e.End > e.Start)
            .ToList();

        static (ushort Nid, ushort Tsid, ushort Sid, ushort Eid, DateTime Start, int Duration) ParsedKey(ParsedEpgEvent e) =>
            (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId, e.Start, e.DurationSeconds);
        static (ushort Nid, ushort Tsid, ushort Sid, ushort Eid, DateTime Start, int Duration) RawKey(EpgEvent e) =>
            (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId, e.Start, e.DurationSeconds);
        static string KeyText((ushort Nid, ushort Tsid, ushort Sid, ushort Eid, DateTime Start, int Duration) k) =>
            $"{k.Nid}:{k.Tsid}:{k.Sid}:eid{k.Eid}:t{k.Start:MM-ddTHH:mm}:d{k.Duration}";

        var accumulatorKeys = scopedAccumulatorEvents.Select(ParsedKey).ToHashSet();
        var rawKeys = scopedRawEvents.Select(RawKey).ToHashSet();
        var missingRawKeys = accumulatorKeys.Except(rawKeys).Take(12).Select(KeyText).ToArray();
        var extraRawKeys = rawKeys.Except(accumulatorKeys).Take(12).Select(KeyText).ToArray();

        var accumulatorByKey = scopedAccumulatorEvents
            .GroupBy(ParsedKey)
            .ToDictionary(g => g.Key, g => g.First());
        var rawByKey = scopedRawEvents
            .GroupBy(RawKey)
            .ToDictionary(g => g.Key, g => g.First());
        var descriptorMismatch = 0;
        var mismatchSamples = new List<string>();
        foreach (var key in accumulatorKeys.Intersect(rawKeys).Take(10000))
        {
            var acc = accumulatorByKey[key];
            var raw = rawByKey[key];
            var accShort = HasRawShort(acc);
            var accExt = HasRawExtended(acc);
            var rawShort = HasRawShort(raw);
            var rawExt = HasRawExtended(raw);
            if (accShort != rawShort || accExt != rawExt)
            {
                descriptorMismatch++;
                if (mismatchSamples.Count < 12)
                    mismatchSamples.Add($"key={KeyText(key)}:accShort={accShort}:rawShort={rawShort}:accExt={accExt}:rawExt={rawExt}");
            }
        }

        var accumulatorTitleAndBody = scopedAccumulatorEvents.Count(e => HasRawShort(e) && HasRawExtended(e));
        var rawTitleAndBody = scopedRawEvents.Count(e => HasRawShort(e) && HasRawExtended(e));
        var rawBodyOnly = scopedRawEvents.Count(e => !HasRawShort(e) && HasRawExtended(e));
        var rawTitleOnly = scopedRawEvents.Count(e => HasRawShort(e) && !HasRawExtended(e));
        var scheduleTitleBodyMergedRaw = scopedRawEvents.Count(e => HasRawShort(e) && HasRawExtended(e) && IsScheduleShortCarrierCandidateTable(e.TableId));
        var expectedPairMergedAccumulator = scopedAccumulators.Count(e => e.HasRawShort && e.HasRawExtended && e.HasExpectedScheduleTitleBodyPair);
        var projectedCountMismatch = scopedAccumulatorEvents.Count != scopedRawEvents.Count;
        var keyMismatch = missingRawKeys.Length > 0 || extraRawKeys.Length > 0;
        var projectionMismatch = projectedCountMismatch || keyMismatch || descriptorMismatch > 0 || accumulatorTitleAndBody != rawTitleAndBody;
        var result = projectionMismatch ? "MISMATCH" : expectedPairMergedAccumulator > 0 ? "PROJECTED_MERGED" : rawBodyOnly > 0 ? "PROJECTED_WITH_RESIDUAL_BODY_ONLY" : "PROJECTED_OK";
        var samples = scopedRawEvents
            .Where(e => HasRawShort(e) && HasRawExtended(e) || (!HasRawShort(e) && HasRawExtended(e) && IsScheduleBodyTable(e.TableId)))
            .OrderByDescending(e => HasRawShort(e) && HasRawExtended(e))
            .ThenBy(e => e.ServiceId)
            .ThenBy(e => e.Start)
            .Take(24)
            .Select(e => $"sid={e.ServiceId}:eid={e.EventId}:t{e.Start:MM-ddTHH:mm}:d{e.DurationSeconds}:table=0x{e.TableId:X2}/s{e.SectionNumber}:rawShort={HasRawShort(e)}:rawExtended={HasRawExtended(e)}:shortBytes={HexSequenceByteLength(e.RawShortEventDescriptorHex)}:extendedBytes={HexSequenceByteLength(e.RawExtendedEventDescriptorHex)}")
            .ToList();
        if (mismatchSamples.Count > 0)
            samples.AddRange(mismatchSamples);

        Log("EIT_ACCUMULATOR_RAW_EVENT_PROJECTION_CONTRACT", $"TS{group.TsId}",
            $"result={result} purpose={purpose} group={group.Group} tsid={group.TsId} targetSids=[{string.Join(",", targetServiceIds)}] " +
            $"projectionSource=parser_event_accumulator rawEventsSource=accumulator_projection identityKey=nid_tsid_sid_eventId_start_duration " +
            $"accumulatorEvents={scopedAccumulatorEvents.Count} rawEvents={scopedRawEvents.Count} accumulatorTitleAndBody={accumulatorTitleAndBody} rawTitleAndBody={rawTitleAndBody} " +
            $"expectedPairMergedAccumulator={expectedPairMergedAccumulator} scheduleTitleBodyMergedRaw={scheduleTitleBodyMergedRaw} rawTitleOnly={rawTitleOnly} rawBodyOnly={rawBodyOnly} " +
            $"projectedCountMismatch={projectedCountMismatch} keyMismatch={keyMismatch} descriptorMismatch={descriptorMismatch} missingRawKeys=[{string.Join('.', missingRawKeys)}] extraRawKeys=[{string.Join('.', extraRawKeys)}] " +
            $"failClosed=True residualBodyOnlyKept=False dbPostFix=none renderMutation=none titleSynthesis=none bodyToTitlePromotion=none existingDbTitleBurnIn=none sample={SafeLogValue(TrimLog(string.Join('|', samples), 7600))} " +
            $"rule=release_contract");

        var rawTitleTables = string.Join(",", scopedRawEvents
            .Where(e => HasRawShort(e))
            .Select(e => $"0x{e.TableId:X2}")
            .Distinct()
            .OrderBy(e => e));
        var rawBodyTables = string.Join(",", scopedRawEvents
            .Where(e => !HasRawShort(e) && HasRawExtended(e) && IsScheduleBodyTable(e.TableId))
            .Select(e => $"0x{e.TableId:X2}")
            .Distinct()
            .OrderBy(e => e));
        var rawMergedTables = string.Join(",", scopedRawEvents
            .Where(e => HasRawShort(e) && HasRawExtended(e))
            .Select(e => $"0x{e.TableId:X2}")
            .Distinct()
            .OrderBy(e => e));
        var waveScopeResult = projectionMismatch ? "MISMATCH" : "OK";
        Log("EIT_ACCUMULATOR_PROJECTION_WAVE_SCOPE_CONTRACT", $"TS{group.TsId}",
            $"result={waveScopeResult} purpose={purpose} group={group.Group} tsid={group.TsId} targetSids=[{string.Join(",", targetServiceIds)}] " +
            $"projectionSource=parser_event_accumulator rawEventsSource=accumulator_projection identityKey=nid_tsid_sid_eventId_start_duration waveAgnostic=True " +
            $"accumulatorEvents={scopedAccumulatorEvents.Count} rawEvents={scopedRawEvents.Count} titleAndBody={rawTitleAndBody} bodyOnly={rawBodyOnly} titleOnly={rawTitleOnly} " +
            $"expectedPairMerged={expectedPairMergedAccumulator} scheduleTitleBodyMergedRaw={scheduleTitleBodyMergedRaw} titleTables=[{rawTitleTables}] bodyOnlyTables=[{rawBodyTables}] mergedTables=[{rawMergedTables}] " +
            $"projectedCountMismatch={projectedCountMismatch} keyMismatch={keyMismatch} descriptorMismatch={descriptorMismatch} " +
            $"mergeIdentityUses=signal_nid_tsid_sid_eventId_start_duration mergeIdentityDoesNotUse=bonDriver_did_chspace_chi_channelName_ch2Line settingMutation=none captureDurationMutation=none " +
            $"failClosed=True residualBodyOnlyKept=False dbPostFix=none renderMutation=none titleSynthesis=none bodyToTitlePromotion=none existingDbTitleBurnIn=none action=audit_only " +
            $"rule=release_contract");
    }



    private sealed record StrictTitleBodyCanonicalMergeStats(
        int RawEventsBefore,
        int RawEventsAfter,
        int BodyOnlyCandidates,
        int Eligible,
        int CanonicalizedFromBodyRow,
        int MergedIntoExistingTitleRow,
        int RemovedBodyRows,
        int Ineligible,
        int AmbiguousStrictMultiple,
        int NoStrictCandidate,
        int TablePairMismatch,
        int MissingTitleRawShort,
        int MissingBodyRawExtended,
        int RawExtendedMerged,
        int RawExtendedAlreadyCovered,
        string Sample);

    private StrictTitleBodyCanonicalMergeStats ApplyStrictTitleBodyCanonicalMerge(
        TsGroup group,
        string purpose,
        IReadOnlyList<ushort> targetServiceIds,
        IReadOnlyList<EpgEventObservation> observations,
        List<EpgEvent> rawEvents)
    {
        var targetSidSet = targetServiceIds.ToHashSet();
        var rawEventsBefore = rawEvents.Count;
        var titleRows = observations
            .Where(e => targetSidSet.Count == 0 || targetSidSet.Contains(e.ServiceId))
            .Where(e => IsScheduleShortCarrierCandidateTable(e.TableId) && HasRawShort(e))
            .ToList();
        var bodyRows = rawEvents
            .Where(e => targetSidSet.Count == 0 || targetSidSet.Contains(e.ServiceId))
            .Where(e => IsScheduleBodyTable(e.TableId) && !HasRawShort(e) && HasRawExtended(e))
            .OrderBy(e => e.ServiceId).ThenBy(e => e.Start).ThenBy(e => e.EventId).ThenBy(e => e.TableId).ThenBy(e => e.SectionNumber)
            .ToList();

        static bool SameObservedEventTime(EpgEventObservation title, EpgEvent body) =>
            title.NetworkId == body.NetworkId &&
            title.TransportStreamId == body.TransportStreamId &&
            title.ServiceId == body.ServiceId &&
            title.EventId == body.EventId &&
            title.Start == body.Start &&
            title.DurationSeconds == body.DurationSeconds;

        static bool SameRawEventTime(EpgEvent title, EpgEvent body) =>
            title.NetworkId == body.NetworkId &&
            title.TransportStreamId == body.TransportStreamId &&
            title.ServiceId == body.ServiceId &&
            title.EventId == body.EventId &&
            title.Start == body.Start &&
            title.DurationSeconds == body.DurationSeconds;

        static bool ExpectedTablePair(byte titleTable, byte bodyTable) =>
            bodyTable >= 0x58 && bodyTable <= 0x5F &&
            titleTable >= 0x50 && titleTable <= 0x57 &&
            titleTable == bodyTable - 0x08;

        static string CompactBody(EpgEvent e) => $"0x{e.TableId:X2}/s{e.SectionNumber}/eid{e.EventId}/t{e.Start:MM-ddTHH:mm}/d{e.DurationSeconds}";
        static string CompactTitle(EpgEventObservation e) => $"0x{e.TableId:X2}/s{e.SectionNumber}/eid{e.EventId}/t{e.Start:MM-ddTHH:mm}/d{e.DurationSeconds}";
        static string CompactRawTitle(EpgEvent e) => $"0x{e.TableId:X2}/s{e.SectionNumber}/eid{e.EventId}/t{e.Start:MM-ddTHH:mm}/d{e.DurationSeconds}";

        var eligible = 0;
        var canonicalizedFromBodyRow = 0;
        var mergedIntoExistingTitleRow = 0;
        var removedBodyRows = 0;
        var ambiguousStrictMultiple = 0;
        var noStrictCandidate = 0;
        var tablePairMismatch = 0;
        var missingTitleRawShort = 0;
        var missingBodyRawExtended = 0;
        var rawExtendedMerged = 0;
        var rawExtendedAlreadyCovered = 0;
        var samples = new List<string>();
        var bodiesToRemove = new HashSet<EpgEvent>();

        foreach (var body in bodyRows)
        {
            if (!HasRawExtended(body))
            {
                missingBodyRawExtended++;
                bodiesToRemove.Add(body);
                continue;
            }

            var strictSameEvent = titleRows
                .Where(t => SameObservedEventTime(t, body))
                .OrderBy(t => t.TableId).ThenBy(t => t.SectionNumber).ThenBy(t => t.ObservationIndex)
                .ToList();
            if (strictSameEvent.Count == 0)
            {
                noStrictCandidate++;
                bodiesToRemove.Add(body);
                if (samples.Count < 36)
                    samples.Add($"sid={body.ServiceId}:body={CompactBody(body)}:merged=False:removed=True:reason=no_strict_candidate");
                continue;
            }

            var rawShortStrict = strictSameEvent.Where(HasRawShort).ToList();
            if (rawShortStrict.Count == 0)
            {
                missingTitleRawShort++;
                bodiesToRemove.Add(body);
                if (samples.Count < 36)
                    samples.Add($"sid={body.ServiceId}:body={CompactBody(body)}:merged=False:removed=True:reason=missing_title_rawShort:strict={strictSameEvent.Count}");
                continue;
            }

            var expectedPairStrict = rawShortStrict.Where(t => ExpectedTablePair(t.TableId, body.TableId)).ToList();
            if (expectedPairStrict.Count == 0)
            {
                tablePairMismatch++;
                bodiesToRemove.Add(body);
                if (samples.Count < 36)
                    samples.Add($"sid={body.ServiceId}:body={CompactBody(body)}:merged=False:removed=True:reason=table_pair_mismatch:strict={rawShortStrict.Count}:candidates=[{string.Join('.', rawShortStrict.Take(6).Select(CompactTitle))}]");
                continue;
            }
            if (expectedPairStrict.Count != 1)
            {
                ambiguousStrictMultiple++;
                bodiesToRemove.Add(body);
                if (samples.Count < 36)
                    samples.Add($"sid={body.ServiceId}:body={CompactBody(body)}:merged=False:removed=True:reason=ambiguous_expected_table_pair:expectedPair={expectedPairStrict.Count}:candidates=[{string.Join('.', expectedPairStrict.Take(6).Select(CompactTitle))}]");
                continue;
            }

            var title = expectedPairStrict[0];
            eligible++;

            var existingTitleRows = rawEvents
                .Where(e => !ReferenceEquals(e, body))
                .Where(e => IsScheduleShortCarrierCandidateTable(e.TableId) && HasRawShort(e))
                .Where(e => SameRawEventTime(e, body))
                .Where(e => ExpectedTablePair(e.TableId, body.TableId))
                .OrderBy(e => e.TableId).ThenBy(e => e.SectionNumber)
                .ToList();

            if (existingTitleRows.Count > 1)
            {
                ambiguousStrictMultiple++;
                bodiesToRemove.Add(body);
                if (samples.Count < 36)
                    samples.Add($"sid={body.ServiceId}:body={CompactBody(body)}:merged=False:removed=True:reason=ambiguous_existing_title_rows:candidates=[{string.Join('.', existingTitleRows.Take(6).Select(CompactRawTitle))}]");
                continue;
            }

            var canonical = existingTitleRows.Count == 1
                ? existingTitleRows[0]
                : CreateCanonicalTitleBodyEvent(title, body);

            var beforeExt = canonical.RawExtendedEventDescriptorHex;
            var beforeLoop = canonical.RawDescriptorLoopHex;
            canonical.RawExtendedEventDescriptorHex = MergeRawHex(canonical.RawExtendedEventDescriptorHex, body.RawExtendedEventDescriptorHex);
            canonical.RawDescriptorLoopHex = MergeRawHex(canonical.RawDescriptorLoopHex, body.RawDescriptorLoopHex);
            canonical.RawContentDescriptorHex = MergeRawHex(canonical.RawContentDescriptorHex, body.RawContentDescriptorHex);
            canonical.RawShortEventDescriptorHex = MergeRawHex(canonical.RawShortEventDescriptorHex, title.RawShortEventDescriptorHex);
            if (canonical.RawDescriptorLoopHex == beforeLoop)
            {
                // no-op marker only for the audit counters below
            }

            if (!ReferenceEquals(canonical, body) && !rawEvents.Contains(canonical))
            {
                rawEvents.Add(canonical);
                canonicalizedFromBodyRow++;
            }
            else if (!ReferenceEquals(canonical, body))
            {
                mergedIntoExistingTitleRow++;
            }

            if (string.Equals(beforeExt, canonical.RawExtendedEventDescriptorHex, StringComparison.Ordinal))
                rawExtendedAlreadyCovered++;
            else
                rawExtendedMerged++;

            bodiesToRemove.Add(body);
            if (samples.Count < 36)
            {
                var op = existingTitleRows.Count == 1 ? "merged_into_existing_title_row" : "canonicalized_from_body_row";
                samples.Add(
                    $"sid={body.ServiceId}:body={CompactBody(body)}:merged=True:removed=True:operation={op}:title={CompactTitle(title)}" +
                    $":rawExtendedBytes={HexSequenceByteLength(body.RawExtendedEventDescriptorHex)}");
            }
        }

        foreach (var body in bodiesToRemove)
        {
            if (rawEvents.Remove(body))
                removedBodyRows++;
        }

        var ineligible = bodyRows.Count - eligible;
        return new StrictTitleBodyCanonicalMergeStats(
            rawEventsBefore,
            rawEvents.Count,
            bodyRows.Count,
            eligible,
            canonicalizedFromBodyRow,
            mergedIntoExistingTitleRow,
            removedBodyRows,
            ineligible,
            ambiguousStrictMultiple,
            noStrictCandidate,
            tablePairMismatch,
            missingTitleRawShort,
            missingBodyRawExtended,
            rawExtendedMerged,
            rawExtendedAlreadyCovered,
            TrimLog(string.Join('|', samples), 7600));
    }

    private static EpgEvent CreateCanonicalTitleBodyEvent(EpgEventObservation title, EpgEvent body)
    {
        return new EpgEvent
        {
            NetworkId = body.NetworkId,
            TransportStreamId = body.TransportStreamId,
            ServiceId = body.ServiceId,
            EventId = body.EventId,
            ServiceName = body.ServiceName,
            Title = body.Title,
            Description = body.Description,
            Genre = body.Genre,
            GenreCodes = body.GenreCodes,
            TableId = title.TableId,
            SectionNumber = title.SectionNumber,
            VersionNumber = title.VersionNumber,
            RawDescriptorLoopHex = MergeRawHex(title.RawDescriptorLoopHex, body.RawDescriptorLoopHex),
            RawShortEventDescriptorHex = title.RawShortEventDescriptorHex,
            RawExtendedEventDescriptorHex = body.RawExtendedEventDescriptorHex,
            RawContentDescriptorHex = MergeRawHex(title.RawContentDescriptorHex, body.RawContentDescriptorHex),
            DurationSeconds = body.DurationSeconds,
            Start = body.Start,
            End = body.End,
            UpdatedAt = body.UpdatedAt
        };
    }

    private static string MergeRawHex(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left)) return NormalizeRawHexSequence(right);
        if (string.IsNullOrWhiteSpace(right)) return NormalizeRawHexSequence(left);

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddRawHexTokens(tokens, seen, left);
        AddRawHexTokens(tokens, seen, right);
        return string.Join(";", tokens);
    }

    private static string NormalizeRawHexSequence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddRawHexTokens(tokens, seen, raw);
        return string.Join(";", tokens);
    }

    private static void AddRawHexTokens(List<string> tokens, HashSet<string> seen, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var token in raw.Split(new[] { ';', ',', '|', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = NormalizeRawHexToken(token);
            if (normalized.Length == 0) continue;
            if (seen.Add(normalized)) tokens.Add(normalized);
        }
    }

    private static string NormalizeRawHexToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var hex = new string(raw.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length < 2) return string.Empty;
        return (hex.Length & 1) == 1 ? hex[..^1] : hex;
    }

    private void LogStrictTitleBodyCanonicalMergeContract(
        TsGroup group,
        string purpose,
        IReadOnlyList<ushort> targetServiceIds,
        StrictTitleBodyCanonicalMergeStats stats)
    {
        var result = stats.RawExtendedMerged > 0 || stats.RawExtendedAlreadyCovered > 0
            ? "CANONICAL_MERGED"
            : stats.RemovedBodyRows > 0
                ? "STRICT_IDENTITY_GUARD_APPLIED"
                : "NO_ELIGIBLE";
        var dominantCause = stats.RawExtendedMerged > 0 || stats.RawExtendedAlreadyCovered > 0
            ? "STRICT_TITLE_BODY_CANONICAL_MERGED"
            : stats.RemovedBodyRows > 0
                ? "RESIDUAL_BODY_ONLY_DROPPED_BEFORE_DB_NO_STRICT_TITLE_IDENTITY"
            : stats.AmbiguousStrictMultiple > 0
                ? "AMBIGUOUS_STRICT_MULTIPLE_SKIPPED"
                : stats.TablePairMismatch > 0
                    ? "TABLE_PAIR_MISMATCH_SKIPPED"
                    : "NO_STRICT_CANDIDATE_SKIPPED";
        Log("EIT_STRICT_TITLE_BODY_CANONICAL_MERGE_CONTRACT", $"TS{group.TsId}",
            $"result={result} purpose={purpose} group={group.Group} tsid={group.TsId} targetSids=[{string.Join(",", targetServiceIds)}] " +
            $"rawEventsBefore={stats.RawEventsBefore} rawEventsAfter={stats.RawEventsAfter} bodyOnlyCandidates={stats.BodyOnlyCandidates} eligible={stats.Eligible} ineligible={stats.Ineligible} " +
            $"canonicalizedFromBodyRow={stats.CanonicalizedFromBodyRow} mergedIntoExistingTitleRow={stats.MergedIntoExistingTitleRow} removedBodyRows={stats.RemovedBodyRows} " +
            $"ambiguousStrictMultiple={stats.AmbiguousStrictMultiple} noStrictCandidate={stats.NoStrictCandidate} tablePairMismatch={stats.TablePairMismatch} missingTitleRawShort={stats.MissingTitleRawShort} missingBodyRawExtended={stats.MissingBodyRawExtended} " +
            $"rawExtendedMerged={stats.RawExtendedMerged} rawExtendedAlreadyCovered={stats.RawExtendedAlreadyCovered} residualStrictCandidate=0 dominantCause={dominantCause} " +
            $"policy=fail_closed_parser_event_identity_nid_tsid_sid_eid_start_duration action=pre_db_strict_identity_guard dbPostFix=none renderMutation=none titleSynthesis=none bodyToTitlePromotion=none existingDbTitleBurnIn=none sample={SafeLogValue(stats.Sample)} " +
            $"rule=release_contract");
    }



    private void LogDbInsertPrecheck(TsGroup group, string purpose, IReadOnlyList<ushort> targetServiceIds, IReadOnlyList<EpgEvent> rawEvents)
    {
        var targetSidSet = targetServiceIds.ToHashSet();
        var targetSidMismatch = rawEvents.Count(e => targetSidSet.Count > 0 && !targetSidSet.Contains(e.ServiceId));
        var rawShortEmpty = rawEvents.Count(e => string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex));
        var rawExtendedOnly = rawEvents.Count(e => string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex) && !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex));
        var loopHasShortButRawShortEmpty = rawEvents.Count(e => DescriptorLoopHasTag(e.RawDescriptorLoopHex, 0x4D) && string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex));
        var loopHasExtendedButRawExtendedEmpty = rawEvents.Count(e => DescriptorLoopHasTag(e.RawDescriptorLoopHex, 0x4E) && string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex));
        var descriptorLoopInvalid = rawEvents.Count(e => DescriptorTagStatus(e.RawDescriptorLoopHex) != "OK");
        var tableBreakdown = string.Join(",", rawEvents
            .GroupBy(e => e.TableId)
            .OrderBy(g => g.Key)
            .Select(g => $"0x{g.Key:X2}:{g.Count()}/{g.Count(e => string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex) && !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex))}"));

        var precheckResult = targetSidMismatch == 0 && loopHasShortButRawShortEmpty == 0 && loopHasExtendedButRawExtendedEmpty == 0 && descriptorLoopInvalid == 0
            ? "OK"
            : "WARN";

        Log("DB_INSERT_PRECHECK", $"TS{group.TsId}",
            $"result={precheckResult} purpose={purpose} group={group.Group} rawEvents={rawEvents.Count} targetSids=[{string.Join(",", targetServiceIds)}] " +
            $"targetSidMismatch={targetSidMismatch} rawShortEmpty={rawShortEmpty} extendedWithoutShort={rawExtendedOnly} " +
            $"loopHas0x4DButRawShortEmpty={loopHasShortButRawShortEmpty} loopHas0x4EButRawExtendedEmpty={loopHasExtendedButRawExtendedEmpty} descriptorLoopInvalid={descriptorLoopInvalid} " +
            $"tableBreakdown=events/extendedWithoutShort[{tableBreakdown}] storagePolicy=raw_only_before_db decodeAfterDb=arib_bridge_to_cellText_only action=audit_only dbMutation=none rule=release_contract");

        foreach (var e in rawEvents
            .Where(e =>
                targetSidSet.Count > 0 && !targetSidSet.Contains(e.ServiceId) ||
                string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex) && !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex) ||
                DescriptorLoopHasTag(e.RawDescriptorLoopHex, 0x4D) && string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex) ||
                DescriptorTagStatus(e.RawDescriptorLoopHex) != "OK")
            .OrderBy(e => e.ServiceId)
            .ThenBy(e => e.Start)
            .ThenBy(e => e.EventId)
            .Take(40))
        {
            var tags = DescriptorTagSummary(e.RawDescriptorLoopHex);
            var status = DescriptorTagStatus(e.RawDescriptorLoopHex);
            var hasShort = DescriptorLoopHasTag(e.RawDescriptorLoopHex, 0x4D);
            var hasExtended = DescriptorLoopHasTag(e.RawDescriptorLoopHex, 0x4E);
            var anomaly = targetSidSet.Count > 0 && !targetSidSet.Contains(e.ServiceId)
                ? "target_sid_mismatch"
                : status != "OK"
                    ? "descriptor_loop_invalid"
                    : hasShort && string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex)
                        ? "has_0x4D_but_raw_short_empty"
                        : !hasShort && hasExtended
                            ? "extended_without_short"
                            : "raw_short_empty";

            Log("DB_INSERT_PRECHECK_DETAIL", $"TS{group.TsId}",
                $"purpose={purpose} group={group.Group} nid={e.NetworkId} tsid={e.TransportStreamId} sid={e.ServiceId} eid={e.EventId} " +
                $"tableId=0x{e.TableId:X2} section={e.SectionNumber} start={e.Start:yyyy-MM-ddTHH:mm:ss} durationSec={e.DurationSeconds} " +
                $"descriptorLoopBytes={HexSequenceByteLength(e.RawDescriptorLoopHex)} descriptorSequences={HexSequenceCount(e.RawDescriptorLoopHex)} rawShortBytes={HexSequenceByteLength(e.RawShortEventDescriptorHex)} rawExtendedBytes={HexSequenceByteLength(e.RawExtendedEventDescriptorHex)} " +
                $"has0x4D={hasShort} has0x4E={hasExtended} descriptorStatus={status} descriptorTags={SafeLogValue(tags)} rawLoopHexHead={SafeLogValue(HexHead(e.RawDescriptorLoopHex, 96))} " +
                $"anomaly={anomaly} storagePolicy=raw_only_before_db rule=release_contract");

            if (status != "OK")
            {
                Log("DB_INSERT_PRECHECK_BOUNDARY", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} nid={e.NetworkId} tsid={e.TransportStreamId} sid={e.ServiceId} eid={e.EventId} " +
                    $"tableId=0x{e.TableId:X2} section={e.SectionNumber} descriptorStatus={status} descriptorLoopBytes={HexSequenceByteLength(e.RawDescriptorLoopHex)} descriptorSequences={HexSequenceCount(e.RawDescriptorLoopHex)} " +
                    $"boundaryTrace={SafeLogValue(DescriptorBoundaryTrace(e.RawDescriptorLoopHex, 28))} rawLoopHexTail={SafeLogValue(HexTail(e.RawDescriptorLoopHex, 96))} " +
                    $"storagePolicy=raw_only_before_db rule=release_contract");
            }
        }
    }













    private static string DescriptorTagSummary(string? rawHex)
    {
        var counts = new SortedDictionary<byte, int>();
        foreach (var bytes in DecodeHexSequences(rawHex))
        {
            var pos = 0;
            while (pos + 2 <= bytes.Length)
            {
                var tag = bytes[pos];
                var len = bytes[pos + 1];
                var next = pos + 2 + len;
                if (next > bytes.Length) break;
                counts[tag] = counts.TryGetValue(tag, out var cur) ? cur + 1 : 1;
                pos = next;
            }
        }
        return counts.Count == 0 ? "-" : string.Join(",", counts.Select(kv => $"0x{kv.Key:X2}:{kv.Value}"));
    }

    private static string DescriptorTagStatus(string? rawHex)
    {
        var sawAny = false;
        foreach (var bytes in DecodeHexSequences(rawHex))
        {
            sawAny = true;
            var pos = 0;
            while (pos + 2 <= bytes.Length)
            {
                var len = bytes[pos + 1];
                var next = pos + 2 + len;
                if (next > bytes.Length) return "INVALID_LENGTH";
                pos = next;
            }
            if (pos != bytes.Length) return "TRAILING_BYTE";
        }
        return sawAny ? "OK" : "EMPTY";
    }

    private static bool DescriptorLoopHasTag(string? rawHex, byte expectedTag)
    {
        foreach (var bytes in DecodeHexSequences(rawHex))
        {
            var pos = 0;
            while (pos + 2 <= bytes.Length)
            {
                var tag = bytes[pos];
                var len = bytes[pos + 1];
                var next = pos + 2 + len;
                if (next > bytes.Length) break;
                if (tag == expectedTag) return true;
                pos = next;
            }
        }
        return false;
    }

    private static int HexSequenceByteLength(string? rawHex)
        => DecodeHexSequences(rawHex).Sum(bytes => bytes.Length);

    private static int HexSequenceCount(string? rawHex)
        => DecodeHexSequences(rawHex).Count();

    private static string DescriptorBoundaryTrace(string? rawHex, int maxSteps)
    {
        var traces = new List<string>();
        var seq = 0;
        foreach (var bytes in DecodeHexSequences(rawHex))
        {
            var pos = 0;
            var steps = 0;
            while (pos + 2 <= bytes.Length && steps < maxSteps)
            {
                var tag = bytes[pos];
                var len = bytes[pos + 1];
                var next = pos + 2 + len;
                if (next > bytes.Length)
                {
                    traces.Add($"s{seq}@{pos}:tag=0x{tag:X2}/len={len}/next={next}/total={bytes.Length}/invalid=length_overrun");
                    return string.Join(">", traces);
                }

                traces.Add($"s{seq}@{pos}:tag=0x{tag:X2}/len={len}/next={next}/remain={bytes.Length - next}");
                pos = next;
                steps++;
            }

            if (pos != bytes.Length)
            {
                traces.Add($"s{seq}@{pos}:total={bytes.Length}/invalid=trailing_or_truncated/steps={steps}");
                return string.Join(">", traces);
            }

            seq++;
        }

        return traces.Count == 0 ? "-" : string.Join(">", traces);
    }

    private static string HexHead(string? rawHex, int maxChars)
    {
        var hex = NormalizeHexForLog(rawHex);
        return hex.Length <= maxChars ? hex : hex[..maxChars] + "...";
    }

    private static string HexTail(string? rawHex, int maxChars)
    {
        var hex = NormalizeHexForLog(rawHex);
        return hex.Length <= maxChars ? hex : "..." + hex[^maxChars..];
    }

    private static IEnumerable<byte[]> DecodeHexSequences(string? rawHex)
    {
        if (string.IsNullOrWhiteSpace(rawHex)) yield break;
        foreach (var token in rawHex.Split(new[] { ';', ',', '|', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var hex = new string(token.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            if (hex.Length < 2) continue;
            if ((hex.Length & 1) == 1) hex = hex[..^1];
            var bytes = new byte[hex.Length / 2];
            var ok = true;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                {
                    ok = false;
                    break;
                }
            }
            if (ok) yield return bytes;
        }
    }

    private static string NormalizeHexForLog(string? rawHex)
    {
        if (string.IsNullOrWhiteSpace(rawHex)) return string.Empty;
        var tokens = rawHex.Split(new[] { ';', ',', '|', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant())
            .Where(t => t.Length > 0)
            .ToArray();
        return string.Join(";", tokens);
    }

    internal List<TsGroup> BuildGroups(IReadOnlyList<ChannelTarget> targets, bool emitDiagnostics = true)
    {
        // release_contract: 通常EPGは「局単位」ではなく .ch2 由来の同一TS束で回す。
        // group+nid+tsid に加えて、実際に BonDriver/TVTest へ渡す chspace/chi も同一であることを
        // TS束の条件に含める。NID/TSIDだけが一致していても、ch2/ChSet上の選局点が違うものを
        // 1つに混ぜない。逆に同じ選局点に複数サービスがある場合は targetSids へ全件載せる。
        var groups = targets
            .GroupBy(t => new
            {
                Group = NormalizeGroup(t.Group),
                t.OriginalNetworkId,
                t.TransportStreamId,
                t.ResolvedSpace,
                t.ResolvedChannelIndex
            })
            .Select(g =>
            {
                var targetList = g
                    .GroupBy(t => t.ServiceId)
                    .Select(x => x.OrderBy(t => t.Ch2LineNumber).First())
                    .OrderBy(t => t.Ch2LineNumber)
                    .ToList();
                var ch2ActiveSameTsServices = targetList.Count == 0 ? 0 : targetList.Max(t => t.SameTransportServiceCount);
                var bundleStatus = ch2ActiveSameTsServices > targetList.Count
                    ? "WARN_ACTIVE_CH2_SERVICE_SUBSET"
                    : "OK_ACTIVE_CH2_SERVICE_COVERED";
                var targetSids = string.Join(",", targetList.Select(t => t.ServiceId).OrderBy(x => x));
                var ch2Lines = string.Join(",", targetList.Select(t => t.Ch2LineNumber).OrderBy(x => x));
                if (emitDiagnostics)
                {
                    Log("EPG_CH2_TS_SCOPE", $"TS{g.Key.TransportStreamId}",
                        $"result={bundleStatus} group={g.Key.Group} nid={g.Key.OriginalNetworkId} tsid={g.Key.TransportStreamId} chspace={g.Key.ResolvedSpace} chi={g.Key.ResolvedChannelIndex} " +
                        $"ch2ActiveSameTsServices={ch2ActiveSameTsServices} epgTargetSidCount={targetList.Count} targetSids=[{targetSids}] ch2Lines=[{ch2Lines}] " +
                        $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC note=epg_uses_ch2_bundle_before_tvairepgrec_job rule=release_contract");
                }
                return new TsGroup(
                    Key:               $"{g.Key.Group}:{g.Key.OriginalNetworkId}:{g.Key.TransportStreamId}:{g.Key.ResolvedSpace}:{g.Key.ResolvedChannelIndex}",
                    Group:             g.Key.Group,
                    TsId:              g.Key.TransportStreamId,
                    BonDriverFileName: ResolveBonDriver(g.Key.Group),
                    Targets:           targetList);
            })
            .OrderBy(g => g.Group == "GR" ? 0 : 1)
            .ThenBy(g => g.TsId)
            .ThenBy(g => g.Targets.FirstOrDefault()?.ResolvedSpace ?? 0)
            .ThenBy(g => g.Targets.FirstOrDefault()?.ResolvedChannelIndex ?? 0)
            .ToList();

        if (emitDiagnostics)
        {
            Log("EPG_CH2_SCOPE_SUMMARY", "EPG",
                $"groups={groups.Count} services={groups.Sum(g => g.Targets.Count)} warnings={groups.Count(g => (g.Targets.Count == 0 ? 0 : g.Targets.Max(t => t.SameTransportServiceCount)) > g.Targets.Count)} " +
                $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
        }
        return groups;
    }

    private string ResolveBonDriver(string group)
    {
        // release_contract: 環境固有BonDriver名の補完を禁止。
        // 設定値は候補であり、実行時はTunerPoolの論理スロット確保結果を正とする。
        // ここではch2由来のTS束へ便宜的な代表名を持たせるだけで、未解決なら空のまま残す。
        var profile = tunerProfiles
            .Where(p => SupportsGroup(p.Group, group))
            .Where(p => !string.Equals(IniSettingsService.NormalizeTunerRole(p.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.BonDriverFileName));
        return profile?.BonDriverFileName ?? string.Empty;
    }

    private string BuildTsFilePath(TsGroup group)
    {
        var dir = string.IsNullOrWhiteSpace(settings.TsRecordDirectory)
            ? Path.Combine(database.DataDirectory, "ts-rec")
            : settings.TsRecordDirectory;
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        return Path.Combine(dir, $"epg_{group.Group}_{group.TsId}_{ts}.ts");
    }

    private static string NormalizeGroup(string group)
    {
        var raw = (group ?? string.Empty).Trim();
        var g = raw.ToUpperInvariant();
        return g switch
        {
            "GR" or "地上波" or "地デジ" => "GR",
            "BS" or "CS" or "BSCS" or "BS/CS" => "BSCS",
            "HYBRID" or "GRBSCS" or "GR/BSCS" or "GR/BS/CS" or "地/BS/CS" or "地デジ/BS/CS" or "地上波/BS/CS" => "HYBRID",
            _ => raw
        };
    }



    private static bool SupportsGroup(string tunerGroup, string requestGroup)
    {
        var tg = NormalizeGroup(tunerGroup);
        var rg = NormalizeGroup(requestGroup);
        return tg == "HYBRID" ? rg is "GR" or "BSCS" or "HYBRID" : tg == rg;
    }

    // ─── プロセス終了待機 ─────────────────────────────────────────

    /// <summary>
    /// TvAIrEpgRec/対象プロセスの終了を待つ。
    /// 終了・タイムアウト・外部キャンセル・既に存在しない状態を分離して返す。
    /// </summary>
    private static async Task<EpgProcessExitWaitResult> WaitForExitOrExternalFailureAsync(
        int pid,
        TimeSpan timeout,
        ActiveEpgWorkerTask workerState,
        long expectedAttemptGeneration,
        CancellationToken ct)
    {
        var waitTask = WaitForOwnedWorkerExitAsync(pid, timeout, workerState, expectedAttemptGeneration, ct);
        var externalFailureTask = workerState.WaitForExternalFailureAsync(expectedAttemptGeneration, ct);
        var completed = await Task.WhenAny(waitTask, externalFailureTask).ConfigureAwait(false);
        if (completed == externalFailureTask)
        {
            await externalFailureTask.ConfigureAwait(false);
            return EpgProcessExitWaitResult.ProcessMissing;
        }

        return await waitTask.ConfigureAwait(false);
    }

    private static async Task<EpgProcessExitWaitResult> WaitForOwnedWorkerExitAsync(
        int pid,
        TimeSpan timeout,
        ActiveEpgWorkerTask workerState,
        long expectedAttemptGeneration,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return EpgProcessExitWaitResult.Cancelled;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return EpgProcessExitWaitResult.Cancelled;

            var snapshot = workerState.Snapshot();
            if (snapshot.AttemptGeneration != expectedAttemptGeneration)
                return EpgProcessExitWaitResult.Exited;
            var identity = GetOwnedWorkerProcessIdentity(snapshot);
            if (identity == EpgWorkerProcessIdentityState.Missing)
                return EpgProcessExitWaitResult.NotFound;
            if (identity == EpgWorkerProcessIdentityState.ReusedPid)
                return EpgProcessExitWaitResult.Exited;

            var remaining = deadline - DateTime.UtcNow;
            var delay = remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500);
            if (delay <= TimeSpan.Zero) break;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return EpgProcessExitWaitResult.Cancelled; }
        }

        return EpgProcessExitWaitResult.Timeout;
    }

    /// <summary>
    /// EPG worker所有Task用。取得した同一Processハンドル上で所有権を再照合してからKillする。
    /// PIDを再取得する別処理へ渡さないため、照合後のPID再利用競合を避ける。
    /// </summary>
    private static string GetOwnedWorkerKillAction(EpgOwnedWorkerKillResult result)
        => result switch
        {
            EpgOwnedWorkerKillResult.Killed => "kill_owned_process",
            EpgOwnedWorkerKillResult.AlreadyExited => "already_exited_no_kill",
            EpgOwnedWorkerKillResult.IdentityMismatch => "kill_skipped_identity_changed",
            EpgOwnedWorkerKillResult.IdentityUnavailable => "kill_skipped_identity_unavailable",
            _ => "kill_failed_keep_tracked"
        };

    private static EpgOwnedWorkerKillResult TryKillOwnedWorkerProcess(
        ActiveEpgWorkerSnapshot snapshot,
        bool deviceGateAlreadyHeld = false)
    {
        if (snapshot.ProcessId <= 0) return EpgOwnedWorkerKillResult.AlreadyExited;

        // deviceGateAlreadyHeld=true は、呼出元startup ownerが同じGR/BSCS device gateの
        // using scope内にいることを明示する契約。awaitを跨ぐAsyncLocalのre-entry推測には依存せず、
        // 同じgateを自己再取得しない。gate外の呼出元は従来どおり物理device gateを取得してからkillする。
        if (deviceGateAlreadyHeld)
            return TryKillOwnedWorkerProcessCore(snapshot);

        try
        {
            using var tunerDeviceAccess = TunerDeviceAccessGate.Enter(
                $"EPG_KILL_OWNED PID={snapshot.ProcessId}",
                snapshot.Group);
            return TryKillOwnedWorkerProcessCore(snapshot);
        }
        catch
        {
            return EpgOwnedWorkerKillResult.Failed;
        }
    }

    private static EpgOwnedWorkerKillResult TryKillOwnedWorkerProcessCore(ActiveEpgWorkerSnapshot snapshot)
    {
        try
        {
            using var process = Process.GetProcessById(snapshot.ProcessId);
            if (process.HasExited) return EpgOwnedWorkerKillResult.AlreadyExited;

            if (snapshot.ProcessStartedAtUtc.HasValue)
            {
                DateTime actualStartedAtUtc;
                try
                {
                    actualStartedAtUtc = process.StartTime.ToUniversalTime();
                }
                catch
                {
                    return EpgOwnedWorkerKillResult.IdentityUnavailable;
                }

                if (Math.Abs((actualStartedAtUtc - snapshot.ProcessStartedAtUtc.Value).TotalMilliseconds) > 1000)
                    return EpgOwnedWorkerKillResult.IdentityMismatch;
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.ProcessExecutablePath))
            {
                string actualPath;
                try
                {
                    actualPath = process.MainModule?.FileName ?? string.Empty;
                }
                catch
                {
                    return EpgOwnedWorkerKillResult.IdentityUnavailable;
                }

                if (string.IsNullOrWhiteSpace(actualPath))
                    return EpgOwnedWorkerKillResult.IdentityUnavailable;

                if (!string.Equals(
                        Path.GetFullPath(actualPath),
                        Path.GetFullPath(snapshot.ProcessExecutablePath),
                        StringComparison.OrdinalIgnoreCase))
                    return EpgOwnedWorkerKillResult.IdentityMismatch;
            }
            else
            {
                return EpgOwnedWorkerKillResult.IdentityUnavailable;
            }

            process.Kill();
            return EpgOwnedWorkerKillResult.Killed;
        }
        catch (ArgumentException)
        {
            return EpgOwnedWorkerKillResult.AlreadyExited;
        }
        catch
        {
            return EpgOwnedWorkerKillResult.Failed;
        }
    }

    private static string TrimLog(string? value, int max = 80)
    {
        var v = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return v.Length <= max ? v : v[..max] + "…";
    }

    // ─── ユーティリティ ──────────────────────────────────────────

    private void Log(string ev, string title, string msg)
        => log.Add(ev, title, msg);

    public void SetStatus(Func<EpgCaptureStatus, EpgCaptureStatus> update)
    {
        lock (statusGate) status = update(status);
    }
}

// ─── 内部型 ──────────────────────────────────────────────────────

internal enum EpgProcessExitWaitResult
{
    Exited,
    Timeout,
    Cancelled,
    NotFound,
    ProcessMissing
}


internal sealed record EpgWorkerTerminalEvidence(bool ResultExists, bool? WorkerSuccess, int? ExitCode, bool? TsReadOk, bool PreserveRuntimeArtifacts);

internal sealed record EpgWorkerLaunchResult(bool Success, int ProcessId, string Message, string? StopSignalPath, string? ResultPath, string? ProgressPath, string? JobPath = null);

internal sealed record EpgCaptureFailureState(
    string Group,
    ushort TsId,
    string Reason,
    string Detail,
    int Attempt,
    int MaxAttempts,
    DateTime CreatedAt);


internal sealed record EpgOutputLifecycleState(
    bool Observed,
    bool NonZero,
    bool Growing,
    long FirstSize,
    long LastSize,
    DateTime FirstObservedAt,
    DateTime LastObservedAt,
    string Phase);

internal enum EpgWorkerTerminalReason
{
    None,
    Completed,
    Failed,
    Cancelled,
    ProcessMissing
}

internal enum EpgOwnedWorkerKillResult
{
    Killed,
    AlreadyExited,
    IdentityMismatch,
    IdentityUnavailable,
    Failed
}

internal enum EpgWorkerProcessIdentityState
{
    Match,
    Missing,
    ReusedPid,
    IdentityUnknown
}

internal sealed record ActiveEpgWorkerSnapshot(
    string Key,
    string RunId,
    int Pass,
    string Group,
    ushort TsId,
    string ServiceName,
    DateTime StartedAt,
    long AttemptGeneration,
    int ProcessId,
    DateTime? ProcessStartedAtUtc,
    string ProcessExecutablePath,
    string WorkerName,
    string? StopSignalPath,
    EpgWorkerTerminalReason TerminalReason,
    EpgWorkerTerminalReason PendingTerminalReason,
    string ExternalFailureCycleId,
    DateTime? ProcessMissingReportedAtUtc,
    DateTime? MissingConvergenceDeadlineUtc,
    bool ProcessExitObserved,
    bool OwnerTaskCompleted,
    EpgWorkerFinalizationState FinalizationState,
    string FinalizerOwner,
    string TunerName,
    string Did,
    string BonDriverFileName,
    Guid? PoolLeaseId,
    long OccupancyGeneration,
    bool PoolLeaseCurrent)
{
    public bool IsTerminal => TerminalReason != EpgWorkerTerminalReason.None;
}

internal enum EpgWorkerFinalizationState
{
    Active,
    Finalizing,
    Finalized
}

internal sealed class ActiveEpgWorkerTask
{
    private readonly object gate = new();
    private TaskCompletionSource<EpgWorkerExternalSignal> externalFailure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long attemptGeneration;
    private readonly TaskCompletionSource<bool> ownerTaskCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int processId;
    private DateTime? processStartedAtUtc;
    private string processExecutablePath = string.Empty;
    private string workerName = string.Empty;
    private string? stopSignalPath;
    private EpgWorkerTerminalReason terminalReason;
    private EpgWorkerTerminalReason pendingTerminalReason;
    private string externalFailureCycleId = string.Empty;
    private DateTime? processMissingReportedAtUtc;
    private DateTime? missingConvergenceDeadlineUtc;
    private bool processExitObserved;
    private bool ownerTaskCompleted;
    private EpgWorkerFinalizationState finalizationState;
    private string finalizerOwner = string.Empty;
    private int exitMonitorStarted;
    private int missingConvergenceMonitorStarted;
    private TunerLease? tunerLease;
    private Action? deferredAdmissionRelease;

    public ActiveEpgWorkerTask(string key, string runId, int pass, string group, ushort tsId, string serviceName, DateTime startedAt)
    {
        Key = key;
        RunId = runId;
        Pass = pass;
        Group = group;
        TsId = tsId;
        ServiceName = serviceName;
        StartedAt = startedAt;
    }

    public string Key { get; }
    public string RunId { get; }
    public int Pass { get; }
    public string Group { get; }
    public ushort TsId { get; }
    public string ServiceName { get; }
    public DateTime StartedAt { get; }

    public bool TryAttachLease(TunerLease lease, out long generation, out string rejectionReason)
    {
        lock (gate)
        {
            generation = attemptGeneration;
            rejectionReason = string.Empty;
            if (terminalReason != EpgWorkerTerminalReason.None || finalizationState != EpgWorkerFinalizationState.Active)
            {
                rejectionReason = "logical_worker_finalizing_or_terminal";
                return false;
            }
            // A new attempt may start only after the previous attempt no longer owns a lease and,
            // if it ever owned a process, that process exit has been observed. Do not overwrite live
            // attempt resources and rely on a later monitor to repair ownership.
            if (tunerLease is not null)
            {
                rejectionReason = "previous_attempt_lease_not_converged";
                return false;
            }
            if (processId > 0 && !processExitObserved)
            {
                rejectionReason = "previous_attempt_process_not_converged";
                return false;
            }
            // EPG_WORKER_ATTEMPT_OWNERSHIP_INVARIANT:
            // retryは同一logical worker task内でも別attemptである。lease/process/external signalを
            // attempt境界で必ず切り替え、旧attempt monitorが新attempt資源へ触れないようにする。
            attemptGeneration++;
            generation = attemptGeneration;
            tunerLease = lease;
            processId = 0;
            processStartedAtUtc = null;
            processExecutablePath = string.Empty;
            workerName = string.Empty;
            stopSignalPath = null;
            externalFailure = new TaskCompletionSource<EpgWorkerExternalSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
            externalFailureCycleId = string.Empty;
            processMissingReportedAtUtc = null;
            missingConvergenceDeadlineUtc = null;
            processExitObserved = false;
            Interlocked.Exchange(ref missingConvergenceMonitorStarted, 0);
            return true;
        }
    }

    public bool AttachProcess(long expectedAttemptGeneration, int pid, string name, string? signalPath)
    {
        DateTime? startedAtUtc = null;
        var executablePath = string.Empty;
        try
        {
            using var process = Process.GetProcessById(pid);
            startedAtUtc = process.StartTime.ToUniversalTime();
            try { executablePath = process.MainModule?.FileName ?? string.Empty; } catch { }
        }
        catch { }

        lock (gate)
        {
            if (attemptGeneration != expectedAttemptGeneration
                || terminalReason != EpgWorkerTerminalReason.None
                || finalizationState != EpgWorkerFinalizationState.Active)
                return false;
            processId = pid;
            processStartedAtUtc = startedAtUtc;
            processExecutablePath = executablePath;
            workerName = name;
            stopSignalPath = signalPath;
            return true;
        }
    }

    public bool MarkProcessExitObserved(long expectedAttemptGeneration)
    {
        lock (gate)
        {
            if (attemptGeneration != expectedAttemptGeneration)
                return false;
            processExitObserved = true;
            return true;
        }
    }

    public bool ReportProcessMissing(string cycleId, long expectedAttemptGeneration)
    {
        lock (gate)
        {
            if (attemptGeneration != expectedAttemptGeneration || terminalReason != EpgWorkerTerminalReason.None) return false;
            externalFailureCycleId = cycleId ?? string.Empty;
            if (!processMissingReportedAtUtc.HasValue)
            {
                processMissingReportedAtUtc = DateTime.UtcNow;
                // Owner task is signalled immediately. The deadline only bounds how long an orphaned
                // attempt may keep its own lease after its process disappeared. The monitor never
                // finalizes or removes the logical worker owner; retry/owner completion retains that authority.
                missingConvergenceDeadlineUtc = processMissingReportedAtUtc.Value.AddSeconds(5);
            }
        }
        return externalFailure.TrySetResult(new EpgWorkerExternalSignal(EpgWorkerExternalSignalKind.ProcessMissing, cycleId ?? string.Empty));
    }

    public Task<EpgWorkerExternalSignal> WaitForExternalFailureAsync(long expectedAttemptGeneration, CancellationToken cancellationToken)
    {
        Task<EpgWorkerExternalSignal> task;
        lock (gate)
        {
            if (attemptGeneration != expectedAttemptGeneration)
                return Task.FromResult(new EpgWorkerExternalSignal(EpgWorkerExternalSignalKind.ProcessMissing, "stale_attempt"));
            task = externalFailure.Task;
        }
        return task.WaitAsync(cancellationToken);
    }

    public bool TryFinalize(EpgWorkerTerminalReason reason)
    {
        lock (gate)
        {
            if (terminalReason != EpgWorkerTerminalReason.None) return false;
            if (pendingTerminalReason == EpgWorkerTerminalReason.None || reason != EpgWorkerTerminalReason.Completed)
                pendingTerminalReason = reason;
            return true;
        }
    }

    public void MarkOwnerTaskCompleted()
    {
        lock (gate) ownerTaskCompleted = true;
        ownerTaskCompletion.TrySetResult(true);
    }

    public async Task<bool> WaitForOwnerTaskCompletionAsync(TimeSpan upperBound, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (ownerTaskCompleted) return true;
        }

        try
        {
            await ownerTaskCompletion.Task.WaitAsync(upperBound, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async Task WaitForOwnerTaskOrDeadlineAsync(DateTime deadlineUtc, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (ownerTaskCompleted) return;
        }

        var remaining = deadlineUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return;

        var deadlineTask = Task.Delay(remaining, cancellationToken);
        await Task.WhenAny(ownerTaskCompletion.Task, deadlineTask).ConfigureAwait(false);
    }

    public bool TryBeginFinalization(string owner, long expectedAttemptGeneration)
    {
        lock (gate)
        {
            if (attemptGeneration != expectedAttemptGeneration)
                return false;
            if (finalizationState != EpgWorkerFinalizationState.Active) return false;
            finalizationState = EpgWorkerFinalizationState.Finalizing;
            finalizerOwner = owner ?? string.Empty;
            return true;
        }
    }

    public void MarkFinalized()
    {
        lock (gate) finalizationState = EpgWorkerFinalizationState.Finalized;
    }

    public void ResetFinalization()
    {
        lock (gate)
        {
            if (finalizationState != EpgWorkerFinalizationState.Finalizing) return;
            finalizationState = EpgWorkerFinalizationState.Active;
            finalizerOwner = string.Empty;
        }
    }

    public bool FinalizeRequestedOr(EpgWorkerTerminalReason fallback)
    {
        lock (gate)
        {
            if (terminalReason != EpgWorkerTerminalReason.None) return false;
            terminalReason = pendingTerminalReason != EpgWorkerTerminalReason.None
                ? pendingTerminalReason
                : fallback;
            return true;
        }
    }

    public void DeferAdmissionRelease(Action? releaseAdmission)
    {
        if (releaseAdmission is null) return;
        lock (gate)
            deferredAdmissionRelease ??= releaseAdmission;
    }

    public bool ReleaseLeaseForAttempt(long expectedAttemptGeneration)
    {
        TunerLease? lease;
        lock (gate)
        {
            if (attemptGeneration != expectedAttemptGeneration)
                return false;
            lease = tunerLease;
            if (lease is null)
                return true;
        }

        try
        {
            lease.Dispose();
        }
        catch
        {
            return false;
        }

        if (lease.IsIdentityCurrent)
            return false;

        Action? releaseAdmission = null;
        lock (gate)
        {
            if (attemptGeneration != expectedAttemptGeneration)
                return false;
            if (ReferenceEquals(tunerLease, lease))
                tunerLease = null;
            if (tunerLease is not null)
                return false;
            releaseAdmission = deferredAdmissionRelease;
            deferredAdmissionRelease = null;
        }

        try { releaseAdmission?.Invoke(); } catch { }
        return true;
    }

    public bool IsCurrentAttempt(long expectedAttemptGeneration)
    {
        lock (gate) return attemptGeneration == expectedAttemptGeneration;
    }

    public bool TrySnapshotAttempt(long expectedAttemptGeneration, out ActiveEpgWorkerSnapshot snapshot)
    {
        lock (gate)
        {
            snapshot = CreateSnapshotUnsafe();
            return attemptGeneration == expectedAttemptGeneration;
        }
    }

    public bool TryStartExitMonitor() => Interlocked.CompareExchange(ref exitMonitorStarted, 1, 0) == 0;
    public void ResetExitMonitor() => Interlocked.Exchange(ref exitMonitorStarted, 0);
    public bool TryStartMissingConvergenceMonitor() => Interlocked.CompareExchange(ref missingConvergenceMonitorStarted, 1, 0) == 0;
    public void ResetMissingConvergenceMonitor(long expectedAttemptGeneration)
    {
        if (IsCurrentAttempt(expectedAttemptGeneration))
            Interlocked.Exchange(ref missingConvergenceMonitorStarted, 0);
    }

    public ActiveEpgWorkerSnapshot Snapshot()
    {
        lock (gate) return CreateSnapshotUnsafe();
    }

    private ActiveEpgWorkerSnapshot CreateSnapshotUnsafe()
        => new(
            Key, RunId, Pass, Group, TsId, ServiceName, StartedAt,
            attemptGeneration, processId, processStartedAtUtc, processExecutablePath, workerName, stopSignalPath, terminalReason, pendingTerminalReason,
            externalFailureCycleId, processMissingReportedAtUtc, missingConvergenceDeadlineUtc,
            processExitObserved, ownerTaskCompleted, finalizationState, finalizerOwner,
            tunerLease?.Name ?? string.Empty, tunerLease?.Did ?? string.Empty, tunerLease?.BonDriverFileName ?? string.Empty,
            tunerLease?.PoolLeaseId, tunerLease?.OccupancyGeneration ?? 0, tunerLease?.IsCurrent == true);
}


internal enum EpgWorkerExternalSignalKind
{
    ProcessMissing
}

internal sealed record EpgWorkerExternalSignal(EpgWorkerExternalSignalKind Kind, string Detail);

internal sealed class EpgWorkerProcessMissingException : Exception
{
    public EpgWorkerProcessMissingException(string cycleId)
        : base("EPG worker process disappeared during an active run.")
    {
        CycleId = cycleId;
    }

    public string CycleId { get; }
}

internal sealed record TsGroup(
    string Key,
    string Group,
    ushort TsId,
    string BonDriverFileName,
    IReadOnlyList<ChannelTarget> Targets);

// ─── 公開型 ──────────────────────────────────────────────────────

internal sealed record PreRecordProbeResult(bool TargetFound, IReadOnlyList<EpgEvent> Events, string EventSummary);

public sealed record EpgCaptureResult(
    bool Success,
    int CompletedGroups,
    int TotalGroups,
    int ImportedEvents,
    string RunResult,
    int MissingGroups,
    string Message,
    string Detail)
{
    public string MissingScopes { get; init; } = string.Empty;
    public string CompletedScopes { get; init; } = string.Empty;
    public IReadOnlyList<EpgEvent> PreRecordEvents { get; init; } = Array.Empty<EpgEvent>();

    public static EpgCaptureResult Failed(string message)
        => new(false, 0, 0, 0, "FAILED", 0, message, "result=FAILED");
}

public sealed record EpgCaptureStatus
{
    public string Phase { get; init; } = "idle";
    public int TotalGroups { get; init; }
    public int CompletedGroups { get; init; }
    public int RunningGroups { get; init; }
    public string RunningGroupNames { get; init; } = "";
    public int ActiveWorkerElapsedSeconds { get; init; }
    public int ActiveWorkerPlannedSeconds { get; init; }
    public int EstimatedProgressPercent { get; init; }
    public DateTime? RunStartedAt { get; init; }
    public DateTime? LastRunAt { get; init; }
    public string LastRunMessage { get; init; } = "";
    public string RunDepth { get; init; } = "";
    public string TargetScope { get; init; } = "All";
    public bool UiVisible { get; init; } = true;
    public string RunPurpose { get; init; } = "normal_epg_capture";
    public string RunSource { get; init; } = "";
    public string UiMode { get; init; } = "Visible";
    public string CancelRoute { get; init; } = "VisibleWidget";
}
