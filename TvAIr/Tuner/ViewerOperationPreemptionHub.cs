using TvAIr.Core;
using TvAIr.Plugin;
using TvAIrPlugin;

namespace TvAIr.Tuner;

/// <summary>
/// Synchronous, viewer-profile-scoped coordination boundary used immediately before a Host-owned
/// explicit Viewer Operation. Plugins may clear their own automatic viewer state here, but the Host
/// never depends on plugin-specific concepts such as zapping or power-off timers.
/// </summary>
public sealed class ViewerOperationPreemptionHub
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Subscriber> _subscribers = new();
    private readonly LogRepository _log;
    private long _nextId;

    public ViewerOperationPreemptionHub(LogRepository log) => _log = log;

    public IDisposable Subscribe(string pluginId, Action<TvAirViewerOperationPreemptingDto> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var owner = PluginIdentity.Normalize(pluginId);
        long id;
        lock (_gate)
        {
            id = ++_nextId;
            _subscribers[id] = new Subscriber(owner, handler);
        }
        return new Subscription(this, id);
    }

    public ViewerOperationPreemptionResult Preempt(
        string viewerProfileId,
        string operationId,
        string sourceKind,
        string? sourceOwnerId,
        string? reservationId)
    {
        Subscriber[] subscribers;
        lock (_gate) subscribers = _subscribers.Values.ToArray();

        var dto = new TvAirViewerOperationPreemptingDto
        {
            ViewerProfileId = viewerProfileId?.Trim() ?? string.Empty,
            OperationId = operationId?.Trim() ?? string.Empty,
            SourceKind = sourceKind?.Trim() ?? string.Empty,
            SourceOwnerId = sourceOwnerId?.Trim() ?? string.Empty,
            ViewerReservationId = reservationId?.Trim() ?? string.Empty,
            OccurredAt = DateTimeOffset.Now
        };

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber.Handler(dto);
            }
            catch (Exception ex)
            {
                _log.Add("VIEWER_OPERATION_PREEMPTION", subscriber.PluginId,
                    $"result=FAILED viewerProfile={Safe(dto.ViewerProfileId)} operationId={Safe(dto.OperationId)} sourceKind={Safe(dto.SourceKind)} sourceOwnerId={Safe(dto.SourceOwnerId)} reservationId={Safe(dto.ViewerReservationId)} error={Safe(ex.GetType().Name)}:{Safe(ex.Message)} action=viewer_operation_not_started rule=viewer_operation_preemption_contract");
                return new ViewerOperationPreemptionResult(false, subscriber.PluginId, ex.Message);
            }
        }

        _log.Add("VIEWER_OPERATION_PREEMPTION", "Host",
            $"result=OK viewerProfile={Safe(dto.ViewerProfileId)} operationId={Safe(dto.OperationId)} sourceKind={Safe(dto.SourceKind)} sourceOwnerId={Safe(dto.SourceOwnerId)} reservationId={Safe(dto.ViewerReservationId)} subscriberCount={subscribers.Length} rule=viewer_operation_preemption_contract");
        return new ViewerOperationPreemptionResult(true, string.Empty, string.Empty);
    }

    private void Remove(long id)
    {
        lock (_gate) _subscribers.Remove(id);
    }

    private static string Safe(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160];
    }

    private sealed record Subscriber(string PluginId, Action<TvAirViewerOperationPreemptingDto> Handler);

    private sealed class Subscription : IDisposable
    {
        private ViewerOperationPreemptionHub? _owner;
        private readonly long _id;
        public Subscription(ViewerOperationPreemptionHub owner, long id) { _owner = owner; _id = id; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(_id);
    }
}

public sealed record ViewerOperationPreemptionResult(bool Success, string FailedPluginId, string Message);
