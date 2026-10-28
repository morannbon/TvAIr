/*
 * 共通割り当てルートの不変条件:
 * - 割り当て順序、single-flight、authoritative commit、Mutation/Wake投影はこの経路へ集約する。
 * - 録画実行はTvAIrEpgRec共通TS runtimeへ渡し、別の録画起動・待機・補正経路を作らない。
 * - 完了待ちは状態、物理解放、worker終了の証拠で判定し、固定wait/sleep/cooldownで不整合を隠さない。
 * - DBへ割り当て専用の隠し状態や二重保存経路を追加せず、既存の正本commitを維持する。
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
    ReservationAllocationWakeRefreshMode WakeRefreshMode = ReservationAllocationWakeRefreshMode.Immediate,
    IReadOnlyList<ReservationTopologyCommand>? TopologyCommands = null,
    bool RejectDeferredTopologyCommands = false)
{
    internal bool IncludesOrdinaryWork { get; init; }
    internal bool DeferRuleRefresh { get; init; }
    internal bool RetainRuleRefresh { get; init; }
    internal bool RetainChainConvergence { get; init; }
    internal ReservationProjectionPromotionService? RetainedProjectionPromotion { get; init; }
    internal bool DeferProjectionPromotion { get; init; }
    internal ReservationAllocationRouteRequest? StopResumeRetry { get; init; }
    internal StopPhaseGate.PhysicalReleaseBarrier? StopPreparationBarrier { get; init; }
}

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
        bool AllocationAttempted,
        bool DeferredKeywordRefresh = false,
        bool DeferredProgramSync = false, bool DeferredChainConvergence = false,
        ReservationProjectionPromotionService? DeferredProjectionPromotion = null, bool ProjectionPromotionAttempted = false);

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

    // Pending work only; INI and ReservationStore remain the authoritative state.
    private int _disabledChainConvergenceRequested;
    public void RequestDisabledChainConvergence()
        => Interlocked.Exchange(ref _disabledChainConvergenceRequested, 1);
    internal bool HasPendingDisabledChainConvergence
        => Volatile.Read(ref _disabledChainConvergenceRequested) != 0;

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

    // Transient index into canonical queued/running commands, never reservation state or a second queue.
    private sealed class LabWithdrawalSlot(int selected)
    {
        public int Selected{get;}=selected;
        public TaskCompletionSource<LabWithdrawalOperationCommand?> Ready{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<string,LabWithdrawalSlot> _labWithdrawalOwners=new(StringComparer.Ordinal);
    public async Task<LabWithdrawalAdmission> AdmitLabWithdrawalAsync(Guid operation,int selected,Func<LabWithdrawalOperationCommand?> create)
    {
        var key=operation.ToString("N");LabWithdrawalSlot slot;bool creator;
        lock(_singleFlightGate){
            foreach(var old in _labWithdrawalOwners.Where(pair=>pair.Value.Ready.Task.IsCompleted
                &&(!pair.Value.Ready.Task.IsCompletedSuccessfully||pair.Value.Ready.Task.Result is null||pair.Value.Ready.Task.Result.Completion.IsCompleted)).Select(pair=>pair.Key).ToArray())_labWithdrawalOwners.Remove(old);
            if(_labWithdrawalOwners.TryGetValue(key,out var existing)){
                if(existing.Selected!=selected)return new(null,"operation_target_mismatch",true);
                slot=existing;creator=false;
            }else{slot=new(selected);_labWithdrawalOwners.Add(key,slot);creator=true;}
        }
        if(creator){
            try{
                var candidate=create();
                if(candidate is not null&&(candidate.RequestedId!=selected||candidate.OperationId!=key))throw new InvalidOperationException("Withdrawal command identity mismatch.");
                slot.Ready.TrySetResult(candidate);
            }catch(Exception ex){slot.Ready.TrySetException(ex);lock(_singleFlightGate)if(_labWithdrawalOwners.TryGetValue(key,out var current)&&ReferenceEquals(current,slot))_labWithdrawalOwners.Remove(key);}
        }
        // Factory/SQLite work and waits never hold the canonical queue gate.
        var command=await slot.Ready.Task;
        if(command is null)return new(null,"not_applicable",!creator);
        Run(new("ReservationMutation","WithdrawRegisteredFuture",SyncProgramRuleReservations:false,ReevaluateAllocations:false,
            RefreshPreRecordEpgEntries:false,RefreshWakeTask:false,TopologyCommands:new[]{command}),waitForActiveSingleFlight:false);
        return new(command,"accepted",!creator);
    }

    private string _appliedChainMode;
    // Preview only. The command rechecks policy and SQLite scope inside this route.
    public IReadOnlyList<Reservation> GetReservationCancellationTargets(int id)
        => _store.GetReservationCancellationTargets(id, _ini.AutomaticChainRecordingEnabled);

    private string EffectiveChainMode => _ini.LaterProgramPriority ? _ini.ChainReservationMode : ChainModeContract.Disabled;

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
        // Startup adopts the configured policy without resetting stored connections.
        // Automatic convergence still runs through the ordinary common allocation cycle.
        _appliedChainMode=EffectiveChainMode;
        _tunerProfiles = tunerProfiles;
        _tunerPool = tunerPool;
        _log = log;
        _serviceProvider = serviceProvider;
    }

    internal void RetainStopResumeRequest(ReservationAllocationRouteRequest request)
    {
        if (request.StopPreparationBarrier is not null)
            throw new InvalidOperationException("A live stop-preparation scope cannot be retained.");
        if (request.TopologyCommands is { } commands)
            request = request with { TopologyCommands = Array.AsReadOnly(commands.ToArray()) };
        request = request with { StopResumeRetry = (request.StopResumeRetry ?? request) with { StopResumeRetry = null } };
        lock (_singleFlightGate)
            _pendingRequest = _pendingRequest is null ? request : MergeRequests(request, _pendingRequest);
    }

    // Only explicitly retryable automatic intents survive failures; strict UI commands are not replayed.
    private static ReservationAllocationRouteRequest? RetainedTopologyRetryRequest(
        ReservationAllocationRouteRequest request, ReservationAllocationRouteRequest? pending)
    {
        var commands = (request.TopologyCommands ?? Array.Empty<ReservationTopologyCommand>())
            .Concat(pending?.TopologyCommands ?? Array.Empty<ReservationTopologyCommand>())
            .Where(c => c.RetainIfUnexecuted).Distinct().ToArray();
        var retainRefresh = request.RetainRuleRefresh || pending?.RetainRuleRefresh == true;
        var retainChain=request.RetainChainConvergence || pending?.RetainChainConvergence == true;
        var promotion = PendingProjectionPromotion(request) ?? PendingProjectionPromotion(pending);
        return commands.Length == 0 && !retainRefresh && !retainChain && promotion is null ? null : new("AutomaticTopology", "RetryUnfinished",
            RunKeywordMatcher: retainRefresh && (request.RunKeywordMatcher || pending?.RunKeywordMatcher == true),
            SyncProgramRuleReservations: retainRefresh && (request.SyncProgramRuleReservations || pending?.SyncProgramRuleReservations == true),
            ReevaluateAllocations:request.ReevaluateAllocations || pending?.ReevaluateAllocations==true,
            RefreshPreRecordEpgEntries:request.RefreshPreRecordEpgEntries || pending?.RefreshPreRecordEpgEntries==true,
            RefreshWakeTask:request.RefreshWakeTask || pending?.RefreshWakeTask==true,
            TopologyCommands: commands) { RetainRuleRefresh = retainRefresh, RetainChainConvergence=retainChain, RetainedProjectionPromotion=promotion };
    }

    // Carry only the service that re-reads current DB/metadata, never captured EPG payloads.
    private static ReservationProjectionPromotionService? PendingProjectionPromotion(ReservationAllocationRouteRequest? request)
        => request?.TopologyCommands?.OfType<EpgCaptureCommitCommand>()
            .Select(c => c.PendingPromotionRetry).FirstOrDefault(p => p is not null)
            ?? request?.RetainedProjectionPromotion;

    internal ReservationAllocationRouteResult RunManualStopPreparation(
        ManualStopPreparationCommand command, StopPhaseGate.PhysicalReleaseBarrier barrier)
        => Run(new("RecordingStop", "PrepareTopology", SyncProgramRuleReservations: false,
            ReevaluateAllocations: false, TopologyCommands: new[] { command },
            RejectDeferredTopologyCommands: true) { StopPreparationBarrier = barrier });

    // Isolated withdrawal integration; accepted intents remain in the one existing queue.
    internal bool HasPendingLabWithdrawalWork
    {
        get
        {
            lock (_singleFlightGate)
                return _pendingRequest?.TopologyCommands?.Any(command =>
                    command is LabWithdrawalOperationCommand || command is LabWithdrawalSettlementCommand
                    || command is ReservationCancellationCommand { LabPreparationReady: not null }) == true;
        }
    }

    public ReservationAllocationRouteResult Run(ReservationAllocationRouteRequest request, bool waitForActiveSingleFlight = true)
    {
        var stopPreparation = request.StopPreparationBarrier is not null;
        if (stopPreparation && (request.StopPreparationBarrier is not { IsActive: true }
            || request.TopologyCommands is not { Count: 1 }
            || request.TopologyCommands[0] is not ManualStopPreparationCommand stopCommand
            || request.StopPreparationBarrier.ReservationId != stopCommand.ReservationId
            || request.RunKeywordMatcher || request.SyncProgramRuleReservations
            || request.ReevaluateAllocations || request.RefreshPreRecordEpgEntries || request.RefreshWakeTask))
            throw new InvalidOperationException("Invalid manual-stop preparation request.");
        if (!stopPreparation && request.TopologyCommands?.Any(c => c is ManualStopPreparationCommand) == true)
            throw new InvalidOperationException("Manual-stop preparation requires its physical-release scope.");

        // Freeze the caller's collection before StopPhase/single-flight can retain it.
        if (request.TopologyCommands is { } commands)
            request = request with
            {
                TopologyCommands = Array.AsReadOnly(commands.ToArray()),
                ReevaluateAllocations = request.ReevaluateAllocations || (!stopPreparation && commands.Any(c=>c.RequiresAllocationAfterCommit)),
                RefreshPreRecordEpgEntries = request.RefreshPreRecordEpgEntries || (!stopPreparation && commands.Any(c=>c.RequiresAllocationAfterCommit)),
                RefreshWakeTask = request.RefreshWakeTask || (!stopPreparation && commands.Any(c=>c.RequiresAllocationAfterCommit))
            };

        if (request.TopologyCommands is not { Count: > 0 })
            request = request with { IncludesOrdinaryWork = true };

        var requestedAt = DateTime.Now;

        if (StopPhaseGate.TryDeferAllocRoute(request,
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
                if (_pendingRequest is not null)
                {
                    if (stopPreparation)
                    {
                        var maintenanceOnly = _pendingRequest.TopologyCommands is { Count: > 0 } pendingCommands
                            ? pendingCommands.All(c => c.NonBlockingDeferralKey is not null)
                            : _pendingRequest.RetainRuleRefresh || _pendingRequest.RetainChainConvergence || _pendingRequest.RetainedProjectionPromotion is not null;
                        if (_pendingRequest.IncludesOrdinaryWork || _pendingRequest.StopResumeRetry is not null || !maintenanceOnly)
                            return new(0, 0, 0, Deferred: true, Reason: "stop_preparation_resume_pending");
                        // Stop preparation may pass parked maintenance, but never executes it
                        // within the caller-owned physical-release barrier.
                    }
                    else
                    {
                        request = MergeRequests(_pendingRequest, request);
                        _pendingRequest = null;
                    }
                }
                _singleFlightRunning = true;
                _singleFlightOwnerThreadId = currentThreadId;
                ownerEpoch = unchecked(++_singleFlightOwnerEpoch);
                isLeader = true;
            }
            else
            {
                // Stop preparation never waits inside another flight or survives its caller.
                // Its physical-release scope must not be merged with ordinary allocation.
                if (stopPreparation)
                    return new(0, 0, 0, Deferred: true, Reason: "stop_preparation_not_accepted_busy");
                // Interactive callers must never receive an unsaved response while their
                // command remains queued to create a reservation afterwards.
                if (request.RejectDeferredTopologyCommands && request.TopologyCommands is { Count: > 0 }
                    && (_singleFlightOwnerThreadId == currentThreadId || !waitForActiveSingleFlight))
                    return new(0, 0, 0, Deferred: true, Reason: "chain_command_not_accepted_busy");

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
            var retry = firstRequest.StopResumeRetry;
            var waiterCount = 0;
            lock (_singleFlightGate)
            {
                // BeginTypedEventOutboxScope / Resolve等、従来の内側tryへ入る前の例外でも
                // ownerを必ず終端する。ただし、このownerが既に正常/異常解放され別ownerへ
                // 世代交代している場合は絶対に触らない。
                if (_singleFlightRunning && _singleFlightOwnerEpoch == ownerEpoch)
                {
                    retry = MergeStopResumeRetries(retry, _pendingRequest?.StopResumeRetry);
                    waiterCount = _pendingWaiters.Count;
                    _singleFlightRunning = false;
                    _singleFlightOwnerThreadId = 0;
                    _pendingRequest = RetainedTopologyRetryRequest(firstRequest, _pendingRequest);
                    CompleteWaitersLocked(null, ex);
                    released = true;
                }
            }

            if (released)
            {
                if (retry is not null) RetainStopResumeRequest(retry);
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
        var currentRequest = firstRequest with { RetainChainConvergence = firstRequest.RetainChainConvergence || (firstRequest.ReevaluateAllocations && (EffectiveChainMode==ChainModeContract.Automatic || EffectiveChainMode!=_appliedChainMode)) };
        var aggregateRequest = currentRequest;
        var mutationJournal = Resolve<ReservationMutationJournal>();
        CycleResult? finalCycle = null;
        ReservationAllocationRouteResult? latestAllocationResult = null;
        var aggregateConflictChanges = new Dictionary<int, AggregateConflictChange>();
        var aggregateAllocationRowChanges = new Dictionary<int, AggregateAllocationRowChange>();
        // Flight-local references only; pending ownership remains in _pendingRequest.
        var parked = new List<ReservationTopologyCommand>();
        var deferredKeywordRefresh = false;
        var deferredProgramSync = false;
        var deferredChainConvergence=false;
        ReservationProjectionPromotionService? deferredProjectionPromotion=null;
        bool BlocksRefresh() => parked.Any(c => c.RetainIfUnexecuted && c.BlocksRuleRefreshWhenDeferred);
        ReservationAllocationRouteRequest WithParked(ReservationAllocationRouteRequest request)
        {
            foreach (var settings in request.TopologyCommands?.OfType<SettingsChangeCommand>()
                ?? Enumerable.Empty<SettingsChangeCommand>())
                if (settings.Followup is { } followup) request = MergeRequests(request, followup);
            // A command may commit and then throw from its observer before the core
            // reports its refresh flags. Preserve that committed intent as well.
            var keyword = deferredKeywordRefresh
                || request.TopologyCommands?.Any(c => c.RequiresKeywordRefreshAfterCommit) == true;
            var program = deferredProgramSync || request.SyncProgramRuleReservations
                || request.TopologyCommands?.Any(c => c.RequiresProgramRuleRefreshAfterCommit) == true;
            return request with
            {
                TopologyCommands = MergeTopologyCommands(parked, request.TopologyCommands),
                RunKeywordMatcher = request.RunKeywordMatcher || keyword,
                SyncProgramRuleReservations = request.SyncProgramRuleReservations || program,
                RetainRuleRefresh = request.RetainRuleRefresh || keyword || program,
                RetainChainConvergence = request.RetainChainConvergence || deferredChainConvergence || (request.ReevaluateAllocations && EffectiveChainMode==ChainModeContract.Automatic),
                RetainedProjectionPromotion = PendingProjectionPromotion(request) ?? deferredProjectionPromotion,
                DeferRuleRefresh = false
            };
        }

        try
        {
            while (true)
            {
                // Preserve sequential command semantics after request coalescing: a following
                // command must observe allocation/conflict changes made by its predecessor.
                var commands = currentRequest.TopologyCommands;
                var cycleCount = Math.Max(1, commands?.Count ?? 0);
                for (var index = 0; index < cycleCount; index++)
                {
                    var cycleRequest = commands is { Count: > 0 }
                        ? currentRequest with
                        {
                            TopologyCommands = new[] { commands[index] },
                            RunKeywordMatcher = index == 0 && currentRequest.RunKeywordMatcher,
                            SyncProgramRuleReservations = index == 0 && currentRequest.SyncProgramRuleReservations,
                            RetainedProjectionPromotion = index == 0 ? currentRequest.RetainedProjectionPromotion : null
                        }
                        : currentRequest;
                    var blockRefresh = BlocksRefresh();
                    cycleRequest = cycleRequest with
                    {
                        DeferRuleRefresh = blockRefresh,
                        RetainedProjectionPromotion = cycleRequest.RetainedProjectionPromotion ?? deferredProjectionPromotion,
                        DeferProjectionPromotion = deferredProjectionPromotion is not null,
                        RunKeywordMatcher = cycleRequest.RunKeywordMatcher || (!blockRefresh && deferredKeywordRefresh),
                        SyncProgramRuleReservations = cycleRequest.SyncProgramRuleReservations || (!blockRefresh && deferredProgramSync)
                    };
                    var command = commands is { Count: > 0 } ? commands[index] : null;
                    var orderingKey = command?.NonBlockingDeferralKey;
                    var behindParked = orderingKey is not null && command!.RetainIfUnexecuted
                        && parked.Any(c => c.RetainIfUnexecuted && ReferenceEquals(c.NonBlockingDeferralKey, orderingKey));
                    finalCycle = behindParked ? null : ExecuteCoreCycle(cycleRequest);
                    if (command is SettingsChangeCommand settings && settings.Followup is { } followup)
                    {
                        cycleRequest = MergeRequests(cycleRequest, followup);
                        aggregateRequest = MergeRequests(aggregateRequest, followup);
                        if (finalCycle is { Result: { Deferred: false, RetryRequired: false } }) settings.CompleteFollowup();
                        else if (settings.RetainIfUnexecuted && !parked.Contains(settings)) parked.Add(settings);
                    }
                    if (orderingKey is not null && (behindParked || finalCycle!.Result.Deferred && command!.DeferredReason is not null))
                    {
                        if (!parked.Contains(command!)) parked.Add(command!);
                        // Do not retry this series inside the same flight. Ordinary work and
                        // unrelated commands still use the existing allocation core.
                        finalCycle = ExecuteCoreCycle(cycleRequest with { TopologyCommands = null, DeferRuleRefresh = BlocksRefresh() });
                    }
                    if (finalCycle is null) throw new InvalidOperationException("Allocation cycle did not execute.");
                    if (finalCycle.Result.Deferred && command?.DeferredReason is not null)
                    {
                        // Park the current and following commands in the existing queue. Do not spin
                        // inside this flight while a runtime publisher still owns its admission.
                        var tail = currentRequest with
                        {
                            TopologyCommands = Array.AsReadOnly(commands!.Skip(index).ToArray()),
                            RunKeywordMatcher = index == 0 && currentRequest.RunKeywordMatcher,
                            SyncProgramRuleReservations = index == 0 && currentRequest.SyncProgramRuleReservations
                        };
                        var completed = BuildEffectiveFinalCycle(finalCycle, latestAllocationResult,
                            aggregateConflictChanges, aggregateAllocationRowChanges);
                        lock (_singleFlightGate)
                        {
                            tail = WithParked(tail);
                            var queued = _pendingRequest is null ? tail : MergeRequests(tail, _pendingRequest);
                            // A synchronous UI waiter receives Deferred below. Its unexecuted intent
                            // must not create/cancel a reservation later after that unsaved response.
                            _pendingRequest = queued with { TopologyCommands = Array.AsReadOnly(
                                (queued.TopologyCommands ?? Array.Empty<ReservationTopologyCommand>())
                                .Where(c => c.RetainIfUnexecuted).ToArray()) };
                            commitEvents();
                            _singleFlightRunning = false;
                            _singleFlightOwnerThreadId = 0;
                            CompleteWaitersLocked(finalCycle.Result, null);
                        }
                        if (latestAllocationResult is not null)
                            QueueFinalDerivedEffects(aggregateRequest, completed, mutationJournal);
                        return finalCycle.Result;
                    }
                    if (finalCycle.DeferredKeywordRefresh) deferredKeywordRefresh = true;
                    else if (!finalCycle.Result.Deferred && cycleRequest.RunKeywordMatcher) deferredKeywordRefresh = false;
                    if (finalCycle.DeferredProgramSync) deferredProgramSync = true;
                    else if (!finalCycle.Result.Deferred && cycleRequest.SyncProgramRuleReservations) deferredProgramSync = false;
                    if(finalCycle.DeferredProjectionPromotion is not null) deferredProjectionPromotion=finalCycle.DeferredProjectionPromotion;
                    else if(finalCycle.ProjectionPromotionAttempted) deferredProjectionPromotion=null;
                    if(finalCycle.DeferredChainConvergence) deferredChainConvergence=true;
                    else if(cycleRequest.ReevaluateAllocations) deferredChainConvergence=false;
                    if (finalCycle.AllocationAttempted)
                        latestAllocationResult = finalCycle.Result;
                    AccumulateConflictChanges(aggregateConflictChanges, finalCycle.Changes);
                    AccumulateAllocationRowChanges(aggregateAllocationRowChanges, finalCycle.RowChanges);
                }

                if (finalCycle is null) throw new InvalidOperationException("Allocation cycle did not execute.");

                ReservationAllocationRouteRequest? nextRequest;
                lock (_singleFlightGate)
                {
                    nextRequest = firstRequest.StopPreparationBarrier is null ? _pendingRequest : null;
                    if (nextRequest is not null) _pendingRequest = null;
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
                var response = effectiveCycle.Result;

                lock (_singleFlightGate)
                {
                    // 正本確定までに到着した要求だけを同じbatchへ統合する。
                    // 派生投影開始後の要求は、新しいsingle-flightとして最新DBを再評価する。
                    if (_pendingRequest is not null && firstRequest.StopPreparationBarrier is null)
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
                    var unfinished = parked.Where(c => c.RetainIfUnexecuted).ToArray();
                    if (unfinished.Length > 0 || deferredKeywordRefresh || deferredProgramSync || deferredChainConvergence || deferredProjectionPromotion is not null)
                    {
                        var retry = new ReservationAllocationRouteRequest("AutomaticTopology", "RetryParked",
                            RunKeywordMatcher: deferredKeywordRefresh, SyncProgramRuleReservations: deferredProgramSync,
                            ReevaluateAllocations:aggregateRequest.ReevaluateAllocations,
                            RefreshPreRecordEpgEntries:aggregateRequest.RefreshPreRecordEpgEntries,
                            RefreshWakeTask:aggregateRequest.RefreshWakeTask, TopologyCommands: unfinished)
                            { RetainRuleRefresh = deferredKeywordRefresh || deferredProgramSync, RetainChainConvergence=deferredChainConvergence, RetainedProjectionPromotion=deferredProjectionPromotion };
                        _pendingRequest = _pendingRequest is null ? retry : MergeRequests(retry, _pendingRequest);
                        if (!aggregateRequest.IncludesOrdinaryWork && aggregateRequest.TopologyCommands is { Count: > 0 } all
                            && all.All(unfinished.Contains))
                            response = response with { Deferred = true, Reason = "automatic_maintenance_parked" };
                    }
                    _singleFlightRunning = false;
                    _singleFlightOwnerThreadId = 0;
                    CompleteWaitersLocked(response, null);
                }

                QueueFinalDerivedEffects(aggregateRequest, effectiveCycle, mutationJournal);

                var elapsedMs = (int)(DateTime.Now - requestedAt).TotalMilliseconds;
                _log.Add("ALLOC_ROUTE", "SingleFlightComplete",
                    $"source={aggregateRequest.Source} action={aggregateRequest.Action} changed={effectiveCycle.Result.ChangedCount} allocationGeneration={effectiveCycle.Result.AllocationGeneration} retryRequired={effectiveCycle.Result.RetryRequired} reason={effectiveCycle.Result.Reason} elapsedMs={elapsedMs} rule=release_contract");
                return response;
            }
        }
        catch (Exception ex)
        {
            var retry = aggregateRequest.StopResumeRetry;
            lock (_singleFlightGate)
            {
                // 正常解放直後の派生enqueue等で例外になり、既に次ownerへ世代交代していても
                // catchした世代が現在のownerである場合だけ終端し、後続ownerの所有権には触れない。
                if (_singleFlightRunning && _singleFlightOwnerEpoch == ownerEpoch)
                {
                    retry = MergeStopResumeRetries(retry, _pendingRequest?.StopResumeRetry);
                    _singleFlightRunning = false;
                    _singleFlightOwnerThreadId = 0;
                    _pendingRequest = RetainedTopologyRetryRequest(WithParked(aggregateRequest), _pendingRequest);
                    CompleteWaitersLocked(null, ex);
                }
            }
            if (retry is not null) RetainStopResumeRequest(retry);
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
        if (request.StopPreparationBarrier is { } preparation)
        {
            if (!preparation.IsActive)
                throw new InvalidOperationException("Manual-stop preparation scope has ended.");
        }
        else if (StopPhaseGate.TryDeferAllocRouteForPhysicalRelease(request,
            msg => _log.Add("ALLOC_ROUTE", "PhysicalRelease", msg)))
        {
            return new CycleResult(new(0, 0, 0, Deferred: true, Reason: "physical_release_pending"),
                Array.Empty<(int, string, string, bool)>(), Array.Empty<AllocationCommittedRowChange>(), false);
        }
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
        string allocationReason = request.StopPreparationBarrier is not null
            ? "stop_preparation_applied" : "not_requested";

        // Explicit topology intent is committed only while this route owns the flight.
        // A receipt records a committed/rejected mutation separately from later allocation failure.
        foreach (var command in request.TopologyCommands ?? Array.Empty<ReservationTopologyCommand>())
        {
            if (command is LabWithdrawalOperationCommand operation) operation.PrepareMode(_ini.AutomaticChainRecordingEnabled);
            if (command is ReservationCancellationCommand cancellation)
                cancellation.PrepareCancellationPolicy(_ini.AutomaticChainRecordingEnabled);
            var runtime = command.RequiresSchedulerContext
                ? Resolve<ReservationScheduler>() ?? throw new InvalidOperationException("Reservation scheduler is unavailable.")
                : null;
            try
            {
                var metadata = command.RequiresProjectionMetadata ? Resolve<ReservationProjectionMetadataStore>() : null;
                if (request.StopPreparationBarrier is { IsActive: false })
                    throw new InvalidOperationException("Manual-stop preparation scope has ended.");
                if (runtime is not null) command.PrepareSchedulerContext(runtime);
                if (command is ManualStopPreparationCommand stopPreparation)
                    stopPreparation.PrepareOccurrencePolicy(runtime!);
                command.Apply(_store, command is UserChainAllocationCommand ? _ini.ManualChainRecordingEnabled : _ini.UserChainRecordingEnabled, metadata);
                if (command is SettingsChangeCommand settings && settings.Followup is { } followup)
                    request = MergeRequests(request, followup);
                if (command.DeferredReason is string deferredReason)
                    return new CycleResult(new(0, 0, 0, Deferred: true, RetryRequired: true,
                        Reason: deferredReason), changes, rowChanges, AllocationAttempted: false);
            }
            finally
            {
                // A post-commit observer error must not skip the canonical runtime seam.
                if (runtime is not null) command.ReconcileRuntime(runtime);
            }
        }

        // A retained command may acquire a committed mutation only on a later cycle.
        // Re-capture its allocation intent after completion in this same canonical exit.
        if(request.StopPreparationBarrier is null && request.TopologyCommands?.Any(c=>c.RequiresAllocationAfterCommit)==true)
            request=request with { ReevaluateAllocations=true,RefreshPreRecordEpgEntries=true,RefreshWakeTask=true };

        if (request.TopologyCommands is { Count: > 0 } explicitCommands
            && explicitCommands.All(c => c.IsRejected)
            && !request.RunKeywordMatcher && !request.SyncProgramRuleReservations && !request.IncludesOrdinaryWork
            && request.RetainedProjectionPromotion is null)
            return new CycleResult(new(0, 0, 0, Deferred: false, Reason: "chain_command_rejected"),
                changes, rowChanges, AllocationAttempted: false);

        var promotionPending = request.TopologyCommands?.OfType<EpgCaptureCommitCommand>()
            .Select(c => c.PendingPromotionRetry).FirstOrDefault(p => p is not null);
        var completedPromotion = request.TopologyCommands?.OfType<EpgCaptureCommitCommand>()
            .Select(c => c.CompletedPromotionService).FirstOrDefault(p => p is not null);
        var promotionAttempted = completedPromotion is not null;
        if (request.DeferProjectionPromotion && completedPromotion is null) promotionPending ??= request.RetainedProjectionPromotion;
        if (!request.DeferProjectionPromotion && request.RetainedProjectionPromotion is { } retainedPromotion
            && !ReferenceEquals(retainedPromotion, completedPromotion) && promotionPending is null)
        {
            // Raw capture has finished. Retry its reservation work under the existing
            // Scheduler admission using current metadata and DB events.
            promotionAttempted = true;
            promotionPending = retainedPromotion;
            var scheduler = Resolve<ReservationScheduler>()
                ?? throw new InvalidOperationException("Projection retry scheduler is unavailable.");
            scheduler.ExecuteProjectionPromotionCore(_store, retainedPromotion, receipt =>
            {
                promotionPending = receipt.RetryRequired ? retainedPromotion : null;
            });
        }

        var committedRuleRefresh = request.TopologyCommands?.Any(c => c.RequiresRuleRefreshAfterCommit) == true;
        var incrementalCommands = request.TopologyCommands?.Select(c => c.CommittedKeywordEvents)
            .Where(events => events is { Count: > 0 }).ToArray();
        var wantsKeywordRefresh = request.RunKeywordMatcher || committedRuleRefresh || incrementalCommands is { Length: > 0 };
        var deferredKeywordRefresh = request.DeferRuleRefresh && wantsKeywordRefresh;
        if (wantsKeywordRefresh && !deferredKeywordRefresh)
        {
            var matcher = Resolve<KeywordMatcher>();
            if (request.RunKeywordMatcher || committedRuleRefresh) matcher?.RunMatching();
            else foreach (var events in incrementalCommands!) matcher?.RunMatching(events!);
        }

        // Matcher observations are only hints. Re-capture channel settings under runtime
        // admission, then select current rows in the allocation-owned transaction. The
        // matcher gate has already been released before this stage begins.
        var inactiveMatcher = request.ReevaluateAllocations ? Resolve<KeywordMatcher>() : null;
        if (inactiveMatcher?.TakeInactiveCleanupRequest() == true)
        {
            KeywordRuleMutationResult? cleanup = null;
            try
            {
                var runtime = Resolve<ReservationScheduler>()
                    ?? throw new InvalidOperationException("Inactive cleanup scheduler is unavailable.");
                runtime.ExecuteInactiveKeywordCleanupCore(_store, inactiveMatcher, committed => cleanup = committed);
                _log.Add("KEYWORD_INACTIVE_CLEANUP", "AllocationRoute",
                    $"source={request.Source} action={request.Action} committed={cleanup?.Applied == true} removed={cleanup?.RemovedIds.Count ?? 0} reason={cleanup?.Reason ?? "runtime_unavailable"}");
            }
            finally
            {
                // Taking the bit before execution preserves a new observation arriving during
                // commit. Never clear that newer request when this attempt succeeds.
                if (cleanup is not { Applied: true, Reason: "applied" }) inactiveMatcher.RestoreInactiveCleanupRequest();
            }
        }

        var wantsProgramSync = request.SyncProgramRuleReservations
            || request.TopologyCommands?.Any(c => c.RequiresProgramRuleRefreshAfterCommit) == true;
        var deferredProgramSync = request.DeferRuleRefresh && wantsProgramSync;
        if (wantsProgramSync && !deferredProgramSync)
            deferredProgramSync=!_store.SyncProgramRuleReservations(selection=>
            {
                if(!_store.HasProgramCleanupWork(selection)) return new(true,"no_candidates",Array.Empty<int>(),0,Array.Empty<int>());
                var runtime=Resolve<ReservationScheduler>();
                return runtime is null ? new(false,"program_sync_scheduler_unavailable",Array.Empty<int>(),0,Array.Empty<int>())
                    : runtime.ExecuteProgramReservationCleanupCore(_store,selection);
            });

        if (deferredKeywordRefresh || deferredProgramSync)
            _log.Add("RULE_REFRESH_DEFERRED", "ProjectionPromotion",
                $"keyword={deferredKeywordRefresh} program={deferredProgramSync} allocationContinues=True rule=common_allocation_route_contract");

        var chainPending=false;
        if (request.ReevaluateAllocations)
        {
            chainPending=!_store.CleanupResolvedProgramGuideMissingDuplicates(selection=>
            {
                if(!_store.HasProgramCleanupWork(selection))return new(true,"no_candidates",Array.Empty<int>(),0,Array.Empty<int>());
                var scheduler=Resolve<ReservationScheduler>();
                return scheduler is null ? new(false,"duplicate_cleanup_scheduler_unavailable",Array.Empty<int>(),0,Array.Empty<int>())
                    : scheduler.ExecuteProgramReservationCleanupCore(_store,selection);
            });
            var suppressedDuplicates = _store.SuppressDuplicateScheduledParentReservations(request.Source, request.Action);
            if (suppressedDuplicates > 0)
            {
                _log.Add("RESERVATION_DEDUPE", "SUMMARY",
                    $"result=SUPPRESSED count={suppressedDuplicates} source={request.Source} action={request.Action} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
            }

            var mode=EffectiveChainMode;
            if(!chainPending && mode!=_appliedChainMode)
            {
                // Mode switches preserve stored edges. Feature disable retains its existing detach path.
                var requiresRebuild=mode==ChainModeContract.Disabled && _appliedChainMode==ChainModeContract.Automatic;
                if(requiresRebuild)
                {
                    var scheduler=Resolve<ReservationScheduler>();
                    chainPending=scheduler is null || !scheduler.PrepareChainModeRebuildCore(
                        "ChainMode:"+_appliedChainMode+"->"+mode,()=>_appliedChainMode=mode);
                }
                else _appliedChainMode=mode; // manual <-> automatic, including startup: keep stored edges.
            }
            if(mode==ChainModeContract.Automatic && !chainPending)
            {
                var scheduler=Resolve<ReservationScheduler>();
                if(scheduler is null) chainPending=true;
                else
                {
                    var snapshot=_store.GetAll().Where(r=>r.IsEnabled && r.Source!=ReservationSource.Epg
                        && ChainReservationEligibilityContract.IsActivePredecessorStatus(r.Status)).ToArray();
                    var parents=snapshot.Where(r=>r.UserChainPreviousId.HasValue).Select(r=>r.UserChainPreviousId!.Value).ToHashSet();
                    var heads=snapshot.Where(r=>r.UserChainPreviousId is null)
                        .GroupBy(r=>(r.NetworkId,r.TransportStreamId,r.ServiceId,r.StartTime)).ToDictionary(g=>g.Key,g=>g.ToArray());
                    var tails=snapshot.Where(r=>!parents.Contains(r.Id))
                        .GroupBy(r=>(r.NetworkId,r.TransportStreamId,r.ServiceId,r.EndTime)).ToDictionary(g=>g.Key,g=>g.ToArray());
                    bool OtherComponent(Reservation a,Reservation b)=>(a.UserChainRootId??a.Id)!=(b.UserChainRootId??b.Id);
                    foreach(var row in snapshot.Where(r=>r.Status==ReservationStatus.Scheduled
                        && (r.UserChainPreviousId is null && tails.TryGetValue((r.NetworkId,r.TransportStreamId,r.ServiceId,r.StartTime),out var before)
                                && before.Any(p=>OtherComponent(r,p))
                            || !parents.Contains(r.Id) && heads.TryGetValue((r.NetworkId,r.TransportStreamId,r.ServiceId,r.EndTime),out var after)
                                && after.Any(n=>OtherComponent(r,n))))
                        .OrderBy(r=>r.StartTime).ThenBy(r=>r.Id))
                {
                    UserChainSequenceMutationResult? receipt=null;
                    try { scheduler.ExecuteAutomaticChainCore(_store,row.Id,true,result=>receipt=result); }
                    finally { if(receipt?.Applied==true) scheduler.ReconcileActiveContinuousChainTopology("AutomaticChain"); }
                    if(receipt is null || receipt.Reason=="runtime_protected") { chainPending=true;break; }
                    }
                }
            }
            var keywordMatcher = Resolve<KeywordMatcher>();
            if (keywordMatcher?.TakeManualChainOptionsRequest() == true)
            {
                try
                {
                    if (_ini.ManualChainRecordingEnabled) ManualKeywordChainPolicy.Apply(_store, _ini, _log, DateTime.Now);
                }
                catch
                {
                    keywordMatcher.RestoreManualChainOptionsRequest();
                    throw;
                }
            }

            if (Interlocked.Exchange(ref _disabledChainConvergenceRequested, 0) != 0
                && !_ini.UserChainRecordingEnabled)
            {
                try
                {
                    var lifecycle = Resolve<ChainLifecycleCoordinator>()
                        ?? throw new InvalidOperationException("Chain lifecycle coordinator is unavailable.");
                    var convergence = lifecycle.ConvergeDisabledFeature($"{request.Source}:{request.Action}");
                    if (!convergence.DetachApplied)
                        throw new InvalidOperationException($"Chain disable convergence failed: {convergence.Reason}");
                }
                catch
                {
                    Interlocked.Exchange(ref _disabledChainConvergenceRequested, 1);
                    throw;
                }
            }

            var userChainPairs = _store.GetChainPredecessors();
            var effectiveLater = _ini.LaterProgramPriority;
            var configuredChainFeature = _ini.UserChainRecordingEnabled;
            var continuousChainPlanning = ChainReservationContract.ShouldPlanContinuousCapture(
                configuredChainFeature, userChainPairs.Count);

            _log.Add("ALLOC_POLICY", "Effective",
                $"source={request.Source} action={request.Action} executionMode={request.ExecutionMode} later={effectiveLater} configuredChainFeature={configuredChainFeature} chainMode={_ini.ChainReservationMode} chainRecordingSetting={_ini.ChainRecordingEnabled} storedChainPairs={userChainPairs.Count} continuousChainPlanning={continuousChainPlanning} rule=common_allocation_route_contract");

            var evaluation = EvaluateWithSingleRetry(effectiveLater, continuousChainPlanning, configuredChainFeature, request);
            changes = evaluation.Changes.ToList();
            rowChanges = evaluation.RowChanges.ToList();
            allocationGeneration = evaluation.AllocationGeneration;
            retryRequired = evaluation.RetryRequired;
            allocationReason = evaluation.Reason;
        }

        if(chainPending) { retryRequired=true;allocationReason="chain_convergence_pending"; }
        if(promotionPending is not null) { retryRequired=true;allocationReason="projection_promotion_pending"; }

        if (request.RefreshPreRecordEpgEntries && !retryRequired)
        {
            var preRecordEntriesChanged = Resolve<EpgScheduler>()?.RefreshPreRecordEpgEntries() ?? false;
            if (preRecordEntriesChanged && request.ReevaluateAllocations)
            {
                var userChainPairs = _store.GetChainPredecessors();
                var effectiveLater = _ini.LaterProgramPriority;
                var configuredChainFeature = _ini.UserChainRecordingEnabled;
                var continuousChainPlanning = ChainReservationContract.ShouldPlanContinuousCapture(
                    configuredChainFeature, userChainPairs.Count);

                var finalEvaluation = EvaluateWithSingleRetry(effectiveLater, continuousChainPlanning, configuredChainFeature, request);
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

        return new CycleResult(result, changes, rowChanges, request.ReevaluateAllocations,
            deferredKeywordRefresh, deferredProgramSync, chainPending, promotionPending, promotionAttempted);
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
        if (lastCycle.Result.Deferred)
            result = result with { Deferred = true, Reason = lastCycle.Result.Reason };
        return new CycleResult(result, changes, rowChanges, latestAllocationResult is not null);
    }


    private void QueueFinalDerivedEffects(
        ReservationAllocationRouteRequest request,
        CycleResult finalCycle,
        ReservationMutationJournal? mutationJournal)
    {
        if (!request.IncludesOrdinaryWork && !finalCycle.AllocationAttempted
            && finalCycle.Result.Reason == "chain_command_rejected") return;

        if (!finalCycle.AllocationAttempted && finalCycle.RowChanges.Count == 0
            && (finalCycle.Result.Deferred || request.StopPreparationBarrier is not null)) return;

        var queuedAt = DateTime.Now;
        // The projection owns a fresh event/Wake scope after the synchronous operation ends.
        // Capturing AsyncLocal outbox/batch here would attach notifications to disposed scopes.
        var restoreFlow = !ExecutionContext.IsFlowSuppressed();
        var flow = restoreFlow ? ExecutionContext.SuppressFlow() : default;
        try
        {
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

        }
        finally
        {
            if (restoreFlow) flow.Undo();
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

        // Preserve already committed row projections, but leave Wake in the same deferred
        // queue while physical ownership is still held. Do not claim its generation early.
        if (request.RefreshWakeTask && StopPhaseGate.TryDeferWakeForPhysicalRelease(
            "AllocationDerived", msg => _log.Add("ALLOC_ROUTE", "PhysicalReleaseWake", msg)))
            request = request with { RefreshWakeTask = false };

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

    internal static ReservationAllocationRouteRequest? MergeStopResumeRetries(
        ReservationAllocationRouteRequest? left, ReservationAllocationRouteRequest? right)
        => left is null ? right : right is null || ReferenceEquals(left, right) ? left : MergeRequests(left, right);

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
                    : ReservationAllocationWakeRefreshMode.Immediate,
            TopologyCommands: MergeTopologyCommands(left.TopologyCommands, right.TopologyCommands))
        { IncludesOrdinaryWork = left.IncludesOrdinaryWork || right.IncludesOrdinaryWork,
            RetainRuleRefresh = left.RetainRuleRefresh || right.RetainRuleRefresh,
            RetainChainConvergence=left.RetainChainConvergence || right.RetainChainConvergence,
            RetainedProjectionPromotion=right.RetainedProjectionPromotion ?? left.RetainedProjectionPromotion,
            StopResumeRetry = MergeStopResumeRetries(left.StopResumeRetry, right.StopResumeRetry) };
    }

    internal static IReadOnlyList<ReservationTopologyCommand>? MergeTopologyCommands(
        IReadOnlyList<ReservationTopologyCommand>? left, IReadOnlyList<ReservationTopologyCommand>? right)
    {
        if (left is null && right is null) return null;
        return Array.AsReadOnly((left ?? Array.Empty<ReservationTopologyCommand>())
            .Concat(right ?? Array.Empty<ReservationTopologyCommand>()).Distinct().ToArray());
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
        bool continuousChainPlanning,
        bool configuredChainFeatureEnabled,
        ReservationAllocationRouteRequest request)
    {
        var first = _store.ReevaluateConflicts(
            _tunerProfiles,
            effectiveLater,
            continuousChainPlanning,
            configuredChainFeatureEnabled,
            _ini.PostEndMarginSeconds,
            _tunerPool,
            _ini.PreStartMarginSeconds);

        if (!first.RetryRequired) return first;

        _log.Add("ALLOC_ROUTE", "Retry",
            $"source={request.Source} action={request.Action} attempt=2 reason={first.Reason} allocationGeneration={first.AllocationGeneration} reservationSnapshotVersion={first.ReservationSnapshotVersion} tunerSnapshotVersion={first.TunerSnapshotVersion} rule=release_contract");

        return _store.ReevaluateConflicts(
            _tunerProfiles,
            effectiveLater,
            continuousChainPlanning,
            configuredChainFeatureEnabled,
            _ini.PostEndMarginSeconds,
            _tunerPool,
            _ini.PreStartMarginSeconds);
    }


    private T? Resolve<T>() where T : class
        => _serviceProvider.GetService<T>();
}
