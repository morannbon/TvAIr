namespace TvAIrPlugin.Events;

public sealed record PluginEventEnvelope(
    string EventId,
    string EventType,
    long Sequence,
    DateTimeOffset OccurredAt,
    string? EntityType,
    string? EntityId,
    long EntityVersion,
    object? Payload,
    string SchemaVersion,
    string? OperationId = null,
    string? SourceOwnerId = null,
    long? DataRevision = null,
    string? ChangeKind = null);

public sealed record PluginEventSubscriptionRequest
{
    public required string EventType { get; init; }
    public long? ResumeAfterSequence { get; init; }
}

public sealed record PluginEventSubscriptionState
{
    public required string SubscriptionId { get; init; }
    public required string EventType { get; init; }
    public long CurrentSequence { get; init; }
    public bool ReplayGapDetected { get; init; }
    public long? OldestAvailableSequence { get; init; }
}

public sealed record PluginEventSubscription(
    PluginEventSubscriptionState State,
    IDisposable Registration) : IDisposable
{
    public void Dispose() => Registration.Dispose();
}

public interface ITvAirPluginEventsApi
{
    PluginEventSubscription Subscribe(PluginEventSubscriptionRequest request, Action<PluginEventEnvelope> handler);
    IDisposable Subscribe(string eventType, Action<PluginEventEnvelope> handler);
    IReadOnlyList<string> ListEventTypes();
}
