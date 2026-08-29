using System.Collections.Concurrent;
using System.Diagnostics;

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
            DateTime.Now,
            null,
            CaptureIdentity(processId));
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
            DateTime.Now,
            null,
            CaptureIdentity(processId));
    }

    public static ManagedProcessIdentity RegisterViewer(int processId, string ownershipId, string? did, string? bonDriverFileName)
    {
        if (processId <= 0) return ManagedProcessIdentity.Unavailable(processId);
        var identity = CaptureIdentity(processId);
        Processes[processId] = new ManagedTvTestProcess(
            processId,
            ManagedTvTestProcessPurpose.Viewer,
            null,
            "Viewer",
            null,
            string.IsNullOrWhiteSpace(did) ? null : did.Trim(),
            string.IsNullOrWhiteSpace(bonDriverFileName) ? null : Path.GetFileName(bonDriverFileName.Trim()),
            null,
            DateTime.Now,
            string.IsNullOrWhiteSpace(ownershipId) ? null : ownershipId.Trim(),
            identity);
        return identity;
    }

    public static ManagedProcessIdentity CaptureIdentity(int processId)
    {
        if (processId <= 0) return ManagedProcessIdentity.Unavailable(processId);
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return ManagedProcessIdentity.Unavailable(processId);
            DateTime? startedAtUtc = null;
            string? executablePath = null;
            try { startedAtUtc = process.StartTime.ToUniversalTime(); } catch { }
            try { executablePath = NormalizeExecutablePath(process.MainModule?.FileName); } catch { }
            return new ManagedProcessIdentity(processId, startedAtUtc, executablePath, startedAtUtc.HasValue || !string.IsNullOrWhiteSpace(executablePath));
        }
        catch
        {
            return ManagedProcessIdentity.Unavailable(processId);
        }
    }

    public static bool IdentityMatches(ManagedProcessIdentity registered, ManagedProcessIdentity observed)
    {
        if (registered.ProcessId != observed.ProcessId) return false;
        if (!registered.IsAvailable || !observed.IsAvailable) return true;
        if (registered.ProcessStartTimeUtc.HasValue && observed.ProcessStartTimeUtc.HasValue &&
            registered.ProcessStartTimeUtc.Value != observed.ProcessStartTimeUtc.Value) return false;
        if (!string.IsNullOrWhiteSpace(registered.ExecutablePath) && !string.IsNullOrWhiteSpace(observed.ExecutablePath) &&
            !string.Equals(registered.ExecutablePath, observed.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string? NormalizeExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(value.Trim()); }
        catch { return value.Trim(); }
    }

    public static bool TryGet(int processId, out ManagedTvTestProcess process)
        => Processes.TryGetValue(processId, out process!);

    public static IReadOnlyList<ManagedTvTestProcess> GetRecordings(int? reservationId = null)
    {
        return Processes.Values
            .Where(p => p.Purpose == ManagedTvTestProcessPurpose.DirectRecorder
                && (!reservationId.HasValue || p.ReservationId == reservationId.Value))
            .OrderBy(p => p.RegisteredAt)
            .ToList();
    }

    public static IReadOnlyList<ManagedTvTestProcess> GetAll()
    {
        return Processes.Values
            .OrderBy(p => p.RegisteredAt)
            .ThenBy(p => p.ProcessId)
            .ToList();
    }

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
    DateTime RegisteredAt,
    string? OwnershipId,
    ManagedProcessIdentity Identity)
{
    public bool IsActivityOnly => Purpose == ManagedTvTestProcessPurpose.ActivityKeeper;
    public bool IsViewer => Purpose == ManagedTvTestProcessPurpose.Viewer;
}


public sealed record ManagedProcessIdentity(int ProcessId, DateTime? ProcessStartTimeUtc, string? ExecutablePath, bool IsAvailable)
{
    public static ManagedProcessIdentity Unavailable(int processId) => new(processId, null, null, false);
}
