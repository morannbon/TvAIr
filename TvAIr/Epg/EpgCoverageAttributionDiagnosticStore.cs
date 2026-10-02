#if TVAIR_DEVELOPER_DIAGNOSTICS
using TvAIr.Core;
using TvAIr.Epg.Projection;

namespace TvAIr.Epg;

/// <summary>
/// Developer Diagnostics only. Captures one normal-EPG run's observation/commit/projection evidence
/// without mutating product EPG data or changing capture, scheduling, tuner, or worker behavior.
/// </summary>
public sealed class EpgCoverageAttributionDiagnosticStore
{
    [Flags]
    private enum CoverageFlags
    {
        None = 0,
        ActualObserved = 1 << 0,
        OtherObserved = 1 << 1,
        OtherConfiguredResolved = 1 << 2,
        OtherBasicAuthority = 1 << 3,
        OtherSupplementCandidate = 1 << 4,
        OtherInserted = 1 << 5,
        ActualCommitCandidate = 1 << 6,
        ActualPresentAfterCommit = 1 << 7,
        ActualBasicAuthority = 1 << 8,
    }

    internal sealed record AttributionSnapshot(
        string RunId,
        bool AttributionAvailable,
        bool ActualObserved,
        bool OtherObserved,
        bool OtherConfiguredResolved,
        bool OtherBasicAuthority,
        bool OtherSupplementCandidate,
        bool OtherInserted,
        bool ActualCommitCandidate,
        bool ActualPresentAfterCommit);

    internal sealed record ServiceObservationSnapshot(
        bool ActualObserved,
        bool OtherObserved,
        int ActualEventCount,
        int OtherEventCount,
        string ActualTableIds,
        string OtherTableIds);

    internal enum DailyCoverageAuditResult
    {
        Pass,
        Fail,
        IncompleteEvidence,
    }

    internal sealed record DailyCoverageAudit(
        string RunId,
        DateTime Date,
        DailyCoverageAuditResult Result,
        int TargetServices,
        int ScheduleCompleteServices,
        int ScheduleIncompleteServices,
        int AuthoritativeEvents,
        int DbPresent,
        int DbMissing,
        int ProjectionPresent,
        int ProjectionMissing,
        int BlankTitleCount,
        int DefectIdentityCount,
        string IncompleteServiceSample,
        string DbMissingSample,
        string ProjectionMissingSample,
        string BlankTitleSample);

    private sealed class ServiceObservation
    {
        public bool ActualObserved;
        public bool OtherObserved;
        public int ActualEventCount;
        public int OtherEventCount;
        public ulong ActualTableMask;
        public ulong OtherTableMask;
    }

    private sealed record ServiceCoverage(bool Complete, string Detail);
    private readonly record struct EventIdentityKey(ushort NetworkId, ushort TransportStreamId, ushort ServiceId, ushort EventId, long StartTicks, int DurationSeconds)
    {
        public DateTime Start => new(StartTicks);
        public DateTime End => Start.AddSeconds(Math.Max(0, DurationSeconds));
        public override string ToString() => $"{NetworkId}:{TransportStreamId}:{ServiceId}:{EventId}:{StartTicks}:{DurationSeconds}";
    }
    private readonly record struct ServiceIdentityKey(ushort NetworkId, ushort TransportStreamId, ushort ServiceId)
    {
        public override string ToString() => $"{NetworkId}:{TransportStreamId}:{ServiceId}";
    }

    private readonly object gate = new();
    private readonly Dictionary<EventIdentityKey, CoverageFlags> flagsByIdentity = new();
    private readonly Dictionary<ServiceIdentityKey, ServiceObservation> serviceObservations = new();
    private readonly Dictionary<ServiceIdentityKey, ServiceCoverage> serviceCoverage = new();
    private string runId = "none";
    private const int MaxIdentities = 100_000;

    internal void BeginRun(string newRunId)
    {
        lock (gate)
        {
            runId = string.IsNullOrWhiteSpace(newRunId) ? "none" : newRunId.Trim();
            flagsByIdentity.Clear();
            serviceObservations.Clear();
            serviceCoverage.Clear();
        }
    }

    internal void ObserveActual(IEnumerable<ParsedEpgEvent> events)
    {
        foreach (var e in events)
        {
            AddOne(Identity(e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId, e.Start, e.DurationSeconds), CoverageFlags.ActualObserved);
            ObserveService(e.NetworkId, e.TransportStreamId, e.ServiceId, e.BestTableId, actual: true);
        }
    }

    internal void ObserveOther(
        IEnumerable<ParsedEpgEvent> events,
        ISet<(ushort NetworkId, ushort TransportStreamId, ushort ServiceId)> configuredServices,
        ISet<(ushort NetworkId, ushort TransportStreamId, ushort ServiceId, ushort EventId, DateTime Start, int DurationSeconds)> basicAuthority)
    {
        foreach (var e in events)
        {
            var key = Identity(e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId, e.Start, e.DurationSeconds);
            AddOne(key, CoverageFlags.OtherObserved);
            ObserveService(e.NetworkId, e.TransportStreamId, e.ServiceId, e.BestTableId, actual: false);
            if (configuredServices.Contains((e.NetworkId, e.TransportStreamId, e.ServiceId)))
                AddOne(key, CoverageFlags.OtherConfiguredResolved);
            if (basicAuthority.Contains((e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId, e.Start, e.DurationSeconds)))
                AddOne(key, CoverageFlags.OtherBasicAuthority);
        }
    }

    internal void MarkSupplementCandidates(IEnumerable<EpgEvent> events)
        => Add(events.Select(EventIdentity), CoverageFlags.OtherSupplementCandidate);

    internal void MarkInserted(IEnumerable<EpgEvent> events)
        => Add(events.Select(EventIdentity), CoverageFlags.OtherInserted);

    internal void MarkActualCommitCandidates(IEnumerable<EpgEvent> events)
        => Add(events.Select(EventIdentity), CoverageFlags.ActualCommitCandidate);

    internal void MarkActualPresentAfterCommit(IEnumerable<EpgEvent> events)
        => Add(events.Select(EventIdentity), CoverageFlags.ActualPresentAfterCommit);

    internal void MarkActualBasicAuthority(IEnumerable<EpgEvent> events)
        => Add(events.Select(EventIdentity), CoverageFlags.ActualBasicAuthority);

    internal void RecordBasicScheduleCoverage(
        IEnumerable<(ushort NetworkId, ushort TransportStreamId, ushort ServiceId)> targetServices,
        IReadOnlyList<EpgSectionStatus> basicStatuses,
        IReadOnlyCollection<string> coverageIssues,
        int rejectedBasicEventHeaders,
        int ignoredBasicVersionSwitchSections,
        int toleratedSameVersionMetadataDrift)
    {
        lock (gate)
        {
            foreach (var target in targetServices.Distinct())
            {
                var statuses = basicStatuses.Where(x => x.ServiceId == target.ServiceId).ToArray();
                var sidPrefix = $"SID={target.ServiceId}:";
                var issues = coverageIssues.Where(x => x.StartsWith(sidPrefix, StringComparison.Ordinal)).ToArray();
                var complete = statuses.Length > 0
                    && statuses.All(x => x.IsComplete)
                    && issues.Length == 0
                    && rejectedBasicEventHeaders == 0
                    && ignoredBasicVersionSwitchSections == 0
                    && toleratedSameVersionMetadataDrift == 0;
                var detail = statuses.Length == 0
                    ? "basic_schedule_not_observed"
                    : issues.Length > 0
                        ? string.Join(";", issues)
                        : !statuses.All(x => x.IsComplete)
                            ? string.Join(";", statuses.Where(x => !x.IsComplete).Select(x => $"table=0x{x.TableId:X2}:sec={x.SeenSectionCount}/{x.ExpectedSectionCount}:seg={x.SegmentSeenTotal}/{x.SegmentExpectedTotal}"))
                            : rejectedBasicEventHeaders > 0
                                ? $"rejected_basic_headers={rejectedBasicEventHeaders}"
                                : ignoredBasicVersionSwitchSections > 0
                                    ? $"version_switch_sections={ignoredBasicVersionSwitchSections}"
                                    : toleratedSameVersionMetadataDrift > 0
                                        ? $"metadata_drift={toleratedSameVersionMetadataDrift}"
                                        : "complete";
                serviceCoverage[ServiceIdentity(target.NetworkId, target.TransportStreamId, target.ServiceId)] = new ServiceCoverage(complete, detail);
            }
        }
    }

    internal DailyCoverageAudit BuildDailyAudit(
        DateTime date,
        DateTime evaluationFrom,
        IReadOnlyList<EpgEvent> dbEvents,
        IReadOnlyList<ProjectedProgramEvent> projectedEvents)
    {
        lock (gate)
        {
            var dayStart = date.Date;
            var from = evaluationFrom > dayStart ? evaluationFrom : dayStart;
            var to = dayStart.AddDays(1);
            var authoritativeKeys = flagsByIdentity
                .Where(x => x.Value.HasFlag(CoverageFlags.ActualBasicAuthority)
                    && x.Key.End > from
                    && x.Key.Start < to)
                .Select(x => x.Key)
                .Distinct()
                .ToArray();

            var dbSet = dbEvents.Select(EventIdentity).ToHashSet();
            var projectionSet = projectedEvents
                .Where(x => x.DbEventExists)
                .Select(x => Identity(x.NetworkId, x.TransportStreamId, x.ServiceId, x.EventId, x.Start, x.DurationSeconds))
                .ToHashSet();
            var projectionByIdentity = projectedEvents
                .Where(x => x.DbEventExists)
                .GroupBy(x => Identity(x.NetworkId, x.TransportStreamId, x.ServiceId, x.EventId, x.Start, x.DurationSeconds))
                .ToDictionary(x => x.Key, x => x.First());
            var dbMissing = authoritativeKeys.Where(x => !dbSet.Contains(x)).ToArray();
            var projectionMissing = authoritativeKeys.Where(x => !projectionSet.Contains(x)).ToArray();
            var blankTitles = authoritativeKeys
                .Where(x => projectionByIdentity.TryGetValue(x, out var p) && string.IsNullOrWhiteSpace(p.Title))
                .ToArray();
            var defectIdentities = dbMissing
                .Concat(projectionMissing)
                .Concat(blankTitles)
                .Distinct()
                .ToArray();
            var incomplete = serviceCoverage.Where(x => !x.Value.Complete).ToArray();
            var result = serviceCoverage.Count <= 0 || authoritativeKeys.Length <= 0
                ? DailyCoverageAuditResult.IncompleteEvidence
                : defectIdentities.Length > 0
                    ? DailyCoverageAuditResult.Fail
                    : DailyCoverageAuditResult.Pass;

            return new DailyCoverageAudit(
                runId,
                from,
                result,
                serviceCoverage.Count,
                serviceCoverage.Count - incomplete.Length,
                incomplete.Length,
                authoritativeKeys.Length,
                authoritativeKeys.Length - dbMissing.Length,
                dbMissing.Length,
                authoritativeKeys.Length - projectionMissing.Length,
                projectionMissing.Length,
                blankTitles.Length,
                defectIdentities.Length,
                Sample(incomplete.Select(x => $"{x.Key}:{x.Value.Detail}")),
                Sample(dbMissing.Select(x => x.ToString())),
                Sample(projectionMissing.Select(x => x.ToString())),
                Sample(blankTitles.Select(x => x.ToString())));
        }
    }

    internal string CurrentRunId
    {
        get { lock (gate) return runId; }
    }

    internal AttributionSnapshot Lookup(ushort nid, ushort tsid, ushort sid, ushort eventId, DateTime start, int durationSeconds)
    {
        lock (gate)
        {
            flagsByIdentity.TryGetValue(Identity(nid, tsid, sid, eventId, start, durationSeconds), out var flags);
            return new AttributionSnapshot(
                runId,
                !string.Equals(runId, "none", StringComparison.Ordinal),
                flags.HasFlag(CoverageFlags.ActualObserved),
                flags.HasFlag(CoverageFlags.OtherObserved),
                flags.HasFlag(CoverageFlags.OtherConfiguredResolved),
                flags.HasFlag(CoverageFlags.OtherBasicAuthority),
                flags.HasFlag(CoverageFlags.OtherSupplementCandidate),
                flags.HasFlag(CoverageFlags.OtherInserted),
                flags.HasFlag(CoverageFlags.ActualCommitCandidate),
                flags.HasFlag(CoverageFlags.ActualPresentAfterCommit));
        }
    }

    internal ServiceObservationSnapshot LookupService(ushort nid, ushort tsid, ushort sid)
    {
        lock (gate)
        {
            if (!serviceObservations.TryGetValue(ServiceIdentity(nid, tsid, sid), out var obs))
                return new ServiceObservationSnapshot(false, false, 0, 0, "-", "-");
            return new ServiceObservationSnapshot(
                obs.ActualObserved,
                obs.OtherObserved,
                obs.ActualEventCount,
                obs.OtherEventCount,
                FormatTableMask(obs.ActualTableMask),
                FormatTableMask(obs.OtherTableMask));
        }
    }

    private void ObserveService(ushort nid, ushort tsid, ushort sid, byte tableId, bool actual)
    {
        lock (gate)
        {
            var key = ServiceIdentity(nid, tsid, sid);
            if (!serviceObservations.TryGetValue(key, out var obs))
            {
                obs = new ServiceObservation();
                serviceObservations[key] = obs;
            }
            if (actual)
            {
                obs.ActualObserved = true;
                obs.ActualEventCount++;
                obs.ActualTableMask |= TableMask(tableId);
            }
            else
            {
                obs.OtherObserved = true;
                obs.OtherEventCount++;
                obs.OtherTableMask |= TableMask(tableId);
            }
        }
    }

    private static ulong TableMask(byte tableId)
        => tableId is >= 0x4E and <= 0x6F ? 1UL << (tableId - 0x4E) : 0UL;

    private static string FormatTableMask(ulong mask)
    {
        if (mask == 0) return "-";
        var ids = new List<string>();
        for (var i = 0; i <= 0x6F - 0x4E; i++)
        {
            if ((mask & (1UL << i)) != 0)
                ids.Add($"0x{0x4E + i:X2}");
        }
        return string.Join("/", ids);
    }

    private static ServiceIdentityKey ServiceIdentity(ushort nid, ushort tsid, ushort sid)
        => new(nid, tsid, sid);

    private void Add(IEnumerable<EventIdentityKey> identities, CoverageFlags flags)
    {
        foreach (var identity in identities)
            AddOne(identity, flags);
    }

    private void AddOne(EventIdentityKey identity, CoverageFlags flags)
    {
        lock (gate)
        {
            if (!flagsByIdentity.ContainsKey(identity) && flagsByIdentity.Count >= MaxIdentities)
                return;
            flagsByIdentity.TryGetValue(identity, out var current);
            flagsByIdentity[identity] = current | flags;
        }
    }

    private static EventIdentityKey EventIdentity(EpgEvent e)
        => Identity(e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId, e.Start, e.DurationSeconds);

    private static EventIdentityKey Identity(ushort nid, ushort tsid, ushort sid, ushort eventId, DateTime start, int durationSeconds)
        => new(nid, tsid, sid, eventId, start.Ticks, durationSeconds);

    private static string Sample(IEnumerable<string> values)
        => string.Join(" | ", values.Take(20));
}
#endif
