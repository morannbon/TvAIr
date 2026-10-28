/* release_contract RECORDING_STOP_PHASE_GUARD: 録画停止処理中だけ開始・割当・Wake再構築を共通ゲートで遅延する。 */
using System;
using System.Collections.Generic;
using System.Threading;

namespace TvAIr.Schedule
{
    /// <summary>
    /// 録画停止処理中に ALLOC_ROUTE / Wake再構築 / 録画開始が侵入して、
    /// lease解放前のチューナー状態を基準に処理されることを防ぐ停止フェーズゲート。
    /// 停止完了後は、停止処理自身の再評価とチューナースロット単位のクールダウンを正本とし、
    /// 全体を固定時間抑制しない。
    /// </summary>
    internal static class StopPhaseGate
    {
        private static readonly object Sync = new();
        private static int _activeStops;
        private static int _physicalReleaseBarriers;
        private static readonly Dictionary<string, int> ActiveStopResources = new(StringComparer.OrdinalIgnoreCase);
        private static int _deferredAllocRoute;
        private static int _deferredWakeRebuild;
        private static ReservationAllocationRouteRequest? _deferredAllocationRequest;

        public static bool IsStopping
        {
            get
            {
                lock (Sync) return _activeStops > 0;
            }
        }

        public static int ActiveStops
        {
            get
            {
                lock (Sync) return _activeStops;
            }
        }

        public static IDisposable Enter(string reservationId, string? resourceKey, Action<string>? log = null)
            => EnterCore(reservationId, resourceKey, log, null);

        // The caller must transfer this scope to the existing physical-stop lifecycle.
        // No timer or automatic replay owns its lifetime.
        public static PhysicalReleaseBarrier EnterPhysicalReleaseBarrier(
            int reservationId, string? resourceKey, Action<string>? log = null)
        {
            if (reservationId <= 0) throw new ArgumentOutOfRangeException(nameof(reservationId));
            return (PhysicalReleaseBarrier)EnterCore($"R{reservationId}", resourceKey, log, reservationId);
        }

        private static IDisposable EnterCore(string reservationId, string? resourceKey, Action<string>? log,
            int? physicalReservationId)
        {
            var physicalReleaseBarrier = physicalReservationId.HasValue;
            IDisposable scope = physicalReleaseBarrier
                ? new PhysicalReleaseBarrier(physicalReservationId!.Value, NormalizeResourceKey(resourceKey), log)
                : new Releaser(reservationId, NormalizeResourceKey(resourceKey), log);
            string message;
            lock (Sync)
            {
                _activeStops++;
                if (physicalReleaseBarrier) _physicalReleaseBarriers++;
                var key = NormalizeResourceKey(resourceKey);
                ActiveStopResources[key] = ActiveStopResources.TryGetValue(key, out var count) ? count + 1 : 1;
                message = $"STOP_PHASE_ENTER reservation={reservationId} resource={key} activeStops={_activeStops}";
            }
            try { log?.Invoke(message); }
            catch
            {
                try { scope.Dispose(); } catch { }
                throw;
            }
            return scope;
        }

        public static bool TryDeferAllocRoute(ReservationAllocationRouteRequest request, Action<string>? log = null)
        {
            lock (Sync)
            {
                if (_activeStops <= 0) return false;
                if (request.StopPreparationBarrier is { } preparation && preparation.IsActiveLocked)
                    return false;
                if (request.BypassStopPhaseGate && _physicalReleaseBarriers == 0) return false;

                if (request.RejectDeferredTopologyCommands && request.TopologyCommands is { Count: > 0 })
                    return true; // rejected before enqueue; caller may retry after the stop

                _deferredAllocRoute++;
                _deferredAllocationRequest = MergeRequests(_deferredAllocationRequest, request);
                log?.Invoke($"STOP_PHASE_DEFER kind=ALLOC_ROUTE source={request.Source} action={request.Action} activeStops={_activeStops} pendingAlloc={_deferredAllocRoute} matcher={_deferredAllocationRequest.RunKeywordMatcher} syncProgram={_deferredAllocationRequest.SyncProgramRuleReservations} reevaluate={_deferredAllocationRequest.ReevaluateAllocations} preEpg={_deferredAllocationRequest.RefreshPreRecordEpgEntries} wake={_deferredAllocationRequest.RefreshWakeTask}");
                return true;
            }
        }

        public static bool TryDeferAllocRouteForPhysicalRelease(
            ReservationAllocationRouteRequest request, Action<string>? log = null)
        {
            lock (Sync)
            {
                if (_physicalReleaseBarriers == 0) return false;
                return TryDeferAllocRoute(request, log);
            }
        }

        public static bool TryDeferWakeForPhysicalRelease(string context, Action<string>? log = null)
        {
            lock (Sync)
            {
                if (_physicalReleaseBarriers == 0) return false;
                return TryDeferWakeRebuild(context, log);
            }
        }

        public static bool TryDeferWakeRebuild(string context, Action<string>? log = null)
        {
            lock (Sync)
            {
                if (_activeStops <= 0) return false;

                _deferredWakeRebuild++;
                log?.Invoke($"STOP_PHASE_DEFER kind=WAKE context={context} activeStops={_activeStops} pendingWake={_deferredWakeRebuild}");
                return true;
            }
        }

        public static bool TryDeferRecordingStart(string stage, string reservationIds, string? resourceKey, Action<string>? log = null)
        {
            lock (Sync)
            {
                if (_activeStops <= 0) return false;
                var key = NormalizeResourceKey(resourceKey);
                if (!ActiveStopResources.ContainsKey(key))
                {
                    log?.Invoke($"REC_DUE_CONTINUE stage={stage} reservations={reservationIds} resource={key} reason=other_tuner_stop_only activeStops={_activeStops}");
                    return false;
                }

                log?.Invoke($"REC_DUE_SUPPRESS stage={stage} reservations={reservationIds} resource={key} reason=same_tuner_stop_phase_active activeStops={_activeStops}");
                return true;
            }
        }

        public static (ReservationAllocationRouteRequest? allocationRequest, bool wakeRebuild, int allocCount, int wakeCount) ConsumeDeferred()
        {
            lock (Sync)
            {
                // A different worker may finish while this worker still owns its resource.
                // Keep requests in the existing queue until the last explicit barrier ends.
                if (_physicalReleaseBarriers > 0) return (null, false, 0, 0);
                var request = _deferredAllocationRequest;
                var alloc = _deferredAllocRoute;
                var wake = _deferredWakeRebuild;
                _deferredAllocationRequest = null;
                _deferredAllocRoute = 0;
                _deferredWakeRebuild = 0;
                return (request, wake > 0, alloc, wake);
            }
        }

        private static string NormalizeResourceKey(string? resourceKey)
            => string.IsNullOrWhiteSpace(resourceKey) ? "UNKNOWN" : resourceKey.Trim();

        private static ReservationAllocationRouteRequest MergeRequests(
            ReservationAllocationRouteRequest? current,
            ReservationAllocationRouteRequest incoming)
        {
            if (current is null)
            {
                return incoming with { BypassStopPhaseGate = true };
            }

            return new ReservationAllocationRouteRequest(
                Source: "StopPhaseDeferred",
                Action: "Merged",
                RunKeywordMatcher: current.RunKeywordMatcher || incoming.RunKeywordMatcher,
                SyncProgramRuleReservations: current.SyncProgramRuleReservations || incoming.SyncProgramRuleReservations,
                ReevaluateAllocations: current.ReevaluateAllocations || incoming.ReevaluateAllocations,
                RefreshPreRecordEpgEntries: current.RefreshPreRecordEpgEntries || incoming.RefreshPreRecordEpgEntries,
                RefreshWakeTask: current.RefreshWakeTask || incoming.RefreshWakeTask,
                BypassStopPhaseGate: true,
                EmitConflictLogs: current.EmitConflictLogs || incoming.EmitConflictLogs,
                ConflictLogCategory: "StopPhaseDeferred",
                ConflictLogTitle: "Conflict",
                ExecutionMode: "DeferredMerged",
                WakeRefreshMode: current.WakeRefreshMode == ReservationAllocationWakeRefreshMode.BoundedCoalesce
                    || incoming.WakeRefreshMode == ReservationAllocationWakeRefreshMode.BoundedCoalesce
                        ? ReservationAllocationWakeRefreshMode.BoundedCoalesce
                        : ReservationAllocationWakeRefreshMode.Immediate,
                TopologyCommands: ReservationAllocationRouteService.MergeTopologyCommands(
                    current.TopologyCommands, incoming.TopologyCommands))
            { IncludesOrdinaryWork = current.IncludesOrdinaryWork || incoming.IncludesOrdinaryWork,
                RetainRuleRefresh = current.RetainRuleRefresh || incoming.RetainRuleRefresh,
                RetainChainConvergence = current.RetainChainConvergence || incoming.RetainChainConvergence,
                RetainedProjectionPromotion = incoming.RetainedProjectionPromotion ?? current.RetainedProjectionPromotion,
                StopResumeRetry = ReservationAllocationRouteService.MergeStopResumeRetries(current.StopResumeRetry, incoming.StopResumeRetry) };
        }

        public sealed class PhysicalReleaseBarrier : Releaser
        {
            private bool _active = true;
            internal PhysicalReleaseBarrier(int reservationId, string resourceKey, Action<string>? log)
                : base($"R{reservationId}", resourceKey, log) => ReservationId = reservationId;
            public int ReservationId { get; }
            internal bool IsActiveLocked => _active;
            public bool IsActive { get { lock (Sync) return _active; } }
            protected override void ReleaseBarrierLocked()
            {
                _active = false;
                if (_physicalReleaseBarriers > 0) _physicalReleaseBarriers--;
            }
        }

        public class Releaser : IDisposable
        {
            private readonly string _reservationId;
            private readonly string _resourceKey;
            private readonly Action<string>? _log;
            private int _disposed;

            public Releaser(string reservationId, string resourceKey, Action<string>? log)
            {
                _reservationId = reservationId;
                _resourceKey = resourceKey;
                _log = log;
            }

            protected virtual void ReleaseBarrierLocked() { }

            public virtual void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                lock (Sync)
                {
                    ReleaseBarrierLocked();
                    if (_activeStops > 0) _activeStops--;
                    if (ActiveStopResources.TryGetValue(_resourceKey, out var count))
                    {
                        if (count <= 1) ActiveStopResources.Remove(_resourceKey);
                        else ActiveStopResources[_resourceKey] = count - 1;
                    }
                    _log?.Invoke($"STOP_PHASE_EXIT reservation={_reservationId} resource={_resourceKey} activeStops={_activeStops} pendingAlloc={_deferredAllocRoute} pendingWake={_deferredWakeRebuild}");
                }
            }
        }
    }
}
