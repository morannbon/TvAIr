using System.Text.Json;
using TvAIr.Core;

namespace TvAIr.Epg;

/// <summary>
/// 定時EPGの日次ライフサイクルの唯一の永続正本。
/// Scheduled Dailyは放送波別の実行状態を持たず、1日1本のAll取得として計画・実行・終端する。
/// SystemDailyEpg予約行、Wake task、ログ、実行Task/CTSは投影または実行資源であり、
/// それらから定時EPGの実行状態を逆算しない。
/// </summary>
internal sealed record DailyEpgRunState(
    DateTime Date,
    DailyEpgRunPhase State,
    DailyEpgConfigSnapshot Config,
    DateTime CanonicalStart,
    DateTime? PlannedStart,
    DateTime? PlannedEnd,
    DateTime MovementDeadline,
    int RequiredSeconds,
    DailyEpgPlacementState Placement,
    DateTime UpdatedAt);

internal enum DailyEpgRunPhase
{
    Planned,
    Running,
    Finalizing,
    Succeeded,
    Failed,
    Expired,
    Cancelled
}

/// <summary>日次runが使用する設定snapshot。Runningへ入った後は変更しない。</summary>
internal sealed record DailyEpgConfigSnapshot(
    bool Enabled,
    int CanonicalHour,
    int CanonicalMinute,
    string Depth)
{
    public DateTime CanonicalStartFor(DateTime date)
        => date.Date
            .AddHours(SettingsDefaults.NormalizeEpgHour(CanonicalHour))
            .AddMinutes(SettingsDefaults.NormalizeEpgMinute(CanonicalMinute));
}

internal enum DailyEpgPlacementState
{
    Placed,
    Unplaced
}

/// <summary>
/// Plannerへ渡す録画占有区間。Scheduled Daily AllはGR/BSCSどちらか一方でも録画占有があれば
/// その区間を使用しない。SafeStartは通常EPGが越えてはならない録画handoff境界。
/// </summary>
internal sealed record DailyEpgRecordingBlock(
    string Group,
    DateTime SafeStart,
    DateTime OccupyEnd,
    long ReservationId);

internal sealed record DailyEpgPlanResult(
    DateTime CanonicalStart,
    DateTime? PlannedStart,
    DateTime? PlannedEnd,
    DateTime MovementDeadline,
    int RequiredSeconds,
    DailyEpgPlacementState Placement,
    string Reason);

internal static class DailyEpgPlanSemantics
{
    public static bool SamePlan(DailyEpgRunState state, DailyEpgPlanResult plan)
        => state.CanonicalStart == plan.CanonicalStart
           && state.PlannedStart == plan.PlannedStart
           && state.PlannedEnd == plan.PlannedEnd
           && state.MovementDeadline == plan.MovementDeadline
           && state.RequiredSeconds == plan.RequiredSeconds
           && state.Placement == plan.Placement;
}

/// <summary>
/// DailyEpgRunStateのatomic persistenceだけを担当する。
/// 読み取れない実行時stateは破棄し、canonical scheduleから再生成する。
/// </summary>
internal sealed class DailyEpgRunStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string path;
    private readonly object fileGate = new();

    public DailyEpgRunStateStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory is required.", nameof(dataDirectory));
        path = Path.Combine(dataDirectory, "epg-daily-run-state.json");
    }

    public string PathForDiagnostics => path;

    public DailyEpgRunState? Load()
    {
        lock (fileGate)
        {
            DeleteStaleTempsLocked();
            if (!File.Exists(path)) return null;

            try
            {
                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<DailyEpgRunState>(json, JsonOptions);
                if (state is null) throw new InvalidDataException("Daily EPG run state is empty or null.");
                ValidateBasicShape(state);
                return state;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                File.Delete(path);
                return null;
            }
        }
    }


    private void WriteStateLocked(DailyEpgRunState nextState)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        DeleteStaleTempsLocked();
        var temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(JsonSerializer.Serialize(nextState, JsonOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public void Save(DailyEpgRunState nextState)
    {
        ArgumentNullException.ThrowIfNull(nextState);
        ValidateBasicShape(nextState);
        lock (fileGate)
            WriteStateLocked(nextState);
    }

    public void Delete()
    {
        lock (fileGate)
        {
            DeleteStaleTempsLocked();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private void DeleteStaleTempsLocked()
    {
        var directory = System.IO.Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return;
        var fileName = System.IO.Path.GetFileName(path);
        foreach (var stale in Directory.EnumerateFiles(directory, fileName + ".tmp*"))
        {
            try { File.Delete(stale); } catch { }
        }
    }

    private static void ValidateBasicShape(DailyEpgRunState state)
    {
        if (state.Date == default) throw new InvalidDataException("Daily EPG run state has no date.");
        if (state.Config is null) throw new InvalidDataException("Daily EPG run state has no config snapshot.");
        if (state.MovementDeadline < state.CanonicalStart)
            throw new InvalidDataException("Daily EPG movement deadline precedes canonical start.");
        if (state.RequiredSeconds < 0)
            throw new InvalidDataException("Daily EPG required duration cannot be negative.");
    }
}
