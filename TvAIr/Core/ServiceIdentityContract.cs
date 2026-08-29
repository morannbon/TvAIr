namespace TvAIr.Core;

/// <summary>
/// TvAIr-wide service identity contract.
/// A service is identified by the exact NID/TSID/SID triplet. ServiceName is mutable display metadata
/// and must never be used as the identity key. Current display name is resolved from the current
/// ChannelTarget set; stored names are fallback snapshots for identities no longer resolvable.
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
