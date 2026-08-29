using System.Text.Json;
using Microsoft.Extensions.Hosting;
using TvAIr.Core;
using TvAIr.Epg;

namespace TvAIr.Schedule;

/// <summary>
/// release_contract: Wakeタスクから起動された2つ目の TvAIr.exe は本体として起動せず signal ファイルだけを残す。
/// 常駐中の本体はここで signal を拾い、予約/EPG 評価を既存プロセス側へ合流させる。
/// </summary>
public sealed class WakeSignalMonitorService : BackgroundService
{
    private readonly LogRepository _log;
    private readonly EpgScheduler _epgScheduler;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly SystemSleepInhibitionService _sleepInhibition;
    private readonly string _signalDir;
    private readonly HashSet<string> _processed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _systemEpgHandoffGate = new();
    private IDisposable? _systemEpgHandoffLease;
    private string _systemEpgHandoffSlotId = string.Empty;
    private DateTime _systemEpgHandoffDeadline;
    private DateTime _systemEpgHandoffScheduledStart;

    // The handoff is intentionally short-lived.  Once Scheduler.Daily has acquired its own
    // SystemRequired lease we release immediately; if it never starts, this deadline guarantees
    // that a stale wake signal can never keep the PC awake indefinitely.
    private static readonly TimeSpan SystemEpgHandoffGrace = TimeSpan.FromMinutes(2);

    public WakeSignalMonitorService(
        LogRepository log,
        EpgScheduler epgScheduler,
        ReservationAllocationRouteService allocationRoute,
        SystemSleepInhibitionService sleepInhibition)
    {
        _log = log;
        _epgScheduler = epgScheduler;
        _allocationRoute = allocationRoute;
        _sleepInhibition = sleepInhibition;
        _signalDir = Path.Combine(AppContext.BaseDirectory, "runtime", "wake-signals");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { Directory.CreateDirectory(_signalDir); } catch { }
        _log.Add("WAKE_SIGNAL", "MONITOR_START",
            $"dir={_signalDir} rule=release_contract");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                MaintainSystemEpgWakeHandoff();
                ProcessPendingSignals();
                MaintainSystemEpgWakeHandoff();
            }
            catch (Exception ex)
            {
                _log.Add("WAKE_SIGNAL", "MONITOR_ERROR",
                    $"message={Compact(ex.Message)} rule=release_contract");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        ReleaseSystemEpgWakeHandoff("monitor_stopping");
    }

    private void ProcessPendingSignals()
    {
        if (!Directory.Exists(_signalDir)) return;

        // Long-run ownership: _processed only suppresses duplicate handling while a signal file still exists.
        // Successfully deleted signal paths are no longer live ownership and must not accumulate for the
        // lifetime of the host process. This does not change signal cadence, validation, or allocation.
        _processed.RemoveWhere(path => !File.Exists(path));

        foreach (var file in Directory.EnumerateFiles(_signalDir, "*.signal").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!_processed.Add(file)) continue;
            WakeSignal signal;
            try
            {
                signal = ReadSignal(file);
            }
            catch (Exception ex)
            {
                _log.Add("WAKE_SIGNAL", "READ_FAILED",
                    $"file={Path.GetFileName(file)} message={Compact(ex.Message)} action=delete rule=release_contract");
                TryDelete(file);
                continue;
            }

            var kind = NormalizeKind(signal.Kind);

            if (kind == "STARTUP")
            {
                _log.Add("APP_SINGLE_INSTANCE_SIGNAL", "RECEIVED",
                    $"sourcePid={signal.SourcePid} file={Path.GetFileName(file)} action=existing_instance_confirmed requestTrayRecovery=True rule=release_contract");
                TryDelete(file);
                continue;
            }

            var validation = ValidateCurrentWakeSignal(signal);
            if (!validation.Accepted)
            {
                _log.Add("WAKE_SIGNAL", validation.Reason,
                    $"kind={kind} at={signal.AtText} generation={ValueOrLegacy(signal.Generation)} slotId={signal.SlotId} reservationId={signal.ReservationId} sourcePid={signal.SourcePid} file={Path.GetFileName(file)} activeGeneration={validation.ActiveGeneration} action=delete_signal_only rule=release_contract");
                TryDelete(file);
                continue;
            }

            _log.Add("WAKE_SIGNAL", "RECEIVED",
                $"kind={kind} at={signal.AtText} generation={ValueOrLegacy(signal.Generation)} slotId={signal.SlotId} reservationId={signal.ReservationId} sourcePid={signal.SourcePid} file={Path.GetFileName(file)} action=merge_existing_instance rule=release_contract");

            // A WAKE task may cover REC/PRE_EPG/SYSTEM_EPG together.  Only an active slot whose
            // authoritative coverage explicitly contains SYSTEM_EPG may bridge wake -> scheduled
            // EPG.  This prevents ordinary recording wake signals from acquiring this lease.
            TryAcquireSystemEpgWakeHandoff(signal);

            try
            {
                _epgScheduler.NotifyWakeSignal(kind, signal.AtText, "WakeSignalMonitor");

                // 録画/録画前EPG系は既存常駐プロセスの共通割り当てルートへ流して、復帰直後の状態評価を早める。
                if (kind is "WAKE" or "REC" or "PRE_EPG" or "EPG" or "RECOVERY")
                {
                    _allocationRoute.Run(new ReservationAllocationRouteRequest(
                        Source: "WakeSignal",
                        Action: $"WakeTask:{kind}",
                        RunKeywordMatcher: false,
                        SyncProgramRuleReservations: false,
                        ReevaluateAllocations: true,
                        RefreshPreRecordEpgEntries: true,
                        RefreshWakeTask: false,
                        EmitConflictLogs: true,
                        ConflictLogCategory: "WAKE_SIGNAL",
                        ConflictLogTitle: "Conflict"));
                }

                _log.Add("WAKE_SIGNAL", "MERGED",
                    $"kind={kind} at={signal.AtText} action=existing_instance_evaluated rule=release_contract");
            }
            catch (Exception ex)
            {
                _log.Add("WAKE_SIGNAL", "MERGE_FAILED",
                    $"kind={kind} at={signal.AtText} message={Compact(ex.Message)} rule=release_contract");
            }
            finally
            {
                TryDelete(file);
            }
        }
    }

    private void TryAcquireSystemEpgWakeHandoff(WakeSignal signal)
    {
        var coverage = ReadSystemEpgCoverage(signal.SlotId);
        if (coverage is null)
            return;

        var now = DateTime.Now;
        var deadline = coverage.ScheduledStart + SystemEpgHandoffGrace;
        if (now >= deadline)
        {
            _log.Add("SYSTEM_EPG_WAKE_HANDOFF", "SKIPPED",
                $"result=SKIPPED slotId={Safe(signal.SlotId)} scheduledStart={coverage.ScheduledStart:O} deadline={deadline:O} reason=deadline_already_passed rule=scheduled_epg_wake_power_handoff_contract");
            return;
        }

        lock (_systemEpgHandoffGate)
        {
            if (_systemEpgHandoffLease is not null
                && string.Equals(_systemEpgHandoffSlotId, signal.SlotId, StringComparison.OrdinalIgnoreCase))
            {
                _systemEpgHandoffDeadline = deadline;
                _systemEpgHandoffScheduledStart = coverage.ScheduledStart;
                return;
            }

            ReleaseSystemEpgWakeHandoffLocked("superseded_by_active_system_epg_slot");

            var generation = coverage.ScheduledStart.Ticks;
            if (!_sleepInhibition.TryAcquire(
                    owner: "WakeSignalMonitor.SYSTEM_EPG",
                    operation: "SYSTEM_EPG_WAKE_HANDOFF",
                    generation: generation,
                    out var lease,
                    out var error)
                || lease is null)
            {
                _log.Add("SYSTEM_EPG_WAKE_HANDOFF", "ACQUIRE_FAILED",
                    $"result=FAILED slotId={Safe(signal.SlotId)} scheduledStart={coverage.ScheduledStart:O} deadline={deadline:O} reason={Safe(error)} action=leave_existing_scheduler_contract_unchanged rule=scheduled_epg_wake_power_handoff_contract");
                return;
            }

            _systemEpgHandoffLease = lease;
            _systemEpgHandoffSlotId = signal.SlotId;
            _systemEpgHandoffScheduledStart = coverage.ScheduledStart;
            _systemEpgHandoffDeadline = deadline;
            _log.Add("SYSTEM_EPG_WAKE_HANDOFF", "ACQUIRED",
                $"result=ACQUIRED slotId={Safe(signal.SlotId)} scheduledStart={coverage.ScheduledStart:O} deadline={deadline:O} release=scheduled_daily_power_owner_or_deadline_or_plan_change scope=SYSTEM_EPG_only rule=scheduled_epg_wake_power_handoff_contract");
        }
    }

    private void MaintainSystemEpgWakeHandoff()
    {
        lock (_systemEpgHandoffGate)
        {
            if (_systemEpgHandoffLease is null)
                return;

            if (_epgScheduler.HasActiveScheduledDailyPowerOwner())
            {
                ReleaseSystemEpgWakeHandoffLocked("scheduled_daily_power_owner_active");
                return;
            }

            var currentCoverage = ReadSystemEpgCoverage(_systemEpgHandoffSlotId);
            if (currentCoverage is null
                || currentCoverage.ScheduledStart != _systemEpgHandoffScheduledStart)
            {
                ReleaseSystemEpgWakeHandoffLocked("active_wake_plan_changed");
                return;
            }

            if (DateTime.Now >= _systemEpgHandoffDeadline)
                ReleaseSystemEpgWakeHandoffLocked("deadline_elapsed_without_scheduled_run");
        }
    }

    private void ReleaseSystemEpgWakeHandoff(string reason)
    {
        lock (_systemEpgHandoffGate)
            ReleaseSystemEpgWakeHandoffLocked(reason);
    }

    private void ReleaseSystemEpgWakeHandoffLocked(string reason)
    {
        var lease = _systemEpgHandoffLease;
        if (lease is null)
            return;

        var slotId = _systemEpgHandoffSlotId;
        var scheduledStart = _systemEpgHandoffScheduledStart;
        var deadline = _systemEpgHandoffDeadline;
        _systemEpgHandoffLease = null;
        _systemEpgHandoffSlotId = string.Empty;
        _systemEpgHandoffScheduledStart = default;
        _systemEpgHandoffDeadline = default;

        try { lease.Dispose(); }
        finally
        {
            _log.Add("SYSTEM_EPG_WAKE_HANDOFF", "RELEASED",
                $"result=RELEASED slotId={Safe(slotId)} scheduledStart={(scheduledStart == default ? "-" : scheduledStart.ToString("O"))} deadline={(deadline == default ? "-" : deadline.ToString("O"))} reason={Safe(reason)} rule=scheduled_epg_wake_power_handoff_contract");
        }
    }

    private static SystemEpgWakeCoverage? ReadSystemEpgCoverage(string? slotId)
    {
        var normalizedSlotId = (slotId ?? string.Empty).Trim();
        if (normalizedSlotId.Length == 0)
            return null;

        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "runtime", "wake-active-coverage.txt");
            if (!File.Exists(file))
                return null;

            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string? task = null;
                string? systemEpgStart = null;
                string? covers = null;
                foreach (var rawPart in line.Split(';'))
                {
                    var part = rawPart.Trim();
                    var eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = part[..eq].Trim();
                    var value = part[(eq + 1)..].Trim();
                    if (key.Equals("task", StringComparison.OrdinalIgnoreCase)) task = value;
                    else if (key.Equals("systemEpgStart", StringComparison.OrdinalIgnoreCase)) systemEpgStart = value;
                    else if (key.Equals("covers", StringComparison.OrdinalIgnoreCase)) covers = value;
                }

                if (!string.Equals(task, normalizedSlotId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(covers)
                    || !covers.Contains(":SYSTEM_EPG:", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (string.IsNullOrWhiteSpace(systemEpgStart)
                    || systemEpgStart == "-"
                    || !DateTime.TryParse(systemEpgStart, null, System.Globalization.DateTimeStyles.RoundtripKind, out var scheduledStart))
                    return null;

                return new SystemEpgWakeCoverage(scheduledStart);
            }
        }
        catch { }

        return null;
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '_');

    private sealed record SystemEpgWakeCoverage(DateTime ScheduledStart);

    private static WakeSignal ReadSignal(string file)
    {
        var json = File.ReadAllText(file);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new WakeSignal(
            Kind: TryGetString(root, "kind"),
            AtText: TryGetString(root, "at"),
            Generation: TryGetString(root, "generation"),
            SlotId: TryGetString(root, "slotId"),
            ReservationId: TryGetString(root, "reservationId"),
            SourcePid: TryGetInt(root, "sourcePid"));
    }

    private static string TryGetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int TryGetInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : -1;

    private static string NormalizeKind(string? kind)
    {
        var k = (kind ?? "UNKNOWN").Trim().ToUpperInvariant().Replace('-', '_');
        return k switch
        {
            "SYSTEMEPG" or "SYSTEM_EPG" => "SYSTEM_EPG",
            "PREEPG" or "PRE_EPG" or "EPG" => "PRE_EPG",
            "REC" or "RECORD" => "REC",
            "RECOVERY" or "WAKE_RECOVERY" => "RECOVERY",
            "WAKE" or "WAKE_SLOT" => "WAKE",
            "STARTUP" or "APP_START" or "TRAY_RECOVER" => "STARTUP",
            _ => string.IsNullOrWhiteSpace(k) ? "UNKNOWN" : k
        };
    }

    private static WakeSignalValidation ValidateCurrentWakeSignal(WakeSignal signal)
    {
        var active = ReadActiveWakeGeneration();
        if (string.IsNullOrWhiteSpace(active))
            return new WakeSignalValidation(true, "ACCEPT_NO_ACTIVE_GENERATION", "none");
        if (string.IsNullOrWhiteSpace(signal.Generation)
            || !string.Equals(active.Trim(), signal.Generation.Trim(), StringComparison.OrdinalIgnoreCase))
            return new WakeSignalValidation(false, "STALE_GENERATION_IGNORED", active);

        var slotId = (signal.SlotId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(slotId))
            return new WakeSignalValidation(false, "STALE_SLOT_IGNORED", active);

        var slots = ReadActiveWakeSlots();
        if (slots.Count == 0)
            return new WakeSignalValidation(true, "ACCEPT_NO_ACTIVE_SLOT_LIST", active);

        return slots.Contains(slotId)
            ? new WakeSignalValidation(true, "ACCEPTED", active)
            : new WakeSignalValidation(false, "STALE_SLOT_IGNORED", active);
    }

    private static string ReadActiveWakeGeneration()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "runtime", "wake-active-generation.txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static HashSet<string> ReadActiveWakeSlots()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "runtime", "wake-active-slots.txt");
            return File.Exists(file)
                ? File.ReadLines(file).Select(x => x.Trim()).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static string ValueOrLegacy(string? value)
        => string.IsNullOrWhiteSpace(value) ? "legacy" : value.Trim();

    private static string Compact(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { }
    }

    private sealed record WakeSignalValidation(bool Accepted, string Reason, string ActiveGeneration);

    private sealed record WakeSignal(string Kind, string AtText, string Generation, string SlotId, string ReservationId, int SourcePid);
}
