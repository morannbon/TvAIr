using System.Collections.Concurrent;
using System.Threading.Channels;

namespace TvAIr.Core;

public sealed record SettingsUiCommitEvent(
    long Sequence,
    bool PersistedChanged,
    bool ThemeChanged,
    long ThemeRevision,
    bool ReservationActionUiHotReloaded,
    bool RequiresRestart,
    bool TunerTopologyRestartRequired,
    DateTimeOffset CommittedAt);

/// <summary>
/// Settings UI commit broadcast SSOT.
/// SettingsChangeApplicationService publishes each committed settings generation once;
/// browser surfaces subscribe here regardless of whether the save originated from the
/// web hamburger/context menu or from the tray-opened settings window.
/// </summary>
public sealed class SettingsUiCommitHub
{
    private readonly ConcurrentDictionary<Guid, Channel<SettingsUiCommitEvent>> _subscribers = new();
    private readonly object _stateGate = new();
    private long _sequence;
    private SettingsUiCommitEvent? _latest;

    public SettingsUiCommitEvent Publish(
        bool persistedChanged,
        bool themeChanged,
        long themeRevision,
        bool reservationActionUiHotReloaded,
        bool requiresRestart,
        bool tunerTopologyRestartRequired)
    {
        var evt = new SettingsUiCommitEvent(
            Sequence: Interlocked.Increment(ref _sequence),
            PersistedChanged: persistedChanged,
            ThemeChanged: themeChanged,
            ThemeRevision: themeRevision,
            ReservationActionUiHotReloaded: reservationActionUiHotReloaded,
            RequiresRestart: requiresRestart,
            TunerTopologyRestartRequired: tunerTopologyRestartRequired,
            CommittedAt: DateTimeOffset.Now);

        lock (_stateGate)
            _latest = evt;

        foreach (var pair in _subscribers)
        {
            if (!pair.Value.Writer.TryWrite(evt))
                Remove(pair.Key);
        }

        return evt;
    }

    public (Guid Id, ChannelReader<SettingsUiCommitEvent> Reader) Subscribe()
    {
        var channel = global::System.Threading.Channels.Channel.CreateBounded<SettingsUiCommitEvent>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;

        SettingsUiCommitEvent? latest;
        lock (_stateGate)
            latest = _latest;
        if (latest is not null)
            channel.Writer.TryWrite(latest);

        return (id, channel.Reader);
    }

    public void Remove(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }
}
