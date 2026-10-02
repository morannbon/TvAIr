using TvAIr.Core;

namespace TvAIr.Channel;

/// <summary>
/// Host-wide operational service eligibility SSOT.
/// EPG storage may contain every observed service, but user-facing/programmatic operations
/// (display/search/reservation/viewer/recording) may only expose services that are enabled
/// in the current TVTest .ch2 state and resolvable to the current ChSet route.
/// </summary>
public sealed class ChannelServiceAccessPolicy
{
    private readonly ChannelFileLoader _loader;

    public ChannelServiceAccessPolicy(ChannelFileLoader loader)
    {
        _loader = loader;
    }

    public ChannelServiceAccessSnapshot Capture()
        => new(_loader.Load());
}

/// <summary>
/// One operation-scoped immutable view of the current channel/service activation state.
/// Capture once at an API/matching/allocation boundary so every decision in that operation
/// is evaluated against the same .ch2/ChSet snapshot.
/// </summary>
public sealed class ChannelServiceAccessSnapshot
{
    private readonly ChannelLoadResult _channels;

    internal ChannelServiceAccessSnapshot(ChannelLoadResult channels)
    {
        _channels = channels;
    }

    public IReadOnlyList<ChannelTarget> ActiveTargets => _channels.Targets;
    public IReadOnlyList<ChannelServiceState> ServiceStates => _channels.ServiceStates;

    public ChannelServiceEligibility GetEligibility(ushort networkId, ushort transportStreamId, ushort serviceId)
        => _channels.GetServiceEligibility(networkId, transportStreamId, serviceId);

    public bool IsTvTestEnabled(ushort networkId, ushort transportStreamId, ushort serviceId)
        => _channels.IsTvTestServiceEnabled(networkId, transportStreamId, serviceId);

    public bool IsOperational(ushort networkId, ushort transportStreamId, ushort serviceId)
        => GetEligibility(networkId, transportStreamId, serviceId) == ChannelServiceEligibility.Active;

    public ChannelTarget? ResolveOperationalTarget(ushort networkId, ushort transportStreamId, ushort serviceId)
        => IsOperational(networkId, transportStreamId, serviceId)
            ? _channels.ResolveActiveTarget(networkId, transportStreamId, serviceId)
            : null;


    public ChannelServiceEligibility GetEligibility(Reservation reservation)
        => GetEligibility(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId);

    public bool IsOperational(Reservation reservation)
        => reservation.Source == ReservationSource.Epg
            || IsOperational(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId);

    public string GetRejectReason(Reservation reservation)
        => reservation.Source == ReservationSource.Epg
            ? string.Empty
            : GetRejectReason(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId);

    public string GetRejectReason(ushort networkId, ushort transportStreamId, ushort serviceId)
        => GetEligibility(networkId, transportStreamId, serviceId) switch
        {
            ChannelServiceEligibility.Disabled => "tvtest_ch2_service_disabled",
            ChannelServiceEligibility.ActiveRouteUnavailable => "active_service_route_unavailable",
            ChannelServiceEligibility.Unknown => "service_not_enabled_in_tvtest_ch2",
            _ => string.Empty
        };
}
