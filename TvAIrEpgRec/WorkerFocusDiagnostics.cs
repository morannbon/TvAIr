#if TVAIR_DEVELOPER_DIAGNOSTICS
using TvAIr.Workers;

namespace TvAIrEpgRec;

internal static class WorkerFocusDiagnostics
{
    private const int PendingCapacity = 64;
    private static readonly object Gate = new();
    private static readonly Queue<string> Pending = new();
    private static IntPtr _window;
    private static long _sequence;
    private static int _dropped;

    internal static void SetWindow(IntPtr window) => Interlocked.Exchange(ref _window, window);

    internal static void Record(string phase)
    {
        try
        {
            var snapshot = WorkerFocusSnapshot.Capture(Interlocked.CompareExchange(ref _window, IntPtr.Zero, IntPtr.Zero));
            lock (Gate)
            {
                if (Pending.Count == PendingCapacity)
                {
                    Pending.Dequeue();
                    _dropped++;
                }
                Pending.Enqueue($"sequence={++_sequence} at={DateTimeOffset.UtcNow:O} phase={phase} {snapshot} observerOnly=True");
            }
        }
        catch { }
    }

    // Queued UI observations precede the canonical progress row in the same write.
    // The final progress stage therefore retains its existing lifecycle meaning.
    internal static string[] Drain(string stage)
    {
        if (stage is "job_loaded" or "started" or "completed" or "failed" or "cancelled"
            || stage.Contains("open_tuner", StringComparison.OrdinalIgnoreCase)
            || stage.Contains("set_channel", StringComparison.OrdinalIgnoreCase)
            || stage.Contains("chain_capture_sink", StringComparison.OrdinalIgnoreCase)
            || stage.Contains("shutdown", StringComparison.OrdinalIgnoreCase)
            || stage.Contains("native_boundary", StringComparison.OrdinalIgnoreCase))
            Record("progress_" + stage);
        lock (Gate)
        {
            var values = Pending.ToArray();
            Pending.Clear();
            if (_dropped > 0)
            {
                values = values.Append($"phase=observer_overflow dropped={_dropped} observerOnly=True").ToArray();
                _dropped = 0;
            }
            return values;
        }
    }
}
#endif
