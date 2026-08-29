/*
 * ============================================================================
 * 【開発者の明示承認なしに変更禁止】共通割り当てルート保護契約
 *
 * 共通割り当てルート、割り当て順序、single-flight、commit、Mutation／Wake投影を
 * 変更する場合は、理由や規模を問わず、着手前に必ず開発者の明示承認を得ること。
 * 不具合修正・整理・最適化・リファクタリング名目での無断変更も禁止する。
 * この保護コメント自体も、開発者の明示承認なしに改変・削除・弱体化してはならない。
 *
 * 録画実行の正本はTvAIrEpgRec／DirectRecorder系とし、割当経路から別の録画起動・待機・補正経路を持たない。
 *
 * タイマー、固定wait、sleep、delay、cooldown、settle、quiet window等を
 * 安易に挟んではならない。完了待ちは現行の状態、物理解放、worker終了などの
 * 証拠で判定し、時間経過で不整合を隠さないこと。
 *
 * DBに余計な小細工を一切してはならない。テーブル、カラム、Trigger、Index、
 * PRAGMA、別DB、一時DB、shadow table、補正値、隠し状態、二重保存、移行処理を
 * 追加・変更する場合は、内容の大小を問わず、着手前に必ず開発者の明示承認を得ること。
 * ============================================================================
 */
/* release_contract STOP_PHASE_ALLOC_ROUTE_GUARD: 録画停止フェーズ中のALLOC_ROUTEはStopPhaseGateで遅延扱いにする。 */
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Tuner;
using TvAIr.Plugin;

namespace TvAIr.Schedule;

public enum ReservationAllocationWakeRefreshMode
{
    Immediate,
    BoundedCoalesce
}

public sealed record ReservationAllocationRouteRequest(
    string Source,
    string Action,
    bool RunKeywordMatcher = false,
    bool SyncProgramRuleReservations = true,
    bool ReevaluateAllocations = true,
    bool RefreshPreRecordEpgEntries = false,
    bool RefreshWakeTask = false,
    bool BypassStopPhaseGate = false,
    bool EmitConflictLogs = false,
    string ConflictLogCategory = "ReservationRoute",
    string ConflictLogTitle = "Conflict",
    string ExecutionMode = "Normal",
    ReservationAllocationWakeRefreshMode WakeRefreshMode = ReservationAllocationWakeRefreshMode.Immediate);

public sealed record ReservationAllocationRouteResult(
    int ChangedCount,
    int ConflictOnCount,
    int ConflictOffCount,
    bool Deferred,
    long AllocationGeneration = 0,
    bool RetryRequired = false,
    string Reason = "applied");

public sealed class ReservationAllocationRouteService
{
    private sealed class FlightWaiter
    {
        public readonly ManualResetEventSlim Signal = new(false);
        public ReservationAllocationRouteResult? Result;
        public Exception? Error;
    }

    private sealed record CycleResult(
        ReservationAllocationRouteResult Result,
        IReadOnlyList<(int Id, string ServiceName, string Title, bool Conflicted)> Changes,
        IReadOnlyList<AllocationCommittedRowChange> RowChanges,
        bool AllocationAttempted);

    private sealed record AggregateConflictChange(
        int Id,
        string ServiceName,
        string Title,
        bool InitialConflicted,
        bool FinalConflicted);


    private sealed record AggregateAllocationRowChange(
        int Id,
        string ServiceName,
        string Title,
        string InitialTunerName,
        string FinalTunerName,
        bool InitialConflicted,
        bool FinalConflicted,
        long InitialDataVersion,
        long FinalDataVersion);
    private readonly ReservationStore _store;
    private readonly IniSettingsService _ini;
    private readonly IReadOnlyList<TunerProfile> _tunerProfiles;
    private readonly TunerPool _tunerPool;
    private readonly LogRepository _log;
    private readonly IServiceProvider _serviceProvider;

    private readonly object _singleFlightGate = new();
    private bool _singleFlightRunning;
    private int _singleFlightOwnerThreadId;
    // single-flight所有権の世代。owner解放直後に別Runが開始しても、旧ownerの異常終端guardが
    // 新ownerを巻き込まないため、取得単位で単調に更新する。
    private long _singleFlightOwnerEpoch;
    private ReservationAllocationRouteRequest? _pendingRequest;
    private readonly List<FlightWaiter> _pendingWaiters = new();

    // ALLOCATION_DERIVED_PROJECTION_QUEUE_INVARIANT:
    // Reservation/Tunerの正本commitとsingle-flight解放を、型付きイベント・ユーザー投影・Wake派生の
    // 逐次配送完了まで待たせない。派生処理はcommit後にこの直列Task tailへ積み、世代検証付きで
    // 順番に投影する。これにより多数のTunerName変更があっても、物理解放済みチューナーを使う
    // 次の再割当と競合解除を外部subscriber速度で数十秒塞がない。
    // 並列Task化、固定wait/sleep、イベント欠落、DBの二重commitへ戻してはならない。
    private readonly object _derivedProjectionQueueGate = new();
    private Task _derivedProjectionTail = Task.CompletedTask;

    public ReservationAllocationRouteService(
        ReservationStore store,
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> tunerProfiles,
        TunerPool tunerPool,
        LogRepository log,
        IServiceProvider serviceProvider)
    {
        _store = store;
        _ini = ini;
        _tunerProfiles = tunerProfiles;
        _tunerPool = tunerPool;
        _log = log;
        _serviceProvider = serviceProvider;
    }

    public ReservationAllocationRouteResult Run(ReservationAllocationRouteRequest request, bool waitForActiveSingleFlight = true)
    {
        var requestedAt = DateTime.Now;

        if (!request.BypassStopPhaseGate
            && StopPhaseGate.TryDeferAllocRoute(request,
                msg => _log.Add("ALLOC_ROUTE", "StopPhase", msg)))
        {
            _log.Add("ALLOC_ROUTE", "Deferred",
                $"source={request.Source} action={request.Action} reason=recording_stop_phase_active elapsedMs={(int)(DateTime.Now - requestedAt).TotalMilliseconds}");
            return new ReservationAllocationRouteResult(0, 0, 0, Deferred: true, Reason: "recording_stop_phase_active");
        }

        var currentThreadId = Environment.CurrentManagedThreadId;
        var waiter = new FlightWaiter();
        var isLeader = false;
        long ownerEpoch = 0;

        lock (_singleFlightGate)
        {
            if (!_singleFlightRunning)
            {
                _singleFlightRunning = true;
                _singleFlightOwnerThreadId = currentThreadId;
                ownerEpoch = unchecked(++_singleFlightOwnerEpoch);
                isLeader = true;
            }
            else
            {
                _pendingRequest = _pendingRequest is null ? request : MergeRequests(_pendingRequest, request);

                // Typed eventなど、先行Runと同一スレッドからの再入は待機するとデッドロックする。
                // 要求は次周回へ統合済みなので、呼出元にはDeferredとして即時返す。
                if (_singleFlightOwnerThreadId == currentThreadId)
                {
                    _log.Add("ALLOC_ROUTE", "MergedReentrant",
                        $"source={request.Source} action={request.Action} result=DEFERRED reason=merged_into_active_single_flight rule=release_contract");
                    return new ReservationAllocationRouteResult(0, 0, 0, Deferred: true, Reason: "merged_into_active_single_flight");
                }

                if (!waitForActiveSingleFlight)
                {
                    _log.Add("ALLOC_ROUTE", "MergedNoWait",
                        $"source={request.Source} action={request.Action} result=DEFERRED reason=active_single_flight_no_wait rule=release_contract");
                    return new ReservationAllocationRouteResult(0, 0, 0, Deferred: true, Reason: "active_single_flight_no_wait");
                }

                _pendingWaiters.Add(waiter);
                _log.Add("ALLOC_ROUTE", "Merged",
                    $"source={request.Source} action={request.Action} result=WAIT reason=active_single_flight pendingWaiters={_pendingWaiters.Count} rule=release_contract");
            }
        }

        if (!isLeader)
        {
            waiter.Signal.Wait();
            waiter.Signal.Dispose();
            if (waiter.Error is not null) throw new InvalidOperationException("ALLOC_ROUTE single-flight failed.", waiter.Error);
            return waiter.Result ?? new ReservationAllocationRouteResult(0, 0, 0, Deferred: true, Reason: "single_flight_result_missing");
        }

        return RunAsSingleFlightLeader(request, requestedAt, ownerEpoch);
    }

    private ReservationAllocationRouteResult RunAsSingleFlightLeader(
        ReservationAllocationRouteRequest firstRequest,
        DateTime requestedAt,
        long ownerEpoch)
    {
        try
        {
            return RunAsSingleFlightLeaderOwned(firstRequest, requestedAt, ownerEpoch);
        }
        catch (Exception ex)
        {
            var released = false;
            var waiterCount = 0;
            lock (_singleFlightGate)
            {
                // BeginTypedEventOutboxScope / Resolve等、従来の内側tryへ入る前の例外でも
                // ownerを必ず終端する。ただし、このownerが既に正常/異常解放され別ownerへ
                // 世代交代している場合は絶対に触らない。
                if (_singleFlightRunning && _singleFlightOwnerEpoch == ownerEpoch)
                {
                    waiterCount = _pendingWaiters.Count;
                    _singleFlightRunning = false;
                    _singleFlightOwnerThreadId = 0;
                    _pendingRequest = null;
                    CompleteWaitersLocked(null, ex);
                    released = true;
                }
            }

            if (released)
            {
                _log.Add("ALLOC_ROUTE", "SingleFlightOwnerTerminal",
                    $"result=RELEASED ownerEpoch={ownerEpoch} pendingWaiters={waiterCount} errorType={ex.GetType().Name} reason=leader_initialization_or_outer_scope_failure rule=single_flight_owner_terminal_contract");
            }
            throw;
        }
    }

    private ReservationAllocationRouteResult RunAsSingleFlightLeaderOwned(
        ReservationAllocationRouteRequest firstRequest,
        DateTime requestedAt,
        long ownerEpoch)
    {
        using var eventScope = BeginTypedEventOutboxScope(out var commitEvents);
        var currentRequest = firstRequest;
        var aggregateRequest = firstRequest;
        var mutationJournal = Resolve<ReservationMutationJournal>();
        CycleResult? finalCycle = null;
        ReservationAllocationRouteResult? latestAllocationResult = null;
        var aggregateConflictChanges = new Dictionary<int, AggregateConflictChange>();
        var aggregateAllocationRowChanges = new Dictionary<int, AggregateAllocationRowChange>();

        try
        {
            while (true)
            {
                finalCycle = ExecuteCoreCycle(currentRequest);
                if (finalCycle.AllocationAttempted)
                    latestAllocationResult = finalCycle.Result;
                AccumulateConflictChanges(aggregateConflictChanges, finalCycle.Changes);
                AccumulateAllocationRowChanges(aggregateAllocationRowChanges, finalCycle.RowChanges);

                ReservationAllocationRouteRequest? nextRequest;
                lock (_singleFlightGate)
                {
                    nextRequest = _pendingRequest;
                    _pendingRequest = null;
                }

                if (nextRequest is not null)
                {
                    aggregateRequest = MergeRequests(aggregateRequest, nextRequest);
                    currentRequest = nextRequest;
                    _log.Add("ALLOC_ROUTE", "SingleFlightNext",
                        $"source={aggregateRequest.Source} action={aggregateRequest.Action} reason=pending_request_merged allocationGeneration={finalCycle.Result.AllocationGeneration} retryRequired={finalCycle.Result.RetryRequired} rule=release_contract");
                    continue;
                }

                // このsingle-flight batchで待機要求が尽きた時点の純差分を確定する。
                // DB/Tuner正本はExecuteCoreCycle内ですでにcommit済み。外部subscriberやWake派生の
                // 配送時間をsingle-flightの所有時間へ含めず、競合解除を次の再評価から即時参照可能にする。
                var effectiveCycle = BuildEffectiveFinalCycle(finalCycle, latestAllocationResult, aggregateConflictChanges, aggregateAllocationRowChanges);

                lock (_singleFlightGate)
                {
                    // 正本確定までに到着した要求だけを同じbatchへ統合する。
                    // 派生投影開始後の要求は、新しいsingle-flightとして最新DBを再評価する。
                    if (_pendingRequest is not null)
                    {
                        currentRequest = _pendingRequest;
                        aggregateRequest = currentRequest;
                        _pendingRequest = null;
                        latestAllocationResult = null;
                        aggregateConflictChanges.Clear();
                        aggregateAllocationRowChanges.Clear();
                        continue;
                    }

                    commitEvents();
                    _log.Add("PLUGIN_TYPED_EVENT_OUTBOX", "AllocationRoute", $"result=COMMITTED operation=AllocationSingleFlight source={aggregateRequest.Source} action={aggregateRequest.Action} allocationGeneration={effectiveCycle.Result.AllocationGeneration} rule=typed_event_outbox");
                    _singleFlightRunning = false;
                    _singleFlightOwnerThreadId = 0;
                    CompleteWaitersLocked(effectiveCycle.Result, null);
                }

                QueueFinalDerivedEffects(aggregateRequest, effectiveCycle, mutationJournal);

                var elapsedMs = (int)(DateTime.Now - requestedAt).TotalMilliseconds;
                _log.Add("ALLOC_ROUTE", "SingleFlightComplete",
                    $"source={aggregateRequest.Source} action={aggregateRequest.Action} changed={effectiveCycle.Result.ChangedCount} allocationGeneration={effectiveCycle.Result.AllocationGeneration} retryRequired={effectiveCycle.Result.RetryRequired} reason={effectiveCycle.Result.Reason} elapsedMs={elapsedMs} rule=release_contract");
                return effectiveCycle.Result;
            }
        }
        catch (Exception ex)
        {
            lock (_singleFlightGate)
            {
                // 正常解放直後の派生enqueue等で例外になり、既に次ownerへ世代交代していても
                // catchした世代が現在のownerである場合だけ終端し、後続ownerの所有権には触れない。
                if (_singleFlightRunning && _singleFlightOwnerEpoch == ownerEpoch)
                {
                    _singleFlightRunning = false;
                    _singleFlightOwnerThreadId = 0;
                    _pendingRequest = null;
                    CompleteWaitersLocked(null, ex);
                }
            }
            throw;
        }
    }

    private IDisposable BeginTypedEventOutboxScope(out Action commit)
    {
        var typedEvents = Resolve<PluginTypedEventHub>();
        if (typedEvents is not null)
            return typedEvents.BeginOutboxScope(out commit);

        commit = static () => { };
        return NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    private CycleResult ExecuteCoreCycle(ReservationAllocationRouteRequest request)
    {
        var startedAt = DateTime.Now;
        var tunerStatusBefore = _tunerPool.GetStatusSummary();
        _log.Add("ALLOC_TRACE", "Enter",
            $"[ALLOC] source={request.Source} action={request.Action} stage=enter tunerStatus={tunerStatusBefore}");
        _log.Add("ALLOC_ROUTE", "Enter",
            $"source={request.Source} action={request.Action} executionMode={request.ExecutionMode} matcher={request.RunKeywordMatcher} syncProgram={request.SyncProgramRuleReservations} reevaluate={request.ReevaluateAllocations} preEpg={request.RefreshPreRecordEpgEntries} wake={request.RefreshWakeTask} bypassStopPhase={request.BypassStopPhaseGate} rule=common_allocation_route_contract");

        List<(int Id, string ServiceName, string Title, bool Conflicted)> changes = new();
        List<AllocationCommittedRowChange> rowChanges = new();
        long allocationGeneration = 0;
        bool retryRequired = false;
        string allocationReason = "not_requested";

        if (request.RunKeywordMatcher)
            Resolve<KeywordMatcher>()?.RunMatching();

        if (request.SyncProgramRuleReservations)
            _store.SyncProgramRuleReservations();

        if (request.ReevaluateAllocations)
        {
            var suppressedDuplicates = _store.SuppressDuplicateScheduledParentReservations(request.Source, request.Action);
            if (suppressedDuplicates > 0)
            {
                _log.Add("RESERVATION_DEDUPE", "SUMMARY",
                    $"result=SUPPRESSED count={suppressedDuplicates} source={request.Source} action={request.Action} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
            }

            var userChainPairs = _store.GetChainPredecessors();
            var effectiveLater = _ini.LaterProgramPriority;
            var effectiveChain = effectiveLater && (_ini.PseudoContinuousRecording || userChainPairs.Count > 0);

            _log.Add("ALLOC_POLICY", "Effective",
                $"source={request.Source} action={request.Action} executionMode={request.ExecutionMode} later={effectiveLater} iniChain={_ini.PseudoContinuousRecording} userChainPairs={userChainPairs.Count} effectiveChain={effectiveChain} rule=common_allocation_route_contract");

            var evaluation = EvaluateWithSingleRetry(effectiveLater, effectiveChain, request);
            changes = evaluation.Changes.ToList();
            rowChanges = evaluation.RowChanges.ToList();
            allocationGeneration = evaluation.AllocationGeneration;
            retryRequired = evaluation.RetryRequired;
            allocationReason = evaluation.Reason;
        }

        if (request.RefreshPreRecordEpgEntries && !retryRequired)
        {
            var preRecordEntriesChanged = Resolve<EpgScheduler>()?.RefreshPreRecordEpgEntries() ?? false;
            if (preRecordEntriesChanged && request.ReevaluateAllocations)
            {
                var userChainPairs = _store.GetChainPredecessors();
                var effectiveLater = _ini.LaterProgramPriority;
                var effectiveChain = effectiveLater && (_ini.PseudoContinuousRecording || userChainPairs.Count > 0);

                var finalEvaluation = EvaluateWithSingleRetry(effectiveLater, effectiveChain, request);
                changes = finalEvaluation.Changes.ToList();
                rowChanges = finalEvaluation.RowChanges.ToList();
                allocationGeneration = finalEvaluation.AllocationGeneration;
                retryRequired = finalEvaluation.RetryRequired;
                allocationReason = finalEvaluation.Reason;

                _log.Add("ALLOC_ROUTE", "PreRecEpgFinalize",
                    $"source={request.Source} action={request.Action} changed={preRecordEntriesChanged} finalAllocationChanges={changes.Count} allocationGeneration={allocationGeneration} retryRequired={retryRequired} reason={allocationReason} rule=single_owner_allocation_route");
            }
        }

        if (retryRequired && request.RefreshPreRecordEpgEntries)
            _log.Add("ALLOC_ROUTE", "PreRecEpgSkipped", $"source={request.Source} action={request.Action} reason=allocation_retry_required allocationReason={allocationReason} rule=release_contract");

        var result = new ReservationAllocationRouteResult(
            ChangedCount: changes.Count,
            ConflictOnCount: changes.Count(x => x.Conflicted),
            ConflictOffCount: changes.Count(x => !x.Conflicted),
            Deferred: false,
            AllocationGeneration: allocationGeneration,
            RetryRequired: retryRequired,
            Reason: allocationReason);

        _log.Add("ALLOC_TRACE", "CycleExit",
            $"[ALLOC] source={request.Source} action={request.Action} stage=core_cycle_exit changed={result.ChangedCount} on={result.ConflictOnCount} off={result.ConflictOffCount} allocationGeneration={result.AllocationGeneration} retryRequired={result.RetryRequired} reason={result.Reason} elapsedMs={(int)(DateTime.Now - startedAt).TotalMilliseconds} tunerStatusBefore={tunerStatusBefore} tunerStatusAfter={_tunerPool.GetStatusSummary()}");

        return new CycleResult(result, changes, rowChanges, request.ReevaluateAllocations);
    }

    private static void AccumulateConflictChanges(
        IDictionary<int, AggregateConflictChange> aggregate,
        IReadOnlyList<(int Id, string ServiceName, string Title, bool Conflicted)> changes)
    {
        foreach (var change in changes)
        {
            if (!aggregate.TryGetValue(change.Id, out var existing))
            {
                aggregate[change.Id] = new AggregateConflictChange(
                    change.Id,
                    change.ServiceName,
                    change.Title,
                    InitialConflicted: !change.Conflicted,
                    FinalConflicted: change.Conflicted);
                continue;
            }

            var updated = existing with
            {
                ServiceName = change.ServiceName,
                Title = change.Title,
                FinalConflicted = change.Conflicted
            };

            if (updated.InitialConflicted == updated.FinalConflicted)
                aggregate.Remove(change.Id);
            else
                aggregate[change.Id] = updated;
        }
    }

    private static void AccumulateAllocationRowChanges(
        IDictionary<int, AggregateAllocationRowChange> aggregate,
        IReadOnlyList<AllocationCommittedRowChange> changes)
    {
        foreach (var change in changes)
        {
            if (!aggregate.TryGetValue(change.Id, out var existing))
            {
                if (string.Equals(change.PreviousTunerName, change.CurrentTunerName, StringComparison.Ordinal)
                    && change.PreviousConflicted == change.CurrentConflicted)
                    continue;
                aggregate[change.Id] = new AggregateAllocationRowChange(
                    change.Id, change.ServiceName, change.Title,
                    change.PreviousTunerName, change.CurrentTunerName,
                    change.PreviousConflicted, change.CurrentConflicted,
                    change.PreviousDataVersion, change.CurrentDataVersion);
                continue;
            }

            var updated = existing with
            {
                ServiceName = change.ServiceName,
                Title = change.Title,
                FinalTunerName = change.CurrentTunerName,
                FinalConflicted = change.CurrentConflicted,
                FinalDataVersion = change.CurrentDataVersion
            };
            if (string.Equals(updated.InitialTunerName, updated.FinalTunerName, StringComparison.Ordinal)
                && updated.InitialConflicted == updated.FinalConflicted)
                aggregate.Remove(change.Id);
            else
                aggregate[change.Id] = updated;
        }
    }

    private static CycleResult BuildEffectiveFinalCycle(
        CycleResult lastCycle,
        ReservationAllocationRouteResult? latestAllocationResult,
        IReadOnlyDictionary<int, AggregateConflictChange> aggregateChanges,
        IReadOnlyDictionary<int, AggregateAllocationRowChange> aggregateRowChanges)
    {
        var changes = aggregateChanges.Values
            .OrderBy(x => x.Id)
            .Select(x => (x.Id, x.ServiceName, x.Title, x.FinalConflicted))
            .ToArray();

        var rowChanges = aggregateRowChanges.Values
            .OrderBy(x => x.Id)
            .Select(x => new AllocationCommittedRowChange(
                x.Id, x.ServiceName, x.Title,
                x.InitialTunerName, x.FinalTunerName,
                x.InitialConflicted, x.FinalConflicted,
                x.InitialDataVersion, x.FinalDataVersion))
            .ToArray();

        var allocationResult = latestAllocationResult ?? lastCycle.Result;
        var result = allocationResult with
        {
            ChangedCount = changes.Length,
            ConflictOnCount = changes.Count(x => x.FinalConflicted),
            ConflictOffCount = changes.Count(x => !x.FinalConflicted)
        };
        return new CycleResult(result, changes, rowChanges, latestAllocationResult is not null);
    }


    private void QueueFinalDerivedEffects(
        ReservationAllocationRouteRequest request,
        CycleResult finalCycle,
        ReservationMutationJournal? mutationJournal)
    {
        var queuedAt = DateTime.Now;
        lock (_derivedProjectionQueueGate)
        {
            _derivedProjectionTail = _derivedProjectionTail.ContinueWith(
                _ =>
                {
                    var startedAt = DateTime.Now;
                    var queueWaitMs = (int)(startedAt - queuedAt).TotalMilliseconds;
                    try
                    {
                        using var projectionScope = BeginTypedEventOutboxScope(out var commitProjectionEvents);
                        ApplyFinalDerivedEffects(request, finalCycle, mutationJournal);
                        commitProjectionEvents();
                        var executionMs = (int)(DateTime.Now - startedAt).TotalMilliseconds;
                        _log.Add("ALLOCATION_DERIVED_PROJECTION", "Completed",
                            $"source={request.Source} action={request.Action} allocationGeneration={finalCycle.Result.AllocationGeneration} rowChanges={finalCycle.RowChanges.Count} queueWaitMs={queueWaitMs} executionMs={executionMs} totalElapsedMs={(int)(DateTime.Now - queuedAt).TotalMilliseconds} ordering=serialized_after_authoritative_commit rule=release_contract");
                    }
                    catch (Exception ex)
                    {
                        // 正本commitは完了済み。派生投影失敗で割当をrollbackせず、次Mutation/再読込が
                        // 最新DBから再投影できるよう失敗だけを記録する。
                        _log.Add("ALLOCATION_DERIVED_PROJECTION", "Failed",
                            $"source={request.Source} action={request.Action} allocationGeneration={finalCycle.Result.AllocationGeneration} error={ex.Message} authoritativeCommitPreserved=True rule=release_contract");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        _log.Add("ALLOCATION_DERIVED_PROJECTION", "Queued",
            $"source={request.Source} action={request.Action} allocationGeneration={finalCycle.Result.AllocationGeneration} rowChanges={finalCycle.RowChanges.Count} singleFlightReleasedBeforeProjection=True rule=release_contract");
    }

    private void ApplyFinalDerivedEffects(
        ReservationAllocationRouteRequest request,
        CycleResult finalCycle,
        ReservationMutationJournal? mutationJournal)
    {
        if (finalCycle.Result.RetryRequired)
        {
            if (request.RefreshWakeTask)
                _log.Add("ALLOC_ROUTE", "WakeSkipped", $"source={request.Source} action={request.Action} reason=allocation_retry_required allocationReason={finalCycle.Result.Reason} rule=release_contract");
            return;
        }

        // Typed eventは最終確定周回だけ配送する。
        var allocationProjection = _store.PublishAllocationMutationEvents(
            finalCycle.RowChanges,
            finalCycle.Result.AllocationGeneration,
            queueWakeFallbackWhenCompleted: request.RefreshWakeTask
                && request.WakeRefreshMode == ReservationAllocationWakeRefreshMode.Immediate);

        // Wake再構築は現在のReservation正本全体から再計画するため、
        // この時点までに確定した全Wake影響Mutationを一つのfresh snapshotで処理できる。
        // 発行前の世代をclaimすると、この直前にPublishしたAllocation Mutationだけが未処理として残り、
        // fallbackから二重Wakeになる。逆にWake実行後のglobal versionをhandledにすると実行中の後着Mutationを
        // 巻き取るため、実行直前のversionを固定してclaim/handledの境界にする。
        var wakeRefreshSnapshotVersion = request.RefreshWakeTask && mutationJournal is not null
            ? mutationJournal.WakePlanVersion
            : 0;
        if (wakeRefreshSnapshotVersion > 0
            && request.WakeRefreshMode == ReservationAllocationWakeRefreshMode.Immediate)
        {
            mutationJournal!.MarkWakePlanClaimed(wakeRefreshSnapshotVersion);
            _log.Add("ALLOC_ROUTE", "WakeOwnerClaim",
                $"source={request.Source} action={request.Action} claimedVersion={wakeRefreshSnapshotVersion} projectionVersion={allocationProjection.WakePlanVersion} globalClaimedVersion={mutationJournal.WakePlanClaimedVersion} owner=reservation_allocation_route mode=immediate rule=allocation_mutation_wake_owner_contract");
        }

        if (request.RefreshWakeTask)
        {
            var wakeHandled = false;
            try
            {
                var taskScheduler = Resolve<TaskSchedulerService>();
                if (taskScheduler is not null)
                {
                    if (allocationProjection.SupersededCount > 0)
                    {
                        _log.Add("ALLOC_ROUTE", "WakeReload",
                            $"source={request.Source} action={request.Action} result=REQUIRED reason=allocation_mutation_projection_superseded projected={allocationProjection.ProjectedCount} superseded={allocationProjection.SupersededCount} action=reload_latest_reservations_from_db rule=allocation_mutation_projection_generation_contract");
                    }

                    // ALLOCATION_MUTATION_WAKE_OWNER_INVARIANT:
                    // 共通割当を通る予約変更のWake正本はここだけ。Mutation投影側は未処理世代の補完に限定し、
                    // 割当前スナップショットでWakeを同期再構築してAPI応答を塞ぐ経路を持たない。
                    if (request.WakeRefreshMode == ReservationAllocationWakeRefreshMode.BoundedCoalesce)
                    {
                        // BoundedCoalesceは「Wake再構築を予約した」だけで完了ではない。
                        // 実際の遅延再構築完了時にTaskSchedulerServiceから世代完了通知を受け、
                        // その時点でMutation世代をHandledへ進める。
                        taskScheduler.RequestWakeTaskRefreshSoon(
                            request.Source,
                            request.Action,
                            TimeSpan.FromMinutes(2),
                            wakeRefreshSnapshotVersion);
                        _log.Add("ALLOC_ROUTE", "Wake",
                            $"source={request.Source} action={request.Action} result=REQUESTED mode=bounded_coalesce wakePlanVersion={wakeRefreshSnapshotVersion} owner=reservation_allocation_route rule=allocation_mutation_wake_owner_contract");
                    }
                    else
                    {
                        taskScheduler.UpdateWakeTask();
                        _log.Add("ALLOC_ROUTE", "Wake",
                            $"source={request.Source} action={request.Action} result=APPLIED mode=immediate owner=reservation_allocation_route rule=allocation_mutation_wake_owner_contract");
                        wakeHandled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Add("ALLOC_ROUTE", "Wake",
                    $"source={request.Source} action={request.Action} result=FAILED error={ex.Message} owner=reservation_allocation_route rule=allocation_mutation_wake_owner_contract");
            }

            if (wakeHandled && mutationJournal is not null)
            {
                // claim時に固定した世代だけを完了扱いにする。実行中に増えた後続Mutationは処理済みにしない。
                if (wakeRefreshSnapshotVersion > 0)
                    mutationJournal.MarkWakePlanHandled(wakeRefreshSnapshotVersion);
                _log.Add("ALLOC_ROUTE", "WakeOwner",
                    $"source={request.Source} action={request.Action} claimedVersion={wakeRefreshSnapshotVersion} handledVersion={wakeRefreshSnapshotVersion} projectionVersion={allocationProjection.WakePlanVersion} globalHandledVersion={mutationJournal.WakePlanHandledVersion} owner=reservation_allocation_route rule=allocation_mutation_wake_owner_contract");
            }
        }

        if (request.EmitConflictLogs)
        {
            foreach (var (id, serviceName, title, conflicted) in finalCycle.Changes)
            {
                var state = conflicted ? "ON" : "OFF";
                var displayTitle = ReservationTitleDisplayContract.ForLog(title);
                var rawTitleBlank = ReservationTitleDisplayContract.RawBlankFlag(title);
                var msg = $"競合フラグ {state}: service=[{serviceName}] title=[{displayTitle}] rawTitleBlank={rawTitleBlank} id=R{id} rule=release_contract";
                _log.Add(request.ConflictLogCategory, request.ConflictLogTitle, msg);
            }
        }
    }

    private void CompleteWaitersLocked(ReservationAllocationRouteResult? result, Exception? error)
    {
        foreach (var waiter in _pendingWaiters)
        {
            waiter.Result = result;
            waiter.Error = error;
            waiter.Signal.Set();
        }
        _pendingWaiters.Clear();
    }

    private static ReservationAllocationRouteRequest MergeRequests(
        ReservationAllocationRouteRequest left,
        ReservationAllocationRouteRequest right)
    {
        return new ReservationAllocationRouteRequest(
            Source: MergeLabel(left.Source, right.Source),
            Action: MergeLabel(left.Action, right.Action),
            RunKeywordMatcher: left.RunKeywordMatcher || right.RunKeywordMatcher,
            SyncProgramRuleReservations: left.SyncProgramRuleReservations || right.SyncProgramRuleReservations,
            ReevaluateAllocations: left.ReevaluateAllocations || right.ReevaluateAllocations,
            RefreshPreRecordEpgEntries: left.RefreshPreRecordEpgEntries || right.RefreshPreRecordEpgEntries,
            RefreshWakeTask: left.RefreshWakeTask || right.RefreshWakeTask,
            BypassStopPhaseGate: left.BypassStopPhaseGate || right.BypassStopPhaseGate,
            EmitConflictLogs: left.EmitConflictLogs || right.EmitConflictLogs,
            ConflictLogCategory: string.Equals(left.ConflictLogCategory, right.ConflictLogCategory, StringComparison.Ordinal)
                ? left.ConflictLogCategory
                : "ReservationRoute",
            ConflictLogTitle: string.Equals(left.ConflictLogTitle, right.ConflictLogTitle, StringComparison.Ordinal)
                ? left.ConflictLogTitle
                : "Conflict",
            ExecutionMode: string.Equals(left.ExecutionMode, right.ExecutionMode, StringComparison.Ordinal)
                ? left.ExecutionMode
                : "MergedSingleFlight",
            WakeRefreshMode: left.WakeRefreshMode == ReservationAllocationWakeRefreshMode.BoundedCoalesce
                || right.WakeRefreshMode == ReservationAllocationWakeRefreshMode.BoundedCoalesce
                    ? ReservationAllocationWakeRefreshMode.BoundedCoalesce
                    : ReservationAllocationWakeRefreshMode.Immediate);
    }

    private static string MergeLabel(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return left;
        var values = left.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(right.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .Take(8);
        return string.Join("+", values);
    }

    private ReservationAllocationEvaluationResult EvaluateWithSingleRetry(
        bool effectiveLater,
        bool effectiveChain,
        ReservationAllocationRouteRequest request)
    {
        var first = _store.ReevaluateConflicts(
            _tunerProfiles,
            effectiveLater,
            effectiveChain,
            _ini.PostEndMarginSeconds,
            _tunerPool,
            _ini.PreStartMarginSeconds);

        if (!first.RetryRequired) return first;

        _log.Add("ALLOC_ROUTE", "Retry",
            $"source={request.Source} action={request.Action} attempt=2 reason={first.Reason} allocationGeneration={first.AllocationGeneration} reservationSnapshotVersion={first.ReservationSnapshotVersion} tunerSnapshotVersion={first.TunerSnapshotVersion} rule=release_contract");

        return _store.ReevaluateConflicts(
            _tunerProfiles,
            effectiveLater,
            effectiveChain,
            _ini.PostEndMarginSeconds,
            _tunerPool,
            _ini.PreStartMarginSeconds);
    }


    private T? Resolve<T>() where T : class
        => _serviceProvider.GetService<T>();
}
