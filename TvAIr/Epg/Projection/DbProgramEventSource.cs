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

    public DbProgramEventSource(EpgStore epgStore) => _epgStore = epgStore;

    internal long ProjectionRevision => _epgStore.ProjectionRevision;

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
            rows[i] = ToProjected(storedEvents[i], retainDbEvent: true);
        return rows;
    }

    public IReadOnlyList<ProjectedProgramEvent> GetAll()
        => BuildProjectionSnapshot().Rows;

    // The merged ProgramGuide snapshot is the long-lived authority.  Do not also retain a second
    // full ProjectedProgramEvent graph here: the DB rows can be rebuilt only when the DB revision
    // actually changes, while direct lookup/range routes read just the required raw rows.
    internal (long Revision, IReadOnlyList<ProjectedProgramEvent> Rows) BuildProjectionSnapshot()
    {
        while (true)
        {
            var revision = _epgStore.ProjectionRevision;
            var raw = _epgStore.GetAllRaw();
            var rows = new ProjectedProgramEvent[raw.Count];
            for (var i = 0; i < raw.Count; i++)
                rows[i] = ToProjected(raw[i], retainDbEvent: false);

            var revisionAfterBuild = _epgStore.ProjectionRevision;
            if (revisionAfterBuild == revision)
                return (revision, rows);
        }
    }

    public IReadOnlyList<ProjectedProgramEvent> GetByRange(DateTime from, DateTime to)
    {
        var raw = _epgStore.GetByRange(from, to);
        if (raw.Count == 0) return Array.Empty<ProjectedProgramEvent>();

        var rows = new ProjectedProgramEvent[raw.Count];
        for (var i = 0; i < raw.Count; i++)
            rows[i] = ToProjected(raw[i], retainDbEvent: true);
        return rows;
    }

    public ProjectedProgramEvent? GetByEventKey(ushort networkId, ushort transportStreamId, ushort serviceId, ushort eventId)
    {
        var raw = _epgStore.GetOne(networkId, transportStreamId, serviceId, eventId);
        return raw is null ? null : ToProjected(raw, retainDbEvent: true);
    }

    public IReadOnlyList<ProjectedProgramEvent> GetByEventKeys(
        IReadOnlyCollection<(ushort NetworkId, ushort TransportStreamId, ushort ServiceId, ushort EventId)> keys)
    {
        if (keys.Count == 0) return Array.Empty<ProjectedProgramEvent>();

        var raw = _epgStore.GetByEventKeys(keys);
        if (raw.Count == 0) return Array.Empty<ProjectedProgramEvent>();

        var rows = new ProjectedProgramEvent[raw.Count];
        for (var i = 0; i < raw.Count; i++)
            rows[i] = ToProjected(raw[i], retainDbEvent: true);
        return rows;
    }

    public ProjectedProgramEvent? GetByProjectedKey(ProjectedEventKey key)
    {
        if (!string.Equals(key.SourceKind, ProjectedEventSourceKinds.TvAirDb, StringComparison.OrdinalIgnoreCase)) return null;
        return GetByEventKey(key.NetworkId, key.TransportStreamId, key.ServiceId, key.EventId);
    }

    private static ProjectedProgramEvent ToProjected(EpgEvent e, bool retainDbEvent)
    {
        var cell = ProgramGuideCellTextDecoder.Decode(e);
        // AUTO_SEARCH_FIELD_SOURCE_CONTRACT:
        // Searchable DB projection fields preserve the descriptor semantics exactly.
        // Display fallbacks must never bleed Items/other description columns into Outline/Detail,
        // otherwise a checkbox can search data belonging to another checkbox.
        var title = FirstNonEmpty(cell.Title, e.Title);
        var shortText = FirstNonEmpty(cell.Outline);
        var extendedText = FirstNonEmpty(cell.Detail);
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
            CellText = ComposeCellText(title, shortText, extendedText),
            Genre = e.Genre ?? string.Empty,
            GenreCodes = e.GenreCodes ?? string.Empty,
            DbEventExists = true,
            DbEvent = retainDbEvent ? e : null,
            DbTableId = e.TableId,
            DbSectionNumber = e.SectionNumber,
            DbVersionNumber = e.VersionNumber,
            DbUpdatedAt = e.UpdatedAt,
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

    private static string ComposeCellText(string title, string outline, string detail)
    {
        var count = (title.Length > 0 ? 1 : 0) + (outline.Length > 0 ? 1 : 0) + (detail.Length > 0 ? 1 : 0);
        if (count == 0) return string.Empty;
        if (count == 1) return title.Length > 0 ? title : outline.Length > 0 ? outline : detail;

        var length = title.Length + outline.Length + detail.Length + count - 1;
        return string.Create(length, (title, outline, detail), static (dst, state) =>
        {
            var pos = 0;
            if (state.title.Length > 0)
            {
                state.title.AsSpan().CopyTo(dst[pos..]);
                pos += state.title.Length;
            }
            if (state.outline.Length > 0)
            {
                if (pos > 0) dst[pos++] = '\n';
                state.outline.AsSpan().CopyTo(dst[pos..]);
                pos += state.outline.Length;
            }
            if (state.detail.Length > 0)
            {
                if (pos > 0) dst[pos++] = '\n';
                state.detail.AsSpan().CopyTo(dst[pos..]);
            }
        });
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

}
