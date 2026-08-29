using Microsoft.Data.Sqlite;
using TvAIr.Core;

namespace TvAIr.Epg;

public sealed record EpgUpsertStorageStats(
    int Incoming,
    int IncomingRawShortPresent,
    int IncomingRawExtendedPresent,
    int IncomingRawContentPresent);

public sealed record EpgUpsertResult(
    int Count,
    EpgUpsertStorageStats Stats);

public sealed record EpgCaptureCommitResult(
    EpgUpsertResult Upsert,
    EpgStaleRetireStats StaleRetire);

public sealed record EpgStaleRetireStats(
    int Services,
    int IncomingEvents,
    int DeletedRows,
    DateTime? ScopeStart,
    DateTime? ScopeEnd)
{
    public static readonly EpgStaleRetireStats Empty = new(0, 0, 0, null, null);
}

/// <summary>
/// EPG取得データだけを保持するキャッシュ。
/// 予約状態・自動検索結果・旧DB互換キーはここに持たない。
/// </summary>
public sealed class EpgStore
{
    private readonly Database db;
    private long projectionRevision;

    /// <summary>
    /// DB由来番組投影の世代。epg_events の確定変更後だけ進み、
    /// DbProgramEventSource が同一DB正本の基底投影を安全に再利用するために使う。
    /// </summary>
    public long ProjectionRevision => Interlocked.Read(ref projectionRevision);

    public EpgStore(Database db)
    {
        this.db = db;
    }

    // ─── 書き込み ────────────────────────────────────────────────

    /// <summary>イベント一覧を UPSERT する。</summary>
    public EpgUpsertResult Upsert(IEnumerable<EpgEvent> events)
    {
        using var con = db.Open();
        using var tx = con.BeginTransaction();
        var result = UpsertCore(con, tx, events);
        tx.Commit();
        if (result.Count > 0)
            Interlocked.Increment(ref projectionRevision);
        return result;
    }

    /// <summary>
    /// 1回のEPG取得で得たイベント更新と、完全取得時のstale退場を同一トランザクションで確定する。
    /// stale退場を許可しない取得では、retireEventsにnullを渡して更新だけをcommitする。
    /// </summary>
    public EpgCaptureCommitResult CommitCapture(
        IEnumerable<EpgEvent> upsertEvents,
        IEnumerable<EpgEvent>? retireEvents)
    {
        using var con = db.Open();
        using var tx = con.BeginTransaction();
        var upsert = UpsertCore(con, tx, upsertEvents);
        var staleRetire = retireEvents is null
            ? EpgStaleRetireStats.Empty
            : RetireStaleEventsForCapturedScopeCore(con, tx, retireEvents);
        tx.Commit();
        if (upsert.Count > 0 || staleRetire.DeletedRows > 0)
            Interlocked.Increment(ref projectionRevision);
        return new EpgCaptureCommitResult(upsert, staleRetire);
    }

    private static EpgUpsertResult UpsertCore(
        SqliteConnection con,
        SqliteTransaction tx,
        IEnumerable<EpgEvent> events)
    {
        var now = DateTime.Now.ToString("O");
        var count = 0;
        var incomingRawShortPresent = 0;
        var incomingRawExtendedPresent = 0;
        var incomingRawContentPresent = 0;

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

        var pNid = cmd.Parameters.Add("$nid", SqliteType.Integer);
        var pTsid = cmd.Parameters.Add("$tsid", SqliteType.Integer);
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pEid = cmd.Parameters.Add("$eid", SqliteType.Integer);
        var pSvc = cmd.Parameters.Add("$svc", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pDesc = cmd.Parameters.Add("$desc", SqliteType.Text);
        var pGenre = cmd.Parameters.Add("$genre", SqliteType.Text);
        var pGenreCodes = cmd.Parameters.Add("$genreCodes", SqliteType.Text);
        var pTableId = cmd.Parameters.Add("$tableId", SqliteType.Integer);
        var pSectionNumber = cmd.Parameters.Add("$sectionNumber", SqliteType.Integer);
        var pVersionNumber = cmd.Parameters.Add("$versionNumber", SqliteType.Integer);
        var pRawDescriptorLoop = cmd.Parameters.Add("$rawDescriptorLoop", SqliteType.Text);
        var pRawShort = cmd.Parameters.Add("$rawShortEventDescriptor", SqliteType.Text);
        var pRawExtended = cmd.Parameters.Add("$rawExtendedEventDescriptor", SqliteType.Text);
        var pRawContent = cmd.Parameters.Add("$rawContentDescriptor", SqliteType.Text);
        var pDuration = cmd.Parameters.Add("$dur", SqliteType.Integer);
        var pStart = cmd.Parameters.Add("$start", SqliteType.Text);
        var pEnd = cmd.Parameters.Add("$end", SqliteType.Text);
        var pUpdatedAt = cmd.Parameters.Add("$updAt", SqliteType.Text);

        foreach (var rawEvent in events)
        {
            var ev = NormalizeEventForStorage(rawEvent);
            if (!string.IsNullOrWhiteSpace(ev.RawShortEventDescriptorHex)) incomingRawShortPresent++;
            if (!string.IsNullOrWhiteSpace(ev.RawExtendedEventDescriptorHex)) incomingRawExtendedPresent++;
            if (!string.IsNullOrWhiteSpace(ev.RawContentDescriptorHex)) incomingRawContentPresent++;

            pNid.Value = (int)ev.NetworkId;
            pTsid.Value = (int)ev.TransportStreamId;
            pSid.Value = (int)ev.ServiceId;
            pEid.Value = (int)ev.EventId;
            pSvc.Value = ev.ServiceName;
            pTitle.Value = ev.Title;
            pDesc.Value = ev.Description;
            pGenre.Value = ev.Genre;
            pGenreCodes.Value = ev.GenreCodes;
            pTableId.Value = (int)ev.TableId;
            pSectionNumber.Value = (int)ev.SectionNumber;
            pVersionNumber.Value = (int)ev.VersionNumber;
            pRawDescriptorLoop.Value = ev.RawDescriptorLoopHex;
            pRawShort.Value = ev.RawShortEventDescriptorHex;
            pRawExtended.Value = ev.RawExtendedEventDescriptorHex;
            pRawContent.Value = ev.RawContentDescriptorHex;
            pDuration.Value = ev.DurationSeconds;
            pStart.Value = ev.Start.ToString("O");
            pEnd.Value = ev.End.ToString("O");
            pUpdatedAt.Value = now;
            cmd.ExecuteNonQuery();
            count++;
        }

        var stats = new EpgUpsertStorageStats(
            count,
            incomingRawShortPresent,
            incomingRawExtendedPresent,
            incomingRawContentPresent);
        return new EpgUpsertResult(count, stats);
    }

    private static EpgStaleRetireStats RetireStaleEventsForCapturedScopeCore(
        SqliteConnection con,
        SqliteTransaction tx,
        IEnumerable<EpgEvent> capturedEvents)
    {
        // Stale retirement is an identity/time-range operation only. Do not normalize or
        // mutate the caller-owned event instances here; the same capture objects are still
        // consumed by post-import logging and notification paths after this method returns.
        var normalized = capturedEvents
            .Where(e => e.Start != DateTime.MinValue && e.End != DateTime.MinValue && e.End > e.Start)
            .ToList();
        if (normalized.Count == 0) return EpgStaleRetireStats.Empty;

        var groups = normalized
            .GroupBy(e => new { e.NetworkId, e.TransportStreamId, e.ServiceId })
            .ToList();

        var deleted = 0;
        DateTime? scopeStart = null;
        DateTime? scopeEnd = null;

        foreach (var g in groups)
        {
            var ids = g.Select(e => e.EventId).Distinct().OrderBy(x => x).ToList();
            if (ids.Count == 0) continue;

            var start = g.Min(e => e.Start);
            var end = g.Max(e => e.End);
            if (end <= start) continue;

            scopeStart = !scopeStart.HasValue || start < scopeStart.Value ? start : scopeStart;
            scopeEnd = !scopeEnd.HasValue || end > scopeEnd.Value ? end : scopeEnd;

            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            var idParams = new List<string>();
            for (var i = 0; i < ids.Count; i++)
            {
                var name = $"$eid{i}";
                idParams.Add(name);
                cmd.Parameters.AddWithValue(name, (int)ids[i]);
            }

            cmd.CommandText = $"""
                DELETE FROM epg_events
                WHERE network_id = $nid
                  AND transport_stream_id = $tsid
                  AND service_id = $sid
                  AND end_time > $scopeStart
                  AND start_time < $scopeEnd
                  AND event_id NOT IN ({string.Join(",", idParams)});
                """;
            cmd.Parameters.AddWithValue("$nid", (int)g.Key.NetworkId);
            cmd.Parameters.AddWithValue("$tsid", (int)g.Key.TransportStreamId);
            cmd.Parameters.AddWithValue("$sid", (int)g.Key.ServiceId);
            cmd.Parameters.AddWithValue("$scopeStart", start.ToString("O"));
            cmd.Parameters.AddWithValue("$scopeEnd", end.ToString("O"));
            deleted += cmd.ExecuteNonQuery();
        }

        return new EpgStaleRetireStats(groups.Count, normalized.Count, deleted, scopeStart, scopeEnd);
    }

    private static EpgEvent NormalizeEventForStorage(EpgEvent source)
    {
        // Storage normalization must never mutate the capture-owned event instance.
        // The same raw event collection is consumed after Upsert by stale-authority
        // selection, diagnostics and post-import notification paths.
        return new EpgEvent
        {
            NetworkId = source.NetworkId,
            TransportStreamId = source.TransportStreamId,
            ServiceId = source.ServiceId,
            EventId = source.EventId,
            ServiceName = source.ServiceName ?? string.Empty,
            Title = string.Empty,
            Description = string.Empty,
            Genre = source.Genre ?? string.Empty,
            GenreCodes = source.GenreCodes ?? string.Empty,
            TableId = source.TableId,
            SectionNumber = source.SectionNumber,
            VersionNumber = source.VersionNumber,
            RawDescriptorLoopHex = source.RawDescriptorLoopHex ?? string.Empty,
            RawShortEventDescriptorHex = source.RawShortEventDescriptorHex ?? string.Empty,
            RawExtendedEventDescriptorHex = source.RawExtendedEventDescriptorHex ?? string.Empty,
            RawContentDescriptorHex = source.RawContentDescriptorHex ?? string.Empty,
            DurationSeconds = source.DurationSeconds,
            Start = source.Start,
            End = source.End,
            UpdatedAt = source.UpdatedAt
        };
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

    /// <summary>指定された放送イベントキーをまとめて取得する。SQLiteのパラメータ上限を避けるため分割実行する。</summary>
    public IReadOnlyList<EpgEvent> GetByEventKeys(
        IReadOnlyCollection<(ushort NetworkId, ushort TransportStreamId, ushort ServiceId, ushort EventId)> keys)
    {
        if (keys.Count == 0) return Array.Empty<EpgEvent>();

        const int keysPerQuery = 200; // 4 parameters/key; safely below SQLite's common 999 parameter limit.
        var uniqueKeys = keys.Distinct().ToArray();
        var result = new List<EpgEvent>(uniqueKeys.Length);

        for (var offset = 0; offset < uniqueKeys.Length; offset += keysPerQuery)
        {
            var batch = uniqueKeys.Skip(offset).Take(keysPerQuery).ToArray();
            using var con = db.Open();
            using var cmd = con.CreateCommand();

            var predicates = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                var key = batch[i];
                predicates[i] = $"(network_id = $nid{i} AND transport_stream_id = $tsid{i} AND service_id = $sid{i} AND event_id = $eid{i})";
                cmd.Parameters.AddWithValue($"$nid{i}", key.NetworkId);
                cmd.Parameters.AddWithValue($"$tsid{i}", key.TransportStreamId);
                cmd.Parameters.AddWithValue($"$sid{i}", key.ServiceId);
                cmd.Parameters.AddWithValue($"$eid{i}", key.EventId);
            }

            cmd.CommandText = $"""
                SELECT network_id, transport_stream_id, service_id, event_id,
                       service_name, title, description,
                       genre, genre_codes, table_id, section_number, version_number,
                       raw_descriptor_loop, raw_short_event_descriptor, raw_extended_event_descriptor, raw_content_descriptor,
                       duration_seconds, start_time, end_time, updated_at
                FROM epg_events
                WHERE {string.Join(" OR ", predicates)}
                ORDER BY start_time;
                """;

            result.AddRange(ReadEvents(cmd));
        }

        return result;
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
        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
            Interlocked.Increment(ref projectionRevision);
        return deleted;
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

