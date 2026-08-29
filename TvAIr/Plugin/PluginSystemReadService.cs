using System.Reflection;
using TvAIr.Schedule;
using TvAIr.Tuner;

namespace TvAIr.Plugin;

internal sealed record PluginWakePlanSnapshotItem(
    DateTime At,
    string Kind,
    int? ReservationId,
    string Title,
    string TaskName);

internal sealed record PluginSystemStatusSnapshot(
    string Version,
    DateTime Now,
    int ReservationCount,
    int ActiveRecordingCount,
    int TunerCount,
    int FreeTunerCount,
    int WakePlanCount,
    DateTime? NextWakeAt);

/// <summary>
/// System status and wake-plan read model shared by the Runtime system API.
/// </summary>
internal sealed class PluginSystemReadService
{
    private readonly PluginReadModelSource _readModels;
    private readonly TaskSchedulerService _taskScheduler;

    public PluginSystemReadService(PluginReadModelSource readModels, TaskSchedulerService taskScheduler)
    {
        _readModels = readModels;
        _taskScheduler = taskScheduler;
    }

    public IReadOnlyList<PluginWakePlanSnapshotItem> GetWakePlan(DateTime? from, DateTime? to, int limit)
        => _taskScheduler.GetWakePlanSnapshot(from, to, Math.Clamp(limit, 1, 500))
            .Select(item => new PluginWakePlanSnapshotItem(
                item.At,
                item.Kind,
                item.ReservationId,
                item.Title,
                item.TaskName))
            .ToList();

    public PluginSystemStatusSnapshot GetStatus()
    {
        var tuners = _readModels.GetTunerStatus().ToList();
        var wake = GetWakePlan(null, null, 13);
        var activeRecordingIds = _readModels.GetActiveRecordingReservationIds();
        return new PluginSystemStatusSnapshot(
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? string.Empty,
            DateTime.Now,
            _readModels.GetReservations().Count(r => r.Source != Core.ReservationSource.Epg),
            activeRecordingIds.Count,
            tuners.Count,
            tuners.Count(s => s.UsageKind == TunerUsageKind.Free),
            wake.Count,
            wake.OrderBy(item => item.At).FirstOrDefault()?.At);
    }
}
