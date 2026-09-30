namespace TvAIr.Core;

/// <summary>
/// TvAIr-wide service identity contract.
/// A service is identified by the exact NID/TSID/SID triplet. ServiceName is mutable display metadata
/// and must never be used as the identity key. General channel display uses the current ChannelTarget;
/// reservation/recording presentation additionally preserves the exact event/persisted service label
/// before falling back to physical-route metadata.
/// </summary>
internal static class ServiceIdentityContract
{
    public readonly record struct Key(ushort NetworkId, ushort TransportStreamId, ushort ServiceId)
    {
        public override string ToString() => $"{NetworkId}:{TransportStreamId}:{ServiceId}";
    }

    public static Key From(ChannelTarget target)
        => new(target.OriginalNetworkId, target.TransportStreamId, target.ServiceId);

    public static Key From(Reservation reservation)
        => new(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId);

    public static bool Matches(ChannelTarget target, ushort networkId, ushort transportStreamId, ushort serviceId)
        => target.OriginalNetworkId == networkId
           && target.TransportStreamId == transportStreamId
           && target.ServiceId == serviceId;

    public static bool Matches(ChannelTarget target, Key key)
        => Matches(target, key.NetworkId, key.TransportStreamId, key.ServiceId);

    public static bool Matches(Reservation left, Reservation right)
        => left.NetworkId == right.NetworkId
           && left.TransportStreamId == right.TransportStreamId
           && left.ServiceId == right.ServiceId;

    public static ChannelTarget? ResolveTarget(
        IEnumerable<ChannelTarget> targets,
        ushort networkId,
        ushort transportStreamId,
        ushort serviceId)
        => targets.FirstOrDefault(target => Matches(target, networkId, transportStreamId, serviceId));

    public static string ResolveCurrentServiceName(
        IEnumerable<ChannelTarget> targets,
        ushort networkId,
        ushort transportStreamId,
        ushort serviceId,
        string? storedFallback = null)
    {
        var target = ResolveTarget(targets, networkId, transportStreamId, serviceId);
        var current = target?.Name?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(current))
            return current;

        return storedFallback?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Reservation/recording presentation resolver. The exact programme-event service name is
    /// the strongest display metadata for the immutable NID/TSID/SID identity, followed by the
    /// persisted reservation snapshot. ChannelTarget is a physical-route catalogue and is used
    /// only as the final fallback; sibling-service route aliases must not overwrite a recording's
    /// exact service label.
    /// </summary>
    public static string ResolveReservationServiceName(
        IEnumerable<ChannelTarget> targets,
        ushort networkId,
        ushort transportStreamId,
        ushort serviceId,
        string? projectedEventServiceName,
        string? storedFallback = null)
    {
        var projected = projectedEventServiceName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(projected))
            return projected;

        var stored = storedFallback?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(stored))
            return stored;

        return ResolveCurrentServiceName(targets, networkId, transportStreamId, serviceId, storedFallback);
    }

    public static bool TryParseKey(string? value, out Key key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return false;
        if (!ushort.TryParse(parts[0], out var nid)
            || !ushort.TryParse(parts[1], out var tsid)
            || !ushort.TryParse(parts[2], out var sid))
            return false;
        key = new Key(nid, tsid, sid);
        return true;
    }
}
