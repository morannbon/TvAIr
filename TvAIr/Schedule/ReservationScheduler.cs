using System.Diagnostics;
using System.Collections.Concurrent;
using System.Management;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using TvAIr.Core;
using TvAIr.Tuner;
using TvAIr.Channel;
using TvAIr.Epg;
using TvAIr.Epg.Projection;
using System.Text.RegularExpressions;

using TvAIr.Plugin;
using TvAIrPlugin;

namespace TvAIr.Schedule;

/// <summary>
/// 予約録画の実行エンジン（BackgroundService）。
///
/// 予約・録画ライフサイクルを共通割当ルートとTvAIrEpgRec録画本線で実行する。
/// 通常Tickとは別に録画dueとチェーン境界を高頻度監視し、確定した物理Tuner所有権を維持する。
///
/// 競合処理:
///   空きあり                         → そのまま録画開始
///   実行中normal EPG waveと競合      → Scheduled+IsConflictedのまま保持し、wave終端後の共通再評価へ戻す
///   その他の録画容量競合             → 既存の競合終端規則に従う
///   視聴Role                         → 録画用Tunerへ流用せず保護する
/// normal EPGを録画要求のために停止・縮退・別Tuner救済してはならない。
/// </summary>
public sealed 
class ReservationScheduler : BackgroundService
{

private readonly ReservationStore _store;
    private readonly TunerPool _tunerPool;
    private readonly ApplicationOperationGate _applicationGate;
    private readonly IniSettingsService _ini;
    private readonly IReadOnlyList<TunerProfile> _tunerProfiles;
    private readonly LogRepository _log;
    private readonly TaskSchedulerService _taskSvc;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly ChannelFileLoader _channelLoader;
    private readonly IProgramEventSource _programEvents;
    private readonly EpgCapture _epgCapture;
    private readonly TvTestActivityKeeper _tvTestActivity;
    private readonly ChainDirectRecorderSessionRegistry _chainSessionRegistry;
    private readonly ServiceLogoStore _serviceLogoStore;
    private readonly UserEventLogService _userEvents;
    private readonly PluginTypedEventHub _typedEvents;
    private readonly RecordingResultStore _recordingResults;
    private readonly NormalEpgWaveOccupation _normalEpgWaveOccupation;
    private readonly ExternalTunerLeaseService _externalTuners;

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // 現在録画中の管理（reservationId → 録画セッション）
    private readonly Dictionary<int, RecordingSession> _activeSessions = new();
    // RESIDUAL_RECORDING_WORKER_GUARD_INVARIANT:
    // stop/kill後も同一worker identityが生存する場合、そのDIDを再利用してはならない。
    // PID番号だけではなくManagedProcessIdentityで追跡し、identity消滅時だけ隔離解除する。
    private readonly ConcurrentDictionary<string, ResidualRecordingWorkerQuarantine> _residualRecordingWorkers = new(StringComparer.OrdinalIgnoreCase);
    // RECORDING_RESOURCE_RELEASE_GUARD_INVARIANT:
    // worker消滅とPool lease identity解放は別契約である。workerが消えてDBを戻せても、
    // 元のPoolLeaseId＋OccupancyGenerationがスロットに残る間は同じgroup＋DIDを再利用しない。
    // Pool lifecycle停止やleaseオブジェクトのDispose状態では解除せず、Pool正本のidentity不一致だけを解除条件とする。
    private readonly ConcurrentDictionary<string, RecordingResourceReleaseQuarantine> _recordingResourceReleaseQuarantines = new(StringComparer.OrdinalIgnoreCase);
    // 手動停止要求は予約ID単位で一回だけ受理し、非同期停止完了まで保持する。
    private readonly HashSet<int> _manualStopRequests = new();
    private readonly Dictionary<int, string> _recordingTerminalFailureReasons = new();
    private readonly ConcurrentDictionary<int, int> _recordingStartupZeroByteReloadAttempts = new();
    // POWER_SUSPEND_RECORDING_IDENTITY_INVARIANT:
    // Resume時点で録画中という理由だけでSuspend跨ぎと判定してはならない。
    // Suspendイベント時点に実在した正式Recording sessionをOperationId/worker/Pool lease identityごとsnapshotし、
    // Resumeではその同一sessionだけを中断回復対象にする。Suspend後に開始した録画は絶対に巻き込まない。
    private readonly object _sessionGate = new();
    // BonDriverのClose/Open衝突を避けるため、録画停止処理は必ず1本ずつ直列化する。
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _stopGatesByTuner = new(StringComparer.OrdinalIgnoreCase);
    // チェーン境界は専用の物理解放証拠を使用し、通常停止経路と混同しない。
    // Bridge継続/ファイル切替が実装されるまでは、同じ境界での重複抑止にも使う。
    private readonly HashSet<int> _chainBoundaryNormalStopSuppressed = new();
    private readonly Dictionary<string, ChainBoundaryExecutionEntry> _chainBoundaryExecutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _chainBoundaryLastWaitBucket = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _recordingDueGate = new(1, 1);
    private readonly ConcurrentDictionary<int, RecordingStartOwnerEntry> _recordingStartInProgress = new();
    // チェーン物理解放callbackが通常due開始より後から到着した場合、別開始taskを増やさず
    // 予約単位single-flightへ同一境界世代の証拠を合流させる。固定待機や二重開始は禁止する。
    private readonly ConcurrentDictionary<int, ChainReleaseStartEvidence> _pendingChainReleaseStartEvidence = new();
    // CHAIN_EXECUTION_ORDER_INVARIANT:
    // CHAIN_DEVELOPER_APPROVAL_REQUIRED — この実行順序・同一物理Tuner固定・stop/restart handoff・後続完全優先は、
    // TvAIr開発者の明示承認なしに変更しない。局所修正・整理・最適化でも境界結果を変える変更は禁止する。
    // 録画worker投入は設定グループごとに最大1件を同時単位とし、同一グループの追加投入は1秒間隔とする。
    // チェーン境界まで10秒以下なら、未開始分を間に合わせる最終措置として通常Cadenceを迂回する。
    // 起動完了時間やPC性能を間隔へ加算してはならず、全放送波を単一ゲートで直列化してはならない。
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _recordingLaunchAdmissionGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _recordingLaunchNextAllowedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, byte> _activePreRecordEpgEntries = new();
    // PRE_REC_TIME_FOLLOW_EVIDENCE_INVARIANT:
    // PreRecで実際に観測した同一EventIdentityの時刻は、録画開始後に古いProgramGuide投影で巻き戻してはならない。
    // 予約ID単位で保持し、terminal到達後はpruneする。録画中Followはこの証拠より後ろへの延長だけを許可する。
    private readonly ConcurrentDictionary<int, RecordingTimeFollowEvidence> _recordingTimeFollowEvidence = new();
    private readonly AsyncLocal<string?> _powerResumeCycleContext = new();
    private readonly object _recordingAfterActionGate = new();
    private CancellationTokenSource? _recordingAfterActionCts;
    private long _recordingAfterActionGeneration;


    private enum ChainBoundaryExecutionState
    {
        NotStarted,
        Executing,
        Succeeded,
        RetryableFailed,
        TerminalFailed
    }

    private sealed record ChainReleaseStartEvidence(
        string BoundaryKey,
        int BoundaryAttempt,
        int PredecessorId,
        int SuccessorId,
        long SuccessorDataVersion,
        int? ChainRootId,
        string ReleasedTuner,
        string PredecessorActualTuner,
        int PredecessorProcessId,
        DateTime ReleasedAt);

    private sealed class RecordingStartOwnerEntry
    {
        public required string OwnerId { get; init; }
        public required string Trigger { get; init; }
        public required DateTime AcquiredAt { get; init; }
        public required int ProcessId { get; init; }
        public object JoinEvidenceGate { get; } = new();
        public bool AcceptingJoinEvidence { get; set; } = true;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed record RecordingAbortCleanupResult(
        bool WorkerIdentityGone,
        bool ActivityHandleReleased,
        bool LeaseReleaseCompleted)
    {
        public bool Complete => WorkerIdentityGone && ActivityHandleReleased && LeaseReleaseCompleted;
    }

    private sealed record ResidualRecordingWorkerQuarantine(
        int ReservationId,
        string Group,
        string Did,
        string TunerName,
        ManagedProcessIdentity WorkerIdentity,
        DateTime DetectedAt);

    private sealed record RecordingResourceReleaseQuarantine(
        int ReservationId,
        string Group,
        string Did,
        string TunerName,
        TunerLease Lease,
        DateTime DetectedAt);

    private sealed record RecordingTimeFollowEvidence(
        ushort NetworkId,
        ushort TransportStreamId,
        ushort ServiceId,
        ushort EventId,
        DateTime Start,
        DateTime End,
        DateTime ObservedAt,
        string LastSuppressedProjectionKey);

    private sealed class ChainBoundaryExecutionEntry
    {
        public required string Key { get; init; }
        public required int PredecessorId { get; init; }
        public required int SuccessorId { get; init; }
        public ChainBoundaryExecutionState State { get; set; } = ChainBoundaryExecutionState.NotStarted;
        public int Attempt { get; set; }
        public DateTime NextRetryAt { get; set; }
        public DateTime LastUpdatedAt { get; set; }
        public string LastReason { get; set; } = string.Empty;
        public ChainReleaseStartEvidence? ReleaseEvidence { get; set; }
    }

    private const int PollingIntervalMs = 10_000;
    // チェーン境界は通常10秒Tickを待たず、後続完全性のため高優先度で監視する。
    private const int ChainBoundaryMonitorIntervalMs = 500;
    // 通常録画dueも録画前EPG確認や10秒Tickに待たせない。
    private const int RecordingDueMonitorIntervalMs = 500;
    // 録画前EPG確認は設定された5/10/15/20分をAdmission horizonとして使う。
    // その時間を使い切る権利ではない。本録画dueと他録画占有から逆算したhard deadlineを必ず優先する。
    private const int PreRecordEpgStopBeforeRecordingDueSeconds = 30;
    private const int PreRecordEpgMinimumProbeSeconds = 8;
    // USER_CHAIN_PROTECTED_PATH: chain-root PreRecの保護上限。チェーン挙動は開発者承認なしに変更しない。
    private const int ProtectedUserChainPreRecordProbeCeilingSeconds = 90;
    private const int ViewingPreemptCountdownSec = 30;
    private const int RecordingLaunchWaitForFreeTunerMs = 5000;
    // release_contract: 本番録画の直前と録画開始競合時は、同一放送波のEPG新規起動を共通ゲートで止める。
    private const int RecordingDueEpgSuppressBeforeAfterSec = 180;
    private const int RecordingTimelineEpgGateSafetySeconds = 320;
    private const int ChainRequestedTunerWaitMs = 15000;
    // Tuner再利用可否はworker終了・デバイス解放・lease解放・occupancy generation更新の証拠で決定する。
    private const int RecordingLaunchCadenceMs = 1_000;
    private const int ChainEmergencyLaunchWindowSeconds = 10;
    private const int TvAIrEpgRecStopTimeoutMs = 4_000;
    private const int TvAIrEpgRecStopGracefulExitWaitMs = 15_000;
    private const int ChainTvAIrEpgRecStopGracefulExitWaitMs = 5_000;
    // 録画プロセスが生存していてもTSファイルが増えない状態を、予定終了まで放置しない。
    // 閾値は名前付き定義で管理し、録画開始直後・終了境界直前は誤検知を避ける。
    private const int RecordingFileGrowthInitialGraceSec = 180;
    private const int RecordingFileGrowthStallSec = 120;
    private const int RecordingFileGrowthPlannedEndGuardSec = 30;
    // BonDriver が OpenTuner/SetChannel 成功後も GetTs を返さない初期化失敗を救命する。
    // 同一プロセス内 SetChannel retry では復帰しない実例があるため、TvAIr側で worker/BonDriver を一度閉じて同じ予約を再起動する。
    private const int RecordingStartupZeroByteReloadMaxAttempts = 2;
    // ChainReservationContract.CHAIN_DEVELOPER_APPROVAL_REQUIRED: 以下の実行境界契約変更は開発者の明示承認が必須。
    // CHAIN_EXECUTION_ORDER_INVARIANT — 変更禁止:
    // この30秒欠落は、共通優先順位と通常マージンで競合判定を通過し、同一物理Tunerへ割当済みの
    // チェーンを実行するときだけ適用する。予約段階の容量確保、競合回避、通常録画マージン短縮には使用しない。
    // 欠損型チェーンは後番組完全優先。録画中の時間追従結果を正本として、後続放送開始30秒前に前段を一斉停止する。
    // 予約作成時刻や固定待機から停止時刻を決めてはならず、対象を逐次停止してはならない。
    private const int ChainFrontCutBeforeSuccessorStartSeconds = 30;

    // Wakeタスク更新の間引き（毎ポーリングごとは不要、1分ごとに更新）
    private int _wakeUpdateCounter;
    private string _lastRecordingTimelineGateSignature = string.Empty;
    private const int WakeUpdateIntervalTicks = 6; // 10秒×6 = 60秒

    // 録画失敗の原因不明化を防ぐため、予約の存在・状態・割当・録画開始を定期監査する。
    // ログ量を抑えるため通常は1分間隔。ただし録画開始対象があるTickでは即時ログを出す。
    private int _reservationAuditCounter;
    private const int ReservationAuditIntervalTicks = 6; // 10秒×6 = 60秒

    // 録画timeline候補は10秒Tickで毎回同じ結果を出さず、候補集合が変わった時だけ監査ログを残す。
    // この集合はPreRec安全境界と開始前Admissionの正本更新に使い、実行中normal/定時EPGの停止には使わない。

    // チューナー再評価の間引き。10秒ごとの全件再評価をやめ、
    // 「起動直後 / 予約接近時 / 状態変化時 / 1分ごと」に限定して負荷を下げる。
    private int _allocationReevaluateCounter;
    private const int AllocationReevaluateIntervalTicks = 6; // 10秒×6 = 60秒
    private static readonly TimeSpan NearStartReevaluateWindow = TimeSpan.FromMinutes(2);
    private bool _forceAllocationReevaluate = true;
    private bool _hadNearStartReservationOnPreviousTick;
    private bool _skipNextTickAllocationReevaluate = true;
    private string _lastPastTerminalAuditSignature = string.Empty;
    private DateTime _lastPastTerminalAuditLogUtc = DateTime.MinValue;

    // 起動復旧時のファイル成長判定は、録画継続中workerの見落としを防ぐための観測窓。
    // 処理待ちではなく2点間のサイズ差を測る契約であり、非同期・キャンセル可能にする。

    public ReservationScheduler(
        ReservationStore store,
        TunerPool tunerPool,
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> tunerProfiles,
        LogRepository log,
        TaskSchedulerService taskSvc,
        ReservationAllocationRouteService allocationRoute,
        ChannelFileLoader channelLoader,
        IProgramEventSource programEvents,
        EpgCapture epgCapture,
        TvTestActivityKeeper tvTestActivity,
        ChainDirectRecorderSessionRegistry chainSessionRegistry,
        ServiceLogoStore serviceLogoStore,
        UserEventLogService userEvents,
        PluginTypedEventHub typedEvents,
        RecordingResultStore recordingResults,
        NormalEpgWaveOccupation normalEpgWaveOccupation,
        ExternalTunerLeaseService externalTuners,
        ApplicationOperationGate applicationGate)
    {
        _store         = store;
        _tunerPool     = tunerPool;
        _ini           = ini;
        _tunerProfiles = tunerProfiles;
        _log           = log;
        _taskSvc       = taskSvc;
        _allocationRoute = allocationRoute;
        _channelLoader = channelLoader;
        _programEvents = programEvents;
        _epgCapture = epgCapture;
        _tvTestActivity = tvTestActivity;
        _chainSessionRegistry = chainSessionRegistry;
        _serviceLogoStore = serviceLogoStore;
        _userEvents = userEvents;
        _typedEvents = typedEvents;
        _recordingResults = recordingResults;
        _normalEpgWaveOccupation = normalEpgWaveOccupation;
        _externalTuners = externalTuners;
        _applicationGate = applicationGate;
    }

    // ─── BackgroundService ───────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        

        _log.Add("Scheduler", "Init", "ReservationScheduler 開始。");
        _log.Add("Scheduler", "Init",
            $"設定確認: PseudoContinuousRecording={_ini.PseudoContinuousRecording} " +
            $"ChainFrontCut={ChainFrontCutBeforeSuccessorStartSeconds}s " +
            $"LaterPriority={_ini.LaterProgramPriority} " +
            $"PreStart={_ini.PreStartMarginSeconds}s PostEnd={_ini.PostEndMarginSeconds}s");

        // HOST+WORKER_INTERRUPTION_DURING_START_INVARIANT:
        // workerがOpenTuner後〜Recording正式昇格前に外部終了し、その直後Hostも失われた場合、
        // runtime側はStartingをFailedへ終端できても次回起動時には通常のRecording/Starting回収入口から外れる。
        // 構造化されたworker-exit provenanceを持つ開始失敗だけを起動時にScheduledへ戻し、
        // 通常の共通割当・due開始経路へ一度だけ再投入する。SetChannel/TS不整合等の開始失敗は対象外。
        var rearmedInterruptedStarts = _store.RearmInterruptedRecordingStartFailuresAtStartup(DateTime.Now);
        foreach (var rearmed in rearmedInterruptedStarts)
        {
            _log.Add("REC_STARTUP_INTERRUPTED_START_REARM", $"R{rearmed.Id}",
                $"result=REARMED status=Scheduled dataVersion={rearmed.DataVersion} start={rearmed.StartTime:MM/dd HH:mm:ss} end={rearmed.EndTime:MM/dd HH:mm:ss} " +
                $"service={SafeValue(rearmed.ServiceName)} title={ReservationDisplayTitle(rearmed.Title)} action=common_allocation_due_retry " +
                "reason=worker_exited_after_open_before_recording rule=interrupted_recording_recovery_contract");
        }

        // release_contract:
        // 起動時に Recording のまま残った予約は「録画途中でTvAIr/PCが止まった残骸」として精査する。
        // Recording のまま予約一覧へ残すことは禁止。既存部分ファイルを保護し、終了前なら別予約として復旧録画を再投入する。
        // 復旧録画は同じTVTest命名規則を使い、既存ファイルと衝突した場合は MakeUniqueRecordingPath の (1)/(2) で別ファイル化する。
        var staleRecording = _store.GetByStatus(ReservationStatus.Recording).ToList();
        if (staleRecording.Count > 0)
        {
            var now = DateTime.Now;
            foreach (var r in staleRecording)
            {
                var reattach = TryReattachAliveRecordingWorker(r, now, RecordingReattachOrigin.Startup);
                if (reattach.State == RecordingWorkerReattachState.Attached)
                    continue;
                if (reattach.State == RecordingWorkerReattachState.WorkerAliveOwnershipUnavailable)
                {
                    _log.Add("REC_STARTUP_REATTACH", $"R{r.Id}",
                        $"result=OWNERSHIP_UNAVAILABLE pid={(reattach.ProcessId.HasValue ? reattach.ProcessId.Value.ToString() : "-")} reason={SafeValue(reattach.Reason)} " +
                        $"action=keep_worker_do_not_create_recovery_reservation rule=recording_worker_reattach_contract");
                    continue;
                }

                HandleInterruptedRecordingWithoutWorker(r, now, InterruptedRecordingRecoveryTrigger.Startup);
            }
        }

        // release_contract: 起動時に終端予約へ残った競合フラグを整理する。
        // 共通割り当てルートの再評価前に、Completed/Cancelled を競合対象から外し、
        // Cancelled行に競合状態を残さず、予約一覧とログの終端状態を一致させる。
        ClearTerminalConflictResiduesSafe("startup_before_allocation");

        // 起動時に全scheduledの競合フラグを再評価（前回終了後の状態を反映）
        ReevaluateAndLog("Init");
        // 起動時のチェーン構成をログ出力
        try
        {
            var initChains = _store.GetChains();
            if (initChains.Count > 0)
            {
                var desc = string.Join(" / ", initChains.Select(c => string.Join("→", c.Select(id => $"R{id}"))));
                _log.Add("Scheduler", "Chain", $"起動時チェーン構成: {desc}");
            }
            else
            {
                // release_contract: no-chain is the normal startup state; keep regular logs for actual chain configurations only.
            }
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", "Chain", $"起動時チェーン構成取得エラー: {ex.Message}");
        }
        _log.Add("Scheduler", "Init", "起動時競合フラグ再評価を実行しました。");
        _allocationReevaluateCounter = 0;
        _forceAllocationReevaluate = false;
        _skipNextTickAllocationReevaluate = true;

        var chainBoundaryMonitorTask = Task.Run(() => ChainBoundaryMonitorLoopAsync(stoppingToken), stoppingToken);
        var recordingDueMonitorTask = Task.Run(() => RecordingDueMonitorLoopAsync(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Add("Scheduler", "Error", $"ポーリング例外: {ex.Message}");
            }

            await Task.Delay(PollingIntervalMs, stoppingToken);
        }

        await StopAllSessionsAsync();
        _log.Add("Scheduler", "Stop", "ReservationScheduler 停止。");
    }


    private enum InterruptedRecordingRecoveryTrigger
    {
        Startup,
        PowerResume,
        PowerResumeVerification,
        RuntimeWorkerMissing
    }

    private enum RecordingReattachOrigin
    {
        Startup,
        Resume
    }

    private readonly record struct InterruptedRecordingFileProbe(
        bool IsPartial,
        string Summary);

    private bool HandleInterruptedRecordingWithoutWorker(Reservation r, DateTime now, InterruptedRecordingRecoveryTrigger trigger)
    {
        using var recoveryEventScope = _typedEvents.BeginOutboxScope(out var commitRecoveryEvents);
        var isStartup = trigger == InterruptedRecordingRecoveryTrigger.Startup;
        var phase = trigger switch
        {
            InterruptedRecordingRecoveryTrigger.Startup => "startup",
            InterruptedRecordingRecoveryTrigger.PowerResume => "power_resume",
            InterruptedRecordingRecoveryTrigger.PowerResumeVerification => "power_resume_verification",
            _ => "runtime_worker_missing"
        };
        var phaseTag = trigger switch
        {
            InterruptedRecordingRecoveryTrigger.Startup => "STARTUP",
            InterruptedRecordingRecoveryTrigger.RuntimeWorkerMissing => "RUNTIME",
            _ => "RESUME"
        };
        var outboxOperation = trigger switch
        {
            InterruptedRecordingRecoveryTrigger.Startup => "StartupRecordingRecovery",
            InterruptedRecordingRecoveryTrigger.RuntimeWorkerMissing => "RuntimeRecordingRecovery",
            _ => "PowerResumeRecordingRecovery"
        };
        var interruptedFile = ProbeInterruptedRecordingFile(r);
        if (TryFinalizePastEndedRecordingAsCompleted(r, interruptedFile.Summary, now))
        {
            commitRecoveryEvents();
            _log.Add("PLUGIN_TYPED_EVENT_OUTBOX", $"R{r.Id}",
                $"result=COMMITTED operation={outboxOperation} rule=typed_event_outbox");
            return true;
        }

        var guard = EvaluateInterruptedRecordingRecoveryGuard(r, interruptedFile);
        if (!guard.ShouldRecover)
        {
            _log.Add($"REC_{phaseTag}_RECOVERY_GUARD", $"R{r.Id}",
                $"result=SKIPPED reason={SafeValue(guard.Reason)} now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} file={SafeValue(interruptedFile.Summary)} workerPid={guard.WorkerPid?.ToString() ?? "-"} completedResult={guard.CompletedResult} trigger={phase} rule=release_contract");
            return false;
        }

        var recoverUntil = r.EndTime.AddSeconds(Math.Max(10, _ini.PostEndMarginSeconds + 30));
        var recoverable = now <= recoverUntil && r.IsEnabled && !r.IsConflicted && r.Source != ReservationSource.Epg;
        _log.Add("REC_INTERRUPTED_DETECTED", $"R{r.Id}",
            $"result=DETECTED recoverable={recoverable} trigger={phase} guard=passed now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} recoverUntil={recoverUntil:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} file={SafeValue(interruptedFile.Summary)} rule=release_contract");

        if (recoverable && now < r.EndTime)
        {
            try
            {
                // INTERRUPTED_RECORDING_RECOVERY_ATOMIC_INVARIANT:
                // 元予約終端化・復旧予約追加・チェーン付替えは同一トランザクションで確定する。
                // Startup/PowerResumeのどちらでも、元予約だけを先にFailedへ落とす二段更新へ戻してはならない。
                var recoveryId = _store.FinalizeAndAddInterruptedRecordingRecoveryReservation(
                    r,
                    now,
                    isStartup
                        ? "recovery_requeued_as_new_reservation"
                        : "power_resume_requeued_as_new_reservation",
                    interruptedFile.Summary,
                    outboxOperation);
                _log.Add($"REC_{phaseTag}_RECOVERY_REQUEUE", $"R{recoveryId}",
                    $"result=REQUEUED_AS_NEW source=R{r.Id} recovery=R{recoveryId} trigger={phase} reason=recording_window_still_recoverable_and_original_finalized_atomically now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} recoverUntil={recoverUntil:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} fileCollisionPolicy=append_number_suffix rule=interrupted_recording_recovery_atomic_contract");
            }
            catch (InvalidOperationException ex)
            {
                _log.Add($"REC_{phaseTag}_RECOVERY_CAS", $"R{r.Id}",
                    $"result=REJECTED action=rollback_atomic_recovery expectedDataVersion={r.DataVersion} trigger={phase} reason={SafeValue(ex.Message)} rule=interrupted_recording_recovery_atomic_contract");
                return false;
            }
        }
        else
        {
            var finalized = _store.FinalizeInterruptedRecording(
                r,
                now,
                isStartup
                    ? "outside_recoverable_window_or_not_recordable"
                    : "power_resume_outside_recoverable_window_or_not_recordable",
                interruptedFile.Summary,
                outboxOperation);
            if (!finalized)
            {
                _log.Add($"REC_{phaseTag}_RECOVERY_CAS", $"R{r.Id}",
                    $"result=REJECTED action=abort_stale_snapshot expectedDataVersion={r.DataVersion} trigger={phase} rule=interrupted_recording_recovery_finalize_cas");
                return false;
            }

            _log.Add($"REC_{phaseTag}_RECOVERY_REQUEUE", $"R{r.Id}",
                $"result=FINALIZED_ONLY trigger={phase} reason={(now >= r.EndTime ? "program_already_finished" : "not_recordable_or_outside_recoverable_window")} now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} recoverUntil={recoverUntil:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} rule=release_contract");
        }

        commitRecoveryEvents();
        _log.Add("PLUGIN_TYPED_EVENT_OUTBOX", $"R{r.Id}",
            $"result=COMMITTED operation={outboxOperation} rule=typed_event_outbox");
        return true;
    }

    private async Task RecordingDueMonitorLoopAsync(CancellationToken stoppingToken)
    {
        _log.Add("REC_DUE_SCHEDULER", "START",
            $"result=STARTED intervalMs={RecordingDueMonitorIntervalMs} lookAheadSeconds={RecordingResponsibilityTiming.DueLookAheadSeconds} policy=high_priority_recording_due_monitor commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var scheduled = _store.GetByStatus(ReservationStatus.Scheduled);
                var starting = _store.GetByStatus(ReservationStatus.Starting);
                // release_contract: due monitorの同一500ms周期では、直前に取得したScheduled snapshotを
                // EPG timeline gateにも共有する。判定正本を変えず、同じstatus全件SELECTの二重materializeだけを避ける。
                PublishRecordingTimelineEpgGate(now, scheduled);

                // ORPHAN_STARTING_DUE_MONITOR_REACHABILITY_INVARIANT:
                // owner/workerを失ったStartingはScheduled一覧から消えるため、Scheduledだけをwake条件にすると
                // 高優先度due monitor自体へ再到達できない。Startingも同じnear-due判定へ含め、
                // 実回収可否は共通のorphan recovery契約（owner/worker不在 + 2秒超 + CAS）だけに委ねる。
                var hasNearDue = scheduled.Any(r => r.IsEnabled
                    && r.Source != ReservationSource.Epg
                    && r.StartTime.AddSeconds(-_ini.PreStartMarginSeconds) <= now.AddSeconds(RecordingResponsibilityTiming.DueLookAheadSeconds))
                    || starting.Any(r => r.IsEnabled
                        && r.Source != ReservationSource.Epg
                        && now < r.EndTime
                        && r.StartTime.AddSeconds(-_ini.PreStartMarginSeconds) <= now.AddSeconds(RecordingResponsibilityTiming.DueLookAheadSeconds));
                if (hasNearDue)
                    await TryLaunchDueReservationsAsync(now, "RecordingDueMonitor", stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Add("REC_DUE_SCHEDULER", "ERROR", $"result=ERROR message={SafeValue(ex.Message)} rule=release_contract");
            }

            await Task.Delay(RecordingDueMonitorIntervalMs, stoppingToken).ConfigureAwait(false);
        }
    }


    private async Task ChainBoundaryMonitorLoopAsync(CancellationToken stoppingToken)
    {
        var configuredRecordingTuners = _tunerPool.GetStatus()
            .Where(slot => string.Equals(slot.Role, "Recording", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var configuredRecordingGroups = configuredRecordingTuners
            .GroupBy(slot => NormalizeRecordingLaunchGroup(slot.Group), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{SafeValue(group.Key)}:{group.Count()}")
            .ToArray();
        _log.Add("CHAIN_BOUNDARY_SCHEDULER", "START",
            $"result=STARTED intervalMs={ChainBoundaryMonitorIntervalMs} normalTickMs={PollingIntervalMs} policy=high_priority_chain_boundary_monitor " +
            $"configuredRecordingTuners={configuredRecordingTuners.Count} configuredRecordingGroups=[{string.Join(',', configuredRecordingGroups)}] " +
            $"fixedTunerLimit=none launchPolicy=dynamic_all_recording_tuners cadenceMs={RecordingLaunchCadenceMs} emergencyWindowSec={ChainEmergencyLaunchWindowSeconds} " +
            $"softwareResponsibility=no_internal_fixed_cap_no_unnecessary_wait_environmentResponsibility=driver_os_storage_worker_startup_performance " +
            $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=chain_dynamic_capacity_responsibility_contract");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_ini.PseudoContinuousRecording)
                    await CheckPseudoContinuousHandoffAsync(DateTime.Now, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Add("CHAIN_BOUNDARY_SCHEDULER", "ERROR",
                    $"result=ERROR message={TrimForLog(ex.Message, 180)} rule=release_contract");
            }

            await Task.Delay(ChainBoundaryMonitorIntervalMs, stoppingToken).ConfigureAwait(false);
        }
    }

    private bool ShouldLogChainBoundaryWait(string key, double remainingSeconds)
    {
        var bucket = (int)Math.Ceiling(Math.Max(0, remainingSeconds) / 10.0);
        lock (_sessionGate)
        {
            if (_chainBoundaryLastWaitBucket.TryGetValue(key, out var last) && last == bucket)
                return false;
            _chainBoundaryLastWaitBucket[key] = bucket;
            return true;
        }
    }

    private bool TryBeginChainBoundaryExecution(string key, int predecessorId, int successorId, DateTime now, out ChainBoundaryExecutionEntry entry)
    {
        lock (_sessionGate)
        {
            if (!_chainBoundaryExecutions.TryGetValue(key, out entry!))
            {
                entry = new ChainBoundaryExecutionEntry
                {
                    Key = key,
                    PredecessorId = predecessorId,
                    SuccessorId = successorId,
                    State = ChainBoundaryExecutionState.NotStarted,
                    NextRetryAt = now,
                    LastUpdatedAt = now
                };
                _chainBoundaryExecutions[key] = entry;
            }

            if (entry.State is ChainBoundaryExecutionState.Succeeded or ChainBoundaryExecutionState.TerminalFailed)
                return false;
            if (entry.State == ChainBoundaryExecutionState.Executing)
                return false;
            if (entry.State == ChainBoundaryExecutionState.RetryableFailed && now < entry.NextRetryAt)
                return false;

            entry.State = ChainBoundaryExecutionState.Executing;
            entry.Attempt++;
            entry.LastUpdatedAt = now;
            return true;
        }
    }

    private void CompleteChainBoundaryExecution(ChainBoundaryExecutionEntry entry, bool success, bool retryable, string reason, DateTime now)
    {
        lock (_sessionGate)
        {
            if (!_chainBoundaryExecutions.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
                return;

            current.LastReason = reason;
            current.LastUpdatedAt = now;
            if (success)
            {
                current.State = ChainBoundaryExecutionState.Succeeded;
                current.NextRetryAt = DateTime.MaxValue;
                return;
            }

            if (!retryable)
            {
                current.State = ChainBoundaryExecutionState.TerminalFailed;
                current.NextRetryAt = DateTime.MaxValue;
                return;
            }

            current.State = ChainBoundaryExecutionState.RetryableFailed;
            // CHAIN_EMERGENCY_RETRY_BACKOFF_INVARIANT:
            // 後続開始まで残り10秒以内ではattempt比例backoffを適用せず、500ms監視ごとに再評価する。
            var successor = _store.GetById(current.SuccessorId);
            var emergencyRetry = successor is not null
                && (successor.StartTime - now).TotalSeconds <= ChainEmergencyLaunchWindowSeconds;
            var retryDelayMs = emergencyRetry
                ? ChainBoundaryMonitorIntervalMs
                : Math.Min(5_000, Math.Max(ChainBoundaryMonitorIntervalMs, current.Attempt * ChainBoundaryMonitorIntervalMs));
            current.NextRetryAt = now.AddMilliseconds(retryDelayMs);
        }
    }

    private async Task RetryPendingChainBoundariesAsync(DateTime now, CancellationToken ct)
    {
        List<ChainBoundaryExecutionEntry> pending;
        lock (_sessionGate)
        {
            foreach (var staleKey in _chainBoundaryExecutions
                .Where(kv => kv.Value.State is ChainBoundaryExecutionState.Succeeded or ChainBoundaryExecutionState.TerminalFailed)
                .Where(kv => now - kv.Value.LastUpdatedAt >= TimeSpan.FromHours(1))
                .Select(kv => kv.Key)
                .ToList())
            {
                _chainBoundaryExecutions.Remove(staleKey);
                _chainBoundaryLastWaitBucket.Remove(staleKey);
            }

            pending = _chainBoundaryExecutions.Values
                .Where(x => x.State == ChainBoundaryExecutionState.RetryableFailed && x.NextRetryAt <= now)
                .ToList();
        }

        foreach (var pendingEntry in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryBeginChainBoundaryExecution(pendingEntry.Key, pendingEntry.PredecessorId, pendingEntry.SuccessorId, now, out var execution))
                continue;

            var successor = _store.GetById(execution.SuccessorId);
            if (successor is null || !successor.IsEnabled || successor.Status is ReservationStatus.Cancelled or ReservationStatus.Completed or ReservationStatus.Failed)
            {
                CompleteChainBoundaryExecution(execution, false, false, "successor_not_recordable", now);
                _log.Add("CHAIN_BOUNDARY_RETRY", $"R{execution.PredecessorId}",
                    $"result=TERMINAL_FAILED predecessor=R{execution.PredecessorId} successor=R{execution.SuccessorId} attempt={execution.Attempt} reason=successor_not_recordable rule=release_contract");
                continue;
            }

            if (successor.Status == ReservationStatus.Recording)
            {
                CompleteChainBoundaryExecution(execution, true, false, "successor_already_recording", now);
                _log.Add("CHAIN_BOUNDARY_RETRY", $"R{execution.PredecessorId}",
                    $"result=SUCCESS predecessor=R{execution.PredecessorId} successor=R{execution.SuccessorId} attempt={execution.Attempt} reason=successor_already_recording rule=release_contract");
                continue;
            }

            if (successor.Status != ReservationStatus.Scheduled || now >= successor.EndTime)
            {
                CompleteChainBoundaryExecution(execution, false, false, "successor_retry_window_closed", now);
                _log.Add("CHAIN_BOUNDARY_RETRY", $"R{execution.PredecessorId}",
                    $"result=TERMINAL_FAILED predecessor=R{execution.PredecessorId} successor=R{execution.SuccessorId} attempt={execution.Attempt} reason=successor_retry_window_closed status={successor.Status} rule=release_contract");
                continue;
            }

            var started = false;
            try
            {
                // CHAIN_BOUNDARY_RETRY_INVARIANT:
                // 境界後の再試行も初回ハンドオフと同じチェーン境界起動である。
                // 通常Admissionへ戻すと、開始済み時刻を過ぎた後続に1秒投入待ちを再適用し、
                // 30秒前に前段を切ったチェーンの後続開始をさらに遅らせるため禁止する。
                ChainReleaseStartEvidence? retryEvidence;
                lock (_sessionGate)
                {
                    retryEvidence = execution.ReleaseEvidence;
                }
                started = await StartRecordingAsync(successor, ct, chainBoundaryLaunch: true, chainReleaseEvidence: retryEvidence).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CompleteChainBoundaryExecution(execution, false, true, "retry_cancelled", now);
                throw;
            }
            catch (Exception ex)
            {
                CompleteChainBoundaryExecution(execution, false, true, "retry_exception", now);
                _log.Add("CHAIN_BOUNDARY_RETRY", $"R{execution.PredecessorId}",
                    $"result=RETRYABLE_FAILED predecessor=R{execution.PredecessorId} successor=R{execution.SuccessorId} attempt={execution.Attempt} reason=retry_exception error={TrimForLog(ex.Message, 180)} rule=release_contract");
                continue;
            }

            var latest = _store.GetById(execution.SuccessorId);
            var converged = latest?.Status == ReservationStatus.Recording;
            var retryable = latest is not null && latest.IsEnabled && latest.Status == ReservationStatus.Scheduled && now < latest.EndTime;
            CompleteChainBoundaryExecution(execution, converged, !converged && retryable, converged ? "successor_started" : "successor_start_failed", now);
            _log.Add("CHAIN_BOUNDARY_RETRY", $"R{execution.PredecessorId}",
                $"result={(converged ? "SUCCESS" : retryable ? "RETRYABLE_FAILED" : "TERMINAL_FAILED")} predecessor=R{execution.PredecessorId} successor=R{execution.SuccessorId} attempt={execution.Attempt} started={started} latestStatus={latest?.Status.ToString() ?? "missing"} rule=release_contract");
        }
    }


    private void ClearTerminalConflictResiduesSafe(string reason)
    {
        try
        {
            var affected = _store.ClearTerminalConflictResidues(reason);
            if (affected > 0)
            {
                _forceAllocationReevaluate = true;
                _log.Add("Scheduler", "TerminalConflictCleanup",
                    $"terminal予約の競合残骸を整理: affected={affected} reason={reason} rule=release_contract");
            }
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", "TerminalConflictCleanup",
                $"terminal予約の競合残骸整理エラー: reason={reason} error={ex.Message}");
        }
    }


    private static string? TryGetCommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject obj in searcher.Get())
            {
                return obj["CommandLine"]?.ToString();
            }
        }
        catch
        {
            // WMI失敗時は不明扱い。安全側としてkillしない。
        }

        return null;
    }


    private bool TryFinalizePastEndedRecordingAsCompleted(Reservation r, string interruptedFile, DateTime now)
    {
        // release_contract:
        // Recoveryで作り直した録画や、TvAIr本体だけ再起動した後の録画が
        // 「Recordingのまま・予定終了を過ぎている」場合、まず完了証跡を確認する。
        // 終了済みのファイルを再び中断扱いにすると、ユーザー運用ログが
        // 「中断→再開→開始→中断」で閉じてしまうため、録画ファイルが予定終了近くまで
        // 伸びている場合は Completed へ収束させる。
        if (r.RecordingFinishedAt.HasValue)
            return false;

        if (now < r.EndTime.AddMinutes(3))
            return false;

        if (FindAliveTvAIrEpgRecRecordingWorkerPid(r.Id).HasValue)
            return false;

        var completedResultEvidence = FindCompletedRecordResultForCurrentRun(r);
        var completedResult = completedResultEvidence.Completed;
        var fileEvidence = EvaluatePastEndedRecordingFileEvidence(r);
        if (completedResult && !fileEvidence.LikelyCompleted)
        {
            _log.Add("REC_INTERRUPTED_RECOVERY_COMPLETED_REJECTED", $"R{r.Id}",
                $"result=REJECT reason=completed_result_but_file_not_complete now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} fileAudit={SafeValue(interruptedFile)} fileEvidence={SafeValue(fileEvidence.Summary)} action=finalize_as_interrupted_not_completed rule=release_contract");
            return false;
        }
        if (!completedResult && !fileEvidence.LikelyCompleted)
            return false;

        var finalize = _store.TryFinalizePastEndedRecordingAsCompleted(r.Id, r.DataVersion, now);
        if (!finalize.Applied)
        {
            _log.Add("REC_INTERRUPTED_RECOVERY_FINALIZE", $"R{r.Id}",
                $"result=REJECTED_PAST_END reason={SafeValue(finalize.Reason)} expectedStatus=Recording actualStatus={SafeValue(finalize.CurrentStatus?.ToString())} expectedVersion={r.DataVersion} actualVersion={finalize.CurrentDataVersion} " +
                $"action=preserve_newer_runtime_or_edit rule=release_contract");
            return false;
        }

        _log.Add("REC_INTERRUPTED_RECOVERY_FINALIZE", $"R{r.Id}",
            $"result=COMPLETED_PAST_END reason={(completedResult ? "completed_record_result" : "recording_file_reached_program_end")} " +
            $"now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} " +
            $"service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} fileAudit={SafeValue(interruptedFile)} " +
            $"fileEvidence={SafeValue(fileEvidence.Summary)} dataVersion={finalize.PreviousDataVersion}->{finalize.CurrentDataVersion} atomicFinishEvidence=True rule=release_contract");
        return true;
    }

    private CompletedRecordingSessionEvidence EvaluateCompletedRecordingSessionEvidence(RecordingSession session, Reservation reservation, DateTime? expectedCompletionEnd = null)
    {
        try
        {
            var evidenceExpectedEnd = expectedCompletionEnd ?? session.PlannedEndTime;
            var expectedSeconds = Math.Max(1, (evidenceExpectedEnd - (reservation.RecordingStartedAt ?? reservation.StartTime)).TotalSeconds);
            var minViableBytes = CalculateMinimumViableCompletedRecordingBytes(expectedSeconds);
            var path = session.RecordingFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return CompletedRecordingSessionEvidence.Failed("recording_file_missing_at_completion", $"path={SafeValue(path)} expectedSeconds={(int)expectedSeconds} minViableBytes={minViableBytes}");

            var fi = new FileInfo(path);
            var observedLastGrowth = session.LastRecordingFileGrowthAt;
            var fileLastWrite = fi.LastWriteTime;
            var effectiveLastGrowth = fileLastWrite > observedLastGrowth ? fileLastWrite : observedLastGrowth;
            var reachedPlannedEnd = effectiveLastGrowth >= evidenceExpectedEnd.AddSeconds(-90);
            var sizeViable = fi.Length >= minViableBytes;
            var summary = $"file={Path.GetFileName(path)} bytes={fi.Length} lastWrite={fileLastWrite:yyyy-MM-dd HH:mm:ss} " +
                $"sessionLastGrowth={observedLastGrowth:yyyy-MM-dd HH:mm:ss} effectiveLastGrowth={effectiveLastGrowth:yyyy-MM-dd HH:mm:ss} " +
                $"plannedEnd={session.PlannedEndTime:yyyy-MM-dd HH:mm:ss} evidenceExpectedEnd={evidenceExpectedEnd:yyyy-MM-dd HH:mm:ss} expectedEndSource={(expectedCompletionEnd.HasValue ? "chain_boundary_cut" : "planned_end")} reachedEnd={reachedPlannedEnd} expectedSeconds={(int)expectedSeconds} " +
                $"minViableBytes={minViableBytes} sizeViable={sizeViable}";

            if (!reachedPlannedEnd)
                return CompletedRecordingSessionEvidence.Failed("recording_file_stopped_before_planned_end", summary);
            if (!sizeViable)
                return CompletedRecordingSessionEvidence.Failed("recording_file_too_small_for_completed_duration", summary);

            return CompletedRecordingSessionEvidence.Completed(summary);
        }
        catch (Exception ex)
        {
            return CompletedRecordingSessionEvidence.Failed("recording_completion_evidence_error", $"error={ex.GetType().Name}:{SafeValue(ex.Message)}");
        }
    }

    private readonly record struct CompletedRecordingSessionEvidence(bool LikelyCompleted, string Reason, string Summary)
    {
        public static CompletedRecordingSessionEvidence Completed(string summary) => new(true, "completed_evidence_ok", summary);
        public static CompletedRecordingSessionEvidence Failed(string reason, string summary) => new(false, reason, summary);
    }

    private PastEndedRecordingFileEvidence EvaluatePastEndedRecordingFileEvidence(Reservation r)
    {
        try
        {
            if (!TvTestRecordingDirectoryResolver.TryResolve(_ini.TvTestExecutablePath, out var directory, out _))
                return PastEndedRecordingFileEvidence.NotCompleted("folder_unresolved");

            // INTERRUPTED_RECORDING_FILE_LINEAGE_TIME_INVARIANT:
            // 部分TS探索も実録画開始と同じ命名時刻正本を使う。復旧予約では RecoveryParentReservationId を
            // ResolveDirectRecorderFileNameTimePolicy が認識し、元録画の StartTime を維持する。
            // ここだけ Immediate/Program の RecordingStartedAt を再計算すると、(1)/(2)... 系列を見失う。
            var namingPolicy = ResolveDirectRecorderFileNameTimePolicy(r, r.RecordingStartedAt ?? r.StartTime);
            var expectedName = BuildDirectRecorderFileName(r, namingPolicy.BaseTime).FileName;
            var candidates = new List<string>();
            if (Directory.Exists(directory))
            {
                // INTERRUPTED_RECORDING_FILE_LINEAGE_INVARIANT:
                // 自動再開は同じTVTest命名正本から (1)/(2)... を生成するため、exactだけを優先すると
                // 2回目以降の中断で古い先頭segmentを誤参照する。常に同名系列を列挙し、最新segmentを証拠正本にする。
                var baseName = Path.GetFileNameWithoutExtension(expectedName);
                candidates.AddRange(Directory.EnumerateFiles(directory, baseName + "*.ts")
                    .OrderByDescending(File.GetLastWriteTime)
                    .Take(3));
            }

            if (candidates.Count == 0)
                return PastEndedRecordingFileEvidence.NotCompleted($"no_file expected={expectedName}");

            var bestPath = candidates
                .Select(path =>
                {
                    try
                    {
                        var fi = new FileInfo(path);
                        return new { Path = path, fi.Length, fi.LastWriteTime };
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(x => x is not null)
                .OrderByDescending(x => x!.LastWriteTime)
                .FirstOrDefault();

            if (bestPath is null)
                return PastEndedRecordingFileEvidence.NotCompleted("stat_failed");

            // 録画末尾付近まで書かれているTSだけを完了証跡として扱う。
            // ただし最終更新時刻だけでは、再起動後に部分ファイルへ後書きされたケースを正常完了に誤認する。
            // 予約尺に対して明らかに小さいTSは、軽微DROP/WARNとは別物として中断証跡にする。
            var expectedSeconds = Math.Max(1, (r.EndTime - r.StartTime).TotalSeconds);
            var reachedEndByLastWrite = bestPath.Length > 0 && bestPath.LastWriteTime >= r.EndTime.AddSeconds(-90);
            var minViableBytes = CalculateMinimumViableCompletedRecordingBytes(expectedSeconds);
            var sizeViable = bestPath.Length >= minViableBytes;
            var likelyCompleted = reachedEndByLastWrite && sizeViable;
            var summary = $"file={Path.GetFileName(bestPath.Path)} bytes={bestPath.Length} lastWrite={bestPath.LastWriteTime:yyyy-MM-dd HH:mm:ss} reachedEnd={reachedEndByLastWrite} minViableBytes={minViableBytes} sizeViable={sizeViable}";
            return likelyCompleted
                ? PastEndedRecordingFileEvidence.Completed(summary)
                : PastEndedRecordingFileEvidence.NotCompleted(summary);
        }
        catch (Exception ex)
        {
            return PastEndedRecordingFileEvidence.NotCompleted($"error={ex.GetType().Name}");
        }
    }

    private static long CalculateMinimumViableCompletedRecordingBytes(double expectedSeconds)
    {
        // release_contract:
        // 中断録画復旧でのみ使う低すぎる録画ファイルの保険値。
        // 通常停止・TS検証OK・軽微WARNの録画を落とすための判定ではない。
        // 0.5Mbps相当を下限にし、TV録画として明らかに短い部分ファイルだけを弾く。
        var seconds = Math.Max(1, expectedSeconds);
        var bytes = seconds * 64d * 1024d;
        return (long)Math.Min(long.MaxValue, Math.Max(1, bytes));
    }

    private readonly record struct PastEndedRecordingFileEvidence(bool LikelyCompleted, string Summary)
    {
        public static PastEndedRecordingFileEvidence Completed(string summary) => new(true, summary);
        public static PastEndedRecordingFileEvidence NotCompleted(string summary) => new(false, summary);
    }

    private RecordingWorkerReattachResult TryReattachAliveRecordingWorker(Reservation reservation, DateTime now, RecordingReattachOrigin origin)
    {
        var source = origin == RecordingReattachOrigin.Startup ? "startup" : "resume";
        var candidates = FindAliveRecordingWorkerCandidates(reservation.Id);
        if (candidates.Count == 0)
            return RecordingWorkerReattachResult.None();
        if (candidates.Count != 1)
            return RecordingWorkerReattachResult.Unavailable(candidates[0].ProcessId, $"worker_candidate_count={candidates.Count}");

        var worker = candidates[0];
        try
        {
            if (!File.Exists(worker.JobPath))
                return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, "job_file_missing");

            using var jobDoc = JsonDocument.Parse(File.ReadAllText(worker.JobPath));
            var root = jobDoc.RootElement;
            var mode = root.TryGetProperty("mode", out var modeProp) ? modeProp.GetString() : null;
            if (!string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase))
                return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, "job_mode_not_record");

            var metadataReservationId = 0;
            if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("reservationId", out var ridProp))
                int.TryParse(ridProp.GetString(), out metadataReservationId);
            if (metadataReservationId != reservation.Id)
                return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, $"job_reservation_mismatch:{metadataReservationId}");

            static string ReadString(JsonElement element, string name)
                => element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? string.Empty : string.Empty;

            var tunerName = ReadString(root, "tuner");
            var did = ReadString(root, "did");
            var bonDriver = ReadString(root, "bonDriver");
            var outputPath = ReadString(root, "outputPath");
            var responsePath = ReadString(root, "resultPath");
            var progressPath = ReadString(root, "progressPath");
            var runtimeStatsPath = ReadString(root, "runtimeStatsPath");
            var stopSignalPath = ReadString(root, "cancelSignalPath");
            var postEndMarginSeconds = root.TryGetProperty("postEndMarginSeconds", out var postMarginProp)
                && postMarginProp.TryGetInt32(out var persistedPostMarginSeconds)
                    ? SettingsDefaults.NormalizePostEndMarginSeconds(persistedPostMarginSeconds)
                    : SettingsDefaults.NormalizePostEndMarginSeconds(_ini.PostEndMarginSeconds);

            if (string.IsNullOrWhiteSpace(tunerName) || string.IsNullOrWhiteSpace(bonDriver)
                || string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(stopSignalPath))
                return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, "job_identity_or_runtime_path_missing");

            var persistedTuner = !string.IsNullOrWhiteSpace(reservation.ActualTunerName)
                ? reservation.ActualTunerName
                : reservation.TunerName;
            if (!string.IsNullOrWhiteSpace(persistedTuner)
                && !string.Equals(persistedTuner, tunerName, StringComparison.OrdinalIgnoreCase))
                return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, $"reservation_tuner_mismatch:{persistedTuner}->{tunerName}");

            var plannedEnd = ResolveChainPlannedEndForLaunch(
                reservation,
                reservation.EndTime.AddSeconds(postEndMarginSeconds));
            if (now >= plannedEnd.AddMinutes(3))
                return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, "worker_alive_after_attach_window");

            var lease = _tunerPool.AttachExistingRecording(
                tunerName,
                did,
                bonDriver,
                reservation.Id,
                worker.ProcessId,
                plannedEnd);
            if (lease is null)
                return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, "tuner_pool_attach_rejected");

            var attachCommit = _store.TryAttachRecordingOwner(
                reservation.Id,
                reservation.DataVersion,
                lease.Name,
                now);
            if (!attachCommit.Applied || attachCommit.Reservation is null)
            {
                lease.Dispose();
                return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, $"reservation_attach_commit_rejected:{attachCommit.Reason}");
            }

            var session = new RecordingSession(
                reservation.Id,
                worker.ProcessId,
                plannedEnd,
                lease,
                outputPath,
                responsePath,
                stopSignalPath,
                progressPath,
                runtimeStatsPath,
                worker.JobPath,
                postEndMarginSeconds,
                activityHandle: null);
            // RECORDING_REATTACH_COMMIT_INVARIANT:
            // 永続状態が既にRecordingであるworkerの再接続なので、登録前に正式録画sessionへ昇格する。
            if (!session.TryMarkRecordingCommitted())
            {
                lease.Dispose();
                return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, "session_commit_state_rejected");
            }

            lock (_sessionGate)
            {
                if (_activeSessions.ContainsKey(reservation.Id))
                {
                    lease.Dispose();
                    return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, "active_session_already_exists");
                }
                _activeSessions[reservation.Id] = session;
            }

            TvAirManagedProcessRegistry.RegisterRecording(worker.ProcessId, reservation.Id, lease.Did, lease.BonDriverFileName, outputPath);
            BindChainDirectRecorderSessionScaffold(attachCommit.Reservation, session, null, "startup_reattached");
            var reattachLogCategory = origin == RecordingReattachOrigin.Startup
                ? "REC_STARTUP_REATTACH"
                : "REC_RESUME_REATTACH";
            _log.Add(reattachLogCategory, $"R{reservation.Id}",
                $"result=ATTACHED source={SafeValue(source)} pid={worker.ProcessId} tuner={lease.Name} did={lease.Did} bonDriver={lease.BonDriverFileName} " +
                $"poolLeaseId={lease.PoolLeaseId} generation={lease.OccupancyGeneration} plannedEnd={plannedEnd:MM/dd HH:mm:ss} " +
                $"output={SafeValue(outputPath)} job={SafeValue(worker.JobPath)} dataVersion={attachCommit.PreviousDataVersion}->{attachCommit.CurrentDataVersion} " +
                "action=resume_monitoring_completion_growth_chain rule=recording_worker_reattach_contract");
            return RecordingWorkerReattachResult.Attached(worker.ProcessId);
        }
        catch (Exception ex)
        {
            return RecordingWorkerReattachResult.Unavailable(worker.ProcessId, $"exception:{ex.GetType().Name}:{ex.Message}");
        }
    }

    private static List<StartupRecordingWorkerCandidate> FindAliveRecordingWorkerCandidates(int reservationId)
    {
        var result = new List<StartupRecordingWorkerCandidate>();
        try
        {
            var expectedExe = ResolveTvAIrEpgRecPath();
            var workDir = Path.Combine(AppContext.BaseDirectory, "runtime", "tvairepgrec-production-recording");
            var jobFiles = Directory.Exists(workDir)
                ? Directory.EnumerateFiles(workDir, $"record_job_R{reservationId}_*.json").Select(Path.GetFullPath).ToList()
                : new List<string>();

            foreach (var process in Process.GetProcessesByName("TvAIrEpgRec"))
            {
                using (process)
                {
                    if (process.HasExited) continue;
                    var actualExe = string.Empty;
                    try { actualExe = process.MainModule?.FileName ?? string.Empty; } catch { }
                    if (string.IsNullOrWhiteSpace(actualExe) || string.IsNullOrWhiteSpace(expectedExe)
                        || !string.Equals(Path.GetFullPath(actualExe), Path.GetFullPath(expectedExe), StringComparison.OrdinalIgnoreCase))
                        continue;

                    var commandLine = TryGetCommandLine(process.Id) ?? string.Empty;
                    var jobPath = jobFiles.FirstOrDefault(path =>
                        commandLine.Contains(path, StringComparison.OrdinalIgnoreCase)
                        || commandLine.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrWhiteSpace(jobPath)) continue;
                    result.Add(new StartupRecordingWorkerCandidate(process.Id, jobPath));
                }
            }
        }
        catch
        {
            // 呼出元でownership unavailableとして扱う。
        }
        return result;
    }

    private enum RecordingWorkerReattachState
    {
        NoWorker,
        Attached,
        WorkerAliveOwnershipUnavailable
    }

    private readonly record struct StartupRecordingWorkerCandidate(int ProcessId, string JobPath);

    private readonly record struct RecordingWorkerReattachResult(
        RecordingWorkerReattachState State,
        int? ProcessId,
        string Reason)
    {
        public static RecordingWorkerReattachResult None()
            => new(RecordingWorkerReattachState.NoWorker, null, "no_alive_worker");
        public static RecordingWorkerReattachResult Attached(int processId)
            => new(RecordingWorkerReattachState.Attached, processId, "attached");
        public static RecordingWorkerReattachResult Unavailable(int? processId, string reason)
            => new(RecordingWorkerReattachState.WorkerAliveOwnershipUnavailable, processId, reason);
    }

    private InterruptedRecordingRecoveryGuardResult EvaluateInterruptedRecordingRecoveryGuard(Reservation r, InterruptedRecordingFileProbe interruptedFile)
    {
        // release_contract:
        // 中断録画復旧は、録画workerの所有証拠と正常完了resultの双方が無い場合だけ許可する。
        // TvAIr本体だけを更新/再起動した直後は、TvAIrEpgRec worker が継続録画中でも DB は Recording のまま見える。
        // ここで復旧予約を作ると (1) ファイルを誤生成するため、安全側で復旧を止める。
        if (r.RecordingFinishedAt.HasValue)
        {
            // A persisted Recording row is still non-terminal even if stale finish metadata exists.
            // Suppressing recovery here leaves the reservation permanently Recording with no worker,
            // so ownership evidence (alive worker / completed result) remains the only recovery blocker.
            _log.Add("REC_INTERRUPTED_RECOVERY_GUARD", $"R{r.Id}",
                $"result=CONTINUE reason=recording_status_with_finished_at now={DateTime.Now:MM/dd HH:mm:ss} finishedAt={r.RecordingFinishedAt.Value:MM/dd HH:mm:ss} " +
                "action=ignore_stale_finish_metadata_and_verify_runtime_ownership rule=interrupted_recording_recovery_contract");
        }

        var workerPid = FindAliveTvAIrEpgRecRecordingWorkerPid(r.Id);
        if (workerPid.HasValue)
            return InterruptedRecordingRecoveryGuardResult.Skip("recording_worker_alive", workerPid.Value, false);

        var completedResultEvidence = FindCompletedRecordResultForCurrentRun(r);
        if (completedResultEvidence.Completed)
        {
            if (!interruptedFile.IsPartial)
                return InterruptedRecordingRecoveryGuardResult.Skip("completed_record_result_exists", null, true);

            _log.Add("REC_INTERRUPTED_RECOVERY_GUARD", $"R{r.Id}",
                $"result=CONTINUE reason=completed_result_ignored_due_partial_file file={SafeValue(interruptedFile.Summary)} action=finalize_as_interrupted_not_completed rule=release_contract");
        }

        // release_contract invariant:
        // Interrupted recording recovery must not be delayed or suppressed by fixed quiet windows or one-shot file-growth sleeps.
        // Worker ownership/identity is resolved before this guard; completed result evidence is checked above.
        // When neither exists, recovery follows the persisted reservation state without timing heuristics.
        return InterruptedRecordingRecoveryGuardResult.Recover();
    }


    private static int? FindAliveTvAIrEpgRecRecordingWorkerPid(int reservationId)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("TvAIrEpgRec"))
            {
                using (proc)
                {
                    if (proc.HasExited) continue;
                    var cmd = TryGetCommandLine(proc.Id) ?? string.Empty;
                    if (cmd.Contains($"record_job_R{reservationId}_", StringComparison.OrdinalIgnoreCase)
                        || cmd.Contains($"record_result_R{reservationId}_", StringComparison.OrdinalIgnoreCase)
                        || cmd.Contains($"record_stop_R{reservationId}_", StringComparison.OrdinalIgnoreCase))
                    {
                        return proc.Id;
                    }
                }
            }
        }
        catch
        {
            // worker確認失敗時は下流のfile/result guardへ委ねる。
        }
        return null;
    }

    private static CompletedRecordResultEvidence FindCompletedRecordResultForCurrentRun(Reservation reservation)
    {
        // STARTUP_RECORD_RESULT_RUN_IDENTITY_INVARIANT:
        // record_result_R{id}_*.json を予約IDだけで総当たりしてはならない。
        // job JSONを実行bundleの正本とし、現在Reservationとidentityを照合したjob自身のresultPathだけを読む。
        // これにより、同一予約の過去runやDB復元前の古いresultが中断録画復旧を誤抑止することを防ぐ。
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "runtime", "tvairepgrec-production-recording");
            if (!Directory.Exists(dir))
                return CompletedRecordResultEvidence.None("runtime_directory_missing");

            var candidates = new List<RecordingRunArtifactBundle>();
            foreach (var jobPath in Directory.EnumerateFiles(dir, $"record_job_R{reservation.Id}_*.json"))
            {
                var bundle = TryLoadRecordingRunArtifactBundle(jobPath, reservation);
                if (bundle is not null)
                    candidates.Add(bundle);
            }

            if (candidates.Count == 0)
                return CompletedRecordResultEvidence.None("no_current_run_job");

            // 同一Reservationに複数の整合jobがある特殊状態では、RecordingStartedAtに最も近いrunを優先する。
            // RecordingStartedAtはworker launchより数秒後になり得るため、絶対時刻の閾値ではなく順位付けにのみ使う。
            var ordered = candidates
                .OrderBy(bundle => reservation.RecordingStartedAt.HasValue
                    ? Math.Abs((bundle.JobFileWriteTime - reservation.RecordingStartedAt.Value).TotalSeconds)
                    : 0d)
                .ThenByDescending(bundle => bundle.JobFileWriteTime)
                .ToList();

            foreach (var bundle in ordered)
            {
                if (string.IsNullOrWhiteSpace(bundle.ResultPath) || !File.Exists(bundle.ResultPath))
                    continue;

                try
                {
                    using var resultDoc = JsonDocument.Parse(File.ReadAllText(bundle.ResultPath));
                    var resultRoot = resultDoc.RootElement;

                    var resultJobId = ReadJsonString(resultRoot, "jobId");
                    if (string.IsNullOrWhiteSpace(resultJobId)
                        || !string.Equals(bundle.JobId, resultJobId, StringComparison.Ordinal))
                        continue;

                    var completed = resultRoot.TryGetProperty("success", out var successProp)
                        && successProp.ValueKind is JsonValueKind.True;
                    if (!completed)
                    {
                        var finalStatus = ReadJsonString(resultRoot, "finalStatus");
                        completed = string.Equals(finalStatus, "Completed", StringComparison.OrdinalIgnoreCase);
                    }

                    if (completed)
                        return CompletedRecordResultEvidence.Found(bundle.JobPath, bundle.ResultPath, bundle.JobId);
                }
                catch
                {
                    // 破損resultは完了証跡として採用しない。別candidateがあれば継続する。
                }
            }

            return CompletedRecordResultEvidence.None("current_run_result_not_completed");
        }
        catch
        {
            return CompletedRecordResultEvidence.None("current_run_result_scan_error");
        }
    }

    private static RecordingRunArtifactBundle? TryLoadRecordingRunArtifactBundle(string jobPath, Reservation reservation)
    {
        try
        {
            using var jobDoc = JsonDocument.Parse(File.ReadAllText(jobPath));
            var root = jobDoc.RootElement;
            if (!string.Equals(ReadJsonString(root, "mode"), "record", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!root.TryGetProperty("recording", out var recording) || recording.ValueKind != JsonValueKind.Object)
                return null;
            if (!recording.TryGetProperty("reservationId", out var ridProp)
                || !ridProp.TryGetInt32(out var recordingReservationId)
                || recordingReservationId != reservation.Id)
                return null;

            var recordingService = ReadJsonString(recording, "serviceName");
            var recordingOutputPath = ReadJsonString(recording, "outputPath");
            if (!recording.TryGetProperty("startTime", out var startProp)
                || startProp.ValueKind != JsonValueKind.String
                || !DateTime.TryParse(startProp.GetString(), out var recordingStart))
                return null;

            // ReservationのstartTimeはjob作成前に確定済みでjobへそのまま保存される。
            // RecordingStartedAtは後段CAS時刻なので、こちらとは完全一致を要求しない。
            if (Math.Abs((recordingStart - reservation.StartTime).TotalSeconds) > 1d)
                return null;

            if (!string.IsNullOrWhiteSpace(recordingService)
                && !string.IsNullOrWhiteSpace(reservation.ServiceName)
                && !string.Equals(recordingService, reservation.ServiceName, StringComparison.Ordinal))
                return null;

            if (!root.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array)
                return null;
            var matchedChannel = false;
            foreach (var channel in channels.EnumerateArray())
            {
                var nid = ReadJsonInt(channel, "networkId");
                var tsid = ReadJsonInt(channel, "transportStreamId");
                var sid = ReadJsonInt(channel, "serviceId");
                if (nid == reservation.NetworkId && tsid == reservation.TransportStreamId && sid == reservation.ServiceId)
                {
                    matchedChannel = true;
                    break;
                }
            }
            if (!matchedChannel)
                return null;

            var jobId = ReadJsonString(root, "jobId");
            if (string.IsNullOrWhiteSpace(jobId))
                return null;

            var resultPath = ReadJsonString(root, "resultPath");
            var progressPath = ReadJsonString(root, "progressPath");
            var runtimeStatsPath = ReadJsonString(root, "runtimeStatsPath");
            var stopSignalPath = ReadJsonString(root, "cancelSignalPath");
            var rootOutputPath = ReadJsonString(root, "outputPath");
            if (string.IsNullOrWhiteSpace(rootOutputPath))
                rootOutputPath = recordingOutputPath;
            if (string.IsNullOrWhiteSpace(resultPath))
                return null;

            var fullJobPath = Path.GetFullPath(jobPath);
            var fullResultPath = Path.GetFullPath(resultPath);
            var runtimeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "runtime", "tvairepgrec-production-recording"));
            if (!IsPathUnderDirectory(fullResultPath, runtimeRoot))
                return null;

            return new RecordingRunArtifactBundle(
                fullJobPath,
                fullResultPath,
                string.IsNullOrWhiteSpace(progressPath) ? string.Empty : Path.GetFullPath(progressPath),
                string.IsNullOrWhiteSpace(runtimeStatsPath) ? string.Empty : Path.GetFullPath(runtimeStatsPath),
                string.IsNullOrWhiteSpace(stopSignalPath) ? string.Empty : Path.GetFullPath(stopSignalPath),
                rootOutputPath,
                jobId,
                File.GetLastWriteTime(fullJobPath));
        }
        catch
        {
            return null;
        }
    }

    private static string ReadJsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadJsonInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var value) ? value : int.MinValue;

    private static bool IsPathUnderDirectory(string path, string directory)
    {
        var normalizedDirectory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RecordingRunArtifactBundle(
        string JobPath,
        string ResultPath,
        string ProgressPath,
        string RuntimeStatsPath,
        string StopSignalPath,
        string OutputPath,
        string JobId,
        DateTime JobFileWriteTime);

    private readonly record struct CompletedRecordResultEvidence(bool Completed, string Reason, string JobPath, string ResultPath, string JobId)
    {
        public static CompletedRecordResultEvidence Found(string jobPath, string resultPath, string jobId)
            => new(true, "current_run_completed_result", jobPath, resultPath, jobId);
        public static CompletedRecordResultEvidence None(string reason)
            => new(false, reason, string.Empty, string.Empty, string.Empty);
    }

    private readonly record struct InterruptedRecordingRecoveryGuardResult(bool ShouldRecover, string Reason, int? WorkerPid, bool CompletedResult)
    {
        public static InterruptedRecordingRecoveryGuardResult Recover() => new(true, "guard_passed", null, false);
        public static InterruptedRecordingRecoveryGuardResult Skip(string reason, int? workerPid, bool completedResult) => new(false, reason, workerPid, completedResult);
    }

    private static string TrimForLog(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }


    // ─── 公開API（外部からの停止要求） ──────────────────────────────

    public StopRecordingRequestResult StopRecording(int reservationId)
    {
        RecordingSession? session;
        lock (_sessionGate)
        {
            if (!_manualStopRequests.Add(reservationId))
                return StopRecordingRequestResult.CreatePending(reservationId);
            _activeSessions.TryGetValue(reservationId, out session);
        }

        try
        {
            var policyReservation = _store.GetById(reservationId);
            var policyService = SafeValue(policyReservation?.ServiceName);
            var policyTitle = ReservationDisplayTitle(policyReservation?.Title);
            var policyRawTitleBlank = ReservationTitleDisplayContract.RawBlankFlag(policyReservation?.Title);
            if (policyReservation is not null)
            {
                var suppressUntil = policyReservation.EndTime.AddSeconds(Math.Max(0, _ini.PostEndMarginSeconds));
                if (suppressUntil < DateTime.Now)
                    suppressUntil = policyReservation.EndTime;
                _store.AddManualStoppedOccurrence(policyReservation, suppressUntil, "recording_stop");
            }

            var siblingStops = policyReservation is null
                ? new List<RecordingSession>()
                : FindSameOccurrenceActiveSessionsForManualStop(policyReservation, reservationId);
            var siblingCancelled = policyReservation is null
                ? 0
                : CancelSameOccurrenceScheduledReservationsForManualStop(policyReservation, reservationId);

            var detached = _store.TryDetachUserChainSuccessorsForManualStopAtomicCas(
                reservationId, out var detachedSuccessors);
            SafeManualStopLog("CHAIN_STOP_POLICY", $"R{reservationId}",
                $"operation=ManualStop service={policyService} title={policyTitle} rawTitleBlank={policyRawTitleBlank} policy=current_segment_only successorAction={(detached ? (detachedSuccessors.Count > 0 ? "DetachKeepScheduled" : "None") : "DetachCasRejectedKeepLatestChain")} detachedSuccessors={detachedSuccessors.Count} cancelSuccessors=False siblingActiveStops={siblingStops.Count} siblingScheduledCancelled={siblingCancelled} normalReservationStopRouteUnchanged=True rule=recording_lifecycle_cas_contract");
            if (detachedSuccessors.Count > 0)
            {
                SafeManualStopLog("CHAIN_SESSION_DETACH", $"R{reservationId}",
                    $"operation=ManualStop result=SUCCESSORS_DETACHED count={detachedSuccessors.Count} targets=[{string.Join(",", detachedSuccessors.Select(x => $"R{x.Id}:{SafeValue(x.ServiceName)}:{ReservationDisplayTitle(x.Title, 40)}"))}] sessionReleaseDeferredUntilStop=True rule=release_contract");
            }

            foreach (var sibling in siblingStops)
            {
                var sid = sibling.ReservationId;
                if (!TryRegisterManualStopRequest(sid))
                {
                    SafeManualStopLog("MANUAL_STOP_OCCURRENCE", $"R{reservationId}",
                        $"result=SIBLING_ALREADY_PENDING target=R{sid} source=R{reservationId} reason=same_occurrence_manual_stop rule=release_contract");
                    continue;
                }

                SafeManualStopLog("MANUAL_STOP_OCCURRENCE", $"R{reservationId}",
                    $"result=SIBLING_STOP_REQUEST target=R{sid} source=R{reservationId} reason=same_occurrence_manual_stop pid={sibling.ProcessId} tuner={SafeValue(sibling.Lease.Name)} rule=release_contract");
                StartManualStopTask(sibling, ReservationStatus.Cancelled, sid,
                    $"同一発生回の手動停止により録画を停止しました。source=R{reservationId}");
            }

            if (session is null)
            {
                try
                {
                    // MANUAL_STOP_NO_SESSION_CAS_INVARIANT:
                    // session不在を理由に状態を無条件Cancelledへ上書きしない。
                    // Scheduled/Startingだけを判定時世代で取消し、Recordingはruntime owner不明として保存する。
                    var latest = _store.GetById(reservationId);
                    if (latest is null)
                        return StopRecordingRequestResult.CreateFailed(reservationId);

                    ReservationLifecycleTransitionResult cancelResult;
                    if (latest.Status == ReservationStatus.Scheduled)
                    {
                        cancelResult = _store.TryFinalizeScheduledReservation(
                            latest.Id, latest.DataVersion, ReservationStatus.Cancelled, "manual_stop_no_session");
                    }
                    else if (latest.Status == ReservationStatus.Starting)
                    {
                        cancelResult = _store.TryCancelStartingReservation(
                            latest.Id, latest.DataVersion, "manual_stop_no_session");
                    }
                    else
                    {
                        SafeManualStopLog("MANUAL_STOP_NO_SESSION", $"R{reservationId}",
                            $"result=REJECTED status={latest.Status} dataVersion={latest.DataVersion} reason=runtime_owner_required action=keep_latest_state rule=recording_lifecycle_cas_contract");
                        return StopRecordingRequestResult.CreateFailed(reservationId);
                    }

                    if (!cancelResult.Applied)
                    {
                        SafeManualStopLog("MANUAL_STOP_NO_SESSION", $"R{reservationId}",
                            $"result=CAS_REJECTED status={latest.Status} reason={SafeValue(cancelResult.Reason)} dataVersion={cancelResult.PreviousDataVersion}->{cancelResult.CurrentDataVersion} action=keep_latest_state rule=recording_lifecycle_cas_contract");
                        return StopRecordingRequestResult.CreateFailed(reservationId);
                    }

                    _forceAllocationReevaluate = true;
                    return StopRecordingRequestResult.CreateAccepted(reservationId);
                }
                finally
                {
                    ReleaseManualStopRequest(reservationId);
                }
            }

            StartManualStopTask(session, ReservationStatus.Cancelled, reservationId, "録画を手動停止しました。");
            return StopRecordingRequestResult.CreateAccepted(reservationId);
        }
        catch (Exception ex)
        {
            ReleaseManualStopRequest(reservationId);
            SafeManualStopLog("MANUAL_STOP_REQUEST", $"R{reservationId}",
                $"result=FAILED action=release_request errorType={ex.GetType().Name} error={TrimForLog(ex.Message, 240)} rule=release_contract");
            return StopRecordingRequestResult.CreateFailed(reservationId);
        }
    }

    private bool TryRegisterManualStopRequest(int reservationId)
    {
        lock (_sessionGate)
            return _manualStopRequests.Add(reservationId);
    }

    private void ReleaseManualStopRequest(int reservationId)
    {
        lock (_sessionGate)
            _manualStopRequests.Remove(reservationId);
    }

    private void StartManualStopTask(RecordingSession session, ReservationStatus finalStatus, int reservationId, string completionMessage)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await StopSessionAsync(session, finalStatus).ConfigureAwait(false);
                    _forceAllocationReevaluate = true;
                    SafeManualStopLog("Scheduler", $"R{reservationId}", completionMessage);
                }
                catch (Exception ex)
                {
                    _forceAllocationReevaluate = true;
                    SafeManualStopLog("MANUAL_STOP_TASK", $"R{reservationId}",
                        $"result=FAILED action=release_request_continue_scheduler errorType={ex.GetType().Name} error={TrimForLog(ex.Message, 240)} rule=release_contract");
                }
                finally
                {
                    ReleaseManualStopRequest(reservationId);
                }
            });
        }
        catch
        {
            ReleaseManualStopRequest(reservationId);
            throw;
        }
    }

    private void SafeManualStopLog(string eventName, string title, string message)
    {
        try { _log.Add(eventName, title, message); }
        catch { }
    }

    private List<RecordingSession> FindSameOccurrenceActiveSessionsForManualStop(Reservation policyReservation, int excludedReservationId)
    {
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.Where(x => x.ReservationId != excludedReservationId).ToList();
        if (active.Count == 0) return new List<RecordingSession>();

        var result = new List<RecordingSession>();
        foreach (var session in active)
        {
            var candidate = _store.GetById(session.ReservationId);
            if (candidate is null) continue;
            if (!IsSameOccurrenceForManualStop(policyReservation, candidate)) continue;
            result.Add(session);
        }
        return result;
    }

    private int CancelSameOccurrenceScheduledReservationsForManualStop(Reservation policyReservation, int excludedReservationId)
    {
        var targets = _store.GetAll()
            .Where(x => x.Id != excludedReservationId)
            .Where(x => x.Status == ReservationStatus.Scheduled)
            .Where(x => IsSameOccurrenceForManualStop(policyReservation, x))
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();

        var cancelled = 0;
        foreach (var target in targets)
        {
            var result = _store.TryFinalizeScheduledReservation(
                target.Id, target.DataVersion, ReservationStatus.Cancelled, "same_occurrence_manual_stop");
            _log.Add("MANUAL_STOP_OCCURRENCE", $"R{excludedReservationId}",
                $"result={(result.Applied ? "SIBLING_CANCEL" : "SIBLING_CANCEL_CAS_REJECTED")} target=R{target.Id} reason=same_occurrence_manual_stop detail={SafeValue(result.Reason)} dataVersion={result.PreviousDataVersion}->{result.CurrentDataVersion} rule=release_contract");
            if (result.Applied) cancelled++;
        }
        return cancelled;
    }

    private static bool IsSameOccurrenceForManualStop(Reservation a, Reservation b)
    {
        if (a.NetworkId != b.NetworkId || a.TransportStreamId != b.TransportStreamId || a.ServiceId != b.ServiceId)
            return false;

        if (a.EventId != 0 && b.EventId != 0 && a.EventId == b.EventId)
            return true;

        var exactTime = a.StartTime == b.StartTime && a.EndTime == b.EndTime;
        if (exactTime) return true;

        var overlaps = a.StartTime < b.EndTime && b.StartTime < a.EndTime;
        if (!overlaps) return false;

        return string.Equals(NormalizeTitleForOccurrenceCompare(a.Title), NormalizeTitleForOccurrenceCompare(b.Title), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTitleForOccurrenceCompare(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        return Regex.Replace(title.Trim(), @"\s+", " ");
    }

    // ─── ポーリング ──────────────────────────────────────────────

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.Now;

        AuditReservationsAroundNow(now, "TickWindow", force: false);

        if (StopPhaseGate.IsStopping)
        {
            // release_contract: 録画停止中でも、既存sessionのworker消失・無進捗・予定終了監視は止めない。
            // 新規割当は各入口のStopPhaseGateで遅延させ、Tick全体を遮断しない。
            _log.Add("Scheduler", "STOP_PHASE_MONITOR_CONTINUE", "action=Tick reason=stop-phase-active monitoring=continue allocation=deferred");
        }

        // 0. 放送終了時刻を過ぎたのに scheduled のまま残っている予約を Cancelled に移行
        //    (無効予約・競合予約・録画失敗残骸など。予約リストを自動掃除する。)
        try
        {
            var expired = _store.ExpirePastScheduledReservations();
            if (expired > 0)
            {
                _log.Add("Scheduler", "Expire", $"放送終了予約を自動キャンセル: {expired}件");

                // release_contract:
                // Expire処理後の保険としてだけ実行する。通常Tickごとには走らせない。
                ClearTerminalConflictResiduesSafe("tick_after_expire");
            }
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", "Expire", $"過去予約掃除エラー: {ex.Message}");
        }

        try
        {
            _store.PurgeExpiredManualStoppedOccurrences(now);
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", "ManualStopOccurrence", $"期限切れ抑止キー掃除エラー: {ex.Message}");
        }

        // 1. 録画中セッションの健全性を先に確認する。
        //    予定終了より十分前に TvAIrEpgRec が消えている場合は、正常停止ルートを待たず中断として終端化する。
        //    停止境界付近は通常停止処理と競合させない。
        await FinalizeActiveRecordingSessionsWithMissingProcessAsync(now).ConfigureAwait(false);

        // 1a. 録画プロセスが生存していても、録画TSファイルが増加しない場合は実録画停止として扱う。
        //     起動時回収だけに頼らず、録画本線の実行中に検知して止める。
        await CheckActiveRecordingFileGrowthAsync(now, ct);

        // 1. 録画中セッションの時間追従を先に復旧する。
        //    録画中チューナー/セッションを維持したまま、最新EPGで予約EndとPlannedEndを更新する。
        //    ここで別チューナー確保や個別停止は行わず、状態更新後に共通割り当てルートを通す。
        await FollowActiveRecordingTimesAsync(now);
        RestoreActiveRecordingStatusesBeforeDecision(now, "tick_before_session_end");

        // 1b. 録画中セッションの終了チェック
        await CheckSessionEndsAsync(now);
        RestoreActiveRecordingStatusesBeforeDecision(now, "tick_after_session_end");

        // 1c. 終了時刻と安全猶予を過ぎても非終端のまま残った予約を、runtime ownerの不在を確認して終端へ収束する。
        //     UI側で過去行を隠してはならない。active sessionが存在する予約には一切触れない。
        ConvergePastNonTerminalReservationsWithoutRuntimeOwner(now);

        // 1b. 明示チェーン境界: 時間追従後の後続開始30秒前に前段を一斉停止
        if (_ini.PseudoContinuousRecording)
        {
            // チェーン検出状況をログ（録画中セッションがある場合のみ）
            bool hasActive;
            lock (_sessionGate) hasActive = _activeSessions.Values.Any(x => x.IsRecordingCommitted);
            if (hasActive)
            {
                var chains = _store.GetChains();
                if (chains.Count > 0)
                {
                    // GetChainsは保存済みチェーントポロジーのIDだけを返すため、
                    // IDリストをそのままログに出す（タイトル取得のためのDB追加呼び出しは行わない）
                    var chainDesc = string.Join(" / ", chains.Select(c =>
                        string.Join("→", c.Select(id => $"R{id}"))));
                    _log.Add("Scheduler", "Chain", $"チェーン構成: {chainDesc}");
                }
            }
            await CheckPseudoContinuousHandoffAsync(now, ct);
        }

        // 2. 録画接近情報はPreRec/開始前Admission用のtimeline正本だけ更新する。
        // normal EPG実行中は波単位占有が競合正本なので、録画接近によるworker preempt/解放は行わない。
        PublishRecordingTimelineEpgGate(now);

        // 3. 開始すべき予約を取得して起動
        // 全件再評価は重いので、起動直後 / 状態変化時 / 開始接近時 / 1分ごとに限定する。
        RestoreActiveRecordingStatusesBeforeDecision(now, "tick_before_due_scan");
        MaybeReevaluateForTick(now);

        // 録画開始を最優先にし、録画前EPG確認はこの後の補助キューで処理する。
        if (!await TryLaunchDueReservationsAsync(now, "TickDueScan", ct).ConfigureAwait(false))
            return;

        // source=Epg の録画前EPG確認は録画dueスキャン後に時差付きAdmissionで投入する。
        await RunDuePreRecordEpgEntriesAsync(now, ct).ConfigureAwait(false);

        // 4. Wakeタスク更新。
        // release_contract: 自動検索予約更新などからのWake再構築要求は視聴中負荷を避けて遅延・差分化されるため、
        // 毎Tickで期限到来分だけ処理する。通常の定期照合は従来どおり1分間隔。
        if (_taskSvc.ApplyDeferredWakeTaskIfDue())
        {
            _wakeUpdateCounter = 0;
        }
        else
        {
            _wakeUpdateCounter++;
            if (_wakeUpdateCounter >= WakeUpdateIntervalTicks)
            {
                _wakeUpdateCounter = 0;
                UpdateWakeTasksForMaintenance();
            }
        }
    }




    private async Task<bool> TryLaunchDueReservationsAsync(DateTime now, string scanSource, CancellationToken ct)
    {
        if (!await _recordingDueGate.WaitAsync(0, ct).ConfigureAwait(false))
            return true;
        try
        {
        // ORPHAN_STARTING_DUE_SCAN_REACHABILITY_INVARIANT:
        // StartRecordingAsync内に共通回収契約があっても、通常due候補をScheduledだけから列挙すると
        // owner/workerを失ったStartingは入口へ二度と到達できない。due時間内のStartingだけを先に
        // 共通回収へ通し、CASでScheduledへ戻ったものを直後の既存Scheduled scanへ自然に再投入する。
        // 正常Starting（single-flight ownerあり / active workerあり / 2秒未満）は一切変更しない。
        await RecoverDueOrphanStartingReservationsAsync(now, ct).ConfigureAwait(false);

        var scheduled = OrderDueReservationsForTransportBatchLaunch(_store.GetByStatus(ReservationStatus.Scheduled)
            .Where(r => r.IsEnabled)
            // source=Epg は予約リスト表示・Wake計算用のシステムエントリであり、
            // 通常録画のTvAIrEpgRec起動ルートには入れない。
            // ch未設定のEPG確認エントリが /rec 起動に流入すると、失敗ログと不要な状態変更を起こすため安全側で除外する。
            .Where(r => r.Source != ReservationSource.Epg)
            .Where(r => r.StartTime - TimeSpan.FromSeconds(_ini.PreStartMarginSeconds) <= now)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList());

        if (scheduled.Count > 0)
        {
            var dueIds = string.Join(",", scheduled.Select(r => $"R{r.Id}"));
            _log.Add("REC_DUE_SCAN", "Due",
                $"count={scheduled.Count} now={now:MM/dd HH:mm:ss} preStart={_ini.PreStartMarginSeconds}s ids={dueIds}");
            foreach (var due in scheduled)
                _log.Add("REC_DUE_SCAN", $"R{due.Id}", FormatReservationForAudit(due, "due"));
            AuditReservationsAroundNow(now, "DueForce", force: true);

            // 停止フェーズは物理チューナー単位で判定する。
            // due一覧全体を止めず、各StartRecordingAsyncで対象Tunerとの競合だけを遅延する。
        }

        var startCandidates = new List<Reservation>(scheduled.Count);
        foreach (var r in scheduled)
        {
            ct.ThrowIfCancellationRequested();

            lock (_sessionGate)
            {
                if (_activeSessions.ContainsKey(r.Id)) continue;
            }

            if (_store.IsManualStoppedOccurrenceSuppressed(r))
            {
                var cancel = _store.TryFinalizeScheduledReservation(
                    r.Id, r.DataVersion, ReservationStatus.Cancelled, "manual_stopped_occurrence_due_suppression");
                if (cancel.Applied)
                    _forceAllocationReevaluate = true;
                _log.Add("REC_START_DECISION", $"R{r.Id}",
                    $"result=SKIP reason=automatic_rerecord_after_manual_stop service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} rawTitleBlank={ReservationTitleDisplayContract.RawBlankFlag(r.Title)} source={r.Source} ruleId={(r.SourceRuleId?.ToString() ?? "-")} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} rule=release_contract");
                continue;
            }

            // release_contract: 競合のままdue到達した予約は、録画開始内部へ入る前に
            // ユーザー運用ログ正本へ1予約1回だけ落とす。REC_FAILEDとは分離する。
            // ここで扱うことで、同時刻に複数dueが並ぶ場合でも後続の正常録画開始に埋もれない。
            var startCandidate = r;
            if (r.IsConflicted)
            {
                var conflictGroup = ResolveGroup(r) ?? "-";
                var disposition = ResolveConflictedReservationForStart(r, conflictGroup, out var conflictTarget, out var epgBlock, out var latestExists);
                if (disposition == ConflictedStartDisposition.AllocationUnsettled)
                {
                    _log.Add("REC_START_DECISION", $"R{r.Id}",
                        $"result=DEFER reason=allocation_unsettled_at_due group={conflictGroup} terminalStatus=none preserveStatus=Scheduled action=retry_next_tick " + FormatReservationForAudit(conflictTarget, "allocation_unsettled_due"));
                    continue;
                }

                if (disposition == ConflictedStartDisposition.ActiveNormalEpgWave)
                {
                    // NORMAL_EPG_CONFLICT_HOLD_INVARIANT:
                    // 実行中normal EPGのwave占有は一時的な競合要因であり、due到達を理由に予約をFailedへ終端しない。
                    // Manual/Immediate等の入口種別で分岐せずScheduled+IsConflictedを保持し、
                    // EPGのComplete / ExplicitCancel / RealFailureでwave占有が解放された後、共通Allocation再評価から通常開始へ戻す。
                    _log.Add("REC_START_DECISION", $"R{r.Id}",
                        $"result=DEFER reason=active_normal_epg_wave_conflict group={conflictGroup} epgSource={SafeValue(epgBlock.Source)} epgSilent={epgBlock.Silent} epgRunGeneration={epgBlock.RunGeneration} epgOccupy={epgBlock.StartedAt:MM/dd HH:mm:ss}〜{EffectiveNormalEpgOccupationEnd(epgBlock):MM/dd HH:mm:ss} terminalStatus=none preserveStatus=Scheduled preserveConflict=True action=wait_for_common_reallocation_after_epg_terminal " + FormatReservationForAudit(conflictTarget, "normal_epg_conflict_held"));
                    continue;
                }

                if (disposition == ConflictedStartDisposition.TerminalConflict)
                {
                    _userEvents.AddRecordingSkippedByConflict(
                        conflictTarget,
                        r.Id,
                        conflictGroup,
                        "tuner_limit_exceeded");
                    _store.FinalizeSkippedByConflictAtDue(r.Id, conflictTarget.DataVersion, conflictGroup, "tuner_limit_exceeded");
                    _log.Add("REC_START_DECISION", $"R{r.Id}",
                        $"result=SKIP reason=conflict_due_user_event latestExists={latestExists} latestConflicted=True group={conflictGroup} terminalStatus=Failed userEvent=REC_SKIPPED_BY_CONFLICT " + FormatReservationForAudit(conflictTarget, "conflict_due_user_event"));
                    continue;
                }

                if (disposition == ConflictedStartDisposition.NotScheduled)
                    continue;

                startCandidate = conflictTarget;
                _log.Add("Scheduler", SafeValue(conflictTarget.ServiceName),
                    $"競合再評価: service=[{SafeValue(conflictTarget.ServiceName)}] title=[{ReservationDisplayTitle(conflictTarget.Title)}] id=R{conflictTarget.Id} 空きチューナーを確認できたため録画開始します。group={conflictGroup} rule=release_contract");
            }

            startCandidates.Add(startCandidate);
        }

        if (startCandidates.Count > 0)
        {
            // 多チューナー環境では、前のworkerがACTIVEになるまで逐次awaitすると、
            // 設定グループ別Admissionより上流で全開始要求が直列化される。
            // 予約IDごとのsingle-flight ownerを同時に投入し、同一グループのcadenceと
            // 異なるグループの並列性は、共通割当確定後のApplyRecordingLaunchAdmissionAsyncだけに所有させる。
            _log.Add("REC_DUE_DISPATCH", "START",
                $"count={startCandidates.Count} ids={string.Join(",", startCandidates.Select(candidate => $"R{candidate.Id}"))} parallelByReservation=True admissionByConfiguredGroup=True owner=reservation_id_singleflight rule=recording_due_parallel_dispatch_contract");

            var startTasks = startCandidates
                .Select(candidate => StartDueReservationIsolatedAsync(candidate, ct))
                .ToArray();
            var startResults = await Task.WhenAll(startTasks).ConfigureAwait(false);
            var started = startResults.Count(result => result);

            _log.Add("REC_DUE_DISPATCH", "END",
                $"count={startResults.Length} started={started} notStarted={startResults.Length - started} parallelByReservation=True admissionByConfiguredGroup=True rule=recording_due_parallel_dispatch_contract");
        }

            return true;
        }
        finally
        {
            _recordingDueGate.Release();
        }
    }

    private async Task<bool> StartDueReservationIsolatedAsync(Reservation reservation, CancellationToken ct)
    {
        try
        {
            return await StartRecordingAsync(reservation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 1予約の開始例外で同じdueバッチの他予約を巻き込まない。
            // StartRecordingAsync側で当該予約の開始claimは収束済みなので、ここでは監査だけを行う。
            _log.Add("REC_DUE_DISPATCH", $"R{reservation.Id}",
                $"result=NOT_STARTED reason=start_exception_isolated exception={ex.GetType().Name} message={SafeValue(ex.Message)} otherReservationsContinue=True rule=recording_due_parallel_dispatch_contract");
            return false;
        }
    }


    private static bool IsPreRecordEpgEntry(Reservation r)
        => r.SourceRuleId.HasValue && ReservationIntentContract.IsPreRecordEpg(r);

    private Task RunDuePreRecordEpgEntriesAsync(DateTime now, CancellationToken ct)
    {
        var candidates = _store.GetByStatus(ReservationStatus.Scheduled)
            .Where(r => r.Source == ReservationSource.Epg)
            .Where(r => r.IsEnabled)
            .Where(IsPreRecordEpgEntry)
            .Where(r => r.StartTime <= now)
            .Where(r => !_activePreRecordEpgEntries.ContainsKey(r.Id))
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        if (candidates.Count == 0) return Task.CompletedTask;

        var plans = new List<PreRecordEpgPlan>();
        foreach (var epg in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var plan = TryBuildPreRecordEpgPlan(epg, now);
            if (plan is not null)
                plans.Add(plan);
        }
        if (plans.Count == 0) return Task.CompletedTask;

        // PRE_RECORD_EPG_CAPACITY_ADMISSION_INVARIANT:
        // 同一放送波を1本に直列化しない。Recording-roleのTvAIr内部空きTunerを正本に、
        // このtickで重複しないTunerへ最大数まで同時Admissionする。固定3/6本は禁止。
        // ここで入らなかった候補は次tickで再評価し、将来割当Tunerを先取りしない。
        // 管理外TVTestの利用状況はAdmission条件に含めない。物理PT3側の競合・再配置はBonDriver_PTx/PT3の責務であり、
        // 「safe」はTvAIr内部のRecording-role/Lease整合だけを意味する。外部利用回避の意味を持たせてはならない。
        var selectedTuners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var started = 0;
        foreach (var originalPlan in plans.OrderBy(p => p.Epg.StartTime).ThenBy(p => p.DueStart).ThenBy(p => p.Epg.Id))
        {
            ct.ThrowIfCancellationRequested();
            var schedulerNow = DateTime.Now;
            var probeEnd = schedulerNow.AddSeconds(originalPlan.ProbeCeilingSeconds);
            var runtimeTuner = SelectPreRecordRuntimeTuner(originalPlan.Group, probeEnd, originalPlan.Parent, selectedTuners);
            if (string.IsNullOrWhiteSpace(runtimeTuner))
            {
                _log.Add("EPG_SCHEDULER", "PreRecAdmission",
                    $"admission=R{originalPlan.Epg.Id} result=DEFER reason=no_distinct_safe_tuner_this_tick epg=R{originalPlan.Epg.Id} parent=R{originalPlan.Parent.Id} group={SafeValue(originalPlan.Group)} selectedTuners=[{string.Join(',', selectedTuners)}] action=reevaluate_next_tick policy=physical_recording_tuner_capacity rule=pre_record_epg_capacity_admission_contract");
                continue;
            }

            var plan = originalPlan with { RuntimeTunerName = runtimeTuner };
            selectedTuners.Add(runtimeTuner);
            if (!_activePreRecordEpgEntries.TryAdd(plan.Epg.Id, 0))
                continue;

            started++;
            _log.Add("EPG_SCHEDULER", "PreRecAdmission",
                $"admission=R{plan.Epg.Id} result=START wave=0 reason=distinct_safe_recording_tuner epg=R{plan.Epg.Id} parent=R{plan.Parent.Id} group={SafeValue(plan.Group)} tuner={SafeValue(plan.RuntimeTunerName)} dueStart={plan.DueStart:MM/dd HH:mm:ss} secondsUntilDueStart={plan.SecondsUntilDueStart} admittedThisTick={started} selectedTuners=[{string.Join(',', selectedTuners)}] policy=physical_recording_tuner_capacity_environment_dynamic rule=pre_record_epg_capacity_admission_contract");
            _ = Task.Run(() => ExecutePreRecordEpgPlanAsync(plan, ct), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    private PreRecordEpgPlan? TryBuildPreRecordEpgPlan(Reservation epg, DateTime now)
    {
        if (epg.EndTime <= now)
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=window_expired epg=R{epg.Id} parent={(epg.SourceRuleId.HasValue ? $"R{epg.SourceRuleId.Value}" : "-")} start={epg.StartTime:MM/dd HH:mm:ss} end={epg.EndTime:MM/dd HH:mm:ss} now={now:MM/dd HH:mm:ss} rule=release_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
            return null;
        }

        var parent = epg.SourceRuleId.HasValue ? _store.GetById(epg.SourceRuleId.Value) : null;
        if (parent is null)
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=parent_missing epg=R{epg.Id} parent={(epg.SourceRuleId.HasValue ? $"R{epg.SourceRuleId.Value}" : "-")} rule=release_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
            return null;
        }

        if (IsExpiredPreRecordEpgForParent(epg, parent, now))
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=deadline_already_passed epg=R{epg.Id} parent=R{parent.Id} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} epgStart={epg.StartTime:MM/dd HH:mm:ss} parentCreated={parent.CreatedAt:MM/dd HH:mm:ss} epgCreated={epg.CreatedAt:MM/dd HH:mm:ss} now={now:MM/dd HH:mm:ss} action=recording_priority rule=release_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_deadline_already_passed");
            return null;
        }

        if (parent.IsUserChain && parent.UserChainPreviousId.HasValue)
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=user_chain_child epg=R{epg.Id} parent=R{parent.Id} predecessor=R{parent.UserChainPreviousId.Value} action=use_chain_root_prerec_time_follow rule=pre_record_epg_chain_root_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_user_chain_child");
            return null;
        }

        if (parent.Status == ReservationStatus.Recording)
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=parent_already_recording epg=R{epg.Id} parent=R{parent.Id} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=release_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
            return null;
        }

        if (parent.Status != ReservationStatus.Scheduled || !parent.IsEnabled || parent.IsConflicted)
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=parent_not_recordable epg=R{epg.Id} parent=R{parent.Id} status={parent.Status} enabled={parent.IsEnabled} conflicted={parent.IsConflicted} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=release_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
            return null;
        }

        var group = ResolveGroup(parent);
        if (string.IsNullOrWhiteSpace(group))
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=group_unresolved epg=R{epg.Id} parent=R{parent.Id} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=release_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
            return null;
        }

        // Tuner空きなしは録画前EPG確認の明示例外。
        // 生成時の一過性状態ではなく、実際のdue時点で録画用TunerPoolを正本として判定する。
        if (!_tunerPool.HasFreeSlot(group))
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=no_free_recording_tuner epg=R{epg.Id} parent=R{parent.Id} group={SafeValue(group)} action=keep_original_recording_time_no_retry viewingTunerUse=False rule=pre_record_epg_tuner_availability_exception_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_no_free_recording_tuner");
            _userEvents.AddPreRecordEpgFailed(parent, "no_free_recording_tuner");
            return null;
        }

        var dueStart = parent.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
        var schedulerNow = DateTime.Now;
        var secondsUntilDueStart = (int)Math.Floor((dueStart - schedulerNow).TotalSeconds);
        if (secondsUntilDueStart <= PreRecordEpgStopBeforeRecordingDueSeconds)
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=too_close_to_recording epg=R{epg.Id} parent=R{parent.Id} secondsUntilDueStart={secondsUntilDueStart} stopBeforeDueSeconds={PreRecordEpgStopBeforeRecordingDueSeconds} dueStart={dueStart:MM/dd HH:mm:ss} now={schedulerNow:MM/dd HH:mm:ss} action=recording_priority service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=release_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
            return null;
        }

        var maxAllowedSeconds = secondsUntilDueStart - PreRecordEpgStopBeforeRecordingDueSeconds;
        if (maxAllowedSeconds < PreRecordEpgMinimumProbeSeconds)
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=insufficient_probe_window epg=R{epg.Id} parent=R{parent.Id} secondsUntilDueStart={secondsUntilDueStart} maxAllowedSeconds={maxAllowedSeconds} minProbeSeconds={PreRecordEpgMinimumProbeSeconds} rule=release_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
            return null;
        }

        // PRE_RECORD_EPG_RUNTIME_TUNER_INVARIANT:
        // EPG確認はプリチューンを兼務しない。親予約の将来割当Tunerを子Intentへ投影せず、
        // 実行時に同一放送波の空き録画Tunerを取得する。チェーンrootも同じ扱い。
        var configuredPreRecordMinutes = SettingsDefaults.ResolveEnabledEpgPreRecordMinutes(_ini.EpgPreRecordMinutes);
        var configuredAdmissionBudgetSeconds = configuredPreRecordMinutes * 60;
        // 設定値は「最早何分前から確認を開始できるか」の予算。workerは対象EventIdentityを見つけたら即終了し、
        // 見つからない場合だけhard deadlineまで観測する。本録画dueを越えて使い切ってはならない。
        var probeCeilingSeconds = parent.IsUserChain
            ? Math.Max(PreRecordEpgMinimumProbeSeconds, Math.Min(ProtectedUserChainPreRecordProbeCeilingSeconds, maxAllowedSeconds))
            : Math.Max(PreRecordEpgMinimumProbeSeconds, Math.Min(configuredAdmissionBudgetSeconds, maxAllowedSeconds));
        var runtimeTuner = SelectPreRecordRuntimeTuner(group, schedulerNow.AddSeconds(probeCeilingSeconds), parent);
        if (string.IsNullOrWhiteSpace(runtimeTuner))
        {
            _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                $"result=SKIP reason=no_safe_free_recording_tuner epg=R{epg.Id} parent=R{parent.Id} group={SafeValue(group)} probeEnd={schedulerNow.AddSeconds(probeCeilingSeconds):MM/dd HH:mm:ss} action=keep_original_recording_time_no_retry rule=pre_record_epg_runtime_tuner_contract");
            TryCompletePreRecordEpgEntry(epg, "skip_no_safe_free_recording_tuner");
            _userEvents.AddPreRecordEpgFailed(parent, "no_safe_free_recording_tuner");
            return null;
        }

        return new PreRecordEpgPlan(
            Epg: epg,
            Parent: parent,
            Group: group,
            AdmissionGroup: NormalizePreRecordAdmissionGroup(group),
            DueStart: dueStart,
            SecondsUntilDueStart: secondsUntilDueStart,
            ProbeCeilingSeconds: probeCeilingSeconds,
            ConfiguredPreRecordMinutes: configuredPreRecordMinutes,
            RuntimeTunerName: runtimeTuner);
    }

    private async Task ExecutePreRecordEpgPlanAsync(PreRecordEpgPlan plan, CancellationToken schedulerToken)
    {
        var epg = plan.Epg;
        var parent = plan.Parent;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(schedulerToken);
            var secondsUntilDueStart = (int)Math.Floor((plan.DueStart - DateTime.Now).TotalSeconds);
            var cancelAfterSeconds = Math.Max(PreRecordEpgMinimumProbeSeconds, secondsUntilDueStart - PreRecordEpgStopBeforeRecordingDueSeconds);
            cts.CancelAfter(TimeSpan.FromSeconds(cancelAfterSeconds));
            var ct = cts.Token;

            var status = _epgCapture.GetStatus();
            var normalEpgOnSameWave = string.Equals(status.Phase, "running", StringComparison.OrdinalIgnoreCase)
                && string.Equals(status.RunPurpose, "normal_epg_capture", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(status.TargetScope, plan.Group, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status.TargetScope, "Both", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status.TargetScope, "ALL", StringComparison.OrdinalIgnoreCase));
            if (normalEpgOnSameWave)
            {
                // PRE_RECORD_EPG_SINGLE_ATTEMPT_INVARIANT:
                // 開始済みnormal EPGはPreRecからも停止しない。同波normal EPG実行中なら、この予定時刻の
                // PreRec確認だけを失敗終端し、親録画は元の予定時刻で進める。再試行・worker preemptは行わない。
                var finalized = TryCompletePreRecordEpgEntry(epg, "normal_epg_running_single_attempt");
                _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                    $"result=SKIP reason=normal_epg_running epg=R{epg.Id} parent=R{parent.Id} group={SafeValue(plan.Group)} phase={status.Phase} runPurpose={SafeValue(status.RunPurpose)} targetScope={SafeValue(status.TargetScope)} prerecEntryFinalized={finalized} action=keep_original_recording_time_no_retry_no_preempt service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=pre_record_epg_single_attempt_contract");
                _userEvents.AddPreRecordEpgFailed(parent, "normal_epg_running");
                return;
            }

            _log.Add("PRE_REC_EPG_START", $"R{epg.Id}",
                $"epg=R{epg.Id} parent=R{parent.Id} group={plan.Group} admissionGroup={plan.AdmissionGroup} tuner={SafeValue(plan.RuntimeTunerName)} recordingTuner={SafeValue(parent.TunerName)} epgAction=time_follow_probe window={epg.StartTime:MM/dd HH:mm:ss}〜{epg.EndTime:MM/dd HH:mm:ss} probeSafetyCeilingSeconds={plan.ProbeCeilingSeconds} configuredPreRecordMinutes={plan.ConfiguredPreRecordMinutes} dueStart={plan.DueStart:MM/dd HH:mm:ss} stopBeforeDueSeconds={PreRecordEpgStopBeforeRecordingDueSeconds} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} nid={parent.NetworkId} tsid={parent.TransportStreamId} sid={parent.ServiceId} eventId={parent.EventId} policy=staggered_parallel_time_follow_probe rule=release_contract");

            _log.Add("PRE_REC_EPG_RUNTIME_PLAN", $"R{epg.Id}",
                $"parent=R{parent.Id} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} group={plan.Group} runtimeTuner={SafeValue(plan.RuntimeTunerName)} expectedNid={parent.NetworkId} expectedTsid={parent.TransportStreamId} expectedSid={parent.ServiceId} expectedEventId={parent.EventId} chainScope={(parent.IsUserChain ? "root" : "single")} policy=time_follow_probe_runtime_tuner_no_pretune rule=pre_record_epg_runtime_tuner_contract");


            // PRE_RECORD_EPG_INDEPENDENT_PROBE_CONTRACT:
            // Chain root and non-chain PreRec both use the independent probe owner. The only protected
            // chain difference is the established DB-backed series-follow input; normal EPG run/status
            // state is never borrowed by PreRec.
            var result = await _epgCapture.RunIndependentPreRecordProbeAsync(
                ct,
                plan.Group,
                parent.NetworkId,
                parent.TransportStreamId,
                parent.ServiceId,
                parent.ServiceName,
                parent.EventId,
                parent.StartTime,
                parent.EndTime,
                plan.ProbeCeilingSeconds,
                plan.RuntimeTunerName,
                preserveUserChainDbSeriesFollow: parent.IsUserChain).ConfigureAwait(false);

            // PRE_RECORD_EPG_SINGLE_ATTEMPT_INVARIANT:
            // 成功・失敗を問わず、この予定時刻でのworker/probe実行は一度だけで終了する。
            // 実行証拠が得られない場合はTimeFollowを適用せず、親予約の元時刻を維持する。
            var probeCompleted = result.Success && result.CompletedGroups > 0;
            if (!probeCompleted)
            {
                var finalized = TryCompletePreRecordEpgEntry(epg, "probe_finished_without_execution_evidence_single_attempt");
                _log.Add("PRE_REC_EPG_RESULT", $"R{epg.Id}",
                    $"result=FINALIZED_WITHOUT_TIME_FOLLOW epg=R{epg.Id} parent=R{parent.Id} probeResult={SafeValue(result.RunResult)} success={result.Success} completedGroups={result.CompletedGroups}/{result.TotalGroups} observedEvents={result.PreRecordEvents.Count} dbImportedEvents={result.ImportedEvents} prerecEntryFinalized={finalized} action=keep_original_recording_time_no_retry service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=pre_record_epg_single_attempt_contract");
                _userEvents.AddPreRecordEpgFailed(parent, $"probe_{result.RunResult.ToLowerInvariant()}");
                return;
            }

            var completed = TryCompletePreRecordEpgEntry(epg, "probe_finished_with_execution_evidence");
            var latestParent = _store.GetById(parent.Id) ?? parent;
            if (!completed || !IsRecordableForPreRecordFollow(latestParent))
            {
                _log.Add("PRE_REC_EPG_RESULT", $"R{epg.Id}",
                    $"result=SKIP_AFTER_PROBE epg=R{epg.Id} parent=R{parent.Id} probe=completed reason={(completed ? "parent_not_recordable_after_probe" : "epg_state_changed_during_probe")} parentStatus={latestParent.Status} parentEnabled={latestParent.IsEnabled} parentConflicted={latestParent.IsConflicted} action=do_not_apply_time_follow service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=release_contract");
                return;
            }

            var followTargets = GetPreRecordTimeFollowTargets(latestParent);
            RememberPreRecordTimeFollowEvidence(followTargets, result.PreRecordEvents);

            IReadOnlyList<TimeFollowApplyResult> followResults;
            if (result.PreRecordEvents.Count > 0)
            {
                // PRE_REC_OBSERVED_TIME_SINGLE_SOURCE_INVARIANT:
                // 通常予約・明示チェーンとも、今回のPreRec workerが実際に観測したsnapshotを時刻追従の正本にする。
                // ProgramGuide DBへ書き戻した値を再読込すると、古いDB-first投影へ巻き戻るため時刻正本には使わない。
                followResults = _store.ApplyTimeFollowingDetailedFromObservedEvents(
                    followTargets,
                    result.PreRecordEvents,
                    allowUserChainSeriesFollow: latestParent.IsUserChain);
                _log.Add("PRE_REC_EPG_FOLLOW_SOURCE", $"R{epg.Id}",
                    $"result=OBSERVED_SNAPSHOT parent=R{latestParent.Id} observedEvents={result.PreRecordEvents.Count} dbReadForProbeResult=False chainScope={(latestParent.IsUserChain ? "root_series" : "single")} rule=pre_record_epg_probe_snapshot_contract");
            }
            else
            {
                // probe snapshotを取得できずParseAndStoreだけ成功した場合は、DBのseries情報からチェーン判定を継続する。
                followResults = _store.ApplyTimeFollowingDetailed(
                    followTargets,
                    _programEvents,
                    allowUserChainSeriesFollow: latestParent.IsUserChain);
                _log.Add("PRE_REC_EPG_FOLLOW_SOURCE", $"R{epg.Id}",
                    $"result=DB_FALLBACK_NO_OBSERVED_SNAPSHOT parent=R{latestParent.Id} observedEvents=0 chainScope={(latestParent.IsUserChain ? "root_series" : "single")} rule=pre_record_epg_probe_snapshot_contract");
            }
            LogPreRecordTimeFollow(epg, latestParent, followResults);

            if (followResults.Any(x => x.Updated))
            {
                _allocationRoute.Run(new ReservationAllocationRouteRequest(
                    Source: "ReservationScheduler",
                    Action: "Reevaluate:PreRecTimeFollow",
                    RunKeywordMatcher: false,
                    SyncProgramRuleReservations: true,
                    ReevaluateAllocations: true,
                    RefreshPreRecordEpgEntries: false,
                    RefreshWakeTask: true,
                    EmitConflictLogs: true,
                    ConflictLogCategory: "PRE_REC_EPG",
                    ConflictLogTitle: "Conflict(PreRecTimeFollow)"));
            }

            var followUpdated = followResults.Count(x => x.Updated);
            var followOutcome = followUpdated > 0 ? "UPDATED" : "NO_CHANGE_OR_NOT_FOUND";
            _log.Add("PRE_REC_EPG_RESULT", $"R{epg.Id}",
                $"result={(result.Success ? "OK" : "FAIL")} epg=R{epg.Id} parent=R{parent.Id} probe=completed followOutcome={followOutcome} timeFollowUpdated={followUpdated} action=keep_or_update_parent_by_time_follow service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=release_contract");
            if (!result.Success)
                _userEvents.AddPreRecordEpgFailed(latestParent, "probe_failed");
        }
        catch (OperationCanceledException) when (!schedulerToken.IsCancellationRequested)
        {
            var finalized = TryCompletePreRecordEpgEntry(epg, "recording_due_priority_cancelled_single_attempt");
            _log.Add("PRE_REC_EPG_RESULT", $"R{epg.Id}",
                $"result=FINALIZED_WITHOUT_TIME_FOLLOW epg=R{epg.Id} parent=R{parent.Id} reason=recording_due_priority prerecEntryFinalized={finalized} action=keep_original_recording_time_no_retry service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=pre_record_epg_single_attempt_contract");
        }
        catch (OperationCanceledException)
        {
            var finalized = TryCompletePreRecordEpgEntry(epg, "scheduler_stop_cancelled_single_attempt");
            _log.Add("PRE_REC_EPG_RESULT", $"R{epg.Id}",
                $"result=FINALIZED_WITHOUT_TIME_FOLLOW epg=R{epg.Id} parent=R{parent.Id} reason=scheduler_stop prerecEntryFinalized={finalized} action=keep_original_recording_time_no_retry service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=pre_record_epg_single_attempt_contract");
        }
        catch (Exception ex)
        {
            var finalized = TryCompletePreRecordEpgEntry(epg, "execution_exception_single_attempt");
            _log.Add("PRE_REC_EPG_RESULT", $"R{epg.Id}",
                $"result=FINALIZED_WITHOUT_TIME_FOLLOW epg=R{epg.Id} parent=R{parent.Id} error={ex.GetType().Name}:{SafeValue(ex.Message)} prerecEntryFinalized={finalized} action=keep_original_recording_time_no_retry service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=pre_record_epg_single_attempt_contract");
            _userEvents.AddPreRecordEpgFailed(parent, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _activePreRecordEpgEntries.TryRemove(epg.Id, out _);
        }
    }


    private IReadOnlyList<Reservation> GetPreRecordTimeFollowTargets(Reservation parent)
    {
        if (!parent.IsUserChain)
            return new[] { parent };

        // PRE_RECORD_EPG_CHAIN_SERIES_FOLLOW_INVARIANT:
        // EPG取得ownerはroot親1件だけ。取得済みの同一サービスEPGスナップショットを、
        // root親と未開始の子へ各EventIdentityで適用する。子ごとのworker/Tuner取得は行わない。
        var rootId = parent.UserChainRootId ?? parent.Id;
        var targets = _store.GetAll()
            .Where(x => x.IsUserChain
                && (x.Id == rootId || x.UserChainRootId == rootId)
                && x.Status == ReservationStatus.Scheduled
                && x.IsEnabled
                && IsSameServiceIdentity(parent, x))
            .OrderBy(x => x.StartTime)
            .ThenBy(x => x.Id)
            .ToList();

        if (targets.All(x => x.Id != parent.Id))
            targets.Insert(0, parent);

        _log.Add("PRE_REC_EPG_CHAIN_FOLLOW_TARGETS", $"R{parent.Id}",
            $"result=RESOLVED root=R{rootId} owner=R{parent.Id} count={targets.Count} targets=[{string.Join(',', targets.Select(x => $"R{x.Id}"))}] source=single_parent_prerec_snapshot childWorkers=0 extraTunerLease=0 rule=pre_record_epg_chain_root_contract");
        return targets;
    }

    private string? SelectPreRecordRuntimeTuner(string group, DateTime probeEnd, Reservation parent, ISet<string>? excludedTuners = null)
    {
        var normalizedGroup = NormalizePreRecordAdmissionGroup(group);
        var now = DateTime.Now;
        var futureAssigned = _store.GetByStatus(ReservationStatus.Scheduled)
            .Where(r => r.Source != ReservationSource.Epg
                && r.IsEnabled
                && !r.IsConflicted
                && !string.IsNullOrWhiteSpace(r.TunerName))
            .ToList();

        var candidates = _tunerPool.GetStatus()
            .Where(s => string.Equals(NormalizePreRecordAdmissionGroup(s.Group), normalizedGroup, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(IniSettingsService.NormalizeTunerRole(s.Role), "Viewing", StringComparison.OrdinalIgnoreCase)
                && s.UsageKind == TunerUsageKind.Free
                && (excludedTuners is null || !excludedTuners.Contains(s.Name)))
            .Select(slot =>
            {
                var nextDue = futureAssigned
                    .Where(r => string.Equals(r.TunerName, slot.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.StartTime.AddSeconds(-_ini.PreStartMarginSeconds))
                    .Where(due => due > now)
                    .DefaultIfEmpty(DateTime.MaxValue)
                    .Min();
                return new { Slot = slot, NextDue = nextDue };
            })
            // 確認workerがそのTunerの次録画開始準備へ食い込まない候補だけを使う。
            .Where(x => x.NextDue >= probeEnd)
            // 直近録画まで最も余裕があるTunerを優先し、同条件は物理slot順で固定する。
            .OrderByDescending(x => x.NextDue)
            .ThenBy(x => x.Slot.SlotIndex)
            .ToList();

        var selected = candidates.FirstOrDefault();
        _log.Add("PRE_REC_EPG_RUNTIME_TUNER", $"R{parent.Id}",
            $"result={(selected is null ? "NONE" : "SELECTED")} group={normalizedGroup} selected={(selected?.Slot.Name ?? "-")} probeEnd={probeEnd:MM/dd HH:mm:ss} " +
            $"freeCandidates=[{string.Join(',', candidates.Select(x => $"{x.Slot.Name}:nextDue={(x.NextDue == DateTime.MaxValue ? "none" : x.NextDue.ToString("HH:mm:ss"))}"))}] " +
            $"recordingAssignedTuner={SafeValue(parent.TunerName)} excluded=[{(excludedTuners is null ? "-" : string.Join(',', excludedTuners))}] selection=runtime_not_persisted rule=pre_record_epg_runtime_tuner_contract");
        return selected?.Slot.Name;
    }

    private static string NormalizePreRecordAdmissionGroup(string group)
    {
        if (string.Equals(group, "GR", StringComparison.OrdinalIgnoreCase)) return "GR";
        if (group.Contains("BS", StringComparison.OrdinalIgnoreCase) || group.Contains("CS", StringComparison.OrdinalIgnoreCase)) return "BSCS";
        return group.Trim();
    }

    private sealed record PreRecordEpgPlan(
        Reservation Epg,
        Reservation Parent,
        string Group,
        string AdmissionGroup,
        DateTime DueStart,
        int SecondsUntilDueStart,
        int ProbeCeilingSeconds,
        int ConfiguredPreRecordMinutes,
        string RuntimeTunerName);

    private static bool IsExpiredPreRecordEpgForParent(Reservation epgEntry, Reservation parent, DateTime now)
    {
        // release_contract: 予約作成・再構築より前に予定されていた録画前EPG確認を後追い実行しない。
        // 通常のスケジューラtick遅延では parent.CreatedAt <= epgEntry.StartTime なので実行を許可する。
        var parentCreated = parent.CreatedAt == default ? DateTime.MinValue : parent.CreatedAt;
        var epgCreated = epgEntry.CreatedAt == default ? DateTime.MinValue : epgEntry.CreatedAt;
        return parentCreated > epgEntry.StartTime.AddSeconds(5)
            || epgCreated > epgEntry.StartTime.AddSeconds(5);
    }

    private bool TryCompletePreRecordEpgEntry(Reservation epgEntry, string reason)
    {
        var current = _store.GetById(epgEntry.Id);
        if (current is null)
        {
            _log.Add("PRE_REC_EPG_RESULT", $"R{epgEntry.Id}",
                $"result=SKIP_STATUS_UPDATE epg=R{epgEntry.Id} reason={reason} currentStatus=Missing action=keep_missing_state rule=release_contract");
            return false;
        }

        if (current.Status == ReservationStatus.Cancelled)
        {
            _log.Add("PRE_REC_EPG_RESULT", $"R{epgEntry.Id}",
                $"result=SKIP_STATUS_UPDATE epg=R{epgEntry.Id} reason={reason} currentStatus=Cancelled action=keep_cancelled_state rule=release_contract");
            return false;
        }

        // 物理Tunerは実行時資源であり、完了Intentへ保存・再投影しない。
        var latestForComplete = _store.GetById(epgEntry.Id);
        if (latestForComplete is null || latestForComplete.Status != ReservationStatus.Scheduled)
        {
            _log.Add("PRE_REC_EPG_RESULT", $"R{epgEntry.Id}",
                $"result=SKIP_STATUS_UPDATE epg=R{epgEntry.Id} reason={reason} currentStatus={(latestForComplete?.Status.ToString() ?? "Missing")} action=keep_latest_state rule=release_contract");
            return false;
        }

        var complete = _store.TryFinalizeScheduledReservation(
            latestForComplete.Id, latestForComplete.DataVersion, ReservationStatus.Completed, "pre_record_epg_complete");
        if (complete.Applied)
            return true;

        // PRE_RECORD_EPG_TERMINAL_CAS_INVARIANT:
        // CAS競合はprobe再実行理由にしない。最新状態がまだScheduledの場合だけ、
        // 最新DataVersionで終端CASを一度収束させる。
        var retryCurrent = _store.GetById(epgEntry.Id);
        if (retryCurrent?.Status == ReservationStatus.Scheduled)
        {
            var retry = _store.TryFinalizeScheduledReservation(
                retryCurrent.Id, retryCurrent.DataVersion, ReservationStatus.Completed, "pre_record_epg_complete_cas_convergence");
            if (retry.Applied)
                return true;

            complete = retry;
            retryCurrent = _store.GetById(epgEntry.Id);
        }

        var terminalAlready = retryCurrent?.Status is ReservationStatus.Completed or ReservationStatus.Failed or ReservationStatus.Cancelled;
        _log.Add("PRE_REC_EPG_RESULT", $"R{epgEntry.Id}",
            $"result={(terminalAlready ? "ALREADY_TERMINAL" : "COMPLETED_CAS_REJECTED")} epg=R{epgEntry.Id} reason={reason} detail={SafeValue(complete.Reason)} dataVersion={complete.PreviousDataVersion}->{complete.CurrentDataVersion} latestStatus={(retryCurrent?.Status.ToString() ?? "Missing")} action=do_not_rerun_probe rule=pre_record_epg_single_attempt_contract");
        return terminalAlready;
    }

    private static bool IsRecordableForPreRecordFollow(Reservation parent)
    {
        return parent.Status == ReservationStatus.Scheduled
            && parent.IsEnabled
            && !parent.IsConflicted;
    }

    private void LogPreRecordTimeFollow(Reservation epgEntry, Reservation parent, IReadOnlyList<TimeFollowApplyResult> results)
    {
        var updated = results.Count(x => x.Updated);
        _log.Add("EPG_SCHEDULER", "TimeFollowSummary",
            $"source=PreRecEpg target=R{parent.Id} epg=R{epgEntry.Id} inspected={results.Count} updated={updated} rule=release_contract");

        foreach (var r in results)
        {
            _log.Add("EPG_SCHEDULER", r.Updated ? "TimeFollowUpdated" : "TimeFollowNoChange",
                $"source=PreRecEpg parent=R{r.ReservationId} epg=R{epgEntry.Id} result={(r.Updated ? "UPDATED" : "NO_CHANGE")} reason={r.Reason} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} old={r.OldStart:MM/dd HH:mm:ss}〜{r.OldEnd:MM/dd HH:mm:ss} new={(r.NewStart.HasValue ? r.NewStart.Value.ToString("MM/dd HH:mm:ss") : "-")}〜{(r.NewEnd.HasValue ? r.NewEnd.Value.ToString("MM/dd HH:mm:ss") : "-")} rule=release_contract");
            var latest = _store.GetById(r.ReservationId);
            if (latest is null)
                continue;

            if (r.Updated && r.NewStart.HasValue && r.NewEnd.HasValue)
            {
                _userEvents.AddTimeFollowUpdated(latest, r.OldStart, r.OldEnd, r.NewStart.Value, r.NewEnd.Value);
            }
            else
            {
                _userEvents.AddPreRecordEpgCheckedNoChange(latest);
            }
        }
    }

    // ─── 録画開始 ────────────────────────────────────────────────


    private async Task<bool> StartRecordingAsync(Reservation r, CancellationToken ct, bool chainBoundaryLaunch = false, ChainReleaseStartEvidence? chainReleaseEvidence = null)
    {
        // ORPHAN_STARTING_COMMON_ENTRY_INVARIANT:
        // Starting回収をowner終了時だけに限定すると、プロセス中断後のStartingが通常due・境界retry・
        // 物理解放handoffの全入口で永久に残る。既存ownerまたは実workerがある場合は触らず、
        // owner/workerとも不在で更新から2秒を超えたStartingだけをCASでScheduledへ戻す。
        var recovered = await TryRecoverOrphanStartingAtCommonEntryAsync(r).ConfigureAwait(false);
        if (recovered is null)
            return false;
        r = recovered;

        var trigger = chainReleaseEvidence is not null
            ? "chain_physical_release_handoff"
            : chainBoundaryLaunch
                ? "chain_boundary_retry"
                : "normal_due";
        var owner = new RecordingStartOwnerEntry
        {
            OwnerId = Guid.NewGuid().ToString("N"),
            Trigger = trigger,
            AcquiredAt = DateTime.Now,
            ProcessId = Environment.ProcessId
        };

        if (!_recordingStartInProgress.TryAdd(r.Id, owner))
        {
            if (!_recordingStartInProgress.TryGetValue(r.Id, out var existingOwner))
            {
                // TryAdd失敗直後にownerが完了していた場合は、固定待機を追加せず直ちに正規入口を再試行する。
                return await StartRecordingAsync(r, ct, chainBoundaryLaunch, chainReleaseEvidence).ConfigureAwait(false);
            }

            if (chainReleaseEvidence is not null)
            {
                lock (existingOwner.JoinEvidenceGate)
                {
                    if (existingOwner.AcceptingJoinEvidence)
                    {
                        _pendingChainReleaseStartEvidence.AddOrUpdate(
                            r.Id,
                            chainReleaseEvidence,
                            (_, current) => chainReleaseEvidence.ReleasedAt >= current.ReleasedAt ? chainReleaseEvidence : current);
                    }
                }
            }

            _log.Add("REC_START_CLAIM", $"R{r.Id}",
                $"result=JOIN ownerId={existingOwner.OwnerId} ownerTrigger={existingOwner.Trigger} ownerPid={existingOwner.ProcessId} " +
                $"ownerAcquiredAt={existingOwner.AcquiredAt:MM/dd HH:mm:ss.fff} joinTrigger={trigger} noSecondStartTask=True rule=recording_start_singleflight_contract");

            // JOINER_CANCELLATION_INVARIANT:
            // joinerのCancellationは待機だけを終了し、既存ownerの処理・Completion・所有権を変更しない。
            return await existingOwner.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        _log.Add("REC_START_CLAIM", $"R{r.Id}",
            $"result=ACQUIRED ownerId={owner.OwnerId} trigger={owner.Trigger} pid={owner.ProcessId} acquiredAt={owner.AcquiredAt:MM/dd HH:mm:ss.fff} rule=recording_start_singleflight_contract");

        var ownerResult = false;
        ExceptionDispatchInfo? cancellationDispatch = null;
        try
        {
            if (chainReleaseEvidence is null
                && _pendingChainReleaseStartEvidence.TryRemove(r.Id, out var pendingEvidence))
            {
                chainReleaseEvidence = pendingEvidence;
            }

            ownerResult = await StartRecordingCoreAsync(r, ct, chainBoundaryLaunch, chainReleaseEvidence).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            SettleInterruptedRecordingStartClaim(r.Id, preferRetry: true, reason: "operation_cancelled");
            cancellationDispatch = ExceptionDispatchInfo.Capture(ex);
        }
        catch (Exception ex)
        {
            // CHAIN_START_EXCEPTION_RETRY_INVARIANT:
            // worker未成立の一般例外でも、チェーン境界retry／物理解放handoff由来なら終端Failedにしない。
            // Scheduledへ戻して500ms境界監視へ再接続し、通常開始の非チェーン例外だけをFailedへ収束する。
            var retryableChainStart = chainBoundaryLaunch || chainReleaseEvidence is not null;
            SettleInterruptedRecordingStartClaim(
                r.Id,
                preferRetry: retryableChainStart,
                reason: $"exception:{ex.GetType().Name}");
            ownerResult = false;
            _log.Add("REC_START_SINGLEFLIGHT", $"R{r.Id}",
                $"result=NOT_STARTED role=owner ownerId={owner.OwnerId} reason=start_exception_converged exceptionType={ex.GetType().Name} error={TrimForLog(ex.Message, 180)} ownerAndJoinerSameResult=True rule=recording_start_singleflight_contract");
        }
        finally
        {
            // START_OWNER_EXIT_CONVERGENCE_INVARIANT:
            // coreの戻り値だけを開始結果の正本にしない。Completion通知とowner解除より前に、
            // 永続状態と実workerを再照合し、偽成功とowner不在Startingを同じ出口で収束する。
            try
            {
                ownerResult = await ConvergeRecordingStartOwnerResultAsync(r.Id, ownerResult, owner.OwnerId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ownerResult = false;
                _forceAllocationReevaluate = true;
                _log.Add("REC_START_OWNER_EXIT", $"R{r.Id}",
                    $"result=SETTLEMENT_EXCEPTION ownerId={owner.OwnerId} resolvedResult=False exceptionType={ex.GetType().Name} " +
                    $"error={TrimForLog(ex.Message, 180)} action=release_joiners_and_return_to_normal_retry rule=recording_start_owner_exit_contract");
            }
            finally
            {
                // START_SINGLEFLIGHT_OWNER_RETURN_INVARIANT:
                // owner直接呼出元とjoinerは、終了収束後に確定した同じ結果を受け取る。try内returnは禁止する。
                // owner終端後にchain release evidenceだけが残留しないよう、joinerからの追加受付停止と
                // 未消費evidenceの破棄を同じgate内で完結してからownerを解除する。
                bool removedOwnerMatch;
                lock (owner.JoinEvidenceGate)
                {
                    owner.AcceptingJoinEvidence = false;
                    _pendingChainReleaseStartEvidence.TryRemove(r.Id, out _);
                    owner.Completion.TrySetResult(ownerResult);
                    removedOwnerMatch = _recordingStartInProgress.TryRemove(r.Id, out var removedOwner)
                        && ReferenceEquals(removedOwner, owner);
                }
                _log.Add("REC_START_CLAIM", $"R{r.Id}",
                    $"result=RELEASED ownerId={owner.OwnerId} ownerResult={ownerResult} removedOwnerMatch={removedOwnerMatch} rule=recording_start_singleflight_contract");
            }
        }

        if (cancellationDispatch is not null && !ownerResult)
            cancellationDispatch.Throw();

        return ownerResult;
    }


    private async Task RecoverDueOrphanStartingReservationsAsync(DateTime now, CancellationToken ct)
    {
        var candidates = _store.GetByStatus(ReservationStatus.Starting)
            .Where(r => r.IsEnabled)
            .Where(r => r.Source != ReservationSource.Epg)
            .Where(r => now < r.EndTime)
            .Where(r => r.StartTime - TimeSpan.FromSeconds(_ini.PreStartMarginSeconds) <= now)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var recovered = await TryRecoverOrphanStartingAtCommonEntryAsync(candidate).ConfigureAwait(false);
            if (recovered?.Status != ReservationStatus.Scheduled)
                continue;

            _log.Add("REC_START_ORPHAN_RECOVERY", $"R{candidate.Id}",
                $"result=REQUEUED source=due_scan now={now:MM/dd HH:mm:ss.fff} " +
                "action=scheduled_candidate_visible_to_same_due_scan rule=recording_start_orphan_recovery_contract");
        }
    }


    private async Task<Reservation?> TryRecoverOrphanStartingAtCommonEntryAsync(Reservation requested)
    {
        var latest = _store.GetById(requested.Id);
        if (latest is null)
            return null;
        if (latest.Status != ReservationStatus.Starting)
            return latest;

        if (_recordingStartInProgress.ContainsKey(latest.Id))
            return latest;

        RecordingSession? session;
        lock (_sessionGate)
            _activeSessions.TryGetValue(latest.Id, out session);
        if (session is not null && IsOwnedRecordingWorkerAlive(session))
        {
            _forceAllocationReevaluate = true;
            return null;
        }

        var age = DateTime.Now - latest.UpdatedAt;
        if (age < TimeSpan.FromSeconds(2))
            return null;

        // ORPHAN_STARTING_DEAD_SESSION_CLEANUP_INVARIANT:
        // dead provisional sessionを残したままDBだけScheduledへ戻すと、due scanのContainsKeyで永久に除外される。
        // 同一sessionのworker/handle/lease cleanupを完了し、worker identity消滅を確認してからrollbackする。
        if (session is not null)
        {
            var cleanup = await AbortUncommittedRecordingStartAsync(session).ConfigureAwait(false);
            if (!cleanup.WorkerIdentityGone)
            {
                _forceAllocationReevaluate = true;
                _log.Add("REC_START_ORPHAN_RECOVERY", $"R{latest.Id}",
                    $"result=DEFERRED ageMs={(long)Math.Max(0, age.TotalMilliseconds)} workerRegistered=True workerAlive=True " +
                    "reason=cleanup_worker_identity_still_alive action=keep_starting_do_not_retry rule=recording_start_orphan_recovery_contract");
                return null;
            }

            latest = _store.GetById(latest.Id);
            if (latest is null || latest.Status != ReservationStatus.Starting)
                return latest?.Status == ReservationStatus.Scheduled ? latest : null;
        }

        var rollback = _store.TryRollbackRecordingStart(latest.Id, latest.DataVersion);
        _forceAllocationReevaluate = true;
        _log.Add("REC_START_ORPHAN_RECOVERY", $"R{latest.Id}",
            $"result={(rollback.Applied ? "ROLLED_BACK" : "CAS_REJECTED")} ageMs={(long)Math.Max(0, age.TotalMilliseconds)} " +
            $"workerRegistered={session is not null} workerAlive=False detail={SafeValue(rollback.Reason)} " +
            $"dataVersion={rollback.PreviousDataVersion}->{rollback.CurrentDataVersion} action=return_to_common_allocation_start_path " +
            "rule=recording_start_orphan_recovery_contract");

        var after = _store.GetById(latest.Id);
        return after?.Status == ReservationStatus.Scheduled ? after : null;
    }


    private async Task<bool> ConvergeRecordingStartOwnerResultAsync(int reservationId, bool coreResult, string ownerId)
    {
        var latest = _store.GetById(reservationId);
        RecordingSession? session;
        lock (_sessionGate)
            _activeSessions.TryGetValue(reservationId, out session);

        var workerAlive = session is not null && IsOwnedRecordingWorkerAlive(session);
        if (latest?.Status == ReservationStatus.Recording)
        {
            if (workerAlive)
            {
                if (!session!.TryMarkRecordingCommitted())
                {
                    _forceAllocationReevaluate = true;
                    return false;
                }

                if (!coreResult)
                {
                    _log.Add("REC_START_OWNER_EXIT", $"R{reservationId}",
                        $"result=CONVERGED_RECORDING ownerId={ownerId} coreResult=False resolvedResult=True pid={session.ProcessId} " +
                        $"tuner={SafeValue(session.Lease.Name)} rule=recording_start_owner_exit_contract");
                }
                return true;
            }

            _forceAllocationReevaluate = true;
            _log.Add("REC_START_OWNER_EXIT", $"R{reservationId}",
                $"result=RECORDING_WITHOUT_ACTIVE_WORKER ownerId={ownerId} coreResult={coreResult} resolvedResult=False " +
                "action=force_runtime_reconciliation rule=recording_start_owner_exit_contract");
            return false;
        }

        if (latest?.Status == ReservationStatus.Starting)
        {
            if (workerAlive)
            {
                if (!session!.TryBeginRecordingCommit(out var commitGeneration))
                {
                    if (session.IsRecordingCommitted)
                        return true;

                    // RECORDING_COMMIT_JOIN_INVARIANT:
                    // 別経路がDB commit中なら、ownerを先に解除して結果正本を分裂させてはならない。
                    // 同じsessionのcommit完了通知を短時間待ち、成功時だけRecording成立として返す。
                    if (session.TryGetRecordingCommitWaitSnapshot(out _, out var commitCompletion))
                    {
                        try
                        {
                            var committed = await commitCompletion
                                .WaitAsync(TimeSpan.FromSeconds(2))
                                .ConfigureAwait(false);
                            if (committed)
                            {
                                var committedLatest = _store.GetById(reservationId);
                                if (committedLatest?.Status == ReservationStatus.Recording
                                    && IsOwnedRecordingWorkerAlive(session))
                                    return true;
                            }
                        }
                        catch (TimeoutException)
                        {
                            // commit ownerは継続中。workerを停止せずruntime再照合へ渡す。
                        }
                    }
                    else if (session.IsRecordingCommitted)
                    {
                        // RECORDING_COMMIT_SNAPSHOT_TOCTOU_INVARIANT:
                        // IsRecordingCommitted確認後、wait snapshot取得前にcommitが完了し得る。
                        // snapshotなしを失敗確定とせず、同じsessionの正式昇格を再確認する。
                        var committedLatest = _store.GetById(reservationId);
                        if (committedLatest?.Status == ReservationStatus.Recording
                            && IsOwnedRecordingWorkerAlive(session))
                            return true;
                    }

                    _forceAllocationReevaluate = true;
                    return false;
                }

                var commit = _store.TryCompleteRecordingStart(reservationId, latest.DataVersion, session.Lease.Name);
                if (commit.Applied && commit.Reservation is not null)
                {
                    if (!session.CompleteRecordingCommit(commitGeneration))
                    {
                        _forceAllocationReevaluate = true;
                        return false;
                    }

                    _log.Add("REC_START_OWNER_EXIT", $"R{reservationId}",
                        $"result=PROVISIONAL_WORKER_COMMITTED ownerId={ownerId} coreResult={coreResult} resolvedResult=True " +
                        $"pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} dataVersion={commit.PreviousDataVersion}->{commit.CurrentDataVersion} " +
                        "action=promote_existing_worker_without_restart rule=recording_start_owner_exit_contract");
                    return true;
                }

                var afterCommitReject = _store.GetById(reservationId);
                if (afterCommitReject?.Status == ReservationStatus.Recording && IsOwnedRecordingWorkerAlive(session))
                {
                    if (session.CompleteRecordingCommit(commitGeneration) || session.IsRecordingCommitted)
                        return true;
                }

                session.CancelRecordingCommit(commitGeneration);
                // 未確定の実workerを残したままfalseだけ返してはならない。
                var cleanup = await AbortUncommittedRecordingStartAsync(session).ConfigureAwait(false);
                if (cleanup.WorkerIdentityGone)
                {
                    var afterAbort = _store.GetById(reservationId);
                    if (afterAbort?.Status == ReservationStatus.Starting)
                        _store.TryRollbackRecordingStart(reservationId, afterAbort.DataVersion);
                }
                _forceAllocationReevaluate = true;
                _log.Add("REC_START_OWNER_EXIT", $"R{reservationId}",
                    $"result=PROVISIONAL_WORKER_COMMIT_REJECTED ownerId={ownerId} coreResult={coreResult} resolvedResult=False pid={session.ProcessId} " +
                    $"workerGone={cleanup.WorkerIdentityGone} detail={SafeValue(commit.Reason)} action={(cleanup.WorkerIdentityGone ? "abort_and_return_to_common_allocation" : "keep_starting_until_worker_disappears")} " +
                    "rule=recording_start_owner_exit_contract");
                return false;
            }

            if (session is not null)
            {
                var cleanup = await AbortUncommittedRecordingStartAsync(session).ConfigureAwait(false);
                if (!cleanup.WorkerIdentityGone)
                {
                    _forceAllocationReevaluate = true;
                    return false;
                }
                latest = _store.GetById(reservationId);
                if (latest?.Status != ReservationStatus.Starting)
                    return false;
            }

            var rollback = _store.TryRollbackRecordingStart(reservationId, latest.DataVersion);
            _forceAllocationReevaluate = true;
            _log.Add("REC_START_OWNER_EXIT", $"R{reservationId}",
                $"result={(rollback.Applied ? "ORPHAN_STARTING_ROLLED_BACK" : "ORPHAN_STARTING_CAS_REJECTED")} ownerId={ownerId} " +
                $"coreResult={coreResult} resolvedResult=False detail={SafeValue(rollback.Reason)} " +
                $"dataVersion={rollback.PreviousDataVersion}->{rollback.CurrentDataVersion} action=return_to_common_allocation_start_path " +
                "rule=recording_start_owner_exit_contract");
            return false;
        }

        return coreResult && latest?.Status == ReservationStatus.Recording && workerAlive;
    }

    private bool TryResolveAssignedRecordingLaunchGroup(Reservation reservation, out string normalizedGroup, out string reason)
    {
        normalizedGroup = string.Empty;
        reason = string.Empty;

        // Admissionの正本は、共通割当が確定した予約のTunerNameと設定済みTunerProfile.Groupである。
        // 放送波IdentityからGR/BSCSへ縮退すると、同一放送波内の独立グループが再び誤合流するため禁止する。
        var assignedTunerName = string.IsNullOrWhiteSpace(reservation.TunerName)
            ? reservation.ActualTunerName
            : reservation.TunerName;
        if (string.IsNullOrWhiteSpace(assignedTunerName))
        {
            reason = "assigned_tuner_missing";
            return false;
        }

        var profile = _tunerProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, assignedTunerName, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            reason = "assigned_tuner_profile_missing";
            return false;
        }

        normalizedGroup = NormalizeRecordingLaunchGroup(profile.Group);
        if (string.Equals(normalizedGroup, "UNKNOWN", StringComparison.Ordinal))
        {
            reason = "assigned_tuner_group_missing";
            return false;
        }

        return true;
    }

    private static string NormalizeRecordingLaunchGroup(string? group)
    {
        var normalized = (group ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return "UNKNOWN";

        if (string.Equals(normalized, "BS/CS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "BS-CS", StringComparison.OrdinalIgnoreCase))
            return "BSCS";

        return normalized.ToUpperInvariant();
    }

    private async Task<bool> ApplyRecordingLaunchAdmissionAsync(Reservation r, CancellationToken ct, bool chainBoundaryLaunch)
    {
        if (!TryResolveAssignedRecordingLaunchGroup(r, out var normalizedGroup, out var groupFailureReason))
        {
            _log.Add("REC_LAUNCH_ADMISSION", $"R{r.Id}",
                $"result=DEFER reason={groupFailureReason} assignedTuner={SafeValue(r.TunerName)} actualTuner={SafeValue(r.ActualTunerName)} " +
                "stage=after_common_allocation action=do_not_claim_starting nextTick=True fallbackGroup=False rule=recording_launch_admission_contract");
            return false;
        }

        var secondsToProgramStart = (r.StartTime - DateTime.Now).TotalSeconds;
        var emergency = chainBoundaryLaunch && secondsToProgramStart <= ChainEmergencyLaunchWindowSeconds;
        if (emergency)
        {
            _log.Add("REC_LAUNCH_ADMISSION", $"R{r.Id}",
                $"result=BYPASS reason=chain_emergency_window group={normalizedGroup} assignedTuner={SafeValue(r.TunerName)} secondsToProgramStart={secondsToProgramStart:F1} emergencyWindowSec={ChainEmergencyLaunchWindowSeconds} policy=all_remaining_start rule=recording_launch_admission_contract");
            return true;
        }

        var gate = _recordingLaunchAdmissionGates.GetOrAdd(normalizedGroup, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.Now;
            if (_recordingLaunchNextAllowedAt.TryGetValue(normalizedGroup, out var nextAllowed) && nextAllowed > now)
            {
                var delay = nextAllowed - now;
                if (chainBoundaryLaunch && (r.StartTime - nextAllowed).TotalSeconds <= ChainEmergencyLaunchWindowSeconds)
                {
                    _log.Add("REC_LAUNCH_ADMISSION", $"R{r.Id}",
                        $"result=BYPASS reason=cadence_would_enter_chain_emergency group={normalizedGroup} assignedTuner={SafeValue(r.TunerName)} waitMs={(int)delay.TotalMilliseconds} programStart={r.StartTime:MM/dd HH:mm:ss} emergencyWindowSec={ChainEmergencyLaunchWindowSeconds} rule=recording_launch_admission_contract");
                }
                else
                {
                    _log.Add("REC_LAUNCH_ADMISSION", $"R{r.Id}",
                        $"result=WAIT group={normalizedGroup} assignedTuner={SafeValue(r.TunerName)} waitMs={(int)delay.TotalMilliseconds} cadenceMs={RecordingLaunchCadenceMs} chain={chainBoundaryLaunch} rule=recording_launch_admission_contract");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }

            // Admissionはworker寿命を保持しない。共通割当で確定した設定グループごとに
            // 投入開始時刻だけを予約し、別グループは互いに待たせない。
            _recordingLaunchNextAllowedAt[normalizedGroup] = DateTime.Now.AddMilliseconds(RecordingLaunchCadenceMs);
            _log.Add("REC_LAUNCH_ADMISSION", $"R{r.Id}",
                $"result=ENTER group={normalizedGroup} assignedTuner={SafeValue(r.TunerName)} chain={chainBoundaryLaunch} cadenceMs={RecordingLaunchCadenceMs} parallelAcrossGroups=True admissionScope=launch_timestamp_only source=committed_allocation rule=recording_launch_admission_contract");
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private void SettleInterruptedRecordingStartClaim(int reservationId, bool preferRetry, string reason)
    {
        var latest = _store.GetById(reservationId);
        if (latest?.Status != ReservationStatus.Starting)
            return;

        RecordingSession? session;
        lock (_sessionGate)
            _activeSessions.TryGetValue(reservationId, out session);
        if (session is not null && IsOwnedRecordingWorkerAlive(session))
        {
            // START_EXCEPTION_WORKER_PRESERVATION_INVARIANT:
            // worker起動後のCancellation/例外でcatch側がStartingをScheduled/Failedへ戻すと、
            // 実worker・leaseとDB状態が分裂する。状態変更はowner終了収束へ委ねる。
            _forceAllocationReevaluate = true;
            _log.Add("REC_START_EXCEPTION_SETTLE", $"R{reservationId}",
                $"result=DEFER_TO_OWNER_EXIT reason={SafeValue(reason)} workerActive=True pid={session.ProcessId} " +
                $"tuner={SafeValue(session.Lease.Name)} action=preserve_starting_until_existing_worker_converges " +
                "rule=recording_lifecycle_cas_contract");
            return;
        }

        var rollback = preferRetry || !latest.IsEnabled;
        var settle = rollback
            ? _store.TryRollbackRecordingStart(reservationId, latest.DataVersion)
            : _store.TryFailRecordingStart(reservationId, latest.DataVersion, reason);
        _log.Add("REC_START_EXCEPTION_SETTLE", $"R{reservationId}",
            $"result={(settle.Applied ? "APPLIED" : "SKIPPED")} reason={SafeValue(reason)} target={(rollback ? "Scheduled" : "Failed")} " +
            $"detail={SafeValue(settle.Reason)} dataVersion={settle.PreviousDataVersion}->{settle.CurrentDataVersion} rule=recording_lifecycle_cas_contract");
        if (settle.Applied)
        {
            _forceAllocationReevaluate = true;
        }
    }

    private bool TryValidateChainReleaseStartEvidence(ChainReleaseStartEvidence evidence, Reservation latestAtStartRequest, out Reservation validated)
    {
        validated = latestAtStartRequest;

        var secondsToProgramStart = (latestAtStartRequest.StartTime - DateTime.Now).TotalSeconds;
        var assignedTuner = latestAtStartRequest.TunerName?.Trim() ?? string.Empty;
        var physicalReleased = !string.IsNullOrWhiteSpace(assignedTuner)
            && _tunerPool.HasFreeSlotByName(assignedTuner);

        var statusMatch = latestAtStartRequest.Status == ReservationStatus.Scheduled;
        var enabledMatch = latestAtStartRequest.IsEnabled;
        var conflictMatch = !latestAtStartRequest.IsConflicted;
        var versionMatch = latestAtStartRequest.DataVersion == evidence.SuccessorDataVersion;
        var successorMatch = latestAtStartRequest.Id == evidence.SuccessorId;
        var previousMatch = latestAtStartRequest.UserChainPreviousId == evidence.PredecessorId;
        var rootMatch = latestAtStartRequest.UserChainRootId == evidence.ChainRootId;
        var tunerMatch = !string.IsNullOrWhiteSpace(assignedTuner)
            && string.Equals(assignedTuner, evidence.ReleasedTuner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(assignedTuner, evidence.PredecessorActualTuner, StringComparison.OrdinalIgnoreCase);
        var accepted = statusMatch
            && enabledMatch
            && conflictMatch
            && versionMatch
            && successorMatch
            && previousMatch
            && rootMatch
            && tunerMatch
            && physicalReleased;

        _log.Add("CHAIN_RELEASE_START_EVIDENCE", $"R{latestAtStartRequest.Id}",
            $"result={(accepted ? "ACCEPTED" : "REJECTED")} secondsToProgramStart={secondsToProgramStart:F1} evidenceWindow=confirmed_physical_release_generation " +
            $"boundaryKey={SafeValue(evidence.BoundaryKey)} boundaryAttempt={evidence.BoundaryAttempt} releasedAt={evidence.ReleasedAt:MM/dd HH:mm:ss.fff} predecessor=R{evidence.PredecessorId} predecessorPid={evidence.PredecessorProcessId} " +
            $"status={latestAtStartRequest.Status} enabled={latestAtStartRequest.IsEnabled} conflicted={latestAtStartRequest.IsConflicted} " +
            $"dataVersion={evidence.SuccessorDataVersion}->{latestAtStartRequest.DataVersion} versionMatch={versionMatch} successorMatch={successorMatch} " +
            $"chainPrevEvidence=R{evidence.PredecessorId} chainPrevLatest={(latestAtStartRequest.UserChainPreviousId.HasValue ? $"R{latestAtStartRequest.UserChainPreviousId.Value}" : "-")} previousMatch={previousMatch} " +
            $"chainRootEvidence={(evidence.ChainRootId.HasValue ? $"R{evidence.ChainRootId.Value}" : "-")} chainRootLatest={(latestAtStartRequest.UserChainRootId.HasValue ? $"R{latestAtStartRequest.UserChainRootId.Value}" : "-")} rootMatch={rootMatch} " +
            $"assignedTuner={SafeValue(assignedTuner)} releasedTuner={SafeValue(evidence.ReleasedTuner)} predecessorActualTuner={SafeValue(evidence.PredecessorActualTuner)} tunerMatch={tunerMatch} " +
            $"physicalReleased={physicalReleased} noOtherPoolOwner={physicalReleased} predecessorDbStatus=not_authoritative_after_release action={(accepted ? "reuse_confirmed_release_generation_and_continue_to_starting_cas" : "fallback_to_before_start_claim_allocation")} " +
            "commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=chain_release_start_evidence_reuse_contract");

        return accepted;
    }



    private enum ConflictedStartDisposition
    {
        Cleared,
        AllocationUnsettled,
        ActiveNormalEpgWave,
        TerminalConflict,
        NotScheduled
    }

    private ConflictedStartDisposition ResolveConflictedReservationForStart(
        Reservation reservation,
        string group,
        out Reservation resolved,
        out NormalEpgWaveOccupationSnapshot epgBlock,
        out bool latestExists)
    {
        // CONFLICT_START_SINGLE_SOURCE:
        // due scan と StartRecordingCoreAsync は競合の意味を個別解釈しない。
        // 共通Allocationで再評価した最新Reservationを正本に、
        // 1) normal EPG waveによる一時競合、2) 通常の終端競合、3) 競合解除、だけを分類する。
        // Manual / Immediate 等の入口種別はここでは見ない。
        var allocation = ReevaluateAndLog($"R{reservation.Id}");
        var latest = _store.GetById(reservation.Id);
        latestExists = latest is not null;
        resolved = latest ?? reservation;
        epgBlock = default!;

        if (allocation is null || allocation.Deferred || allocation.RetryRequired)
            return ConflictedStartDisposition.AllocationUnsettled;

        if (latest is not null && latest.Status != ReservationStatus.Scheduled)
            return ConflictedStartDisposition.NotScheduled;

        if (resolved.IsConflicted)
        {
            if (_store.TryGetNormalEpgConflictBlockerEvidence(resolved.Id, out var evidence)
                && string.Equals(evidence.Group, group, StringComparison.OrdinalIgnoreCase)
                && _normalEpgWaveOccupation.TryGet(group, out var activeWave)
                && activeWave.RunGeneration == evidence.RunGeneration
                && activeWave.Revision == evidence.OccupationRevision)
            {
                epgBlock = activeWave;
                return ConflictedStartDisposition.ActiveNormalEpgWave;
            }
            return ConflictedStartDisposition.TerminalConflict;
        }

        return ConflictedStartDisposition.Cleared;
    }

    private DateTime EffectiveNormalEpgOccupationEnd(NormalEpgWaveOccupationSnapshot occupation)
        => occupation.PlannedEndAt > DateTime.Now ? occupation.PlannedEndAt : DateTime.Now;

    private async Task<bool> StartRecordingCoreAsync(Reservation r, CancellationToken ct, bool chainBoundaryLaunch, ChainReleaseStartEvidence? chainReleaseEvidence)
    {
        var confirmedChainReleaseEvidence = chainReleaseEvidence;
        if (confirmedChainReleaseEvidence is null
            && _pendingChainReleaseStartEvidence.TryRemove(r.Id, out var mergedReleaseEvidence))
        {
            confirmedChainReleaseEvidence = mergedReleaseEvidence;
            _log.Add("CHAIN_RELEASE_START_EVIDENCE", $"R{r.Id}",
                $"result=CONSUMED boundaryKey={SafeValue(mergedReleaseEvidence.BoundaryKey)} boundaryAttempt={mergedReleaseEvidence.BoundaryAttempt} " +
                $"releasedTuner={SafeValue(mergedReleaseEvidence.ReleasedTuner)} releasedAt={mergedReleaseEvidence.ReleasedAt:MM/dd HH:mm:ss.fff} " +
                "source=existing_reservation_singleflight rule=chain_release_start_evidence_reuse_contract");
        }
        if (!_applicationGate.TryAdmit("recording_start", $"R{r.Id}", out var shutdownReason))
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=REJECTED reason={shutdownReason} stage=application_admission action=do_not_start_worker rule=shutdown_admission_contract");
            return false;
        }
        var requestedReservationId = r.Id;
        var chainContinuationAtStart = IsChainContinuation(r);
        if (StopPhaseGate.TryDeferRecordingStart("StartRecordingAsync", $"R{r.Id}", r.TunerName, msg => _log.Add("REC_DUE_SUPPRESS", $"R{r.Id}", msg)))
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=DEFER reason=stop_phase_active stage=start_request chain={chainContinuationAtStart} nextTick=True " + FormatReservationForAudit(r, "start_deferred"));
            return false;
        }

        // release_contract: Due scan / chain boundary が保持している Reservation は、
        // ○×・取消・削除・時刻追従より前のスナップショットである可能性がある。
        // worker 起動前の判断は必ずDBの最新予約を正本にする。
        var latestAtStartRequest = _store.GetById(requestedReservationId);
        if (latestAtStartRequest is null)
        {
            _log.Add("REC_START_DECISION", $"R{requestedReservationId}",
                "result=SKIP reason=reservation_missing_before_start_claim stage=latest_reload rule=recording_lifecycle_cas_contract");
            return false;
        }

        r = latestAtStartRequest;
        chainContinuationAtStart = IsChainContinuation(r);
        if (r.Status != ReservationStatus.Scheduled || !r.IsEnabled)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=SKIP reason=reservation_not_startable_before_claim status={r.Status} enabled={r.IsEnabled} dataVersion={r.DataVersion} " +
                "stage=latest_reload rule=recording_lifecycle_cas_contract " + FormatReservationForAudit(r, "latest_not_startable"));
            return false;
        }

        if (r.Source == ReservationSource.Epg)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                "result=SKIP reason=system_epg_entry_excluded_from_recording_route " + FormatReservationForAudit(r, "system_epg_skip"));
            return false;
        }

        if (_store.IsManualStoppedOccurrenceSuppressed(r))
        {
            var cancel = _store.TryFinalizeScheduledReservation(
                r.Id, r.DataVersion, ReservationStatus.Cancelled, "manual_stopped_occurrence_start_suppression");
            if (cancel.Applied)
                _forceAllocationReevaluate = true;
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=SKIP reason=automatic_rerecord_after_manual_stop stage=start_request cancelApplied={cancel.Applied} cancelReason={SafeValue(cancel.Reason)} dataVersion={cancel.PreviousDataVersion}->{cancel.CurrentDataVersion} " + FormatReservationForAudit(r, "manual_stop_suppressed"));
            return false;
        }

        _log.Add("REC_START_REQUEST", $"R{r.Id}", FormatReservationForAudit(r, "start_request"));
        _log.Add("RESERVATION_PIPELINE_AUDIT", $"R{r.Id}",
            BuildReservationPipelineAudit(r, "start_request", plannedTuner: EffectiveTunerName(r), finalGuardApplied: false, note: "pre_group_resolution"));

        var group = ResolveGroup(r);
        if (group is null)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}", "result=FAIL reason=group_unresolved");
            Fail(r, $"グループを特定できませんでした。service_id={r.ServiceId}");
            return false;
        }
        _log.Add("REC_START_DECISION", $"R{r.Id}", $"stage=group_resolved group={group} tuner={SafeValue(EffectiveTunerName(r))} ch={SafeValue(r.ChannelArgument)} " + FormatReservationForAudit(r, "group_resolved"));

        // release_contract: 競合予約は録画開始へ進めない。
        // 先に共通割り当てルートで再評価し、まだ競合なら静かにスキップする。
        // normal EPG実行中の同波予約もFinalConflictPlan上の競合としてここで止まり、EPG workerには触れない。
        if (r.IsConflicted)
        {
            var disposition = ResolveConflictedReservationForStart(r, group, out var conflictTarget, out var epgBlock, out var latestExists);
            if (disposition == ConflictedStartDisposition.AllocationUnsettled)
            {
                _log.Add("REC_START_DECISION", $"R{r.Id}",
                    $"result=DEFER reason=allocation_unsettled_before_start group={group} terminalStatus=none preserveStatus=Scheduled action=return_to_due_tick " + FormatReservationForAudit(conflictTarget, "allocation_unsettled_before_start"));
                return false;
            }

            if (disposition == ConflictedStartDisposition.ActiveNormalEpgWave)
            {
                _log.Add("REC_START_DECISION", $"R{r.Id}",
                    $"result=DEFER reason=active_normal_epg_wave_conflict_before_start group={group} epgSource={SafeValue(epgBlock.Source)} epgSilent={epgBlock.Silent} epgRunGeneration={epgBlock.RunGeneration} epgOccupy={epgBlock.StartedAt:MM/dd HH:mm:ss}〜{EffectiveNormalEpgOccupationEnd(epgBlock):MM/dd HH:mm:ss} terminalStatus=none preserveStatus=Scheduled preserveConflict=True action=return_to_due_tick_after_common_reallocation " + FormatReservationForAudit(conflictTarget, "normal_epg_conflict_start_deferred"));
                return false;
            }

            if (disposition == ConflictedStartDisposition.TerminalConflict)
            {
                _userEvents.AddRecordingSkippedByConflict(
                    conflictTarget,
                    r.Id,
                    group,
                    "tuner_limit_exceeded");
                _store.FinalizeSkippedByConflictAtDue(r.Id, conflictTarget.DataVersion, group, "tuner_limit_exceeded");
                _log.Add("REC_START_DECISION", $"R{r.Id}",
                    $"result=SKIP reason=conflict_still_true_before_start latestExists={latestExists} latestConflicted=True group={group} terminalStatus=Failed userEvent=REC_SKIPPED_BY_CONFLICT " + FormatReservationForAudit(conflictTarget, "conflict_start_suppressed"));
                return false;
            }

            if (disposition == ConflictedStartDisposition.NotScheduled)
                return false;

            r = conflictTarget;
            _log.Add("Scheduler", SafeValue(r.ServiceName),
                $"競合再評価: service=[{SafeValue(r.ServiceName)}] title=[{ReservationDisplayTitle(r.Title)}] id=R{r.Id} 空きチューナーを確認できたため録画開始します。group={group} rule=release_contract");
        }

        // CHAIN_RELEASE_START_EVIDENCE_REUSE_INVARIANT:
        // チェーン後続は、前番組を30秒前に停止して同一物理Tunerを継承する正規順序である。
        // 物理解放callbackが発行した同一境界世代と、Status／DataVersion／Chain／割当Tuner／Free状態を全件再検証できた場合、
        // FinalConflictPlanの確定証拠を再利用し、開始直前に全体割当を重複再計算せずStarting CASへ進む。
        // これを残り10秒だけへ制限すると、解放済みでもallocation single-flightに阻まれ後番組先頭を欠落させるため禁止する。
        // 前番組DB状態は物理解放後にRecording→Stopping→Completedの反映順が競合するため受理条件にしない。
        // 通常録画へ適用せず、証拠不一致時だけ共通割り当てへ戻す。固定wait／sleep／delayやDB状態は追加しない。
        Reservation? releaseEvidenceValidated = null;
        var releaseEvidenceAccepted = confirmedChainReleaseEvidence is not null
            && TryValidateChainReleaseStartEvidence(confirmedChainReleaseEvidence, r, out releaseEvidenceValidated);

        // CHAIN_DUE_SINGLEFLIGHT_HANDOFF_INVARIANT:
        // チェーン後続の通常due要求は、前番組workerがまだ同一物理Tunerを所有している間、
        // 全体allocation barrierへ入って予約単位single-flightを占有してはならない。
        // 物理解放callbackが同じsingle-flightへ証拠を合流し、次の500ms due/boundary再試行がStarting CASへ進む。
        // これにより二重開始を避けつつ、物理解放済みなのに長時間Barrier待ちとなる経路を禁止する。
        if (!releaseEvidenceAccepted && chainContinuationAtStart)
        {
            var predecessorId = TryResolveChainPredecessorId(r, out var predecessorSource);
            var activePredecessor = predecessorId.HasValue ? TryGetActiveSession(predecessorId.Value) : null;
            if (activePredecessor is not null)
            {
                _log.Add("REC_START_DECISION", $"R{r.Id}",
                    $"result=DEFER reason=chain_predecessor_physical_release_pending predecessor=R{predecessorId!.Value} predecessorSource={SafeValue(predecessorSource)} " +
                    $"predecessorTuner={SafeValue(activePredecessor.Lease.Name)} successorAssignedTuner={SafeValue(r.TunerName)} pid={activePredecessor.ProcessId} " +
                    "action=release_reservation_singleflight_before_allocation_barrier retry=boundary_or_due_500ms rule=chain_due_singleflight_handoff_contract");
                return false;
            }
        }

        Reservation? allocated;
        if (releaseEvidenceAccepted && releaseEvidenceValidated is not null)
        {
            allocated = releaseEvidenceValidated;
        }
        else
        {
            // チェーン境界起動は、稼働中single-flightの完了待ちへ参加しない。
            // ここで待たせると残り10秒到達後も同じ開始taskが拘束されるため、Scheduledのまま境界再試行へ戻す。
            // single-flightが空いている場合は従来どおりこの呼出しがleaderとなり、通常のBeforeStartClaimを完了する。
            var allocationResult = ReevaluateAndLog(
                $"R{r.Id}:BeforeStartClaim",
                syncProgramRules: false,
                waitForActiveSingleFlight: !chainBoundaryLaunch);
            if (chainBoundaryLaunch
                && allocationResult is { Deferred: true, Reason: "active_single_flight_no_wait" })
            {
                _log.Add("CHAIN_RELEASE_START_EVIDENCE", $"R{r.Id}",
                    $"result=RETRY reason=before_start_claim_single_flight_busy secondsToProgramStart={(r.StartTime - DateTime.Now).TotalSeconds:F1} " +
                    "action=return_to_chain_boundary_retry noWait=True rule=chain_release_start_evidence_reuse_contract");
                return false;
            }
            if (allocationResult is null || allocationResult.Deferred || allocationResult.RetryRequired)
            {
                _log.Add("REC_START_DECISION", $"R{r.Id}",
                    $"result=DEFER reason=allocation_unsettled_after_before_start_claim deferred={(allocationResult?.Deferred.ToString() ?? "unknown")} retryRequired={(allocationResult?.RetryRequired.ToString() ?? "unknown")} allocationReason={SafeValue(allocationResult?.Reason)} stage=allocation_barrier nextTick=True action=do_not_claim_starting rule=common_allocation_start_barrier " + FormatReservationForAudit(r, "allocation_barrier_unsettled"));
                return false;
            }

            allocated = _store.GetById(r.Id);
        }
        if (allocated is null)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                "result=SKIP reason=reservation_missing_after_allocation_barrier stage=allocation_barrier rule=common_allocation_start_barrier");
            return false;
        }

        if (allocated.Status != ReservationStatus.Scheduled || !allocated.IsEnabled)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=SKIP reason=reservation_not_startable_after_allocation_barrier status={allocated.Status} enabled={allocated.IsEnabled} dataVersion={allocated.DataVersion} " +
                "stage=allocation_barrier rule=common_allocation_start_barrier " + FormatReservationForAudit(allocated, "allocation_barrier_not_startable"));
            return false;
        }

        if (allocated.IsConflicted)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=SKIP reason=conflict_after_allocation_barrier tuner={SafeValue(allocated.TunerName)} dataVersion={allocated.DataVersion} " +
                "stage=allocation_barrier nextTick=True rule=common_allocation_start_barrier " + FormatReservationForAudit(allocated, "allocation_barrier_conflict"));
            return false;
        }

        if (string.IsNullOrWhiteSpace(allocated.TunerName))
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=DEFER reason=assigned_tuner_not_committed_after_allocation_barrier dataVersion={allocated.DataVersion} " +
                "stage=allocation_barrier nextTick=True action=do_not_claim_starting rule=common_allocation_start_barrier " + FormatReservationForAudit(allocated, "allocation_barrier_deferred"));
            return false;
        }

        r = allocated;
        _log.Add("REC_START_ALLOCATION_BARRIER", $"R{r.Id}",
            $"result=PASSED assignedTuner={SafeValue(r.TunerName)} dataVersion={r.DataVersion} source={r.Source} " +
            "commonRoute=ALLOC_ROUTE/TUNER_ALLOC action=continue_to_launch_admission rule=common_allocation_start_barrier");

        // 設定グループ別Admissionは、共通割当でTunerNameが確定した後、Starting CASより前に一度だけ通す。
        // これより上流で放送波fallbackを使ったAdmissionを行ってはならない。
        if (!await ApplyRecordingLaunchAdmissionAsync(r, ct, chainBoundaryLaunch).ConfigureAwait(false))
            return false;

        // release_contract: 実チューナー操作へ入る直前に、最新の Scheduled + Enabled + DataVersion を
        // Starting へ原子的に確定する。ここで負けた要求はEPG停止・Lease取得・worker起動へ進まない。
        var latestBeforeStartClaim = _store.GetById(r.Id);
        if (latestBeforeStartClaim is null)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                "result=SKIP reason=reservation_missing_before_start_claim stage=cas_reload rule=recording_lifecycle_cas_contract");
            return false;
        }

        if (latestBeforeStartClaim.Status != ReservationStatus.Scheduled || !latestBeforeStartClaim.IsEnabled)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=SKIP reason=reservation_not_startable_before_claim status={latestBeforeStartClaim.Status} enabled={latestBeforeStartClaim.IsEnabled} dataVersion={latestBeforeStartClaim.DataVersion} " +
                "stage=cas_reload rule=recording_lifecycle_cas_contract " + FormatReservationForAudit(latestBeforeStartClaim, "cas_reload_not_startable"));
            return false;
        }

        // Admission待機中に共通割当が更新された場合、旧Tunerのグループで通したAdmissionを流用しない。
        // 次Tickで最新割当を正本としてAdmissionからやり直す。
        if (!string.Equals(latestBeforeStartClaim.TunerName, r.TunerName, StringComparison.OrdinalIgnoreCase))
        {
            _log.Add("REC_LAUNCH_ADMISSION", $"R{r.Id}",
                $"result=RETRY reason=assigned_tuner_changed_after_admission admittedTuner={SafeValue(r.TunerName)} latestTuner={SafeValue(latestBeforeStartClaim.TunerName)} " +
                $"dataVersion={r.DataVersion}->{latestBeforeStartClaim.DataVersion} action=return_to_due_tick noStartClaim=True rule=recording_launch_admission_contract");
            return false;
        }

        var startClaim = _store.TryBeginRecordingStart(r.Id, latestBeforeStartClaim.DataVersion);
        if (!startClaim.Applied || startClaim.Reservation is null)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=SKIP reason=start_claim_rejected detail={SafeValue(startClaim.Reason)} previousStatus={startClaim.PreviousStatus?.ToString() ?? "-"} currentStatus={startClaim.CurrentStatus?.ToString() ?? "-"} " +
                $"dataVersion={startClaim.PreviousDataVersion}->{startClaim.CurrentDataVersion} stage=lifecycle_cas rule=recording_lifecycle_cas_contract");
            return false;
        }

        r = startClaim.Reservation;
        chainContinuationAtStart = IsChainContinuation(r);
        _log.Add("REC_START_DECISION", $"R{r.Id}",
            $"result=CLAIMED status=Starting dataVersion={startClaim.PreviousDataVersion}->{startClaim.CurrentDataVersion} chain={chainContinuationAtStart} " +
            "stage=lifecycle_cas rule=recording_lifecycle_cas_contract");

        // NORMAL_EPG_RUNNING_FRAME_INVARIANT:
        // normal EPG は開始後、録画dueを理由に停止・縮退・Tuner単位preemptしない。
        // 実行中の同波予約はNormalEpgWaveOccupationをFinalConflictPlanへ投影して競合化し、
        // EPGは Complete / ExplicitCancel / RealFailure まで通常のcapture lifecycleを継続する。
        // ここでRecordingLifecycleGateへnormal EPG抑止を書いたり、EPG workerを追い出してはならない。

        // 視聴競合チェック（今すぐ録画等でViewingが占有している場合）
        var hasFreeBeforeViewing = _tunerPool.HasFreeSlot(group);
        var hasViewing = _tunerPool.HasViewingSlot(group);
        _log.Add("REC_TUNER_CHECK", $"R{r.Id}", $"stage=before_viewing_preempt group={group} hasFree={hasFreeBeforeViewing} hasViewing={hasViewing} tuner={SafeValue(r.TunerName)}");
        if (!hasFreeBeforeViewing && hasViewing)
        {
            _log.Add("TUNER_PROTECT", "Viewing", $"result=KEEP_VIEWING reason=no_recordable_free_slot group={group} reservation=R{r.Id} tuner={SafeValue(r.TunerName)}");
        }

        // ASSIGNED_TUNER_WAIT_INVARIANT:
        // FinalConflictPlanが確定した物理Tunerを実行正本とするため、グループ全体の空き待ちはここで行わない。
        // グループ内の別Tunerが空いているかどうかは、確定Tunerの再利用可否を証明しない。
        // 逆にグループ全体が満杯でも、直後に解放される確定Tunerだけを待てばよい。
        // 実際の待機・取得はLaunchNewRecordingAsync内のWaitForFreeSlotByNameDetailedAsync / AcquireForRecordingByNameへ一本化する。
        // ここへgroup-wide timeoutを再導入すると、exact-tuner待機との二重待機と環境依存遅延が再発する。

        return await LaunchNewRecordingAsync(
            r,
            group,
            ct,
            chainBoundaryLaunch,
            releaseEvidenceAccepted ? confirmedChainReleaseEvidence : null);
    }

    // ─── 通常録画起動 ────────────────────────────────────────────

    private async Task<bool> LaunchNewRecordingAsync(
        Reservation r,
        string group,
        CancellationToken ct,
        bool chainBoundaryLaunch,
        ChainReleaseStartEvidence? confirmedChainReleaseEvidence = null)
    {
        var postEndMarginSeconds = SettingsDefaults.NormalizePostEndMarginSeconds(_ini.PostEndMarginSeconds);
        var basePlannedEnd = r.EndTime.AddSeconds(postEndMarginSeconds);
        var plannedEnd = ResolveChainPlannedEndForLaunch(r, basePlannedEnd);
        _log.Add("REC_LAUNCH_PREP", $"R{r.Id}",
            $"stage=launch_prepare group={group} plannedEnd={plannedEnd:MM/dd HH:mm:ss} basePlannedEnd={basePlannedEnd:MM/dd HH:mm:ss} preStart={_ini.PreStartMarginSeconds}s postEnd={postEndMarginSeconds}s chainMode=assigned_tuner_prearm " + FormatReservationForAudit(r, "launch"));
        _log.Add("RESERVATION_PIPELINE_AUDIT", $"R{r.Id}",
            BuildReservationPipelineAudit(r, "launch_prepare", group: group, plannedTuner: EffectiveTunerName(r), plannedEnd: plannedEnd, finalGuardApplied: false, note: "before_channel_resolve_and_tuner_acquire"));

        var chainContinuationAtLaunch = IsChainContinuation(r);
        if (StopPhaseGate.TryDeferRecordingStart("LaunchNewRecordingAsync", $"R{r.Id}", r.TunerName, msg => _log.Add("REC_DUE_SUPPRESS", $"R{r.Id}", msg)))
        {
            var rollback = _store.TryRollbackRecordingStart(r.Id, r.DataVersion);
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=DEFER reason=stop_phase_active stage=before_tuner_acquire chain={chainContinuationAtLaunch} nextTick=True " +
                $"rollbackApplied={rollback.Applied} rollbackReason={SafeValue(rollback.Reason)} dataVersion={rollback.PreviousDataVersion}->{rollback.CurrentDataVersion} " +
                FormatReservationForAudit(rollback.Reservation ?? r, "launch_deferred"));
            return false;
        }

        // 録画開始直前に、現在の .ch2 から必ず再解決する。
        // フロント/DBに残った古い /chi や /sid 付き ChannelArgument は信用しない。
        var resolvedChannel = _channelLoader.Load().Targets.FirstOrDefault(t =>
            t.OriginalNetworkId == r.NetworkId &&
            t.TransportStreamId == r.TransportStreamId &&
            t.ServiceId == r.ServiceId);
        if (resolvedChannel is not null)
        {
            var beforeChannelArg = r.ChannelArgument ?? "";
            var baseChannelArg = resolvedChannel.ChannelArgument ?? "";
            var recordingChannelArg = BuildRecordingChannelArgumentWithServiceIdentity(
                baseChannelArg,
                resolvedChannel.OriginalNetworkId,
                resolvedChannel.TransportStreamId,
                resolvedChannel.ServiceId);
            r.ChannelArgument = recordingChannelArg;
            if (string.IsNullOrWhiteSpace(r.ServiceName)) r.ServiceName = resolvedChannel.Name;

            _log.Add("CH2_CHANNEL_MATCH", $"R{r.Id}",
                $"result=OK service={SafeValue(r.ServiceName)} ch2Name={SafeValue(resolvedChannel.Name)} " +
                $"group={SafeValue(resolvedChannel.Group)} nid={resolvedChannel.OriginalNetworkId} tsid={resolvedChannel.TransportStreamId} sid={resolvedChannel.ServiceId} " +
                $"ch2File={SafeValue(resolvedChannel.Ch2FileName)} ch2Line={resolvedChannel.Ch2LineNumber} " +
                $"bonCh={resolvedChannel.BonDriverChannel} resolvedSpace={resolvedChannel.ResolvedSpace} resolvedChi={resolvedChannel.ResolvedChannelIndex} " +
                $"sameTsServices={resolvedChannel.SameTransportServiceCount} buildSource={SafeValue(resolvedChannel.ChannelBuildSource)} " +
                $"baseArg={SafeValue(baseChannelArg)} generatedArg={SafeValue(recordingChannelArg)} beforeArg={SafeValue(beforeChannelArg)} rule=recording_nid_tsid_sid_identity");

            if (!string.Equals(beforeChannelArg, r.ChannelArgument, StringComparison.OrdinalIgnoreCase))
            {
                _log.Add("CHANNEL_RESOLVE", $"R{r.Id}",
                    $"launch_re_resolved service={SafeValue(r.ServiceName)} svcId={r.ServiceId} before={SafeValue(beforeChannelArg)} after={SafeValue(r.ChannelArgument)} source=current_ch2_tvtest_channel_nid_tsid_sid");
            }
        }
        else
        {
            var beforeChannelArg = r.ChannelArgument ?? "";
            r.ChannelArgument = BuildRecordingChannelArgumentWithServiceIdentity(
                beforeChannelArg,
                r.NetworkId,
                r.TransportStreamId,
                r.ServiceId);
            _log.Add("CH2_CHANNEL_MATCH", $"R{r.Id}",
                $"result=MISS service={SafeValue(r.ServiceName)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} beforeArg={SafeValue(beforeChannelArg)} generatedArg={SafeValue(r.ChannelArgument)} rule=fallback_nid_tsid_sid_identity");
        }

        _log.Add("RESERVATION_PIPELINE_AUDIT", $"R{r.Id}",
            BuildReservationPipelineAudit(r, "channel_resolved", group: group, plannedTuner: EffectiveTunerName(r), resolvedChannel: resolvedChannel, finalGuardApplied: false, note: "ch2_nid_tsid_sid_revalidation_complete"));

        // ChannelArgumentが空の場合は録画不可（タイトルなし-.tsが生成されるため）
        if (string.IsNullOrWhiteSpace(r.ChannelArgument))
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}", "result=FAIL reason=empty_channel_argument");
            Fail(r, $"チャンネル引数が未設定のため録画をスキップしました。service_id={r.ServiceId}");
            return false;
        }

        // release_contract: チェーン後続は既存TvAIrEpgRecセッションへ接続しない。
        // 前番組を後番組開始前マージンで明示停止し、同じ実チューナーを通常録画起動で再取得する。
        // これにより「1番組=1TSファイル」を維持し、前番組末尾欠損を契約上の許容範囲に閉じ込める。
        if (IsChainContinuation(r))
        {
            _log.Add("CHAIN_RESTART_ROUTE", $"R{r.Id}",
                $"stage=before_record_start result=USE_ASSIGNED_TUNER_PREARM reason=program_file_per_reservation successor=R{r.Id} " +
                $"assignedTuner={SafeValue(r.TunerName)} plannedEnd={plannedEnd:MM/dd HH:mm:ss} activeAttachDisabled=True commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
        }

        var pt3StartLockWaitAt = DateTime.Now;
        _log.Add("CHAIN_TRACE", $"R{r.Id}", $"[CHAIN] stage=pt3_start_lock_wait_start group={group} status={_tunerPool.GetStatusSummary()}");
        using var tunerDeviceAccess = await TunerDeviceAccessGate.EnterAsync(
            $"REC_START R{r.Id}",
            group,
            msg => _log.Add("TUNER_DEVICE_LOCK", $"R{r.Id}", msg),
            ct).ConfigureAwait(false);
        var pt3StartLockWaitMs = (int)(DateTime.Now - pt3StartLockWaitAt).TotalMilliseconds;
        _log.Add("CHAIN_TRACE", $"R{r.Id}", $"[CHAIN] stage=pt3_start_lock_entered waitMs={pt3StartLockWaitMs} group={group} status={_tunerPool.GetStatusSummary()}");



        // 事前割り当てチューナーを優先して確保する。
        // チェーン予約では「指定チューナーが取れないなら別チューナーへ逃がす」を禁止する。
        // Cマークは同一チューナー引き継ぎが前提のため、ここで通常空きチューナーへフォールバックすると
        // 前番組末尾カットだけが発生し、後続が同一物理チューナーで始まらない不整合になる。
        var chainContinuation = IsChainContinuation(r);
        _log.Add("CHAIN_TRACE", $"R{r.Id}", $"[CHAIN] stage=before_tuner_acquire chain={chainContinuation} group={group} requestedTuner={SafeValue(r.TunerName)} status={_tunerPool.GetStatusSummary()}");
        LogChainRecordingEvaluation(r, group, chainContinuation, "before_tuner_acquire");

        // 管理外TVTestとそのチューナーはTvAIrの責任範囲外。
        // 録画割当はTvAIr自身のTunerPool状態だけを正本として決定する。

        TunerLease? lease = null;
        var predIdForActualChain = chainContinuation ? TryResolveChainPredecessorId(r, out _) : null;

        // CHAIN_PHYSICAL_TUNER_SINGLE_SOURCE_INVARIANT:
        // チェーン境界後の実行正本は、物理解放callbackが確定したReleasedTunerだけである。
        // FinalConflictPlanのTunerNameは境界前にReleasedTunerとの一致を検証済みであり、
        // 境界後にActiveSessionや空きTuner一覧から再推論・再選択しない。
        // 通常due経路でまだ物理解放証拠がない場合は、FinalConflictPlanの確定Tunerを待つだけとし、
        // 別物理Tunerへのフォールバックは行わない。
        if (chainContinuation)
        {
            var boundaryEvidenceAvailable = confirmedChainReleaseEvidence is not null;
            var chainPreferredTuner = boundaryEvidenceAvailable
                ? confirmedChainReleaseEvidence!.ReleasedTuner
                : r.TunerName;
            var chainPreferredSource = boundaryEvidenceAvailable
                ? "boundary_physical_release_evidence"
                : "final_plan_assigned_tuner";

            if (string.IsNullOrWhiteSpace(chainPreferredTuner))
            {
                var rollback = _store.TryRollbackRecordingStart(r.Id, r.DataVersion);
                _forceAllocationReevaluate = true;
                _log.Add("CHAIN_ASSIGNED_TUNER_CONTRACT", $"R{r.Id}",
                    $"result=DEFER reason=missing_chain_execution_tuner successor=R{r.Id} predecessor={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} " +
                    $"source={chainPreferredSource} rollbackApplied={rollback.Applied} rollbackReason={SafeValue(rollback.Reason)} dataVersion={rollback.PreviousDataVersion}->{rollback.CurrentDataVersion} " +
                    $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC action=retry_same_tuner_no_fallback rule=chain_same_physical_tuner_contract");
                return false;
            }

            if (boundaryEvidenceAvailable
                && !string.Equals(r.TunerName, chainPreferredTuner, StringComparison.OrdinalIgnoreCase))
            {
                var rollback = _store.TryRollbackRecordingStart(r.Id, r.DataVersion);
                _forceAllocationReevaluate = true;
                _log.Add("CHAIN_ASSIGNED_TUNER_CONTRACT", $"R{r.Id}",
                    $"result=DEFER reason=final_plan_tuner_changed_after_physical_release successor=R{r.Id} predecessor=R{confirmedChainReleaseEvidence!.PredecessorId} " +
                    $"releasedTuner={SafeValue(chainPreferredTuner)} assignedTuner={SafeValue(r.TunerName)} boundaryKey={SafeValue(confirmedChainReleaseEvidence.BoundaryKey)} " +
                    $"rollbackApplied={rollback.Applied} rollbackReason={SafeValue(rollback.Reason)} dataVersion={rollback.PreviousDataVersion}->{rollback.CurrentDataVersion} " +
                    "action=do_not_reinfer_or_switch_tuner rule=chain_same_physical_tuner_contract");
                return false;
            }

            _log.Add("CHAIN_ASSIGNED_TUNER_CONTRACT", $"R{r.Id}",
                $"result=OK successor=R{r.Id} predecessor={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} " +
                $"executionTuner={SafeValue(chainPreferredTuner)} source={chainPreferredSource} boundaryEvidence={boundaryEvidenceAvailable} " +
                "commonRoute=ALLOC_ROUTE/TUNER_ALLOC action=acquire_exact_physical_tuner noReinfer=True noFallback=True rule=chain_same_physical_tuner_contract");

            _log.Add("CHAIN_RECORDING_RUNTIME_AUDIT", $"R{r.Id}",
                $"result=EXPECT_EXACT_TUNER stage=before_tuner_wait successor=R{r.Id} predecessor={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} " +
                $"expectedTuner={SafeValue(chainPreferredTuner)} expectedSource={chainPreferredSource} plannedTuner={SafeValue(r.TunerName)} actualTuner=- " +
                $"boundaryEvidence={boundaryEvidenceAvailable} commonRoute=ALLOC_ROUTE/TUNER_ALLOC stopRestart=True assignedTunerRequired=True rule=chain_same_physical_tuner_contract");

            var chainWaitStartedAt = DateTime.UtcNow;
            if (!_tunerPool.HasFreeSlotByName(chainPreferredTuner))
            {
                _log.Add("REC_CHAIN_TUNER_WAIT", $"R{r.Id}",
                    $"waiting_for_exact_chain_tuner tuner={SafeValue(chainPreferredTuner)} source={chainPreferredSource} " +
                    $"pred={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} waitMode=tuner_pool_state_signal " +
                    $"limitMs={ChainRequestedTunerWaitMs} status={_tunerPool.GetStatusSummary()} rule=chain_same_physical_tuner_contract");
                var chainWaitResult = await _tunerPool.WaitForFreeSlotByNameDetailedAsync(
                    chainPreferredTuner,
                    TimeSpan.FromMilliseconds(ChainRequestedTunerWaitMs),
                    ct).ConfigureAwait(false);
                if (chainWaitResult is TunerAvailabilityWaitResult.PoolStopping or TunerAvailabilityWaitResult.PoolDisposed)
                {
                    _log.Add("REC_START_DECISION", $"R{r.Id}",
                        $"result=REJECTED reason={chainWaitResult} stage=chain_tuner_wait tuner={SafeValue(chainPreferredTuner)} action=do_not_start_worker rule=shutdown_admission_contract");
                    SettleInterruptedRecordingStartClaim(r.Id, preferRetry: true, reason: chainWaitResult.ToString());
                    return false;
                }
                if (chainWaitResult == TunerAvailabilityWaitResult.TimedOut)
                {
                    var rollback = _store.TryRollbackRecordingStart(r.Id, r.DataVersion);
                    _forceAllocationReevaluate = true;
                    _log.Add("REC_START_DECISION", $"R{r.Id}",
                        $"result=DEFER reason=exact_chain_tuner_wait_timed_out stage=chain_tuner_wait tuner={SafeValue(chainPreferredTuner)} source={chainPreferredSource} " +
                        $"rollbackApplied={rollback.Applied} rollbackReason={SafeValue(rollback.Reason)} dataVersion={rollback.PreviousDataVersion}->{rollback.CurrentDataVersion} " +
                        "action=retry_same_tuner_no_fallback rule=chain_same_physical_tuner_contract");
                    return false;
                }
            }
            var waitedMs = (int)Math.Min(ChainRequestedTunerWaitMs, Math.Max(0, (DateTime.UtcNow - chainWaitStartedAt).TotalMilliseconds));

            lease = _tunerPool.AcquireForRecordingByName(chainPreferredTuner, r.Id, plannedEnd);
            if (lease is null)
            {
                var rollback = _store.TryRollbackRecordingStart(r.Id, r.DataVersion);
                _forceAllocationReevaluate = true;
                _log.Add("REC_CHAIN_TUNER_INHERIT", $"R{r.Id}",
                    $"result=DEFER reason=exact_chain_tuner_unavailable tuner={SafeValue(chainPreferredTuner)} source={chainPreferredSource} waitedMs={waitedMs} " +
                    $"rollbackApplied={rollback.Applied} rollbackReason={SafeValue(rollback.Reason)} dataVersion={rollback.PreviousDataVersion}->{rollback.CurrentDataVersion} " +
                    $"status={_tunerPool.GetStatusSummary()} action=retry_same_tuner_no_fallback rule=chain_same_physical_tuner_contract");
                return false;
            }

            var exactTunerMatched = string.Equals(chainPreferredTuner, lease.Name, StringComparison.OrdinalIgnoreCase);
            if (!exactTunerMatched)
            {
                lease.Dispose();
                var rollback = _store.TryRollbackRecordingStart(r.Id, r.DataVersion);
                _forceAllocationReevaluate = true;
                _log.Add("REC_CHAIN_TUNER_INHERIT", $"R{r.Id}",
                    $"result=DEFER reason=pool_returned_different_tuner expected={SafeValue(chainPreferredTuner)} actual={SafeValue(lease.Name)} " +
                    $"rollbackApplied={rollback.Applied} rollbackReason={SafeValue(rollback.Reason)} action=reject_different_tuner rule=chain_same_physical_tuner_contract");
                return false;
            }

            _log.Add("REC_CHAIN_TUNER_INHERIT", $"R{r.Id}",
                $"result=OK expectedTuner={SafeValue(chainPreferredTuner)} actualTuner={SafeValue(lease.Name)} did={SafeValue(lease.Did)} " +
                $"source={chainPreferredSource} boundaryEvidence={boundaryEvidenceAvailable} waitedMs={waitedMs} noReinfer=True noFallback=True rule=chain_same_physical_tuner_contract");
            _log.Add("CHAIN_RECORDING_RUNTIME_AUDIT", $"R{r.Id}",
                $"result=EXACT_TUNER_ACQUIRED stage=after_tuner_acquire successor=R{r.Id} predecessor={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} " +
                $"expectedTuner={SafeValue(chainPreferredTuner)} expectedSource={chainPreferredSource} plannedTuner={SafeValue(r.TunerName)} actualTuner={SafeValue(lease.Name)} did={SafeValue(lease.Did)} " +
                $"boundaryEvidence={boundaryEvidenceAvailable} waitedMs={waitedMs} commonRoute=ALLOC_ROUTE/TUNER_ALLOC stopRestart=True rule=chain_same_physical_tuner_contract");
        }
        else
        {
            // release_contract: 通常録画も FinalConflictPlan が永続化した TunerName を実行正本にする。
            // 指定チューナーが一時的に使用中でも、別の空きチューナーへ黙って逃がすと
            // 共通割当の計画と実行が分離するため禁止する。解放を待ち、取れなければ Starting を戻して再割当する。
            if (string.IsNullOrWhiteSpace(r.TunerName))
            {
                var rollback = _store.TryRollbackRecordingStart(r.Id, r.DataVersion);
                _forceAllocationReevaluate = true;
                _log.Add("REC_START_DECISION", $"R{r.Id}",
                    $"result=DEFER reason=missing_final_plan_assigned_tuner stage=before_tuner_acquire " +
                    $"rollbackApplied={rollback.Applied} rollbackReason={SafeValue(rollback.Reason)} dataVersion={rollback.PreviousDataVersion}->{rollback.CurrentDataVersion} " +
                    "commonRoute=ALLOC_ROUTE/TUNER_ALLOC nextTick=True rule=common_allocation_start_barrier");
                return false;
            }

            _log.Add("REC_TUNER_VIRTUAL_POLICY", $"R{r.Id}",
                $"virtualTuner={SafeValue(r.TunerName)} group={group} chain={chainContinuation} action=require_final_plan_assigned_tuner reason=common_allocation_start_barrier");

            if (!_tunerPool.HasFreeSlotByName(r.TunerName))
            {
                _log.Add("REC_ASSIGNED_TUNER_WAIT", $"R{r.Id}",
                    $"waiting_for_assigned_tuner assignedTuner={SafeValue(r.TunerName)} group={group} " +
                    $"waitMode=tuner_pool_state_signal limitMs={RecordingLaunchWaitForFreeTunerMs} status={_tunerPool.GetStatusSummary()} rule=common_allocation_start_barrier");
                var assignedWaitResult = await _tunerPool.WaitForFreeSlotByNameDetailedAsync(
                    r.TunerName,
                    TimeSpan.FromMilliseconds(RecordingLaunchWaitForFreeTunerMs),
                    ct).ConfigureAwait(false);
                if (assignedWaitResult is TunerAvailabilityWaitResult.PoolStopping or TunerAvailabilityWaitResult.PoolDisposed)
                {
                    SettleInterruptedRecordingStartClaim(r.Id, preferRetry: true, reason: assignedWaitResult.ToString());
                    _log.Add("REC_START_DECISION", $"R{r.Id}",
                        $"result=REJECTED reason={assignedWaitResult} stage=assigned_tuner_wait assignedTuner={SafeValue(r.TunerName)} action=do_not_start_worker rule=shutdown_admission_contract");
                    return false;
                }
            }

            lease = _tunerPool.AcquireForRecordingByName(r.TunerName, r.Id, plannedEnd);
            if (lease is null)
            {
                var rollback = _store.TryRollbackRecordingStart(r.Id, r.DataVersion);
                _forceAllocationReevaluate = true;
                _log.Add("REC_START_DECISION", $"R{r.Id}",
                    $"result=DEFER reason=assigned_tuner_unavailable_no_fallback assignedTuner={SafeValue(r.TunerName)} group={group} " +
                    $"rollbackApplied={rollback.Applied} rollbackReason={SafeValue(rollback.Reason)} dataVersion={rollback.PreviousDataVersion}->{rollback.CurrentDataVersion} " +
                    $"status={_tunerPool.GetStatusSummary()} commonRoute=ALLOC_ROUTE/TUNER_ALLOC nextTick=True rule=common_allocation_start_barrier");
                return false;
            }
        }

        if (lease is null)
        {
            _log.Add("REC_TUNER_ACQUIRE", $"R{r.Id}", $"result=FAIL group={group} requestedTuner={SafeValue(r.TunerName)} chain={chainContinuation} reason=acquire_returned_null status={_tunerPool.GetStatusSummary()}");
            SettleClaimedRecordingLaunchFailure(
                r,
                $"チューナーを確保できませんでした。group={group} tuner={r.TunerName}",
                retryableChainStart: chainBoundaryLaunch || confirmedChainReleaseEvidence is not null);
            return false;
        }
        if (IsRecordingResourceReleaseDidQuarantined(group, lease.Did, out var resourceOwner))
        {
            _log.Add("RECORDING_RESOURCE_RELEASE_GUARD", $"R{r.Id}",
                $"result=DENY group={group} candidateTuner={lease.Name} did={lease.Did} resourceOwner={resourceOwner} " +
                "action=release_acquired_tuner_and_do_not_start reason=prior_pool_lease_identity_is_still_current rule=recording_resource_release_guard_contract");
            ReleaseRejectedRecordingStartLease(r.Id, lease, "prior_pool_lease_identity_is_still_current");
            SettleInterruptedRecordingStartClaim(r.Id, preferRetry: true, reason: "prior_pool_lease_identity_is_still_current");
            _forceAllocationReevaluate = true;
            return false;
        }
        if (IsResidualRecordingWorkerDidQuarantined(group, lease.Did, out var residualOwner))
        {
            _log.Add("RESIDUAL_RECORDING_WORKER_GUARD", $"R{r.Id}",
                $"result=DENY group={group} candidateTuner={lease.Name} did={lease.Did} residualOwner={residualOwner} " +
                "action=release_acquired_tuner_and_do_not_start reason=residual_worker_identity_is_still_alive rule=recording_worker_identity_guard_contract");
            ReleaseRejectedRecordingStartLease(r.Id, lease, "residual_worker_identity_is_still_alive");
            SettleInterruptedRecordingStartClaim(r.Id, preferRetry: true, reason: "residual_worker_identity_is_still_alive");
            _forceAllocationReevaluate = true;
            return false;
        }
        if (IsActiveRecordingDidOccupiedByOtherReservation(group, lease.Did, r.Id, out var activeDidOwner))
        {
            _log.Add("ACTIVE_RECORDING_DID_GUARD", $"R{r.Id}",
                $"result=DENY group={group} candidateTuner={lease.Name} did={lease.Did} owner={activeDidOwner} requestedTuner={SafeValue(r.TunerName)} action=release_acquired_tuner_and_fail_this_start reason=active_recording_did_is_source_of_truth rule=release_contract");
            ReleaseRejectedRecordingStartLease(r.Id, lease, "active_recording_did_is_source_of_truth");
            SettleClaimedRecordingLaunchFailure(
                r,
                $"録画中の実チューナーを保護したため録画開始を中止しました。did={lease.Did} owner={activeDidOwner}",
                retryableChainStart: chainBoundaryLaunch || confirmedChainReleaseEvidence is not null);
            return false;
        }

        if (!chainContinuation && !string.IsNullOrWhiteSpace(r.TunerName))
        {
            var locked = string.Equals(r.TunerName, lease.Name, StringComparison.OrdinalIgnoreCase);
            _log.Add(locked ? "REC_ASSIGNED_TUNER_MATCH" : "REC_ASSIGNED_TUNER_MISMATCH", $"R{r.Id}",
                $"result={(locked ? "OK" : "WARN")} assignedTuner={SafeValue(r.TunerName)} actualTuner={lease.Name} did={lease.Did} group={group} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} rule=release_contract");
        }

        _log.Add("REC_TUNER_ACQUIRE", $"R{r.Id}",
            $"result=OK group={group} requestedTuner={SafeValue(r.TunerName)} chain={chainContinuation} lease={lease.Name} bonDriver={lease.BonDriverFileName} did={lease.Did} elapsedSinceReleaseMs={(lease.ElapsedSinceReleaseMs.HasValue ? lease.ElapsedSinceReleaseMs.Value.ToString("F0") : "-")}");
        _log.Add("TUNER_PIPELINE_AUDIT", $"R{r.Id}",
            BuildReservationPipelineAudit(r, "tuner_acquired", group: group, plannedTuner: r.TunerName, actualTuner: lease.Name, lease: lease, resolvedChannel: resolvedChannel, plannedEnd: plannedEnd, finalGuardApplied: false, note: "actual_tuner_lease_acquired"));
        _log.Add("CHAIN_TRACE", $"R{r.Id}", $"[CHAIN] stage=after_tuner_acquire chain={chainContinuation} lease={lease.Name} did={lease.Did} pid=- status={_tunerPool.GetStatusSummary()}");
        LogChainRecordingEvaluation(r, group, chainContinuation, $"after_tuner_acquire lease={lease.Name} did={lease.Did}");

        // EXTERNAL_TVTEST_PT3_RESPONSIBILITY_BOUNDARY:
        // ここで保護するのはTvAIr自身が設定したViewing-role DIDだけである。管理外TVTestの利用状況は観測せず、
        // その存在を理由に録画Tunerを回避・待機・譲歩・候補除外してはならない。物理PT3チューナーの選択、
        // 包含的利用、競合処理、再配置はBonDriver_PTx/PT3側へ委ね、TvAIrは割当済み論理Tuner/DIDへ通常取得要求を出す。
        // HostからPT3内部の物理割当へ介入する実装を追加しない。
        var viewingAudit = TvTestProcessAuditor.EmitViewingProtectionAudit(
            _log,
            "REC_START_BEFORE_LAUNCH",
            $"R{r.Id}",
            lease.Did,
            lease.BonDriverFileName,
            _tunerPool.GetProtectedViewingDids(),
            blockOnSameDid: true);
        if (viewingAudit.ShouldBlock || _tunerPool.IsViewingReservedDid(lease.Did))
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=FAIL reason=viewing_protection_block targetDid={SafeValue(lease.Did)} targetBonDriver={SafeValue(lease.BonDriverFileName)} targetIsViewingRole={viewingAudit.TargetIsViewingRole} unmanagedExternalTvTest=out_of_scope protectedViewingDids={string.Join(",", viewingAudit.ProtectedViewingDids)}");
            lease.Dispose();
            Fail(r, $"TvAIr設定上の視聴専用チューナー保護のため録画開始を中止しました。targetDid={lease.Did}");
            return false;
        }

        // RECORDING_TUNER_REUSE_INVARIANT:
        // worker終了、デバイス解放、lease解放、occupancy generation更新が揃った時点を再利用可能証拠とする。
        // 経過時間だけを根拠に再利用可否を決める待機を追加してはならない。
        if (lease.ElapsedSinceReleaseMs.HasValue)
        {
            _log.Add("REC_TUNER_REUSE_EVIDENCE", $"R{r.Id}",
                $"result=READY slot={lease.Name} did={lease.Did} elapsedSinceReleaseMs={lease.ElapsedSinceReleaseMs.Value:F0} " +
                "waitMs=0 source=worker_exit+device_release+lease_release+generation rule=recording_tuner_reuse_evidence_contract");
        }

        var channelArg = r.ChannelArgument ?? "";
        var recordFolderForDiscovery = ResolveReservationRecordFolder(r);
        // 録画開始ログはDirectRecorder本線へ渡す入力だけを記録する。
        // EDCB/EpgDataCap_Bonと同じく、局名ではなく .ch2 由来の NID/TSID/SID と chspace/chi を主キーにする。
        _log.Add("REC_OPERATIONAL_MODE", $"R{r.Id}",
            $"mode=TvAIrEpgRecProduction legacyTvTestRecording=False nativeRecProbe=False legacyRecorderExecutableDependency=false service={SafeValue(r.ServiceName)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} channelArg={SafeValue(channelArg)} rule=release_contract");

        TvTestActivityHandle? activityHandle = null;
        _log.Add("RECORDER_ACTIVITY", "RECORD_KEEPER_DISABLED",
            $"reservation=R{r.Id} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title, 80)} reason=recording_process_monitor_migrated_to_tvairepgrec action=no_activitykeeper_tvtest_started rule=release_contract");
        // TUNER_DEVICE_GATE_SCOPE_INVARIANT:
        // 放送波GateはBonDriver Openの同時衝突だけを防ぐ。TvAIrEpgRecがOpenTunerOkを報告した時点で
        // 対象workerは自身のDID/物理デバイス所有を確立済みなので、TS開始・ファイル生成・完全Ready待ちまで
        // Gateを保持してはならない。保持を延ばすと別物理Tunerの1秒刻み投入が数秒単位で直列化される。
        // Gate削除、固定wait追加、OpenTunerOkより前の解放は禁止する。
        var tunerDeviceGateReleased = 0;
        void ReleaseTunerDeviceGateAfterOpen(int workerPid, DirectRecorderStartupObservation observation)
        {
            if (Interlocked.Exchange(ref tunerDeviceGateReleased, 1) != 0) return;
            tunerDeviceAccess.Dispose();
            _log.Add("TUNER_DEVICE_LOCK", $"R{r.Id}",
                $"TUNER_DEVICE_LOCK_RELEASE_AFTER_OPEN owner=REC_START R{r.Id} gateKey={group} pid={workerPid} " +
                $"phase={SafeValue(observation.Phase)} open={observation.OpenTunerOk} setChannel={observation.SetChannelOk} " +
                "reason=physical_device_ownership_established remaining_startup_outside_gate=True rule=tuner_device_gate_minimum_scope_contract");
        }

        var directStart = await TryStartDirectRecorderRecordingAsync(
            r, group, lease, resolvedChannel, plannedEnd, postEndMarginSeconds, recordFolderForDiscovery, ct,
            ReleaseTunerDeviceGateAfterOpen).ConfigureAwait(false);
        var retryableStartupFailureObserved = directStart.RetryableStartupFailure;
        if (!directStart.Success && directStart.RetryableStartupFailure)
        {
            _log.Add("TVAIREPGREC_RECORD_START_RETRY", $"R{r.Id}",
                $"result=RETRY reason={SafeValue(directStart.FailureDetail)} attempt=2 maxAttempts=2 tuner={lease.Name} did={lease.Did} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=worker_structured_start_contract");
            directStart = await TryStartDirectRecorderRecordingAsync(
                r, group, lease, resolvedChannel, plannedEnd, postEndMarginSeconds, recordFolderForDiscovery, ct,
                ReleaseTunerDeviceGateAfterOpen).ConfigureAwait(false);
            retryableStartupFailureObserved |= directStart.RetryableStartupFailure;
        }
        if (!directStart.Success)
        {
            activityHandle?.Dispose();
            lease.Dispose();
            // RECORDING_START_TRANSIENT_FAILURE_INVARIANT:
            // OpenTuner失敗やOpen前worker終了は、通常due／チェーン境界の入口差に関係なく一時失敗として扱う。
            // worker・leaseを解放した後はStarting→Scheduledへ戻し、共通開始経路で再評価する。
            // チャンネル未設定、視聴専用DID、実行ファイル欠落など事前に確定できる恒久設定不備だけをFailedへ終端する。
            SettleClaimedRecordingLaunchFailure(
                r,
                directStart.Message,
                retryableChainStart: chainBoundaryLaunch
                    || confirmedChainReleaseEvidence is not null
                    || retryableStartupFailureObserved,
                startupFailureKind: directStart.FailureKind,
                tunerOpenedBeforeFailure: directStart.TunerOpenedBeforeFailure);
            return false;
        }

        // worker起動成功時点で、取得済みlease identityへPIDを接続する。
        // TunerLease側がPoolLeaseId + OccupancyGenerationを検証するため、名前一致だけで別leaseへPIDを誤接続しない。
        lease.SetProcessId(directStart.ProcessId);
        _log.Add("REC_TUNER_PROCESS_ATTACH", $"R{r.Id}",
            $"result=APPLIED tuner={lease.Name} reservation=R{r.Id} pid={directStart.ProcessId} leaseId={lease.PoolLeaseId} generation={lease.OccupancyGeneration} " +
            "source=worker_start_success identity=pool_lease_id+occupancy_generation rule=recording_tuner_process_identity_contract");

        var session = new RecordingSession(r.Id, directStart.ProcessId, plannedEnd, lease, directStart.OutputPath, directStart.ResponsePath, directStart.StopSignalPath, directStart.ProgressPath, directStart.RuntimeStatsPath, directStart.JobPath, postEndMarginSeconds, activityHandle);

        // PROVISIONAL_RECORDING_SESSION_INVARIANT:
        // worker起動成功からStarting→Recording CAS完了までを無監視にしてはならない。
        // 物理worker・DID・leaseの存在証拠として暫定sessionを先に登録し、CAS成功後だけ正式録画へ昇格する。
        // 同一予約の既存sessionを上書きせず、競合時は新しく起動したworkerだけを停止する。
        var provisionalRegistered = false;
        lock (_sessionGate)
        {
            if (!_activeSessions.ContainsKey(r.Id))
            {
                _activeSessions[r.Id] = session;
                provisionalRegistered = true;
            }
        }
        if (!provisionalRegistered)
        {
            _log.Add("REC_START_PROVISIONAL_SESSION", $"R{r.Id}",
                $"result=REJECTED reason=active_session_already_exists pid={directStart.ProcessId} tuner={SafeValue(lease.Name)} " +
                "action=stop_new_worker_preserve_existing_session rule=recording_lifecycle_cas_contract");
            await AbortUncommittedRecordingStartAsync(session).ConfigureAwait(false);
            return false;
        }

        _log.Add("REC_START_PROVISIONAL_SESSION", $"R{r.Id}",
            $"result=REGISTERED state=Starting committed=False pid={directStart.ProcessId} tuner={SafeValue(lease.Name)} " +
            $"poolLeaseId={lease.PoolLeaseId} generation={lease.OccupancyGeneration} rule=recording_lifecycle_cas_contract");

        // worker起動成功後の永続化はStarting + DataVersionのCASで一括確定する。
        // session側をcommit中へ予約してからDB CASを行い、その間のabort cleanupを禁止する。
        // 状態・実チューナー・開始実績・競合解除を別UPDATEに分けない。
        if (!session.TryBeginRecordingCommit(out var commitGeneration))
        {
            // PROVISIONAL_COMMIT_OWNER_CONFLICT_INVARIANT:
            // commit owner取得失敗を単純な開始失敗として返してはならない。
            // 別経路が既に正式録画へ昇格している場合は成功、cleanup開始済みなら同じcleanup完了を待つ。
            // commit進行中など未確定状態はworkerを止めず、owner終了時のDB/runtime収束へ委ねる。
            if (session.IsRecordingCommitted && IsOwnedRecordingWorkerAlive(session))
                return true;

            if (session.IsAbortCleanupStarted)
            {
                await session.AbortCleanupCompletion.ConfigureAwait(false);
            }
            else if (session.TryGetRecordingCommitWaitSnapshot(out _, out var commitCompletion))
            {
                try
                {
                    await commitCompletion
                        .WaitAsync(TimeSpan.FromSeconds(2), ct)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // commit ownerは継続中。workerを停止せずowner終了収束へ渡す。
                }
            }
            else if (session.IsRecordingCommitted && IsOwnedRecordingWorkerAlive(session))
            {
                return true;
            }

            _forceAllocationReevaluate = true;
            return IsActiveRecordingSessionSourceOfTruth(r.Id, out _);
        }

        var completeStart = _store.TryCompleteRecordingStart(r.Id, r.DataVersion, lease.Name);
        if (!completeStart.Applied || completeStart.Reservation is null)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=ABORT reason=recording_start_commit_rejected detail={SafeValue(completeStart.Reason)} " +
                $"previousStatus={completeStart.PreviousStatus?.ToString() ?? "-"} currentStatus={completeStart.CurrentStatus?.ToString() ?? "-"} " +
                $"dataVersion={completeStart.PreviousDataVersion}->{completeStart.CurrentDataVersion} pid={directStart.ProcessId} tuner={SafeValue(lease.Name)} " +
                "action=stop_uncommitted_worker_release_lease rule=recording_lifecycle_cas_contract");
            var latestAfterCommitReject = _store.GetById(r.Id);
            if (latestAfterCommitReject?.Status == ReservationStatus.Recording
                && IsOwnedRecordingWorkerAlive(session)
                && session.CompleteRecordingCommit(commitGeneration))
            {
                return true;
            }

            session.CancelRecordingCommit(commitGeneration);
            var cleanupAfterCommitReject = await AbortUncommittedRecordingStartAsync(session).ConfigureAwait(false);

            latestAfterCommitReject = _store.GetById(r.Id);
            if (cleanupAfterCommitReject.WorkerIdentityGone && latestAfterCommitReject?.Status == ReservationStatus.Starting)
            {
                // CHAIN_START_COMMIT_REJECT_RETRY_INVARIANT:
                // チェーン境界開始のcommit拒否は、同時更新・境界handoff競合など一時的な失敗になり得る。
                // ここでFailedへ終端すると500ms境界retryへ戻れないため、worker消滅確認後はScheduledへrollbackする。
                // 通常開始の恒久失敗だけをFailedへ収束し、無効予約も従来どおりScheduledへ戻す。
                var retryableChainStart = chainBoundaryLaunch || confirmedChainReleaseEvidence is not null;
                var rollback = retryableChainStart || !latestAfterCommitReject.IsEnabled;
                var settle = rollback
                    ? _store.TryRollbackRecordingStart(r.Id, latestAfterCommitReject.DataVersion)
                    : _store.TryFailRecordingStart(r.Id, latestAfterCommitReject.DataVersion, "recording_start_commit_rejected");
                _log.Add("REC_START_DECISION", $"R{r.Id}",
                    $"result=SETTLED_AFTER_COMMIT_REJECT target={(rollback ? "Scheduled" : "Failed")} retryableChainStart={retryableChainStart} " +
                    $"applied={settle.Applied} detail={SafeValue(settle.Reason)} dataVersion={settle.PreviousDataVersion}->{settle.CurrentDataVersion} " +
                    "rule=recording_lifecycle_cas_contract");
            }
            return false;
        }

        r = completeStart.Reservation;
        if (!session.CompleteRecordingCommit(commitGeneration))
        {
            _log.Add("REC_START_PROVISIONAL_SESSION", $"R{r.Id}",
                $"result=PROMOTION_REJECTED state={session.RecordingCommitState} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} " +
                "action=force_runtime_reconciliation rule=recording_lifecycle_cas_contract");
            _forceAllocationReevaluate = true;
            return false;
        }
        _log.Add("REC_START_PROVISIONAL_SESSION", $"R{r.Id}",
            $"result=PROMOTED state=Recording committed=True pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} " +
            "rule=recording_lifecycle_cas_contract");
        _log.Add("REC_CONFLICT_RECONCILE", $"R{r.Id}",
            $"result=CLEARED_AT_START_COMMIT reason=recording_started_with_actual_tuner actualTuner={lease.Name} did={lease.Did} " +
            $"group={group} statusBefore=Starting previousConflictClearedAtomically=True dataVersion={completeStart.PreviousDataVersion}->{completeStart.CurrentDataVersion} " +
            "rule=release_contract");
        BindChainDirectRecorderSessionScaffold(r, session, null, "recording_started");
        TvAirManagedProcessRegistry.RegisterRecording(directStart.ProcessId, r.Id, lease.Did, lease.BonDriverFileName, directStart.OutputPath);
        _log.Add("REC_START_MODE", $"R{r.Id}",
            $"mode=TvAIrEpgRecProcessStarted pid={directStart.ProcessId} service={SafeValue(r.ServiceName)} sid={r.ServiceId} tuner={lease.Name} did={lease.Did} path={SafeValue(directStart.OutputPath)} seconds={directStart.Seconds} rule=release_contract");
        if (chainContinuation)
        {
            var plannedActualMismatchAtStart = !string.IsNullOrWhiteSpace(r.TunerName)
                && !string.Equals(r.TunerName, lease.Name, StringComparison.OrdinalIgnoreCase);
            _log.Add("CHAIN_RECORDING_RUNTIME_AUDIT", $"R{r.Id}",
                $"result=RECORDING_STARTED stage=recording_started successor=R{r.Id} predecessor={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} " +
                $"plannedTuner={SafeValue(r.TunerName)} actualTuner={SafeValue(lease.Name)} did={SafeValue(lease.Did)} pid={directStart.ProcessId} path={SafeValue(directStart.OutputPath)} " +
                $"handoffSource={(confirmedChainReleaseEvidence is not null ? "boundary_physical_release_evidence" : "final_plan_persisted_tuner")} expectedSource={(confirmedChainReleaseEvidence is not null ? "boundary_physical_release_evidence" : "final_plan_persisted_tuner")} sourceTransition=exact_tuner_acquired_then_recording_started sourceDecision=no_reinfer_no_fallback plannedActualMismatch={plannedActualMismatchAtStart} inheritMatchedActual=True " +
                $"plannedEnd={plannedEnd:MM/dd HH:mm:ss} seconds={directStart.Seconds} separateTsFile=True commonRoute=ALLOC_ROUTE/TUNER_ALLOC assignedTunerPreArm=True behaviorChanged=True rule=release_contract");
        }
        _log.Add("RESERVATION_EXECUTION_CONTRACT", $"R{r.Id}",
            BuildReservationPipelineAudit(r, "recording_started", group: group, plannedTuner: r.TunerName, actualTuner: lease.Name, lease: lease, plannedEnd: plannedEnd, outputPath: directStart.OutputPath, finalGuardApplied: true, note: $"pid={directStart.ProcessId};seconds={directStart.Seconds};commonRoute=ALLOC_ROUTE/TUNER_ALLOC"));
        _log.Add("Scheduler", $"R{r.Id}",
            $"TvAIrEpgRec録画開始: [{r.Title}] tuner={lease.Name}/{lease.Did} pid={directStart.ProcessId} path={directStart.OutputPath}");
        return true;
    }


    private sealed record DirectRecorderStartResult(
        bool Success,
        int ProcessId,
        string OutputPath,
        int Seconds,
        string ResponsePath,
        string StopSignalPath,
        string ProgressPath,
        string RuntimeStatsPath,
        string JobPath,
        string Message,
        bool RetryableStartupFailure = false,
        string FailureDetail = "",
        DirectRecorderStartupFailureKind FailureKind = DirectRecorderStartupFailureKind.None,
        bool TunerOpenedBeforeFailure = false);

    private async Task<DirectRecorderStartResult> TryStartDirectRecorderRecordingAsync(
        Reservation r,
        string group,
        TunerLease lease,
        ChannelTarget? resolvedChannel,
        DateTime plannedEnd,
        int postEndMarginSeconds,
        string recordFolder,
        CancellationToken ct,
        Action<int, DirectRecorderStartupObservation>? onTunerOpened = null)
    {
        if (resolvedChannel is null)
        {
            return new DirectRecorderStartResult(false, 0, string.Empty, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                $"TvAIrEpgRec選局解決に失敗しました。nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId}");
        }

        var bonDriverPath = ResolveBonDriverPathForDirectRecorder(lease.BonDriverFileName);
        if (!File.Exists(bonDriverPath))
        {
            return new DirectRecorderStartResult(false, 0, string.Empty, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                $"TvAIrEpgRec用BonDriverが見つかりません。path={bonDriverPath}");
        }

        var epgRecExe = ResolveTvAIrEpgRecPath();
        if (string.IsNullOrWhiteSpace(epgRecExe) || !File.Exists(epgRecExe))
        {
            return new DirectRecorderStartResult(false, 0, string.Empty, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                "TvAIrEpgRec.exe が見つかりません。");
        }

        _log.Add("TVAIREPGREC_RECORD_ROUTE", $"R{r.Id}",
            $"result=SELECTED exe={SafeValue(epgRecExe)} previousProductionRoute=retired currentProductionRoute=TvAIrEpgRec mode=record " +
            $"legacyRecorderExecutableUsed=false chainDecisionOwner=TvAIrCommonAllocationRoute filenameOwner=TvAIrCurrentPolicy rule=release_contract");

        var now = DateTime.Now;
        var seconds = Math.Max(5, (int)Math.Ceiling((plannedEnd - now).TotalSeconds));
        seconds = Math.Min(seconds, 12 * 60 * 60);
        Directory.CreateDirectory(recordFolder);
        var namingPolicy = ResolveDirectRecorderFileNameTimePolicy(r, now);
        var fileNameBuild = BuildDirectRecorderFileName(r, namingPolicy.BaseTime, lease, resolvedChannel);
        var outputPath = MakeUniqueRecordingPath(Path.Combine(recordFolder, fileNameBuild.FileName));
        _log.Add("RECORD_FILE_NAME_TIME_POLICY", $"R{r.Id}",
            $"mode={namingPolicy.Mode} base={namingPolicy.Base} source={r.Source} reservationStart={r.StartTime:yyyy-MM-dd HH:mm:ss} actualStart={now:yyyy-MM-dd HH:mm:ss} fileBaseTime={namingPolicy.BaseTime:yyyy-MM-dd HH:mm:ss} rule=release_contract");
        _log.Add("RECORD_FILENAME_TITLE_GUARD", $"R{r.Id}",
            $"result={fileNameBuild.TitleGuardResult} source={fileNameBuild.TitleSource} persisted={fileNameBuild.TitlePersisted} originalTitle={SafeValue(fileNameBuild.OriginalReservationTitle)} resolvedTitle={SafeValue(fileNameBuild.RawTitle)} service={SafeValue(r.ServiceName)} event={r.NetworkId}/{r.TransportStreamId}/{r.ServiceId}/{r.EventId} chainPrev={(r.UserChainPreviousId.HasValue ? $"R{r.UserChainPreviousId.Value}" : "-")} chainRoot={(r.UserChainRootId.HasValue ? $"R{r.UserChainRootId.Value}" : "-")} rule=release_contract");
        var fileNameNormalizedChanged = !string.Equals(fileNameBuild.RawTitle, fileNameBuild.NormalizedEventName, StringComparison.Ordinal);
        var fileNameDetail = fileNameNormalizedChanged
            ? $" rawTitle={SafeValue(fileNameBuild.RawTitle)} normalizedEventName={SafeValue(fileNameBuild.NormalizedEventName)} sanitizedEventName={SafeValue(fileNameBuild.SanitizedEventName)}"
            : string.Empty;
        _log.Add("RECORD_FILENAME_NORMALIZE", $"R{r.Id}",
            $"result=OK changed={fileNameNormalizedChanged} fileName={SafeValue(fileNameBuild.FileName)} outputPath={SafeValue(outputPath)} unsupportedTokens={SafeValue(fileNameBuild.UnsupportedTokens)}{fileNameDetail} rule=release_contract");
        _log.Add("RECORD_FILENAME_PIPELINE_AUDIT", $"R{r.Id}",
            BuildReservationPipelineAudit(r, "filename_finalized", group: group, plannedTuner: r.TunerName, actualTuner: lease.Name, lease: lease, resolvedChannel: resolvedChannel, fileNameBuild: fileNameBuild, outputPath: outputPath, plannedEnd: plannedEnd, finalGuardApplied: true, note: $"namingMode={namingPolicy.Mode};timeBase={namingPolicy.Base};changed={fileNameNormalizedChanged}"));
        var workDir = Path.Combine(AppContext.BaseDirectory, "runtime", "tvairepgrec-production-recording");
        Directory.CreateDirectory(workDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var requestPath = Path.Combine(workDir, $"record_job_R{r.Id}_{stamp}_{Guid.NewGuid():N}.json");
        var responsePath = Path.Combine(workDir, $"record_result_R{r.Id}_{stamp}_{Guid.NewGuid():N}.json");
        var progressPath = Path.Combine(workDir, $"record_progress_R{r.Id}_{stamp}_{Guid.NewGuid():N}.jsonl");
        var runtimeStatsPath = Path.Combine(workDir, $"record_runtime_R{r.Id}_{stamp}_{Guid.NewGuid():N}.jsonl");
        var stopSignalPath = Path.Combine(workDir, $"record_stop_R{r.Id}_{stamp}_{Guid.NewGuid():N}.signal");
        var directRecorderChannelIndex = ResolveDirectRecorderChannelIndex(resolvedChannel);
        var directRecorderChannelReason = ResolveDirectRecorderChannelReason(resolvedChannel, directRecorderChannelIndex);
        var channelArgument = $"/chspace {resolvedChannel.ResolvedSpace} /chi {directRecorderChannelIndex} /nid {resolvedChannel.OriginalNetworkId} /tsid {resolvedChannel.TransportStreamId} /sid {resolvedChannel.ServiceId}";
        var displayLogoPaths = _serviceLogoStore.ResolveDisplayLogoPaths(
            resolvedChannel.OriginalNetworkId,
            resolvedChannel.TransportStreamId,
            resolvedChannel.ServiceId,
            r.ServiceName);
        var displayLogoPath = displayLogoPaths.TitleBarLogoPath;
        var centerLogoPath = displayLogoPaths.CenterLogoPath;
        var tvairEpgRecJob = new
        {
            jobId = $"tvairepgrec_production_record_R{r.Id}_{stamp}_{Guid.NewGuid():N}",
            mode = "record",
            group,
            tuner = lease.Name,
            did = lease.Did,
            bonDriver = lease.BonDriverFileName,
            bonDriverPath,
            tvTestExecutablePath = _ini.TvTestExecutablePath ?? string.Empty,
            outputPath,
            resultPath = responsePath,
            progressPath,
            runtimeStatsPath,
            cancelSignalPath = stopSignalPath,
            tsReadSeconds = seconds,
            postEndMarginSeconds,
            recording = new
            {
                reservationId = r.Id,
                serviceName = r.ServiceName ?? string.Empty,
                title = r.Title ?? string.Empty,
                startTime = r.StartTime,
                endTime = r.EndTime,
                outputPath
            },
            channels = new[]
            {
                new
                {
                    serviceName = r.ServiceName,
                    networkId = resolvedChannel.OriginalNetworkId,
                    transportStreamId = resolvedChannel.TransportStreamId,
                    serviceId = resolvedChannel.ServiceId,
                    channelSpace = resolvedChannel.ResolvedSpace,
                    channelIndex = directRecorderChannelIndex,
                    channelArgument
                }
            },
            metadata = new Dictionary<string, string>
            {
                ["caller"] = "TvAIr",
                ["tvairVersion"] = TvAIrVersionContract.ProductVersion,
                ["purpose"] = "production_record_route_tvairepgrec",
                ["recordRuntime"] = "true",
                ["reservationId"] = r.Id.ToString(),
                ["serviceName"] = r.ServiceName ?? string.Empty,
                ["title"] = r.Title ?? string.Empty,
                ["displayServiceName"] = r.ServiceName ?? string.Empty,
                ["displayTitle"] = ReservationTitleDisplayContract.ForUser(r.Title),
                ["displayLogoPath"] = displayLogoPath ?? string.Empty,
                ["titleBarLogoPath"] = displayLogoPath ?? string.Empty,
                ["centerLogoPath"] = centerLogoPath ?? string.Empty,
                ["displayLogoTitleBarType"] = displayLogoPaths.TitleBarLogoType?.ToString() ?? string.Empty,
                ["displayLogoCenterType"] = displayLogoPaths.CenterLogoType?.ToString() ?? string.Empty,
                ["displayLogoTarget"] = "worker_titlebar_center_only",
                ["group"] = group,
                ["tuner"] = lease.Name,
                ["did"] = lease.Did,
                ["didSelectionTransport"] = "process_command_line_/DID",
                ["bonDriver"] = lease.BonDriverFileName,
                ["tvTestExecutablePathPassed"] = string.IsNullOrWhiteSpace(_ini.TvTestExecutablePath) ? "false" : "true",
                ["channelArgument"] = channelArgument,
                ["productionRecordRoute"] = "TvAIrEpgRec",
                ["recordExecutionRoute"] = "TvAIrEpgRec",
                ["legacyRecorderExecutableUsed"] = "false",
                ["chainDecisionOwner"] = "TvAIr common allocation route",
                ["outputPathPolicy"] = "decided_by_TvAIr_current_recording_filename_policy_before_worker_launch",
                ["taskbarIconVisible"] = _ini.ShowTvAIrEpgRecTaskbarIcon ? "true" : "false",
                ["directSetChannelIndex"] = directRecorderChannelIndex.ToString(),
                ["directSetChannelRule"] = directRecorderChannelReason,
                ["rule"] = "release_contract"
            }
        };

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(tvairEpgRecJob, jsonOptions), ct).ConfigureAwait(false);

        var launchKind = TvAIrEpgRecLaunchKind.PrimaryRecord;
        var showWorkerTaskbarIcon = WorkerProcessStartInfoFactory.IsTaskbarIconVisible(launchKind, _ini.ShowTvAIrEpgRecTaskbarIcon);
        var windowPolicy = WorkerProcessStartInfoFactory.GetWindowPolicy(launchKind, _ini.ShowTvAIrEpgRecTaskbarIcon);
        var process = new Process
        {
            StartInfo = WorkerProcessStartInfoFactory.CreateTvAIrEpgRec(epgRecExe, launchKind, _ini.ShowTvAIrEpgRecTaskbarIcon),
            EnableRaisingEvents = false
        };
        WorkerProcessStartInfoFactory.AppendPhysicalTunerDidArgument(process.StartInfo, lease.Did);
        process.StartInfo.ArgumentList.Add("--mode");
        process.StartInfo.ArgumentList.Add("record");
        process.StartInfo.ArgumentList.Add("--job");
        process.StartInfo.ArgumentList.Add(requestPath);
        process.StartInfo.ArgumentList.Add("--progress");
        process.StartInfo.ArgumentList.Add(progressPath);
        process.StartInfo.ArgumentList.Add("--result");
        process.StartInfo.ArgumentList.Add(responsePath);
        process.StartInfo.ArgumentList.Add("--cancel");
        process.StartInfo.ArgumentList.Add(stopSignalPath);
        process.StartInfo.ArgumentList.Add("--ts-read-probe");
        process.StartInfo.ArgumentList.Add("true");
        process.StartInfo.ArgumentList.Add("--read-seconds");
        process.StartInfo.ArgumentList.Add(seconds.ToString());
        process.StartInfo.ArgumentList.Add("--getstream-variant");
        process.StartInfo.ArgumentList.Add("ready-only");
        process.StartInfo.ArgumentList.Add("--ready-threshold");
        process.StartInfo.ArgumentList.Add("50");

        try
        {
            if (!process.Start())
            {
                return new DirectRecorderStartResult(false, 0, outputPath, seconds, responsePath, stopSignalPath, progressPath, runtimeStatsPath, requestPath, "TvAIrEpgRecの起動に失敗しました。");
            }
        }
        catch (Exception ex)
        {
            return new DirectRecorderStartResult(false, 0, outputPath, seconds, responsePath, stopSignalPath, progressPath, runtimeStatsPath, requestPath,
                $"TvAIrEpgRec起動例外: {ex.GetType().Name} {ex.Message}");
        }

        _log.Add("TVAIREPGREC_RECORD_START", $"R{r.Id}",
            $"pid={process.Id} exe={SafeValue(epgRecExe)} job={SafeValue(requestPath)} result={SafeValue(responsePath)} progress={SafeValue(progressPath)} stopSignal={SafeValue(stopSignalPath)} " +
            $"service={SafeValue(r.ServiceName)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} " +
            $"space={resolvedChannel.ResolvedSpace} ch={directRecorderChannelIndex} tvtestArgCh={resolvedChannel.ResolvedChannelIndex} bonCh={resolvedChannel.BonDriverChannel} channelReason={SafeValue(directRecorderChannelReason)} tuner={lease.Name} did={lease.Did} " +
            $"seconds={seconds} path={SafeValue(outputPath)} recordingReservation=R{r.Id} contract=one_worker_one_reservation_one_ts_one_quality_result launchKind={launchKind} taskbarIconVisible={showWorkerTaskbarIcon} windowPolicy={windowPolicy} titleBarLogoPath={SafeValue(displayLogoPath)} centerLogoPath={SafeValue(centerLogoPath)} logoTarget=worker_titlebar_center_only productionRoute=TvAIrEpgRec previousRoute=retired legacyRecorderExecutableUsed=false state=Starting rule=release_contract");

        var startup = await WaitForDirectRecorderStartupAsync(
            process, progressPath, stopSignalPath, r.Id, ct, onTunerOpened).ConfigureAwait(false);
        if (!startup.Success)
        {
            _log.Add("TVAIREPGREC_RECORD_START_GATE", $"R{r.Id}",
                $"result=FAILED pid={process.Id} reason={SafeValue(startup.Reason)} phase={SafeValue(startup.Phase)} open={startup.OpenTunerOk} setChannel={startup.SetChannelOk} tsStarted={startup.TsReadStarted} scopeReady={startup.ScopeReady} mediaPackets={startup.MediaPackets} bytesWritten={startup.BytesWritten} rule=worker_structured_start_contract");
            var workerFailure = ReadDirectRecorderWorkerFailure(responsePath);
            var retryableStartupFailure = IsRetryableDirectRecorderStartupFailure(startup);
            var failureDetail = string.IsNullOrWhiteSpace(workerFailure)
                ? startup.Reason
                : $"{startup.Reason};{workerFailure}";
            _log.Add("TVAIREPGREC_RECORD_START_FAILURE_DETAIL", $"R{r.Id}",
                $"result=FAILED pid={process.Id} retryable={retryableStartupFailure} detail={SafeValue(failureDetail)} resultPath={SafeValue(responsePath)} rule=worker_structured_start_contract");

            // RECORDING_STARTUP_FAILURE_ARTIFACT_LIFECYCLE_INVARIANT:
            // RecordingSession成立前の失敗attemptは通常録画終了cleanupへ到達しない。
            // failure detailをHostログへ転記した後、当該workerの終了を確認できた場合に限り、
            // そのattempt固有のjob/result/progress/runtime/stop signalだけを削除する。
            // worker生存中の証拠削除、別attempt/正常録画sessionのartifact削除、retry条件変更は禁止する。
            await CleanupFailedDirectRecorderStartupArtifactsAsync(
                process,
                r.Id,
                requestPath,
                responsePath,
                progressPath,
                runtimeStatsPath,
                stopSignalPath).ConfigureAwait(false);

            return new DirectRecorderStartResult(false, 0, outputPath, seconds, responsePath, stopSignalPath, progressPath, runtimeStatsPath, requestPath,
                string.IsNullOrWhiteSpace(workerFailure) ? "録画データを取得できませんでした。" : $"録画処理を開始できませんでした。{workerFailure}",
                retryableStartupFailure,
                failureDetail,
                startup.FailureKind,
                startup.OpenTunerOk);
        }

        _log.Add("TVAIREPGREC_RECORD_START_GATE", $"R{r.Id}",
            $"result=ACTIVE pid={process.Id} phase={SafeValue(startup.Phase)} open={startup.OpenTunerOk} setChannel={startup.SetChannelOk} tsStarted={startup.TsReadStarted} scopeReady={startup.ScopeReady} mediaPackets={startup.MediaPackets} bytesWritten={startup.BytesWritten} rule=worker_structured_start_contract");

        return new DirectRecorderStartResult(true, process.Id, outputPath, seconds, responsePath, stopSignalPath, progressPath, runtimeStatsPath, requestPath, "TvAIrEpgRec recording active");
    }



    private async Task CleanupFailedDirectRecorderStartupArtifactsAsync(
        Process process,
        int reservationId,
        string jobPath,
        string responsePath,
        string progressPath,
        string runtimeStatsPath,
        string stopSignalPath)
    {
        var exited = process.HasExited || await WaitForProcessExitSignalAsync(process, 1000).ConfigureAwait(false);
        var targets = new[] { jobPath, responsePath, progressPath, runtimeStatsPath, stopSignalPath }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!exited)
        {
            _log.Add("TVAIREPGREC_RECORD_START_RUNTIME_CLEANUP", $"R{reservationId}",
                $"result=KEPT reason=worker_not_terminal pid={process.Id} targetFiles={targets.Length} rule=recording_startup_failure_artifact_lifecycle_contract");
            return;
        }

        var deleted = 0;
        var failed = 0;
        foreach (var path in targets)
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deleted++;
            }
            catch
            {
                failed++;
            }
        }

        _log.Add("TVAIREPGREC_RECORD_START_RUNTIME_CLEANUP", $"R{reservationId}",
            $"result={(failed == 0 ? "OK" : "PARTIAL")} reason=terminal_startup_failure_session_not_created pid={process.Id} deleted={deleted} failed={failed} targetFiles={targets.Length} rule=recording_startup_failure_artifact_lifecycle_contract");
    }


    private static string ReadDirectRecorderWorkerFailure(string responsePath)
    {
        if (string.IsNullOrWhiteSpace(responsePath) || !File.Exists(responsePath)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(responsePath));
            var root = doc.RootElement;
            var errorType = root.TryGetProperty("errorType", out var et) ? et.GetString() : null;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var tsReadError = root.TryGetProperty("tsReadProbe", out var tsp)
                && tsp.ValueKind == JsonValueKind.Object
                && tsp.TryGetProperty("error", out var tse)
                ? tse.GetString()
                : null;
            var resultMessage = root.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("message", out var rm)
                ? rm.GetString()
                : null;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            string? detail = !string.IsNullOrWhiteSpace(errorType)
                ? errorType
                : !string.IsNullOrWhiteSpace(tsReadError)
                    ? tsReadError
                    : !string.IsNullOrWhiteSpace(error)
                        ? error
                        : !string.IsNullOrWhiteSpace(resultMessage)
                            ? resultMessage
                            : !string.IsNullOrWhiteSpace(message)
                                ? message
                                : null;
            if (detail is null) return string.Empty;
            return detail.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();
        }
        catch (Exception ex)
        {
            return $"result_read_error:{ex.GetType().Name}:{ex.Message}";
        }
    }

    private enum DirectRecorderStartupFailureKind
    {
        None,
        OpenTunerFailed,
        SetChannelFailed,
        ResourceNotFound,
        LoadLibraryFailed,
        CommonTsRouteNotReady,
        WorkerReportedFailure,
        WorkerExited,
        StartupDeadlineExceeded
    }

    private sealed record DirectRecorderStartupObservation(
        bool Success,
        string Reason,
        DirectRecorderStartupFailureKind FailureKind,
        string Phase,
        bool OpenTunerOk,
        bool SetChannelOk,
        bool TsReadStarted,
        bool ScopeReady,
        long MediaPackets,
        long BytesWritten);

    private async Task<DirectRecorderStartupObservation> WaitForDirectRecorderStartupAsync(
        Process process,
        string progressPath,
        string stopSignalPath,
        int reservationId,
        CancellationToken ct,
        Action<int, DirectRecorderStartupObservation>? onTunerOpened = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var directory = Path.GetDirectoryName(progressPath);
        var fileName = Path.GetFileName(progressPath);
        using var changed = new SemaphoreSlim(0, int.MaxValue);
        using var watcher = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            }
            : null;

        if (watcher is not null)
        {
            FileSystemEventHandler signal = (_, _) =>
            {
                try { changed.Release(); } catch (SemaphoreFullException) { }
            };
            RenamedEventHandler renamed = (_, _) =>
            {
                try { changed.Release(); } catch (SemaphoreFullException) { }
            };
            watcher.Changed += signal;
            watcher.Created += signal;
            watcher.Renamed += renamed;
        }

        var exitTask = process.WaitForExitAsync(ct);
        DirectRecorderStartupObservation last = new(false, "progress_not_ready", DirectRecorderStartupFailureKind.None, "WorkerStarting", false, false, false, false, 0, 0);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            last = ReadDirectRecorderStartupObservation(progressPath);
            if (last.OpenTunerOk)
            {
                // OpenTunerOk is the worker-owned physical-device handoff point. The callback is idempotent;
                // repeated progress observations must not release the same gate more than once.
                onTunerOpened?.Invoke(process.Id, last);
            }
            if (last.Success) return last;
            if (IsTerminalStartupFailure(last))
            {
                await StopFailedStartupWorkerAsync(process, stopSignalPath, reservationId, "worker_reported_failure").ConfigureAwait(false);
                return last with { Reason = string.IsNullOrWhiteSpace(last.Reason) ? "worker_reported_failure" : last.Reason, FailureKind = last.FailureKind == DirectRecorderStartupFailureKind.None ? DirectRecorderStartupFailureKind.WorkerReportedFailure : last.FailureKind };
            }
            if (process.HasExited)
            {
                return last with { Reason = $"worker_exited_{process.ExitCode}", FailureKind = DirectRecorderStartupFailureKind.WorkerExited };
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var signalTask = changed.WaitAsync(ct);
            var deadlineTask = Task.Delay(remaining, ct);
            var completed = await Task.WhenAny(signalTask, exitTask, deadlineTask).ConfigureAwait(false);
            if (completed == exitTask)
            {
                return ReadDirectRecorderStartupObservation(progressPath) with { Reason = $"worker_exited_{process.ExitCode}", FailureKind = DirectRecorderStartupFailureKind.WorkerExited };
            }
            if (completed == deadlineTask) break;
        }

        await StopFailedStartupWorkerAsync(process, stopSignalPath, reservationId, "startup_deadline_exceeded").ConfigureAwait(false);
        return last with { Reason = "startup_deadline_exceeded", FailureKind = DirectRecorderStartupFailureKind.StartupDeadlineExceeded };
    }

    private static DirectRecorderStartupObservation ReadDirectRecorderStartupObservation(string progressPath)
    {
        if (string.IsNullOrWhiteSpace(progressPath) || !File.Exists(progressPath))
            return new(false, "progress_missing", DirectRecorderStartupFailureKind.None, "WorkerStarting", false, false, false, false, 0, 0);
        try
        {
            // progressには汎用メッセージ行と構造化TS状態行が共存する。
            // 末尾が汎用行でも直前の構造化状態を失わないよう、直近行を逆順に確認する。
            string snapshot;
            using (var stream = new FileStream(
                progressPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
            {
                snapshot = reader.ReadToEnd();
            }

            var lines = snapshot
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .TakeLast(64)
                .Reverse();

            DirectRecorderStartupObservation? latestStructured = null;
            foreach (var line in lines)
            {
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // worker追記中の末尾行だけが未完成でも、直前の完成済み構造化行を探し続ける。
                    continue;
                }
                using (doc)
                {
                var root = doc.RootElement;
                var hasStructuredState = root.TryGetProperty("Phase", out _)
                    || root.TryGetProperty("phase", out _)
                    || root.TryGetProperty("OpenTunerOk", out _)
                    || root.TryGetProperty("openTunerOk", out _)
                    || root.TryGetProperty("ScopeReady", out _)
                    || root.TryGetProperty("scopeReady", out _)
                    || root.TryGetProperty("MediaPackets", out _)
                    || root.TryGetProperty("mediaPackets", out _)
                    || root.TryGetProperty("FailureCode", out _)
                    || root.TryGetProperty("failureCode", out _);
                if (!hasStructuredState)
                    continue;

                var phase = ReadJsonString(root, "Phase", "phase", "WorkerRunning");
                var stage = ReadJsonString(root, "Stage", "stage", string.Empty);
                var failureCode = ReadJsonString(root, "FailureCode", "failureCode", string.Empty);
                var failureDetail = ReadJsonString(root, "FailureDetail", "failureDetail", string.Empty);
                var open = ReadJsonBool(root, "OpenTunerOk", "openTunerOk");
                var channel = ReadJsonBool(root, "SetChannelOk", "setChannelOk");
                var ts = ReadJsonBool(root, "TsReadStarted", "tsReadStarted");
                var scope = ReadJsonBool(root, "ScopeReady", "scopeReady");
                var media = ReadJsonLong(root, "MediaPackets", "mediaPackets");
                var bytes = ReadJsonLong(root, "BytesWritten", "bytesWritten");
                var success = open && channel && ts && scope && media > 0 && bytes > 0;
                var failureKind = ParseDirectRecorderStartupFailureKind(failureCode);
                var reason = !string.IsNullOrWhiteSpace(failureDetail)
                    ? failureDetail
                    : !string.IsNullOrWhiteSpace(failureCode) ? failureCode : stage;
                    latestStructured = new(success, reason, failureKind, phase, open, channel, ts, scope, media, bytes);
                    break;
                }
            }

            return latestStructured
                ?? new(false, "progress_not_structured_yet", DirectRecorderStartupFailureKind.None, "WorkerStarting", false, false, false, false, 0, 0);
        }
        catch (IOException)
        {
            return new(false, "progress_busy", DirectRecorderStartupFailureKind.None, "WorkerRunning", false, false, false, false, 0, 0);
        }
        catch (JsonException)
        {
            return new(false, "progress_partial", DirectRecorderStartupFailureKind.None, "WorkerRunning", false, false, false, false, 0, 0);
        }
    }

    private static DirectRecorderStartupFailureKind ParseDirectRecorderStartupFailureKind(string? failureCode)
        => failureCode?.Trim().ToLowerInvariant() switch
        {
            "open_tuner_failed" => DirectRecorderStartupFailureKind.OpenTunerFailed,
            "set_channel_failed" => DirectRecorderStartupFailureKind.SetChannelFailed,
            "resource_not_found" => DirectRecorderStartupFailureKind.ResourceNotFound,
            "load_library_failed" => DirectRecorderStartupFailureKind.LoadLibraryFailed,
            "common_ts_route_not_ready" => DirectRecorderStartupFailureKind.CommonTsRouteNotReady,
            "stage_failed" => DirectRecorderStartupFailureKind.WorkerReportedFailure,
            "stage_blocked" => DirectRecorderStartupFailureKind.WorkerReportedFailure,
            _ => DirectRecorderStartupFailureKind.None
        };

    private static bool IsRetryableDirectRecorderStartupFailure(DirectRecorderStartupObservation observation)
    {
        // workerが物理Tunerを所有する前の失敗だけを一時失敗とする。
        // Open後のSetChannel／TS開始失敗は入力・局同定・実装不整合の可能性があるため、ここでは自動再試行へ広げない。
        if (observation.OpenTunerOk || observation.SetChannelOk || observation.TsReadStarted)
            return false;

        return observation.FailureKind is DirectRecorderStartupFailureKind.WorkerExited
            or DirectRecorderStartupFailureKind.OpenTunerFailed;
    }

    private static bool IsTerminalStartupFailure(DirectRecorderStartupObservation observation)
        => observation.FailureKind is DirectRecorderStartupFailureKind.OpenTunerFailed
            or DirectRecorderStartupFailureKind.SetChannelFailed
            or DirectRecorderStartupFailureKind.ResourceNotFound
            or DirectRecorderStartupFailureKind.LoadLibraryFailed
            or DirectRecorderStartupFailureKind.CommonTsRouteNotReady
            or DirectRecorderStartupFailureKind.WorkerReportedFailure;

    private async Task StopFailedStartupWorkerAsync(Process process, string stopSignalPath, int reservationId, string reason)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(stopSignalPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stopSignalPath)!);
                await File.WriteAllTextAsync(stopSignalPath, $"reason={reason} at={DateTimeOffset.Now:O}").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Add("TVAIREPGREC_RECORD_START_GATE", $"R{reservationId}", $"result=STOP_SIGNAL_FAILED type={ex.GetType().Name} rule=worker_structured_start_contract");
        }

        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Processオブジェクト自体がこの起動で得た所有handle。PID再検索は行わず、
            // 同じhandleがまだ生存している場合だけ、そのworkerを終了する。
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: false);
            }
            catch { }
        }
        catch { }
    }

    private static string ReadJsonString(JsonElement root, string primary, string secondary, string fallback)
    {
        if (root.TryGetProperty(primary, out var value) || root.TryGetProperty(secondary, out value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
        return fallback;
    }

    private static bool ReadJsonBool(JsonElement root, string primary, string secondary)
    {
        if (!(root.TryGetProperty(primary, out var value) || root.TryGetProperty(secondary, out value))) return false;
        return value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);
    }

    private static long ReadJsonLong(JsonElement root, string primary, string secondary)
    {
        if (!(root.TryGetProperty(primary, out var value) || root.TryGetProperty(secondary, out value))) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed) ? parsed : 0;
    }




    private (string sids, string names) BuildDirectRecorderTsGroupText(ChannelTarget target)
    {
        var targets = _channelLoader.Load().Targets
            .Where(t => t.OriginalNetworkId == target.OriginalNetworkId && t.TransportStreamId == target.TransportStreamId)
            .OrderBy(t => t.ServiceId)
            .ToList();
        if (targets.Count == 0) targets.Add(target);
        var sids = string.Join('/', targets.Select(t => t.ServiceId.ToString()));
        var names = string.Join(" | ", targets.Select(t => $"{t.ServiceId}:{t.Name}"));
        return (sids, names);
    }

    private static int ResolveDirectRecorderChannelIndex(ChannelTarget target)
    {
        // ChannelTarget.ResolvedChannelIndex is built from the explicit ch2 + ChSet contract.
        // Do not fall back to .ch2-only BonDriverChannel values here.
        return target.ResolvedChannelIndex;
    }

    private static string ResolveDirectRecorderChannelReason(ChannelTarget target, int directRecorderChannelIndex)
    {
        if (string.Equals(target.Group, "GR", StringComparison.OrdinalIgnoreCase))
            return $"gr_uses_explicit_chset_resolved_channel;tvtestArgCh={target.ResolvedChannelIndex};directCh={directRecorderChannelIndex};source={target.ChannelBuildSource}";
        return $"bscs_uses_resolved_chspace_chi;directCh={directRecorderChannelIndex};source={target.ChannelBuildSource}";
    }

    private string ResolveBonDriverPathForDirectRecorder(string bonDriverFileName)
    {
        if (Path.IsPathFullyQualified(bonDriverFileName)) return bonDriverFileName;
        var baseDir = _ini.BonDriverDirectory ?? string.Empty;
        return Path.GetFullPath(Path.Combine(baseDir, bonDriverFileName));
    }


    private static string? ResolveTvAIrEpgRecPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "TvAIrEpgRec.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "TvAIrEpgRec", "TvAIrEpgRec.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TvAIrEpgRec", "bin", "Release", "net8.0-windows", "TvAIrEpgRec.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TvAIrEpgRec", "bin", "Debug", "net8.0-windows", "TvAIrEpgRec.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TvAIrEpgRec", "bin", "Release", "net8.0", "TvAIrEpgRec.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TvAIrEpgRec", "bin", "Debug", "net8.0", "TvAIrEpgRec.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static RecordingFileNameTimePolicy ResolveDirectRecorderFileNameTimePolicy(Reservation r, DateTime actualRecordingStartTime)
    {
        // INTERRUPTED_RECORDING_RECOVERY_FILENAME_INVARIANT:
        // どのイレギュラー入口（Startup / PowerResume / RuntimeWorkerMissing 等）から復旧しても、
        // RecoveryParentReservationId を持つ予約は元録画と同じ命名基準時刻を継承する。
        // 復旧予約生成時に StartTime は元予約からそのまま継承されるため、ここでは reservation_start を正本にする。
        // これにより通常の MakeUniqueRecordingPath が同一ベース名へ (1)/(2)... を一貫して付与できる。
        // 通常の Immediate / Program の初回録画は従来どおり actual_start を維持する。
        if (r.RecoveryParentReservationId.HasValue)
            return new RecordingFileNameTimePolicy("InterruptedRecovery", "recovery_source_start", r.StartTime);

        // 番組表予約/通常予約/自動検索予約は、放送中に後追い登録されてもEPG番組開始時刻を維持する。
        // 今すぐ録画は、フロント/APIが ReservationSource.Immediate を明示した場合だけ録画開始実時刻を使う。
        // プログラム録画はEPG番組単位ではなく時間指定録画なので、今すぐ録画側と同じく録画開始実時刻を使う。
        return r.Source switch
        {
            ReservationSource.Immediate => new RecordingFileNameTimePolicy("Immediate", "actual_start", actualRecordingStartTime),
            ReservationSource.Program => new RecordingFileNameTimePolicy("Program", "actual_start", actualRecordingStartTime),
            _ => new RecordingFileNameTimePolicy(r.Source.ToString(), "reservation_start", r.StartTime)
        };
    }

    private DirectRecorderFileNameBuildResult BuildDirectRecorderFileName(Reservation r, DateTime baseTime, TunerLease? lease = null, ChannelTarget? resolvedChannel = null)
    {
        // TvAIrEpgRec直録画では、TVTest.ini の RecordFileName を正本にして
        // TvAIr側で録画用の相対パスへ展開する。
        // TvAIr独自の全角→半角強制変換・後段の _ 置換・サブフォルダ潰しは行わない。
        var titleGuard = ResolveRecordingFileNameTitle(r);
        var rawTitle = titleGuard.Title;

        string template;
        string evidence;
        if (!TvTestRecordFileNameTemplateResolver.TryResolve(_ini.TvTestExecutablePath, out template, out evidence))
        {
            template = "%year2%年%month2%月%day2%日%hour2%時%minute2%分-%event-name%.ts";
            evidence = $"{evidence} fallback=tvair_default_template";
        }

        var format = TvTestRecordFileNameFormatter.Format(new TvTestRecordFileNameFormatRequest(
            Template: template,
            Now: baseTime,
            StartTime: r.StartTime,
            EndTime: r.EndTime,
            TotTime: null,
            EventName: rawTitle,
            ServiceName: r.ServiceName,
            ChannelName: r.ServiceName,
            ChannelNo: ResolveRecordFileNameChannelNo(r, resolvedChannel),
            ServiceId: r.ServiceId,
            EventId: r.EventId,
            TunerFileName: lease?.BonDriverFileName ?? string.Empty,
            TunerName: lease?.Name ?? (!string.IsNullOrWhiteSpace(r.ActualTunerName) ? r.ActualTunerName : r.TunerName)));

        return new DirectRecorderFileNameBuildResult(
            FileName: format.FileName,
            RawTitle: rawTitle,
            NormalizedEventName: format.EventName,
            SanitizedEventName: format.EventName,
            Template: template,
            Evidence: evidence,
            UnsupportedTokens: format.UnknownTokens,
            OriginalReservationTitle: titleGuard.OriginalTitle,
            TitleSource: titleGuard.Source,
            TitleGuardResult: titleGuard.Result,
            TitlePersisted: titleGuard.Persisted,
            FormatterRule: format.Rule);
    }

    private RecordingFileNameTitleGuardResult ResolveRecordingFileNameTitle(Reservation r)
    {
        var originalTitle = r.Title ?? string.Empty;
        var reservationTitle = originalTitle.Trim();
        if (!string.IsNullOrWhiteSpace(reservationTitle))
            return new RecordingFileNameTitleGuardResult(reservationTitle, originalTitle, "reservation", "PASSTHROUGH", false);

        var epgTitle = ResolveEpgProjectedTitle(r);
        if (!string.IsNullOrWhiteSpace(epgTitle))
        {
            var persisted = _store.UpdateTitleIfBlank(r.Id, epgTitle, r.ServiceName);
            if (persisted) r.Title = epgTitle;
            return new RecordingFileNameTitleGuardResult(epgTitle, originalTitle, "epg_projection", "RECOVERED", persisted);
        }

        var chainTitle = ResolveChainMemberTitleFallback(r);
        if (!string.IsNullOrWhiteSpace(chainTitle))
        {
            var persisted = _store.UpdateTitleIfBlank(r.Id, chainTitle, r.ServiceName);
            if (persisted) r.Title = chainTitle;
            return new RecordingFileNameTitleGuardResult(chainTitle, originalTitle, "chain_member", "RECOVERED", persisted);
        }

        var serviceFallback = string.IsNullOrWhiteSpace(r.ServiceName) ? "TvAIr" : r.ServiceName.Trim();
        var fallbackTitle = $"{serviceFallback}-R{r.Id}";
        return new RecordingFileNameTitleGuardResult(fallbackTitle, originalTitle, "service_reservation_id", "FALLBACK", false);
    }

    private string ResolveEpgProjectedTitle(Reservation r)
    {
        if (r.EventId == 0 || r.ServiceId == 0) return string.Empty;
        try
        {
            var ev = _programEvents.GetByEventKey(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId);
            return (ev?.Title ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private string ResolveChainMemberTitleFallback(Reservation r)
    {
        try
        {
            var chainIds = new HashSet<int>();
            if (r.UserChainRootId.HasValue) chainIds.Add(r.UserChainRootId.Value);
            if (r.UserChainPreviousId.HasValue) chainIds.Add(r.UserChainPreviousId.Value);

            var all = _store.GetAll();
            foreach (var x in all)
            {
                if (x.Id == r.Id) continue;
                if (r.UserChainRootId.HasValue && x.UserChainRootId == r.UserChainRootId) chainIds.Add(x.Id);
                if (x.UserChainRootId == r.Id || x.UserChainPreviousId == r.Id) chainIds.Add(x.Id);
            }

            foreach (var id in chainIds.Where(id => id != r.Id).Distinct().OrderBy(id => id))
            {
                var member = all.FirstOrDefault(x => x.Id == id) ?? _store.GetById(id);
                var title = member?.Title?.Trim();
                if (!string.IsNullOrWhiteSpace(title)) return title;

                if (member is not null)
                {
                    var projected = ResolveEpgProjectedTitle(member);
                    if (!string.IsNullOrWhiteSpace(projected)) return projected;
                }
            }
        }
        catch
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static string ResolveRecordFileNameChannelNo(Reservation r, ChannelTarget? resolvedChannel)
    {
        var source = r.ChannelArgument ?? string.Empty;
        var chMatch = Regex.Match(source, @"(?:^|\s)/ch\s+(\d+)", RegexOptions.IgnoreCase);
        if (chMatch.Success) return chMatch.Groups[1].Value;
        if (resolvedChannel is not null && resolvedChannel.ResolvedChannelIndex >= 0)
            return resolvedChannel.ResolvedChannelIndex.ToString();
        var chiMatch = Regex.Match(source, @"(?:^|\s)/chi\s+(\d+)", RegexOptions.IgnoreCase);
        return chiMatch.Success ? chiMatch.Groups[1].Value : string.Empty;
    }

    private sealed record DirectRecorderFileNameBuildResult(
        string FileName,
        string RawTitle,
        string NormalizedEventName,
        string SanitizedEventName,
        string Template,
        string Evidence,
        string UnsupportedTokens,
        string OriginalReservationTitle,
        string TitleSource,
        string TitleGuardResult,
        bool TitlePersisted,
        string FormatterRule);

    private sealed record RecordingFileNameTitleGuardResult(string Title, string OriginalTitle, string Source, string Result, bool Persisted);

    private sealed record RecordingFileNameTimePolicy(string Mode, string Base, DateTime BaseTime);

    private DateTime ResolveChainPlannedEndForLaunch(Reservation r, DateTime basePlannedEnd)
    {
        // release_contract: チェーン録画は番組単位ファイルを維持するため、rootプロセスをチェーン末尾まで延命しない。
        // 後続番組はFinalConflictPlanのAssignedTunerを正本に、前段と同一物理Tunerで境界pre-arm起動する。
        // 別Tunerへの並行開始はチェーン不変条件違反であり、許可しない。
        if (HasEnabledChainSuccessor(r))
        {
            _log.Add("CHAIN_RECORDING_WINDOW_POLICY", $"R{r.Id}",
                $"result=BASE_ONLY basePlannedEnd={basePlannedEnd:MM/dd HH:mm:ss} " +
                $"reason=assigned_tuner_prearm_program_file_per_reservation commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
        }
        return basePlannedEnd;
    }


    private DateTime ResolveChainPlannedEndForRecordingFollow(Reservation r, DateTime singleProgramPlannedEnd, DateTime currentSessionPlannedEnd)
    {
        // release_contract: RecordingFollowもチェーン末尾延長を行わない。
        // 後続は共通割当済みAssignedTuner（前段ActualTunerをpinした同一物理Tuner）を使う別録画として開始し、
        // root延命や別Tuner再配置で割当ルートを迂回しない。
        if (HasEnabledChainSuccessor(r))
        {
            _log.Add("REC_FOLLOW_CHAIN_PLANNED_END", $"R{r.Id}",
                $"result=BASE_ONLY singleProgramPlannedEnd={singleProgramPlannedEnd:MM/dd HH:mm:ss} currentSessionPlannedEnd={currentSessionPlannedEnd:MM/dd HH:mm:ss} " +
                $"reason=assigned_tuner_prearm_does_not_extend_root_session commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
        }
        return singleProgramPlannedEnd;
    }

    private bool HasEnabledChainSuccessor(Reservation r)
    {
        try
        {
            return _store.GetChainPredecessors().Any(kv =>
            {
                if (kv.Value != r.Id) return false;
                var next = _store.GetById(kv.Key);
                return next is not null
                    && next.IsEnabled
                    && next.Status is ReservationStatus.Scheduled or ReservationStatus.Recording
                    && IsSameServiceIdentity(r, next);
            });
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOwnedRecordingWorkerAlive(RecordingSession session)
    {
        if (session.ProcessId <= 0) return false;

        // Recording lifecycle must distinguish a dead PID from an identity that is merely unavailable.
        // TvAirManagedProcessRegistry.IdentityMatches intentionally treats unavailable identity as
        // non-disproving for broader ownership reconciliation, so using it alone here would make a
        // terminated TvAIrEpgRec look alive until the independent file-growth watchdog fires.
        if (!IsProcessAlive(session.ProcessId)) return false;

        var observed = TvAirManagedProcessRegistry.CaptureIdentity(session.ProcessId);
        if (!session.WorkerIdentity.IsAvailable || !observed.IsAvailable)
            return true; // PID is alive; metadata access alone must not fail an active recording.

        return TvAirManagedProcessRegistry.IdentityMatches(session.WorkerIdentity, observed);
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Refresh();
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    // チェーン後続は予約ごとに独立したTvAIrEpgRecセッションを開始する。
    // チェーン境界では StopSessionAsync → StartRecordingAsync で後続を別録画ファイルとして起動する。

    private static bool IsSameServiceIdentity(Reservation a, Reservation b)
        => a.NetworkId == b.NetworkId
            && a.TransportStreamId == b.TransportStreamId
            && a.ServiceId == b.ServiceId;

    private void BindChainDirectRecorderSessionScaffold(Reservation current, RecordingSession session, Reservation? next, string stage)
    {
        var chainRootId = current.UserChainRootId ?? current.Id;
        var actualTuner = SafeValue(session.Lease.Name);
        var did = SafeValue(session.Lease.Did);
        var bonDriver = SafeValue(session.Lease.BonDriverFileName);
        var nextReservation = next ?? _store.GetAll()
            .Where(x => x.IsUserChain
                && x.UserChainPreviousId == current.Id
                && x.Status == ReservationStatus.Scheduled
                && x.IsEnabled)
            .OrderBy(x => x.StartTime)
            .FirstOrDefault();

        if (nextReservation is null && !current.IsUserChain)
        {
            // 通常予約で後続チェーンがまだ無い場合は、ログを出さず通常経路を完全に静かに保つ。
            return;
        }

        var chainSession = new ChainDirectRecorderSession
        {
            ChainRootReservationId = chainRootId,
            CurrentReservationId = current.Id,
            CurrentServiceName = current.ServiceName,
            CurrentTitle = current.Title,
            ActualTunerName = actualTuner,
            Did = did,
            BonDriverFileName = bonDriver,
            BridgeProcessId = session.ProcessId,
            OutputPath = session.RecordingFilePath,
            SegmentStartTime = current.StartTime,
            SegmentEndTime = current.EndTime,
            PlannedEndTime = session.PlannedEndTime,
        };
        if (nextReservation is not null)
            chainSession.AttachNext(nextReservation);

        var isNew = _chainSessionRegistry.Bind(chainSession);

        _log.Add("CHAIN_SESSION_BIND", $"R{current.Id}",
            $"result={(isNew ? "BOUND" : "UPDATED")} {chainSession.ToLogFields(stage)} " +
            $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC actualTunerIsCurrentSessionOnly=True successorAssignedTunerSource=FinalConflictPlan auditVisible=True normalExecutorFrozen=True rule=release_contract");
    }

    private void RemoveChainDirectRecorderSessionScaffold(int reservationId, string stage, string reason)
    {
        _chainSessionRegistry.Remove(reservationId, out var removed);

        if (removed is not null)
        {
            _log.Add("CHAIN_SESSION_RELEASE", $"R{reservationId}",
                $"result=REMOVED stage={stage} reason={reason} {removed.ToLogFields("release")} rule=release_contract");
        }
    }

    private static string MakeUniqueRecordingPath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    // ─── 明示チェーン境界実行 ───────────────────────────────

    /// <summary>
    /// CHAIN_EXECUTION_ORDER_INVARIANT:
    /// 録画中の時間追従結果から後続番組の最新StartTimeを取得し、その30秒前に前段を停止する。
    /// 同時刻の複数前段は一斉に停止開始し、同一物理Tuner内の停止処理だけを直列化する。
    /// 前段Stopping中もチェーン占有単位と確定Tunerを保持し、後続を通常予約として再配置してはならない。
    /// </summary>
    private async Task CheckPseudoContinuousHandoffAsync(DateTime now, CancellationToken ct)
    {
        await RetryPendingChainBoundariesAsync(now, ct).ConfigureAwait(false);

        // 録画中セッションを取得
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.Where(x => x.IsRecordingCommitted).ToList();
        if (active.Count == 0) return;

        // 連続番組チェーンを取得
        var chains = _store.GetChainPredecessors(); // key=後続ID, value=前番組ID
        // 反転: 前番組ID → 後続番組ID
        var successorOf = chains.ToDictionary(kv => kv.Value, kv => kv.Key);

        if (successorOf.Count == 0) return;

        _log.Add("Scheduler", "Chain",
            $"チェーン検索: セッション数={active.Count} 有効チェーンペア数={successorOf.Count} " +
            $"[{string.Join(", ", successorOf.Select(kv => $"R{kv.Key}→R{kv.Value}"))}]");

        var boundaryTasks = active
            .Select(session => ProcessPseudoContinuousHandoffSessionAsync(session, successorOf, now, ct))
            .ToArray();
        var recordingTunerSnapshot = _tunerPool.GetStatus()
            .Where(slot => string.Equals(slot.Role, "Recording", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var recordingGroupSnapshot = recordingTunerSnapshot
            .GroupBy(slot => NormalizeRecordingLaunchGroup(slot.Group), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{SafeValue(group.Key)}:{group.Count()}")
            .ToArray();
        _log.Add("CHAIN_BOUNDARY_BATCH", "START",
            $"result=BEGIN activeSessions={active.Count} chainPairs={successorOf.Count} configuredRecordingTuners={recordingTunerSnapshot.Count} " +
            $"configuredRecordingGroups=[{string.Join(',', recordingGroupSnapshot)}] fixedTunerLimit=none " +
            $"parallelByReservation=True serializedOnlyByPhysicalTuner=True boundaryCutSec={ChainFrontCutBeforeSuccessorStartSeconds} " +
            $"responsibilityBoundary=tvair_dispatch_and_internal_waits_vs_environment_open_and_worker_ready rule=chain_dynamic_capacity_responsibility_contract");
        await Task.WhenAll(boundaryTasks).ConfigureAwait(false);
        _log.Add("CHAIN_BOUNDARY_BATCH", "END",
            $"result=COMPLETED activeSessions={active.Count} chainPairs={successorOf.Count} parallelByReservation=True serializedOnlyByPhysicalTuner=True rule=chain_simultaneous_front_cut_contract");
    }


    private async Task ProcessPseudoContinuousHandoffSessionAsync(
        RecordingSession session,
        IReadOnlyDictionary<int, int> successorOf,
        DateTime now,
        CancellationToken ct)
    {

            if (!successorOf.TryGetValue(session.ReservationId, out var successorId)) return;

            var successor = _store.GetById(successorId);
            if (successor is null)
            {
                _log.Add("Scheduler", $"R{session.ReservationId}",
                    $"チェーン確認: 後続R{successorId}がDBに存在しません（削除済み？）");
                return;
            }

            if (successor.Status != ReservationStatus.Scheduled)
            {
                _log.Add("Scheduler", $"R{session.ReservationId}",
                    $"チェーン確認: 後続R{successorId}[{successor.Title}] status={successor.Status}のためスキップ");
                return;
            }

            if (!successor.IsEnabled)
            {
                _log.Add("Scheduler", $"R{session.ReservationId}",
                    $"チェーン確認: 後続R{successorId}[{successor.Title}] is_enabled=falseのためスキップ");
                return;
            }

            // CHAIN_FRONT_CUT_ELIGIBILITY_INVARIANT — 変更禁止:
            // 前番組末尾30秒の欠落は、後続が有効・Scheduled・非競合で、同一チェーンと同一固定物理Tunerを
            // 正当に実行できる境界に限る。競合中の後続を救済するために前段を停止してはならない。
            if (successor.IsConflicted)
            {
                _log.Add("CHAIN_FRONT_CUT", $"R{session.ReservationId}",
                    $"result=SKIP predecessor=R{session.ReservationId} successor=R{successorId} reason=successor_conflicted action=no_front_cut rule=chain_front_cut_eligibility_contract");
                return;
            }

            var predecessorForBind = _store.GetById(session.ReservationId);
            if (predecessorForBind is not null)
                BindChainDirectRecorderSessionScaffold(predecessorForBind, session, successor, "handoff_scan");

            // release_contract: チェーン境界は前番組の実物理Tuner継承を不変条件とする。
            // 後続AssignedTunerは共通割当ルートで前段ActualTunerにpinされ、必ず一致しなければならない。
            // 異なるTunerへ再配置された状態では開始せず、共通割当ルートの退行として再試行する。
            // 欠損型チェーンは時間追従後の後続放送開始30秒前に前段を停止する。
            var predecessor = _store.GetById(session.ReservationId);
            var successorDueTime = successor.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
            var assignedSuccessorTuner = successor.TunerName ?? string.Empty;
            var activePredecessorTuner = session.Lease.Name;
            var activePredecessorDid = session.Lease.Did;
            var assignedTunerMissing = string.IsNullOrWhiteSpace(assignedSuccessorTuner);
            var sameAssignedTunerAsPredecessor = !assignedTunerMissing
                && string.Equals(assignedSuccessorTuner, activePredecessorTuner, StringComparison.OrdinalIgnoreCase);
            var boundaryActionAt = successor.StartTime.AddSeconds(-ChainFrontCutBeforeSuccessorStartSeconds);
            var preArmLeadSeconds = Math.Max(0, (int)Math.Round((successorDueTime - boundaryActionAt).TotalSeconds));
            var timeToBoundaryAction = boundaryActionAt - now;
            var boundaryExecutionKey = $"R{session.ReservationId}->R{successorId}";

            _log.Add("CHAIN_BOUNDARY_PLAN", $"R{session.ReservationId}",
                $"result={(assignedTunerMissing ? "MISSING_ASSIGNED_TUNER" : "OK")} predecessor=R{session.ReservationId} successor=R{successorId} " +
                $"assignedTuner={SafeValue(assignedSuccessorTuner)} activePredecessorTuner={SafeValue(activePredecessorTuner)} activePredecessorDid={SafeValue(activePredecessorDid)} " +
                $"sameAssignedTunerAsPredecessor={sameAssignedTunerAsPredecessor} successorDue={successorDueTime:MM/dd HH:mm:ss} successorStart={successor.StartTime:MM/dd HH:mm:ss} " +
                $"boundaryActionAt={boundaryActionAt:MM/dd HH:mm:ss} preArmLeadSeconds={preArmLeadSeconds} scheduler=high_priority intervalMs={ChainBoundaryMonitorIntervalMs} normalTickMs={PollingIntervalMs} commonRoute=ALLOC_ROUTE/TUNER_ALLOC source=FinalConflictPlan.AssignedTuner rule=release_contract");

            if (assignedTunerMissing)
            {
                _log.Add("CHAIN_SUCCESSOR_PREARM", $"R{session.ReservationId}",
                    $"result=SKIP reason=missing_final_plan_assigned_tuner predecessor=R{session.ReservationId} successor=R{successorId} action=no_normal_reallocation_fallback commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
                return;
            }

            if (now < boundaryActionAt)
            {
                if (timeToBoundaryAction.TotalSeconds <= 60 && ShouldLogChainBoundaryWait(boundaryExecutionKey, timeToBoundaryAction.TotalSeconds))
                {
                    _log.Add("CHAIN_SUCCESSOR_PREARM", $"R{session.ReservationId}",
                        $"result=WAIT predecessor=R{session.ReservationId} successor=R{successorId} assignedTuner={SafeValue(assignedSuccessorTuner)} " +
                        $"sameAssignedTunerAsPredecessor={sameAssignedTunerAsPredecessor} boundaryActionAt={boundaryActionAt:HH:mm:ss} remainingSec={timeToBoundaryAction.TotalSeconds:F0} " +
                        $"scheduler=high_priority intervalMs={ChainBoundaryMonitorIntervalMs} normalTickMs={PollingIntervalMs} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
                }
                return;
            }

            if (!TryBeginChainBoundaryExecution(boundaryExecutionKey, session.ReservationId, successorId, now, out var boundaryExecution))
            {
                ChainBoundaryExecutionState boundaryState;
                DateTime nextRetryAt;
                int attempt;
                lock (_sessionGate)
                {
                    var current = _chainBoundaryExecutions.TryGetValue(boundaryExecutionKey, out var existing) ? existing : null;
                    boundaryState = current?.State ?? ChainBoundaryExecutionState.NotStarted;
                    nextRetryAt = current?.NextRetryAt ?? DateTime.MinValue;
                    attempt = current?.Attempt ?? 0;
                }
                if (boundaryState is not (ChainBoundaryExecutionState.Succeeded or ChainBoundaryExecutionState.TerminalFailed))
                {
                    _log.Add("CHAIN_SUCCESSOR_PREARM", $"R{session.ReservationId}",
                        $"result=SKIP reason=boundary_execution_not_due predecessor=R{session.ReservationId} successor=R{successorId} key={boundaryExecutionKey} " +
                        $"state={boundaryState} attempt={attempt} nextRetryAt={(nextRetryAt == DateTime.MaxValue ? "terminal" : nextRetryAt.ToString("MM/dd HH:mm:ss.fff"))} " +
                        $"scheduler=high_priority commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
                }
                return;
            }

            var successorLateAtBoundary = now > successorDueTime;

            if (!sameAssignedTunerAsPredecessor)
            {
                CompleteChainBoundaryExecution(boundaryExecution, false, true, "chain_assigned_tuner_mismatch", now);
                _log.Add("CHAIN_TUNER_INVARIANT", $"R{session.ReservationId}",
                    $"result=RETRYABLE_FAILED predecessor=R{session.ReservationId} successor=R{successorId} expectedTuner={SafeValue(activePredecessorTuner)} assignedTuner={SafeValue(assignedSuccessorTuner)} action=do_not_start_on_different_tuner commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=chain_same_physical_tuner_contract");
                return;
            }

            _log.Add("CHAIN_FRONT_CUT", $"R{session.ReservationId}",
                $"result=BEGIN predecessor=R{session.ReservationId} successor=R{successorId} assignedTuner={SafeValue(assignedSuccessorTuner)} did={SafeValue(activePredecessorDid)} pid={session.ProcessId} " +
                $"successorDue={successorDueTime:MM/dd HH:mm:ss} boundaryActionAt={boundaryActionAt:MM/dd HH:mm:ss} preArmLeadSeconds={preArmLeadSeconds} frontTailMayBeCut=True " +
                $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC reason=same_assigned_tuner_boundary rule=release_contract");

            _log.Add("CHAIN_RECORDING_RUNTIME_AUDIT", $"R{session.ReservationId}",
                $"result=BOUNDARY_STOP_BEGIN stage=front_cut predecessor=R{session.ReservationId} successor=R{successorId} " +
                $"plannedTuner={SafeValue(assignedSuccessorTuner)} actualPredecessorTuner={SafeValue(activePredecessorTuner)} did={SafeValue(activePredecessorDid)} pid={session.ProcessId} boundaryActionAt={boundaryActionAt:MM/dd HH:mm:ss} " +
                $"handoffSource=final_plan_assigned_tuner expectedSource=FinalConflictPlan.AssignedTuner sourceTransition=common_route_assigned_tuner_preserved sourceDecision=cut_front_only_when_same_assigned_tuner plannedActualMismatch=False inheritMatchedActual=True " +
                $"successorDue={successorDueTime:MM/dd HH:mm:ss} separateTsFile=True commonRoute=ALLOC_ROUTE/TUNER_ALLOC stopRestart=True behaviorChanged=True rule=release_contract");

            _log.Add("CHAIN_COMPLETION_EXPECTATION", $"R{session.ReservationId}",
                $"result=BOUNDARY_EXPECTED_END predecessor=R{session.ReservationId} successor=R{successorId} boundaryCutAt={boundaryActionAt:MM/dd HH:mm:ss} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} evidenceUsesBoundaryCut=True rule=release_contract");

            var started = false;
            var successorRecordableAfterRelease = true;
            Reservation? successorForUserEvent = null;
            string? successorStartFailureReason = null;
            try
            {
                await StopSessionAsync(
                    session,
                    ReservationStatus.Completed,
                    suppressNormalStopForBoundary: false,
                    expectedCompletionEnd: boundaryActionAt,
                    afterPhysicalRelease: async () =>
                    {
                        var latestSuccessor = _store.GetById(successorId);
                        successorForUserEvent = latestSuccessor;
                        if (latestSuccessor is null
                            || !latestSuccessor.IsEnabled
                            || latestSuccessor.IsConflicted
                            || latestSuccessor.Status != ReservationStatus.Scheduled)
                        {
                            successorRecordableAfterRelease = false;
                            successorStartFailureReason = "successor_not_recordable_after_physical_release";
                            _log.Add("CHAIN_SUCCESSOR_PREARM", $"R{session.ReservationId}",
                                $"result=SKIP_START reason=successor_not_recordable predecessor=R{session.ReservationId} successor=R{successorId} " +
                                $"exists={latestSuccessor is not null} enabled={latestSuccessor?.IsEnabled.ToString() ?? "-"} conflicted={latestSuccessor?.IsConflicted.ToString() ?? "-"} status={latestSuccessor?.Status.ToString() ?? "-"} boundaryState=TerminalFailed rule=chain_front_cut_eligibility_contract");
                            return;
                        }

                        var releaseEvidence = new ChainReleaseStartEvidence(
                            boundaryExecution.Key,
                            boundaryExecution.Attempt,
                            session.ReservationId,
                            successorId,
                            latestSuccessor.DataVersion,
                            latestSuccessor.UserChainRootId,
                            activePredecessorTuner,
                            activePredecessorTuner,
                            session.ProcessId,
                            DateTime.Now);
                        lock (_sessionGate)
                        {
                            if (_chainBoundaryExecutions.TryGetValue(boundaryExecution.Key, out var current)
                                && ReferenceEquals(current, boundaryExecution))
                            {
                                current.ReleaseEvidence = releaseEvidence;
                            }
                        }
                        _log.Add("CHAIN_PHYSICAL_RELEASE_HANDOFF", $"R{session.ReservationId}",
                            $"result=BEGIN predecessor=R{session.ReservationId} successor=R{successorId} releasedTuner={SafeValue(activePredecessorTuner)} " +
                            $"boundaryKey={SafeValue(boundaryExecution.Key)} boundaryAttempt={boundaryExecution.Attempt} successorDataVersion={latestSuccessor.DataVersion} releaseGeneration=confirmed " +
                            $"action=start_successor_before_predecessor_quality_finalization commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=chain_physical_release_handoff_contract");
                        started = await StartRecordingAsync(latestSuccessor, ct, chainBoundaryLaunch: true, chainReleaseEvidence: releaseEvidence).ConfigureAwait(false);
                        if (!started)
                            successorStartFailureReason = "successor_start_returned_false_after_physical_release";
                        _log.Add("CHAIN_PHYSICAL_RELEASE_HANDOFF", $"R{session.ReservationId}",
                            $"result={(started ? "SUCCESS" : "START_FAILED")} predecessor=R{session.ReservationId} successor=R{successorId} releasedTuner={SafeValue(activePredecessorTuner)} " +
                            $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=chain_physical_release_handoff_contract");
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CompleteChainBoundaryExecution(boundaryExecution, false, true, "successor_start_cancelled_after_physical_release", now);
                throw;
            }
            catch (Exception ex)
            {
                CompleteChainBoundaryExecution(boundaryExecution, false, true, successorStartFailureReason ?? "predecessor_stop_or_successor_start_exception", now);
                _log.Add("CHAIN_FRONT_CUT", $"R{session.ReservationId}",
                    $"result=RETRYABLE_FAILED predecessor=R{session.ReservationId} successor=R{successorId} attempt={boundaryExecution.Attempt} reason={SafeValue(successorStartFailureReason ?? "predecessor_stop_or_successor_start_exception")} error={TrimForLog(ex.Message, 180)} rule=release_contract");
                return;
            }

            if (!successorRecordableAfterRelease)
            {
                CompleteChainBoundaryExecution(boundaryExecution, false, false, successorStartFailureReason ?? "successor_not_recordable_after_physical_release", now);
                return;
            }
            var successorStartObservedAt = DateTime.Now;
            var boundaryTargetMissed = successorStartObservedAt > successorDueTime;
            var programStartLate = successorForUserEvent is not null && successorStartObservedAt > successorForUserEvent.StartTime;
            var boundaryDelayMs = Math.Max(0L, (long)(successorStartObservedAt - successorDueTime).TotalMilliseconds);
            _log.Add("CHAIN_SUCCESSOR_START_RESULT", $"R{session.ReservationId}",
                $"result={(started ? "SUCCESS" : "START_FAILED")} predecessor=R{session.ReservationId} successor=R{successorId} assignedTuner={SafeValue(assignedSuccessorTuner)} did={SafeValue(activePredecessorDid)} " +
                $"sameAssignedTunerAsPredecessor=True separateTsFile=True stopRestart=True frontTailMayBeCut=True preArmLeadSeconds={preArmLeadSeconds} " +
                $"boundaryTarget={successorDueTime:MM/dd HH:mm:ss.fff} startObservedAt={successorStartObservedAt:MM/dd HH:mm:ss.fff} boundaryTargetMissed={boundaryTargetMissed} boundaryDelayMs={boundaryDelayMs} programStartLate={programStartLate} " +
                $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
            _log.Add("CHAIN_RECORDING_RUNTIME_AUDIT", $"R{session.ReservationId}",
                $"result={(started ? "SUCCESS" : "START_FAILED")} stage=successor_start_result predecessor=R{session.ReservationId} successor=R{successorId} " +
                $"expectedTuner={SafeValue(assignedSuccessorTuner)} expectedSource=FinalConflictPlan.AssignedTuner actualTuner={SafeValue(assignedSuccessorTuner)} " +
                $"handoffSource=final_plan_assigned_tuner sourceTransition=common_route_assigned_tuner_preserved sourceDecision=use_final_plan_value_already_pinned_from_predecessor_actual_tuner inheritMatchedActual={started} started={started} " +
                $"separateTsFile=True commonRoute=ALLOC_ROUTE/TUNER_ALLOC stopRestart=True frontTailMayBeCut=True behaviorChanged=True rule=release_contract");
            var latestAfterStart = _store.GetById(successorId);
            var converged = latestAfterStart?.Status == ReservationStatus.Recording;
            var retryable = !converged
                && latestAfterStart is not null
                && latestAfterStart.IsEnabled
                && latestAfterStart.Status == ReservationStatus.Scheduled
                && now < latestAfterStart.EndTime;
            CompleteChainBoundaryExecution(boundaryExecution, converged, retryable,
                converged ? "successor_started" : "successor_start_failed_after_front_cut", now);
            var successorForEvent = latestAfterStart ?? successorForUserEvent;
            if (successorForEvent is not null)
                _userEvents.AddChainSwitched(predecessor, successorForEvent, converged);
            return;
        
    }

    private void ConvergePastNonTerminalReservationsWithoutRuntimeOwner(DateTime now)
    {
        // PAST_NONTERMINAL_OWNER_CONVERGENCE_INVARIANT:
        // 終了済み予約を表示側で隠さず、Reservation lifecycle自身をterminalへ収束させる。
        // ただしgeneric cleanupがRecordingFailedを生成してはならない。録画開始証拠のないScheduled/StartingだけをCancelledへ収束する。
        // RecordingStartedAtあり、Recording/Stopping、UserChainは各録画owner/chain lifecycleだけが終端権限を持つ。
        // 予定終了直後は通常停止・post-margin・worker終端と競合し得るため2分の安全猶予を置く。
        var cutoff = now.AddMinutes(-2);
        var candidates = _store.GetAll()
            .Where(r => r.Source != ReservationSource.Epg)
            .Where(r => !r.IsUserChain)
            .Where(r => r.EndTime < cutoff)
            .Where(r => !r.RecordingStartedAt.HasValue)
            .Where(r => r.Status is ReservationStatus.Scheduled or ReservationStatus.Starting)
            .OrderBy(r => r.EndTime)
            .ThenBy(r => r.Id)
            .ToList();

        if (candidates.Count == 0) return;

        HashSet<int> runtimeOwned;
        lock (_sessionGate) runtimeOwned = _activeSessions.Keys.ToHashSet();

        var applied = 0;
        var skippedRuntimeOwner = 0;
        var rejected = 0;
        foreach (var r in candidates)
        {
            if (runtimeOwned.Contains(r.Id))
            {
                skippedRuntimeOwner++;
                continue;
            }

            ReservationLifecycleTransitionResult terminal;
            const string reason = "past_never_started_without_runtime_owner";

            terminal = r.Status switch
            {
                ReservationStatus.Scheduled => _store.TryFinalizeScheduledReservation(
                    r.Id, r.DataVersion, ReservationStatus.Cancelled,
                    "past_nonterminal_owner_convergence", null),
                ReservationStatus.Starting => _store.TryCancelStartingReservation(
                    r.Id, r.DataVersion, "past_nonterminal_owner_convergence"),
                _ => new ReservationLifecycleTransitionResult(false, "not_never_started_nonterminal", r.Id, r.Status, r.Status, r.DataVersion, r.DataVersion, r)
            };

            _log.Add("RESERVATION_PAST_NONTERMINAL_CONVERGENCE", $"R{r.Id}",
                $"result={(terminal.Applied ? "APPLIED" : "REJECTED")} from={r.Status} to={(terminal.Reservation?.Status.ToString() ?? "-")} " +
                $"start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} recordingStartedAt={(r.RecordingStartedAt.HasValue ? r.RecordingStartedAt.Value.ToString("MM/dd HH:mm:ss") : "-")} " +
                $"recordingFinishedAt={(r.RecordingFinishedAt.HasValue ? r.RecordingFinishedAt.Value.ToString("MM/dd HH:mm:ss") : "-")} " +
                $"runtimeOwner=False reason={reason} casReason={SafeValue(terminal.Reason)} dataVersion={terminal.PreviousDataVersion}->{terminal.CurrentDataVersion} " +
                "rule=past_nonterminal_owner_convergence_contract");

            if (terminal.Applied) applied++; else rejected++;
        }

        if (applied > 0 || skippedRuntimeOwner > 0 || rejected > 0)
        {
            _log.Add("RESERVATION_PAST_NONTERMINAL_CONVERGENCE", "Summary",
                $"result=COMPLETED candidates={candidates.Count} applied={applied} skippedRuntimeOwner={skippedRuntimeOwner} rejected={rejected} cutoff={cutoff:MM/dd HH:mm:ss} " +
                "uiFiltering=none lifecycleOwner=reservation_scheduler rule=past_nonterminal_owner_convergence_contract");
        }

        if (applied > 0)
            _forceAllocationReevaluate = true;
    }

    // ─── 録画中時間追従（退化修復） ───────────────────────────────

    private void RestoreActiveRecordingStatusesBeforeDecision(DateTime now, string stage)
    {
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.Where(x => x.IsRecordingCommitted).ToList();
        if (active.Count == 0) return;

        foreach (var session in active)
        {
            var r = _store.GetById(session.ReservationId);
            if (r is null) continue;
            if (r.Status != ReservationStatus.Failed) continue;
            if (now >= session.PlannedEndTime) continue;
            if (!IsOwnedRecordingWorkerAlive(session)) continue;

            var restore = _store.TryRestoreFailedRecordingRuntime(r.Id, r.DataVersion);
            _log.Add("REC_STATUS_GUARD_ACTIVE_SESSION", $"R{r.Id}",
                $"result={(restore.Applied ? "RESTORED_BEFORE_DECISION" : "RESTORE_REJECTED")} stage={stage} reason=active_tvairepgrec_session_is_source_of_truth pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} previousStatus=Failed " +
                $"casReason={SafeValue(restore.Reason)} dataVersion={restore.PreviousDataVersion}->{restore.CurrentDataVersion} rule=recording_lifecycle_cas_contract");
        }
    }

    public int ObservePowerSuspendRecordingState(DateTime suspendedAt, string suspendCycleId)
    {
        List<RecordingSession> active;
        lock (_sessionGate)
            active = _activeSessions.Values.Where(x => x.IsRecordingCommitted).ToList();

        _log.Add("REC_POWER_SUSPEND_RECORDING_OBSERVE", suspendCycleId,
            $"result=OBSERVED sessions={active.Count} suspend={suspendedAt:MM/dd HH:mm:ss} " +
            "action=observe_only_no_recording_interrupt_decision_on_power_notification rule=power_notification_observation_only_contract");
        return active.Count;
    }


    public async Task<TunerOwnershipReconcileResult> ReconcileOwnedProcessesAfterResumeAsync(
        TunerOwnershipReconcileContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reconcileNow = DateTime.Now;
        List<RecordingSession> before;
        lock (_sessionGate) before = _activeSessions.Values.ToList();

        // Resume時も起動時と同じ所有契約を使う。DBがRecordingなのにactive sessionが欠落している場合だけ、
        // 生存workerを完全Identity照合して再Attachする。NoWorker／Identity不一致は推測補正せず隔離する。
        var activeIds = before.Select(x => x.ReservationId).ToHashSet();
        var dbRecording = _store.GetByStatus(ReservationStatus.Recording).ToList();
        var resumeAttached = 0;
        var resumeOwnershipUnavailable = 0;
        var resumeNoWorker = 0;
        var resumeRecovered = 0;
        var finalizeMissingWorker = context.Kind == TunerOwnershipReconcileKind.PowerResumeVerification;
        foreach (var reservation in dbRecording.Where(x => !activeIds.Contains(x.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reattach = TryReattachAliveRecordingWorker(reservation, reconcileNow, RecordingReattachOrigin.Resume);
            if (reattach.State == RecordingWorkerReattachState.Attached)
            {
                resumeAttached++;
                continue;
            }

            if (reattach.State == RecordingWorkerReattachState.NoWorker && finalizeMissingWorker)
            {
                // Resume直後の最初の観測ではWindowsのプロセス可視性がまだ収束していない可能性があるため、
                // 2秒後のverificationでNoWorkerが再確認された場合だけ、起動時と同じ中断録画回復契約へ収束させる。
                // DBをRecordingのまま永久保持して将来の割当を塞ぐ状態へ戻してはならない。
                if (HandleInterruptedRecordingWithoutWorker(reservation, reconcileNow, InterruptedRecordingRecoveryTrigger.PowerResumeVerification))
                {
                    resumeRecovered++;
                    _log.Add("REC_RESUME_REATTACH", $"R{reservation.Id}",
                        $"result=RECOVERED_AFTER_VERIFICATION cycle={context.CycleId} source={context.Source} workerState={reattach.State} reason={SafeValue(reattach.Reason)} action=converged_via_interrupted_recording_recovery rule=recording_worker_reattach_contract");
                    continue;
                }
            }

            if (reattach.State == RecordingWorkerReattachState.NoWorker)
                resumeNoWorker++;
            else
                resumeOwnershipUnavailable++;

            _log.Add("REC_RESUME_REATTACH", $"R{reservation.Id}",
                $"result=OWNERSHIP_UNAVAILABLE cycle={context.CycleId} source={context.Source} " +
                $"workerState={reattach.State} pid={(reattach.ProcessId.HasValue ? reattach.ProcessId.Value.ToString() : "-")} reason={SafeValue(reattach.Reason)} " +
                $"action={(reattach.State == RecordingWorkerReattachState.NoWorker && !finalizeMissingWorker ? "preserve_until_resume_verification" : "preserve_db_recording_do_not_kill_do_not_reassign")} rule=recording_worker_reattach_contract");
        }

        var previousCycle = _powerResumeCycleContext.Value;
        _powerResumeCycleContext.Value = context.CycleId;
        try
        {
            if (context.Kind == TunerOwnershipReconcileKind.PowerResume)
            {
                List<RecordingSession> aliveSessions;
                lock (_sessionGate)
                    aliveSessions = _activeSessions.Values.Where(x => x.IsRecordingCommitted && IsOwnedRecordingWorkerAlive(x)).ToList();

                foreach (var session in aliveSessions)
                {
                    _log.Add("REC_POWER_RESUME_RECORDING_OBSERVE", $"R{session.ReservationId}",
                        $"result=PRESERVED_ALIVE cycle={context.CycleId} source={context.Source} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} " +
                        "reason=owned_worker_alive powerNotification=observation_only action=continue_same_session_same_file rule=power_notification_observation_only_contract");
                }
            }

            if (context.Kind == TunerOwnershipReconcileKind.PowerResumeVerification)
            {
                List<RecordingSession> missingAfterVerification;
                lock (_sessionGate)
                {
                    missingAfterVerification = _activeSessions.Values
                        .Where(x => x.IsRecordingCommitted)
                        .Where(x => reconcileNow < x.PlannedEndTime.AddSeconds(-30))
                        .Where(x => !IsOwnedRecordingWorkerAlive(x))
                        .ToList();
                }

                foreach (var session in missingAfterVerification)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reservation = _store.GetById(session.ReservationId);
                    lock (_sessionGate)
                        _recordingTerminalFailureReasons[session.ReservationId] = "worker_missing_confirmed_after_power_resume";

                    _log.Add("REC_POWER_RESUME_RECORDING_EVIDENCE", $"R{session.ReservationId}",
                        $"result=WORKER_MISSING_CONFIRMED cycle={context.CycleId} source={context.Source} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} " +
                        $"service={SafeValue(reservation?.ServiceName)} title={ReservationDisplayTitle(reservation?.Title)} " +
                        "evidence=owned_worker_missing_after_bounded_verification action=close_missing_segment_then_use_common_recovery rule=power_resume_worker_evidence_contract");

                    await StopSessionAsync(
                        session,
                        ReservationStatus.Failed,
                        suppressNormalStopForBoundary: false,
                        processAlreadyGone: true,
                        suppressPostStopMaintenance: true,
                        preserveRecordingLifecycleForRecovery: true).ConfigureAwait(false);

                    var recoverySource = _store.GetById(session.ReservationId);
                    if (recoverySource?.Status == ReservationStatus.Recording
                        && HandleInterruptedRecordingWithoutWorker(recoverySource, DateTime.Now, InterruptedRecordingRecoveryTrigger.PowerResumeVerification))
                    {
                        resumeRecovered++;
                        _log.Add("REC_POWER_RESUME_RECORDING_EVIDENCE", $"R{session.ReservationId}",
                            $"result=RECOVERY_QUEUED cycle={context.CycleId} source={context.Source} action=common_interrupted_recording_recovery fileCollisionPolicy=append_number_suffix rule=power_resume_worker_evidence_contract");
                    }
                }
            }

            // PowerResume直後はプロセス可視性の収束前なので、missingだけを根拠に終端しない。
            // Verificationでは上の実証拠付き回復が所有する。通常Periodic/Startupでは既存missing-process収束を維持する。
            await CheckSessionEndsAsync(reconcileNow).ConfigureAwait(false);
            if (context.Kind is not (TunerOwnershipReconcileKind.PowerResume or TunerOwnershipReconcileKind.PowerResumeVerification))
                await FinalizeActiveRecordingSessionsWithMissingProcessAsync(reconcileNow).ConfigureAwait(false);
        }
        finally
        {
            _powerResumeCycleContext.Value = previousCycle;
        }

        List<RecordingSession> after;
        lock (_sessionGate) after = _activeSessions.Values.ToList();
        var finalized = Math.Max(0, before.Count + resumeAttached - after.Count);
        var stalePoolLeases = after.Where(session => !session.Lease.IsCurrent).ToList();
        foreach (var stale in stalePoolLeases)
        {
            _log.Add("RECORDING_RECONCILE_STALE_POOL_LEASE", $"R{stale.ReservationId}",
                $"result=OWNERSHIP_UNAVAILABLE cycle={context.CycleId} source={context.Source} pid={stale.ProcessId} tuner={SafeValue(stale.Lease.Name)} " +
                $"poolLeaseId={stale.Lease.PoolLeaseId:D} occupancyGeneration={stale.Lease.OccupancyGeneration} action=preserve_worker_reject_stale_cleanup rule=release_contract");
        }
        return new TunerOwnershipReconcileResult(
            "Recording",
            Math.Max(before.Count, dbRecording.Count),
            Math.Max(0, after.Count - stalePoolLeases.Count),
            finalized,
            stalePoolLeases.Count + resumeOwnershipUnavailable + resumeNoWorker,
            0,
            $"cycle={context.CycleId} source={context.Source} resumeAttached={resumeAttached} resumeRecovered={resumeRecovered} " +
            $"resumeOwnershipUnavailable={resumeOwnershipUnavailable} resumeNoWorker={resumeNoWorker} stalePoolLease={stalePoolLeases.Count}");
    }

    private async Task FinalizeActiveRecordingSessionsWithMissingProcessAsync(DateTime now)
    {
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.Where(x => x.IsRecordingCommitted).ToList();
        if (active.Count == 0) return;

        var finalizedCount = 0;
        foreach (var session in active)
        {
            // 通常停止境界では StopSessionAsync が正本。境界付近をここで失敗扱いしない。
            if (now >= session.PlannedEndTime.AddSeconds(-30))
                continue;

            if (IsOwnedRecordingWorkerAlive(session))
                continue;

            var r = _store.GetById(session.ReservationId);
            if (r is null)
            {
                _log.Add("REC_INTERRUPTED_DETECTED", $"R{session.ReservationId}",
                    $"result=DETECTED reason=reservation_missing_worker_process_gone pid={session.ProcessId} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} tuner={SafeValue(session.Lease.Name)} action=release_session_only rule=release_contract");
            }
            else
            {
                var fileEvidence = ProbeInterruptedRecordingFile(r);
                _log.Add("REC_INTERRUPTED_DETECTED", $"R{session.ReservationId}",
                    $"result=DETECTED reason=worker_process_missing_before_planned_end pid={session.ProcessId} now={now:MM/dd HH:mm:ss} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} file={SafeValue(fileEvidence.Summary)} rule=release_contract");
                lock (_sessionGate)
                    _recordingTerminalFailureReasons[session.ReservationId] = "worker_process_missing_before_planned_end";
            }

            var preserveForRecovery = r?.Status == ReservationStatus.Recording;
            await StopSessionAsync(
                session,
                ReservationStatus.Failed,
                suppressNormalStopForBoundary: false,
                processAlreadyGone: true,
                suppressPostStopMaintenance: true,
                preserveRecordingLifecycleForRecovery: preserveForRecovery).ConfigureAwait(false);

            if (preserveForRecovery)
            {
                var recoverySource = _store.GetById(session.ReservationId);
                if (recoverySource?.Status == ReservationStatus.Recording
                    && HandleInterruptedRecordingWithoutWorker(recoverySource, DateTime.Now, InterruptedRecordingRecoveryTrigger.RuntimeWorkerMissing))
                {
                    _log.Add("REC_INTERRUPTED_RECOVERY_RUNTIME", $"R{session.ReservationId}",
                        $"result=RECOVERY_QUEUED reason=worker_process_missing_before_planned_end pid={session.ProcessId} " +
                        "action=common_interrupted_recording_recovery fileCollisionPolicy=append_number_suffix rule=interrupted_recording_recovery_contract");
                }
            }

            finalizedCount++;
        }

        if (finalizedCount > 0)
        {
            var deferred = StopPhaseGate.ConsumeDeferred();
            if (deferred.allocationRequest is not null || deferred.wakeRebuild)
            {
                _log.Add("Scheduler", "RecordingMissingProcessBatch",
                    $"STOP_PHASE_CONSUME pendingAlloc={deferred.allocCount} pendingWake={deferred.wakeCount} reason=after_missing_process_batch_completed");
            }

            if (deferred.allocationRequest is not null)
            {
                var request = deferred.allocationRequest with
                {
                    ReevaluateAllocations = true,
                    RefreshWakeTask = true,
                    BypassStopPhaseGate = true
                };
                var applied = _allocationRoute.Run(request);
                _log.Add("STOP_PHASE_DEFERRED_ROUTE_APPLIED", "RecordingMissingProcessBatch",
                    $"result=OK source={request.Source} action={request.Action} matcher={request.RunKeywordMatcher} syncProgram={request.SyncProgramRuleReservations} reevaluate={request.ReevaluateAllocations} preEpg={request.RefreshPreRecordEpgEntries} wake={request.RefreshWakeTask} changed={applied.ChangedCount} conflictOn={applied.ConflictOnCount} conflictOff={applied.ConflictOffCount} deferredAgain={applied.Deferred} pendingAlloc={deferred.allocCount} pendingWake={deferred.wakeCount} rule=release_contract");
            }
            else
            {
                ReevaluateAndLog($"録画worker消失{finalizedCount}件終了後", bypassStopPhaseGate: true);
            }

            _log.Add("REC_STOP_BATCH_FINALIZE", "RecordingMissingProcessBatch",
                $"result=OK finalized={finalizedCount} allocation=once wake=once afterAction=skipped_failed_sessions rule=release_contract");
        }
    }

    private static bool IsUserRuntimeStartRewindGuardTarget(Reservation r, RecordingSession session)
    {
        // release_contract: Immediate/manual user recordings can be registered after the EPG event has already started.
        // Once TvAIrEpgRec is running, the reservation/session start is the runtime truth; REC_FOLLOW may still extend the end,
        // but must not move StartTime/occupancy/segmentStart backwards to the EPG event start.
        if (session.ProcessId <= 0) return false;
        return r.Source is ReservationSource.Immediate or ReservationSource.Manual;
    }

    private async Task CheckActiveRecordingFileGrowthAsync(DateTime now, CancellationToken ct)
    {
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.Where(x => x.IsRecordingCommitted).ToList();
        if (active.Count == 0) return;

        foreach (var session in active)
        {
            if (session.RecordingFileStallStopRequested) continue;
            if (now >= session.PlannedEndTime.AddSeconds(-RecordingFileGrowthPlannedEndGuardSec)) continue;

            var r = _store.GetById(session.ReservationId);
            if (r is null || r.Status != ReservationStatus.Recording) continue;

            var observation = ObserveRecordingFileGrowth(session, now);
            if (!observation.ShouldStop) continue;

            if (IsStartupZeroByteRecoveryReason(observation)
                && await TryRecoverStartupZeroByteRecordingAsync(session, r, observation, now, ct).ConfigureAwait(false))
            {
                continue;
            }

            session.MarkRecordingFileStallStopRequested();
            lock (_sessionGate) _recordingTerminalFailureReasons[session.ReservationId] = observation.Reason;
            _log.Add("REC_WRITE_STALLED", $"R{session.ReservationId}",
                $"result=FAILED reason={observation.Reason} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} " +
                $"path={SafeValue(session.RecordingFilePath)} bytes={observation.Bytes} previousBytes={observation.PreviousBytes} " +
                $"monitorStarted={session.RecordingFileGrowthWatchStartedAt:MM/dd HH:mm:ss} lastGrowth={session.LastRecordingFileGrowthAt:MM/dd HH:mm:ss} " +
                $"elapsedSinceStartSec={(int)Math.Max(0, (now - session.RecordingFileGrowthWatchStartedAt).TotalSeconds)} elapsedSinceGrowthSec={(int)Math.Max(0, (now - session.LastRecordingFileGrowthAt).TotalSeconds)} " +
                $"initialGraceSec={RecordingFileGrowthInitialGraceSec} stallSec={RecordingFileGrowthStallSec} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} " +
                $"pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} action=stop_and_fail_after_recovery_exhausted rule=release_contract");

            await StopSessionAsync(session, ReservationStatus.Failed).ConfigureAwait(false);
        }
    }

    private static bool IsStartupZeroByteRecoveryReason(RecordingFileGrowthObservation observation)
        => observation.StopReason is RecordingFileGrowthStopReason.NoDataAfterInitialGrace
            or RecordingFileGrowthStopReason.FileMissingAfterInitialGrace;

    private async Task<bool> TryRecoverStartupZeroByteRecordingAsync(RecordingSession session, Reservation r, RecordingFileGrowthObservation observation, DateTime now, CancellationToken ct)
    {
        var attempt = _recordingStartupZeroByteReloadAttempts.AddOrUpdate(session.ReservationId, 1, (_, current) => current + 1);
        if (attempt > RecordingStartupZeroByteReloadMaxAttempts)
        {
            _log.Add("REC_STARTUP_ZERO_BYTE_RECOVERY", $"R{session.ReservationId}",
                $"result=SKIP reason=attempt_exhausted attempts={attempt - 1} max={RecordingStartupZeroByteReloadMaxAttempts} " +
                $"sourceReason={SafeValue(observation.Reason)} bytes={observation.Bytes} previousBytes={observation.PreviousBytes} " +
                $"pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} rule=release_contract");
            return false;
        }

        if (now >= session.PlannedEndTime.AddSeconds(-RecordingFileGrowthPlannedEndGuardSec))
        {
            _log.Add("REC_STARTUP_ZERO_BYTE_RECOVERY", $"R{session.ReservationId}",
                $"result=SKIP reason=near_planned_end attempt={attempt} sourceReason={SafeValue(observation.Reason)} now={now:MM/dd HH:mm:ss} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} rule=release_contract");
            return false;
        }

        var group = ResolveGroup(r);
        if (string.IsNullOrWhiteSpace(group))
        {
            _log.Add("REC_STARTUP_ZERO_BYTE_RECOVERY", $"R{session.ReservationId}",
                $"result=SKIP reason=group_unresolved attempt={attempt} sourceReason={SafeValue(observation.Reason)} service={SafeValue(r.ServiceName)} sid={r.ServiceId} rule=release_contract");
            return false;
        }

        _log.Add("REC_STARTUP_ZERO_BYTE_RECOVERY", $"R{session.ReservationId}",
            $"result=BEGIN action=reload_worker_and_bondriver attempt={attempt} max={RecordingStartupZeroByteReloadMaxAttempts} " +
            $"sourceReason={SafeValue(observation.Reason)} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} " +
            $"bytes={observation.Bytes} previousBytes={observation.PreviousBytes} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} did={SafeValue(session.Lease.Did)} " +
            $"plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} rule=release_contract");

        session.MarkRecordingFileStallStopRequested();

        var stopped = await TryStopTvAIrEpgRecProcessAsync(session, session.ReservationId, TvAIrEpgRecStopTimeoutMs).ConfigureAwait(false);
        if (!stopped && IsOwnedRecordingWorkerAlive(session))
        {
            _log.Add("REC_STARTUP_ZERO_BYTE_RECOVERY", $"R{session.ReservationId}",
                $"result=FALLBACK action=kill_stuck_worker attempt={attempt} pid={session.ProcessId} reason=stop_signal_timeout rule=release_contract");
            await KillProcessTreeAsync(session.ProcessId, session.ReservationId).ConfigureAwait(false);
        }

        // workerの物理終了確認後は追加の固定待機を入れずleaseを解放する。
        // 先にTunerPoolへ解放時刻を確定し、再起動時の同一スロット再利用保護は
        // worker終了・lease解放・generation更新を再利用可能証拠の単一正本とする。
        try { session.Lease.Dispose(); } catch { }
        try { session.ActivityHandle?.Dispose(); } catch { }
        _log.Add("REC_STARTUP_ZERO_BYTE_RECOVERY", $"R{session.ReservationId}",
            $"result=RELEASED stage=after_old_worker_stop attempt={attempt} tuner={SafeValue(session.Lease.Name)} did={SafeValue(session.Lease.Did)} " +
            $"nextGate=worker_exit_lease_release_generation rule=release_contract");
        TvAirManagedProcessRegistry.Unregister(session.ProcessId);
        lock (_sessionGate)
        {
            _activeSessions.Remove(session.ReservationId);
            _recordingTerminalFailureReasons.Remove(session.ReservationId);
        }
        RemoveChainDirectRecorderSessionScaffold(session.ReservationId, "startup_zero_byte_recovery", "reload_worker_and_bondriver");

        var latest = _store.GetById(session.ReservationId);
        if (latest is null || !latest.IsEnabled || latest.Source == ReservationSource.Epg || now >= latest.EndTime)
        {
            var latestEndText = latest is null ? "-" : latest.EndTime.ToString("MM/dd HH:mm:ss");
            _log.Add("REC_STARTUP_ZERO_BYTE_RECOVERY", $"R{session.ReservationId}",
                $"result=FAIL reason=reservation_not_recordable_after_old_worker_stop attempt={attempt} exists={latest is not null} enabled={latest?.IsEnabled.ToString() ?? "-"} status={latest?.Status.ToString() ?? "-"} now={now:MM/dd HH:mm:ss} end={latestEndText} rule=release_contract");
            if (latest?.Status == ReservationStatus.Recording)
            {
                var failed = _store.TryFinalizeRecordingRuntimeFailure(latest.Id, latest.DataVersion, DateTime.Now, "reservation_not_recordable_after_old_worker_stop");
                _log.Add("REC_STARTUP_ZERO_BYTE_RECOVERY_TERMINAL_CAS", $"R{session.ReservationId}",
                    $"result={(failed.Applied ? "APPLIED" : "REJECTED")} reason={SafeValue(failed.Reason)} dataVersion={failed.PreviousDataVersion}->{failed.CurrentDataVersion} source=reservation_not_recordable_after_old_worker_stop rule=recording_lifecycle_cas_contract");
            }
            _recordingStartupZeroByteReloadAttempts.TryRemove(session.ReservationId, out _);
            return true;
        }

        var restarted = await LaunchNewRecordingAsync(latest, group, ct, chainBoundaryLaunch: false).ConfigureAwait(false);
        _log.Add("REC_STARTUP_ZERO_BYTE_RECOVERY", $"R{session.ReservationId}",
            $"result={(restarted ? "RESTARTED" : "RESTART_FAILED")} action=reload_worker_and_bondriver attempt={attempt} group={SafeValue(group)} " +
            $"service={SafeValue(latest.ServiceName)} title={ReservationDisplayTitle(latest.Title)} oldPid={session.ProcessId} rule=release_contract");

        if (!restarted)
        {
            var failedSnapshot = _store.GetById(latest.Id);
            if (failedSnapshot?.Status == ReservationStatus.Recording)
            {
                var failed = _store.TryFinalizeRecordingRuntimeFailure(failedSnapshot.Id, failedSnapshot.DataVersion, DateTime.Now, "restart_failed");
                _log.Add("REC_STARTUP_ZERO_BYTE_RECOVERY_TERMINAL_CAS", $"R{session.ReservationId}",
                    $"result={(failed.Applied ? "APPLIED" : "REJECTED")} reason={SafeValue(failed.Reason)} dataVersion={failed.PreviousDataVersion}->{failed.CurrentDataVersion} source=restart_failed rule=recording_lifecycle_cas_contract");
            }
            _recordingStartupZeroByteReloadAttempts.TryRemove(session.ReservationId, out _);
        }

        return true;
    }

    private RecordingFileGrowthObservation ObserveRecordingFileGrowth(RecordingSession session, DateTime now)
    {
        var previousBytes = session.LastObservedRecordingFileBytes;
        var bytes = -1L;
        var exists = false;

        try
        {
            if (!string.IsNullOrWhiteSpace(session.RecordingFilePath) && File.Exists(session.RecordingFilePath))
            {
                var fi = new FileInfo(session.RecordingFilePath);
                exists = true;
                bytes = fi.Length;
            }
        }
        catch (Exception ex)
        {
            return RecordingFileGrowthObservation.Stop(RecordingFileGrowthStopReason.FileProbeError, "file_probe_error_" + ex.GetType().Name, bytes, previousBytes);
        }

        if (exists && bytes > previousBytes)
        {
            session.MarkRecordingFileGrowth(now, bytes);
            if (bytes > 0) _recordingStartupZeroByteReloadAttempts.TryRemove(session.ReservationId, out _);
            if (previousBytes <= 0 && bytes > 0)
            {
                _log.Add("REC_FILE_GROWTH_WATCH", $"R{session.ReservationId}",
                    $"result=OBSERVED path={SafeValue(session.RecordingFilePath)} bytes={bytes} previousBytes={previousBytes} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} rule=release_contract");
            }
            return RecordingFileGrowthObservation.Continue(bytes, previousBytes);
        }

        var elapsedSinceStartSec = (now - session.RecordingFileGrowthWatchStartedAt).TotalSeconds;
        var elapsedSinceGrowthSec = (now - session.LastRecordingFileGrowthAt).TotalSeconds;

        if (!exists)
        {
            return elapsedSinceStartSec >= RecordingFileGrowthInitialGraceSec
                ? RecordingFileGrowthObservation.Stop(RecordingFileGrowthStopReason.FileMissingAfterInitialGrace, "recording_file_missing_after_initial_grace", bytes, previousBytes)
                : RecordingFileGrowthObservation.Continue(bytes, previousBytes);
        }

        if (bytes < previousBytes && previousBytes >= 0)
        {
            session.MarkRecordingFileGrowth(now, bytes);
            _log.Add("REC_FILE_GROWTH_WATCH", $"R{session.ReservationId}",
                $"result=RESET reason=file_size_decreased path={SafeValue(session.RecordingFilePath)} bytes={bytes} previousBytes={previousBytes} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} rule=release_contract");
            return RecordingFileGrowthObservation.Continue(bytes, previousBytes);
        }

        if (bytes <= 0)
        {
            return elapsedSinceStartSec >= RecordingFileGrowthInitialGraceSec
                ? RecordingFileGrowthObservation.Stop(RecordingFileGrowthStopReason.NoDataAfterInitialGrace, "no_recording_data_after_initial_grace", bytes, previousBytes)
                : RecordingFileGrowthObservation.Continue(bytes, previousBytes);
        }

        if (session.RecordingFileEverGrew && elapsedSinceGrowthSec >= RecordingFileGrowthStallSec)
            return RecordingFileGrowthObservation.Stop(RecordingFileGrowthStopReason.GrowthStalled, "recording_file_growth_stalled", bytes, previousBytes);

        return RecordingFileGrowthObservation.Continue(bytes, previousBytes);
    }

    private async Task FollowActiveRecordingTimesAsync(DateTime now)
    {
        PruneRecordingTimeFollowEvidence();

        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.Where(x => x.IsRecordingCommitted).ToList();
        if (active.Count == 0) return;

        foreach (var session in active)
        {
            var r = _store.GetById(session.ReservationId);
            if (r is null)
            {
                _log.Add("REC_FOLLOW_CHECK", $"R{session.ReservationId}",
                    $"result=SKIP reason=reservation_missing tuner={SafeValue(session.Lease.Name)} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss}");
                continue;
            }

            if (r.Status == ReservationStatus.Failed && IsOwnedRecordingWorkerAlive(session) && now < session.PlannedEndTime)
            {
                var restore = _store.TryRestoreFailedRecordingRuntime(r.Id, r.DataVersion);
                r = restore.Reservation ?? _store.GetById(session.ReservationId) ?? r;
                _log.Add("REC_STATUS_RECONCILE", $"R{session.ReservationId}",
                    $"result={(restore.Applied ? "RESTORED_TO_RECORDING" : "RESTORE_REJECTED")} reason=active_tvairepgrec_session_is_source_of_truth pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} previousStatus=Failed " +
                    $"casReason={SafeValue(restore.Reason)} dataVersion={restore.PreviousDataVersion}->{restore.CurrentDataVersion} rule=recording_lifecycle_cas_contract");
            }

            if (r.Status != ReservationStatus.Recording)
            {
                _log.Add("REC_FOLLOW_CHECK", $"R{r.Id}",
                    $"result=SKIP reason=status_not_recording status={r.Status} tuner={SafeValue(session.Lease.Name)} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} " + FormatReservationForAudit(r, "follow_skip"));
                continue;
            }

            if (r.ServiceId == 0 || r.EventId == 0)
            {
                _log.Add("REC_FOLLOW_CHECK", $"R{r.Id}",
                    $"result=SKIP reason=missing_service_or_event_id svcId={r.ServiceId} eventId={r.EventId} tuner={SafeValue(session.Lease.Name)} source=active_recording_session_no_extra_tuner");
                continue;
            }

            var projectedEvent = _programEvents.GetByEventKey(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId);
            var ev = ResolveRecordingFollowEvent(r, projectedEvent?.ToEpgEvent(), session);
            if (ev is null)
            {
                _log.Add("REC_FOLLOW_CHECK", $"R{r.Id}",
                    $"result=MISS reason=epg_event_not_found nid={r.NetworkId} tsid={r.TransportStreamId} svcId={r.ServiceId} eventId={r.EventId} tuner={SafeValue(session.Lease.Name)} source=active_recording_session_no_extra_tuner plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss}");
                continue;
            }

            var singleProgramFollowedEnd = ev.End.AddSeconds(session.PostEndMarginSeconds);
            var followedEnd = ResolveChainPlannedEndForRecordingFollow(r, singleProgramFollowedEnd, session.PlannedEndTime);
            var chainPlannedEndPreserved = followedEnd > singleProgramFollowedEnd;
            var epgStartMovesEarlier = ev.Start < r.StartTime.AddSeconds(-30);
            var protectUserRuntimeStart = IsUserRuntimeStartRewindGuardTarget(r, session);
            var suppressStartRewind = epgStartMovesEarlier && protectUserRuntimeStart;
            var effectiveNewStart = suppressStartRewind ? r.StartTime : ev.Start;
            var startChanged = Math.Abs((effectiveNewStart - r.StartTime).TotalSeconds) > 30;
            var hasScheduledDirectChainSuccessor = HasScheduledDirectChainSuccessor(r);
            var chainBoundaryChanged = hasScheduledDirectChainSuccessor
                && Math.Abs((ev.End - r.EndTime).TotalSeconds) > 1;
            // CHAIN_FOLLOW_EXACT_BOUNDARY_INVARIANT:
            // 通常番組の時間追従には30秒の変化検出閾値を維持するが、明示チェーン直接後続の境界は別契約である。
            // 後続開始30秒前の一斉停止、残り10秒の緊急投入、Completed handoff pinが同じStartTimeを参照するため、
            // チェーン境界の変化を30秒未満として捨ててはならない。1秒超の実境界変化を正本へ反映する。
            var endChanged = Math.Abs((ev.End - r.EndTime).TotalSeconds) > 30 || chainBoundaryChanged;
            var plannedChanged = Math.Abs((followedEnd - session.PlannedEndTime).TotalSeconds) > 1;

            _log.Add("REC_FOLLOW_CHECK", $"R{r.Id}",
                $"result=OK source=active_recording_session_no_extra_tuner tuner={SafeValue(session.Lease.Name)} pid={session.ProcessId} " +
                $"event={r.NetworkId}/{r.TransportStreamId}/{r.ServiceId}/{r.EventId} " +
                $"dbStart={r.StartTime:MM/dd HH:mm:ss} epgStart={ev.Start:MM/dd HH:mm:ss} effectiveNewStart={effectiveNewStart:MM/dd HH:mm:ss} dbEnd={r.EndTime:MM/dd HH:mm:ss} epgEnd={ev.End:MM/dd HH:mm:ss} " +
                $"sessionPlannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} singleProgramPlannedEnd={singleProgramFollowedEnd:MM/dd HH:mm:ss} " +
                $"followedPlannedEnd={followedEnd:MM/dd HH:mm:ss} chainPlannedEndPreserved={chainPlannedEndPreserved} startRewindSuppressed={suppressStartRewind} " +
                $"scheduledDirectChainSuccessor={hasScheduledDirectChainSuccessor} chainBoundaryChanged={chainBoundaryChanged} changed={startChanged || endChanged || plannedChanged} " +
                $"rule=release_contract");

            if (suppressStartRewind)
            {
                _log.Add("REC_FOLLOW_START_REWIND_GUARD", $"R{r.Id}",
                    $"result=SUPPRESSED source={r.Source} reason=user_runtime_recording_start_is_source_of_truth oldStart={r.StartTime:MM/dd HH:mm:ss} epgStart={ev.Start:MM/dd HH:mm:ss} keptStart={effectiveNewStart:MM/dd HH:mm:ss} " +
                    $"epgEnd={ev.End:MM/dd HH:mm:ss} plannedEnd={followedEnd:MM/dd HH:mm:ss} tuner={SafeValue(session.Lease.Name)} actualTuner={SafeValue(r.ActualTunerName)} pid={session.ProcessId} userChain={r.IsUserChain} chainPrev=R{(r.UserChainPreviousId.HasValue ? r.UserChainPreviousId.Value.ToString() : "-")} chainRoot=R{(r.UserChainRootId.HasValue ? r.UserChainRootId.Value.ToString() : "-")} " +
                    $"rule=release_contract");
            }

            if (!startChanged && !endChanged && !plannedChanged)
            {
                // 親自身に時刻変化がなくても、後続子のEITだけが更新される場合がある。
                // 同一録画セッションから未開始memberを確認し、追加Tunerなしで追従する。
                FollowScheduledChainMembersFromActiveSession(r, session);
                continue;
            }

            if (startChanged || endChanged)
            {
                var directSuccessors = GetScheduledDirectChainSuccessors(r);
                if (directSuccessors.Count > 1)
                {
                    // CHAIN_FOLLOW_DIRECT_SUCCESSOR_UNIQUENESS_INVARIANT:
                    // 直接後続が複数ある壊れたチェーンを先頭1件だけ更新してはならない。
                    // 境界を推測せず前段だけ更新し、チェーン構造不整合を監査ログへ明示する。
                    var predecessorOnlyUpdated = _store.TryUpdateRecordingFollowTimeCas(
                        r.Id, r.DataVersion, effectiveNewStart, ev.End);
                    _log.Add("REC_FOLLOW_CHAIN_BOUNDARY", $"R{r.Id}",
                        $"result={(predecessorOnlyUpdated ? "REJECTED_SUCCESSOR_STRUCTURE_PREDECESSOR_UPDATED_CAS" : "REJECTED_PREDECESSOR_CAS")} reason=multiple_direct_successors successorIds={string.Join(',', directSuccessors.Select(x => $"R{x.Id}"))} " +
                        $"followedBoundary={ev.End:MM/dd HH:mm:ss} action={(predecessorOnlyUpdated ? "keep_successor_times" : "abort_stale_follow_snapshot")} rule=chain_follow_boundary_contract");
                    if (!predecessorOnlyUpdated)
                        continue;
                }
                else if (directSuccessors.Count == 1 && ev.End < directSuccessors[0].EndTime)
                {
                    var successor = directSuccessors[0];
                    var oldSuccessorStart = successor.StartTime;
                    var atomicUpdated = _store.UpdateRecordingFollowChainBoundaryAtomic(
                        r.Id,
                        r.DataVersion,
                        effectiveNewStart,
                        ev.End,
                        successor.Id,
                        successor.DataVersion,
                        ev.End);
                    _log.Add("REC_FOLLOW_CHAIN_BOUNDARY", $"R{r.Id}",
                        $"result={(atomicUpdated ? "UPDATED_ATOMIC" : "REJECTED_ATOMIC_CAS")} successor=R{successor.Id} oldSuccessorStart={oldSuccessorStart:MM/dd HH:mm:ss} " +
                        $"newSuccessorStart={ev.End:MM/dd HH:mm:ss} successorEnd={successor.EndTime:MM/dd HH:mm:ss} " +
                        $"source=active_recording_time_follow directSuccessorOnly=True descendantsChanged=False atomicBoundary=True " +
                        $"rule=chain_follow_boundary_contract");
                    if (!atomicUpdated)
                        continue;
                }
                else
                {
                    var predecessorOnlyUpdated = _store.TryUpdateRecordingFollowTimeCas(
                        r.Id, r.DataVersion, effectiveNewStart, ev.End);
                    if (!predecessorOnlyUpdated)
                    {
                        _log.Add("REC_FOLLOW_CHAIN_BOUNDARY", $"R{r.Id}",
                            $"result=REJECTED_PREDECESSOR_CAS reason=stale_recording_follow_snapshot followedBoundary={ev.End:MM/dd HH:mm:ss} " +
                            $"action=abort_before_session_and_allocation_update rule=chain_follow_boundary_contract");
                        continue;
                    }
                    if (directSuccessors.Count == 1)
                    {
                        var successor = directSuccessors[0];
                        _log.Add("REC_FOLLOW_CHAIN_BOUNDARY", $"R{r.Id}",
                            $"result=REJECTED successor=R{successor.Id} reason=followed_boundary_not_before_successor_end " +
                            $"oldSuccessorStart={successor.StartTime:MM/dd HH:mm:ss} followedBoundary={ev.End:MM/dd HH:mm:ss} successorEnd={successor.EndTime:MM/dd HH:mm:ss} " +
                            $"action=keep_existing_successor_time predecessorUpdatedCas=True rule=chain_follow_boundary_contract");
                    }
                }
            }

            session.UpdatePlannedEndTime(followedEnd);
            session.Lease.UpdatePlannedEndTime(followedEnd, $"RecordingFollow R{r.Id}");

            _log.Add("REC_FOLLOW_UPDATE", $"R{r.Id}",
                $"source=active_recording_session_no_extra_tuner route=ALLOC_ROUTE oldStart={r.StartTime:MM/dd HH:mm:ss} newStart={effectiveNewStart:MM/dd HH:mm:ss} epgStart={ev.Start:MM/dd HH:mm:ss} startRewindSuppressed={suppressStartRewind} " +
                $"oldEnd={r.EndTime:MM/dd HH:mm:ss} newEnd={ev.End:MM/dd HH:mm:ss} newPlannedEnd={followedEnd:MM/dd HH:mm:ss} " +
                $"tuner={SafeValue(session.Lease.Name)} pid={session.ProcessId} now={now:MM/dd HH:mm:ss} rule=release_contract");

            try
            {
                _allocationRoute.Run(new ReservationAllocationRouteRequest(
                    Source: "RecordingFollow",
                    Action: $"UpdateStopAt:R{r.Id}",
                    RunKeywordMatcher: false,
                    SyncProgramRuleReservations: false,
                    ReevaluateAllocations: true,
                    RefreshPreRecordEpgEntries: false,
                    RefreshWakeTask: false,
                    BypassStopPhaseGate: false,
                    EmitConflictLogs: true,
                    ConflictLogCategory: "REC_FOLLOW_ALLOC",
                    ConflictLogTitle: $"R{r.Id}",
                    WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));
            }
            catch (Exception ex)
            {
                _log.Add("REC_FOLLOW_UPDATE", $"R{r.Id}", $"alloc_route_error={TrimForLog(ex.Message, 240)}");
            }

            // 前番組と直接境界のCASを収束させた後、最新snapshotで未開始memberを追従する。
            FollowScheduledChainMembersFromActiveSession(_store.GetById(r.Id) ?? r, session);

            if (now >= followedEnd)
            {
                _log.Add("REC_FOLLOW_END_DETECTED", $"R{r.Id}",
                    $"reason=followed_event_already_ended now={now:MM/dd HH:mm:ss} epgEnd={ev.End:MM/dd HH:mm:ss} plannedEnd={followedEnd:MM/dd HH:mm:ss} tuner={SafeValue(session.Lease.Name)} route=CheckSessionEndsAsync_next");
            }
        }

        await Task.CompletedTask;
    }


    private void RememberPreRecordTimeFollowEvidence(
        IReadOnlyList<Reservation> targets,
        IReadOnlyList<EpgEvent> observedEvents)
    {
        if (targets.Count == 0 || observedEvents.Count == 0)
            return;

        var byIdentity = observedEvents
            .GroupBy(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.UpdatedAt).First());

        foreach (var target in targets)
        {
            if (!byIdentity.TryGetValue((target.NetworkId, target.TransportStreamId, target.ServiceId, target.EventId), out var observed))
                continue;
            if (observed.End <= observed.Start)
                continue;

            _recordingTimeFollowEvidence[target.Id] = new RecordingTimeFollowEvidence(
                observed.NetworkId,
                observed.TransportStreamId,
                observed.ServiceId,
                observed.EventId,
                observed.Start,
                observed.End,
                observed.UpdatedAt == default ? DateTime.Now : observed.UpdatedAt,
                string.Empty);
        }
    }

    private EpgEvent? ResolveRecordingFollowEvent(Reservation reservation, EpgEvent? projectedEvent, RecordingSession session)
    {
        var hasMatchingEvidence = _recordingTimeFollowEvidence.TryGetValue(reservation.Id, out var evidence)
            && evidence.NetworkId == reservation.NetworkId
            && evidence.TransportStreamId == reservation.TransportStreamId
            && evidence.ServiceId == reservation.ServiceId
            && evidence.EventId == reservation.EventId;

        if (!hasMatchingEvidence)
        {
            // RECORDING_TIME_FOLLOW_RESTART_REHYDRATE_INVARIANT:
            // PreRecで追従した予約時刻はDBへ永続化される一方、実観測evidenceはprocess-localである。
            // TvAIr再起動を挟んだ場合でも、録画開始時点の予約正本より古いProgramGuide投影へ戻してはならない。
            // 同一EventIdentityの投影が予約より後退している場合だけ、現在の予約時刻を再起動後の最低境界としてevidenceへ再構成する。
            // 投影が同等または新しい場合は何も作らず、通常の録画中Follow（短縮を含む）をそのまま許可する。
            var projectionBehindReservation = projectedEvent is not null
                && (projectedEvent.Start < reservation.StartTime || projectedEvent.End < reservation.EndTime);
            if (!projectionBehindReservation)
                return projectedEvent;

            evidence = new RecordingTimeFollowEvidence(
                reservation.NetworkId,
                reservation.TransportStreamId,
                reservation.ServiceId,
                reservation.EventId,
                reservation.StartTime,
                reservation.EndTime,
                DateTime.Now,
                string.Empty);
            _recordingTimeFollowEvidence[reservation.Id] = evidence;

            _log.Add("REC_FOLLOW_EVIDENCE_REHYDRATE", $"R{reservation.Id}",
                $"result=REHYDRATED source=reservation_runtime_baseline reason=projection_behind_persisted_reservation " +
                $"reservationStart={reservation.StartTime:MM/dd HH:mm:ss} reservationEnd={reservation.EndTime:MM/dd HH:mm:ss} " +
                $"projectedStart={projectedEvent!.Start:MM/dd HH:mm:ss} projectedEnd={projectedEvent.End:MM/dd HH:mm:ss} " +
                $"tuner={SafeValue(session.Lease.Name)} pid={session.ProcessId} action=restore_no_stale_rollback_boundary_after_restart " +
                $"rule=recording_time_follow_no_stale_rollback_contract");
        }

        // TryGetValue成功時、または上のrehydrate分岐で新規作成済みのため、ここでは必ずevidenceが存在する。
        // null-forgivingは実行時挙動を変えず、制御フロー上の不変条件をnullable解析へ明示するだけ。
        var resolvedEvidence = evidence!;

        if (projectedEvent is null)
        {
            return new EpgEvent
            {
                NetworkId = resolvedEvidence.NetworkId,
                TransportStreamId = resolvedEvidence.TransportStreamId,
                ServiceId = resolvedEvidence.ServiceId,
                EventId = resolvedEvidence.EventId,
                Start = resolvedEvidence.Start,
                End = resolvedEvidence.End,
                DurationSeconds = Math.Max(0, (int)(resolvedEvidence.End - resolvedEvidence.Start).TotalSeconds),
                UpdatedAt = resolvedEvidence.ObservedAt
            };
        }

        // RECORDING_TIME_FOLLOW_NO_STALE_ROLLBACK_INVARIANT:
        // PreRecの実観測値より前へ戻す投影は録画欠落を生むため採用しない。
        // 後ろへの開始移動・終了延長は安全側の新しい追従として採用し、その最大境界を証拠へ昇格する。
        var resolvedStart = projectedEvent.Start < resolvedEvidence.Start ? resolvedEvidence.Start : projectedEvent.Start;
        var resolvedEnd = projectedEvent.End < resolvedEvidence.End ? resolvedEvidence.End : projectedEvent.End;
        if (resolvedEnd <= resolvedStart)
            resolvedEnd = resolvedEvidence.End;

        var rollbackSuppressed = projectedEvent.Start < resolvedEvidence.Start || projectedEvent.End < resolvedEvidence.End;
        var suppressionKey = $"{projectedEvent.Start.Ticks}:{projectedEvent.End.Ticks}";
        var evidenceChanged = false;
        if (rollbackSuppressed && !string.Equals(resolvedEvidence.LastSuppressedProjectionKey, suppressionKey, StringComparison.Ordinal))
        {
            _log.Add("REC_FOLLOW_STALE_ROLLBACK_GUARD", $"R{reservation.Id}",
                $"result=SUPPRESSED source=program_guide_projection_vs_follow_evidence evidenceStart={resolvedEvidence.Start:MM/dd HH:mm:ss} evidenceEnd={resolvedEvidence.End:MM/dd HH:mm:ss} " +
                $"projectedStart={projectedEvent.Start:MM/dd HH:mm:ss} projectedEnd={projectedEvent.End:MM/dd HH:mm:ss} resolvedStart={resolvedStart:MM/dd HH:mm:ss} resolvedEnd={resolvedEnd:MM/dd HH:mm:ss} " +
                $"tuner={SafeValue(session.Lease.Name)} pid={session.ProcessId} action=preserve_latest_observed_boundary rule=recording_time_follow_no_stale_rollback_contract");
            resolvedEvidence = resolvedEvidence with { LastSuppressedProjectionKey = suppressionKey };
            evidenceChanged = true;
        }

        if (resolvedStart > resolvedEvidence.Start || resolvedEnd > resolvedEvidence.End)
        {
            resolvedEvidence = resolvedEvidence with
            {
                Start = resolvedStart,
                End = resolvedEnd,
                ObservedAt = projectedEvent.UpdatedAt == default ? DateTime.Now : projectedEvent.UpdatedAt,
                LastSuppressedProjectionKey = string.Empty
            };
            evidenceChanged = true;
        }

        if (evidenceChanged)
            _recordingTimeFollowEvidence[reservation.Id] = resolvedEvidence;

        return new EpgEvent
        {
            NetworkId = projectedEvent.NetworkId,
            TransportStreamId = projectedEvent.TransportStreamId,
            ServiceId = projectedEvent.ServiceId,
            EventId = projectedEvent.EventId,
            ServiceName = projectedEvent.ServiceName,
            Title = projectedEvent.Title,
            Description = projectedEvent.Description,
            Genre = projectedEvent.Genre,
            GenreCodes = projectedEvent.GenreCodes,
            TableId = projectedEvent.TableId,
            SectionNumber = projectedEvent.SectionNumber,
            VersionNumber = projectedEvent.VersionNumber,
            RawDescriptorLoopHex = projectedEvent.RawDescriptorLoopHex,
            RawShortEventDescriptorHex = projectedEvent.RawShortEventDescriptorHex,
            RawExtendedEventDescriptorHex = projectedEvent.RawExtendedEventDescriptorHex,
            RawContentDescriptorHex = projectedEvent.RawContentDescriptorHex,
            DurationSeconds = Math.Max(0, (int)(resolvedEnd - resolvedStart).TotalSeconds),
            Start = resolvedStart,
            End = resolvedEnd,
            UpdatedAt = projectedEvent.UpdatedAt
        };
    }

    private void PruneRecordingTimeFollowEvidence()
    {
        foreach (var pair in _recordingTimeFollowEvidence)
        {
            var reservation = _store.GetById(pair.Key);
            if (reservation is null || reservation.Status is ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed)
                _recordingTimeFollowEvidence.TryRemove(pair.Key, out _);
        }
    }


    private void FollowScheduledChainMembersFromActiveSession(Reservation activeReservation, RecordingSession session)
    {
        if (!activeReservation.IsUserChain)
            return;

        var rootId = activeReservation.UserChainRootId ?? activeReservation.Id;
        var targets = _store.GetAll()
            .Where(x => x.IsUserChain
                && x.Id != activeReservation.Id
                && x.UserChainRootId == rootId
                && x.Status == ReservationStatus.Scheduled
                && x.IsEnabled
                && IsSameServiceIdentity(activeReservation, x))
            .OrderBy(x => x.StartTime)
            .ThenBy(x => x.Id)
            .ToList();
        if (targets.Count == 0)
            return;

        // ACTIVE_CHAIN_SESSION_TIME_FOLLOW_INVARIANT:
        // 未開始memberもPreRecで実観測した時刻証拠より古いProgramGuide投影へ巻き戻さない。
        // 後ろへの延長は受け入れるが、追加worker、再選局、追加Tuner leaseは禁止する。
        var resolvedEvents = new List<EpgEvent>(targets.Count);
        foreach (var target in targets)
        {
            var projected = _programEvents.GetByEventKey(target.NetworkId, target.TransportStreamId, target.ServiceId, target.EventId)?.ToEpgEvent();
            var resolved = ResolveRecordingFollowEvent(target, projected, session);
            if (resolved is not null)
                resolvedEvents.Add(resolved);
        }

        var results = _store.ApplyTimeFollowingDetailedFromObservedEvents(
            targets,
            resolvedEvents,
            allowUserChainSeriesFollow: true);
        var updated = results.Where(x => x.Updated).ToList();

        _log.Add("REC_CHAIN_MEMBER_FOLLOW", $"R{activeReservation.Id}",
            $"result={(updated.Count > 0 ? "UPDATED" : "NO_CHANGE")} root=R{rootId} source=active_recording_session_no_extra_tuner tuner={SafeValue(session.Lease.Name)} pid={session.ProcessId} checked={results.Count} updated={updated.Count} targets=[{string.Join(',', targets.Select(x => $"R{x.Id}"))}] updatedIds=[{string.Join(',', updated.Select(x => $"R{x.ReservationId}"))}] childWorkers=0 extraTunerLease=0 rule=chain_time_follow_single_session_contract");

        if (updated.Count == 0)
            return;

        _allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "RecordingFollow",
            Action: $"Reevaluate:ChainMembers:R{activeReservation.Id}",
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: false,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: false,
            RefreshWakeTask: false,
            BypassStopPhaseGate: false,
            EmitConflictLogs: true,
            ConflictLogCategory: "REC_CHAIN_FOLLOW_ALLOC",
            ConflictLogTitle: $"R{activeReservation.Id}"));
    }

    private IReadOnlyList<Reservation> GetScheduledDirectChainSuccessors(Reservation predecessor)
    {
        return _store.GetAll()
            .Where(x => x.IsUserChain
                && x.UserChainPreviousId == predecessor.Id
                && x.IsEnabled
                && x.Status == ReservationStatus.Scheduled
                && IsSameServiceIdentity(predecessor, x))
            .OrderBy(x => x.StartTime)
            .ThenBy(x => x.Id)
            .ToList();
    }

    private bool HasScheduledDirectChainSuccessor(Reservation predecessor)
        => GetScheduledDirectChainSuccessors(predecessor).Count == 1;

    // ─── 録画終了チェック ─────────────────────────────────────────

    private async Task CheckSessionEndsAsync(DateTime now)
    {
        List<RecordingSession> toEnd;
        lock (_sessionGate)
        {
            toEnd = _activeSessions.Values
                .Where(s => s.IsRecordingCommitted && now >= s.PlannedEndTime)
                .ToList();
        }

        foreach (var session in toEnd)
        {
            if (TryGetPendingChainSuccessorId(session.ReservationId, out var successorId))
            {
                lock (_sessionGate)
                {
                    if (_chainBoundaryNormalStopSuppressed.Contains(session.ReservationId))
                    {
                        _log.Add("CHAIN_HANDOFF_GUARD", $"R{session.ReservationId}",
                            $"stage=planned_end_stop_guard result=NORMAL_STOP_STILL_SUPPRESSED predecessor=R{session.ReservationId} successor=R{successorId} " +
                            $"actualTuner={SafeValue(session.Lease.Name)} did={SafeValue(session.Lease.Did)} pid={session.ProcessId} " +
                            $"stopSignalSuppressed=True leaseReleaseSuppressed=True reason=boundary_handoff_waiting_for_bridge_file_switch rule=release_contract");
                        continue;
                    }
                }
            }
            await StopSessionAsync(session, ReservationStatus.Completed);
        }
    }

    private bool TryGetPendingChainSuccessorId(int predecessorReservationId, out int successorId)
    {
        successorId = 0;
        try
        {
            var predecessors = _store.GetChainPredecessors();
            foreach (var kv in predecessors)
            {
                if (kv.Value != predecessorReservationId) continue;
                var successor = _store.GetById(kv.Key);
                if (successor is null || !successor.IsEnabled || successor.Status != ReservationStatus.Scheduled) continue;
                successorId = kv.Key;
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", $"R{predecessorReservationId}", $"チェーン後続確認エラー: {ex.Message}");
        }
        return false;
    }

    private async Task StopSessionAsync(
        RecordingSession session,
        ReservationStatus finalStatus,
        bool suppressNormalStopForBoundary = false,
        bool processAlreadyGone = false,
        bool suppressPostStopMaintenance = false,
        DateTime? expectedCompletionEnd = null,
        Func<Task>? afterPhysicalRelease = null,
        bool preserveRecordingLifecycleForRecovery = false)
    {
        using var eventScope = _typedEvents.BeginOutboxScope(out var commitEvents);
        if (!session.TryBeginFinalization())
        {
            _log.Add("REC_FINALIZE_DUPLICATE", $"R{session.ReservationId}",
                $"result=SKIP state={session.FinalizationState} operationId={session.OperationId} pid={session.ProcessId} rule=release_contract");
            return;
        }

        var finalizationCompleted = false;
        Exception? stopFailure = null;
        long? stoppingDataVersion = null;
        var lifecycleClaimed = false;
        var physicalStopStarted = false;
        var stopGateRequestAt = DateTime.Now;
        var stopGateKey = !string.IsNullOrWhiteSpace(session.Lease.Name) ? session.Lease.Name : $"DID:{session.Lease.Did}";
        var stopGate = _stopGatesByTuner.GetOrAdd(stopGateKey, static _ => new SemaphoreSlim(1, 1));
        _log.Add("CHAIN_TRACE", $"R{session.ReservationId}", $"[CHAIN] stage=stop_gate_wait_start finalStatus={finalStatus} operationId={session.OperationId} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} gateKey={stopGateKey} activeSessions={FormatActiveSessionsForLog()}");
        await stopGate.WaitAsync();
        var stopGateWaitMs = (int)(DateTime.Now - stopGateRequestAt).TotalMilliseconds;
        _log.Add("CHAIN_TRACE", $"R{session.ReservationId}", $"[CHAIN] stage=stop_gate_entered waitMs={stopGateWaitMs} finalStatus={finalStatus} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} activeSessions={FormatActiveSessionsForLog()}");
        try
        {
            var pid = session.ProcessId;
            var rid = session.ReservationId;
            var reservationForEvidence = _store.GetById(rid);
            string? terminalFailureReason;
            lock (_sessionGate) _recordingTerminalFailureReasons.TryGetValue(rid, out terminalFailureReason);

            // 正常完了は予約時刻を過ぎたという事実だけでは成立させない。
            // スリープ・休止・長時間停止をまたいだ場合、workerが生存したまま終了処理だけ後刻に走り、
            // 実TSが開始直後で止まっていてもCompletedになり得る。録画ファイルの最終成長時刻と
            // 最低限のファイル量を予約尺に照合し、明白な部分録画は共通Failed終端へ落とす。
            if (finalStatus == ReservationStatus.Completed && reservationForEvidence is not null)
            {
                var sessionCompletionEvidence = EvaluateCompletedRecordingSessionEvidence(session, reservationForEvidence, expectedCompletionEnd);
                if (!sessionCompletionEvidence.LikelyCompleted)
                {
                    finalStatus = ReservationStatus.Failed;
                    terminalFailureReason = sessionCompletionEvidence.Reason;
                    lock (_sessionGate)
                        _recordingTerminalFailureReasons[rid] = sessionCompletionEvidence.Reason;
                    _log.Add("REC_COMPLETION_EVIDENCE", $"R{rid}",
                        $"result=FAILED reason={SafeValue(sessionCompletionEvidence.Reason)} service={SafeValue(reservationForEvidence.ServiceName)} " +
                        $"title={ReservationDisplayTitle(reservationForEvidence.Title)} evidence={SafeValue(sessionCompletionEvidence.Summary)} " +
                        "action=downgrade_completed_to_failed rule=recording_completion_evidence_contract");
                }
                else
                {
                    _log.Add("REC_COMPLETION_EVIDENCE", $"R{rid}",
                        $"result=OK service={SafeValue(reservationForEvidence.ServiceName)} title={ReservationDisplayTitle(reservationForEvidence.Title)} " +
                        $"evidence={SafeValue(sessionCompletionEvidence.Summary)} rule=recording_completion_evidence_contract");
                }
            }

            var terminalFailureReasonPart = string.IsNullOrWhiteSpace(terminalFailureReason) ? "terminalFailureReason=-" : $"terminalFailureReason={SafeValue(terminalFailureReason)}";
            var terminationEvidence = new RecordingTerminationEvidence
            {
                RecordingRecoveryChainId = reservationForEvidence?.RecordingRecoveryChainId ?? string.Empty,
                PowerResumeCycleId = _powerResumeCycleContext.Value ?? string.Empty,
                TerminationReason = string.IsNullOrWhiteSpace(terminalFailureReason)
                    ? (finalStatus == ReservationStatus.Completed ? "completed" : "recording_failed")
                    : terminalFailureReason!,
                WorkerProcessId = pid,
                WorkerIdentityResult = processAlreadyGone ? "process_missing" : "owned_process",
                LastFileGrowthAt = session.LastRecordingFileGrowthAt,
                LastObservedFileSize = session.LastObservedRecordingFileBytes,
                StopRequestedAt = DateTime.Now
            };
            var hasPendingChainSuccessor = TryGetPendingChainSuccessorId(rid, out var pendingChainSuccessorId);
            var isChainBoundaryHandoff = finalStatus == ReservationStatus.Completed && hasPendingChainSuccessor;
            if (isChainBoundaryHandoff)
            {
                _log.Add("CHAIN_HANDOFF_GUARD", $"R{rid}",
                    $"stage=stop_route_guard result=BOUNDARY_HANDOFF_DETECTED predecessor=R{rid} successor=R{pendingChainSuccessorId} " +
                    $"suppressNormalStopForBoundary={suppressNormalStopForBoundary} protectedObservation=True recordingMode=stop_restart separateTsFile=True " +
                    $"normalManualStopRouteUnchanged=True rule=release_contract");

                if (suppressNormalStopForBoundary)
                {
                    lock (_sessionGate) _chainBoundaryNormalStopSuppressed.Add(rid);
                    var boundaryStopReservation = _store.GetById(rid);
                    var boundaryGroup = boundaryStopReservation is not null ? ResolveGroup(boundaryStopReservation) : null;
                    _log.Add("REC_STOP_COMMON_ENTER", $"R{rid}",
                        $"mode=SuppressedByChainBoundaryHandoff group={SafeValue(boundaryGroup)} pid={pid} route=TvAIrEpgRecOnly " +
                        $"recorderStopTarget=False chainHandoff=True boundaryHandoff=True stopSignalSuppressed=True leaseReleaseSuppressed=True " +
                        $"successor=R{pendingChainSuccessorId} rule=release_contract");
                    _log.Add("CHAIN_HANDOFF_GUARD", $"R{rid}",
                        $"stage=stop_route_guard result=NORMAL_STOP_SUPPRESSED predecessor=R{rid} successor=R{pendingChainSuccessorId} " +
                        $"actualTuner={SafeValue(session.Lease.Name)} did={SafeValue(session.Lease.Did)} pid={pid} " +
                        $"stopSignalSuppressed=True leaseReleaseSuppressed=True statusUpdateSuppressed=True activeSessionKept=True " +
                        $"reason=stop_restart_route_never_suppresses_normal_boundary_stop rule=release_contract");
                    return;
                }
            }
            var lifecycleReservation = _store.GetById(rid);
            if (preserveRecordingLifecycleForRecovery)
            {
                _log.Add("REC_STOP_DECISION", $"R{rid}",
                    $"result=PRESERVE_RECORDING_FOR_RECOVERY status={SafeValue(lifecycleReservation?.Status.ToString())} dataVersion={lifecycleReservation?.DataVersion.ToString() ?? "-"} finalStatus={finalStatus} operationId={session.OperationId} " +
                    $"action=physical_owner_cleanup_then_atomic_recovery rule=interrupted_recording_recovery_contract");
            }
            else if (lifecycleReservation is not null)
            {
                if (lifecycleReservation.Status == ReservationStatus.Recording)
                {
                    var stopClaim = _store.TryBeginRecordingStop(rid, lifecycleReservation.DataVersion);
                    if (stopClaim.Applied)
                    {
                        lifecycleClaimed = true;
                        stoppingDataVersion = stopClaim.CurrentDataVersion;
                        _log.Add("REC_STOP_DECISION", $"R{rid}",
                            $"result=CLAIMED status=Stopping dataVersion={stopClaim.PreviousDataVersion}->{stopClaim.CurrentDataVersion} finalStatus={finalStatus} operationId={session.OperationId} rule=release_contract");
                    }
                    else
                    {
                        _log.Add("REC_STOP_DECISION", $"R{rid}",
                            $"result=CLAIM_REJECTED reason={SafeValue(stopClaim.Reason)} expectedStatus=Recording actualStatus={SafeValue(stopClaim.CurrentStatus?.ToString())} expectedVersion={lifecycleReservation.DataVersion} actualVersion={stopClaim.CurrentDataVersion} finalStatus={finalStatus} operationId={session.OperationId} action=continue_physical_owner_cleanup_without_db_overwrite rule=release_contract");
                    }
                }
                else if (lifecycleReservation.Status == ReservationStatus.Stopping)
                {
                    lifecycleClaimed = true;
                    stoppingDataVersion = lifecycleReservation.DataVersion;
                    _log.Add("REC_STOP_DECISION", $"R{rid}",
                        $"result=ALREADY_CLAIMED status=Stopping dataVersion={lifecycleReservation.DataVersion} finalStatus={finalStatus} operationId={session.OperationId} rule=release_contract");
                }
                else
                {
                    _log.Add("REC_STOP_DECISION", $"R{rid}",
                        $"result=NOT_CLAIMABLE status={lifecycleReservation.Status} dataVersion={lifecycleReservation.DataVersion} finalStatus={finalStatus} operationId={session.OperationId} action=continue_physical_owner_cleanup_without_db_overwrite rule=release_contract");
                }
            }
            else
            {
                _log.Add("REC_STOP_DECISION", $"R{rid}",
                    $"result=RESERVATION_MISSING finalStatus={finalStatus} operationId={session.OperationId} action=continue_physical_owner_cleanup_without_db_overwrite rule=release_contract");
            }

            if (hasPendingChainSuccessor)
            {
                _log.Add("CHAIN_HANDOFF_RELEASE_POLICY", $"R{rid}",
                    $"successor=R{pendingChainSuccessorId} fixedWaitMs=0 evidence=worker_exit+lease_generation reason=tvair_epgrec_process_owns_bondriver rule=recording_physical_release_contract");
            }

            LogRecordingStopChainContext(session, finalStatus, "before_kill");
            _log.Add("CHAIN_TRACE", $"R{rid}", $"[CHAIN] stage=stop_before_kill successor={(hasPendingChainSuccessor ? $"R{pendingChainSuccessorId}" : "-")} finalStatus={finalStatus} pid={pid} lease={session.Lease.Name} fixedWaitMs=0 status={_tunerPool.GetStatusSummary()}");
            TvTestProcessAuditor.EmitViewingProtectionAudit(
                _log,
                "REC_STOP_BEFORE_KILL",
                $"R{rid}",
                session.Lease.Did,
                session.Lease.BonDriverFileName,
                _tunerPool.GetProtectedViewingDids(),
                blockOnSameDid: false);
            _log.Add("PROCESS_OWNERSHIP", $"R{rid}", $"stopTargetPid={pid} ownedByReservation=True ownershipScope=tvair_managed_pid_only recFile={session.RecordingFilePath}");
            _log.Add("Scheduler", $"R{rid}", $"録画停止開始: PID={pid} finalStatus={finalStatus} stopGate=entered recFile={session.RecordingFilePath}");

            using var stopPhase = StopPhaseGate.Enter($"R{rid}", session.Lease.Name,
                msg => _log.Add("Scheduler", $"R{rid}", msg));

            var stopReservation = _store.GetById(rid);
            var stopGroup = stopReservation is not null ? ResolveGroup(stopReservation) : null;
            var recorderStopTarget = stopReservation is not null && pid > 0;
            var stopMode = recorderStopTarget ? "TvAIrEpgRecStopSignalThenWait" : "SinglePidExitOnly";
            _log.Add("REC_STOP_COMMON_ENTER", $"R{rid}",
                $"mode={stopMode} group={SafeValue(stopGroup)} pid={pid} route={("TvAIrEpgRecOnly")} recorderStopTarget={recorderStopTarget} chainHandoff={hasPendingChainSuccessor} boundaryHandoff={isChainBoundaryHandoff} rule=release_contract");
            _log.Add("REC_STOP_MODE", $"R{rid}",
                $"mode={stopMode} reason=release_contract group={SafeValue(stopGroup)} pid={pid} stopTarget={recorderStopTarget} legacyTvTestRoute=False chainHandoff={hasPendingChainSuccessor} pt3LockDuringStopWait=False timeoutMs={TvAIrEpgRecStopTimeoutMs}");

            // TvAIrEpgRecへstop signalを送り、既にworker消失を確認した経路では停止要求とKillを重ねない。
            physicalStopStarted = true;
            var recorderStopSucceeded = processAlreadyGone
                || await TryStopTvAIrEpgRecProcessAsync(session, rid, TvAIrEpgRecStopTimeoutMs).ConfigureAwait(false);
            var processExitedWithoutKill = recorderStopSucceeded;
            terminationEvidence.StopRequestResult = processAlreadyGone
                ? "process_already_gone"
                : (recorderStopSucceeded ? "graceful_stop_succeeded" : "graceful_stop_failed");

            if (!processExitedWithoutKill)
            {
                // release_contract: Bridge RecordStop が失敗・無応答、または通常終了待ちで残存した場合だけ、
                // TvAIr所有PID単体終了へフォールバックする。成功時は Kill を避ける。
                _log.Add("REC_STOP_SINGLE_PID_EXIT", $"R{rid}",
                    $"stage=tuner_device_stop_lock_skipped_for_single_pid_fallback pid={pid} lease={session.Lease.Name} reason=do_not_expand_internal_tuner_device_lock_on_timeout_kill status={_tunerPool.GetStatusSummary()} rule=release_contract");
                _log.Add("REC_STOP_SINGLE_PID_EXIT", $"R{rid}",
                    $"stage=before_single_pid_exit pid={pid} route={("TvAIrEpgRecOnly")} stopSignalTried={recorderStopTarget} stopSignalSucceeded={recorderStopSucceeded} gracefulExitSucceeded=False treeKillForbidden=True fallback=True");
                await KillProcessTreeAsync(pid, rid);

                // STOP_TIMEOUT_FALLBACK_DIAGNOSTIC:
                // TIMEOUT時はlease解放前に対象workerの生存とidentityを観測する。
                // この観測結果は停止・lease解放・後続開始の分岐条件には使用しない。
                var fallbackWorkerAlive = IsOwnedRecordingWorkerAlive(session);
                var fallbackObservedIdentity = TvAirManagedProcessRegistry.CaptureIdentity(pid);
                var fallbackIdentityMatches = session.WorkerIdentity.IsAvailable
                    && fallbackObservedIdentity.IsAvailable
                    && TvAirManagedProcessRegistry.IdentityMatches(session.WorkerIdentity, fallbackObservedIdentity);
                _log.Add("REC_STOP_SINGLE_PID_EXIT_DIAGNOSTIC", $"R{rid}",
                    $"stage=after_single_pid_exit_before_lease_release pid={pid} ownedWorkerAlive={fallbackWorkerAlive} " +
                    $"originalIdentityAvailable={session.WorkerIdentity.IsAvailable} observedIdentityAvailable={fallbackObservedIdentity.IsAvailable} " +
                    $"identityMatches={fallbackIdentityMatches} observedStartUtc={fallbackObservedIdentity.ProcessStartTimeUtc?.ToString("O") ?? "-"} " +
                    $"observedPath={SafeValue(fallbackObservedIdentity.ExecutablePath)} leaseReleaseBehavior=unchanged diagnosticOnly=True rule=release_contract");

                terminationEvidence.StopRequestResult = "single_pid_fallback_completed";
                _log.Add("REC_STOP_SINGLE_PID_EXIT", $"R{rid}",
                    $"stage=after_single_pid_exit pid={pid} route={("TvAIrEpgRecOnly")} stopSignalTried={recorderStopTarget} stopSignalSucceeded={recorderStopSucceeded} gracefulExitSucceeded=False treeKillForbidden=True fallback=True");
                _log.Add("CHAIN_TRACE", $"R{rid}", $"[CHAIN] stage=single_pid_exit_before_device_wait pid={pid} lease={session.Lease.Name} status={_tunerPool.GetStatusSummary()}");
            }
            else
            {
                _log.Add("REC_STOP_SINGLE_PID_EXIT", $"R{rid}",
                    $"stage=skipped pid={pid} route={("TvAIrEpgRecOnly")} recorderStopSucceeded=True gracefulExitSucceeded=True reason={(processAlreadyGone ? "process_already_gone" : "no_kill_after_recordstop_ok")}");
                _log.Add("CHAIN_TRACE", $"R{rid}", $"[CHAIN] stage=pt3_stop_lock_skipped_process_already_exited pid={pid} lease={session.Lease.Name} status={_tunerPool.GetStatusSummary()}");
            }

            // RECORDING_PHYSICAL_RELEASE_INVARIANT:
            // worker終了でBonDriverプロセス所有権が消滅した後、lease解放とgeneration更新へ直結する。
            // 物理解放証拠の後へ固定settleを追加して後続開始を遅らせてはならない。
            _log.Add("REC_STOP_DEVICE_RELEASE_EVIDENCE", $"R{rid}",
                $"result=READY fixedWaitMs=0 processExited={processExitedWithoutKill || processAlreadyGone} chainHandoff={hasPendingChainSuccessor} tuner={SafeValue(session.Lease.Name)} rule=recording_physical_release_contract");

            // CHAIN_PHYSICAL_RELEASE_HANDOFF_INVARIANT:
            // 後続開始に必要なのは前段worker終了・デバイス解放・lease解放・active owner除去まで。
            // TS検証、result file待ち、品質集計、結果保存、typed event確定を後続開始の前へ戻してはならない。
            _log.Add("CHAIN_TRACE", $"R{rid}", $"[CHAIN] stage=before_lease_dispose successor={(hasPendingChainSuccessor ? $"R{pendingChainSuccessorId}" : "-")} pid={pid} lease={session.Lease.Name} status={_tunerPool.GetStatusSummary()}");
            try
            {
                session.Lease.Dispose();
                terminationEvidence.LeaseReleaseResult = "released";
            }
            catch (Exception ex)
            {
                terminationEvidence.LeaseReleaseResult = $"failed:{SafeValue(ex.GetType().Name)}";
                _log.Add("REC_RELEASE_EVIDENCE", $"R{rid}",
                    $"result=FAILED target=tuner_lease pid={pid} reason={SafeValue(ex.Message)} rule=recording_termination_evidence");
            }
            terminationEvidence.TunerStateAfterRelease = _tunerPool.GetStatusSummary();
            lock (_sessionGate)
            {
                _activeSessions.Remove(rid);
                _recordingTerminalFailureReasons.Remove(rid);
            }
            _recordingStartupZeroByteReloadAttempts.TryRemove(rid, out _);
            _log.Add("CHAIN_TRACE", $"R{rid}", $"[CHAIN] stage=after_lease_dispose successor={(hasPendingChainSuccessor ? $"R{pendingChainSuccessorId}" : "-")} pid={pid} activeOwnerRemoved=True status={terminationEvidence.TunerStateAfterRelease}");

            if (afterPhysicalRelease is not null)
            {
                // StartRecordingAsync must not be deferred by the stop-phase gate after physical ownership is gone.
                stopPhase.Dispose();
                _log.Add("CHAIN_PHYSICAL_RELEASE_HANDOFF", $"R{rid}",
                    $"result=READY successor={(hasPendingChainSuccessor ? $"R{pendingChainSuccessorId}" : "-")} tuner={SafeValue(session.Lease.Name)} pid={pid} " +
                    $"stopPhaseReleased=True lifecycle=Stopping postProcessing=pending rule=chain_physical_release_handoff_contract");
                await afterPhysicalRelease().ConfigureAwait(false);
            }

            var verifyReservation = _store.GetById(rid);
            RecordingTsVerifier.VerificationResult? tsVerification = null;
            if (verifyReservation is not null && !string.IsNullOrWhiteSpace(session.RecordingFilePath))
                tsVerification = await RecordingTsVerifier.VerifyAsync(verifyReservation, session.RecordingFilePath, "after_stop_before_release", _log);
            else if (verifyReservation is null)
                _log.Add("REC_TS_VERIFY", $"R{rid}", $"stage=after_stop_before_release result=SKIP reason=reservation_not_found path={session.RecordingFilePath}");
            else
                _log.Add("REC_TS_VERIFY", $"R{rid}", "stage=after_stop_before_release result=SKIP reason=effective_recording_file_unknown");

            // TVAIREPGREC_RESULT_PUBLICATION_INVARIANT:
            // TvAIrEpgRec writes ResultPath before process exit. After worker-exit evidence is established,
            // do not add a second fixed/polling wait for the same result. Missing JSON is recorded as evidence
            // and the existing TS verification/final-status contract decides the outcome.
            var recorderOutcome = ReadDirectRecorderOutcome(session.ResponsePath, session.RuntimeStatsPath);
            terminationEvidence.ResultFileState = recorderOutcome.ResponseExists
                ? (recorderOutcome.Success ? "present_success" : "present_failed")
                : "missing";
            terminationEvidence.TransportStreamValidation = tsVerification is null
                ? "not_available"
                : $"result={tsVerification.Result};verdict={tsVerification.Verdict};readable={tsVerification.ReadableJudgement}";
            var qualityService = verifyReservation?.ServiceName ?? stopReservation?.ServiceName ?? "-";
            var qualityTitle = verifyReservation?.Title ?? stopReservation?.Title ?? "-";
            var completionEvidence = EvaluateRecordingCompletionEvidence(recorderOutcome, tsVerification);
            var qualityClassification = ClassifyDirectRecorderQuality(recorderOutcome, tsVerification, completionEvidence);
            var qualityGroup = verifyReservation is not null ? ResolveGroup(verifyReservation) : (stopReservation is not null ? ResolveGroup(stopReservation) : "-");
            var qualityContext = BuildActiveRecordingGroupContext(qualityGroup ?? "-");
            _log.Add("REC_QUALITY_RESULT", $"R{rid}",
                $"service={SafeValue(qualityService)} title={ReservationTitleDisplayContract.ForLog(qualityTitle, 80)} " +
                $"qualityClass={qualityClassification.ClassName} severity={qualityClassification.Severity} userMeaning={qualityClassification.UserMeaning} action={qualityClassification.Action} " +
                $"verdict={SafeValue(recorderOutcome.QualityVerdict)} responseExists={recorderOutcome.ResponseExists} responseSuccess={recorderOutcome.Success} " +
                $"qualityGroup={SafeValue(qualityContext.Group)} qualityTuner={SafeValue(session.Lease.Name)} qualityDid={SafeValue(session.Lease.Did)} concurrentSameGroupRecordings={qualityContext.Count} concurrentSameGroupTuners={SafeValue(qualityContext.Names)} concurrentSameGroupDids={SafeValue(qualityContext.Dids)} " +
                $"inputRawDrops={SafeValue(recorderOutcome.RawContinuityDrops)} inputRawCcErrors={SafeValue(recorderOutcome.RawContinuityErrors)} inputRawSyncErrors={SafeValue(recorderOutcome.RawSyncErrors)} inputRawScrambled={SafeValue(recorderOutcome.RawScrambledPackets)} " +
                $"outputDrops={SafeValue(recorderOutcome.OutputContinuityDrops)} outputCcErrors={SafeValue(recorderOutcome.OutputContinuityErrors)} outputSyncErrors={SafeValue(recorderOutcome.OutputSyncErrors)} outputScrambled={SafeValue(recorderOutcome.OutputScrambledPackets)} " +
                $"rawLayerMeaning=pre_write_input_observation outputLayerMeaning=recorded_file_integrity runtimeStatsEmitted={SafeValue(recorderOutcome.RuntimeStatsEmitted)} runtimeStatsPath={SafeValue(recorderOutcome.RuntimeStatsPath)} " +
                $"rule=release_contract");
            LogDirectRecorderQualityCorrelation(rid, qualityService, qualityTitle, qualityClassification, recorderOutcome, qualityContext, session.Lease.Name, session.Lease.Did);
            LogDirectRecorderRuntimeTimeline(rid, qualityService, qualityTitle, qualityGroup ?? "-", recorderOutcome);
            if (finalStatus == ReservationStatus.Completed && (!recorderOutcome.ResponseExists || !recorderOutcome.Success))
            {
                var service = verifyReservation?.ServiceName ?? stopReservation?.ServiceName ?? "-";
                var title = verifyReservation?.Title ?? stopReservation?.Title ?? "-";
                var sourceKind = recorderOutcome.ResponseExists ? "worker_reported_ng" : "worker_response_missing";
                if (completionEvidence.CanKeepCompleted)
                {
                    _log.Add("TVAIREPGREC_FINAL_STATUS", $"R{rid}",
                        $"result=COMPLETED_BY_RECORDING_EVIDENCE source={sourceKind} service={SafeValue(service)} title={TrimForLog(title, 80)} responseExists={recorderOutcome.ResponseExists} responseSuccess={recorderOutcome.Success} finalStatus=Completed " + terminalFailureReasonPart + " " +
                        $"evidenceResult={completionEvidence.Result} evidenceReason={completionEvidence.Reason} verifyResult={SafeValue(tsVerification?.Result)} verifyVerdict={SafeValue(tsVerification?.Verdict)} verifyReadable={SafeValue(tsVerification?.ReadableJudgement)} " +
                        $"bytesWritten={recorderOutcome.BytesWritten} packetsWritten={recorderOutcome.PacketsWritten} outputSyncErrors={SafeValue(recorderOutcome.OutputSyncErrors)} outputScrambled={SafeValue(recorderOutcome.OutputScrambledPackets)} outputDrops={SafeValue(recorderOutcome.OutputContinuityDrops)} outputCcErrors={SafeValue(recorderOutcome.OutputContinuityErrors)} response={recorderOutcome.Summary} " +
                        $"rule=recording_completion_evidence_contract");
                }
                else
                {
                    _log.Add("TVAIREPGREC_FINAL_STATUS", $"R{rid}",
                        $"result=FAILED_BY_RECORDING_EVIDENCE source={sourceKind} service={SafeValue(service)} title={TrimForLog(title, 80)} responseExists={recorderOutcome.ResponseExists} responseSuccess={recorderOutcome.Success} finalStatus=Failed " + terminalFailureReasonPart + " " +
                        $"evidenceResult={completionEvidence.Result} evidenceReason={completionEvidence.Reason} verifyResult={SafeValue(tsVerification?.Result)} verifyVerdict={SafeValue(tsVerification?.Verdict)} verifyReadable={SafeValue(tsVerification?.ReadableJudgement)} " +
                        $"bytesWritten={recorderOutcome.BytesWritten} packetsWritten={recorderOutcome.PacketsWritten} outputSyncErrors={SafeValue(recorderOutcome.OutputSyncErrors)} outputScrambled={SafeValue(recorderOutcome.OutputScrambledPackets)} outputDrops={SafeValue(recorderOutcome.OutputContinuityDrops)} outputCcErrors={SafeValue(recorderOutcome.OutputContinuityErrors)} response={recorderOutcome.Summary} " +
                        $"rule=recording_completion_evidence_contract");
                    finalStatus = ReservationStatus.Failed;
                    terminalFailureReason = completionEvidence.Reason;
                }
            }
            else
            {
                var service = verifyReservation?.ServiceName ?? stopReservation?.ServiceName ?? "-";
                var title = verifyReservation?.Title ?? stopReservation?.Title ?? "-";
                var finalResultKind = recorderOutcome.ResponseExists
                    ? (recorderOutcome.Success ? "OK_CLEAR_OR_TVAIREPGREC_OK" : "TVAIREPGREC_NG_NON_COMPLETED")
                    : "NO_RESPONSE_NON_COMPLETED";
                _log.Add("TVAIREPGREC_FINAL_STATUS", $"R{rid}",
                    $"result={finalResultKind} service={SafeValue(service)} title={TrimForLog(title, 80)} finalStatus={finalStatus} " + terminalFailureReasonPart + " " +
                    $"evidenceResult={completionEvidence.Result} evidenceReason={completionEvidence.Reason} verifyResult={SafeValue(tsVerification?.Result)} verifyVerdict={SafeValue(tsVerification?.Verdict)} verifyReadable={SafeValue(tsVerification?.ReadableJudgement)} " +
                    $"response={recorderOutcome.Summary} rule=recording_completion_evidence_contract");
            }

            var finalizedReservation = verifyReservation ?? stopReservation ?? _store.GetById(rid);
            TvAirRecordingResultDto? finalizedResultDto = null;
            if (finalizedReservation is not null)
            {
                static long? ParseQuality(string value) => long.TryParse(value, out var parsed) ? parsed : null;
                var drop = ParseQuality(recorderOutcome.OutputContinuityDrops);
                var error = ParseQuality(recorderOutcome.OutputContinuityErrors);
                var scramble = ParseQuality(recorderOutcome.OutputScrambledPackets);
                var qualityAvailable = drop.HasValue && error.HasValue && scramble.HasValue;
                bool? fileCreated = string.IsNullOrWhiteSpace(session.RecordingFilePath)
                    ? null
                    : File.Exists(session.RecordingFilePath);
                var finalizedProgram = finalizedReservation.EventId == 0
                    ? null
                    : _programEvents.GetByEventKey(finalizedReservation.NetworkId, finalizedReservation.TransportStreamId, finalizedReservation.ServiceId, finalizedReservation.EventId);
                var resultDto = new TvAirRecordingResultDto
                {
                    ReservationId = $"R{rid}",
                    RecordingId = $"R{rid}",
                    ServiceName = finalizedReservation.ServiceName,
                    EventTitle = finalizedReservation.Title,
                    NetworkId = finalizedReservation.NetworkId,
                    TransportStreamId = finalizedReservation.TransportStreamId,
                    ServiceId = finalizedReservation.ServiceId,
                    EventId = finalizedReservation.EventId,
                    ScheduledStartTime = new DateTimeOffset(finalizedReservation.ScheduledStartTime ?? finalizedReservation.StartTime),
                    Genre = finalizedProgram is null ? string.Empty : EpgProjection.GenreLabel(finalizedProgram.Genre, finalizedProgram.GenreCodes),
                    GenreCodes = finalizedProgram?.GenreCodes ?? string.Empty,
                    // Final lifecycle timestamps are committed by ReservationStore.TryCompleteRecordingStop.
                    // Do not stamp a second, pre-CAS ActualEndTime here; the committed reservation
                    // is applied to the finalized result immediately after the terminal CAS succeeds.
                    ActualStartTime = finalizedReservation.RecordingStartedAt.HasValue ? new DateTimeOffset(finalizedReservation.RecordingStartedAt.Value) : null,
                    ActualEndTime = null,
                    Result = finalStatus.ToString(),
                    EndReason = finalStatus switch
                    {
                        ReservationStatus.Completed => "Completed",
                        ReservationStatus.Cancelled => "UserStopped",
                        _ when !string.IsNullOrWhiteSpace(terminalFailureReason) => terminalFailureReason!,
                        _ => "RecordingFailed"
                    },
                    FilePath = string.IsNullOrWhiteSpace(session.RecordingFilePath) ? null : session.RecordingFilePath,
                    FileCreated = fileCreated,
                    Drop = drop,
                    Error = error,
                    Scramble = scramble,
                    QualityDataAvailable = qualityAvailable,
                    QualityCompleteness = qualityAvailable ? recorderOutcome.QualityCompleteness : "unavailable",
                    QualitySource = qualityAvailable ? recorderOutcome.QualitySource : "Unavailable",
                    ResourceReleaseState = string.IsNullOrWhiteSpace(terminationEvidence.LeaseReleaseResult) ? "unknown" : terminationEvidence.LeaseReleaseResult,
                    ResultFinalized = true
                };
                finalizedResultDto = resultDto;
            }

            CleanupTvAIrEpgRecRecordingRuntimeFiles(session, recorderOutcome, rid, finalStatus, tsVerification);

            var stopLifecycleCommitted = false;
            Reservation? committedReservation = null;
            if (lifecycleClaimed && stoppingDataVersion.HasValue)
            {
                var stopCommit = _store.TryCompleteRecordingStop(rid, stoppingDataVersion.Value, finalStatus, terminalFailureReason);
                if (!stopCommit.Applied)
                {
                    _log.Add("REC_STOP_LIFECYCLE_COMMIT", $"R{rid}",
                        $"result=REJECTED reason={SafeValue(stopCommit.Reason)} expectedStatus=Stopping actualStatus={SafeValue(stopCommit.CurrentStatus?.ToString())} expectedVersion={stoppingDataVersion.Value} actualVersion={stopCommit.CurrentDataVersion} finalStatus={finalStatus} action=suppress_terminal_projections rule=release_contract");
                }
                else
                {
                    stopLifecycleCommitted = true;
                    committedReservation = _store.GetById(rid);
                    if (committedReservation is not null && finalizedResultDto is not null)
                        finalizedResultDto = ApplyCommittedRecordingLifecycle(finalizedResultDto, committedReservation);
                    _log.Add("REC_STOP_LIFECYCLE_COMMIT", $"R{rid}",
                        $"result=APPLIED status={finalStatus} dataVersion={stopCommit.PreviousDataVersion}->{stopCommit.CurrentDataVersion} rule=release_contract");
                }
            }
            else
            {
                _log.Add("REC_STOP_LIFECYCLE_COMMIT", $"R{rid}",
                    preserveRecordingLifecycleForRecovery
                        ? $"result=SKIPPED reason=preserved_for_atomic_recovery finalStatus={finalStatus} action=common_interrupted_recording_recovery rule=interrupted_recording_recovery_contract"
                        : $"result=SKIPPED reason=stop_lifecycle_not_claimed finalStatus={finalStatus} action=physical_owner_cleanup_only rule=release_contract");
            }
            RemoveChainDirectRecorderSessionScaffold(rid, "stop_session_finally", finalStatus.ToString());
            try
            {
                TvAirManagedProcessRegistry.Unregister(pid);
                terminationEvidence.RegistryReleaseResult = "unregistered";
            }
            catch (Exception ex)
            {
                terminationEvidence.RegistryReleaseResult = $"failed:{SafeValue(ex.GetType().Name)}";
                _log.Add("REC_RELEASE_EVIDENCE", $"R{rid}",
                    $"result=FAILED target=managed_process_registry pid={pid} reason={SafeValue(ex.Message)} rule=recording_termination_evidence");
            }
            if (session.ActivityHandle is null)
            {
                terminationEvidence.ActivityReleaseResult = "not_registered";
            }
            else
            {
                try
                {
                    session.ActivityHandle.Dispose();
                    terminationEvidence.ActivityReleaseResult = "released";
                }
                catch (Exception ex)
                {
                    terminationEvidence.ActivityReleaseResult = $"failed:{SafeValue(ex.GetType().Name)}";
                    _log.Add("REC_RELEASE_EVIDENCE", $"R{rid}",
                        $"result=FAILED target=activity_handle pid={pid} reason={SafeValue(ex.Message)} rule=recording_termination_evidence");
                }
            }
            _log.Add("PROCESS_OWNERSHIP", $"R{rid}",
                $"unregisterManagedPid=True pid={pid} reason=recording_session_finished rule=release_contract");

            if (stopLifecycleCommitted && finalizedResultDto is not null && committedReservation is not null)
            {
                // RECORDING_STOP_RESULT_PROJECTION_ISOLATION_INVARIANT:
                // The reservation terminal CAS is already committed at this point. Result persistence and
                // typed-event projection are downstream side effects and must not throw the stop lifecycle
                // back into exception-settle or leave the session finalization state open.
                try
                {
                    var updated = _userEvents.EnrichRecordingResult(finalizedResultDto, terminationEvidence.ToDetails());
                    _log.Add("REC_STOP_RESULT_PROJECTION", $"R{rid}",
                        $"target=UserEventLog result={(updated > 0 ? "APPLIED" : "FAILED")} updated={updated} terminalStatusCommitted=True finalStatus={finalStatus} rule=recording_result_projection_contract");
                }
                catch (Exception projectionEx)
                {
                    _log.Add("REC_STOP_RESULT_PROJECTION", $"R{rid}",
                        $"target=UserEventLog result=FAILED terminalStatusCommitted=True finalStatus={finalStatus} errorType={projectionEx.GetType().Name} error={TrimForLog(projectionEx.Message, 240)} action=continue_remaining_projections rule=recording_result_projection_contract");
                }

                RecordingResultUpsertOutcome? recordingResultOutcome = null;
                try
                {
                    recordingResultOutcome = _recordingResults.Upsert(finalizedResultDto, terminationEvidence);
                    _log.Add("REC_STOP_RESULT_PROJECTION", $"R{rid}",
                        $"target=RecordingResultStore result={recordingResultOutcome.Value} terminalStatusCommitted=True finalStatus={finalStatus} rule=recording_result_projection_contract");
                }
                catch (Exception projectionEx)
                {
                    _log.Add("REC_STOP_RESULT_PROJECTION", $"R{rid}",
                        $"target=RecordingResultStore result=FAILED terminalStatusCommitted=True finalStatus={finalStatus} errorType={projectionEx.GetType().Name} error={TrimForLog(projectionEx.Message, 240)} action=skip_finalized_event rule=recording_result_projection_contract");
                }

                if (recordingResultOutcome == RecordingResultUpsertOutcome.AppliedNewFinalized)
                {
                    try
                    {
                        _typedEvents.Publish(new TvAirEventDto
                        {
                            EventType = TvAirEventType.RecordingResultFinalized,
                            EntityId = $"recording:R{rid}",
                            EntityVersion = committedReservation.DataVersion,
                            DataRevision = committedReservation.DataVersion,
                            ReservationId = $"R{rid}",
                            ServiceName = committedReservation.ServiceName,
                            ProgramTitle = committedReservation.Title,
                            Reservation = PluginTypedEventHub.ToSnapshot(committedReservation),
                            RecordingResult = finalizedResultDto
                        });
                        _log.Add("REC_STOP_RESULT_PROJECTION", $"R{rid}",
                            $"target=PluginTypedEvent result=QUEUED terminalStatusCommitted=True finalStatus={finalStatus} source=AppliedNewFinalized rule=recording_result_finalize_once_contract");
                    }
                    catch (Exception projectionEx)
                    {
                        _log.Add("REC_STOP_RESULT_PROJECTION", $"R{rid}",
                            $"target=PluginTypedEvent result=FAILED terminalStatusCommitted=True finalStatus={finalStatus} errorType={projectionEx.GetType().Name} error={TrimForLog(projectionEx.Message, 240)} action=keep_other_projections rule=recording_result_projection_contract");
                    }
                }
                else
                {
                    _log.Add("REC_STOP_RESULT_PROJECTION", $"R{rid}",
                        $"target=PluginTypedEvent result=SKIPPED reason=recording_result_not_newly_finalized storeOutcome={(recordingResultOutcome?.ToString() ?? "store_failed")} rule=recording_result_finalize_once_contract");
                }
            }

            finalizationCompleted = true;
            if (stopLifecycleCommitted)
            {
                commitEvents();
                _log.Add("PLUGIN_TYPED_EVENT_OUTBOX", $"R{rid}", $"result=COMMITTED operation=RecordingStop finalStatus={finalStatus} rule=typed_event_outbox");
            }
            else
            {
                _log.Add("PLUGIN_TYPED_EVENT_OUTBOX", $"R{rid}", $"result=DISCARDED operation=RecordingStop finalStatus={finalStatus} reason=reservation_terminal_commit_not_applied rule=typed_event_outbox");
            }
            session.MarkFinalized();
            _log.Add("REC_STOP_COMMON_EXIT", $"R{rid}",
                $"status={finalStatus} operationId={session.OperationId} pid={pid} tuner={SafeValue(session.Lease.Name)} route={("TvAIrEpgRecOnly")} tunerStatus={_tunerPool.GetStatusSummary()}");
            _log.Add("Scheduler", $"R{rid}", $"録画終了完了: status={finalStatus} tunerStatus={_tunerPool.GetStatusSummary()}");
            LogRecordingStopChainContext(session, finalStatus, "after_release_and_status_update");

            // 録画終了後に競合フラグを再評価（チューナー解放により競合が解消する場合がある）。
            // 遅延要求の消費前に停止フェーズを終了する。停止中のままConsumeしてから共通ルートを実行すると、
            // その実行中に到着した別要求が再度deferされ、後続のConsumeがないまま残留するため。
            if (!suppressPostStopMaintenance)
            {
                stopPhase.Dispose();
                var deferred = StopPhaseGate.ConsumeDeferred();
                if (deferred.allocationRequest is not null || deferred.wakeRebuild)
                {
                    _log.Add("Scheduler", $"R{rid}",
                        $"STOP_PHASE_CONSUME pendingAlloc={deferred.allocCount} pendingWake={deferred.wakeCount} reason=after_recording_stop_completed");
                }

                if (deferred.allocationRequest is not null)
                {
                    var request = deferred.allocationRequest with
                    {
                        ReevaluateAllocations = true,
                        RefreshWakeTask = true,
                        BypassStopPhaseGate = true
                    };
                    var applied = _allocationRoute.Run(request);
                    _log.Add("STOP_PHASE_DEFERRED_ROUTE_APPLIED", $"R{rid}",
                        $"result=OK source={request.Source} action={request.Action} matcher={request.RunKeywordMatcher} syncProgram={request.SyncProgramRuleReservations} reevaluate={request.ReevaluateAllocations} preEpg={request.RefreshPreRecordEpgEntries} wake={request.RefreshWakeTask} changed={applied.ChangedCount} conflictOn={applied.ConflictOnCount} conflictOff={applied.ConflictOffCount} deferredAgain={applied.Deferred} pendingAlloc={deferred.allocCount} pendingWake={deferred.wakeCount} rule=release_contract");
                }
                else
                {
                    ReevaluateAndLog($"R{rid}終了後", bypassStopPhaseGate: true);
                }

                TriggerRecordingAfterActionIfNeeded(rid, finalStatus, verifyReservation ?? stopReservation);
            }
            else
            {
                _log.Add("REC_STOP_BATCH_DEFER", $"R{rid}",
                    "result=DEFERRED actions=allocation,wake,after_action reason=batch_missing_process_finalize rule=release_contract");
            }
        }
        catch (Exception ex)
        {
            stopFailure = ex;
            throw;
        }
        finally
        {
            if (!finalizationCompleted)
            {
                var exceptionSettleApplied = false;
                string? committedStopFailureReason = null;
                if (lifecycleClaimed && stoppingDataVersion.HasValue)
                {
                    try
                    {
                        ReservationLifecycleTransitionResult settle;
                        if (physicalStopStarted)
                        {
                            committedStopFailureReason = BuildRecordingStopExceptionReason(stopFailure);
                            settle = _store.TryCompleteRecordingStop(
                                session.ReservationId,
                                stoppingDataVersion.Value,
                                ReservationStatus.Failed,
                                committedStopFailureReason);
                        }
                        else
                        {
                            settle = _store.TryTransitionLifecycle(
                                session.ReservationId,
                                ReservationStatus.Stopping,
                                stoppingDataVersion.Value,
                                ReservationStatus.Recording);
                        }
                        exceptionSettleApplied = settle.Applied;

                        _log.Add("REC_STOP_EXCEPTION_SETTLE", $"R{session.ReservationId}",
                            $"result={(settle.Applied ? "APPLIED" : "REJECTED")} action={(physicalStopStarted ? "fail_after_physical_stop_started" : "rollback_before_physical_stop")} reason={SafeValue(settle.Reason)} expectedVersion={stoppingDataVersion.Value} actualVersion={settle.CurrentDataVersion} actualStatus={SafeValue(settle.CurrentStatus?.ToString())} operationId={session.OperationId} rule=release_contract");
                    }
                    catch (Exception settleEx)
                    {
                        _log.Add("REC_STOP_EXCEPTION_SETTLE", $"R{session.ReservationId}",
                            $"result=FAILED action={(physicalStopStarted ? "fail_after_physical_stop_started" : "rollback_before_physical_stop")} errorType={settleEx.GetType().Name} error={TrimForLog(settleEx.Message, 240)} operationId={session.OperationId} rule=release_contract");
                    }
                }

                if (physicalStopStarted && exceptionSettleApplied && stopFailure is not null)
                {
                    ProjectStopExceptionFallbackResult(
                        session,
                        stopFailure,
                        committedStopFailureReason ?? BuildRecordingStopExceptionReason(stopFailure));
                }
                session.CancelFinalization();
            }
            stopGate.Release();
        }
    }


    // RECORDING_FAILURE_REASON_SINGLE_SOURCE_INVARIANT:
    // A terminal failure reason committed by ReservationStore is also the exact machine-readable reason
    // projected to RecordingResult, termination evidence, user-event enrichment and typed events.
    // Downstream projections must not rename or reinterpret the same failure.
    private static string BuildRecordingStopExceptionReason(Exception? stopFailure)
        => stopFailure is null ? "recording_stop_exception" : $"recording_stop_exception:{stopFailure.GetType().Name}";

    private void ProjectStopExceptionFallbackResult(RecordingSession session, Exception stopFailure, string committedFailureReason)
    {
        var rid = session.ReservationId;
        var reservation = _store.GetById(rid);
        if (reservation is null)
        {
            _log.Add("REC_STOP_EXCEPTION_RESULT", $"R{rid}",
                $"result=SKIPPED reason=reservation_missing errorType={stopFailure.GetType().Name} rule=recording_result_projection_contract");
            return;
        }

        var outcome = ReadDirectRecorderOutcome(session.ResponsePath, session.RuntimeStatsPath);
        static long? ParseQuality(string value) => long.TryParse(value, out var parsed) ? parsed : null;
        var drop = ParseQuality(outcome.OutputContinuityDrops);
        var error = ParseQuality(outcome.OutputContinuityErrors);
        var scramble = ParseQuality(outcome.OutputScrambledPackets);
        var qualityAvailable = drop.HasValue && error.HasValue && scramble.HasValue;
        var evidence = new RecordingTerminationEvidence
        {
            RecordingRecoveryChainId = reservation.RecordingRecoveryChainId ?? string.Empty,
            PowerResumeCycleId = _powerResumeCycleContext.Value ?? string.Empty,
            TerminationReason = committedFailureReason,
            WorkerProcessId = session.ProcessId,
            WorkerIdentityResult = "stop_exception_settled",
            LastFileGrowthAt = session.LastRecordingFileGrowthAt,
            LastObservedFileSize = session.LastObservedRecordingFileBytes,
            StopRequestedAt = DateTime.Now,
            StopRequestResult = "exception_settle_failed_recording",
            ResultFileState = outcome.ResponseExists ? "present" : "missing_or_recovered",
            LeaseReleaseResult = "unknown_after_stop_exception",
            TunerStateAfterRelease = _tunerPool.GetStatusSummary()
        };
        var fallbackProgram = reservation.EventId == 0
            ? null
            : _programEvents.GetByEventKey(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId, reservation.EventId);
        var result = new TvAirRecordingResultDto
        {
            ReservationId = $"R{rid}",
            RecordingId = $"R{rid}",
            ServiceName = reservation.ServiceName,
            EventTitle = reservation.Title,
            NetworkId = reservation.NetworkId,
            TransportStreamId = reservation.TransportStreamId,
            ServiceId = reservation.ServiceId,
            EventId = reservation.EventId,
            ScheduledStartTime = new DateTimeOffset(reservation.ScheduledStartTime ?? reservation.StartTime),
            Genre = fallbackProgram is null ? string.Empty : EpgProjection.GenreLabel(fallbackProgram.Genre, fallbackProgram.GenreCodes),
            GenreCodes = fallbackProgram?.GenreCodes ?? string.Empty,
            ActualStartTime = reservation.RecordingStartedAt.HasValue ? new DateTimeOffset(reservation.RecordingStartedAt.Value) : null,
            ActualEndTime = reservation.RecordingFinishedAt.HasValue ? new DateTimeOffset(reservation.RecordingFinishedAt.Value) : null,
            Result = ReservationStatus.Failed.ToString(),
            EndReason = committedFailureReason,
            FilePath = string.IsNullOrWhiteSpace(session.RecordingFilePath) ? null : session.RecordingFilePath,
            FileCreated = string.IsNullOrWhiteSpace(session.RecordingFilePath) ? null : File.Exists(session.RecordingFilePath),
            Drop = drop,
            Error = error,
            Scramble = scramble,
            QualityDataAvailable = qualityAvailable,
            QualityCompleteness = qualityAvailable ? outcome.QualityCompleteness : "unavailable",
            QualitySource = qualityAvailable ? outcome.QualitySource : "Unavailable",
            ResourceReleaseState = evidence.LeaseReleaseResult,
            ResultFinalized = true
        };

        try
        {
            var updated = _userEvents.EnrichRecordingResult(result, evidence.ToDetails());
            _log.Add("REC_STOP_EXCEPTION_RESULT", $"R{rid}",
                $"target=UserEventLog result={(updated > 0 ? "APPLIED" : "FAILED")} updated={updated} rule=recording_result_projection_contract");
        }
        catch (Exception projectionEx)
        {
            _log.Add("REC_STOP_EXCEPTION_RESULT", $"R{rid}",
                $"target=UserEventLog result=FAILED errorType={projectionEx.GetType().Name} error={TrimForLog(projectionEx.Message, 240)} action=continue_remaining_projections rule=recording_result_projection_contract");
        }

        RecordingResultUpsertOutcome? recordingResultOutcome = null;
        try
        {
            recordingResultOutcome = _recordingResults.Upsert(result, evidence);
            _log.Add("REC_STOP_EXCEPTION_RESULT", $"R{rid}",
                $"target=RecordingResultStore result={recordingResultOutcome.Value} rule=recording_result_projection_contract");
        }
        catch (Exception projectionEx)
        {
            _log.Add("REC_STOP_EXCEPTION_RESULT", $"R{rid}",
                $"target=RecordingResultStore result=FAILED errorType={projectionEx.GetType().Name} error={TrimForLog(projectionEx.Message, 240)} action=skip_finalized_event rule=recording_result_projection_contract");
        }

        if (recordingResultOutcome == RecordingResultUpsertOutcome.AppliedNewFinalized)
        {
            try
            {
                using var eventScope = _typedEvents.BeginOutboxScope(out var commitEvents);
                _typedEvents.Publish(new TvAirEventDto
                {
                    EventType = TvAirEventType.RecordingResultFinalized,
                    EntityId = $"recording:R{rid}",
                    EntityVersion = reservation.DataVersion,
                    DataRevision = reservation.DataVersion,
                    ReservationId = $"R{rid}",
                    ServiceName = reservation.ServiceName,
                    ProgramTitle = reservation.Title,
                    Reservation = PluginTypedEventHub.ToSnapshot(reservation),
                    RecordingResult = result
                });
                commitEvents();
                _log.Add("REC_STOP_EXCEPTION_RESULT", $"R{rid}",
                    "target=PluginTypedEvent result=COMMITTED source=AppliedNewFinalized rule=recording_result_finalize_once_contract");
            }
            catch (Exception projectionEx)
            {
                _log.Add("REC_STOP_EXCEPTION_RESULT", $"R{rid}",
                    $"target=PluginTypedEvent result=FAILED errorType={projectionEx.GetType().Name} error={TrimForLog(projectionEx.Message, 240)} action=keep_other_projections rule=recording_result_projection_contract");
            }
        }
        else
        {
            _log.Add("REC_STOP_EXCEPTION_RESULT", $"R{rid}",
                $"target=PluginTypedEvent result=SKIPPED reason=recording_result_not_newly_finalized storeOutcome={(recordingResultOutcome?.ToString() ?? "store_failed")} rule=recording_result_finalize_once_contract");
        }
    }


    // RECORDING_RESULT_LIFECYCLE_SINGLE_SOURCE_INVARIANT:
    // RecordingStartedAt / RecordingFinishedAt are committed by the reservation lifecycle transaction.
    // RecordingResultStore, user-event enrichment, typed events, and plugin history must project those
    // committed timestamps rather than creating a second wall-clock value after or before the CAS.
    private static TvAirRecordingResultDto ApplyCommittedRecordingLifecycle(TvAirRecordingResultDto result, Reservation reservation)
        => new()
        {
            ReservationId = result.ReservationId,
            RecordingId = result.RecordingId,
            ServiceName = result.ServiceName,
            EventTitle = result.EventTitle,
            NetworkId = result.NetworkId,
            TransportStreamId = result.TransportStreamId,
            ServiceId = result.ServiceId,
            EventId = result.EventId,
            ScheduledStartTime = result.ScheduledStartTime,
            Genre = result.Genre,
            GenreCodes = result.GenreCodes,
            ActualStartTime = reservation.RecordingStartedAt.HasValue ? new DateTimeOffset(reservation.RecordingStartedAt.Value) : result.ActualStartTime,
            ActualEndTime = reservation.RecordingFinishedAt.HasValue ? new DateTimeOffset(reservation.RecordingFinishedAt.Value) : result.ActualEndTime,
            Result = result.Result,
            EndReason = result.EndReason,
            FilePath = result.FilePath,
            FileCreated = result.FileCreated,
            Drop = result.Drop,
            Error = result.Error,
            Scramble = result.Scramble,
            QualityDataAvailable = result.QualityDataAvailable,
            QualityCompleteness = result.QualityCompleteness,
            QualitySource = result.QualitySource,
            ResourceReleaseState = result.ResourceReleaseState,
            ResultFinalized = result.ResultFinalized
        };


    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        CancelRecordingAfterActionPlan("host_stopping", 0);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    // RECORDING_AFTER_ACTION_SETTINGS_BOUNDARY_CONTRACT
    // 待機中planは作成時のaction/delay snapshotを所有する。設定変更時点で待機中planを破棄し、
    // 実行直前の再読込による二重判定を持たない。
    // 新設定は、次に「最後に残っている録画」がCompleted（正常完了）終端した時点で新しいplanとして確定する。
    // Cancelled/Failed等の非Completed終端では録画終了後アクションを起動しない。
    public void OnRecordingAfterActionSettingsChanged()
        => CancelRecordingAfterActionPlan("setting_changed", 0);

    private void TriggerRecordingAfterActionIfNeeded(int reservationId, ReservationStatus finalStatus, Reservation? reservation)
    {
        var action = IniSettingsService.NormalizeRecordingAfterAction(_ini.RecordingAfterAction);
        var service = reservation?.ServiceName ?? "-";
        var title = reservation?.Title ?? "-";
        if (action == "none")
        {
            CancelRecordingAfterActionPlan("setting_none", reservationId);
            _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result=SKIP action=none service={SafeValue(service)} title={TrimForLog(title, 80)}");
            return;
        }

        if (finalStatus != ReservationStatus.Completed)
        {
            _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result=SKIP action={action} reason=final_status_not_completed finalStatus={finalStatus} service={SafeValue(service)} title={TrimForLog(title, 80)}");
            return;
        }

        int activeCount;
        lock (_sessionGate) activeCount = _activeSessions.Count;
        if (activeCount > 0)
        {
            _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result=WAIT action={action} reason=active_recordings_remain activeCount={activeCount} service={SafeValue(service)} title={TrimForLog(title, 80)}");
            return;
        }

        var delayMinutes = IniSettingsService.NormalizeRecordingAfterActionDelayMinutes(_ini.RecordingAfterActionDelayMinutes);
        var executeAt = DateTime.Now.AddMinutes(delayMinutes);
        CancellationTokenSource cts;
        long generation;
        lock (_recordingAfterActionGate)
        {
            _recordingAfterActionCts?.Cancel();
            _recordingAfterActionCts?.Dispose();
            cts = new CancellationTokenSource();
            _recordingAfterActionCts = cts;
            generation = ++_recordingAfterActionGeneration;
        }

        _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result=ARMED action={action} reason=last_completed_recording service={SafeValue(service)} title={TrimForLog(title, 80)} delayMinutes={delayMinutes} executeAt={executeAt:yyyy/MM/dd HH:mm:ss} generation={generation} rule=release_contract");
        _ = RunRecordingAfterActionPlanAsync(new RecordingAfterActionPlan(
            generation,
            reservationId,
            action,
            delayMinutes,
            executeAt,
            service,
            title), cts.Token);
    }

    private async Task RunRecordingAfterActionPlanAsync(RecordingAfterActionPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            var notifyAt = plan.ExecuteAt.AddMinutes(-1);
            var beforeNotice = notifyAt - DateTime.Now;
            if (beforeNotice > TimeSpan.Zero)
                await Task.Delay(beforeNotice, cancellationToken).ConfigureAwait(false);

            if (!CanContinueRecordingAfterAction(plan, out var cancelReason, out var cancelDetail))
            {
                LogRecordingAfterActionCancelled(plan, cancelReason, cancelDetail);
                CompleteRecordingAfterActionPlan(plan.Generation);
                return;
            }

            var remaining = plan.ExecuteAt - DateTime.Now;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            using var countdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var decisionTask = TvAIrNotificationDialog.ShowPowerActionCountdownAsync(
                plan.Action,
                remaining,
                _ini.SystemTheme,
                countdownCts.Token);

            while (!decisionTask.IsCompleted)
            {
                await Task.WhenAny(
                    decisionTask,
                    Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)).ConfigureAwait(false);

                if (decisionTask.IsCompleted)
                    break;

                if (!CanContinueRecordingAfterAction(plan, out cancelReason, out cancelDetail))
                {
                    countdownCts.Cancel();
                    try { await decisionTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    LogRecordingAfterActionCancelled(plan, cancelReason, cancelDetail);
                    CompleteRecordingAfterActionPlan(plan.Generation);
                    return;
                }
            }

            var decision = await decisionTask.ConfigureAwait(false);

            if (decision == PowerActionCountdownDecision.Cancel)
            {
                _log.Add("RECORDING_AFTER_ACTION", $"R{plan.ReservationId}", $"result=CANCEL action={plan.Action} reason=user_cancelled generation={plan.Generation}");
                CompleteRecordingAfterActionPlan(plan.Generation);
                return;
            }

            if (decision == PowerActionCountdownDecision.Closed)
            {
                _log.Add("RECORDING_AFTER_ACTION", $"R{plan.ReservationId}", $"result=CANCEL action={plan.Action} reason=notification_closed generation={plan.Generation}");
                CompleteRecordingAfterActionPlan(plan.Generation);
                return;
            }

            if (!CanContinueRecordingAfterAction(plan, out cancelReason, out cancelDetail))
            {
                LogRecordingAfterActionCancelled(plan, cancelReason, cancelDetail);
                CompleteRecordingAfterActionPlan(plan.Generation);
                return;
            }

            if (decision != PowerActionCountdownDecision.ExecuteNow)
            {
                var finalWait = plan.ExecuteAt - DateTime.Now;
                if (finalWait > TimeSpan.Zero)
                    await Task.Delay(finalWait, cancellationToken).ConfigureAwait(false);
            }

            if (!CanContinueRecordingAfterAction(plan, out cancelReason, out cancelDetail))
            {
                LogRecordingAfterActionCancelled(plan, cancelReason, cancelDetail);
                CompleteRecordingAfterActionPlan(plan.Generation);
                return;
            }

            ExecuteRecordingAfterAction(plan);
            CompleteRecordingAfterActionPlan(plan.Generation);
        }
        catch (OperationCanceledException)
        {
            _log.Add("RECORDING_AFTER_ACTION", $"R{plan.ReservationId}", $"result=CANCEL action={plan.Action} reason=plan_replaced_or_host_stopping generation={plan.Generation}");
        }
        catch (Exception ex)
        {
            _log.Add("RECORDING_AFTER_ACTION", $"R{plan.ReservationId}", $"result=ERROR action={plan.Action} generation={plan.Generation} error={ex.GetType().Name}:{ex.Message}");
            CompleteRecordingAfterActionPlan(plan.Generation);
        }
    }

    private bool CanContinueRecordingAfterAction(RecordingAfterActionPlan plan, out string reason, out string detail)
    {
        lock (_recordingAfterActionGate)
        {
            if (_recordingAfterActionGeneration != plan.Generation || _recordingAfterActionCts is null)
            {
                reason = "plan_replaced";
                detail = $"currentGeneration={_recordingAfterActionGeneration}";
                return false;
            }
        }

        int activeCount;
        lock (_sessionGate) activeCount = _activeSessions.Count;
        if (activeCount > 0)
        {
            reason = "new_recording_started";
            detail = $"activeCount={activeCount}";
            return false;
        }

        var grEpg = _tunerPool.HasActiveEpgInGroup("GR", out var grSummary);
        var bscsEpg = _tunerPool.HasActiveEpgInGroup("BSCS", out var bscsSummary);
        if (grEpg || bscsEpg)
        {
            reason = "epg_running";
            detail = $"gr={SafeValue(grSummary)} bscs={SafeValue(bscsSummary)}";
            return false;
        }

        var activeViewerLeases = _externalTuners.GetActiveLeases();
        if (activeViewerLeases.Count > 0)
        {
            reason = "viewer_active";
            detail = $"activeViewerCount={activeViewerLeases.Count} profiles={string.Join(",", activeViewerLeases.Select(x => SafeValue(x.ViewerProfileId)).Distinct(StringComparer.OrdinalIgnoreCase))}";
            return false;
        }

        var now = DateTime.Now;
        var nextProtectedWake = _taskSvc.GetNextPowerActionWakeProtectionBoundary(now);
        if (nextProtectedWake is not null && now >= nextProtectedWake.WakeAt)
        {
            reason = "next_recording_wake_protected";
            detail = $"purpose={nextProtectedWake.Purpose} nextReservation={(nextProtectedWake.ReservationId.HasValue ? $"R{nextProtectedWake.ReservationId.Value}" : "-")} wakeAt={nextProtectedWake.WakeAt:yyyy/MM/dd HH:mm:ss} start={nextProtectedWake.StartTime:yyyy/MM/dd HH:mm:ss}";
            return false;
        }

        var currentAction = IniSettingsService.NormalizeRecordingAfterAction(_ini.RecordingAfterAction);
        var currentDelayMinutes = IniSettingsService.NormalizeRecordingAfterActionDelayMinutes(_ini.RecordingAfterActionDelayMinutes);
        if (currentAction != plan.Action || currentDelayMinutes != plan.DelayMinutes)
        {
            reason = "setting_changed";
            detail = $"currentAction={currentAction} currentDelayMinutes={currentDelayMinutes}";
            return false;
        }

        reason = string.Empty;
        detail = string.Empty;
        return true;
    }

    private void ExecuteRecordingAfterAction(RecordingAfterActionPlan plan)
    {
        if (!_applicationGate.TryBeginPowerTransition($"recording_after_action:{plan.Action}", out var transitionGeneration))
        {
            _log.Add("RECORDING_AFTER_ACTION", $"R{plan.ReservationId}", $"result=CANCEL action={plan.Action} reason=operation_gate_not_running state={_applicationGate.State} generation={plan.Generation} rule=power_transition_admission_contract");
            return;
        }

        IDisposable? physicalGate = null;
        try
        {
            physicalGate = TunerDeviceAccessGate.Enter(
                "RECORDING_AFTER_ACTION",
                msg => _log.Add("TUNER_DEVICE_LOCK", "RecordingAfterAction", msg));

            if (!CanContinueRecordingAfterAction(plan, out var finalReason, out var finalDetail))
            {
                _applicationGate.EndPowerTransition(transitionGeneration, $"cancelled_{finalReason}");
                LogRecordingAfterActionCancelled(plan, finalReason, finalDetail);
                return;
            }

            if (plan.Action == "sleep")
            {
                var ok = false;
                var err = 0;
                try
                {
                    ok = SetSuspendState(false, true, false);
                    err = ok ? 0 : Marshal.GetLastWin32Error();
                    _log.Add("RECORDING_AFTER_ACTION", $"R{plan.ReservationId}", $"result={(ok ? "EXECUTED" : "FAILED")} action=sleep win32={err} delayMinutes={plan.DelayMinutes} service={SafeValue(plan.Service)} title={TrimForLog(plan.Title, 80)} generation={plan.Generation} transitionGeneration={transitionGeneration} rule=release_contract");
                }
                finally
                {
                    _applicationGate.EndPowerTransition(transitionGeneration, ok ? "sleep_resumed" : $"sleep_failed_win32_{err}");
                    physicalGate.Dispose();
                    physicalGate = null;
                }
                return;
            }

            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/s /t 0 /c \"TvAIr 録画終了後アクション\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is null)
                {
                    _applicationGate.EndPowerTransition(transitionGeneration, "shutdown_process_start_failed");
                    physicalGate.Dispose();
                    physicalGate = null;
                    _log.Add("RECORDING_AFTER_ACTION", $"R{plan.ReservationId}", $"result=FAILED action=shutdown reason=process_start_returned_null delayMinutes={plan.DelayMinutes} generation={plan.Generation} rule=power_transition_admission_contract");
                    return;
                }

                _log.Add("RECORDING_AFTER_ACTION", $"R{plan.ReservationId}", $"result=EXECUTED action=shutdown pid={process.Id} windowsTimeoutSec=0 delayMinutes={plan.DelayMinutes} service={SafeValue(plan.Service)} title={TrimForLog(plan.Title, 80)} generation={plan.Generation} transitionGeneration={transitionGeneration} rule=release_contract");
                // shutdownが成立した場合は、新規物理操作を再開させない。
                // ApplicationStoppingがPowerTransitionからQuiescingへ引き継ぎ、プロセス終了がgate所有を終端する。
                physicalGate = null;
            }
            finally
            {
                process?.Dispose();
            }
        }
        catch
        {
            physicalGate?.Dispose();
            _applicationGate.EndPowerTransition(transitionGeneration, "power_action_exception");
            throw;
        }
        finally
        {
            physicalGate?.Dispose();
        }
    }

    private void LogRecordingAfterActionCancelled(RecordingAfterActionPlan plan, string reason, string detail)
        => _log.Add("RECORDING_AFTER_ACTION", $"R{plan.ReservationId}", $"result=CANCEL action={plan.Action} reason={reason} {detail} generation={plan.Generation}");

    private void CancelRecordingAfterActionPlan(string reason, int reservationId)
    {
        CancellationTokenSource? cts;
        long generation;
        lock (_recordingAfterActionGate)
        {
            cts = _recordingAfterActionCts;
            if (cts is null) return;
            generation = _recordingAfterActionGeneration;
            _recordingAfterActionCts = null;
            _recordingAfterActionGeneration++;
        }
        cts.Cancel();
        cts.Dispose();
        _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result=CANCEL action=scheduled reason={reason} generation={generation}");
    }

    private void CompleteRecordingAfterActionPlan(long generation)
    {
        CancellationTokenSource? cts = null;
        lock (_recordingAfterActionGate)
        {
            if (_recordingAfterActionGeneration != generation) return;
            cts = _recordingAfterActionCts;
            _recordingAfterActionCts = null;
        }
        cts?.Dispose();
    }

    private sealed record RecordingAfterActionPlan(
        long Generation,
        int ReservationId,
        string Action,
        int DelayMinutes,
        DateTime ExecuteAt,
        string Service,
        string Title);

    private void CleanupTvAIrEpgRecRecordingRuntimeFiles(RecordingSession session, DirectRecorderOutcome outcome, int reservationId, ReservationStatus finalStatus, RecordingTsVerifier.VerificationResult? tsVerification)
    {
        // RECORDING_RUNTIME_EVIDENCE_RETENTION_INVARIANT:
        // TS実体がclearでもworker response欠落はIPC/finalization異常の独立証拠である。
        // ClearEnoughForCompletedは予約終端の救済判断には使えても、runtime evidence削除の根拠にはしない。
        var keepReason = finalStatus == ReservationStatus.Failed
            ? "recording_failed_keep_runtime_evidence"
            : !outcome.ResponseExists
                ? "response_missing_keep_runtime_evidence"
                : !outcome.Success
                    ? "worker_reported_ng_keep_runtime_evidence"
                    : string.Empty;
        var shouldKeep = !string.IsNullOrWhiteSpace(keepReason);
        var targets = new[]
            {
                session.JobPath,
                session.ResponsePath,
                session.ProgressPath,
                session.StopSignalPath,
                session.RuntimeStatsPath,
                outcome.RuntimeStatsPath
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (shouldKeep)
        {
            _log.Add("TVAIREPGREC_RECORD_RUNTIME_CLEANUP", $"R{reservationId}",
                $"result=KEPT reason={keepReason} status={finalStatus} responseExists={outcome.ResponseExists} workerSuccess={outcome.Success} tsClear={tsVerification?.ClearEnoughForCompleted == true} files={targets.Count} job={SafeValue(session.JobPath)} result={SafeValue(session.ResponsePath)} progress={SafeValue(session.ProgressPath)} runtime={SafeValue(session.RuntimeStatsPath)} stopSignal={SafeValue(session.StopSignalPath)} rule=release_contract");
            return;
        }

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

        _log.Add("TVAIREPGREC_RECORD_RUNTIME_CLEANUP", $"R{reservationId}",
            $"result=OK status={finalStatus} deleted={deleted} failed={failed} targetFiles={targets.Count} reason=completed_recording_runtime_files_are_internal_artifacts rule=release_contract");
    }

    private static async Task<bool> WaitForProcessExitSignalAsync(
        System.Diagnostics.Process process,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (process.HasExited) return true;
        if (timeoutMs <= 0) return process.HasExited;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Refresh();
            return process.HasExited;
        }
    }

    private static string ReadTvAIrEpgRecStopProgressSummary(string? progressPath)
    {
        if (string.IsNullOrWhiteSpace(progressPath) || !File.Exists(progressPath)) return "progress=missing";
        try
        {
            var lines = File.ReadLines(progressPath).Where(x => !string.IsNullOrWhiteSpace(x)).TakeLast(80).ToList();
            var stages = new List<string>();
            foreach (var line in lines)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var stage = GetJsonString(root, "stage");
                    var message = TrimForLog(GetJsonString(root, "message"), 120);
                    if (stage.Contains("stop", StringComparison.OrdinalIgnoreCase)
                        || stage.Contains("shutdown", StringComparison.OrdinalIgnoreCase)
                        || stage.Contains("flush", StringComparison.OrdinalIgnoreCase)
                        || stage.Contains("close", StringComparison.OrdinalIgnoreCase)
                        || stage.Contains("release", StringComparison.OrdinalIgnoreCase))
                    {
                        stages.Add($"{stage}:{message}");
                    }
                }
                catch { }
            }
            if (stages.Count == 0) return $"progress=found stopStages=0 tailLines={lines.Count}";
            return $"progress=found stopStages={stages.Count} tail={TrimForLog(string.Join(" | ", stages.TakeLast(8)), 700)}";
        }
        catch (Exception ex)
        {
            return $"progress=read_error type={ex.GetType().Name}";
        }
    }

    private async Task<bool> TryStopTvAIrEpgRecProcessAsync(RecordingSession session, int reservationId, int timeoutMs)
    {
        var pid = session.ProcessId;
        try
        {
            if (string.IsNullOrWhiteSpace(session.StopSignalPath))
            {
                _log.Add("TVAIREPGREC_STOP_SIGNAL", $"R{reservationId}",
                    $"result=NG reason=stop_signal_path_empty pid={pid} rule=release_contract");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(session.StopSignalPath) ?? AppContext.BaseDirectory);
            await File.WriteAllTextAsync(session.StopSignalPath, DateTime.Now.ToString("O")).ConfigureAwait(false);
            _log.Add("TVAIREPGREC_STOP_SIGNAL", $"R{reservationId}",
                $"result=SENT pid={pid} stopSignal={SafeValue(session.StopSignalPath)} response={SafeValue(session.ResponsePath)} timeoutMs={timeoutMs} rule=release_contract");

            var started = DateTime.UtcNow;
            bool exited;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                exited = await WaitForProcessExitSignalAsync(p, timeoutMs).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                exited = true;
            }

            if (exited)
            {
                var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                // ResultPath is published before TvAIrEpgRec exits; process-exit evidence is the synchronization point.
                // Do not reintroduce result-file polling after this point.
                var responseExists = !string.IsNullOrWhiteSpace(session.ResponsePath) && File.Exists(session.ResponsePath);
                var fileSize = File.Exists(session.RecordingFilePath) ? new FileInfo(session.RecordingFilePath).Length : -1;
                var bridgeResponse = ReadDirectRecorderResponseSummary(session.ResponsePath);
                _log.Add("TVAIREPGREC_STOP_RESULT", $"R{reservationId}",
                    $"result=OK pid={pid} elapsedMs={elapsed} responseExists={responseExists} resultPublication=before_process_exit fileSize={fileSize} path={SafeValue(session.RecordingFilePath)} response={bridgeResponse} rule=release_contract");
                return true;
            }

            var timeoutResponse = ReadDirectRecorderResponseSummary(session.ResponsePath);
            var stopProgress = ReadTvAIrEpgRecStopProgressSummary(session.ProgressPath);
            _log.Add("TVAIREPGREC_STOP_RESULT", $"R{reservationId}",
                $"result=TIMEOUT pid={pid} timeoutMs={timeoutMs} response={timeoutResponse} progress={stopProgress} fallback=single_pid_exit rule=release_contract");
            return false;
        }
        catch (Exception ex)
        {
            var exceptionResponse = ReadDirectRecorderResponseSummary(session.ResponsePath);
            _log.Add("TVAIREPGREC_STOP_RESULT", $"R{reservationId}",
                $"result=NG pid={pid} error={TrimForLog(ex.Message, 240)} response={exceptionResponse} fallback=single_pid_exit rule=release_contract");
            return false;
        }
    }

    /// <summary>
    /// Bridge RecordStop が成功した録画用 TVTest を、Killではなく通常終了へ寄せる。
    /// CloseMainWindow が使えない/待っても残る場合のみ呼び出し側で単体PID終了へフォールバックする。
    /// </summary>

    /// <summary>
    /// TvAIr が起動・所有している録画用 TVTest の PID 単体だけを終了する。
    /// release_contract: taskkill /T と子プロセス kill は禁止。視聴用 TVTest/LIVETest を巻き込まない。
    /// </summary>
    private async Task KillProcessTreeAsync(int pid, int reservationId)
    {
        System.Diagnostics.Process? target = null;
        try
        {
            target = System.Diagnostics.Process.GetProcessById(pid);
            if (target.HasExited)
            {
                _log.Add("PROC_TRACE", $"R{reservationId}", $"[PROC] stage=probe pid={pid} exists=True alive=False killIssued=False");
                _log.Add("Scheduler", $"R{reservationId}", $"PID={pid} はすでに終了済み（Kill不要）");
                return;
            }
        }
        catch (ArgumentException)
        {
            _log.Add("PROC_TRACE", $"R{reservationId}", $"[PROC] stage=probe pid={pid} exists=False killIssued=False");
            _log.Add("Scheduler", $"R{reservationId}", $"PID={pid} はすでに終了済み（Kill不要）");
            return;
        }
        catch (Exception ex)
        {
            _log.Add("PROC_TRACE", $"R{reservationId}", $"[PROC] stage=probe_exception pid={pid} error={TrimForLog(ex.Message, 240)}");
            return;
        }

        try
        {
            _log.Add("PROCESS_OWNERSHIP", $"R{reservationId}",
                $"stopMode=single_pid_only targetPid={pid} treeKillForbidden=True taskkillForbidden=True closeMainWindowSkipped=True reason=protect_viewing_tvtest");
            _log.Add("PROC_TRACE", $"R{reservationId}",
                $"[PROC] stage=before_single_pid_kill pid={pid} alive=True killIssued=True entireTree=False");

            target.Kill(entireProcessTree: false);

            var killWaitStarted = DateTime.UtcNow;
            try
            {
                if (await WaitForProcessExitSignalAsync(target, 5000).ConfigureAwait(false))
                {
                    var elapsedMs = (int)(DateTime.UtcNow - killWaitStarted).TotalMilliseconds;
                    _log.Add("PROC_TRACE", $"R{reservationId}",
                        $"[PROC] stage=after_single_pid_kill pid={pid} alive=False elapsedMs={elapsedMs} entireTree=False");
                    _log.Add("Scheduler", $"R{reservationId}",
                        $"PID={pid} 単体終了完了（taskkill未使用・子プロセスkill禁止）");
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                var elapsedMs = (int)(DateTime.UtcNow - killWaitStarted).TotalMilliseconds;
                _log.Add("PROC_TRACE", $"R{reservationId}",
                    $"[PROC] stage=after_single_pid_kill pid={pid} alive=False elapsedMs={elapsedMs} entireTree=False");
                return;
            }

            _log.Add("PROC_TRACE", $"R{reservationId}",
                $"[PROC] stage=single_pid_kill_timeout pid={pid} alive=True warning=still_exists entireTree=False");
            _log.Add("Scheduler", $"R{reservationId}",
                $"警告: PID={pid} が単体Kill後も残存している可能性があります（taskkill /T は使用しません）");
        }
        catch (Exception ex)
        {
            _log.Add("PROC_TRACE", $"R{reservationId}",
                $"[PROC] stage=single_pid_kill_exception pid={pid} error={TrimForLog(ex.Message, 240)}");
            _log.Add("Scheduler", $"R{reservationId}", $"PID単体Kill例外: {ex.Message} / PID={pid}");
        }
        finally
        {
            target?.Dispose();
        }
    }

    private async Task StopAllSessionsAsync()
    {
        List<RecordingSession> all;
        lock (_sessionGate) all = _activeSessions.Values.ToList();

        foreach (var s in all)
        {
            var now = DateTime.Now;
            var preserveForRecovery = now < s.PlannedEndTime;
            if (preserveForRecovery)
            {
                _log.Add("REC_APP_SHUTDOWN_POLICY", $"R{s.ReservationId}",
                    $"result=PRESERVE_RECORDING_FOR_RESTART now={now:MM/dd HH:mm:ss} plannedEnd={s.PlannedEndTime:MM/dd HH:mm:ss} pid={s.ProcessId} tuner={SafeValue(s.Lease.Name)} " +
                    "action=stop_physical_worker_release_lease_keep_recording_lifecycle rule=interrupted_recording_recovery_contract");
            }

            await StopSessionAsync(
                s,
                ReservationStatus.Completed,
                suppressPostStopMaintenance: preserveForRecovery,
                preserveRecordingLifecycleForRecovery: preserveForRecovery).ConfigureAwait(false);
        }
    }


    private static string FormatPidList(IEnumerable<int> pids)
    {
        var list = pids.Select(p => p.ToString()).ToList();
        return list.Count == 0 ? "-" : string.Join(",", list);
    }

    // ─── 視聴強制終了（カウントダウン通知付き） ───────────────────


    // ─── ユーティリティ ──────────────────────────────────────────

    /// <summary>
    /// 競合フラグを再評価し、変化があった予約をログに出力する。
    /// </summary>
    private void MaybeReevaluateForTick(DateTime now)
    {
        _allocationReevaluateCounter++;

        bool hasNearStartReservation = false;
        try
        {
            var nearThreshold = now + NearStartReevaluateWindow;
            hasNearStartReservation = _store.GetByStatus(ReservationStatus.Scheduled)
                .Any(r => r.IsEnabled && r.StartTime <= nearThreshold);
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", "Conflict(Tick)", $"近接予約確認エラー: {ex.Message}");
        }

        if (_skipNextTickAllocationReevaluate)
        {
            _skipNextTickAllocationReevaluate = false;
            _allocationReevaluateCounter = 0;
            _hadNearStartReservationOnPreviousTick = hasNearStartReservation;
            return;
        }

        var nearStartEdgeTriggered = hasNearStartReservation && !_hadNearStartReservationOnPreviousTick;
        _hadNearStartReservationOnPreviousTick = hasNearStartReservation;

        if (!_forceAllocationReevaluate
            && !nearStartEdgeTriggered
            && _allocationReevaluateCounter < AllocationReevaluateIntervalTicks)
        {
            return;
        }

        ReevaluateAndLog("Tick", syncProgramRules: false);
        _allocationReevaluateCounter = 0;
        _forceAllocationReevaluate = false;
    }

    private ReservationAllocationRouteResult? ReevaluateAndLog(
        string context,
        bool syncProgramRules = true,
        bool bypassStopPhaseGate = false,
        bool waitForActiveSingleFlight = true)
    {
        try
        {
            return _allocationRoute.Run(new ReservationAllocationRouteRequest(
                Source: "ReservationScheduler",
                Action: $"Reevaluate:{context}",
                RunKeywordMatcher: false,
                SyncProgramRuleReservations: syncProgramRules,
                ReevaluateAllocations: true,
                RefreshPreRecordEpgEntries: false,
                RefreshWakeTask: false,
                BypassStopPhaseGate: bypassStopPhaseGate,
                EmitConflictLogs: true,
                ConflictLogCategory: "Scheduler",
                ConflictLogTitle: $"Conflict({context})"),
                waitForActiveSingleFlight);
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", $"Conflict({context})", $"競合再評価エラー: {ex.Message}");
            return null;
        }
    }

    private void UpdateWakeTasksForMaintenance()
    {
        try
        {
            if (_taskSvc.HasPendingDeferredWakeTask())
            {
                _log.Add("Scheduler", "Wake(Maintenance)",
                    "定期Wakeタスク更新を省略: reason=deferred-wake-pending rule=release_contract");
                return;
            }

            _taskSvc.UpdateWakeTask();
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", "Wake(Maintenance)", $"定期Wakeタスク更新エラー: {ex.Message}");
        }
    }

    private void PublishRecordingTimelineEpgGate(DateTime now, IReadOnlyList<Reservation>? scheduledSnapshot = null)
    {
        // 録画前EPG確認用timeline boundaryは、このメソッドだけが現在の予約一覧から再構築する。
        // 呼び出し元ごとに異なるhorizonの候補集合を渡すと、広いhorizonで置いた境界を
        // 狭いhorizon側が直後にClearできてしまうため、候補抽出と置換/消去を同じ正本へ集約する。
        var safetySeconds = RecordingTimelineEpgGateSafetySeconds;
        var horizon = now.AddSeconds(safetySeconds);
        var scheduled = scheduledSnapshot ?? _store.GetByStatus(ReservationStatus.Scheduled);
        var upcoming = BuildUpcomingRecordingsForEpgGate(scheduled, horizon);
        var targets = upcoming
            .Where(x => x.StartTime.AddSeconds(-_ini.PreStartMarginSeconds) <= horizon)
            .GroupBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.StartTime).ThenBy(x => x.ReservationId).First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in new[] { "GR", "BSCS" })
        {
            if (!targets.TryGetValue(group, out var rec))
            {
                RecordingLifecycleGate.ClearRecordingTimelineBoundary(group);
                continue;
            }

            var dueAt = rec.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
            var blockNewWorkerAfter = dueAt.AddSeconds(-safetySeconds);
            var protectUntil = rec.StartTime.AddSeconds(RecordingDueEpgSuppressBeforeAfterSec);
            RecordingLifecycleGate.ReplaceRecordingTimelineBoundary(
                group,
                dueAt,
                protectUntil,
                "recording_timeline_due",
                $"R{rec.ReservationId}",
                $"service={SafeValue(rec.ServiceName)} title={ReservationDisplayTitle(rec.Title)}",
                blockNewWorkerAfter);
        }

        var orderedTargets = targets.Values
            .OrderBy(x => x.StartTime)
            .ThenBy(x => x.ReservationId)
            .ToList();
        var signature = orderedTargets.Count == 0
            ? "none"
            : string.Join("|", orderedTargets.Select(x => $"{x.Group}:R{x.ReservationId}:{x.StartTime:HHmmss}"));
        if (!string.Equals(signature, _lastRecordingTimelineGateSignature, StringComparison.Ordinal))
        {
            _log.Add("EPG_RECORDING_TIMELINE_GATE", "EPG",
                $"result=UPDATED count={orderedTargets.Count} safetySec={safetySeconds} horizon={horizon:MM/dd HH:mm:ss} " +
                $"targets=[{(orderedTargets.Count == 0 ? "-" : string.Join(",", orderedTargets.Select(x => $"{x.Group}/R{x.ReservationId}/due={x.StartTime.AddSeconds(-_ini.PreStartMarginSeconds):HH:mm:ss}/start={x.StartTime:HH:mm:ss}")))}] " +
                "scope=all_recording_timeline_events action=replace_or_clear_current_boundary rule=release_contract");
            _lastRecordingTimelineGateSignature = signature;
        }
    }


    private IReadOnlyList<UpcomingRecording> BuildUpcomingRecordingsForEpgGate(
        IEnumerable<Reservation> scheduled,
        DateTime horizon)
    {
        var result = new List<UpcomingRecording>();
        foreach (var r in scheduled)
        {
            var dueStart = r.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
            if (!r.IsEnabled || r.Source == ReservationSource.Epg || dueStart > horizon)
                continue;

            var group = ResolveGroup(r);
            if (group is null)
                continue;

            result.Add(new UpcomingRecording(r.Id, group, r.StartTime, r.ServiceName, r.Title));
        }
        return result;
    }

    private bool IsChainContinuation(Reservation r)
    {
        if (r.IsUserChain) return true;
        if (!_ini.PseudoContinuousRecording) return false;
        try
        {
            return _store.GetChainPredecessors().ContainsKey(r.Id);
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", $"R{r.Id}", $"チェーン継続判定エラー: {ex.Message}");
            return false;
        }
    }


    private int? TryResolveChainPredecessorId(Reservation r, out string source)
    {
        source = "none";
        if (r.UserChainPreviousId.HasValue)
        {
            source = "user_chain_column";
            return r.UserChainPreviousId.Value;
        }

        try
        {
            var predecessors = _store.GetChainPredecessors();
            if (predecessors.TryGetValue(r.Id, out var predId))
            {
                source = "detected_chain_map";
                return predId;
            }
        }
        catch (Exception ex)
        {
            source = "error:" + ex.Message;
        }

        return null;
    }

    internal IReadOnlyList<ActiveRecordingSessionSnapshot> GetActiveRecordingSessions()
    {
        lock (_sessionGate)
        {
            return _activeSessions.Values
                .Where(session => session.IsRecordingCommitted)
                .Select(session => new ActiveRecordingSessionSnapshot(
                    session.OperationId,
                    session.ReservationId,
                    session.ProcessId,
                    session.PlannedEndTime,
                    session.Lease.Name,
                    session.Lease.Did,
                    session.Lease.BonDriverFileName,
                    session.RecordingFilePath,
                    session.FinalizationState,
                    session.Lease.PoolLeaseId,
                    session.Lease.OccupancyGeneration,
                    session.Lease.IsCurrent))
                .ToArray();
        }
    }

    private RecordingSession? TryGetActiveSession(int reservationId)
    {
        lock (_sessionGate)
            return _activeSessions.TryGetValue(reservationId, out var session) ? session : null;
    }

    private bool IsActiveRecordingDidOccupiedByOtherReservation(string group, string did, int reservationId, out string owner)
    {
        lock (_sessionGate)
        {
            foreach (var kv in _activeSessions)
            {
                if (kv.Key == reservationId) continue;
                var s = kv.Value;
                if (!string.Equals(s.Lease.Group, group, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(s.Lease.Did, did, StringComparison.OrdinalIgnoreCase)) continue;
                owner = $"R{kv.Key}/{s.Lease.Name}/{s.Lease.Did}/pid={s.ProcessId}";
                return true;
            }
        }
        owner = "-";
        return false;
    }

    private string FormatActiveSessionsForLog()
    {
        lock (_sessionGate)
        {
            if (_activeSessions.Count == 0) return "none";
            return string.Join(",", _activeSessions.Values
                .OrderBy(x => x.ReservationId)
                .Select(x => $"R{x.ReservationId}/pid={x.ProcessId}/tuner={SafeValue(x.Lease.Name)}/plannedEnd={x.PlannedEndTime:HH:mm:ss}"));
        }
    }

    private void LogChainRecordingEvaluation(Reservation r, string group, bool chainContinuation, string stage)
    {
        try
        {
            var predId = TryResolveChainPredecessorId(r, out var predSource);
            var pred = predId.HasValue ? _store.GetById(predId.Value) : null;
            var activePred = predId.HasValue ? TryGetActiveSession(predId.Value) : null;
            var expectedDue = r.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
            var sameTunerAsPred = pred is not null
                && !string.IsNullOrWhiteSpace(EffectiveTunerName(pred))
                && string.Equals(EffectiveTunerName(pred), EffectiveTunerName(r), StringComparison.OrdinalIgnoreCase);
            _log.Add("CHAIN_RECORDING_EVAL", $"R{r.Id}",
                $"stage={stage} chainContinuation={chainContinuation} pred={(predId.HasValue ? $"R{predId.Value}" : "-")} predSource={predSource} " +
                $"predStatus={(pred?.Status.ToString() ?? "-")} predTuner={SafeValue(pred is null ? null : EffectiveTunerName(pred))} predActive={(activePred is not null)} " +
                $"predPid={(activePred?.ProcessId.ToString() ?? "-")} predLease={(activePred is null ? "-" : SafeValue(activePred.Lease.Name))} " +
                $"sameTunerAsPred={sameTunerAsPred} reservationTuner={SafeValue(EffectiveTunerName(r))} group={group} " +
                $"start={r.StartTime:MM/dd HH:mm:ss} due={expectedDue:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} " +
                $"preStart={_ini.PreStartMarginSeconds}s postEnd={_ini.PostEndMarginSeconds}s activeSessions={FormatActiveSessionsForLog()}");
        }
        catch (Exception ex)
        {
            _log.Add("CHAIN_RECORDING_EVAL", $"R{r.Id}", $"stage={stage} error={ex.Message}");
        }
    }

    private void LogRecordingStopChainContext(RecordingSession session, ReservationStatus finalStatus, string stage)
    {
        try
        {
            var rid = session.ReservationId;
            var predecessors = _store.GetChainPredecessors();
            var successorIds = predecessors
                .Where(kv => kv.Value == rid)
                .Select(kv => kv.Key)
                .OrderBy(x => x)
                .ToList();
            if (successorIds.Count == 0)
            {
                _log.Add("REC_STOP_CHAIN_CONTEXT", $"R{rid}",
                    $"stage={stage} finalStatus={finalStatus} successor=none pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} activeSessions={FormatActiveSessionsForLog()}");
                return;
            }

            foreach (var sid in successorIds)
            {
                var successor = _store.GetById(sid);
                var successorDue = successor?.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
                var expectedNoProcessGapMs = successor is null
                    ? -1
                    : Math.Max(0, (successorDue!.Value - DateTime.Now).TotalMilliseconds);
                _log.Add("REC_STOP_CHAIN_CONTEXT", $"R{rid}",
                    $"stage={stage} finalStatus={finalStatus} successor=R{sid} succStatus={(successor?.Status.ToString() ?? "-")} " +
                    $"succStart={(successor is null ? "-" : successor.StartTime.ToString("MM/dd HH:mm:ss"))} succDue={(successorDue.HasValue ? successorDue.Value.ToString("MM/dd HH:mm:ss") : "-")} " +
                    $"succTuner={SafeValue(successor is null ? null : EffectiveTunerName(successor))} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} " +
                    $"preStart={_ini.PreStartMarginSeconds}s expectedGapFromNowMs={expectedNoProcessGapMs:F0} activeSessions={FormatActiveSessionsForLog()}");
            }
        }
        catch (Exception ex)
        {
            _log.Add("REC_STOP_CHAIN_CONTEXT", $"R{session.ReservationId}", $"stage={stage} error={ex.Message}");
        }
    }

    /// <summary>予約の正規チャンネル／チューナーIdentityから放送波グループを解決する。</summary>
    private string? ResolveGroup(Reservation r)
        => ReservationTunerGroupResolver.Resolve(r, _tunerProfiles);

    private List<Reservation> OrderDueReservationsForTransportBatchLaunch(List<Reservation> reservations)
    {
        if (reservations.Count <= 2)
            return reservations;

        var indexed = reservations.Select((reservation, index) => new { reservation, index }).ToList();
        var ordered = new List<Reservation>(reservations.Count);

        foreach (var startGroup in indexed.GroupBy(x => x.reservation.StartTime).OrderBy(g => g.Key))
        {
            var transportGroups = startGroup
                .GroupBy(x => BuildTransportBatchKey(x.reservation), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Min(x => x.index));

            foreach (var transportGroup in transportGroups)
            {
                foreach (var item in transportGroup.OrderBy(x => x.index))
                    ordered.Add(item.reservation);
            }
        }

        var before = string.Join(",", reservations.Select(r => $"R{r.Id}:{BuildTransportBatchKey(r)}"));
        var after = string.Join(",", ordered.Select(r => $"R{r.Id}:{BuildTransportBatchKey(r)}"));
        if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            _log.Add("REC_MULTI_START_BATCH", "Order",
                $"result=REORDERED count={ordered.Count} before={before} after={after} rule=release_contract");
        }

        return ordered;
    }



    private string BuildTransportBatchKey(Reservation r)
    {
        return $"{ResolveGroup(r) ?? "-"}:{r.NetworkId}:{r.TransportStreamId}";
    }

    private bool IsActiveRecordingSessionSourceOfTruth(int reservationId, out RecordingSession? activeSession)
    {
        lock (_sessionGate)
            _activeSessions.TryGetValue(reservationId, out activeSession);

        if (activeSession is null || !activeSession.IsRecordingCommitted)
            return false;

        if (DateTime.Now >= activeSession.PlannedEndTime)
            return false;

        return IsOwnedRecordingWorkerAlive(activeSession);
    }

    private async Task<RecordingAbortCleanupResult> AbortUncommittedRecordingStartAsync(RecordingSession session)
    {
        if (!session.TryBeginAbortCleanup())
        {
            // 正式録画昇格が先行した場合は、そのworkerをabortしてはならない。
            if (session.IsRecordingCommitted || session.IsRecordingCommitInProgress)
                return new RecordingAbortCleanupResult(false, false, false);

            return await session.AbortCleanupCompletion.ConfigureAwait(false);
        }

        lock (_sessionGate)
        {
            if (_activeSessions.TryGetValue(session.ReservationId, out var current)
                && ReferenceEquals(current, session))
            {
                _activeSessions.Remove(session.ReservationId);
            }
        }

        var result = new RecordingAbortCleanupResult(false, false, false);
        try
        {
            var stopped = false;
            try { stopped = await TryStopTvAIrEpgRecProcessAsync(session, session.ReservationId, TvAIrEpgRecStopTimeoutMs).ConfigureAwait(false); } catch { }
            if (!stopped)
            {
                try { await KillProcessTreeAsync(session.ProcessId, session.ReservationId).ConfigureAwait(false); } catch { }
            }

            var workerGone = !IsOwnedRecordingWorkerAlive(session);
            var activityReleased = false;
            var leaseReleased = false;
            try
            {
                session.ActivityHandle?.Dispose();
                activityReleased = true;
            }
            catch { }
            try
            {
                session.Lease.Dispose();
            }
            catch { }
            // LEASE_RELEASE_COMPLETION_INVARIANT:
            // Disposeが例外なく戻ったかではなく、Pool正本から同じidentityが消えたかを正とする。
            // Release例外後にidentityが残る場合はfalseとなり、後続の再投入を安全側へ抑止できる。
            try
            {
                leaseReleased = !session.Lease.IsIdentityCurrent;
            }
            catch
            {
                leaseReleased = false;
            }

            result = new RecordingAbortCleanupResult(workerGone, activityReleased, leaseReleased);
            if (!workerGone)
                RegisterResidualRecordingWorkerQuarantine(session);
            if (!leaseReleased)
                RegisterRecordingResourceReleaseQuarantine(session);

            _log.Add("REC_START_ABORT_CLEANUP", $"R{session.ReservationId}",
                $"result={(result.Complete ? "COMPLETE" : "PARTIAL_FAILURE")} pid={session.ProcessId} gracefulStop={stopped} " +
                $"workerGone={result.WorkerIdentityGone} activityHandleReleased={result.ActivityHandleReleased} leaseReleaseCompleted={result.LeaseReleaseCompleted} " +
                $"tuner={SafeValue(session.Lease.Name)} did={session.Lease.Did} poolLeaseId={session.Lease.PoolLeaseId} generation={session.Lease.OccupancyGeneration} " +
                "rule=recording_lifecycle_cas_contract");
            return result;
        }
        catch (Exception ex)
        {
            result = new RecordingAbortCleanupResult(false, false, false);
            RegisterResidualRecordingWorkerQuarantine(session);
            RegisterRecordingResourceReleaseQuarantine(session);
            _log.Add("REC_START_ABORT_CLEANUP", $"R{session.ReservationId}",
                $"result=PARTIAL_FAILURE pid={session.ProcessId} exceptionType={ex.GetType().Name} error={TrimForLog(ex.Message, 180)} " +
                "action=keep_starting_and_quarantine_did rule=recording_lifecycle_cas_contract");
            return result;
        }
        finally
        {
            session.CompleteAbortCleanup(result);
        }
    }

    private static string BuildResidualRecordingWorkerKey(string group, string did)
        => $"{group?.Trim().ToUpperInvariant()}|{did?.Trim().ToUpperInvariant()}";

    private void RegisterResidualRecordingWorkerQuarantine(RecordingSession session)
    {
        var key = BuildResidualRecordingWorkerKey(session.Lease.Group, session.Lease.Did);
        var entry = new ResidualRecordingWorkerQuarantine(
            session.ReservationId,
            session.Lease.Group,
            session.Lease.Did,
            session.Lease.Name,
            session.WorkerIdentity,
            DateTime.Now);
        _residualRecordingWorkers[key] = entry;
        _log.Add("RESIDUAL_RECORDING_WORKER_GUARD", $"R{session.ReservationId}",
            $"result=QUARANTINED group={entry.Group} tuner={entry.TunerName} did={entry.Did} pid={session.ProcessId} detectedAt={entry.DetectedAt:O} " +
            "action=deny_did_reuse_until_worker_identity_disappears rule=recording_worker_identity_guard_contract");
    }

    private bool IsResidualRecordingWorkerDidQuarantined(string group, string did, out string owner)
    {
        owner = string.Empty;
        var key = BuildResidualRecordingWorkerKey(group, did);
        if (!_residualRecordingWorkers.TryGetValue(key, out var entry))
            return false;

        var observed = TvAirManagedProcessRegistry.CaptureIdentity(entry.WorkerIdentity.ProcessId);
        if (TvAirManagedProcessRegistry.IdentityMatches(entry.WorkerIdentity, observed))
        {
            owner = $"R{entry.ReservationId}/pid={entry.WorkerIdentity.ProcessId}/tuner={entry.TunerName}";
            return true;
        }

        if (((ICollection<KeyValuePair<string, ResidualRecordingWorkerQuarantine>>)_residualRecordingWorkers)
            .Remove(new KeyValuePair<string, ResidualRecordingWorkerQuarantine>(key, entry)))
        {
            _log.Add("RESIDUAL_RECORDING_WORKER_GUARD", $"R{entry.ReservationId}",
                $"result=RELEASED group={entry.Group} tuner={entry.TunerName} did={entry.Did} reason=worker_identity_no_longer_alive " +
                "action=allow_future_did_reuse rule=recording_worker_identity_guard_contract");
        }
        return false;
    }

    private void ReleaseRejectedRecordingStartLease(int reservationId, TunerLease lease, string reason)
    {
        Exception? releaseError = null;
        try
        {
            lease.Dispose();
        }
        catch (Exception ex)
        {
            releaseError = ex;
        }

        var identityStillCurrent = true;
        try
        {
            identityStillCurrent = lease.IsIdentityCurrent;
        }
        catch
        {
            // Pool正本を観測できない場合は、安全側にlease identity残存として扱う。
            identityStillCurrent = true;
        }

        _log.Add("REC_START_REJECTED_LEASE_RELEASE", $"R{reservationId}",
            $"result={(identityStillCurrent ? "INCOMPLETE" : "RELEASED")} reason={reason} tuner={lease.Name} did={lease.Did} " +
            $"poolLeaseId={lease.PoolLeaseId} generation={lease.OccupancyGeneration} " +
            $"disposeException={(releaseError is null ? "-" : releaseError.GetType().Name)} " +
            "source=recording_start_rejection rule=recording_resource_release_guard_contract");

        if (identityStillCurrent)
            RegisterRecordingResourceReleaseQuarantine(reservationId, lease);
    }

    private void RegisterRecordingResourceReleaseQuarantine(RecordingSession session)
        => RegisterRecordingResourceReleaseQuarantine(session.ReservationId, session.Lease);

    private void RegisterRecordingResourceReleaseQuarantine(int reservationId, TunerLease lease)
    {
        var key = BuildResidualRecordingWorkerKey(lease.Group, lease.Did);
        var entry = new RecordingResourceReleaseQuarantine(
            reservationId,
            lease.Group,
            lease.Did,
            lease.Name,
            lease,
            DateTime.Now);
        _recordingResourceReleaseQuarantines[key] = entry;
        _log.Add("RECORDING_RESOURCE_RELEASE_GUARD", $"R{reservationId}",
            $"result=QUARANTINED group={entry.Group} tuner={entry.TunerName} did={entry.Did} " +
            $"poolLeaseId={entry.Lease.PoolLeaseId} generation={entry.Lease.OccupancyGeneration} detectedAt={entry.DetectedAt:O} " +
            "action=deny_did_reuse_until_pool_lease_identity_disappears rule=recording_resource_release_guard_contract");
    }

    private bool IsRecordingResourceReleaseDidQuarantined(string group, string did, out string owner)
    {
        owner = string.Empty;
        var key = BuildResidualRecordingWorkerKey(group, did);
        if (!_recordingResourceReleaseQuarantines.TryGetValue(key, out var entry))
            return false;

        var identityStillCurrent = true;
        try
        {
            identityStillCurrent = entry.Lease.IsIdentityCurrent;
        }
        catch
        {
            // Pool identityを観測できない場合は安全側に隔離を維持する。
            identityStillCurrent = true;
        }

        if (identityStillCurrent)
        {
            owner = $"R{entry.ReservationId}/lease={entry.Lease.PoolLeaseId}/generation={entry.Lease.OccupancyGeneration}/tuner={entry.TunerName}";
            return true;
        }

        if (((ICollection<KeyValuePair<string, RecordingResourceReleaseQuarantine>>)_recordingResourceReleaseQuarantines)
            .Remove(new KeyValuePair<string, RecordingResourceReleaseQuarantine>(key, entry)))
        {
            _log.Add("RECORDING_RESOURCE_RELEASE_GUARD", $"R{entry.ReservationId}",
                $"result=RELEASED group={entry.Group} tuner={entry.TunerName} did={entry.Did} " +
                $"poolLeaseId={entry.Lease.PoolLeaseId} generation={entry.Lease.OccupancyGeneration} reason=pool_lease_identity_no_longer_current " +
                "action=allow_future_did_reuse rule=recording_resource_release_guard_contract");
        }
        return false;
    }


    private void SettleClaimedRecordingLaunchFailure(
        Reservation r,
        string reason,
        bool retryableChainStart,
        DirectRecorderStartupFailureKind startupFailureKind = DirectRecorderStartupFailureKind.None,
        bool tunerOpenedBeforeFailure = false)
    {
        // CHAIN_START_TRANSIENT_FAILURE_RETRY_INVARIANT:
        // チェーン境界のTuner取得・DID占有・worker起動失敗は、物理解放やDataVersion競合の一時状態を含む。
        // Starting→Failedへ終端すると500ms境界retryが停止するため、worker未成立を確認した入口ではScheduledへ戻す。
        // チャンネル未設定・視聴専用DIDなど恒久設定不良はこの入口を使わず、従来どおりFailで終端する。
        var latest = _store.GetById(r.Id);
        if (latest?.Status != ReservationStatus.Starting)
        {
            _log.Add("REC_START_FAILURE_SETTLE", $"R{r.Id}",
                $"result=SKIPPED reason=status_changed currentStatus={latest?.Status.ToString() ?? "missing"} " +
                $"retryableChainStart={retryableChainStart} originalReason={TrimForLog(reason, 180)} rule=recording_lifecycle_cas_contract");
            return;
        }

        var interruptedAfterOpen = startupFailureKind == DirectRecorderStartupFailureKind.WorkerExited && tunerOpenedBeforeFailure;
        var failureKind = interruptedAfterOpen ? "worker_exited_after_open_before_recording" : null;
        var settle = retryableChainStart || !latest.IsEnabled
            ? _store.TryRollbackRecordingStart(r.Id, latest.DataVersion)
            : _store.TryFailRecordingStart(r.Id, latest.DataVersion, reason, failureKind);
        _log.Add("REC_START_FAILURE_SETTLE", $"R{r.Id}",
            $"result={(settle.Applied ? "APPLIED" : "REJECTED")} target={(retryableChainStart || !latest.IsEnabled ? "Scheduled" : "Failed")} " +
            $"retryableChainStart={retryableChainStart} reason={TrimForLog(reason, 180)} detail={SafeValue(settle.Reason)} " +
            $"failureKind={SafeValue(failureKind)} workerFailureKind={startupFailureKind} tunerOpenedBeforeFailure={tunerOpenedBeforeFailure} " +
            $"dataVersion={settle.PreviousDataVersion}->{settle.CurrentDataVersion} rule=recording_lifecycle_cas_contract");
        if (settle.Applied)
            _forceAllocationReevaluate = true;
    }

    private void Fail(Reservation r, string reason)
    {
        var starting = _store.GetById(r.Id);
        if (starting?.Status == ReservationStatus.Starting)
        {
            var settle = starting.IsEnabled
                ? _store.TryFailRecordingStart(r.Id, starting.DataVersion, reason)
                : _store.TryRollbackRecordingStart(r.Id, starting.DataVersion);
            _log.Add("REC_FAIL", $"R{r.Id}",
                $"reason={reason} lifecycleFrom=Starting lifecycleTarget={(starting.IsEnabled ? "Failed" : "Scheduled")} " +
                $"casApplied={settle.Applied} casReason={SafeValue(settle.Reason)} dataVersion={settle.PreviousDataVersion}->{settle.CurrentDataVersion} " +
                FormatReservationForAudit(settle.Reservation ?? starting, "start_failure_settled"));
            if (settle.Applied)
            {
                _forceAllocationReevaluate = true;
            }
            return;
        }
        if (IsActiveRecordingSessionSourceOfTruth(r.Id, out var activeSession))
        {
            var current = _store.GetById(r.Id);
            ReservationLifecycleTransitionResult? restore = null;
            if (current?.Status == ReservationStatus.Failed)
                restore = _store.TryRestoreFailedRecordingRuntime(r.Id, current.DataVersion);

            _log.Add("REC_FAIL_SUPPRESSED_ACTIVE_SESSION", $"R{r.Id}",
                $"result=KEEP_RECORDING reason=active_tvairepgrec_session_is_source_of_truth pid={activeSession!.ProcessId} " +
                $"tuner={SafeValue(activeSession.Lease.Name)} plannedEnd={activeSession.PlannedEndTime:MM/dd HH:mm:ss} originalReason={TrimForLog(reason, 180)} " +
                $"restore={(restore is null ? "not_required" : restore.Applied ? "applied" : "rejected")} restoreReason={(restore is null ? "-" : SafeValue(restore.Reason))} " +
                $"dataVersion={(restore is null ? "-" : $"{restore.PreviousDataVersion}->{restore.CurrentDataVersion}")} rule=recording_lifecycle_cas_contract " + FormatReservationForAudit(r, "fail_suppressed_active_session"));
            return;
        }

        var latest = _store.GetById(r.Id);
        if (IsInUnfinishedRecordingWindow(latest))
        {
            _log.Add("REC_FAIL_SUPPRESSED_RECORDING_WINDOW", $"R{r.Id}",
                $"result=KEEP_EXISTING_STATE reason=recording_started_without_finished_at originalReason={TrimForLog(reason, 180)} " +
                $"status={latest!.Status} start={latest.StartTime:MM/dd HH:mm:ss} end={latest.EndTime:MM/dd HH:mm:ss} " +
                $"recordingStartedAt={latest.RecordingStartedAt:MM/dd HH:mm:ss} tuner={SafeValue(EffectiveReservationTuner(latest))} " +
                $"rule=release_contract " + FormatReservationForAudit(latest, "fail_suppressed_recording_window"));
            return;
        }

        _log.Add("REC_FAIL", $"R{r.Id}", $"reason={reason} " + FormatReservationForAudit(r, "fail"));
        if (latest is null)
        {
            _log.Add("REC_FAIL_TERMINAL_CAS", $"R{r.Id}",
                "result=REJECTED reason=not_found action=do_not_force_terminal_state rule=recording_lifecycle_cas_contract");
            return;
        }

        // RECORDING_FAILURE_SINGLE_TERMINAL_OWNER_INVARIANT:
        // 開始失敗・worker異常・停止処理は各ライフサイクルCASが所有する。
        // ここからUpdateStatus(force:true)で新しい状態や世代を上書きしてはならない。
        ReservationLifecycleTransitionResult? terminal = latest.Status switch
        {
            ReservationStatus.Recording => _store.TryFinalizeRecordingRuntimeFailure(latest.Id, latest.DataVersion, DateTime.Now, reason),
            ReservationStatus.Starting => _store.TryFailRecordingStart(latest.Id, latest.DataVersion, reason),
            ReservationStatus.Scheduled => _store.TryFinalizeScheduledReservation(latest.Id, latest.DataVersion, ReservationStatus.Failed, "recording_failure", reason),
            _ => null
        };

        if (terminal is null)
        {
            _log.Add("REC_FAIL_TERMINAL_CAS", $"R{r.Id}",
                $"result=REJECTED from={latest.Status} reason=terminal_or_owned_by_other_lifecycle action=preserve_newer_state rule=recording_lifecycle_cas_contract");
            return;
        }

        _log.Add("REC_FAIL_TERMINAL_CAS", $"R{r.Id}",
            $"result={(terminal.Applied ? "APPLIED" : "REJECTED")} from={latest.Status} reason={SafeValue(terminal.Reason)} dataVersion={terminal.PreviousDataVersion}->{terminal.CurrentDataVersion} action={(terminal.Applied ? "terminal_committed" : "preserve_newer_state")} rule=recording_lifecycle_cas_contract");
        if (terminal.Applied)
        {
            var failedSuccessors = _store.FailScheduledUserChainSuccessorsAfterPredecessorFailure(r.Id, reason);
            _forceAllocationReevaluate = true;
            _log.Add("Scheduler", $"R{r.Id}",
                $"録画失敗: [{r.Title}] {reason} chainSuccessorsFailed={failedSuccessors.Count}");
        }
    }

    private static bool IsInUnfinishedRecordingWindow(Reservation? r)
    {
        if (r is null) return false;
        if (!r.RecordingStartedAt.HasValue) return false;
        if (r.RecordingFinishedAt.HasValue) return false;
        return DateTime.Now < r.EndTime.AddMinutes(2);
    }

    private static string EffectiveReservationTuner(Reservation r)
        => !string.IsNullOrWhiteSpace(r.ActualTunerName) ? r.ActualTunerName : (r.TunerName ?? string.Empty);

    private void AuditReservationsAroundNow(DateTime now, string context, bool force)
    {
        if (!force)
        {
            _reservationAuditCounter++;
            if (_reservationAuditCounter < ReservationAuditIntervalTicks)
                return;
            _reservationAuditCounter = 0;
        }

        try
        {
            var from = now.AddHours(-2);
            var to = now.AddHours(2);
            var reservations = _store.GetAll()
                .Where(r => r.Source != ReservationSource.Epg)
                .Where(r => r.StartTime <= to && r.EndTime >= from)
                .OrderBy(r => r.StartTime)
                .ThenBy(r => r.Id)
                .ToList();

            var activeReservations = new List<(Reservation Reservation, string ExecutionState)>();
            var terminalPastReservations = new List<(Reservation Reservation, string ExecutionState)>();

            foreach (var r in reservations)
            {
                var dueStart = r.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
                var executionState = r.Status switch
                {
                    ReservationStatus.Scheduled when !r.IsEnabled => "not_due_disabled",
                    ReservationStatus.Scheduled when r.IsConflicted => now >= dueStart ? "due_but_conflicted" : "future_conflicted",
                    ReservationStatus.Scheduled when now >= dueStart && now <= r.EndTime => "due_or_recording_window",
                    ReservationStatus.Scheduled when now > r.EndTime => "missed_still_scheduled",
                    ReservationStatus.Scheduled => "future_scheduled",
                    ReservationStatus.Recording => "recording",
                    ReservationStatus.Completed => "completed",
                    ReservationStatus.Cancelled => "cancelled",
                    ReservationStatus.Failed => "failed",
                    _ => r.Status.ToString()
                };

                if (IsPastTerminalAuditReservation(r, now))
                    terminalPastReservations.Add((r, executionState));
                else
                    activeReservations.Add((r, executionState));
            }

            _log.Add("RESERVATION_AUDIT", context,
                $"window={from:MM/dd HH:mm:ss}〜{to:MM/dd HH:mm:ss} count={reservations.Count} visible={activeReservations.Count} suppressedPastTerminal={terminalPastReservations.Count} now={now:MM/dd HH:mm:ss} note=non_epg_reservations_only rule=release_contract");

            foreach (var item in activeReservations)
            {
                var r = item.Reservation;
                _log.Add("RESERVATION_AUDIT", $"{TrimForLog(r.ServiceName, 24)}", FormatReservationForAudit(r, item.ExecutionState));
            }

            if (terminalPastReservations.Count > 0)
            {
                var completed = terminalPastReservations.Count(x => x.Reservation.Status == ReservationStatus.Completed);
                var cancelled = terminalPastReservations.Count(x => x.Reservation.Status == ReservationStatus.Cancelled);
                var failed = terminalPastReservations.Count(x => x.Reservation.Status == ReservationStatus.Failed);
                var samples = string.Join(";", terminalPastReservations
                    .OrderByDescending(x => x.Reservation.UpdatedAt)
                    .ThenByDescending(x => x.Reservation.Id)
                    .Take(3)
                    .Select(x => $"R{x.Reservation.Id}:{TrimForLog(x.Reservation.ServiceName, 18)}:{x.Reservation.Status}"));
                var signature = $"{context}:{terminalPastReservations.Count}:{completed}:{cancelled}:{failed}:{samples}";
                var nowUtc = DateTime.UtcNow;
                if (!string.Equals(_lastPastTerminalAuditSignature, signature, StringComparison.Ordinal)
                    || nowUtc - _lastPastTerminalAuditLogUtc >= TimeSpan.FromMinutes(10))
                {
                    _lastPastTerminalAuditSignature = signature;
                    _lastPastTerminalAuditLogUtc = nowUtc;
                    _log.Add("RESERVATION_AUDIT", $"{context}_PAST_TERMINAL_SUMMARY",
                        $"suppressed={terminalPastReservations.Count} completed={completed} cancelled={cancelled} failed={failed} sample=diagnostic_only rule=reservation_audit_terminal_summary_release_trim");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Add("RESERVATION_AUDIT", context, $"audit_error={ex.Message}");
        }
    }


    private static bool IsPastTerminalAuditReservation(Reservation r, DateTime now)
    {
        if (r.EndTime >= now)
            return false;

        return r.Status == ReservationStatus.Completed
            || r.Status == ReservationStatus.Cancelled
            || r.Status == ReservationStatus.Failed;
    }

    private string FormatReservationForAudit(Reservation r, string state)
    {
        var dueStart = r.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
        return $"service={TrimForLog(r.ServiceName, 40)} title={ReservationDisplayTitle(r.Title, 60)} state={state} id=R{r.Id} status={r.Status} source={r.Source} enabled={r.IsEnabled} conflicted={r.IsConflicted} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} dueStart={dueStart:MM/dd HH:mm:ss} svcId={r.ServiceId} group={ResolveGroup(r) ?? "-"} tuner={SafeValue(EffectiveTunerName(r))} ch={SafeValue(r.ChannelArgument)} userChain={r.IsUserChain} chainPrev={(r.UserChainPreviousId.HasValue ? $"R{r.UserChainPreviousId.Value}" : "-")} chainRoot={(r.UserChainRootId.HasValue ? $"R{r.UserChainRootId.Value}" : "-")} created={r.CreatedAt:MM/dd HH:mm:ss} updated={r.UpdatedAt:MM/dd HH:mm:ss} rule=release_contract";
    }


    private static string BuildRecordingChannelArgumentWithServiceIdentity(string channelArgument, ushort networkId, ushort transportStreamId, ushort serviceId)
    {
        if (string.IsNullOrWhiteSpace(channelArgument)) return string.Empty;

        var tokens = channelArgument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!tokens.Any(t => string.Equals(t, "/chspace", StringComparison.OrdinalIgnoreCase)))
            return channelArgument.Trim();

        var kept = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (string.Equals(token, "/nid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "/tsid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "/sid", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < tokens.Length) i++;
                continue;
            }

            kept.Add(token);
        }

        var core = string.Join(' ', kept).Trim();
        if (networkId == 0 || transportStreamId == 0 || serviceId == 0)
            return core;

        return $"{core} /nid {networkId} /tsid {transportStreamId} /sid {serviceId}";
    }

    private InterruptedRecordingFileProbe ProbeInterruptedRecordingFile(Reservation r)
    {
        try
        {
            if (!TvTestRecordingDirectoryResolver.TryResolve(_ini.TvTestExecutablePath, out var directory, out var evidence))
                return new InterruptedRecordingFileProbe(false, $"result=folder_unresolved evidence={TrimForLog(evidence, 420)} rule=release_contract");

            var namingPolicy = ResolveDirectRecorderFileNameTimePolicy(r, r.RecordingStartedAt ?? r.StartTime);
            var expectedName = BuildDirectRecorderFileName(r, namingPolicy.BaseTime).FileName;
            var candidates = new List<string>();
            if (Directory.Exists(directory))
            {
                // INTERRUPTED_RECORDING_FILE_LINEAGE_INVARIANT:
                // 自動再開は同じTVTest命名正本から (1)/(2)... を生成するため、exactだけを優先すると
                // 2回目以降の中断で古い先頭segmentを誤参照する。常に同名系列を列挙し、最新segmentを証拠正本にする。
                var baseName = Path.GetFileNameWithoutExtension(expectedName);
                candidates.AddRange(Directory.EnumerateFiles(directory, baseName + "*.ts")
                    .OrderByDescending(File.GetLastWriteTime)
                    .Take(3));
            }

            if (candidates.Count == 0)
                return new InterruptedRecordingFileProbe(false, $"result=no_file folder={SafeValue(directory)} expected={SafeValue(expectedName)} start={r.StartTime:yyyy-MM-dd HH:mm:ss} end={r.EndTime:yyyy-MM-dd HH:mm:ss} rule=release_contract");

            var details = candidates.Select(path =>
            {
                try
                {
                    var fi = new FileInfo(path);
                    return $"file={SafeValue(fi.Name)} bytes={fi.Length} lastWrite={fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
                }
                catch (Exception ex)
                {
                    return $"file={SafeValue(Path.GetFileName(path))} statError={SafeValue(ex.GetType().Name)}";
                }
            });

            return new InterruptedRecordingFileProbe(true, $"result=partial_file_detected folder={SafeValue(directory)} expected={SafeValue(expectedName)} {string.Join(" | ", details)} reservation={r.StartTime:yyyy-MM-dd HH:mm:ss}〜{r.EndTime:yyyy-MM-dd HH:mm:ss} rule=release_contract");
        }
        catch (Exception ex)
        {
            return new InterruptedRecordingFileProbe(false, $"result=audit_error type={ex.GetType().Name} message={TrimForLog(ex.Message, 240)} rule=release_contract");
        }
    }

    private string ResolveReservationRecordFolder(Reservation r)
    {
        // release_contract: BS/CSはBridgeでTVTest内部currentSid確定後にTVTest標準録画を開始する。
        // 停止時はBridge RecordStop待ちを行わず、STOP_PHASE/TUNER_DEVICE_LOCKを詰まらせないTvAIr所有PID単体終了へ戻す。
        // 通常予約録画ではTVTest.ini の RecordFolder/RecordFileName に準拠する。
        // TvAIrEpgRec直録画では、RecordFileName をTvAIr側で録画用の相対パスへ展開する。
        if (!TvTestRecordingDirectoryResolver.TryResolve(_ini.TvTestExecutablePath, out var directory, out var evidence))
        {
            _log.Add("RECORD_FOLDER_RESOLVE", $"R{r.Id}", $"result=FAIL evidence={TrimForLog(evidence, 420)}");
            _log.Add("RECORD_FILE_PATH_BUILD", $"R{r.Id}", "result=FAIL reason=record_folder_unresolved route=TvAIrEpgRecTemplateBuild");
            return string.Empty;
        }

        _log.Add("RECORD_FOLDER_RESOLVE", $"R{r.Id}", $"result=OK folder={directory} evidence={TrimForLog(evidence, 420)}");
        _log.Add("RECORD_FILE_PATH_BUILD", $"R{r.Id}", "result=OK reason=tvairepgrec_uses_recordfilename_ini source=TVTest.RecordFileName route=TvAIrEpgRecTemplateBuild rule=recordfilename_ini_template");
        return directory;
    }

    private static string EffectiveTunerName(Reservation r)
        => !string.IsNullOrWhiteSpace(r.ActualTunerName) ? r.ActualTunerName : r.TunerName;


    private sealed record RuntimeStatsSample(
        string At,
        DateTime? AtTime,
        long RawPackets,
        long RawContinuityDrops,
        long RawContinuityErrors,
        long RawContinuityGapEvents,
        long RawSameCcContentMismatches,
        long RawDuplicatePackets,
        long RawDiscontinuityResets,
        long RawTransportErrors,
        long RawSyncErrors,
        long RawScrambledPackets,
        long OutputPackets,
        long OutputContinuityDrops,
        long OutputContinuityErrors,
        long OutputContinuityGapEvents,
        long OutputSameCcContentMismatches,
        long OutputDuplicatePackets,
        long OutputDiscontinuityResets,
        long OutputTransportErrors,
        long OutputSyncErrors,
        long OutputScrambledPackets,
        long BytesWritten);

    private sealed record RuntimeStatsDelta(
        RuntimeStatsSample Sample,
        long RawDropDelta,
        long RawCcDelta,
        long RawGapDelta,
        long RawSameCcMismatchDelta,
        long OutputDropDelta,
        long OutputCcDelta,
        long OutputGapDelta,
        long OutputSameCcMismatchDelta,
        long OutputSyncDelta,
        long BytesDelta);

    private sealed record ActiveGroupContext(string Group, int Count, string Names, string Dids);

    private void LogDirectRecorderRuntimeTimeline(int rid, string service, string title, string group, DirectRecorderOutcome outcome)
    {
        var context = BuildActiveRecordingGroupContext(group);
        var samples = ReadRuntimeStatsSamples(outcome.RuntimeStatsPath, 4096);
        if (samples.Count == 0)
        {
            _log.Add("REC_DROP_TIMELINE_SUMMARY", $"R{rid}",
                $"service={SafeValue(service)} title={TrimForLog(title, 80)} verdict={SafeValue(outcome.QualityVerdict)} " +
                $"samples=0 reason=runtime_stats_missing_or_empty path={SafeValue(outcome.RuntimeStatsPath)} " +
                $"group={SafeValue(context.Group)} groupRecordingCount={context.Count} groupTuners={SafeValue(context.Names)} groupDids={SafeValue(context.Dids)} " +
                $"rule=release_contract");
            return;
        }

        var deltas = BuildRuntimeStatsDeltas(samples);
        RuntimeStatsDelta? firstDrop = deltas.FirstOrDefault(d => d.RawDropDelta > 0 || d.RawCcDelta > 0 || d.OutputDropDelta > 0 || d.OutputCcDelta > 0 || d.OutputSyncDelta > 0);
        RuntimeStatsDelta? lastDrop = deltas.LastOrDefault(d => d.RawDropDelta > 0 || d.RawCcDelta > 0 || d.OutputDropDelta > 0 || d.OutputCcDelta > 0 || d.OutputSyncDelta > 0);
        RuntimeStatsDelta maxOutputDrop = deltas.OrderByDescending(d => d.OutputDropDelta).ThenByDescending(d => d.OutputCcDelta).First();
        RuntimeStatsDelta maxRawDrop = deltas.OrderByDescending(d => d.RawDropDelta).ThenByDescending(d => d.RawCcDelta).First();
        var first = samples.First();
        var final = samples.Last();
        var secondsToFirstDrop = (firstDrop?.Sample.AtTime is DateTime fd && first.AtTime is DateTime st) ? (int)Math.Round((fd - st).TotalSeconds) : -1;
        var dropWithinFirst30s = secondsToFirstDrop >= 0 && secondsToFirstDrop <= 30;
        var dropActive = final.RawContinuityDrops > 0 || final.RawContinuityErrors > 0 || final.OutputContinuityDrops > 0 || final.OutputContinuityErrors > 0 || final.OutputSyncErrors > 0;

        var timelineClassification = ClassifyDropTimeline(outcome, samples, deltas, firstDrop, lastDrop, secondsToFirstDrop);
        _log.Add("REC_DROP_TIMELINE_SUMMARY", $"R{rid}",
            $"service={SafeValue(service)} title={TrimForLog(title, 80)} timelineClass={timelineClassification.ClassName} phase={timelineClassification.Phase} severity={timelineClassification.Severity} userMeaning={timelineClassification.UserMeaning} action={timelineClassification.Action} verdict={SafeValue(outcome.QualityVerdict)} " +
            $"samples={samples.Count} firstAt={SafeValue(first.At)} firstDropAt={SafeValue(firstDrop?.Sample.At)} lastDropAt={SafeValue(lastDrop?.Sample.At)} " +
            $"secondsToFirstDrop={(secondsToFirstDrop >= 0 ? secondsToFirstDrop.ToString() : "-")} dropWithinFirst30s={dropWithinFirst30s} dropActive={dropActive} " +
            $"maxOutputDropDelta={maxOutputDrop.OutputDropDelta} maxOutputDropAt={SafeValue(maxOutputDrop.Sample.At)} maxOutputCcDelta={maxOutputDrop.OutputCcDelta} " +
            $"maxRawDropDelta={maxRawDrop.RawDropDelta} maxRawDropAt={SafeValue(maxRawDrop.Sample.At)} maxRawCcDelta={maxRawDrop.RawCcDelta} " +
            $"finalInputRawDrops={final.RawContinuityDrops} finalInputRawCcErrors={final.RawContinuityErrors} finalInputRawSyncErrors={final.RawSyncErrors} finalInputRawScrambled={final.RawScrambledPackets} " +
            $"finalOutputDrops={final.OutputContinuityDrops} finalOutputCcErrors={final.OutputContinuityErrors} finalOutputSyncErrors={final.OutputSyncErrors} finalOutputScrambled={final.OutputScrambledPackets} finalBytesWritten={final.BytesWritten} " +
            $"rawLayerMeaning=pre_write_input_observation outputLayerMeaning=recorded_file_integrity group={SafeValue(context.Group)} groupRecordingCount={context.Count} groupTuners={SafeValue(context.Names)} groupDids={SafeValue(context.Dids)} runtimeStatsPath={SafeValue(outcome.RuntimeStatsPath)} " +
            $"rule=release_contract");

        var selected = new List<(string Label, RuntimeStatsDelta Delta)>();
        selected.Add(("first", deltas.First()));
        if (firstDrop is not null) selected.Add(("first_drop", firstDrop));
        selected.Add(("max_output_delta", maxOutputDrop));
        selected.Add(("max_raw_delta", maxRawDrop));
        if (lastDrop is not null) selected.Add(("last_drop", lastDrop));
        selected.Add(("final", deltas.Last()));

        foreach (var item in selected
            .GroupBy(x => $"{x.Label}:{x.Delta.Sample.At}:{x.Delta.Sample.OutputContinuityDrops}:{x.Delta.Sample.RawContinuityDrops}")
            .Select(g => g.First())
            .Take(8))
        {
            var d = item.Delta;
            var x = d.Sample;
            _log.Add("TVAIREPGREC_RUNTIME_STATS", $"R{rid}",
                $"sample={item.Label} at={SafeValue(x.At)} service={SafeValue(service)} title={TrimForLog(title, 60)} " +
                $"inputRawPackets={x.RawPackets} inputRawDrops={x.RawContinuityDrops} inputRawDropDelta={d.RawDropDelta} inputRawCcErrors={x.RawContinuityErrors} inputRawCcDelta={d.RawCcDelta} inputRawGapEvents={x.RawContinuityGapEvents} inputRawGapDelta={d.RawGapDelta} inputRawSameCcMismatch={x.RawSameCcContentMismatches} inputRawSameCcMismatchDelta={d.RawSameCcMismatchDelta} inputRawDuplicates={x.RawDuplicatePackets} inputRawDiscontinuityResets={x.RawDiscontinuityResets} inputRawTei={x.RawTransportErrors} inputRawSyncErrors={x.RawSyncErrors} inputRawScrambled={x.RawScrambledPackets} " +
                $"outputPackets={x.OutputPackets} outputDrops={x.OutputContinuityDrops} outputDropDelta={d.OutputDropDelta} outputCcErrors={x.OutputContinuityErrors} outputCcDelta={d.OutputCcDelta} outputGapEvents={x.OutputContinuityGapEvents} outputGapDelta={d.OutputGapDelta} outputSameCcMismatch={x.OutputSameCcContentMismatches} outputSameCcMismatchDelta={d.OutputSameCcMismatchDelta} outputDuplicates={x.OutputDuplicatePackets} outputDiscontinuityResets={x.OutputDiscontinuityResets} outputTei={x.OutputTransportErrors} outputSyncErrors={x.OutputSyncErrors} outputSyncDelta={d.OutputSyncDelta} outputScrambled={x.OutputScrambledPackets} " +
                $"bytesWritten={x.BytesWritten} bytesDelta={d.BytesDelta} rawLayerMeaning=pre_write_input_observation outputLayerMeaning=recorded_file_integrity group={SafeValue(context.Group)} groupRecordingCount={context.Count} groupTuners={SafeValue(context.Names)} groupDids={SafeValue(context.Dids)} " +
                $"rule=release_contract");
        }
    }

    private ActiveGroupContext BuildActiveRecordingGroupContext(string? group)
    {
        var effectiveGroup = string.IsNullOrWhiteSpace(group) ? "-" : group.Trim();
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.Where(x => x.IsRecordingCommitted).ToList();
        var groupActive = active
            .Where(x => string.Equals(x.Lease.Group, effectiveGroup, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Lease.Name)
            .ToList();
        var names = groupActive.Count == 0 ? "-" : string.Join(",", groupActive.Select(x => x.Lease.Name));
        var dids = groupActive.Count == 0 ? "-" : string.Join(",", groupActive.Select(x => x.Lease.Did));
        return new ActiveGroupContext(effectiveGroup, groupActive.Count, names, dids);
    }

    private void LogDirectRecorderQualityCorrelation(
        int rid,
        string service,
        string title,
        DirectRecorderQualityClassification quality,
        DirectRecorderOutcome outcome,
        ActiveGroupContext context,
        string tunerName,
        string did)
    {
        var outputDrops = ParseLogLong(outcome.OutputContinuityDrops);
        var outputCc = ParseLogLong(outcome.OutputContinuityErrors);
        var outputSync = ParseLogLong(outcome.OutputSyncErrors);
        var outputScrambled = ParseLogLong(outcome.OutputScrambledPackets);
        var rawDrops = ParseLogLong(outcome.RawContinuityDrops);
        var rawCc = ParseLogLong(outcome.RawContinuityErrors);
        var rawSync = ParseLogLong(outcome.RawSyncErrors);
        var rawScrambled = ParseLogLong(outcome.RawScrambledPackets);
        var outputDamage = outputDrops + outputCc + outputSync + outputScrambled;
        var rawNoise = rawDrops + rawCc + rawSync + rawScrambled;
        var correlation = ClassifyDropCorrelation(quality, outputDamage, rawNoise, context.Count);

        _log.Add("REC_QUALITY_CORRELATION", $"R{rid}",
            $"service={SafeValue(service)} title={TrimForLog(title, 80)} " +
            $"correlationClass={correlation.ClassName} priority={correlation.Priority} axis={correlation.Axis} userMeaning={correlation.UserMeaning} action={correlation.Action} " +
            $"qualityClass={quality.ClassName} qualitySeverity={quality.Severity} group={SafeValue(context.Group)} tuner={SafeValue(tunerName)} did={SafeValue(did)} concurrentSameGroupRecordings={context.Count} concurrentSameGroupTuners={SafeValue(context.Names)} concurrentSameGroupDids={SafeValue(context.Dids)} " +
            $"outputDamageScore={outputDamage} outputDrops={SafeValue(outcome.OutputContinuityDrops)} outputCcErrors={SafeValue(outcome.OutputContinuityErrors)} outputSyncErrors={SafeValue(outcome.OutputSyncErrors)} outputScrambled={SafeValue(outcome.OutputScrambledPackets)} " +
            $"rawNoiseScore={rawNoise} inputRawDrops={SafeValue(outcome.RawContinuityDrops)} inputRawCcErrors={SafeValue(outcome.RawContinuityErrors)} inputRawSyncErrors={SafeValue(outcome.RawSyncErrors)} inputRawScrambled={SafeValue(outcome.RawScrambledPackets)} " +
            $"rule=release_contract");
    }

    private static DropCorrelationClassification ClassifyDropCorrelation(
        DirectRecorderQualityClassification quality,
        long outputDamage,
        long rawNoise,
        int concurrentSameGroupRecordings)
    {
        if (string.Equals(quality.Severity, "FAIL", StringComparison.OrdinalIgnoreCase))
        {
            return new DropCorrelationClassification(
                "FAIL_OUTPUT_OR_BRIDGE",
                "HIGH",
                "recorder_or_output_integrity",
                "recording_result_requires_investigation",
                "inspect_bridge_response_and_output_ts");
        }

        if (outputDamage > 0)
        {
            var axis = concurrentSameGroupRecordings >= 2 ? "same_group_parallel_recording" : "single_recording_or_device_specific";
            var priority = outputDamage >= 50 ? "HIGH" : "MEDIUM";
            return new DropCorrelationClassification(
                concurrentSameGroupRecordings >= 2 ? "WARN_OUTPUT_DAMAGE_WITH_PARALLEL_LOAD" : "WARN_OUTPUT_DAMAGE_SINGLE_OR_DEVICE",
                priority,
                axis,
                "recorded_file_has_real_drop_or_cc_error",
                "compare_same_did_single_vs_parallel_recording");
        }

        if (rawNoise > 0)
        {
            return new DropCorrelationClassification(
                "OK_RAW_INPUT_NOISE_ONLY",
                "LOW",
                "raw_input_observation_only",
                "raw_input_noise_was_not_written_to_recorded_output",
                "observe_without_recovery");
        }

        return new DropCorrelationClassification(
            "OK_CLEAN_OUTPUT",
            "LOW",
            "none",
            "no_recording_quality_issue_detected",
            "none");
    }

    private static List<RuntimeStatsSample> ReadRuntimeStatsSamples(string? path, int maxLines)
    {
        var result = new List<RuntimeStatsSample>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;

        static RuntimeStatsSample ParseRuntimeStatsSample(JsonElement root)
        {
            var at = GetJsonString(root, "at");
            DateTime? atTime = DateTime.TryParse(at, out var parsedAt) ? parsedAt : null;
            return new RuntimeStatsSample(
                at,
                atTime,
                GetJsonInt64(root, "rawPackets"),
                GetJsonInt64(root, "rawContinuityDrops"),
                GetJsonInt64(root, "rawContinuityErrors"),
                GetJsonInt64(root, "rawContinuityGapEvents"),
                GetJsonInt64(root, "rawSameCcContentMismatches"),
                GetJsonInt64(root, "rawDuplicatePackets"),
                GetJsonInt64(root, "rawDiscontinuityResets"),
                GetJsonInt64(root, "rawTransportErrors"),
                GetJsonInt64(root, "rawSyncErrors"),
                GetJsonInt64(root, "rawScrambledPackets"),
                GetJsonInt64(root, "outputPackets"),
                GetJsonInt64(root, "outputContinuityDrops"),
                GetJsonInt64(root, "outputContinuityErrors"),
                GetJsonInt64(root, "outputContinuityGapEvents"),
                GetJsonInt64(root, "outputSameCcContentMismatches"),
                GetJsonInt64(root, "outputDuplicatePackets"),
                GetJsonInt64(root, "outputDiscontinuityResets"),
                GetJsonInt64(root, "outputTransportErrors"),
                GetJsonInt64(root, "outputSyncErrors"),
                GetJsonInt64(root, "outputScrambledPackets"),
                GetJsonInt64(root, "bytesWritten"));
        }

        try
        {
            // 正規契約は1サンプル=1物理行のJSONL。現行生成物はこの経路で読む。
            foreach (var line in File.ReadLines(path).Take(maxLines))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    result.Add(ParseRuntimeStatsSample(doc.RootElement));
                }
                catch (JsonException)
                {
                    result.Clear();
                    break;
                }
            }

            if (result.Count > 0) return result;

            // 旧形式にはJSONL拡張子で単一JSONを書いた生成物がある。
            // 互換読取として、行単位解析できない場合はファイル全体を単一サンプルとして読み直す。
            var legacyJson = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(legacyJson)) return result;
            using var legacyDoc = JsonDocument.Parse(legacyJson);
            result.Add(ParseRuntimeStatsSample(legacyDoc.RootElement));
            return result;
        }
        catch
        {
            return new List<RuntimeStatsSample>();
        }
    }

    private static List<RuntimeStatsDelta> BuildRuntimeStatsDeltas(List<RuntimeStatsSample> samples)
    {
        var list = new List<RuntimeStatsDelta>();
        RuntimeStatsSample? prev = null;
        foreach (var s in samples)
        {
            list.Add(new RuntimeStatsDelta(
                s,
                Math.Max(0, s.RawContinuityDrops - (prev?.RawContinuityDrops ?? 0)),
                Math.Max(0, s.RawContinuityErrors - (prev?.RawContinuityErrors ?? 0)),
                Math.Max(0, s.RawContinuityGapEvents - (prev?.RawContinuityGapEvents ?? 0)),
                Math.Max(0, s.RawSameCcContentMismatches - (prev?.RawSameCcContentMismatches ?? 0)),
                Math.Max(0, s.OutputContinuityDrops - (prev?.OutputContinuityDrops ?? 0)),
                Math.Max(0, s.OutputContinuityErrors - (prev?.OutputContinuityErrors ?? 0)),
                Math.Max(0, s.OutputContinuityGapEvents - (prev?.OutputContinuityGapEvents ?? 0)),
                Math.Max(0, s.OutputSameCcContentMismatches - (prev?.OutputSameCcContentMismatches ?? 0)),
                Math.Max(0, s.OutputSyncErrors - (prev?.OutputSyncErrors ?? 0)),
                Math.Max(0, s.BytesWritten - (prev?.BytesWritten ?? 0))));
            prev = s;
        }
        return list;
    }

    private sealed record DirectRecorderQualityClassification(string ClassName, string Severity, string UserMeaning, string Action);

    private sealed record DropCorrelationClassification(string ClassName, string Priority, string Axis, string UserMeaning, string Action);

    private sealed record DropTimelineClassification(string ClassName, string Phase, string Severity, string UserMeaning, string Action);

    private static DirectRecorderQualityClassification ClassifyDirectRecorderQuality(
        DirectRecorderOutcome outcome,
        RecordingTsVerifier.VerificationResult? tsVerification,
        RecordingCompletionEvidence completionEvidence)
    {
        var outputDrops = ParseLogLong(outcome.OutputContinuityDrops);
        var outputCc = ParseLogLong(outcome.OutputContinuityErrors);
        var outputSync = ParseLogLong(outcome.OutputSyncErrors);
        var outputScrambled = ParseLogLong(outcome.OutputScrambledPackets);
        var rawDrops = ParseLogLong(outcome.RawContinuityDrops);
        var rawCc = ParseLogLong(outcome.RawContinuityErrors);
        var rawSync = ParseLogLong(outcome.RawSyncErrors);
        var rawScrambled = ParseLogLong(outcome.RawScrambledPackets);

        var outputHasDamage = outputDrops > 0 || outputCc > 0 || outputSync > 0 || outputScrambled > 0;
        var rawHasOnlyInputNoise = !outputHasDamage && (rawDrops > 0 || rawCc > 0 || rawSync > 0 || rawScrambled > 0);

        if (!outcome.ResponseExists)
        {
            if (completionEvidence.CanKeepCompleted)
            {
                return new DirectRecorderQualityClassification(
                    "WARN_RESPONSE_MISSING_BUT_RECORDING_EVIDENCE_CLEAR",
                    "WARN",
                    "worker_response_missing_but_recording_structure_and_runtime_output_evidence_are_valid",
                    "keep_completed_with_response_missing_warning_and_preserve_runtime_evidence");
            }

            return new DirectRecorderQualityClassification(
                "FAIL_RESPONSE_MISSING_RECORDING_EVIDENCE_INSUFFICIENT",
                "FAIL",
                "worker_response_missing_and_recording_completion_evidence_not_sufficient",
                "preserve_runtime_evidence_and_investigate_recording_failure");
        }

        if (!outcome.Success)
        {
            if (completionEvidence.CanKeepCompleted)
            {
                return new DirectRecorderQualityClassification(
                    "WARN_WORKER_NG_BUT_RECORDING_EVIDENCE_CLEAR",
                    "WARN",
                    "worker_reported_failure_but_recording_structure_and_runtime_output_evidence_are_valid",
                    "keep_completed_with_worker_warning_and_preserve_runtime_evidence");
            }

            return new DirectRecorderQualityClassification(
                "FAIL_WORKER_NG_RECORDING_EVIDENCE_INSUFFICIENT",
                "FAIL",
                "worker_reported_failure_and_recording_completion_evidence_not_sufficient",
                "preserve_runtime_evidence_and_investigate_recorder_summary");
        }

        if (outputScrambled > 0)
        {
            return new DirectRecorderQualityClassification(
                "FAIL_OUTPUT_SCRAMBLED",
                "FAIL",
                "recorded_file_contains_scrambled_packets",
                "investigate_b25_card_reader_or_service_selection");
        }

        if (outputDrops > 0 || outputCc > 0 || outputSync > 0)
        {
            return new DirectRecorderQualityClassification(
                "WARN_OUTPUT_TRANSPORT_DAMAGE",
                "WARN",
                "recorded_file_clear_but_transport_drop_detected_not_immediate_failure",
                "observe_drop_count_and_compare_same_tuner_over_time");
        }

        if (rawHasOnlyInputNoise)
        {
            return new DirectRecorderQualityClassification(
                "OK_OUTPUT_CLEAR_RAW_INPUT_WARN",
                "OK",
                "recorded_file_clear_raw_input_had_transient_noise",
                "observe_only_no_auto_recovery");
        }

        return new DirectRecorderQualityClassification(
            "OK_OUTPUT_CLEAR",
            "OK",
            "recorded_file_clear",
            "none");
    }

    private static DropTimelineClassification ClassifyDropTimeline(
        DirectRecorderOutcome outcome,
        IReadOnlyList<RuntimeStatsSample> samples,
        IReadOnlyList<RuntimeStatsDelta> deltas,
        RuntimeStatsDelta? firstDrop,
        RuntimeStatsDelta? lastDrop,
        int secondsToFirstDrop)
    {
        if (samples.Count == 0 || deltas.Count == 0)
        {
            return new DropTimelineClassification(
                "UNKNOWN_TIMELINE_MISSING",
                "unknown",
                "UNKNOWN",
                "runtime_stats_missing",
                "check_runtime_stats_emission");
        }

        var final = samples[^1];
        var outputDamage = final.OutputContinuityDrops > 0 || final.OutputContinuityErrors > 0 || final.OutputSyncErrors > 0 || final.OutputScrambledPackets > 0;
        var rawNoise = final.RawContinuityDrops > 0 || final.RawContinuityErrors > 0 || final.RawSyncErrors > 0 || final.RawScrambledPackets > 0;
        var anyOutputDelta = deltas.Any(d => d.OutputDropDelta > 0 || d.OutputCcDelta > 0 || d.OutputSyncDelta > 0);
        var anyRawDelta = deltas.Any(d => d.RawDropDelta > 0 || d.RawCcDelta > 0);

        if (final.OutputScrambledPackets > 0)
        {
            return new DropTimelineClassification(
                "FAIL_OUTPUT_SCRAMBLED_TIMELINE",
                ClassifyDropPhase(secondsToFirstDrop, firstDrop, lastDrop),
                "FAIL",
                "recorded_output_scrambled_detected",
                "investigate_descramble_route");
        }

        if (outputDamage || anyOutputDelta)
        {
            return new DropTimelineClassification(
                "WARN_OUTPUT_DAMAGE_TIMELINE",
                ClassifyDropPhase(secondsToFirstDrop, firstDrop, lastDrop),
                "WARN",
                "recorded_output_error_detected",
                "compare_tuner_overlap_and_pt3_load");
        }

        if (rawNoise || anyRawDelta)
        {
            return new DropTimelineClassification(
                secondsToFirstDrop >= 0 && secondsToFirstDrop <= 30 ? "OK_RAW_STARTUP_TRANSIENT" : "OK_RAW_INPUT_ONLY_WARN",
                ClassifyDropPhase(secondsToFirstDrop, firstDrop, lastDrop),
                "OK",
                "raw_input_noise_not_committed_to_output",
                "observe_only_no_auto_recovery");
        }

        return new DropTimelineClassification(
            "OK_NO_DROP",
            "none",
            "OK",
            "no_drop_or_cc_error_detected",
            "none");
    }

    private static string ClassifyDropPhase(int secondsToFirstDrop, RuntimeStatsDelta? firstDrop, RuntimeStatsDelta? lastDrop)
    {
        if (firstDrop is null) return "none";
        if (secondsToFirstDrop >= 0 && secondsToFirstDrop <= 30) return "startup_first_30s";
        if (firstDrop.Sample.At == lastDrop?.Sample.At) return "single_point";
        return "during_recording";
    }

    private static long ParseLogLong(string? value)
        => long.TryParse(value, out var parsed) ? parsed : 0;

    private static string GetJsonString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) ? (prop.GetString() ?? "-") : "-";

    private static long GetJsonInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop)) return 0;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value)) return value;
        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed)) return parsed;
        return 0;
    }

    private sealed record RecordingCompletionEvidence(bool CanKeepCompleted, string Result, string Reason);

    private static RecordingCompletionEvidence EvaluateRecordingCompletionEvidence(
        DirectRecorderOutcome outcome,
        RecordingTsVerifier.VerificationResult? verification)
    {
        // RECORDING_COMPLETION_EVIDENCE_CONTRACT:
        // Worker応答が欠落/NGでも、予約Completedを維持できるかは録画実体の同じ意味で判定する。
        // RecordingTsVerifierは対象service/PMT/video PID/対象video非scrambleという構造証拠、
        // result/runtime statsは全区間の出力実績を担う。drop/continuity errorは品質WARNであり、
        // 品質観測値だけを録画不成立の判定へ昇格させず、録画ライフサイクルの結果と分離する。
        if (verification?.ClearEnoughForCompleted != true)
            return new RecordingCompletionEvidence(false, "INSUFFICIENT", "ts_structure_not_clear");

        if (!long.TryParse(outcome.BytesWritten, out var bytesWritten) || bytesWritten <= 0)
            return new RecordingCompletionEvidence(false, "INSUFFICIENT", "bytes_written_missing_or_zero");
        if (!long.TryParse(outcome.PacketsWritten, out var packetsWritten) || packetsWritten <= 0)
            return new RecordingCompletionEvidence(false, "INSUFFICIENT", "packets_written_missing_or_zero");
        if (!long.TryParse(outcome.OutputSyncErrors, out var outputSyncErrors))
            return new RecordingCompletionEvidence(false, "INSUFFICIENT", "output_sync_evidence_missing");
        if (!long.TryParse(outcome.OutputScrambledPackets, out var outputScrambled))
            return new RecordingCompletionEvidence(false, "INSUFFICIENT", "output_scramble_evidence_missing");
        if (outputSyncErrors != 0)
            return new RecordingCompletionEvidence(false, "FAILED", $"output_sync_errors={outputSyncErrors}");
        if (outputScrambled != 0)
            return new RecordingCompletionEvidence(false, "FAILED", $"output_scrambled_packets={outputScrambled}");

        return new RecordingCompletionEvidence(true, "CLEAR", "ts_structure_clear+positive_output+output_sync0+output_scramble0");
    }


    private sealed record DirectRecorderOutcome(
        bool ResponseExists,
        bool Success,
        string BytesWritten,
        string PacketsWritten,
        string QualityVerdict,
        string RuntimeStatsPath,
        string RuntimeStatsEmitted,
        string RawContinuityDrops,
        string RawContinuityErrors,
        string RawSyncErrors,
        string RawScrambledPackets,
        string OutputContinuityDrops,
        string OutputContinuityErrors,
        string OutputSyncErrors,
        string OutputScrambledPackets,
        string QualitySource,
        string QualityCompleteness,
        string Summary);

    private static DirectRecorderOutcome ReadDirectRecorderOutcome(string? responsePath, string? fallbackRuntimeStatsPath = null)
    {
        var summary = ReadDirectRecorderResponseSummary(responsePath);
        if (string.IsNullOrWhiteSpace(responsePath) || !File.Exists(responsePath))
            return TryRecoverDirectRecorderOutcomeFromRuntimeStats(fallbackRuntimeStatsPath, false, summary)
                ?? new DirectRecorderOutcome(false, false, "-", "-", "NO_RESPONSE", fallbackRuntimeStatsPath ?? "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "Unavailable", "unavailable", summary);

        try
        {
            var json = File.ReadAllText(responsePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var successProp) && successProp.ValueKind == JsonValueKind.True;
            string bytesWritten = "-";
            string packetsWritten = "-";
            string qualityVerdict = "-";
            string runtimeStatsPath = "-";
            string runtimeStatsEmitted = "-";
            string rawContinuityDrops = "-";
            string rawContinuityErrors = "-";
            string rawSyncErrors = "-";
            string rawScrambledPackets = "-";
            string outputContinuityDrops = "-";
            string outputContinuityErrors = "-";
            string outputSyncErrors = "-";
            string outputScrambledPackets = "-";
            var hasResultObject = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object;
            if (hasResultObject)
            {
                if (result.TryGetProperty("bytesWritten", out var bytesProp)) bytesWritten = bytesProp.ToString();
                if (result.TryGetProperty("packetsWritten", out var packetsProp)) packetsWritten = packetsProp.ToString();
                if (result.TryGetProperty("qualityVerdict", out var qProp)) qualityVerdict = qProp.GetString() ?? "-";
                if (result.TryGetProperty("runtimeStatsPath", out var rspProp)) runtimeStatsPath = rspProp.GetString() ?? "-";
                if (result.TryGetProperty("runtimeStatsEmitted", out var rseProp)) runtimeStatsEmitted = rseProp.ToString();
                if (result.TryGetProperty("rawContinuityDrops", out var rcdProp)) rawContinuityDrops = rcdProp.ToString();
                if (result.TryGetProperty("rawContinuityErrors", out var rceProp)) rawContinuityErrors = rceProp.ToString();
                if (result.TryGetProperty("rawSyncErrors", out var rse2Prop)) rawSyncErrors = rse2Prop.ToString();
                if (result.TryGetProperty("rawScrambledPackets", out var rsp2Prop)) rawScrambledPackets = rsp2Prop.ToString();
                if (result.TryGetProperty("outputContinuityDrops", out var ocdProp)) outputContinuityDrops = ocdProp.ToString();
                if (result.TryGetProperty("outputContinuityErrors", out var oceProp)) outputContinuityErrors = oceProp.ToString();
                if (result.TryGetProperty("outputSyncErrors", out var oseProp)) outputSyncErrors = oseProp.ToString();
                if (result.TryGetProperty("outputScrambledPackets", out var ospProp)) outputScrambledPackets = ospProp.ToString();
            }

            static bool HasQualityValue(string value) => long.TryParse(value, out _);
            var resultJsonHasQuality = hasResultObject
                && HasQualityValue(outputContinuityDrops)
                && HasQualityValue(outputContinuityErrors)
                && HasQualityValue(outputScrambledPackets);

            // A syntactically valid top-level worker failure JSON may not contain the record result object.
            // In that case the runtime timeline is the last durable quality source.  Do not convert a
            // recoverable partial recording into QualityDataAvailable=false merely because the wrapper JSON exists.
            if (!resultJsonHasQuality)
            {
                var recovered = TryRecoverDirectRecorderOutcomeFromRuntimeStats(
                    string.IsNullOrWhiteSpace(runtimeStatsPath) || runtimeStatsPath == "-" ? fallbackRuntimeStatsPath : runtimeStatsPath,
                    true,
                    summary);
                if (recovered is not null) return recovered;
            }

            return new DirectRecorderOutcome(true, success, bytesWritten, packetsWritten, qualityVerdict, runtimeStatsPath, runtimeStatsEmitted, rawContinuityDrops, rawContinuityErrors, rawSyncErrors, rawScrambledPackets, outputContinuityDrops, outputContinuityErrors, outputSyncErrors, outputScrambledPackets, "ResultJson", success ? "complete" : "partial", summary);
        }
        catch
        {
            return TryRecoverDirectRecorderOutcomeFromRuntimeStats(fallbackRuntimeStatsPath, true, summary)
                ?? new DirectRecorderOutcome(true, false, "-", "-", "READ_ERROR", fallbackRuntimeStatsPath ?? "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "Unavailable", "unavailable", summary);
        }
    }

    private static DirectRecorderOutcome? TryRecoverDirectRecorderOutcomeFromRuntimeStats(string? runtimeStatsPath, bool responseExists, string responseSummary)
    {
        if (string.IsNullOrWhiteSpace(runtimeStatsPath) || !File.Exists(runtimeStatsPath)) return null;
        try
        {
            // An abrupt worker/process termination can leave a truncated final JSONL append.  Recovery must
            // therefore walk backwards to the last parseable snapshot, not merely read the last non-empty line.
            var lines = File.ReadAllLines(runtimeStatsPath);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    static string Read(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.ToString() : "-";
                    var completeness = root.TryGetProperty("completeness", out var completenessProp)
                        ? completenessProp.GetString() ?? "partial"
                        : "partial";
                    return new DirectRecorderOutcome(
                        responseExists,
                        false,
                        Read(root, "bytesWritten"),
                        Read(root, "packetsWritten"),
                        "RUNTIME_STATS_RECOVERY",
                        runtimeStatsPath,
                        $"recovered_sample_line_{i + 1}",
                        Read(root, "rawContinuityDrops"),
                        Read(root, "rawContinuityErrors"),
                        Read(root, "rawSyncErrors"),
                        Read(root, "rawScrambledPackets"),
                        Read(root, "outputContinuityDrops"),
                        Read(root, "outputContinuityErrors"),
                        Read(root, "outputSyncErrors"),
                        Read(root, "outputScrambledPackets"),
                        "RuntimeStatsRecovery",
                        completeness,
                        responseSummary);
                }
                catch (JsonException)
                {
                    // Keep walking backwards. A partial final append is expected after a hard termination.
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadDirectRecorderResponseSummary(string? responsePath)
    {
        if (string.IsNullOrWhiteSpace(responsePath))
            return "path=- exists=False";

        try
        {
            if (!File.Exists(responsePath))
                return $"path={SafeValue(responsePath)} exists=False";

            var json = File.ReadAllText(responsePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var topSuccess = root.TryGetProperty("success", out var successProp) && successProp.ValueKind == JsonValueKind.True;
            var exitCode = root.TryGetProperty("exitCode", out var exitCodeProp) && exitCodeProp.TryGetInt32(out var ec) ? ec.ToString() : "-";
            var processId = root.TryGetProperty("processId", out var processIdProp) && processIdProp.TryGetInt32(out var pid) ? pid.ToString() : "-";

            string outputPath = "-";
            string bytesWritten = "-";
            string packetsWritten = "-";
            string qualityVerdict = "-";
            string runtimeStatsEmitted = "-";
            string startupGate = "-";
            string startupTimedOut = "-";
            string startupReason = "-";
            string startupDiscardedDrops = "-";
            string startupRecoveryAction = "-";
            string startupRecoveryCount = "-";
            string startupRecoveryResult = "-";
            string rawDrops = "-";
            string outputDrops = "-";
            string recorderMessage = "-";

            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("outputPath", out var outputPathProp))
                    outputPath = outputPathProp.GetString() ?? "-";
                if (result.TryGetProperty("bytesWritten", out var bytesProp))
                    bytesWritten = bytesProp.ToString();
                if (result.TryGetProperty("packetsWritten", out var packetsProp))
                    packetsWritten = packetsProp.ToString();
                if (result.TryGetProperty("qualityVerdict", out var qProp))
                    qualityVerdict = qProp.GetString() ?? "-";
                if (result.TryGetProperty("runtimeStatsEmitted", out var rseProp))
                    runtimeStatsEmitted = rseProp.ToString();
                if (result.TryGetProperty("startupStabilityGateReleased", out var sgrProp))
                    startupGate = sgrProp.ToString();
                if (result.TryGetProperty("startupStabilityGateTimedOut", out var sgtProp))
                    startupTimedOut = sgtProp.ToString();
                if (result.TryGetProperty("startupStabilityGateReason", out var sgrsProp))
                    startupReason = sgrsProp.GetString() ?? "-";
                if (result.TryGetProperty("startupStabilityDiscardedOutputDrops", out var sdodProp))
                    startupDiscardedDrops = sdodProp.ToString();
                if (result.TryGetProperty("startupRecoveryAction", out var sraProp))
                    startupRecoveryAction = sraProp.GetString() ?? "-";
                if (result.TryGetProperty("startupRecoveryCount", out var srcProp))
                    startupRecoveryCount = srcProp.ToString();
                if (result.TryGetProperty("startupRecoveryResult", out var srrProp))
                    startupRecoveryResult = srrProp.GetString() ?? "-";
                if (result.TryGetProperty("rawContinuityDrops", out var rawDropProp))
                    rawDrops = rawDropProp.ToString();
                if (result.TryGetProperty("outputContinuityDrops", out var outDropProp))
                    outputDrops = outDropProp.ToString();
                if (result.TryGetProperty("message", out var messageProp))
                    recorderMessage = messageProp.GetString() ?? "-";
            }
            else if (root.TryGetProperty("message", out var topMessageProp))
            {
                recorderMessage = topMessageProp.GetString() ?? "-";
            }

            recorderMessage = recorderMessage
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ");

            return $"path={SafeValue(responsePath)} exists=True success={topSuccess} exitCode={exitCode} recorderPid={processId} bytesWritten={bytesWritten} packetsWritten={packetsWritten} qualityVerdict={SafeValue(qualityVerdict)} rawDrops={SafeValue(rawDrops)} outputDrops={SafeValue(outputDrops)} runtimeStatsEmitted={SafeValue(runtimeStatsEmitted)} startupGateReleased={SafeValue(startupGate)} startupGateTimedOut={SafeValue(startupTimedOut)} startupReason={SafeValue(startupReason)} startupDiscardedOutputDrops={SafeValue(startupDiscardedDrops)} startupRecoveryAction={SafeValue(startupRecoveryAction)} startupRecoveryCount={SafeValue(startupRecoveryCount)} startupRecoveryResult={SafeValue(startupRecoveryResult)} outputPath={SafeValue(outputPath)} message={TrimForLog(recorderMessage, 1800)}";
        }
        catch (Exception ex)
        {
            return $"path={SafeValue(responsePath)} exists={(File.Exists(responsePath) ? "True" : "False")} readError={TrimForLog(ex.Message, 240)}";
        }
    }




    private string BuildReservationPipelineAudit(
        Reservation r,
        string stage,
        string? group = null,
        string? plannedTuner = null,
        string? actualTuner = null,
        TunerLease? lease = null,
        ChannelTarget? resolvedChannel = null,
        DirectRecorderFileNameBuildResult? fileNameBuild = null,
        string? outputPath = null,
        DateTime? plannedEnd = null,
        bool finalGuardApplied = false,
        string? note = null)
    {
        var reservationKind = ResolveReservationKindForPipelineAudit(r);
        var route = ResolveExecutionRouteForPipelineAudit(r);
        var now = DateTime.Now;
        var ageMin = r.CreatedAt == default ? "-" : Math.Max(0, (int)Math.Round((now - r.CreatedAt).TotalMinutes)).ToString();
        var scheduledStart = r.ScheduledStartTime.HasValue ? r.ScheduledStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-";
        var created = r.CreatedAt == default ? "-" : r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
        var updated = r.UpdatedAt == default ? "-" : r.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");
        var channelIdentity = resolvedChannel is null
            ? $"nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} ch={SafeValue(r.ChannelArgument)} channelSource=reservation_snapshot"
            : $"nid={resolvedChannel.OriginalNetworkId} tsid={resolvedChannel.TransportStreamId} sid={resolvedChannel.ServiceId} chspace={resolvedChannel.ResolvedSpace} chi={resolvedChannel.ResolvedChannelIndex} ch2={SafeValue(resolvedChannel.Ch2FileName)}:{resolvedChannel.Ch2LineNumber} channelSource=current_ch2";
        var fileNamePart = fileNameBuild is null
            ? "fileName=- outputPath=- filenameGuard=not_yet"
            : $"fileName={SafeValue(fileNameBuild.FileName)} outputPath={SafeValue(outputPath)} filenameGuard=recordfilename_ini_template formatterRule={SafeValue(fileNameBuild.FormatterRule)} rawTitleChanged=false unsupportedTokens={SafeValue(fileNameBuild.UnsupportedTokens)}";
        var leasePart = lease is null
            ? $"plannedTuner={SafeValue(plannedTuner)} actualTuner={SafeValue(actualTuner)} did=- bonDriver=- leaseKind=-"
            : $"plannedTuner={SafeValue(plannedTuner)} actualTuner={SafeValue(actualTuner ?? lease.Name)} did={SafeValue(lease.Did)} bonDriver={SafeValue(lease.BonDriverFileName)} leaseKind=Recording elapsedSinceReleaseMs={(lease.ElapsedSinceReleaseMs.HasValue ? lease.ElapsedSinceReleaseMs.Value.ToString("F0") : "-")}";
        var chainPart = $"userChain={r.IsUserChain} chainPrev={(r.UserChainPreviousId.HasValue ? $"R{r.UserChainPreviousId.Value}" : "-")} chainRoot={(r.UserChainRootId.HasValue ? $"R{r.UserChainRootId.Value}" : "-")} chainContinuation={IsChainContinuation(r)}";
        var timePart = $"start={r.StartTime:yyyy-MM-dd HH:mm:ss} end={r.EndTime:yyyy-MM-dd HH:mm:ss} scheduledStart={scheduledStart} plannedEnd={(plannedEnd.HasValue ? plannedEnd.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-")} created={created} updated={updated} ageMin={ageMin}";
        return $"result=OK stage={stage} reservation=R{r.Id} kind={reservationKind} source={r.Source} route={route} status={r.Status} enabled={r.IsEnabled} conflicted={r.IsConflicted} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title, 80)} group={SafeValue(group ?? ResolveGroup(r))} {leasePart} {channelIdentity} {fileNamePart} finalGuardApplied={finalGuardApplied} commonAllocationRoute=True wakeCoupled=True preRecordEpgCoupled=True {chainPart} {timePart} sourceRule={(r.SourceRuleId.HasValue ? r.SourceRuleId.Value.ToString() : "-")} sourceRuleName={SafeValue(r.SourceRuleName)} note={SafeValue(note)} rule=reservation_tuner_filename_pipeline_audit_release_contract";
    }

    private static string ResolveReservationKindForPipelineAudit(Reservation r)
    {
        if (r.IsUserChain) return "UserChain";
        return r.Source switch
        {
            ReservationSource.Immediate => "Immediate",
            ReservationSource.Program => "Program",
            // release_contract: KeywordSearch is a program-guide search result/manual action.
            // Keyword is the KeywordMatcher / auto-search generated reservation.
            ReservationSource.KeywordSearch => "ManualOrProgramGuide",
            ReservationSource.Keyword => "AutoSearch",
            ReservationSource.Epg => "SystemEpg",
            ReservationSource.Manual => r.SourceRuleId.HasValue ? "ProgramGuideOrManualWithRule" : "ManualOrProgramGuide",
            _ => r.Source.ToString()
        };
    }

    private static string ResolveExecutionRouteForPipelineAudit(Reservation r)
    {
        if (r.Source == ReservationSource.Epg) return "SystemEpgExcludedFromRecording";
        if (r.IsUserChain) return "ALLOC_ROUTE/TUNER_ALLOC/UserChain/TvAIrEpgRec";
        return "ALLOC_ROUTE/TUNER_ALLOC/TvAIrEpgRec";
    }

    private static string ReservationDisplayTitle(string? rawTitle, int maxLength = 120)
        => ReservationTitleDisplayContract.ForLog(rawTitle, maxLength);

    private static string SafeValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}

// ─── 内部型 ──────────────────────────────────────────────────────

internal enum RecordingFileGrowthStopReason
{
    None,
    FileProbeError,
    FileMissingAfterInitialGrace,
    NoDataAfterInitialGrace,
    GrowthStalled
}

internal sealed record RecordingFileGrowthObservation(
    bool ShouldStop,
    RecordingFileGrowthStopReason StopReason,
    string Reason,
    long Bytes,
    long PreviousBytes)
{
    public static RecordingFileGrowthObservation Continue(long bytes, long previousBytes)
        => new(false, RecordingFileGrowthStopReason.None, string.Empty, bytes, previousBytes);

    public static RecordingFileGrowthObservation Stop(RecordingFileGrowthStopReason stopReason, string reason, long bytes, long previousBytes)
        => new(true, stopReason, reason, bytes, previousBytes);
}

internal sealed record ActiveRecordingSessionSnapshot(
    Guid OperationId,
    int ReservationId,
    int ProcessId,
    DateTime PlannedEndTime,
    string TunerName,
    string Did,
    string BonDriverFileName,
    string RecordingFilePath,
    string State,
    Guid PoolLeaseId,
    long OccupancyGeneration,
    bool PoolLeaseCurrent);

internal sealed class RecordingSession
{
    private int finalizationState;
    // PROVISIONAL_SESSION_STATE_INVARIANT:
    // 正式録画昇格とabort cleanupは同じ暫定sessionから排他的に分岐する。
    // 0=StartingProvisional, 1=RecordingCommitInProgress, 2=RecordingCommitted, 3=AbortCleanupStarted。
    // DBのStarting→Recording CAS中はcleanupを禁止し、CAS失敗時だけ暫定へ戻す。
    // 別々のフラグで管理すると、Recording確定直後にcleanupが同じworkerを停止できるため禁止する。
    private int recordingLifecycleState;
    private readonly object recordingLifecycleGate = new();
    private long recordingCommitGeneration;
    private TaskCompletionSource<bool> recordingCommitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ReservationScheduler.RecordingAbortCleanupResult> abortCleanupCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid          OperationId    { get; } = Guid.NewGuid();
    public int           ReservationId  { get; }
    public int           ProcessId      { get; }
    public ManagedProcessIdentity WorkerIdentity { get; }
    public string FinalizationState => Volatile.Read(ref finalizationState) switch
    {
        1 => "Finalizing",
        2 => "Finalized",
        _ => "Active"
    };
    public bool IsRecordingCommitInProgress
    {
        get { lock (recordingLifecycleGate) return recordingLifecycleState == 1; }
    }
    public bool IsRecordingCommitted
    {
        get { lock (recordingLifecycleGate) return recordingLifecycleState == 2; }
    }
    public bool IsAbortCleanupStarted
    {
        get { lock (recordingLifecycleGate) return recordingLifecycleState == 3; }
    }
    public string RecordingCommitState
    {
        get
        {
            lock (recordingLifecycleGate)
            {
                return recordingLifecycleState switch
                {
                    1 => "RecordingCommitInProgress",
                    2 => "RecordingCommitted",
                    3 => "AbortCleanupStarted",
                    _ => "StartingProvisional"
                };
            }
        }
    }
    public bool TryGetRecordingCommitWaitSnapshot(out long generation, out Task<bool> completion)
    {
        lock (recordingLifecycleGate)
        {
            if (recordingLifecycleState != 1)
            {
                generation = recordingCommitGeneration;
                completion = Task.FromResult(false);
                return false;
            }

            generation = recordingCommitGeneration;
            completion = recordingCommitCompletion.Task;
            return true;
        }
    }
    public Task<ReservationScheduler.RecordingAbortCleanupResult> AbortCleanupCompletion => abortCleanupCompletion.Task;
    public bool TryBeginAbortCleanup()
    {
        lock (recordingLifecycleGate)
        {
            if (recordingLifecycleState != 0)
                return false;
            recordingLifecycleState = 3;
            return true;
        }
    }
    public void CompleteAbortCleanup(ReservationScheduler.RecordingAbortCleanupResult result) => abortCleanupCompletion.TrySetResult(result);
    public DateTime      PlannedEndTime { get; private set; }
    public int           PostEndMarginSeconds { get; }
    public TunerLease    Lease          { get; }
    public string        RecordingFilePath { get; }
    public string        ResponsePath { get; }
    public string        StopSignalPath { get; }
    public string        ProgressPath { get; }
    public string        RuntimeStatsPath { get; }
    public string        JobPath { get; }
    public TvTestActivityHandle? ActivityHandle { get; }
    public DateTime RecordingFileGrowthWatchStartedAt { get; private set; }
    public DateTime LastRecordingFileGrowthAt { get; private set; }
    public long LastObservedRecordingFileBytes { get; private set; } = -1;
    public bool RecordingFileEverGrew { get; private set; }
    public bool RecordingFileStallStopRequested { get; private set; }
    public RecordingSession(int reservationId, int processId, DateTime plannedEndTime, TunerLease lease, string recordingFilePath, string responsePath = "", string stopSignalPath = "", string progressPath = "", string runtimeStatsPath = "", string jobPath = "", int postEndMarginSeconds = SettingsDefaults.PostEndMarginSeconds, TvTestActivityHandle? activityHandle = null)
    {
        ReservationId  = reservationId;
        ProcessId      = processId;
        WorkerIdentity = TvAirManagedProcessRegistry.CaptureIdentity(processId);
        PlannedEndTime = plannedEndTime;
        PostEndMarginSeconds = SettingsDefaults.NormalizePostEndMarginSeconds(postEndMarginSeconds);
        Lease          = lease;
        RecordingFilePath = recordingFilePath;
        ResponsePath = responsePath;
        StopSignalPath = stopSignalPath;
        ProgressPath = progressPath;
        RuntimeStatsPath = runtimeStatsPath;
        JobPath = jobPath;
        ActivityHandle = activityHandle;
        RecordingFileGrowthWatchStartedAt = DateTime.Now;
        LastRecordingFileGrowthAt = RecordingFileGrowthWatchStartedAt;
    }

    public bool TryBeginRecordingCommit(out long generation)
    {
        lock (recordingLifecycleGate)
        {
            if (recordingLifecycleState != 0)
            {
                generation = recordingCommitGeneration;
                return false;
            }

            // RECORDING_COMMIT_GENERATION_INVARIANT:
            // commit失敗後に同じsessionで再試行する場合、前世代の完了Taskを使い回してはならない。
            // 試行ごとに新しい世代とCompletionを発行し、待機側と完了側は同じ世代だけを操作する。
            // 遅れて戻った旧世代が、新世代のcommitを完了または取消してはならない。
            recordingCommitGeneration++;
            generation = recordingCommitGeneration;
            recordingCommitCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            recordingLifecycleState = 1;
            return true;
        }
    }

    public bool CompleteRecordingCommit(long generation)
    {
        TaskCompletionSource<bool>? completion = null;
        lock (recordingLifecycleGate)
        {
            if (recordingLifecycleState == 2)
                return recordingCommitGeneration == generation;
            if (recordingLifecycleState != 1 || recordingCommitGeneration != generation)
                return false;

            recordingLifecycleState = 2;
            completion = recordingCommitCompletion;
        }

        completion!.TrySetResult(true);
        return true;
    }

    public void CancelRecordingCommit(long generation)
    {
        TaskCompletionSource<bool>? completion = null;
        lock (recordingLifecycleGate)
        {
            if (recordingLifecycleState != 1 || recordingCommitGeneration != generation)
                return;

            recordingLifecycleState = 0;
            completion = recordingCommitCompletion;
        }

        completion!.TrySetResult(false);
    }

    // DBが既にRecordingである再Attach／先行確定の収束専用。
    public bool TryMarkRecordingCommitted()
    {
        TaskCompletionSource<bool>? completion = null;
        lock (recordingLifecycleGate)
        {
            if (recordingLifecycleState == 2)
                return true;
            if (recordingLifecycleState == 3)
                return false;

            if (recordingLifecycleState == 1)
                completion = recordingCommitCompletion;
            recordingLifecycleState = 2;
        }

        completion?.TrySetResult(true);
        return true;
    }

    public bool TryBeginFinalization()
        => Interlocked.CompareExchange(ref finalizationState, 1, 0) == 0;

    public void MarkFinalized()
        => Interlocked.Exchange(ref finalizationState, 2);

    public void CancelFinalization()
        => Interlocked.CompareExchange(ref finalizationState, 0, 1);

    public void UpdatePlannedEndTime(DateTime plannedEndTime)
    {
        PlannedEndTime = plannedEndTime;
    }

    public void MarkRecordingFileGrowth(DateTime at, long bytes)
    {
        LastObservedRecordingFileBytes = bytes;
        LastRecordingFileGrowthAt = at;
        if (bytes > 0) RecordingFileEverGrew = true;
    }

    public void MarkRecordingFileStallStopRequested()
    {
        RecordingFileStallStopRequested = true;
    }

// release_contract: 停止フェーズ中の再評価侵入を抑止するための共通判定。

}


public readonly record struct StopRecordingRequestResult(bool Accepted, bool Pending, int ReservationId, string Message)
{
    public static StopRecordingRequestResult CreateAccepted(int id) => new(true, false, id, "録画停止を受け付けました。");
    public static StopRecordingRequestResult CreatePending(int id) => new(false, true, id, "録画停止処理中です。");
    public static StopRecordingRequestResult CreateFailed(int id) => new(false, false, id, "録画停止要求を開始できませんでした。");
}
