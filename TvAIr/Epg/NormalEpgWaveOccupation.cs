namespace TvAIr.Epg;

/// <summary>
/// 通常EPG実行中の放送波単位の論理占有を保持する。
/// 物理TunerのLease/Owner正本ではなく、EPG枠実行中は同波の録画用Tuner群を
/// 予約競合上まとめて利用不可とするための時間軸状態だけを管理する。
/// </summary>
public sealed class NormalEpgWaveOccupation
{
    private readonly object gate = new();
    private readonly Dictionary<string, NormalEpgWaveOccupationSnapshot> active = new(StringComparer.OrdinalIgnoreCase);
    private long revision;

    public NormalEpgWaveOccupationSnapshot Set(
        string group,
        DateTime startedAt,
        DateTime plannedEndAt,
        string source,
        bool silent,
        long runGeneration)
    {
        var normalizedGroup = NormalizeGroup(group);
        lock (gate)
        {
            var snapshot = new NormalEpgWaveOccupationSnapshot(
                normalizedGroup,
                startedAt,
                plannedEndAt > startedAt ? plannedEndAt : startedAt,
                string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Trim(),
                silent,
                runGeneration,
                ++revision,
                false,
                Array.Empty<string>());
            active[normalizedGroup] = snapshot;
            return snapshot;
        }
    }


    public bool TryTransitionToPhysicalTail(
        string group,
        long runGeneration,
        IReadOnlyList<string> physicalTuners,
        out NormalEpgWaveOccupationSnapshot snapshot)
    {
        var normalizedGroup = NormalizeGroup(group);
        var normalizedTuners = (physicalTuners ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (gate)
        {
            if (!active.TryGetValue(normalizedGroup, out var current)
                || current.RunGeneration != runGeneration)
            {
                snapshot = default!;
                return false;
            }

            if (current.PhysicalTailOnly
                && current.PhysicalTailTuners.SequenceEqual(normalizedTuners, StringComparer.OrdinalIgnoreCase))
            {
                snapshot = current;
                return false;
            }

            snapshot = current with
            {
                Revision = ++revision,
                PhysicalTailOnly = true,
                PhysicalTailTuners = normalizedTuners
            };
            active[normalizedGroup] = snapshot;
            return true;
        }
    }

    public bool Clear(string group, long runGeneration)
    {
        var normalizedGroup = NormalizeGroup(group);
        lock (gate)
        {
            if (!active.TryGetValue(normalizedGroup, out var current))
                return false;
            if (current.RunGeneration != runGeneration)
                return false;
            return active.Remove(normalizedGroup);
        }
    }

    public int ClearRun(long runGeneration)
    {
        lock (gate)
        {
            var keys = active
                .Where(x => x.Value.RunGeneration == runGeneration)
                .Select(x => x.Key)
                .ToArray();
            foreach (var key in keys)
                active.Remove(key);
            return keys.Length;
        }
    }

    public bool TryGet(string group, out NormalEpgWaveOccupationSnapshot snapshot)
    {
        var normalizedGroup = NormalizeGroup(group);
        lock (gate)
        {
            if (active.TryGetValue(normalizedGroup, out var found))
            {
                snapshot = found;
                return true;
            }
            snapshot = default!;
            return false;
        }
    }

    public IReadOnlyList<NormalEpgWaveOccupationSnapshot> Snapshot()
    {
        lock (gate)
            return active.Values.OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeGroup(string group)
        => string.Equals(group, "BSCS", StringComparison.OrdinalIgnoreCase) ? "BSCS" : "GR";
}

public sealed record NormalEpgWaveOccupationSnapshot(
    string Group,
    DateTime StartedAt,
    DateTime PlannedEndAt,
    string Source,
    bool Silent,
    long RunGeneration,
    long Revision,
    bool PhysicalTailOnly,
    IReadOnlyList<string> PhysicalTailTuners);
