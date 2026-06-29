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
///   4. TvAIrEpgRec終了後に TS ファイルを EPG でパース
///   5. 対象TS/SIDを今回TSから読めた結果だけでDBへUPSERT
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
    private readonly TvTestActivityKeeper tvTestActivity;
    private readonly ServiceLogoStore serviceLogoStore;
    private readonly EpgLogoExtractor logoExtractor;
    private readonly BroadcastClockService broadcastClock;

    // キャプチャ状態（UIへの進捗通知用）
    private EpgCaptureStatus status = new();
    private readonly object statusGate = new();

    // ─── LIVE視聴中TVTestが使う(BonDriver,DID)集合 ───
    // RunAsync 開始時に一度だけ検出して保持。同一EPG取得セッション中は不変として扱う。
    // null = 未検出/機能無効。空集合 = 検出済みだがLIVE視聴なし。
    private IReadOnlySet<(string BonDriverFileName, string Did)>? liveTvTestKeys;

    // EPG取得開始はGR/BSCS混在バーストを避けるためグローバルにシリアル化する。
    private readonly SemaphoreSlim epgLaunchStartGate = new(1, 1);

    // EPG取得終了もシリアル化し、TVTest終了確認・チューナー解放・
    // スケジューラ再評価が BonDriver へ一斉にヒットしないようにする。
    private readonly SemaphoreSlim epgEndingGate = new(1, 1);

    // EPGラン全体用の代表ActivityKeeper TVTestは起動しない。
    // 実際に取得中の局を示す個別TVTestだけを診断対象とし、
    // TVTestプロセス監視利用者向けに「取得中局のTVTestアイコンが見えているか」をログ化する。
    // key=pid, value=対象TS/局情報。制御には使わず診断ログ専用。
    private readonly ConcurrentDictionary<int, ActiveEpgWorkerProcess> activeEpgWorkerProcesses = new();
    // release_contract: 手動/定時EPGで録画優先ロックにより起動できなかったTSをrun単位で集計する。
    // 0/N全抑止をPARTIALではなくBLOCKEDへ分類し、ユーザーへ「開始できなかった」と伝える。
    private readonly ConcurrentDictionary<string, string> currentRunBlockedGroups = new(StringComparer.OrdinalIgnoreCase);
    // EPG取得失敗はrun単位で保持する。
    private readonly ConcurrentDictionary<string, EpgCaptureFailureState> currentRunCaptureFailures = new(StringComparer.OrdinalIgnoreCase);
    // 前TVTest終了後、次TVTest起動前のチューナークールダウン待機を
    // TVTestアイコン空白の想定内要因として分類する。実プロセスは存在しないため
    // アイコン表示対象にはできないが、WARN誤判定を避ける診断情報として扱う。
    private readonly ConcurrentDictionary<string, EpgCooldownWait> epgCooldownWaits = new();
    private DateTime lastEpgWorkerCoverageLogUtc = DateTime.MinValue;
    private DateTime lastEpgWorkerGapLogUtc = DateTime.MinValue;
    private volatile bool epgRunAcceptingNewWorkers = false;
    private long epgRunSequence = 0;
    private string currentEpgRunId = "none";
    // release_contract: EPG worker task state is tracked by actual task registration, not by increment/decrement counters.
    // Cancellation can finish the run before worker finally blocks drain; a global counter can underflow in that case.
    private readonly ConcurrentDictionary<string, ActiveEpgWorkerTask> activeEpgWorkerTasks = new(StringComparer.Ordinal);
    // 通常EPG取得中だけ、TS間クールダウンでSleepGuard監視対象TVTestがゼロになる穴を塞ぐ。
    // 録画前EPG確認では使わず、BonDriver実機取得には触らないTvAIr管理TVTestマーカーとして扱う。
    // 起動/停止/診断を専用ヘルパーへ分離し、キャンセル/例外時も監視用TVTestと監視タスクを確実に片付ける。
    private int epgSleepGuardBridgePid = 0;

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
        TvTestActivityKeeper tvTestActivity,
        ServiceLogoStore serviceLogoStore,
        EpgLogoExtractor logoExtractor,
        BroadcastClockService broadcastClock)
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
        this.tvTestActivity = tvTestActivity;
        this.serviceLogoStore = serviceLogoStore;
        this.logoExtractor = logoExtractor;
        this.broadcastClock = broadcastClock;
    }

    public EpgCaptureStatus GetStatus()
    {
        EpgCaptureStatus snapshot;
        lock (statusGate) snapshot = status with { };

        if (!string.Equals(snapshot.Phase, "running", StringComparison.OrdinalIgnoreCase))
            return snapshot;

        var now = DateTime.Now;
        var alive = new List<ActiveEpgWorkerProcess>();
        foreach (var worker in activeEpgWorkerProcesses.Values)
        {
            if (IsProcessAlive(worker.Pid))
                alive.Add(worker);
            else
                activeEpgWorkerProcesses.TryRemove(worker.Pid, out _);
        }

        alive = alive
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
            // 取得完了イベントより先に100%扱いすると、完了グループ数との見た目が逆転するため上限は95%。
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
            CancelRoute = uiVisible ? "WidgetOrTray" : "TrayOnly",
            TargetScope = string.IsNullOrWhiteSpace(targetScope) ? "All" : targetScope,
            LastRunMessage = uiVisible ? "取得開始準備中" : "サイレントEPG取得中"
        });
    }

    /// <summary>
    /// EPGキャンセル完了を、停止要求ではなく「worker停止・activity解放・EPG lease解放」後として扱う。
    /// EpgScheduler はこの完了待ちの後で EPG_RUN_END と ALLOC_ROUTE:EpgCancelled を出す。
    /// </summary>
    public async Task WaitForCancellationQuiescenceAsync(string source, bool silent, string targetScope, TimeSpan? timeout = null)
    {
        var waitLimit = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + waitLimit;
        var loggedWait = false;

        while (true)
        {
            var runId = currentEpgRunId;
            var activeTasks = SnapshotActiveEpgWorkerTasks(runId);
            var tracked = activeEpgWorkerProcesses.Values.OrderBy(x => x.Group).ThenBy(x => x.TsId).ThenBy(x => x.Pid).ToList();
            var alive = new List<ActiveEpgWorkerProcess>();
            foreach (var worker in tracked)
            {
                if (IsProcessAlive(worker.Pid)) alive.Add(worker);
                else activeEpgWorkerProcesses.TryRemove(worker.Pid, out _);
            }

            var epgSlots = tunerPool.GetStatus()
                .Where(s => s.UsageKind == TunerUsageKind.Epg)
                .OrderBy(s => s.SlotIndex)
                .ToList();

            var cooldowns = epgCooldownWaits.Values
                .Where(x => x.Until >= DateTime.Now)
                .OrderBy(x => x.Group).ThenBy(x => x.TsId).ThenBy(x => x.SlotName)
                .ToList();
            foreach (var expired in epgCooldownWaits.Values.Where(x => x.Until < DateTime.Now).ToList())
                epgCooldownWaits.TryRemove(expired.Key, out _);

            if (alive.Count == 0 && activeTasks.Count == 0 && epgSlots.Count == 0)
            {
                LogEpgWorkerCoverage("cancel_quiescence_complete", force: true);
                Log("EPG_CANCEL_RELEASE_COMPLETE", "EPG",
                    $"result=OK source={SafeLog(source)} silent={silent} uiMode={(silent ? "Silent" : "Visible")} targetScope={SafeLog(targetScope)} runId={SafeLog(runId)} aliveWorkers=0 activeWorkerTasks=0 epgSlots=0 cooldownWaits={cooldowns.Count} action=allow_epg_run_end_and_allocation_reevaluate rule=release_contract");
                return;
            }

            if (!loggedWait)
            {
                loggedWait = true;
                LogEpgWorkerCoverage("cancel_quiescence_wait", force: true);
                Log("EPG_CANCEL_RELEASE_WAIT", "EPG",
                    $"source={SafeLog(source)} silent={silent} uiMode={(silent ? "Silent" : "Visible")} targetScope={SafeLog(targetScope)} runId={SafeLog(runId)} aliveWorkers={alive.Count} activeWorkerTasks={activeTasks.Count} epgSlots={epgSlots.Count} cooldownWaits={cooldowns.Count} action=wait_before_epg_run_end_and_allocation_reevaluate rule=release_contract");
            }

            if (DateTime.UtcNow >= deadline)
            {
                LogEpgWorkerCoverage("cancel_quiescence_timeout", force: true);
                var activeTaskSummary = FormatActiveEpgWorkerTaskSummary(activeTasks);
                Log("EPG_CANCEL_RELEASE_COMPLETE", "WARN",
                    $"result=TIMEOUT source={SafeLog(source)} silent={silent} uiMode={(silent ? "Silent" : "Visible")} targetScope={SafeLog(targetScope)} runId={SafeLog(runId)} aliveWorkers={alive.Count} activeWorkerTasks={activeTasks.Count} activeTaskSummary={SafeLog(activeTaskSummary)} epgSlots={epgSlots.Count} cooldownWaits={cooldowns.Count} action=continue_with_warn_before_allocation_reevaluate rule=release_contract");
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
        ushort? expectedNetworkId = null,
        ushort? expectedTransportStreamId = null,
        ushort? expectedServiceId = null,
        string? expectedServiceName = null,
        ushort? expectedEventId = null,
        DateTime? expectedStartTime = null,
        DateTime? expectedEndTime = null,
        bool isPreRecordCheck = false,
        int? maxCaptureSeconds = null,
        bool showProgress = true,
        string? preferredRecordingTunerName = null,
        string? preTuneChainPosition = null,
        string? preTuneAction = null,
        bool preTuneKeepWorkerUntilSafetyCeiling = false)
    {
        var started = DateTime.Now;
        var normalizedScope = NormalizeTargetScope(targetScope);
        var normalizedDepth = NormalizeDepth(runDepth ?? effectiveEpgDepth);
        var singleServiceMode = expectedServiceId.HasValue || expectedTransportStreamId.HasValue || expectedNetworkId.HasValue || !string.IsNullOrWhiteSpace(expectedServiceName);
        var runPurpose = isPreRecordCheck ? "pre_record_time_follow" : "normal_epg_capture";
        var runId = $"epg-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Interlocked.Increment(ref epgRunSequence)}";
        currentEpgRunId = runId;
        PruneCompletedEpgWorkerTasksForOldRuns(runId);
        runtimeEpgDepthOverride = normalizedDepth;
        if (!isPreRecordCheck)
        {
            currentRunBlockedGroups.Clear();
            currentRunCaptureFailures.Clear();
        }
        if (isPreRecordCheck)
        {
            Log("PRE_REC_EPG_PROBE_RUN_START", "EPG確認",
                $"目的番組の開始時刻確認を開始します。targetScope={normalizedScope} uiVisible={showProgress} singleServiceMode={singleServiceMode} safetyCeilingSeconds={(maxCaptureSeconds?.ToString() ?? "-")} expectedNid={(expectedNetworkId?.ToString() ?? "-")} expectedTsid={(expectedTransportStreamId?.ToString() ?? "-")} expectedSid={(expectedServiceId?.ToString() ?? "-")} expectedEventId={(expectedEventId?.ToString() ?? "-")} expectedStart={(expectedStartTime?.ToString("MM/dd HH:mm:ss") ?? "-")} expectedEnd={(expectedEndTime?.ToString("MM/dd HH:mm:ss") ?? "-")} expectedService={SafeLogValue(expectedServiceName)} preferredTuner={SafeLogValue(preferredRecordingTunerName)} chainPosition={SafeLogValue(preTuneChainPosition)} preTuneAction={SafeLogValue(preTuneAction)} keepWorkerUntilSafetyCeiling={preTuneKeepWorkerUntilSafetyCeiling} policy=silent_time_follow_probe_and_record_pretune_same_tuner rule=release_contract" );
        }
        else
        {
            Log("EPG_RUN_START", "EPG", $"EPG取得を開始します。targetScope={normalizedScope} runDepth={normalizedDepth} purpose={runPurpose} uiVisible={showProgress} uiMode={(showProgress ? "Visible" : "Silent")} singleServiceMode={singleServiceMode} maxCaptureSeconds={(maxCaptureSeconds?.ToString() ?? "-")} expectedNid={(expectedNetworkId?.ToString() ?? "-")} expectedTsid={(expectedTransportStreamId?.ToString() ?? "-")} expectedSid={(expectedServiceId?.ToString() ?? "-")} expectedService={SafeLogValue(expectedServiceName)} preferredTuner={SafeLogValue(preferredRecordingTunerName)} rule=release_contract");
        }
        epgRunAcceptingNewWorkers = true;
        TvTestActivityHandle? epgSleepGuardBridge = null;
        CancellationTokenSource? coverageCts = null;
        Task? coverageMonitor = null;
        var bridgeStopReason = "finally_cleanup";
        epgSleepGuardBridge = StartEpgSleepGuardBridgeIfNeeded(isPreRecordCheck, normalizedScope, normalizedDepth);

        try
        {

        // 録画前EPG確認では、外部プロセス一覧で見えるTVTestをTvAIr管理対象へ混ぜない。
        // 管理対象はTunerPool/TvAirManagedProcessRegistry上のTvAIr/AIrCon管理下だけに限定する。
        if (isPreRecordCheck)
        {
            liveTvTestKeys = new HashSet<(string, string)>();
            Log("EPG_LIVE_DETECT", "EPG",
                "result=SKIPPED reason=pre_record_check_uses_managed_tuner_pool_only externalTvTestProcessScan=False rule=release_contract");
        }
        else if (ini.EpgExcludeLiveTvTest)
        {
            try
            {
                liveTvTestKeys = LiveTvTestDetector.Detect();
                if (liveTvTestKeys.Count > 0)
                {
                    Log("EPG_LIVE_DETECT", "EPG",
                        $"LIVE視聴中TVTest検出: {liveTvTestKeys.Count}本 [" +
                        string.Join(", ", liveTvTestKeys.Select(k => $"{k.BonDriverFileName}/{k.Did}")) + "]");
                }
            }
            catch (Exception ex)
            {
                liveTvTestKeys = new HashSet<(string, string)>();
                Log("EPG_LIVE_DETECT", "EPG", $"LIVE視聴中チューナー検出失敗（無視して継続）: {ex.Message}");
            }
        }
        else
        {
            liveTvTestKeys = new HashSet<(string, string)>();
        }

        // チャンネル読み込み
        var load = channelLoader.Load();
        Log("EPG_CH2_LOADED", "EPG", load.Message);
        foreach (var warning in load.Warnings)
            Log("EPG_CHANNEL_CONFIG", "Settings", warning);

        if (load.Targets.Count == 0)
        {
            Log("EPG_RUN_FAIL", "EPG", "有効なチャンネルがありません。ch2/ChSetを設定画面で明示してください。source=explicit_settings rule=release_contract");
            return EpgCaptureResult.Failed("有効なチャンネルがありません。ch2/ChSetを設定画面で明示してください。");
        }

        // TSグループ化（TransportStreamId単位）
        var allGroups = BuildGroups(load.Targets);
        var groups = FilterGroupsByScope(allGroups, normalizedScope);
        if (singleServiceMode)
        {
            groups = FilterGroupsForExpectedService(groups, expectedNetworkId, expectedTransportStreamId, expectedServiceId, expectedServiceName);
            Log("EPG_PRE_REC_TARGET_FILTER", "EPG",
                $"targetScope={normalizedScope} matchedGroups={groups.Count} expectedNid={(expectedNetworkId?.ToString() ?? "-")} expectedTsid={(expectedTransportStreamId?.ToString() ?? "-")} expectedSid={(expectedServiceId?.ToString() ?? "-")} expectedService={SafeLogValue(expectedServiceName)} rule=release_contract");
        }
        if (groups.Count == 0)
        {
            var noTarget = $"EPG取得対象がありません。targetScope={normalizedScope}";
            Log("EPG_RUN_FAIL", "EPG", noTarget);
            SetStatus(s => s with { Phase = "idle", TotalGroups = 0, CompletedGroups = 0, RunningGroups = 0, RunningGroupNames = "", ActiveWorkerElapsedSeconds = 0, ActiveWorkerPlannedSeconds = 0, EstimatedProgressPercent = 0, RunStartedAt = null, LastRunAt = DateTime.Now, LastRunMessage = noTarget, RunDepth = normalizedDepth, TargetScope = normalizedScope, UiVisible = showProgress, RunPurpose = runPurpose, UiMode = showProgress ? "Visible" : "Silent", CancelRoute = showProgress ? "WidgetOrTray" : "TrayOnly" });
            epgRunAcceptingNewWorkers = false;
            return EpgCaptureResult.Failed(noTarget);
        }
        SetStatus(s => s with
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
            LastRunMessage = isPreRecordCheck ? "録画前EPG確認中" : "取得中",
            RunDepth = normalizedDepth,
            TargetScope = normalizedScope,
            UiVisible = showProgress,
            RunPurpose = runPurpose,
            UiMode = showProgress ? "Visible" : "Silent",
            CancelRoute = showProgress ? "WidgetOrTray" : "TrayOnly"
        });
        if (isPreRecordCheck)
        {
            Log("PRE_REC_EPG_PROBE_PLAN", "EPG確認",
                $"targetScope={normalizedScope} targetServices={groups.SelectMany(g => g.Targets).Count()} targetGroups={groups.Count} allGroups={allGroups.Count} policy=probe_only_no_epg_capture_panel rule=epg_probe_runtime_contract");
        }
        else
        {
            Log("EPG_PLAN", "EPG",
                $"TS単位巡回取得を開始します。targetScope={normalizedScope} 対象 {groups.SelectMany(g => g.Targets).Count()} 局 / {groups.Count} グループ。allGroups={allGroups.Count} rule=epg_plan_contract");
        }


        using var limitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limitCts.CancelAfter(TimeSpan.FromMinutes(settings.TotalTimeLimitMinutes));
        var execToken = limitCts.Token;

        coverageCts = CancellationTokenSource.CreateLinkedTokenSource(execToken);
        coverageMonitor = StartEpgWorkerCoverageMonitorAsync(started, coverageCts.Token);

        var totalImported = 0;
        var completedGroups = new HashSet<string>();

        totalImported += await RunPassAsync(groups, completedGroups, pass: 1, execToken, isPreRecordCheck, maxCaptureSeconds, expectedNetworkId, expectedTransportStreamId, expectedServiceId, expectedEventId, expectedStartTime, expectedEndTime, preferredRecordingTunerName, preTuneChainPosition, preTuneAction, preTuneKeepWorkerUntilSafetyCeiling);
        execToken.ThrowIfCancellationRequested();

        execToken.ThrowIfCancellationRequested();
        epgRunAcceptingNewWorkers = false;

        if (isPreRecordCheck)
        {
            Log("PRE_REC_EPG_PROBE_ENDING", "EPG確認",
                $"enter completed={completedGroups.Count}/{groups.Count} importedEvents={totalImported} rule=epg_probe_runtime_contract");
        }
        else
        {
            Log("EPG_ENDING_PHASE", "EPG",
                $"enter completed={completedGroups.Count}/{groups.Count} imported={totalImported} note=serialize_exit_release_cleanup");
        }

        // 前TVTest終了後、次TVTest起動前にクールダウン待機を挟む。
        // cleanup/UI reload work begins. This keeps the end of EPG acquisition from
        // bunching together with ALLOC_ROUTE and browser refresh.
        try { await Task.Delay(1500, execToken); } catch (OperationCanceledException) { }

        var elapsed = (int)(DateTime.Now - started).TotalSeconds;
        var totalGroups = groups.Count;
        var completedCount = completedGroups.Count;
        var missingGroups = Math.Max(0, totalGroups - completedCount);
        var missingGroupDetails = FormatMissingGroupDetails(groups, completedGroups);
        var failureSummary = FormatCaptureFailureSummary(groups, completedGroups);
        var blockedReason = ResolveEpgRunBlockedReason(groups, normalizedScope, completedCount, totalGroups);
        var recordingConcurrent = HasRecordingConcurrentWithEpgRun(groups);
        var runResult = !string.IsNullOrWhiteSpace(blockedReason)
            ? "BLOCKED"
            : completedCount == totalGroups && totalGroups > 0
                ? "OK"
                : "FAILED";
        var detailCore = $"result={runResult} targetScope={normalizedScope} runDepth={normalizedDepth} completed={completedCount}/{totalGroups} imported={totalImported} missingGroups={missingGroups} missingGroupDetails=[{missingGroupDetails}] failureSummary=[{failureSummary}]";
        var detailExtra = $"blockedReason={SafeLogValue(blockedReason)} recordingConcurrent={recordingConcurrent}";
        var detail = detailCore + " " + detailExtra;
        var captureResultDetail = $"blockedReason={SafeLogValue(blockedReason)}; recordingConcurrent={recordingConcurrent}; missingGroupDetails={missingGroupDetails}; failureSummary={failureSummary}";
        var msg = isPreRecordCheck
            ? "録画前時刻確認を終了しました"
            : runResult == "OK"
                ? $"EPG取得を完了しました\n{completedCount}/{totalGroups} グループ完了\n{totalImported} 件取得しました"
                : runResult == "BLOCKED"
                    ? "EPG取得を開始できません"
                    : $"EPG取得に失敗しました\n{completedCount}/{totalGroups} グループ完了\n{totalImported} 件取得しました";
        if (isPreRecordCheck)
        {
            Log("PRE_REC_EPG_PROBE_RUN_END", "OK", $"result=OK targetScope={normalizedScope} targetGroups={groups.Count} completedGroups={completedGroups.Count} importedEvents={totalImported} elapsedSec={elapsed} rule=epg_probe_runtime_contract");
        }
        else
        {
            var runEvent = runResult == "OK" ? "EPG_RUN_OK" : runResult == "BLOCKED" ? "EPG_RUN_BLOCKED" : "EPG_RUN_FAILED";
            var runTitle = runResult == "OK" ? "EPG" : runResult == "BLOCKED" ? "BLOCKED" : "FAILED";
            Log(runEvent, runTitle, msg.Replace("\n", " / ") + " " + detail + " rule=release_contract");
            Log("EPG_RUN_END", runResult, detail + " rule=release_contract");
            // GR全滅チェック
            var grGroupKeys = groups.Where(g => g.Group == "GR").Select(g => g.Key).ToList();
            if (grGroupKeys.Count > 0 && !grGroupKeys.Any(k => completedGroups.Contains(k)))
                Log("EPG_GR_ALL_EMPTY", "WARN", $"result=WARN grGroups={grGroupKeys.Count} grCompleted=0 rule=release_contract");
        }
        var finalPhase = runResult == "BLOCKED" ? "blocked" : "completed";
        var finalCompletedGroups = runResult == "BLOCKED" ? completedCount : groups.Count;
        var finalProgress = runResult == "BLOCKED" ? 0 : 100;
        SetStatus(s => s with { Phase = finalPhase, CompletedGroups = finalCompletedGroups, RunningGroups = 0, RunningGroupNames = "", ActiveWorkerElapsedSeconds = 0, ActiveWorkerPlannedSeconds = WaitSecondsForDepth(normalizedDepth), EstimatedProgressPercent = finalProgress, LastRunAt = DateTime.Now, LastRunMessage = msg, RunDepth = normalizedDepth, TargetScope = normalizedScope, UiVisible = showProgress, RunPurpose = runPurpose, UiMode = showProgress ? "Visible" : "Silent", CancelRoute = showProgress ? "WidgetOrTray" : "TrayOnly" });

        // 期限切れデータを削除（昨日より前）
        var deleted = store.DeleteExpired(DateTime.Now.Date.AddDays(-1));
        if (deleted > 0)
            Log("EPG_CLEANUP", "EPG", $"期限切れ {deleted} 件を削除しました。");

        await StopEpgWorkerCoverageMonitorIfStartedAsync(coverageCts, coverageMonitor);
        coverageCts = null;
        coverageMonitor = null;
        LogEpgWorkerCoverage("run_end", force: true);
        bridgeStopReason = "epg_run_finished";

        return new EpgCaptureResult(true, completedCount, totalGroups, totalImported, runResult, missingGroups, msg, captureResultDetail);
        }
        finally
        {
            epgRunAcceptingNewWorkers = false;
            await StopEpgWorkerCoverageMonitorIfStartedAsync(coverageCts, coverageMonitor);
            StopEpgSleepGuardBridgeIfStarted(epgSleepGuardBridge, isPreRecordCheck, normalizedScope, normalizedDepth, bridgeStopReason);
        }
    }

    private string FormatMissingGroupDetails(IReadOnlyList<TsGroup> groups, HashSet<string> completedGroups)
    {
        var missing = groups
            .Where(g => !completedGroups.Contains(g.Key))
            .Select(g => $"{g.Group}:TS{g.TsId}:{SafeLogValue(g.Targets.FirstOrDefault()?.Name)}")
            .ToList();
        return missing.Count == 0 ? "-" : string.Join(",", missing);
    }

    private string FormatCaptureFailureSummary(IReadOnlyList<TsGroup> groups, HashSet<string> completedGroups)
    {
        var failures = groups
            .Where(g => !completedGroups.Contains(g.Key))
            .Select(g => currentRunCaptureFailures.TryGetValue(g.Key, out var f)
                ? $"{g.Group}:TS{g.TsId}:{SafeLogValue(g.Targets.FirstOrDefault()?.Name)}:{SafeLogValue(f.Reason)}:{SafeLogValue(f.Detail)}"
                : $"{g.Group}:TS{g.TsId}:{SafeLogValue(g.Targets.FirstOrDefault()?.Name)}:unknown")
            .ToList();
        return failures.Count == 0 ? "-" : string.Join("|", failures);
    }

    private void RecordCaptureFailure(TsGroup group, string reason, string detail, int attempt, int maxAttempts)
    {
        var state = new EpgCaptureFailureState(group.Group, group.TsId, reason, detail, attempt, maxAttempts, DateTime.Now);
        currentRunCaptureFailures[group.Key] = state;
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

    private string ResolveEpgRunBlockedReason(IReadOnlyList<TsGroup> groups, string normalizedScope, int completedCount, int totalGroups)
    {
        if (totalGroups <= 0 || completedCount > 0) return string.Empty;

        var blockedReason = currentRunBlockedGroups.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (!string.IsNullOrWhiteSpace(blockedReason)) return NormalizeBlockedReason(blockedReason);

        var targetGroups = groups
            .Select(g => NormalizeEpgTargetGroup(g.Group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var group in targetGroups)
        {
            if (RecordingLifecycleGate.IsNormalEpgSuppressed(group, out _, out var reason, out _, out _))
                return NormalizeBlockedReason(reason);
        }

        return string.Empty;
    }

    private bool HasRecordingConcurrentWithEpgRun(IReadOnlyList<TsGroup> groups)
    {
        var targetGroups = groups
            .Select(g => NormalizeEpgTargetGroup(g.Group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tunerPool.GetStatus().Any(s =>
            s.UsageKind == TunerUsageKind.Recording
            && s.ReservationId.HasValue
            && targetGroups.Contains(NormalizeEpgTargetGroup(s.Group)));
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
        if (string.IsNullOrWhiteSpace(reason)) return "recording_preempt_lock";
        var normalized = reason.Trim();
        if (normalized.Contains("window_insufficient", StringComparison.OrdinalIgnoreCase)) return "recording_window_insufficient";
        if (normalized.Contains("warmup", StringComparison.OrdinalIgnoreCase)) return "recording_warmup";
        if (normalized.Contains("stable", StringComparison.OrdinalIgnoreCase)) return "recording_stable_free_tuner_allowed";
        return normalized.Contains("recording", StringComparison.OrdinalIgnoreCase)
            ? "recording_preempt_lock"
            : normalized;
    }

    // ─── 1パス処理 ───────────────────────────────────────────────

    private async Task<int> RunPassAsync(
        IReadOnlyList<TsGroup> groups,
        HashSet<string> completedGroups,
        int pass,
        CancellationToken ct,
        bool isPreRecordCheck,
        int? maxCaptureSeconds,
        ushort? expectedNetworkId = null,
        ushort? expectedTransportStreamId = null,
        ushort? expectedServiceId = null,
        ushort? expectedEventId = null,
        DateTime? expectedStartTime = null,
        DateTime? expectedEndTime = null,
        string? preferredRecordingTunerName = null,
        string? preTuneChainPosition = null,
        string? preTuneAction = null,
        bool preTuneKeepWorkerUntilSafetyCeiling = false)
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
            g => Math.Max(0, tunerPool.CountEpgUsableFreeSlots(g, liveTvTestKeys)),
            StringComparer.OrdinalIgnoreCase);
        var groupEpgCapacity = targetGroups.ToDictionary(
            g => g,
            g => isPreRecordCheck
                ? (groupLogicalUsableSlots.TryGetValue(g, out var preSlots) && preSlots > 0 ? 1 : 0)
                : (groupLogicalUsableSlots.TryGetValue(g, out var normalSlots) ? normalSlots : 0),
            StringComparer.OrdinalIgnoreCase);
        var totalRecordableSlots = groupRecordableSlots.Values.Sum();
        var totalLogicalEpgUsableSlots = groupLogicalUsableSlots.Values.Sum();
        var totalGroupEpgAdmissionCapacity = groupEpgCapacity.Values.Sum();
        var groupRecordableSummary = string.Join(",", targetGroups.Select(g => $"{g}:{(groupRecordableSlots.TryGetValue(g, out var recordableValue) ? recordableValue : 0)}"));
        var groupLogicalSummary = string.Join(",", targetGroups.Select(g => $"{g}:{(groupLogicalUsableSlots.TryGetValue(g, out var logicalValue) ? logicalValue : 0)}"));
        var groupCapacitySummary = string.Join(",", targetGroups.Select(g => $"{g}:{(groupEpgCapacity.TryGetValue(g, out var capacityValue) ? capacityValue : 0)}"));
        var liveExclusionSummary = liveTvTestKeys is null || liveTvTestKeys.Count == 0
            ? "-"
            : string.Join(",", liveTvTestKeys.Select(k => $"{SafeLogValue(k.BonDriverFileName)}/{SafeLogValue(k.Did)}"));

        if (isPreRecordCheck)
        {
            Log("PRE_REC_EPG_PROBE_GROUP_START", string.Join("+", targetGroups),
                $"pass={pass} groups={groups.Count} policy=GroupEpgCapacityAdmission groupLogical=[{groupLogicalSummary}] groupCapacity=[{groupCapacitySummary}] admissionCapacity={totalGroupEpgAdmissionCapacity} rule=release_contract");
        }
        else
        {
            Log("EPG_GLOBAL_JOB_QUEUE_PLAN", "EPG",
                $"pass={pass} groups={groups.Count} targetGroups=[{string.Join(",", targetGroups)}] recordableSlots={totalRecordableSlots} logicalEpgUsableSlots={totalLogicalEpgUsableSlots} groupRecordable=[{groupRecordableSummary}] groupLogical=[{groupLogicalSummary}] groupCapacity=[{groupCapacitySummary}] admissionCapacity={totalGroupEpgAdmissionCapacity} liveExcluded=[{liveExclusionSummary}] policy=group_capacity_epg_job_queue rule=release_contract");
        }

        var orderedGroups = BuildEpgTargetGroupFairQueue(groups, targetGroups);
        var admissibleHeadCount = Math.Max(1, Math.Min(totalGroupEpgAdmissionCapacity <= 0 ? targetGroups.Count : totalGroupEpgAdmissionCapacity, orderedGroups.Count));
        var queueHeadSummary = string.Join(",", orderedGroups
            .Take(admissibleHeadCount)
            .Select(g => $"{NormalizeEpgTargetGroup(g.Group)}:{g.TsId}"));
        if (!isPreRecordCheck)
        {
            Log("EPG_TARGET_GROUP_QUEUE_PLAN", "EPG",
                $"pass={pass} queuePolicy=EpgTargetGroupFairQueue admissionPolicy=GroupEpgCapacity targetGroups=[{string.Join(",", targetGroups)}] queueHead=[{queueHeadSummary}] groupCapacity=[{groupCapacitySummary}] admissionCapacity={totalGroupEpgAdmissionCapacity} rule=release_contract");
        }

        var groupSemaphores = groupEpgCapacity
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => new SemaphoreSlim(kv.Value), StringComparer.OrdinalIgnoreCase);
        if (groupSemaphores.Count == 0)
        {
            Log("EPG_GROUP_CAPACITY_ADMISSION", "EPG",
                $"result=NO_CAPACITY pass={pass} targetGroups=[{string.Join(",", targetGroups)}] groupCapacity=[{groupCapacitySummary}] liveExcluded=[{liveExclusionSummary}] action=skip_new_workers rule=release_contract");
            return 0;
        }

        var imported = 0;
        var tasks = new List<Task>();
        var pendingGroups = orderedGroups.ToList();
        var gate = new object();
        var firstLaunch = true;
        var staggerMs = Math.Max(0, ini.EpgLaunchStaggerMs);

        async Task LaunchAsync(TsGroup group, SemaphoreSlim groupSemaphore)
        {
            if (!firstLaunch && staggerMs > 0)
                await Task.Delay(staggerMs, ct);
            firstLaunch = false;

            var g = group;
            var releaseSemaphore = groupSemaphore;
            var workerTaskState = RegisterActiveEpgWorkerTask(g, pass);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var count = await CaptureGroupAsync(g, pass, ct, isPreRecordCheck, maxCaptureSeconds, expectedNetworkId, expectedTransportStreamId, expectedServiceId, expectedEventId, expectedStartTime, expectedEndTime, preferredRecordingTunerName, preTuneChainPosition, preTuneAction, preTuneKeepWorkerUntilSafetyCeiling);

                    if (IsNormalEpgCaptureSufficient(g, count, isPreRecordCheck))
                    {
                        lock (gate)
                        {
                            completedGroups.Add(g.Key);
                            imported += count;
                        }
                        SetStatus(s => s with { CompletedGroups = completedGroups.Count });
                    }
                    else if (count > 0)
                    {
                        lock (gate)
                        {
                            imported += count;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    releaseSemaphore.Release();
                    CompleteActiveEpgWorkerTask(workerTaskState);
                }
            }, CancellationToken.None));
        }

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
                    if (!isPreRecordCheck)
                    {
                        Log("EPG_GROUP_CAPACITY_ADMISSION", $"TS{group.TsId}",
                            $"result=SKIP_NO_GROUP_CAPACITY pass={pass} group={groupKey} groupCapacity=[{groupCapacitySummary}] rule=release_contract");
                    }
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

            var completedTask = await Task.WhenAny(tasks);
            tasks.Remove(completedTask);
            await completedTask;
        }

        if (ct.IsCancellationRequested)
        {
            epgRunAcceptingNewWorkers = false;
            Log("EPG_CANCEL_NEW_WORKERS_STOPPED", "EPG", $"pass={pass} reason=ct_cancelled rule=release_contract");
        }

        await Task.WhenAll(tasks);
        foreach (var semaphore in groupSemaphores.Values) semaphore.Dispose();
        return imported;
    }


    private bool IsNormalEpgCaptureSufficient(TsGroup group, int imported, bool isPreRecordCheck)
    {
        if (imported <= 0) return false;

        Log("EPG_COMPLETION", $"TS{group.TsId}",
            $"result={(imported > 0 ? "COMPLETE" : "EMPTY")} imported={imported} purpose={(isPreRecordCheck ? "pre_record_check" : "normal_epg_capture")} group={group.Group} services={group.Targets.Count} rule=release_contract");

        return true;
    }

    /// <summary>
    /// 同一グループ（GR または BSCS）の TsGroup リストを並列処理する。
    /// 並列数は TunerPool の当該 BonDriver の空きスロット数で自動決定する。
    /// 並列上限内であっても、ジョブ投入間に EpgLaunchStaggerMs の間隔を入れて
    /// 同時CmdSetCh/CmdOpenTuner集中を緩和する。
    /// </summary>
    private async Task<int> RunGroupsAsync(
        IReadOnlyList<TsGroup> groups,
        HashSet<string> completedGroups,
        int pass,
        CancellationToken ct,
        bool isPreRecordCheck,
        int? maxCaptureSeconds,
        ushort? expectedNetworkId = null,
        ushort? expectedTransportStreamId = null,
        ushort? expectedServiceId = null,
        ushort? expectedEventId = null,
        DateTime? expectedStartTime = null,
        DateTime? expectedEndTime = null,
        string? preferredRecordingTunerName = null,
        string? preTuneChainPosition = null,
        string? preTuneAction = null,
        bool preTuneKeepWorkerUntilSafetyCeiling = false)
    {
        if (groups.Count == 0) return 0;

        // このグループの録画用スロット数を並列上限にする。
        // Viewing専用スロットを並列上限に含めると、実際にはEPG確保できない余剰ワーカーが
        // EPG_TUNER_BUSYを連発して空欄TSを増やすため、録画用として使える枠だけで回す。
        var groupName  = groups[0].Group;
        var recordableSlotCount = tunerPool.GetStatus()
            .Count(s => string.Equals(s.Group, groupName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(s.Role, "Viewing", StringComparison.OrdinalIgnoreCase));
        var initialFreeSlots = tunerPool.CountRecordableFreeSlots(groupName);
        var initialEpgUsableFreeSlots = tunerPool.CountEpgUsableFreeSlots(groupName, liveTvTestKeys);
        // release_contract: 録画用本数などの設定値ではなく、保護/占有/外部TVTest除外後の
        // EPG用途で実際に使える論理スロット数だけを並列数にする。
        var logicalEpgSlotCount = initialEpgUsableFreeSlots;
        var concurrent = Math.Max(1, logicalEpgSlotCount);
        if (!isPreRecordCheck)
        {
            var status = tunerPool.GetStatus();
            var fixedExactSlots = status.Count(s => string.Equals(s.Group, groupName, StringComparison.OrdinalIgnoreCase));
            var nonViewingExactSlots = status.Count(s => string.Equals(s.Group, groupName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(s.Role, "Viewing", StringComparison.OrdinalIgnoreCase));
            var freeExactNonViewing = status.Count(s => string.Equals(s.Group, groupName, StringComparison.OrdinalIgnoreCase)
                && s.UsageKind == TunerUsageKind.Free
                && !string.Equals(s.Role, "Viewing", StringComparison.OrdinalIgnoreCase));
            var liveExclusionSummary = liveTvTestKeys is null || liveTvTestKeys.Count == 0
                ? "-"
                : string.Join(",", liveTvTestKeys.Select(k => $"{SafeLogValue(k.BonDriverFileName)}/{SafeLogValue(k.Did)}"));
            Log("EPG_WORKER_CONCURRENCY_PLAN", groupName,
                $"group={groupName} groups={groups.Count} recordableSlots={recordableSlotCount} initialFreeSlots={initialFreeSlots} logicalEpgUsableSlots={logicalEpgSlotCount} concurrent={concurrent} liveExcluded=[{liveExclusionSummary}] policy=settings_to_logical_resource rule=release_contract");
        }

        // ジョブ投入インターバル
        var staggerMs = Math.Max(0, ini.EpgLaunchStaggerMs);

        using var sem = new SemaphoreSlim(concurrent);
        var imported  = 0;
        var tasks     = new List<Task>();
        var gate      = new object();
        var firstLaunch = true;

        foreach (var group in groups)
        {
            if (ct.IsCancellationRequested)
            {
                epgRunAcceptingNewWorkers = false;
                Log("EPG_CANCEL_NEW_WORKERS_STOPPED", "EPG", $"pass={pass} reason=ct_cancelled rule=epg_plan_contract");
                break;
            }
            if (completedGroups.Contains(group.Key)) continue;

            await sem.WaitAsync(ct);

            // 2件目以降は staggerMs だけ待ってから投入し、
            // CmdSetCh/CmdOpenTuner の集中を分散する。最初の1件は即時。
            if (!firstLaunch && staggerMs > 0)
            {
                try { await Task.Delay(staggerMs, ct); }
                catch (OperationCanceledException) { sem.Release(); break; }
            }
            firstLaunch = false;

            var g = group;
            var workerTaskState = RegisterActiveEpgWorkerTask(g, pass);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var count = await CaptureGroupAsync(g, pass, ct, isPreRecordCheck, maxCaptureSeconds, expectedNetworkId, expectedTransportStreamId, expectedServiceId, expectedEventId, expectedStartTime, expectedEndTime, preferredRecordingTunerName, preTuneChainPosition, preTuneAction, preTuneKeepWorkerUntilSafetyCeiling);

                    if (IsNormalEpgCaptureSufficient(g, count, isPreRecordCheck))
                    {
                        lock (gate)
                        {
                            completedGroups.Add(g.Key);
                            imported += count;
                        }
                        SetStatus(s => s with { CompletedGroups = completedGroups.Count });
                    }
                    else if (count > 0)
                    {
                        lock (gate)
                        {
                            imported += count;
                        }
                    }
                }
                catch (OperationCanceledException) { /* 時間切れは正常 */ }
                finally
                {
                    sem.Release();
                    CompleteActiveEpgWorkerTask(workerTaskState);
                }
            }, CancellationToken.None));
        }

        await Task.WhenAll(tasks);
        return imported;
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
        bool preTuneKeepWorkerUntilSafetyCeiling = false)
    {
        await epgLaunchStartGate.WaitAsync(ct);
        try
        {
            var gapMs = Math.Max(0, ini.EpgLaunchStaggerMs);
            Log("EPG_LAUNCH_GATE", $"TS{group.TsId}",
                $"enter worker={workerName} group={group.Group} bonDriver={bonDriverFileName} did={did} gapAfterLaunch={gapMs}ms route=TvAIrEpgRec mode=epg-ts rule=epg_worker_route_contract");

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
                    $"worker={workerName} targetDid={did} bonDriver={bonDriverFileName} externalSameDid={viewingAudit.ExternalSameDid} targetIsViewingRole={viewingAudit.TargetIsViewingRole}");
                return new EpgWorkerLaunchResult(false, 0, "EPG blocked by viewing protection", null, null, null);
            }

            var launch = StartTvAIrEpgRecForEpg(
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
                preTuneKeepWorkerUntilSafetyCeiling);

            if (launch.Success && launch.ProcessId > 0)
            {
                var primaryServiceName = displayServiceName ?? group.Targets.FirstOrDefault()?.Name ?? $"TS{group.TsId}";
                RegisterActiveEpgWorkerProcess(launch.ProcessId, workerName, group, primaryServiceName, "launch_pending", launch.StopSignalPath);
            }

            if (gapMs > 0)
            {
                try { await Task.Delay(gapMs, ct); }
                catch (OperationCanceledException)
                {
                    if (launch.Success && launch.ProcessId > 0)
                    {
                        await StopAndRetireActiveEpgWorkerAsync(
                            launch,
                            workerName,
                            group,
                            "launch_gate_delay",
                            "launch_gate_cancelled").ConfigureAwait(false);
                    }
                    throw;
                }
            }

            Log("EPG_LAUNCH_GATE", $"TS{group.TsId}",
                $"exit worker={workerName} success={launch.Success} pid={launch.ProcessId} route=TvAIrEpgRec mode=epg-ts rule=epg_worker_route_contract");
            return launch;
        }
        finally
        {
            epgLaunchStartGate.Release();
        }
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
        bool preTuneKeepWorkerUntilSafetyCeiling = false)
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
        // 表版: 内部実行統計は生成しない。EPG/録画判定に必要な job/result/progress は
        // 親プロセスで読み終えた後に削除する。
        var runtimeStatsPath = string.Empty;
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
                ["rule"] = "release_contract",
                // TvAIrEpgRec process icon visibility follows the user setting for active EPG workers.
                ["taskbarIconVisible"] = ini.ShowTvAIrEpgRecTaskbarIcon ? "true" : "false",
                ["preTuneEnabled"] = isPreRecordCheck && !string.IsNullOrWhiteSpace(preferredRecordingTunerName) ? "true" : "false",
                ["preTuneChainPosition"] = preTuneChainPosition ?? string.Empty,
                ["preTuneAction"] = preTuneAction ?? string.Empty,
                ["preTuneDisplayLabel"] = string.Empty,
                ["preTuneTargetDisplayLabel"] = isPreRecordCheck && !string.IsNullOrWhiteSpace(preferredRecordingTunerName) ? "録画準備" : string.Empty,
                ["keepWorkerUntilSafetyCeiling"] = preTuneKeepWorkerUntilSafetyCeiling ? "true" : "false"
            }
        };

        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, EpgJobJsonOptions));

        var launchKind = isPreRecordCheck ? TvAIrEpgRecLaunchKind.PreRecordEpgCheckWorker : TvAIrEpgRecLaunchKind.EpgTransportStreamWorker;
        var showWorkerTaskbarIcon = WorkerProcessStartInfoFactory.IsTaskbarIconVisible(launchKind, ini.ShowTvAIrEpgRecTaskbarIcon);
        var windowPolicy = WorkerProcessStartInfoFactory.GetWindowPolicy(launchKind, ini.ShowTvAIrEpgRecTaskbarIcon);
        var psi = WorkerProcessStartInfoFactory.CreateTvAIrEpgRec(exe, launchKind, ini.ShowTvAIrEpgRecTaskbarIcon);
        psi.Arguments = $"--job \"{jobPath}\" --mode {(isPreRecordCheck ? "epg-check" : "epg")} --result \"{resultPath}\" --progress \"{progressPath}\"";

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
                    $"result=NG pid={launch.ProcessId} worker={workerName} reason={SafeLog(reason)} error={SafeLog(ex.Message)} rule=epg_worker_route_contract");
            }
        }
        KillProcess(launch.ProcessId);
    }

    private async Task<bool> StopAndRetireActiveEpgWorkerAsync(
        EpgWorkerLaunchResult launch,
        string workerName,
        TsGroup group,
        string phase,
        string reason,
        TimeSpan? gracefulTimeout = null)
    {
        if (!launch.Success || launch.ProcessId <= 0) return false;

        var pid = launch.ProcessId;
        var timeout = gracefulTimeout ?? TimeSpan.FromSeconds(4);

        if (IsProcessAlive(pid))
        {
            Log("EPG_CANCEL_PROCESS_STOP_REQUEST", $"TS{group.TsId}",
                $"pid={pid} worker={workerName} phase={SafeLog(phase)} route=TvAIrEpgRec reason={SafeLog(reason)} rule=epg_worker_route_contract");
            RequestTvAIrEpgRecStopOrKill(launch, workerName, group, reason);

            await WaitForExitAsync(pid, timeout, CancellationToken.None).ConfigureAwait(false);
            if (IsProcessAlive(pid))
            {
                Log("EPG_CANCEL_PROCESS_STOP_WAIT", $"TS{group.TsId}",
                    $"result=TIMEOUT pid={pid} worker={workerName} phase={SafeLog(phase)} waitMs={(int)timeout.TotalMilliseconds} action=kill rule=epg_worker_route_contract");
                KillProcess(pid);
                await WaitForExitAsync(pid, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (!IsProcessAlive(pid))
        {
            UnregisterActiveEpgWorkerProcess(pid, workerName, group, reason);
            Log("EPG_CANCEL_PROCESS_EXIT_OK", $"TS{group.TsId}",
                $"pid={pid} worker={workerName} phase={SafeLog(phase)} action=stopped_and_unregistered rule=epg_worker_route_contract");
            return true;
        }

        Log("EPG_CANCEL_PROCESS_EXIT_WARN", $"TS{group.TsId}",
            $"pid={pid} worker={workerName} phase={SafeLog(phase)} action=still_alive_keep_tracked rule=epg_worker_route_contract");
        return false;
    }

    // ─── 1グループのキャプチャ ────────────────────────────────────

    private async Task<int> CaptureGroupAsync(TsGroup group, int pass, CancellationToken ct, bool isPreRecordCheck, int? maxCaptureSeconds, ushort? expectedNetworkId, ushort? expectedTransportStreamId, ushort? expectedServiceId, ushort? expectedEventId, DateTime? expectedStartTime, DateTime? expectedEndTime, string? preferredRecordingTunerName = null, string? preTuneChainPosition = null, string? preTuneAction = null, bool preTuneKeepWorkerUntilSafetyCeiling = false)
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
        // 設定表示・定時EPG枠・通常EPG実取得・録画前EPG確認で同じ変換結果を使う。
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
                $" expectedNid={(expectedNetworkId?.ToString() ?? "-")} expectedTsid={(expectedTransportStreamId?.ToString() ?? "-")} expectedSid={(expectedServiceId?.ToString() ?? "-")} expectedEventId={(expectedEventId?.ToString() ?? "-")} expectedStart={(expectedStartTime?.ToString("MM/dd HH:mm:ss") ?? "-")} preferredTuner={SafeLogValue(preferredRecordingTunerName)} policy=stop_as_soon_as_target_event_seen_and_pretune_same_recording_tuner rule=release_contract");
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

            var effectiveWait = waitSec;

            // TunerPool からチューナーを確保
            // LIVE視聴中のチューナーを除外
            var plannedEnd = DateTime.Now.AddSeconds(effectiveWait + 30);
            var lease = await AcquireEpgLeaseWithShortWaitAsync(group, plannedEnd, workerName, ct, isPreRecordCheck, isPreRecordCheck ? preferredRecordingTunerName : null);
            if (lease is null)
            {
                if (!isPreRecordCheck)
                {
                    var liveExclusionSummary = liveTvTestKeys is null || liveTvTestKeys.Count == 0
                        ? "-"
                        : string.Join(",", liveTvTestKeys.Select(k => $"{SafeLogValue(k.BonDriverFileName)}/{SafeLogValue(k.Did)}"));
                    RecordCaptureFailure(group, "epg_tuner_unavailable", $"waitMs={ResolveEpgLogicalAcquireWaitMs(plannedEnd, isPreRecordCheck)} liveExcluded=[{liveExclusionSummary}] policy=logical_resource_queue", attempt, maxAttempts);
                }
                return 0;
            }

            // ─── TvAIrEpgRec 終了直後の同一物理チューナー再利用保護 ───
            // 旧TVTest録画時代の15秒直列待機はEPG経路では使用しない。
            // ここで残すのは、worker終了直後のClose/Open競合を避ける最小保護だけ。
            const int EpgWorkerExitSettleMs = 2000;
            var cooldownMs = EpgWorkerExitSettleMs;
            Log("EPG_TUNER_COOLDOWN_POLICY", $"TS{group.TsId}",
                $"{workerName}: effective={cooldownMs}ms policy=TvAIrEpgRec_epg_process_exit_settle_only preRecordCheck={isPreRecordCheck} rule=epg_tuner_cooldown_contract");
            if (cooldownMs > 0 && lease.ElapsedSinceReleaseMs is double epgElapsed && epgElapsed < cooldownMs)
            {
                var waitMs = (int)Math.Ceiling(cooldownMs - epgElapsed);
                if (waitMs > 0)
                {
                    var cooldownKey = workerName + ":" + lease.Name + ":" + DateTime.UtcNow.Ticks;
                    var primaryServiceName = group.Targets.FirstOrDefault()?.Name ?? $"TS{group.TsId}";
                    epgCooldownWaits[cooldownKey] = new EpgCooldownWait(
                        cooldownKey,
                        workerName,
                        group.Group,
                        group.TsId,
                        primaryServiceName,
                        lease.Name,
                        DateTime.Now,
                        DateTime.Now.AddMilliseconds(waitMs));
                    Log("EPG_TUNER_COOLDOWN", $"TS{group.TsId}",
                        $"{workerName}: チューナー直列化ゲート待機 slot={lease.Name} " +
                        $"前回解放から {epgElapsed:F0}ms / 有効 {cooldownMs}ms → {waitMs}ms 待機 policy=TvAIrEpgRec_epg_process_exit_settle_only rule=epg_tuner_cooldown_contract");
                    LogEpgWorkerCoverage("cooldown_wait", force: true);
                    try { await Task.Delay(waitMs, ct); }
                    catch (OperationCanceledException) { epgCooldownWaits.TryRemove(cooldownKey, out _); lease.Dispose(); throw; }
                    finally { epgCooldownWaits.TryRemove(cooldownKey, out _); }
                }
            }

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
                    preTuneKeepWorkerUntilSafetyCeiling);
                activeLaunch = launch;

                if (!launch.Success)
                {
                    Log("EPG_CAPTURE_FAIL", $"TS{group.TsId}", launch.Message);
                    return 0;
                }

                lease.SetProcessId(launch.ProcessId);
                var primaryServiceName = displayServiceName;
                RegisterActiveEpgWorkerProcess(launch.ProcessId, workerName, group, primaryServiceName, "attach", launch.StopSignalPath);
                using var activityHandle = tvTestActivity.AttachExisting(isPreRecordCheck ? "EPG確認中" : "EPG取得中", launch.ProcessId, primaryServiceName, workerName);
                if (isPreRecordCheck)
                    Log("PRE_REC_EPG_PROBE_TVAIREPGREC_START", $"TS{group.TsId}", launch.Message);
                else
                    Log("EPG_CAPTURE_TVAIREPGREC_START", $"TS{group.TsId}", launch.Message);

                var outputLifecycle = await ObserveEpgOutputLifecycleAsync(
                    group,
                    workerName,
                    tsFile,
                    launch.ProcessId,
                    TimeSpan.FromSeconds(isPreRecordCheck ? 10 : 25),
                    ct);

                // ─── TVTest起動後の安定化待機 ───
                // /recdelay 8 でTVTest側がチャンネルロックを待つが、それとは別に
                // ホスト側でも EpgPostLaunchStabilizeMs ぶん追加待機して、
                // 直後の他チューナー起動と CmdSetCh 発火が時間軸で重ならないようにする。
                // この待機中は WaitForExit と同じ ct で中断可能。
                var stabilizeMs = Math.Max(0, ini.EpgPostLaunchStabilizeMs);
                if (stabilizeMs > 0)
                {
                    try { await Task.Delay(stabilizeMs, ct); }
                    catch (OperationCanceledException)
                    {
                        activeLaunchRetired = await StopAndRetireActiveEpgWorkerAsync(
                            launch,
                            workerName,
                            group,
                            "post_launch_stabilize",
                            "post_launch_stabilize_cancelled").ConfigureAwait(false);
                        throw;
                    }
                }

                if (isPreRecordCheck)
                {
                    var probe = await WaitForPreRecordTargetEventAsync(
                        group,
                        tsFile,
                        launch.ProcessId,
                        workerName,
                        TimeSpan.FromSeconds(effectiveWait),
                        expectedNetworkId,
                        expectedTransportStreamId,
                        expectedServiceId,
                        expectedEventId,
                        expectedStartTime,
                        expectedEndTime,
                        ct,
                        preferredRecordingTunerName,
                        preTuneKeepWorkerUntilSafetyCeiling);

                    broadcastClock.ObserveFromTsFile(tsFile, "pre_record_epg", group.Group, displayServiceName ?? group.Targets.FirstOrDefault()?.Name, displayServiceName ?? group.Targets.FirstOrDefault()?.Name);

                    if (IsProcessAlive(launch.ProcessId))
                    {
                        Log("PRE_REC_EPG_PROBE_STOP_REQUEST", $"TS{group.TsId}",
                            $"pid={launch.ProcessId} worker={workerName} reason={(probe.TargetFound ? "target_event_seen" : "safety_ceiling_reached")} rule=epg_probe_runtime_contract");
                        RequestTvAIrEpgRecStopOrKill(launch, workerName, group, "pre_record_target_probe_done");
                    }
                    await WaitForExitAsync(launch.ProcessId, TimeSpan.FromSeconds(5), CancellationToken.None);
                    await HandleEpgProcessEndAsync(group, workerName, launch.ProcessId, ct);
                    UnregisterActiveEpgWorkerProcess(launch.ProcessId, workerName, group, "process_end");
                    activeLaunchRetired = true;

                    imported = probe.ImportedEvents > 0
                        ? probe.ImportedEvents
                        : await ParseAndStoreAsync(group, tsFile, attempt, maxAttempts, ct, isPreRecordCheck);
                    if (probe.ImportedEvents > 0)
                    {
                        try { File.Delete(tsFile); } catch { }
                        CleanupTvAIrEpgRecRuntimeFiles(launch, workerName, group, reason: "pre_record_probe_target_found");
                    }
                    else if (imported > 0)
                    {
                        CleanupTvAIrEpgRecRuntimeFiles(launch, workerName, group, reason: "pre_record_probe_parse_ok");
                    }
                }
                else
                {
                    // TvAIrEpgRec の終了を待つ
                    var processTimeout = TimeSpan.FromSeconds(effectiveWait + 8 + 30);
                    var cancelled = await WaitForExitAsync(launch.ProcessId, processTimeout, ct);

                    // キャンセル時はTvAIrEpgRecへ停止要求を送る
                    if (cancelled)
                    {
                        activeLaunchRetired = await StopAndRetireActiveEpgWorkerAsync(
                            launch,
                            workerName,
                            group,
                            "wait_for_exit",
                            "wait_for_exit_cancelled").ConfigureAwait(false);
                        Log("EPG_CAPTURE_CANCELLED", $"TS{group.TsId}",
                            $"EPG取得がキャンセルされました。TvAIrEpgRec(PID={launch.ProcessId})を終了しました。");
                        ct.ThrowIfCancellationRequested();
                        return 0;
                    }

                    await HandleEpgProcessEndAsync(group, workerName, launch.ProcessId, ct);
                    UnregisterActiveEpgWorkerProcess(launch.ProcessId, workerName, group, "process_end");
                    activeLaunchRetired = true;

                    broadcastClock.ObserveFromTsFile(tsFile, "normal_epg", group.Group, group.Targets.FirstOrDefault()?.Name, group.Targets.FirstOrDefault()?.Name);

                    // TSパース
                    imported = await ParseAndStoreAsync(group, tsFile, attempt, maxAttempts, ct, isPreRecordCheck);
                    var captureSufficient = IsNormalEpgCaptureSufficient(group, imported, isPreRecordCheck);
                    CleanupTvAIrEpgRecRuntimeFiles(launch, workerName, group, reason: captureSufficient ? "epg_parse_done" : "epg_parse_done");
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
                        "capture_scope_cancelled").ConfigureAwait(false);
                }
                throw;
            }
            finally
            {
                // チューナーを確実に解放
                lease.Dispose();
            }

            if (imported > 0)
            {
                currentRunCaptureFailures.TryRemove(group.Key, out _);
                return imported;
            }

            if (!isPreRecordCheck)
            {
                var failureReason = ClassifyImportFailure(group, tsFile);
                RecordCaptureFailure(group, failureReason, $"file={SafeLogValue(tsFile)}", attempt, maxAttempts);
                if (attempt < maxAttempts)
                {
                    Log("EPG_CAPTURE_RETRY", $"TS{group.TsId}",
                        $"reason={SafeLogValue(failureReason)} nextAttempt={attempt + 1}/{maxAttempts} group={group.Group} service={SafeLogValue(group.Targets.FirstOrDefault()?.Name)} policy=output_lifecycle_retry rule=release_contract");
                    try { await Task.Delay(Math.Max(1000, ini.EpgLaunchStaggerMs), ct); }
                    catch (OperationCanceledException) { throw; }
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
        TimeSpan waitLimit,
        CancellationToken ct)
    {
        var started = DateTime.Now;
        var observed = false;
        var nonZero = false;
        var growing = false;
        long firstSize = 0;
        long lastSize = 0;
        var firstObservedAt = DateTime.MinValue;
        var lastObservedAt = DateTime.MinValue;

        Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
            $"phase=worker_started worker={workerName} pid={processId} group={group.Group} output={SafeLogValue(tsFile)} waitLimitSec={(int)waitLimit.TotalSeconds} rule=release_contract");

        while ((DateTime.Now - started) < waitLimit)
        {
            ct.ThrowIfCancellationRequested();
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

        var phase = !observed ? "ts_file_not_created" : !nonZero ? "ts_file_zero" : "ts_file_not_growing";
        Log("EPG_OUTPUT_LIFECYCLE", $"TS{group.TsId}",
            $"phase={phase} worker={workerName} pid={processId} group={group.Group} observed={observed} nonZero={nonZero} growing={growing} firstSize={firstSize} lastSize={lastSize} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} output={SafeLogValue(tsFile)} action=continue_to_worker_exit_then_parse_or_retry rule=release_contract");
        return new EpgOutputLifecycleState(observed, nonZero, growing, firstSize, lastSize, firstObservedAt, lastObservedAt, phase);
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

    private TvTestActivityHandle? StartEpgSleepGuardBridgeIfNeeded(bool isPreRecordCheck, string normalizedScope, string normalizedDepth)
    {
        Log("EPG_RUN_ACTIVITYKEEPER", "DISABLED",
            $"result=SKIPPED targetScope={normalizedScope} runDepth={normalizedDepth} reason={(isPreRecordCheck ? "pre_record_probe_uses_individual_station_tvtest_only" : "normal_epg_uses_individual_station_tvtest_only_no_run_bridge")} policy=no_representative_activitykeeper_tvtest rule=release_contract");
        return null;

    }

    private void StopEpgSleepGuardBridgeIfStarted(TvTestActivityHandle? handle, bool isPreRecordCheck, string normalizedScope, string normalizedDepth, string reason)
    {
        var bridgePid = Interlocked.Exchange(ref epgSleepGuardBridgePid, 0);
        try { handle?.Dispose(); } catch { }
        if (!isPreRecordCheck && bridgePid > 0)
        {
            Log("EPG_SLEEPGUARD_BRIDGE", "STOP",
                $"pid={bridgePid} targetScope={normalizedScope} runDepth={normalizedDepth} reason={SafeLog(reason)} rule=release_contract");
        }
    }

    private Task StartEpgWorkerCoverageMonitorAsync(DateTime runStarted, CancellationToken ct)
    {
        LogEpgWorkerCoverage("run_start", force: true);
        return Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    LogEpgWorkerCoverage("periodic", force: false);
                    await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("EPG_WORKER_COVERAGE", "WARN",
                    $"monitor_error={SafeLog(ex.Message)} rule=epg_tuner_cooldown_contract");
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

    public async Task<int> PreemptNormalEpgWorkersForPreRecordAsync(string targetGroup, string reason, CancellationToken ct)
    {
        var group = string.IsNullOrWhiteSpace(targetGroup) ? string.Empty : targetGroup.Trim();
        var targets = activeEpgWorkerProcesses.Values
            .Where(w => string.IsNullOrWhiteSpace(group) || string.Equals(w.Group, group, StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.TsId)
            .ThenBy(w => w.Pid)
            .ToList();

        if (targets.Count == 0)
        {
            Log("PRE_REC_PRETUNE_PREEMPT_NORMAL_EPG", "EPG",
                $"result=NO_TARGET targetGroup={SafeLog(group)} reason={SafeLog(reason)} action=continue_pre_record_probe rule=release_contract");
            return 0;
        }

        var stopped = 0;
        foreach (var w in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!string.IsNullOrWhiteSpace(w.StopSignalPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(w.StopSignalPath) ?? AppContext.BaseDirectory);
                    await File.WriteAllTextAsync(w.StopSignalPath, DateTimeOffset.Now.ToString("O"), ct).ConfigureAwait(false);
                    Log("PRE_REC_PRETUNE_PREEMPT_NORMAL_EPG", $"TS{w.TsId}",
                        $"result=STOP_SIGNAL_SENT pid={w.Pid} worker={SafeLog(w.WorkerName)} group={SafeLog(w.Group)} service={SafeLog(w.ServiceName)} reason={SafeLog(reason)} stopSignal={SafeLogValue(w.StopSignalPath)} action=make_room_for_pre_record_pretune rule=release_contract");
                }
                else
                {
                    KillProcess(w.Pid);
                    Log("PRE_REC_PRETUNE_PREEMPT_NORMAL_EPG", $"TS{w.TsId}",
                        $"result=KILL_SENT pid={w.Pid} worker={SafeLog(w.WorkerName)} group={SafeLog(w.Group)} service={SafeLog(w.ServiceName)} reason={SafeLog(reason)} action=make_room_for_pre_record_pretune rule=release_contract");
                }
                stopped++;
            }
            catch (Exception ex)
            {
                Log("PRE_REC_PRETUNE_PREEMPT_NORMAL_EPG", $"TS{w.TsId}",
                    $"result=ERROR pid={w.Pid} worker={SafeLog(w.WorkerName)} group={SafeLog(w.Group)} service={SafeLog(w.ServiceName)} reason={SafeLog(reason)} error={SafeLog(ex.Message)} action=continue_pre_record_probe rule=release_contract");
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var alive = targets.Where(w => IsProcessAlive(w.Pid)).ToList();
            if (alive.Count == 0) break;
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        foreach (var w in targets)
        {
            if (!IsProcessAlive(w.Pid))
                activeEpgWorkerProcesses.TryRemove(w.Pid, out _);
        }
        LogEpgWorkerCoverage("pre_record_preempt_normal_epg", force: true);
        return stopped;
    }

    private void RegisterActiveEpgWorkerProcess(int pid, string workerName, TsGroup group, string serviceName, string phase = "attach", string? stopSignalPath = null)
    {
        activeEpgWorkerProcesses[pid] = new ActiveEpgWorkerProcess(pid, workerName, group.Group, group.TsId, serviceName, DateTime.Now, stopSignalPath);
        LogEpgWorkerCoverage(phase, force: true);
    }

    private void UnregisterActiveEpgWorkerProcess(int pid, string workerName, TsGroup group, string reason)
    {
        activeEpgWorkerProcesses.TryRemove(pid, out _);
        LogEpgWorkerCoverage($"release:{reason}", force: true);
    }

    private void LogEpgWorkerCoverage(string phase, bool force)
    {
        var nowUtc = DateTime.UtcNow;
        if (!force && nowUtc - lastEpgWorkerCoverageLogUtc < TimeSpan.FromSeconds(30))
            return;
        lastEpgWorkerCoverageLogUtc = nowUtc;

        var entries = activeEpgWorkerProcesses.Values.OrderBy(x => x.Group).ThenBy(x => x.TsId).ThenBy(x => x.Pid).ToList();
        var alive = new List<ActiveEpgWorkerProcess>();
        foreach (var e in entries)
        {
            if (IsProcessAlive(e.Pid))
            {
                alive.Add(e);
            }
            else
            {
                activeEpgWorkerProcesses.TryRemove(e.Pid, out _);
            }
        }

        var cooldowns = epgCooldownWaits.Values
            .Where(x => x.Until >= DateTime.Now)
            .OrderBy(x => x.Group).ThenBy(x => x.TsId).ThenBy(x => x.SlotName)
            .ToList();
        foreach (var expired in epgCooldownWaits.Values.Where(x => x.Until < DateTime.Now).ToList())
            epgCooldownWaits.TryRemove(expired.Key, out _);

        var summary = alive.Count == 0
            ? "-"
            : string.Join(",", alive.Take(8).Select(e => $"{e.Group}:{e.TsId}/{e.ServiceName}/pid={e.Pid}"));
        if (alive.Count > 8) summary += $",...+{alive.Count - 8}";

        var cooldownSummary = cooldowns.Count == 0
            ? "-"
            : string.Join(",", cooldowns.Take(6).Select(e => $"{e.Group}:{e.TsId}/{e.ServiceName}/slot={e.SlotName}"));
        if (cooldowns.Count > 6) cooldownSummary += $",...+{cooldowns.Count - 6}";

        var activeTasks = SnapshotActiveEpgWorkerTasks(currentEpgRunId);
        var activeWorkers = activeTasks.Count;
        var activeTaskSummary = FormatActiveEpgWorkerTaskSummary(activeTasks);
        var bridgePid = Volatile.Read(ref epgSleepGuardBridgePid);
        var bridgeAlive = bridgePid > 0 && IsProcessAlive(bridgePid);
        if (bridgePid > 0 && !bridgeAlive)
            Interlocked.CompareExchange(ref epgSleepGuardBridgePid, 0, bridgePid);

        var tvTestIconActive = alive.Count > 0 || bridgeAlive;

        Log("EPG_PROCESS_COVERAGE", "EPG",
            $"phase={phase} activeTrackedIndividual={entries.Count} aliveIndividualTvAIrEpgRec={alive.Count} cooldownWaits={cooldowns.Count} activeWorkers={activeWorkers} " +
            $"runActivityKeeper={bridgeAlive} representativePid={(bridgePid > 0 ? bridgePid.ToString() : "-")} representativeAlive={bridgeAlive} processVisible={tvTestIconActive} individualPids={FormatPidList(alive.Select(e => e.Pid))} " +
            $"individualSummary={SafeLog(summary)} cooldownSummary={SafeLog(cooldownSummary)} activeTaskSummary={SafeLog(activeTaskSummary)} " +
            "mode=tvairepgrec_worker_processes rule=epg_tuner_cooldown_contract");

        if (!tvTestIconActive && phase != "run_start" && phase != "run_end")
        {
            if (cooldowns.Count > 0)
            {
                if (force || nowUtc - lastEpgWorkerGapLogUtc >= TimeSpan.FromSeconds(30))
                {
                    lastEpgWorkerGapLogUtc = nowUtc;
                    Log("EPG_PROCESS_COOLDOWN_GAP", "INFO",
                        $"phase={phase} activeTrackedIndividual={entries.Count} aliveIndividualTvAIrEpgRec=0 cooldownWaits={cooldowns.Count} " +
                        $"note=no_tvairepgrec_worker_while_waiting_tuner_cooldown processVisible=False cooldownSummary={SafeLog(cooldownSummary)} " +
                        "action=cooldown_rechecked_no_representative_process_started rule=epg_tuner_cooldown_contract");
                }
            }
            else if (!epgRunAcceptingNewWorkers || activeWorkers <= 1)
            {
                if (force || nowUtc - lastEpgWorkerGapLogUtc >= TimeSpan.FromSeconds(30))
                {
                    lastEpgWorkerGapLogUtc = nowUtc;
                    Log("EPG_PROCESS_ENDING_GAP", "INFO",
                        $"phase={phase} activeTrackedIndividual={entries.Count} aliveIndividualTvAIrEpgRec=0 cooldownWaits=0 activeWorkers={activeWorkers} " +
                        "note=no_tvairepgrec_worker_during_epg_tail_or_last_worker_cleanup processVisible=False action=tail_cleanup_no_representative_process rule=epg_tuner_cooldown_contract");
                }
            }
            else if (force || nowUtc - lastEpgWorkerGapLogUtc >= TimeSpan.FromSeconds(30))
            {
                lastEpgWorkerGapLogUtc = nowUtc;
                Log("EPG_PROCESS_GAP", "WARN",
                    $"phase={phase} activeTrackedIndividual={entries.Count} aliveIndividualTvAIrEpgRec=0 cooldownWaits=0 activeWorkers={activeWorkers} note=epg_run_has_no_tvairepgrec_worker_at_this_moment processVisible=False " +
                    "action=inspect_worker_transition rule=epg_tuner_cooldown_contract");
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private ActiveEpgWorkerTask RegisterActiveEpgWorkerTask(TsGroup group, int pass)
    {
        var runId = currentEpgRunId;
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
        activeEpgWorkerTasks.TryRemove(state.Key, out _);
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
            if (!string.Equals(item.Value.RunId, currentRunId, StringComparison.Ordinal))
                activeEpgWorkerTasks.TryRemove(item.Key, out _);
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

    private static string GenreLabelFromCodes(string? genreCodes)
    {
        var first = string.IsNullOrWhiteSpace(genreCodes)
            ? string.Empty
            : genreCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToUpperInvariant() ?? string.Empty;
        if (first.StartsWith("0X", StringComparison.OrdinalIgnoreCase)) first = first[2..];
        if (first.Length == 0) return string.Empty;
        return first[0] switch
        {
            '0' => "ニュース/報道",
            '1' => "スポーツ",
            '2' => "情報/ワイドショー",
            '3' => "ドラマ",
            '4' => "音楽",
            '5' => "バラエティ",
            '6' => "映画",
            '7' => "アニメ/特撮",
            '8' => "ドキュメンタリー/教養",
            '9' => "劇場/公演",
            'A' => "趣味/教育",
            'B' => "福祉",
            _ => "その他"
        };
    }

    private static string SafeLog(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();


    private async Task<PreRecordProbeResult> WaitForPreRecordTargetEventAsync(
        TsGroup group,
        string tsFile,
        int processId,
        string workerName,
        TimeSpan safetyCeiling,
        ushort? expectedNetworkId,
        ushort? expectedTransportStreamId,
        ushort? expectedServiceId,
        ushort? expectedEventId,
        DateTime? expectedStartTime,
        DateTime? expectedEndTime,
        CancellationToken ct,
        string? preferredRecordingTunerName = null,
        bool preTuneKeepWorkerUntilSafetyCeiling = false)
    {
        var started = DateTime.Now;
        var deadline = started.Add(safetyCeiling);
        var pollNo = 0;
        var lastLength = 0L;
        var lastImported = 0;

        Log("PRE_REC_EPG_PROBE_WAIT", $"TS{group.TsId}",
            $"start worker={workerName} pid={processId} safetyCeilingSec={(int)safetyCeiling.TotalSeconds} expectedNid={(expectedNetworkId?.ToString() ?? "-")} expectedTsid={(expectedTransportStreamId?.ToString() ?? "-")} expectedSid={(expectedServiceId?.ToString() ?? "-")} expectedEventId={(expectedEventId?.ToString() ?? "-")} expectedStart={(expectedStartTime?.ToString("MM/dd HH:mm:ss") ?? "-")} preferredTuner={SafeLogValue(preferredRecordingTunerName)} policy=stop_as_soon_as_target_event_seen_and_pretune_same_recording_tuner rule=release_contract");

        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsProcessAlive(processId)) break;

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
                ct);
            lastImported = Math.Max(lastImported, probe.ImportedEvents);

            if (probe.TargetFound)
            {
                if (preTuneKeepWorkerUntilSafetyCeiling && !string.IsNullOrWhiteSpace(preferredRecordingTunerName))
                {
                    Log("PRE_REC_EPG_PROBE_EVENT_FOUND", $"TS{group.TsId}",
                        $"result=FOUND worker={workerName} pid={processId} poll={pollNo} fileBytes={fi.Length} imported={probe.ImportedEvents} event={probe.EventSummary} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} action=transition_to_record_pretune_hold preferredTuner={SafeLogValue(preferredRecordingTunerName)} rule=release_contract");
                    Log("PRE_REC_PRETUNE_STATE", $"TS{group.TsId}",
                        $"state=record_prepare display=録画準備中 worker={workerName} pid={processId} preferredTuner={SafeLogValue(preferredRecordingTunerName)} holdUntil={deadline:MM/dd HH:mm:ss} reason=target_event_seen_same_worker_no_relaunch rule=release_contract");
                    var foundProbe = probe with { TargetFound = true };
                    while (DateTime.Now < deadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!IsProcessAlive(processId)) break;
                        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                        catch (OperationCanceledException) { throw; }
                    }
                    return foundProbe;
                }

                Log("PRE_REC_EPG_PROBE_EVENT_FOUND", $"TS{group.TsId}",
                    $"result=FOUND worker={workerName} pid={processId} poll={pollNo} fileBytes={fi.Length} imported={probe.ImportedEvents} event={probe.EventSummary} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} action=stop_tvtest_now rule=epg_probe_runtime_contract");
                return probe with { TargetFound = true };
            }
        }

        Log("PRE_REC_EPG_PROBE_EVENT_NOT_FOUND", $"TS{group.TsId}",
            $"result=NOT_FOUND worker={workerName} pid={processId} imported={lastImported} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} action=fall_back_to_original_reservation_time rule=epg_probe_runtime_contract");
        return new PreRecordProbeResult(false, lastImported, "-");
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
                    Genre = GenreLabelFromCodes(e.GenreCodes),
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

            if (events.Count > 0)
            {
                try { store.Upsert(events); } catch { }
            }

            Log("PRE_REC_EPG_PARSE", $"TS{group.TsId}",
                $"result=OK events={events.Count} {epg.StatsLine} rule=release_contract");

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
                return new PreRecordProbeResult(true, events.Count,
                    $"{target.NetworkId}/{target.TransportStreamId}/{target.ServiceId}/{target.EventId} {target.Start:MM/dd HH:mm:ss}〜{target.End:MM/dd HH:mm:ss} {SafeLog(target.Title)}");
            }
            return new PreRecordProbeResult(false, events.Count, "-");
        }
        catch
        {
            return new PreRecordProbeResult(false, 0, "-");
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

    private async Task HandleEpgProcessEndAsync(TsGroup group, string workerName, int processId, CancellationToken ct)
    {
        await epgEndingGate.WaitAsync(ct);
        try
        {
            Log("EPG_ENDING_GATE", $"TS{group.TsId}",
                $"enter worker={workerName} pid={processId} group={group.Group} note=serialize_tvtest_exit_release");
            Log("EPG_CAPTURE_END", $"TS{group.TsId}", $"TvAIrEpgRec終了確認。PID={processId}");

            // A very small settle interval after TVTest has exited. This is intentionally
            // shorter than tuner cooldown and only separates Close/route side effects.
            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { throw; }

            Log("EPG_ENDING_GATE", $"TS{group.TsId}",
                $"exit worker={workerName} pid={processId}");
        }
        finally
        {
            epgEndingGate.Release();
        }
    }

    private const int NormalEpgTimelineSafetySeconds = 600;
    private const int NormalEpgNearReservationHardStopSeconds = 600;

    private void RefreshRecordingTimelineGateForNormalEpgWorker(string group)
    {
        var normalizedGroup = NormalizeEpgTargetGroup(group);
        var now = DateTime.Now;
        var horizon = now.AddSeconds(NormalEpgTimelineSafetySeconds);
        try
        {
            var next = reservationStore.GetByStatus(ReservationStatus.Scheduled)
                .Where(r => r.IsEnabled && r.Source != ReservationSource.Epg)
                .Select(r => new { Reservation = r, Group = ResolveReservationGroupForTimeline(r), DueAt = r.StartTime.AddSeconds(-ini.PreStartMarginSeconds) })
                .Where(x => string.Equals(x.Group, normalizedGroup, StringComparison.OrdinalIgnoreCase) && x.DueAt <= horizon)
                .OrderBy(x => x.DueAt)
                .ThenBy(x => x.Reservation.Id)
                .FirstOrDefault();

            if (next is null) return;

            var blockAfter = next.DueAt.AddSeconds(-NormalEpgTimelineSafetySeconds);
            var protectUntil = next.Reservation.StartTime.AddSeconds(30);
            RecordingLifecycleGate.RegisterRecordingTimelineBoundary(
                normalizedGroup,
                next.DueAt,
                protectUntil,
                "recording_timeline_due",
                $"R{next.Reservation.Id}",
                $"service={SafeLogValue(next.Reservation.ServiceName)} title={SafeLogValue(next.Reservation.Title)}",
                blockAfter);
        }
        catch (Exception ex)
        {
            Log("EPG_RECORDING_TIMELINE_GATE", "WARN",
                $"result=REFRESH_SKIPPED group={normalizedGroup} reason={ex.GetType().Name} rule=release_contract");
        }
    }

    private static string ResolveReservationGroupForTimeline(Reservation r)
        => (!string.IsNullOrWhiteSpace(r.ChannelArgument) && r.ChannelArgument.Contains("/chspace", StringComparison.OrdinalIgnoreCase))
            ? "BSCS"
            : "GR";



    private static int ResolveEpgLogicalAcquireWaitMs(DateTime plannedEnd, bool isPreRecordCheck)
    {
        if (isPreRecordCheck) return 3000;

        // 通常EPGは「空きがなければすぐ諦める」ではなく、取得runの残り予算内で論理スロットを待つ。
        // 物理DID構成・BonDriver終了速度・外部TVTest残存時間への依存を減らすため、
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
        string? preferredRecordingTunerName = null)
    {
        var maxWaitMs = ResolveEpgLogicalAcquireWaitMs(plannedEnd, isPreRecordCheck);
        const int pollMs = 500;
        var waitedMs = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!isPreRecordCheck)
                RefreshRecordingTimelineGateForNormalEpgWorker(group.Group);

            DateTime suppressUntil;
            string suppressReason;
            string suppressOwner;
            string suppressLabel;
            var suppressesNormalEpg = isPreRecordCheck
                ? RecordingLifecycleGate.IsEpgSuppressed(group.Group, out suppressUntil, out suppressReason, out suppressOwner, out suppressLabel)
                : RecordingLifecycleGate.IsNormalEpgSuppressed(group.Group, plannedEnd, out suppressUntil, out suppressReason, out suppressOwner, out suppressLabel);
            if (suppressesNormalEpg)
            {
                currentRunBlockedGroups[group.Key] = string.IsNullOrWhiteSpace(suppressReason) ? "recording_preempt_lock" : suppressReason;
                if (RecordingLifecycleGate.ShouldLogEpgSuppression(group.Group, suppressUntil, suppressReason, suppressOwner))
                {
                    Log("EPG_SUPPRESSED_BY_REC_DUE", $"TS{group.TsId}",
                        $"{workerName}: EPG新規起動を抑止 group={group.Group} until={suppressUntil:MM/dd HH:mm:ss} " +
                        $"owner={suppressOwner} reason={suppressReason} label={suppressLabel} pass={group.Group}-{group.TsId} " +
                        "rule=release_contract");
                }
                return null;
            }

            TunerLease? lease = null;
            if (!string.IsNullOrWhiteSpace(preferredRecordingTunerName))
            {
                lease = tunerPool.AcquireForEpgByName(preferredRecordingTunerName, group.Group, plannedEnd, liveTvTestKeys, "pre_record_pretune_same_recording_tuner");
                if (lease is not null)
                {
                    Log("PRE_REC_PRETUNE_TUNER_ACQUIRE", $"TS{group.TsId}",
                        $"result=OK worker={workerName} preferredTuner={preferredRecordingTunerName} actualTuner={lease.Name} did={lease.Did} group={group.Group} targetSids=[{string.Join(',', group.Targets.Select(t => t.ServiceId).OrderBy(x => x))}] policy=same_actual_tuner_before_recording rule=release_contract");
                }
                else if (waitedMs == 0)
                {
                    Log("PRE_REC_PRETUNE_TUNER_ACQUIRE", $"TS{group.TsId}",
                        $"result=PREFERRED_BUSY worker={workerName} preferredTuner={preferredRecordingTunerName} group={group.Group} action=wait_then_use_free_epg_tuner_if_still_busy rule=release_contract");
                }
            }

            lease ??= tunerPool.AcquireForEpg(group.Group, plannedEnd, liveTvTestKeys);
            if (lease is not null)
            {
                if (waitedMs > 0)
                {
                    Log("EPG_TUNER_ACQUIRE_WAIT_OK", $"TS{group.TsId}",
                        $"{workerName}: チューナー空き待ち後に確保しました。waitMs={waitedMs} tuner={lease.Name} preferredTuner={SafeLogValue(preferredRecordingTunerName)}");
                }
                else if (!string.IsNullOrWhiteSpace(preferredRecordingTunerName) && !string.Equals(lease.Name, preferredRecordingTunerName, StringComparison.OrdinalIgnoreCase))
                {
                    Log("PRE_REC_PRETUNE_TUNER_ACQUIRE", $"TS{group.TsId}",
                        $"result=USE_FREE_EPG_TUNER worker={workerName} preferredTuner={preferredRecordingTunerName} actualTuner={lease.Name} did={lease.Did} group={group.Group} reason=preferred_not_available policy=recording_priority_no_block rule=release_contract");
                }
                return lease;
            }

            if (waitedMs >= maxWaitMs)
            {
                Log("EPG_TUNER_BUSY", $"TS{group.TsId}",
                    $"{workerName}: EPG用途の論理チューナーが確保できませんでした（録画/視聴/外部TVTest保護を優先）。waitMs={waitedMs} policy=logical_resource_queue");
                return null;
            }

            if (waitedMs == 0)
            {
                Log("EPG_TUNER_WAIT", $"TS{group.TsId}",
                    $"{workerName}: EPG用途の論理チューナー待機を開始します。maxWaitMs={maxWaitMs} policy=logical_resource_queue");
            }

            await Task.Delay(pollMs, ct);
            waitedMs += pollMs;
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
                    $"boundaryStatus={SafeLogValue(a.BoundaryStatus)} lang={SafeLogValue(a.Iso639LanguageCode)} eventNameLength={a.EventNameLength} eventNameBytesLen={a.EventNameBytesLength} eventNameBytesHex={SafeLogValue(a.EventNameBytesHex)} " +
                    $"decodeRoute={SafeLogValue(a.DecodeRoute)} decodeStatus={SafeLogValue(a.DecodeStatus)} decodedTitle={SafeLogValue(TrimLog(a.DecodedTitle))} decodedTitleLength={a.DecodedTitleLength} " +
                    $"textLength={a.TextLength} textBytesLen={a.TextBytesLength} textBytesHexHead={SafeLogValue(a.TextBytesHexHead)} decodedTextHead={SafeLogValue(TrimLog(a.DecodedTextHead))} " +
                    $"emptyReason={SafeLogValue(a.EmptyReason)} rule=release_contract");
            }

            var completenessLines = epg.SectionStatuses
                .Where(st => targetSidSet.Count == 0 || targetSidSet.Contains(st.ServiceId))
                .OrderBy(st => st.ServiceId).ThenBy(st => st.TableId)
                .Take(40)
                .Select(st => $"SID={st.ServiceId} table=0x{st.TableId:X2} sec={st.SeenSectionCount}/{st.ExpectedSectionCount} seg={st.SegmentSeenTotal}/{st.SegmentExpectedTotal} missingSegs=[{string.Join(",", st.MissingSegments)}]")
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

            var upsertedCount = store.Upsert(rawEvents);
            var mergeStats = store.LastUpsertMergeStats;
            var mergeResult = mergeStats.IncomingRawShortBlankExistingRawShortPresent == 0 && mergeStats.IncomingRawExtendedBlankExistingRawExtendedPresent == 0
                ? "OK"
                : "CURRENT_CAPTURE_OVERWRITE";
            Log("EPG_EVENT_DESCRIPTOR_MERGE_CONTRACT", $"TS{group.TsId}",
                $"result={mergeResult} purpose={purpose} group={group.Group} incoming={mergeStats.Incoming} existingRows={mergeStats.ExistingRows} " +
                $"incomingRawShortPresent={mergeStats.IncomingRawShortPresent} incomingRawExtendedPresent={mergeStats.IncomingRawExtendedPresent} " +
                $"incomingRawShortBlankExistingRawShortPresent={mergeStats.IncomingRawShortBlankExistingRawShortPresent} incomingRawExtendedBlankExistingRawExtendedPresent={mergeStats.IncomingRawExtendedBlankExistingRawExtendedPresent} incomingRawContentBlankExistingRawContentPresent={mergeStats.IncomingRawContentBlankExistingRawContentPresent} " +
                $"incomingExtendedOnlyExistingTitle={mergeStats.IncomingExtendedOnlyExistingRawShortPresent} sameEventExtendedMergedPreservingExistingTitle=0 " +
                $"tableMetadataPreservedForExistingTitle=0 policy=current_capture_raw_descriptor_overwrites_existing_db_raw_descriptors titleSynthesis=none bodyToTitlePromotion=none dbSchemaMutation=none rule=release_contract");

            var staleStats = store.RetireStaleEventsForCapturedScope(rawEvents);
            var staleScopeStart = staleStats.ScopeStart.HasValue ? staleStats.ScopeStart.Value.ToString("O") : "-";
            var staleScopeEnd = staleStats.ScopeEnd.HasValue ? staleStats.ScopeEnd.Value.ToString("O") : "-";
            Log("EPG_STALE_EVENT_RETIRE_CONTRACT", $"TS{group.TsId}",
                $"result=OK purpose={purpose} group={group.Group} services={staleStats.Services} incoming={staleStats.IncomingEvents} deleted={staleStats.DeletedRows} " +
                $"scopeStart={staleScopeStart} scopeEnd={staleScopeEnd} " +
                $"policy=captured_service_time_range_raw_event_identity titleSynthesis=none bodyToTitlePromotion=none reservationTitleBorrow=none startupDbClear=none dbSchemaMutation=none rule=release_contract");

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
        catch (Exception ex)
        {
            Log("EPG_PARSE_FAIL", $"TS{group.TsId}",
                $"result=ERROR purpose={(isPreRecordCheck ? "pre_record_check" : "normal_epg_capture")} error={ex.GetType().Name}:{SafeLogValue(ex.Message)} rule=release_contract");
            return 0;
        }
    }


    // ─── TS グループ構築 ──────────────────────────────────────────

    private static string NormalizeTargetScope(string? value)
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

    private static List<TsGroup> FilterGroupsByScope(IReadOnlyList<TsGroup> groups, string scope)
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

    private static bool IsScheduleTitleTable(byte tableId) => tableId is 0x50 or 0x51;
    private static bool IsScheduleBodyTable(byte tableId) => tableId is >= 0x58 and <= 0x5F;
    private static bool HasRawShort(EpgEvent e) => !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex);
    private static bool HasRawShort(ParsedEpgEvent e) => !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex);
    private static bool HasRawShort(EpgEventObservation e) => !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex);
    private static bool HasRawExtended(EpgEvent e) => !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex);
    private static bool HasRawExtended(ParsedEpgEvent e) => !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex);
    private static bool HasRawExtended(EpgEventObservation e) => !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex);





    private static byte ExpectedTitleTableForBodyTable(byte bodyTableId)
        => bodyTableId == 0x59 ? (byte)0x51 : (byte)0x50;





    private static byte ExpectedTitleTableForBodyTableByDelta(byte bodyTableId)
        => bodyTableId >= 0x58 && bodyTableId <= 0x5F ? (byte)(bodyTableId - 0x08) : ExpectedTitleTableForBodyTable(bodyTableId);

    private static bool IsScheduleShortCarrierCandidateTable(byte tableId) => tableId is >= 0x50 and <= 0x57;

    private static int ScheduleSegmentOf(byte sectionNumber) => sectionNumber / 8;








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
                Genre = GenreLabelFromCodes(e.GenreCodes),
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

    private List<TsGroup> BuildGroups(IReadOnlyList<ChannelTarget> targets)
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
                Log("EPG_CH2_TS_SCOPE", $"TS{g.Key.TransportStreamId}",
                    $"result={bundleStatus} group={g.Key.Group} nid={g.Key.OriginalNetworkId} tsid={g.Key.TransportStreamId} chspace={g.Key.ResolvedSpace} chi={g.Key.ResolvedChannelIndex} " +
                    $"ch2ActiveSameTsServices={ch2ActiveSameTsServices} epgTargetSidCount={targetList.Count} targetSids=[{targetSids}] ch2Lines=[{ch2Lines}] " +
                    $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC note=epg_uses_ch2_bundle_before_tvairepgrec_job rule=release_contract");
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

        Log("EPG_CH2_SCOPE_SUMMARY", "EPG",
            $"groups={groups.Count} services={groups.Sum(g => g.Targets.Count)} warnings={groups.Count(g => (g.Targets.Count == 0 ? 0 : g.Targets.Max(t => t.SameTransportServiceCount)) > g.Targets.Count)} " +
            $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
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
            ? Path.Combine(ResolveDataDir(), "ts-rec")
            : settings.TsRecordDirectory;
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        return Path.Combine(dir, $"epg_{group.Group}_{group.TsId}_{ts}.ts");
    }

    private string ResolveDataDir()
    {
        var raw = string.IsNullOrWhiteSpace(ini.DataDirectory) ? "data" : ini.DataDirectory.Trim();
        return Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw));
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
    /// 正常終了・タイムアウトの場合 false、外部キャンセル(ct)の場合 true を返す。
    /// </summary>
    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    if (p.HasExited) return false;
                }
                catch (ArgumentException) { return false; } // プロセスが既に終了
                await Task.Delay(500, cts.Token);
            }
        }
        catch (OperationCanceledException) { /* タイムアウト or 外部キャンセル */ }

        // タイムアウト(timeout経過)は正常扱い、外部キャンセルのみ true
        return ct.IsCancellationRequested;
    }

    /// <summary>対象プロセスを強制終了する。</summary>
    private static void KillProcess(int pid)
    {
        try
        {
            using var tunerDeviceAccess = TunerDeviceAccessGate.Enter($"EPG_KILL PID={pid}");
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            if (!p.HasExited) p.Kill();
        }
        catch { /* 既に終了している場合は無視 */ }
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

internal sealed record ActiveEpgWorkerProcess(
    int Pid,
    string WorkerName,
    string Group,
    ushort TsId,
    string ServiceName,
    DateTime StartedAt,
    string? StopSignalPath = null);

internal sealed record ActiveEpgWorkerTask(
    string Key,
    string RunId,
    int Pass,
    string Group,
    ushort TsId,
    string ServiceName,
    DateTime StartedAt);

internal sealed record EpgCooldownWait(
    string Key,
    string WorkerName,
    string Group,
    ushort TsId,
    string ServiceName,
    string SlotName,
    DateTime StartedAt,
    DateTime Until);


internal sealed record TsGroup(
    string Key,
    string Group,
    ushort TsId,
    string BonDriverFileName,
    IReadOnlyList<ChannelTarget> Targets);

// ─── 公開型 ──────────────────────────────────────────────────────

internal sealed record PreRecordProbeResult(bool TargetFound, int ImportedEvents, string EventSummary);

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
    public string CancelRoute { get; init; } = "WidgetOrTray";
}
