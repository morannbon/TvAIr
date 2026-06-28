using System.Text.Json;
using Microsoft.Extensions.Hosting;
using TvAIr.Core;
using TvAIr.Epg;

namespace TvAIr.Schedule;

/// <summary>
/// v0.9.38: Wakeタスクから起動された2つ目の TvAIr.exe は本体として起動せず signal ファイルだけを残す。
/// 常駐中の本体はここで signal を拾い、予約/EPG 評価を既存プロセス側へ合流させる。
/// </summary>
public sealed class WakeSignalMonitorService : BackgroundService
{
    private readonly LogRepository _log;
    private readonly EpgScheduler _epgScheduler;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly string _signalDir;
    private readonly HashSet<string> _processed = new(StringComparer.OrdinalIgnoreCase);

    public WakeSignalMonitorService(
        LogRepository log,
        EpgScheduler epgScheduler,
        ReservationAllocationRouteService allocationRoute)
    {
        _log = log;
        _epgScheduler = epgScheduler;
        _allocationRoute = allocationRoute;
        _signalDir = Path.Combine(AppContext.BaseDirectory, "runtime", "wake-signals");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { Directory.CreateDirectory(_signalDir); } catch { }
        _log.Add("WAKE_SIGNAL", "MONITOR_START",
            $"dir={_signalDir} rule=v0.9.38_wake_fixed_slot_recovery_scheduler");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { ProcessPendingSignals(); }
            catch (Exception ex)
            {
                _log.Add("WAKE_SIGNAL", "MONITOR_ERROR",
                    $"message={Compact(ex.Message)} rule=v0.9.38_wake_fixed_slot_recovery_scheduler");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ProcessPendingSignals()
    {
        if (!Directory.Exists(_signalDir)) return;

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
                    $"file={Path.GetFileName(file)} message={Compact(ex.Message)} action=delete rule=v0.9.38_wake_fixed_slot_recovery_scheduler");
                TryDelete(file);
                continue;
            }

            var kind = NormalizeKind(signal.Kind);

            if (kind == "STARTUP")
            {
                _log.Add("APP_SINGLE_INSTANCE_SIGNAL", "RECEIVED",
                    $"sourcePid={signal.SourcePid} file={Path.GetFileName(file)} action=existing_instance_confirmed requestTrayRecovery=True rule=v0.11.122_process_lifecycle_tray_recovery_complete");
                TryDelete(file);
                continue;
            }

            var validation = ValidateCurrentWakeSignal(signal);
            if (!validation.Accepted)
            {
                _log.Add("WAKE_SIGNAL", validation.Reason,
                    $"kind={kind} at={signal.AtText} generation={ValueOrLegacy(signal.Generation)} slotId={signal.SlotId} reservationId={signal.ReservationId} sourcePid={signal.SourcePid} file={Path.GetFileName(file)} activeGeneration={validation.ActiveGeneration} action=delete_signal_only rule=v0.10.11_wake_plan_generation_slot_guard");
                TryDelete(file);
                continue;
            }

            _log.Add("WAKE_SIGNAL", "RECEIVED",
                $"kind={kind} at={signal.AtText} generation={ValueOrLegacy(signal.Generation)} slotId={signal.SlotId} reservationId={signal.ReservationId} sourcePid={signal.SourcePid} file={Path.GetFileName(file)} action=merge_existing_instance rule=v0.10.11_wake_plan_generation_slot_guard");

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
                    $"kind={kind} at={signal.AtText} action=existing_instance_evaluated rule=v0.9.38_wake_fixed_slot_recovery_scheduler");
            }
            catch (Exception ex)
            {
                _log.Add("WAKE_SIGNAL", "MERGE_FAILED",
                    $"kind={kind} at={signal.AtText} message={Compact(ex.Message)} rule=v0.9.38_wake_fixed_slot_recovery_scheduler");
            }
            finally
            {
                TryDelete(file);
            }
        }
    }

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
