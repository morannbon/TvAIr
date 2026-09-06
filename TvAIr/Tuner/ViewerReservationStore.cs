using System.Text.Json;
using TvAIr.Core;
using TvAIr.Epg.Projection;
using TvAIr.Plugin;
using TvAIrPlugin;

namespace TvAIr.Tuner;

public enum ViewerReservationState
{
    Scheduled,
    Completed,
    Cancelled,
    Failed
}

public sealed class ViewerReservationStore
{
    public static readonly TimeSpan MaxFutureHorizon = TimeSpan.FromHours(6);
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly LogRepository _log;
    private readonly IProgramEventSource _programEvents;
    private readonly ViewerOperationService _viewerOperations;
    private Dictionary<string, ViewerReservationRecord> _rows;

    public ViewerReservationStore(Database database, LogRepository log, IProgramEventSource programEvents, ViewerOperationService viewerOperations)
    {
        _log = log;
        _programEvents = programEvents;
        _viewerOperations = viewerOperations;
        Directory.CreateDirectory(database.DataDirectory);
        _filePath = Path.Combine(database.DataDirectory, "viewer-reservations.json");
        _rows = Load();
        PruneTerminalUnsafe(DateTimeOffset.Now, saveWhenChanged: true);
    }

    public TvAirViewerReservationMutationResultDto Create(string pluginId, TvAirViewerReservationCreateRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var owner = PluginIdentity.Normalize(pluginId);
        var now = DateTimeOffset.Now;
        var profile = (request.ViewerProfileId ?? string.Empty).Trim();
        if (profile.Length == 0)
            return Fail("invalidViewerProfile", "ViewerProfileId is required.");
        if (!TryTriplet(request.NetworkId, request.TransportStreamId, request.ServiceId, out var nid, out var tsid, out var sid) || request.EventId is <= 0 or > ushort.MaxValue)
            return Fail("invalidEventIdentity", "Service/Event identity is incomplete or out of range.");
        var eventId = (ushort)request.EventId;
        var projected = _programEvents.GetByEventKey(nid, tsid, sid, eventId);
        if (projected is null)
            return Fail("eventIdentityNotFound", "The selected program can no longer be resolved by exact Event identity.");
        var canonicalStart = ToOffset(projected.Start);
        var canonicalEnd = ToOffset(projected.End);
        if (canonicalStart <= now)
            return Fail("scheduledStartNotFuture", "The selected program is no longer in the future.");
        if (canonicalStart - now > MaxFutureHorizon)
            return Fail("scheduledStartTooFar", $"Viewer reservations may be scheduled up to {MaxFutureHorizon.TotalHours:0} hours ahead.");
        if (canonicalEnd <= canonicalStart)
            return Fail("invalidScheduledRange", "The selected program has an invalid scheduled range.");

        var prepare = _viewerOperations.Prepare(new ViewerOperationPreparationRequest(
            owner, "viewerReservationRegistration", profile, null, null, nid, tsid, sid, null, null, true, "preserve", false));
        if (!prepare.Success)
            return Fail(string.IsNullOrWhiteSpace(prepare.ErrorCode) ? "viewerTargetUnavailable" : prepare.ErrorCode, prepare.Message);

        lock (_gate)
        {
            PruneTerminalUnsafe(now, saveWhenChanged: false);
            var conflict = _rows.Values.FirstOrDefault(x => ConflictsExecutionSlot(x, profile, canonicalStart));
            if (conflict is not null)
                return Fail("viewerReservationConflict", "Another viewer reservation already targets this ViewerProfile at the same scheduled start time.");

            var id = Guid.NewGuid().ToString("N");
            var row = new ViewerReservationRecord
            {
                ReservationId = id,
                OwnerPluginId = owner,
                ViewerProfileId = profile,
                NetworkId = nid,
                TransportStreamId = tsid,
                ServiceId = sid,
                EventId = eventId,
                ScheduledStart = canonicalStart,
                ScheduledEnd = canonicalEnd,
                State = ViewerReservationState.Scheduled,
                CreatedAt = now,
                UpdatedAt = now
            };
            _rows[id] = row;
            SaveUnsafe();
            _log.Add("VIEWER_RESERVATION_STORE", owner,
                $"result=CREATED reservationId={id} viewerProfile={Safe(profile)} nid={nid} tsid={tsid} sid={sid} eventId={row.EventId} scheduledStart={row.ScheduledStart:O} rule=viewer_reservation_contract");
            return Ok(row);
        }
    }

    public TvAirViewerReservationMutationResultDto Cancel(string pluginId, string reservationId)
    {
        var owner = PluginIdentity.Normalize(pluginId);
        var id = NormalizeId(reservationId);
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row)) return Fail("viewerReservationNotFound", "Viewer reservation was not found.");
            if (!string.Equals(row.OwnerPluginId, owner, StringComparison.OrdinalIgnoreCase))
                return Fail("viewerReservationOwnerMismatch", "This viewer reservation is owned by another plugin.");
            if (row.State != ViewerReservationState.Scheduled)
                return Fail("viewerReservationAlreadyTerminal", "Viewer reservation is already terminal.");
            row.State = ViewerReservationState.Cancelled;
            row.UpdatedAt = DateTimeOffset.Now;
            SaveUnsafe();
            return Ok(row);
        }
    }

    public IReadOnlyList<ViewerReservationRecord> ListOwned(string pluginId, string? viewerProfileId, bool includeTerminal)
    {
        var owner = PluginIdentity.Normalize(pluginId);
        var profile = viewerProfileId?.Trim();
        lock (_gate)
        {
            PruneTerminalUnsafe(DateTimeOffset.Now, saveWhenChanged: true);
            return _rows.Values
                .Where(x => string.Equals(x.OwnerPluginId, owner, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(profile) || string.Equals(x.ViewerProfileId, profile, StringComparison.OrdinalIgnoreCase))
                .Where(x => includeTerminal || x.State == ViewerReservationState.Scheduled)
                .OrderBy(x => x.ScheduledStart)
                .Select(x => x.Clone())
                .ToArray();
        }
    }

    public IReadOnlyList<ViewerReservationRecord> ListScheduled()
    {
        lock (_gate)
            return _rows.Values.Where(x => x.State == ViewerReservationState.Scheduled).OrderBy(x => x.ScheduledStart).Select(x => x.Clone()).ToArray();
    }

    public ViewerReservationRecord? Get(string reservationId)
    {
        var id = NormalizeId(reservationId);
        lock (_gate) return _rows.TryGetValue(id, out var row) ? row.Clone() : null;
    }

    public ViewerReservationFollowResult UpdateFollow(string reservationId, ProjectedProgramEvent projected)
    {
        var id = NormalizeId(reservationId);
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row) || row.State != ViewerReservationState.Scheduled) return ViewerReservationFollowResult.None;
            if (projected.NetworkId != row.NetworkId || projected.TransportStreamId != row.TransportStreamId || projected.ServiceId != row.ServiceId || projected.EventId != row.EventId)
                return ViewerReservationFollowResult.None;
            var projectedStart = ToOffset(projected.Start);
            var projectedEnd = ToOffset(projected.End);
            if (projectedEnd <= projectedStart)
            {
                row.State = ViewerReservationState.Failed;
                row.FailureReason = "invalidScheduledRangeAfterFollow";
                row.UpdatedAt = DateTimeOffset.Now;
                SaveUnsafe();
                return ViewerReservationFollowResult.Failed(row.Clone(), "The followed program has an invalid scheduled range.");
            }
            if (row.ScheduledStart == projectedStart && row.ScheduledEnd == projectedEnd) return ViewerReservationFollowResult.None;

            var conflict = _rows.Values.FirstOrDefault(x =>
                !string.Equals(x.ReservationId, row.ReservationId, StringComparison.OrdinalIgnoreCase) &&
                ConflictsExecutionSlot(x, row.ViewerProfileId, projectedStart));
            if (conflict is not null)
            {
                row.ScheduledStart = projectedStart;
                row.ScheduledEnd = projectedEnd;
                row.State = ViewerReservationState.Failed;
                row.FailureReason = "viewerReservationConflictAfterFollow";
                row.UpdatedAt = DateTimeOffset.Now;
                SaveUnsafe();
                return ViewerReservationFollowResult.Failed(row.Clone(), "EPG time following would target the same ViewerProfile at the same scheduled start time as another viewer reservation.");
            }

            row.ScheduledStart = projectedStart;
            row.ScheduledEnd = projectedEnd;
            row.UpdatedAt = DateTimeOffset.Now;
            SaveUnsafe();
            return ViewerReservationFollowResult.Updated(row.Clone());
        }
    }

    public ViewerReservationRecord? MarkTerminal(string reservationId, ViewerReservationState state, string? failureReason)
    {
        if (state == ViewerReservationState.Scheduled) throw new ArgumentOutOfRangeException(nameof(state));
        var id = NormalizeId(reservationId);
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row) || row.State != ViewerReservationState.Scheduled) return null;
            row.State = state;
            row.FailureReason = failureReason?.Trim() ?? string.Empty;
            row.UpdatedAt = DateTimeOffset.Now;
            SaveUnsafe();
            return row.Clone();
        }
    }

    public void PruneTerminal()
    {
        lock (_gate) PruneTerminalUnsafe(DateTimeOffset.Now, saveWhenChanged: true);
    }

    public static TvAirViewerReservationDto ToDto(ViewerReservationRecord row) => new()
    {
        ReservationId = row.ReservationId,
        OwnerPluginId = row.OwnerPluginId,
        ViewerProfileId = row.ViewerProfileId,
        NetworkId = row.NetworkId,
        TransportStreamId = row.TransportStreamId,
        ServiceId = row.ServiceId,
        EventId = row.EventId,
        ScheduledStart = row.ScheduledStart,
        ScheduledEnd = row.ScheduledEnd,
        State = row.State.ToString(),
        FailureReason = row.FailureReason,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };

    private void PruneTerminalUnsafe(DateTimeOffset now, bool saveWhenChanged)
    {
        var removed = _rows.Where(x => x.Value.State != ViewerReservationState.Scheduled && now - x.Value.UpdatedAt >= TerminalRetention).Select(x => x.Key).ToArray();
        if (removed.Length == 0) return;
        foreach (var id in removed) _rows.Remove(id);
        if (saveWhenChanged) SaveUnsafe();
        _log.Add("VIEWER_RESERVATION_STORE", "Host", $"result=PRUNED terminal={removed.Length} retentionMinutes={TerminalRetention.TotalMinutes:0} rule=viewer_reservation_contract");
    }

    private Dictionary<string, ViewerReservationRecord> Load()
    {
        if (!File.Exists(_filePath)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = JsonSerializer.Deserialize<List<ViewerReservationRecord>>(File.ReadAllText(_filePath), JsonOptions) ?? new();
            var loaded = rows.Where(x => !string.IsNullOrWhiteSpace(x.ReservationId)).GroupBy(x => x.ReservationId, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            _log.Add("VIEWER_RESERVATION_STORE_LOAD", "Host", $"result=OK count={loaded.Count} rule=viewer_reservation_contract");
            return loaded;
        }
        catch (Exception ex)
        {
            _log.Add("VIEWER_RESERVATION_STORE_LOAD", "Host", $"result=FAILED filePreserved=True reason={Safe(ex.Message)} rule=viewer_reservation_contract");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveUnsafe()
    {
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_rows.Values.OrderBy(x => x.ScheduledStart).ToArray(), JsonOptions));
        File.Move(temp, _filePath, true);
    }

    // Viewer Reservation is a future one-shot Viewer Operation, not a programme-duration resource lease.
    // ScheduledEnd remains programme metadata; only ViewerProfile + ScheduledStart owns an execution slot.
    private static bool ConflictsExecutionSlot(ViewerReservationRecord row, string viewerProfileId, DateTimeOffset scheduledStart)
        => (row.State is ViewerReservationState.Scheduled or ViewerReservationState.Completed)
           && string.Equals(row.ViewerProfileId, viewerProfileId, StringComparison.OrdinalIgnoreCase)
           && row.ScheduledStart == scheduledStart;
    private static TvAirViewerReservationMutationResultDto Ok(ViewerReservationRecord row) => new() { Success = true, Reservation = ToDto(row) };
    private static TvAirViewerReservationMutationResultDto Fail(string code, string message) => new() { Success = false, ErrorCode = code, Message = message };
    private static string NormalizeId(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    private static bool TryTriplet(int networkId, int transportStreamId, int serviceId, out ushort nid, out ushort tsid, out ushort sid)
    {
        nid = tsid = sid = 0;
        if (networkId is <= 0 or > ushort.MaxValue || transportStreamId is <= 0 or > ushort.MaxValue || serviceId is <= 0 or > ushort.MaxValue) return false;
        nid = (ushort)networkId; tsid = (ushort)transportStreamId; sid = (ushort)serviceId; return true;
    }
    private static DateTimeOffset ToOffset(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local))
            : new DateTimeOffset(value);

    private static string Safe(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r',' ').Replace('\n',' ').Trim();
        return text.Length <= 160 ? text : text[..160];
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}

public sealed class ViewerReservationRecord
{
    public string ReservationId { get; set; } = string.Empty;
    public string OwnerPluginId { get; set; } = string.Empty;
    public string ViewerProfileId { get; set; } = string.Empty;
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public DateTimeOffset ScheduledStart { get; set; }
    public DateTimeOffset? ScheduledEnd { get; set; }
    public ViewerReservationState State { get; set; }
    public string FailureReason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ViewerReservationRecord Clone() => (ViewerReservationRecord)MemberwiseClone();
}

public sealed class ViewerReservationFollowResult
{
    public static readonly ViewerReservationFollowResult None = new();
    public ViewerReservationRecord? UpdatedReservation { get; init; }
    public ViewerReservationRecord? FailedReservation { get; init; }
    public string Message { get; init; } = string.Empty;
    public static ViewerReservationFollowResult Updated(ViewerReservationRecord row) => new() { UpdatedReservation = row };
    public static ViewerReservationFollowResult Failed(ViewerReservationRecord row, string message) => new() { FailedReservation = row, Message = message };
}
