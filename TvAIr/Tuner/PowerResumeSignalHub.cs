namespace TvAIr.Tuner;

/// <summary>
/// Power/Tuner層と上位の業務schedulerを疎結合に保つためのプロセス内通知境界。
/// Suspend/Resumeの物理owner整合はPowerResumeTunerReconciliationServiceが所有し、
/// このhubは観測済みSuspendとResume reconciliation完了の事実だけを配送する。
/// 受信側の業務状態を直接変更しない。
/// </summary>
public sealed class PowerResumeSignalHub
{
    public event Action<string, DateTime>? SuspendObserved;
    public event Action<string, DateTime>? ResumeReconciled;

    internal void PublishSuspendObserved(string cycleId, DateTime observedAt)
        => PublishSafely(SuspendObserved, cycleId, observedAt);

    internal void PublishResumeReconciled(string cycleId, DateTime reconciledAt)
        => PublishSafely(ResumeReconciled, cycleId, reconciledAt);

    private static void PublishSafely(Action<string, DateTime>? handlers, string cycleId, DateTime at)
    {
        if (handlers is null) return;
        foreach (Action<string, DateTime> handler in handlers.GetInvocationList())
        {
            try { handler(cycleId, at); }
            catch
            {
                // 上位subscriberの失敗でPower/Tuner reconciliation自体を停止させない。
                // subscriber側が自身の業務ログを所有する。
            }
        }
    }
}
