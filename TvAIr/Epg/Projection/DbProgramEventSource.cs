using TvAIr.Core;

namespace TvAIr.Epg.Projection;

/// <summary>
/// TvAIr DB由来イベントだけをProjectedProgramEventへ変換する第1段階の入口。
/// 外部EPG投影はまだ扱わない。
///
/// epg_events の確定世代が変わらない間は、DB→ProjectedProgramEvent の基底投影を
/// 1つのimmutable snapshotとして再利用する。GetAll/GetByRange/GetByEventKey(s)ごとに
/// 同じDB行・CellText・ProjectedProgramEventを作り直さない。
/// </summary>
public sealed class DbProgramEventSource : IProgramEventSource
{
    private readonly EpgStore _epgStore;
    private readonly object _snapshotGate = new();
    private ProjectionSnapshot? _snapshot;

    public DbProgramEventSource(EpgStore epgStore) => _epgStore = epgStore;

    public IReadOnlyList<ProjectedProgramEvent> ProjectCommittedDbEvents(IReadOnlyList<EpgEvent> committedEvents)
    {
        ArgumentNullException.ThrowIfNull(committedEvents);
        if (committedEvents.Count == 0) return Array.Empty<ProjectedProgramEvent>();

        // CommitCapture normalizes descriptor-backed fields before writing. Read only the keys
        // from this committed batch so incremental matching sees the same DB authority as GetAll(),
        // without rebuilding the full DB projection snapshot after every transport-stream commit.
        var keys = committedEvents
            .Select(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
            .Distinct()
            .ToArray();
        var storedEvents = _epgStore.GetByEventKeys(keys);
        var rows = new ProjectedProgramEvent[storedEvents.Count];
        for (var i = 0; i < storedEvents.Count; i++)
            rows[i] = ToProjected(storedEvents[i]);
        return rows;
    }

    public IReadOnlyList<ProjectedProgramEvent> GetAll()
        => GetSnapshot().Rows;

    internal (long Revision, IReadOnlyList<ProjectedProgramEvent> Rows) CaptureProjectionSnapshot()
    {
        var snapshot = GetSnapshot();
        return (snapshot.Revision, snapshot.Rows);
    }

    public IReadOnlyList<ProjectedProgramEvent> GetByRange(DateTime from, DateTime to)
    {
        var rows = GetSnapshot().Rows;
        var result = new List<ProjectedProgramEvent>();
        foreach (var row in rows)
        {
            if (row.End > from && row.Start < to)
                result.Add(row);
        }
        return result;
    }

    public ProjectedProgramEvent? GetByEventKey(ushort networkId, ushort transportStreamId, ushort serviceId, ushort eventId)
    {
        var key = (networkId, transportStreamId, serviceId, eventId);
        return GetSnapshot().ByEventKey.TryGetValue(key, out var row) ? row : null;
    }

    public IReadOnlyList<ProjectedProgramEvent> GetByEventKeys(
        IReadOnlyCollection<(ushort NetworkId, ushort TransportStreamId, ushort ServiceId, ushort EventId)> keys)
    {
        if (keys.Count == 0) return Array.Empty<ProjectedProgramEvent>();

        var snapshot = GetSnapshot();
        var result = new List<ProjectedProgramEvent>(keys.Count);
        foreach (var key in keys.Distinct())
        {
            if (snapshot.ByEventKey.TryGetValue(key, out var row))
                result.Add(row);
        }
        return result;
    }

    public ProjectedProgramEvent? GetByProjectedKey(ProjectedEventKey key)
    {
        if (!string.Equals(key.SourceKind, ProjectedEventSourceKinds.TvAirDb, StringComparison.OrdinalIgnoreCase)) return null;
        return GetByEventKey(key.NetworkId, key.TransportStreamId, key.ServiceId, key.EventId);
    }

    private ProjectionSnapshot GetSnapshot()
    {
        var revision = _epgStore.ProjectionRevision;
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null && snapshot.Revision == revision)
            return snapshot;

        lock (_snapshotGate)
        {
            while (true)
            {
                revision = _epgStore.ProjectionRevision;
                snapshot = _snapshot;
                if (snapshot is not null && snapshot.Revision == revision)
                    return snapshot;

                // A capture commit may race the DB read. Publish only when the EPG generation
                // remained unchanged across the complete raw-read + projection build.
                var raw = _epgStore.GetAllRaw();
                var rows = new ProjectedProgramEvent[raw.Count];
                var byEventKey = new Dictionary<(ushort NetworkId, ushort TransportStreamId, ushort ServiceId, ushort EventId), ProjectedProgramEvent>(raw.Count);
                for (var i = 0; i < raw.Count; i++)
                {
                    var projected = ToProjected(raw[i]);
                    rows[i] = projected;
                    byEventKey[(projected.NetworkId, projected.TransportStreamId, projected.ServiceId, projected.EventId)] = projected;
                }

                var revisionAfterBuild = _epgStore.ProjectionRevision;
                if (revisionAfterBuild != revision)
                    continue;

                snapshot = new ProjectionSnapshot(revision, rows, byEventKey);
                Volatile.Write(ref _snapshot, snapshot);
                return snapshot;
            }
        }
    }

    private static ProjectedProgramEvent ToProjected(EpgEvent e)
    {
        var cell = ProgramGuideCellTextDecoder.Decode(e);
        var title = FirstNonEmpty(cell.Title, e.Title);
        var shortText = FirstNonEmpty(cell.Outline, e.Description);
        var extendedText = FirstNonEmpty(cell.Detail, cell.Items, e.Description);
        var key = ProjectedEventKey.FromDb(e);
        return new ProjectedProgramEvent
        {
            Key = key,
            ProjectionState = ProjectedEventStates.DbOnly,
            NetworkId = e.NetworkId,
            TransportStreamId = e.TransportStreamId,
            ServiceId = e.ServiceId,
            EventId = e.EventId,
            Start = e.Start,
            End = e.End,
            DurationSeconds = e.DurationSeconds,
            ServiceName = e.ServiceName ?? string.Empty,
            Title = title,
            ShortText = shortText,
            ExtendedText = extendedText,
            ExtendedItems = cell.Items ?? string.Empty,
            UpdatedAt = e.UpdatedAt,
            CellText = string.Join("\n", new[] { title, shortText, extendedText }.Where(v => !string.IsNullOrWhiteSpace(v))),
            Genre = e.Genre ?? string.Empty,
            GenreCodes = e.GenreCodes ?? string.Empty,
            DbEventExists = true,
            DbEvent = e,
            SourceKind = ProjectedEventSourceKinds.TvAirDb,
            SourcePluginId = string.Empty,
            SourceEventKey = key.SourceEventKey,
            ProjectionTitleDbPresent = !string.IsNullOrWhiteSpace(title),
            ProjectionTitleOverlayCandidatePresent = false,
            ProjectionTitleSource = !string.IsNullOrWhiteSpace(title) ? "db" : "none",
            ProjectionOutlineDbPresent = !string.IsNullOrWhiteSpace(shortText),
            ProjectionOutlineOverlayCandidatePresent = false,
            ProjectionOutlineSource = !string.IsNullOrWhiteSpace(shortText) ? "db" : "none",
            ProjectionDetailDbPresent = !string.IsNullOrWhiteSpace(extendedText),
            ProjectionDetailOverlayCandidatePresent = false,
            ProjectionDetailSource = !string.IsNullOrWhiteSpace(extendedText) ? "db" : "none"
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length > 0) return text;
        }
        return string.Empty;
    }

    private sealed record ProjectionSnapshot(
        long Revision,
        ProjectedProgramEvent[] Rows,
        IReadOnlyDictionary<(ushort NetworkId, ushort TransportStreamId, ushort ServiceId, ushort EventId), ProjectedProgramEvent> ByEventKey);
}
