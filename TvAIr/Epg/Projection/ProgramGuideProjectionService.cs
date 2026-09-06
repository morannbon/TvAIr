using System.Diagnostics;
using TvAIr.Core;

namespace TvAIr.Epg.Projection;

/// <summary>
/// DB由来イベントと外部EPGイベントをProjectedProgramEventへ合成する入口。
/// DB世代と外部EPG世代が変わらない間は、全体の合成結果を1つのimmutable snapshotとして再利用する。
/// GetByRangeは同じsnapshotから範囲抽出し、日付移動ごとの再Merge・再投影を行わない。
/// </summary>
public sealed class ProgramGuideProjectionService : IProgramEventSource
{
    private readonly DbProgramEventSource _dbSource;
    private readonly ExternalEpgSourceStore _externalStore;
    private readonly LogRepository _log;
    private readonly object _fullSnapshotGate = new();
    private FullMergedSnapshot? _fullSnapshot;

    public ProgramGuideProjectionService(DbProgramEventSource dbSource, ExternalEpgSourceStore externalStore, LogRepository log)
    {
        _dbSource = dbSource;
        _externalStore = externalStore;
        _log = log;
    }

    public IReadOnlyList<ProjectedProgramEvent> ProjectCommittedDbEvents(IReadOnlyList<EpgEvent> committedEvents)
    {
        ArgumentNullException.ThrowIfNull(committedEvents);
        var dbEvents = _dbSource.ProjectCommittedDbEvents(committedEvents);
        if (dbEvents.Count == 0) return dbEvents;

        var externalSnapshot = _externalStore.GetAll();
        if (externalSnapshot.Count == 0) return dbEvents;

        // Incremental matching is scoped to rows that were just committed.  Preserve the same
        // DB-first overlay semantics as GetAll(), but do not rebuild the unrelated full guide.
        var serviceKeys = dbEvents
            .Select(e => (e.NetworkId, e.TransportStreamId, e.ServiceId))
            .ToHashSet();
        var relevantExternal = externalSnapshot
            .Where(e => serviceKeys.Contains((e.NetworkId, e.TransportStreamId, e.ServiceId)))
            .ToArray();
        if (relevantExternal.Length == 0) return dbEvents;

        var externalByEventIdentity = relevantExternal
            .Where(e => e.EventId != 0)
            .GroupBy(e => EventIdentity(e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var externalByTimingIdentity = relevantExternal
            .GroupBy(e => TimingIdentity(e.NetworkId, e.TransportStreamId, e.ServiceId, e.Start, e.End))
            .ToDictionary(g => g.Key, g => g.ToArray());

        ExternalEpgEvent? FindExternal(ProjectedProgramEvent db)
        {
            if (db.EventId != 0
                && externalByEventIdentity.TryGetValue(
                    EventIdentity(db.NetworkId, db.TransportStreamId, db.ServiceId, db.EventId),
                    out var sameEventId))
            {
                var exact = sameEventId.FirstOrDefault(e => IsSameBroadcastEvent(db, e));
                if (exact is not null) return exact;
            }

            return externalByTimingIdentity.TryGetValue(
                    TimingIdentity(db.NetworkId, db.TransportStreamId, db.ServiceId, db.Start, db.End),
                    out var sameTiming)
                ? sameTiming.FirstOrDefault(e => IsSameBroadcastEvent(db, e))
                : null;
        }

        var result = new ProjectedProgramEvent[dbEvents.Count];
        for (var i = 0; i < dbEvents.Count; i++)
        {
            var db = dbEvents[i];
            var external = FindExternal(db);
            result[i] = external is null ? db : OverlayDbEvent(db, external);
        }

        return result;
    }

    public IReadOnlyList<ProjectedProgramEvent> GetAll()
    {
        var snapshot = GetFullMergedSnapshot();
        return snapshot.Rows;
    }

    public IReadOnlyList<ProjectedProgramEvent> GetByRange(DateTime from, DateTime to)
    {
        // ProgramGuideProjectionContract:
        // 日付移動は表示範囲を変えるだけで、DB/External EPGの世代自体は変えない。
        // 同一revision中はGetAllと同じ合成snapshotを正本とし、範囲ごとのMergeを禁止する。
        // 日付別cacheを積み増さず、保持する合成世代は常にこの1本だけとする。
        var snapshot = GetFullMergedSnapshot();
        var rows = new List<ProjectedProgramEvent>();
        foreach (var row in snapshot.Rows)
        {
            if (row.End > from && row.Start < to)
                rows.Add(row);
        }

        return rows;
    }

    private FullMergedSnapshot GetFullMergedSnapshot()
    {
        var dbSnapshot = _dbSource.CaptureProjectionSnapshot();
        var externalSnapshot = _externalStore.CaptureProjectionSnapshot();

        var cached = Volatile.Read(ref _fullSnapshot);
        if (cached is not null
            && cached.DbRevision == dbSnapshot.Revision
            && cached.ExternalRevision == externalSnapshot.Revision)
            return cached;

        lock (_fullSnapshotGate)
        {
            cached = _fullSnapshot;
            if (cached is not null
                && cached.DbRevision == dbSnapshot.Revision
                && cached.ExternalRevision == externalSnapshot.Revision)
                return cached;

            var merged = Merge(dbSnapshot.Rows, externalSnapshot.Rows, _log, "GetAll", null, null);
            var stableRows = Array.AsReadOnly(merged.ToArray());
            cached = new FullMergedSnapshot(
                dbSnapshot.Revision,
                externalSnapshot.Revision,
                dbSnapshot.Rows.Count,
                externalSnapshot.Rows.Count,
                stableRows);
            Volatile.Write(ref _fullSnapshot, cached);
            return cached;
        }
    }


    public ProjectedProgramEvent? GetByEventKey(ushort networkId, ushort transportStreamId, ushort serviceId, ushort eventId)
    {
        var db = _dbSource.GetByEventKey(networkId, transportStreamId, serviceId, eventId);
        var external = _externalStore.GetAll()
            .FirstOrDefault(e => e.NetworkId == networkId
                              && e.TransportStreamId == transportStreamId
                              && e.ServiceId == serviceId
                              && e.EventId == eventId);

        if (db is not null)
            return external is not null && IsSameBroadcastEvent(db, external)
                ? OverlayDbEvent(db, external)
                : db;

        return external is null ? null : ToOverlayOnly(external);
    }

    public ProjectedProgramEvent? GetByProjectedKey(ProjectedEventKey key)
    {
        if (string.Equals(key.SourceKind, ProjectedEventSourceKinds.TvAirDb, StringComparison.OrdinalIgnoreCase))
        {
            var db = _dbSource.GetByProjectedKey(key);
            if (db is null) return null;

            var external = _externalStore.GetAll().FirstOrDefault(e => IsSameBroadcastEvent(db, e));
            return external is null ? db : OverlayDbEvent(db, external);
        }

        var externalEvent = _externalStore.GetByProjectedKey(key);
        if (externalEvent is null) return null;

        var promotedDb = _dbSource.GetByEventKey(
            externalEvent.NetworkId,
            externalEvent.TransportStreamId,
            externalEvent.ServiceId,
            externalEvent.EventId);

        if (promotedDb is not null && IsSameBroadcastEvent(promotedDb, externalEvent))
            return OverlayDbEvent(promotedDb, externalEvent);

        var overlayOnly = ToOverlayOnly(externalEvent);
        if (key.Start > overlayOnly.Start || key.End < overlayOnly.End)
        {
            if (key.Start >= overlayOnly.Start && key.End <= overlayOnly.End && key.End > key.Start)
                return CloneTimelineFragment(overlayOnly, key.Start, key.End, "db_interval_subtraction");
        }

        return overlayOnly;
    }


    private IReadOnlyList<ProjectedProgramEvent> Merge(
        IReadOnlyList<ProjectedProgramEvent> dbEvents,
        IReadOnlyList<ExternalEpgEvent> externalEvents,
        LogRepository log,
        string route,
        DateTime? rangeFrom,
        DateTime? rangeTo)
    {
        if (externalEvents.Count == 0) return dbEvents;

#if TVAIR_DEVELOPER_DIAGNOSTICS
        var elapsed = Stopwatch.StartNew();
#endif
        var result = new List<ProjectedProgramEvent>(dbEvents.Count + externalEvents.Count);
        var matchedExternal = new HashSet<string>(StringComparer.Ordinal);
        var dbWithOverlay = 0;
        var overlayOnly = 0;

        // The display route can merge thousands of DB and ExternalEpg rows per day.
        // A per-DB FirstOrDefault over the full external snapshot turns this into O(DB × external)
        // and made seven-day guide loading grow to tens of seconds.  Build immutable request-local
        // identity indexes once, then keep the same event-id-first / timing-fallback contract.
        var externalByEventIdentity = externalEvents
            .Where(e => e.EventId != 0)
            .GroupBy(e => EventIdentity(e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var externalByTimingIdentity = externalEvents
            .GroupBy(e => TimingIdentity(e.NetworkId, e.TransportStreamId, e.ServiceId, e.Start, e.End))
            .ToDictionary(g => g.Key, g => g.ToArray());

        ExternalEpgEvent? FindExternal(ProjectedProgramEvent db)
        {
            if (db.EventId != 0
                && externalByEventIdentity.TryGetValue(
                    EventIdentity(db.NetworkId, db.TransportStreamId, db.ServiceId, db.EventId),
                    out var sameEventId))
            {
                var exact = sameEventId.FirstOrDefault(e => IsSameBroadcastEvent(db, e));
                if (exact is not null) return exact;
            }

            return externalByTimingIdentity.TryGetValue(
                    TimingIdentity(db.NetworkId, db.TransportStreamId, db.ServiceId, db.Start, db.End),
                    out var sameTiming)
                ? sameTiming.FirstOrDefault(e => IsSameBroadcastEvent(db, e))
                : null;
        }

        foreach (var db in dbEvents)
        {
            var external = FindExternal(db);
            if (external is null)
            {
                result.Add(db);
                continue;
            }

            matchedExternal.Add(ExternalIdentity(external));
            dbWithOverlay++;
            result.Add(OverlayDbEvent(db, external));
        }

        var directDbPromoted = 0;
        var externalSuppressedByDbIdentity = 0;
        var unmatchedExternal = externalEvents
            .Where(external => !matchedExternal.Contains(ExternalIdentity(external)))
            .ToArray();

        // Preserve the global DB-identity boundary contract without issuing one SQLite query
        // per overlay-only candidate.  Resolve all unmatched event identities in bounded batches
        // and keep the same IsSameBroadcastEvent / range-overlap decisions in memory.
        IReadOnlyDictionary<string, ProjectedProgramEvent[]> directDbByIdentity =
            new Dictionary<string, ProjectedProgramEvent[]>(StringComparer.Ordinal);
        var directDbLookupKeys = 0;
        var directDbLookupRows = 0;
        if (rangeFrom is not null && rangeTo is not null && unmatchedExternal.Length > 0)
        {
            var keys = unmatchedExternal
                .Select(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
                .Distinct()
                .ToArray();
            directDbLookupKeys = keys.Length;
            var directDbRows = _dbSource.GetByEventKeys(keys);
            directDbLookupRows = directDbRows.Count;
            directDbByIdentity = directDbRows
                .GroupBy(e => EventIdentity(e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Start).ThenBy(e => e.End).ToArray(), StringComparer.Ordinal);
        }

        var overlayFragments = 0;
        var overlayFullyCovered = 0;
        foreach (var external in unmatchedExternal)
        {
            if (rangeFrom is not null && rangeTo is not null
                && directDbByIdentity.TryGetValue(
                    EventIdentity(external.NetworkId, external.TransportStreamId, external.ServiceId, external.EventId),
                    out var directDbCandidates))
            {
                var directDb = directDbCandidates.FirstOrDefault(candidate => IsSameBroadcastEvent(candidate, external));
                if (directDb is not null)
                {
                    if (directDb.End > rangeFrom.Value && directDb.Start < rangeTo.Value)
                    {
                        directDbPromoted++;
                        dbWithOverlay++;
                        result.Add(OverlayDbEvent(directDb, external));
                    }
                    else
                    {
                        externalSuppressedByDbIdentity++;
                    }

                    continue;
                }
            }

            var externalProjected = ToOverlayOnly(external);
            var segments = ComposeUncoveredExternalSegments(externalProjected, dbEvents, rangeFrom, rangeTo);
            if (segments.Count == 0)
            {
                overlayFullyCovered++;
                continue;
            }

            overlayOnly += segments.Count;
            overlayFragments += segments.Count(e => e.IsTimelineFragment);
            result.AddRange(segments);
        }

        var merged = result.OrderBy(e => e.Start).ThenBy(e => e.NetworkId).ThenBy(e => e.TransportStreamId).ThenBy(e => e.ServiceId).ToList();
#if TVAIR_DEVELOPER_DIAGNOSTICS
        elapsed.Stop();
        log.Add("PROGRAM_GUIDE_PROJECTION_MERGE", "ExternalEpg",
            $"result=OK route={route} dbEvents={dbEvents.Count} externalEvents={externalEvents.Count} dbWithOverlay={dbWithOverlay} overlayOnly={overlayOnly} directDbPromoted={directDbPromoted} externalSuppressedByDbIdentity={externalSuppressedByDbIdentity} overlayFragments={overlayFragments} overlayFullyCovered={overlayFullyCovered} directDbLookupKeys={directDbLookupKeys} directDbLookupRows={directDbLookupRows} merged={merged.Count} elapsedMs={elapsed.ElapsedMilliseconds} matchStrategy=request_local_identity_index directDbStrategy=batched_event_identity_lookup target=runtime_projection rule=program_guide_projection_contract");
#endif
        return merged;
    }

    private static IReadOnlyList<ProjectedProgramEvent> ComposeUncoveredExternalSegments(
        ProjectedProgramEvent external,
        IReadOnlyList<ProjectedProgramEvent> dbEvents,
        DateTime? rangeFrom,
        DateTime? rangeTo)
    {
        var start = rangeFrom is not null && external.Start < rangeFrom.Value ? rangeFrom.Value : external.Start;
        var end = rangeTo is not null && external.End > rangeTo.Value ? rangeTo.Value : external.End;
        if (end <= start) return Array.Empty<ProjectedProgramEvent>();

        var covered = dbEvents
            .Where(e => e.DbEventExists)
            .Where(e => e.NetworkId == external.NetworkId
                     && e.TransportStreamId == external.TransportStreamId
                     && e.ServiceId == external.ServiceId)
            .Select(e => (Start: e.Start < start ? start : e.Start, End: e.End > end ? end : e.End))
            .Where(x => x.End > x.Start)
            .OrderBy(x => x.Start)
            .ThenBy(x => x.End)
            .ToList();

        if (covered.Count == 0)
            return new[] { external };

        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var interval in covered)
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End)
                merged.Add(interval);
            else if (interval.End > merged[^1].End)
                merged[^1] = (merged[^1].Start, interval.End);
        }

        var segments = new List<ProjectedProgramEvent>();
        var cursor = start;
        foreach (var interval in merged)
        {
            if (interval.Start > cursor)
                segments.Add(CloneTimelineFragment(external, cursor, interval.Start, "db_interval_subtraction"));
            if (interval.End > cursor) cursor = interval.End;
            if (cursor >= end) break;
        }
        if (cursor < end)
            segments.Add(CloneTimelineFragment(external, cursor, end, "db_interval_subtraction"));
        return segments;
    }

    private static ProjectedProgramEvent CloneTimelineFragment(
        ProjectedProgramEvent source, DateTime start, DateTime end, string reason)
    {
        var canonicalStart = source.CanonicalStart == default ? source.Start : source.CanonicalStart;
        var canonicalEnd = source.CanonicalEnd == default ? source.End : source.CanonicalEnd;
        var fragmentKey = new ProjectedEventKey(
            source.Key.SourceKind, source.Key.SourcePluginId, source.Key.SourceEventKey,
            source.NetworkId, source.TransportStreamId, source.ServiceId, source.EventId,
            start, end, source.Key.TitleKey);
        return new ProjectedProgramEvent
        {
            Key = fragmentKey,
            ProjectionState = source.ProjectionState,
            NetworkId = source.NetworkId, TransportStreamId = source.TransportStreamId, ServiceId = source.ServiceId, EventId = source.EventId,
            Start = start, End = end, DurationSeconds = (int)(end - start).TotalSeconds,
            CanonicalStart = canonicalStart, CanonicalEnd = canonicalEnd, IsTimelineFragment = true, TimelineFragmentReason = reason,
            ServiceName = source.ServiceName, Title = source.Title, ShortText = source.ShortText, ExtendedText = source.ExtendedText, ExtendedItems = source.ExtendedItems,
            UpdatedAt = source.UpdatedAt, CellText = source.CellText, Genre = source.Genre, GenreCodes = source.GenreCodes,
            DbEventExists = source.DbEventExists, DbEvent = source.DbEvent, SourceKind = source.SourceKind, SourcePluginId = source.SourcePluginId, SourceEventKey = source.SourceEventKey,
            ProjectionTitleDbPresent = source.ProjectionTitleDbPresent, ProjectionTitleOverlayCandidatePresent = source.ProjectionTitleOverlayCandidatePresent, ProjectionTitleSource = source.ProjectionTitleSource,
            ProjectionOutlineDbPresent = source.ProjectionOutlineDbPresent, ProjectionOutlineOverlayCandidatePresent = source.ProjectionOutlineOverlayCandidatePresent, ProjectionOutlineSource = source.ProjectionOutlineSource,
            ProjectionDetailDbPresent = source.ProjectionDetailDbPresent, ProjectionDetailOverlayCandidatePresent = source.ProjectionDetailOverlayCandidatePresent, ProjectionDetailSource = source.ProjectionDetailSource
        };
    }

    private static string EventIdentity(ushort networkId, ushort transportStreamId, ushort serviceId, ushort eventId)
        => $"{networkId}:{transportStreamId}:{serviceId}:{eventId}";

    private static string TimingIdentity(ushort networkId, ushort transportStreamId, ushort serviceId, DateTime start, DateTime end)
        => $"{networkId}:{transportStreamId}:{serviceId}:{start.Ticks}:{end.Ticks}";

    private static bool IsSameBroadcastEvent(ProjectedProgramEvent db, ExternalEpgEvent external)
    {
        if (db.NetworkId != external.NetworkId) return false;
        if (db.TransportStreamId != external.TransportStreamId) return false;
        if (db.ServiceId != external.ServiceId) return false;

        if (db.EventId != 0 && external.EventId != 0 && db.EventId == external.EventId)
            return db.End > external.Start && db.Start < external.End;
        return db.Start == external.Start && db.End == external.End;
    }

    private static ProjectedProgramEvent OverlayDbEvent(ProjectedProgramEvent db, ExternalEpgEvent external)
    {
        var dbTitle = FirstNonEmpty(db.Title);
        var overlayTitle = FirstNonEmpty(external.Title);
        var dbShortText = FirstNonEmpty(db.ShortText);
        var overlayShortText = FirstNonEmpty(external.ShortText);
        var dbExtendedText = FirstNonEmpty(db.ExtendedText);
        var overlayExtendedText = FirstNonEmpty(external.ExtendedText, external.ExtendedItems);

        // DbWithOverlay keeps TvAIr DB as the identity and title authority.
        // Overlay text is used only when the corresponding DB display field is empty.
        var title = FirstNonEmpty(dbTitle, overlayTitle);
        var shortText = FirstNonEmpty(dbShortText, overlayShortText);
        var extendedText = FirstNonEmpty(dbExtendedText, overlayExtendedText);
        var cellText = BuildCellText(title, shortText, extendedText);

        return new ProjectedProgramEvent
        {
            Key = db.Key,
            ProjectionState = ProjectedEventStates.DbWithOverlay,
            NetworkId = db.NetworkId,
            TransportStreamId = db.TransportStreamId,
            ServiceId = db.ServiceId,
            EventId = db.EventId,
            Start = db.Start,
            End = db.End,
            DurationSeconds = db.DurationSeconds,
            CanonicalStart = db.Start,
            CanonicalEnd = db.End,
            ServiceName = FirstNonEmpty(db.ServiceName, external.ServiceName),
            Title = title,
            ShortText = shortText,
            ExtendedText = extendedText,
            ExtendedItems = db.ExtendedItems.Length > 0 ? db.ExtendedItems : external.ExtendedItems,
            UpdatedAt = db.UpdatedAt != default ? db.UpdatedAt : external.UpdatedAt,
            CellText = cellText,
            Genre = FirstNonEmpty(db.Genre, external.Genre),
            GenreCodes = FirstNonEmpty(db.GenreCodes, external.GenreCodes),
            DbEventExists = true,
            DbEvent = db.DbEvent,
            SourceKind = db.SourceKind,
            SourcePluginId = external.SourcePluginId,
            SourceEventKey = external.SourceEventKey,
            ProjectionTitleDbPresent = dbTitle.Length > 0,
            ProjectionTitleOverlayCandidatePresent = overlayTitle.Length > 0,
            ProjectionTitleSource = dbTitle.Length > 0 ? "db" : overlayTitle.Length > 0 ? "overlay" : "none",
            ProjectionOutlineDbPresent = dbShortText.Length > 0,
            ProjectionOutlineOverlayCandidatePresent = overlayShortText.Length > 0,
            ProjectionOutlineSource = dbShortText.Length > 0 ? "db" : overlayShortText.Length > 0 ? "overlay" : "none",
            ProjectionDetailDbPresent = dbExtendedText.Length > 0,
            ProjectionDetailOverlayCandidatePresent = overlayExtendedText.Length > 0,
            ProjectionDetailSource = dbExtendedText.Length > 0 ? "db" : overlayExtendedText.Length > 0 ? "overlay" : "none"
        };
    }

    private static ProjectedProgramEvent ToOverlayOnly(ExternalEpgEvent external)
    {
        var key = ProjectedEventKey.FromExternal(external);
        var extendedText = FirstNonEmpty(external.ExtendedText, external.ExtendedItems);
        return new ProjectedProgramEvent
        {
            Key = key,
            ProjectionState = ProjectedEventStates.OverlayOnly,
            NetworkId = external.NetworkId,
            TransportStreamId = external.TransportStreamId,
            ServiceId = external.ServiceId,
            EventId = external.EventId,
            Start = external.Start,
            End = external.End,
            DurationSeconds = external.DurationSeconds,
            CanonicalStart = external.Start,
            CanonicalEnd = external.End,
            ServiceName = external.ServiceName ?? string.Empty,
            Title = external.Title ?? string.Empty,
            ShortText = external.ShortText ?? string.Empty,
            ExtendedText = extendedText,
            ExtendedItems = external.ExtendedItems ?? string.Empty,
            UpdatedAt = external.UpdatedAt,
            CellText = BuildCellText(external.Title, external.ShortText, extendedText),
            Genre = external.Genre ?? string.Empty,
            GenreCodes = external.GenreCodes ?? string.Empty,
            DbEventExists = false,
            DbEvent = null,
            SourceKind = ProjectedEventSourceKinds.ExternalEpg,
            SourcePluginId = external.SourcePluginId,
            SourceEventKey = external.SourceEventKey,
            ProjectionTitleDbPresent = false,
            ProjectionTitleOverlayCandidatePresent = !string.IsNullOrWhiteSpace(external.Title),
            ProjectionTitleSource = !string.IsNullOrWhiteSpace(external.Title) ? "overlay" : "none",
            ProjectionOutlineDbPresent = false,
            ProjectionOutlineOverlayCandidatePresent = !string.IsNullOrWhiteSpace(external.ShortText),
            ProjectionOutlineSource = !string.IsNullOrWhiteSpace(external.ShortText) ? "overlay" : "none",
            ProjectionDetailDbPresent = false,
            ProjectionDetailOverlayCandidatePresent = !string.IsNullOrWhiteSpace(extendedText),
            ProjectionDetailSource = !string.IsNullOrWhiteSpace(extendedText) ? "overlay" : "none"
        };
    }

    private static string ExternalIdentity(ExternalEpgEvent e)
        => $"{e.SourcePluginId}\u001f{e.SourceEventKey}";

    private static string BuildCellText(params string?[] parts)
        => string.Join("\n", parts.Select(v => (v ?? string.Empty).Trim()).Where(v => v.Length > 0));

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length > 0) return text;
        }
        return string.Empty;
    }
    private sealed record FullMergedSnapshot(
        long DbRevision,
        long ExternalRevision,
        int DbCount,
        int ExternalCount,
        IReadOnlyList<ProjectedProgramEvent> Rows);

}
