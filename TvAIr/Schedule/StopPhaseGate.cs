/* release_contract RECORDING_START_DUE_QUIET_GUARD: 録画停止中/停止直後の録画開始系も共通ゲートで抑制する。 */
using System;
using System.Threading;

namespace TvAIr.Schedule
{
    /// <summary>
    /// 録画停止中に ALLOC_ROUTE / Wake再構築等が侵入して BonDriver へ
    /// 多重アクセスすることを防ぐための停止フェーズゲート。
    ///
    /// release_contract:
    /// 停止フェーズ中の遅延に加え、停止フェーズ終了直後の短い静穏期間も
    /// 共通入口で抑制する。停止処理自身が行う「停止後の1回だけの再評価」は
    /// ReservationAllocationRouteRequest.BypassStopPhaseGate で明示的に通す。
    /// </summary>
    internal static class StopPhaseGate
    {
        private static readonly object Sync = new();
        private static int _activeStops;
        private static int _deferredAllocRoute;
        private static int _deferredWakeRebuild;
        private static int _suppressedAllocRouteAfterExit;
        private static int _suppressedWakeRebuildAfterExit;
        private static DateTimeOffset _lastExit = DateTimeOffset.MinValue;
        private static DateTimeOffset _postStopQuietUntil = DateTimeOffset.MinValue;
        private const int PostStopQuietMs = 5_000;

        public static bool IsStopping
        {
            get
            {
                lock (Sync) return _activeStops > 0;
            }
        }

        public static bool IsPostStopQuietActive
        {
            get
            {
                lock (Sync) return DateTimeOffset.Now < _postStopQuietUntil;
            }
        }

        public static bool IsStopOrQuietActive
        {
            get
            {
                lock (Sync) return _activeStops > 0 || DateTimeOffset.Now < _postStopQuietUntil;
            }
        }

        public static int PostStopQuietMilliseconds => PostStopQuietMs;

        public static int ActiveStops
        {
            get
            {
                lock (Sync) return _activeStops;
            }
        }

        public static DateTimeOffset LastExit
        {
            get
            {
                lock (Sync) return _lastExit;
            }
        }

        public static IDisposable Enter(string reservationId, Action<string>? log = null)
        {
            lock (Sync)
            {
                _activeStops++;
                log?.Invoke($"STOP_PHASE_ENTER reservation={reservationId} activeStops={_activeStops}");
            }
            return new Releaser(reservationId, log);
        }

        public static bool TryDeferAllocRoute(string source, string action, Action<string>? log = null)
        {
            lock (Sync)
            {
                var now = DateTimeOffset.Now;
                if (_activeStops > 0)
                {
                    _deferredAllocRoute++;
                    log?.Invoke($"STOP_PHASE_DEFER kind=ALLOC_ROUTE source={source} action={action} activeStops={_activeStops} pendingAlloc={_deferredAllocRoute}");
                    return true;
                }

                if (now < _postStopQuietUntil)
                {
                    _suppressedAllocRouteAfterExit++;
                    var remainingMs = Math.Max(0, (int)(_postStopQuietUntil - now).TotalMilliseconds);
                    log?.Invoke($"STOP_PHASE_SUPPRESS kind=ALLOC_ROUTE source={source} action={action} reason=post_stop_quiet remainingMs={remainingMs} suppressedAlloc={_suppressedAllocRouteAfterExit}");
                    return true;
                }

                return false;
            }
        }

        public static bool TryDeferWakeRebuild(string context, Action<string>? log = null)
        {
            lock (Sync)
            {
                var now = DateTimeOffset.Now;
                if (_activeStops > 0)
                {
                    _deferredWakeRebuild++;
                    log?.Invoke($"STOP_PHASE_DEFER kind=WAKE context={context} activeStops={_activeStops} pendingWake={_deferredWakeRebuild}");
                    return true;
                }

                if (now < _postStopQuietUntil)
                {
                    _suppressedWakeRebuildAfterExit++;
                    var remainingMs = Math.Max(0, (int)(_postStopQuietUntil - now).TotalMilliseconds);
                    log?.Invoke($"STOP_PHASE_SUPPRESS kind=WAKE context={context} reason=post_stop_quiet remainingMs={remainingMs} suppressedWake={_suppressedWakeRebuildAfterExit}");
                    return true;
                }

                return false;
            }
        }

        public static bool TryDeferRecordingStart(string stage, string reservationIds, Action<string>? log = null, bool bypassPostStopQuiet = false)
        {
            lock (Sync)
            {
                var now = DateTimeOffset.Now;
                if (_activeStops > 0)
                {
                    log?.Invoke($"REC_DUE_SUPPRESS stage={stage} reservations={reservationIds} reason=stop_phase_active activeStops={_activeStops}");
                    return true;
                }

                if (now < _postStopQuietUntil)
                {
                    var remainingMs = Math.Max(0, (int)(_postStopQuietUntil - now).TotalMilliseconds);
                    if (bypassPostStopQuiet)
                    {
                        log?.Invoke($"REC_DUE_BYPASS stage={stage} reservations={reservationIds} reason=chain_boundary_restart_bypass_post_stop_quiet remainingMs={remainingMs}");
                        return false;
                    }

                    log?.Invoke($"REC_DUE_SUPPRESS stage={stage} reservations={reservationIds} reason=post_stop_quiet remainingMs={remainingMs}");
                    return true;
                }

                return false;
            }
        }

        public static (bool allocRoute, bool wakeRebuild, int allocCount, int wakeCount, int suppressedAllocCount, int suppressedWakeCount) ConsumeDeferred()
        {
            lock (Sync)
            {
                var alloc = _deferredAllocRoute;
                var wake = _deferredWakeRebuild;
                var suppressedAlloc = _suppressedAllocRouteAfterExit;
                var suppressedWake = _suppressedWakeRebuildAfterExit;
                _deferredAllocRoute = 0;
                _deferredWakeRebuild = 0;
                _suppressedAllocRouteAfterExit = 0;
                _suppressedWakeRebuildAfterExit = 0;
                return (alloc > 0, wake > 0, alloc, wake, suppressedAlloc, suppressedWake);
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly string _reservationId;
            private readonly Action<string>? _log;
            private int _disposed;

            public Releaser(string reservationId, Action<string>? log)
            {
                _reservationId = reservationId;
                _log = log;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                lock (Sync)
                {
                    if (_activeStops > 0) _activeStops--;
                    _lastExit = DateTimeOffset.Now;
                    var quietUntil = _lastExit.AddMilliseconds(PostStopQuietMs);
                    if (quietUntil > _postStopQuietUntil) _postStopQuietUntil = quietUntil;
                    _log?.Invoke($"STOP_PHASE_EXIT reservation={_reservationId} activeStops={_activeStops} pendingAlloc={_deferredAllocRoute} pendingWake={_deferredWakeRebuild} quietMs={PostStopQuietMs}");
                }
            }
        }
    }
}
