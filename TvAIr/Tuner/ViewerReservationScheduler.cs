using Microsoft.Extensions.Hosting;
using TvAIr.Core;
using TvAIr.Epg.Projection;
using TvAIr.Plugin;
using TvAIrPlugin;

namespace TvAIr.Tuner;

/// <summary>
/// Host-owned future Viewer Operation scheduler. One bounded loop owns all viewer reservations;
/// no per-reservation long-running timers are created.
/// </summary>
public sealed class ViewerReservationScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ViewerPreparationLead = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MissedExecutionTolerance = TimeSpan.FromSeconds(30);
    private readonly ViewerReservationStore _store;
    private readonly IProgramEventSource _programEvents;
    private readonly ViewerOperationPreemptionHub _preemption;
    private readonly ViewerOperationService _viewerOperations;
    private readonly PluginTypedEventHub _typedEvents;
    private readonly LogRepository _log;
    private DateTimeOffset _lastFollowRefresh = DateTimeOffset.MinValue;

    public ViewerReservationScheduler(
        ViewerReservationStore store,
        IProgramEventSource programEvents,
        ViewerOperationPreemptionHub preemption,
        ViewerOperationService viewerOperations,
        PluginTypedEventHub typedEvents,
        LogRepository log)
    {
        _store = store;
        _programEvents = programEvents;
        _preemption = preemption;
        _viewerOperations = viewerOperations;
        _typedEvents = typedEvents;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { RunOnce(); }
            catch (Exception ex)
            {
                _log.Add("VIEWER_RESERVATION_SCHEDULER", "Host", $"result=ERROR error={Safe(ex.GetType().Name)}:{Safe(ex.Message)} action=continue_next_tick rule=viewer_reservation_contract");
            }

            try { await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private void RunOnce()
    {
        var now = DateTimeOffset.Now;
        var scheduled = _store.ListScheduled();
        if (scheduled.Count == 0)
        {
            _store.PruneTerminal();
            return;
        }

        if (now - _lastFollowRefresh >= TimeSpan.FromSeconds(30))
        {
            _lastFollowRefresh = now;
            foreach (var row in scheduled)
            {
                var projected = _programEvents.GetByEventKey(row.NetworkId, row.TransportStreamId, row.ServiceId, row.EventId);
                if (projected is null) continue;
                var follow = _store.UpdateFollow(row.ReservationId, projected);
                if (follow.FailedReservation is not null)
                {
                    PublishChanged(follow.FailedReservation, "Failed", operationId: null, follow.Message);
                    _log.Add("VIEWER_RESERVATION_TIME_FOLLOW", follow.FailedReservation.OwnerPluginId,
                        $"result=FAILED reservationId={follow.FailedReservation.ReservationId} viewerProfile={Safe(follow.FailedReservation.ViewerProfileId)} errorCode={Safe(follow.FailedReservation.FailureReason)} message={Safe(follow.Message)} retry=False rule=viewer_reservation_contract");
                }
                else if (follow.UpdatedReservation is not null)
                {
                    var updated = follow.UpdatedReservation;
                    PublishChanged(updated, "Updated", operationId: null);
                    _log.Add("VIEWER_RESERVATION_TIME_FOLLOW", updated.OwnerPluginId,
                        $"result=UPDATED reservationId={updated.ReservationId} viewerProfile={Safe(updated.ViewerProfileId)} nid={updated.NetworkId} tsid={updated.TransportStreamId} sid={updated.ServiceId} eventId={updated.EventId} scheduledStart={updated.ScheduledStart:O} scheduledEnd={(updated.ScheduledEnd.HasValue ? updated.ScheduledEnd.Value.ToString("O") : "-")} rule=viewer_reservation_contract");
                }
            }
            scheduled = _store.ListScheduled();
        }

        foreach (var row in scheduled
                     .Where(x => x.ScheduledStart - ViewerPreparationLead <= now)
                     .OrderBy(x => x.ScheduledStart))
            ExecuteDue(row, now);
        _store.PruneTerminal();
    }

    private void ExecuteDue(ViewerReservationRecord row, DateTimeOffset now)
    {
        // Exact EventIdentity is mandatory. Never replace by service/time/title similarity.
        var projected = _programEvents.GetByEventKey(row.NetworkId, row.TransportStreamId, row.ServiceId, row.EventId);
        if (projected is null)
        {
            Fail(row, "eventIdentityNotFound", "The scheduled program can no longer be resolved by exact Event identity.");
            return;
        }

        var projectedStart = ToOffset(projected.Start);
        var projectedEnd = projected.DurationSeconds > 0 ? projectedStart.AddSeconds(projected.DurationSeconds) : row.ScheduledEnd;
        if (projectedStart != row.ScheduledStart || projectedEnd != row.ScheduledEnd)
        {
            var follow = _store.UpdateFollow(row.ReservationId, projected);
            if (follow.FailedReservation is not null)
                PublishChanged(follow.FailedReservation, "Failed", null, follow.Message);
            else if (follow.UpdatedReservation is not null)
                PublishChanged(follow.UpdatedReservation, "Updated", null);
            return;
        }

        var executionAt = projectedStart - ViewerPreparationLead;
        if (now < executionAt)
            return;

        if (now - projectedStart > MissedExecutionTolerance)
        {
            Fail(row, "scheduledStartMissed", "Viewer reservation execution time was missed while the Host was unavailable or delayed.");
            return;
        }

        var operationId = $"viewer-reservation:{row.ReservationId}:{Guid.NewGuid():N}";
        var operation = _viewerOperations.EnsureTunedHostManaged(
            new ViewerOperationEnsureTunedRequest(
                row.OwnerPluginId,
                row.ViewerProfileId,
                row.NetworkId,
                row.TransportStreamId,
                row.ServiceId,
                ServiceName: null,
                GroupHint: null,
                PreserveViewerWindowState: true,
                // Use the same activation contract as a normal user-initiated cold Viewer Start.
                // The reservation scheduler owns only when the operation happens, not how TVTest is initialized.
                ViewerActivation: "activate",
                SourceDisplayName: "ViewerReservation",
                ActionName: "viewerReservation"),
            () => _preemption.Preempt(row.ViewerProfileId, operationId, "ViewerReservation", row.OwnerPluginId, row.ReservationId));

        if (!operation.Success || !operation.OperationCompleted)
        {
            Fail(row, string.IsNullOrWhiteSpace(operation.ErrorCode) ? "viewerOperationFailed" : operation.ErrorCode,
                string.IsNullOrWhiteSpace(operation.Message) ? "Viewer operation failed." : operation.Message,
                operationId);
            return;
        }

        // The reservation owns exactly one future Viewer Operation. Persist terminal success immediately
        // after that operation succeeds so EPG follow, manual tuning, zapping, or Host restart cannot replay it.
        var completed = _store.MarkTerminal(row.ReservationId, ViewerReservationState.Completed, null);
        if (completed is null) return;
        PublishChanged(completed, "Completed", operationId);
        _log.Add("VIEWER_RESERVATION_EXECUTION", completed.OwnerPluginId,
            $"result=COMPLETED reservationId={completed.ReservationId} viewerProfile={Safe(completed.ViewerProfileId)} executionAt={executionAt:O} scheduledStart={completed.ScheduledStart:O} viewerSession={Safe(operation.ViewerSessionId)} generation={operation.Generation} nid={completed.NetworkId} tsid={completed.TransportStreamId} sid={completed.ServiceId} eventId={completed.EventId} completion=viewer_operation_success retry=False rule=viewer_reservation_contract");
    }

    private void Fail(ViewerReservationRecord row, string code, string message, string? operationId = null)
    {
        var failed = _store.MarkTerminal(row.ReservationId, ViewerReservationState.Failed, code);
        if (failed is null) return;
        PublishChanged(failed, "Failed", operationId, message);
        _log.Add("VIEWER_RESERVATION_EXECUTION", failed.OwnerPluginId,
            $"result=FAILED reservationId={failed.ReservationId} viewerProfile={Safe(failed.ViewerProfileId)} errorCode={Safe(code)} message={Safe(message)} retry=False rule=viewer_reservation_contract");
    }

    public void PublishCreated(ViewerReservationRecord row) => PublishChanged(row, "Added", null);
    public void PublishCancelled(ViewerReservationRecord row) => PublishChanged(row, "Cancelled", null);

    private void PublishChanged(ViewerReservationRecord row, string change, string? operationId, string? message = null)
    {
        _typedEvents.Publish(new TvAirEventDto
        {
            EventType = TvAirEventType.ViewerReservationChanged,
            EntityId = $"viewer-reservation:{row.ReservationId}",
            OperationId = operationId,
            SourceOwnerId = row.OwnerPluginId,
            ChangeKind = change,
            ViewerReservation = ViewerReservationStore.ToDto(row),
            Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["change"] = change,
                ["viewerReservationId"] = row.ReservationId,
                ["viewerProfileId"] = row.ViewerProfileId,
                ["state"] = row.State.ToString(),
                ["message"] = message ?? string.Empty,
                ["displayTargetAfterSuccess"] = row.State == ViewerReservationState.Completed ? "plugin_may_select_completed_profile" : "none"
            }
        });
    }

    private static DateTimeOffset ToOffset(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local))
            : new DateTimeOffset(value);

    private static string Safe(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160];
    }
}
