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
        => new(_loader.Load(), _loader.CaptureConfiguredTuners());
}

/// <summary>
/// One operation-scoped immutable view of the current channel/service activation state.
/// Capture once at an API/matching/allocation boundary so every decision in that operation
/// is evaluated against the same .ch2/ChSet snapshot.
/// </summary>
public sealed class ChannelServiceAccessSnapshot
{
    private readonly ChannelLoadResult _channels;
    private readonly IReadOnlyList<TunerProfileDto> _configuredTuners;
    private readonly Lazy<IReadOnlyList<ChannelTarget>> _displayTargets;
    private readonly Lazy<HashSet<BroadcastServiceKey>> _displayServices;

    internal ChannelServiceAccessSnapshot(ChannelLoadResult channels, IReadOnlyList<TunerProfileDto> configuredTuners)
    {
        _channels = channels;
        _configuredTuners = configuredTuners;
        _displayTargets = new(() => GetProgramGuideDisplayTargets(_configuredTuners));
        _displayServices = new(() => DisplayTargets.Select(t => new BroadcastServiceKey(t.OriginalNetworkId, t.TransportStreamId, t.ServiceId)).ToHashSet());
    }

    public IReadOnlyList<ChannelTarget> ActiveTargets => _channels.Targets;
    public IReadOnlyList<ChannelTarget> DisplayTargets => _displayTargets.Value;
    public bool IsAvailableForDisplay(ushort nid, ushort tsid, ushort sid)
        => _displayServices.Value.Contains(new(nid, tsid, sid));
    public bool IsAvailableForRecording(ushort nid, ushort tsid, ushort sid)
        => IsOperationalForConfiguredRecordingTuners(nid, tsid, sid, _configuredTuners);
    public bool IsAvailableForRecording(Reservation reservation)
        => reservation.Source == ReservationSource.Epg || IsAvailableForRecording(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId);
    /// <summary>
    /// ChSet-independent source inventory. Entries are not globally operational.
    /// Use TryResolveDirectRouteForLease before any physical tuning attempt.
    /// </summary>
    public IReadOnlyList<BonDriverChannelRoute> DirectSourceServices => _channels.DirectSourceServices;

    /// <summary>
    /// Display-only service projection for programme guides and searches.
    /// BonDriver-specific tuning coordinates are deliberately NOT materialized here.
    /// A tuning target can only be constructed after acquiring the physical lease.
    /// </summary>
    public IReadOnlyList<ChannelTarget> GetProgramGuideDisplayTargets(
        IEnumerable<TunerProfileDto> configuredTuners)
    {
        var result = _channels.Targets.ToList();
        var seen = result.Select(t => new BroadcastServiceKey(
            t.OriginalNetworkId, t.TransportStreamId, t.ServiceId)).ToHashSet();
        if (_channels.BonDriverRoutes is not { } catalog)
            return OrderProgramGuideDisplayTargets(result);

        foreach (var tuner in configuredTuners)
        {
            if (string.IsNullOrWhiteSpace(tuner.ChannelCh2Path)
                || string.IsNullOrWhiteSpace(tuner.BonDriverFileName))
                continue;
            var group = (tuner.Group ?? string.Empty).Trim().ToUpperInvariant();
            // Unknown/hybrid grouping must not be guessed from a cable ONID.
            if (group is not ("GR" or "BSCS" or "HYBRID" or "CATV"))
                continue;
            // The display row must belong to the same explicit BonDriver/.ch2
            // source as the route admitted for this tuner.  Matching the service
            // identity alone may pick a name from another driver's channel file.
            string boundSource;
            try { boundSource = Path.GetFullPath(tuner.ChannelCh2Path.Trim()); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            foreach (var route in _channels.DirectSourceServices)
            {
                if (seen.Contains(route.Service)
                    || !string.Equals(route.BonDriverIdentity, tuner.BonDriverFileName,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(route.SourceCh2Path, boundSource,
                        StringComparison.OrdinalIgnoreCase)
                    || !catalog.TryResolveTransportForSource(tuner.BonDriverFileName,
                        boundSource, new[] { route.Service }, out _)
                    || GetEligibility(route.Service.NetworkId, route.Service.TransportStreamId,
                        route.Service.ServiceId) == ChannelServiceEligibility.Disabled)
                    continue;
                var displayGroup = BroadcastNetworkIdentity.TryResolveWave(route.Service.NetworkId, out var wave)
                    ? wave : group;
                if (group == "CATV") displayGroup = BroadcastNetworkIdentity.ResolveLegacyChannelGroup("CATV", route.Service.NetworkId);
                if (group != "CATV" && !SupportsGroup(group, displayGroup)) continue;
                seen.Add(route.Service);
                result.Add(new ChannelTarget
                {
                    Group = displayGroup,
                    OriginalNetworkId = route.Service.NetworkId,
                    TransportStreamId = route.Service.TransportStreamId,
                    ServiceId = route.Service.ServiceId,
                    Name = route.ServiceName,
                    ChannelArgument = string.Empty,
                    Ch2FileName = Path.GetFileName(route.SourceCh2Path),
                    Ch2TuningSpace = -1,
                    Ch2ChannelIndex = -1,
                    ChannelBuildSource = "direct_service_display_only"
                });
            }
        }
        return OrderProgramGuideDisplayTargets(result);
    }

    // Single owner of ProgramGuide service-column order. Keep the TVTest .ch2
    // encounter order within each broadcast wave, while making wave precedence
    // independent of the channel file/tuner enumeration order. Only presentation
    // targets are ordered here; tuning and recording routes remain unchanged.
    private static IReadOnlyList<ChannelTarget> OrderProgramGuideDisplayTargets(List<ChannelTarget> targets)
        => targets.OrderBy(t => ProgramGuideWaveOrder(
            BroadcastNetworkIdentity.ResolveDisplayWave(t.Group, t.OriginalNetworkId))).ToArray();

    private static int ProgramGuideWaveOrder(string? group)
        => group?.Trim().ToUpperInvariant() switch
        {
            "GR" => 0,
            "BS" => 1,
            "CS" => 2,
            "CATV" => 3,
            _ => 4
        };

    public IReadOnlyList<ChannelServiceState> ServiceStates => _channels.ServiceStates;

    /// <summary>Physical capability is checked independently of service identity.</summary>
    public bool CanTune(TunerProfile tuner, Reservation reservation)
    {
        if (GetEligibility(reservation) == ChannelServiceEligibility.Disabled) return false;
        // CATV routing is an explicitly configured BonDriver/.ch2 physical route.
        // A cable retransmission can retain the original terrestrial/satellite NID;
        // its NID must not override the configured physical CATV tuner group.
        if (TunerDisplayName.NormalizeGroup(tuner.Group) == "CATV")
            return !string.IsNullOrWhiteSpace(tuner.ChannelCh2Path)
                && TryResolveDirectRouteForConfiguredSource(tuner.BonDriverFileName,
                    tuner.ChannelCh2Path, new BroadcastServiceKey(reservation.NetworkId,
                        reservation.TransportStreamId, reservation.ServiceId));
        var group = ResolveSchedulingGroup(reservation, new[] { tuner });
        if (!SupportsGroup(tuner.Group, group)) return false;
        if (!string.IsNullOrWhiteSpace(tuner.ChannelCh2Path))
            return TryResolveDirectRouteForConfiguredSource(tuner.BonDriverFileName,
                tuner.ChannelCh2Path, new BroadcastServiceKey(reservation.NetworkId,
                    reservation.TransportStreamId, reservation.ServiceId));
        return IsOperational(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId);
    }

    public static bool SupportsGroup(string tunerGroup, string requestedGroup)
    {
        var physical = TunerDisplayName.NormalizeGroup(tunerGroup);
        var requested = TunerDisplayName.NormalizeGroup(requestedGroup);
        return physical == requested || (physical == "HYBRID" && requested is "GR" or "BSCS" or "HYBRID");
    }

    public string ResolveSchedulingGroup(Reservation reservation, IReadOnlyList<TunerProfile> tuners)
    {
        var key = new BroadcastServiceKey(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId);
        if (tuners.Count > 0 && tuners.All(t => TunerDisplayName.NormalizeGroup(t.Group) == "CATV")
            && tuners.Any(t => !string.IsNullOrWhiteSpace(t.ChannelCh2Path)
                && TryResolveDirectRouteForConfiguredSource(t.BonDriverFileName, t.ChannelCh2Path, key)))
            return "CATV";
        if (BroadcastNetworkIdentity.TryResolveWave(reservation.NetworkId, out var wave)) return wave;
        var boundGroups = tuners.Where(t => !string.IsNullOrWhiteSpace(t.ChannelCh2Path)
                && TryResolveDirectRouteForConfiguredSource(t.BonDriverFileName, t.ChannelCh2Path, key))
            .Select(t => TunerDisplayName.NormalizeGroup(t.Group)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (boundGroups.Contains("HYBRID", StringComparer.OrdinalIgnoreCase)) return "HYBRID";
        if (boundGroups.Length == 1) return boundGroups[0];
        return ReservationTunerGroupResolver.Resolve(reservation, tuners);
    }

    public bool TryResolveTargetForProfile(TunerProfile tuner, BroadcastServiceKey service, out ChannelTarget target)
    {
        target = null!;
        var reservation = new Reservation { NetworkId = service.NetworkId, TransportStreamId = service.TransportStreamId, ServiceId = service.ServiceId };
        var group = ResolveSchedulingGroup(reservation, new[] { tuner });
        if (!CanTune(tuner, reservation)) return false;
        if (string.IsNullOrWhiteSpace(tuner.ChannelCh2Path))
        {
            target = ResolveOperationalTarget(service.NetworkId, service.TransportStreamId, service.ServiceId)!;
            return target is not null;
        }
        if (_channels.BonDriverRoutes is not { } catalog
            || !catalog.TryResolveTransportForSource(tuner.BonDriverFileName, tuner.ChannelCh2Path, new[] { service }, out var route)) return false;
        target = new ChannelTarget
        {
            Group = group, OriginalNetworkId = service.NetworkId, TransportStreamId = service.TransportStreamId,
            ServiceId = service.ServiceId, Name = route.ServiceName,
            ResolvedSpace = route.TuningSpace, ResolvedChannelIndex = route.ChannelIndex,
            Ch2TuningSpace = route.TuningSpace, Ch2ChannelIndex = route.ChannelIndex,
            BonDriverChannel = route.ChannelIndex, Ch2FileName = Path.GetFileName(route.SourceCh2Path),
            ChannelArgument = $"/chspace {route.TuningSpace} /chi {route.ChannelIndex} /sid {service.ServiceId}",
            ChannelBuildSource = "explicit_bondriver_profile"
        };
        return true;
    }

    /// <summary>
    /// Pure allocation-time service compatibility check for a configured BonDriver/.ch2 pair.
    /// This is not RF verification or authority to tune without a physical lease.
    /// </summary>
    public bool TryResolveDirectRouteForConfiguredSource(
        string bonDriverIdentity, string sourceCh2Path, BroadcastServiceKey service)
        => _channels.BonDriverRoutes is { } catalog
           && catalog.TryResolveTransportForSource(bonDriverIdentity, sourceCh2Path,
               new[] { service }, out _);


    /// <summary>
    /// Resolve an enabled service using the explicitly configured table for the
    /// actual physical lease. This does not imply a globally usable legacy
    /// ChannelTarget, and never borrows another BonDriver's coordinates.
    /// </summary>
    public bool TryResolveDirectRouteForLease(
        TvAIr.Tuner.TunerLease lease,
        ushort networkId,
        ushort transportStreamId,
        ushort serviceId,
        out BonDriverChannelRoute route)
    {
        route = null!;
        var service = new BroadcastServiceKey(networkId, transportStreamId, serviceId);
        return GetEligibility(networkId, transportStreamId, serviceId) != ChannelServiceEligibility.Disabled
            && _channels.BonDriverRoutes is { } catalog
            && catalog.TryResolveForLease(lease, service, out route);
    }


    /// <summary>
    /// Construct an operational direct-route target only after the actual physical
    /// tuner is leased. This does not make a direct source globally available.
    /// </summary>
    public bool TryResolveDirectTargetForLease(
        TvAIr.Tuner.TunerLease lease,
        ushort networkId,
        ushort transportStreamId,
        ushort serviceId,
        string scheduledGroup,
        out ChannelTarget target)
    {
        target = null!;
        return GetEligibility(networkId, transportStreamId, serviceId) != ChannelServiceEligibility.Disabled
            && _channels.BonDriverRoutes is { } catalog
            && catalog.TryBuildTargetForLease(lease,
                new BroadcastServiceKey(networkId, transportStreamId, serviceId),
                scheduledGroup, out target);
    }

    /// <summary>
    /// Resolve a whole transport atomically for the actual leased BonDriver.
    /// A partially resolvable service list is never published.
    /// </summary>
    public bool TryResolveDirectTransportTargetsForLease(
        TvAIr.Tuner.TunerLease lease,
        IReadOnlyCollection<BroadcastServiceKey> services,
        string scheduledGroup,
        out IReadOnlyList<ChannelTarget> targets)
    {
        targets = Array.Empty<ChannelTarget>();
        return _channels.BonDriverRoutes is { } catalog
            && catalog.TryBuildTransportTargetsForLease(lease, services, scheduledGroup, out targets);
    }

    public ChannelServiceEligibility GetEligibility(ushort networkId, ushort transportStreamId, ushort serviceId)
        => _channels.GetServiceEligibility(networkId, transportStreamId, serviceId);

    public bool IsTvTestEnabled(ushort networkId, ushort transportStreamId, ushort serviceId)
        => _channels.IsTvTestServiceEnabled(networkId, transportStreamId, serviceId);

    public bool IsOperational(ushort networkId, ushort transportStreamId, ushort serviceId)
        => GetEligibility(networkId, transportStreamId, serviceId) == ChannelServiceEligibility.Active;

    /// <summary>
    /// Admission for a new ordinary recording reservation. Service identity is canonical;
    /// direct BonDriver tuning coordinates must only be chosen after tuner allocation.
    /// This method never exports coordinates from another driver's channel table.
    /// </summary>
    public bool TryResolveRecordingAdmission(
        ushort networkId, ushort transportStreamId, ushort serviceId,
        IEnumerable<TunerProfileDto> configuredTuners,
        out string serviceName, out string channelArgument)
    {
        serviceName = string.Empty;
        channelArgument = string.Empty;
        var legacyTarget = ResolveOperationalTarget(networkId, transportStreamId, serviceId);
        if (legacyTarget is not null)
        {
            serviceName = legacyTarget.Name;
            channelArgument = legacyTarget.ChannelArgument ?? string.Empty;
            return true;
        }
        if (GetEligibility(networkId, transportStreamId, serviceId) == ChannelServiceEligibility.Disabled)
            return false;
        var service = new BroadcastServiceKey(networkId, transportStreamId, serviceId);
        if (!service.IsValid || _channels.BonDriverRoutes is not { } catalog)
            return false;
        foreach (var tuner in configuredTuners)
        {
            if (!string.Equals(tuner.Role, "Recording", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(tuner.ChannelCh2Path)
                || (BroadcastNetworkIdentity.TryResolveWave(networkId, out var wave)
                    && !SupportsGroup(tuner.Group, wave))
                || !catalog.TryResolveTransportForSource(tuner.BonDriverFileName,
                    tuner.ChannelCh2Path, new[] { service }, out _))
                continue;
            // Do not persist driver-specific Space/Channel prior to physical allocation.
            serviceName = _channels.DirectSourceServices.FirstOrDefault(r =>
                r.Service == service &&
                string.Equals(r.BonDriverIdentity, tuner.BonDriverFileName,
                    StringComparison.OrdinalIgnoreCase))?.ServiceName ?? string.Empty;
            return true;
        }
        return false;
    }

    public ChannelTarget? ResolveOperationalTarget(ushort networkId, ushort transportStreamId, ushort serviceId)
        => IsOperational(networkId, transportStreamId, serviceId)
            ? _channels.ResolveActiveTarget(networkId, transportStreamId, serviceId)
            : null;


    public ChannelServiceEligibility GetEligibility(Reservation reservation)
        => GetEligibility(reservation.NetworkId, reservation.TransportStreamId, reservation.ServiceId);

    /// <summary>
    /// Reservation admission against the actual configured recording tuner set.
    /// A direct .ch2 service is admissible only if at least one recording-capable
    /// tuner has an explicit, unambiguous route. Physical assignment and lease
    /// resolution still validate the selected tuner independently.
    /// </summary>
    public bool IsOperationalForRecording(Reservation reservation,
        IReadOnlyCollection<TunerProfile> recordingTuners)
    {
        if (reservation.Source == ReservationSource.Epg || IsOperational(reservation))
            return true;
        if (GetEligibility(reservation) == ChannelServiceEligibility.Disabled)
            return false;
        var service = new BroadcastServiceKey(reservation.NetworkId,
            reservation.TransportStreamId, reservation.ServiceId);
        if (!service.IsValid || _channels.BonDriverRoutes is not { } catalog)
            return false;
        return recordingTuners.Any(t => CanTune(t, reservation));
    }

    /// <summary>
    /// Recording eligibility for operations that have the persisted tuner configuration
    /// rather than physical runtime profiles (e.g. automatic keyword matching).
    /// Uses the same BonDriver-specific route catalog as reservation allocation.
    /// Does not imply that a physical tuner has been acquired.
    /// </summary>
    /// <summary>
    /// Whether a service remains enabled by a configured channel source.
    /// Keep this separate from operational routing: a temporarily unavailable
    /// tuner/route must not delete an existing automatic reservation.
    /// </summary>
    public bool IsEnabledForConfiguredRecordingTuners(
        ushort networkId, ushort transportStreamId, ushort serviceId,
        IEnumerable<TunerProfileDto> configuredTuners)
    {
        if (GetEligibility(networkId, transportStreamId, serviceId) == ChannelServiceEligibility.Disabled)
            return false;
        if (IsTvTestEnabled(networkId, transportStreamId, serviceId))
            return true;
        var service = new BroadcastServiceKey(networkId, transportStreamId, serviceId);
        if (!service.IsValid || _channels.BonDriverRoutes is not { } catalog)
            return false;
        return configuredTuners.Any(t =>
            string.Equals(t.Role, "Recording", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(t.ChannelCh2Path)
            && (!BroadcastNetworkIdentity.TryResolveWave(networkId, out var wave) || SupportsGroup(t.Group, wave))
            && catalog.TryResolveTransportForSource(t.BonDriverFileName,
                t.ChannelCh2Path, new[] { service }, out _));
    }

    public bool IsOperationalForConfiguredRecordingTuners(
        ushort networkId, ushort transportStreamId, ushort serviceId,
        IEnumerable<TunerProfileDto> configuredTuners)
    {
        if (IsOperational(networkId, transportStreamId, serviceId))
            return true;
        if (GetEligibility(networkId, transportStreamId, serviceId) == ChannelServiceEligibility.Disabled
            || _channels.BonDriverRoutes is not { } catalog)
            return false;
        var service = new BroadcastServiceKey(networkId, transportStreamId, serviceId);
        if (!service.IsValid)
            return false;
        return configuredTuners.Any(t =>
            string.Equals(t.Role, "Recording", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(t.ChannelCh2Path)
            && (!BroadcastNetworkIdentity.TryResolveWave(networkId, out var wave) || SupportsGroup(t.Group, wave))
            && catalog.TryResolveTransportForSource(t.BonDriverFileName,
                t.ChannelCh2Path, new[] { service }, out _));
    }

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
