using System.Text.Json;

namespace TvAIr.Epg;

/// <summary>
/// 放送波TDT/TOTで観測した「Windows時刻との差」を保持する単一正本。
/// Windows時計・予約時刻・録画時刻・Wake時刻は変更しない。
///
/// PreRecは放送局が実際に使っている番組時刻へ合わせて判断する必要があるため、
/// TDT/TOTを観測するたびに、その観測時点のbroadcastTimeとsystemObservedAtから最新offsetを再計算して更新する。
/// 固定補正値を永久に使うのではなく、「毎回の有効な放送波観測で更新し、その最新値を次の観測まで維持する」が正本契約である。
///
/// 放送波とWindows時計にずれが観測されたなら、その時点ではWindows時計側に基礎的なずれがあると扱う。
/// 一般ユーザーが短時間ごとに外部の時刻補正を繰り返すことを前提にはしないため、経過時間、ageの大小、
/// systemNowと観測時刻の前後関係だけを理由に最新offsetを失効させてWindows時計へ戻してはならない。
/// それを行うとPreRecだけが途中で時計基準を切り替え、同じ予約判定が時間帯や再起動を境に異なる基準で動くためである。
///
/// ユーザーが途中でWindows時刻を手動/外部ソフトで補正した場合、直前観測から次回観測まで保持中offsetに一時的な誤差が
/// 出ることは許容する。次のTDT/TOT観測時には必ずその時点の差へ更新される。ageは診断値であり失効条件ではない。
///
/// 「次の観測まで維持」はTvAIrプロセス寿命にも依存させない。TvAIr再起動だけで最新offsetを失うと、次回観測までPreRecが
/// raw Windows時刻へ退化するため、最後に確定した有効なreferenceをruntimeへ永続化し、次回起動時に復元する。
/// この永続値も新しい有効なTDT/TOT観測が来れば必ず置き換える。
/// </summary>
public sealed class BroadcastTimeReference
{
    private readonly object gate = new();
    private readonly string persistencePath;
    private BroadcastTimeReferenceSnapshot? latest;

    private static readonly TimeSpan MaxAcceptedAbsoluteOffset = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public BroadcastTimeReference()
    {
        persistencePath = Path.Combine(AppContext.BaseDirectory, "runtime", "broadcast-time-reference.json");
        latest = TryLoadPersistedSnapshot();
    }

    public bool TryUpdate(
        DateTime broadcastTime,
        DateTime systemObservedAt,
        long offsetMilliseconds,
        int sourceTableId,
        int observationCount,
        out string reason)
    {
        var offset = TimeSpan.FromMilliseconds(offsetMilliseconds);
        if (broadcastTime == DateTime.MinValue || systemObservedAt == DateTime.MinValue)
        {
            reason = "invalid_timestamp";
            return false;
        }
        if (Math.Abs(offset.TotalMilliseconds) > MaxAcceptedAbsoluteOffset.TotalMilliseconds)
        {
            reason = "offset_out_of_range";
            return false;
        }
        if (sourceTableId is not (0x70 or 0x73))
        {
            reason = "unsupported_table";
            return false;
        }

        BroadcastTimeReferenceSnapshot next;
        lock (gate)
        {
            // Every newer valid TDT/TOT observation supersedes the previous correction.
            // We hold only the latest measured offset between observations; it is not a fixed calibration value.
            if (latest is not null && systemObservedAt < latest.SystemObservedAt)
            {
                reason = "older_than_current";
                return false;
            }

            next = new BroadcastTimeReferenceSnapshot(
                broadcastTime,
                systemObservedAt,
                offset,
                sourceTableId,
                Math.Max(1, observationCount));
            latest = next;
        }

        PersistSnapshotBestEffort(next);
        reason = "updated";
        return true;
    }

    public DateTime GetEffectivePreRecordNow(DateTime systemNow, out BroadcastTimeReferenceUse use)
    {
        BroadcastTimeReferenceSnapshot? snapshot;
        lock (gate) snapshot = latest;

        if (snapshot is null)
        {
            use = BroadcastTimeReferenceUse.Fallback("no_observation");
            return systemNow;
        }

        var age = systemNow - snapshot.SystemObservedAt;
        // Keep applying the last confirmed broadcast-vs-Windows offset until another valid TDT/TOT
        // observation recalculates and replaces it. age may be large or even negative after a user or
        // time-sync tool adjusts Windows time; neither condition invalidates the reference.
        use = BroadcastTimeReferenceUse.FromApplied(snapshot, age);
        return systemNow + snapshot.Offset;
    }

    public BroadcastTimeReferenceSnapshot? Snapshot()
    {
        lock (gate) return latest;
    }

    private BroadcastTimeReferenceSnapshot? TryLoadPersistedSnapshot()
    {
        try
        {
            if (!File.Exists(persistencePath)) return null;
            var persisted = JsonSerializer.Deserialize<PersistedBroadcastTimeReference>(File.ReadAllText(persistencePath), JsonOptions);
            if (persisted is null) return null;
            if (persisted.BroadcastTime == DateTime.MinValue || persisted.SystemObservedAt == DateTime.MinValue) return null;
            if (persisted.SourceTableId is not (0x70 or 0x73)) return null;
            if (Math.Abs(persisted.OffsetMilliseconds) > MaxAcceptedAbsoluteOffset.TotalMilliseconds) return null;

            return new BroadcastTimeReferenceSnapshot(
                persisted.BroadcastTime,
                persisted.SystemObservedAt,
                TimeSpan.FromMilliseconds(persisted.OffsetMilliseconds),
                persisted.SourceTableId,
                Math.Max(1, persisted.ObservationCount));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private void PersistSnapshotBestEffort(BroadcastTimeReferenceSnapshot snapshot)
    {
        try
        {
            var directory = Path.GetDirectoryName(persistencePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var persisted = new PersistedBroadcastTimeReference(
                snapshot.BroadcastTime,
                snapshot.SystemObservedAt,
                (long)Math.Round(snapshot.Offset.TotalMilliseconds),
                snapshot.SourceTableId,
                snapshot.ObservationCount);

            var tempPath = persistencePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(persisted, JsonOptions));
            File.Move(tempPath, persistencePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PersistedBroadcastTimeReference(
        DateTime BroadcastTime,
        DateTime SystemObservedAt,
        long OffsetMilliseconds,
        int SourceTableId,
        int ObservationCount);
}

public sealed record BroadcastTimeReferenceSnapshot(
    DateTime BroadcastTime,
    DateTime SystemObservedAt,
    TimeSpan Offset,
    int SourceTableId,
    int ObservationCount);

public sealed record BroadcastTimeReferenceUse(
    bool Applied,
    string Reason,
    TimeSpan Offset,
    TimeSpan Age,
    int SourceTableId,
    int ObservationCount)
{
    public static BroadcastTimeReferenceUse Fallback(string reason, BroadcastTimeReferenceSnapshot? s = null)
        => new(false, reason, s?.Offset ?? TimeSpan.Zero, TimeSpan.Zero, s?.SourceTableId ?? -1, s?.ObservationCount ?? 0);

    public static BroadcastTimeReferenceUse FromApplied(BroadcastTimeReferenceSnapshot s, TimeSpan age)
        => new(true, "broadcast_reference", s.Offset, age, s.SourceTableId, s.ObservationCount);
}
