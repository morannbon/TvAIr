namespace TvAIr.Epg.Projection;

/// <summary>
/// 外部EPGイベントの一時保持領域。TvAIr DB / epg_events には書き戻さない。
/// プラグインが未導入・未登録の場合は常に空集合を返す。
/// </summary>
public sealed record ExternalEpgSnapshotReplaceResult(
    bool Changed,
    int PreviousCount,
    int CurrentCount,
    int RejectedDuplicateCount,
    long OwnerRevision,
    long StoreRevision);

public sealed class ExternalEpgSourceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ExternalEpgEvent>> _eventsByPlugin = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _ownerRevisions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ExternalEpgEvent> _allSnapshot = Array.Empty<ExternalEpgEvent>();
    private long _storeRevision;

    public ExternalEpgSnapshotReplaceResult ReplaceSnapshot(string sourcePluginId, IEnumerable<ExternalEpgEvent> events)
    {
        if (string.IsNullOrWhiteSpace(sourcePluginId))
            return new ExternalEpgSnapshotReplaceResult(false, 0, 0, 0, 0, 0);

        var canonicalSourcePluginId = TvAIr.Plugin.PluginIdentity.Normalize(sourcePluginId);
        var normalizedInput = events
            .Where(e => e.Start < e.End)
            .Select(e => Normalize(canonicalSourcePluginId, e))
            .ToList();

        // SourceEventKey is the stable external identity used by ProjectedEventKey.
        // Keep the first occurrence deterministically and report the remaining rows as rejected.
        var normalized = new List<ExternalEpgEvent>(normalizedInput.Count);
        var seenSourceEventKeys = new HashSet<string>(StringComparer.Ordinal);
        var rejectedDuplicateCount = 0;
        foreach (var item in normalizedInput)
        {
            if (!seenSourceEventKeys.Add(item.SourceEventKey))
            {
                rejectedDuplicateCount++;
                continue;
            }
            normalized.Add(item);
        }

        lock (_gate)
        {
            _eventsByPlugin.TryGetValue(canonicalSourcePluginId, out var previous);
            previous ??= new List<ExternalEpgEvent>();
            var changed = !SnapshotEquals(previous, normalized);

            var ownerRevision = _ownerRevisions.TryGetValue(canonicalSourcePluginId, out var currentOwnerRevision)
                ? currentOwnerRevision
                : 0;
            if (changed)
            {
                if (normalized.Count == 0)
                    _eventsByPlugin.Remove(canonicalSourcePluginId);
                else
                    _eventsByPlugin[canonicalSourcePluginId] = normalized;
                ownerRevision = checked(ownerRevision + 1);
                _ownerRevisions[canonicalSourcePluginId] = ownerRevision;
                _storeRevision = checked(_storeRevision + 1);
                _allSnapshot = Array.AsReadOnly(_eventsByPlugin.Values.SelectMany(v => v).ToArray());
            }

            return new ExternalEpgSnapshotReplaceResult(
                changed, previous.Count, normalized.Count, rejectedDuplicateCount, ownerRevision, _storeRevision);
        }
    }

    public ExternalEpgSnapshotReplaceResult Clear(string sourcePluginId)
        => ReplaceSnapshot(sourcePluginId, Array.Empty<ExternalEpgEvent>());

    public IReadOnlyList<ExternalEpgEvent> GetBySourcePlugin(string sourcePluginId)
    {
        if (string.IsNullOrWhiteSpace(sourcePluginId)) return Array.Empty<ExternalEpgEvent>();
        var canonicalSourcePluginId = TvAIr.Plugin.PluginIdentity.Normalize(sourcePluginId);
        lock (_gate)
        {
            return _eventsByPlugin.TryGetValue(canonicalSourcePluginId, out var events)
                ? events.ToArray()
                : Array.Empty<ExternalEpgEvent>();
        }
    }

    public IReadOnlyList<ExternalEpgEvent> GetAll()
    {
        lock (_gate)
        {
            return _allSnapshot;
        }
    }

    internal (long Revision, IReadOnlyList<ExternalEpgEvent> Rows) CaptureProjectionSnapshot()
    {
        lock (_gate)
        {
            return (_storeRevision, _allSnapshot);
        }
    }

    public IReadOnlyList<ExternalEpgEvent> GetByRange(DateTime from, DateTime to)
    {
        lock (_gate)
        {
            return _eventsByPlugin.Values
                .SelectMany(v => v)
                .Where(e => e.End > from && e.Start < to)
                .ToList();
        }
    }

    public ExternalEpgEvent? GetByProjectedKey(ProjectedEventKey key)
    {
        if (!string.Equals(key.SourceKind, ProjectedEventSourceKinds.ExternalEpg, StringComparison.OrdinalIgnoreCase)) return null;

        lock (_gate)
        {
            return _eventsByPlugin.Values
                .SelectMany(v => v)
                .FirstOrDefault(e => string.Equals(e.SourcePluginId, key.SourcePluginId, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(e.SourceEventKey, key.SourceEventKey, StringComparison.Ordinal));
        }
    }

    private static bool SnapshotEquals(IReadOnlyList<ExternalEpgEvent> left, IReadOnlyList<ExternalEpgEvent> right)
    {
        if (left.Count != right.Count) return false;
        var a = left.Select(BuildComparisonKey).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var b = right.Select(BuildComparisonKey).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static string BuildComparisonKey(ExternalEpgEvent e)
        => string.Join("\u001f",
            e.SourcePluginId, e.SourceKind, e.SourceEventKey,
            e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId,
            e.Start.Ticks, e.End.Ticks, e.ServiceName, e.Title, e.ShortText,
            e.ExtendedText, e.ExtendedItems, e.Genre, e.GenreCodes);

    private static ExternalEpgEvent Normalize(string sourcePluginId, ExternalEpgEvent e)
        => new()
        {
            SourcePluginId = sourcePluginId,
            SourceKind = string.IsNullOrWhiteSpace(e.SourceKind) ? "TVTestEpgData" : e.SourceKind.Trim(),
            SourceEventKey = string.IsNullOrWhiteSpace(e.SourceEventKey)
                ? BuildFallbackSourceEventKey(sourcePluginId, e)
                : e.SourceEventKey.Trim(),
            NetworkId = e.NetworkId,
            TransportStreamId = e.TransportStreamId,
            ServiceId = e.ServiceId,
            EventId = e.EventId,
            Start = e.Start,
            End = e.End,
            ServiceName = e.ServiceName ?? string.Empty,
            Title = e.Title ?? string.Empty,
            ShortText = e.ShortText ?? string.Empty,
            ExtendedText = e.ExtendedText ?? string.Empty,
            ExtendedItems = e.ExtendedItems ?? string.Empty,
            Genre = e.Genre ?? string.Empty,
            GenreCodes = e.GenreCodes ?? string.Empty,
            UpdatedAt = e.UpdatedAt == default ? DateTime.UtcNow : e.UpdatedAt
        };

    private static string BuildFallbackSourceEventKey(string sourcePluginId, ExternalEpgEvent e)
        => $"external:{sourcePluginId}:{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}:{e.EventId}:{e.Start:O}:{e.End:O}";
}
