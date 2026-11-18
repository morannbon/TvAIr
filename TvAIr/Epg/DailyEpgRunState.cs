using System.Text.Json;
using TvAIr.Core;

namespace TvAIr.Epg;

/// <summary>
/// 定時EPGの日次ライフサイクルの唯一の永続正本。
/// Scheduled Dailyは1日1本のAll取得として計画・実行・終端する。
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
/// Plannerへ渡す録画占有区間。Daily Allは全録画予約の占有期間を一括して避ける。
/// SafeStartは通常EPGが越えてはならない録画handoff境界。
/// </summary>
internal sealed record DailyEpgRecordingBlock(
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

/// <summary>
/// Daily Allの完走期限と必要時間から、開始できる最後の時刻を一度だけ定義する。
/// Planner・失効commit・待機解除の全経路が同じ境界を使う。
/// </summary>
internal static class DailyEpgExecutionWindow
{
    public static DateTime LastPossibleStart(DateTime movementDeadline, int requiredSeconds)
        => requiredSeconds > 0
            ? movementDeadline.AddSeconds(-requiredSeconds)
            : movementDeadline;

    // 未配置でも最後の開始時刻までは録画停止等による再配置を許す。
    // その時刻の再計画でも配置できなければ、以後は完走不能として終端する。
    public static bool IsUnplacedExhausted(DailyEpgRunState state, DateTime now)
        => state.State == DailyEpgRunPhase.Planned
           && state.Date.Date == now.Date
           && state.Placement == DailyEpgPlacementState.Unplaced
           && now >= LastPossibleStart(state.MovementDeadline, state.RequiredSeconds);
}

/// <summary>
/// Expiredは「前の取得深度では時間内に完走不能」という未開始の結果。
/// 明示取消・失敗・成功は不可逆とし、当日同じ定時枠の取得深度が短縮され、
/// 現在の録画占有と期限を再評価した結果、実行できる場合だけPlannedへ戻せる。
/// 単なる予約取消・録画停止・設定以外の再評価では復帰しない。
/// </summary>
internal static class DailyEpgDepthReplanPolicy
{
    public static bool CanApplyDepthRevision(
        DailyEpgRunState current,
        DailyEpgConfigSnapshot nextConfig,
        DailyEpgPlanResult nextPlan,
        DateTime now)
        => current.State == DailyEpgRunPhase.Expired
           && current.Date.Date == now.Date
           && current.Config.Enabled && nextConfig.Enabled
           && current.Config.CanonicalHour == nextConfig.CanonicalHour
           && current.Config.CanonicalMinute == nextConfig.CanonicalMinute
           && !string.Equals(current.Config.Depth, nextConfig.Depth, StringComparison.OrdinalIgnoreCase)
           && nextPlan.RequiredSeconds > 0
           && current.CanonicalStart == nextPlan.CanonicalStart
           && current.MovementDeadline == nextPlan.MovementDeadline;

    public static bool CanReopen(
        DailyEpgRunState current,
        DailyEpgConfigSnapshot nextConfig,
        DailyEpgPlanResult nextPlan,
        DateTime now)
        => CanApplyDepthRevision(current, nextConfig, nextPlan, now)
           && current.RequiredSeconds > 0
           && nextPlan.RequiredSeconds < current.RequiredSeconds
           && nextPlan.Placement == DailyEpgPlacementState.Placed
           && nextPlan.PlannedStart.HasValue
           && nextPlan.PlannedEnd.HasValue
           && nextPlan.PlannedStart.Value >= now
           && nextPlan.PlannedEnd.Value <= nextPlan.MovementDeadline;
}

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
