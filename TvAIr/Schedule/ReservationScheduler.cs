using System.Diagnostics;
using System.Management;
using System.Text.Json;
using System.Runtime.InteropServices;
using TvAIr.Core;
using TvAIr.Tuner;
using TvAIr.Channel;
using TvAIr.Epg;
using System.Text.RegularExpressions;

namespace TvAIr.Schedule;

/// <summary>
/// 予約録画の実行エンジン（BackgroundService）。
///
/// 10秒ごとにDBをポーリングし、開始時刻が迫った予約をチューナー確保→TVTest起動→
/// 終了監視→status更新の順で処理する。
///
/// 競合処理:
///   空きあり           → そのまま録画開始
///   EPGが競合         → TunerPool が自動解放してから録画開始
///   視聴が競合        → Windowsトースト通知（カウントダウン）→ 強制解放→録画開始
///   録画中と競合（前番組優先設定） → status=failed に更新してログ出力
///   録画中と競合（後番組優先設定） → 前番組セッションを終了させてから録画開始
/// </summary>
public sealed 
class ReservationScheduler : BackgroundService
{

    void LogAlloc(string msg)
    {
        System.Diagnostics.Debug.WriteLine("[TUNER_ALLOC] " + msg);
    }



    


            
        // ===== v0.3.7 CHAIN_DIAGNOSTIC: チェーン録画のプロセス分断・タイトルなしTS発生経路を観測するためのログ強化。 =====


    private readonly ReservationStore _store;
    private readonly TunerPool _tunerPool;
    private readonly IniSettingsService _ini;
    private readonly IReadOnlyList<TunerProfile> _tunerProfiles;
    private readonly LogRepository _log;
    private readonly TaskSchedulerService _taskSvc;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly ChannelFileLoader _channelLoader;
    private readonly EpgStore _epgStore;
    private readonly EpgCapture _epgCapture;
    private readonly TvTestActivityKeeper _tvTestActivity;
    private readonly ChainDirectRecorderSessionRegistry _chainSessionRegistry;
    private readonly ServiceLogoStore _serviceLogoStore;
    private readonly BroadcastClockService _broadcastClock;
    private readonly UserEventLogService _userEvents;

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // 現在録画中の管理（reservationId → 録画セッション）
    private readonly Dictionary<int, RecordingSession> _activeSessions = new();
    private readonly Dictionary<int, string> _recordingTerminalFailureReasons = new();
    private readonly object _sessionGate = new();
    // BonDriverのClose/Open衝突を避けるため、録画停止処理は必ず1本ずつ直列化する。
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    // v0.6.19: チェーン境界で旧来の通常停止経路へ落とさないための観測用ガード。
    // Bridge継続/ファイル切替が実装されるまでは、同じ境界での重複抑止にも使う。
    private readonly HashSet<int> _chainBoundaryNormalStopSuppressed = new();
    private readonly HashSet<string> _chainBoundaryExecutionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _chainBoundaryLastWaitBucket = new(StringComparer.OrdinalIgnoreCase);

    private const int PollingIntervalMs = 10_000;
    // チェーン境界は通常10秒Tickを待たず、後続完全性のため高優先度で監視する。
    private const int ChainBoundaryMonitorIntervalMs = 500;
    private const int ViewingPreemptCountdownSec = 30;
    private const int RecordingLaunchWaitForFreeTunerMs = 15000;
    private const int RecordingLaunchWaitPollMs = 1000;
    // v0.4.16: 本番録画の直前/停止中/停止直後は、同一放送波のEPG新規起動を共通ゲートで止める。
    private const int RecordingDueEpgSuppressBeforeAfterSec = 180;
    private const int RecordingTimelineEpgGateSafetySeconds = 320;
    private const int RecordingStopEpgSuppressAfterSec = 45;
    private const int ChainRequestedTunerWaitMs = 15000;
    private const int ChainRequestedTunerPollMs = 250;
    // BonDriverはプロセス終了後もデバイス解放が遅れるため、Free化前に十分待つ。
    private const int PostKillDeviceReleaseWaitMs = 1_500;
    // チェーン引き継ぎ時でもBonDriver解放待ちを削り過ぎない。
    // 後続番組の前半保護は、前番組を早めに切ることで担保し、解放待ち0〜1.5秒には戻さない。
    private const int ChainPostKillDeviceReleaseWaitMs = 2_500;
    // Free化後もALLOC_ROUTE/Wake再構築を即時に走らせず、まとめて遅延させる。
    private const int PostReleaseAllocationSettleMs = 500;
    private const int ChainPostReleaseAllocationSettleMs = 500;
    private const int TvAIrEpgRecStopTimeoutMs = 4_000;
    private const int TvAIrEpgRecStopGracefulExitWaitMs = 15_000;
    private const int ChainTvAIrEpgRecStopGracefulExitWaitMs = 5_000;
    // 録画プロセスが生存していてもTSファイルが増えない状態を、予定終了まで放置しない。
    // 閾値は名前付き定義で管理し、録画開始直後・終了境界直前は誤検知を避ける。
    private const int RecordingFileGrowthInitialGraceSec = 180;
    private const int RecordingFileGrowthStallSec = 120;
    private const int RecordingFileGrowthPlannedEndGuardSec = 30;
    // v0.9.80: チェーン境界の同一SID・同一チューナー再取得だけは、通常の15秒直列化待ちを短縮する。
    // 前番組末尾欠損を許容するチェーン契約では、後続番組の開始完全性を優先するため最小settleに留める。
    private const int ChainRestartSameTunerSettleMs = 1_500;
    // チェーン後続は FinalConflictPlan/RoleBinding で確定済みの録画用チューナーを正本にする。
    // 同一チューナー境界だけ、後続開始完全性を守るため通常dueより少し前に前番組を切る。
    private const int ChainSuccessorPreArmLeadSeconds = 8;

    // Wakeタスク更新の間引き（毎ポーリングごとは不要、1分ごとに更新）
    private int _wakeUpdateCounter;
    private string _lastRecordingTimelineGateSignature = string.Empty;
    private const int WakeUpdateIntervalTicks = 6; // 10秒×6 = 60秒

    // 録画失敗の原因不明化を防ぐため、予約の存在・状態・割当・録画開始を定期監査する。
    // ログ量を抑えるため通常は1分間隔。ただし録画開始対象があるTickでは即時ログを出す。
    private int _reservationAuditCounter;
    private const int ReservationAuditIntervalTicks = 6; // 10秒×6 = 60秒

    // v0.8.13: EPGプリエンプト対象フィルタは10秒Tickで毎回同じ結果を出さない。
    // 有効予約/無効予約/システムEPG/グループ不明の状態が変わった時だけ監査ログを残す。
    private string? _lastEpgPreemptFilterSignature;

    // チューナー再評価の間引き。v34.40: 10秒ごとの全件再評価をやめ、
    // 「起動直後 / 予約接近時 / 状態変化時 / 1分ごと」に限定して負荷を下げる。
    private int _allocationReevaluateCounter;
    private const int AllocationReevaluateIntervalTicks = 6; // 10秒×6 = 60秒
    private static readonly TimeSpan NearStartReevaluateWindow = TimeSpan.FromMinutes(2);
    private bool _forceAllocationReevaluate = true;
    private bool _hadNearStartReservationOnPreviousTick;
    private bool _skipNextTickAllocationReevaluate = true;
    private string _lastPastTerminalAuditSignature = string.Empty;
    private DateTime _lastPastTerminalAuditLogUtc = DateTime.MinValue;

    // 起動時に残存するTvAIr管理下TVTestを整理した直後は、BonDriver 側が不安定になりやすい。
    // 連続 taskkill と再Open が背中合わせにならないよう、クワイエット期間を入れる。
    private const int CleanupProcessKillSpacingMs = 1500;
    private const int CleanupPostKillQuietMs = 8000;

    public ReservationScheduler(
        ReservationStore store,
        TunerPool tunerPool,
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> tunerProfiles,
        LogRepository log,
        TaskSchedulerService taskSvc,
        ReservationAllocationRouteService allocationRoute,
        ChannelFileLoader channelLoader,
        EpgStore epgStore,
        EpgCapture epgCapture,
        TvTestActivityKeeper tvTestActivity,
        ChainDirectRecorderSessionRegistry chainSessionRegistry,
        ServiceLogoStore serviceLogoStore,
        BroadcastClockService broadcastClock,
        UserEventLogService userEvents)
    {
        _store         = store;
        _tunerPool     = tunerPool;
        _ini           = ini;
        _tunerProfiles = tunerProfiles;
        _log           = log;
        _taskSvc       = taskSvc;
        _allocationRoute = allocationRoute;
        _channelLoader = channelLoader;
        _epgStore = epgStore;
        _epgCapture = epgCapture;
        _tvTestActivity = tvTestActivity;
        _chainSessionRegistry = chainSessionRegistry;
        _serviceLogoStore = serviceLogoStore;
        _broadcastClock = broadcastClock;
        _userEvents = userEvents;
    }

    // ─── BackgroundService ───────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogAlloc("Scheduler cycle start");

        

        _log.Add("Scheduler", "Init", "ReservationScheduler 開始。");
        _log.Add("Scheduler", "Init",
            $"設定確認: PseudoContinuousRecording={_ini.PseudoContinuousRecording} " +
            $"Margin={_ini.PseudoContinuousMarginSeconds}s " +
            $"LaterPriority={_ini.LaterProgramPriority} " +
            $"PreStart={_ini.PreStartMarginSeconds}s PostEnd={_ini.PostEndMarginSeconds}s");

        // v0.11.85:
        // 起動時に Recording のまま残った予約は「録画途中でTvAIr/PCが止まった残骸」として精査する。
        // Recording のまま予約一覧へ残すことは禁止。既存部分ファイルを保護し、終了前なら別予約として復旧録画を再投入する。
        // 復旧録画は同じTVTest命名規則を使い、既存ファイルと衝突した場合は MakeUniqueRecordingPath の (1)/(2) で別ファイル化する。
        var staleRecording = _store.GetByStatus(ReservationStatus.Recording).ToList();
        if (staleRecording.Count > 0)
        {
            var now = _broadcastClock.Now;
            foreach (var r in staleRecording)
            {
                var interruptedFile = ProbeInterruptedRecordingFile(r);
                if (TryFinalizePastEndedRecordingAsCompletedAtStartup(r, interruptedFile, now))
                    continue;

                var guard = EvaluateStartupRecoveryGuard(r, interruptedFile, now);
                if (!guard.ShouldRecover)
                {
                    _log.Add("REC_STARTUP_RECOVERY_GUARD", $"R{r.Id}",
                        $"result=SKIPPED reason={SafeValue(guard.Reason)} now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} file={SafeValue(interruptedFile)} workerPid={guard.WorkerPid?.ToString() ?? "-"} fileGrowing={guard.FileGrowing} completedResult={guard.CompletedResult} rule=v0.11.116_recovery_past_end_completion_guard");
                    continue;
                }

                var recoverUntil = r.EndTime.AddSeconds(Math.Max(10, _ini.PostEndMarginSeconds + 30));
                var recoverable = now <= recoverUntil && r.IsEnabled && !r.IsConflicted && r.Source != ReservationSource.Epg;
                _log.Add("REC_INTERRUPTED_DETECTED", $"R{r.Id}",
                    $"result=DETECTED recoverable={recoverable} guard=passed now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} recoverUntil={recoverUntil:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} file={SafeValue(interruptedFile)} rule=v0.11.90_startup_recovery_false_positive_guard");

                _store.FinalizeInterruptedRecordingAtStartup(r.Id, now, recoverable ? "recovery_requeued_as_new_reservation" : "outside_recoverable_window_or_not_recordable", interruptedFile);

                if (recoverable && now < r.EndTime)
                {
                    var recoveryId = _store.AddStartupRecoveryReservation(r, now);
                    _log.Add("REC_STARTUP_RECOVERY_REQUEUE", $"R{recoveryId}",
                        $"result=REQUEUED_AS_NEW source=R{r.Id} recovery=R{recoveryId} reason=recording_window_still_recoverable_and_original_finalized now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} recoverUntil={recoverUntil:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} fileCollisionPolicy=append_number_suffix rule=v0.11.90_startup_recovery_false_positive_guard");
                }
                else
                {
                    _log.Add("REC_STARTUP_RECOVERY_REQUEUE", $"R{r.Id}",
                        $"result=FINALIZED_ONLY reason={(now >= r.EndTime ? "program_already_finished" : "not_recordable_or_outside_recoverable_window")} now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} recoverUntil={recoverUntil:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} rule=v0.11.90_startup_recovery_false_positive_guard");
                }
            }
        }

        // v0.8.23: 起動時に終端予約へ残った競合フラグを整理する。
        // 共通割り当てルートの再評価前に、Completed/Cancelled を競合対象から外し、
        // 過去のCancelled行が予約一覧・監査ログで競合表示される残骸を消す。
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
                // v0.10.54: no-chain is the normal startup state; keep regular logs for actual chain configurations only.
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


    private async Task ChainBoundaryMonitorLoopAsync(CancellationToken stoppingToken)
    {
        _log.Add("CHAIN_BOUNDARY_SCHEDULER", "START",
            $"result=STARTED intervalMs={ChainBoundaryMonitorIntervalMs} normalTickMs={PollingIntervalMs} policy=high_priority_chain_boundary_monitor commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_ini.PseudoContinuousRecording)
                    await CheckPseudoContinuousHandoffAsync(_broadcastClock.Now, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Add("CHAIN_BOUNDARY_SCHEDULER", "ERROR",
                    $"result=ERROR message={TrimForLog(ex.Message, 180)} rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
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

    private bool TryBeginChainBoundaryExecution(string key)
    {
        lock (_sessionGate)
        {
            if (_chainBoundaryExecutionKeys.Contains(key))
                return false;
            _chainBoundaryExecutionKeys.Add(key);
            return true;
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
                    $"terminal予約の競合残骸を整理: affected={affected} reason={reason} rule=v0.8.23_terminal_conflict_residue_cleanup");
            }
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", "TerminalConflictCleanup",
                $"terminal予約の競合残骸整理エラー: reason={reason} error={ex.Message}");
        }
    }


    private bool IsTvAirManagedRecordingProcess(System.Diagnostics.Process proc, out string commandLine)
    {
        commandLine = TryGetCommandLine(proc.Id) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        var cmd = commandLine.ToLowerInvariant();
        var hasRec = cmd.Contains(" /rec") || cmd.Contains("\"/rec") || cmd.Contains("-rec");
        if (!hasRec)
            return false;

        // TvAIrが起動した録画/EPG用TVTestの特徴:
        //  - /rec を含む
        //  - 録画制御オプションを伴う
        // 手動視聴TVTestは通常 /rec を含まないため除外できる。
        if (cmd.Contains(" /recfile ")
            || cmd.Contains(" /recduration ")
            || cmd.Contains(" /recexit")
            || cmd.Contains(" /silent"))
            return true;

        return false;
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


    private bool TryFinalizePastEndedRecordingAsCompletedAtStartup(Reservation r, string interruptedFile, DateTime now)
    {
        // v0.11.116:
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

        var completedResult = HasCompletedRecordResultForReservation(r.Id);
        var fileEvidence = EvaluatePastEndedRecordingFileEvidence(r);
        if (completedResult && !fileEvidence.LikelyCompleted)
        {
            _log.Add("REC_STARTUP_RECOVERY_COMPLETED_REJECTED", $"R{r.Id}",
                $"result=REJECT reason=completed_result_but_file_not_complete now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} fileAudit={SafeValue(interruptedFile)} fileEvidence={SafeValue(fileEvidence.Summary)} action=finalize_as_interrupted_not_completed rule=v0.11.137_startup_partial_file_not_completed");
            return false;
        }
        if (!completedResult && !fileEvidence.LikelyCompleted)
            return false;

        _store.MarkRecordingFinished(r.Id);
        _store.UpdateStatus(r.Id, ReservationStatus.Completed, force: true);
        _log.Add("REC_STARTUP_RECOVERY_FINALIZE", $"R{r.Id}",
            $"result=COMPLETED_PAST_END reason={(completedResult ? "completed_record_result" : "recording_file_reached_program_end")} " +
            $"now={now:MM/dd HH:mm:ss} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} " +
            $"service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} fileAudit={SafeValue(interruptedFile)} " +
            $"fileEvidence={SafeValue(fileEvidence.Summary)} rule=v0.11.116_recovery_past_end_completion_guard");
        return true;
    }

    private PastEndedRecordingFileEvidence EvaluatePastEndedRecordingFileEvidence(Reservation r)
    {
        try
        {
            if (!TvTestRecordingDirectoryResolver.TryResolve(_ini.TvTestExecutablePath, out var directory, out _))
                return PastEndedRecordingFileEvidence.NotCompleted("folder_unresolved");

            var expectedName = BuildDirectRecorderFileName(r, r.Source is ReservationSource.Immediate or ReservationSource.Program
                ? (r.RecordingStartedAt ?? r.StartTime)
                : r.StartTime).FileName;
            var exactPath = Path.Combine(directory, expectedName);
            var candidates = new List<string>();
            if (File.Exists(exactPath))
            {
                candidates.Add(exactPath);
            }
            else if (Directory.Exists(directory))
            {
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
        // v0.11.138:
        // 起動時復旧でのみ使う低すぎる録画ファイルの保険値。
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

    private StartupRecoveryGuardResult EvaluateStartupRecoveryGuard(Reservation r, string interruptedFile, DateTime now)
    {
        // v0.11.90:
        // 起動時復旧は「録画workerが消え、ファイルも伸びず、正常結果も無い」場合だけ許可する。
        // TvAIr本体だけを更新/再起動した直後は、TvAIrEpgRec worker が継続録画中でも DB は Recording のまま見える。
        // ここで復旧予約を作ると (1) ファイルを誤生成するため、安全側で復旧を止める。
        if (r.RecordingFinishedAt.HasValue)
            return StartupRecoveryGuardResult.Skip("recording_finished_at_already_set", null, false, false);

        if (IsWithinStopOrCompletionQuietWindow(r, now))
            return StartupRecoveryGuardResult.Skip("inside_stop_or_completion_quiet_window", null, false, false);

        var workerPid = FindAliveTvAIrEpgRecRecordingWorkerPid(r.Id);
        if (workerPid.HasValue)
            return StartupRecoveryGuardResult.Skip("recording_worker_alive", workerPid.Value, false, false);

        if (HasCompletedRecordResultForReservation(r.Id))
        {
            if (!IsPartialInterruptedFileEvidence(interruptedFile))
                return StartupRecoveryGuardResult.Skip("completed_record_result_exists", null, false, true);

            _log.Add("REC_STARTUP_RECOVERY_GUARD", $"R{r.Id}",
                $"result=CONTINUE reason=completed_result_ignored_due_partial_file file={SafeValue(interruptedFile)} action=finalize_as_interrupted_not_completed rule=v0.11.137_startup_partial_file_not_completed");
        }

        var fileGrowing = IsRecordingFileGrowing(interruptedFile);
        if (fileGrowing)
            return StartupRecoveryGuardResult.Skip("recording_file_still_growing", null, true, false);

        return StartupRecoveryGuardResult.Recover();
    }

    private static bool IsPartialInterruptedFileEvidence(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return false;
        return evidence.Contains("partial_file_detected", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("reachedEnd=False", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithinStopOrCompletionQuietWindow(Reservation r, DateTime now)
    {
        // 停止境界では record_result / finalStatus / status=Completed への反映に数秒差がある。
        // この時間帯を復旧対象にすると、通常停止中の録画を中断扱いにしてしまう。
        return now >= r.EndTime.AddSeconds(-90) && now <= r.EndTime.AddMinutes(3);
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

    private static bool HasCompletedRecordResultForReservation(int reservationId)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "runtime", "tvairepgrec-production-recording");
            if (!Directory.Exists(dir)) return false;
            foreach (var path in Directory.EnumerateFiles(dir, $"record_result_R{reservationId}_*.json"))
            {
                var text = File.ReadAllText(path);
                if (text.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("\"success\": true", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("\"finalStatus\":\"Completed\"", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("\"finalStatus\": \"Completed\"", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // 読めない場合は完了証跡なし扱い。復旧判断は他のguardも併用する。
        }
        return false;
    }

    private static bool IsRecordingFileGrowing(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var before = new FileInfo(path).Length;
            Thread.Sleep(800);
            var after = new FileInfo(path).Length;
            return after > before;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct StartupRecoveryGuardResult(bool ShouldRecover, string Reason, int? WorkerPid, bool FileGrowing, bool CompletedResult)
    {
        public static StartupRecoveryGuardResult Recover() => new(true, "guard_passed", null, false, false);
        public static StartupRecoveryGuardResult Skip(string reason, int? workerPid, bool fileGrowing, bool completedResult) => new(false, reason, workerPid, fileGrowing, completedResult);
    }

    private static string TrimForLog(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }


    // ─── 公開API（外部からの停止要求） ──────────────────────────────

    public void StopRecording(int reservationId)
    {
        RecordingSession? session;
        lock (_sessionGate)
            _activeSessions.TryGetValue(reservationId, out session);

        var policyReservation = _store.GetById(reservationId);
        var policyService = SafeValue(policyReservation?.ServiceName);
        var policyTitle = ReservationDisplayTitle(policyReservation?.Title);
        var policyRawTitleBlank = ReservationTitleDisplayContract.RawBlankFlag(policyReservation?.Title);
        var detachedSuccessors = _store.DetachUserChainSuccessorsForManualStop(reservationId);
        _log.Add("CHAIN_STOP_POLICY", $"R{reservationId}",
            $"operation=ManualStop service={policyService} title={policyTitle} rawTitleBlank={policyRawTitleBlank} policy=current_segment_only successorAction={(detachedSuccessors.Count > 0 ? "DetachKeepScheduled" : "None")} detachedSuccessors={detachedSuccessors.Count} cancelSuccessors=False normalReservationStopRouteUnchanged=True rule=v0.11.516_chain_stop_policy_title_metadata_contract");
        if (detachedSuccessors.Count > 0)
        {
            _log.Add("CHAIN_SESSION_DETACH", $"R{reservationId}",
                $"operation=ManualStop result=SUCCESSORS_DETACHED count={detachedSuccessors.Count} targets=[{string.Join(",", detachedSuccessors.Select(x => $"R{x.Id}:{SafeValue(x.ServiceName)}:{ReservationDisplayTitle(x.Title, 40)}"))}] sessionReleaseDeferredUntilStop=True rule=v0.9.83_failed_direct_route_guard");
        }

        if (session is null)
        {
            _store.UpdateStatus(reservationId, ReservationStatus.Cancelled);
            _forceAllocationReevaluate = true;
            return;
        }

        Task.Run(/*LOG*/async () =>
        {
            await StopSessionAsync(session, ReservationStatus.Cancelled);
            _forceAllocationReevaluate = true;
            _log.Add("Scheduler", $"R{reservationId}", "録画を手動停止しました。");
        });
    }

    // ─── ポーリング ──────────────────────────────────────────────

    private async Task TickAsync(CancellationToken ct)
    {
        var now = _broadcastClock.Now;

        AuditReservationsAroundNow(now, "TickWindow", force: false);

        if (StopPhaseGate.IsStopping)
        {
            _log.Add("Scheduler", "STOP_PHASE_BLOCKED_ACTION", "action=Tick reason=stop-phase-active");
            return;
        }

        // 0. v32.85: 放送終了時刻を過ぎたのに scheduled のまま残っている予約を Cancelled に移行
        //    (無効予約・競合予約・録画失敗残骸など。予約リストを自動掃除する。)
        try
        {
            var expired = _store.ExpirePastScheduledReservations();
            if (expired > 0)
            {
                _log.Add("Scheduler", "Expire", $"放送終了予約を自動キャンセル: {expired}件");

                // v0.8.23:
                // Expire処理後の保険としてだけ実行する。通常Tickごとには走らせない。
                ClearTerminalConflictResiduesSafe("tick_after_expire");
            }
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", "Expire", $"過去予約掃除エラー: {ex.Message}");
        }

        // 1. 録画中セッションの健全性を先に確認する。
        //    予定終了より十分前に TvAIrEpgRec が消えている場合は、正常停止ルートを待たず中断として終端化する。
        //    停止境界付近は通常停止処理と競合させない。
        FinalizeActiveRecordingSessionsWithMissingProcess(now);

        // 1a. 録画プロセスが生存していても、録画TSファイルが増加しない場合は実録画停止として扱う。
        //     起動時回収だけに頼らず、録画本線の実行中に検知して止める。
        await CheckActiveRecordingFileGrowthAsync(now);

        // 1. 録画中セッションの時間追従を先に復旧する。
        //    録画中チューナー/セッションを維持したまま、最新EPGで予約EndとPlannedEndを更新する。
        //    ここで別チューナー確保や個別停止は行わず、状態更新後に共通割り当てルートを通す。
        await FollowActiveRecordingTimesAsync(now);
        RestoreActiveRecordingStatusesBeforeDecision(now, "tick_before_session_end");

        // 1b. 録画中セッションの終了チェック
        await CheckSessionEndsAsync(now);
        RestoreActiveRecordingStatusesBeforeDecision(now, "tick_after_session_end");

        // 1b. 疑似チューナー引き継ぎ: 連続番組の前番組を前倒し終了
        if (_ini.PseudoContinuousRecording)
        {
            // チェーン検出状況をログ（録画中セッションがある場合のみ）
            bool hasActive;
            lock (_sessionGate) hasActive = _activeSessions.Count > 0;
            if (hasActive)
            {
                var chains = _store.GetChains();
                if (chains.Count > 0)
                {
                    // GetChainPredecessors内でscheduledを取得済みなので、GetChainsの結果はIDのみで十分
                    // IDリストをそのままログに出す（タイトル取得のためのDB追加呼び出しは行わない）
                    var chainDesc = string.Join(" / ", chains.Select(c =>
                        string.Join("→", c.Select(id => $"R{id}"))));
                    _log.Add("Scheduler", "Chain", $"チェーン構成: {chainDesc}");
                }
            }
            await CheckPseudoContinuousHandoffAsync(now, ct);
        }

        // 2. 録画接近チェック → EPG を先手で解放
        var upcoming = GetUpcomingRecordings(now);
        PublishRecordingTimelineEpgGate(upcoming, now);
        _tunerPool.PreemptEpgForUpcomingRecordings(upcoming);

        // 3. 開始すべき予約を取得して起動
        // v34.40: 全件再評価は重いので、起動直後 / 状態変化時 / 開始接近時 / 1分ごとに限定する。
        RestoreActiveRecordingStatusesBeforeDecision(now, "tick_before_due_scan");
        MaybeReevaluateForTick(now);

        // v0.6.29: source=Epg の直前EPG確認は通常録画ルートから除外したまま、
        // 専用のDueルートで実行する。生成・Wake登録済みなのに消費だけされる退化を防ぐ。
        await RunDuePreRecordEpgEntriesAsync(now, ct);

        var scheduled = OrderDueReservationsForTransportBatchLaunch(_store.GetByStatus(ReservationStatus.Scheduled)
            .Where(r => r.IsEnabled)
            // source=Epg は予約リスト表示・Wake計算用のシステムエントリであり、
            // 通常録画のTVTest起動ルートには入れない。
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

            var hasChainContinuationDue = scheduled.Any(IsChainContinuation);
            if (StopPhaseGate.TryDeferRecordingStart("TickDueScan", dueIds, msg => _log.Add("REC_DUE_SUPPRESS", "Due", msg), bypassPostStopQuiet: hasChainContinuationDue))
            {
                foreach (var due in scheduled)
                {
                    var chain = IsChainContinuation(due);
                    _log.Add("REC_DUE_SUPPRESS", $"R{due.Id}",
                        $"result=DEFER reason=stop_or_post_stop_quiet chain={chain} nextTick=True " + FormatReservationForAudit(due, "deferred_due"));
                }
                return;
            }
        }

        var launchedAtLeastOneThisTick = false;
        Reservation? previousLaunchedThisTick = null;
        var transportBatchLaunchCount = 0;
        foreach (var r in scheduled)
        {
            ct.ThrowIfCancellationRequested();

            lock (_sessionGate)
            {
                if (_activeSessions.ContainsKey(r.Id)) continue;
            }

            // v0.11.135: 競合のままdue到達した予約は、録画開始内部へ入る前に
            // ユーザー運用ログ正本へ1予約1回だけ落とす。REC_FAILEDとは分離する。
            // ここで扱うことで、同時刻に複数dueが並ぶ場合でも後続の正常録画開始に埋もれない。
            var startCandidate = r;
            if (r.IsConflicted)
            {
                var conflictGroup = ResolveGroup(r) ?? "-";
                ReevaluateAndLog($"R{r.Id}");
                var latest = _store.GetById(r.Id);
                if (latest is null || latest.IsConflicted)
                {
                    var conflictTarget = latest ?? r;
                    _userEvents.AddRecordingSkippedByConflict(
                        conflictTarget,
                        r.Id,
                        conflictGroup,
                        "tuner_limit_exceeded");
                    _store.FinalizeSkippedByConflictAtDue(r.Id, conflictGroup, "tuner_limit_exceeded");
                    _log.Add("REC_START_DECISION", $"R{r.Id}",
                        $"result=SKIP reason=conflict_due_user_event latestExists={latest is not null} latestConflicted={(latest?.IsConflicted.ToString() ?? "-")} group={conflictGroup} terminalStatus=Failed userEvent=REC_SKIPPED_BY_CONFLICT " + FormatReservationForAudit(conflictTarget, "conflict_due_user_event"));
                    continue;
                }

                _store.UpdateConflicted(r.Id, false);
                latest.IsConflicted = false;
                startCandidate = latest;
                _forceAllocationReevaluate = true;
                _log.Add("Scheduler", SafeValue(latest.ServiceName),
                    $"競合再評価: service=[{SafeValue(latest.ServiceName)}] title=[{ReservationDisplayTitle(latest.Title)}] id=R{latest.Id} 空きチューナーを確認・競合フラグをクリアして録画開始します。group={conflictGroup} rule=v0.11.135_conflict_due_terminal_state");
            }

            if (launchedAtLeastOneThisTick && _ini.TunerSlotCooldownMs > 0)
            {
                var waitPlan = ResolveTransportBatchLaunchWait(previousLaunchedThisTick, startCandidate, transportBatchLaunchCount);
                _log.Add("REC_MULTI_START_BATCH", $"R{startCandidate.Id}",
                    $"mode={waitPlan.Mode} waitMs={waitPlan.WaitMs} baseCooldownMs={_ini.TunerSlotCooldownMs} " +
                    $"previous={(previousLaunchedThisTick is null ? "-" : $"R{previousLaunchedThisTick.Id}")} sameTransport={waitPlan.SameTransport} sameStart={waitPlan.SameStart} " +
                    $"batchCount={transportBatchLaunchCount} currentKey={BuildTransportBatchKey(startCandidate)} previousKey={(previousLaunchedThisTick is null ? "-" : BuildTransportBatchKey(previousLaunchedThisTick))} " +
                    $"reason={waitPlan.Reason} rule=v1.0.0_due_recording_batch_launch_contract");
                if (waitPlan.WaitMs > 0)
                {
                    await Task.Delay(waitPlan.WaitMs, ct);
                }
            }

            var launched = await StartRecordingAsync(startCandidate, ct);
            if (launched)
            {
                launchedAtLeastOneThisTick = true;
                if (previousLaunchedThisTick is not null && IsSameTransportBatch(previousLaunchedThisTick, startCandidate) && IsSameStartBucket(previousLaunchedThisTick, startCandidate))
                    transportBatchLaunchCount = transportBatchLaunchCount >= 2 ? 1 : transportBatchLaunchCount + 1;
                else
                    transportBatchLaunchCount = 1;
                previousLaunchedThisTick = startCandidate;
            }
        }

        // 4. Wakeタスク更新。
        // v0.5.66: 自動検索予約更新などからのWake再構築要求は視聴中負荷を避けて遅延・差分化されるため、
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




    private static string BuildPreRecordEpgRuntimeTitle(Reservation parent, string tunerName)
    {
        var title = parent.Title?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(title)
            && !EpgTitleProjectionGuard.IsInternalPurposeTitle(title)
            && !title.Equals("SystemEpg", StringComparison.OrdinalIgnoreCase)
            && !title.Equals("PreRecEpg", StringComparison.OrdinalIgnoreCase))
            return title;
        return !string.IsNullOrWhiteSpace(parent.ServiceName)
            ? parent.ServiceName
            : "番組タイトル未解決";
    }

    private static bool IsPreRecordEpgEntry(Reservation r)
    {
        if (r.Source != ReservationSource.Epg) return false;
        if (!r.SourceRuleId.HasValue) return false;
        if (string.Equals(r.SourceRuleName, "PreRecEpg", StringComparison.OrdinalIgnoreCase)) return true;
        // Legacy compatibility: v0.11.175 and earlier stored internal purpose in title.
        return r.Title.StartsWith("EPG確認", StringComparison.Ordinal);
    }

    private async Task RunDuePreRecordEpgEntriesAsync(DateTime now, CancellationToken ct)
    {
        var candidates = _store.GetByStatus(ReservationStatus.Scheduled)
            .Where(r => r.Source == ReservationSource.Epg)
            .Where(r => r.IsEnabled)
            .Where(IsPreRecordEpgEntry)
            .Where(r => r.StartTime <= now)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        if (candidates.Count == 0) return;

        foreach (var epg in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (epg.EndTime <= now)
            {
                _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                    $"result=SKIP reason=window_expired epg=R{epg.Id} parent={(epg.SourceRuleId.HasValue ? $"R{epg.SourceRuleId.Value}" : "-")} start={epg.StartTime:MM/dd HH:mm:ss} end={epg.EndTime:MM/dd HH:mm:ss} now={now:MM/dd HH:mm:ss} rule=v0.6.37_prerec_cancelled_child_completion_guard");
                TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
                continue;
            }

            var parent = epg.SourceRuleId.HasValue ? _store.GetById(epg.SourceRuleId.Value) : null;
            if (parent is null)
            {
                _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                    $"result=SKIP reason=parent_missing epg=R{epg.Id} parent={(epg.SourceRuleId.HasValue ? $"R{epg.SourceRuleId.Value}" : "-")} rule=v0.6.37_prerec_cancelled_child_completion_guard");
                TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
                continue;
            }

            if (IsExpiredPreRecordEpgForParent(epg, parent, now))
            {
                _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                    $"result=SKIP reason=deadline_already_passed epg=R{epg.Id} parent=R{parent.Id} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} epgStart={epg.StartTime:MM/dd HH:mm:ss} parentCreated={parent.CreatedAt:MM/dd HH:mm:ss} epgCreated={epg.CreatedAt:MM/dd HH:mm:ss} now={now:MM/dd HH:mm:ss} action=recording_priority rule=v0.11.141_prerec_deadline_epg_blocked_classification");
                TryCompletePreRecordEpgEntry(epg, "skip_deadline_already_passed");
                continue;
            }

            if (parent.Status == ReservationStatus.Recording)
            {
                _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                    $"result=SKIP reason=parent_already_recording epg=R{epg.Id} parent=R{parent.Id} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=v0.6.37_prerec_cancelled_child_completion_guard");
                TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
                continue;
            }

            if (parent.Status != ReservationStatus.Scheduled || !parent.IsEnabled || parent.IsConflicted)
            {
                _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                    $"result=SKIP reason=parent_not_recordable epg=R{epg.Id} parent=R{parent.Id} status={parent.Status} enabled={parent.IsEnabled} conflicted={parent.IsConflicted} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=v0.6.37_prerec_cancelled_child_completion_guard");
                TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
                continue;
            }

            var group = ResolveGroup(parent);
            if (string.IsNullOrWhiteSpace(group))
            {
                _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                    $"result=SKIP reason=group_unresolved epg=R{epg.Id} parent=R{parent.Id} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=v0.6.37_prerec_cancelled_child_completion_guard");
                TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
                continue;
            }

            var status = _epgCapture.GetStatus();
            if (string.Equals(status.Phase, "running", StringComparison.OrdinalIgnoreCase))
            {
                _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                    $"result=CONTINUE reason=pre_record_priority_over_normal_epg epg=R{epg.Id} parent=R{parent.Id} phase={status.Phase} runPurpose={SafeValue(status.RunPurpose)} targetScope={SafeValue(status.TargetScope)} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} action=preempt_normal_epg_and_run_prerec rule=v0.9.96_prerec_pretune_preempts_normal_epg");
                try
                {
                    var preempted = await _epgCapture.PreemptNormalEpgWorkersForPreRecordAsync(group, $"pre_record_epg_R{epg.Id}_parent_R{parent.Id}", ct).ConfigureAwait(false);
                    _log.Add("PRE_REC_PRETUNE_PREEMPT_NORMAL_EPG", $"R{epg.Id}",
                        $"result=REQUESTED parent=R{parent.Id} group={SafeValue(group)} preemptedWorkers={preempted} action=continue_pre_record_epg_without_defer service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=v0.9.96_prerec_pretune_preempts_normal_epg");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Add("PRE_REC_PRETUNE_PREEMPT_NORMAL_EPG", $"R{epg.Id}",
                        $"result=WARN parent=R{parent.Id} group={SafeValue(group)} error={ex.GetType().Name}:{SafeValue(ex.Message)} action=continue_pre_record_epg_without_defer rule=v0.9.96_prerec_pretune_preempts_normal_epg");
                }
            }

            var dueStart = parent.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
            var schedulerNow = _broadcastClock.Now;
            var secondsUntilDueStart = (int)Math.Floor((dueStart - schedulerNow).TotalSeconds);
            // v0.10.07:
            // SleepGuard は TvAIrEpgRec.exe を監視しているため、録画前EPG確認workerを
            // 録画due直前まで維持し、PreRecEpg終了→Record起動のプロセス空白を最小化する。
            // これはチューナー予備確保や待ち時間延長ではなく、既存TvAIrEpgRec監視前提のハンドオフ調整。
            const int preRecSafetySeconds = 1;
            const int preRecProbeCeilingSeconds = 90;
            var maxAllowedSeconds = secondsUntilDueStart - preRecSafetySeconds;
            if (maxAllowedSeconds < 8)
            {
                _log.Add("PRE_REC_EPG_DUE_SCAN", $"R{epg.Id}",
                    $"result=SKIP reason=too_close_to_recording epg=R{epg.Id} parent=R{parent.Id} secondsUntilDueStart={secondsUntilDueStart} safetySeconds={preRecSafetySeconds} minProbeSeconds=8 dueStart={dueStart:MM/dd HH:mm:ss} now={schedulerNow:MM/dd HH:mm:ss} action=recording_priority_and_follow_during_recording service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=v0.6.37_prerec_cancelled_child_completion_guard");
                TryCompletePreRecordEpgEntry(epg, "skip_before_probe");
                continue;
            }
            var preRecProbeSafetyCeilingSeconds = Math.Max(8, Math.Min(preRecProbeCeilingSeconds, maxAllowedSeconds));

            var chainContext = BuildPreTuneChainContext(parent);
            if (chainContext.SkipNewWorker)
            {
                _log.Add("PRE_REC_PRETUNE_PLAN", $"R{epg.Id}",
                    $"parent=R{parent.Id} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} group={group} chainPosition={chainContext.ChainPosition} action={chainContext.Action} preferredRecordingTuner={SafeValue(parent.TunerName)} predecessor=R{(parent.UserChainPreviousId.HasValue ? parent.UserChainPreviousId.Value.ToString() : "-")} reason={chainContext.Reason} policy=use_existing_recording_session_no_extra_window rule=v0.9.94_chain_head_pretune_state_transition");
                TryCompletePreRecordEpgEntry(epg, "skip_successor_existing_session");
                continue;
            }

            var displayTuner = string.IsNullOrWhiteSpace(chainContext.PreferredTunerName) ? parent.TunerName : chainContext.PreferredTunerName;
            if (!string.IsNullOrWhiteSpace(displayTuner)
                && !string.Equals(epg.TunerName, displayTuner, StringComparison.OrdinalIgnoreCase))
            {
                _store.RebindScheduledPreRecordEpgEntry(epg.Id, parent.Id, epg.StartTime, epg.EndTime, displayTuner);
                _log.Add("PRE_REC_PRETUNE_META_REBIND", $"R{epg.Id}",
                    $"result=OK epg=R{epg.Id} parent=R{parent.Id} oldTuner={SafeValue(epg.TunerName)} displayTuner={SafeValue(displayTuner)} recordingTuner={SafeValue(parent.TunerName)} reason=align_prerec_child_metadata_with_actual_pretune_route rule=v0.10.10_prerec_child_tuner_metadata_align");
                epg.TunerName = displayTuner;
                epg.Title = BuildPreRecordEpgRuntimeTitle(parent, displayTuner);
            }

            _log.Add("PRE_REC_EPG_START", $"R{epg.Id}",
                $"epg=R{epg.Id} parent=R{parent.Id} group={group} tuner={SafeValue(epg.TunerName)} recordingTuner={SafeValue(parent.TunerName)} chainPosition={chainContext.ChainPosition} preTuneAction={chainContext.Action} window={epg.StartTime:MM/dd HH:mm:ss}〜{epg.EndTime:MM/dd HH:mm:ss} probeSafetyCeilingSeconds={preRecProbeSafetyCeilingSeconds} dueStart={dueStart:MM/dd HH:mm:ss} safetySeconds={preRecSafetySeconds} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} nid={parent.NetworkId} tsid={parent.TransportStreamId} sid={parent.ServiceId} eventId={parent.EventId} policy=silent_time_follow_probe_and_record_pretune_same_tuner rule=v0.10.10_prerec_child_tuner_metadata_align");

            _log.Add("PRE_REC_PRETUNE_PLAN", $"R{epg.Id}",
                $"parent=R{parent.Id} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} group={group} chainPosition={chainContext.ChainPosition} action={chainContext.Action} preferredRecordingTuner={SafeValue(chainContext.PreferredTunerName)} expectedNid={parent.NetworkId} expectedTsid={parent.TransportStreamId} expectedSid={parent.ServiceId} expectedEventId={parent.EventId} keepWorkerUntilSafetyCeiling={chainContext.KeepWorkerUntilSafetyCeiling} taskbarIcon=setting_dependent policy=reduce_cross_tuner_record_start_failure rule=v0.9.94_chain_head_pretune_state_transition");

            try
            {
                _broadcastClock.LogPreRecordClockState($"R{epg.Id}", parent.ServiceName, parent.Title);

                var result = await _epgCapture.RunAsync(
                    ct: ct,
                    targetScope: group,
                    runDepth: null,
                    expectedNetworkId: parent.NetworkId,
                    expectedTransportStreamId: parent.TransportStreamId,
                    expectedServiceId: parent.ServiceId,
                    expectedServiceName: parent.ServiceName,
                    expectedEventId: parent.EventId,
                    expectedStartTime: parent.StartTime,
                    expectedEndTime: parent.EndTime,
                    isPreRecordCheck: true,
                    maxCaptureSeconds: preRecProbeSafetyCeilingSeconds,
                    showProgress: false,
                    preferredRecordingTunerName: chainContext.PreferredTunerName,
                    preTuneChainPosition: chainContext.ChainPosition,
                    preTuneAction: chainContext.Action,
                    preTuneKeepWorkerUntilSafetyCeiling: chainContext.KeepWorkerUntilSafetyCeiling);

                var completed = TryCompletePreRecordEpgEntry(epg, "probe_finished");
                var latestParent = _store.GetById(parent.Id) ?? parent;
                if (!completed || !IsRecordableForPreRecordFollow(latestParent))
                {
                    _log.Add("PRE_REC_EPG_RESULT", $"R{epg.Id}",
                        $"result=SKIP_AFTER_PROBE epg=R{epg.Id} parent=R{parent.Id} probe=completed reason={(completed ? "parent_not_recordable_after_probe" : "epg_cancelled_during_probe")} parentStatus={latestParent.Status} parentEnabled={latestParent.IsEnabled} parentConflicted={latestParent.IsConflicted} action=do_not_apply_time_follow service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=v0.6.37_prerec_cancelled_child_completion_guard");
                    continue;
                }

                var followResults = _store.ApplyTimeFollowingDetailed(new[] { latestParent }, _epgStore);
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
                    $"result={(result.Success ? "OK" : "FAIL")} epg=R{epg.Id} parent=R{parent.Id} probe=completed followOutcome={followOutcome} timeFollowUpdated={followUpdated} action=keep_or_update_parent_by_time_follow service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=v0.6.37_prerec_cancelled_child_completion_guard");
                if (!result.Success)
                    _userEvents.AddPreRecordEpgFailed(latestParent, "probe_failed");
            }
            catch (OperationCanceledException)
            {
                _log.Add("PRE_REC_EPG_RESULT", $"R{epg.Id}",
                    $"result=CANCELLED epg=R{epg.Id} parent=R{parent.Id} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=v0.6.37_prerec_cancelled_child_completion_guard");
                throw;
            }
            catch (Exception ex)
            {
                _log.Add("PRE_REC_EPG_RESULT", $"R{epg.Id}",
                    $"result=ERROR epg=R{epg.Id} parent=R{parent.Id} error={ex.GetType().Name}:{SafeValue(ex.Message)} service={SafeValue(parent.ServiceName)} title={ReservationDisplayTitle(parent.Title)} rule=v0.6.37_prerec_cancelled_child_completion_guard");
                _userEvents.AddPreRecordEpgFailed(parent, $"{ex.GetType().Name}: {ex.Message}");
                _store.UpdateStatus(epg.Id, ReservationStatus.Failed, force: true);
            }
        }
    }


    private static bool IsExpiredPreRecordEpgForParent(Reservation epgEntry, Reservation parent, DateTime now)
    {
        // v0.11.141: 予約作成・再構築より前に予定されていた録画前EPG確認を後追い実行しない。
        // 通常のスケジューラtick遅延では parent.CreatedAt <= epgEntry.StartTime なので実行を許可する。
        var parentCreated = parent.CreatedAt == default ? DateTime.MinValue : parent.CreatedAt;
        var epgCreated = epgEntry.CreatedAt == default ? DateTime.MinValue : epgEntry.CreatedAt;
        return parentCreated > epgEntry.StartTime.AddSeconds(5)
            || epgCreated > epgEntry.StartTime.AddSeconds(5);
    }

    private bool TryCompletePreRecordEpgEntry(Reservation epgEntry, string reason)
    {
        var current = _store.GetById(epgEntry.Id);
        if (current is not null && current.Status == ReservationStatus.Cancelled)
        {
            _log.Add("PRE_REC_EPG_RESULT", $"R{epgEntry.Id}",
                $"result=SKIP_STATUS_UPDATE epg=R{epgEntry.Id} reason={reason} currentStatus=Cancelled action=keep_cancelled_state rule=v0.6.37_prerec_cancelled_child_completion_guard");
            return false;
        }

        // v0.10.11:
        // 録画前EPG確認の実行中に親予約の暫定チューナーが再評価で揺れると、
        // 子予約タイトルだけ古い「EPG確認（S1）」等へ戻ることがあった。
        // 完了直前に、実際にこのPreRecEpgが使った表示チューナーへ再同期してからCompletedへ進める。
        if (!string.IsNullOrWhiteSpace(epgEntry.TunerName) && epgEntry.Source == ReservationSource.Epg)
        {
            var parentId = epgEntry.SourceRuleId ?? 0;
            if (parentId > 0)
            {
                _store.RebindScheduledPreRecordEpgEntry(epgEntry.Id, parentId, epgEntry.StartTime, epgEntry.EndTime, epgEntry.TunerName);
                _log.Add("PRE_REC_EPG_DISPLAY_TUNER_FINALIZE", $"R{epgEntry.Id}",
                    $"result=OK epg=R{epgEntry.Id} parent=R{parentId} displayTuner={SafeValue(epgEntry.TunerName)} reason=finalize_child_display_tuner_before_completed rule=v0.10.11_wake_plan_generation_slot_guard");
            }
        }

        _store.UpdateStatus(epgEntry.Id, ReservationStatus.Completed);
        return true;
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
            $"source=PreRecEpg target=R{parent.Id} epg=R{epgEntry.Id} inspected={results.Count} updated={updated} rule=v0.6.37_prerec_cancelled_child_completion_guard");

        foreach (var r in results)
        {
            _log.Add("EPG_SCHEDULER", r.Updated ? "TimeFollowUpdated" : "TimeFollowNoChange",
                $"source=PreRecEpg parent=R{r.ReservationId} epg=R{epgEntry.Id} result={(r.Updated ? "UPDATED" : "NO_CHANGE")} reason={r.Reason} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} old={r.OldStart:MM/dd HH:mm:ss}〜{r.OldEnd:MM/dd HH:mm:ss} new={(r.NewStart.HasValue ? r.NewStart.Value.ToString("MM/dd HH:mm:ss") : "-")}〜{(r.NewEnd.HasValue ? r.NewEnd.Value.ToString("MM/dd HH:mm:ss") : "-")} rule=v0.6.37_prerec_cancelled_child_completion_guard");
            if (r.Updated && r.NewStart.HasValue && r.NewEnd.HasValue)
            {
                var latest = _store.GetById(r.ReservationId);
                if (latest is not null)
                    _userEvents.AddTimeFollowUpdated(latest, r.OldStart, r.OldEnd, r.NewStart.Value, r.NewEnd.Value);
            }
        }
    }

    // ─── 録画開始 ────────────────────────────────────────────────


    private async Task<bool> StartRecordingAsync(Reservation r, CancellationToken ct)
    {
        var chainContinuationAtStart = IsChainContinuation(r);
        if (StopPhaseGate.TryDeferRecordingStart("StartRecordingAsync", $"R{r.Id}", msg => _log.Add("REC_DUE_SUPPRESS", $"R{r.Id}", msg), bypassPostStopQuiet: chainContinuationAtStart))
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=DEFER reason=stop_or_post_stop_quiet stage=start_request chain={chainContinuationAtStart} nextTick=True " + FormatReservationForAudit(r, "start_deferred"));
            return false;
        }

        if (chainContinuationAtStart && StopPhaseGate.IsPostStopQuietActive)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                "result=CONTINUE reason=chain_boundary_restart_bypassed_post_stop_quiet stage=start_request " +
                FormatReservationForAudit(r, "start_quiet_bypass"));
        }

        if (r.Source == ReservationSource.Epg)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                "result=SKIP reason=system_epg_entry_excluded_from_recording_route " + FormatReservationForAudit(r, "system_epg_skip"));
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

        // v0.9.81: 競合予約は録画開始系の前処理（REC_PREEMPT_GROUP_LOCK / EPG preempt）に入れない。
        // 先に共通割り当てルートで再評価し、まだ競合なら静かにスキップする。
        // これにより、競合表示は維持しつつ、毎Tickの録画前抑止ログ増殖を止める。
        if (r.IsConflicted)
        {
            ReevaluateAndLog($"R{r.Id}");
            var latest = _store.GetById(r.Id);
            if (latest is null || latest.IsConflicted)
            {
                var conflictTarget = latest ?? r;
                _userEvents.AddRecordingSkippedByConflict(
                    conflictTarget,
                    r.Id,
                    group,
                    "tuner_limit_exceeded");
                _store.FinalizeSkippedByConflictAtDue(r.Id, group, "tuner_limit_exceeded");
                _log.Add("REC_START_DECISION", $"R{r.Id}",
                    $"result=SKIP reason=conflict_still_true_before_preempt latestExists={latest is not null} latestConflicted={(latest?.IsConflicted.ToString() ?? "-")} group={group} terminalStatus=Failed userEvent=REC_SKIPPED_BY_CONFLICT " + FormatReservationForAudit(conflictTarget, "conflict_preempt_suppressed"));
                return false;
            }

            _store.UpdateConflicted(r.Id, false);
            r.IsConflicted = false;
            _forceAllocationReevaluate = true;
            _log.Add("Scheduler", SafeValue(r.ServiceName),
                $"競合再評価: service=[{SafeValue(r.ServiceName)}] title=[{ReservationDisplayTitle(r.Title)}] id=R{r.Id} 空きチューナーを確認・競合フラグをクリアして録画開始します。group={group} rule=v0.9.83_failed_direct_route_guard");
        }

        var recDueSuppressUntil = DateTime.Now.AddSeconds(RecordingDueEpgSuppressBeforeAfterSec);
        RecordingLifecycleGate.SuppressEpg(
            group,
            recDueSuppressUntil,
            "recording_due_or_launching",
            $"R{r.Id}",
            FormatReservationForLifecycleLog(r));
        _log.Add("REC_PREEMPT_GROUP_LOCK", $"R{r.Id}",
            $"group={group} until={recDueSuppressUntil:MM/dd HH:mm:ss} reason=recording_due_or_launching " +
            FormatReservationForAudit(r, "epg_suppressed_before_recording"));

        // v0.3.98: 予約録画は手動/定時EPG取得より常に上位。
        // TunerPoolだけを先にFree化すると、TVTest/BonDriver実体がまだ終了していないDIDを録画が踏むため、
        // ここで同一グループのTvAIr管理EPGプロセスを停止し、終了確認とクールダウンを済ませてから録画判断へ進む。
        await PreemptManagedEpgBeforeRecordingAsync(group, r, ct);

        // 視聴競合チェック（今すぐ録画等でViewingが占有している場合）
        var hasFreeBeforeViewing = _tunerPool.HasFreeSlot(group);
        var hasViewing = _tunerPool.HasViewingSlot(group);
        _log.Add("REC_TUNER_CHECK", $"R{r.Id}", $"stage=before_viewing_preempt group={group} hasFree={hasFreeBeforeViewing} hasViewing={hasViewing} tuner={SafeValue(r.TunerName)}");
        if (!hasFreeBeforeViewing && hasViewing)
        {
            _log.Add("TUNER_PROTECT", "Viewing", $"result=KEEP_VIEWING reason=no_recordable_free_slot group={group} reservation=R{r.Id} tuner={SafeValue(r.TunerName)}");
        }

        // チューナーが取れない場合は最大15秒待つ（BonDriver解放遅延対策）。
        var hasFreeBeforeLaunch = _tunerPool.HasFreeSlot(group);
        _log.Add("REC_TUNER_CHECK", $"R{r.Id}", $"stage=before_launch group={group} hasFree={hasFreeBeforeLaunch} tuner={SafeValue(r.TunerName)} conflicted={r.IsConflicted}");
        if (!hasFreeBeforeLaunch)
        {
            var waitedMs = 0;
            while (waitedMs < RecordingLaunchWaitForFreeTunerMs && !_tunerPool.HasFreeSlot(group))
            {
                var waitStepMs = Math.Min(RecordingLaunchWaitPollMs, RecordingLaunchWaitForFreeTunerMs - waitedMs);
                _log.Add("REC_TUNER_WAIT", $"R{r.Id}",
                    $"waiting_for_free_tuner group={group} waitedMs={waitedMs} nextWaitMs={waitStepMs} limitMs={RecordingLaunchWaitForFreeTunerMs}");
                await Task.Delay(waitStepMs, ct);
                waitedMs += waitStepMs;
            }

            hasFreeBeforeLaunch = _tunerPool.HasFreeSlot(group);
            _log.Add("REC_TUNER_CHECK", $"R{r.Id}",
                $"stage=after_wait_for_free_tuner group={group} hasFree={hasFreeBeforeLaunch} waitedMs={waitedMs}");
            if (!hasFreeBeforeLaunch)
            {
                _log.Add("REC_START_DECISION", $"R{r.Id}", $"result=FAIL reason=no_free_tuner_before_launch_after_wait group={group} waitedMs={waitedMs}");
                Fail(r, $"チューナー不足により録画できませんでした。group={group} wait={waitedMs}ms");
                return false;
            }
        }

        return await LaunchNewRecordingAsync(r, group, ct);
    }

    // ─── 通常録画起動 ────────────────────────────────────────────

    private async Task<bool> LaunchNewRecordingAsync(Reservation r, string group, CancellationToken ct)
    {
        var basePlannedEnd = r.EndTime.AddSeconds(_ini.PostEndMarginSeconds);
        var plannedEnd = ResolveChainPlannedEndForLaunch(r, basePlannedEnd);
        _log.Add("REC_LAUNCH_PREP", $"R{r.Id}",
            $"stage=launch_prepare group={group} plannedEnd={plannedEnd:MM/dd HH:mm:ss} basePlannedEnd={basePlannedEnd:MM/dd HH:mm:ss} preStart={_ini.PreStartMarginSeconds}s postEnd={_ini.PostEndMarginSeconds}s chainMode=assigned_tuner_prearm " + FormatReservationForAudit(r, "launch"));
        _log.Add("RESERVATION_PIPELINE_AUDIT", $"R{r.Id}",
            BuildReservationPipelineAudit(r, "launch_prepare", group: group, plannedTuner: EffectiveTunerName(r), plannedEnd: plannedEnd, finalGuardApplied: false, note: "before_channel_resolve_and_tuner_acquire"));

        var chainContinuationAtLaunch = IsChainContinuation(r);
        if (StopPhaseGate.TryDeferRecordingStart("LaunchNewRecordingAsync", $"R{r.Id}", msg => _log.Add("REC_DUE_SUPPRESS", $"R{r.Id}", msg), bypassPostStopQuiet: chainContinuationAtLaunch))
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                $"result=DEFER reason=stop_or_post_stop_quiet stage=before_tuner_acquire chain={chainContinuationAtLaunch} nextTick=True " + FormatReservationForAudit(r, "launch_deferred"));
            return false;
        }

        if (chainContinuationAtLaunch && StopPhaseGate.IsPostStopQuietActive)
        {
            _log.Add("REC_START_DECISION", $"R{r.Id}",
                "result=CONTINUE reason=chain_boundary_restart_bypassed_post_stop_quiet stage=before_tuner_acquire " +
                FormatReservationForAudit(r, "launch_quiet_bypass"));
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

        // v0.9.76: チェーン後続は既存TvAIrEpgRecセッションへ接続しない。
        // 前番組を後番組開始前マージンで明示停止し、同じ実チューナーを通常録画起動で再取得する。
        // これにより「1番組=1TSファイル」を維持し、前番組末尾欠損を契約上の許容範囲に閉じ込める。
        if (IsChainContinuation(r))
        {
            _log.Add("CHAIN_RESTART_ROUTE", $"R{r.Id}",
                $"stage=before_record_start result=USE_ASSIGNED_TUNER_PREARM reason=program_file_per_reservation successor=R{r.Id} " +
                $"assignedTuner={SafeValue(r.TunerName)} plannedEnd={plannedEnd:MM/dd HH:mm:ss} activeAttachDisabled=True commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
        }

        var pt3StartLockWaitAt = DateTime.Now;
        _log.Add("CHAIN_TRACE", $"R{r.Id}", $"[CHAIN] stage=pt3_start_lock_wait_start group={group} status={_tunerPool.GetStatusSummary()}");
        using var tunerDeviceAccess = await TunerDeviceAccessGate.EnterAsync($"REC_START R{r.Id}", msg => _log.Add("TUNER_DEVICE_LOCK", $"R{r.Id}", msg));
        var pt3StartLockWaitMs = (int)(DateTime.Now - pt3StartLockWaitAt).TotalMilliseconds;
        _log.Add("CHAIN_TRACE", $"R{r.Id}", $"[CHAIN] stage=pt3_start_lock_entered waitMs={pt3StartLockWaitMs} group={group} status={_tunerPool.GetStatusSummary()}");



        // 事前割り当てチューナーを優先して確保する。
        // チェーン予約では「指定チューナーが取れないなら別チューナーへ逃がす」を禁止する。
        // Cマークは同一チューナー引き継ぎが前提のため、ここで通常空きチューナーへフォールバックすると
        // 前番組末尾カットだけが発生し、後続が同一物理チューナーで始まらない不整合になる。
        var chainContinuation = IsChainContinuation(r);
        _log.Add("CHAIN_TRACE", $"R{r.Id}", $"[CHAIN] stage=before_tuner_acquire chain={chainContinuation} group={group} requestedTuner={SafeValue(r.TunerName)} status={_tunerPool.GetStatusSummary()}");
        LogChainRecordingEvaluation(r, group, chainContinuation, "before_tuner_acquire");

        // 外部TVTestは明示検出できたDID/BonDriverの衝突だけを見る。
        // DID不明の外部TVTestを理由に録画用スロットを推測で空けない。
        var preAcquireViewingAudit = TvTestProcessAuditor.EmitViewingProtectionAudit(
            _log,
            "REC_START_PRE_ACQUIRE",
            $"R{r.Id}",
            targetDid: null,
            targetBonDriver: null,
            protectedViewingDids: _tunerPool.GetProtectedViewingDids(),
            blockOnSameDid: false);
        var unknownExternalLiveGuard = preAcquireViewingAudit.UnknownLiveDid
            && preAcquireViewingAudit.Processes.Any(p => p.IsLiveViewing)
            && !chainContinuation;
        _log.Add("TUNER_EXTERNAL_GUARD", $"R{r.Id}",
            $"enabled={unknownExternalLiveGuard} chain={chainContinuation} unknownLiveDid={preAcquireViewingAudit.UnknownLiveDid} " +
            $"liveViewingCount={preAcquireViewingAudit.Processes.Count(p => p.IsLiveViewing)} group={group} requestedTuner={SafeValue(r.TunerName)} " +
            "rule=v0.4.8_unknown_external_live_reserve_one_recording_slot");

        TunerLease? lease = null;
        var predIdForActualChain = chainContinuation ? TryResolveChainPredecessorId(r, out _) : null;
        var activePredSession = predIdForActualChain.HasValue ? TryGetActiveSession(predIdForActualChain.Value) : null;

        // v0.4.12: チェーン後続は「通常のT/S優先順位で再解決」してはいけない。
        // 前番組が録画中なら実Lease.Name、停止済みなら予約に引き継がれたTunerNameをチェーン継承候補として扱い、
        // その同一仮想チューナーを最優先で再取得する。これにより TSS「サバ缶、宇宙へ行く」→「TSSニュースナイト」
        // のように前番組が T2 実録画だったのに後続が T1 へ戻る不整合を防ぐ。
        if (chainContinuation)
        {
            // v0.11.678: チェーン後続のチューナー正本は FinalConflictPlan が予約へ永続化した TunerName。
            // active predecessor の実チューナーで上書き・継承しない。ここを崩すと ALLOC_ROUTE/TUNER_ALLOC の横串が切れる。
            var chainPreferredTuner = r.TunerName;
            var chainPreferredSource = "final_plan_assigned_tuner";

            if (string.IsNullOrWhiteSpace(chainPreferredTuner))
            {
                _log.Add("CHAIN_ASSIGNED_TUNER_CONTRACT", $"R{r.Id}",
                    $"result=FAIL reason=missing_final_plan_assigned_tuner successor=R{r.Id} predecessor={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} " +
                    $"activePred={(activePredSession is not null)} activePredTuner={(activePredSession is null ? "-" : SafeValue(activePredSession.Lease.Name))} status={_tunerPool.GetStatusSummary()} " +
                    $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC action=fail_no_fallback rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
                Fail(r, "チェーン後続予約の割当チューナーが未確定のため録画を開始できませんでした。");
                return false;
            }

            var predecessorTuner = activePredSession?.Lease.Name ?? string.Empty;
            var sameAsActivePredecessor = !string.IsNullOrWhiteSpace(predecessorTuner)
                && string.Equals(predecessorTuner, chainPreferredTuner, StringComparison.OrdinalIgnoreCase);

            _log.Add("CHAIN_ASSIGNED_TUNER_CONTRACT", $"R{r.Id}",
                $"result=OK successor=R{r.Id} predecessor={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} " +
                $"assignedTuner={SafeValue(chainPreferredTuner)} source={chainPreferredSource} activePred={(activePredSession is not null)} " +
                $"activePredTuner={(activePredSession is null ? "-" : SafeValue(activePredSession.Lease.Name))} sameAsActivePredecessor={sameAsActivePredecessor} " +
                $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC action=use_assigned_tuner_no_inherit_override rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");

            var plannedActualMismatchBeforeAcquire = false;
            _log.Add("CHAIN_RECORDING_RUNTIME_AUDIT", $"R{r.Id}",
                $"result=EXPECT_ASSIGNED_TUNER stage=before_tuner_wait successor=R{r.Id} predecessor={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} " +
                $"expectedTuner={SafeValue(chainPreferredTuner)} expectedSource={chainPreferredSource} handoffSource=final_plan_assigned_tuner sourceTransition=common_route_assigned_tuner_preserved sourceDecision=do_not_inherit_predecessor_actual_tuner " +
                $"plannedTuner={SafeValue(r.TunerName)} actualTuner=- plannedActualMismatch={plannedActualMismatchBeforeAcquire} inheritMatchedActual=- reservationTuner={SafeValue(r.TunerName)} " +
                $"activePred={(activePredSession is not null)} activePredPid={(activePredSession?.ProcessId.ToString() ?? "-")} " +
                $"activePredTuner={(activePredSession is null ? "-" : SafeValue(activePredSession.Lease.Name))} activePredDid={(activePredSession is null ? "-" : SafeValue(activePredSession.Lease.Did))} " +
                $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC stopRestart=False assignedTunerRequired=True behaviorChanged=True rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");

            var waitedMs = 0;
            while (waitedMs < ChainRequestedTunerWaitMs && !_tunerPool.HasFreeSlotByName(chainPreferredTuner))
            {
                var waitStepMs = Math.Min(ChainRequestedTunerPollMs, ChainRequestedTunerWaitMs - waitedMs);
                _log.Add("REC_CHAIN_TUNER_WAIT", $"R{r.Id}",
                    $"waiting_for_assigned_chain_tuner assignedTuner={SafeValue(chainPreferredTuner)} source={chainPreferredSource} " +
                    $"pred={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} sameAsActivePredecessor={sameAsActivePredecessor} " +
                    $"waitedMs={waitedMs} nextWaitMs={waitStepMs} limitMs={ChainRequestedTunerWaitMs} status={_tunerPool.GetStatusSummary()} rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
                await Task.Delay(waitStepMs);
                waitedMs += waitStepMs;
            }

            lease = _tunerPool.AcquireForRecordingByName(chainPreferredTuner, r.Id, plannedEnd);
            if (lease is null)
            {
                _log.Add("REC_CHAIN_TUNER_INHERIT", $"R{r.Id}",
                    $"result=FAIL reason=inherited_tuner_unavailable_no_silent_fallback chainPreferredTuner={SafeValue(chainPreferredTuner)} " +
                    $"source={chainPreferredSource} waitedMs={waitedMs} status={_tunerPool.GetStatusSummary()}");
                Fail(r, $"チェーン予約の継承チューナーを確保できませんでした。tuner={chainPreferredTuner} wait={waitedMs}ms");
                return false;
            }

            _log.Add("REC_CHAIN_TUNER_INHERIT", $"R{r.Id}",
                $"result=OK assignedTuner={SafeValue(chainPreferredTuner)} lease={lease.Name} did={lease.Did} " +
                $"source={chainPreferredSource} waitedMs={waitedMs} rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
            var inheritedTunerMatched = string.Equals(chainPreferredTuner, lease.Name, StringComparison.OrdinalIgnoreCase);
            var plannedActualMismatchAfterAcquire = !string.IsNullOrWhiteSpace(r.TunerName)
                && !string.Equals(r.TunerName, lease.Name, StringComparison.OrdinalIgnoreCase);
            _log.Add("CHAIN_RECORDING_RUNTIME_AUDIT", $"R{r.Id}",
                $"result=ASSIGNED_TUNER_ACQUIRED stage=after_tuner_acquire successor=R{r.Id} predecessor={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} " +
                $"expectedTuner={SafeValue(chainPreferredTuner)} expectedSource={chainPreferredSource} handoffSource=final_plan_assigned_tuner sourceTransition=common_route_assigned_tuner_preserved sourceDecision=do_not_inherit_predecessor_actual_tuner " +
                $"plannedTuner={SafeValue(r.TunerName)} actualTuner={SafeValue(lease.Name)} did={SafeValue(lease.Did)} matched={inheritedTunerMatched} inheritMatchedActual={inheritedTunerMatched} plannedActualMismatch={plannedActualMismatchAfterAcquire} " +
                $"waitedMs={waitedMs} source={chainPreferredSource} commonRoute=ALLOC_ROUTE/TUNER_ALLOC stopRestart=False assignedTunerRequired=True behaviorChanged=True rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(r.TunerName))
            {
                _log.Add("REC_TUNER_VIRTUAL_POLICY", $"R{r.Id}",
                    $"virtualTuner={SafeValue(r.TunerName)} group={group} chain={chainContinuation} action=prefer_reserved_or_pretuned_tuner_at_launch reason=v0.9.98_active_recording_did_guard_compile_fix");
                lease = _tunerPool.AcquireForRecordingByNameWithExternalGuard(r.TunerName, r.Id, plannedEnd, unknownExternalLiveGuard, "pretune_or_allocation_locked_recording_tuner");
                if (lease is null)
                {
                    _log.Add("PRETUNE_LOCK_MISMATCH", $"R{r.Id}",
                        $"result=REQUESTED_TUNER_UNAVAILABLE requestedTuner={SafeValue(r.TunerName)} group={group} chain={chainContinuation} action=fallback_to_common_allocation_if_safe reason=pretuned_or_allocated_tuner_not_free status={_tunerPool.GetStatusSummary()} rule=v0.9.98_active_recording_did_guard_compile_fix");
                }
            }
            lease ??= _tunerPool.AcquireForRecordingWithExternalGuard(group, r.Id, plannedEnd, unknownExternalLiveGuard, "external_live_did_unknown_virtual_tuner_group_resolve");
        }

        if (lease is null)
        {
            _log.Add("REC_TUNER_ACQUIRE", $"R{r.Id}", $"result=FAIL group={group} requestedTuner={SafeValue(r.TunerName)} chain={chainContinuation} reason=acquire_returned_null status={_tunerPool.GetStatusSummary()}");
            Fail(r, $"チューナーを確保できませんでした。group={group} tuner={r.TunerName}");
            return false;
        }
        if (IsActiveRecordingDidOccupiedByOtherReservation(group, lease.Did, r.Id, out var activeDidOwner))
        {
            _log.Add("ACTIVE_RECORDING_DID_GUARD", $"R{r.Id}",
                $"result=DENY group={group} candidateTuner={lease.Name} did={lease.Did} owner={activeDidOwner} requestedTuner={SafeValue(r.TunerName)} action=release_candidate_and_fail_this_start reason=active_recording_did_is_source_of_truth rule=v0.9.98_active_recording_did_guard_compile_fix");
            lease.Dispose();
            Fail(r, $"録画中の実チューナーを保護したため録画開始を中止しました。did={lease.Did} owner={activeDidOwner}");
            return false;
        }

        if (!chainContinuation && !string.IsNullOrWhiteSpace(r.TunerName))
        {
            var locked = string.Equals(r.TunerName, lease.Name, StringComparison.OrdinalIgnoreCase);
            _log.Add(locked ? "PRETUNE_LOCK_MATCH" : "PRETUNE_LOCK_MISMATCH", $"R{r.Id}",
                $"result={(locked ? "OK" : "WARN")} requestedOrPretunedTuner={SafeValue(r.TunerName)} actualTuner={lease.Name} did={lease.Did} group={group} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} rule=v0.9.98_active_recording_did_guard_compile_fix");
        }

        _log.Add("REC_TUNER_ACQUIRE", $"R{r.Id}",
            $"result=OK group={group} requestedTuner={SafeValue(r.TunerName)} chain={chainContinuation} lease={lease.Name} bonDriver={lease.BonDriverFileName} did={lease.Did} elapsedSinceReleaseMs={(lease.ElapsedSinceReleaseMs.HasValue ? lease.ElapsedSinceReleaseMs.Value.ToString("F0") : "-")}");
        _log.Add("TUNER_PIPELINE_AUDIT", $"R{r.Id}",
            BuildReservationPipelineAudit(r, "tuner_acquired", group: group, plannedTuner: r.TunerName, actualTuner: lease.Name, lease: lease, resolvedChannel: resolvedChannel, plannedEnd: plannedEnd, finalGuardApplied: false, note: "actual_tuner_lease_acquired"));
        _log.Add("CHAIN_TRACE", $"R{r.Id}", $"[CHAIN] stage=after_tuner_acquire chain={chainContinuation} lease={lease.Name} did={lease.Did} pid=- status={_tunerPool.GetStatusSummary()}");
        LogChainRecordingEvaluation(r, group, chainContinuation, $"after_tuner_acquire lease={lease.Name} did={lease.Did}");

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
                $"result=FAIL reason=viewing_protection_block targetDid={SafeValue(lease.Did)} targetBonDriver={SafeValue(lease.BonDriverFileName)} externalSameDid={viewingAudit.ExternalSameDid} targetIsViewingRole={viewingAudit.TargetIsViewingRole} protectedViewingDids={string.Join(",", viewingAudit.ProtectedViewingDids)}");
            lease.Dispose();
            Fail(r, $"視聴用TVTest/LIVETest保護のため録画開始を中止しました。targetDid={lease.Did}");
            return false;
        }

        // ─── 同一物理チューナースロット直列化ゲート（v32.69） ───
        // 直前まで同じ物理チューナーが使われていた場合、BonDriver 側で
        // CmdCloseTuner → CmdOpenTuner が背中合わせで走り、ストリーム断裂・カクつきを誘発する。
        // lease.ElapsedSinceReleaseMs が設定クールダウンを下回っていれば、差分だけ待つ。
        var cooldownMs = _ini.TunerSlotCooldownMs;
        if (cooldownMs > 0 && lease.ElapsedSinceReleaseMs is double elapsed && elapsed < cooldownMs)
        {
            var chainRestartCooldown = TryResolveChainRestartCooldown(r, lease, cooldownMs, out var chainCooldownReason, out var chainCooldownPredId);
            if (chainRestartCooldown.HasValue)
            {
                var effectiveCooldownMs = chainRestartCooldown.Value;
                var waitMs = Math.Max(0, (int)Math.Ceiling(effectiveCooldownMs - elapsed));
                _log.Add("CHAIN_RESTART_COOLDOWN_BYPASS", $"R{r.Id}",
                    $"result={(waitMs > 0 ? "SHORT_WAIT" : "SKIP_WAIT")} predecessor={(chainCooldownPredId.HasValue ? $"R{chainCooldownPredId.Value}" : "-")} " +
                    $"slot={lease.Name} did={lease.Did} configuredCooldownMs={cooldownMs} effectiveCooldownMs={effectiveCooldownMs} " +
                    $"elapsedSinceReleaseMs={elapsed:F0} waitMs={waitMs} reason={chainCooldownReason} " +
                    $"rule=v0.9.83_failed_direct_route_guard");
                if (waitMs > 0)
                {
                    try
                    {
                        await Task.Delay(waitMs, ct);
                    }
                    catch (Exception ex)
                    {
                        _log.Add("Scheduler", $"R{r.Id}", $"チェーン直列化ゲート短縮待機中の例外（無視して続行）: {ex.Message}");
                    }
                }
            }
            else
            {
                var waitMs = (int)Math.Ceiling(cooldownMs - elapsed);
                _log.Add("Scheduler", $"R{r.Id}",
                    $"チューナー直列化ゲート待機: slot={lease.Name} 前回解放から {elapsed:F0}ms / しきい値 {cooldownMs}ms → {waitMs}ms 待機");
                try
                {
                    await Task.Delay(waitMs, ct);
                }
                catch (Exception ex)
                {
                    _log.Add("Scheduler", $"R{r.Id}", $"直列化ゲート待機中の例外（無視して続行）: {ex.Message}");
                }
            }
        }

        var channelArg = r.ChannelArgument ?? "";
        var recordFolderForDiscovery = ResolveReservationRecordFolder(r);
        // v0.5.65: 旧nativeRecProbe/TVTest録画ルートへは戻さず、DirectRecorder本線の入力だけをログ化する。
        // EDCB/EpgDataCap_Bonと同じく、局名ではなく .ch2 由来の NID/TSID/SID と chspace/chi を主キーにする。
        _log.Add("REC_OPERATIONAL_MODE", $"R{r.Id}",
            $"mode=TvAIrEpgRecProduction legacyTvTestRecording=False nativeRecProbe=False retiredLegacyDirectRecorderBridgeReference=none service={SafeValue(r.ServiceName)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} channelArg={SafeValue(channelArg)} rule=v0.8.01_epg_transport_stream_import_restore");

        TvTestActivityHandle? activityHandle = null;
        _log.Add("RECORDER_ACTIVITY", "RECORD_KEEPER_DISABLED",
            $"reservation=R{r.Id} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title, 80)} reason=recording_process_monitor_migrated_to_tvairepgrec action=no_activitykeeper_tvtest_started rule=v0.8.01_epg_transport_stream_import_restore");
        var directStart = await TryStartDirectRecorderRecordingAsync(r, group, lease, resolvedChannel, plannedEnd, recordFolderForDiscovery, ct).ConfigureAwait(false);
        if (!directStart.Success)
        {
            activityHandle?.Dispose();
            lease.Dispose();
            Fail(r, directStart.Message);
            return false;
        }

        var session = new RecordingSession(r.Id, directStart.ProcessId, plannedEnd, lease, directStart.OutputPath, directStart.ResponsePath, directStart.StopSignalPath, directStart.ProgressPath, directStart.RuntimeStatsPath, directStart.JobPath, directStart.SegmentPlanPath, activityHandle);
        lock (_sessionGate) _activeSessions[r.Id] = session;

        // v0.8.22: 共通割り当てルートの事前評価で一時的に競合化された予約でも、
        // 実行時に実チューナーを確保して TvAIrEpgRec 起動まで成功した場合は
        // 「Recording + conflicted=True」という矛盾状態を残さない。
        // 外部視聴の明示DID衝突評価と、実行時の空きチューナー確保結果がズレるケースで、
        // UIだけ競合表示になるのを防ぐ。録画成功の事実は actualTuner/activeSession を優先する。
        var latestBeforeRecordingStatus = _store.GetById(r.Id);
        if (latestBeforeRecordingStatus?.IsConflicted == true)
        {
            _store.UpdateConflicted(r.Id, false);
            _log.Add("REC_CONFLICT_RECONCILE", $"R{r.Id}",
                $"result=CLEARED reason=recording_started_with_actual_tuner actualTuner={lease.Name} did={lease.Did} " +
                $"group={group} statusBefore={latestBeforeRecordingStatus.Status} previousConflict=True " +
                "rule=v0.8.22_recording_start_clears_stale_conflict_flag");
        }

        _store.UpdateStatus(r.Id, ReservationStatus.Recording);
        _store.MarkRecordingStarted(r.Id, lease.Name);
        var stableEpgWarmupUntil = DateTime.Now.AddSeconds(30);
        var stableEpgAllowanceUntil = plannedEnd < DateTime.Now.AddMinutes(10) ? plannedEnd : DateTime.Now.AddMinutes(10);
        RecordingLifecycleGate.AllowStableRecordingEpg(group, stableEpgWarmupUntil, stableEpgAllowanceUntil, $"R{r.Id}", FormatReservationForLifecycleLog(r));
        _log.Add("REC_LIFECYCLE_EPG_GATE", $"R{r.Id}",
            $"state=RecordingWarmup group={group} stableEpgAfter={stableEpgWarmupUntil:MM/dd HH:mm:ss} allowanceUntil={stableEpgAllowanceUntil:MM/dd HH:mm:ss} action=allow_normal_epg_on_free_recording_tuner_after_warmup rule=v0.11.150_recording_timeline_epg_gate_effective_worker_block");
        BindChainDirectRecorderSessionScaffold(r, session, null, "recording_started");
        TvAirManagedProcessRegistry.RegisterRecording(directStart.ProcessId, r.Id, lease.Did, lease.BonDriverFileName, directStart.OutputPath);
        _log.Add("REC_START_MODE", $"R{r.Id}",
            $"mode=TvAIrEpgRecProcessStarted pid={directStart.ProcessId} service={SafeValue(r.ServiceName)} sid={r.ServiceId} tuner={lease.Name} did={lease.Did} path={SafeValue(directStart.OutputPath)} seconds={directStart.Seconds} rule=v0.8.01_epg_transport_stream_import_restore");
        if (chainContinuation)
        {
            var plannedActualMismatchAtStart = !string.IsNullOrWhiteSpace(r.TunerName)
                && !string.Equals(r.TunerName, lease.Name, StringComparison.OrdinalIgnoreCase);
            _log.Add("CHAIN_RECORDING_RUNTIME_AUDIT", $"R{r.Id}",
                $"result=RECORDING_STARTED stage=recording_started successor=R{r.Id} predecessor={(predIdForActualChain.HasValue ? $"R{predIdForActualChain.Value}" : "-")} " +
                $"plannedTuner={SafeValue(r.TunerName)} actualTuner={SafeValue(lease.Name)} did={SafeValue(lease.Did)} pid={directStart.ProcessId} path={SafeValue(directStart.OutputPath)} " +
                $"handoffSource=actual_recording_session expectedSource={(predIdForActualChain.HasValue ? "active_predecessor_actual_tuner" : "final_plan_persisted_tuner")} sourceTransition=recording_started_uses_acquired_actual_tuner sourceDecision=actual_tuner_is_output_truth plannedActualMismatch={plannedActualMismatchAtStart} inheritMatchedActual=True " +
                $"plannedEnd={plannedEnd:MM/dd HH:mm:ss} seconds={directStart.Seconds} separateTsFile=True commonRoute=ALLOC_ROUTE/TUNER_ALLOC assignedTunerPreArm=True behaviorChanged=True rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
        }
        _log.Add("RESERVATION_EXECUTION_CONTRACT", $"R{r.Id}",
            BuildReservationPipelineAudit(r, "recording_started", group: group, plannedTuner: r.TunerName, actualTuner: lease.Name, lease: lease, plannedEnd: plannedEnd, outputPath: directStart.OutputPath, finalGuardApplied: true, note: $"pid={directStart.ProcessId};seconds={directStart.Seconds};commonRoute=ALLOC_ROUTE/TUNER_ALLOC"));
        _log.Add("Scheduler", $"R{r.Id}",
            $"TvAIrEpgRec録画開始: [{r.Title}] tuner={lease.Name}/{lease.Did} pid={directStart.ProcessId} path={directStart.OutputPath}");
        return true;
    }


    private sealed record DirectRecorderStartResult(bool Success, int ProcessId, string OutputPath, int Seconds, string ResponsePath, string StopSignalPath, string ProgressPath, string RuntimeStatsPath, string JobPath, string SegmentPlanPath, string Message);

    private async Task<DirectRecorderStartResult> TryStartDirectRecorderRecordingAsync(
        Reservation r,
        string group,
        TunerLease lease,
        ChannelTarget? resolvedChannel,
        DateTime plannedEnd,
        string recordFolder,
        CancellationToken ct)
    {
        if (resolvedChannel is null)
        {
            return new DirectRecorderStartResult(false, 0, string.Empty, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                $"TvAIrEpgRec選局解決に失敗しました。nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId}");
        }

        var bonDriverPath = ResolveBonDriverPathForDirectRecorder(lease.BonDriverFileName);
        if (!File.Exists(bonDriverPath))
        {
            return new DirectRecorderStartResult(false, 0, string.Empty, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                $"TvAIrEpgRec用BonDriverが見つかりません。path={bonDriverPath}");
        }

        var epgRecExe = ResolveTvAIrEpgRecPath();
        if (string.IsNullOrWhiteSpace(epgRecExe) || !File.Exists(epgRecExe))
        {
            return new DirectRecorderStartResult(false, 0, string.Empty, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                "TvAIrEpgRec.exe が見つかりません。");
        }

        _log.Add("TVAIREPGREC_RECORD_ROUTE", $"R{r.Id}",
            $"result=SELECTED exe={SafeValue(epgRecExe)} previousProductionRoute=retired currentProductionRoute=TvAIrEpgRec mode=record " +
            $"retiredLegacyDirectRecorderBridgeTouched=false chainDecisionOwner=TvAIrCommonAllocationRoute filenameOwner=TvAIrCurrentPolicy rule=v0.8.01_epg_transport_stream_import_restore");

        var now = _broadcastClock.Now;
        var seconds = Math.Max(5, (int)Math.Ceiling((plannedEnd - now).TotalSeconds));
        seconds = Math.Min(seconds, 12 * 60 * 60);
        Directory.CreateDirectory(recordFolder);
        var namingPolicy = ResolveDirectRecorderFileNameTimePolicy(r, now);
        var fileNameBuild = BuildDirectRecorderFileName(r, namingPolicy.BaseTime);
        var outputPath = MakeUniqueRecordingPath(Path.Combine(recordFolder, fileNameBuild.FileName));
        _log.Add("RECORD_FILE_NAME_TIME_POLICY", $"R{r.Id}",
            $"mode={namingPolicy.Mode} base={namingPolicy.Base} source={r.Source} reservationStart={r.StartTime:yyyy-MM-dd HH:mm:ss} actualStart={now:yyyy-MM-dd HH:mm:ss} fileBaseTime={namingPolicy.BaseTime:yyyy-MM-dd HH:mm:ss} rule=v0.5.65_recording_kind_filename_time");
        _log.Add("RECORD_FILENAME_TITLE_GUARD", $"R{r.Id}",
            $"result={fileNameBuild.TitleGuardResult} source={fileNameBuild.TitleSource} persisted={fileNameBuild.TitlePersisted} originalTitle={SafeValue(fileNameBuild.OriginalReservationTitle)} resolvedTitle={SafeValue(fileNameBuild.RawTitle)} service={SafeValue(r.ServiceName)} event={r.NetworkId}/{r.TransportStreamId}/{r.ServiceId}/{r.EventId} chainPrev={(r.UserChainPreviousId.HasValue ? $"R{r.UserChainPreviousId.Value}" : "-")} chainRoot={(r.UserChainRootId.HasValue ? $"R{r.UserChainRootId.Value}" : "-")} rule=v0.11.448_recording_filename_title_guard");
        var fileNameNormalizedChanged = !string.Equals(fileNameBuild.RawTitle, fileNameBuild.NormalizedEventName, StringComparison.Ordinal);
        var fileNameDetail = fileNameNormalizedChanged
            ? $" rawTitle={SafeValue(fileNameBuild.RawTitle)} normalizedEventName={SafeValue(fileNameBuild.NormalizedEventName)} sanitizedEventName={SafeValue(fileNameBuild.SanitizedEventName)}"
            : string.Empty;
        _log.Add("RECORD_FILENAME_NORMALIZE", $"R{r.Id}",
            $"result=OK changed={fileNameNormalizedChanged} fileName={SafeValue(fileNameBuild.FileName)} outputPath={SafeValue(outputPath)} unsupportedTokens={SafeValue(fileNameBuild.UnsupportedTokens)}{fileNameDetail} rule=v0.10.45_cleanup_log_contract_polish");
        _log.Add("RECORD_FILENAME_PIPELINE_AUDIT", $"R{r.Id}",
            BuildReservationPipelineAudit(r, "filename_finalized", group: group, plannedTuner: r.TunerName, actualTuner: lease.Name, lease: lease, resolvedChannel: resolvedChannel, fileNameBuild: fileNameBuild, outputPath: outputPath, plannedEnd: plannedEnd, finalGuardApplied: true, note: $"namingMode={namingPolicy.Mode};timeBase={namingPolicy.Base};changed={fileNameNormalizedChanged}"));
        var workDir = Path.Combine(AppContext.BaseDirectory, "runtime", "tvairepgrec-production-recording");
        Directory.CreateDirectory(workDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var requestPath = Path.Combine(workDir, $"record_job_R{r.Id}_{stamp}_{Guid.NewGuid():N}.json");
        var responsePath = Path.Combine(workDir, $"record_result_R{r.Id}_{stamp}_{Guid.NewGuid():N}.json");
        var progressPath = Path.Combine(workDir, $"record_progress_R{r.Id}_{stamp}_{Guid.NewGuid():N}.jsonl");
        // 表版: worker内部runtime統計JSONLは生成しない。
        var runtimeStatsPath = string.Empty;
        var segmentPlanPath = Path.Combine(workDir, $"record_segments_R{r.Id}_{stamp}_{Guid.NewGuid():N}.json");
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
        var recordSegments = BuildChainRecordSegments(r, recordFolder, outputPath, now);
        await WriteChainRecordSegmentPlanAsync(segmentPlanPath, recordSegments, ct).ConfigureAwait(false);
        _log.Add("CHAIN_RECORD_SEGMENT_PLAN", $"R{r.Id}",
            $"result=WRITTEN path={SafeValue(segmentPlanPath)} count={recordSegments.Count} first=R{recordSegments.FirstOrDefault()?.ReservationId ?? 0} last=R{recordSegments.LastOrDefault()?.ReservationId ?? 0} " +
            $"execution=single_record_stop_restart rule=v0.9.83_failed_direct_route_guard");

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
            segmentPlanPath,
            tsReadSeconds = seconds,
            recordSegments,
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
                ["tvairVersion"] = "0.8.01",
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
                ["bonDriver"] = lease.BonDriverFileName,
                ["tvTestExecutablePathPassed"] = string.IsNullOrWhiteSpace(_ini.TvTestExecutablePath) ? "false" : "true",
                ["channelArgument"] = channelArgument,
                ["productionRecordRoute"] = "TvAIrEpgRec",
                ["previousProductionRecordRoute"] = "retiredLegacyRecorder",
                ["detachedLegacyDirectRecorderBridgeTouched"] = "false",
                ["chainDecisionOwner"] = "TvAIr common allocation route",
                ["outputPathPolicy"] = "decided_by_TvAIr_current_recording_filename_policy_before_worker_launch",
                ["chainSegmentPlanPath"] = segmentPlanPath,
                ["chainSegmentCount"] = recordSegments.Count.ToString(),
                ["tvTestRecordCurServiceOnly"] = _ini.TvTestRecordCurServiceOnly ? "true" : "false",
                ["tvTestRecordSubtitle"] = _ini.TvTestRecordSubtitle ? "true" : "false",
                ["tvTestRecordDataCarrousel"] = _ini.TvTestRecordDataCarrousel ? "true" : "false",
                ["taskbarIconVisible"] = _ini.ShowTvAIrEpgRecTaskbarIcon ? "true" : "false",
                ["directSetChannelIndex"] = directRecorderChannelIndex.ToString(),
                ["directSetChannelRule"] = directRecorderChannelReason,
                ["rule"] = "v0.8.01_epg_transport_stream_import_restore"
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
                return new DirectRecorderStartResult(false, 0, outputPath, seconds, responsePath, stopSignalPath, progressPath, runtimeStatsPath, requestPath, segmentPlanPath, "TvAIrEpgRecの起動に失敗しました。");
            }
        }
        catch (Exception ex)
        {
            return new DirectRecorderStartResult(false, 0, outputPath, seconds, responsePath, stopSignalPath, progressPath, runtimeStatsPath, requestPath, segmentPlanPath,
                $"TvAIrEpgRec起動例外: {ex.GetType().Name} {ex.Message}");
        }

        _log.Add("TVAIREPGREC_RECORD_START", $"R{r.Id}",
            $"pid={process.Id} exe={SafeValue(epgRecExe)} job={SafeValue(requestPath)} result={SafeValue(responsePath)} progress={SafeValue(progressPath)} stopSignal={SafeValue(stopSignalPath)} " +
            $"service={SafeValue(r.ServiceName)} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} " +
            $"space={resolvedChannel.ResolvedSpace} ch={directRecorderChannelIndex} tvtestArgCh={resolvedChannel.ResolvedChannelIndex} bonCh={resolvedChannel.BonDriverChannel} channelReason={SafeValue(directRecorderChannelReason)} tuner={lease.Name} did={lease.Did} " +
            $"seconds={seconds} path={SafeValue(outputPath)} segmentPlan={SafeValue(segmentPlanPath)} segmentCount={recordSegments.Count} tvTestRecordCurServiceOnly={_ini.TvTestRecordCurServiceOnly} tvTestRecordSubtitle={_ini.TvTestRecordSubtitle} tvTestRecordDataCarrousel={_ini.TvTestRecordDataCarrousel} launchKind={launchKind} taskbarIconVisible={showWorkerTaskbarIcon} windowPolicy={windowPolicy} titleBarLogoPath={SafeValue(displayLogoPath)} centerLogoPath={SafeValue(centerLogoPath)} logoTarget=worker_titlebar_center_only productionRoute=TvAIrEpgRec previousRoute=retired retiredLegacyDirectRecorderBridgeTouched=false rule=v0.9.85_logo_worker_display_inventory_route");

        return new DirectRecorderStartResult(true, process.Id, outputPath, seconds, responsePath, stopSignalPath, progressPath, runtimeStatsPath, requestPath, segmentPlanPath, "TvAIrEpgRec started");
    }


    private sealed record ChainRecordSegmentPlanItem(
        int ReservationId,
        string ServiceName,
        string Title,
        DateTime StartTime,
        DateTime EndTime,
        DateTime SwitchAt,
        string OutputPath);

    private List<ChainRecordSegmentPlanItem> BuildChainRecordSegments(Reservation root, string recordFolder, string rootOutputPath, DateTime actualStartTime)
    {
        // v0.9.76: 実行本線ではTvAIrEpgRec内ファイル切替を使わない。
        // チェーン後続は境界で前番組を停止し、後続予約を新しいTvAIrEpgRec録画として起動する。
        // recordSegmentsはTvAIrEpgRec互換の単一セグメント情報に限定する。
        return new List<ChainRecordSegmentPlanItem>
        {
            new ChainRecordSegmentPlanItem(
                root.Id,
                root.ServiceName ?? string.Empty,
                root.Title ?? string.Empty,
                root.StartTime,
                root.EndTime,
                actualStartTime,
                rootOutputPath)
        };
    }

    private static async Task WriteChainRecordSegmentPlanAsync(string path, List<ChainRecordSegmentPlanItem> segments, CancellationToken ct)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var payload = new
        {
            version = "v0.9.83_failed_direct_route_guard",
            createdAt = DateTime.Now,
            segments
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, options), ct).ConfigureAwait(false);
    }

    private string PrepareDirectRecorderWinscardLocalCopy(string bridgeExe)
    {
        // v0.7.68: keep the old method name for source compatibility only.
        // Do not copy winscard.dll / winscard.ini into TvAIr or bridge folders.
        // Card-reader access is resolved by passing TVTest.exe to TvAIrEpgRec and preloading TVTest\winscard.dll there.
        try
        {
            var tvTestExe = _ini.TvTestExecutablePath ?? string.Empty;
            var tvTestDir = string.IsNullOrWhiteSpace(tvTestExe) ? string.Empty : Path.GetDirectoryName(tvTestExe) ?? string.Empty;
            var winscardDll = string.IsNullOrWhiteSpace(tvTestDir) ? string.Empty : Path.Combine(tvTestDir, "winscard.dll");
            var winscardIni = string.IsNullOrWhiteSpace(tvTestDir) ? string.Empty : Path.Combine(tvTestDir, "winscard.ini");
            var result = !string.IsNullOrWhiteSpace(tvTestDir) && Directory.Exists(tvTestDir) && File.Exists(winscardDll)
                ? "OK_REFERENCE_ONLY"
                : "SKIP_TVTEST_WINSCARD_MISSING";
            return $"result={result} tvTestDir={SafeValue(tvTestDir)} winscardDll={(File.Exists(winscardDll) ? "exists" : "missing")} winscardIni={(File.Exists(winscardIni) ? "exists" : "missing")} bridgeDir={SafeValue(Path.GetDirectoryName(bridgeExe) ?? AppContext.BaseDirectory)} copied=none rule=v0.7.68_no_app_local_winscard_copy_card_reader_reference_tvtest_dir";
        }
        catch (Exception ex)
        {
            return $"result=NG error={ex.GetType().Name}:{SafeValue(ex.Message)} copied=none rule=v0.7.68_no_app_local_winscard_copy_card_reader_reference_tvtest_dir";
        }
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

    private DirectRecorderFileNameBuildResult BuildDirectRecorderFileName(Reservation r, DateTime baseTime)
    {
        // v0.6.17 cleanup note (based on v0.6.10):
        // DirectRecorderはTVTest本体の最終命名処理を通らないため、TVTest.ini の
        // RecordFileName 書式をTvAIr側で最低限解釈する。
        // ただし半角化/禁止文字処理は %event-name% 相当だけに限定し、
        // EPGタイトル・番組表・自動検索・REC_FOLLOW_CHECK には一切触らない。
        // v0.11.448: TimeFollow/chain再評価で予約DB上のTitleが空になっても、
        // 録画開始直前のファイル名生成で DB/EPG/ChainGroup から番組名を復元し、
        // %event-name% が空のまま TvAIr.ts へ落ちることを防ぐ。
        var titleGuard = ResolveRecordingFileNameTitle(r);
        var rawTitle = titleGuard.Title;
        var normalizedTitle = RecordingFileNameNormalizer.NormalizeEventNameForFileName(rawTitle);
        var sanitizedTitle = RecordingFileNameNormalizer.SanitizeFileNamePart(normalizedTitle);

        string template;
        string evidence;
        if (!TvTestRecordFileNameTemplateResolver.TryResolve(_ini.TvTestExecutablePath, out template, out evidence))
        {
            template = "%year2%年%month2%月%day2%日%hour2%時%minute2%分-%event-name%.ts";
            evidence = $"{evidence} fallback=tvair_legacy_compatible_template";
        }

        var rendered = RenderTvTestRecordFileNameTemplate(template, baseTime, sanitizedTitle, r, out var unsupportedTokens);
        rendered = RecordingFileNameNormalizer.SanitizeRenderedFileName(rendered);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(rendered)))
        {
            rendered += ".ts";
        }

        return new DirectRecorderFileNameBuildResult(
            FileName: rendered,
            RawTitle: rawTitle,
            NormalizedEventName: normalizedTitle,
            SanitizedEventName: sanitizedTitle,
            Template: template,
            Evidence: evidence,
            UnsupportedTokens: unsupportedTokens.Count == 0 ? "-" : string.Join(',', unsupportedTokens),
            OriginalReservationTitle: titleGuard.OriginalTitle,
            TitleSource: titleGuard.Source,
            TitleGuardResult: titleGuard.Result,
            TitlePersisted: titleGuard.Persisted);
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

        var serviceFallback = RecordingFileNameNormalizer.SanitizeFileNamePart(r.ServiceName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(serviceFallback)) serviceFallback = "TvAIr";
        var fallbackTitle = $"{serviceFallback}-R{r.Id}";
        return new RecordingFileNameTitleGuardResult(fallbackTitle, originalTitle, "service_reservation_id", "FALLBACK", false);
    }

    private string ResolveEpgProjectedTitle(Reservation r)
    {
        if (r.EventId == 0 || r.ServiceId == 0) return string.Empty;
        try
        {
            var ev = _epgStore.GetOne(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId);
            return (ev is null ? string.Empty : EpgProjection.Title(ev)).Trim();
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

    private static string RenderTvTestRecordFileNameTemplate(
        string template,
        DateTime baseTime,
        string eventName,
        Reservation r,
        out List<string> unsupportedTokens)
    {
        var unsupportedTokenSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = string.IsNullOrWhiteSpace(template)
            ? "%year2%年%month2%月%day2%日%hour2%時%minute2%分-%event-name%.ts"
            : template;

        // v0.6.17 cleanup note (based on v0.6.10):
        // DirectRecorder側でTVTest本体の命名処理を完全再実装しない。
        // ただし、現在のTVTest RecordFileName運用で使われやすく、かつ副作用なく評価できる
        // 日時・番組名・サービス名系トークンだけを明示対応する。
        // 未対応トークンは勝手に空文字へ潰さず、そのまま残してログへ一意化して出す。
        var serviceName = string.IsNullOrWhiteSpace(r.ServiceName)
            ? "TvAIr"
            : RecordingFileNameNormalizer.SanitizeFileNamePart(r.ServiceName);

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["%event-name%"] = eventName,
            ["%year%"] = baseTime.ToString("yyyy"),
            ["%year2%"] = baseTime.ToString("yy"),
            ["%month%"] = baseTime.Month.ToString(),
            ["%month2%"] = baseTime.ToString("MM"),
            ["%day%"] = baseTime.Day.ToString(),
            ["%day2%"] = baseTime.ToString("dd"),
            ["%hour%"] = baseTime.Hour.ToString(),
            ["%hour2%"] = baseTime.ToString("HH"),
            ["%minute%"] = baseTime.Minute.ToString(),
            ["%minute2%"] = baseTime.ToString("mm"),
            ["%second%"] = baseTime.Second.ToString(),
            ["%second2%"] = baseTime.ToString("ss"),
            ["%channel-name%"] = serviceName,
            ["%service-name%"] = serviceName,
        };

        text = Regex.Replace(text, "%[A-Za-z0-9_-]+%", m =>
        {
            if (replacements.TryGetValue(m.Value, out var replacement))
                return replacement;

            unsupportedTokenSet.Add(m.Value);
            return m.Value;
        });

        unsupportedTokens = unsupportedTokenSet.ToList();
        return text;
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
        bool TitlePersisted);

    private sealed record RecordingFileNameTitleGuardResult(string Title, string OriginalTitle, string Source, string Result, bool Persisted);

    private sealed record RecordingFileNameTimePolicy(string Mode, string Base, DateTime BaseTime);

    private static string NormalizeEventNameForRecordingFile(string value)
        => RecordingFileNameNormalizer.NormalizeEventNameForFileName(value);

    private static string SanitizeFileName(string value)
        => RecordingFileNameNormalizer.SanitizeRenderedFileName(value);


    private DateTime ResolveChainPlannedEndForLaunch(Reservation r, DateTime basePlannedEnd)
    {
        // v0.11.678: チェーン録画は番組単位ファイルを維持するため、rootプロセスをチェーン末尾まで延命しない。
        // 後続番組はFinalConflictPlanのAssignedTunerを正本に、別チューナーなら並行開始、同一チューナーなら境界pre-armで起動する。
        if (HasEnabledChainSuccessor(r))
        {
            _log.Add("CHAIN_RECORDING_WINDOW_POLICY", $"R{r.Id}",
                $"result=BASE_ONLY basePlannedEnd={basePlannedEnd:MM/dd HH:mm:ss} " +
                $"reason=assigned_tuner_prearm_program_file_per_reservation commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
        }
        return basePlannedEnd;
    }


    private DateTime ResolveChainPlannedEndForRecordingFollow(Reservation r, DateTime singleProgramPlannedEnd, DateTime currentSessionPlannedEnd)
    {
        // v0.11.678: RecordingFollowもチェーン末尾延長を行わない。
        // 後続は共通割当済みAssignedTunerを使う別録画として開始し、root延命で割当ルートを迂回しない。
        if (HasEnabledChainSuccessor(r))
        {
            _log.Add("REC_FOLLOW_CHAIN_PLANNED_END", $"R{r.Id}",
                $"result=BASE_ONLY singleProgramPlannedEnd={singleProgramPlannedEnd:MM/dd HH:mm:ss} currentSessionPlannedEnd={currentSessionPlannedEnd:MM/dd HH:mm:ss} " +
                $"reason=assigned_tuner_prearm_does_not_extend_root_session commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
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

    // v0.9.76: 既存TvAIrEpgRecセッションへ後続予約を付け替える旧ファイル切替方式は廃止。
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

        var scaffold = new ChainDirectRecorderSession
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
            SegmentPlanPath = session.SegmentPlanPath,
            SegmentStartTime = current.StartTime,
            SegmentEndTime = current.EndTime,
            PlannedEndTime = session.PlannedEndTime,
        };
        if (nextReservation is not null)
            scaffold.AttachNext(nextReservation);

        var isNew = _chainSessionRegistry.Bind(scaffold);

        _log.Add("CHAIN_SESSION_BIND", $"R{current.Id}",
            $"result={(isNew ? "BOUND" : "UPDATED")} {scaffold.ToLogFields(stage)} " +
            $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC actualTunerIsCurrentSessionOnly=True successorAssignedTunerSource=FinalConflictPlan auditVisible=True normalExecutorFrozen=True rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
    }

    private void RemoveChainDirectRecorderSessionScaffold(int reservationId, string stage, string reason)
    {
        _chainSessionRegistry.Remove(reservationId, out var removed);

        if (removed is not null)
        {
            _log.Add("CHAIN_SESSION_RELEASE", $"R{reservationId}",
                $"result=REMOVED stage={stage} reason={reason} {removed.ToLogFields("release")} rule=v0.9.83_failed_direct_route_guard");
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

    // ─── 疑似チューナー引き継ぎ（前番組前倒し終了） ────────────────

    /// <summary>
    /// 録画中の前番組が後続番組と連続している場合、
    /// 後続番組の StartTime - PseudoContinuousMarginSeconds を過ぎたら前番組を終了させる。
    /// 後続番組は通常の PreStartMarginSeconds で同チューナーを使って起動される。
    /// </summary>
    private async Task CheckPseudoContinuousHandoffAsync(DateTime now, CancellationToken ct)
    {
        // 録画中セッションを取得
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.ToList();
        if (active.Count == 0) return;

        // 連続番組チェーンを取得
        var chains = _store.GetChainPredecessors(); // key=後続ID, value=前番組ID
        // 反転: 前番組ID → 後続番組ID
        var successorOf = chains.ToDictionary(kv => kv.Value, kv => kv.Key);

        if (successorOf.Count == 0) return;

        _log.Add("Scheduler", "Chain",
            $"チェーン検索: セッション数={active.Count} 有効チェーンペア数={successorOf.Count} " +
            $"[{string.Join(", ", successorOf.Select(kv => $"R{kv.Key}→R{kv.Value}"))}]");

        foreach (var session in active)
        {
            if (!successorOf.TryGetValue(session.ReservationId, out var successorId)) continue;

            var successor = _store.GetById(successorId);
            if (successor is null)
            {
                _log.Add("Scheduler", $"R{session.ReservationId}",
                    $"チェーン確認: 後続R{successorId}がDBに存在しません（削除済み？）");
                continue;
            }

            if (successor.Status != ReservationStatus.Scheduled)
            {
                _log.Add("Scheduler", $"R{session.ReservationId}",
                    $"チェーン確認: 後続R{successorId}[{successor.Title}] status={successor.Status}のためスキップ");
                continue;
            }

            if (!successor.IsEnabled)
            {
                _log.Add("Scheduler", $"R{session.ReservationId}",
                    $"チェーン確認: 後続R{successorId}[{successor.Title}] is_enabled=falseのためスキップ");
                continue;
            }

            var predecessorForBind = _store.GetById(session.ReservationId);
            if (predecessorForBind is not null)
                BindChainDirectRecorderSessionScaffold(predecessorForBind, session, successor, "handoff_scan");

            // v0.11.678: チェーン境界は「前番組実チューナー継承」ではなく、
            // FinalConflictPlan/RoleBinding で予約へ永続化された後続AssignedTunerを正本にする。
            // 後続が別チューナーに割り当て済みなら前番組を止めずに後続を開始する。
            // 同一チューナーだけ、後続due直前に前番組を切って後続開始完全性を優先する。
            var predecessor = _store.GetById(session.ReservationId);
            var successorDueTime = successor.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
            var assignedSuccessorTuner = successor.TunerName ?? string.Empty;
            var activePredecessorTuner = session.Lease.Name;
            var activePredecessorDid = session.Lease.Did;
            var assignedTunerMissing = string.IsNullOrWhiteSpace(assignedSuccessorTuner);
            var sameAssignedTunerAsPredecessor = !assignedTunerMissing
                && string.Equals(assignedSuccessorTuner, activePredecessorTuner, StringComparison.OrdinalIgnoreCase);
            var preArmLeadSeconds = sameAssignedTunerAsPredecessor
                ? Math.Min(Math.Max(0, ChainSuccessorPreArmLeadSeconds), Math.Max(0, _ini.PreStartMarginSeconds))
                : 0;
            var boundaryActionAt = sameAssignedTunerAsPredecessor
                ? successorDueTime.AddSeconds(-preArmLeadSeconds)
                : successorDueTime;
            var timeToBoundaryAction = boundaryActionAt - now;
            var boundaryExecutionKey = $"R{session.ReservationId}->R{successorId}";

            _log.Add("CHAIN_BOUNDARY_PLAN", $"R{session.ReservationId}",
                $"result={(assignedTunerMissing ? "MISSING_ASSIGNED_TUNER" : "OK")} predecessor=R{session.ReservationId} successor=R{successorId} " +
                $"assignedTuner={SafeValue(assignedSuccessorTuner)} activePredecessorTuner={SafeValue(activePredecessorTuner)} activePredecessorDid={SafeValue(activePredecessorDid)} " +
                $"sameAssignedTunerAsPredecessor={sameAssignedTunerAsPredecessor} successorDue={successorDueTime:MM/dd HH:mm:ss} successorStart={successor.StartTime:MM/dd HH:mm:ss} " +
                $"boundaryActionAt={boundaryActionAt:MM/dd HH:mm:ss} preArmLeadSeconds={preArmLeadSeconds} scheduler=high_priority intervalMs={ChainBoundaryMonitorIntervalMs} normalTickMs={PollingIntervalMs} commonRoute=ALLOC_ROUTE/TUNER_ALLOC source=FinalConflictPlan.AssignedTuner rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");

            if (assignedTunerMissing)
            {
                _log.Add("CHAIN_SUCCESSOR_PREARM", $"R{session.ReservationId}",
                    $"result=SKIP reason=missing_final_plan_assigned_tuner predecessor=R{session.ReservationId} successor=R{successorId} action=no_tuner_inheritance_fallback commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
                continue;
            }

            if (now < boundaryActionAt)
            {
                if (timeToBoundaryAction.TotalSeconds <= 60 && ShouldLogChainBoundaryWait(boundaryExecutionKey, timeToBoundaryAction.TotalSeconds))
                {
                    _log.Add("CHAIN_SUCCESSOR_PREARM", $"R{session.ReservationId}",
                        $"result=WAIT predecessor=R{session.ReservationId} successor=R{successorId} assignedTuner={SafeValue(assignedSuccessorTuner)} " +
                        $"sameAssignedTunerAsPredecessor={sameAssignedTunerAsPredecessor} boundaryActionAt={boundaryActionAt:HH:mm:ss} remainingSec={timeToBoundaryAction.TotalSeconds:F0} " +
                        $"scheduler=high_priority intervalMs={ChainBoundaryMonitorIntervalMs} normalTickMs={PollingIntervalMs} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
                }
                continue;
            }

            if (!TryBeginChainBoundaryExecution(boundaryExecutionKey))
            {
                _log.Add("CHAIN_SUCCESSOR_PREARM", $"R{session.ReservationId}",
                    $"result=SKIP reason=boundary_execution_already_in_progress predecessor=R{session.ReservationId} successor=R{successorId} key={boundaryExecutionKey} " +
                    $"scheduler=high_priority commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
                continue;
            }

            var successorLateAtBoundary = now > successorDueTime;

            if (!sameAssignedTunerAsPredecessor)
            {
                _log.Add("CHAIN_SUCCESSOR_PREARM", $"R{session.ReservationId}",
                    $"result=START_WITH_ASSIGNED_TUNER predecessor=R{session.ReservationId} successor=R{successorId} assignedTuner={SafeValue(assignedSuccessorTuner)} " +
                    $"activePredecessorTuner={SafeValue(activePredecessorTuner)} action=keep_predecessor_running successorDue={successorDueTime:MM/dd HH:mm:ss} late={successorLateAtBoundary} " +
                    $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");

                var latestSuccessorForPreArm = _store.GetById(successorId);
                if (latestSuccessorForPreArm is null || !latestSuccessorForPreArm.IsEnabled || latestSuccessorForPreArm.Status != ReservationStatus.Scheduled)
                {
                    _log.Add("CHAIN_SUCCESSOR_PREARM", $"R{session.ReservationId}",
                        $"result=SKIP_START reason=successor_not_recordable predecessor=R{session.ReservationId} successor=R{successorId} " +
                        $"exists={latestSuccessorForPreArm is not null} enabled={latestSuccessorForPreArm?.IsEnabled.ToString() ?? "-"} status={latestSuccessorForPreArm?.Status.ToString() ?? "-"} rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
                    continue;
                }

                var startedDifferentTuner = await StartRecordingAsync(latestSuccessorForPreArm, ct).ConfigureAwait(false);
                _log.Add(startedDifferentTuner && successorLateAtBoundary ? "CHAIN_SUCCESSOR_LATE" : "CHAIN_SUCCESSOR_START_RESULT", $"R{session.ReservationId}",
                    $"result={(startedDifferentTuner ? "SUCCESS" : "START_FAILED")} predecessor=R{session.ReservationId} successor=R{successorId} assignedTuner={SafeValue(assignedSuccessorTuner)} " +
                    $"activePredecessorTuner={SafeValue(activePredecessorTuner)} sameAssignedTunerAsPredecessor=False separateTsFile=True predecessorKeptRunning=True late={successorLateAtBoundary} " +
                    $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
                _userEvents.AddChainSwitched(predecessor, latestSuccessorForPreArm, startedDifferentTuner);
                continue;
            }

            _log.Add("CHAIN_FRONT_CUT", $"R{session.ReservationId}",
                $"result=BEGIN predecessor=R{session.ReservationId} successor=R{successorId} assignedTuner={SafeValue(assignedSuccessorTuner)} did={SafeValue(activePredecessorDid)} pid={session.ProcessId} " +
                $"successorDue={successorDueTime:MM/dd HH:mm:ss} boundaryActionAt={boundaryActionAt:MM/dd HH:mm:ss} preArmLeadSeconds={preArmLeadSeconds} frontTailMayBeCut=True " +
                $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC reason=same_assigned_tuner_boundary rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");

            _log.Add("CHAIN_RECORDING_RUNTIME_AUDIT", $"R{session.ReservationId}",
                $"result=BOUNDARY_STOP_BEGIN stage=front_cut predecessor=R{session.ReservationId} successor=R{successorId} " +
                $"plannedTuner={SafeValue(assignedSuccessorTuner)} actualPredecessorTuner={SafeValue(activePredecessorTuner)} did={SafeValue(activePredecessorDid)} pid={session.ProcessId} boundaryActionAt={boundaryActionAt:MM/dd HH:mm:ss} " +
                $"handoffSource=final_plan_assigned_tuner expectedSource=FinalConflictPlan.AssignedTuner sourceTransition=common_route_assigned_tuner_preserved sourceDecision=cut_front_only_when_same_assigned_tuner plannedActualMismatch=False inheritMatchedActual=True " +
                $"successorDue={successorDueTime:MM/dd HH:mm:ss} separateTsFile=True commonRoute=ALLOC_ROUTE/TUNER_ALLOC stopRestart=True behaviorChanged=True rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");

            await StopSessionAsync(session, ReservationStatus.Completed, suppressNormalStopForBoundary: false).ConfigureAwait(false);

            var latestSuccessor = _store.GetById(successorId);
            if (latestSuccessor is null || !latestSuccessor.IsEnabled || latestSuccessor.Status != ReservationStatus.Scheduled)
            {
                _log.Add("CHAIN_SUCCESSOR_PREARM", $"R{session.ReservationId}",
                    $"result=SKIP_START reason=successor_not_recordable predecessor=R{session.ReservationId} successor=R{successorId} " +
                    $"exists={latestSuccessor is not null} enabled={latestSuccessor?.IsEnabled.ToString() ?? "-"} status={latestSuccessor?.Status.ToString() ?? "-"} rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
                continue;
            }

            var started = await StartRecordingAsync(latestSuccessor, ct).ConfigureAwait(false);
            var successorLateAfterStart = DateTime.Now > successorDueTime;
            var resultEvent = started && successorLateAfterStart ? "CHAIN_SUCCESSOR_LATE" : "CHAIN_SUCCESSOR_START_RESULT";
            _log.Add(resultEvent, $"R{session.ReservationId}",
                $"result={(started ? "SUCCESS" : "START_FAILED")} predecessor=R{session.ReservationId} successor=R{successorId} assignedTuner={SafeValue(assignedSuccessorTuner)} did={SafeValue(activePredecessorDid)} " +
                $"sameAssignedTunerAsPredecessor=True separateTsFile=True stopRestart=True frontTailMayBeCut=True preArmLeadSeconds={preArmLeadSeconds} late={successorLateAfterStart} " +
                $"commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
            _log.Add("CHAIN_RECORDING_RUNTIME_AUDIT", $"R{session.ReservationId}",
                $"result={(started ? "SUCCESS" : "START_FAILED")} stage=successor_start_result predecessor=R{session.ReservationId} successor=R{successorId} " +
                $"expectedTuner={SafeValue(assignedSuccessorTuner)} expectedSource=FinalConflictPlan.AssignedTuner actualTuner={SafeValue(assignedSuccessorTuner)} " +
                $"handoffSource=final_plan_assigned_tuner sourceTransition=common_route_assigned_tuner_preserved sourceDecision=do_not_inherit_predecessor_actual_tuner inheritMatchedActual={started} started={started} " +
                $"separateTsFile=True commonRoute=ALLOC_ROUTE/TUNER_ALLOC stopRestart=True frontTailMayBeCut=True behaviorChanged=True rule=v0.11.678_chain_boundary_high_priority_scheduler_contract");
            _userEvents.AddChainSwitched(predecessor, latestSuccessor, started);
            continue;
        }
    }

    // ─── 録画中時間追従（退化修復） ───────────────────────────────

    private void RestoreActiveRecordingStatusesBeforeDecision(DateTime now, string stage)
    {
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.ToList();
        if (active.Count == 0) return;

        foreach (var session in active)
        {
            var r = _store.GetById(session.ReservationId);
            if (r is null) continue;
            if (r.Status != ReservationStatus.Failed) continue;
            if (now >= session.PlannedEndTime) continue;
            if (!IsProcessAlive(session.ProcessId)) continue;

            _store.UpdateStatus(r.Id, ReservationStatus.Recording);
            _log.Add("REC_STATUS_GUARD_ACTIVE_SESSION", $"R{r.Id}",
                $"result=RESTORED_BEFORE_DECISION stage={stage} reason=active_tvairepgrec_session_is_source_of_truth pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} previousStatus=Failed rule=v0.9.83_failed_direct_route_guard");
        }
    }

    private void FinalizeActiveRecordingSessionsWithMissingProcess(DateTime now)
    {
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.ToList();
        if (active.Count == 0) return;

        foreach (var session in active)
        {
            // 通常停止境界では StopSessionAsync が正本。境界付近をここで失敗扱いしない。
            if (now >= session.PlannedEndTime.AddSeconds(-30))
                continue;

            if (IsProcessAlive(session.ProcessId))
                continue;

            var r = _store.GetById(session.ReservationId);
            if (r is null)
            {
                _log.Add("REC_INTERRUPTED_DETECTED", $"R{session.ReservationId}",
                    $"result=DETECTED reason=reservation_missing_worker_process_gone pid={session.ProcessId} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} tuner={SafeValue(session.Lease.Name)} action=release_session_only rule=v0.11.138_recording_interruption_recovery_common");
            }
            else
            {
                var fileEvidence = ProbeInterruptedRecordingFile(r);
                _log.Add("REC_INTERRUPTED_DETECTED", $"R{session.ReservationId}",
                    $"result=DETECTED reason=worker_process_missing_before_planned_end pid={session.ProcessId} now={now:MM/dd HH:mm:ss} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} file={SafeValue(fileEvidence)} rule=v0.11.138_recording_interruption_recovery_common");
                _store.FinalizeInterruptedRecordingAtStartup(r.Id, now, "worker_process_missing_before_planned_end", fileEvidence, "RecordingPipeline");
            }

            try { session.Lease.Dispose(); } catch { }
            try { session.ActivityHandle?.Dispose(); } catch { }
            TvAirManagedProcessRegistry.Unregister(session.ProcessId);
            lock (_sessionGate)
            {
                _activeSessions.Remove(session.ReservationId);
                _recordingTerminalFailureReasons.Remove(session.ReservationId);
            }
            RemoveChainDirectRecorderSessionScaffold(session.ReservationId, "recording_interrupted_worker_missing", ReservationStatus.Failed.ToString());
            _forceAllocationReevaluate = true;
            UpdateWakeTasksForRecordingLifecycle($"R{session.ReservationId}", "録画中断後", swallowError: true);
        }
    }

    private static bool IsUserRuntimeStartRewindGuardTarget(Reservation r, RecordingSession session)
    {
        // v0.11.582: Immediate/manual user recordings can be registered after the EPG event has already started.
        // Once TvAIrEpgRec is running, the reservation/session start is the runtime truth; REC_FOLLOW may still extend the end,
        // but must not move StartTime/occupancy/segmentStart backwards to the EPG event start.
        if (session.ProcessId <= 0) return false;
        return r.Source is ReservationSource.Immediate or ReservationSource.Manual;
    }

    private async Task CheckActiveRecordingFileGrowthAsync(DateTime now)
    {
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.ToList();
        if (active.Count == 0) return;

        foreach (var session in active)
        {
            if (session.RecordingFileStallStopRequested) continue;
            if (now >= session.PlannedEndTime.AddSeconds(-RecordingFileGrowthPlannedEndGuardSec)) continue;

            var r = _store.GetById(session.ReservationId);
            if (r is null || r.Status != ReservationStatus.Recording) continue;

            var observation = ObserveRecordingFileGrowth(session, now);
            if (!observation.ShouldStop) continue;

            session.MarkRecordingFileStallStopRequested();
            lock (_sessionGate) _recordingTerminalFailureReasons[session.ReservationId] = observation.Reason;
            _log.Add("REC_WRITE_STALLED", $"R{session.ReservationId}",
                $"result=FAILED reason={observation.Reason} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title)} " +
                $"path={SafeValue(session.RecordingFilePath)} bytes={observation.Bytes} previousBytes={observation.PreviousBytes} " +
                $"monitorStarted={session.RecordingFileGrowthWatchStartedAt:MM/dd HH:mm:ss} lastGrowth={session.LastRecordingFileGrowthAt:MM/dd HH:mm:ss} " +
                $"elapsedSinceStartSec={(int)Math.Max(0, (now - session.RecordingFileGrowthWatchStartedAt).TotalSeconds)} elapsedSinceGrowthSec={(int)Math.Max(0, (now - session.LastRecordingFileGrowthAt).TotalSeconds)} " +
                $"initialGraceSec={RecordingFileGrowthInitialGraceSec} stallSec={RecordingFileGrowthStallSec} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} " +
                $"pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} action=stop_and_fail rule=v0.11.678_recording_file_growth_watchdog");

            await StopSessionAsync(session, ReservationStatus.Failed).ConfigureAwait(false);
        }
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
            return new RecordingFileGrowthObservation(true, "file_probe_error_" + ex.GetType().Name, bytes, previousBytes);
        }

        if (exists && bytes > previousBytes)
        {
            session.MarkRecordingFileGrowth(now, bytes);
            if (previousBytes <= 0 && bytes > 0)
            {
                _log.Add("REC_FILE_GROWTH_WATCH", $"R{session.ReservationId}",
                    $"result=OBSERVED path={SafeValue(session.RecordingFilePath)} bytes={bytes} previousBytes={previousBytes} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} rule=v0.11.678_recording_file_growth_watchdog");
            }
            return RecordingFileGrowthObservation.Continue(bytes, previousBytes);
        }

        var elapsedSinceStartSec = (now - session.RecordingFileGrowthWatchStartedAt).TotalSeconds;
        var elapsedSinceGrowthSec = (now - session.LastRecordingFileGrowthAt).TotalSeconds;

        if (!exists)
        {
            return elapsedSinceStartSec >= RecordingFileGrowthInitialGraceSec
                ? new RecordingFileGrowthObservation(true, "recording_file_missing_after_initial_grace", bytes, previousBytes)
                : RecordingFileGrowthObservation.Continue(bytes, previousBytes);
        }

        if (bytes < previousBytes && previousBytes >= 0)
        {
            session.MarkRecordingFileGrowth(now, bytes);
            _log.Add("REC_FILE_GROWTH_WATCH", $"R{session.ReservationId}",
                $"result=RESET reason=file_size_decreased path={SafeValue(session.RecordingFilePath)} bytes={bytes} previousBytes={previousBytes} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} rule=v0.11.678_recording_file_growth_watchdog");
            return RecordingFileGrowthObservation.Continue(bytes, previousBytes);
        }

        if (bytes <= 0)
        {
            return elapsedSinceStartSec >= RecordingFileGrowthInitialGraceSec
                ? new RecordingFileGrowthObservation(true, "no_recording_data_after_initial_grace", bytes, previousBytes)
                : RecordingFileGrowthObservation.Continue(bytes, previousBytes);
        }

        if (session.RecordingFileEverGrew && elapsedSinceGrowthSec >= RecordingFileGrowthStallSec)
            return new RecordingFileGrowthObservation(true, "recording_file_growth_stalled", bytes, previousBytes);

        return RecordingFileGrowthObservation.Continue(bytes, previousBytes);
    }

    private async Task FollowActiveRecordingTimesAsync(DateTime now)
    {
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.ToList();
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

            if (r.Status == ReservationStatus.Failed && IsProcessAlive(session.ProcessId) && now < session.PlannedEndTime)
            {
                _store.UpdateStatus(r.Id, ReservationStatus.Recording);
                r = _store.GetById(session.ReservationId) ?? r;
                _log.Add("REC_STATUS_RECONCILE", $"R{session.ReservationId}",
                    $"result=RESTORED_TO_RECORDING reason=active_tvairepgrec_session_is_source_of_truth pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} previousStatus=Failed rule=v0.9.83_failed_direct_route_guard");
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

            var ev = _epgStore.GetOne(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId);
            if (ev is null)
            {
                _log.Add("REC_FOLLOW_CHECK", $"R{r.Id}",
                    $"result=MISS reason=epg_event_not_found nid={r.NetworkId} tsid={r.TransportStreamId} svcId={r.ServiceId} eventId={r.EventId} tuner={SafeValue(session.Lease.Name)} source=active_recording_session_no_extra_tuner plannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss}");
                continue;
            }

            var singleProgramFollowedEnd = ev.End.AddSeconds(_ini.PostEndMarginSeconds);
            var followedEnd = ResolveChainPlannedEndForRecordingFollow(r, singleProgramFollowedEnd, session.PlannedEndTime);
            var chainPlannedEndPreserved = followedEnd > singleProgramFollowedEnd;
            var epgStartMovesEarlier = ev.Start < r.StartTime.AddSeconds(-30);
            var protectUserRuntimeStart = IsUserRuntimeStartRewindGuardTarget(r, session);
            var suppressStartRewind = epgStartMovesEarlier && protectUserRuntimeStart;
            var effectiveNewStart = suppressStartRewind ? r.StartTime : ev.Start;
            var startChanged = Math.Abs((effectiveNewStart - r.StartTime).TotalSeconds) > 30;
            var endChanged = Math.Abs((ev.End - r.EndTime).TotalSeconds) > 30;
            var plannedChanged = Math.Abs((followedEnd - session.PlannedEndTime).TotalSeconds) > 1;

            _log.Add("REC_FOLLOW_CHECK", $"R{r.Id}",
                $"result=OK source=active_recording_session_no_extra_tuner tuner={SafeValue(session.Lease.Name)} pid={session.ProcessId} " +
                $"event={r.NetworkId}/{r.TransportStreamId}/{r.ServiceId}/{r.EventId} " +
                $"dbStart={r.StartTime:MM/dd HH:mm:ss} epgStart={ev.Start:MM/dd HH:mm:ss} effectiveNewStart={effectiveNewStart:MM/dd HH:mm:ss} dbEnd={r.EndTime:MM/dd HH:mm:ss} epgEnd={ev.End:MM/dd HH:mm:ss} " +
                $"sessionPlannedEnd={session.PlannedEndTime:MM/dd HH:mm:ss} singleProgramPlannedEnd={singleProgramFollowedEnd:MM/dd HH:mm:ss} " +
                $"followedPlannedEnd={followedEnd:MM/dd HH:mm:ss} chainPlannedEndPreserved={chainPlannedEndPreserved} startRewindSuppressed={suppressStartRewind} changed={startChanged || endChanged || plannedChanged} " +
                $"rule=v0.11.582_rec_follow_user_start_rewind_guard");

            if (suppressStartRewind)
            {
                _log.Add("REC_FOLLOW_START_REWIND_GUARD", $"R{r.Id}",
                    $"result=SUPPRESSED source={r.Source} reason=user_runtime_recording_start_is_source_of_truth oldStart={r.StartTime:MM/dd HH:mm:ss} epgStart={ev.Start:MM/dd HH:mm:ss} keptStart={effectiveNewStart:MM/dd HH:mm:ss} " +
                    $"epgEnd={ev.End:MM/dd HH:mm:ss} plannedEnd={followedEnd:MM/dd HH:mm:ss} tuner={SafeValue(session.Lease.Name)} actualTuner={SafeValue(r.ActualTunerName)} pid={session.ProcessId} userChain={r.IsUserChain} chainPrev=R{(r.UserChainPreviousId.HasValue ? r.UserChainPreviousId.Value.ToString() : "-")} chainRoot=R{(r.UserChainRootId.HasValue ? r.UserChainRootId.Value.ToString() : "-")} " +
                    $"rule=v0.11.582_rec_follow_user_start_rewind_guard");
            }

            if (!startChanged && !endChanged && !plannedChanged)
                continue;

            if (startChanged || endChanged)
            {
                _store.UpdateStartEndTime(r.Id, effectiveNewStart, ev.End);
            }
            session.UpdatePlannedEndTime(followedEnd);
            session.Lease.UpdatePlannedEndTime(followedEnd, $"RecordingFollow R{r.Id}");

            _log.Add("REC_FOLLOW_UPDATE", $"R{r.Id}",
                $"source=active_recording_session_no_extra_tuner route=ALLOC_ROUTE oldStart={r.StartTime:MM/dd HH:mm:ss} newStart={effectiveNewStart:MM/dd HH:mm:ss} epgStart={ev.Start:MM/dd HH:mm:ss} startRewindSuppressed={suppressStartRewind} " +
                $"oldEnd={r.EndTime:MM/dd HH:mm:ss} newEnd={ev.End:MM/dd HH:mm:ss} newPlannedEnd={followedEnd:MM/dd HH:mm:ss} " +
                $"tuner={SafeValue(session.Lease.Name)} pid={session.ProcessId} now={now:MM/dd HH:mm:ss} rule=v0.11.582_rec_follow_user_start_rewind_guard");

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
                    ConflictLogTitle: $"R{r.Id}"));
            }
            catch (Exception ex)
            {
                _log.Add("REC_FOLLOW_UPDATE", $"R{r.Id}", $"alloc_route_error={TrimForLog(ex.Message, 240)}");
            }

            if (now >= followedEnd)
            {
                _log.Add("REC_FOLLOW_END_DETECTED", $"R{r.Id}",
                    $"reason=followed_event_already_ended now={now:MM/dd HH:mm:ss} epgEnd={ev.End:MM/dd HH:mm:ss} plannedEnd={followedEnd:MM/dd HH:mm:ss} tuner={SafeValue(session.Lease.Name)} route=CheckSessionEndsAsync_next");
            }
        }

        await Task.CompletedTask;
    }

    // ─── 録画終了チェック ─────────────────────────────────────────

    private async Task CheckSessionEndsAsync(DateTime now)
    {
        List<RecordingSession> toEnd;
        lock (_sessionGate)
        {
            toEnd = _activeSessions.Values
                .Where(s => now >= s.PlannedEndTime)
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
                            $"stopSignalSuppressed=True leaseReleaseSuppressed=True reason=boundary_handoff_waiting_for_bridge_file_switch rule=v0.9.83_failed_direct_route_guard");
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

    private async Task StopSessionAsync(RecordingSession session, ReservationStatus finalStatus, bool suppressNormalStopForBoundary = false)
    {
        var stopGateRequestAt = DateTime.Now;
        _log.Add("CHAIN_TRACE", $"R{session.ReservationId}", $"[CHAIN] stage=stop_gate_wait_start finalStatus={finalStatus} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} activeSessions={FormatActiveSessionsForLog()}");
        await _stopGate.WaitAsync();
        var stopGateWaitMs = (int)(DateTime.Now - stopGateRequestAt).TotalMilliseconds;
        _log.Add("CHAIN_TRACE", $"R{session.ReservationId}", $"[CHAIN] stage=stop_gate_entered waitMs={stopGateWaitMs} finalStatus={finalStatus} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} activeSessions={FormatActiveSessionsForLog()}");
        try
        {
            var pid = session.ProcessId;
            var rid = session.ReservationId;
            string? terminalFailureReason;
            lock (_sessionGate) _recordingTerminalFailureReasons.TryGetValue(rid, out terminalFailureReason);
            var terminalFailureReasonPart = string.IsNullOrWhiteSpace(terminalFailureReason) ? "terminalFailureReason=-" : $"terminalFailureReason={SafeValue(terminalFailureReason)}";
            var hasPendingChainSuccessor = TryGetPendingChainSuccessorId(rid, out var pendingChainSuccessorId);
            var isChainBoundaryHandoff = finalStatus == ReservationStatus.Completed && hasPendingChainSuccessor;
            if (isChainBoundaryHandoff)
            {
                _log.Add("CHAIN_HANDOFF_GUARD", $"R{rid}",
                    $"stage=stop_route_guard result=BOUNDARY_HANDOFF_DETECTED predecessor=R{rid} successor=R{pendingChainSuccessorId} " +
                    $"suppressNormalStopForBoundary={suppressNormalStopForBoundary} protectedObservation=True recordingMode=stop_restart separateTsFile=True " +
                    $"normalManualStopRouteUnchanged=True rule=v0.9.83_failed_direct_route_guard");

                if (suppressNormalStopForBoundary)
                {
                    lock (_sessionGate) _chainBoundaryNormalStopSuppressed.Add(rid);
                    var boundaryStopReservation = _store.GetById(rid);
                    var boundaryGroup = boundaryStopReservation is not null ? ResolveGroup(boundaryStopReservation) : null;
                    _log.Add("REC_STOP_COMMON_ENTER", $"R{rid}",
                        $"mode=SuppressedByChainBoundaryHandoff group={SafeValue(boundaryGroup)} pid={pid} route=TvAIrEpgRecOnly " +
                        $"recorderStopTarget=False chainHandoff=True boundaryHandoff=True stopSignalSuppressed=True leaseReleaseSuppressed=True " +
                        $"successor=R{pendingChainSuccessorId} rule=v0.9.83_failed_direct_route_guard");
                    _log.Add("CHAIN_HANDOFF_GUARD", $"R{rid}",
                        $"stage=stop_route_guard result=NORMAL_STOP_SUPPRESSED predecessor=R{rid} successor=R{pendingChainSuccessorId} " +
                        $"actualTuner={SafeValue(session.Lease.Name)} did={SafeValue(session.Lease.Did)} pid={pid} " +
                        $"stopSignalSuppressed=True leaseReleaseSuppressed=True statusUpdateSuppressed=True activeSessionKept=True " +
                        $"reason=stop_restart_route_never_suppresses_normal_boundary_stop rule=v0.9.83_failed_direct_route_guard");
                    return;
                }
            }
            var postKillWaitMs = hasPendingChainSuccessor ? ChainPostKillDeviceReleaseWaitMs : PostKillDeviceReleaseWaitMs;
            var postReleaseSettleMs = hasPendingChainSuccessor ? ChainPostReleaseAllocationSettleMs : PostReleaseAllocationSettleMs;
            if (hasPendingChainSuccessor)
            {
                _log.Add("CHAIN_HANDOFF_WAIT_POLICY", $"R{rid}",
                    $"successor=R{pendingChainSuccessorId} postKillWaitMs={postKillWaitMs} postReleaseSettleMs={postReleaseSettleMs} reason=protect_successor_head");
            }

            LogRecordingStopChainContext(session, finalStatus, "before_kill");
            _log.Add("CHAIN_TRACE", $"R{rid}", $"[CHAIN] stage=stop_before_kill successor={(hasPendingChainSuccessor ? $"R{pendingChainSuccessorId}" : "-")} finalStatus={finalStatus} pid={pid} lease={session.Lease.Name} postKillWaitMs={postKillWaitMs} postReleaseSettleMs={postReleaseSettleMs} status={_tunerPool.GetStatusSummary()}");
            TvTestProcessAuditor.EmitViewingProtectionAudit(
                _log,
                "REC_STOP_BEFORE_KILL",
                $"R{rid}",
                session.Lease.Did,
                session.Lease.BonDriverFileName,
                _tunerPool.GetProtectedViewingDids(),
                blockOnSameDid: false);
            _log.Add("PROCESS_OWNERSHIP", $"R{rid}", $"stopTargetPid={pid} ownedByReservation=True externalTvTestSkipped=True recFile={session.RecordingFilePath}");
            _log.Add("Scheduler", $"R{rid}", $"録画停止開始: PID={pid} finalStatus={finalStatus} stopGate=entered recFile={session.RecordingFilePath}");

            using var stopPhase = StopPhaseGate.Enter($"R{rid}",
                msg => _log.Add("Scheduler", $"R{rid}", msg));

            var stopReservation = _store.GetById(rid);
            var stopGroup = stopReservation is not null ? ResolveGroup(stopReservation) : null;
            if (!string.IsNullOrWhiteSpace(stopGroup))
            {
                var stopSuppressUntil = DateTime.Now.AddSeconds(RecordingStopEpgSuppressAfterSec + Math.Max(postKillWaitMs, postReleaseSettleMs) / 1000);
                RecordingLifecycleGate.SuppressEpg(
                    stopGroup,
                    stopSuppressUntil,
                    "recording_stop_or_post_stop_cooldown",
                    $"R{rid}",
                    stopReservation is null ? $"pid={pid}" : FormatReservationForLifecycleLog(stopReservation));
                _log.Add("REC_STOP_GROUP_LOCK", $"R{rid}",
                    $"group={stopGroup} until={stopSuppressUntil:MM/dd HH:mm:ss} reason=recording_stop_or_post_stop_cooldown " +
                    (stopReservation is null ? $"pid={pid}" : FormatReservationForAudit(stopReservation, "stop_epg_suppressed")));
            }
            var recorderStopTarget = stopReservation is not null && pid > 0;
            var stopMode = recorderStopTarget ? "TvAIrEpgRecStopSignalThenWait" : "SinglePidExitOnly";
            _log.Add("REC_STOP_COMMON_ENTER", $"R{rid}",
                $"mode={stopMode} group={SafeValue(stopGroup)} pid={pid} route={("TvAIrEpgRecOnly")} recorderStopTarget={recorderStopTarget} chainHandoff={hasPendingChainSuccessor} boundaryHandoff={isChainBoundaryHandoff} rule=v0.9.83_failed_direct_route_guard");
            _log.Add("REC_STOP_MODE", $"R{rid}",
                $"mode={stopMode} reason=v0.5.65_stop_signal_then_graceful_exit group={SafeValue(stopGroup)} pid={pid} stopTarget={recorderStopTarget} legacyTvTestRoute=False chainHandoff={hasPendingChainSuccessor} pt3LockDuringStopWait=False timeoutMs={TvAIrEpgRecStopTimeoutMs}");

            // v0.5.65: TvAIrEpgRec は旧TVTest停止ブリッジではなく、stop signalで自前停止させる。
            var recorderStopSucceeded = await TryStopTvAIrEpgRecProcessAsync(session, rid, TvAIrEpgRecStopTimeoutMs).ConfigureAwait(false);
            var processExitedWithoutKill = recorderStopSucceeded;

            if (!processExitedWithoutKill)
            {
                // v0.4.14: Bridge RecordStop が失敗・無応答、または通常終了待ちで残存した場合だけ、
                // TvAIr所有PID単体終了へフォールバックする。成功時は Kill を避ける。
                _log.Add("REC_STOP_SINGLE_PID_EXIT", $"R{rid}",
                    $"stage=tuner_device_stop_lock_skipped_for_single_pid_fallback pid={pid} lease={session.Lease.Name} reason=do_not_expand_internal_tuner_device_lock_on_timeout_kill status={_tunerPool.GetStatusSummary()} rule=v0.8.21_stop_root_trace_no_wait_extension");
                _log.Add("REC_STOP_SINGLE_PID_EXIT", $"R{rid}",
                    $"stage=before_single_pid_exit pid={pid} route={("TvAIrEpgRecOnly")} stopSignalTried={recorderStopTarget} stopSignalSucceeded={recorderStopSucceeded} gracefulExitSucceeded=False treeKillForbidden=True fallback=True");
                await KillProcessTreeAsync(pid, rid);
                _log.Add("REC_STOP_SINGLE_PID_EXIT", $"R{rid}",
                    $"stage=after_single_pid_exit pid={pid} route={("TvAIrEpgRecOnly")} stopSignalTried={recorderStopTarget} stopSignalSucceeded={recorderStopSucceeded} gracefulExitSucceeded=False treeKillForbidden=True fallback=True");
                _log.Add("CHAIN_TRACE", $"R{rid}", $"[CHAIN] stage=single_pid_exit_before_device_wait pid={pid} lease={session.Lease.Name} status={_tunerPool.GetStatusSummary()}");
            }
            else
            {
                _log.Add("REC_STOP_SINGLE_PID_EXIT", $"R{rid}",
                    $"stage=skipped pid={pid} route={("TvAIrEpgRecOnly")} recorderStopSucceeded=True gracefulExitSucceeded=True reason=no_kill_after_recordstop_ok");
                _log.Add("CHAIN_TRACE", $"R{rid}", $"[CHAIN] stage=pt3_stop_lock_skipped_process_already_exited pid={pid} lease={session.Lease.Name} status={_tunerPool.GetStatusSummary()}");
            }

            // taskkillの完了はTVTestプロセス終了であって、BonDriverのデバイス解放完了ではない。
            // ここでLeaseをFree化すると、別TVTest/LIVETestが解放途中の同一デバイスに触れてフリーズし得るため、
            // Free扱いにする前に安全待機を入れる。ただし TUNER_DEVICE_LOCK は保持しない。
            if (postKillWaitMs > 0)
            {
                _log.Add("REC_STOP_DEVICE_SETTLE", $"R{rid}",
                    $"stage=begin waitMs={postKillWaitMs} chainHandoff={hasPendingChainSuccessor} tunerStatus={_tunerPool.GetStatusSummary()} rule=v0.5.65_wait_before_tunerpool_release");
                _log.Add("Scheduler", $"R{rid}",
                    $"録画停止後デバイス解放待機開始: waitMs={postKillWaitMs} chainHandoff={hasPendingChainSuccessor} tunerStatus={_tunerPool.GetStatusSummary()}");
                await Task.Delay(postKillWaitMs);
                _log.Add("REC_STOP_DEVICE_SETTLE", $"R{rid}",
                    $"stage=end waitMs={postKillWaitMs} chainHandoff={hasPendingChainSuccessor}");
                _log.Add("Scheduler", $"R{rid}",
                    $"録画停止後デバイス解放待機完了: waitMs={postKillWaitMs} chainHandoff={hasPendingChainSuccessor}");
            }

            var verifyReservation = _store.GetById(rid);
            RecordingTsVerifier.VerificationResult? tsVerification = null;
            if (verifyReservation is not null && !string.IsNullOrWhiteSpace(session.RecordingFilePath))
                tsVerification = await RecordingTsVerifier.VerifyAsync(verifyReservation, session.RecordingFilePath, "after_stop_before_release", _log);
            else if (verifyReservation is null)
                _log.Add("REC_TS_VERIFY", $"R{rid}", $"stage=after_stop_before_release result=SKIP reason=reservation_not_found path={session.RecordingFilePath}");
            else
                _log.Add("REC_TS_VERIFY", $"R{rid}", "stage=after_stop_before_release result=SKIP reason=effective_recording_file_unknown");

            await WaitForTvAIrEpgRecResultFileAsync(session, rid, pid, 4000).ConfigureAwait(false);
            var recorderOutcome = ReadDirectRecorderOutcome(session.ResponsePath);
            var qualityService = verifyReservation?.ServiceName ?? stopReservation?.ServiceName ?? "-";
            var qualityTitle = verifyReservation?.Title ?? stopReservation?.Title ?? "-";
            var qualityClassification = ClassifyDirectRecorderQuality(recorderOutcome, tsVerification);
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
                $"rule=v0.6.41_drop_quality_correlation_summary");
            LogDirectRecorderQualityCorrelation(rid, qualityService, qualityTitle, qualityClassification, recorderOutcome, qualityContext, session.Lease.Name, session.Lease.Did);
            LogDirectRecorderRuntimeTimeline(rid, qualityService, qualityTitle, qualityGroup ?? "-", recorderOutcome);
            if (finalStatus == ReservationStatus.Completed && recorderOutcome.ResponseExists && !recorderOutcome.Success)
            {
                var service = verifyReservation?.ServiceName ?? stopReservation?.ServiceName ?? "-";
                var title = verifyReservation?.Title ?? stopReservation?.Title ?? "-";
                if (CanKeepCompletedByTsVerification(recorderOutcome, tsVerification))
                {
                    _log.Add("TVAIREPGREC_FINAL_STATUS", $"R{rid}",
                        $"result=OVERRIDDEN_BY_REC_TS_VERIFY service={SafeValue(service)} title={TrimForLog(title, 80)} responseSuccess=False finalStatus=Completed " + terminalFailureReasonPart + " " +
                        $"verifyResult={SafeValue(tsVerification?.Result)} verifyVerdict={SafeValue(tsVerification?.Verdict)} verifyReadable={SafeValue(tsVerification?.ReadableJudgement)} " +
                        $"bytesWritten={recorderOutcome.BytesWritten} packetsWritten={recorderOutcome.PacketsWritten} outputScrambled={SafeValue(recorderOutcome.OutputScrambledPackets)} outputSyncErrors={SafeValue(recorderOutcome.OutputSyncErrors)} response={recorderOutcome.Summary} " +
                        $"rule=v0.10.14_wake_credential_context_record_verdict_crosscheck");
                }
                else
                {
                    _log.Add("TVAIREPGREC_FINAL_STATUS", $"R{rid}",
                        $"result=FAILED_BY_TVAIREPGREC service={SafeValue(service)} title={TrimForLog(title, 80)} responseSuccess=False " + terminalFailureReasonPart + " " +
                        $"verifyResult={SafeValue(tsVerification?.Result)} verifyVerdict={SafeValue(tsVerification?.Verdict)} verifyReadable={SafeValue(tsVerification?.ReadableJudgement)} " +
                        $"bytesWritten={recorderOutcome.BytesWritten} packetsWritten={recorderOutcome.PacketsWritten} response={recorderOutcome.Summary} " +
                        $"rule=v0.10.14_wake_credential_context_record_verdict_crosscheck");
                    finalStatus = ReservationStatus.Failed;
                }
            }
            else
            {
                var service = verifyReservation?.ServiceName ?? stopReservation?.ServiceName ?? "-";
                var title = verifyReservation?.Title ?? stopReservation?.Title ?? "-";
                var finalResultKind = recorderOutcome.ResponseExists
                    ? (recorderOutcome.Success ? "OK_CLEAR_OR_TVAIREPGREC_OK" : "TVAIREPGREC_NG_NON_COMPLETED")
                    : (tsVerification?.ClearEnoughForCompleted == true ? "NO_RESPONSE_BUT_REC_TS_VERIFY_CLEAR" : "NO_RESPONSE");
                _log.Add("TVAIREPGREC_FINAL_STATUS", $"R{rid}",
                    $"result={finalResultKind} service={SafeValue(service)} title={TrimForLog(title, 80)} finalStatus={finalStatus} " + terminalFailureReasonPart + " " +
                    $"verifyResult={SafeValue(tsVerification?.Result)} verifyVerdict={SafeValue(tsVerification?.Verdict)} verifyReadable={SafeValue(tsVerification?.ReadableJudgement)} " +
                    $"response={recorderOutcome.Summary} rule=v0.9.92_result_response_stabilize");
            }

            CleanupTvAIrEpgRecRecordingRuntimeFiles(session, recorderOutcome, rid, finalStatus, tsVerification);

            _log.Add("CHAIN_TRACE", $"R{rid}", $"[CHAIN] stage=before_lease_dispose successor={(hasPendingChainSuccessor ? $"R{pendingChainSuccessorId}" : "-")} pid={pid} lease={session.Lease.Name} status={_tunerPool.GetStatusSummary()}");
            session.Lease.Dispose();
            _log.Add("CHAIN_TRACE", $"R{rid}", $"[CHAIN] stage=after_lease_dispose successor={(hasPendingChainSuccessor ? $"R{pendingChainSuccessorId}" : "-")} pid={pid} status={_tunerPool.GetStatusSummary()}");
            _store.UpdateStatus(rid, finalStatus, force: finalStatus == ReservationStatus.Failed);
            _store.MarkRecordingFinished(rid);
            lock (_sessionGate)
            {
                _activeSessions.Remove(rid);
                _recordingTerminalFailureReasons.Remove(rid);
            }
            RemoveChainDirectRecorderSessionScaffold(rid, "stop_session_finally", finalStatus.ToString());
            TvAirManagedProcessRegistry.Unregister(pid);
            session.ActivityHandle?.Dispose();
            _log.Add("PROCESS_OWNERSHIP", $"R{rid}",
                $"unregisterManagedPid=True pid={pid} reason=recording_session_finished rule=v0.5.65_route_classification_directrecorder");

            _log.Add("REC_STOP_COMMON_EXIT", $"R{rid}",
                $"status={finalStatus} pid={pid} tuner={SafeValue(session.Lease.Name)} route={("TvAIrEpgRecOnly")} tunerStatus={_tunerPool.GetStatusSummary()}");
            _log.Add("Scheduler", $"R{rid}", $"録画終了完了: status={finalStatus} tunerStatus={_tunerPool.GetStatusSummary()}");
            LogRecordingStopChainContext(session, finalStatus, "after_release_and_status_update");

            // Free化直後に共通割り当て・Wake再構築を走らせず、BonDriverが落ち着く時間を追加する。
            if (postReleaseSettleMs > 0)
            {
                await Task.Delay(postReleaseSettleMs);
                _log.Add("Scheduler", $"R{rid}", $"録画終了後スロット解放確定待ち完了: waitMs={postReleaseSettleMs} chainHandoff={hasPendingChainSuccessor} tunerStatus={_tunerPool.GetStatusSummary()}");
            }
            else if (hasPendingChainSuccessor)
            {
                _log.Add("Scheduler", $"R{rid}", $"録画終了後スロット解放確定待ち省略: chainHandoff=True successor=R{pendingChainSuccessorId}");
            }

            // 録画終了後に競合フラグを再評価（チューナー解放により競合が解消する場合がある）。
            // 停止フェーズ中に侵入したALLOC_ROUTEはここでまとめて消費し、
            // TunerPoolのFree化とクールダウン完了後の状態だけを基準に1回だけ再評価する。
            var deferred = StopPhaseGate.ConsumeDeferred();
            if (deferred.allocRoute || deferred.wakeRebuild || deferred.suppressedAllocCount > 0 || deferred.suppressedWakeCount > 0)
            {
                _log.Add("Scheduler", $"R{rid}",
                    $"STOP_PHASE_CONSUME pendingAlloc={deferred.allocCount} pendingWake={deferred.wakeCount} suppressedAlloc={deferred.suppressedAllocCount} suppressedWake={deferred.suppressedWakeCount} reason=after_recording_stop_settled");
            }
            ReevaluateAndLog($"R{rid}終了後", bypassStopPhaseGate: true);

            // Wake再構築は停止ごとに即時実行しない。次回メンテナンスTickにまとめる。
            RequestWakeTaskRefreshSoon($"R{rid}", "録画終了後");

            TriggerRecordingAfterActionIfNeeded(rid, finalStatus, verifyReservation ?? stopReservation);
        }
        finally
        {
            _stopGate.Release();
        }
    }


    private void TriggerRecordingAfterActionIfNeeded(int reservationId, ReservationStatus finalStatus, Reservation? reservation)
    {
        var action = IniSettingsService.NormalizeRecordingAfterAction(_ini.RecordingAfterAction);
        var service = reservation?.ServiceName ?? "-";
        var title = reservation?.Title ?? "-";
        if (action == "none")
        {
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
        var delayMs = (int)TimeSpan.FromMinutes(delayMinutes).TotalMilliseconds;
        _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result=ARMED action={action} reason=last_completed_recording service={SafeValue(service)} title={TrimForLog(title, 80)} delayMinutes={delayMinutes} delayMs={delayMs} basis=after_recording_process_end rule=v0.6.36_macro_cleanup_after_action_delay");
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                int currentActive;
                lock (_sessionGate) currentActive = _activeSessions.Count;
                if (currentActive > 0)
                {
                    _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result=CANCEL action={action} reason=new_recording_started activeCount={currentActive} delayMinutes={delayMinutes}");
                    return;
                }

                var currentAction = IniSettingsService.NormalizeRecordingAfterAction(_ini.RecordingAfterAction);
                var currentDelayMinutes = IniSettingsService.NormalizeRecordingAfterActionDelayMinutes(_ini.RecordingAfterActionDelayMinutes);
                if (currentAction != action || currentDelayMinutes != delayMinutes)
                {
                    _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result=CANCEL action={action} reason=setting_changed currentAction={currentAction} armedDelayMinutes={delayMinutes} currentDelayMinutes={currentDelayMinutes} rule=v0.6.36_macro_cleanup_after_action_delay");
                    return;
                }

                if (action == "sleep")
                {
                    var ok = SetSuspendState(false, true, false);
                    var err = ok ? 0 : Marshal.GetLastWin32Error();
                    _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result={(ok ? "EXECUTED" : "FAILED")} action=sleep win32={err} delayMinutes={delayMinutes} service={SafeValue(service)} title={TrimForLog(title, 80)} rule=v0.6.36_macro_cleanup_after_action_delay");
                }
                else if (action == "shutdown")
                {
                    using var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "shutdown.exe",
                        Arguments = "/s /t 0 /c \"TvAIr 録画終了後アクション\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result=EXECUTED action=shutdown pid={(p?.Id.ToString() ?? "-")} windowsTimeoutSec=0 delayMinutes={delayMinutes} service={SafeValue(service)} title={TrimForLog(title, 80)} rule=v0.6.36_macro_cleanup_after_action_delay");
                }
            }
            catch (Exception ex)
            {
                _log.Add("RECORDING_AFTER_ACTION", $"R{reservationId}", $"result=ERROR action={action} delayMinutes={delayMinutes} error={ex.GetType().Name}:{ex.Message}");
            }
        });
    }

    private void CleanupTvAIrEpgRecRecordingRuntimeFiles(RecordingSession session, DirectRecorderOutcome outcome, int reservationId, ReservationStatus finalStatus, RecordingTsVerifier.VerificationResult? tsVerification)
    {
        var responseMissingButFileClear = !outcome.ResponseExists && tsVerification?.ClearEnoughForCompleted == true;
        // 表版: 成否に関係なく内部 job/result/progress/runtime/stopSignal は保持しない。
        // 不具合報告は外部掲示板などで受け、配布版の長期運用では蓄積を避ける。
        var shouldKeep = false;
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
                $"result=KEPT reason=recording_failed_worker_reported_ng_or_unverified_response_missing status={finalStatus} files={targets.Count} job={SafeValue(session.JobPath)} result={SafeValue(session.ResponsePath)} progress={SafeValue(session.ProgressPath)} runtime={SafeValue(session.RuntimeStatsPath)} stopSignal={SafeValue(session.StopSignalPath)} rule=v0.8.01_epg_transport_stream_import_restore");
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
            $"result=OK status={finalStatus} deleted={deleted} failed={failed} targetFiles={targets.Count} reason=completed_recording_runtime_files_are_internal_artifacts rule=v0.8.01_epg_transport_stream_import_restore");
    }

    private async Task<string> WaitForTvAIrEpgRecResultFileAsync(RecordingSession session, int reservationId, int pid, int waitMs)
    {
        if (string.IsNullOrWhiteSpace(session.ResponsePath))
            return "no_response_path";
        if (File.Exists(session.ResponsePath))
            return "already_exists";

        var started = DateTime.UtcNow;
        var polls = 0;
        while ((DateTime.UtcNow - started).TotalMilliseconds < waitMs)
        {
            polls++;
            if (File.Exists(session.ResponsePath))
            {
                var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                _log.Add("TVAIREPGREC_RESULT_WAIT", $"R{reservationId}",
                    $"result=FOUND pid={pid} elapsedMs={elapsed} polls={polls} response={SafeValue(session.ResponsePath)} rule=v0.9.92_result_response_stabilize");
                return $"found_after_{elapsed}ms";
            }
            await Task.Delay(200).ConfigureAwait(false);
        }

        // v0.10.00:
        // 結果JSONのflush/ファイル検出がTS検証より遅れる場合、ここで即 missing と断定すると
        // 後段のTS clear判定と矛盾したログになる。録画ファイルが実体を持つ場合は
        // 「結果JSON遅延/欠落だが録画実体あり」として扱い、録画成否はTS検証と最終判定へ委ねる。
        var fileSize = File.Exists(session.RecordingFilePath) ? new FileInfo(session.RecordingFilePath).Length : -1;
        var processAlive = IsProcessAlive(pid);
        if (fileSize > 0)
        {
            _log.Add("TVAIREPGREC_RESULT_WAIT", $"R{reservationId}",
                $"result=DEFERRED_RESPONSE_FILE_PRESENT pid={pid} processAlive={processAlive} waitMs={waitMs} polls={polls} fileSize={fileSize} " +
                $"response={SafeValue(session.ResponsePath)} progress={ReadTvAIrEpgRecStopProgressSummary(session.ProgressPath)} " +
                $"action=use_ts_verify_and_final_status rule=v0.10.00_result_response_wait_stabilize");
            return $"deferred_response_file_present_{waitMs}ms";
        }

        _log.Add("TVAIREPGREC_RESULT_WAIT", $"R{reservationId}",
            $"result=MISSING_AFTER_WAIT pid={pid} waitMs={waitMs} polls={polls} response={SafeValue(session.ResponsePath)} progress={ReadTvAIrEpgRecStopProgressSummary(session.ProgressPath)} rule=v0.10.00_result_response_wait_stabilize");
        return $"missing_after_{waitMs}ms";
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
                    $"result=NG reason=stop_signal_path_empty pid={pid} rule=v0.5.65_no_kill_before_flush");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(session.StopSignalPath) ?? AppContext.BaseDirectory);
            await File.WriteAllTextAsync(session.StopSignalPath, DateTime.Now.ToString("O")).ConfigureAwait(false);
            _log.Add("TVAIREPGREC_STOP_SIGNAL", $"R{reservationId}",
                $"result=SENT pid={pid} stopSignal={SafeValue(session.StopSignalPath)} response={SafeValue(session.ResponsePath)} timeoutMs={timeoutMs} rule=v0.5.65_stop_file_graceful_flush");

            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalMilliseconds < timeoutMs)
            {
                bool exited = false;
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    p.Refresh();
                    exited = p.HasExited;
                }
                catch (ArgumentException)
                {
                    exited = true;
                }

                if (exited)
                {
                    var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                    var responseWait = await WaitForTvAIrEpgRecResultFileAsync(session, reservationId, pid, 4000).ConfigureAwait(false);
                    var responseExists = !string.IsNullOrWhiteSpace(session.ResponsePath) && File.Exists(session.ResponsePath);
                    var fileSize = File.Exists(session.RecordingFilePath) ? new FileInfo(session.RecordingFilePath).Length : -1;
                    var bridgeResponse = ReadDirectRecorderResponseSummary(session.ResponsePath);
                    _log.Add("TVAIREPGREC_STOP_RESULT", $"R{reservationId}",
                        $"result=OK pid={pid} elapsedMs={elapsed} responseExists={responseExists} responseWait={responseWait} fileSize={fileSize} path={SafeValue(session.RecordingFilePath)} response={bridgeResponse} rule=v0.9.92_result_response_stabilize");
                    return true;
                }
                await Task.Delay(250).ConfigureAwait(false);
            }

            var timeoutResponse = ReadDirectRecorderResponseSummary(session.ResponsePath);
            var stopProgress = ReadTvAIrEpgRecStopProgressSummary(session.ProgressPath);
            _log.Add("TVAIREPGREC_STOP_RESULT", $"R{reservationId}",
                $"result=TIMEOUT pid={pid} timeoutMs={timeoutMs} response={timeoutResponse} progress={stopProgress} fallback=single_pid_exit rule=v0.8.21_stop_root_trace_no_wait_extension");
            return false;
        }
        catch (Exception ex)
        {
            var exceptionResponse = ReadDirectRecorderResponseSummary(session.ResponsePath);
            _log.Add("TVAIREPGREC_STOP_RESULT", $"R{reservationId}",
                $"result=NG pid={pid} error={TrimForLog(ex.Message, 240)} response={exceptionResponse} fallback=single_pid_exit rule=v0.5.65_bridge_stop_exception");
            return false;
        }
    }

    /// <summary>
    /// Bridge RecordStop が成功した録画用 TVTest を、Killではなく通常終了へ寄せる。
    /// CloseMainWindow が使えない/待っても残る場合のみ呼び出し側で単体PID終了へフォールバックする。
    /// </summary>
    private async Task<bool> TryGracefulTvTestExitAsync(int pid, int reservationId, int waitMs, string route)
    {
        System.Diagnostics.Process? target = null;
        try
        {
            target = System.Diagnostics.Process.GetProcessById(pid);
            if (target.HasExited)
            {
                _log.Add("REC_STOP_GRACEFUL_EXIT", $"R{reservationId}",
                    $"result=OK stage=already_exited pid={pid} route={route} waitMs=0 killIssued=False rule=v0.4.14_no_kill_after_recordstop_ok");
                return true;
            }
        }
        catch (ArgumentException)
        {
            _log.Add("REC_STOP_GRACEFUL_EXIT", $"R{reservationId}",
                $"result=OK stage=process_not_found pid={pid} route={route} waitMs=0 killIssued=False rule=v0.4.14_no_kill_after_recordstop_ok");
            return true;
        }
        catch (Exception ex)
        {
            _log.Add("REC_STOP_GRACEFUL_EXIT", $"R{reservationId}",
                $"result=NG stage=probe_exception pid={pid} route={route} error={TrimForLog(ex.Message, 240)} fallback=single_pid_exit");
            return false;
        }

        var closeIssued = false;
        try
        {
            target.Refresh();
            closeIssued = target.CloseMainWindow();
            _log.Add("REC_STOP_GRACEFUL_EXIT", $"R{reservationId}",
                $"stage=close_main_window pid={pid} route={route} issued={closeIssued} waitMs={waitMs} killIssued=False rule=v0.4.15_recordstop_ok_then_graceful_exit");
        }
        catch (Exception ex)
        {
            _log.Add("REC_STOP_GRACEFUL_EXIT", $"R{reservationId}",
                $"stage=close_main_window_exception pid={pid} route={route} error={TrimForLog(ex.Message, 240)} fallback=single_pid_exit");
            return false;
        }

        var started = DateTime.UtcNow;
        while ((DateTime.UtcNow - started).TotalMilliseconds < waitMs)
        {
            await Task.Delay(250);
            try
            {
                target.Refresh();
                if (target.HasExited)
                {
                    var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                    _log.Add("REC_STOP_GRACEFUL_EXIT", $"R{reservationId}",
                        $"result=OK stage=exited_after_close pid={pid} route={route} elapsedMs={elapsed} closeIssued={closeIssued} killIssued=False");
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                _log.Add("REC_STOP_GRACEFUL_EXIT", $"R{reservationId}",
                    $"result=OK stage=invalid_operation_after_close pid={pid} route={route} elapsedMs={elapsed} closeIssued={closeIssued} killIssued=False");
                return true;
            }
            catch (Exception ex)
            {
                _log.Add("REC_STOP_GRACEFUL_EXIT", $"R{reservationId}",
                    $"result=NG stage=wait_exception pid={pid} route={route} error={TrimForLog(ex.Message, 240)} fallback=single_pid_exit");
                return false;
            }
        }

        _log.Add("REC_STOP_GRACEFUL_EXIT", $"R{reservationId}",
            $"result=TIMEOUT stage=still_alive_after_close pid={pid} route={route} waitMs={waitMs} closeIssued={closeIssued} fallback=single_pid_exit");
        return false;
    }

    /// <summary>
    /// TvAIr が起動・所有している録画用 TVTest の PID 単体だけを終了する。
    /// v0.3.53: taskkill /T と子プロセス kill は禁止。視聴用 TVTest/LIVETest を巻き込まない。
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

            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(250);
                try
                {
                    target.Refresh();
                    if (target.HasExited)
                    {
                        _log.Add("PROC_TRACE", $"R{reservationId}",
                            $"[PROC] stage=after_single_pid_kill pid={pid} alive=False elapsedMs={(i + 1) * 250} entireTree=False");
                        _log.Add("Scheduler", $"R{reservationId}",
                            $"PID={pid} 単体終了完了（taskkill未使用・子プロセスkill禁止）");
                        return;
                    }
                }
                catch (InvalidOperationException)
                {
                    _log.Add("PROC_TRACE", $"R{reservationId}",
                        $"[PROC] stage=after_single_pid_kill pid={pid} alive=False elapsedMs={(i + 1) * 250} entireTree=False");
                    return;
                }
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
            await StopSessionAsync(s, ReservationStatus.Completed);
    }

    // ─── EPG強制退避（予約録画優先） ───────────────────────

    private async Task PreemptManagedEpgBeforeRecordingAsync(string group, Reservation r, CancellationToken ct)
    {
        const int processExitTimeoutMs = 12000;
        const int releaseWaitTimeoutMs = 20000;
        const int releasePollMs = 500;
        var phase = $"EPG_PREEMPT_BEFORE_REC:R{r.Id}";
        var suppressUntil = DateTime.Now.AddSeconds(RecordingDueEpgSuppressBeforeAfterSec);
        RecordingLifecycleGate.SuppressEpg(group, suppressUntil, "recording_preempt_existing_epg", $"R{r.Id}", FormatReservationForLifecycleLog(r));
        _log.Add("REC_PREEMPT_GROUP_LOCK", $"R{r.Id}",
            $"group={group} until={suppressUntil:MM/dd HH:mm:ss} reason=recording_preempt_existing_epg " +
            FormatReservationForAudit(r, "preempt_existing_epg"));

        var beforeEpgSlots = _tunerPool.CountEpgSlots(group);
        var tunerEpgTargets = _tunerPool.GetStatus()
            .Where(s => string.Equals(s.Group, group, StringComparison.OrdinalIgnoreCase)
                && s.UsageKind == TunerUsageKind.Epg
                && s.ProcessId.HasValue)
            .OrderBy(s => s.SlotIndex)
            .ToList();
        var snapshot = TvTestProcessAuditor.Capture(_log, phase, emitLegacyEvents: false);
        var tvTestTargets = snapshot.Processes
            .Where(p => IsManagedEpgCaptureProcess(p) && IsProcessForGroup(p, group))
            .OrderBy(p => p.ProcessId)
            .ToList();

        var preemptTargets = new List<ManagedEpgPreemptTarget>();
        foreach (var slot in tunerEpgTargets)
        {
            preemptTargets.Add(new ManagedEpgPreemptTarget(
                slot.ProcessId!.Value,
                slot.Did,
                slot.BonDriverFileName,
                slot.Name,
                slot.SlotIndex,
                "tuner_pool_epg_slot"));
        }
        foreach (var proc in tvTestTargets)
        {
            if (preemptTargets.Any(x => x.ProcessId == proc.ProcessId)) continue;
            preemptTargets.Add(new ManagedEpgPreemptTarget(
                proc.ProcessId,
                proc.Did ?? "",
                proc.BonDriverFileName ?? "",
                "-",
                -1,
                "tvtest_process_audit"));
        }

        _log.Add("EPG_PREEMPT_REQUEST", $"R{r.Id}",
            $"group={group} epgSlots={beforeEpgSlots} tunerPoolTargets={tunerEpgTargets.Count} tvTestTargets={tvTestTargets.Count} targetProcesses={preemptTargets.Count} " +
            $"pids={FormatPidList(preemptTargets.Select(p => p.ProcessId))} route=ReservationScheduler->TunerPoolProcessId reason=recording_priority_over_manual_epg rule=epg_preempt_release_semantics " +
            FormatReservationForAudit(r, "preempt_request"));

        var stopResults = new List<ManagedEpgStopResult>();
        var forceReleaseOk = 0;
        var forceReleaseMiss = 0;
        var processedPids = new HashSet<int>();
        foreach (var target in preemptTargets)
        {
            ct.ThrowIfCancellationRequested();
            processedPids.Add(target.ProcessId);
            var stopResult = await StopManagedEpgProcessAsync(target, r.Id, group, processExitTimeoutMs, ct);
            stopResults.Add(stopResult);

            var released = _tunerPool.ForceReleaseEpgByProcessId(group, target.ProcessId, target.Did, target.BonDriverFileName, $"R{r.Id}", FormatReservationForLifecycleLog(r));
            if (released) forceReleaseOk++; else forceReleaseMiss++;
        }

        // v0.8.15: PID付与のタイミング競争で初回収集から漏れたEPG slotを、
        // 録画開始直前にもう一度PID付き停止対象として処理する。
        var lateTargets = _tunerPool.GetStatus()
            .Where(s => string.Equals(s.Group, group, StringComparison.OrdinalIgnoreCase)
                && s.UsageKind == TunerUsageKind.Epg
                && s.ProcessId.HasValue
                && !processedPids.Contains(s.ProcessId.Value))
            .OrderBy(s => s.SlotIndex)
            .Select(s => new ManagedEpgPreemptTarget(
                s.ProcessId!.Value,
                s.Did,
                s.BonDriverFileName,
                s.Name,
                s.SlotIndex,
                "late_tuner_pool_epg_slot"))
            .ToList();
        foreach (var target in lateTargets)
        {
            ct.ThrowIfCancellationRequested();
            processedPids.Add(target.ProcessId);
            preemptTargets.Add(target);
            var stopResult = await StopManagedEpgProcessAsync(target, r.Id, group, processExitTimeoutMs, ct);
            stopResults.Add(stopResult);

            var released = _tunerPool.ForceReleaseEpgByProcessId(group, target.ProcessId, target.Did, target.BonDriverFileName, $"R{r.Id}", FormatReservationForLifecycleLog(r));
            if (released) forceReleaseOk++; else forceReleaseMiss++;
        }

        // v0.8.15: 残ったPIDなしEPG leaseは、起動直前/終了直後/PID未反映の一時状態として
        // プロセス停止ではなくTunerPool上の録画優先解放として扱う。
        var pidlessForceReleaseOk = _tunerPool.ForceReleasePidlessEpgSlots(group, $"R{r.Id}", FormatReservationForLifecycleLog(r));

        // force-release直後の集約。ログは増やすのではなく、処理責務が成立したかだけを出す。
        var afterForceReleaseSlots = _tunerPool.CountEpgSlots(group);
        var stoppedAfterForceRelease = stopResults.Count(x => x.Status == "exited_after_kill" || x.Status == "already_exited" || x.Status == "already_gone");
        var stopTimeoutsAfterForceRelease = stopResults.Count(x => x.Status == "timeout");
        var stopErrorsAfterForceRelease = stopResults.Count(x => x.Status == "error");
        var afterForceReleaseResult = afterForceReleaseSlots == 0
            ? (beforeEpgSlots > 0 || preemptTargets.Count > 0 ? "CLEARED_AFTER_FORCE_RELEASE" : "NOOP_AFTER_FORCE_RELEASE")
            : "WAITING_FOR_RELEASE";
        _log.Add("EPG_PREEMPT_RESULT", $"R{r.Id}",
            $"stage=after_force_release result={afterForceReleaseResult} group={group} beforeEpgSlots={beforeEpgSlots} afterEpgSlots={afterForceReleaseSlots} " +
            $"targetProcesses={preemptTargets.Count} stopped={stoppedAfterForceRelease} stopTimeouts={stopTimeoutsAfterForceRelease} stopErrors={stopErrorsAfterForceRelease} " +
            $"forceReleaseOk={forceReleaseOk} forceReleaseMiss={forceReleaseMiss} pidlessReleaseOk={pidlessForceReleaseOk} waitedMs=0 " +
            $"pids={FormatPidList(preemptTargets.Select(p => p.ProcessId))} rule=epg_preempt_release_semantics " +
            FormatReservationForAudit(r, "preempt_after_force_release"));

        var waitedMs = 0;
        while (_tunerPool.CountEpgSlots(group) > 0 && waitedMs < releaseWaitTimeoutMs)
        {
            var remaining = _tunerPool.CountEpgSlots(group);
            _log.Add("EPG_PREEMPT_RELEASE_WAIT", $"R{r.Id}",
                $"group={group} remainingEpgSlots={remaining} waitedMs={waitedMs} nextWaitMs={releasePollMs} limitMs={releaseWaitTimeoutMs}");
            await Task.Delay(releasePollMs, ct);
            waitedMs += releasePollMs;
        }

        var afterEpgSlots = _tunerPool.CountEpgSlots(group);
        var stopped = stopResults.Count(x => x.Status == "exited_after_kill" || x.Status == "already_exited" || x.Status == "already_gone");
        var stopTimeouts = stopResults.Count(x => x.Status == "timeout");
        var stopErrors = stopResults.Count(x => x.Status == "error");
        var result = afterEpgSlots == 0
            ? (beforeEpgSlots > 0 || preemptTargets.Count > 0 ? "CLEARED" : "NOOP")
            : "REMAINING";

        _log.Add("EPG_PREEMPT_RESULT", $"R{r.Id}",
            $"result={result} group={group} beforeEpgSlots={beforeEpgSlots} afterEpgSlots={afterEpgSlots} " +
            $"targetProcesses={preemptTargets.Count} stopped={stopped} stopTimeouts={stopTimeouts} stopErrors={stopErrors} " +
            $"forceReleaseOk={forceReleaseOk} forceReleaseMiss={forceReleaseMiss} pidlessReleaseOk={pidlessForceReleaseOk} waitedMs={waitedMs} " +
            $"pids={FormatPidList(preemptTargets.Select(p => p.ProcessId))} rule=epg_preempt_release_semantics " +
            FormatReservationForAudit(r, "preempt_result"));

        if (afterEpgSlots > 0)
        {
            _log.Add("EPG_PREEMPT_RELEASE_TIMEOUT", $"R{r.Id}",
                $"group={group} remainingEpgSlots={afterEpgSlots} waitedMs={waitedMs} action=continue_to_existing_free_tuner_check risk=recording_may_need_other_free_tuner rule=epg_preempt_release_semantics");
        }
        else if (beforeEpgSlots > 0 || preemptTargets.Count > 0)
        {
            // 録画開始前に既存EPG workerを止めた直後の最小再利用保護。
            // 旧15秒待機は録画開始を遅らせるため使用しない。
            const int EpgPreemptWorkerExitSettleMs = 2000;
            var cooldownMs = EpgPreemptWorkerExitSettleMs;
            _log.Add("EPG_PREEMPT_COOLDOWN_BEGIN", $"R{r.Id}",
                $"group={group} waitedMs={waitedMs} cooldownMs={cooldownMs} reason=TvAIrEpgRec_epg_process_exit_settle_only rule=epg_preempt_release_semantics");
            await Task.Delay(cooldownMs, ct);
            _log.Add("EPG_PREEMPT_COOLDOWN_END", $"R{r.Id}",
                $"group={group} cooldownMs={cooldownMs} result=OK rule=epg_preempt_release_semantics");
        }
        else
        {
            _log.Add("EPG_PREEMPT_NOOP", $"R{r.Id}",
                $"group={group} reason=no_managed_epg_process_or_slot rule=epg_preempt_release_semantics");
        }
    }

    private sealed record ManagedEpgPreemptTarget(
        int ProcessId,
        string? Did,
        string? BonDriverFileName,
        string TunerName,
        int SlotIndex,
        string Source);

    private sealed record ManagedEpgStopResult(
        int ProcessId,
        string Status,
        int WaitedMs);

    private async Task<ManagedEpgStopResult> StopManagedEpgProcessAsync(ManagedEpgPreemptTarget info, int reservationId, string group, int timeoutMs, CancellationToken ct)
    {
        System.Diagnostics.Process? process = null;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(info.ProcessId);
            if (process.HasExited)
            {
                _log.Add("EPG_PREEMPT_PROCESS_EXIT_OK", $"R{reservationId}",
                    $"group={group} pid={info.ProcessId} did={SafeValue(info.Did)} bonDriver={SafeValue(info.BonDriverFileName)} tuner={SafeValue(info.TunerName)} slot={info.SlotIndex} source={info.Source} alreadyExited=True rule=epg_preempt_release_semantics");
                return new ManagedEpgStopResult(info.ProcessId, "already_exited", 0);
            }

            _log.Add("EPG_PREEMPT_PROCESS_STOP_REQUEST", $"R{reservationId}",
                $"group={group} pid={info.ProcessId} did={SafeValue(info.Did)} bonDriver={SafeValue(info.BonDriverFileName)} tuner={SafeValue(info.TunerName)} slot={info.SlotIndex} source={info.Source} " +
                "method=kill_single_process treeKill=False reason=recording_due_epg_lower_priority rule=epg_preempt_release_semantics");
            process.Kill(entireProcessTree: false);

            var waitedMs = 0;
            while (waitedMs < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(250, ct);
                waitedMs += 250;
                process.Refresh();
                if (process.HasExited)
                {
                    _log.Add("EPG_PREEMPT_PROCESS_EXIT_OK", $"R{reservationId}",
                        $"group={group} pid={info.ProcessId} did={SafeValue(info.Did)} bonDriver={SafeValue(info.BonDriverFileName)} tuner={SafeValue(info.TunerName)} slot={info.SlotIndex} source={info.Source} waitedMs={waitedMs} rule=epg_preempt_release_semantics");
                    return new ManagedEpgStopResult(info.ProcessId, "exited_after_kill", waitedMs);
                }
            }

            _log.Add("EPG_PREEMPT_PROCESS_EXIT_TIMEOUT", $"R{reservationId}",
                $"group={group} pid={info.ProcessId} did={SafeValue(info.Did)} bonDriver={SafeValue(info.BonDriverFileName)} tuner={SafeValue(info.TunerName)} slot={info.SlotIndex} source={info.Source} waitedMs={waitedMs} rule=epg_preempt_release_semantics");
            return new ManagedEpgStopResult(info.ProcessId, "timeout", waitedMs);
        }
        catch (ArgumentException)
        {
            _log.Add("EPG_PREEMPT_PROCESS_EXIT_OK", $"R{reservationId}",
                $"group={group} pid={info.ProcessId} did={SafeValue(info.Did)} bonDriver={SafeValue(info.BonDriverFileName)} tuner={SafeValue(info.TunerName)} slot={info.SlotIndex} source={info.Source} alreadyGone=True rule=epg_preempt_release_semantics");
            return new ManagedEpgStopResult(info.ProcessId, "already_gone", 0);
        }
        catch (Exception ex)
        {
            _log.Add("EPG_PREEMPT_PROCESS_STOP_ERROR", $"R{reservationId}",
                $"group={group} pid={info.ProcessId} did={SafeValue(info.Did)} bonDriver={SafeValue(info.BonDriverFileName)} tuner={SafeValue(info.TunerName)} slot={info.SlotIndex} source={info.Source} error={TrimForLog(ex.Message, 240)} rule=epg_preempt_release_semantics");
            return new ManagedEpgStopResult(info.ProcessId, "error", 0);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool IsManagedEpgCaptureProcess(TvTestProcessInfo p)
    {
        var cmd = p.CommandLine ?? string.Empty;
        return p.IsTvAirManaged
            && p.IsRecording
            && (cmd.Contains("/recduration", StringComparison.OrdinalIgnoreCase)
                || cmd.Contains("/noview", StringComparison.OrdinalIgnoreCase)
                || cmd.Contains("/silent", StringComparison.OrdinalIgnoreCase)
                || cmd.Contains("\\data\\ts-rec\\", StringComparison.OrdinalIgnoreCase)
                || cmd.Contains("/data/ts-rec/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsProcessForGroup(TvTestProcessInfo p, string group)
    {
        var bon = p.BonDriverFileName ?? string.Empty;
        if (string.Equals(group, "GR", StringComparison.OrdinalIgnoreCase))
            return bon.Contains("-T", StringComparison.OrdinalIgnoreCase) || bon.Contains("_T", StringComparison.OrdinalIgnoreCase) || bon.Contains("PTx-T", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(group, "BSCS", StringComparison.OrdinalIgnoreCase))
            return bon.Contains("-S", StringComparison.OrdinalIgnoreCase) || bon.Contains("_S", StringComparison.OrdinalIgnoreCase) || bon.Contains("PTx-S", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static string FormatPidList(IEnumerable<int> pids)
    {
        var list = pids.Select(p => p.ToString()).ToList();
        return list.Count == 0 ? "-" : string.Join(",", list);
    }

    // ─── 視聴強制終了（カウントダウン通知付き） ───────────────────

    private async Task PreemptViewingAsync(string group, Reservation r)
    {
        _log.Add("Scheduler", $"R{r.Id}",
            $"視聴競合: [{r.Title}] {ViewingPreemptCountdownSec} 秒後に視聴を終了します。");

        await Task.Delay(TimeSpan.FromSeconds(ViewingPreemptCountdownSec));

        _tunerPool.ForceReleaseViewing(group);
        _log.Add("Scheduler", $"R{r.Id}", "視聴を強制終了しました。");
    }

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

    private void ReevaluateAndLog(string context, bool syncProgramRules = true, bool bypassStopPhaseGate = false)
    {
        try
        {
            _allocationRoute.Run(new ReservationAllocationRouteRequest(
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
                ConflictLogTitle: $"Conflict({context})"));
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", $"Conflict({context})", $"競合再評価エラー: {ex.Message}");
        }
    }

    private void UpdateWakeTasksForMaintenance()
    {
        try
        {
            if (_taskSvc.HasPendingDeferredWakeTask())
            {
                _log.Add("Scheduler", "Wake(Maintenance)",
                    "定期Wakeタスク更新を省略: reason=deferred-wake-pending rule=v0.5.66_viewing_safe_wake");
                return;
            }

            _taskSvc.UpdateWakeTask();
        }
        catch (Exception ex)
        {
            _log.Add("Scheduler", "Wake(Maintenance)", $"定期Wakeタスク更新エラー: {ex.Message}");
        }
    }

    private void UpdateWakeTasksForRecordingLifecycle(string title, string context, bool swallowError = false)
    {
        try
        {
            _taskSvc.UpdateWakeTask();
        }
        catch (Exception ex)
        {
            if (!swallowError)
            {
                _log.Add("Scheduler", title, $"Wakeタスク更新エラー({context}): {ex.Message}");
            }
        }
    }

    private void PublishRecordingTimelineEpgGate(IReadOnlyList<UpcomingRecording> upcoming, DateTime now)
    {
        // v0.11.147: 録画安定中EPGの復帰は維持しつつ、次の録画タイムラインへ食い込む
        // 通常EPG workerの新規投入を止める。チェーンだけでなく、異局重複・通常予約・即時予約も同じ境界として扱う。
        if (upcoming.Count == 0) return;

        // v0.11.148: 147の未定義CurrentWaitSec参照を撤去。
        // ここは特定の待機ループ状態ではなく、通常EPG workerの最大実行見込み＋終了余裕を
        // 録画タイムライン境界登録の固定安全幅として扱う。
        var safetySeconds = RecordingTimelineEpgGateSafetySeconds;
        var horizon = now.AddSeconds(safetySeconds);
        var targets = upcoming
            .Where(x => x.StartTime.AddSeconds(-_ini.PreStartMarginSeconds) <= horizon)
            .GroupBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => x.StartTime).ThenBy(x => x.ReservationId).First())
            .OrderBy(x => x.StartTime)
            .ThenBy(x => x.ReservationId)
            .ToList();

        if (targets.Count == 0) return;

        foreach (var rec in targets)
        {
            var dueAt = rec.StartTime.AddSeconds(-_ini.PreStartMarginSeconds);
            var blockNewWorkerAfter = dueAt.AddSeconds(-safetySeconds);
            var protectUntil = rec.StartTime.AddSeconds(RecordingDueEpgSuppressBeforeAfterSec);
            RecordingLifecycleGate.RegisterRecordingTimelineBoundary(
                rec.Group,
                dueAt,
                protectUntil,
                "recording_timeline_due",
                $"R{rec.ReservationId}",
                $"service={SafeValue(rec.ServiceName)} title={ReservationDisplayTitle(rec.Title)}",
                blockNewWorkerAfter);
        }

        var signature = string.Join("|", targets.Select(x => $"{x.Group}:R{x.ReservationId}:{x.StartTime:HHmmss}"));
        if (!string.Equals(signature, _lastRecordingTimelineGateSignature, StringComparison.Ordinal))
        {
            _log.Add("EPG_RECORDING_TIMELINE_GATE", "EPG",
                $"result=REGISTERED count={targets.Count} safetySec={safetySeconds} horizon={horizon:MM/dd HH:mm:ss} " +
                $"targets=[{string.Join(",", targets.Select(x => $"{x.Group}/R{x.ReservationId}/due={x.StartTime.AddSeconds(-_ini.PreStartMarginSeconds):HH:mm:ss}/start={x.StartTime:HH:mm:ss}"))}] " +
                "scope=all_recording_timeline_events action=block_new_normal_epg_worker_if_worker_end_overlaps_next_recording rule=v0.11.150_recording_timeline_epg_gate_effective_worker_block");
            _lastRecordingTimelineGateSignature = signature;
        }
    }

    private void RequestWakeTaskRefreshSoon(string title, string context)
    {
        // 停止処理の直後にタスクスケジューラI/Oを重ねない。
        // 次回の定期Wake更新でまとめて処理するため、カウンタだけ進める。
        _wakeUpdateCounter = Math.Max(_wakeUpdateCounter, WakeUpdateIntervalTicks - 1);
        _log.Add("Scheduler", title, $"Wakeタスク更新を次回Tickへ遅延: context={context}");
    }

    private IReadOnlyList<UpcomingRecording> GetUpcomingRecordings(DateTime now)
    {
        var horizon = now.AddMinutes(_ini.WakeMinutesBefore);
        var scheduled = _store.GetByStatus(ReservationStatus.Scheduled)
            .Where(r => r.StartTime <= horizon)
            .ToList();

        var skippedDisabled = scheduled.Count(r => !r.IsEnabled);
        var skippedSystemEpg = scheduled.Count(r => r.IsEnabled && r.Source == ReservationSource.Epg);
        var skippedInvalidGroup = 0;
        var result = new List<UpcomingRecording>();

        foreach (var r in scheduled)
        {
            if (!r.IsEnabled) continue;
            if (r.Source == ReservationSource.Epg) continue;

            var g = ResolveGroup(r);
            if (g is null)
            {
                skippedInvalidGroup++;
                continue;
            }

            result.Add(new UpcomingRecording(r.Id, g, r.StartTime, r.ServiceName, r.Title));
        }

        var eligibleIds = string.Join(",", result.Select(x => x.ReservationId).OrderBy(x => x));
        var signature = $"eligible={result.Count}:{eligibleIds}|disabled={skippedDisabled}|systemEpg={skippedSystemEpg}|invalidGroup={skippedInvalidGroup}";
        if (!string.Equals(signature, _lastEpgPreemptFilterSignature, StringComparison.Ordinal))
        {
            _log.Add("EPG_PREEMPT_FILTER", "Upcoming",
                $"result=STATE_CHANGED eligible={result.Count} eligibleIds=[{eligibleIds}] skippedDisabled={skippedDisabled} skippedSystemEpg={skippedSystemEpg} skippedInvalidGroup={skippedInvalidGroup} horizon={horizon:MM/dd HH:mm:ss} previous={(string.IsNullOrWhiteSpace(_lastEpgPreemptFilterSignature) ? "-" : _lastEpgPreemptFilterSignature)} rule=epg_preempt_release_semantics");
            _lastEpgPreemptFilterSignature = signature;
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

    private int? TryResolveChainRestartCooldown(Reservation r, TunerLease lease, int configuredCooldownMs, out string reason, out int? predecessorId)
    {
        reason = "not_chain_restart";
        predecessorId = null;

        if (!r.IsUserChain)
            return null;

        var predId = TryResolveChainPredecessorId(r, out var predSource);
        if (!predId.HasValue)
        {
            reason = "missing_predecessor";
            return null;
        }
        predecessorId = predId.Value;

        var pred = _store.GetById(predId.Value);
        if (pred is null)
        {
            reason = $"predecessor_not_found source={predSource}";
            return null;
        }

        var sameService = pred.NetworkId == r.NetworkId
            && pred.TransportStreamId == r.TransportStreamId
            && pred.ServiceId == r.ServiceId;
        if (!sameService)
        {
            reason = $"service_mismatch source={predSource} predNid={pred.NetworkId} predTsid={pred.TransportStreamId} predSid={pred.ServiceId} nextNid={r.NetworkId} nextTsid={r.TransportStreamId} nextSid={r.ServiceId}";
            return null;
        }

        if (pred.Status != ReservationStatus.Completed)
        {
            reason = $"predecessor_not_completed source={predSource} predStatus={pred.Status}";
            return null;
        }

        var predActualTuner = !string.IsNullOrWhiteSpace(pred.ActualTunerName) ? pred.ActualTunerName : pred.TunerName;
        var nextTuner = !string.IsNullOrWhiteSpace(r.ActualTunerName) ? r.ActualTunerName : r.TunerName;
        var sameTuner = string.Equals(predActualTuner, lease.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(nextTuner, lease.Name, StringComparison.OrdinalIgnoreCase);
        if (!sameTuner)
        {
            reason = $"tuner_mismatch source={predSource} predTuner={SafeValue(predActualTuner)} nextTuner={SafeValue(nextTuner)} lease={SafeValue(lease.Name)}";
            return null;
        }

        reason = $"same_sid_same_tuner_stop_restart source={predSource} front_tail_cut_contract=True";
        return Math.Min(configuredCooldownMs, ChainRestartSameTunerSettleMs);
    }

    private int GetEffectiveRecordingDelaySeconds()
    {
        // v0.5.59 build fix:
        // 0.5.58 の安全クリーニングで旧TVTest録画ルート用ファイルを外したが、
        // チェーン/停止監査ログは現在も共通の録画開始遅延値を参照する。
        // DirectRecorder 本線の制御値として IniSettingsService.RecDelaySeconds を使い、
        // 不正値だけログ用に安全範囲へ丸める。
        return Math.Clamp(_ini.RecDelaySeconds, 0, 120);
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
                $"preStart={_ini.PreStartMarginSeconds}s postEnd={_ini.PostEndMarginSeconds}s recDelay={GetEffectiveRecordingDelaySeconds()}s activeSessions={FormatActiveSessionsForLog()}");
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
                    : Math.Max(0, (successorDue!.Value - DateTime.Now).TotalMilliseconds) + (_ini.RecDelaySeconds * 1000.0);
                _log.Add("REC_STOP_CHAIN_CONTEXT", $"R{rid}",
                    $"stage={stage} finalStatus={finalStatus} successor=R{sid} succStatus={(successor?.Status.ToString() ?? "-")} " +
                    $"succStart={(successor is null ? "-" : successor.StartTime.ToString("MM/dd HH:mm:ss"))} succDue={(successorDue.HasValue ? successorDue.Value.ToString("MM/dd HH:mm:ss") : "-")} " +
                    $"succTuner={SafeValue(successor is null ? null : EffectiveTunerName(successor))} pid={session.ProcessId} tuner={SafeValue(session.Lease.Name)} " +
                    $"preStart={_ini.PreStartMarginSeconds}s recDelay={GetEffectiveRecordingDelaySeconds()}s expectedGapFromNowMs={expectedNoProcessGapMs:F0} activeSessions={FormatActiveSessionsForLog()}");
            }
        }
        catch (Exception ex)
        {
            _log.Add("REC_STOP_CHAIN_CONTEXT", $"R{session.ReservationId}", $"stage={stage} error={ex.Message}");
        }
    }

    /// <summary>予約の ChannelArgument / NetworkId / チューナー名からグループ（GR/BSCS）を解決する。</summary>
    private string? ResolveGroup(Reservation r)
    {
        if (!string.IsNullOrWhiteSpace(r.ChannelArgument))
        {
            var isBscs = r.ChannelArgument.Contains("/chspace", StringComparison.OrdinalIgnoreCase);
            return isBscs ? "BSCS" : "GR";
        }

        var hint = $"{r.TunerName} {r.Title} {r.ServiceName}";
        if (hint.Contains("BS/CS", StringComparison.OrdinalIgnoreCase)
            || hint.Contains("ＢＳ", StringComparison.Ordinal)
            || hint.Contains("ＣＳ", StringComparison.Ordinal))
            return "BSCS";
        if (hint.Contains("地上波", StringComparison.OrdinalIgnoreCase))
            return "GR";

        return r.NetworkId == 4 ? "BSCS" : "GR";
    }

    private sealed record TransportBatchLaunchWait(int WaitMs, string Mode, bool SameTransport, bool SameStart, string Reason);

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
                $"result=REORDERED count={ordered.Count} before={before} after={after} rule=v1.0.0_due_recording_batch_launch_contract");
        }

        return ordered;
    }

    private TransportBatchLaunchWait ResolveTransportBatchLaunchWait(Reservation? previous, Reservation current, int currentBatchCount)
    {
        if (previous is null)
            return new TransportBatchLaunchWait(0, "FIRST", false, false, "first_launch_this_tick");

        var sameTransport = IsSameTransportBatch(previous, current);
        var sameStart = IsSameStartBucket(previous, current);

        // 録画本線は TvAIrEpgRec へ移行済み。
        // 旧TVTest録画ルート由来の TunerSlotCooldownMs=15000 を、同時刻の別予約起動間隔として流用しない。
        // TunerSlotCooldownMs は StartRecordingAsync 内の同一チューナー再利用ゲートでのみ効かせる。
        if (sameStart)
            return new TransportBatchLaunchWait(0, "SAME_START_BATCH", sameTransport, true, "launch_all_due_reservations_without_legacy_tvtest_serial_wait");

        return new TransportBatchLaunchWait(0, "DUE_BATCH", sameTransport, false, "no_inter_launch_wait_tuner_cooldown_is_per_slot_only");
    }

    private bool IsSameTransportBatch(Reservation left, Reservation right)
    {
        return string.Equals(ResolveGroup(left), ResolveGroup(right), StringComparison.OrdinalIgnoreCase)
            && left.NetworkId == right.NetworkId
            && left.TransportStreamId == right.TransportStreamId
            && left.NetworkId != 0
            && left.TransportStreamId != 0;
    }

    private static bool IsSameStartBucket(Reservation left, Reservation right)
    {
        return left.StartTime == right.StartTime;
    }

    private string BuildTransportBatchKey(Reservation r)
    {
        return $"{ResolveGroup(r) ?? "-"}:{r.NetworkId}:{r.TransportStreamId}";
    }

    private bool IsActiveRecordingSessionSourceOfTruth(int reservationId, out RecordingSession? activeSession)
    {
        lock (_sessionGate)
            _activeSessions.TryGetValue(reservationId, out activeSession);

        if (activeSession is null)
            return false;

        if (DateTime.Now >= activeSession.PlannedEndTime)
            return false;

        return IsProcessAlive(activeSession.ProcessId);
    }

    private void Fail(Reservation r, string reason)
    {
        if (IsActiveRecordingSessionSourceOfTruth(r.Id, out var activeSession))
        {
            var current = _store.GetById(r.Id);
            if (current is not null && current.Status != ReservationStatus.Recording)
                _store.UpdateStatus(r.Id, ReservationStatus.Recording);

            _log.Add("REC_FAIL_SUPPRESSED_ACTIVE_SESSION", $"R{r.Id}",
                $"result=KEEP_RECORDING reason=active_tvairepgrec_session_is_source_of_truth pid={activeSession!.ProcessId} " +
                $"tuner={SafeValue(activeSession.Lease.Name)} plannedEnd={activeSession.PlannedEndTime:MM/dd HH:mm:ss} originalReason={TrimForLog(reason, 180)} " +
                $"rule=v0.9.83_failed_direct_route_guard " + FormatReservationForAudit(r, "fail_suppressed_active_session"));
            return;
        }

        var latest = _store.GetById(r.Id);
        if (IsInUnfinishedRecordingWindow(latest))
        {
            _store.UpdateStatus(r.Id, ReservationStatus.Failed);
            _log.Add("REC_FAIL_SUPPRESSED_RECORDING_WINDOW", $"R{r.Id}",
                $"result=KEEP_EXISTING_STATE reason=recording_started_without_finished_at originalReason={TrimForLog(reason, 180)} " +
                $"status={latest!.Status} start={latest.StartTime:MM/dd HH:mm:ss} end={latest.EndTime:MM/dd HH:mm:ss} " +
                $"recordingStartedAt={latest.RecordingStartedAt:MM/dd HH:mm:ss} tuner={SafeValue(EffectiveReservationTuner(latest))} " +
                $"rule=v0.9.83_failed_direct_route_guard " + FormatReservationForAudit(latest, "fail_suppressed_recording_window"));
            return;
        }

        _log.Add("REC_FAIL", $"R{r.Id}", $"reason={reason} " + FormatReservationForAudit(r, "fail"));
        _store.UpdateStatus(r.Id, ReservationStatus.Failed, force: true);
        _forceAllocationReevaluate = true;
        _log.Add("Scheduler", $"R{r.Id}", $"録画失敗: [{r.Title}] {reason}");
        UpdateWakeTasksForRecordingLifecycle($"R{r.Id}", "録画失敗後", swallowError: true);
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
                $"window={from:MM/dd HH:mm:ss}〜{to:MM/dd HH:mm:ss} count={reservations.Count} visible={activeReservations.Count} suppressedPastTerminal={terminalPastReservations.Count} now={now:MM/dd HH:mm:ss} note=non_epg_reservations_only rule=v0.5.86_dueforce_terminal_audit_quiet");

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
                        $"suppressed={terminalPastReservations.Count} completed={completed} cancelled={cancelled} failed={failed} sample=diagnostic_only rule=reservation_audit_terminal_summary_release_candidate_trim");
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
        return $"service={TrimForLog(r.ServiceName, 40)} title={ReservationDisplayTitle(r.Title, 60)} state={state} id=R{r.Id} status={r.Status} source={r.Source} enabled={r.IsEnabled} conflicted={r.IsConflicted} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} dueStart={dueStart:MM/dd HH:mm:ss} svcId={r.ServiceId} group={ResolveGroup(r) ?? "-"} tuner={SafeValue(EffectiveTunerName(r))} ch={SafeValue(r.ChannelArgument)} userChain={r.IsUserChain} chainPrev={(r.UserChainPreviousId.HasValue ? $"R{r.UserChainPreviousId.Value}" : "-")} chainRoot={(r.UserChainRootId.HasValue ? $"R{r.UserChainRootId.Value}" : "-")} created={r.CreatedAt:MM/dd HH:mm:ss} updated={r.UpdatedAt:MM/dd HH:mm:ss} rule=v0.5.78_service_title_first_audit";
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

    private static string FormatReservationForLifecycleLog(Reservation r)
        => $"service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title, 80)} svcId={r.ServiceId} " +
           $"start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss}";

    private string ProbeInterruptedRecordingFile(Reservation r)
    {
        try
        {
            if (!TvTestRecordingDirectoryResolver.TryResolve(_ini.TvTestExecutablePath, out var directory, out var evidence))
                return $"result=folder_unresolved evidence={TrimForLog(evidence, 420)} rule=v0.5.67_interrupted_file_audit";

            var expectedName = BuildDirectRecorderFileName(r, r.Source is ReservationSource.Immediate or ReservationSource.Program
                ? (r.RecordingStartedAt ?? r.StartTime)
                : r.StartTime).FileName;
            var exactPath = Path.Combine(directory, expectedName);
            var candidates = new List<string>();
            if (File.Exists(exactPath))
            {
                candidates.Add(exactPath);
            }
            else if (Directory.Exists(directory))
            {
                var baseName = Path.GetFileNameWithoutExtension(expectedName);
                candidates.AddRange(Directory.EnumerateFiles(directory, baseName + "*.ts")
                    .OrderByDescending(File.GetLastWriteTime)
                    .Take(3));
            }

            if (candidates.Count == 0)
                return $"result=no_file folder={SafeValue(directory)} expected={SafeValue(expectedName)} start={r.StartTime:yyyy-MM-dd HH:mm:ss} end={r.EndTime:yyyy-MM-dd HH:mm:ss} rule=v0.5.67_interrupted_file_audit";

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

            return $"result=partial_file_detected folder={SafeValue(directory)} expected={SafeValue(expectedName)} {string.Join(" | ", details)} reservation={r.StartTime:yyyy-MM-dd HH:mm:ss}〜{r.EndTime:yyyy-MM-dd HH:mm:ss} rule=v0.5.67_interrupted_file_audit";
        }
        catch (Exception ex)
        {
            return $"result=audit_error type={ex.GetType().Name} message={TrimForLog(ex.Message, 240)} rule=v0.5.67_interrupted_file_audit";
        }
    }

    private string ResolveReservationRecordFolder(Reservation r)
    {
        // v0.4.8: BS/CSはBridgeでTVTest内部currentSid確定後にTVTest標準録画を開始する。
        // 停止時はBridge RecordStop待ちを行わず、STOP_PHASE/TUNER_DEVICE_LOCKを詰まらせないTvAIr所有PID単体終了へ戻す。
        // 通常予約録画ではTvAIrが録画ファイル名を生成せず、TVTest.ini の RecordFolder/RecordFileName に準拠する。
        // TVTest.ini の RecordFolder だけを起動時確定済み値として確認し、RecordFileName はTVTestへ委譲する。
        if (!TvTestRecordingDirectoryResolver.TryResolve(_ini.TvTestExecutablePath, out var directory, out var evidence))
        {
            _log.Add("RECORD_FOLDER_RESOLVE", $"R{r.Id}", $"result=FAIL evidence={TrimForLog(evidence, 420)}");
            _log.Add("RECORD_FILE_PATH_BUILD", $"R{r.Id}", "result=FAIL reason=record_folder_unresolved route=TvAIrEpgRecTemplateBuild");
            return string.Empty;
        }

        _log.Add("RECORD_FOLDER_RESOLVE", $"R{r.Id}", $"result=OK folder={directory} evidence={TrimForLog(evidence, 420)}");
        _log.Add("RECORD_FILE_PATH_BUILD", $"R{r.Id}", "result=OK reason=tvairepgrec_uses_tvtest_recordfilename_template_with_normalized_event_name source=TVTest.RecordFileName route=TvAIrEpgRecTemplateBuild rule=v0.9.83_failed_direct_route_guard");
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
        long RawSyncErrors,
        long RawScrambledPackets,
        long OutputPackets,
        long OutputContinuityDrops,
        long OutputContinuityErrors,
        long OutputSyncErrors,
        long OutputScrambledPackets,
        long BytesWritten);

    private sealed record RuntimeStatsDelta(
        RuntimeStatsSample Sample,
        long RawDropDelta,
        long RawCcDelta,
        long OutputDropDelta,
        long OutputCcDelta,
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
                $"rule=v0.5.91_drop_timeline_summary_with_startup_same_did_relock");
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
            $"rule=v0.6.41_drop_quality_correlation_summary");

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
                $"inputRawPackets={x.RawPackets} inputRawDrops={x.RawContinuityDrops} inputRawDropDelta={d.RawDropDelta} inputRawCcErrors={x.RawContinuityErrors} inputRawCcDelta={d.RawCcDelta} inputRawSyncErrors={x.RawSyncErrors} inputRawScrambled={x.RawScrambledPackets} " +
                $"outputPackets={x.OutputPackets} outputDrops={x.OutputContinuityDrops} outputDropDelta={d.OutputDropDelta} outputCcErrors={x.OutputContinuityErrors} outputCcDelta={d.OutputCcDelta} outputSyncErrors={x.OutputSyncErrors} outputSyncDelta={d.OutputSyncDelta} outputScrambled={x.OutputScrambledPackets} " +
                $"bytesWritten={x.BytesWritten} bytesDelta={d.BytesDelta} rawLayerMeaning=pre_write_input_observation outputLayerMeaning=recorded_file_integrity group={SafeValue(context.Group)} groupRecordingCount={context.Count} groupTuners={SafeValue(context.Names)} groupDids={SafeValue(context.Dids)} " +
                $"rule=v0.6.41_runtime_stats_quality_correlation_summary");
        }
    }

    private ActiveGroupContext BuildActiveRecordingGroupContext(string? group)
    {
        var effectiveGroup = string.IsNullOrWhiteSpace(group) ? "-" : group.Trim();
        List<RecordingSession> active;
        lock (_sessionGate) active = _activeSessions.Values.ToList();
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
            $"rule=v0.6.41_drop_quality_correlation_summary");
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
        try
        {
            foreach (var line in File.ReadLines(path).Take(maxLines))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var at = GetJsonString(root, "at");
                DateTime? atTime = DateTime.TryParse(at, out var parsedAt) ? parsedAt : null;
                result.Add(new RuntimeStatsSample(
                    at,
                    atTime,
                    GetJsonInt64(root, "rawPackets"),
                    GetJsonInt64(root, "rawContinuityDrops"),
                    GetJsonInt64(root, "rawContinuityErrors"),
                    GetJsonInt64(root, "rawSyncErrors"),
                    GetJsonInt64(root, "rawScrambledPackets"),
                    GetJsonInt64(root, "outputPackets"),
                    GetJsonInt64(root, "outputContinuityDrops"),
                    GetJsonInt64(root, "outputContinuityErrors"),
                    GetJsonInt64(root, "outputSyncErrors"),
                    GetJsonInt64(root, "outputScrambledPackets"),
                    GetJsonInt64(root, "bytesWritten")));
            }
        }
        catch
        {
            return new List<RuntimeStatsSample>();
        }
        return result;
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
                Math.Max(0, s.OutputContinuityDrops - (prev?.OutputContinuityDrops ?? 0)),
                Math.Max(0, s.OutputContinuityErrors - (prev?.OutputContinuityErrors ?? 0)),
                Math.Max(0, s.OutputSyncErrors - (prev?.OutputSyncErrors ?? 0)),
                Math.Max(0, s.BytesWritten - (prev?.BytesWritten ?? 0))));
            prev = s;
        }
        return list;
    }

    private sealed record DirectRecorderQualityClassification(string ClassName, string Severity, string UserMeaning, string Action);

    private sealed record DropCorrelationClassification(string ClassName, string Priority, string Axis, string UserMeaning, string Action);

    private sealed record DropTimelineClassification(string ClassName, string Phase, string Severity, string UserMeaning, string Action);

    private static DirectRecorderQualityClassification ClassifyDirectRecorderQuality(DirectRecorderOutcome outcome, RecordingTsVerifier.VerificationResult? tsVerification)
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
            if (tsVerification?.ClearEnoughForCompleted == true)
            {
                return new DirectRecorderQualityClassification(
                    "NO_RESPONSE_BUT_TS_VERIFY_CLEAR",
                    "WARN",
                    "worker_response_missing_but_recorded_file_verified_clear",
                    "inspect_tvairepgrec_stop_progress_not_recording_file");
            }

            return new DirectRecorderQualityClassification(
                "UNKNOWN_NO_BRIDGE_RESPONSE",
                "UNKNOWN",
                "bridge_response_missing_quality_not_confirmed",
                "check_tvairepgrec_response_and_file");
        }

        if (!outcome.Success)
        {
            if (tsVerification?.ClearEnoughForCompleted == true)
            {
                return new DirectRecorderQualityClassification(
                    "WARN_BRIDGE_NG_BUT_TS_VERIFY_CLEAR",
                    "WARN",
                    "tvairepgrec_reported_failure_but_recorded_file_verified_clear",
                    "keep_completed_with_worker_warning_and_inspect_runtime_summary");
            }

            return new DirectRecorderQualityClassification(
                "FAIL_BRIDGE_REPORTED",
                "FAIL",
                "tvairepgrec_reported_failure",
                "investigate_recorder_summary");
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

    private static bool CanKeepCompletedByTsVerification(DirectRecorderOutcome outcome, RecordingTsVerifier.VerificationResult? verification)
    {
        if (verification?.ClearEnoughForCompleted != true) return false;
        var bytesWritten = ParseLongOrZero(outcome.BytesWritten);
        var packetsWritten = ParseLongOrZero(outcome.PacketsWritten);
        var outputScrambled = ParseLongOrZero(outcome.OutputScrambledPackets);
        var outputSyncErrors = ParseLongOrZero(outcome.OutputSyncErrors);
        var outputCcErrors = ParseLongOrZero(outcome.OutputContinuityErrors);

        if (bytesWritten < 32L * 1024L * 1024L && packetsWritten < 100_000) return false;
        if (outputSyncErrors > 0) return false;

        // TvAIrEpgRecのruntime判定は復号直後の境界・終了時の少量スクランブルを厳しく拾う。
        // TS実体検証で対象サービス・PMT・映像PID・対象サービス非スクランブルが確認できる場合だけ、
        // 少量の出力スクランブル/CCを警告へ降格し、予約ステータスはCompletedを維持する。
        if (outputScrambled <= 1000 && outputCcErrors <= 16) return true;
        if (packetsWritten > 0 && outputScrambled * 100_000L <= packetsWritten * 100L && outputCcErrors <= 16) return true;
        return false;
    }

    private static long ParseLongOrZero(string? value)
    {
        return long.TryParse(value, out var parsed) ? parsed : 0;
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
        string Summary);

    private static DirectRecorderOutcome ReadDirectRecorderOutcome(string? responsePath)
    {
        var summary = ReadDirectRecorderResponseSummary(responsePath);
        if (string.IsNullOrWhiteSpace(responsePath) || !File.Exists(responsePath))
            return new DirectRecorderOutcome(false, false, "-", "-", "NO_RESPONSE", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", summary);

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
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
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
            return new DirectRecorderOutcome(true, success, bytesWritten, packetsWritten, qualityVerdict, runtimeStatsPath, runtimeStatsEmitted, rawContinuityDrops, rawContinuityErrors, rawSyncErrors, rawScrambledPackets, outputContinuityDrops, outputContinuityErrors, outputSyncErrors, outputScrambledPackets, summary);
        }
        catch
        {
            return new DirectRecorderOutcome(true, false, "-", "-", "READ_ERROR", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", summary);
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


    private PreTuneChainContext BuildPreTuneChainContext(Reservation parent)
    {
        var all = _store.GetAll();
        var hasSuccessor = all.Any(x => x.IsUserChain && x.UserChainPreviousId == parent.Id);
        var hasPredecessor = parent.IsUserChain && parent.UserChainPreviousId.HasValue;

        if (!hasPredecessor)
        {
            return new PreTuneChainContext(
                ChainPosition: hasSuccessor ? "head" : "single",
                Action: hasSuccessor ? "pretune_same_recording_tuner_chain_head" : "pretune_same_recording_tuner",
                PreferredTunerName: parent.TunerName,
                SkipNewWorker: false,
                KeepWorkerUntilSafetyCeiling: true,
                Reason: hasSuccessor ? "chain_head_may_enter_from_another_tuner" : "normal_recording_start_stability");
        }

        var predecessor = _store.GetById(parent.UserChainPreviousId!.Value);
        var sameSid = predecessor is not null && predecessor.ServiceId == parent.ServiceId && predecessor.NetworkId == parent.NetworkId && predecessor.TransportStreamId == parent.TransportStreamId;
        var sameTuner = predecessor is not null && !string.IsNullOrWhiteSpace(predecessor.TunerName) && string.Equals(predecessor.TunerName, parent.TunerName, StringComparison.OrdinalIgnoreCase);
        var predecessorRecording = predecessor is not null && predecessor.Status == ReservationStatus.Recording;

        if (predecessorRecording && sameSid && sameTuner)
        {
            return new PreTuneChainContext(
                ChainPosition: "successor",
                Action: "use_existing_recording_session",
                PreferredTunerName: null,
                SkipNewWorker: true,
                KeepWorkerUntilSafetyCeiling: false,
                Reason: "predecessor_recording_same_sid_same_tuner");
        }

        return new PreTuneChainContext(
            ChainPosition: "successor",
            Action: "pretune_required_predecessor_not_reusable",
            PreferredTunerName: parent.TunerName,
            SkipNewWorker: false,
            KeepWorkerUntilSafetyCeiling: true,
            Reason: $"predecessorRecording={predecessorRecording};sameSid={sameSid};sameTuner={sameTuner}");
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
            : $"fileName={SafeValue(fileNameBuild.FileName)} outputPath={SafeValue(outputPath)} filenameGuard=final_event_name_normalizer rawTitleChanged={!string.Equals(fileNameBuild.RawTitle, fileNameBuild.NormalizedEventName, StringComparison.Ordinal)} unsupportedTokens={SafeValue(fileNameBuild.UnsupportedTokens)}";
        var leasePart = lease is null
            ? $"plannedTuner={SafeValue(plannedTuner)} actualTuner={SafeValue(actualTuner)} did=- bonDriver=- leaseKind=-"
            : $"plannedTuner={SafeValue(plannedTuner)} actualTuner={SafeValue(actualTuner ?? lease.Name)} did={SafeValue(lease.Did)} bonDriver={SafeValue(lease.BonDriverFileName)} leaseKind=Recording elapsedSinceReleaseMs={(lease.ElapsedSinceReleaseMs.HasValue ? lease.ElapsedSinceReleaseMs.Value.ToString("F0") : "-")}";
        var chainPart = $"userChain={r.IsUserChain} chainPrev={(r.UserChainPreviousId.HasValue ? $"R{r.UserChainPreviousId.Value}" : "-")} chainRoot={(r.UserChainRootId.HasValue ? $"R{r.UserChainRootId.Value}" : "-")} chainContinuation={IsChainContinuation(r)}";
        var timePart = $"start={r.StartTime:yyyy-MM-dd HH:mm:ss} end={r.EndTime:yyyy-MM-dd HH:mm:ss} scheduledStart={scheduledStart} plannedEnd={(plannedEnd.HasValue ? plannedEnd.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-")} created={created} updated={updated} ageMin={ageMin}";
        return $"result=OK stage={stage} reservation=R{r.Id} kind={reservationKind} source={r.Source} route={route} status={r.Status} enabled={r.IsEnabled} conflicted={r.IsConflicted} service={SafeValue(r.ServiceName)} title={ReservationDisplayTitle(r.Title, 80)} group={SafeValue(group ?? ResolveGroup(r))} {leasePart} {channelIdentity} {fileNamePart} finalGuardApplied={finalGuardApplied} commonAllocationRoute=True wakeCoupled=True preRecordEpgCoupled=True {chainPart} {timePart} sourceRule={(r.SourceRuleId.HasValue ? r.SourceRuleId.Value.ToString() : "-")} sourceRuleName={SafeValue(r.SourceRuleName)} note={SafeValue(note)} rule=reservation_tuner_filename_pipeline_audit_v0.10.57";
    }

    private static string ResolveReservationKindForPipelineAudit(Reservation r)
    {
        if (r.IsUserChain) return "UserChain";
        return r.Source switch
        {
            ReservationSource.Immediate => "Immediate",
            ReservationSource.Program => "Program",
            // v0.11.101: KeywordSearch is a program-guide search result/manual action.
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

internal sealed record RecordingFileGrowthObservation(bool ShouldStop, string Reason, long Bytes, long PreviousBytes)
{
    public static RecordingFileGrowthObservation Continue(long bytes, long previousBytes)
        => new(false, string.Empty, bytes, previousBytes);
}

internal sealed record PreTuneChainContext(
    string ChainPosition,
    string Action,
    string? PreferredTunerName,
    bool SkipNewWorker,
    bool KeepWorkerUntilSafetyCeiling,
    string Reason);

internal sealed class RecordingSession
{
    public int           ReservationId  { get; }
    public int           ProcessId      { get; }
    public DateTime      PlannedEndTime { get; private set; }
    public TunerLease    Lease          { get; }
    public string        RecordingFilePath { get; }
    public string        ResponsePath { get; }
    public string        StopSignalPath { get; }
    public string        ProgressPath { get; }
    public string        RuntimeStatsPath { get; }
    public string        JobPath { get; }
    public string        SegmentPlanPath { get; }
    public TvTestActivityHandle? ActivityHandle { get; }
    public DateTime RecordingFileGrowthWatchStartedAt { get; }
    public DateTime LastRecordingFileGrowthAt { get; private set; }
    public long LastObservedRecordingFileBytes { get; private set; } = -1;
    public bool RecordingFileEverGrew { get; private set; }
    public bool RecordingFileStallStopRequested { get; private set; }
    public RecordingSession(int reservationId, int processId, DateTime plannedEndTime, TunerLease lease, string recordingFilePath, string responsePath = "", string stopSignalPath = "", string progressPath = "", string runtimeStatsPath = "", string jobPath = "", string segmentPlanPath = "", TvTestActivityHandle? activityHandle = null)
    {
        ReservationId  = reservationId;
        ProcessId      = processId;
        PlannedEndTime = plannedEndTime;
        Lease          = lease;
        RecordingFilePath = recordingFilePath;
        ResponsePath = responsePath;
        StopSignalPath = stopSignalPath;
        ProgressPath = progressPath;
        RuntimeStatsPath = runtimeStatsPath;
        JobPath = jobPath;
        SegmentPlanPath = segmentPlanPath;
        ActivityHandle = activityHandle;
        RecordingFileGrowthWatchStartedAt = DateTime.Now;
        LastRecordingFileGrowthAt = RecordingFileGrowthWatchStartedAt;
    }

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

// v0.2.34: 停止フェーズ中の再評価侵入を抑止するための共通判定。
private static bool ShouldDeferBecauseStopping(string source, string action)
{
    return StopPhaseGate.TryDeferAllocRoute(source, action, null);
}

}

// if (isChain && isNearStartWindow) conflicted = false; // override

// CONFLICT_OVERRIDE_APPLIED
