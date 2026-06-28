using System.Collections.Concurrent;

namespace TvAIr.Core;

/// <summary>
/// TvAIr が起動・所有している TVTest 系 PID の軽量レジストリ。
/// DirectRecorder本線ではTVTestに録画本体を任せないが、録画/EPG中のタスクバー表示と
/// SleepGuard監視用にTvAIr所有TVTestを起動するため、外部視聴プロセスと誤分類しないようにする。
/// </summary>
public static class TvAirManagedProcessRegistry
{
    private static readonly ConcurrentDictionary<int, ManagedTvTestProcess> Processes = new();

    public static void RegisterRecording(int processId, int reservationId, string? did, string? bonDriverFileName, string? recordingFilePath)
    {
        if (processId <= 0) return;
        Processes[processId] = new ManagedTvTestProcess(
            processId,
            ManagedTvTestProcessPurpose.DirectRecorder,
            reservationId,
            null,
            null,
            did,
            bonDriverFileName,
            recordingFilePath,
            DateTime.Now);
    }

    public static void RegisterActivity(int processId, string reason, string? serviceName)
    {
        if (processId <= 0) return;
        Processes[processId] = new ManagedTvTestProcess(
            processId,
            ManagedTvTestProcessPurpose.ActivityKeeper,
            null,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            string.IsNullOrWhiteSpace(serviceName) ? null : serviceName.Trim(),
            null,
            null,
            null,
            DateTime.Now);
    }

    public static void RegisterViewer(int processId, string? did, string? bonDriverFileName)
    {
        if (processId <= 0) return;
        Processes[processId] = new ManagedTvTestProcess(
            processId,
            ManagedTvTestProcessPurpose.Viewer,
            null,
            "Viewer",
            null,
            string.IsNullOrWhiteSpace(did) ? null : did.Trim(),
            string.IsNullOrWhiteSpace(bonDriverFileName) ? null : Path.GetFileName(bonDriverFileName.Trim()),
            null,
            DateTime.Now);
    }

    public static bool TryGet(int processId, out ManagedTvTestProcess process)
        => Processes.TryGetValue(processId, out process!);

    public static IReadOnlyList<ManagedTvTestProcess> GetViewers(string? did = null, string? bonDriverFileName = null)
    {
        var normalizedDid = string.IsNullOrWhiteSpace(did) ? string.Empty : did.Trim();
        var normalizedBon = string.IsNullOrWhiteSpace(bonDriverFileName) ? string.Empty : Path.GetFileName(bonDriverFileName.Trim());
        return Processes.Values
            .Where(p => p.IsViewer
                && (string.IsNullOrWhiteSpace(normalizedDid) || string.Equals(p.Did ?? string.Empty, normalizedDid, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(normalizedBon) || string.Equals(Path.GetFileName(p.BonDriverFileName ?? string.Empty), normalizedBon, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.RegisteredAt)
            .ToList();
    }

    public static void Unregister(int processId)
    {
        if (processId <= 0) return;
        Processes.TryRemove(processId, out _);
    }
}

public enum ManagedTvTestProcessPurpose
{
    DirectRecorder,
    ActivityKeeper,
    Viewer
}

public sealed record ManagedTvTestProcess(
    int ProcessId,
    ManagedTvTestProcessPurpose Purpose,
    int? ReservationId,
    string? ActivityReason,
    string? ActivityServiceName,
    string? Did,
    string? BonDriverFileName,
    string? RecordingFilePath,
    DateTime RegisteredAt)
{
    public bool IsActivityOnly => Purpose == ManagedTvTestProcessPurpose.ActivityKeeper;
    public bool IsViewer => Purpose == ManagedTvTestProcessPurpose.Viewer;
}
