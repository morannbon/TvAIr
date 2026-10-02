using Microsoft.Extensions.Options;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Tuner;
using TvAIr.Schedule;
using TvAIr.Epg.Projection;
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
///      明示チェーンrootのDB-backed追従経路は通常EPG/PreRecとは分離して維持する
///
/// 並列数は TunerPool の空きスロット数で自動決定する。
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
#if TVAIR_DEVELOPER_DIAGNOSTICS
    private readonly EpgCoverageAttributionDiagnosticStore coverageAttribution;
    private readonly DbProgramEventSource diagnosticDbProgramEventSource;
#endif
    private readonly BroadcastTimeReference broadcastTimeReference;

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
        KeywordMatcher keywordMatcher,
#if TVAIR_DEVELOPER_DIAGNOSTICS
        EpgCoverageAttributionDiagnosticStore coverageAttribution,
        DbProgramEventSource diagnosticDbProgramEventSource,
#endif
        BroadcastTimeReference broadcastTimeReference)
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
#if TVAIR_DEVELOPER_DIAGNOSTICS
        this.coverageAttribution = coverageAttribution;
        this.diagnosticDbProgramEventSource = diagnosticDbProgramEventSource;
#endif
        this.broadcastTimeReference = broadcastTimeReference;
    }


    private sealed class RecordingEpgSupplementState
    {
        public long Offset;
        public PsiSectionAssembler Assembler = new();
        public EitSectionReader Reader = new(EitTransportStreamScope.ActualOnly);
        public readonly Dictionary<string, string> CommittedSignatures = new(StringComparer.Ordinal);
        public readonly SemaphoreSlim Gate = new(1, 1);

        public void ResetParser()
        {
            Assembler = new PsiSectionAssembler();
            Reader = new EitSectionReader(EitTransportStreamScope.ActualOnly);
        }
    }

    private readonly ConcurrentDictionary<int, RecordingEpgSupplementState> recordingSupplementStates = new();
    private const int RecordingSupplementMaxBytesPerPass = 32 * 1024 * 1024;

    /// <summary>
    /// 録画TSを読み取り専用で追尾し、同一TS上の全サービスEITを通常EPGと同じ解析・canonical merge・EpgStoreへ補完する。
    /// 録画worker/録画ファイル書込み/Recording Follow/チューナーには一切介入しない。
    /// 補完源はupsert-onlyでありstale退場権限を持たない。
    /// </summary>
    public async Task<int> ObserveRecordingTsSupplementAsync(int reservationId, string groupName, string tsPath, CancellationToken ct = default)
    {
        if (reservationId <= 0 || string.IsNullOrWhiteSpace(tsPath) || !File.Exists(tsPath)) return 0;
        var state = recordingSupplementStates.GetOrAdd(reservationId, _ => new RecordingEpgSupplementState());
        if (!await state.Gate.WaitAsync(0, ct).ConfigureAwait(false)) return 0;
        var resetParserAfterPass = false;
        try
        {
            await using var stream = new FileStream(tsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            if (stream.Length < state.Offset)
            {
                state.Offset = 0;
                state.CommittedSignatures.Clear();
                state.ResetParser();
            }
            if (state.Offset >= stream.Length) return 0;
            stream.Position = state.Offset;

            var packet = new byte[TsPacketReader.PacketSize];
            var budget = Math.Min((long)RecordingSupplementMaxBytesPerPass, stream.Length - state.Offset);
            long consumed = 0;
            var sectionsBefore = state.Reader.EitSectionCount;
            var versionSwitchBefore = state.Reader.IgnoredVersionSwitchEitSectionCount;
            while (consumed + packet.Length <= budget)
            {
                ct.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(packet.AsMemory(0, packet.Length), ct).ConfigureAwait(false);
                if (read != packet.Length) break;
                consumed += read;
                if (packet[0] != 0x47) continue;
                if (!TsPacketReader.TryRead(packet, out var packetView) || packetView.Pid is not (0x12 or 0x26 or 0x27)) continue;
                foreach (var section in state.Assembler.Feed(packet)) state.Reader.TryRead(section);
            }
            state.Offset += consumed;
            resetParserAfterPass = state.Reader.IgnoredVersionSwitchEitSectionCount > versionSwitchBefore;
            if (state.Reader.EitSectionCount <= sectionsBefore) return 0;

            var parsed = state.Reader.BuildEvents();
            if (parsed.Count == 0) return 0;
            var eventObservations = state.Reader.BuildEventObservations();
            var analyze = new EpgAnalyzeResult(
                0, 0,
                state.Assembler.TransportErrorPacketCount, state.Assembler.ContinuityDiscontinuityCount,
                state.Assembler.DiscontinuityIndicatorResetCount, state.Assembler.DuplicatePayloadPacketCount, state.Assembler.ResyncWaitDropPacketCount,
                state.Assembler.InvalidSectionLengthResetCount, state.Assembler.InvalidPointerResetCount,
                0, state.Reader.EitSectionCount, state.Reader.ShortEventDescriptorCount,
                state.Reader.DecodeAttemptCount, state.Reader.ExtendedWithoutShortCount, state.Reader.DescriptorRecoveryCount,
                state.Reader.RawSectionShortResolverCandidates, state.Reader.RawSectionShortResolverMerged, state.Reader.RawSectionShortResolverUnresolved,
                state.Reader.CommonEventCount, state.Reader.CommonResolvedCount, state.Reader.CommonUnresolvedCount,
                state.Reader.RejectedEventHeaderCount, state.Reader.RejectedBasicScheduleEventHeaderCount,
                state.Reader.IgnoredOtherTransportStreamEitSectionCount, state.Reader.InvalidEitSectionCount,
                state.Reader.IgnoredNonCurrentEitSectionCount, state.Reader.IgnoredDuplicateEitSectionCount,
                state.Reader.IgnoredVersionSwitchEitSectionCount, state.Reader.IgnoredBasicScheduleVersionSwitchEitSectionCount,
                state.Reader.InvalidSyntaxOrLengthEitSectionCount, state.Reader.InvalidCrcEitSectionCount,
                state.Reader.InvalidHeaderConsistencyEitSectionCount, state.Reader.ToleratedSameVersionScheduleMetadataDriftCount,
                0, 0, 0,
                Array.Empty<EpgCaptureSubtableVersion>(), Array.Empty<EpgPersistentCacheSubtable>(),
                Array.Empty<EpgSectionEventInventory>(), Array.Empty<EpgSectionEventInventory>(),
                Array.Empty<EpgEventObservation>(), Array.Empty<EpgEventObservation>(),
                Array.Empty<EpgPreviousVersionCacheSubtable>(), Array.Empty<EpgEventObservation>(),
                state.Reader.RejectedEventHeaders.ToArray(), state.Reader.TitleDecodes.ToArray(),
                state.Reader.BuildSectionStatuses(), eventObservations, state.Reader.BuildAccumulatorAudits(), parsed);

            var channels = channelLoader.Load().Targets;
            var serviceNameByIdentity = channels
                .GroupBy(c => (c.OriginalNetworkId, c.TransportStreamId, c.ServiceId))
                .ToDictionary(g => g.Key, g => g.First().Name);
            var serviceNameBySid = parsed
                .GroupBy(e => e.ServiceId)
                .ToDictionary(g => g.Key, g => serviceNameByIdentity.TryGetValue((g.First().NetworkId, g.First().TransportStreamId, g.Key), out var name) ? name : string.Empty);
            var targetSids = parsed.Select(e => e.ServiceId).Distinct().OrderBy(x => x).ToArray();
            var tsid = parsed.Select(e => e.TransportStreamId).FirstOrDefault();
            var targets = channels.Where(c => c.TransportStreamId == tsid).ToArray();
            var group = new TsGroup($"recording:{reservationId}", groupName ?? string.Empty, tsid, string.Empty, targets);
            var rawEvents = BuildRawEventsFromAccumulatorProjection(parsed, targetSids.ToHashSet(), serviceNameBySid);
            rawEvents = FilterCurrentOrFutureImportEvents(rawEvents, DateTime.Now);
            _ = ApplyStrictTitleBodyCanonicalMerge(group, "recording_ts_supplement", targetSids, analyze, rawEvents);

            static string Signature(EpgEvent e) => $"{e.NetworkId}|{e.TransportStreamId}|{e.ServiceId}|{e.EventId}|{e.Start.Ticks}|{e.DurationSeconds}|{e.TableId}|{e.SectionNumber}|{e.VersionNumber}|{e.RawShortEventDescriptorHex ?? string.Empty}|{e.RawExtendedEventDescriptorHex ?? string.Empty}|{e.RawContentDescriptorHex ?? string.Empty}";
            static string Identity(EpgEvent e) => $"{e.NetworkId}/{e.TransportStreamId}/{e.ServiceId}/{e.EventId}";

            var changed = new List<EpgEvent>();
            foreach (var e in rawEvents)
            {
                var key = Identity(e);
                var sig = Signature(e);
                if (state.CommittedSignatures.TryGetValue(key, out var old) && string.Equals(old, sig, StringComparison.Ordinal)) continue;
                changed.Add(e);
                state.CommittedSignatures[key] = sig;
            }
            if (changed.Count == 0) return 0;

            await epgImportCommitGate.WaitAsync(ct).ConfigureAwait(false);
            int committed;
            int promoted;
            try
            {
                var result = store.CommitCapture(changed, retireEvents: null, preserveEvents: changed);
                committed = result.Upsert.Count;
                promoted = projectionPromotion.PromotePending($"RecordingTsSupplement:R{reservationId}", runAllocationRoute: false);
            }
            finally
            {
                epgImportCommitGate.Release();
            }

            var keywordAdded = 0;
            if (committed > 0)
            {
                try
                {
                    keywordAdded = keywordMatcher.RunMatching(changed);
                    if (keywordAdded > 0)
                        projectionPromotion.RunAllocationRoute($"RecordingTsSupplement:R{reservationId}:KeywordMatch", ReservationAllocationWakeRefreshMode.BoundedCoalesce);
                    else if (promoted > 0)
                        projectionPromotion.RunAllocationRoute($"RecordingTsSupplement:R{reservationId}:ProjectionPromote", ReservationAllocationWakeRefreshMode.BoundedCoalesce);
                }
                catch (Exception ex)
                {
                    Log("RECORDING_EPG_SUPPLEMENT", $"R{reservationId}", $"result=POST_COMMIT_ERROR error={SafeLogValue(ex.Message)} committed={committed} action=keep_epg_commit recordingImpact=none rule=recording_ts_epg_supplement_contract");
                }
            }

#if TVAIR_DEVELOPER_DIAGNOSTICS
            Log("RECORDING_EPG_SUPPLEMENT", $"R{reservationId}",
                $"result=COMMITTED group={SafeLogValue(groupName)} tsid={tsid} bytesRead={consumed} offset={state.Offset} services={targetSids.Length} parsed={rawEvents.Count} changed={changed.Count} committed={committed} promoted={promoted} keywordAdded={keywordAdded} staleRetire=none tunerMutation=none recordingMutation=none followMutation=none rule=recording_ts_epg_supplement_contract");
#endif
            return committed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
#if TVAIR_DEVELOPER_DIAGNOSTICS
        catch (Exception ex)
#else
        catch (Exception)
#endif
        {
#if TVAIR_DEVELOPER_DIAGNOSTICS
            Log("RECORDING_EPG_SUPPLEMENT", $"R{reservationId}", $"result=SKIP_ERROR error={ex.GetType().Name}:{SafeLogValue(ex.Message)} action=ignore_supplement_failure recordingImpact=none rule=recording_ts_epg_supplement_contract");
#endif
            return 0;
        }
        finally
        {
            if (resetParserAfterPass) state.ResetParser();
            state.Gate.Release();
        }
    }

    public void PruneRecordingTsSupplementStates(IReadOnlyCollection<int> activeReservationIds)
    {
        var active = activeReservationIds.ToHashSet();
        foreach (var id in recordingSupplementStates.Keys)
            if (!active.Contains(id)) recordingSupplementStates.TryRemove(id, out _);
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
        var otherSeenReset = store.ResetOtherScheduleSeen();
#if TVAIR_DEVELOPER_DIAGNOSTICS
        coverageAttribution.BeginRun(runId);
        Log("EPG_COVERAGE_ATTRIBUTION", "EPG", $"result=RESET runId={runId} otherScheduleSeenResetRows={otherSeenReset} scope=normal_epg_identity_attribution mutation=epg_cache_provenance_marker_reset rule=epg_overlay_gap_attribution");
#endif
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

            var allGroups = BuildGroups(load.Targets, load.ServiceStates);
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

#if TVAIR_DEVELOPER_DIAGNOSTICS
            if (string.Equals(runResult, "OK", StringComparison.OrdinalIgnoreCase))
            {
                var auditNow = DateTime.Now;
                var auditBaseDate = auditNow.Date;
                var audits = new List<EpgCoverageAttributionDiagnosticStore.DailyCoverageAudit>(7);
                for (var dayOffset = 0; dayOffset < 7; dayOffset++)
                {
                    var auditDate = auditBaseDate.AddDays(dayOffset);
                    var auditFrom = dayOffset == 0 ? auditNow : auditDate;
                    var auditTo = auditDate.AddDays(1);
                    var dbEventsForAudit = store.GetByRange(auditFrom, auditTo);
                    var projectedEventsForAudit = diagnosticDbProgramEventSource.GetByRange(auditFrom, auditTo);
                    var audit = coverageAttribution.BuildDailyAudit(auditDate, auditFrom, dbEventsForAudit, projectedEventsForAudit);
                    audits.Add(audit);
                    var auditResult = audit.Result switch
                    {
                        EpgCoverageAttributionDiagnosticStore.DailyCoverageAuditResult.Pass => "PASS",
                        EpgCoverageAttributionDiagnosticStore.DailyCoverageAuditResult.Fail => "FAIL",
                        _ => "INCOMPLETE_EVIDENCE",
                    };
                    var evidenceReason = audit.TargetServices <= 0
                        ? "NO_TARGET_SERVICES"
                        : audit.AuthoritativeEvents <= 0
                            ? "NO_AUTHORITY"
                            : "COMPLETE";
                    Log("EPG_DAILY_COVERAGE_AUDIT", "EPG",
                        $"result={auditResult} evidence={evidenceReason} runId={audit.RunId} dayOffset=D+{dayOffset} date={audit.Date:yyyy-MM-dd} targetServices={audit.TargetServices} scheduleCompleteServices={audit.ScheduleCompleteServices} scheduleIncompleteServices={audit.ScheduleIncompleteServices} " +
                        $"authoritativeEvents={audit.AuthoritativeEvents} dbPresent={audit.DbPresent} dbMissing={audit.DbMissing} projectionPresent={audit.ProjectionPresent} projectionMissing={audit.ProjectionMissing} blankTitleCount={audit.BlankTitleCount} defectIdentityCount={audit.DefectIdentityCount} " +
                        $"incompleteServiceSample=[{SafeLogValue(audit.IncompleteServiceSample)}] dbMissingSample=[{SafeLogValue(audit.DbMissingSample)}] projectionMissingSample=[{SafeLogValue(audit.ProjectionMissingSample)}] blankTitleSample=[{SafeLogValue(audit.BlankTitleSample)}] " +
                        "coverageBasis=basic_schedule_section_completion_plus_observed_event_identity continuityRule=observed_basic_to_db_to_db_projection offAirPolicy=no_wall_clock_gap_inference mutation=none publicBuild=compiled_out rule=epg_daily_coverage_audit");
                }

                static double RetentionPercent(IEnumerable<EpgCoverageAttributionDiagnosticStore.DailyCoverageAudit> source)
                {
                    var rows = source.ToArray();
                    var authoritative = rows.Sum(x => x.AuthoritativeEvents);
                    if (authoritative <= 0)
                        return 0.0;
                    var defects = rows.Sum(x => x.DefectIdentityCount);
                    return Math.Max(0.0, 100.0 * (authoritative - defects) / authoritative);
                }

                var first72h = audits.Take(3).ToArray();
                var week = audits.Take(7).ToArray();
                var retention72h = RetentionPercent(first72h);
                var retentionWeek = RetentionPercent(week);
                static string DayResult(EpgCoverageAttributionDiagnosticStore.DailyCoverageAudit x)
                    => x.Result switch
                    {
                        EpgCoverageAttributionDiagnosticStore.DailyCoverageAuditResult.Pass => "PASS",
                        EpgCoverageAttributionDiagnosticStore.DailyCoverageAuditResult.Fail => "FAIL",
                        _ => x.TargetServices <= 0 ? "NO_TARGET_SERVICES" : "NO_AUTHORITY",
                    };

                // Release acceptance contract follows usable EPG identities: D is the strict zero-defect gate;
                // 72h is D..D+2 at >=99%; week is D..D+6 at 100%. Section-version churn stays
                // diagnostic evidence and must not by itself become an EPG-missing verdict.
                static bool HasCompleteEvidence(EpgCoverageAttributionDiagnosticStore.DailyCoverageAudit x)
                    => x.TargetServices > 0 && x.AuthoritativeEvents > 0;

                var authority72h = first72h.All(HasCompleteEvidence);
                var authorityWeek = week.All(HasCompleteEvidence);
                var passD0 = HasCompleteEvidence(audits[0])
                    && audits[0].Result == EpgCoverageAttributionDiagnosticStore.DailyCoverageAuditResult.Pass;
                var pass72h = authority72h && passD0 && retention72h >= 99.0;
                var passWeek = authorityWeek && passD0 && retentionWeek >= 100.0;
                var d0Result = DayResult(audits[0]);
                var window72hResult = !authority72h ? "INCOMPLETE_EVIDENCE" : pass72h ? "PASS" : "FAIL";
                var windowWeekResult = !authorityWeek ? "INCOMPLETE_EVIDENCE" : passWeek ? "PASS" : "FAIL";
                var retention72hText = authority72h ? retention72h.ToString("F3") : "NA";
                var retentionWeekText = authorityWeek ? retentionWeek.ToString("F3") : "NA";
                var overallResult = !authority72h || !authorityWeek ? "INCOMPLETE_EVIDENCE" : pass72h && passWeek ? "PASS" : "FAIL";
                Log("EPG_COVERAGE_RELEASE_AUDIT", "EPG",
                    $"result={overallResult} runId={coverageAttribution.CurrentRunId} baseDate={auditBaseDate:yyyy-MM-dd} " +
                    $"d0={d0Result} d1={DayResult(audits[1])} d2={DayResult(audits[2])} d3={DayResult(audits[3])} d4={DayResult(audits[4])} d5={DayResult(audits[5])} d6={DayResult(audits[6])} " +
                    $"window72h=D..D+2 windowWeek=D..D+6 passD0={d0Result} " +
                    $"authority72h={(authority72h ? "COMPLETE" : "INCOMPLETE")} pass72h={window72hResult} retention72h={retention72hText} target72h=99.000 " +
                    $"authorityWeek={(authorityWeek ? "COMPLETE" : "INCOMPLETE")} passWeek={windowWeekResult} retentionWeek={retentionWeekText} targetWeek=100.000 " +
                    $"authoritative72h={first72h.Sum(x => x.AuthoritativeEvents)} dbMissing72h={first72h.Sum(x => x.DbMissing)} projectionMissing72h={first72h.Sum(x => x.ProjectionMissing)} blankTitle72h={first72h.Sum(x => x.BlankTitleCount)} defectIdentities72h={first72h.Sum(x => x.DefectIdentityCount)} " +
                    $"authoritativeWeek={week.Sum(x => x.AuthoritativeEvents)} dbMissingWeek={week.Sum(x => x.DbMissing)} projectionMissingWeek={week.Sum(x => x.ProjectionMissing)} blankTitleWeek={week.Sum(x => x.BlankTitleCount)} defectIdentitiesWeek={week.Sum(x => x.DefectIdentityCount)} " +
                    "metric=observed_basic_schedule_identity_retention noWallClockGapInference=true noBroadcasterIntentInference=true mutation=none publicBuild=compiled_out rule=epg_release_coverage_audit");
            }
#endif
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
        PreRecordProbeExecutionContract executionContract,
        TunerLease preclaimedPreRecordLease,
        bool preserveUserChainDbSeriesFollow = false)
    {
        ArgumentNullException.ThrowIfNull(executionContract);
        var started = DateTime.Now;
        var normalizedScope = NormalizeTargetScope(executionContract.TargetScope);
        var initialRemainingSeconds = (int)Math.Ceiling((executionContract.HardDeadline - DateTimeOffset.Now).TotalSeconds);
        if (initialRemainingSeconds <= 0)
        {
            try { preclaimedPreRecordLease.Dispose(); } catch { }
            Log("PRE_REC_EPG_INDEPENDENT_PROBE", "EPG確認",
                $"result=HARD_DEADLINE_EXPIRED_BEFORE_LAUNCH targetScope={normalizedScope} hardDeadline={executionContract.HardDeadline:O} action=keep_original_recording_time rule=pre_record_epg_execution_lifecycle_contract");
            return new EpgCaptureResult(false, 0, 1, 0, "FAILED", 1,
                "録画前EPG確認の実行期限を過ぎました。",
                "failureStage=execution_contract;failureReason=hard_deadline_expired_before_launch");
        }
        var runId = $"prerec-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Interlocked.Increment(ref epgRunSequence)}";
        if (preclaimedPreRecordLease is null || !preclaimedPreRecordLease.IsCurrent)
        {
            Log("PRE_REC_EPG_INDEPENDENT_PROBE", "EPG確認",
                $"result=ADMISSION_CLAIM_INVALID runId={runId} tuner={SafeLogValue(executionContract.RuntimeTunerName)} failureStage=admission_claim failureReason=admission_claim_not_current action=keep_original_recording_time rule=pre_record_epg_admission_claim_ssot");
            try { preclaimedPreRecordLease?.Dispose(); } catch { }
            return new EpgCaptureResult(false, 0, 1, 0, "FAILED", 1,
                "録画前EPG確認用チューナーの実行権を維持できませんでした。",
                "failureStage=admission_claim;failureReason=admission_claim_not_current");
        }
        var load = channelLoader.Load();
        if (load.Targets.Count == 0)
        {
            try { preclaimedPreRecordLease.Dispose(); } catch { }
            return new EpgCaptureResult(false, 0, 1, 0, "FAILED", 1,
                "録画前EPG確認対象チャンネルがありません。",
                "failureStage=target_resolution;failureReason=no_prerec_channels");
        }

        // PRE_RECORD_EPG_RECORDING_IDENTITY_SSOT:
        // The parent recording reservation owns the target service identity. Resolve the physical TS only
        // from the exact NID/TSID/SID triplet. ServiceName is mutable display metadata and must never
        // participate in PreRec admission/target resolution.
        var groups = FilterGroupsForExpectedServiceIdentity(
            FilterGroupsByScope(BuildGroups(load.Targets), normalizedScope),
            executionContract.NetworkId,
            executionContract.TransportStreamId,
            executionContract.ServiceId);
        if (groups.Count != 1)
        {
            var failureReason = groups.Count == 0
                ? "target_service_identity_not_found"
                : "target_service_identity_ambiguous";
            Log("PRE_REC_EPG_INDEPENDENT_PROBE", "EPG確認",
                $"result={(groups.Count == 0 ? "TARGET_IDENTITY_NOT_FOUND" : "TARGET_IDENTITY_AMBIGUOUS")} runId={runId} targetScope={normalizedScope} matchedGroups={groups.Count} expectedNid={executionContract.NetworkId} expectedTsid={executionContract.TransportStreamId} expectedSid={executionContract.ServiceId} expectedEventId={(executionContract.EventId?.ToString() ?? "-")} identitySource=parent_recording_reservation serviceNameUsedForIdentity=false action=keep_original_recording_time rule=pre_record_epg_recording_identity_ssot");
            try { preclaimedPreRecordLease.Dispose(); } catch { }
            return new EpgCaptureResult(false, 0, 1, 0, "FAILED", 1,
                groups.Count == 0
                    ? "録画対象サービスのNID/TSID/SIDに対応する放送TSを解決できませんでした。"
                    : "録画対象サービスのNID/TSID/SIDに対応する放送TSが複数見つかりました。",
                $"failureStage=target_resolution;failureReason={failureReason}");
        }

        var group = groups[0];
        var snapshotOnly = !preserveUserChainDbSeriesFollow;
        var events = new ConcurrentBag<EpgEvent>();
        PreRecordProbeResult? probeCompletion = null;
        var workerState = RegisterActiveEpgWorkerTask(group, 1, runId);
        var imported = 0;
        var admissionLeaseConsumedByWorkerState = false;
        try
        {
            Log("PRE_REC_EPG_INDEPENDENT_PROBE", $"TS{group.TsId}",
                $"result=START runId={runId} group={group.Group} tuner={SafeLogValue(executionContract.RuntimeTunerName)} hardDeadline={executionContract.HardDeadline:O} remainingSec={Math.Max(0, (int)Math.Ceiling((executionContract.HardDeadline - DateTimeOffset.Now).TotalSeconds))} expectedEventId={(executionContract.EventId?.ToString() ?? "-")} chainDbSeriesFollow={preserveUserChainDbSeriesFollow} lifecycleOwner=EpgCapture deadlineOwner=EpgCapture policy=parallel_by_physical_recording_tuner_no_normal_epg_status_owner rule=pre_record_epg_execution_lifecycle_contract");

            imported = await CaptureGroupAsync(
                group: group,
                pass: 1,
                ct: ct,
                isPreRecordCheck: true,
                workerTaskState: workerState,
                maxCaptureSeconds: Math.Max(1, (int)Math.Ceiling((executionContract.HardDeadline - DateTimeOffset.Now).TotalSeconds)),
                preRecordExecutionContract: executionContract,
                preferredRecordingTunerName: executionContract.RuntimeTunerName,
                preTuneChainPosition: null,
                preTuneAction: null,
                preTuneKeepWorkerUntilSafetyCeiling: false,
                preRecordProbeSnapshotOnly: snapshotOnly,
                preRecordEvents: events,
                onPreRecordProbeCompleted: probe => probeCompletion = probe,
                releaseWorkerAdmission: null,
                reacquireWorkerAdmissionAsync: null,
                preclaimedPreRecordLease: preclaimedPreRecordLease,
                onPreclaimedLeaseAttached: () => admissionLeaseConsumedByWorkerState = true).ConfigureAwait(false);
        }
        finally
        {
            workerState.MarkOwnerTaskCompleted();
            var ownerSnapshot = workerState.Snapshot();
            if (!TryConvergeExitedEpgWorker(workerState, EpgWorkerTerminalReason.Completed, "independent_prerec_owner_finally", ownerSnapshot.AttemptGeneration))
                StartResidualEpgWorkerExitMonitor(workerState);
            if (!admissionLeaseConsumedByWorkerState && preclaimedPreRecordLease.IsIdentityCurrent)
            {
                try { preclaimedPreRecordLease.Dispose(); } catch { }
            }
        }

        var snapshot = events.OrderBy(e => e.Start).ThenBy(e => e.EventId).ToArray();
        var terminalReason = probeCompletion?.TerminalReason ?? PreRecordProbeTerminalReason.ObservationOnly;
        var targetObserved = terminalReason == PreRecordProbeTerminalReason.TargetObserved;
        var success = targetObserved && (preserveUserChainDbSeriesFollow ? imported > 0 : imported > 0 && snapshot.Length > 0);
        var runResult = success ? "OK" : "FAILED";
        Log("PRE_REC_EPG_INDEPENDENT_PROBE", $"TS{group.TsId}",
            $"result={runResult} runId={runId} group={group.Group} tuner={SafeLogValue(executionContract.RuntimeTunerName)} terminalReason={terminalReason} targetObserved={targetObserved} observedEvents={snapshot.Length} importedEvents={(preserveUserChainDbSeriesFollow ? imported : 0)} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} dbWrite={(preserveUserChainDbSeriesFollow ? "epg_store_user_chain_protected" : "none")} lifecycleOwner=EpgCapture normalEpgStatusOwner=untouched rule=pre_record_epg_execution_lifecycle_contract");
        var failureDetail = success
            ? $"result={runResult};independentPreRec=true;terminalReason={terminalReason};tuner={SafeLogValue(executionContract.RuntimeTunerName)}"
            : $"failureStage=probe_lifecycle;failureReason={terminalReason};targetObserved={targetObserved};observedEvents={snapshot.Length};tuner={SafeLogValue(executionContract.RuntimeTunerName)}";
        return new EpgCaptureResult(success, success ? 1 : 0, 1, preserveUserChainDbSeriesFollow ? imported : 0, runResult, success ? 0 : 1,
            "録画前時刻確認を終了しました",
            failureDetail)
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
                        preRecordExecutionContract: null,
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
        PreRecordProbeExecutionContract? preRecordExecutionContract = null,
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
                preRecordExecutionContract);

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
        PreRecordProbeExecutionContract? preRecordExecutionContract = null)
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
            // PRE_REC_WIRE_CONTRACT_SSOT: this is the exact Scheduler-frozen target contract.
            // channels[] remains only the physical same-TS observation/tuning scope.
            ["preRecordProbe"] = preRecordExecutionContract is null ? null : new Dictionary<string, object?>
            {
                ["networkId"] = (int)preRecordExecutionContract.NetworkId,
                ["transportStreamId"] = (int)preRecordExecutionContract.TransportStreamId,
                ["serviceId"] = (int)preRecordExecutionContract.ServiceId,
                ["eventId"] = preRecordExecutionContract.EventId.HasValue ? (int)preRecordExecutionContract.EventId.Value : null,
                ["expectedStartTime"] = preRecordExecutionContract.ExpectedStartTime.ToString("O"),
                ["expectedEndTime"] = preRecordExecutionContract.ExpectedEndTime.ToString("O"),
                ["hardDeadlineUtc"] = preRecordExecutionContract.HardDeadline.UtcDateTime.ToString("O"),
                ["runtimeTunerName"] = preRecordExecutionContract.RuntimeTunerName
            },
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

            var routeNid = isPreRecordCheck ? preRecordExecutionContract?.NetworkId : target?.OriginalNetworkId;
            var routeSid = isPreRecordCheck ? preRecordExecutionContract?.ServiceId : target?.ServiceId;
            Log("TVAIREPGREC_EPG_ROUTE", $"TS{group.TsId}",
                $"result=SELECTED pid={process.Id} exe={SafeLogValue(exe)} job={SafeLogValue(jobPath)} result={SafeLogValue(resultPath)} progress={SafeLogValue(progressPath)} stopSignal={SafeLogValue(stopSignalPath)} service={SafeLogValue(displayServiceName ?? target?.Name)} nid={(routeNid?.ToString() ?? "-")} tsid={group.TsId} sid={(routeSid?.ToString() ?? "-")} targetSidCount={group.Targets.Count} targetSids=[{string.Join(",", group.Targets.Select(t => t.ServiceId).OrderBy(x => x))}] targetIdentitySource={(isPreRecordCheck ? "pre_record_execution_contract" : "ts_group_first_target")} expectedEventId={(preRecordExecutionContract?.EventId?.ToString() ?? "-")} scope={(isPreRecordCheck ? "target_event_pre_record_check" : "transport_stream_schedule_epg")} recordServiceScopeShared=false channelArgument={SafeLogValue(channelArgument)} bonDriverSetChannelSpace={(target?.ResolvedSpace.ToString() ?? "-")} bonDriverSetChannelIndex={(target?.ResolvedChannelIndex.ToString() ?? "-")} did={did} seconds={effectiveWait} hardDeadline={(preRecordExecutionContract?.HardDeadline.ToString("O") ?? "-")} getStreamVariant={SafeLogValue(getStreamVariant)} logoPolicy={SafeLogValue(logoPolicy)} output={SafeLogValue(tsFile)} route=TvAIrEpgRec mode={(isPreRecordCheck ? "epg-check" : "epg")} launchKind={launchKind} taskbarIconVisible={showWorkerTaskbarIcon} windowPolicy={windowPolicy} preTuneChainPosition={SafeLogValue(preTuneChainPosition)} preTuneAction={SafeLogValue(preTuneAction)} keepWorkerUntilSafetyCeiling={preTuneKeepWorkerUntilSafetyCeiling} titleBarLogoPath={SafeLogValue(displayLogoPath)} centerLogoPath={SafeLogValue(centerLogoPath)} logoTarget=worker_titlebar_center_only rule=epg_worker_display_contract");

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

    private async Task<int> CaptureGroupAsync(TsGroup group, int pass, CancellationToken ct, bool isPreRecordCheck, ActiveEpgWorkerTask workerTaskState, int? maxCaptureSeconds, PreRecordProbeExecutionContract? preRecordExecutionContract = null, string? preferredRecordingTunerName = null, string? preTuneChainPosition = null, string? preTuneAction = null, bool preTuneKeepWorkerUntilSafetyCeiling = false, bool preRecordProbeSnapshotOnly = false, ConcurrentBag<EpgEvent>? preRecordEvents = null, Action<PreRecordProbeResult>? onPreRecordProbeCompleted = null, Action? releaseWorkerAdmission = null, Func<CancellationToken, Task>? reacquireWorkerAdmissionAsync = null, TunerLease? preclaimedPreRecordLease = null, Action? onPreclaimedLeaseAttached = null)
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
                $" expectedNid={(preRecordExecutionContract?.NetworkId.ToString() ?? "-")} expectedTsid={(preRecordExecutionContract?.TransportStreamId.ToString() ?? "-")} expectedSid={(preRecordExecutionContract?.ServiceId.ToString() ?? "-")} expectedEventId={(preRecordExecutionContract?.EventId?.ToString() ?? "-")} expectedStart={(preRecordExecutionContract is null ? "-" : preRecordExecutionContract.ExpectedStartTime.ToString("MM/dd HH:mm:ss"))} runtimeTuner={SafeLogValue(preferredRecordingTunerName)} lifecycleOwner=EpgCapture terminalOwner=EpgCapture workerRole=transport_observer targetNotFoundAuthority=hard_deadline_only rule=pre_record_epg_execution_lifecycle_contract");
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
            if (isPreRecordCheck && preRecordExecutionContract is not null)
            {
                var remaining = (int)Math.Ceiling((preRecordExecutionContract.HardDeadline - DateTimeOffset.Now).TotalSeconds);
                if (remaining <= 0)
                {
                    Log("PRE_REC_EPG_PROBE_TERMINAL", $"TS{group.TsId}",
                        $"result=ABORTED reason=hard_deadline_reached_before_worker_launch worker={workerName} hardDeadline={preRecordExecutionContract.HardDeadline:O} targetNotFound=False action=no_worker_launch lifecycleOwner=EpgCapture rule=pre_record_epg_execution_lifecycle_contract");
                    return 0;
                }
                effectiveWait = Math.Min(effectiveWait, remaining);
            }

            // TunerPool からチューナーを確保
            // TvAIr管理TunerPoolのみから確保
            var plannedEnd = isPreRecordCheck && preRecordExecutionContract is not null
                ? preRecordExecutionContract.HardDeadline.LocalDateTime
                : DateTime.Now.AddSeconds(effectiveWait + 30);
            TunerLease? lease;
            if (isPreRecordCheck && preclaimedPreRecordLease is not null)
            {
                // PRE_RECORD_EPG_ADMISSION_CLAIM_SSOT:
                // ReservationSchedulerが安全候補選択と同時に確保したleaseをそのままworker ownerへ渡す。
                // ここでは波単位RecordingLifecycleGateも再Acquireも行わず、Admissionの物理Tuner実行権を覆さない。
                lease = preclaimedPreRecordLease;
                if (!lease.IsCurrent
                    || !string.Equals(lease.Name, preferredRecordingTunerName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(NormalizePreRecordAdmissionGroupForCapture(lease.Group), NormalizePreRecordAdmissionGroupForCapture(group.Group), StringComparison.OrdinalIgnoreCase))
                {
                    Log("PRE_REC_EPG_RUNTIME_TUNER_ACQUIRE", $"TS{group.TsId}",
                        $"result=ADMISSION_CLAIM_INVALID worker={workerName} selectedTuner={SafeLogValue(preferredRecordingTunerName)} leaseTuner={SafeLogValue(lease.Name)} leaseGroup={SafeLogValue(lease.Group)} targetGroup={SafeLogValue(group.Group)} failureStage=admission_claim failureReason=claim_identity_mismatch action=no_worker_launch rule=pre_record_epg_admission_claim_ssot");
                    return 0;
                }
                Log("PRE_REC_EPG_RUNTIME_TUNER_ACQUIRE", $"TS{group.TsId}",
                    $"result=OK_PRECLAIMED worker={workerName} selectedTuner={preferredRecordingTunerName} actualTuner={lease.Name} did={lease.Did} group={group.Group} leaseId={lease.PoolLeaseId} generation={lease.OccupancyGeneration} targetSids=[{string.Join(',', group.Targets.Select(t => t.ServiceId).OrderBy(x => x))}] rule=pre_record_epg_admission_claim_ssot");
            }
            else
            {
                lease = await AcquireEpgLeaseWithShortWaitAsync(group, plannedEnd, workerName, ct, isPreRecordCheck, workerTaskState.RunId, isPreRecordCheck ? preferredRecordingTunerName : null);
            }
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
            if (isPreRecordCheck && preclaimedPreRecordLease is not null && ReferenceEquals(lease, preclaimedPreRecordLease))
                onPreclaimedLeaseAttached?.Invoke();

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
                var displayServiceName = ResolveActivityDisplayServiceName(group, isPreRecordCheck, preRecordExecutionContract?.ServiceId);
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
                    preRecordExecutionContract,
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
                    // queueの後続と同じcapacity gateを取り直して次attemptへ進む。PreRecは単発。
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
                        launch.ProgressPath,
                        workerTaskState,
                        attemptGeneration,
                        preRecordExecutionContract ?? throw new InvalidOperationException("PreRec execution contract is required for epg-check mode."),
                        !preRecordProbeSnapshotOnly,
                        ct,
                        preferredRecordingTunerName,
                        preTuneKeepWorkerUntilSafetyCeiling);
                    onPreRecordProbeCompleted?.Invoke(probe);

                    if (!workerTaskState.TrySnapshotAttempt(attemptGeneration, out var probeSnapshot))
                        return 0;
                    var probeIdentity = GetOwnedWorkerProcessIdentity(probeSnapshot);
                    if (probeIdentity == EpgWorkerProcessIdentityState.Match)
                    {
                        Log("PRE_REC_EPG_PROBE_STOP_REQUEST", $"TS{group.TsId}",
                            $"pid={launch.ProcessId} worker={workerName} reason={(probe.TargetFound ? "target_event_seen" : probe.TerminalReason == PreRecordProbeTerminalReason.DeadlineReachedTargetNotObserved ? "safety_ceiling_reached" : probe.TerminalReason.ToString())} terminalReason={probe.TerminalReason} lifecycleOwner=EpgCapture rule=pre_record_epg_execution_lifecycle_contract");
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

                    // PRE_REC_BROADCAST_TIME_HANDOFF_INVARIANT:
                    // PreRec may stop its worker immediately after the target event is proven.  That early
                    // terminal path must still consume the worker's latest TDT/TOT evidence before runtime
                    // artifacts are deleted; otherwise a successful short PreRec silently loses the newest
                    // broadcast-vs-Windows correction.  Normal EPG terminal processing and recording-runtime
                    // observation already feed the same BroadcastTimeReference SSOT.
                    _ = TryApplyBroadcastTimeFromWorkerArtifacts(launch, group, "pre_record_check_early_terminal");

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
                            var snapshotResult = probe.TerminalReason == PreRecordProbeTerminalReason.DeadlineReachedTargetNotObserved
                                ? "TARGET_NOT_FOUND"
                                : "EXECUTION_FAILED";
                            Log("PRE_REC_EPG_SNAPSHOT", $"TS{group.TsId}",
                                $"result={snapshotResult} terminalReason={probe.TerminalReason} observedEvents={probe.Events.Count} dbWrite=none expectedEventId={(preRecordExecutionContract?.EventId?.ToString() ?? "-")} action=fail_probe_keep_original_reservation_time lifecycleOwner=EpgCapture rule=pre_record_epg_execution_lifecycle_contract");
                        }
                        try { File.Delete(tsFile); } catch { }
                        CleanupTvAIrEpgRecRuntimeFiles(launch, workerName, group, reason: probe.TargetFound
                            ? "pre_record_probe_target_found"
                            : probe.TerminalReason == PreRecordProbeTerminalReason.DeadlineReachedTargetNotObserved
                                ? "pre_record_probe_target_not_found_after_deadline"
                                : $"pre_record_probe_execution_failed_{probe.TerminalReason}");
                    }
                    else
                    {
                        // USER_CHAIN_PRE_REC_OBSERVED_SNAPSHOT_INVARIANT:
                        // chain-rootも今回のworkerが実観測したsnapshotをschedulerへ返す。
                        // 既存DB保存は表示投影用に維持するが、時間追従の正本はこの観測snapshotとする。
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
#if TVAIR_DEVELOPER_DIAGNOSTICS
                    using var completenessObservationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var completenessObservationTask = ObserveNormalEpgCompletenessDuringCaptureAsync(
                        group,
                        tsFile,
                        launch.ProcessId,
                        workerName,
                        workerTaskState,
                        attemptGeneration,
                        TimeSpan.FromSeconds(effectiveWait),
                        completenessObservationCts.Token);
#endif
                    // TvAIrEpgRec の終了を待つ。
                    // 現行では予定枠/実取得上限を変更せず、completenessはDeveloper観測専用。
                    // basic scheduleだけの早期completeで0x58-0x5F詳細scheduleを削らないことを優先する。
                    var processTimeout = TimeSpan.FromSeconds(effectiveWait + 8 + 30);
                    var waitResult = await WaitForExitOrExternalFailureAsync(
                        launch.ProcessId,
                        processTimeout,
                        workerTaskState,
                        attemptGeneration,
                        ct).ConfigureAwait(false);
#if TVAIR_DEVELOPER_DIAGNOSTICS
                    completenessObservationCts.Cancel();
                    try { await completenessObservationTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (completenessObservationCts.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        Log("EPG_CAPTURE_COMPLETENESS_OBSERVER", $"TS{group.TsId}",
                            $"result=OBSERVER_ERROR worker={workerName} pid={launch.ProcessId} error={ex.GetType().Name}:{SafeLogValue(ex.Message)} action=ignore_observer_error_keep_capture_contract rule=epg_capture_completeness_observer");
                    }
#endif

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
                        launch, workerName, group, tsFile, attempt, maxAttempts, imported, isPreRecordCheck);

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



    private bool TryApplyBroadcastTimeFromWorkerArtifacts(EpgWorkerLaunchResult launch, TsGroup group, string purpose)
    {
        // Prefer the terminal result because it contains the worker's final observation.  A short PreRec
        // can be stopped before that result is durable, so progress is an equal-authority fallback.
        if (TryReadBroadcastTimeFromResult(launch.ResultPath, out var observation))
        {
            return ApplyBroadcastTimeObservation(
                group, purpose, "worker_result", observation.BroadcastTime, observation.SystemObservedAt,
                observation.OffsetMilliseconds, observation.SourceTableId, observation.ObservationCount);
        }

        if (TryReadBroadcastTimeFromProgress(launch.ProgressPath, out observation))
        {
            return ApplyBroadcastTimeObservation(
                group, purpose, "worker_progress", observation.BroadcastTime, observation.SystemObservedAt,
                observation.OffsetMilliseconds, observation.SourceTableId, observation.ObservationCount);
        }

        Log("BROADCAST_TIME_OBSERVATION", $"TS{group.TsId}",
            $"result=NOT_OBSERVED purpose={SafeLogValue(purpose)} source=worker_artifacts action=keep_existing_prerec_reference_unchanged " +
            "updatePolicy=refresh_on_every_valid_observation_hold_latest_between_observations_persist_across_restart windowsTimeChanged=False " +
            "reservationClockMutation=none prerecClockMutation=none recordingClockMutation=none epgClockMutation=none wakeClockMutation=none rule=broadcast_time_reference_single_source");
        return false;
    }

    private bool ApplyBroadcastTimeObservation(
        TsGroup group,
        string purpose,
        string source,
        DateTimeOffset broadcastTime,
        DateTimeOffset systemObservedAt,
        long offsetMilliseconds,
        int sourceTableId,
        int observationCount)
    {
        var sourceTable = sourceTableId >= 0 ? $"0x{sourceTableId:X2}" : "-";
        var referenceUpdated = broadcastTimeReference.TryUpdate(
            broadcastTime.LocalDateTime,
            systemObservedAt.LocalDateTime,
            offsetMilliseconds,
            sourceTableId,
            Math.Max(1, observationCount),
            out var referenceReason);

        Log("BROADCAST_TIME_OBSERVATION", $"TS{group.TsId}",
            $"result=OBSERVED purpose={SafeLogValue(purpose)} source={SafeLogValue(source)} tableId={sourceTable} observations={Math.Max(1, observationCount)} " +
            $"broadcastTime={broadcastTime:O} systemTimeAtObservation={systemObservedAt:O} offsetMs={offsetMilliseconds} " +
            $"precision=section_second_resolution_transport_latency_uncompensated action=update_prerec_reference_only updatePolicy=refresh_on_every_valid_observation_hold_latest_between_observations_persist_across_restart " +
            $"referenceUpdated={referenceUpdated} referenceReason={SafeLogValue(referenceReason)} windowsTimeChanged=False " +
            "reservationClockMutation=none prerecClockMutation=effective_clock_only recordingClockMutation=none epgClockMutation=none wakeClockMutation=none rule=broadcast_time_reference_single_source");
        return referenceUpdated;
    }

    private static bool TryReadBroadcastTimeFromResult(string? resultPath, out BroadcastTimeWorkerObservation observation)
    {
        observation = default;
        if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(resultPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("tsReadProbe", out var probe) || probe.ValueKind != JsonValueKind.Object) return false;
            return TryReadBroadcastTimeObservation(probe, out observation);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (JsonException) { return false; }
    }

    private static bool TryReadBroadcastTimeFromProgress(string? progressPath, out BroadcastTimeWorkerObservation observation)
    {
        observation = default;
        if (string.IsNullOrWhiteSpace(progressPath) || !File.Exists(progressPath)) return false;
        try
        {
            foreach (var line in File.ReadLines(progressPath).Where(x => !string.IsNullOrWhiteSpace(x)).TakeLast(128).Reverse())
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (TryReadBroadcastTimeObservation(doc.RootElement, out observation)) return true;
                }
                catch (JsonException)
                {
                    // A worker can still be finishing the last JSONL line.  Older complete progress
                    // records remain valid evidence and are checked next.
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static bool TryReadBroadcastTimeObservation(JsonElement root, out BroadcastTimeWorkerObservation observation)
    {
        observation = default;

        static bool TryProperty(JsonElement element, string pascal, string camel, out JsonElement value)
            => element.TryGetProperty(pascal, out value) || element.TryGetProperty(camel, out value);

        if (!TryProperty(root, "BroadcastTimeObserved", "broadcastTimeObserved", out var observedElement)
            || observedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !observedElement.GetBoolean()) return false;
        if (!TryProperty(root, "BroadcastTimeValue", "broadcastTimeValue", out var broadcastElement)
            || broadcastElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(broadcastElement.GetString(), out var broadcastTime)) return false;
        if (!TryProperty(root, "BroadcastTimeSystemObservedAt", "broadcastTimeSystemObservedAt", out var systemElement)
            || systemElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(systemElement.GetString(), out var systemObservedAt)) return false;
        if (!TryProperty(root, "BroadcastTimeOffsetMilliseconds", "broadcastTimeOffsetMilliseconds", out var offsetElement)
            || offsetElement.ValueKind != JsonValueKind.Number
            || !offsetElement.TryGetInt64(out var offsetMilliseconds)) return false;
        if (!TryProperty(root, "BroadcastTimeSourceTableId", "broadcastTimeSourceTableId", out var tableElement)
            || tableElement.ValueKind != JsonValueKind.Number
            || !tableElement.TryGetInt32(out var sourceTableId)) return false;

        var observationCount = 1;
        if (TryProperty(root, "BroadcastTimeObservationCount", "broadcastTimeObservationCount", out var countElement)
            && countElement.ValueKind == JsonValueKind.Number
            && countElement.TryGetInt32(out var parsedCount))
            observationCount = Math.Max(1, parsedCount);

        observation = new BroadcastTimeWorkerObservation(
            broadcastTime, systemObservedAt, offsetMilliseconds, sourceTableId, observationCount);
        return true;
    }

    private readonly record struct BroadcastTimeWorkerObservation(
        DateTimeOffset BroadcastTime,
        DateTimeOffset SystemObservedAt,
        long OffsetMilliseconds,
        int SourceTableId,
        int ObservationCount);

    private EpgWorkerTerminalEvidence LogTvAIrEpgRecTerminalEvidence(
        EpgWorkerLaunchResult launch,
        string workerName,
        TsGroup group,
        string tsFile,
        int attempt,
        int maxAttempts,
        int imported,
        bool isPreRecordCheck)
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
        bool broadcastTimeObserved = false;
        int broadcastTimeObservationCount = 0;
        int broadcastTimeSourceTableId = -1;
        DateTimeOffset? broadcastTimeValue = null;
        DateTimeOffset? broadcastTimeSystemObservedAt = null;
        long broadcastTimeOffsetMilliseconds = 0;
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

                if (tsAvailable
                    && ts.TryGetProperty("broadcastTimeObserved", out var broadcastObservedValue)
                    && broadcastObservedValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    broadcastTimeObserved = broadcastObservedValue.GetBoolean();
                    if (ts.TryGetProperty("broadcastTimeObservationCount", out var countValue)
                        && countValue.ValueKind == JsonValueKind.Number)
                        countValue.TryGetInt32(out broadcastTimeObservationCount);
                    if (ts.TryGetProperty("broadcastTimeSourceTableId", out var tableValue)
                        && tableValue.ValueKind == JsonValueKind.Number)
                        tableValue.TryGetInt32(out broadcastTimeSourceTableId);
                    if (ts.TryGetProperty("broadcastTimeValue", out var broadcastValue)
                        && broadcastValue.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(broadcastValue.GetString(), out var parsedBroadcastTime))
                        broadcastTimeValue = parsedBroadcastTime;
                    if (ts.TryGetProperty("broadcastTimeSystemObservedAt", out var observedAtValue)
                        && observedAtValue.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(observedAtValue.GetString(), out var parsedSystemObservedAt))
                        broadcastTimeSystemObservedAt = parsedSystemObservedAt;
                    if (ts.TryGetProperty("broadcastTimeOffsetMilliseconds", out var offsetValue)
                        && offsetValue.ValueKind == JsonValueKind.Number)
                        offsetValue.TryGetInt64(out broadcastTimeOffsetMilliseconds);
                }

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

        if (broadcastTimeObserved && broadcastTimeValue.HasValue && broadcastTimeSystemObservedAt.HasValue)
        {
            _ = ApplyBroadcastTimeObservation(
                group,
                isPreRecordCheck ? "pre_record_check" : "normal_epg_capture",
                "worker_result",
                broadcastTimeValue.Value,
                broadcastTimeSystemObservedAt.Value,
                broadcastTimeOffsetMilliseconds,
                broadcastTimeSourceTableId,
                broadcastTimeObservationCount);
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


    private static async Task WaitForWorkerExitOrDeadlineAsync(int processId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var remaining = deadline - DateTimeOffset.Now;
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
        string? progressPath,
        ActiveEpgWorkerTask workerTaskState,
        long expectedAttemptGeneration,
        PreRecordProbeExecutionContract executionContract,
        bool persistProbeEventsToDb,
        CancellationToken ct,
        string? preferredRecordingTunerName = null,
        bool preTuneKeepWorkerUntilSafetyCeiling = false)
    {
        var started = DateTimeOffset.Now;
        var deadline = executionContract.HardDeadline;
        var pollNo = 0;
        var workerExitedBeforeDecision = false;
        var attemptSuperseded = false;
        IReadOnlyList<EpgEvent> lastEvents = Array.Empty<EpgEvent>();

        // PRE_REC_OBSERVATION_SSOT:
        // TvAIrEpgRec is the sole TS/EIT observation owner. EpgCapture consumes only the worker's
        // structured observation evidence and remains the sole lifecycle/terminal owner.
        // Host-side TS copy/reparse is intentionally absent.
        Log("PRE_REC_EPG_PROBE_WAIT", $"TS{group.TsId}",
            $"start worker={workerName} pid={processId} hardDeadline={deadline:O} initialRemainingSec={Math.Max(0, (int)Math.Ceiling((deadline - started).TotalSeconds))} expectedNid={executionContract.NetworkId} expectedTsid={executionContract.TransportStreamId} expectedSid={executionContract.ServiceId} expectedEventId={(executionContract.EventId?.ToString() ?? "-")} expectedStart={executionContract.ExpectedStartTime:MM/dd HH:mm:ss} runtimeTuner={SafeLogValue(executionContract.RuntimeTunerName)} contractIdentity={executionContract.IdentityText} lifecycleOwner=EpgCapture terminalOwner=EpgCapture observationOwner=TvAIrEpgRec targetNotFoundAuthority=hard_deadline_only rule=pre_record_epg_execution_lifecycle_contract");

        while (DateTimeOffset.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var attemptSnapshot = workerTaskState.Snapshot();
            if (attemptSnapshot.AttemptGeneration != expectedAttemptGeneration)
            {
                attemptSuperseded = true;
                break;
            }

            if (TryReadPreRecordObservationFromWorkerProgress(progressPath, executionContract, out var observed))
            {
                lastEvents = observed.Events;
                pollNo++;
                if (preTuneKeepWorkerUntilSafetyCeiling && !string.IsNullOrWhiteSpace(preferredRecordingTunerName))
                {
                    Log("PRE_REC_EPG_PROBE_EVENT_FOUND", $"TS{group.TsId}",
                        $"result=FOUND worker={workerName} pid={processId} poll={pollNo} observedEvents={observed.Events.Count} dbWrite=none event={observed.EventSummary} elapsedSec={(int)(DateTimeOffset.Now - started).TotalSeconds} action=transition_to_record_pretune_hold preferredTuner={SafeLogValue(preferredRecordingTunerName)} observationOwner=TvAIrEpgRec lifecycleOwner=EpgCapture rule=pre_record_epg_execution_lifecycle_contract");
                    await WaitForWorkerExitOrDeadlineAsync(processId, deadline, ct).ConfigureAwait(false);
                    return observed with { TerminalReason = PreRecordProbeTerminalReason.TargetObserved };
                }

                Log("PRE_REC_EPG_PROBE_EVENT_FOUND", $"TS{group.TsId}",
                    $"result=FOUND worker={workerName} pid={processId} poll={pollNo} observedEvents={observed.Events.Count} dbWrite=none event={observed.EventSummary} elapsedSec={(int)(DateTimeOffset.Now - started).TotalSeconds} action=host_terminal_success_stop_worker observationOwner=TvAIrEpgRec lifecycleOwner=EpgCapture rule=pre_record_epg_execution_lifecycle_contract");
                return observed with { TerminalReason = PreRecordProbeTerminalReason.TargetObserved };
            }

            var identity = GetOwnedWorkerProcessIdentity(attemptSnapshot);
            if (identity is EpgWorkerProcessIdentityState.Missing or EpgWorkerProcessIdentityState.ReusedPid)
            {
                workerExitedBeforeDecision = true;
                break;
            }

            var remaining = deadline - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        // One final worker-progress snapshot closes the observation race at process/deadline boundary.
        if (TryReadPreRecordObservationFromWorkerProgress(progressPath, executionContract, out var finalObserved))
        {
            Log("PRE_REC_EPG_PROBE_EVENT_FOUND", $"TS{group.TsId}",
                $"result=FOUND_FINAL_PROGRESS worker={workerName} pid={processId} observedEvents={finalObserved.Events.Count} dbWrite=none event={finalObserved.EventSummary} elapsedSec={(int)(DateTimeOffset.Now - started).TotalSeconds} action=host_terminal_success_from_worker_observation observationOwner=TvAIrEpgRec lifecycleOwner=EpgCapture rule=pre_record_epg_execution_lifecycle_contract");
            return finalObserved with { TerminalReason = PreRecordProbeTerminalReason.TargetObserved };
        }

        if (attemptSuperseded)
        {
            Log("PRE_REC_EPG_PROBE_TERMINAL", $"TS{group.TsId}",
                $"result=ABORTED reason=attempt_superseded worker={workerName} pid={processId} observedEvents={lastEvents.Count} elapsedSec={(int)(DateTimeOffset.Now - started).TotalSeconds} targetNotFound=False action=fallback_without_false_target_not_found lifecycleOwner=EpgCapture rule=pre_record_epg_execution_lifecycle_contract");
            return new PreRecordProbeResult(false, lastEvents, "-") { TerminalReason = PreRecordProbeTerminalReason.AttemptSuperseded };
        }

        if (workerExitedBeforeDecision && DateTimeOffset.Now < deadline)
        {
            Log("PRE_REC_EPG_PROBE_TERMINAL", $"TS{group.TsId}",
                $"result=EXECUTION_FAILED reason=worker_exited_before_host_decision worker={workerName} pid={processId} observedEvents={lastEvents.Count} elapsedSec={(int)(DateTimeOffset.Now - started).TotalSeconds} deadline={deadline:MM/dd HH:mm:ss} targetNotFound=False action=fallback_preserve_original_reservation_time lifecycleOwner=EpgCapture rule=pre_record_epg_execution_lifecycle_contract");
            return new PreRecordProbeResult(false, lastEvents, "-") { TerminalReason = PreRecordProbeTerminalReason.WorkerExitedBeforeDecision };
        }

        Log("PRE_REC_EPG_PROBE_EVENT_NOT_FOUND", $"TS{group.TsId}",
            $"result=NOT_FOUND reason=hard_deadline_reached worker={workerName} pid={processId} observedEvents={lastEvents.Count} dbWrite=none elapsedSec={(int)(DateTimeOffset.Now - started).TotalSeconds} deadline={deadline:MM/dd HH:mm:ss} observationOwner=TvAIrEpgRec targetNotFoundAuthority=hard_deadline_only action=fall_back_to_original_reservation_time lifecycleOwner=EpgCapture rule=pre_record_epg_execution_lifecycle_contract");
        return new PreRecordProbeResult(false, lastEvents, "-") { TerminalReason = PreRecordProbeTerminalReason.DeadlineReachedTargetNotObserved };
    }

    private static bool TryReadPreRecordObservationFromWorkerProgress(
        string? progressPath,
        PreRecordProbeExecutionContract executionContract,
        out PreRecordProbeResult result)
    {
        result = new PreRecordProbeResult(false, Array.Empty<EpgEvent>(), "-");
        if (!TryReadWorkerProgressSnapshot(progressPath, out var stages)) return false;

        foreach (var (rawStage, message) in stages.AsEnumerable().Reverse())
        {
            var stage = NormalizeEpgWorkerProgressStage(rawStage);
            if (!string.Equals(stage, "tsvariant_eit_target_service_found", StringComparison.OrdinalIgnoreCase)) continue;
            var identityProven = message.Contains("targetIdentityProven=True", StringComparison.OrdinalIgnoreCase)
                || message.Contains("exactEventSeen=True", StringComparison.OrdinalIgnoreCase);
            if (!identityProven) continue;
            if (!TryReadProgressField(message, "target", out var targetText)) continue;
            var triplet = targetText.Split('/');
            if (triplet.Length != 3 || !ushort.TryParse(triplet[0], out var nid) || !ushort.TryParse(triplet[1], out var tsid) || !ushort.TryParse(triplet[2], out var sid)) continue;
            if (!TryReadProgressField(message, "expectedEventId", out var eventText) || !ushort.TryParse(eventText, out var eventId) || eventId == 0) continue;
            if (nid != executionContract.NetworkId) continue;
            if (tsid != executionContract.TransportStreamId) continue;
            if (sid != executionContract.ServiceId) continue;
            if (executionContract.EventId.HasValue && executionContract.EventId.Value != 0 && eventId != executionContract.EventId.Value) continue;

            DateTime start = executionContract.ExpectedStartTime;
            DateTime end = executionContract.ExpectedEndTime;
            if (TryReadProgressField(message, "observedStart", out var startText) && DateTime.TryParse(startText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var observedStart)) start = observedStart;
            if (TryReadProgressField(message, "observedEnd", out var endText) && DateTime.TryParse(endText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var observedEnd)) end = observedEnd;
            var observedEvent = new EpgEvent
            {
                NetworkId = nid,
                TransportStreamId = tsid,
                ServiceId = sid,
                EventId = eventId,
                Start = start,
                End = end,
                DurationSeconds = start != DateTime.MinValue && end > start ? (int)Math.Round((end - start).TotalSeconds) : 0,
                UpdatedAt = DateTime.Now
            };
            var events = new[] { observedEvent };
            result = new PreRecordProbeResult(true, events, $"{nid}/{tsid}/{sid}/{eventId} {start:MM/dd HH:mm:ss}〜{end:MM/dd HH:mm:ss}")
            {
                TerminalReason = PreRecordProbeTerminalReason.TargetObserved
            };
            return true;
        }
        return false;
    }

    private static bool TryReadProgressField(string message, string key, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(key)) return false;
        var marker = key + "=";
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        start += marker.Length;
        var end = message.IndexOf(' ', start);
        if (end < 0) end = message.Length;
        value = message[start..end].Trim();
        return value.Length > 0;
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
    private static string NormalizePreRecordAdmissionGroupForCapture(string group)
    {
        if (string.Equals(group, "GR", StringComparison.OrdinalIgnoreCase)) return "GR";
        if (group.Contains("BS", StringComparison.OrdinalIgnoreCase) || group.Contains("CS", StringComparison.OrdinalIgnoreCase)) return "BSCS";
        return group.Trim();
    }

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

#if TVAIR_DEVELOPER_DIAGNOSTICS
    private async Task ObserveNormalEpgCompletenessDuringCaptureAsync(
        TsGroup group,
        string tsFile,
        int processId,
        string workerName,
        ActiveEpgWorkerTask workerTaskState,
        long expectedAttemptGeneration,
        TimeSpan safetyCeiling,
        CancellationToken ct)
    {
        // Developer diagnosis only:
        // Keep the scheduler's fixed occupancy window and worker hard ceiling unchanged.
        // Observe when the existing EpgSectionStatus contract becomes complete so the later
        // production change can distinguish safe early finish from late extended EIT arrival.
        var started = DateTime.Now;
        var deadline = started.Add(safetyCeiling);
        var targetSidSet = group.Targets.Select(t => t.ServiceId).ToHashSet();
        var lastLength = -1L;
        string? lastSignature = null;
        DateTime? firstBasicCompleteAt = null;
        DateTime? firstAllObservedActualCompleteAt = null;

        Log("EPG_CAPTURE_COMPLETENESS_OBSERVER", $"TS{group.TsId}",
            $"result=START worker={workerName} pid={processId} group={group.Group} hardCeilingSec={(int)safetyCeiling.TotalSeconds} targetSidCount={targetSidSet.Count} targetSids=[{string.Join(",", targetSidSet.OrderBy(x => x))}] mutation=none schedulerWindow=unchanged workerCeiling=unchanged rule=epg_capture_completeness_observer");

        while (DateTime.Now < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var snapshot = workerTaskState.Snapshot();
            if (snapshot.AttemptGeneration != expectedAttemptGeneration)
                break;
            var identity = GetOwnedWorkerProcessIdentity(snapshot);
            if (identity is EpgWorkerProcessIdentityState.Missing or EpgWorkerProcessIdentityState.ReusedPid)
                break;

            FileInfo info;
            try
            {
                info = new FileInfo(tsFile);
                if (!info.Exists || info.Length < 188L * 512 || info.Length == lastLength)
                    continue;
                lastLength = info.Length;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            EpgAnalyzeResult epg;
            try
            {
                epg = await new EpgAnalyzer(settings.MaxPacketsToScan).AnalyzeAsync(tsFile, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var actualSchedule = epg.SectionStatuses
                .Where(st => targetSidSet.Contains(st.ServiceId) && EitTableContract.IsActualSchedule(st.TableId))
                .OrderBy(st => st.ServiceId)
                .ThenBy(st => st.TableId)
                .ThenBy(st => st.VersionNumber)
                .ToArray();
            var basicSchedule = actualSchedule.Where(st => EitTableContract.IsActualBasicSchedule(st.TableId)).ToArray();
            var extendedSchedule = actualSchedule.Where(st => EitTableContract.IsActualExtendedSchedule(st.TableId)).ToArray();
            var basicCoverageIssues = BuildBasicScheduleTableCoverageIssues(basicSchedule, targetSidSet);
            var basicIncomplete = basicSchedule.Count(st => !st.IsComplete);
            var extendedIncomplete = extendedSchedule.Count(st => !st.IsComplete);
            var basicComplete = basicSchedule.Length > 0 && basicIncomplete == 0 && basicCoverageIssues.Length == 0;
            var allObservedActualComplete = basicComplete && actualSchedule.All(st => st.IsComplete);

            if (basicComplete && firstBasicCompleteAt is null)
                firstBasicCompleteAt = DateTime.Now;
            if (allObservedActualComplete && firstAllObservedActualCompleteAt is null)
                firstAllObservedActualCompleteAt = DateTime.Now;

            var signature = $"basic={basicComplete}:{basicSchedule.Length}:{basicIncomplete}:{basicCoverageIssues.Length};extended={extendedSchedule.Length}:{extendedIncomplete};allObserved={allObservedActualComplete};ignoredOther={epg.IgnoredOtherTransportStreamEitSectionCount}";
            if (string.Equals(signature, lastSignature, StringComparison.Ordinal))
                continue;
            lastSignature = signature;

            Log("EPG_CAPTURE_COMPLETENESS_OBSERVER", $"TS{group.TsId}",
                $"result=OBSERVED worker={workerName} pid={processId} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} fileBytes={info.Length} " +
                $"basicComplete={basicComplete} basicTables={basicSchedule.Length} basicIncomplete={basicIncomplete} basicCoverageIssues={basicCoverageIssues.Length} " +
                $"extendedObserved={extendedSchedule.Length > 0} extendedTables={extendedSchedule.Length} extendedIncomplete={extendedIncomplete} allObservedActualComplete={allObservedActualComplete} " +
                $"firstBasicCompleteSec={(firstBasicCompleteAt.HasValue ? (int)(firstBasicCompleteAt.Value - started).TotalSeconds : -1)} firstAllObservedActualCompleteSec={(firstAllObservedActualCompleteAt.HasValue ? (int)(firstAllObservedActualCompleteAt.Value - started).TotalSeconds : -1)} " +
                $"ignoredOtherSections={epg.IgnoredOtherTransportStreamEitSectionCount} mutation=none stopRequested=false rule=epg_capture_completeness_observer");
        }

        Log("EPG_CAPTURE_COMPLETENESS_OBSERVER", $"TS{group.TsId}",
            $"result=END worker={workerName} pid={processId} elapsedSec={(int)(DateTime.Now - started).TotalSeconds} firstBasicCompleteSec={(firstBasicCompleteAt.HasValue ? (int)(firstBasicCompleteAt.Value - started).TotalSeconds : -1)} firstAllObservedActualCompleteSec={(firstAllObservedActualCompleteAt.HasValue ? (int)(firstAllObservedActualCompleteAt.Value - started).TotalSeconds : -1)} mutation=none rule=epg_capture_completeness_observer");
    }
#endif

#if TVAIR_DEVELOPER_DIAGNOSTICS
    private void ObserveOtherTransportStreamEit(
        TsGroup group,
        EpgAnalyzeResult other,
        EpgAnalyzeResult actualEpg)
    {
        // Developer-only evidence path over the same isolated other-TS analysis used by
        // the product supplement path. Never performs an additional TS parse or mutation.
        var configuredServices = channelLoader.Load().Targets
            .Select(t => (t.OriginalNetworkId, t.TransportStreamId, t.ServiceId))
            .ToHashSet();

        var scheduleEvents = other.Events
            .Where(e => EitTableContract.IsOtherSchedule(e.BestTableId))
            .Where(e => e.NetworkId != 0 && e.TransportStreamId != 0 && e.ServiceId != 0 && e.EventId != 0)
            .GroupBy(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
            .Select(g => g.OrderByDescending(e => !string.IsNullOrWhiteSpace(e.Title))
                .ThenByDescending(e => e.Start)
                .First())
            .ToArray();

        var configured = scheduleEvents
            .Where(e => configuredServices.Contains((e.NetworkId, e.TransportStreamId, e.ServiceId)))
            .ToArray();
        var unresolved = scheduleEvents.Length - configured.Length;

        var configuredKeys = configured
            .Select(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
            .Distinct()
            .ToArray();
        var dbKeys = store.GetByEventKeys(configuredKeys)
            .Select(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
            .ToHashSet();
        var actualCaptureKeys = actualEpg.Events
            .Select(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
            .ToHashSet();

        var alreadyInDb = configuredKeys.Count(dbKeys.Contains);
        var alsoInCurrentActualCapture = configuredKeys.Count(actualCaptureKeys.Contains);
        var missingFromDb = configuredKeys.Where(k => !dbKeys.Contains(k)).ToArray();
        var missingFromDbAndCurrentActual = missingFromDb.Count(k => !actualCaptureKeys.Contains(k));
        var titled = configured.Count(e => !string.IsNullOrWhiteSpace(e.Title));
        var validTime = configured.Count(e => e.Start != DateTime.MinValue && e.DurationSeconds > 0);

        var byNetwork = configured
            .GroupBy(e => e.NetworkId)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:{g.Select(e => (e.TransportStreamId, e.ServiceId, e.EventId)).Distinct().Count()}")
            .ToArray();
        var missingSample = missingFromDb
            .Take(12)
            .Select(k => $"{k.NetworkId}/{k.TransportStreamId}/{k.ServiceId}/{k.EventId}")
            .ToArray();

        Log("EPG_OTHER_TS_EIT_OBSERVER", $"TS{group.TsId}",
            $"result=OBSERVED tunedGroup={group.Group} tunedTsid={group.TsId} " +
            $"acceptedOtherSections={other.EitSectionCount} parsedOtherEvents={other.Events.Count} scheduleOtherEvents={scheduleEvents.Length} " +
            $"configuredServiceResolvedEvents={configured.Length} unresolvedServiceEvents={unresolved} titledResolvedEvents={titled} validTimeResolvedEvents={validTime} " +
            $"existingDbEvents={alreadyInDb} currentActualCaptureOverlap={alsoInCurrentActualCapture} missingDbEvents={missingFromDb.Length} missingDbAndCurrentActualEvents={missingFromDbAndCurrentActual} " +
            $"networkEventCounts=[{string.Join(",", byNetwork)}] missingSample=[{string.Join(",", missingSample)}] " +
            $"identity=onid_tsid_sid_eventId serviceResolution=configured_ch2_exact mutation=none dbWrite=none projectionWrite=none schedulerWindow=unchanged workerCeiling=unchanged " +
            $"rule=epg_other_ts_eit_observer");
    }
#endif

    private List<EpgEvent> BuildOtherTransportStreamSupplementEvents(EpgAnalyzeResult other)
    {
        var configuredTargets = channelLoader.Load().Targets
            .GroupBy(t => (t.OriginalNetworkId, t.TransportStreamId, t.ServiceId))
            .ToDictionary(g => g.Key, g => g.First());

        // Existence authority for other-TS schedule comes only from basic schedule 0x60-0x67.
        // Extended schedule 0x68-0x6F may enrich a matching event but can never create an
        // event on its own. This mirrors the actual-TS basic/extended responsibility split.
        var basicAuthorityKeys = other.EventAccumulatorAudits
            .Where(a => a.HasBasicScheduleObservation)
            .Select(a => (a.NetworkId, a.TransportStreamId, a.ServiceId, a.EventId, a.Start, a.DurationSeconds))
            .ToHashSet();

        return other.Events
            .Where(e => e.Start != DateTime.MinValue && e.End > e.Start)
            .Where(e => configuredTargets.ContainsKey((e.NetworkId, e.TransportStreamId, e.ServiceId)))
            .Where(e => basicAuthorityKeys.Contains((e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId, e.Start, e.DurationSeconds)))
            .GroupBy(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
            .Select(g => g
                .OrderByDescending(e => !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex))
                .ThenByDescending(e => !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex))
                .ThenByDescending(e => e.Start)
                .First())
            .Select(e =>
            {
                var target = configuredTargets[(e.NetworkId, e.TransportStreamId, e.ServiceId)];
                return new EpgEvent
                {
                    NetworkId = e.NetworkId,
                    TransportStreamId = e.TransportStreamId,
                    ServiceId = e.ServiceId,
                    EventId = e.EventId,
                    ServiceName = target.Name ?? string.Empty,
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
                };
            })
            .OrderBy(e => e.NetworkId)
            .ThenBy(e => e.TransportStreamId)
            .ThenBy(e => e.ServiceId)
            .ThenBy(e => e.Start)
            .ThenBy(e => e.EventId)
            .ToList();
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
            var epg = await new EpgAnalyzer(
                settings.MaxPacketsToScan,
                EitTransportStreamScope.ActualOnly,
                persistentSectionCache: !isPreRecordCheck).AnalyzeAsync(tsFile, ct);
            var configuredServiceIds = group.Targets
                .Select(t => t.ServiceId)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            var targetNetworkId = group.Targets.FirstOrDefault()?.OriginalNetworkId
                ?? epg.Events.FirstOrDefault()?.NetworkId
                ?? (ushort)0;
            var observedActualServiceIds = epg.Events
                .Where(e => e.NetworkId == targetNetworkId && e.TransportStreamId == group.TsId)
                .Select(e => e.ServiceId)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            // Normal EPG uses the active/routable .ch2 service only to tune the physical TS.
            // Once the TS is captured, every service identity actually observed in that TS is
            // eligible for canonical EPG import, even when the service is absent/disabled in
            // the current TVTest .ch2. PreRec remains reservation-service scoped.
            var targetServiceIds = isPreRecordCheck
                ? configuredServiceIds
                : configuredServiceIds
                    .Concat(observedActualServiceIds)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();
            var targetSidSet = targetServiceIds.ToHashSet();
            var serviceNameBySid = group.Targets
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .GroupBy(t => t.ServiceId)
                .ToDictionary(g => g.Key, g => g.First().Name);
            var purpose = isPreRecordCheck ? "pre_record_check" : "normal_epg_capture";
            var canonicalImportCutoff = DateTime.Now;

            Log("EPG_ACTUAL_TS_SERVICE_SCOPE", $"TS{group.TsId}",
                $"result=OK purpose={purpose} group={group.Group} nid={targetNetworkId} tsid={group.TsId} " +
                $"configuredTargetSids=[{string.Join(",", configuredServiceIds)}] observedActualSids=[{string.Join(",", observedActualServiceIds)}] effectiveImportSids=[{string.Join(",", targetServiceIds)}] " +
                $"physicalTuneSource=tvtest_ch2_active_service normalImportSource=observed_actual_ts_services preRecordScope=configured_target_only serviceActivationAffectsNormalEpgImport=false rule=release_contract");

#if TVAIR_DEVELOPER_DIAGNOSTICS
            coverageAttribution.ObserveActual(epg.Events);
#endif

            EpgAnalyzeResult? otherEpg = null;
            List<EpgEvent> otherSupplementCandidates = new();
            if (!isPreRecordCheck
                && string.Equals(group.Group, "BSCS", StringComparison.OrdinalIgnoreCase)
                && epg.IgnoredOtherTransportStreamEitSectionCount > 0)
            {
                try
                {
                    otherEpg = await new EpgAnalyzer(
                        settings.MaxPacketsToScan,
                        EitTransportStreamScope.OtherOnly,
                        persistentSectionCache: true).AnalyzeAsync(tsFile, ct).ConfigureAwait(false);
                    otherSupplementCandidates = FilterCurrentOrFutureImportEvents(
                        BuildOtherTransportStreamSupplementEvents(otherEpg), canonicalImportCutoff);
#if TVAIR_DEVELOPER_DIAGNOSTICS
                    var configuredServiceKeys = channelLoader.Load().Targets
                        .Select(t => (t.OriginalNetworkId, t.TransportStreamId, t.ServiceId))
                        .ToHashSet();
                    var otherBasicAuthorityKeys = otherEpg.EventAccumulatorAudits
                        .Where(a => a.HasBasicScheduleObservation)
                        .Select(a => (a.NetworkId, a.TransportStreamId, a.ServiceId, a.EventId, a.Start, a.DurationSeconds))
                        .ToHashSet();
                    coverageAttribution.ObserveOther(otherEpg.Events, configuredServiceKeys, otherBasicAuthorityKeys);
                    coverageAttribution.MarkSupplementCandidates(otherSupplementCandidates);
#endif
                }
                catch (IOException ex)
                {
                    Log("EPG_OTHER_TS_EIT_SUPPLEMENT", $"TS{group.TsId}",
                        $"result=SKIPPED reason=io_error error={SafeLogValue(ex.Message)} dbWrite=none staleRetire=none rule=epg_other_ts_schedule_supplement");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log("EPG_OTHER_TS_EIT_SUPPLEMENT", $"TS{group.TsId}",
                        $"result=SKIPPED reason=access_error error={SafeLogValue(ex.Message)} dbWrite=none staleRetire=none rule=epg_other_ts_schedule_supplement");
                }
            }

            Log("EPG_IMPORT_SCOPE", $"TS{group.TsId}",
                $"result=OK purpose={purpose} preRecord={isPreRecordCheck} rule=release_contract");

            Log("EPG_PARSE", $"TS{group.TsId}",
                $"purpose={purpose} source=ts_file group={group.Group} {epg.StatsLine} rule=release_contract");

            Log("EPG_COMMON_NORMALIZATION", $"TS{group.TsId}",
                $"result=OK purpose={purpose} group={group.Group} commonEvents={epg.CommonEventCount} commonResolved={epg.CommonResolvedCount} commonUnresolved={epg.CommonUnresolvedCount} " +
                $"resolutionKey=nid_tsid_referencedSid_referencedEid identityMutation=none titleGuessing=disabled sidGuessing=disabled timeGuessing=disabled rule=epg_event_group_common_normalization_contract");

            if (epg.IgnoredOtherTransportStreamEitSectionCount > 0)
            {
                Log("EPG_OTHER_TS_EIT_IGNORED", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} ignoredSections={epg.IgnoredOtherTransportStreamEitSectionCount} " +
                    $"acceptedTables=0x4E,0x50-0x5F isolatedSupplementTables=0x60-0x6F ignoredPfOther=0x4F action=actual_import_isolated_other_schedule_supplement " +
                    $"rule=actual_transport_stream_eit_scope");
            }

#if TVAIR_DEVELOPER_DIAGNOSTICS
            if (otherEpg is not null)
                ObserveOtherTransportStreamEit(group, otherEpg, epg);
#endif

            if (epg.InvalidEitSectionCount > 0)
            {
                Log("EPG_INVALID_EIT_SECTION_DROPPED", $"TS{group.TsId}",
                    $"purpose={purpose} group={group.Group} invalidSections={epg.InvalidEitSectionCount} " +
                    $"syntaxOrLength={epg.InvalidSyntaxOrLengthEitSectionCount} crc={epg.InvalidCrcEitSectionCount} headerConsistency={epg.InvalidHeaderConsistencyEitSectionCount} " +
                    $"transportErrorPackets={epg.TransportErrorPacketCount} continuityDiscontinuities={epg.ContinuityDiscontinuityCount} discontinuityIndicatorResets={epg.DiscontinuityIndicatorResetCount} " +
                    $"exactDuplicatePayloadPackets={epg.DuplicatePayloadPacketCount} resyncWaitDropPackets={epg.ResyncWaitDropPacketCount} " +
                    $"invalidSectionLengthResets={epg.InvalidSectionLengthResetCount} invalidPointerResets={epg.InvalidPointerResetCount} " +
                    $"validation=section_syntax_length_crc32_header_consistency transportResync=pusi_after_error_or_discontinuity action=drop_before_section_tracking_event_header_accumulator_projection_db_import " +
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

            if (epg.ToleratedSameVersionScheduleMetadataDriftCount > 0)
            {
                Log("EPG_SAME_VERSION_SCHEDULE_METADATA_DRIFT", $"TS{group.TsId}",
                    $"result=TOLERATED purpose={purpose} group={group.Group} sections={epg.ToleratedSameVersionScheduleMetadataDriftCount} " +
                    $"action=accept_crc_valid_section_widen_bounds_no_stale_retire rule=eit_same_version_schedule_metadata_drift_contract");
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
                .Where(st => EitTableContract.IsActualBasicSchedule(st.TableId))
                .ToArray();
            // Basic schedule is the sole existence authority. Normal EPG may also observe PF-only
            // services that are not configured in ch2 (for example a data/auxiliary SID). Such an
            // observation is import/enrichment scope, not a reason to invalidate a complete Basic
            // snapshot for unrelated services. Derive stale-retire completeness from the services
            // that actually supplied Basic schedule authority in this capture.
            var staleAuthoritySidSet = staleAuthoritySectionStatuses
                .Select(st => st.ServiceId)
                .Distinct()
                .ToHashSet();
            var nonAuthorityObservedSids = targetSidSet
                .Where(sid => !staleAuthoritySidSet.Contains(sid))
                .OrderBy(sid => sid)
                .ToArray();
            var incompleteSectionStatuses = staleAuthoritySectionStatuses
                .Where(st => !st.IsComplete)
                .ToArray();
            var scheduleCoverageIssues = BuildBasicScheduleTableCoverageIssues(staleAuthoritySectionStatuses, staleAuthoritySidSet);
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
            rawEvents = FilterCurrentOrFutureImportEvents(rawEvents, canonicalImportCutoff);
            LogAccumulatorRawEventProjectionContract(group, purpose, targetServiceIds, epg.EventAccumulatorAudits, epg.Events, rawEvents);

            LogDbInsertPrecheck(group, purpose, targetServiceIds, rawEvents);
            var strictTitleBodyMergeStats = ApplyStrictTitleBodyCanonicalMerge(group, purpose, targetServiceIds, epg, rawEvents);
            LogStrictTitleBodyCanonicalMergeContract(group, purpose, targetServiceIds, strictTitleBodyMergeStats);
            rawEvents = rawEvents
                .OrderBy(e => e.ServiceId)
                .ThenBy(e => e.Start)
                .ThenBy(e => e.EventId)
                .ThenBy(e => e.TableId)
                .ToList();

#if TVAIR_DEVELOPER_DIAGNOSTICS
            coverageAttribution.MarkActualCommitCandidates(rawEvents);
            var incomingEventIdTimingCollisions = rawEvents
                .GroupBy(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
                .Select(g => new
                {
                    g.Key,
                    Events = g
                        .GroupBy(e => (e.Start, e.DurationSeconds))
                        .Select(x => x.First())
                        .OrderBy(e => e.Start)
                        .ThenBy(e => e.DurationSeconds)
                        .ToArray()
                })
                .Where(x => x.Events.Length > 1)
                .ToArray();
            if (incomingEventIdTimingCollisions.Length > 0)
            {
                var collisionEvents = incomingEventIdTimingCollisions.Sum(x => x.Events.Length);
                var sample = string.Join(" | ", incomingEventIdTimingCollisions.Take(12).Select(x =>
                    $"{x.Key.NetworkId}/{x.Key.TransportStreamId}/{x.Key.ServiceId}/{x.Key.EventId}=[{string.Join(",", x.Events.Take(8).Select(e => $"{e.Start:MM-ddTHH:mm:ss}/d{e.DurationSeconds}/t0x{e.TableId:X2}"))}]"));
                Log("EPG_EVENT_ID_TIMING_COLLISION", $"TS{group.TsId}",
                    $"result=OBSERVED phase=before_commit purpose={purpose} group={group.Group} collisionKeys={incomingEventIdTimingCollisions.Length} collisionEvents={collisionEvents} " +
                    $"incomingIdentity=nid_tsid_sid_eventId_start_duration storagePrimaryKey=nid_tsid_sid_eventId sample=[{SafeLogValue(sample)}] mutation=none dbWrite=none rule=epg_event_identity_collision_observer");
            }
#endif

            var basicScheduleEventKeys = epg.EventAccumulatorAudits
                .Where(a => a.HasBasicScheduleObservation)
                .Select(a => (a.NetworkId, a.TransportStreamId, a.ServiceId, a.EventId, a.Start, a.DurationSeconds))
                .ToHashSet();
            var staleAuthorityEvents = rawEvents
                .Where(e => basicScheduleEventKeys.Contains((e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId, e.Start, e.DurationSeconds)))
                .ToList();
#if TVAIR_DEVELOPER_DIAGNOSTICS
            coverageAttribution.MarkActualBasicAuthority(staleAuthorityEvents);
            coverageAttribution.RecordBasicScheduleCoverage(
                group.Targets.Select(t => (t.OriginalNetworkId, t.TransportStreamId, t.ServiceId)),
                staleAuthoritySectionStatuses,
                scheduleCoverageIssues,
                epg.RejectedBasicScheduleEventHeaderCount,
                epg.IgnoredBasicScheduleVersionSwitchEitSectionCount,
                epg.ToleratedSameVersionScheduleMetadataDriftCount);
#endif

            var staleRetireBlockReasons = new List<string>();
            if (staleAuthorityEvents.Count == 0) staleRetireBlockReasons.Add("no_captured_basic_schedule_events");
            if (incompleteSectionStatuses.Length > 0) staleRetireBlockReasons.Add("incomplete_section_snapshot");
            if (scheduleCoverageIssues.Length > 0) staleRetireBlockReasons.Add("incomplete_schedule_table_coverage");
            if (epg.RejectedBasicScheduleEventHeaderCount > 0) staleRetireBlockReasons.Add("rejected_basic_schedule_event_header");
            if (epg.IgnoredBasicScheduleVersionSwitchEitSectionCount > 0) staleRetireBlockReasons.Add("basic_schedule_version_switch_during_capture");
            if (epg.ToleratedSameVersionScheduleMetadataDriftCount > 0) staleRetireBlockReasons.Add("same_version_schedule_metadata_drift_during_capture");
            var staleRetireEligible = staleAuthorityEvents.Count > 0 && staleRetireBlockReasons.Count == 0;

            var importGateWaitStartedAt = DateTime.Now;
            await epgImportCommitGate.WaitAsync(ct).ConfigureAwait(false);
            int upsertedCount;
            int promotedReservations;
            EpgUpsertStorageStats storageStats;
            EpgStaleRetireStats staleStats;
            var retiredKeywordReservations = 0;
            List<EpgEvent> otherSupplementCommittedEvents = new();
            try
            {
                var importGateWaitMs = (int)Math.Max(0, (DateTime.Now - importGateWaitStartedAt).TotalMilliseconds);
                Log("EPG_IMPORT_COMMIT_GATE", $"TS{group.TsId}",
                    $"result=ENTER purpose={purpose} group={group.Group} waitMs={importGateWaitMs} action=serialize_sqlite_write_keep_ts_analysis_parallel rule=epg_import_commit_contract");

                // Canonical existence authority is Basic schedule EIT only. PF / extended / other
                // observations may enrich a current identity but must never preserve an older EID
                // after Basic EIT has moved the same service/time interval to a new identity.
                var commitResult = store.CommitCapture(
                    rawEvents,
                    staleRetireEligible ? staleAuthorityEvents : null,
                    staleRetireEligible ? staleAuthorityEvents : rawEvents);
                upsertedCount = commitResult.Upsert.Count;
                storageStats = commitResult.Upsert.Stats;
                staleStats = commitResult.StaleRetire;

                // The canonical EPG transition owns the lifecycle of automatic reservations that
                // were derived from the retired exact occurrence. Do this while still holding the
                // import commit lane so no later projection/allocation pass can observe a stale
                // Scheduled keyword reservation as if it were still backed by canonical EPG.
                if (staleStats.RetiredEvents.Count > 0)
                {
                    retiredKeywordReservations = reservationStore
                        .DeleteScheduledKeywordReservationsForRetiredEpgEvents(staleStats.RetiredEvents);
                    Log("EPG_AUTHORITY_RESERVATION_RECONCILE", $"TS{group.TsId}",
                        $"result=OK purpose={purpose} group={group.Group} retiredEvents={staleStats.RetiredEvents.Count} removedScheduledKeyword={retiredKeywordReservations} " +
                        "source=canonical_basic_eit_generation_transition manualProgramRuntimePreserved=True rule=epg_authority_reservation_reconcile_contract");
                }

#if TVAIR_DEVELOPER_DIAGNOSTICS
                if (rawEvents.Count > 0)
                {
                    var committedKeys = rawEvents
                        .Select(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
                        .Distinct()
                        .ToArray();
                    var storedAfterCommit = store.GetByEventKeys(committedKeys);
                    var exactRetained = rawEvents
                        .Where(e => storedAfterCommit.Any(dbEvent =>
                            dbEvent.NetworkId == e.NetworkId &&
                            dbEvent.TransportStreamId == e.TransportStreamId &&
                            dbEvent.ServiceId == e.ServiceId &&
                            dbEvent.EventId == e.EventId &&
                            dbEvent.Start == e.Start &&
                            dbEvent.DurationSeconds == e.DurationSeconds))
                        .ToArray();
                    coverageAttribution.MarkActualPresentAfterCommit(exactRetained);
                    Log("EPG_ACTUAL_COMMIT_CONTINUITY", $"TS{group.TsId}",
                        $"result=OBSERVED purpose={purpose} group={group.Group} commitCandidates={rawEvents.Count} exactPresentAfterCommit={exactRetained.Length} exactMissingAfterCommit={Math.Max(0, rawEvents.Count - exactRetained.Length)} mutation=none observerOnly=True rule=epg_actual_commit_continuity_observer");
                }

                if (incomingEventIdTimingCollisions.Length > 0)
                {
                    var collisionKeys = incomingEventIdTimingCollisions
                        .Select(x => (x.Key.NetworkId, x.Key.TransportStreamId, x.Key.ServiceId, x.Key.EventId))
                        .ToArray();
                    var storedCollisionRows = store.GetByEventKeys(collisionKeys);
                    var exactIncomingRetained = incomingEventIdTimingCollisions.Sum(c =>
                        c.Events.Count(e => storedCollisionRows.Any(dbEvent =>
                            dbEvent.NetworkId == e.NetworkId &&
                            dbEvent.TransportStreamId == e.TransportStreamId &&
                            dbEvent.ServiceId == e.ServiceId &&
                            dbEvent.EventId == e.EventId &&
                            dbEvent.Start == e.Start &&
                            dbEvent.DurationSeconds == e.DurationSeconds)));
                    var distinctIncoming = incomingEventIdTimingCollisions.Sum(x => x.Events.Length);
                    var sample = string.Join(" | ", incomingEventIdTimingCollisions.Take(12).Select(c =>
                    {
                        var retained = storedCollisionRows
                            .Where(e => e.NetworkId == c.Key.NetworkId && e.TransportStreamId == c.Key.TransportStreamId && e.ServiceId == c.Key.ServiceId && e.EventId == c.Key.EventId)
                            .Select(e => $"{e.Start:MM-ddTHH:mm:ss}/d{e.DurationSeconds}")
                            .ToArray();
                        return $"{c.Key.NetworkId}/{c.Key.TransportStreamId}/{c.Key.ServiceId}/{c.Key.EventId}:incoming={c.Events.Length}:stored=[{string.Join(",", retained)}]";
                    }));
                    Log("EPG_EVENT_ID_TIMING_COLLISION", $"TS{group.TsId}",
                        $"result=OBSERVED phase=after_commit purpose={purpose} group={group.Group} collisionKeys={incomingEventIdTimingCollisions.Length} distinctIncomingEvents={distinctIncoming} " +
                        $"storedRows={storedCollisionRows.Count} exactIncomingRetained={exactIncomingRetained} overwrittenOrUnrepresented={Math.Max(0, distinctIncoming - exactIncomingRetained)} " +
                        $"storagePrimaryKey=nid_tsid_sid_eventId mutation=none observerOnly=True sample=[{SafeLogValue(sample)}] rule=epg_event_identity_collision_observer");
                }
#endif

                var otherSupplementInserted = 0;
                var otherSupplementExisting = 0;
                if (otherSupplementCandidates.Count > 0)
                {
                    // Re-check identity under the serialized import gate. Other-TS schedule is
                    // supplement-only: it may insert a missing event, but never overwrites an
                    // existing row and never participates in stale retirement. A later actual-TS
                    // capture remains authoritative and may update the inserted row normally.
                    var candidateKeys = otherSupplementCandidates
                        .Select(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
                        .Distinct()
                        .ToArray();
                    var existingKeys = store.GetByEventKeys(candidateKeys)
                        .Select(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
                        .ToHashSet();
                    var missingOnly = otherSupplementCandidates
                        .Where(e => !existingKeys.Contains((e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId)))
                        .ToList();
                    otherSupplementExisting = candidateKeys.Length - missingOnly.Count;
                    if (existingKeys.Count > 0)
                        store.MarkOtherScheduleSeen(existingKeys);
                    if (missingOnly.Count > 0)
                    {
                        otherSupplementInserted = store.UpsertOtherScheduleSupplement(missingOnly).Count;
                        if (otherSupplementInserted > 0)
                        {
                            otherSupplementCommittedEvents = missingOnly.Take(otherSupplementInserted).ToList();
#if TVAIR_DEVELOPER_DIAGNOSTICS
                            coverageAttribution.MarkInserted(otherSupplementCommittedEvents);
#endif
                        }
                    }

                    Log("EPG_OTHER_TS_EIT_SUPPLEMENT", $"TS{group.TsId}",
                        $"result=OK group={group.Group} candidates={otherSupplementCandidates.Count} existingAtCommit={otherSupplementExisting} insertedMissing={otherSupplementInserted} " +
                        $"identity=onid_tsid_sid_eventId serviceResolution=configured_ch2_exact existenceAuthority=0x60-0x67 enrichment=0x68-0x6F actualPriority=existing_row_preserved auxiliaryObservation=other_schedule_seen existenceVetoAgainstActualBasic=none staleRetire=none pfOther=ignored schedulerWindow=unchanged workerCeiling=unchanged " +
                        $"rule=epg_other_ts_schedule_supplement");
                }
                else if (otherEpg is not null)
                {
                    Log("EPG_OTHER_TS_EIT_SUPPLEMENT", $"TS{group.TsId}",
                        $"result=OK group={group.Group} candidates=0 existingAtCommit=0 insertedMissing=0 identity=onid_tsid_sid_eventId serviceResolution=configured_ch2_exact existenceAuthority=0x60-0x67 enrichment=0x68-0x6F actualPriority=existing_row_preserved auxiliaryObservation=other_schedule_seen existenceVetoAgainstActualBasic=none staleRetire=none pfOther=ignored schedulerWindow=unchanged workerCeiling=unchanged rule=epg_other_ts_schedule_supplement");
                }
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
            if (!isPreRecordCheck && (upsertedCount > 0 || otherSupplementCommittedEvents.Count > 0))
            {
                try
                {
                    var keywordEvents = rawEvents
                        .Concat(otherSupplementCommittedEvents)
                        .GroupBy(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
                        .Select(g => g.First())
                        .ToList();
                    var keywordAdded = keywordMatcher.RunMatching(keywordEvents);
                    var keywordReservationChanged = keywordAdded > 0 || retiredKeywordReservations > 0;
                    if (keywordReservationChanged)
                    {
                        projectionPromotion.RunAllocationRoute(
                            $"EpgCapture:{purpose}:{group.Group}:TS{group.TsId}:KeywordAuthorityConverge",
                            ReservationAllocationWakeRefreshMode.BoundedCoalesce);
                    }
                    Log("EPG_INCREMENTAL_KEYWORD_MATCH", $"TS{group.TsId}",
                        $"result=OK purpose={purpose} group={group.Group} imported={upsertedCount} otherSupplementInserted={otherSupplementCommittedEvents.Count} retiredKeyword={retiredKeywordReservations} keywordAdded={keywordAdded} allocationRun={keywordReservationChanged} timing=after_committed_ts_import_before_full_epg_completion rule=incremental_keyword_match_contract");
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
                $"authoritySids=[{string.Join(",", staleAuthoritySidSet.OrderBy(x => x))}] nonAuthorityObservedSids=[{string.Join(",", nonAuthorityObservedSids)}] scopeStart={staleScopeStart} scopeEnd={staleScopeEnd} blockReasons=[{string.Join(",", staleRetireBlockReasons)}] incompleteSectionSample=[{string.Join(",", incompleteSectionSample)}] " +
                $"policy=delete_only_overlapping_current_authoritative_basic_schedule_event authorityTables=0x50-0x57 preserveAuthority=basic_schedule_identity_only supplementaryTables=0x4E,0x58-0x5F supplementaryExistenceVeto=none otherScheduleExistenceVeto=none retiredKeyword={retiredKeywordReservations} upsertPartialCapture=allowed titleSynthesis=none bodyToTitlePromotion=none reservationTitleBorrow=none startupDbClear=none dbSchemaMutation=none rule=release_contract");

            var normalizedTitlePresentCount = rawEvents.Count(e => !string.IsNullOrWhiteSpace(e.Title));
            var normalizedTitleBlankCount = Math.Max(0, rawEvents.Count - normalizedTitlePresentCount);
            var importedSids = rawEvents.Select(e => e.ServiceId).Distinct().OrderBy(x => x).ToArray();
            Log("EPG_IMPORT", $"TS{group.TsId}",
                $"result=OK purpose={purpose} " +
                $"imported={upsertedCount} rawEvents={rawEvents.Count} otherSupplementCandidates={otherSupplementCandidates.Count} normalizedTitlePresent={normalizedTitlePresentCount} normalizedTitleBlank={normalizedTitleBlankCount} " +
                $"storageTitleColumn=descriptor_canonical_empty_by_design displayTitleSource=raw_short_event_descriptor_decoder commonEvents={epg.CommonEventCount} commonResolved={epg.CommonResolvedCount} commonUnresolved={epg.CommonUnresolvedCount} " +
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


    private static List<TsGroup> FilterGroupsForExpectedServiceIdentity(
        IReadOnlyList<TsGroup> groups,
        ushort expectedNetworkId,
        ushort expectedTransportStreamId,
        ushort expectedServiceId)
    {
        var expected = new ServiceIdentityContract.Key(expectedNetworkId, expectedTransportStreamId, expectedServiceId);
        return groups
            .Where(g => g.Targets.Any(t => ServiceIdentityContract.Matches(t, expected)))
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
                .Where(x => x.ServiceId == sid && EitTableContract.IsActualBasicSchedule(x.TableId))
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

    private static bool IsScheduleBodyTable(byte tableId) => EitTableContract.IsActualExtendedSchedule(tableId);
    private static bool HasRawShort(EpgEvent e) => !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex);
    private static bool HasRawShort(ParsedEpgEvent e) => !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex);
    private static bool HasRawShort(EpgEventObservation e) => !string.IsNullOrWhiteSpace(e.RawShortEventDescriptorHex);
    private static bool HasRawExtended(EpgEvent e) => !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex);
    private static bool HasRawExtended(ParsedEpgEvent e) => !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex);
    private static bool HasRawExtended(EpgEventObservation e) => !string.IsNullOrWhiteSpace(e.RawExtendedEventDescriptorHex);











    private static bool IsScheduleShortCarrierCandidateTable(byte tableId) => EitTableContract.IsActualBasicSchedule(tableId);









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
        int NearbyTimingAligned,
        int PreviousVersionDonorUsed,
        int TablePairMismatch,
        int MissingTitleRawShort,
        int MissingBodyRawExtended,
        int RawExtendedMerged,
        int RawExtendedAlreadyCovered,
        int BodyOnlyCurrentExactTitlePresent,
        int BodyOnlyCacheExactTitlePresent,
        int BodyOnlyCurrentAuthorityMissingCacheStored,
        int BodyOnlyCurrentAuthorityMissingCacheAbsent,
        int BodyOnlyCurrentAuthorityPresentCacheEventAbsent,
        int BodyOnlyCurrentPairedSectionPresent,
        int BodyOnlyCachePairedSectionPresent,
        int BodyOnlyPairedSectionContainsEventId,
        int BodyOnlyBasicEventIdFoundOtherSection,
        int BodyOnlyPairedSectionMissingBoth,
        int BodyOnlyPairedSectionPresentEventAbsent,
        int BodyOnlyPreviousVersionAvailable,
        int BodyOnlyPreviousBasicExactEventFound,
        int BodyOnlyPreviousBasicShortPresent,
        int BodyOnlyPreviousBasicDonorEligible,
        int BodyOnlyPreviousBasicDonorAmbiguous,
        string CacheGapSample,
        string Sample);

    private StrictTitleBodyCanonicalMergeStats ApplyStrictTitleBodyCanonicalMerge(
        TsGroup group,
        string purpose,
        IReadOnlyList<ushort> targetServiceIds,
        EpgAnalyzeResult epg,
        List<EpgEvent> rawEvents)
    {
        var observations = epg.EventObservations;
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
            EitTableContract.IsActualExtendedSchedule(bodyTable) &&
            EitTableContract.IsActualBasicSchedule(titleTable) &&
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
        var nearbyTimingAligned = 0;
        var previousVersionDonorUsed = 0;
#if TVAIR_DEVELOPER_DIAGNOSTICS
        var existingDbExactIdentityFound = 0;
        var existingDbExactTimingFound = 0;
        var existingDbDecodedTitlePresent = 0;
        var existingDbRawShortPresent = 0;
        var existingDbDonorEligible = 0;
        var existingDbDonorTimingMismatch = 0;
        var existingDbDonorSamples = new List<string>();
        var residualNoPreviousVersionSubtable = 0;
        var residualPreviousVersionNoSameEventId = 0;
        var residualPreviousSameEventIdTimingMismatch = 0;
        var residualPreviousExactTimingNoShort = 0;
        var residualPreviousShortAmbiguous = 0;
        var residualNearbyCurrentNone = 0;
        var residualNearbyCurrentAmbiguous = 0;
        var residualByDate = new Dictionary<string, int>(StringComparer.Ordinal);
        var residualReasonSamples = new List<string>();
#endif
        var tablePairMismatch = 0;
        var missingTitleRawShort = 0;
        var missingBodyRawExtended = 0;
        var rawExtendedMerged = 0;
        var rawExtendedAlreadyCovered = 0;
        var bodyOnlyCurrentExactTitlePresent = 0;
        var bodyOnlyCacheExactTitlePresent = 0;
        var bodyOnlyCurrentAuthorityMissingCacheStored = 0;
        var bodyOnlyCurrentAuthorityMissingCacheAbsent = 0;
        var bodyOnlyCurrentAuthorityPresentCacheEventAbsent = 0;
        var bodyOnlyCurrentPairedSectionPresent = 0;
        var bodyOnlyCachePairedSectionPresent = 0;
        var bodyOnlyPairedSectionContainsEventId = 0;
        var bodyOnlyBasicEventIdFoundOtherSection = 0;
        var bodyOnlyPairedSectionMissingBoth = 0;
        var bodyOnlyPairedSectionPresentEventAbsent = 0;
        var bodyOnlyPreviousVersionAvailable = 0;
        var bodyOnlyPreviousBasicExactEventFound = 0;
        var bodyOnlyPreviousBasicShortPresent = 0;
        var bodyOnlyPreviousBasicDonorEligible = 0;
        var bodyOnlyPreviousBasicDonorAmbiguous = 0;
        var cacheGapSamples = new List<string>();
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

            // EIT basic schedule (0x50-0x57) and extended schedule (0x58-0x5F) are
            // separate subtables and broadcasters may move an event's timing in one
            // table before the paired table catches up.  Do not discard a valid body
            // solely for that transient timing skew when the broadcast identity is
            // otherwise exact: same NID/TSID/SID/EID, expected paired table, one
            // unique nearby title timing, and a real raw short descriptor.  The body
            // remains timing authority; this only supplies its title descriptor.
            var titleCandidates = strictSameEvent;
            var usedNearbyTimingAlignment = false;
            var usedPreviousVersionDonor = false;
            if (titleCandidates.Count == 0)
            {
                var expectedTitleTable = (byte)(body.TableId - 0x08);
                var nearbyDistinct = titleRows
                    .Where(t =>
                        t.NetworkId == body.NetworkId &&
                        t.TransportStreamId == body.TransportStreamId &&
                        t.ServiceId == body.ServiceId &&
                        t.EventId == body.EventId &&
                        t.TableId == expectedTitleTable &&
                        HasRawShort(t) &&
                        Math.Abs((t.Start - body.Start).TotalMinutes) <= EitAlignmentNearbyTitleStartWindowMinutes)
                    .GroupBy(t => (t.Start, t.DurationSeconds, t.TableId))
                    .Select(g => g.OrderBy(t => t.SectionNumber).ThenBy(t => t.ObservationIndex).First())
                    .OrderBy(t => Math.Abs((t.Start - body.Start).TotalSeconds))
                    .ThenBy(t => t.Start)
                    .ToList();

                if (nearbyDistinct.Count == 1)
                {
                    titleCandidates = nearbyDistinct;
                    usedNearbyTimingAlignment = true;
                    nearbyTimingAligned++;
                }
                else
                {
                    // No usable Basic title exists in the current capture/same-version
                    // replay set.  A previous-version row may donate only its raw short
                    // descriptor when the full broadcast identity and timing are exact
                    // and exactly one expected Basic-table donor exists.  The current
                    // Extended row remains authority for identity, timing and body.
                    noStrictCandidate++;
                    var previousExactEvents = epg.PreviousVersionCacheEventObservations
                        .Where(t =>
                            t.NetworkId == body.NetworkId &&
                            t.TransportStreamId == body.TransportStreamId &&
                            t.ServiceId == body.ServiceId &&
                            t.EventId == body.EventId &&
                            t.Start == body.Start &&
                            t.DurationSeconds == body.DurationSeconds &&
                            t.TableId == expectedTitleTable)
                        .OrderBy(t => t.SectionNumber)
                        .ThenBy(t => t.ObservationIndex)
                        .ToList();
                    var previousShortEvents = previousExactEvents.Where(HasRawShort).ToList();

#if TVAIR_DEVELOPER_DIAGNOSTICS
                    // Keep the capture-gap attribution counters comparable even after the
                    // validated donor path becomes active.  These values describe the
                    // current-capture gap, not whether the product later rescued it.
                    var currentExactTitle = epg.CurrentCaptureEventObservations.Any(t =>
                        t.NetworkId == body.NetworkId &&
                        t.TransportStreamId == body.TransportStreamId &&
                        t.ServiceId == body.ServiceId &&
                        t.EventId == body.EventId &&
                        t.Start == body.Start &&
                        t.DurationSeconds == body.DurationSeconds &&
                        t.TableId == expectedTitleTable &&
                        HasRawShort(t));
                    var cacheExactTitle = epg.PersistentCacheEventObservations.Any(t =>
                        t.NetworkId == body.NetworkId &&
                        t.TransportStreamId == body.TransportStreamId &&
                        t.ServiceId == body.ServiceId &&
                        t.EventId == body.EventId &&
                        t.Start == body.Start &&
                        t.DurationSeconds == body.DurationSeconds &&
                        t.TableId == expectedTitleTable &&
                        HasRawShort(t));
                    var currentAuthority = epg.CurrentCaptureSubtables.FirstOrDefault(v =>
                        v.NetworkId == body.NetworkId &&
                        v.TransportStreamId == body.TransportStreamId &&
                        v.ServiceId == body.ServiceId &&
                        v.TableId == expectedTitleTable);
                    var cacheSubtable = epg.PersistentCacheSubtables.FirstOrDefault(v =>
                        v.NetworkId == body.NetworkId &&
                        v.TransportStreamId == body.TransportStreamId &&
                        v.ServiceId == body.ServiceId &&
                        v.TableId == expectedTitleTable);
                    var stored = new PersistentEitSectionCache().ProbeStoredSubtable(
                        body.NetworkId, body.TransportStreamId, body.ServiceId, expectedTitleTable);

                    if (currentExactTitle) bodyOnlyCurrentExactTitlePresent++;
                    if (cacheExactTitle) bodyOnlyCacheExactTitlePresent++;
                    if (currentAuthority is null)
                    {
                        if (stored.Exists && stored.Valid && stored.SectionCount > 0) bodyOnlyCurrentAuthorityMissingCacheStored++;
                        else bodyOnlyCurrentAuthorityMissingCacheAbsent++;
                    }
                    else if (!cacheExactTitle)
                    {
                        bodyOnlyCurrentAuthorityPresentCacheEventAbsent++;
                    }

                    var currentPairedSection = epg.CurrentCaptureSectionInventories.FirstOrDefault(section =>
                        section.NetworkId == body.NetworkId &&
                        section.TransportStreamId == body.TransportStreamId &&
                        section.ServiceId == body.ServiceId &&
                        section.TableId == expectedTitleTable &&
                        section.SectionNumber == body.SectionNumber);
                    var cachePairedSection = epg.PersistentCacheSectionInventories.FirstOrDefault(section =>
                        section.NetworkId == body.NetworkId &&
                        section.TransportStreamId == body.TransportStreamId &&
                        section.ServiceId == body.ServiceId &&
                        section.TableId == expectedTitleTable &&
                        section.SectionNumber == body.SectionNumber);
                    var currentEventElsewhere = epg.CurrentCaptureSectionInventories.Any(section =>
                        section.NetworkId == body.NetworkId &&
                        section.TransportStreamId == body.TransportStreamId &&
                        section.ServiceId == body.ServiceId &&
                        section.TableId == expectedTitleTable &&
                        section.SectionNumber != body.SectionNumber &&
                        section.EventIds.Contains(body.EventId));
                    var cacheEventElsewhere = epg.PersistentCacheSectionInventories.Any(section =>
                        section.NetworkId == body.NetworkId &&
                        section.TransportStreamId == body.TransportStreamId &&
                        section.ServiceId == body.ServiceId &&
                        section.TableId == expectedTitleTable &&
                        section.SectionNumber != body.SectionNumber &&
                        section.EventIds.Contains(body.EventId));
                    var pairedContainsEventId =
                        currentPairedSection?.EventIds.Contains(body.EventId) == true ||
                        cachePairedSection?.EventIds.Contains(body.EventId) == true;

                    if (currentPairedSection is not null) bodyOnlyCurrentPairedSectionPresent++;
                    if (cachePairedSection is not null) bodyOnlyCachePairedSectionPresent++;
                    if (pairedContainsEventId) bodyOnlyPairedSectionContainsEventId++;
                    if (currentEventElsewhere || cacheEventElsewhere) bodyOnlyBasicEventIdFoundOtherSection++;
                    if (currentPairedSection is null && cachePairedSection is null)
                        bodyOnlyPairedSectionMissingBoth++;
                    else if (!pairedContainsEventId)
                        bodyOnlyPairedSectionPresentEventAbsent++;

                    var previousVersionSubtable = epg.PreviousVersionCacheSubtables.FirstOrDefault(v =>
                        v.NetworkId == body.NetworkId &&
                        v.TransportStreamId == body.TransportStreamId &&
                        v.ServiceId == body.ServiceId &&
                        v.TableId == expectedTitleTable);
                    if (previousVersionSubtable is not null) bodyOnlyPreviousVersionAvailable++;
                    if (previousExactEvents.Count > 0) bodyOnlyPreviousBasicExactEventFound++;
                    if (previousShortEvents.Count > 0) bodyOnlyPreviousBasicShortPresent++;
                    if (previousShortEvents.Count == 1) bodyOnlyPreviousBasicDonorEligible++;
                    else if (previousShortEvents.Count > 1) bodyOnlyPreviousBasicDonorAmbiguous++;

                    if (cacheGapSamples.Count < 36)
                    {
                        cacheGapSamples.Add(
                            $"sid={body.ServiceId}:body={CompactBody(body)}:titleTable=0x{expectedTitleTable:X2}" +
                            $":currentExact={currentExactTitle}:cacheExact={cacheExactTitle}" +
                            $":currentAuthority={(currentAuthority is null ? "none" : $"v{currentAuthority.VersionNumber}")}" +
                            $":loadedCacheSubtable={(cacheSubtable is null ? "none" : $"v{cacheSubtable.VersionNumber}/sec{cacheSubtable.SectionCount}")}" +
                            $":storedCache={(stored.Exists ? (stored.Valid ? $"v{stored.VersionNumber}/sec{stored.SectionCount}" : "invalid") : "none")}" +
                            $":pairedSection=s{body.SectionNumber}:current={(currentPairedSection is null ? "missing" : $"v{currentPairedSection.VersionNumber}/events{currentPairedSection.EventIds.Count}")}" +
                            $":cache={(cachePairedSection is null ? "missing" : $"v{cachePairedSection.VersionNumber}/events{cachePairedSection.EventIds.Count}")}" +
                            $":pairedContainsEid={pairedContainsEventId}:eventElsewhere={currentEventElsewhere || cacheEventElsewhere}" +
                            $":previousVersion={(previousVersionSubtable is null ? "none" : $"currentV{previousVersionSubtable.CurrentVersionNumber}/previousV{previousVersionSubtable.PreviousVersionNumber}/sec{previousVersionSubtable.SectionCount}")}" +
                            $":previousExact={previousExactEvents.Count}:previousShort={previousShortEvents.Count}:previousDonorEligible={previousShortEvents.Count == 1}:previousDonorAmbiguous={previousShortEvents.Count > 1}");
                    }
#endif

                    if (previousShortEvents.Count == 1)
                    {
                        titleCandidates = previousShortEvents;
                        usedPreviousVersionDonor = true;
                        previousVersionDonorUsed++;
                    }
                    else
                    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
                        // Classify the remaining body-only rows without relaxing any donor rule.
                        // The categories are intentionally fail-closed and mutually exclusive so the next step can
                        // distinguish true previous-version absence from EID/timing/descriptor mismatches.
                        var previousExpectedTableSameEid = epg.PreviousVersionCacheEventObservations
                            .Where(t =>
                                t.NetworkId == body.NetworkId &&
                                t.TransportStreamId == body.TransportStreamId &&
                                t.ServiceId == body.ServiceId &&
                                t.EventId == body.EventId &&
                                t.TableId == expectedTitleTable)
                            .OrderBy(t => t.Start)
                            .ThenBy(t => t.DurationSeconds)
                            .ThenBy(t => t.SectionNumber)
                            .ThenBy(t => t.ObservationIndex)
                            .ToList();
                        var previousVersionSubtableForResidual = epg.PreviousVersionCacheSubtables.FirstOrDefault(v =>
                            v.NetworkId == body.NetworkId &&
                            v.TransportStreamId == body.TransportStreamId &&
                            v.ServiceId == body.ServiceId &&
                            v.TableId == expectedTitleTable);

                        string residualReason;
                        if (previousVersionSubtableForResidual is null)
                        {
                            residualNoPreviousVersionSubtable++;
                            residualReason = "no_previous_version_subtable";
                        }
                        else if (previousExpectedTableSameEid.Count == 0)
                        {
                            residualPreviousVersionNoSameEventId++;
                            residualReason = "previous_version_no_same_eid";
                        }
                        else if (previousExactEvents.Count == 0)
                        {
                            residualPreviousSameEventIdTimingMismatch++;
                            residualReason = "previous_same_eid_timing_mismatch";
                        }
                        else if (previousShortEvents.Count == 0)
                        {
                            residualPreviousExactTimingNoShort++;
                            residualReason = "previous_exact_timing_no_short";
                        }
                        else
                        {
                            residualPreviousShortAmbiguous++;
                            residualReason = "previous_short_ambiguous";
                        }

                        if (nearbyDistinct.Count == 0) residualNearbyCurrentNone++;
                        else residualNearbyCurrentAmbiguous++;

                        var residualDateKey = body.Start.ToString("yyyy-MM-dd");
                        residualByDate[residualDateKey] = residualByDate.TryGetValue(residualDateKey, out var residualDateCount)
                            ? residualDateCount + 1
                            : 1;

                        if (residualReasonSamples.Count < 36)
                        {
                            var previousTimingSample = string.Join(',', previousExpectedTableSameEid.Take(4).Select(t => $"{t.Start:MM-ddTHH:mm:ss}/d{t.DurationSeconds}/short{HasRawShort(t)}"));
                            residualReasonSamples.Add(
                                $"{body.NetworkId}/{body.TransportStreamId}/{body.ServiceId}/{body.EventId}" +
                                $":body={body.Start:MM-ddTHH:mm:ss}/d{body.DurationSeconds}:table=0x{expectedTitleTable:X2}" +
                                $":nearbyCurrent={nearbyDistinct.Count}:previousSubtable={(previousVersionSubtableForResidual is null ? "none" : $"v{previousVersionSubtableForResidual.PreviousVersionNumber}")}" +
                                $":previousSameEid={previousExpectedTableSameEid.Count}:previousExact={previousExactEvents.Count}:previousShort={previousShortEvents.Count}" +
                                $":reason={residualReason}:previousTiming=[{previousTimingSample}]");
                        }

                        // Existing-DB donor evidence is retained as diagnostics only for regression
                        // comparison. It remains non-authoritative and is never used as a donor.
                        var existingDb = store.GetOne(body.NetworkId, body.TransportStreamId, body.ServiceId, body.EventId);
                        if (existingDb is not null)
                        {
                            existingDbExactIdentityFound++;
                            var exactTiming = existingDb.Start == body.Start && existingDb.DurationSeconds == body.DurationSeconds;
                            if (exactTiming)
                            {
                                existingDbExactTimingFound++;
                                var existingDbDecodedTitle = ProgramGuideCellTextDecoder.Decode(existingDb).Title;
                                if (!string.IsNullOrWhiteSpace(existingDbDecodedTitle)) existingDbDecodedTitlePresent++;
                                if (HasRawShort(existingDb)) existingDbRawShortPresent++;
                                if (!string.IsNullOrWhiteSpace(existingDbDecodedTitle) && HasRawShort(existingDb))
                                    existingDbDonorEligible++;
                            }
                            else
                            {
                                existingDbDonorTimingMismatch++;
                            }

                            if (existingDbDonorSamples.Count < 24)
                            {
                                existingDbDonorSamples.Add(
                                    $"{body.NetworkId}/{body.TransportStreamId}/{body.ServiceId}/{body.EventId}" +
                                    $":body={body.Start:MM-ddTHH:mm:ss}/d{body.DurationSeconds}" +
                                    $":db={existingDb.Start:MM-ddTHH:mm:ss}/d{existingDb.DurationSeconds}" +
                                    $":exactTiming={exactTiming}:decodedTitlePresent={!string.IsNullOrWhiteSpace(ProgramGuideCellTextDecoder.Decode(existingDb).Title)}:rawShortPresent={HasRawShort(existingDb)}");
                            }
                        }
#endif
                        bodiesToRemove.Add(body);
                        if (samples.Count < 36)
                            samples.Add($"sid={body.ServiceId}:body={CompactBody(body)}:merged=False:removed=True:reason=no_current_or_previous_exact_title:nearbyDistinct={nearbyDistinct.Count}:previousShort={previousShortEvents.Count}");
                        continue;
                    }
                }
            }

            var rawShortStrict = titleCandidates.Where(HasRawShort).ToList();
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
                .Where(e => ExpectedTablePair(e.TableId, body.TableId))
                .Where(e => usedNearbyTimingAlignment
                    ? e.NetworkId == title.NetworkId &&
                      e.TransportStreamId == title.TransportStreamId &&
                      e.ServiceId == title.ServiceId &&
                      e.EventId == title.EventId &&
                      e.Start == title.Start &&
                      e.DurationSeconds == title.DurationSeconds
                    : SameRawEventTime(e, body))
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
                : CreateCanonicalTitleBodyEvent(title, body, shortDescriptorOnlyDonor: usedPreviousVersionDonor);

            if (usedNearbyTimingAlignment && existingTitleRows.Count == 1)
            {
                // The paired extended table carries the newer timing.  Move the
                // existing title row to that timing instead of adding a second row
                // with the same broadcast EID; the DB event key remains collision-free.
                canonical.Start = body.Start;
                canonical.End = body.End;
                canonical.DurationSeconds = body.DurationSeconds;
            }

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
                    $":timingAlignment={(usedNearbyTimingAlignment ? "nearby_same_event_id" : "exact")}" +
                    $":titleSource={(usedPreviousVersionDonor ? "previous_version_exact_short_donor" : "current_or_same_version")}" +
                    $":rawExtendedBytes={HexSequenceByteLength(body.RawExtendedEventDescriptorHex)}");
            }
        }

        foreach (var body in bodiesToRemove)
        {
            if (rawEvents.Remove(body))
                removedBodyRows++;
        }

#if TVAIR_DEVELOPER_DIAGNOSTICS
        Log("EIT_BODY_ONLY_RESIDUAL_REASON_OBSERVER", $"TS{group.TsId}",
            $"result=OBSERVED purpose={purpose} group={group.Group} tsid={group.TsId} targetSids=[{string.Join(",", targetServiceIds)}] " +
            $"candidateScope=no_current_or_previous_exact_short total={residualNoPreviousVersionSubtable + residualPreviousVersionNoSameEventId + residualPreviousSameEventIdTimingMismatch + residualPreviousExactTimingNoShort + residualPreviousShortAmbiguous} " +
            $"noPreviousVersionSubtable={residualNoPreviousVersionSubtable} previousVersionNoSameEventId={residualPreviousVersionNoSameEventId} " +
            $"previousSameEventIdTimingMismatch={residualPreviousSameEventIdTimingMismatch} previousExactTimingNoShort={residualPreviousExactTimingNoShort} previousShortAmbiguous={residualPreviousShortAmbiguous} " +
            $"nearbyCurrentNone={residualNearbyCurrentNone} nearbyCurrentAmbiguous={residualNearbyCurrentAmbiguous} " +
            $"byDate=[{string.Join(',', residualByDate.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"))}] " +
            $"mutation=none donorRule=unchanged dbWrite=none projectionWrite=none schedulerWindow=unchanged workerCeiling=unchanged " +
            $"sample={SafeLogValue(TrimLog(string.Join('|', residualReasonSamples), 7600))} rule=epg_body_only_residual_reason_observer");

        Log("EIT_BODY_ONLY_EXISTING_DB_TITLE_DONOR_OBSERVER", $"TS{group.TsId}",
            $"result=OBSERVED purpose={purpose} group={group.Group} tsid={group.TsId} targetSids=[{string.Join(",", targetServiceIds)}] " +
            $"candidateScope=no_current_or_previous_exact_title exactIdentityFound={existingDbExactIdentityFound} exactTimingFound={existingDbExactTimingFound} " +
            $"decodedTitlePresent={existingDbDecodedTitlePresent} rawShortPresent={existingDbRawShortPresent} donorEligible={existingDbDonorEligible} timingMismatch={existingDbDonorTimingMismatch} " +
            $"identity=nid_tsid_sid_eventId timing=start_duration donorRequirement=exact_identity_and_exact_timing_and_decodable_raw_short mutation=none dbWrite=none projectionWrite=none schedulerWindow=unchanged workerCeiling=unchanged " +
            $"sample={SafeLogValue(TrimLog(string.Join('|', existingDbDonorSamples), 7600))} rule=epg_existing_db_exact_short_donor_observer");
#endif

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
            nearbyTimingAligned,
            previousVersionDonorUsed,
            tablePairMismatch,
            missingTitleRawShort,
            missingBodyRawExtended,
            rawExtendedMerged,
            rawExtendedAlreadyCovered,
            bodyOnlyCurrentExactTitlePresent,
            bodyOnlyCacheExactTitlePresent,
            bodyOnlyCurrentAuthorityMissingCacheStored,
            bodyOnlyCurrentAuthorityMissingCacheAbsent,
            bodyOnlyCurrentAuthorityPresentCacheEventAbsent,
            bodyOnlyCurrentPairedSectionPresent,
            bodyOnlyCachePairedSectionPresent,
            bodyOnlyPairedSectionContainsEventId,
            bodyOnlyBasicEventIdFoundOtherSection,
            bodyOnlyPairedSectionMissingBoth,
            bodyOnlyPairedSectionPresentEventAbsent,
            bodyOnlyPreviousVersionAvailable,
            bodyOnlyPreviousBasicExactEventFound,
            bodyOnlyPreviousBasicShortPresent,
            bodyOnlyPreviousBasicDonorEligible,
            bodyOnlyPreviousBasicDonorAmbiguous,
            TrimLog(string.Join('|', cacheGapSamples), 7600),
            TrimLog(string.Join('|', samples), 7600));
    }

    private static EpgEvent CreateCanonicalTitleBodyEvent(
        EpgEventObservation title,
        EpgEvent body,
        bool shortDescriptorOnlyDonor = false)
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
            // A previous-version donor contributes only raw short_event_descriptor.
            // Preserve the current Extended row's table/section/version and all other
            // descriptors so stale previous-version metadata cannot become authority.
            TableId = shortDescriptorOnlyDonor ? body.TableId : title.TableId,
            SectionNumber = shortDescriptorOnlyDonor ? body.SectionNumber : title.SectionNumber,
            VersionNumber = shortDescriptorOnlyDonor ? body.VersionNumber : title.VersionNumber,
            RawDescriptorLoopHex = shortDescriptorOnlyDonor
                ? body.RawDescriptorLoopHex
                : MergeRawHex(title.RawDescriptorLoopHex, body.RawDescriptorLoopHex),
            RawShortEventDescriptorHex = title.RawShortEventDescriptorHex,
            RawExtendedEventDescriptorHex = body.RawExtendedEventDescriptorHex,
            RawContentDescriptorHex = shortDescriptorOnlyDonor
                ? body.RawContentDescriptorHex
                : MergeRawHex(title.RawContentDescriptorHex, body.RawContentDescriptorHex),
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
            $"ambiguousStrictMultiple={stats.AmbiguousStrictMultiple} noStrictCandidate={stats.NoStrictCandidate} nearbyTimingAligned={stats.NearbyTimingAligned} previousVersionDonorUsed={stats.PreviousVersionDonorUsed} tablePairMismatch={stats.TablePairMismatch} missingTitleRawShort={stats.MissingTitleRawShort} missingBodyRawExtended={stats.MissingBodyRawExtended} " +
            $"rawExtendedMerged={stats.RawExtendedMerged} rawExtendedAlreadyCovered={stats.RawExtendedAlreadyCovered} residualStrictCandidate=0 dominantCause={dominantCause} " +
            $"policy=current_exact_then_unique_nearby_then_previous_version_exact_short_donor expectedTablePair=True previousDonorIdentity=nid_tsid_sid_eid_start_duration previousDonorAmbiguous=reject nearbyWindowMinutes={EitAlignmentNearbyTitleStartWindowMinutes} action=pre_db_title_body_canonical_merge dbPostFix=none renderMutation=none titleSynthesis=none bodyToTitlePromotion=none existingDbTitleBurnIn=none sample={SafeLogValue(stats.Sample)} " +
            $"rule=release_contract");

#if TVAIR_DEVELOPER_DIAGNOSTICS
        Log("EIT_BODY_ONLY_BASIC_SECTION_CACHE_ATTRIBUTION", $"TS{group.TsId}",
            $"result=OBSERVED purpose={purpose} group={group.Group} tsid={group.TsId} targetSids=[{string.Join(",", targetServiceIds)}] " +
            $"bodyOnlyNoStrict={stats.NoStrictCandidate} currentExactTitlePresent={stats.BodyOnlyCurrentExactTitlePresent} cacheExactTitlePresent={stats.BodyOnlyCacheExactTitlePresent} " +
            $"currentAuthorityMissingCacheStored={stats.BodyOnlyCurrentAuthorityMissingCacheStored} currentAuthorityMissingCacheAbsent={stats.BodyOnlyCurrentAuthorityMissingCacheAbsent} " +
            $"currentAuthorityPresentCacheEventAbsent={stats.BodyOnlyCurrentAuthorityPresentCacheEventAbsent} " +
            $"currentPairedSectionPresent={stats.BodyOnlyCurrentPairedSectionPresent} cachePairedSectionPresent={stats.BodyOnlyCachePairedSectionPresent} " +
            $"pairedSectionContainsEventId={stats.BodyOnlyPairedSectionContainsEventId} basicEventIdFoundOtherSection={stats.BodyOnlyBasicEventIdFoundOtherSection} " +
            $"pairedSectionMissingBoth={stats.BodyOnlyPairedSectionMissingBoth} pairedSectionPresentEventAbsent={stats.BodyOnlyPairedSectionPresentEventAbsent} " +
            $"previousVersionAvailable={stats.BodyOnlyPreviousVersionAvailable} previousBasicExactEventFound={stats.BodyOnlyPreviousBasicExactEventFound} " +
            $"previousBasicShortPresent={stats.BodyOnlyPreviousBasicShortPresent} previousBasicDonorEligible={stats.BodyOnlyPreviousBasicDonorEligible} previousBasicDonorAmbiguous={stats.BodyOnlyPreviousBasicDonorAmbiguous} " +
            $"previousVersionDonorUsed={stats.PreviousVersionDonorUsed} interpretation=previous_version_exact_short_descriptor_donor_enabled_only_when_current_strict_and_nearby_title_are_unavailable_and_exact_nid_tsid_sid_eid_start_duration_expected_basic_table_has_one_short_descriptor;paired_section_missing_both_means_section_coverage_gap_present_event_absent_means_basic_extended_content_asymmetry_event_elsewhere_means_section_relocation_or_segment_skew " +
            $"dbWrite=canonical_merge_only schedulerWindow=unchanged workerCeiling=unchanged sample={SafeLogValue(stats.CacheGapSample)} rule=epg_body_only_basic_section_cache_attribution");
#endif
    }



    private static List<EpgEvent> FilterCurrentOrFutureImportEvents(IEnumerable<EpgEvent> events, DateTime cutoff)
        => events
            .Where(e => e.End > cutoff)
            .ToList();

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

    internal List<TsGroup> BuildGroups(
        IReadOnlyList<ChannelTarget> targets,
        IReadOnlyList<ChannelServiceState>? epgServiceStates = null,
        bool emitDiagnostics = true)
    {
        // release_contract: 通常EPGは「局単位」ではなく .ch2 由来の同一TS束で回す。
        // 物理選局はTVTestでactiveかつChSet解決済みのChannelTargetを正本とする一方、
        // 通常EPGのサービス収集範囲は同一NID/TSIDの.ch2記載サービス全体とする。
        // これにより、TVTestでOFFのサブサービスもEPGへ蓄積するが、検索・予約・録画の
        // active判定には一切昇格させない。PreRecはepgServiceStatesを渡さず従来どおりactive対象のみ。
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
                var activeTargets = g
                    .GroupBy(t => t.ServiceId)
                    .Select(x => x.OrderBy(t => t.Ch2LineNumber).First())
                    .OrderBy(t => t.Ch2LineNumber)
                    .ToList();

                // The first entry must remain an active/routable service because it owns the physical tune.
                // Same-TS .ch2 service rows may be attached here as planning metadata, but this list is not
                // authoritative for normal-EPG import. ParseAndStoreAsync expands canonical import from the
                // actual service identities observed in the captured TS.
                var targetList = new List<ChannelTarget>(activeTargets);
                var routeVariantCount = targets
                    .Where(t => t.OriginalNetworkId == g.Key.OriginalNetworkId
                                && t.TransportStreamId == g.Key.TransportStreamId)
                    .Select(t => (t.ResolvedSpace, t.ResolvedChannelIndex))
                    .Distinct()
                    .Count();
                var canExpandAllServices = epgServiceStates is not null && activeTargets.Count > 0 && routeVariantCount == 1;
                if (canExpandAllServices)
                {
                    var representative = activeTargets[0];
                    var presentSids = targetList.Select(t => t.ServiceId).ToHashSet();
                    var allSameTsStates = epgServiceStates!
                        .Where(s => s.OriginalNetworkId == g.Key.OriginalNetworkId
                                    && s.TransportStreamId == g.Key.TransportStreamId)
                        .GroupBy(s => s.ServiceId)
                        .Select(x => x.OrderBy(s => s.Ch2LineNumber).First())
                        .OrderBy(s => s.Ch2LineNumber)
                        .ToList();

                    foreach (var state in allSameTsStates)
                    {
                        if (!presentSids.Add(state.ServiceId)) continue;
                        targetList.Add(new ChannelTarget
                        {
                            Group = representative.Group,
                            ServiceId = state.ServiceId,
                            OriginalNetworkId = state.OriginalNetworkId,
                            TransportStreamId = state.TransportStreamId,
                            Name = state.Name,
                            ChannelArgument = BuildEpgTransportRouteArgument(representative),
                            Ch2FileName = state.Ch2FileName,
                            Ch2LineNumber = state.Ch2LineNumber,
                            BonDriverChannel = representative.BonDriverChannel,
                            ResolvedSpace = representative.ResolvedSpace,
                            ResolvedChannelIndex = representative.ResolvedChannelIndex,
                            SameTransportServiceCount = allSameTsStates.Count,
                            ChannelBuildSource = "epg_same_transport_service_identity"
                        });
                    }
                }

                var ch2ActiveSameTsServices = activeTargets.Count == 0
                    ? 0
                    : activeTargets.Max(t => t.SameTransportServiceCount);
                var epgConfiguredSameTsServices = targetList.Count;
                var bundleStatus = epgServiceStates is null
                    ? (ch2ActiveSameTsServices > activeTargets.Count ? "WARN_ACTIVE_CH2_SERVICE_SUBSET" : "OK_ACTIVE_CH2_SERVICE_COVERED")
                    : routeVariantCount > 1
                        ? "WARN_AMBIGUOUS_TS_ROUTE_FOR_ALL_SERVICE_EPG"
                        : "OK_ALL_CH2_SERVICES_COVERED";
                var targetSids = string.Join(",", targetList.Select(t => t.ServiceId).OrderBy(x => x));
                var ch2Lines = string.Join(",", targetList.Select(t => t.Ch2LineNumber).OrderBy(x => x));
                if (emitDiagnostics)
                {
                    Log("EPG_CH2_TS_SCOPE", $"TS{g.Key.TransportStreamId}",
                        $"result={bundleStatus} group={g.Key.Group} nid={g.Key.OriginalNetworkId} tsid={g.Key.TransportStreamId} chspace={g.Key.ResolvedSpace} chi={g.Key.ResolvedChannelIndex} " +
                        $"ch2ActiveSameTsServices={ch2ActiveSameTsServices} epgConfiguredSameTsServices={epgConfiguredSameTsServices} epgTargetSidCount={targetList.Count} targetSids=[{targetSids}] ch2Lines=[{ch2Lines}] routeVariants={routeVariantCount} " +
                        $"serviceActivationAffectsPhysicalTune=true serviceActivationAffectsNormalEpgImport=false commonRoute=ALLOC_ROUTE/TUNER_ALLOC note=physical_route_scope_plus_ch2_metadata_actual_import_expands_from_observed_ts rule=release_contract");
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


    private static string BuildEpgTransportRouteArgument(ChannelTarget representative)
    {
        // EPGの追加サービスidentityは物理TSを共有するだけで、サービスactive状態を変更しない。
        // /sid は視聴・録画サービス選択の意味を持つため、EPG用の合成identityには持ち込まない。
        return NormalizeGroup(representative.Group) == "GR"
            ? $"/ch {representative.ResolvedChannelIndex}"
            : $"/chspace {representative.ResolvedSpace} /chi {representative.ResolvedChannelIndex}";
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
        // 同じgateを自己再取得しない。gate外の呼出元は物理device gateを取得してからkillする。
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

    [System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
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
            // attempt境界でmonitorを切り替え、別attemptの資源へ触れないようにする。
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

internal enum PreRecordProbeTerminalReason
{
    ObservationOnly = 0,
    TargetObserved = 1,
    DeadlineReachedTargetNotObserved = 2,
    WorkerExitedBeforeDecision = 3,
    AttemptSuperseded = 4
}

internal sealed record PreRecordProbeResult(bool TargetFound, IReadOnlyList<EpgEvent> Events, string EventSummary)
{
    public PreRecordProbeTerminalReason TerminalReason { get; init; } = PreRecordProbeTerminalReason.ObservationOnly;
}

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
