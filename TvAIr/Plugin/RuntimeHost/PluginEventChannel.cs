using System.Collections.Concurrent;
using TvAIrPlugin;
using TvAIrPlugin.Events;
using TvAIr.Core;

namespace TvAIr.Plugin.RuntimeHost;

internal sealed class PluginEventChannel : ITvAirPluginEventsApi, IDisposable
{
    private const int ReplayCapacityPerType = 512;
    public const string RuntimeConnectedEventType = "RuntimeConnected";
    private readonly PluginRuntimeManager _runtime;
    private readonly ITvAirEventsApi _hostEvents;
    private readonly LogRepository _log;
    private readonly ConcurrentDictionary<string, EventBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IDisposable> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public PluginEventChannel(PluginRuntimeManager runtime, ITvAirEventsApi hostEvents, LogRepository log)
    {
        _runtime = runtime;
        _hostEvents = hostEvents;
        _log = log;
    }

    public PluginEventSubscription Subscribe(PluginEventSubscriptionRequest request, Action<PluginEventEnvelope> handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_runtime.IsConnected) throw new InvalidOperationException("Plugin runtime is disconnected.");
        var isRuntimeConnected = string.Equals(request.EventType, RuntimeConnectedEventType, StringComparison.OrdinalIgnoreCase);
        TvAirEventType parsed = default;
        if (!isRuntimeConnected)
        {
            if (!Enum.TryParse(request.EventType, true, out parsed))
                throw new ArgumentOutOfRangeException(nameof(request.EventType), $"Unsupported event type: {request.EventType}");
        }

        var eventType = isRuntimeConnected ? RuntimeConnectedEventType : parsed.ToString();
        var buffer = _buffers.GetOrAdd(eventType, _ => new EventBuffer(_runtime.PluginId, eventType, _log));
        if (!isRuntimeConnected)
            EnsureCompatibilitySubscription(parsed, eventType, buffer);

        var snapshot = buffer.Snapshot();
        var resumeAfter = request.ResumeAfterSequence;
        long? oldest = snapshot.Count == 0 ? null : snapshot[0].Sequence;
        var gap = resumeAfter is long resumeSequence
            && oldest is long oldestSequence
            && resumeSequence < oldestSequence - 1;
        if (resumeAfter.HasValue)
        {
            foreach (var item in snapshot.Where(x => x.Sequence > resumeAfter.Value))
                handler(item);
        }

        var subscriber = buffer.Subscribe(handler);
        var state = new PluginEventSubscriptionState
        {
            SubscriptionId = Guid.NewGuid().ToString("N"),
            EventType = eventType,
            CurrentSequence = snapshot.Count == 0 ? 0 : snapshot[^1].Sequence,
            ReplayGapDetected = gap,
            OldestAvailableSequence = oldest
        };
        return new PluginEventSubscription(state, subscriber);
    }

    public IDisposable Subscribe(string eventType, Action<PluginEventEnvelope> handler)
        => Subscribe(new PluginEventSubscriptionRequest { EventType = eventType }, handler);

    public IReadOnlyList<string> ListEventTypes() => [RuntimeConnectedEventType, .. Enum.GetNames<TvAirEventType>()];

    public void PublishRuntimeConnected()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_runtime.IsConnected) return;
        var buffer = _buffers.GetOrAdd(RuntimeConnectedEventType, _ => new EventBuffer(_runtime.PluginId, RuntimeConnectedEventType, _log));
        buffer.Publish(new PluginEventEnvelope(
            Guid.NewGuid().ToString("N"), RuntimeConnectedEventType, 1, DateTimeOffset.Now,
            "PluginRuntime", _runtime.PluginId, 1,
            new { pluginId = _runtime.PluginId, runtimeSessionId = _runtime.RuntimeSessionId, sdkContractVersion = _runtime.SdkContractVersion },
            SchemaVersion: "1"));
    }

    private void EnsureCompatibilitySubscription(TvAirEventType parsed, string eventType, EventBuffer buffer)
    {
        _registrations.GetOrAdd(eventType, _ => _hostEvents.Subscribe(parsed, dto =>
        {
            if (Volatile.Read(ref _disposed) != 0 || !_runtime.IsConnected) return;
            buffer.Publish(new PluginEventEnvelope(
                dto.EventInstanceId,
                dto.EventType.ToString(),
                dto.Sequence,
                dto.OccurredAt,
                null,
                dto.EntityId,
                dto.EntityVersion,
                dto,
                OperationId: dto.OperationId,
                SourceOwnerId: dto.SourceOwnerId,
                DataRevision: dto.DataRevision,
                ChangeKind: dto.ChangeKind,
                SchemaVersion: "1"));
        }));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var registration in _registrations.Values) registration.Dispose();
        _registrations.Clear();
        foreach (var buffer in _buffers.Values) buffer.Dispose();
        _buffers.Clear();
    }

    private sealed class EventBuffer : IDisposable
    {
        private readonly string _pluginId;
        private readonly string _eventType;
        private readonly LogRepository _log;
        private readonly object _gate = new();
        private readonly Queue<PluginEventEnvelope> _replay = new();
        private readonly Dictionary<long, Action<PluginEventEnvelope>> _subscribers = new();
        private long _nextSubscriberId;
        private bool _disposed;

        public EventBuffer(string pluginId, string eventType, LogRepository log)
        {
            _pluginId = pluginId;
            _eventType = eventType;
            _log = log;
        }

        public void Publish(PluginEventEnvelope envelope)
        {
            Action<PluginEventEnvelope>[] subscribers;
            lock (_gate)
            {
                if (_disposed) return;
                _replay.Enqueue(envelope);
                while (_replay.Count > ReplayCapacityPerType) _replay.Dequeue();
                subscribers = _subscribers.Values.ToArray();
            }
            foreach (var subscriber in subscribers)
            {
                try
                {
                    subscriber(envelope);
                }
                catch (Exception ex)
                {
                    _log.Add("PLUGIN_EVENT_DISPATCH", _pluginId,
                        $"result=SUBSCRIBER_FAILED eventType={_eventType} sequence={envelope.Sequence} error={ex.GetType().Name}:{ex.Message} action=continue_other_subscribers rule=release_contract");
                }
            }
        }

        public IReadOnlyList<PluginEventEnvelope> Snapshot()
        {
            lock (_gate) return _replay.ToArray();
        }

        public IDisposable Subscribe(Action<PluginEventEnvelope> handler)
        {
            long id;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                id = ++_nextSubscriberId;
                _subscribers.Add(id, handler);
            }
            return new Subscription(this, id);
        }

        private void Remove(long id)
        {
            lock (_gate) _subscribers.Remove(id);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _subscribers.Clear();
                _replay.Clear();
            }
        }

        private sealed class Subscription(EventBuffer owner, long id) : IDisposable
        {
            private EventBuffer? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(id);
        }
    }
}
