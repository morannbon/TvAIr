using Microsoft.Data.Sqlite;
using TvAIr.Core;

namespace TvAIr.Epg;

public sealed record EpgUpsertMergeStats(
    int Incoming,
    int ExistingRows,
    int IncomingRawShortBlankExistingRawShortPresent,
    int IncomingRawExtendedBlankExistingRawExtendedPresent,
    int IncomingRawContentBlankExistingRawContentPresent,
    int IncomingExtendedOnlyExistingRawShortPresent,
    int SameEventExtendedMergedPreservingExistingTitle,
    int TableMetadataPreservedForExistingTitle,
    int IncomingRawShortPresent,
    int IncomingRawExtendedPresent)
{
    public static readonly EpgUpsertMergeStats Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// EPG取得データだけを保持するキャッシュ。
/// 予約状態・自動検索結果・旧DB互換キーはここに持たない。
/// </summary>
public sealed class EpgStore
{
    private readonly Database db;
    public EpgUpsertMergeStats LastUpsertMergeStats { get; private set; } = EpgUpsertMergeStats.Empty;

    public EpgStore(Database db)
    {
        this.db = db;
    }

    // ─── 書き込み ────────────────────────────────────────────────

    /// <summary>イベント一覧を UPSERT する。</summary>
    public int Upsert(IEnumerable<EpgEvent> events)
    {
        using var con = db.Open();
        using var tx  = con.BeginTransaction();
        var now   = DateTime.Now.ToString("O");
        var count = 0;
        var existingRows = 0;
        var incomingRawShortBlankExistingRawShortPresent = 0;
        var incomingRawExtendedBlankExistingRawExtendedPresent = 0;
        var incomingRawContentBlankExistingRawContentPresent = 0;
        var incomingExtendedOnlyExistingRawShortPresent = 0;
        var sameEventExtendedMergedPreservingExistingTitle = 0;
        var tableMetadataPreservedForExistingTitle = 0;
        var incomingRawShortPresent = 0;
        var incomingRawExtendedPresent = 0;

        foreach (var rawEvent in events)
        {
            var ev = NormalizeEventForStorage(rawEvent);
            if (!string.IsNullOrWhiteSpace(ev.RawShortEventDescriptorHex)) incomingRawShortPresent++;
            if (!string.IsNullOrWhiteSpace(ev.RawExtendedEventDescriptorHex)) incomingRawExtendedPresent++;

            var existingRawShort = string.Empty;
            var existingRawExtended = string.Empty;
            var existingRawContent = string.Empty;
            using (var existingCmd = con.CreateCommand())
            {
                existingCmd.Transaction = tx;
                existingCmd.CommandText = """
                    SELECT raw_short_event_descriptor, raw_extended_event_descriptor, raw_content_descriptor
                    FROM epg_events
                    WHERE network_id = $nid AND transport_stream_id = $tsid AND service_id = $sid AND event_id = $eid
                    LIMIT 1;
                    """;
                existingCmd.Parameters.AddWithValue("$nid", ev.NetworkId);
                existingCmd.Parameters.AddWithValue("$tsid", ev.TransportStreamId);
                existingCmd.Parameters.AddWithValue("$sid", ev.ServiceId);
                existingCmd.Parameters.AddWithValue("$eid", ev.EventId);
                using var existingReader = existingCmd.ExecuteReader();
                if (existingReader.Read())
                {
                    existingRows++;
                    existingRawShort = existingReader.IsDBNull(0) ? string.Empty : existingReader.GetString(0);
                    existingRawExtended = existingReader.IsDBNull(1) ? string.Empty : existingReader.GetString(1);
                    existingRawContent = existingReader.IsDBNull(2) ? string.Empty : existingReader.GetString(2);
                }
            }

            var incomingRawShortBlank = string.IsNullOrWhiteSpace(ev.RawShortEventDescriptorHex);
            var incomingRawExtendedBlank = string.IsNullOrWhiteSpace(ev.RawExtendedEventDescriptorHex);
            var incomingRawContentBlank = string.IsNullOrWhiteSpace(ev.RawContentDescriptorHex);
            var existingRawShortPresent = !string.IsNullOrWhiteSpace(existingRawShort);
            var existingRawExtendedPresent = !string.IsNullOrWhiteSpace(existingRawExtended);
            var existingRawContentPresent = !string.IsNullOrWhiteSpace(existingRawContent);
            if (incomingRawShortBlank && existingRawShortPresent)
            {
                incomingRawShortBlankExistingRawShortPresent++;
                tableMetadataPreservedForExistingTitle++;
                if (!incomingRawExtendedBlank)
                {
                    incomingExtendedOnlyExistingRawShortPresent++;
                    sameEventExtendedMergedPreservingExistingTitle++;
                }
            }
            if (incomingRawExtendedBlank && existingRawExtendedPresent) incomingRawExtendedBlankExistingRawExtendedPresent++;
            if (incomingRawContentBlank && existingRawContentPresent) incomingRawContentBlankExistingRawContentPresent++;

            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO epg_events (
                    network_id, transport_stream_id, service_id, event_id,
                    service_name, title, description,
                    genre, genre_codes, table_id, section_number, version_number,
                    raw_descriptor_loop, raw_short_event_descriptor, raw_extended_event_descriptor, raw_content_descriptor,
                    duration_seconds, start_time, end_time, updated_at)
                VALUES (
                    $nid, $tsid, $sid, $eid,
                    $svc, $title, $desc,
                    $genre, $genreCodes, $tableId, $sectionNumber, $versionNumber,
                    $rawDescriptorLoop, $rawShortEventDescriptor, $rawExtendedEventDescriptor, $rawContentDescriptor,
                    $dur, $start, $end, $updAt)
                ON CONFLICT(network_id, transport_stream_id, service_id, event_id) DO UPDATE SET
                    service_name = excluded.service_name,
                    title = excluded.title,
                    description = excluded.description,
                    genre = excluded.genre,
                    genre_codes = excluded.genre_codes,
                    table_id = excluded.table_id,
                    section_number = excluded.section_number,
                    version_number = excluded.version_number,
                    raw_descriptor_loop = excluded.raw_descriptor_loop,
                    raw_short_event_descriptor = excluded.raw_short_event_descriptor,
                    raw_extended_event_descriptor = excluded.raw_extended_event_descriptor,
                    raw_content_descriptor = excluded.raw_content_descriptor,
                    duration_seconds = excluded.duration_seconds,
                    start_time = excluded.start_time,
                    end_time = excluded.end_time,
                    updated_at = excluded.updated_at;
                """;

            cmd.Parameters.AddWithValue("$nid",        ev.NetworkId);
            cmd.Parameters.AddWithValue("$tsid",       ev.TransportStreamId);
            cmd.Parameters.AddWithValue("$sid",        ev.ServiceId);
            cmd.Parameters.AddWithValue("$eid",        ev.EventId);
            cmd.Parameters.AddWithValue("$svc",        ev.ServiceName);
            cmd.Parameters.AddWithValue("$title",      ev.Title);
            cmd.Parameters.AddWithValue("$desc",       ev.Description);
            cmd.Parameters.AddWithValue("$genre",      ev.Genre);
            cmd.Parameters.AddWithValue("$genreCodes", ev.GenreCodes);
            cmd.Parameters.AddWithValue("$tableId", ev.TableId);
            cmd.Parameters.AddWithValue("$sectionNumber", ev.SectionNumber);
            cmd.Parameters.AddWithValue("$versionNumber", ev.VersionNumber);
            cmd.Parameters.AddWithValue("$rawDescriptorLoop", ev.RawDescriptorLoopHex);
            cmd.Parameters.AddWithValue("$rawShortEventDescriptor", ev.RawShortEventDescriptorHex);
            cmd.Parameters.AddWithValue("$rawExtendedEventDescriptor", ev.RawExtendedEventDescriptorHex);
            cmd.Parameters.AddWithValue("$rawContentDescriptor", ev.RawContentDescriptorHex);
            cmd.Parameters.AddWithValue("$dur",        ev.DurationSeconds);
            cmd.Parameters.AddWithValue("$start",      ev.Start.ToString("O"));
            cmd.Parameters.AddWithValue("$end",        ev.End.ToString("O"));
            cmd.Parameters.AddWithValue("$updAt",      now);
            cmd.ExecuteNonQuery();
            count++;
        }

        LastUpsertMergeStats = new EpgUpsertMergeStats(
            count,
            existingRows,
            incomingRawShortBlankExistingRawShortPresent,
            incomingRawExtendedBlankExistingRawExtendedPresent,
            incomingRawContentBlankExistingRawContentPresent,
            incomingExtendedOnlyExistingRawShortPresent,
            sameEventExtendedMergedPreservingExistingTitle,
            tableMetadataPreservedForExistingTitle,
            incomingRawShortPresent,
            incomingRawExtendedPresent);

        tx.Commit();
        return count;
    }

    private static EpgEvent NormalizeEventForStorage(EpgEvent source)
    {
        source.Title = string.Empty;
        source.Description = string.Empty;
        source.Genre = source.Genre ?? string.Empty;
        source.GenreCodes = source.GenreCodes ?? string.Empty;
        source.RawDescriptorLoopHex = source.RawDescriptorLoopHex ?? string.Empty;
        source.RawShortEventDescriptorHex = source.RawShortEventDescriptorHex ?? string.Empty;
        source.RawExtendedEventDescriptorHex = source.RawExtendedEventDescriptorHex ?? string.Empty;
        source.RawContentDescriptorHex = source.RawContentDescriptorHex ?? string.Empty;
        return source;
    }

    // ─── 読み取り ────────────────────────────────────────────────

    /// <summary>指定期間のイベント一覧を取得する。</summary>
    public IReadOnlyList<EpgEvent> GetByRange(DateTime from, DateTime to)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT network_id, transport_stream_id, service_id, event_id,
                   service_name, title, description,
                   genre, genre_codes, table_id, section_number, version_number,
                   raw_descriptor_loop, raw_short_event_descriptor, raw_extended_event_descriptor, raw_content_descriptor,
                   duration_seconds, start_time, end_time, updated_at
            FROM epg_events
            WHERE end_time > $from AND start_time < $to
            ORDER BY start_time;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString("O"));
        cmd.Parameters.AddWithValue("$to",   to.ToString("O"));
        return ReadEvents(cmd);
    }


    /// <summary>番組イベントDBを条件なしで読み出す。検証用のraw unsealed route。</summary>
    public IReadOnlyList<EpgEvent> GetAllRaw()
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT network_id, transport_stream_id, service_id, event_id,
                   service_name, title, description,
                   genre, genre_codes, table_id, section_number, version_number,
                   raw_descriptor_loop, raw_short_event_descriptor, raw_extended_event_descriptor, raw_content_descriptor,
                   duration_seconds, start_time, end_time, updated_at
            FROM epg_events
            ORDER BY start_time, network_id, transport_stream_id, service_id, event_id, table_id, section_number;
            """;
        return ReadEvents(cmd);
    }

    /// <summary>指定サービス・イベントIDのイベントを1件取得する。</summary>
    public EpgEvent? GetOne(ushort networkId, ushort tsId, ushort serviceId, ushort eventId)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT network_id, transport_stream_id, service_id, event_id,
                   service_name, title, description,
                   genre, genre_codes, table_id, section_number, version_number,
                   raw_descriptor_loop, raw_short_event_descriptor, raw_extended_event_descriptor, raw_content_descriptor,
                   duration_seconds, start_time, end_time, updated_at
            FROM epg_events
            WHERE network_id = $nid
              AND transport_stream_id = $tsid
              AND service_id = $sid
              AND event_id = $eid
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$nid",  networkId);
        cmd.Parameters.AddWithValue("$tsid", tsId);
        cmd.Parameters.AddWithValue("$sid",  serviceId);
        cmd.Parameters.AddWithValue("$eid",  eventId);
        return ReadEvents(cmd).FirstOrDefault();
    }

    /// <summary>キーワード・チャンネル・曜日・時間帯で番組を検索する。</summary>
    public IReadOnlyList<EpgEvent> Search(
        string? keyword,
        bool searchDescription,
        IEnumerable<ushort>? serviceIds,
        IEnumerable<int>? daysOfWeek,   // 0=日 〜 6=土
        int? timeFromHour,
        int? timeToHour,
        DateTime? dateFrom,
        DateTime? dateTo,
        int limit = 300)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();

        var where = new List<string>();
        var now = DateTime.Now.ToString("O");

        // 過去分を除外（終了時刻が現在以降）
        where.Add("end_time >= $now");
        cmd.Parameters.AddWithValue("$now", now);

        // 期間
        if (dateFrom.HasValue)
        {
            where.Add("start_time >= $dateFrom");
            cmd.Parameters.AddWithValue("$dateFrom", dateFrom.Value.ToString("O"));
        }
        if (dateTo.HasValue)
        {
            where.Add("start_time < $dateTo");
            cmd.Parameters.AddWithValue("$dateTo", dateTo.Value.ToString("O"));
        }

        // キーワードはDBの旧title/description列で絞らない。
        // raw descriptorから作る共通投影(EpgProjection)でReadEvents後に判定する。

        // サービスID
        var svcList = serviceIds?.ToList();
        if (svcList is { Count: > 0 })
        {
            var sidParamNames = svcList.Select((_, i) => $"$sid{i}").ToList();
            where.Add($"service_id IN ({string.Join(",", sidParamNames)})");
            for (var i = 0; i < svcList.Count; i++)
                cmd.Parameters.AddWithValue($"$sid{i}", (int)svcList[i]);
        }

        // raw descriptor 投影でタイトル検索するため、SQL段階で旧title列に依存しない。
        // キーワード指定時は先に十分な件数を読み、投影後に最終limitを掛ける。
        var prefilterLimit = string.IsNullOrWhiteSpace(keyword) ? Math.Max(1, limit) : Math.Max(limit, 10000);

        cmd.CommandText = $"""
            SELECT network_id, transport_stream_id, service_id, event_id,
                   service_name, title, description,
                   genre, genre_codes, table_id, section_number, version_number,
                   raw_descriptor_loop, raw_short_event_descriptor, raw_extended_event_descriptor, raw_content_descriptor,
                   duration_seconds, start_time, end_time, updated_at
            FROM epg_events
            WHERE {string.Join(" AND ", where)}
            ORDER BY start_time
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", prefilterLimit);

        var events = ReadEvents(cmd);

        // 曜日・時間帯・キーワードフィルターはメモリ上で適用（SQLiteに曜日関数がないため）。
        // キーワード本文は旧description列ではなく、raw descriptor共通デコード投影を見る。
        var dowSet = daysOfWeek?.ToHashSet();
        IEnumerable<EpgEvent> result = events;

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            result = searchDescription
                ? result.Where(e => ContainsProjectionText(EpgProjection.Title(e), kw)
                    || ContainsProjectionText(EpgProjection.ShortText(e), kw)
                    || ContainsProjectionText(EpgProjection.ExtendedText(e), kw))
                : result.Where(e => ContainsProjectionText(EpgProjection.Title(e), kw));
        }

        if (dowSet is { Count: > 0 })
            result = result.Where(e => dowSet.Contains((int)e.Start.DayOfWeek));

        if (timeFromHour.HasValue)
            result = result.Where(e => e.Start.Hour >= timeFromHour.Value);

        if (timeToHour.HasValue && timeToHour.Value < 24)
            result = result.Where(e => e.Start.Hour < timeToHour.Value);

        return result.Take(Math.Max(1, limit)).ToList();
    }
    public int DeleteExpired(DateTime before)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM epg_events WHERE end_time < $before;";
        cmd.Parameters.AddWithValue("$before", before.ToString("O"));
        return cmd.ExecuteNonQuery();
    }


    // ─── 内部ヘルパー ────────────────────────────────────────────

    private static bool ContainsProjectionText(string? source, string keyword)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(keyword)) return false;
        if (source.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        var normalizedSource = NormalizeSearchText(source);
        var normalizedKeyword = NormalizeSearchText(keyword);
        if (normalizedSource.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase)) return true;
        return CompactSearchText(normalizedSource).Contains(CompactSearchText(normalizedKeyword), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSearchText(string text)
        => (text ?? string.Empty).Normalize(System.Text.NormalizationForm.FormKC);

    private static string CompactSearchText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var chars = text.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }

    private static IReadOnlyList<EpgEvent> ReadEvents(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<EpgEvent>();
        while (reader.Read())
        {
            var rawTitle = reader.IsDBNull(5) ? "" : reader.GetString(5);
            var title = rawTitle ?? string.Empty;
            var rawExtendedEventDescriptorHex = reader.IsDBNull(14) ? "" : reader.GetString(14);

            list.Add(new EpgEvent
            {
                NetworkId           = (ushort)reader.GetInt32(0),
                TransportStreamId   = (ushort)reader.GetInt32(1),
                ServiceId           = (ushort)reader.GetInt32(2),
                EventId             = (ushort)reader.GetInt32(3),
                ServiceName         = reader.IsDBNull(4)  ? "" : reader.GetString(4),
                Title               = title,
                Description         = reader.IsDBNull(6)  ? "" : reader.GetString(6),
                Genre               = reader.IsDBNull(7)  ? "" : reader.GetString(7),
                GenreCodes          = reader.IsDBNull(8)  ? "" : reader.GetString(8),
                TableId             = reader.IsDBNull(9) ? (byte)0 : (byte)reader.GetInt32(9),
                SectionNumber       = reader.IsDBNull(10) ? (byte)0 : (byte)reader.GetInt32(10),
                VersionNumber       = reader.IsDBNull(11) ? (byte)0 : (byte)reader.GetInt32(11),
                RawDescriptorLoopHex = reader.IsDBNull(12) ? "" : reader.GetString(12),
                RawShortEventDescriptorHex = reader.IsDBNull(13) ? "" : reader.GetString(13),
                RawExtendedEventDescriptorHex = rawExtendedEventDescriptorHex,
                RawContentDescriptorHex = reader.IsDBNull(15) ? "" : reader.GetString(15),
                DurationSeconds     = reader.IsDBNull(16) ? 0  : reader.GetInt32(16),
                Start               = reader.IsDBNull(17) ? DateTime.MinValue : DateTime.Parse(reader.GetString(17)),
                End                 = reader.IsDBNull(18) ? DateTime.MinValue : DateTime.Parse(reader.GetString(18)),
                UpdatedAt           = reader.IsDBNull(19) ? DateTime.MinValue : DateTime.Parse(reader.GetString(19))
            });
        }
        return list;
    }
}

