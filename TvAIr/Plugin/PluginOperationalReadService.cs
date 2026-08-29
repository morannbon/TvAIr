using TvAIr.Core;
using TvAIr.Epg;

namespace TvAIr.Plugin;

/// <summary>
/// Shared operational read owner for plugin-facing logs, program-guide update history,
/// and current EPG execution state. Runtime APIs project their own DTOs
/// from the same snapshots without changing their observable contracts.
/// </summary>
internal sealed class PluginOperationalReadService
{
    private readonly LogRepository _log;
    private readonly EpgScheduler _epgScheduler;

    public PluginOperationalReadService(LogRepository log, EpgScheduler epgScheduler)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _epgScheduler = epgScheduler ?? throw new ArgumentNullException(nameof(epgScheduler));
    }

    public IReadOnlyList<LogEntry> GetAllLogs()
        => _log.GetAll();

    public IReadOnlyList<LogEntry> GetRecentLogs(int count)
        => _log.GetRecent(count);

    public IReadOnlyList<LogEntry> GetProgramGuideUpdateLogs()
        => _log.GetAll().Where(IsProgramGuideUpdateLog).ToList();

    public EpgRunState GetEpgRunState()
        => _epgScheduler.GetRunState();

    private static bool IsProgramGuideUpdateLog(LogEntry entry)
    {
        var text = $"{entry.Event} {entry.Title} {entry.Message}";
        return ContainsAny(text, "EPG_RUN_OK", "EPG_RUN_PARTIAL", "PROGRAM_GUIDE_UPDATED", "EPG_IMPORTED", "EPG_COMPLETION");
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}
