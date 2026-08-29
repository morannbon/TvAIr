using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Epg.Projection;
using TvAIr.Schedule;
using TvAIr.Tuner;

namespace TvAIr.Plugin;


internal sealed record PluginProgramGuideWaveFilterSnapshot(
    string Key,
    string Group,
    string Label,
    int Order,
    bool IsProgramGuideFilter);

/// <summary>
/// Runtime capability APIs share this authoritative source for program, channel, reservation, rule, and tuner snapshots.
/// Contract-specific filtering and DTO projection remain owned by each Runtime API surface.
/// </summary>
internal sealed class PluginReadModelSource
{
    private readonly IProgramEventSource _programEvents;
    private readonly ChannelFileLoader _channelLoader;
    private readonly ReservationStore _reservationStore;
    private readonly TunerPool _tunerPool;

    public PluginReadModelSource(
        IProgramEventSource programEvents,
        ChannelFileLoader channelLoader,
        ReservationStore reservationStore,
        TunerPool tunerPool)
    {
        _programEvents = programEvents;
        _channelLoader = channelLoader;
        _reservationStore = reservationStore;
        _tunerPool = tunerPool;
    }

    public IReadOnlyList<ProjectedProgramEvent> GetProgramEvents(DateTime from, DateTime to)
        => _programEvents.GetByRange(from, to);

    public IReadOnlyList<ProjectedProgramEvent> GetAllProgramEvents()
        => _programEvents.GetAll();

    public ProjectedProgramEvent? GetProgramEvent(ushort networkId, ushort transportStreamId, ushort serviceId, ushort eventId)
        => _programEvents.GetByEventKey(networkId, transportStreamId, serviceId, eventId);

    public ChannelLoadResult GetChannelLoad()
        => _channelLoader.Load();

    public IReadOnlyList<PluginProgramGuideWaveFilterSnapshot> GetProgramGuideWaveFilters()
        => new[]
        {
            new PluginProgramGuideWaveFilterSnapshot("GR", "GR", "地上波", 0, true),
            new PluginProgramGuideWaveFilterSnapshot("BS", "BS", "BS", 1, true),
            new PluginProgramGuideWaveFilterSnapshot("CS", "CS", "CS", 2, true)
        };

    public IReadOnlyList<Reservation> GetReservations()
        => _reservationStore.GetAll();

    public string ResolveCurrentServiceName(ushort networkId, ushort transportStreamId, ushort serviceId, string? storedFallback = null)
        => ServiceIdentityContract.ResolveCurrentServiceName(
            GetChannelLoad().Targets,
            networkId,
            transportStreamId,
            serviceId,
            storedFallback);

    public string ResolveCurrentServiceName(Reservation reservation)
        => ResolveCurrentServiceName(
            reservation.NetworkId,
            reservation.TransportStreamId,
            reservation.ServiceId,
            reservation.ServiceName);

    public IReadOnlyList<TunerSlotStatus> GetTunerStatus()
        => _tunerPool.GetStatus();

    public IReadOnlyList<KeywordRule> GetKeywordRules()
        => _reservationStore.GetKeywordRules();

    public IReadOnlyList<ProgramRule> GetProgramRules()
        => _reservationStore.GetProgramRules();

    public HashSet<int> GetActiveRecordingReservationIds()
        => GetTunerStatus()
            .Where(status => status.UsageKind == TunerUsageKind.Recording && status.ReservationId.HasValue)
            .Select(status => status.ReservationId!.Value)
            .ToHashSet();
}
