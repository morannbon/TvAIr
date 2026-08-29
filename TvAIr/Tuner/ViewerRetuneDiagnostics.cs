using System.Collections.Concurrent;
using System.Globalization;

namespace TvAIr.Tuner;

/// <summary>
/// Shared classification and delayed-death tracking for profile-scoped viewer retunes.
/// All viewer operation entry points use this single source.
/// </summary>
public static class ViewerRetuneFailureClassifier
{
    public static bool IsProcessLost(string? errorCode)
        => string.Equals(errorCode, "process_exited", StringComparison.Ordinal)
            || string.Equals(errorCode, "process_not_found", StringComparison.Ordinal);
}

public static class ViewerRetuneDelayedDeathAudit
{
    private sealed record State(DateTime RetunedAt, ushort NetworkId, ushort TransportStreamId, ushort ServiceId, string Group, string Did, string BonDriver);

    private static readonly ConcurrentDictionary<int, State> LastRetuneByPid = new();
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    // 長期連続稼働保護: この監査状態は直近の再同調後にTVTestが遅延死したかを判定するためだけの一時情報。
    // 正常停止・再起動で必ず除去し、異常経路でも24時間を超えたPIDを保持しない。

    public static void MarkRetuned(int pid, ushort networkId, ushort transportStreamId, ushort serviceId, string? group, string? did, string? bonDriver)
    {
        if (pid <= 0) return;
        PruneExpired(DateTime.Now);
        LastRetuneByPid[pid] = new State(
            DateTime.Now,
            networkId,
            transportStreamId,
            serviceId,
            string.IsNullOrWhiteSpace(group) ? "-" : group.Trim(),
            string.IsNullOrWhiteSpace(did) ? "-" : did.Trim(),
            string.IsNullOrWhiteSpace(bonDriver) ? "-" : Path.GetFileName(bonDriver.Trim()));
    }

    public static void MarkProcessEnded(int pid)
    {
        if (pid > 0)
            LastRetuneByPid.TryRemove(pid, out _);
    }

    private static void PruneExpired(DateTime now)
    {
        foreach (var pair in LastRetuneByPid)
        {
            if (now - pair.Value.RetunedAt > Retention)
                LastRetuneByPid.TryRemove(pair.Key, out _);
        }
    }

    public static DelayedDeathResult MarkDetectedDead(int pid)
    {
        var detectedAt = DateTime.Now;
        PruneExpired(detectedAt);
        if (pid <= 0 || !LastRetuneByPid.TryRemove(pid, out var state))
        {
            return new DelayedDeathResult(
                "UNKNOWN_LAST_RETUNE", "-", detectedAt.ToString("O", CultureInfo.InvariantCulture),
                "-", "-", "-", "-", "-", "-", "-");
        }

        var elapsedMs = Math.Max(0, (long)(detectedAt - state.RetunedAt).TotalMilliseconds);
        return new DelayedDeathResult(
            "DETECTED",
            state.RetunedAt.ToString("O", CultureInfo.InvariantCulture),
            detectedAt.ToString("O", CultureInfo.InvariantCulture),
            elapsedMs.ToString(CultureInfo.InvariantCulture),
            state.NetworkId.ToString(CultureInfo.InvariantCulture),
            state.TransportStreamId.ToString(CultureInfo.InvariantCulture),
            state.ServiceId.ToString(CultureInfo.InvariantCulture),
            state.Group,
            state.Did,
            state.BonDriver);
    }
}

public sealed record DelayedDeathResult(
    string Result,
    string LastRetuneAtText,
    string DetectedDeadAtText,
    string ElapsedMsText,
    string NetworkIdText,
    string TransportStreamIdText,
    string ServiceIdText,
    string Group,
    string Did,
    string BonDriver);
