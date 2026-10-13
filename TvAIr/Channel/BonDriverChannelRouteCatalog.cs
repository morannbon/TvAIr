using TvAIr.Core;
using System.Text;

namespace TvAIr.Channel;

/// <summary>
/// One canonical service identity, independent of transport tuning and tuner allocation.
/// </summary>
public readonly record struct BroadcastServiceKey(ushort NetworkId, ushort TransportStreamId, ushort ServiceId)
{
    public bool IsValid => NetworkId != 0 && TransportStreamId != 0 && ServiceId != 0;
}

/// <summary>
/// Explicit source contract: these coordinates originate from a channel table for
/// the specified BonDriver. A shared GR/BSCS .ch2 cannot imply a different driver.
/// </summary>
public sealed record BonDriverChannelRoute(
    string BonDriverIdentity,
    BroadcastServiceKey Service,
    int TuningSpace,
    int ChannelIndex,
    string SourceCh2Path,
    string ServiceName = "");


/// <summary>
/// Explicit association between one BonDriver and the .ch2 produced for its channel table.
/// A shared GR/BSCS configuration cannot be used to infer this association.
/// </summary>
public sealed record BonDriverChannelSource(string BonDriverIdentity, string Ch2Path);

/// <summary>
/// BonDriver-specific tuning lookup. Never guess another driver's coordinates,
/// fall back to an arbitrary entry, or resolve ambiguous source rows.
/// This is a pure domain component; activation requires integration with the
/// configured BonDriver/channel sources and tuner allocation.
/// </summary>
public sealed class BonDriverChannelRouteCatalog
{
    private readonly Dictionary<(string Driver, BroadcastServiceKey Service), BonDriverChannelRoute> routes = new();
    private readonly HashSet<(string Driver, BroadcastServiceKey Service)> ambiguous = new();
    private readonly List<(string Driver, BroadcastServiceKey Service)> sourceOrder = new();
    // One transport has one physical tuning route within a single BonDriver.
    // If its enabled services disagree, reject the entire transport rather
    // than letting a single-SID recording pick an arbitrary frequency.
    private readonly HashSet<(string Driver, ushort NetworkId, ushort TransportStreamId)> ambiguousTransports = new();
    public IReadOnlyList<string> SourceErrors { get; private set; } = Array.Empty<string>();

    public BonDriverChannelRouteCatalog(IEnumerable<BonDriverChannelRoute> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var route in entries)
        {
            if (route is null) throw new ArgumentException("Null channel route", nameof(entries));
            var driver = NormalizeIdentity(route.BonDriverIdentity);
            if (driver.Length == 0 || !route.Service.IsValid || route.TuningSpace < 0 || route.ChannelIndex < 0
                || string.IsNullOrWhiteSpace(route.SourceCh2Path))
                throw new ArgumentException("Incomplete BonDriver channel route", nameof(entries));

            var key = (driver, route.Service);
            if (ambiguous.Contains(key)) continue;
            if (routes.TryGetValue(key, out var existing))
            {
                // Duplicate source lines are safe only if they specify one physical route.
                if (existing.TuningSpace != route.TuningSpace || existing.ChannelIndex != route.ChannelIndex)
                {
                    routes.Remove(key);
                    ambiguous.Add(key);
                }
            }
            else
            {
                routes.Add(key, route);
                sourceOrder.Add(key);
            }
        }

        var byTransport = routes.Values.GroupBy(x => (
            Driver: NormalizeIdentity(x.BonDriverIdentity),
            x.Service.NetworkId, x.Service.TransportStreamId));
        foreach (var transport in byTransport)
        {
            if (transport.Select(x => (x.TuningSpace, x.ChannelIndex)).Distinct().Skip(1).Any())
                ambiguousTransports.Add(transport.Key);
        }
        // A service-level collision also taints its entire transport. If a
        // single service was ambiguous, sibling SID routes are not a proof of
        // which physical frequency is safe for that transport.
        foreach (var key in ambiguous)
            ambiguousTransports.Add((key.Driver, key.Service.NetworkId, key.Service.TransportStreamId));
    }

    /// <summary>
    /// Construct verified-by-source (not verified-by-RF) routes from explicitly bound
    /// BonDriver/.ch2 pairs. Reject absent sources rather than borrowing coordinates
    /// from another driver's channel table. This does not open a tuner.
    /// </summary>
    public static BonDriverChannelRouteCatalog FromCh2Sources(IEnumerable<BonDriverChannelSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var entries = new List<BonDriverChannelRoute>();
        foreach (var source in sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.BonDriverIdentity)
                || string.IsNullOrWhiteSpace(source.Ch2Path))
                throw new ArgumentException("BonDriver and .ch2 must be explicitly associated", nameof(sources));

            var path = Path.GetFullPath(source.Ch2Path.Trim());
            if (!File.Exists(path)) throw new FileNotFoundException("BonDriver channel source not found", path);
            using var reader = new StreamReader(path, Encoding.GetEncoding(932), detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                var columns = ChannelFileLoader.ParseCh2Columns(line.Trim().TrimStart('\uFEFF'));
                if (columns.Length < 9) continue;
                if (!ChannelFileLoader.TryReadServiceState(columns, "", Path.GetFileName(path), 0, out var state)
                    || !state.IsEnabled) continue;
                if (!int.TryParse(columns[1], out var space) || space < 0
                    || !int.TryParse(columns[2], out var channel) || channel < 0) continue;
                if (!ushort.TryParse(columns[5], out var sid)
                    || !ushort.TryParse(columns[6], out var onid)
                    || !ushort.TryParse(columns[7], out var tsid)) continue;
                var service = new BroadcastServiceKey(onid, tsid, sid);
                if (!service.IsValid) continue;
                entries.Add(new BonDriverChannelRoute(source.BonDriverIdentity, service, space, channel, path, state.Name));
            }
        }
        return new BonDriverChannelRouteCatalog(entries);
    }

    /// <summary>
    /// Build from the single persisted tuner configuration. An optional per-tuner .ch2
    /// is never inferred from a shared GR/BSCS table. Multiple tuner slots may share
    /// one BonDriver only when their explicitly configured channel table is identical.
    /// </summary>
    public static BonDriverChannelRouteCatalog FromConfiguredTuners(IEnumerable<TunerProfileDto> tuners)
    {
        ArgumentNullException.ThrowIfNull(tuners);
        var sources = new Dictionary<string, BonDriverChannelSource>(StringComparer.OrdinalIgnoreCase);
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var tuner in tuners)
        {
            var path = (tuner.ChannelCh2Path ?? string.Empty).Trim();
            if (path.Length == 0) continue; // Legacy tuner: preserve existing channel resolver.
            var driver = NormalizeIdentity(tuner.BonDriverFileName);
            if (driver.Length == 0) { errors.Add("Explicit .ch2 has no BonDriver binding"); continue; }
            if (rejected.Contains(driver)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                rejected.Add(driver); errors.Add($"{driver}: invalid channel source path"); continue;
            }
            if (sources.TryGetValue(driver, out var existing))
            {
                if (!string.Equals(existing.Ch2Path, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    rejected.Add(driver); errors.Add($"{driver}: conflicting channel source bindings");
                }
                continue;
            }
            sources.Add(driver, new BonDriverChannelSource(driver, fullPath));
        }
        var routes = new List<BonDriverChannelRoute>();
        foreach (var source in sources.Values.Where(s => !rejected.Contains(NormalizeIdentity(s.BonDriverIdentity))))
        {
            try { routes.AddRange(FromCh2Sources(new[] { source }).SourceRoutesInFileOrder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { errors.Add($"{source.BonDriverIdentity}: channel source unavailable ({ex.GetType().Name})"); }
        }
        return new BonDriverChannelRouteCatalog(routes) { SourceErrors = errors.ToArray() };
    }

    /// <summary>
    /// Unambiguous services in first-source/first-row order for display. This is a
    /// source inventory only, NOT an operational channel list: a physical tuner
    /// must still be allocated and the route verified against its lease.
    /// </summary>
    public IReadOnlyList<BonDriverChannelRoute> SourceRoutesInFileOrder
        => sourceOrder.Where(key => routes.ContainsKey(key))
            .Select(key => routes[key])
            .Where(x => !ambiguousTransports.Contains((NormalizeIdentity(x.BonDriverIdentity),
                x.Service.NetworkId, x.Service.TransportStreamId)))
            .ToArray();

    // Keep the canonical lookup inventory ordering independent of display ordering.
    public IReadOnlyList<BonDriverChannelRoute> SourceRoutes
        => routes.Values
            .Where(x => !ambiguousTransports.Contains((NormalizeIdentity(x.BonDriverIdentity),
                x.Service.NetworkId, x.Service.TransportStreamId)))
            .OrderBy(x => x.BonDriverIdentity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Service.NetworkId)
            .ThenBy(x => x.Service.TransportStreamId)
            .ThenBy(x => x.Service.ServiceId)
            .ToArray();

    public bool TryResolve(string bonDriverIdentity, BroadcastServiceKey service, out BonDriverChannelRoute route)
    {
        var key = (NormalizeIdentity(bonDriverIdentity), service);
        if (service.IsValid && !ambiguous.Contains(key)
            && !ambiguousTransports.Contains((key.Item1, service.NetworkId, service.TransportStreamId))
            && routes.TryGetValue(key, out var found))
        {
            route = found;
            return true;
        }
        route = null!;
        return false;
    }

    /// <summary>
    /// Resolve from the actual allocated physical tuner, never from a wave-global
    /// channel coordinate. A legacy lease deliberately has no direct .ch2 route.
    /// </summary>
    public bool TryResolveForLease(TvAIr.Tuner.TunerLease lease, BroadcastServiceKey service, out BonDriverChannelRoute route)
    {
        ArgumentNullException.ThrowIfNull(lease);
        route = null!;
        if (!lease.IsCurrent || string.IsNullOrWhiteSpace(lease.ChannelCh2Path)) return false;
        if (!TryResolve(lease.BonDriverFileName, service, out var found)) return false;
        var boundSource = Path.GetFullPath(lease.ChannelCh2Path.Trim());
        if (!string.Equals(boundSource, found.SourceCh2Path, StringComparison.OrdinalIgnoreCase)) return false;
        route = found;
        return true;
    }

    /// <summary>
    /// EPG captures one transport stream, not one independently tuned service.
    /// Every requested service must be present in this BonDriver's own channel
    /// table and map to exactly the same physical (space, channel). Never accept
    /// a representative SID while silently omitting another requested SID.
    /// </summary>
    // The same transport contract is used before physical admission and after
    // acquiring the lease. Do not maintain two independent SID/route checks.
    public bool TryResolveTransportForSource(
        string bonDriverIdentity,
        string sourceCh2Path,
        IReadOnlyCollection<BroadcastServiceKey> services,
        out BonDriverChannelRoute route)
    {
        ArgumentNullException.ThrowIfNull(services);
        route = null!;
        if (services.Count == 0 || string.IsNullOrWhiteSpace(sourceCh2Path)) return false;
        string source;
        try { source = Path.GetFullPath(sourceCh2Path.Trim()); }
        catch { return false; }

        BonDriverChannelRoute? first = null;
        foreach (var service in services)
        {
            if (!service.IsValid || !TryResolve(bonDriverIdentity, service, out var current)
                || !string.Equals(source, current.SourceCh2Path, StringComparison.OrdinalIgnoreCase))
                return false;
            if (first is null)
            {
                first = current;
                continue;
            }
            if (service.NetworkId != first.Service.NetworkId
                || service.TransportStreamId != first.Service.TransportStreamId
                || current.TuningSpace != first.TuningSpace
                || current.ChannelIndex != first.ChannelIndex)
                return false;
        }

        if (first is null) return false;
        route = first;
        return true;
    }

    public bool TryResolveTransportForLease(
        TvAIr.Tuner.TunerLease lease,
        IReadOnlyCollection<BroadcastServiceKey> services,
        out BonDriverChannelRoute route)
    {
        ArgumentNullException.ThrowIfNull(lease);
        route = null!;
        return lease.IsCurrent && TryResolveTransportForSource(
            lease.BonDriverFileName, lease.ChannelCh2Path, services, out route);
    }

    /// <summary>
    /// Materialize a tuner-bound channel target. Never publish this result into a
    /// wave-global ActiveTargets list: its tuning coordinates only belong to the
    /// actual leased BonDriver. The caller supplies the established scheduler
    /// group instead of guessing GR/BS/CS from an unfamiliar cable ONID.
    /// </summary>
    public bool TryBuildTargetForLease(
        TvAIr.Tuner.TunerLease lease,
        BroadcastServiceKey service,
        string scheduledGroup,
        out ChannelTarget target)
    {
        target = null!;
        if (string.IsNullOrWhiteSpace(scheduledGroup)
            || !TryResolveForLease(lease, service, out var route)) return false;

        // The chosen physical tuner must admit the caller's scheduled group;
        // do not silently reinterpret a cable service as terrestrial/satellite.
        var group = scheduledGroup.Trim().ToUpperInvariant();
        // HYBRID is only a physical route category for unclassified cable services.
        // It is never inferred as a terrestrial/satellite service identity.
        if (group is not ("GR" or "BS" or "CS" or "BSCS" or "HYBRID")) return false;
        if (group == "HYBRID" && lease.Group.Trim().ToUpperInvariant() != "HYBRID") return false;
        var leaseGroup = lease.Group.Trim().ToUpperInvariant();
        if (leaseGroup != "HYBRID" && leaseGroup != group
            && !(leaseGroup == "BSCS" && (group is "BS" or "CS"))) return false;

        var siblingCount = SourceRoutes.Count(r =>
            string.Equals(NormalizeIdentity(r.BonDriverIdentity), NormalizeIdentity(route.BonDriverIdentity), StringComparison.Ordinal)
            && r.Service.NetworkId == service.NetworkId
            && r.Service.TransportStreamId == service.TransportStreamId
            && string.Equals(r.SourceCh2Path, route.SourceCh2Path, StringComparison.OrdinalIgnoreCase));
        var argument = $"/chspace {route.TuningSpace} /chi {route.ChannelIndex}";
        if (siblingCount > 1) argument += $" /sid {service.ServiceId}";
        target = new ChannelTarget
        {
            Group = group,
            ServiceId = service.ServiceId,
            OriginalNetworkId = service.NetworkId,
            TransportStreamId = service.TransportStreamId,
            Name = route.ServiceName,
            ChannelArgument = argument,
            Ch2FileName = Path.GetFileName(route.SourceCh2Path),
            BonDriverChannel = route.ChannelIndex,
            Ch2TuningSpace = route.TuningSpace,
            Ch2ChannelIndex = route.ChannelIndex,
            ResolvedSpace = route.TuningSpace,
            ResolvedChannelIndex = route.ChannelIndex,
            SameTransportServiceCount = siblingCount,
            ChannelBuildSource = "explicit_bondriver_ch2_lease"
        };
        return true;
    }

    /// <summary>
    /// Materialize one lease-bound target per requested service only when the
    /// complete transport has been admitted by the canonical transport check.
    /// Do not construct partial EPG/recording routes if one SID is missing.
    /// </summary>
    public bool TryBuildTransportTargetsForLease(
        TvAIr.Tuner.TunerLease lease,
        IReadOnlyCollection<BroadcastServiceKey> services,
        string scheduledGroup,
        out IReadOnlyList<ChannelTarget> targets)
    {
        targets = Array.Empty<ChannelTarget>();
        if (lease is null || services is null || services.Count == 0
            || !TryResolveTransportForLease(lease, services, out _)) return false;

        var resolved = new List<ChannelTarget>();
        foreach (var service in services.Distinct().OrderBy(x => x.ServiceId))
        {
            if (!TryBuildTargetForLease(lease, service, scheduledGroup, out var target))
                return false;
            resolved.Add(target);
        }
        targets = resolved;
        return true;
    }

    public bool IsAmbiguous(string bonDriverIdentity, BroadcastServiceKey service)
    {
        var driver = NormalizeIdentity(bonDriverIdentity);
        return ambiguous.Contains((driver, service))
            || ambiguousTransports.Contains((driver, service.NetworkId, service.TransportStreamId));
    }

    private static string NormalizeIdentity(string? value)
        => (value ?? string.Empty).Trim().Replace('\\', '/').ToUpperInvariant();
}
