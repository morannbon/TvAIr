using System.Text.Json;

namespace TvAIr.Core;

/// <summary>
/// Broadcast-wave clock correction based on TDT/TOT carried in the received TS.
/// This never changes Windows system time.  It keeps only a TvAIr-local offset used
/// by scheduling decisions and diagnostics.
/// </summary>
public sealed class BroadcastClockService
{
    private const string Rule = "broadcast_clock_passive_only_epg_orphan_safety";
    private static readonly TimeSpan MaxAcceptedOffset = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan OffsetValidity = TimeSpan.FromHours(6);
    private readonly LogRepository _log;
    private readonly string _statePath;
    private readonly object _gate = new();
    private BroadcastClockState? _state;

    public BroadcastClockService(LogRepository log)
    {
        _log = log;
        var dir = Path.Combine(AppContext.BaseDirectory, "runtime", "broadcast-clock");
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "broadcast-clock-state.json");
        _state = LoadStateSafe();
    }

    public DateTime Now => DateTime.Now;

    public TimeSpan GetValidOffsetOrZero()
    {
        // release_contract: 放送波時刻は観測専用。TvAIr内部offsetとしても使用しない。
        return TimeSpan.Zero;
    }

    public string GetStatusCompact()
    {
        lock (_gate)
        {
            if (_state is null) return "state=none offsetMs=0 valid=False";
            var ageSec = (int)Math.Max(0, (DateTimeOffset.UtcNow - _state.ObservedAtUtc).TotalSeconds);
            var valid = ageSec <= (int)OffsetValidity.TotalSeconds && Math.Abs(_state.OffsetMilliseconds) <= MaxAcceptedOffset.TotalMilliseconds;
            return $"state=available offsetMs={_state.OffsetMilliseconds} valid={valid} ageSec={ageSec} source={Safe(_state.Source)} group={Safe(_state.Group)} service={Safe(_state.ServiceName)} broadcastTime={_state.BroadcastTimeLocal:yyyy-MM-dd HH:mm:ss} observedLocal={_state.ObservedLocal:yyyy-MM-dd HH:mm:ss}";
        }
    }

    public void LogPreRecordClockState(string reservationLabel, string serviceName, string title)
    {
        var offset = GetValidOffsetOrZero();
        var status = GetStatusCompact();
        _log.Add("BROADCAST_CLOCK", reservationLabel,
            $"result=INFO use=passive_observation_only offsetMs=0 {status} action=do_not_adjust_windows_or_tvair_internal_time service={Safe(serviceName)} title={Safe(title)} rule={Rule}");
    }

    public BroadcastClockObservationResult ObserveFromTsFile(string? tsPath, string source, string group, string? serviceName, string? title)
    {
        if (string.IsNullOrWhiteSpace(tsPath) || !File.Exists(tsPath))
        {
            var missing = new BroadcastClockObservationResult(false, "NO_TS_FILE", null, 0, 0);
            LogObservation(missing, source, group, serviceName, title, tsPath);
            return missing;
        }

        try
        {
            var (clock, packetsScanned, sectionsSeen) = TryReadBroadcastTime(tsPath);
            if (clock is null)
            {
                var none = new BroadcastClockObservationResult(false, "NO_TDT_TOT", null, packetsScanned, sectionsSeen);
                LogObservation(none, source, group, serviceName, title, tsPath);
                return none;
            }

            var observedLocal = DateTime.Now;
            var offset = clock.Value - observedLocal;
            var offsetMs = (int)Math.Round(offset.TotalMilliseconds);
            if (Math.Abs(offset.TotalMilliseconds) > MaxAcceptedOffset.TotalMilliseconds)
            {
                var rejected = new BroadcastClockObservationResult(false, "OFFSET_TOO_LARGE", clock.Value, packetsScanned, sectionsSeen, offsetMs, StateUpdated: false);
                LogObservation(rejected, source, group, serviceName, title, tsPath);
                return rejected;
            }

            var canUpdateInternalOffset = AllowsInternalOffsetUpdate(source);
            if (canUpdateInternalOffset)
            {
                var state = new BroadcastClockState
                {
                    BroadcastTimeLocal = clock.Value,
                    ObservedLocal = observedLocal,
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                    OffsetMilliseconds = offsetMs,
                    Source = source,
                    Group = group,
                    ServiceName = serviceName ?? string.Empty
                };

                lock (_gate)
                {
                    _state = state;
                    SaveStateSafe(state);
                }
            }

            var ok = new BroadcastClockObservationResult(true, canUpdateInternalOffset ? "OK_INTERNAL_UPDATE_DISABLED" : "OBSERVED_NO_UPDATE_NORMAL_EPG", clock.Value, packetsScanned, sectionsSeen, offsetMs, StateUpdated: canUpdateInternalOffset);
            LogObservation(ok, source, group, serviceName, title, tsPath);
            return ok;
        }
        catch (Exception ex)
        {
            var error = new BroadcastClockObservationResult(false, ex.GetType().Name, null, 0, 0);
            _log.Add("BROADCAST_CLOCK", group,
                $"result=WARN source={Safe(source)} group={Safe(group)} service={Safe(serviceName)} title={Safe(title)} error={ex.GetType().Name}:{Safe(ex.Message)} action=keep_existing_offset_or_local_time rule={Rule}");
            return error;
        }
    }

    private void LogObservation(BroadcastClockObservationResult r, string source, string group, string? serviceName, string? title, string? tsPath)
    {
        var outcome = r.Accepted ? "OK" : "SKIP";
        var action = r.Accepted
            ? (r.StateUpdated ? "observe_only_no_internal_offset_update" : "observe_only_no_internal_offset_update")
            : "keep_existing_offset_or_local_time";
        _log.Add("BROADCAST_CLOCK", group,
            $"result={outcome} reason={Safe(r.Reason)} source={Safe(source)} group={Safe(group)} service={Safe(serviceName)} title={Safe(title)} broadcastTime={(r.BroadcastTimeLocal.HasValue ? r.BroadcastTimeLocal.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-")} offsetMs={(r.OffsetMilliseconds.HasValue ? r.OffsetMilliseconds.Value.ToString() : "-")} packetsScanned={r.PacketsScanned} sectionsSeen={r.SectionsSeen} path={SafePath(tsPath)} action={action} stateUpdated={r.StateUpdated} windowsTimeChanged=False rule={Rule}");
    }

    private static bool AllowsInternalOffsetUpdate(string source)
        => false;

    private static (DateTime? BroadcastTime, int PacketsScanned, int SectionsSeen) TryReadBroadcastTime(string path)
    {
        const int packetSize = 188;
        const int maxBytes = 16 * 1024 * 1024;
        var buffer = new byte[packetSize];
        var packets = 0;
        var sections = 0;
        using var fs = File.OpenRead(path);
        while (fs.Position < fs.Length && fs.Position < maxBytes)
        {
            var read = fs.Read(buffer, 0, packetSize);
            if (read < packetSize) break;
            packets++;
            if (buffer[0] != 0x47) continue;

            var pid = ((buffer[1] & 0x1F) << 8) | buffer[2];
            if (pid != 0x0014) continue; // TDT/TOT PID

            var payloadStart = (buffer[1] & 0x40) != 0;
            var afc = (buffer[3] >> 4) & 0x03;
            if (afc == 0 || afc == 2) continue;
            var pos = 4;
            if (afc == 3)
            {
                if (pos >= packetSize) continue;
                pos += 1 + buffer[pos];
            }
            if (pos >= packetSize) continue;
            if (payloadStart)
            {
                var pointer = buffer[pos];
                pos += 1 + pointer;
            }
            if (pos + 8 > packetSize) continue;

            var tableId = buffer[pos];
            if (tableId != 0x70 && tableId != 0x73) continue; // TDT / TOT
            sections++;
            var dt = DecodeMjdBcd(buffer.AsSpan(pos + 3, 5));
            if (dt != DateTime.MinValue) return (NormalizeBroadcastTimeCandidate(dt), packets, sections);
        }
        return (null, packets, sections);
    }

    private static DateTime DecodeMjdBcd(ReadOnlySpan<byte> b)
    {
        if (b.Length < 5 || b[0] == 0xFF) return DateTime.MinValue;
        var mjd = (b[0] << 8) | b[1];
        var y = (int)((mjd - 15078.2) / 365.25);
        var m = (int)((mjd - 14956.1 - (int)(y * 365.25)) / 30.6001);
        var day = mjd - 14956 - (int)(y * 365.25) - (int)(m * 30.6001);
        var k = (m == 14 || m == 15) ? 1 : 0;
        var year = y + k + 1900;
        var mon = m - 1 - k * 12;
        var h = Bcd(b[2]);
        var min = Bcd(b[3]);
        var sec = Bcd(b[4]);
        if (year < 2020 || year > 2100 || mon < 1 || mon > 12 || day < 1 || day > 31 || h > 23 || min > 59 || sec > 59)
            return DateTime.MinValue;
        try { return new DateTime(year, mon, day, h, min, sec, DateTimeKind.Local); }
        catch { return DateTime.MinValue; }
    }

    private static int Bcd(byte v) => ((v >> 4) & 0x0F) * 10 + (v & 0x0F);

    private static DateTime NormalizeBroadcastTimeCandidate(DateTime raw)
    {
        // ISDB運用ではJSTとして見える系統とUTCとして扱う系統の両方に備え、
        // 現在のPC時刻に近い方を TvAIr 内部補正候補にする。Windows時刻は変更しない。
        var asLocal = DateTime.SpecifyKind(raw, DateTimeKind.Local);
        var asUtcToLocal = DateTime.SpecifyKind(raw, DateTimeKind.Utc).ToLocalTime();
        var now = DateTime.Now;
        return Math.Abs((asUtcToLocal - now).TotalSeconds) < Math.Abs((asLocal - now).TotalSeconds)
            ? asUtcToLocal
            : asLocal;
    }

    private BroadcastClockState? LoadStateSafe()
    {
        try
        {
            if (!File.Exists(_statePath)) return null;
            return JsonSerializer.Deserialize<BroadcastClockState>(File.ReadAllText(_statePath));
        }
        catch { return null; }
    }

    private void SaveStateSafe(BroadcastClockState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statePath, json);
        }
        catch { }
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("\r", " ").Replace("\n", " ");
    private static string SafePath(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : Safe(value);

    private sealed class BroadcastClockState
    {
        public DateTime BroadcastTimeLocal { get; set; }
        public DateTime ObservedLocal { get; set; }
        public DateTimeOffset ObservedAtUtc { get; set; }
        public int OffsetMilliseconds { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
    }
}

public sealed record BroadcastClockObservationResult(
    bool Accepted,
    string Reason,
    DateTime? BroadcastTimeLocal,
    int PacketsScanned,
    int SectionsSeen,
    int? OffsetMilliseconds = null,
    bool StateUpdated = false);
