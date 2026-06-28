using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Channel;

namespace TvAIr.Schedule;

/// <summary>
/// 時間追従の適用結果。
/// 親予約へ反映したものだけでなく、EPG未検出・閾値未満・保護スキップもログへ出すための単位。
/// </summary>
public sealed record TimeFollowApplyResult(
    int ReservationId,
    string ServiceName,
    string Title,
    bool Updated,
    string Reason,
    DateTime OldStart,
    DateTime OldEnd,
    DateTime? NewStart,
    DateTime? NewEnd,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort EventId);


public sealed record PreRecordEpgCleanupResult(
    int Deleted,
    int ParentMissing,
    int ParentDisabled,
    int ParentTerminal,
    int ParentConflicted,
    int ParentUserChain,
    string ParentIds);

/// <summary>
/// 予約・キーワードルール・プログラム予約ルールのDB操作。
/// </summary>
public sealed class ReservationStore
{
    private readonly Database db;
    private readonly LogRepository log;
    private readonly ChainDirectRecorderSessionRegistry chainSessionRegistry;
    private readonly ChannelFileLoader channelLoader;
    private readonly UserEventLogService userEvents;
    // v0.3.33: 通常競合ポリシーから「自動救済1回だけ」の足かせを撤去。
    // 競合が判明した時点で、前番組優先/後番組優先の共通ポリシーだけで勝敗を決める。


    private sealed class ConflictOccupancyUnit
    {
        public int UnitId { get; init; }
        public List<Reservation> Reservations { get; init; } = new();
        public Reservation PriorityReservation { get; init; } = new();
        public string Group { get; init; } = string.Empty;
        public DateTime OccupyStart { get; init; }
        public DateTime OccupyEnd { get; init; }
        public bool IsUserChain { get; init; }
        public int? ChainRootId { get; init; }
        public bool HasActiveAnchor { get; init; }
        public string? PreferredTuner { get; init; }
        public string UnitKey => ChainRootId.HasValue ? $"{Group}:CHAIN:{ChainRootId.Value}" : $"{Group}:UNIT:{UnitId}";
        public string MemberIds => string.Join(">", Reservations.Select(r => $"R{r.Id}"));
    }

    /// <summary>時間追従の変化検出閾値（秒）。この秒数以上ずれていれば更新対象とみなす。</summary>
    private const int TimeFollowThresholdSeconds = 30;

    public ReservationStore(Database db, LogRepository log, ChainDirectRecorderSessionRegistry chainSessionRegistry, ChannelFileLoader channelLoader, UserEventLogService userEvents)
    {
        this.db = db;
        this.log = log;
        this.chainSessionRegistry = chainSessionRegistry;
        this.channelLoader = channelLoader;
        this.userEvents = userEvents;
    }

    private static string SafeTuner(string? tuner)
        => string.IsNullOrWhiteSpace(tuner) ? "-" : tuner.Trim();

    private static string EffectiveTunerName(Reservation? r)
        => r is null
            ? string.Empty
            : (!string.IsNullOrWhiteSpace(r.ActualTunerName) ? r.ActualTunerName.Trim() : (r.TunerName ?? string.Empty).Trim());

    private static string TrimForAudit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "-";
        var t = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= 40 ? t : t[..40] + "…";
    }

    private static string TrimTitleForAudit(string? rawTitle)
        => ReservationTitleDisplayContract.ForLog(rawTitle, 40);

    private static string RawTitleBlankForAudit(string? rawTitle)
        => ReservationTitleDisplayContract.RawBlankFlag(rawTitle);

    private static ReservationOriginClassification ClassifyReservationOrigin(Reservation? r)
        => ReservationOriginClassifier.Classify(r);

    private static bool IsProgramGuideMissingReservation(Reservation? r)
        => ClassifyReservationOrigin(r).IsProgramGuideMissingProgramRule;

    private static bool IsRegularResolvedReservation(Reservation? r)
        => ClassifyReservationOrigin(r).IsResolvedEventReservation;

    private static bool HasBroadcastTimeOverlap(Reservation a, Reservation b)
        => a.StartTime < b.EndTime && b.StartTime < a.EndTime;

    private int AutoDeleteResolvedProgramGuideMissingDuplicates(List<Reservation> allScheduled, List<Reservation> scheduled)
    {
        // v0.11.378: 一覧上のReservation照合ではなく、DB上の予約実体で完全一致を確認する。
        // UI説明は追加しない。安全な空欄ProgramRule重複だけ削除する。
        var targets = new List<(int MissingId, int? RuleId, int ResolvedId, string ServiceName, string StartText, string EndText, string ResolvedSource)>();

        using (var con = db.Open())
        {
            using var scan = con.CreateCommand();
            scan.CommandText = """
                SELECT m.id,
                       m.source_rule_id,
                       r.id,
                       COALESCE(m.service_name, ''),
                       COALESCE(m.start_time, ''),
                       COALESCE(m.end_time, ''),
                       COALESCE(r.source, '')
                FROM reservations m
                JOIN reservations r
                  ON r.id <> m.id
                 AND r.network_id = m.network_id
                 AND r.transport_stream_id = m.transport_stream_id
                 AND r.service_id = m.service_id
                 AND r.start_time = m.start_time
                 AND r.end_time = m.end_time
                WHERE m.source = 'program'
                  AND m.event_id = 0
                  AND m.status = 'scheduled'
                  AND m.is_enabled = 1
                  AND m.source_rule_id IS NOT NULL
                  AND (m.recording_started_at IS NULL OR m.recording_started_at = '')
                  AND (m.recording_finished_at IS NULL OR m.recording_finished_at = '')
                  AND (m.title = 'ProgramGuideBlank' OR m.source_rule_name = 'ProgramGuideBlank' OR lower(m.source_rule_name) = 'programguidemissing')
                  AND r.source IN ('manual', 'keyword')
                  AND r.event_id > 0
                  AND r.status = 'scheduled'
                  AND r.is_enabled = 1
                  AND (r.recording_started_at IS NULL OR r.recording_started_at = '')
                  AND (r.recording_finished_at IS NULL OR r.recording_finished_at = '')
                ORDER BY m.id, CASE WHEN r.source = 'manual' THEN 0 ELSE 1 END, r.id;
                """;
            using var reader = scan.ExecuteReader();
            var seenMissing = new HashSet<int>();
            while (reader.Read())
            {
                var missingId = Convert.ToInt32(reader.GetValue(0));
                if (!seenMissing.Add(missingId)) continue;

                int? ruleId = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1));
                var resolvedId = Convert.ToInt32(reader.GetValue(2));
                var serviceName = reader.GetString(3);
                var startText = reader.GetString(4);
                var endText = reader.GetString(5);
                var resolvedSource = reader.GetString(6);
                targets.Add((missingId, ruleId, resolvedId, serviceName, startText, endText, resolvedSource));
            }
        }

        if (targets.Count == 0) return 0;

        using var writeCon = db.Open();
        using var tx = writeCon.BeginTransaction();
        var deletedReservationIds = new HashSet<int>();
        var deletedRuleIds = new HashSet<int>();

        foreach (var target in targets)
        {
            if (deletedReservationIds.Contains(target.MissingId)) continue;

            using (var delReservation = writeCon.CreateCommand())
            {
                delReservation.Transaction = tx;
                delReservation.CommandText = """
                    DELETE FROM reservations
                    WHERE id = $id
                      AND source = 'program'
                      AND event_id = 0
                      AND status = 'scheduled'
                      AND is_enabled = 1
                      AND source_rule_id IS NOT NULL
                      AND (recording_started_at IS NULL OR recording_started_at = '')
                      AND (recording_finished_at IS NULL OR recording_finished_at = '')
                      AND (title = 'ProgramGuideBlank' OR source_rule_name = 'ProgramGuideBlank' OR lower(source_rule_name) = 'programguidemissing');
                    """;
                delReservation.Parameters.AddWithValue("$id", target.MissingId);
                var affected = delReservation.ExecuteNonQuery();
                if (affected <= 0) continue;
            }

            deletedReservationIds.Add(target.MissingId);

            if (target.RuleId.HasValue && !deletedRuleIds.Contains(target.RuleId.Value))
            {
                var ruleId = target.RuleId.Value;
                var deleteRule = false;
                using (var checkRule = writeCon.CreateCommand())
                {
                    checkRule.Transaction = tx;
                    checkRule.CommandText = """
                        SELECT name
                        FROM program_rules
                        WHERE id = $ruleId;
                        """;
                    checkRule.Parameters.AddWithValue("$ruleId", ruleId);
                    var ruleName = checkRule.ExecuteScalar() as string;
                    deleteRule = ReservationOriginClassifier.IsProgramGuideMissingRuleName(ruleName);
                }

                if (deleteRule)
                {
                    using var checkRemaining = writeCon.CreateCommand();
                    checkRemaining.Transaction = tx;
                    checkRemaining.CommandText = """
                        SELECT COUNT(1)
                        FROM reservations
                        WHERE source = 'program'
                          AND source_rule_id = $ruleId;
                        """;
                    checkRemaining.Parameters.AddWithValue("$ruleId", ruleId);
                    deleteRule = Convert.ToInt32(checkRemaining.ExecuteScalar()) == 0;
                }

                if (deleteRule)
                {
                    using var delRule = writeCon.CreateCommand();
                    delRule.Transaction = tx;
                    delRule.CommandText = "DELETE FROM program_rules WHERE id = $ruleId;";
                    delRule.Parameters.AddWithValue("$ruleId", ruleId);
                    if (delRule.ExecuteNonQuery() > 0)
                        deletedRuleIds.Add(ruleId);
                }
            }

            log.Add("PROGRAM_GUIDE_MISSING_DUPLICATE_CLEANUP", "AutoDelete",
                $"result=DELETED missing=R{target.MissingId} resolved=R{target.ResolvedId} service={TrimForAudit(target.ServiceName)} start={TrimForAudit(target.StartText)} end={TrimForAudit(target.EndText)} origin=ProgramGuideMissingProgramRule identity=TimeIdentity resolvedSource={target.ResolvedSource} commonRoute=ALLOC_ROUTE/TUNER_ALLOC ui=none rule=v0.11.378_programguide_missing_duplicate_cleanup_trace_residue");
        }

        tx.Commit();

        if (deletedReservationIds.Count > 0)
        {
            allScheduled.RemoveAll(r => deletedReservationIds.Contains(r.Id));
            scheduled.RemoveAll(r => deletedReservationIds.Contains(r.Id));
            log.Add("PROGRAM_GUIDE_MISSING_DUPLICATE_CLEANUP", "Summary",
                $"result=OK deletedReservations=[{string.Join(',', deletedReservationIds.OrderBy(x => x).Select(x => $"R{x}"))}] deletedRules=[{string.Join(',', deletedRuleIds.OrderBy(x => x))}] mode=db_same_service_exact_time_resolved_event_only ui=none commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.378_programguide_missing_duplicate_cleanup_trace_residue");
        }

        return deletedReservationIds.Count;
    }

    // ─── 予約 CRUD ───────────────────────────────────────────────

    public IReadOnlyList<Reservation> GetAll()
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                   recording_started_at, recording_finished_at, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id
            FROM reservations
            ORDER BY start_time;
            """;
        return ReadReservations(cmd);
    }

    public IReadOnlyList<Reservation> GetByStatus(ReservationStatus status)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                   recording_started_at, recording_finished_at, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id
            FROM reservations
            WHERE status = $status
            ORDER BY start_time;
            """;
        cmd.Parameters.AddWithValue("$status", status.ToString().ToLower());
        return ReadReservations(cmd);
    }

    /// <summary>指定期間内に開始する予約を取得する。</summary>
    public IReadOnlyList<Reservation> GetByRange(DateTime from, DateTime to)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                   recording_started_at, recording_finished_at, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id
            FROM reservations
            WHERE start_time >= $from AND start_time < $to
            ORDER BY start_time;
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString("O"));
        cmd.Parameters.AddWithValue("$to",   to.ToString("O"));
        return ReadReservations(cmd);
    }

    public Reservation? GetById(int id)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                   recording_started_at, recording_finished_at, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id
            FROM reservations WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return ReadReservations(cmd).FirstOrDefault();
    }


    /// <summary>同一番組の有効予約を取得する。競合予約をユーザー明示チェーンへ昇格する入口で使用する。</summary>
    public Reservation? GetActiveByEvent(ushort networkId, ushort tsId, ushort serviceId, ushort eventId)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                   recording_started_at, recording_finished_at, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id
            FROM reservations
            WHERE network_id = $nid AND transport_stream_id = $tsid
              AND service_id = $sid AND event_id = $eid
              AND status IN ('scheduled','recording')
            ORDER BY id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$nid", networkId);
        cmd.Parameters.AddWithValue("$tsid", tsId);
        cmd.Parameters.AddWithValue("$sid", serviceId);
        cmd.Parameters.AddWithValue("$eid", eventId);
        return ReadReservations(cmd).FirstOrDefault();
    }

    /// <summary>既存予約を、チェーンボタンの専用入口からユーザー明示チェーンへ昇格する。</summary>
    public void UpdateUserChainLink(int id, int predecessorId, int rootId)
    {
        var before = GetById(id);
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET is_user_chain = 1,
                user_chain_previous_id = $predecessorId,
                user_chain_root_id = $rootId,
                is_enabled = 1,
                updated_at = $updated
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$predecessorId", predecessorId);
        cmd.Parameters.AddWithValue("$rootId", rootId);
        cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
        var affected = cmd.ExecuteNonQuery();
        log.Add("RESERVATION_AUDIT", "UpdateUserChain",
            $"id=R{id} affected={affected} predecessor=R{predecessorId} root=R{rootId} beforeConflicted={(before?.IsConflicted.ToString() ?? "-")} beforeTuner={SafeTuner(EffectiveTunerName(before))} title={TrimTitleForAudit(before?.Title)} rawTitleBlank={RawTitleBlankForAudit(before?.Title)}");
    }

    private static bool IsActiveParentReservation(Reservation r)
    {
        // SystemEpg / PreRecEpg child entries are operational helpers, not user-facing parent reservations.
        if (r.Source == ReservationSource.Epg) return false;
        if (r.Status != ReservationStatus.Scheduled && r.Status != ReservationStatus.Recording) return false;
        if (!r.IsEnabled) return false;
        return true;
    }

    private static string ReservationDedupeKey(Reservation r)
    {
        if (r.EventId != 0)
            return $"event:{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{r.EventId}";

        var start = r.StartTime.ToString("O");
        var end = r.EndTime.ToString("O");
        return $"time:{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{start}:{end}:{NormalizeDedupeText(r.Title)}";
    }

    private static string NormalizeDedupeText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Trim().Normalize(NormalizationForm.FormKC);

    private static string NormalizeReservationTitleForStorage(string? title, string? serviceName, int id = 0)
    {
        return title ?? string.Empty;
    }

    public Reservation? FindActiveParentDuplicate(Reservation candidate)
    {
        if (candidate.Source == ReservationSource.Immediate)
            return null;

        var key = ReservationDedupeKey(candidate);
        return GetAll()
            .Where(IsActiveParentReservation)
            .Where(r => r.Id != candidate.Id)
            .Where(r => string.Equals(ReservationDedupeKey(r), key, StringComparison.Ordinal))
            .OrderByDescending(r => r.Status == ReservationStatus.Recording)
            .ThenBy(r => r.Id)
            .FirstOrDefault();
    }

    public int SuppressDuplicateScheduledParentReservations(string source, string action)
    {
        var activeParents = GetAll()
            .Where(IsActiveParentReservation)
            .GroupBy(ReservationDedupeKey, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        var suppressIds = new List<int>();
        foreach (var group in activeParents)
        {
            var keep = group
                .OrderByDescending(r => r.Status == ReservationStatus.Recording)
                .ThenBy(r => r.Id)
                .First();

            foreach (var duplicate in group.Where(r => r.Id != keep.Id && r.Status == ReservationStatus.Scheduled).OrderBy(r => r.Id))
            {
                suppressIds.Add(duplicate.Id);
                log.Add("RESERVATION_DEDUPE", "SUPPRESS_DUPLICATE",
                    $"result=CANCEL_DUPLICATE source={source} action={action} keep=R{keep.Id} duplicate=R{duplicate.Id} service={TrimForAudit(duplicate.ServiceName)} title={TrimTitleForAudit(duplicate.Title)} rawTitleBlank={RawTitleBlankForAudit(duplicate.Title)} start={duplicate.StartTime:MM/dd HH:mm:ss} end={duplicate.EndTime:MM/dd HH:mm:ss} key={group.Key} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.87_reservation_parent_dedupe");
            }
        }

        if (suppressIds.Count == 0)
            return 0;

        using var con = db.Open();
        using var tx = con.BeginTransaction();
        foreach (var id in suppressIds.Distinct())
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE reservations
                SET status = 'cancelled',
                    is_conflicted = 0,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'scheduled';
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return suppressIds.Distinct().Count();
    }

    public int Add(Reservation r)
    {
        // v0.11.175: 予約入口の最終防衛。EPG DB 側で修復できたタイトルだけでなく、
        // 既存DB/自動検索/番組表から来た途中汚染もここで切り落とす。
        r.Title = NormalizeReservationTitleForStorage(r.Title, r.ServiceName, r.Id);
        var now = DateTime.Now.ToString("O");
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO reservations
              (network_id, transport_stream_id, service_id, event_id,
               title, start_time, end_time, status, source, created_at, updated_at,
               channel_argument, is_conflicted, is_enabled, tuner_name, service_name,
               scheduled_start_time, source_rule_id, source_rule_name,
               is_user_chain, user_chain_previous_id, user_chain_root_id)
            VALUES
              ($nid, $tsid, $sid, $eid,
               $title, $start, $end, $status, $source, $now, $now,
               $charg, 0, 1, '', $svcname,
               $start, $sourceRuleId, $sourceRuleName,
               $isUserChain, $userChainPreviousId, $userChainRootId);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$nid",     r.NetworkId);
        cmd.Parameters.AddWithValue("$tsid",    r.TransportStreamId);
        cmd.Parameters.AddWithValue("$sid",     r.ServiceId);
        cmd.Parameters.AddWithValue("$eid",     r.EventId);
        cmd.Parameters.AddWithValue("$title",   r.Title);
        cmd.Parameters.AddWithValue("$start",   r.StartTime.ToString("O"));
        cmd.Parameters.AddWithValue("$end",     r.EndTime.ToString("O"));
        cmd.Parameters.AddWithValue("$status",  r.Status.ToString().ToLower());
        cmd.Parameters.AddWithValue("$source",  r.Source.ToString().ToLower());
        cmd.Parameters.AddWithValue("$charg",   r.ChannelArgument);
        cmd.Parameters.AddWithValue("$svcname", r.ServiceName);
        if (r.SourceRuleId.HasValue)
            cmd.Parameters.AddWithValue("$sourceRuleId", r.SourceRuleId.Value);
        else
            cmd.Parameters.AddWithValue("$sourceRuleId", DBNull.Value);
        cmd.Parameters.AddWithValue("$sourceRuleName", r.SourceRuleName ?? "");
        cmd.Parameters.AddWithValue("$isUserChain", r.IsUserChain ? 1 : 0);
        if (r.UserChainPreviousId.HasValue) cmd.Parameters.AddWithValue("$userChainPreviousId", r.UserChainPreviousId.Value); else cmd.Parameters.AddWithValue("$userChainPreviousId", DBNull.Value);
        if (r.UserChainRootId.HasValue) cmd.Parameters.AddWithValue("$userChainRootId", r.UserChainRootId.Value); else cmd.Parameters.AddWithValue("$userChainRootId", DBNull.Value);
        cmd.Parameters.AddWithValue("$now",     now);
        var newId = Convert.ToInt32(cmd.ExecuteScalar());
        log.Add("RESERVATION_AUDIT", "ADD",
            $"service={TrimForAudit(r.ServiceName)} title={TrimTitleForAudit(r.Title)} rawTitleBlank={RawTitleBlankForAudit(r.Title)} id=R{newId} status={r.Status} source={r.Source} enabled={r.IsEnabled} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} svcId={r.ServiceId} ch={r.ChannelArgument} tuner={SafeTuner(r.TunerName)} userChain={r.IsUserChain} chainPrev={(r.UserChainPreviousId.HasValue ? $"R{r.UserChainPreviousId.Value}" : "-")} chainRoot={(r.UserChainRootId.HasValue ? $"R{r.UserChainRootId.Value}" : "-")}");
        userEvents.AddReservationAdded(r, newId);
        return newId;
    }


    /// <summary>
    /// 競合のまま録画開始時刻へ到達した予約を、再起動後にも復活しない終端状態へ確定する。
    /// REC_SKIPPED_BY_CONFLICT のユーザー運用イベントは呼び出し側で発行し、ここでは予約本体だけを閉じる。
    /// </summary>
    public void FinalizeSkippedByConflictAtDue(int id, string group, string reason)
    {
        var before = GetById(id);
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET status = 'failed',
                is_conflicted = 1,
                tuner_name = '',
                actual_tuner_name = '',
                recording_finished_at = CASE WHEN COALESCE(recording_finished_at, '') = '' THEN $finished ELSE recording_finished_at END,
                updated_at = $now
            WHERE id = $id
              AND status = 'scheduled';
            """;
        var now = DateTime.Now;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$finished", now.ToString("O"));
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        var affected = cmd.ExecuteNonQuery();

        log.Add("RESERVATION_AUDIT", "SKIP_CONFLICT_FINALIZE",
            $"service={TrimForAudit(before?.ServiceName)} title={TrimTitleForAudit(before?.Title)} rawTitleBlank={RawTitleBlankForAudit(before?.Title)} id=R{id} affected={affected} from={(before is null ? "<missing>" : before.Status.ToString())} to=Failed reason={reason} group={group} enabled={(before?.IsEnabled.ToString() ?? "-")} conflicted={(before?.IsConflicted.ToString() ?? "-")} start={(before is null ? "-" : before.StartTime.ToString("MM/dd HH:mm:ss"))} end={(before is null ? "-" : before.EndTime.ToString("MM/dd HH:mm:ss"))} tuner={SafeTuner(EffectiveTunerName(before))} rule=v0.11.135_conflict_skip_terminal_state");
    }

    public void UpdateStatus(int id, ReservationStatus status, bool force = false)
    {
        var before = GetById(id);
        if (status == ReservationStatus.Scheduled && HasOpenRecordingRuntime(before))
        {
            log.Add("RESERVATION_STATUS_WRITE_GUARD", $"R{id}",
                $"result=SUPPRESSED from={before!.Status} attempted=Scheduled kept=Recording reason=open_recording_runtime source=UpdateStatus force={force} " +
                $"started={before.RecordingStartedAt:MM/dd HH:mm:ss} finished={(before.RecordingFinishedAt.HasValue ? before.RecordingFinishedAt.Value.ToString("MM/dd HH:mm:ss") : "-")} " +
                $"end={before.EndTime:MM/dd HH:mm:ss} tuner={SafeTuner(EffectiveTunerName(before))} service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} " +
                $"rule=v0.10.08_recording_status_write_contract");
            return;
        }
        if (!force && ShouldSuppressTransientFailedStatus(before, status))
        {
            log.Add("RESERVATION_STATUS_GUARD_RECORDING_WINDOW", $"R{id}",
                $"result=SUPPRESSED requestedStatus=Failed currentStatus={before!.Status} reason=recording_window_status_flip_guard " +
                $"start={before.StartTime:MM/dd HH:mm:ss} end={before.EndTime:MM/dd HH:mm:ss} now={DateTime.Now:MM/dd HH:mm:ss} " +
                $"tuner={SafeTuner(EffectiveTunerName(before))} service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} " +
                $"rule=v0.9.83_failed_direct_route_guard");
            return;
        }

        var clearTerminalConflict = ClearsConflictFlagForTerminalStatus(status);
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = clearTerminalConflict
            ? """
              UPDATE reservations
              SET status = $status, is_conflicted = 0, updated_at = $now
              WHERE id = $id;
              """
            : """
              UPDATE reservations
              SET status = $status, updated_at = $now
              WHERE id = $id;
              """;
        cmd.Parameters.AddWithValue("$status", status.ToString().ToLower());
        cmd.Parameters.AddWithValue("$now",    DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id",     id);
        var affected = cmd.ExecuteNonQuery();
        log.Add("RESERVATION_AUDIT", "STATUS",
            $"service={TrimForAudit(before?.ServiceName)} title={TrimTitleForAudit(before?.Title)} rawTitleBlank={RawTitleBlankForAudit(before?.Title)} id=R{id} affected={affected} from={(before is null ? "<missing>" : before.Status.ToString())} to={status} enabled={(before?.IsEnabled.ToString() ?? "-")} conflicted={(before?.IsConflicted.ToString() ?? "-")} terminalConflictClear={clearTerminalConflict} start={(before is null ? "-" : before.StartTime.ToString("MM/dd HH:mm:ss"))} end={(before is null ? "-" : before.EndTime.ToString("MM/dd HH:mm:ss"))} tuner={SafeTuner(EffectiveTunerName(before))} userChain={(before?.IsUserChain.ToString() ?? "-")} chainPrev={(before?.UserChainPreviousId.HasValue == true ? $"R{before.UserChainPreviousId.Value}" : "-")}");
        if (affected > 0)
            userEvents.AddReservationStatusChanged(before, id, status);
    }

    private static bool ShouldSuppressTransientFailedStatus(Reservation? before, ReservationStatus requestedStatus)
    {
        if (before is null) return false;
        if (requestedStatus != ReservationStatus.Failed) return false;
        if (!before.RecordingStartedAt.HasValue) return false;
        if (before.RecordingFinishedAt.HasValue) return false;

        // v0.9.83:
        // v0.9.82では Recording -> Failed のみを抑止していたため、
        // 録画開始済みセッションが一瞬別状態に見えた経路から Failed が
        // 書かれるケースを取りこぼしていた。
        // 録画開始実績があり、終了実績がなく、予定終了+2分以内であれば、
        // force=true の明示的な失敗確定以外は Failed への直接遷移を共通出口で止める。
        var guardUntil = before.EndTime.AddMinutes(2);
        return DateTime.Now < guardUntil;
    }

    private static bool HasOpenRecordingRuntime(Reservation? reservation)
    {
        if (reservation is null) return false;
        if (reservation.Source == ReservationSource.Epg) return false;
        if (!reservation.RecordingStartedAt.HasValue) return false;
        if (reservation.RecordingFinishedAt.HasValue) return false;
        if (reservation.Status is ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed) return false;

        // v0.10.08:
        // DB status は永続化された表示状態であり、録画開始後の実行中判定の主ではない。
        // 録画開始実績があり終了実績がない予約は、予定終了+2分までは
        // Runtime/TunerPool 側の録画セッションとして扱い、Scheduled/Conflicted へ戻す書き込みを拒否する。
        return DateTime.Now < reservation.EndTime.AddMinutes(2);
    }

    private static ReservationStatus ProjectRuntimeStatus(Reservation reservation)
    {
        if (HasOpenRecordingRuntime(reservation))
            return ReservationStatus.Recording;
        return reservation.Status;
    }

    private static bool ClearsConflictFlagForTerminalStatus(ReservationStatus status)
    {
        // v0.8.23:
        // Completed/Cancelled は予約実行上の終端であり、以後のチューナー競合判断対象ではない。
        // Failed は失敗原因として競合情報を残したいケースがあるため、ここでは触らない。
        return status == ReservationStatus.Completed
            || status == ReservationStatus.Cancelled;
    }


    /// <summary>
    /// 起動時に録画中断を検出した元予約を終端化する。
    /// Recording のまま予約一覧へ残さず、既存部分ファイルを保護したうえで失敗終端へ落とす。
    /// </summary>
    public void FinalizeInterruptedRecordingAtStartup(int id, DateTime finishedAt, string reason, string? fileEvidence = null, string trigger = "StartupRecovery")
    {
        var before = GetById(id);
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET status = 'failed',
                is_conflicted = 0,
                recording_finished_at = CASE WHEN COALESCE(recording_finished_at, '') = '' THEN $finished ELSE recording_finished_at END,
                updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$finished", finishedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        var affected = cmd.ExecuteNonQuery();
        log.Add("RESERVATION_AUDIT", "STARTUP_INTERRUPTED_FINALIZE",
            $"service={TrimForAudit(before?.ServiceName)} title={TrimTitleForAudit(before?.Title)} rawTitleBlank={RawTitleBlankForAudit(before?.Title)} id=R{id} affected={affected} from={(before is null ? "<missing>" : before.Status.ToString())} to=Failed reason={reason} started={(before?.RecordingStartedAt.HasValue == true ? before.RecordingStartedAt.Value.ToString("MM/dd HH:mm:ss") : "-")} finished={finishedAt:MM/dd HH:mm:ss} end={(before is null ? "-" : before.EndTime.ToString("MM/dd HH:mm:ss"))} tuner={SafeTuner(EffectiveTunerName(before))} rule=v0.11.138_recording_interruption_recovery_common");
        if (affected > 0)
            userEvents.AddRecordingInterruptedAtStartup(before, id, reason, fileEvidence, finishedAt, trigger);
    }

    /// <summary>
    /// 起動時復旧用に、元予約と同じ番組を別予約として再投入する。
    /// 既存部分ファイルを上書きしないため、録画ファイル名は録画開始時の一意化で (1)/(2) 側へ逃がす。
    /// </summary>
    public int AddStartupRecoveryReservation(Reservation original, DateTime now)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO reservations
              (network_id, transport_stream_id, service_id, event_id,
               title, start_time, end_time, status, source, created_at, updated_at,
               channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
               recording_started_at, recording_finished_at, service_name,
               scheduled_start_time, source_rule_id, source_rule_name,
               is_user_chain, user_chain_previous_id, user_chain_root_id)
            VALUES
              ($nid, $tsid, $sid, $eid,
               $title, $start, $end, 'scheduled', $source, $now, $now,
               $charg, 0, 1, $tuner, '',
               '', '', $svcname,
               $scheduledStart, $sourceRuleId, $sourceRuleName,
               $isUserChain, $userChainPreviousId, $userChainRootId);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$nid", original.NetworkId);
        cmd.Parameters.AddWithValue("$tsid", original.TransportStreamId);
        cmd.Parameters.AddWithValue("$sid", original.ServiceId);
        cmd.Parameters.AddWithValue("$eid", original.EventId);
        cmd.Parameters.AddWithValue("$title", original.Title ?? string.Empty);
        cmd.Parameters.AddWithValue("$start", original.StartTime.ToString("O"));
        cmd.Parameters.AddWithValue("$end", original.EndTime.ToString("O"));
        cmd.Parameters.AddWithValue("$source", original.Source.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$charg", original.ChannelArgument ?? string.Empty);
        cmd.Parameters.AddWithValue("$tuner", !string.IsNullOrWhiteSpace(original.ActualTunerName) ? original.ActualTunerName : (original.TunerName ?? string.Empty));
        cmd.Parameters.AddWithValue("$svcname", original.ServiceName ?? string.Empty);
        cmd.Parameters.AddWithValue("$scheduledStart", (original.ScheduledStartTime ?? original.StartTime).ToString("O"));
        if (original.SourceRuleId.HasValue)
            cmd.Parameters.AddWithValue("$sourceRuleId", original.SourceRuleId.Value);
        else
            cmd.Parameters.AddWithValue("$sourceRuleId", DBNull.Value);
        cmd.Parameters.AddWithValue("$sourceRuleName", original.SourceRuleName ?? string.Empty);
        cmd.Parameters.AddWithValue("$isUserChain", original.IsUserChain ? 1 : 0);
        if (original.UserChainPreviousId.HasValue) cmd.Parameters.AddWithValue("$userChainPreviousId", original.UserChainPreviousId.Value); else cmd.Parameters.AddWithValue("$userChainPreviousId", DBNull.Value);
        if (original.UserChainRootId.HasValue) cmd.Parameters.AddWithValue("$userChainRootId", original.UserChainRootId.Value); else cmd.Parameters.AddWithValue("$userChainRootId", DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        var newId = Convert.ToInt32(cmd.ExecuteScalar());
        log.Add("RESERVATION_AUDIT", "STARTUP_RECOVERY_ADD",
            $"source=R{original.Id} recovery=R{newId} service={TrimForAudit(original.ServiceName)} title={TrimTitleForAudit(original.Title)} rawTitleBlank={RawTitleBlankForAudit(original.Title)} start={original.StartTime:MM/dd HH:mm:ss} end={original.EndTime:MM/dd HH:mm:ss} tuner={SafeTuner(!string.IsNullOrWhiteSpace(original.ActualTunerName) ? original.ActualTunerName : original.TunerName)} userChainCopied={original.IsUserChain} chainPrev={(original.UserChainPreviousId.HasValue ? $"R{original.UserChainPreviousId.Value}" : "-")} chainRoot={(original.UserChainRootId.HasValue ? $"R{original.UserChainRootId.Value}" : "-")} reason=startup_interrupted_recording_recovery rule=v0.11.116_recovery_chain_context_preserve");
        return newId;
    }

    public void UpdateEndTime(int id, DateTime endTime)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET end_time = $end, updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$end", endTime.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id",  id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 時間追従による開始・終了時刻の更新。
    /// scheduled_start_timeは元の予約時刻として変更しない。
    /// </summary>
    public void UpdateStartEndTime(int id, DateTime startTime, DateTime endTime)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET start_time = $start, end_time = $end, updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$start", startTime.ToString("O"));
        cmd.Parameters.AddWithValue("$end",   endTime.ToString("O"));
        cmd.Parameters.AddWithValue("$now",   DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id",    id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateTitleStartEndTime(int id, string title, string serviceName, DateTime startTime, DateTime endTime)
    {
        var safeTitle = NormalizeReservationTitleForStorage(title, serviceName, id);
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET title = $title, start_time = $start, end_time = $end, updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$title", safeTitle);
        cmd.Parameters.AddWithValue("$start", startTime.ToString("O"));
        cmd.Parameters.AddWithValue("$end",   endTime.ToString("O"));
        cmd.Parameters.AddWithValue("$now",   DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id",    id);
        cmd.ExecuteNonQuery();
    }

    public bool UpdateTitleIfBlank(int id, string title, string serviceName)
    {
        var safeTitle = NormalizeReservationTitleForStorage(title, serviceName, id);
        if (string.IsNullOrWhiteSpace(safeTitle)) return false;

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET title = $title, updated_at = $now
            WHERE id = $id AND (title IS NULL OR trim(title) = '');
            """;
        cmd.Parameters.AddWithValue("$title", safeTitle);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void Delete(int id)
    {
        var before = GetById(id);
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM reservations WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var affected = cmd.ExecuteNonQuery();
        log.Add("RESERVATION_AUDIT", "DELETE",
            $"service={TrimForAudit(before?.ServiceName)} title={TrimTitleForAudit(before?.Title)} rawTitleBlank={RawTitleBlankForAudit(before?.Title)} id=R{id} affected={affected} status={(before?.Status.ToString() ?? "-")} source={(before?.Source.ToString() ?? "-")} enabled={(before?.IsEnabled.ToString() ?? "-")} start={(before is null ? "-" : before.StartTime.ToString("MM/dd HH:mm:ss"))} end={(before is null ? "-" : before.EndTime.ToString("MM/dd HH:mm:ss"))} userChain={(before?.IsUserChain.ToString() ?? "-")} chainPrev={(before?.UserChainPreviousId.HasValue == true ? $"R{before.UserChainPreviousId.Value}" : "-")}");
        if (affected > 0 && before is not null)
            userEvents.AddReservationDeleted(before, id);
    }

    public int DeleteLogEntries()
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"DELETE FROM reservations
WHERE source <> 'Epg'
  AND status IN ('completed', 'cancelled', 'failed');";
        var affected = cmd.ExecuteNonQuery();
        log.Add("RESERVATION_AUDIT", "DELETE_LOGS", $"affected={affected} source=non_epg status=completed,cancelled,failed");
        return affected;
    }

    /// <summary>
    /// ユーザー明示チェーンの取消対象を返す。
    /// 指定予約を起点に、user_chain_previous_id で直列につながる後続予約をすべて対象にする。
    /// 末尾なら1件のみ、途中ならそこ以降、先頭ならチェーン全体が対象になる。
    /// </summary>
    public IReadOnlyList<Reservation> GetUserChainCancelTargets(int id)
    {
        var all = GetAll()
            .Where(r => r.Status == ReservationStatus.Scheduled || r.Status == ReservationStatus.Recording)
            .ToList();

        var start = all.FirstOrDefault(r => r.Id == id);
        if (start is null) return Array.Empty<Reservation>();

        var byPrevious = all
            .Where(r => r.UserChainPreviousId.HasValue)
            .GroupBy(r => r.UserChainPreviousId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.StartTime).ToList());

        var result = new List<Reservation>();
        var visited = new HashSet<int>();
        var queue = new Queue<Reservation>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current.Id)) continue;
            result.Add(current);

            if (byPrevious.TryGetValue(current.Id, out var children))
            {
                foreach (var child in children)
                    queue.Enqueue(child);
            }
        }

        return result.OrderBy(r => r.StartTime).ToList();
    }

    /// <summary>
    /// 録画中の手動停止では、後続チェーンをキャンセルせず通常予約として残す。
    /// 予約取消時のチェーン一括取消とは意味を分けるため、停止対象の直後以降だけをチェーンから切り離す。
    /// </summary>
    public IReadOnlyList<Reservation> DetachUserChainSuccessorsForManualStop(int predecessorId)
    {
        var all = GetAll()
            .Where(r => r.Status == ReservationStatus.Scheduled || r.Status == ReservationStatus.Recording)
            .ToList();

        var byPrevious = all
            .Where(r => r.IsUserChain && r.UserChainPreviousId.HasValue)
            .GroupBy(r => r.UserChainPreviousId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.StartTime).ToList());

        var result = new List<Reservation>();
        var visited = new HashSet<int>();
        var queue = new Queue<Reservation>();
        if (byPrevious.TryGetValue(predecessorId, out var children))
        {
            foreach (var child in children)
                queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current.Id)) continue;
            result.Add(current);

            if (byPrevious.TryGetValue(current.Id, out var nextChildren))
            {
                foreach (var child in nextChildren)
                    queue.Enqueue(child);
            }
        }

        var targets = result
            .Where(r => r.Status == ReservationStatus.Scheduled)
            .OrderBy(r => r.StartTime)
            .ToList();

        var predecessor = GetById(predecessorId);
        var predecessorService = TrimForAudit(predecessor?.ServiceName);
        var predecessorTitle = TrimTitleForAudit(predecessor?.Title);
        var predecessorRawTitleBlank = RawTitleBlankForAudit(predecessor?.Title);

        if (targets.Count == 0)
        {
            log.Add("CHAIN_STOP_POLICY", $"R{predecessorId}",
                $"operation=ManualStop service={predecessorService} title={predecessorTitle} rawTitleBlank={predecessorRawTitleBlank} result=NO_SUCCESSOR_TO_DETACH cancelledSuccessors=0 detachedSuccessors=0 rule=v0.11.516_chain_stop_policy_title_metadata_contract");
            return Array.Empty<Reservation>();
        }

        using var con = db.Open();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE reservations
            SET is_user_chain = 0,
                user_chain_previous_id = NULL,
                user_chain_root_id = NULL,
                tuner_name = '',
                actual_tuner_name = '',
                updated_at = $now
            WHERE id = $id
              AND status = 'scheduled';
            """;
        var pNow = cmd.Parameters.Add("$now", SqliteType.Text);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        pNow.Value = DateTime.Now.ToString("O");

        var affected = 0;
        foreach (var target in targets)
        {
            pId.Value = target.Id;
            affected += cmd.ExecuteNonQuery();
        }
        tx.Commit();

        var targetText = string.Join(",", targets.Select(x => $"R{x.Id}:{TrimForAudit(x.ServiceName)}:{TrimTitleForAudit(x.Title)}:rawTitleBlank={RawTitleBlankForAudit(x.Title)}"));
        log.Add("CHAIN_STOP_POLICY", $"R{predecessorId}",
            $"operation=ManualStop service={predecessorService} title={predecessorTitle} rawTitleBlank={predecessorRawTitleBlank} result=DETACH_SUCCESSORS cancelledSuccessors=0 detachedSuccessors={targets.Count} affected={affected} action=keep_successors_as_normal_scheduled_reservations targets=[{targetText}] rule=v0.11.516_chain_stop_policy_title_metadata_contract");
        foreach (var target in targets)
        {
            log.Add("RESERVATION_AUDIT", "CHAIN_DETACH",
                $"service={TrimForAudit(target.ServiceName)} title={TrimTitleForAudit(target.Title)} rawTitleBlank={RawTitleBlankForAudit(target.Title)} id=R{target.Id} fromUserChain=True toUserChain=False previous=R{(target.UserChainPreviousId.HasValue ? target.UserChainPreviousId.Value.ToString() : "-")} root=R{(target.UserChainRootId.HasValue ? target.UserChainRootId.Value.ToString() : "-")} status={target.Status} action=manual_stop_detach_keep_scheduled");
        }
        return targets;
    }

    /// <summary>複数予約を同じステータスに変更する。</summary>
    public int UpdateStatusMany(IEnumerable<int> ids, ReservationStatus status)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return 0;
        var beforeMap = idList.ToDictionary(x => x, x => GetById(x));
        var clearTerminalConflict = ClearsConflictFlagForTerminalStatus(status);

        using var con = db.Open();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = clearTerminalConflict
            ? "UPDATE reservations SET status = $status, is_conflicted = 0, updated_at = $now WHERE id = $id;"
            : "UPDATE reservations SET status = $status, updated_at = $now WHERE id = $id;";
        var pStatus = cmd.Parameters.Add("$status", SqliteType.Text);
        var pNow = cmd.Parameters.Add("$now", SqliteType.Text);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        pStatus.Value = status.ToString().ToLower();
        pNow.Value = DateTime.Now.ToString("O");

        var count = 0;
        foreach (var targetId in idList)
        {
            beforeMap.TryGetValue(targetId, out var before);
            if (status == ReservationStatus.Scheduled && HasOpenRecordingRuntime(before))
            {
                log.Add("RESERVATION_STATUS_WRITE_GUARD", $"R{targetId}",
                    $"result=SUPPRESSED from={before!.Status} attempted=Scheduled kept=Recording reason=open_recording_runtime source=UpdateStatusMany " +
                    $"started={before.RecordingStartedAt:MM/dd HH:mm:ss} finished={(before.RecordingFinishedAt.HasValue ? before.RecordingFinishedAt.Value.ToString("MM/dd HH:mm:ss") : "-")} " +
                    $"end={before.EndTime:MM/dd HH:mm:ss} tuner={SafeTuner(EffectiveTunerName(before))} service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} " +
                    $"rule=v0.10.08_recording_status_write_contract");
                continue;
            }
            pId.Value = targetId;
            count += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        foreach (var targetId in idList)
        {
            beforeMap.TryGetValue(targetId, out var before);
            log.Add("RESERVATION_AUDIT", "STATUS_MANY",
                $"service={TrimForAudit(before?.ServiceName)} title={TrimTitleForAudit(before?.Title)} rawTitleBlank={RawTitleBlankForAudit(before?.Title)} id=R{targetId} to={status} from={(before?.Status.ToString() ?? "-")} enabled={(before?.IsEnabled.ToString() ?? "-")} conflicted={(before?.IsConflicted.ToString() ?? "-")} terminalConflictClear={clearTerminalConflict} start={(before is null ? "-" : before.StartTime.ToString("MM/dd HH:mm:ss"))} end={(before is null ? "-" : before.EndTime.ToString("MM/dd HH:mm:ss"))}");
        }
        return count;
    }

    /// <summary>is_conflictedフラグを更新する。</summary>
    public void UpdateConflicted(int id, bool conflicted)
    {
        var before = GetById(id);
        if (conflicted && HasOpenRecordingRuntime(before))
        {
            log.Add("RESERVATION_STATUS_WRITE_GUARD", $"R{id}",
                $"result=SUPPRESSED field=IsConflicted attempted=True kept=False reason=open_recording_runtime source=UpdateConflicted " +
                $"status={before!.Status} started={before.RecordingStartedAt:MM/dd HH:mm:ss} end={before.EndTime:MM/dd HH:mm:ss} " +
                $"tuner={SafeTuner(EffectiveTunerName(before))} service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} " +
                $"rule=v0.10.08_recording_status_write_contract");
            return;
        }
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET is_conflicted = $v, updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$v",   conflicted ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id",  id);
        var affected = cmd.ExecuteNonQuery();
        log.Add("RESERVATION_AUDIT", "CONFLICT_FLAG",
            $"service={TrimForAudit(before?.ServiceName)} title={TrimTitleForAudit(before?.Title)} rawTitleBlank={RawTitleBlankForAudit(before?.Title)} id=R{id} affected={affected} from={(before?.IsConflicted.ToString() ?? "-")} to={conflicted} status={(before?.Status.ToString() ?? "-")} enabled={(before?.IsEnabled.ToString() ?? "-")} start={(before is null ? "-" : before.StartTime.ToString("MM/dd HH:mm:ss"))} end={(before is null ? "-" : before.EndTime.ToString("MM/dd HH:mm:ss"))}");
    }

    /// <summary>
    /// v0.8.23: Completed / Cancelled に残った is_conflicted を整理する。
    /// 競合は録画実行前の割当判断であり、終端予約が以後も競合表示されるとUI判断を誤らせる。
    /// Failed は失敗原因の保持対象になり得るため、ここでは触らない。
    /// </summary>
    public int ClearTerminalConflictResidues(string reason)
    {
        var targets = GetAll()
            .Where(r => r.IsConflicted)
            .Where(r => r.Status == ReservationStatus.Completed || r.Status == ReservationStatus.Cancelled)
            .Where(r => r.Source != ReservationSource.Epg)
            .OrderBy(r => r.EndTime)
            .ThenBy(r => r.Id)
            .ToList();

        if (targets.Count == 0)
            return 0;

        var now = DateTime.Now;
        using var con = db.Open();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE reservations
            SET is_conflicted = 0, updated_at = $now
            WHERE id = $id
              AND is_conflicted = 1
              AND source != 'epg'
              AND (status = 'completed' OR status = 'cancelled');
            """;
        var pNow = cmd.Parameters.Add("$now", SqliteType.Text);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        pNow.Value = now.ToString("O");

        var affected = 0;
        foreach (var target in targets)
        {
            pId.Value = target.Id;
            affected += cmd.ExecuteNonQuery();
        }
        tx.Commit();

        var sample = string.Join(";", targets
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(5)
            .Select(x => $"R{x.Id}:{TrimForAudit(x.ServiceName)}:{x.Status}"));

        log.Add("RESERVATION_AUDIT", "TERMINAL_CONFLICT_CLEANUP",
            $"affected={affected} candidates={targets.Count} reason={reason} statuses=Completed,Cancelled failedPreserved=True sample={sample} rule=v0.8.23_terminal_conflict_residue_cleanup");

        return affected;
    }

    /// <summary>is_enabledフラグを更新する（ユーザーによる録画ON/OFF）。</summary>
    public void UpdateEnabled(int id, bool enabled)
    {
        var before = GetById(id);
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET is_enabled = $v, updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$v",   enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id",  id);
        var affected = cmd.ExecuteNonQuery();

        // v0.5.65:
        // 自動検索予約はルール更新時に scheduled の旧ヒットを削除し、同じ番組を別IDで再生成することがある。
        // そのためユーザーが個別に「無効」にした keyword 予約は、reservation.id だけでなく
        // ruleId + NID/TSID/SID + 開始/終了 + titleHash のヒット単位抑止として保存する。
        // これにより次回マッチングで同じ番組が Rxxx として復活するのを防ぐ。
        if (affected > 0 && before is not null && before.Source == ReservationSource.Keyword)
        {
            if (!enabled)
                AddKeywordSuppressionForUserDisabled(before, "enabled_toggle_off");
            else
                RemoveKeywordSuppressionForUserEnabled(before, "enabled_toggle_on");
        }

        log.Add("RESERVATION_AUDIT", "ENABLED_FLAG",
            $"service={TrimForAudit(before?.ServiceName)} title={TrimTitleForAudit(before?.Title)} rawTitleBlank={RawTitleBlankForAudit(before?.Title)} id=R{id} affected={affected} from={(before?.IsEnabled.ToString() ?? "-")} to={enabled} status={(before?.Status.ToString() ?? "-")} conflicted={(before?.IsConflicted.ToString() ?? "-")} start={(before is null ? "-" : before.StartTime.ToString("MM/dd HH:mm:ss"))} end={(before is null ? "-" : before.EndTime.ToString("MM/dd HH:mm:ss"))} userChain={(before?.IsUserChain.ToString() ?? "-")} chainPrev={(before?.UserChainPreviousId.HasValue == true ? $"R{before.UserChainPreviousId.Value}" : "-")}");
        if (affected > 0 && before is not null)
            userEvents.AddReservationEnabledChanged(before, id, enabled);
    }

    /// <summary>
    /// v32.85: 放送終了時刻を過ぎても scheduled のまま残っているユーザー予約を Cancelled に移行する。
    /// 無効予約(IsEnabled=false)・競合予約(IsConflicted=true)・録画失敗などで録画されなかった残骸を掃除する。
    /// EPG関連エントリ(source=Epg)は独自のライフサイクル管理があるため対象外。
    /// 録画中(Recording)・完了(Completed)・失敗(Failed)・キャンセル済(Cancelled)は一切触らない。
    /// 返り値: 移行した件数。
    /// </summary>
    public int ExpirePastScheduledReservations()
    {
        var now = DateTime.Now;
        var candidates = GetAll()
            .Where(r => r.Status == ReservationStatus.Scheduled && r.Source != ReservationSource.Epg && !r.RecordingStartedAt.HasValue && r.EndTime < now)
            .OrderBy(r => r.EndTime)
            .ToList();

        foreach (var r in candidates)
        {
            log.Add("RESERVATION_AUDIT", "EXPIRE_CANDIDATE",
                $"service={TrimForAudit(r.ServiceName)} title={TrimTitleForAudit(r.Title)} rawTitleBlank={RawTitleBlankForAudit(r.Title)} id=R{r.Id} status={r.Status} source={r.Source} enabled={r.IsEnabled} conflicted={r.IsConflicted} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} tuner={SafeTuner(r.TunerName)} userChain={r.IsUserChain} chainPrev={(r.UserChainPreviousId.HasValue ? $"R{r.UserChainPreviousId.Value}" : "-")} reason=end_time_past_without_recording rule=v0.5.78_service_title_first_audit");
        }

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET status = 'cancelled', is_conflicted = 0, updated_at = $now
            WHERE status = 'scheduled'
              AND source != 'epg'
              AND (recording_started_at IS NULL OR recording_started_at = '')
              AND end_time < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$now",    now.ToString("O"));
        cmd.Parameters.AddWithValue("$cutoff", now.ToString("O"));
        var affected = cmd.ExecuteNonQuery();
        if (affected > 0)
            log.Add("RESERVATION_AUDIT", "EXPIRE_APPLY", $"affected={affected} reason=end_time_past_without_recording");
        return affected;
    }

    /// <summary>事前割り当てチューナー名を更新する。</summary>
    public void UpdateTunerName(int id, string tunerName)
    {
        var before = GetById(id);
        if (HasOpenRecordingRuntime(before) && string.IsNullOrWhiteSpace(tunerName))
        {
            log.Add("RESERVATION_STATUS_WRITE_GUARD", $"R{id}",
                $"result=SUPPRESSED field=TunerName attempted=blank kept={SafeTuner(before!.TunerName)} reason=open_recording_runtime source=UpdateTunerName " +
                $"status={before.Status} started={before.RecordingStartedAt:MM/dd HH:mm:ss} end={before.EndTime:MM/dd HH:mm:ss} service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} " +
                $"rule=v0.10.08_recording_status_write_contract");
            return;
        }
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET tuner_name = $v, updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$v",   tunerName);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id",  id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>録画開始時に実際に確保したチューナーを保存する。ログタブのチューナー表示はこの値を優先する。</summary>
    public void MarkRecordingStarted(int id, string actualTunerName)
    {
        var actual = actualTunerName ?? string.Empty;
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET actual_tuner_name = $tuner,
                recording_started_at = CASE WHEN recording_started_at = '' THEN $now ELSE recording_started_at END,
                updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$tuner", actual);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();

        log.Add("RESERVATION_AUDIT", "ACTUAL_TUNER",
            $"id=R{id} actualTuner={SafeTuner(actual)} action=recording_started_actual_tuner_only rule=v0.5.78_audit_tag");
    }

    /// <summary>録画終了実績を保存する。未録画の予約取消とは区別する。</summary>
    public void MarkRecordingFinished(int id)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET recording_finished_at = CASE WHEN recording_finished_at = '' THEN $now ELSE recording_finished_at END,
                updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// EPG取得スケジュールエントリを登録・更新する。
    /// グループ（GR/BSCS）ごとに1エントリ。既存エントリがあれば日付を更新。
    /// 毎日の定時EPG取得を予約リストに表示し競合評価に含めるために使用する。
    /// </summary>
    public void UpsertEpgScheduleEntry(string group, DateTime start, DateTime end, string title, string tunerName = "")
    {
        using var con = db.Open();
        using var sel = con.CreateCommand();
        sel.CommandText = """
            SELECT id FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND title = $title
            LIMIT 1;
            """;
        sel.Parameters.AddWithValue("$title", title);
        var existingId = sel.ExecuteScalar();

        var now = DateTime.Now.ToString("O");
        if (existingId is not null)
        {
            using var upd = con.CreateCommand();
            upd.CommandText = """
                UPDATE reservations
                SET start_time = $start, end_time = $end, tuner_name = $tuner, updated_at = $now
                WHERE id = $id;
                """;
            upd.Parameters.AddWithValue("$start", start.ToString("O"));
            upd.Parameters.AddWithValue("$end",   end.ToString("O"));
            upd.Parameters.AddWithValue("$tuner", tunerName);
            upd.Parameters.AddWithValue("$now",   now);
            upd.Parameters.AddWithValue("$id",    existingId);
            upd.ExecuteNonQuery();
        }
        else
        {
            using var ins = con.CreateCommand();
            ins.CommandText = """
                INSERT INTO reservations
                  (network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source,
                   channel_argument, is_conflicted, is_enabled, tuner_name, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name, created_at, updated_at)
                VALUES
                  (0, 0, 0, 0,
                   $title, $start, $end, 'scheduled', 'epg',
                   '', 0, 1, $tuner, '',
                   '', NULL, '', $now, $now);
                """;
            ins.Parameters.AddWithValue("$title", title);
            ins.Parameters.AddWithValue("$start", start.ToString("O"));
            ins.Parameters.AddWithValue("$end",   end.ToString("O"));
            ins.Parameters.AddWithValue("$tuner", tunerName);
            ins.Parameters.AddWithValue("$now",   now);
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>通常EPG取得完了時に、定時EPG取得エントリだけをcompletedに変更する。</summary>
    public void CompleteEpgScheduleEntries()
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET status = 'completed', updated_at = $now
            WHERE source = 'epg' AND status = 'scheduled'
              AND title LIKE 'EPG取得%';
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// scheduledのEPGエントリを全削除する。
    /// EPG設定がOFFの場合や初期化時に呼ぶ。
    /// </summary>
    public int DeleteAllEpgEntries()
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            DELETE FROM reservations
            WHERE source = 'epg' AND status = 'scheduled';
            """;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 指定チューナーに紐づくscheduled状態のEPG系エントリを削除する。
    /// 視聴用チューナーをEPG/録画前確認から完全保護する際に使用する。
    /// </summary>
    public int DeleteScheduledEpgEntriesForTuner(string tunerName)
    {
        if (string.IsNullOrWhiteSpace(tunerName)) return 0;
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            DELETE FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND tuner_name = $tuner;
            """;
        cmd.Parameters.AddWithValue("$tuner", tunerName);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 指定親予約に紐づくscheduled状態の録画前EPG確認エントリを削除する。
    /// 期限切れの後追いEPG確認がWake/録画開始を汚さないようにする。
    /// </summary>
    public int DeleteScheduledPreRecordEpgEntriesForParent(int parentReservationId)
    {
        if (parentReservationId <= 0) return 0;
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            DELETE FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND source_rule_id = $parent
              AND (source_rule_name = 'PreRecEpg' OR title LIKE 'EPG確認%' OR title LIKE '録画前EPG確認%');
            """;
        cmd.Parameters.AddWithValue("$parent", parentReservationId);
        return cmd.ExecuteNonQuery();
    }


    public PreRecordEpgCleanupResult DeleteInvalidScheduledPreRecordEpgEntries()
    {
        using var con = db.Open();
        var targets = new List<(int ChildId, int? ParentId, string Reason)>();
        using (var sel = con.CreateCommand())
        {
            sel.CommandText = """
                SELECT child.id,
                       child.source_rule_id,
                       parent.id,
                       COALESCE(parent.status, ''),
                       COALESCE(parent.is_enabled, 0),
                       COALESCE(parent.is_conflicted, 0),
                       COALESCE(parent.is_user_chain, 0)
                FROM reservations child
                LEFT JOIN reservations parent ON parent.id = child.source_rule_id
                WHERE child.source = 'epg'
                  AND child.status = 'scheduled'
                  AND (child.source_rule_name = 'PreRecEpg' OR child.title LIKE 'EPG確認%' OR child.title LIKE '録画前EPG確認%');
                """;
            using var rdr = sel.ExecuteReader();
            while (rdr.Read())
            {
                var childId = rdr.GetInt32(0);
                int? parentId = rdr.IsDBNull(1) ? null : rdr.GetInt32(1);
                var parentExists = !rdr.IsDBNull(2);
                var status = rdr.GetString(3);
                var enabled = rdr.GetInt32(4) != 0;
                var conflicted = rdr.GetInt32(5) != 0;
                var userChain = rdr.GetInt32(6) != 0;
                var reason = string.Empty;
                if (!parentExists) reason = "parent_missing";
                else if (!enabled) reason = "parent_disabled";
                else if (conflicted) reason = "parent_conflicted";
                else if (userChain) reason = "parent_user_chain";
                else if (!string.Equals(status, "scheduled", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(status, "recording", StringComparison.OrdinalIgnoreCase)) reason = "parent_terminal";
                if (!string.IsNullOrWhiteSpace(reason)) targets.Add((childId, parentId, reason));
            }
        }

        if (targets.Count == 0)
            return new PreRecordEpgCleanupResult(0, 0, 0, 0, 0, 0, "-");

        using (var tx = con.BeginTransaction())
        {
            foreach (var target in targets)
            {
                using var del = con.CreateCommand();
                del.Transaction = tx;
                del.CommandText = """
                    DELETE FROM reservations
                    WHERE id = $id
                      AND source = 'epg'
                      AND status = 'scheduled'
                      AND (source_rule_name = 'PreRecEpg' OR title LIKE 'EPG確認%' OR title LIKE '録画前EPG確認%');
                    """;
                del.Parameters.AddWithValue("$id", target.ChildId);
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }

        int Count(string reason) => targets.Count(x => string.Equals(x.Reason, reason, StringComparison.OrdinalIgnoreCase));
        var parents = string.Join(',', targets
            .Select(x => x.ParentId.HasValue ? $"R{x.ParentId.Value}" : "parent_missing")
            .Distinct()
            .Take(20));
        if (string.IsNullOrWhiteSpace(parents)) parents = "-";
        return new PreRecordEpgCleanupResult(
            targets.Count,
            Count("parent_missing"),
            Count("parent_disabled"),
            Count("parent_terminal"),
            Count("parent_conflicted"),
            Count("parent_user_chain"),
            parents);
    }

    /// <summary>
    /// 完了・失敗・キャンセルから7日以上経過した予約レコードを削除する。
    /// 起動時に1回実行することを想定。
    /// </summary>
    public int PurgeOldLogEntries()
    {
        var cutoff = DateTime.Now.AddDays(-7).ToString("O");
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            DELETE FROM reservations
            WHERE status IN ('completed', 'failed', 'cancelled')
              AND updated_at <> '' AND updated_at < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    private static bool IsExactContinuousPair(Reservation predecessor, Reservation successor)
    {
        // v0.9.67: チェーン録画の対象は「同一局=同一SID」の同一時刻またぎ連続番組のみ。
        // チャンネル引数や局名のフォールバック一致は、同一TS内別SIDやサブチャンネル跨ぎを誤って
        // チェーン扱いする余地があるため、共通割り当てルートでも認めない。
        var sameServiceByIds = predecessor.ServiceId != 0 && successor.ServiceId != 0
            && predecessor.NetworkId == successor.NetworkId
            && predecessor.TransportStreamId == successor.TransportStreamId
            && predecessor.ServiceId == successor.ServiceId;

        if (!sameServiceByIds)
            return false;

        // 番組表上は明らかに連続しているのに、EPG側の境界が数十秒ずれるケースを吸収する。
        // occupy ではなく番組本来の start/end を使い、同一SIDの直列番組だけを緩やかに連続扱いする。
        return ChainReservationContract.IsAdjacent(predecessor.EndTime, successor.StartTime);
    }

    private static Dictionary<int, int> BuildPotentialChainPredecessors(IEnumerable<Reservation> reservations)
{
    // v0.2.34:
    // 以前は「同一サービスで終了時刻==次番組開始時刻」なら自動でチェーン候補にしていた。
    // しかし (C) はユーザーが番組後半欠落を許容して明示的にチェーン予約した場合だけ表示・適用する。
    // そのため、自動検索/通常予約の連続番組からチェーンを自動生成しない。
    var activeIds = reservations
        .Where(r => r.Source != ReservationSource.Epg && r.IsEnabled)
        .Select(r => r.Id)
        .ToHashSet();

    var result = new Dictionary<int, int>();
    foreach (var r in reservations.Where(r => r.IsUserChain && r.UserChainPreviousId.HasValue))
    {
        var predId = r.UserChainPreviousId!.Value;
        if (activeIds.Contains(predId))
            result[r.Id] = predId;
    }

    return result;
}

    /// <summary>
    /// 実際に同一チューナーで引き継がれる連続番組ペアを返す。
    /// 条件: 同一 service（network_id / ts_id / service_id 完全一致）かつ
    ///       前番組 EndTime と後番組 StartTime がほぼ連続（-5秒〜+90秒）かつ
    ///       同一 TunerName が確定していること。
    /// key=後続ID, value=前番組ID
    /// </summary>
    public Dictionary<int, int> GetChainPredecessors()
{
    // v0.2.34:
    // 予約リストの (C) 表示、およびチェーン監査の元データは
    // DB上の明示チェーン(is_user_chain/user_chain_previous_id)だけを使う。
    // 同一チューナー・連続時間からの動的チェーン化は禁止。
    var active = GetByStatus(ReservationStatus.Scheduled)
        .Concat(GetByStatus(ReservationStatus.Recording))
        .Where(r => r.Source != ReservationSource.Epg && r.IsEnabled)
        .ToList();

    var activeIds = active.Select(r => r.Id).ToHashSet();
    var result = new Dictionary<int, int>();

    foreach (var forced in active.Where(r => r.IsUserChain && r.UserChainPreviousId.HasValue))
    {
        var predId = forced.UserChainPreviousId!.Value;
        if (activeIds.Contains(predId))
            result[forced.Id] = predId;
    }

    return result;
}

    /// <summary>
    /// チェーン全体を列挙する。ログ出力用。
    /// 戻り値: チェーンの先頭から末尾まで順番に並べた予約IDリストのリスト。
    /// 例: [[R1, R2, R3], [R5, R6]] のように複数チェーンが存在しうる。
    /// </summary>
    public List<List<int>> GetChains()
    {
        var predecessors = GetChainPredecessors();
        // predecessors: key=後続ID, value=前番組ID
        // 後続でないID（先頭）を見つけてチェーンを構築する
        var leaderIds    = predecessors.Values.Where(v => !predecessors.ContainsKey(v)).Distinct();

        // successorOf: 前番組ID → 後続番組ID（ループ外で1回だけ生成）
        var successorOf = predecessors.ToDictionary(kv => kv.Value, kv => kv.Key);

        var chains = new List<List<int>>();
        foreach (var leaderId in leaderIds)
        {
            var chain = new List<int> { leaderId };
            var current = leaderId;
            while (successorOf.TryGetValue(current, out var next))
            {
                chain.Add(next);
                current = next;
            }
            chains.Add(chain);
        }
        return chains;
    }


    /// <summary>
    /// 録画前EPG確認の同一親予約・同一イベント・同一時間窓の再生成を抑止する。
    /// v0.9.99: Completed済みのPreRecEpgも有効な実行済み証跡として扱い、
    /// 録画開始直前の再評価で同じ確認をもう一度作らない。
    /// </summary>
    public bool TryFindReusablePreRecordEpgEntry(
        int parentReservationId,
        string tunerName,
        DateTime start,
        DateTime end,
        ushort eventId,
        out int existingId,
        out ReservationStatus existingStatus,
        out string existingTunerName)
    {
        existingId = 0;
        existingStatus = ReservationStatus.Scheduled;
        existingTunerName = string.Empty;
        if (parentReservationId <= 0)
            return false;

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, status, COALESCE(tuner_name, '') FROM reservations
            WHERE source = 'epg'
              AND status IN ('scheduled', 'recording', 'completed')
              AND source_rule_id = $parent
              AND (source_rule_name = 'PreRecEpg' OR title LIKE 'EPG確認%' OR title LIKE '録画前EPG確認%')
              AND event_id = $eventId
              AND start_time = $start
              AND end_time = $end
            ORDER BY
              CASE status
                WHEN 'recording' THEN 0
                WHEN 'scheduled' THEN 1
                WHEN 'completed' THEN 2
                ELSE 9
              END,
              CASE WHEN tuner_name = $tuner THEN 0 ELSE 1 END,
              id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$tuner", tunerName ?? string.Empty);
        cmd.Parameters.AddWithValue("$parent", parentReservationId);
        cmd.Parameters.AddWithValue("$eventId", eventId);
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));

        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read())
            return false;

        existingId = rdr.GetInt32(0);
        existingStatus = ParseStatus(rdr.GetString(1));
        existingTunerName = rdr.GetString(2);
        return true;
    }

    public void RebindScheduledPreRecordEpgEntry(int epgReservationId, int parentReservationId, DateTime start, DateTime end, string tunerName)
    {
        if (epgReservationId <= 0 || parentReservationId <= 0 || string.IsNullOrWhiteSpace(tunerName))
            return;

        var parent = GetById(parentReservationId);
        var title = BuildPreRecordEpgStorageTitle(parent, tunerName);
        var now = DateTime.Now.ToString("O");

        using var con = db.Open();
        using var upd = con.CreateCommand();
        upd.CommandText = """
            UPDATE reservations
            SET title = $title, start_time = $start, end_time = $end,
                tuner_name = $tuner, source_rule_id = $parent,
                network_id = $nid, transport_stream_id = $tsid, service_id = $sid, event_id = $eventId,
                channel_argument = $channelArg, service_name = $serviceName,
                source_rule_name = $sourceRuleName,
                updated_at = $now
            WHERE id = $id
              AND source = 'epg'
              AND status = 'scheduled'
              AND source_rule_id = $parent
              AND (source_rule_name = 'PreRecEpg' OR title LIKE 'EPG確認%' OR title LIKE '録画前EPG確認%');
            """;
        BindPreRecordEpgParameters(upd, title, start, end, tunerName, parentReservationId, parent, now);
        upd.Parameters.AddWithValue("$id", epgReservationId);
        upd.ExecuteNonQuery();

        DeleteDuplicateScheduledPreRecordEpgEntries(con, epgReservationId, parentReservationId, parent?.EventId ?? 0, start, end);
    }

    /// <summary>
    /// 録画直前EPG確認エントリを登録・更新する。
    /// チューナー名で一意に管理し、定時EPGエントリと差し替える形で登録する。
    /// v0.11.197: Title は内部用途名に固定する。親番組タイトルは source_rule_id から辿るメタ属性であり、子予約Titleへ入れない。
    /// </summary>
    public void UpsertPreRecordEpgEntry(int reservationId, string group, DateTime start, DateTime end, string tunerName = "")
    {
        var parent = GetById(reservationId);
        var title = BuildPreRecordEpgStorageTitle(parent, tunerName);
        using var con = db.Open();

        // v34.06: 定時EPG取得は表示上優先できるよう残す。
        // 直前EPG確認を作る際に定時EPG取得を物理削除しない。
        // v0.6.29: 直前EPG確認を実行ルートへ載せるため、親予約IDと局同定情報を保持する。

        using var sel = con.CreateCommand();
        sel.CommandText = """
            SELECT id FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND source_rule_id = $parent
              AND (source_rule_name = 'PreRecEpg' OR title LIKE 'EPG確認%' OR title LIKE '録画前EPG確認%')
              AND event_id = $eventId
              AND start_time = $start
              AND end_time = $end
            ORDER BY CASE WHEN tuner_name = $tuner THEN 0 ELSE 1 END, id DESC
            LIMIT 1;
            """;
        sel.Parameters.AddWithValue("$parent", reservationId);
        sel.Parameters.AddWithValue("$eventId", parent?.EventId ?? 0);
        sel.Parameters.AddWithValue("$start", start.ToString("O"));
        sel.Parameters.AddWithValue("$end", end.ToString("O"));
        sel.Parameters.AddWithValue("$tuner", tunerName);
        var existingId = sel.ExecuteScalar();

        if (existingId is null)
        {
            using var tunerSel = con.CreateCommand();
            tunerSel.CommandText = """
                SELECT id FROM reservations
                WHERE source = 'epg' AND status = 'scheduled'
                  AND tuner_name = $tuner AND (source_rule_name = 'PreRecEpg' OR title LIKE 'EPG確認%' OR title LIKE '録画前EPG確認%')
                LIMIT 1;
                """;
            tunerSel.Parameters.AddWithValue("$tuner", tunerName);
            existingId = tunerSel.ExecuteScalar();
        }

        var now = DateTime.Now.ToString("O");
        if (existingId is not null)
        {
            using var upd = con.CreateCommand();
            upd.CommandText = """
                UPDATE reservations
                SET title = $title, start_time = $start, end_time = $end,
                    tuner_name = $tuner, source_rule_id = $parent,
                    network_id = $nid, transport_stream_id = $tsid, service_id = $sid, event_id = $eventId,
                    channel_argument = $channelArg, service_name = $serviceName,
                    source_rule_name = $sourceRuleName,
                    updated_at = $now
                WHERE id = $id;
                """;
            BindPreRecordEpgParameters(upd, title, start, end, tunerName, reservationId, parent, now);
            upd.Parameters.AddWithValue("$id", existingId);
            upd.ExecuteNonQuery();
            DeleteDuplicateScheduledPreRecordEpgEntries(con, Convert.ToInt32(existingId), reservationId, parent?.EventId ?? 0, start, end);
        }
        else
        {
            using var ins = con.CreateCommand();
            ins.CommandText = """
                INSERT INTO reservations
                  (network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source,
                   channel_argument, is_conflicted, is_enabled, tuner_name, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name, created_at, updated_at)
                VALUES
                  ($nid, $tsid, $sid, $eventId,
                   $title, $start, $end, 'scheduled', 'epg',
                   $channelArg, 0, 1, $tuner, $serviceName,
                   '', $parent, $sourceRuleName, $now, $now);
                """;
            BindPreRecordEpgParameters(ins, title, start, end, tunerName, reservationId, parent, now);
            ins.ExecuteNonQuery();
            using var last = con.CreateCommand();
            last.CommandText = "SELECT last_insert_rowid();";
            var insertedId = Convert.ToInt32(last.ExecuteScalar());
            DeleteDuplicateScheduledPreRecordEpgEntries(con, insertedId, reservationId, parent?.EventId ?? 0, start, end);
        }
    }

    private static void DeleteDuplicateScheduledPreRecordEpgEntries(
        Microsoft.Data.Sqlite.SqliteConnection con,
        int keepId,
        int parentReservationId,
        ushort eventId,
        DateTime start,
        DateTime end)
    {
        using var del = con.CreateCommand();
        del.CommandText = """
            DELETE FROM reservations
            WHERE source = 'epg'
              AND status = 'scheduled'
              AND source_rule_id = $parent
              AND (source_rule_name = 'PreRecEpg' OR title LIKE 'EPG確認%' OR title LIKE '録画前EPG確認%')
              AND event_id = $eventId
              AND start_time = $start
              AND end_time = $end
              AND id <> $keepId;
            """;
        del.Parameters.AddWithValue("$parent", parentReservationId);
        del.Parameters.AddWithValue("$eventId", eventId);
        del.Parameters.AddWithValue("$start", start.ToString("O"));
        del.Parameters.AddWithValue("$end", end.ToString("O"));
        del.Parameters.AddWithValue("$keepId", keepId);
        del.ExecuteNonQuery();
    }


    private static string BuildPreRecordEpgStorageTitle(Reservation? parent, string tunerName)
    {
        // v0.11.197:
        // PreRecEpg/SystemEpg は内部制御予約であり、予約一覧上の番組タイトルとして親番組名を名乗らせない。
        // 親番組名は parent/source_rule_id で辿れるメタ属性であり、内部予約の Title は用途名に固定する。
        var tuner = string.IsNullOrWhiteSpace(tunerName) ? string.Empty : tunerName.Trim();
        return string.IsNullOrWhiteSpace(tuner) ? "録画前EPG確認" : $"録画前EPG確認（{tuner}）";
    }

    private static void BindPreRecordEpgParameters(
        Microsoft.Data.Sqlite.SqliteCommand cmd,
        string title,
        DateTime start,
        DateTime end,
        string tunerName,
        int parentId,
        Reservation? parent,
        string now)
    {
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        cmd.Parameters.AddWithValue("$tuner", tunerName);
        cmd.Parameters.AddWithValue("$parent", parentId);
        cmd.Parameters.AddWithValue("$nid", parent?.NetworkId ?? 0);
        cmd.Parameters.AddWithValue("$tsid", parent?.TransportStreamId ?? 0);
        cmd.Parameters.AddWithValue("$sid", parent?.ServiceId ?? 0);
        cmd.Parameters.AddWithValue("$eventId", parent?.EventId ?? 0);
        cmd.Parameters.AddWithValue("$channelArg", parent?.ChannelArgument ?? "");
        cmd.Parameters.AddWithValue("$serviceName", parent?.ServiceName ?? "");
        cmd.Parameters.AddWithValue("$sourceRuleName", "PreRecEpg");
        cmd.Parameters.AddWithValue("$now", now);
    }

    /// <summary>
    /// EPG確認エントリが不要になったチューナーに対して定時EPGエントリを復元する。
    /// 同グループの他チューナーの定時EPG時刻を流用して再登録する。
    /// </summary>
    public int RestoreDailyEpgEntry(Core.TunerProfile tuner, IReadOnlyList<Core.TunerProfile> allTuners)
    {
        using var con = db.Open();

        // EPG確認エントリが存在しない場合は何もしない
        using var chk = con.CreateCommand();
        chk.CommandText = """
            SELECT COUNT(*) FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND tuner_name = $tuner AND (source_rule_name = 'PreRecEpg' OR title LIKE 'EPG確認%' OR title LIKE '録画前EPG確認%');
            """;
        chk.Parameters.AddWithValue("$tuner", tuner.Name);
        if (Convert.ToInt32(chk.ExecuteScalar()) == 0) return 0;

        // 同グループの他チューナーの定時EPGエントリから時刻を取得
        var otherTuner = allTuners.FirstOrDefault(t =>
            string.Equals(t.Group, tuner.Group, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(t.Name, tuner.Name, StringComparison.OrdinalIgnoreCase));
        if (otherTuner == null) return 0;

        using var sel = con.CreateCommand();
        sel.CommandText = """
            SELECT start_time, end_time FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND tuner_name = $tuner AND title LIKE 'EPG取得%'
            LIMIT 1;
            """;
        sel.Parameters.AddWithValue("$tuner", otherTuner.Name);
        using var rdr = sel.ExecuteReader();
        if (!rdr.Read()) return 0;
        var start = DateTime.Parse(rdr.GetString(0));
        var end   = DateTime.Parse(rdr.GetString(1));
        rdr.Close();

        // EPG確認エントリを削除して定時EPGエントリを復元
        using var del = con.CreateCommand();
        del.CommandText = """
            DELETE FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND tuner_name = $tuner AND (source_rule_name = 'PreRecEpg' OR title LIKE 'EPG確認%' OR title LIKE '録画前EPG確認%');
            """;
        del.Parameters.AddWithValue("$tuner", tuner.Name);
        var deleted = del.ExecuteNonQuery();

        UpsertEpgScheduleEntry(tuner.Group.ToUpperInvariant(), start, end,
            $"EPG取得（{tuner.Name}）", tuner.Name);
        return deleted > 0 ? deleted : 1;
    }

    /// <summary>
    /// EPG取得完了後に時間追従を適用する。
    /// 対象予約のservice_id+event_idでEPGを引き直し、
    /// start_time/end_timeが変化していれば更新する。
    /// scheduled_start_timeは変更しない（元の予約時刻を保持）。
    /// </summary>
    /// <returns>更新した予約のID一覧</returns>
    public IReadOnlyList<int> ApplyTimeFollowing(
        IEnumerable<Reservation> targets,
        Epg.EpgStore epgStore)
        => ApplyTimeFollowingDetailed(targets, epgStore)
            .Where(r => r.Updated)
            .Select(r => r.ReservationId)
            .ToList();

    /// <summary>
    /// 時間追従の詳細適用。
    /// v0.6.28: 完成確認用に、更新/未更新/EPG未検出/保護スキップを呼び出し側で監査できる形で返す。
    /// </summary>
    public IReadOnlyList<TimeFollowApplyResult> ApplyTimeFollowingDetailed(
        IEnumerable<Reservation> targets,
        Epg.EpgStore epgStore)
    {
        var results = new List<TimeFollowApplyResult>();
        foreach (var r in targets)
        {
            if (r.Source == ReservationSource.Epg)
                continue;

            // v0.9.70: ユーザー明示チェーン後続は個別の時間追従で開始/終了時刻をずらさない。
            // 連続一挙放送では chainRoot/chainPrev による連続契約が優先であり、
            // 後続1本だけのEPG確認結果で gap を作るとチェーン全体を壊す。
            if (r.IsUserChain)
            {
                results.Add(BuildTimeFollowResult(r, false, "SKIP_USER_CHAIN_CONTRACT", null));
                continue;
            }

            if (r.Status != ReservationStatus.Scheduled)
            {
                results.Add(BuildTimeFollowResult(r, false, $"SKIP_STATUS_{r.Status}", null));
                continue;
            }

            if (!r.IsEnabled)
            {
                results.Add(BuildTimeFollowResult(r, false, "SKIP_DISABLED", null));
                continue;
            }

            var wasConflicted = r.IsConflicted;

            // v0.11.378:
            // 競合中の予約も EventIdentity を持つ限り EIT 更新追従の対象にする。
            // ここで SKIP_CONFLICT すると、EPG側で時刻変更されて競合が解消可能になっても、
            // 古い時刻のまま共通割り当てルートへ戻り、競合が固定化する。
            // disabled/user-chain/system EPG は従来どおり保護し、event_id=0 の TimeIdentity は追従しない。
            if (r.ServiceId == 0 || r.EventId == 0)
            {
                results.Add(BuildTimeFollowResult(r, false, "NO_SERVICE_OR_EVENT_ID", null));
                continue;
            }

            var ev = epgStore.GetOne(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId);
            if (ev is null)
            {
                results.Add(BuildTimeFollowResult(r, false, "EPG_EVENT_NOT_FOUND", null));
                continue;
            }

            if (ev.End <= ev.Start)
            {
                results.Add(BuildTimeFollowResult(r, false, "EPG_EVENT_INVALID_RANGE", ev));
                continue;
            }

            var startDelta = Math.Abs((ev.Start - r.StartTime).TotalSeconds);
            var endDelta   = Math.Abs((ev.End   - r.EndTime).TotalSeconds);
            var timeChanged = startDelta > TimeFollowThresholdSeconds || endDelta > TimeFollowThresholdSeconds;

            // v0.11.678: 録画前EPG/通常EPGの時刻追従で、既存予約タイトルを空タイトルへ戻さない。
            // EPGの生値が空であること自体は許容するが、予約表示メタデータの正本は
            // 「ユーザーが予約した時点の表示タイトル」または既に解決済みの予約タイトルを保持する。
            // ファイル名だけ RECORD_FILENAME_TITLE_GUARD で救う局所復旧ではなく、
            // FinalConflictPlan / ChainOccupancy / REC_DUE_SCAN / REC_START_REQUEST まで同じ予約タイトルを参照させる。
            var eventTitle = NormalizeReservationTitleForStorage(ev.Title, ev.ServiceName, r.Id);
            var currentTitle = NormalizeReservationTitleForStorage(r.Title, r.ServiceName, r.Id);
            var preserveExistingTitle = string.IsNullOrWhiteSpace(eventTitle) && !string.IsNullOrWhiteSpace(currentTitle);
            var effectiveTitle = preserveExistingTitle ? currentTitle : eventTitle;
            var titleChanged = !string.Equals(effectiveTitle, currentTitle, StringComparison.Ordinal);

            if (!timeChanged && !titleChanged)
            {
                results.Add(BuildTimeFollowResult(r, false,
                    preserveExistingTitle ? "UNCHANGED_TITLE_PRESERVED_EMPTY_EPG" : "UNCHANGED_WITHIN_THRESHOLD", ev));
                continue;
            }

            UpdateTitleStartEndTime(r.Id, effectiveTitle, ev.ServiceName, ev.Start, ev.End);
            var reason = preserveExistingTitle
                ? (wasConflicted ? "UPDATED_CONFLICT_TIME_TITLE_PRESERVED_EMPTY_EPG" : "UPDATED_TIME_TITLE_PRESERVED_EMPTY_EPG")
                : wasConflicted
                    ? (titleChanged && !timeChanged ? "UPDATED_CONFLICT_TITLE_ONLY" : "UPDATED_CONFLICT")
                    : (titleChanged && !timeChanged ? "UPDATED_TITLE_ONLY" : "UPDATED");
            results.Add(BuildTimeFollowResult(r, true, reason, ev, effectiveTitle));
        }
        return results;
    }

    private static TimeFollowApplyResult BuildTimeFollowResult(Reservation r, bool updated, string reason, EpgEvent? ev, string? effectiveTitle = null)
        => new(
            r.Id,
            r.ServiceName,
            effectiveTitle ?? r.Title,
            updated,
            reason,
            r.StartTime,
            r.EndTime,
            ev?.Start,
            ev?.End,
            r.NetworkId,
            r.TransportStreamId,
            r.ServiceId,
            r.EventId);

    /// <summary>
    /// scheduled状態の全予約についてチューナーを事前割り当てし、競合フラグを再評価する。
    ///
    /// 【割り当てアルゴリズム】
    /// 1. 予約を開始時刻昇順でソート（時系列順）
    /// 2. 各予約に対して「空きチューナー」を探す
    ///    - 空き = チューナーの現在の終了予定 <= この予約の開始時刻
    /// 3. 空きチューナーが複数ある場合、後番組優先なら「終了予定が最も遅い（直前まで使われていた）」
    ///    チューナーを選ぶ（連続利用で効率化）。前番組優先なら「最も早く空く」チューナーを選ぶ。
    /// 4. 同時刻に複数の予約が競合してチューナーが不足する場合：
    ///    - 後番組優先: 開始時刻が遅い方（後番組）を優先 → 前番組を競合扱い
    ///    - 前番組優先: 開始時刻が早い方（前番組）を優先 → 後番組を競合扱い
    ///    同時刻の場合は登録ID（古い予約）が優先
    /// </summary>
    public IReadOnlyList<(int Id, string ServiceName, string Title, bool Conflicted)> ReevaluateConflicts(
        IReadOnlyList<Core.TunerProfile> tunerProfiles, bool laterProgramPriority,
        bool pseudoContinuous = false, int postEndMarginSeconds = 0,
        IReadOnlyList<Tuner.TunerSlotStatus>? tunerSlots = null,
        int preStartMarginSeconds = 30)
    {
        var preMargin  = TimeSpan.FromSeconds(preStartMarginSeconds);
        var postMargin = TimeSpan.FromSeconds(postEndMarginSeconds);
        var conflictPolicy = laterProgramPriority
            ? "PreferLaterProgram"   // 後番組優先: 後から始まる番組を尊重し、重なる前番組を落とす
            : "PreferEarlierProgram"; // 前番組優先: 前から始まっている番組を尊重し、後番組を落とす


        // 視聴用チューナーは録画・EPG取得の割当対象から外す。
        // 録画割り当て・競合評価・候補チューナー数から除外する。
        var recordingTunerProfiles = tunerProfiles
            .Where(p => !string.Equals(IniSettingsService.NormalizeTunerRole(p.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // グループ判定
        static string ResolveGroup(Reservation r)
        {
            if (!string.IsNullOrWhiteSpace(r.ChannelArgument))
                return r.ChannelArgument.Contains("/chspace", StringComparison.OrdinalIgnoreCase)
                    ? "BSCS" : "GR";

            var hint = $"{r.TunerName} {r.Title} {r.ServiceName}";
            if (!string.IsNullOrWhiteSpace(hint))
            {
                if (hint.Contains("BS/CS", StringComparison.OrdinalIgnoreCase)
                    || hint.Contains("ＢＳ", StringComparison.Ordinal)
                    || hint.Contains("ＣＳ", StringComparison.Ordinal))
                {
                    return "BSCS";
                }

                if (hint.Contains("地上波", StringComparison.OrdinalIgnoreCase))
                {
                    return "GR";
                }
            }

            return r.NetworkId == 4 ? "BSCS" : "GR";
        }

        static bool SupportsReservationGroup(string tunerGroup, string reservationGroup)
        {
            var tg = (tunerGroup ?? string.Empty).Trim().ToUpperInvariant();
            var rg = (reservationGroup ?? string.Empty).Trim().ToUpperInvariant();
            if (tg == "HYBRID") return rg is "GR" or "BSCS" or "HYBRID";
            return tg == rg;
        }

        static string CanonicalTunerDisplayName(TunerProfile profile)
        {
            return (profile.Name ?? string.Empty).Trim();
        }

        // v0.11.678:
        // 予約競合/最終割当ログのチューナー候補名は、RoleBinding の LogicalTunerDisplayName を正本にする。
        // Recording role だけを group 別 capacity として使い、表示名を T1/S1 などに再採番しない。
        // 再採番すると Viewing role を含む設定で「T2/T3/T4」が「T1/T2/T3」に見えるため、
        // EPG側の GroupEpgCapacity と予約競合側の RoleBinding 表示が横串でズレる。
        var roleBindingRecordingTunerNamesByGroup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in new[] { "GR", "BSCS" })
        {
            roleBindingRecordingTunerNamesByGroup[g] = recordingTunerProfiles
                .Where(p => SupportsReservationGroup(p.Group, g))
                .Select(CanonicalTunerDisplayName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var roleBindingRecordingTunerNameSet = roleBindingRecordingTunerNamesByGroup.Values
            .SelectMany(x => x)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string ToRoleBindingTunerName(string? tunerName, string group)
        {
            var value = (tunerName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            if (roleBindingRecordingTunerNameSet.Contains(value))
                return value;

            // 旧予約などが保持する名前が現在の RoleBinding に無い場合、推測変換せず原値を残す。
            // ここで再採番・補正すると、設定正本とログ/割当表示の横串が再び崩れる。
            return value;
        }

        // 評価対象：scheduledのユーザー予約のみ
        var allScheduled = GetByStatus(ReservationStatus.Scheduled).ToList();
        var scheduled    = allScheduled
            .Where(r => r.Status == ReservationStatus.Scheduled)
            .Where(r => r.Source != ReservationSource.Epg)
            .Where(r => r.IsEnabled)
            .ToList();

        // v0.11.378: 空欄ProgramRuleと同一局・同一時刻の正規イベント予約が存在する場合のみ、空欄側を自動削除する。
        // UI説明は増やさない。削除判定と実処理は共通割り当てルート内に閉じる。
        AutoDeleteResolvedProgramGuideMissingDuplicates(allScheduled, scheduled);

        // 録画中の前番組もチェーン継続元として扱う。
        // ここを scheduled だけで見ると、前番組が Recording に移行した瞬間に
        // 後続チェーンが通常予約へ戻り、境界マージン重複で競合化する。
        var recordingReservations = GetByStatus(ReservationStatus.Recording)
            .Where(r => r.Source != ReservationSource.Epg)
            .Where(r => r.IsEnabled)
            .ToList();
        var recordingReservationIds = recordingReservations.Select(r => r.Id).ToHashSet();

        // チェーンhandoff直後は、前番組が既にCompletedへ移行しているため
        // scheduled + recording だけを見ると UserChainPreviousId の参照先が評価対象外になり、
        // 後続チェーンの TunerName が空に戻って競合化する。
        // そのため、明示チェーンの直前予約は Completed でも「チェーンアンカー」として扱い、
        // 直前予約の実チューナーを後続へ継承できるようにする。
        var completedChainAnchorById = new Dictionary<int, Reservation>();
        foreach (var forced in scheduled.Where(r => r.IsUserChain && r.UserChainPreviousId.HasValue))
        {
            var pred = GetById(forced.UserChainPreviousId!.Value);
            if (pred != null
                && pred.Source != ReservationSource.Epg
                && pred.IsEnabled
                && pred.Status == ReservationStatus.Completed
                && !string.IsNullOrWhiteSpace(EffectiveTunerName(pred))
                && IsExactContinuousPair(pred, forced))
            {
                completedChainAnchorById[pred.Id] = pred;
            }
        }
        var completedChainAnchorIds = completedChainAnchorById.Keys.ToHashSet();

        // v0.4.9: ユーザー明示チェーンは「物理DID固定」ではなく、
        // 予約時点の LogicalTunerDisplayName を優先候補として扱う。
        // 実DIDは録画開始時にTunerPoolが空き状況・外部視聴ガードを見て決定する。
        // v0.9.67: チェーンは「後番組優先ON＋チェーン録画ON」を利用条件にしたうえで、
        // ユーザー明示の同一SID連続予約だけを共通割り当てルートで有効化する。
        var configuredPseudoContinuous = pseudoContinuous;
        var chainModeEnabled = laterProgramPriority && configuredPseudoContinuous;

        var userChainLockedTunerById = new Dictionary<int, string>();
        foreach (var chainReservation in scheduled.Where(x => chainModeEnabled && x.IsUserChain && x.UserChainPreviousId.HasValue))
        {
            var predForContract = GetById(chainReservation.UserChainPreviousId!.Value);
            if (predForContract == null
                || predForContract.Status == ReservationStatus.Cancelled
                || !IsExactContinuousPair(predForContract, chainReservation))
            {
                var predecessorLabel = predForContract == null ? "-" : "R" + predForContract.Id;
                var sameSid = predForContract != null
                    && predForContract.NetworkId == chainReservation.NetworkId
                    && predForContract.TransportStreamId == chainReservation.TransportStreamId
                    && predForContract.ServiceId == chainReservation.ServiceId;
                var gapText = predForContract == null
                    ? "-"
                    : ((int)Math.Round((chainReservation.StartTime - predForContract.EndTime).TotalSeconds)).ToString();
                log.Add("CHAIN_CONTRACT_AUDIT", $"R{chainReservation.Id}",
                    $"result=IGNORED_AT_ALLOC_ROUTE reason=invalid_user_chain_contract predecessor={predecessorLabel} sameSid={sameSid} gapSec={gapText} rule=v0.9.67_user_chain_contract_gate");
                continue;
            }

            var lockedTuner = ToRoleBindingTunerName(EffectiveTunerName(chainReservation), ResolveGroup(chainReservation));
            if (string.IsNullOrWhiteSpace(lockedTuner) || lockedTuner == "-")
                lockedTuner = ToRoleBindingTunerName(EffectiveTunerName(predForContract), ResolveGroup(chainReservation));

            if (!string.IsNullOrWhiteSpace(lockedTuner) && lockedTuner != "-")
                userChainLockedTunerById[chainReservation.Id] = lockedTuner;
        }

        var userChainCandidatePairs = scheduled.Count(r => r.IsUserChain && r.UserChainPreviousId.HasValue);
        var hasUserForcedChain = chainModeEnabled && userChainCandidatePairs > 0;
        pseudoContinuous = chainModeEnabled && hasUserForcedChain;

        // グループ別チューナー本数（地/BS/CS は GR/BSCS の両方の候補に含める）
        var tunerCountByGroup = scheduled
            .Select(ResolveGroup)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g,
                g => recordingTunerProfiles.Count(p => SupportsReservationGroup(p.Group, g)),
                StringComparer.OrdinalIgnoreCase);

        // チェーン予約ペアの競合除外（pseudoContinuous=trueの場合のみ）
        // 前番組と後番組は同一チューナーで引き継ぐため、後番組は前番組の占有区間と重複しない扱いにする。
        // 具体的には: チェーンの後続番組は「前番組が使うチューナー」を予約済みとして扱い、
        // 前番組の占有終了タイミングで引き継ぐため競合カウントから外す。
        // 実装上はチェーンペアのIDセットを持ち、後続番組の占有区間を前番組の占有終了に合わせて調整する。
        var chainPredecessors  = new Dictionary<int, int>(); // key=後続ID, value=前番組ID
        var chainSuccessors    = new Dictionary<int, int>(); // key=前番組ID, value=後続ID
        if (pseudoContinuous)
        {
            // v0.3.33: チェーンは「候補表示」と「確定予約」を分離する。
            // 共通割り当てルートでは、ユーザーが明示した UserChain のみを
            // チェーンとして扱う。通常予約・自動検索・EPG確認は、同一局連続でも
            // 勝手にチェーン化しない。
            foreach (var forced in scheduled.Where(r => r.IsUserChain && r.UserChainPreviousId.HasValue))
            {
                var predId = forced.UserChainPreviousId!.Value;
                var pred = allScheduled.FirstOrDefault(x => x.Id == predId)
                    ?? recordingReservations.FirstOrDefault(x => x.Id == predId)
                    ?? (completedChainAnchorById.TryGetValue(predId, out var completedPred) ? completedPred : null);

                if (pred == null)
                {
                    log.Add("CHAIN_CONTRACT_AUDIT", $"R{forced.Id}",
                        $"result=IGNORED_AT_ALLOC_ROUTE reason=predecessor_missing predecessor=R{predId} rule=v0.9.67_user_chain_contract_gate");
                    continue;
                }

                if (pred.Status == ReservationStatus.Cancelled)
                {
                    log.Add("CHAIN_CONTRACT_AUDIT", $"R{forced.Id}",
                        $"result=IGNORED_AT_ALLOC_ROUTE reason=predecessor_cancelled predecessor=R{predId} rule=v0.9.67_user_chain_contract_gate");
                    continue;
                }

                if (!IsExactContinuousPair(pred, forced))
                {
                    log.Add("CHAIN_CONTRACT_AUDIT", $"R{forced.Id}",
                        $"result=IGNORED_AT_ALLOC_ROUTE reason=not_same_sid_adjacent predecessor=R{predId} prevNid={pred.NetworkId} prevTsid={pred.TransportStreamId} prevSid={pred.ServiceId} nextNid={forced.NetworkId} nextTsid={forced.TransportStreamId} nextSid={forced.ServiceId} gapSec={(int)Math.Round((forced.StartTime - pred.EndTime).TotalSeconds)} rule=v0.9.67_user_chain_contract_gate");
                    continue;
                }

                chainPredecessors[forced.Id] = predId;
            }
            chainSuccessors = chainPredecessors
                .GroupBy(kv => kv.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(kv => scheduled.FirstOrDefault(r => r.Id == kv.Key)?.StartTime ?? DateTime.MaxValue)
                          .ThenBy(kv => kv.Key)
                          .First().Key);
        }

        // EPGエントリのis_conflictedは常にfalse
        foreach (var r in allScheduled.Where(r => r.Source == ReservationSource.Epg && r.IsConflicted))
            UpdateConflicted(r.Id, false);

        // 各予約の占有区間（前後マージン込み）
        // reservationId → (OccupyStart, OccupyEnd)
        DateTime OccupyStart(Reservation r) => r.StartTime - preMargin;
        DateTime OccupyEnd(Reservation r)   => r.EndTime   + postMargin;

        var keywordRuleSortOrderById = GetKeywordRules()
            .ToDictionary(r => r.Id, r => r.SortOrder > 0 ? r.SortOrder : int.MaxValue);

        // ProgramRule には明示SortOrderが無いため、現行UI/APIの一覧順（id昇順）を優先順位として扱う。
        // これは「プログラム予約も上にあるルールが優先」という運用仕様を、現行DBスキーマ上で
        // もっとも安定して再現するための正本マップ。
        var programRuleOrderById = GetProgramRules()
            .Select((r, index) => new { r.Id, Order = index + 1 })
            .ToDictionary(x => x.Id, x => x.Order);

        static bool IsProgramGuideLikeSource(Reservation r)
            => r.Source is ReservationSource.Manual
                or ReservationSource.Immediate
                or ReservationSource.KeywordSearch;

        static bool IsAutoSearchSource(Reservation r)
            => r.Source == ReservationSource.Keyword;

        static bool IsProgramReservationSource(Reservation r)
            => r.Source == ReservationSource.Program;

        static int SourcePriorityRank(Reservation r)
        {
            if (IsProgramGuideLikeSource(r)) return 300;
            if (IsAutoSearchSource(r)) return 200;
            if (IsProgramReservationSource(r)) return 100;
            return 0;
        }

        static bool IsSameService(Reservation a, Reservation b)
            => a.NetworkId == b.NetworkId
               && a.TransportStreamId == b.TransportStreamId
               && a.ServiceId == b.ServiceId;

        int CompareReservationConflictPriority(Reservation a, Reservation b)
        {
            // 正の値なら a を優先、負の値なら b を優先。
            var chain = GetChainPriorityScore(a.Id).CompareTo(GetChainPriorityScore(b.Id));
            if (chain != 0) return chain;

            var source = SourcePriorityRank(a).CompareTo(SourcePriorityRank(b));
            if (source != 0) return source;

            if (IsAutoSearchSource(a) && IsAutoSearchSource(b))
            {
                var aOrder = a.SourceRuleId.HasValue && keywordRuleSortOrderById.TryGetValue(a.SourceRuleId.Value, out var aso) ? aso : int.MaxValue;
                var bOrder = b.SourceRuleId.HasValue && keywordRuleSortOrderById.TryGetValue(b.SourceRuleId.Value, out var bso) ? bso : int.MaxValue;
                if (aOrder != bOrder)
                    return bOrder.CompareTo(aOrder); // sort_order が小さいルールを優先
            }

            if (IsProgramReservationSource(a) && IsProgramReservationSource(b))
            {
                var aOrder = a.SourceRuleId.HasValue && programRuleOrderById.TryGetValue(a.SourceRuleId.Value, out var apo) ? apo : int.MaxValue;
                var bOrder = b.SourceRuleId.HasValue && programRuleOrderById.TryGetValue(b.SourceRuleId.Value, out var bpo) ? bpo : int.MaxValue;
                if (aOrder != bOrder)
                    return bOrder.CompareTo(aOrder); // 一覧上位（小さい順）を優先
            }

            // 前番組優先/後番組優先は、同一サービスの前後・隣接・マージン重複だけに適用する。
            // チェーン明示ではない通常予約の勝敗方向を決めるだけで、前番組末尾を削って成立させる処理ではない。
            if (IsSameService(a, b)
                && a.StartTime != b.StartTime
                && (a.EndTime == b.StartTime || b.EndTime == a.StartTime))
            {
                var aIsLater = a.StartTime > b.StartTime;
                if (laterProgramPriority)
                    return aIsLater ? 1 : -1;
                return aIsLater ? -1 : 1;
            }

            if (IsProgramGuideLikeSource(a) && IsProgramGuideLikeSource(b))
            {
                var created = a.CreatedAt.CompareTo(b.CreatedAt);
                if (created != 0) return created; // ユーザー能動予約同士は後から選択されたものを優先
            }

            var fallbackCreated = a.CreatedAt.CompareTo(b.CreatedAt);
            if (fallbackCreated != 0) return fallbackCreated;

            return a.Id.CompareTo(b.Id);
        }

        // TunerPoolの現在録画中スロットを「仮想予約」として占有区間に追加
        // これにより起動直後・録画中でも正確な競合判定が可能
        // チェーン前番組が録画中の場合は handoff時刻（後続のStartTime）で占有終了を打ち切る
        var virtualOccupied = new List<(string TunerName, string TunerGroup, DateTime OccupyEnd, int? ReservationId)>();

        // チェーン前番組のhandoff時刻マップ: reservationId → 後続のStartTime
        // （録画中スロットの占有終了調整用）
        var chainHandoffByReservationId = new Dictionary<int, DateTime>();
        if (pseudoContinuous)
        {
            foreach (var kv in chainPredecessors) // key=後続ID, value=前番組ID
            {
                var successor = GetById(kv.Key);
                if (successor != null)
                    chainHandoffByReservationId[kv.Value] = OccupyStart(successor);
            }
        }

        if (tunerSlots != null)
        {
            foreach (var slot in tunerSlots.Where(s =>
                s.UsageKind == Tuner.TunerUsageKind.Recording && s.PlannedEndTime.HasValue))
            {
                var grp = string.Equals(slot.Group, "BSCS", StringComparison.OrdinalIgnoreCase)
                    ? "BSCS" : "GR";
                // チェーン前番組なら占有終了をhandoff時刻に差し替える
                var occupyEnd = (slot.ReservationId.HasValue
                                 && chainHandoffByReservationId.TryGetValue(slot.ReservationId.Value, out var handoff))
                    ? handoff
                    : slot.PlannedEndTime!.Value;
                virtualOccupied.Add((ToRoleBindingTunerName(slot.Name, grp), slot.Group, occupyEnd, slot.ReservationId));
            }
        }
        else
        {
            // TunerPoolがない場合はDBのrecordingでフォールバック
            foreach (var rec in GetByStatus(ReservationStatus.Recording)
                .Where(r => r.Source != ReservationSource.Epg))
            {
                var occupyEnd = chainHandoffByReservationId.TryGetValue(rec.Id, out var handoff)
                    ? handoff
                    : rec.EndTime + postMargin;
                virtualOccupied.Add((ToRoleBindingTunerName(EffectiveTunerName(rec), ResolveGroup(rec)), ResolveGroup(rec), occupyEnd, rec.Id));
            }
        }

        // 競合判定：グループ別に処理
        // 各予約の占有区間に対して、同時に占有されるチューナー数がグループのチューナー本数を超えるか判定
        var conflictedIds = new HashSet<int>();
        var assignedIds   = new HashSet<int>(); // 競合しない（チューナーを確保できる）予約
        var debugTrace = new List<TunerAllocationDebugTraceEntry>();
        // チェーン占有終了調整用（ループ内DBアクセス回避）
        var scheduledById = scheduled.ToDictionary(s => s.Id);
        // v0.11.447:
        // チェーンroot/currentがRecordingへ移行した後も、後続を単独unit化させない。
        // ScheduledだけではなくRecording/CompletedアンカーもChainGroup構築対象に含める。
        var chainUnitCandidateById = scheduled
            .Concat(recordingReservations)
            .Concat(completedChainAnchorById.Values)
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g
                .OrderByDescending(r => r.Status == ReservationStatus.Recording)
                .ThenByDescending(r => r.Status == ReservationStatus.Completed)
                .First());

        void AddTrace(string stage, Reservation reservation, string detail)
        {
            debugTrace.Add(new TunerAllocationDebugTraceEntry
            {
                Stage = stage,
                Group = ResolveGroup(reservation),
                ReservationId = reservation.Id,
                Title = reservation.Title,
                Detail = detail
            });
        }

        foreach (var locked in userChainLockedTunerById.OrderBy(x => x.Key))
        {
            var lockedReservation = scheduledById.TryGetValue(locked.Key, out var lr) ? lr : GetById(locked.Key);
            if (lockedReservation != null)
                AddTrace("CHAIN_GROUP_LOCK_REBUILD", lockedReservation, $"id=R{locked.Key} lockTuner={locked.Value} source=db_tuner_before_reallocation");
        }

        bool IsChainRelated(int reservationId)
            => pseudoContinuous && (chainPredecessors.ContainsKey(reservationId) || chainSuccessors.ContainsKey(reservationId));

        int GetChainPriorityScore(int reservationId)
        {
            if (!IsChainRelated(reservationId))
                return 0;

            // 前番組未割当の後続は「チェーン候補」ではあるが保護を弱める。
            if (chainPredecessors.TryGetValue(reservationId, out var predecessorId) && !assignedIds.Contains(predecessorId))
                return 20;

            return 100;
        }

        List<ConflictOccupancyUnit> BuildConflictOccupancyUnitsForGroup(string groupKey, IReadOnlyList<Reservation> groupReservations)
        {
            var units = new List<ConflictOccupancyUnit>();
            var groupedReservationIds = new HashSet<int>();
            var nextUnitId = 1;

            ConflictOccupancyUnit CreateUnit(List<Reservation> members, bool isUserChain, int? chainRootId, string? preferredTuner = null)
            {
                var ordered = members
                    .OrderBy(x => x.StartTime)
                    .ThenBy(x => x.Id)
                    .ToList();
                var head = ordered.First();
                var unitPreferredTuner = !string.IsNullOrWhiteSpace(preferredTuner)
                    ? preferredTuner
                    : ordered
                        .Select(x => ToRoleBindingTunerName(EffectiveTunerName(x), groupKey))
                        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "-");
                return new ConflictOccupancyUnit
                {
                    UnitId = nextUnitId++,
                    Reservations = ordered,
                    PriorityReservation = head,
                    Group = groupKey,
                    OccupyStart = ordered.Min(OccupyStart),
                    OccupyEnd = ordered.Max(OccupyEnd),
                    IsUserChain = isUserChain,
                    ChainRootId = chainRootId,
                    HasActiveAnchor = ordered.Any(x => x.Status == ReservationStatus.Recording || x.Status == ReservationStatus.Completed),
                    PreferredTuner = unitPreferredTuner
                };
            }

            // v0.11.447:
            // 競合判定用ChainGroupは、Scheduled予約だけで作らない。
            // chainRoot/currentがRecordingへ移行した瞬間にrootが評価対象外になると、
            // R138->R141->R143 が R141単独/R143単独unitへ分裂し、後続がtuner_limit_exceededで落ちる。
            // そのため、Recording中root / Completedアンカーも明示チェーンの先頭メンバーとして保持する。
            var candidateInGroup = chainUnitCandidateById.Values
                .Where(r => string.Equals(ResolveGroup(r), groupKey, StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => r.Id)
                .Select(g => g.First())
                .ToList();

            var roots = candidateInGroup
                .Where(r => chainSuccessors.ContainsKey(r.Id))
                .Where(r => !chainPredecessors.ContainsKey(r.Id))
                .OrderBy(r => r.StartTime)
                .ThenBy(r => r.Id)
                .ToList();

            foreach (var root in roots)
            {
                var members = new List<Reservation> { root };
                groupedReservationIds.Add(root.Id);
                var currentId = root.Id;
                var guard = 0;
                while (chainSuccessors.TryGetValue(currentId, out var nextId)
                    && chainUnitCandidateById.TryGetValue(nextId, out var next)
                    && string.Equals(ResolveGroup(next), groupKey, StringComparison.OrdinalIgnoreCase)
                    && guard++ < 32)
                {
                    members.Add(next);
                    groupedReservationIds.Add(next.Id);
                    currentId = next.Id;
                }

                if (members.Count > 1)
                {
                    var activeAnchorTuner = members
                        .Where(x => x.Status == ReservationStatus.Recording || x.Status == ReservationStatus.Completed)
                        .Select(x => ToRoleBindingTunerName(EffectiveTunerName(x), groupKey))
                        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "-");
                    units.Add(CreateUnit(members, isUserChain: true, chainRootId: root.UserChainRootId ?? root.Id, preferredTuner: activeAnchorTuner));
                }
            }

            foreach (var r in groupReservations.OrderBy(r => r.StartTime).ThenBy(r => r.Id))
            {
                if (groupedReservationIds.Contains(r.Id))
                    continue;

                // 前番組がRecording/Completedで評価対象外の場合でも、後続チェーンは
                // 「予約1件」ではなく「外部アンカーを持つチェーン占有」として扱う。
                var detachedChain = pseudoContinuous && chainPredecessors.ContainsKey(r.Id);
                if (detachedChain && r.UserChainPreviousId.HasValue)
                {
                    var chainMembers = new List<Reservation>();
                    var rootId = r.UserChainRootId ?? r.UserChainPreviousId.Value;
                    if (chainUnitCandidateById.TryGetValue(rootId, out var rootCandidate)
                        && string.Equals(ResolveGroup(rootCandidate), groupKey, StringComparison.OrdinalIgnoreCase))
                    {
                        chainMembers.Add(rootCandidate);
                        groupedReservationIds.Add(rootCandidate.Id);
                        var currentId = rootCandidate.Id;
                        var guard = 0;
                        while (chainSuccessors.TryGetValue(currentId, out var nextId)
                            && chainUnitCandidateById.TryGetValue(nextId, out var next)
                            && string.Equals(ResolveGroup(next), groupKey, StringComparison.OrdinalIgnoreCase)
                            && guard++ < 32)
                        {
                            chainMembers.Add(next);
                            groupedReservationIds.Add(next.Id);
                            currentId = next.Id;
                        }
                    }

                    if (chainMembers.Count > 1)
                    {
                        var activeAnchorTuner = chainMembers
                            .Where(x => x.Status == ReservationStatus.Recording || x.Status == ReservationStatus.Completed)
                            .Select(x => ToRoleBindingTunerName(EffectiveTunerName(x), groupKey))
                            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "-");
                        units.Add(CreateUnit(chainMembers, isUserChain: true, chainRootId: rootId, preferredTuner: activeAnchorTuner));
                        continue;
                    }
                }

                units.Add(CreateUnit(new List<Reservation> { r }, detachedChain, detachedChain ? (r.UserChainRootId ?? r.UserChainPreviousId) : null));
            }

            return units
                .GroupBy(u => u.UnitKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(u => u.OccupyStart)
                .ThenBy(u => u.PriorityReservation.StartTime)
                .ThenBy(u => u.PriorityReservation.Id)
                .ToList();
        }

        int UnitChainPriorityScore(ConflictOccupancyUnit unit)
            => unit.IsUserChain ? 100 : GetChainPriorityScore(unit.PriorityReservation.Id);

        int CompareOccupancyUnitPriority(ConflictOccupancyUnit a, ConflictOccupancyUnit b)
        {
            var chain = UnitChainPriorityScore(a).CompareTo(UnitChainPriorityScore(b));
            if (chain != 0) return chain;
            return CompareReservationConflictPriority(a.PriorityReservation, b.PriorityReservation);
        }

        void MarkUnitAssigned(ConflictOccupancyUnit unit)
        {
            foreach (var member in unit.Reservations)
            {
                assignedIds.Add(member.Id);
                conflictedIds.Remove(member.Id);
            }
        }

        void MarkUnitConflicted(ConflictOccupancyUnit unit)
        {
            foreach (var member in unit.Reservations)
            {
                conflictedIds.Add(member.Id);
                assignedIds.Remove(member.Id);
            }
        }

        void RemoveUnitAssigned(ConflictOccupancyUnit unit)
        {
            foreach (var member in unit.Reservations)
                assignedIds.Remove(member.Id);
        }

        var assignedOccupancyUnits = new Dictionary<string, ConflictOccupancyUnit>(StringComparer.OrdinalIgnoreCase);
        var reservationIdToUnitId = new Dictionary<int, int>();
        var reservationIdToOccupancyUnit = new Dictionary<int, ConflictOccupancyUnit>();
        var allConflictOccupancyUnits = new List<ConflictOccupancyUnit>();

        foreach (var grpKey in scheduled.GroupBy(r => ResolveGroup(r)).Select(g => g.Key).Distinct())
        {
            var tunerCount = tunerCountByGroup.TryGetValue(grpKey, out var tc) ? tc : 0;
            var recordingCapacityTuners = roleBindingRecordingTunerNamesByGroup.TryGetValue(grpKey, out var capacityTunerList)
                ? string.Join(",", capacityTunerList)
                : "-";
            debugTrace.Add(new TunerAllocationDebugTraceEntry
            {
                Stage = "CONFLICT_POLICY",
                Group = grpKey,
                ReservationId = 0,
                Title = "Policy",
                Detail = $"policy={conflictPolicy} tunerLimit={tunerCount} capacitySource=RoleBinding/Recording recordingTuners={recordingCapacityTuners} displaySource=LogicalTunerDisplayName note=occupancy_unit_chain_group_folding user_chain_cost_one rule=v0.11.678_rolebinding_recording_tuner_display_contract"
            });
            var inGroup = scheduled
                .Where(r => ResolveGroup(r) == grpKey)
                .OrderBy(r => r.StartTime)
                .ThenBy(r => r.Id)
                .ToList();

            var occupancyUnits = BuildConflictOccupancyUnitsForGroup(grpKey, inGroup);
            allConflictOccupancyUnits.AddRange(occupancyUnits);
            if (occupancyUnits.Count > 0)
            {
                var planRepresentative = occupancyUnits[0].PriorityReservation;
                var planUnits = string.Join(";", occupancyUnits.Select(u => $"U{u.UnitId}:{u.MemberIds}:chain={u.IsUserChain}:active={u.HasActiveAnchor}:key={u.UnitKey}"));
                AddTrace("OCCUPANCY_UNIT_PLAN_SUMMARY", planRepresentative,
                    $"stage=occupancy_units_built group={grpKey} units={occupancyUnits.Count} recordingTuners={recordingCapacityTuners} displaySource=LogicalTunerDisplayName unitPlan={planUnits} source=occupancy_unit_evaluator_mirror result=VISIBLE rule=v0.11.678_rolebinding_recording_tuner_display_contract");
            }
            else
            {
                AddTrace("OCCUPANCY_UNIT_PLAN_SUMMARY", new Reservation { Id = 0, Title = "FinalConflictPlan" },
                    $"stage=occupancy_units_built group={grpKey} units=0 recordingTuners={recordingCapacityTuners} displaySource=LogicalTunerDisplayName source=occupancy_unit_evaluator_mirror result=EMPTY rule=v0.11.678_rolebinding_recording_tuner_display_contract");
            }
            foreach (var unit in occupancyUnits)
            {
                foreach (var member in unit.Reservations)
                {
                    reservationIdToUnitId[member.Id] = unit.UnitId;
                    reservationIdToOccupancyUnit[member.Id] = unit;
                }

                AddTrace(unit.IsUserChain ? "CHAIN_OCCUPANCY_UNIT" : "OCCUPANCY_UNIT", unit.PriorityReservation,
                    $"unit={unit.UnitId} members={unit.MemberIds} chainRoot=R{(unit.ChainRootId.HasValue ? unit.ChainRootId.Value.ToString() : "-")} cost=1 occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} capacitySource=RoleBinding/Recording recordingTuners={recordingCapacityTuners} displaySource=LogicalTunerDisplayName rule=v0.11.678_rolebinding_recording_tuner_display_contract");
            }

            foreach (var unit in occupancyUnits)
            {
                var representative = unit.PriorityReservation;
                AddTrace("OCCUPANCY_EVALUATE", representative,
                    $"unit={unit.UnitId} members={unit.MemberIds} occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} chain={unit.IsUserChain} conflictPolicy={conflictPolicy} laterPriority={laterProgramPriority}");

                foreach (var member in unit.Reservations.Where(m => chainPredecessors.TryGetValue(m.Id, out _)))
                {
                    var predId = chainPredecessors[member.Id];
                    var predecessorAssignedForChain = assignedIds.Contains(predId) || recordingReservationIds.Contains(predId) || completedChainAnchorIds.Contains(predId);
                    AddTrace("CHAIN_CHECK", member,
                        $"predecessor=R{predId} predecessorAssigned={predecessorAssignedForChain} predecessorRecording={recordingReservationIds.Contains(predId)} predecessorCompletedAnchor={completedChainAnchorIds.Contains(predId)} occupancyUnit={unit.UnitId}");
                }

                var overlappingAssignedUnits = assignedOccupancyUnits.Values
                    .Where(x => x.Group.Equals(grpKey, StringComparison.OrdinalIgnoreCase))
                    .Where(x => x.OccupyStart < unit.OccupyEnd && x.OccupyEnd > unit.OccupyStart)
                    .OrderBy(x => x.OccupyStart)
                    .ThenBy(x => x.PriorityReservation.Id)
                    .ToList();

                var overlappingAssigned = overlappingAssignedUnits.Count;
                var unitMemberIds = unit.Reservations.Select(x => x.Id).ToHashSet();
                var overlappingVirtual = virtualOccupied
                    .Where(v => SupportsReservationGroup(v.TunerGroup, grpKey)
                             && v.OccupyEnd > unit.OccupyStart
                             && (!v.ReservationId.HasValue || !unitMemberIds.Contains(v.ReservationId.Value)))
                    .Count();
                var totalOccupied = overlappingAssigned + overlappingVirtual;

                if (totalOccupied < tunerCount)
                {
                    MarkUnitAssigned(unit);
                    assignedOccupancyUnits[unit.UnitKey] = unit;
                    AddTrace(unit.IsUserChain ? "CHAIN_OCCUPANCY_APPLY" : "ASSIGN", representative,
                        $"unit={unit.UnitId} members={unit.MemberIds} overlapUnits={overlappingAssigned} overlapVirtual={overlappingVirtual} total={totalOccupied} limit={tunerCount} capacitySource=RoleBinding/Recording recordingTuners={recordingCapacityTuners} result=ASSIGNED cost=1 displaySource=LogicalTunerDisplayName rule=v0.11.678_rolebinding_recording_tuner_display_contract");
                    if (!unit.IsUserChain)
                    {
                        AddTrace("OCCUPANCY_UNIT_ASSIGN_MIRROR", representative,
                            $"stage=occupancy_apply_mirror unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} chainRoot=R{(unit.ChainRootId.HasValue ? unit.ChainRootId.Value.ToString() : "-")} tuner={(unit.PreferredTuner ?? "-")} overlapUnits={overlappingAssigned} overlapVirtual={overlappingVirtual} total={totalOccupied} limit={tunerCount} group={grpKey} capacitySource=RoleBinding/Recording recordingTuners={recordingCapacityTuners} displaySource=LogicalTunerDisplayName source=occupancy_unit_evaluator_mirror result=ASSIGNED rule=v0.11.678_rolebinding_recording_tuner_display_contract");
                    }
                    continue;
                }

                AddTrace("CONFLICT", representative,
                    $"unit={unit.UnitId} members={unit.MemberIds} overlapUnits={overlappingAssigned} overlapVirtual={overlappingVirtual} total={totalOccupied} limit={tunerCount}");

                var candidateLosers = overlappingAssignedUnits
                    .OrderBy(x => CompareOccupancyUnitPriority(x, unit))
                    .ThenBy(x => x.OccupyStart)
                    .ThenBy(x => x.PriorityReservation.Id)
                    .ToList();

                var victim = candidateLosers.FirstOrDefault(x => CompareOccupancyUnitPriority(unit, x) > 0);
                if (victim != null)
                {
                    RemoveUnitAssigned(victim);
                    MarkUnitConflicted(victim);
                    assignedOccupancyUnits.Remove(victim.UnitKey);
                    MarkUnitAssigned(unit);
                    assignedOccupancyUnits[unit.UnitKey] = unit;

                    var priorityLayer = unit.IsUserChain || victim.IsUserChain
                        ? "CHAIN_OCCUPANCY_PRIORITY"
                        : IsSameService(unit.PriorityReservation, victim.PriorityReservation)
                            ? (laterProgramPriority ? "SAME_SERVICE_PREFER_LATER" : "SAME_SERVICE_PREFER_EARLIER")
                            : SourcePriorityRank(unit.PriorityReservation) != SourcePriorityRank(victim.PriorityReservation)
                                ? "SOURCE_PRIORITY"
                                : IsAutoSearchSource(unit.PriorityReservation) && IsAutoSearchSource(victim.PriorityReservation)
                                    ? "AUTO_SEARCH_RULE_PRIORITY"
                                    : "PEER_Z_AXIS";

                    AddTrace("PRIORITY_CONFLICT", representative,
                        $"winnerUnit={unit.UnitId} winner={unit.MemberIds} loserUnit={victim.UnitId} loser={victim.MemberIds} priorityLayer={priorityLayer} winnerChain={unit.IsUserChain} loserChain={victim.IsUserChain} rule=v0.11.447_chain_active_root_unit_core");
                    foreach (var loser in victim.Reservations)
                    {
                        AddTrace("DROP", loser, $"result=CONFLICT losingAgainstUnit={unit.UnitId} reason=occupancy_unit_priority priorityLayer={priorityLayer}");
                        AddTrace("OCCUPANCY_UNIT_CONFLICT_MIRROR", loser,
                            $"stage=occupancy_priority_mirror unitKey={victim.UnitKey} unit={victim.UnitId} members={victim.MemberIds} losingAgainstUnit={unit.UnitId} group={grpKey} reason=occupancy_unit_priority priorityLayer={priorityLayer} source=occupancy_unit_evaluator_mirror rule=v0.11.502_settings_genre_editor_spacing_contract");
                    }
                }
                else
                {
                    MarkUnitConflicted(unit);
                    var protectedUnit = candidateLosers
                        .OrderByDescending(x => CompareOccupancyUnitPriority(x, unit))
                        .ThenBy(x => x.OccupyStart)
                        .ThenBy(x => x.PriorityReservation.Id)
                        .FirstOrDefault();

                    if (protectedUnit != null)
                    {
                        var conflictStart = protectedUnit.OccupyStart > unit.OccupyStart ? protectedUnit.OccupyStart : unit.OccupyStart;
                        var conflictEnd = protectedUnit.OccupyEnd < unit.OccupyEnd ? protectedUnit.OccupyEnd : unit.OccupyEnd;
                        var protectedList = string.Join(",", overlappingAssignedUnits.Select(x => $"U{x.UnitId}:{x.MemberIds}"));
                        var priorityLayer = unit.IsUserChain || protectedUnit.IsUserChain
                            ? "CHAIN_OCCUPANCY_PRIORITY"
                            : IsSameService(unit.PriorityReservation, protectedUnit.PriorityReservation)
                                ? (laterProgramPriority ? "SAME_SERVICE_PREFER_LATER" : "SAME_SERVICE_PREFER_EARLIER")
                                : SourcePriorityRank(unit.PriorityReservation) != SourcePriorityRank(protectedUnit.PriorityReservation)
                                    ? "SOURCE_PRIORITY"
                                    : IsAutoSearchSource(unit.PriorityReservation) && IsAutoSearchSource(protectedUnit.PriorityReservation)
                                        ? "AUTO_SEARCH_RULE_PRIORITY"
                                        : "PEER_Z_AXIS";
                        AddTrace("PRIORITY_CONFLICT", representative,
                            $"protectedUnit={protectedUnit.UnitId} protected={protectedUnit.MemberIds} droppedUnit={unit.UnitId} dropped={unit.MemberIds} priorityLayer={priorityLayer} conflictWindow={conflictStart:MM/dd HH:mm:ss}〜{conflictEnd:MM/dd HH:mm:ss} protectedSet={protectedList} currentScore={UnitChainPriorityScore(unit)} protectedScore={UnitChainPriorityScore(protectedUnit)} rule=v0.11.447_chain_active_root_unit_core");
                    }
                    else
                    {
                        AddTrace("PRIORITY_CONFLICT", representative,
                            $"protected=virtual_or_unknown droppedUnit={unit.UnitId} dropped={unit.MemberIds} overlapVirtual={overlappingVirtual} total={totalOccupied} limit={tunerCount} currentScore={UnitChainPriorityScore(unit)} rule=v0.11.447_chain_active_root_unit_core");
                    }

                    foreach (var member in unit.Reservations)
                    {
                        AddTrace("DROP", member, $"result=CONFLICT reason=occupancy_unit_tuner_limit unit={unit.UnitId} members={unit.MemberIds}");
                        AddTrace("OCCUPANCY_UNIT_CONFLICT_MIRROR", member,
                            $"stage=occupancy_limit_mirror unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} group={grpKey} reason=occupancy_unit_tuner_limit overlapVirtual={overlappingVirtual} total={totalOccupied} limit={tunerCount} source=occupancy_unit_evaluator_mirror rule=v0.11.502_settings_genre_editor_spacing_contract");
                    }
                }
            }
        }

        // チューナー名の割り当て（assignedIdsに含まれる予約に LogicalTunerDisplayName を割り当てる）
        // LogicalTunerDisplayName は優先順位用であり、LogicalTunerIdentity を未来日予約へ固定しない。
        var tunerAssignment = new Dictionary<int, string>(); // reservationId → LogicalTunerDisplayName
        var tunerFreeAt = roleBindingRecordingTunerNamesByGroup.Values.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(name => name, _ => DateTime.MinValue, StringComparer.OrdinalIgnoreCase);
        var chainUnitAssignedTuner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rrCursor = 0;

        // TunerPoolの録画中スロットでプリセット
        if (tunerSlots != null)
        {
            foreach (var slot in tunerSlots.Where(s =>
                s.UsageKind == Tuner.TunerUsageKind.Recording
                && s.PlannedEndTime.HasValue
                && !string.IsNullOrEmpty(s.Name)))
            {
                var virtualName = ToRoleBindingTunerName(slot.Name, slot.Group);
                if (!tunerFreeAt.ContainsKey(virtualName)) continue;
                var freeAt = (slot.ReservationId.HasValue
                              && chainHandoffByReservationId.TryGetValue(slot.ReservationId.Value, out var ho))
                    ? ho
                    : slot.PlannedEndTime!.Value;
                tunerFreeAt[virtualName] = freeAt;

                if (slot.ReservationId.HasValue && pseudoContinuous
                    && chainSuccessors.ContainsKey(slot.ReservationId.Value))
                    tunerAssignment[slot.ReservationId.Value] = virtualName;
            }
        }

        // TunerPoolスナップショットが無い/不足するケースでも、DB上のRecording予約の
        // TunerNameをチェーン継続元として使う。録画中前番組→後続のC予約を競合化させないため。
        if (pseudoContinuous)
        {
            foreach (var rec in recordingReservations)
            {
                if (!rec.Id.Equals(0)
                    && chainSuccessors.ContainsKey(rec.Id)
                    && !string.IsNullOrWhiteSpace(EffectiveTunerName(rec))
                    && tunerFreeAt.ContainsKey(EffectiveTunerName(rec)))
                {
                    var recTuner = EffectiveTunerName(rec);
                    tunerAssignment[rec.Id] = recTuner;
                    if (chainHandoffByReservationId.TryGetValue(rec.Id, out var recHandoff))
                        tunerFreeAt[recTuner] = recHandoff;
                }
            }
        }

        // Completed化済みの直前番組も、明示チェーン後続のチューナー継承元として登録する。
        // これが無いと R387終了直後のように、前番組は完了済み・後続はScheduledの境界で
        // 後続R389/R390のTunerNameが空へ更新され、録画開始時にconflict_still_true /*FORCE_REALLOC_TRIGGER*/でスキップされる。
        if (pseudoContinuous)
        {
            foreach (var anchor in completedChainAnchorById.Values)
            {
                var anchorTuner = EffectiveTunerName(anchor);
                if (!string.IsNullOrWhiteSpace(anchorTuner) && tunerFreeAt.ContainsKey(anchorTuner))
                {
                    tunerAssignment[anchor.Id] = anchorTuner;
                    if (chainHandoffByReservationId.TryGetValue(anchor.Id, out var completedHandoff))
                        tunerFreeAt[anchorTuner] = completedHandoff;
                    AddTrace("CHAIN_ANCHOR", anchor, $"result=REGISTER_COMPLETED_PREDECESSOR tuner={anchorTuner} reason=user_chain_handoff_completed_anchor");
                }
            }
        }

        // v0.2.24: チェーン後続が将来使用する同一チューナーを予約済みとして保護する。
        // 非チェーン予約が同時刻付近でそのチューナーを先取りすると、チェーン後続が録画開始時に
        // requested_chain_tuner_not_free で失敗するため、通常候補から除外する。
        var chainProtectedTunerClaims = new List<(string TunerName, DateTime OccupyStart, DateTime OccupyEnd, int ReservationId)>();

        bool IsBlockedByChainProtectedClaim(string tunerName, Reservation current, DateTime currentStart, DateTime currentEnd)
        {
            if (!pseudoContinuous)
                return false;
            if (current.IsUserChain || chainPredecessors.ContainsKey(current.Id))
                return false;

            return chainProtectedTunerClaims.Any(c =>
                c.ReservationId != current.Id
                && string.Equals(c.TunerName, tunerName, StringComparison.OrdinalIgnoreCase)
                && c.OccupyStart < currentEnd
                && c.OccupyEnd > currentStart);
        }

        bool IsTunerFreeForReservation(string tunerName, Reservation current, DateTime currentStart, DateTime currentEnd)
        {
            if (string.IsNullOrWhiteSpace(tunerName))
                return false;
            if (!tunerFreeAt.TryGetValue(tunerName, out var freeAt))
                return false;
            if (freeAt > currentStart)
                return false;
            return !IsBlockedByChainProtectedClaim(tunerName, current, currentStart, currentEnd);
        }

        // v0.11.453:
        // チェーンunitの最終チューナー選択は ReconcileFinalConflictPlanByUnitKey() に一本化。
        // 旧 ChooseChainUnitTuner / ApplyChainUnitTuner は final plan と二重正本になるため削除。

        void RegisterChainSuccessorTunerClaim(Reservation predecessor, string tunerName)
        {
            if (!pseudoContinuous || string.IsNullOrWhiteSpace(tunerName))
                return;
            if (!chainSuccessors.TryGetValue(predecessor.Id, out var successorId))
                return;
            if (!scheduledById.TryGetValue(successorId, out var successor))
                return;
            if (!assignedIds.Contains(successorId) || conflictedIds.Contains(successorId))
                return;

            var claimStart = OccupyStart(successor);
            var claimEnd = chainHandoffByReservationId.TryGetValue(successor.Id, out var successorHandoff)
                ? successorHandoff
                : OccupyEnd(successor);
            if (claimEnd <= claimStart)
                claimEnd = OccupyEnd(successor);

            if (chainProtectedTunerClaims.Any(c => c.ReservationId == successorId
                && string.Equals(c.TunerName, tunerName, StringComparison.OrdinalIgnoreCase)))
                return;

            chainProtectedTunerClaims.Add((tunerName, claimStart, claimEnd, successorId));
            AddTrace("CHAIN_TUNER_PROTECT", successor,
                $"reserved_by_predecessor=R{predecessor.Id} tuner={tunerName} occupy={claimStart:MM/dd HH:mm:ss}〜{claimEnd:MM/dd HH:mm:ss} reason=protect_user_chain_successor_from_non_chain_reallocation");
        }

        var globallyAssigned = scheduled
            .Where(r => assignedIds.Contains(r.Id))
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        foreach (var r in globallyAssigned)
        {
            if (conflictedIds.Contains(r.Id))
                continue;

            var reqGroup = ResolveGroup(r);
            var oStart = OccupyStart(r);
            var oEnd   = OccupyEnd(r);
            AddTrace("EVALUATE", r, $"occupy={oStart:MM/dd HH:mm:ss}〜{oEnd:MM/dd HH:mm:ss} created={r.CreatedAt:MM/dd HH:mm:ss} conflictPolicy={conflictPolicy} laterPriority={laterProgramPriority} chain={pseudoContinuous}");

            var candidateNames = roleBindingRecordingTunerNamesByGroup.TryGetValue(reqGroup, out var virtualCandidates)
                ? virtualCandidates.ToList()
                : new List<string>();

            if (candidateNames.Count == 0)
            {
                conflictedIds.Add(r.Id);
                assignedIds.Remove(r.Id);
                AddTrace("DROP", r, $"result=CONFLICT reason=no_candidate_tuner_for_group requestGroup={reqGroup}");
                continue;
            }

            if (reservationIdToOccupancyUnit.TryGetValue(r.Id, out var chainUnitForReservation)
                && chainUnitForReservation.IsUserChain)
            {
                // v0.11.453:
                // チェーン専用の CHAIN_UNIT_TUNER_ASSIGN / APPLY がここで先に最終チューナーを
                // 決めると、後段の UnitKey final plan と二重正本になる。
                // ここではチェーンunitとして評価対象へ残すだけにし、最終チューナー確定は
                // ReconcileFinalConflictPlanByUnitKey() に一本化する。
                AddTrace("CHAIN_UNIT_TUNER_DEFER", chainUnitForReservation.PriorityReservation,
                    $"unitKey={chainUnitForReservation.UnitKey} unit={chainUnitForReservation.UnitId} members={chainUnitForReservation.MemberIds} chainRoot=R{(chainUnitForReservation.ChainRootId.HasValue ? chainUnitForReservation.ChainRootId.Value.ToString() : "-")} reason=final_unit_plan_is_single_source_of_truth rule=v0.11.502_settings_genre_editor_spacing_contract");
                continue;
            }

            if (r.IsUserChain)
            {
                // v0.6.17:
                // チェーン後続の TunerName は、予約追加時点の仮値ではなく、
                // 共通割り当てルートで確定した前番組の割当を最優先で継承する。
                // これにより、通常予約追加などの再ALLOC後に R637→R640 が S3→S1 のように
                // 分裂するケースを防ぎ、Cマークの意味である「同一チューナー引き継ぎ」を守る。
                if (r.UserChainPreviousId.HasValue
                    && tunerAssignment.TryGetValue(r.UserChainPreviousId.Value, out var userChainTuner)
                    && candidateNames.Contains(userChainTuner, StringComparer.OrdinalIgnoreCase))
                {
                    userChainLockedTunerById.TryGetValue(r.Id, out var previousLockedTuner);
                    tunerAssignment[r.Id] = userChainTuner;
                    var userChainFreeAt = chainHandoffByReservationId.TryGetValue(r.Id, out var userHandoff) ? userHandoff : oEnd;
                    tunerFreeAt[userChainTuner] = userChainFreeAt;
                    AddTrace("CHAIN_ALLOC_LOCK", r,
                        $"inherit_from=R{r.UserChainPreviousId.Value} result=ALLOCATED tuner={userChainTuner} previousLockedTuner={(string.IsNullOrWhiteSpace(previousLockedTuner) ? "-" : previousLockedTuner)} reason=common_alloc_predecessor_tuner_overrides_stale_chain_tuner handoffUntil={userChainFreeAt:MM/dd HH:mm:ss}");
                    RegisterChainSuccessorTunerClaim(r, userChainTuner);
                    continue;
                }

                // 前番組がまだ評価対象外の場合のみ、予約時点で保持していた仮想チューナーを暫定ロックとして使う。
                // ただし前番組が共通割り当て済みになった次回評価では上の predecessor 継承で上書きされる。
                if (userChainLockedTunerById.TryGetValue(r.Id, out var lockedUserChainTuner)
                    && candidateNames.Contains(lockedUserChainTuner, StringComparer.OrdinalIgnoreCase))
                {
                    tunerAssignment[r.Id] = lockedUserChainTuner;
                    var lockedFreeAt = chainHandoffByReservationId.TryGetValue(r.Id, out var lockedHandoff) ? lockedHandoff : oEnd;
                    tunerFreeAt[lockedUserChainTuner] = lockedFreeAt;
                    AddTrace("CHAIN_ALLOC_LOCK", r,
                        $"result=ALLOCATED tuner={lockedUserChainTuner} reason=fallback_to_existing_chain_tuner_until_predecessor_allocation_visible previousTuner={EffectiveTunerName(r)} handoffUntil={lockedFreeAt:MM/dd HH:mm:ss}");
                    RegisterChainSuccessorTunerClaim(r, lockedUserChainTuner);
                    continue;
                }

                // 旧予約をキャンセル→同一番組を再予約した場合、UserChainPreviousId が
                // Cancelled 予約を指したまま残ることがある。その場合は stale な明示リンクで
                // 競合に落とさず、同一局・連続時刻から再検出した chainPredecessors にフォールバックする。
                if (pseudoContinuous
                    && chainPredecessors.TryGetValue(r.Id, out var autoChainPred)
                    && (!r.UserChainPreviousId.HasValue || autoChainPred != r.UserChainPreviousId.Value)
                    && tunerAssignment.TryGetValue(autoChainPred, out var autoChainTuner)
                    && candidateNames.Contains(autoChainTuner, StringComparer.OrdinalIgnoreCase))
                {
                    tunerAssignment[r.Id] = autoChainTuner;
                    var autoChainFreeAt = chainHandoffByReservationId.TryGetValue(r.Id, out var autoHandoff) ? autoHandoff : oEnd;
                    tunerFreeAt[autoChainTuner] = autoChainFreeAt;
                    AddTrace("USER_CHAIN_APPLY", r, $"inherit_from=R{autoChainPred} result=ALLOCATED tuner={autoChainTuner} reason=stale_user_chain_fallback_to_detected_chain stalePredecessor=R{(r.UserChainPreviousId.HasValue ? r.UserChainPreviousId.Value.ToString() : "-")} handoffUntil={autoChainFreeAt:MM/dd HH:mm:ss}");
                    RegisterChainSuccessorTunerClaim(r, autoChainTuner);
                    continue;
                }

                conflictedIds.Add(r.Id);
                assignedIds.Remove(r.Id);
                AddTrace("DROP", r, $"result=CONFLICT reason=user_forced_chain_predecessor_not_allocated predecessor=R{(r.UserChainPreviousId.HasValue ? r.UserChainPreviousId.Value.ToString() : "-")}");
                continue;
            }

            string? chosen = null;

            // v0.11.97:
            // ユーザー明示チェーンの先頭予約は通常予約扱いだが、後続チェーンの
            // チューナー継承元でもある。再ALLOCのたびに空き順/round-robinだけで
            // 先頭チューナーが揺れると、後続の IsUserChain がその揺れを正として
            // 継承し、別チェーン系列や別局重なりへ波及する。
            // そのため、chainSuccessors を持つチェーン先頭/中継予約は、共通割り当て
            // ルート内で既存の planned/actual tuner を第一候補として尊重する。
            // ただし空き確認と chainProtectedClaim は必ず通し、無理な固定や
            // 別系列チューナーの奪取は行わない。
            if (pseudoContinuous && chainSuccessors.ContainsKey(r.Id))
            {
                var preferredChainRootTuner = ToRoleBindingTunerName(EffectiveTunerName(r), reqGroup);
                if (!string.IsNullOrWhiteSpace(preferredChainRootTuner)
                    && candidateNames.Contains(preferredChainRootTuner, StringComparer.OrdinalIgnoreCase)
                    && IsTunerFreeForReservation(preferredChainRootTuner, r, oStart, oEnd))
                {
                    chosen = preferredChainRootTuner;
                    AddTrace("CHAIN_ROOT_TUNER_KEEP", r,
                        $"result=PREFERRED tuner={preferredChainRootTuner} reason=keep_existing_chain_series_tuner_in_common_allocation successor=R{chainSuccessors[r.Id]} occupyStart={oStart:MM/dd HH:mm:ss} occupyEnd={oEnd:MM/dd HH:mm:ss}");
                }
            }

            if (pseudoContinuous && chainPredecessors.TryGetValue(r.Id, out var predId3)
                && (assignedIds.Contains(predId3) || recordingReservationIds.Contains(predId3) || completedChainAnchorIds.Contains(predId3))
                && !conflictedIds.Contains(predId3)
                && tunerAssignment.TryGetValue(predId3, out var inheritedPredTuner)
                && candidateNames.Contains(inheritedPredTuner, StringComparer.OrdinalIgnoreCase))
            {
                // 完全性優先: チェーン成立済みの後続は、preStart/postEnd の重なりで
                // 前番組チューナーが「まだ空いていない」ように見えても別チューナーへ逃がさない。
                // Cマークの意味は同一チューナーhandoffなので、ここでは tunerFreeAt <= oStart を条件にしない。
                var previousFreeAt = tunerFreeAt[inheritedPredTuner];
                tunerAssignment[r.Id] = inheritedPredTuner;
                var inheritedFreeAt = chainHandoffByReservationId.TryGetValue(r.Id, out var inheritedHandoff)
                    ? inheritedHandoff
                    : oEnd;
                tunerFreeAt[inheritedPredTuner] = inheritedFreeAt;
                AddTrace("CHAIN_APPLY", r,
                    $"inherit_from=R{predId3} result=ALLOCATED tuner={inheritedPredTuner} reason=force_same_tuner_handoff_completeness_priority previousFreeAt={previousFreeAt:MM/dd HH:mm:ss} occupyStart={oStart:MM/dd HH:mm:ss} handoffUntil={inheritedFreeAt:MM/dd HH:mm:ss}");
                RegisterChainSuccessorTunerClaim(r, inheritedPredTuner);
                continue;
            }

            if (chosen == null)
            {
                var freeNonChainCandidates = candidateNames
                    .Where(name => tunerFreeAt[name] <= oStart)
                    .Where(name => !IsBlockedByChainProtectedClaim(name, r, oStart, oEnd))
                    .OrderBy(name => tunerFreeAt[name])
                    .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (freeNonChainCandidates.Count > 0)
                {
                    chosen = freeNonChainCandidates[rrCursor % freeNonChainCandidates.Count];
                    rrCursor++;
                }
            }

            if (chosen == null)
            {
                var freeCandidates = candidateNames
                    .Where(name => tunerFreeAt[name] <= oStart)
                    .Where(name => !IsBlockedByChainProtectedClaim(name, r, oStart, oEnd))
                    .OrderBy(name => tunerFreeAt[name])
                    .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (freeCandidates.Count > 0)
                {
                    chosen = freeCandidates[rrCursor % freeCandidates.Count];
                    rrCursor++;
                }
            }

            if (chosen == null)
            {
                conflictedIds.Add(r.Id);
                assignedIds.Remove(r.Id);
                AddTrace("DROP", r, $"result=CONFLICT reason=no_free_supported_tuner requestGroup={reqGroup} candidates={string.Join(',', candidateNames)}");
                continue;
            }

            tunerAssignment[r.Id] = chosen;
            var chosenFreeAt = oEnd;
            tunerFreeAt[chosen]   = chosenFreeAt;
            RegisterChainSuccessorTunerClaim(r, chosen);
        }

        // v0.11.453:
        // 旧 v0.11.449 の active successor conflict suppress は症状別の上塗りだったため、
        // ここでは実行しない。active chain/current/successor の救済も UnitKey final plan が
        // 一括で担う。

        // v0.11.450:
        // 449の「後続だけ抑止」は症状別の上塗りになり始めていたため、最終の競合状態は
        // reservation単体ではなく ConflictOccupancyUnit(UnitKey) を正本にして再合算する。
        // active recording / active chain / normal reservation を同じUnitKeyタイムラインで一度だけ数え、
        // 空きRecording role tunerがある通常予約を tuner=- のまま競合に落とさない。
        void ReconcileFinalConflictPlanByUnitKey()
        {
            AddTrace("FINAL_CONFLICT_PLAN_BEGIN", new Reservation { Id = 0, Title = "FinalConflictPlan" },
                $"scheduled={scheduled.Count} recording={recordingReservations.Count} occupancyUnits={allConflictOccupancyUnits.Count} groups={string.Join(',', allConflictOccupancyUnits.Select(u => u.Group).Distinct(StringComparer.OrdinalIgnoreCase))} rule=v0.11.502_settings_genre_editor_spacing_contract");
            var evaluatedIds = scheduled.Select(x => x.Id).ToHashSet();
            var finalAssignedIds = new HashSet<int>();
            var finalConflictedIds = new HashSet<int>();
            var finalTunerAssignment = new Dictionary<int, string>();

            var unitsByKey = allConflictOccupancyUnits
                .GroupBy(u => u.UnitKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Where(u => u.Reservations.Any(m => evaluatedIds.Contains(m.Id)))
                .OrderBy(u => u.OccupyStart)
                .ThenBy(u => u.PriorityReservation.StartTime)
                .ThenBy(u => u.PriorityReservation.Id)
                .ToList();

            var unitMemberIds = unitsByKey
                .SelectMany(u => u.Reservations)
                .Select(r => r.Id)
                .ToHashSet();

            foreach (var grpKey in unitsByKey.Select(u => u.Group).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidateNames = roleBindingRecordingTunerNamesByGroup.TryGetValue(grpKey, out var virtualCandidates)
                    ? virtualCandidates.ToList()
                    : new List<string>();
                var finalFreeAt = candidateNames.ToDictionary(name => name, _ => DateTime.MinValue, StringComparer.OrdinalIgnoreCase);
                var recordingCapacityTuners = candidateNames.Count > 0 ? string.Join(",", candidateNames) : "-";

                if (candidateNames.Count == 0)
                {
                    foreach (var unit in unitsByKey.Where(u => string.Equals(u.Group, grpKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        foreach (var member in unit.Reservations.Where(m => evaluatedIds.Contains(m.Id)))
                        {
                            finalConflictedIds.Add(member.Id);
                            AddTrace("FINAL_UNIT_CONFLICT", member,
                                $"unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} reason=no_candidate_tuner_for_group group={grpKey} rule=v0.11.502_settings_genre_editor_spacing_contract");
                        }
                    }
                    continue;
                }

                if (tunerSlots != null)
                {
                    foreach (var slot in tunerSlots.Where(s =>
                        s.UsageKind == Tuner.TunerUsageKind.Recording
                        && s.PlannedEndTime.HasValue
                        && !string.IsNullOrEmpty(s.Name)
                        && SupportsReservationGroup(s.Group, grpKey)))
                    {
                        if (slot.ReservationId.HasValue && unitMemberIds.Contains(slot.ReservationId.Value))
                            continue;
                        var virtualName = ToRoleBindingTunerName(slot.Name, grpKey);
                        if (!finalFreeAt.ContainsKey(virtualName)) continue;
                        var current = finalFreeAt[virtualName];
                        if (slot.PlannedEndTime!.Value > current)
                            finalFreeAt[virtualName] = slot.PlannedEndTime.Value;
                        AddTrace("FINAL_UNIT_EXTERNAL_OCCUPY", new Reservation { Id = slot.ReservationId ?? 0, Title = "ExternalOrActiveRecording" },
                            $"group={grpKey} tuner={virtualName} reservation=R{(slot.ReservationId.HasValue ? slot.ReservationId.Value.ToString() : "-")} occupyUntil={slot.PlannedEndTime.Value:MM/dd HH:mm:ss} reason=active_recording_outside_unit rule=v0.11.502_settings_genre_editor_spacing_contract");
                    }
                }
                else
                {
                    foreach (var rec in recordingReservations.Where(r => SupportsReservationGroup(ResolveGroup(r), grpKey)))
                    {
                        if (unitMemberIds.Contains(rec.Id))
                            continue;
                        var virtualName = ToRoleBindingTunerName(EffectiveTunerName(rec), grpKey);
                        if (string.IsNullOrWhiteSpace(virtualName) || !finalFreeAt.ContainsKey(virtualName)) continue;
                        var recEnd = rec.EndTime + postMargin;
                        if (recEnd > finalFreeAt[virtualName])
                            finalFreeAt[virtualName] = recEnd;
                        AddTrace("FINAL_UNIT_EXTERNAL_OCCUPY", rec,
                            $"group={grpKey} tuner={virtualName} reservation=R{rec.Id} occupyUntil={recEnd:MM/dd HH:mm:ss} reason=db_recording_outside_unit rule=v0.11.502_settings_genre_editor_spacing_contract");
                    }
                }

                bool UnitOverlaps(ConflictOccupancyUnit a, ConflictOccupancyUnit b)
                    => a.OccupyStart < b.OccupyEnd && b.OccupyStart < a.OccupyEnd;

                bool IntervalOverlaps(DateTime start, DateTime end, DateTime otherStart, DateTime otherEnd)
                    => start < otherEnd && otherStart < end;

                DateTime UnitUserDecisionTime(ConflictOccupancyUnit unit)
                    => unit.Reservations.Count == 0 ? DateTime.MinValue : unit.Reservations.Max(r => r.CreatedAt);

                int RuleOrderForUnit(ConflictOccupancyUnit unit)
                {
                    var r = unit.PriorityReservation;
                    if (IsAutoSearchSource(r))
                        return r.SourceRuleId.HasValue && keywordRuleSortOrderById.TryGetValue(r.SourceRuleId.Value, out var ko) ? ko : int.MaxValue;
                    if (IsProgramReservationSource(r))
                        return r.SourceRuleId.HasValue && programRuleOrderById.TryGetValue(r.SourceRuleId.Value, out var po) ? po : int.MaxValue;
                    return int.MaxValue;
                }

                int CompareFinalUnitPriority(ConflictOccupancyUnit a, ConflictOccupancyUnit b)
                {
                    // 正の値なら a を優先、負の値なら b を優先。
                    if (a.HasActiveAnchor != b.HasActiveAnchor)
                        return a.HasActiveAnchor ? 1 : -1;

                    var source = SourcePriorityRank(a.PriorityReservation).CompareTo(SourcePriorityRank(b.PriorityReservation));
                    if (source != 0) return source;

                    var aHead = a.PriorityReservation;
                    var bHead = b.PriorityReservation;

                    if (IsAutoSearchSource(aHead) && IsAutoSearchSource(bHead))
                    {
                        var aOrder = RuleOrderForUnit(a);
                        var bOrder = RuleOrderForUnit(b);
                        if (aOrder != bOrder)
                            return bOrder.CompareTo(aOrder); // 小さい順が上位
                    }

                    if (IsProgramReservationSource(aHead) && IsProgramReservationSource(bHead))
                    {
                        var aOrder = RuleOrderForUnit(a);
                        var bOrder = RuleOrderForUnit(b);
                        if (aOrder != bOrder)
                            return bOrder.CompareTo(aOrder); // 小さい順が上位
                    }

                    var sameServiceAdjacent = IsSameService(aHead, bHead)
                        && aHead.StartTime != bHead.StartTime
                        && (aHead.EndTime == bHead.StartTime || bHead.EndTime == aHead.StartTime)
                        && UnitOverlaps(a, b);
                    if (sameServiceAdjacent)
                    {
                        var aIsLater = aHead.StartTime > bHead.StartTime;
                        if (laterProgramPriority)
                            return aIsLater ? 1 : -1;
                        return aIsLater ? -1 : 1;
                    }

                    if (IsProgramGuideLikeSource(aHead) && IsProgramGuideLikeSource(bHead))
                    {
                        var decision = UnitUserDecisionTime(a).CompareTo(UnitUserDecisionTime(b));
                        if (decision != 0) return decision; // 後から能動選択されたunitを優先
                    }

                    var created = UnitUserDecisionTime(a).CompareTo(UnitUserDecisionTime(b));
                    if (created != 0) return created;
                    return a.UnitId.CompareTo(b.UnitId);
                }

                var finalIntervals = candidateNames.ToDictionary(
                    name => name,
                    _ => new List<(DateTime Start, DateTime End, ConflictOccupancyUnit? Unit, string Reason)>(),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var kv in finalFreeAt)
                {
                    if (kv.Value > DateTime.MinValue && finalIntervals.ContainsKey(kv.Key))
                    {
                        finalIntervals[kv.Key].Add((DateTime.MinValue, kv.Value, null, "external_or_active_recording"));
                    }
                }

                foreach (var unit in unitsByKey.Where(u => string.Equals(u.Group, grpKey, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(u => u, Comparer<ConflictOccupancyUnit>.Create((a, b) => -CompareFinalUnitPriority(a, b)))
                    .ThenBy(u => u.OccupyStart)
                    .ThenBy(u => u.PriorityReservation.StartTime)
                    .ThenBy(u => u.PriorityReservation.Id))
                {
                    string? preferred = null;
                    if (!string.IsNullOrWhiteSpace(unit.PreferredTuner) && unit.PreferredTuner != "-")
                        preferred = unit.PreferredTuner;
                    if (string.IsNullOrWhiteSpace(preferred))
                    {
                        preferred = unit.Reservations
                            .Select(m => tunerAssignment.TryGetValue(m.Id, out var existing) ? existing : ToRoleBindingTunerName(EffectiveTunerName(m), grpKey))
                            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "-" && candidateNames.Contains(x, StringComparer.OrdinalIgnoreCase));
                    }

                    bool CanUseTuner(string name)
                        => finalIntervals.TryGetValue(name, out var intervals)
                           && !intervals.Any(x => IntervalOverlaps(unit.OccupyStart, unit.OccupyEnd, x.Start, x.End));

                    bool IsAdjacentBoundaryPair(ConflictOccupancyUnit challenger, ConflictOccupancyUnit incumbent)
                    {
                        var a = challenger.PriorityReservation;
                        var b = incumbent.PriorityReservation;
                        return a.StartTime != b.StartTime
                            && (a.EndTime == b.StartTime || b.EndTime == a.StartTime)
                            && UnitOverlaps(challenger, incumbent);
                    }

                    bool IsAdjacentBoundaryOverride(ConflictOccupancyUnit challenger, ConflictOccupancyUnit incumbent)
                    {
                        var a = challenger.PriorityReservation;
                        var b = incumbent.PriorityReservation;
                        return IsAdjacentBoundaryPair(challenger, incumbent)
                            && IsSameService(a, b)
                            && SourcePriorityRank(a) == SourcePriorityRank(b)
                            && CompareFinalUnitPriority(challenger, incumbent) > 0;
                    }

                    string? AdjacentBoundarySkipReason(ConflictOccupancyUnit challenger, ConflictOccupancyUnit incumbent)
                    {
                        var a = challenger.PriorityReservation;
                        var b = incumbent.PriorityReservation;
                        if (!IsAdjacentBoundaryPair(challenger, incumbent))
                            return null;
                        if (!IsSameService(a, b))
                            return "different_service";
                        if (SourcePriorityRank(a) != SourcePriorityRank(b))
                            return "different_priority_layer";
                        if (CompareFinalUnitPriority(challenger, incumbent) <= 0)
                            return "boundary_priority_does_not_select_challenger";
                        return null;
                    }

                    string UnitTraceId(ConflictOccupancyUnit unit)
                        => $"U{unit.UnitId}:R{unit.PriorityReservation.Id}";

                    string? TryReplaceAdjacentBoundaryVictim(ConflictOccupancyUnit challenger)
                    {
                        foreach (var name in candidateNames)
                        {
                            if (!finalIntervals.TryGetValue(name, out var intervals))
                                continue;
                            var overlaps = intervals
                                .Where(x => IntervalOverlaps(challenger.OccupyStart, challenger.OccupyEnd, x.Start, x.End))
                                .ToList();
                            if (overlaps.Count == 0)
                                return name;
                            if (overlaps.Any(x => x.Unit == null))
                                continue;
                            var victims = overlaps.Select(x => x.Unit!).Distinct().ToList();
                            foreach (var victim in victims)
                            {
                                var skipReason = AdjacentBoundarySkipReason(challenger, victim);
                                if (skipReason != null && !string.Equals(skipReason, "boundary_priority_does_not_select_challenger", StringComparison.OrdinalIgnoreCase))
                                {
                                    var clickWinner = UnitUserDecisionTime(challenger) >= UnitUserDecisionTime(victim) ? challenger : victim;
                                    var boundaryWinner = CompareFinalUnitPriority(challenger, victim) >= 0 ? challenger : victim;
                                    AddTrace("FINAL_ADJACENT_BOUNDARY_SKIP", challenger.PriorityReservation,
                                        $"challenger={UnitTraceId(challenger)} incumbent={UnitTraceId(victim)} tuner={name} group={grpKey} sameService={IsSameService(challenger.PriorityReservation, victim.PriorityReservation)} boundary=True marginOverlap=True laterPriority={laterProgramPriority} clickOrderWinner={UnitTraceId(clickWinner)} boundaryPriorityWinner={UnitTraceId(boundaryWinner)} overridden=False reason={skipReason} rule=v0.11.502_settings_genre_editor_spacing_contract");
                                }
                            }
                            if (victims.Count == 0 || victims.Any(v => !IsAdjacentBoundaryOverride(challenger, v)))
                                continue;

                            var remaining = intervals
                                .Where(x => x.Unit == null || !victims.Contains(x.Unit))
                                .ToList();
                            if (remaining.Any(x => IntervalOverlaps(challenger.OccupyStart, challenger.OccupyEnd, x.Start, x.End)))
                                continue;

                            foreach (var victim in victims)
                            {
                                intervals.RemoveAll(x => x.Unit == victim);
                                foreach (var member in victim.Reservations.Where(m => evaluatedIds.Contains(m.Id)))
                                {
                                    finalAssignedIds.Remove(member.Id);
                                    finalTunerAssignment.Remove(member.Id);
                                    finalConflictedIds.Add(member.Id);
                                    AddTrace("FINAL_UNIT_CONFLICT", member,
                                        $"unitKey={victim.UnitKey} unit={victim.UnitId} members={victim.MemberIds} occupy={victim.OccupyStart:MM/dd HH:mm:ss}〜{victim.OccupyEnd:MM/dd HH:mm:ss} group={grpKey} reason=adjacent_boundary_priority_override loserAgainstUnit={challenger.UnitId} candidates={recordingCapacityTuners} displaySource=LogicalTunerDisplayName prioritySourceRank={SourcePriorityRank(victim.PriorityReservation)} userDecision={UnitUserDecisionTime(victim):MM/dd HH:mm:ss} ruleOrder={RuleOrderForUnit(victim)} rule=v0.11.502_settings_genre_editor_spacing_contract");
                                }
                            }
                            var overriddenClickWinners = string.Join(",", victims
                                .Where(v => UnitUserDecisionTime(v) > UnitUserDecisionTime(challenger))
                                .Select(UnitTraceId));
                            if (string.IsNullOrWhiteSpace(overriddenClickWinners))
                                overriddenClickWinners = "-";
                            AddTrace("FINAL_ADJACENT_BOUNDARY_OVERRIDE", challenger.PriorityReservation,
                                $"winner={UnitTraceId(challenger)} losers={string.Join(",", victims.Select(UnitTraceId))} tuner={name} group={grpKey} sameService=True boundary=True marginOverlap=True laterPriority={laterProgramPriority} clickOrderWinnerOverridden={overriddenClickWinners} boundaryPriorityWinner={UnitTraceId(challenger)} overridden=True reason=front_back_priority_overrides_manual_click_order rule=v0.11.502_settings_genre_editor_spacing_contract");
                            return name;
                        }
                        return null;
                    }

                    string? chosen = null;
                    var preferredIsCandidate = !string.IsNullOrWhiteSpace(preferred) && candidateNames.Contains(preferred, StringComparer.OrdinalIgnoreCase);
                    if (preferredIsCandidate && CanUseTuner(preferred!))
                        chosen = preferred;

                    if (chosen == null)
                    {
                        chosen = candidateNames
                            .Where(CanUseTuner)
                            .OrderBy(name => finalIntervals[name]
                                .Where(x => x.End <= unit.OccupyStart)
                                .Select(x => x.End)
                                .DefaultIfEmpty(DateTime.MinValue)
                                .Max())
                            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
                    }

                    if (chosen == null)
                        chosen = TryReplaceAdjacentBoundaryVictim(unit);

                    if (chosen == null)
                    {
                        var blockingUnits = candidateNames
                            .Where(name => finalIntervals.ContainsKey(name))
                            .SelectMany(name => finalIntervals[name]
                                .Where(x => x.Unit != null && IntervalOverlaps(unit.OccupyStart, unit.OccupyEnd, x.Start, x.End))
                                .Select(x => (Tuner: name, Unit: x.Unit!)))
                            .GroupBy(x => x.Unit.UnitKey, StringComparer.OrdinalIgnoreCase)
                            .Select(g => g.First())
                            .ToList();

                        foreach (var blocker in blockingUnits)
                        {
                            var victim = blocker.Unit;
                            if (!IsAdjacentBoundaryPair(unit, victim))
                                continue;

                            var sameService = IsSameService(unit.PriorityReservation, victim.PriorityReservation);
                            var samePriorityLayer = SourcePriorityRank(unit.PriorityReservation) == SourcePriorityRank(victim.PriorityReservation);
                            var clickWinner = UnitUserDecisionTime(unit) >= UnitUserDecisionTime(victim) ? unit : victim;
                            var boundaryWinner = CompareFinalUnitPriority(unit, victim) >= 0 ? unit : victim;
                            var clickWinnerId = UnitTraceId(clickWinner);
                            var boundaryWinnerId = UnitTraceId(boundaryWinner);
                            var overridden = !string.Equals(clickWinnerId, boundaryWinnerId, StringComparison.OrdinalIgnoreCase);

                            if (sameService && samePriorityLayer)
                            {
                                var reason = overridden
                                    ? "front_back_priority_overrides_manual_click_order"
                                    : "front_back_priority_applied_without_click_order_change";
                                AddTrace("FINAL_ADJACENT_BOUNDARY_OVERRIDE", unit.PriorityReservation,
                                    $"challenger={UnitTraceId(unit)} incumbent={UnitTraceId(victim)} tuner={blocker.Tuner} group={grpKey} sameService=True boundary=True marginOverlap=True laterPriority={laterProgramPriority} clickOrderWinner={clickWinnerId} boundaryPriorityWinner={boundaryWinnerId} overridden={overridden} reason={reason} rule=v0.11.502_settings_genre_editor_spacing_contract");
                            }
                            else
                            {
                                var reason = !sameService
                                    ? "different_service"
                                    : "different_priority_layer";
                                AddTrace("FINAL_ADJACENT_BOUNDARY_SKIP", unit.PriorityReservation,
                                    $"challenger={UnitTraceId(unit)} incumbent={UnitTraceId(victim)} tuner={blocker.Tuner} group={grpKey} sameService={sameService} boundary=True marginOverlap=True laterPriority={laterProgramPriority} clickOrderWinner={clickWinnerId} boundaryPriorityWinner={boundaryWinnerId} overridden=False reason={reason} rule=v0.11.502_settings_genre_editor_spacing_contract");
                            }
                        }

                        var overlapSummary = string.Join("|", candidateNames.Select(name =>
                        {
                            var overlaps = finalIntervals[name]
                                .Where(x => IntervalOverlaps(unit.OccupyStart, unit.OccupyEnd, x.Start, x.End))
                                .Select(x => x.Unit == null ? $"{name}:external" : $"{name}:{x.Unit.UnitKey}")
                                .ToList();
                            return overlaps.Count == 0 ? $"{name}:free" : string.Join(",", overlaps);
                        }));
                        foreach (var member in unit.Reservations.Where(m => evaluatedIds.Contains(m.Id)))
                        {
                            finalConflictedIds.Add(member.Id);
                            AddTrace("FINAL_UNIT_CONFLICT", member,
                                $"unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} group={grpKey} reason=no_free_recording_tuner_after_priority_plan candidates={recordingCapacityTuners} displaySource=LogicalTunerDisplayName prioritySourceRank={SourcePriorityRank(unit.PriorityReservation)} userDecision={UnitUserDecisionTime(unit):MM/dd HH:mm:ss} ruleOrder={RuleOrderForUnit(unit)} overlaps={overlapSummary} rule=v0.11.502_settings_genre_editor_spacing_contract");
                        }
                        continue;
                    }

                    var previousFreeAt = finalIntervals[chosen]
                        .Where(x => x.End <= unit.OccupyStart)
                        .Select(x => x.End)
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max();
                    finalIntervals[chosen].Add((unit.OccupyStart, unit.OccupyEnd, unit, "final_unit_plan"));
                    finalIntervals[chosen].Sort((a, b) => a.Start.CompareTo(b.Start));
                    foreach (var member in unit.Reservations.Where(m => evaluatedIds.Contains(m.Id)))
                    {
                        finalAssignedIds.Add(member.Id);
                        finalTunerAssignment[member.Id] = chosen;
                    }
                    AddTrace(unit.IsUserChain ? "FINAL_CHAIN_UNIT_ASSIGN" : "FINAL_UNIT_ASSIGN", unit.PriorityReservation,
                        $"unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} chainRoot=R{(unit.ChainRootId.HasValue ? unit.ChainRootId.Value.ToString() : "-")} tuner={chosen} previousFreeAt={previousFreeAt:MM/dd HH:mm:ss} occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} group={grpKey} capacitySource=RoleBinding/Recording recordingTuners={recordingCapacityTuners} displaySource=LogicalTunerDisplayName prioritySourceRank={SourcePriorityRank(unit.PriorityReservation)} userDecision={UnitUserDecisionTime(unit):MM/dd HH:mm:ss} ruleOrder={RuleOrderForUnit(unit)} result=ASSIGNED rule=v0.11.678_rolebinding_recording_tuner_display_contract");
                    if (unit.IsUserChain)
                    {
                        chainUnitAssignedTuner[unit.UnitKey] = chosen;
                        AddTrace("CHAIN_UNIT_TUNER_ASSIGN", unit.PriorityReservation,
                            $"unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} chainRoot=R{(unit.ChainRootId.HasValue ? unit.ChainRootId.Value.ToString() : "-")} tuner={chosen} unitOccupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} capacitySource=RoleBinding/Recording candidates={recordingCapacityTuners} displaySource=LogicalTunerDisplayName reason=final_unit_plan_selected result=ASSIGNED source=final_unit_plan rule=v0.11.678_rolebinding_recording_tuner_display_contract");
                    }
                }
            }

            foreach (var id in evaluatedIds)
            {
                conflictedIds.Remove(id);
                assignedIds.Remove(id);
                tunerAssignment.Remove(id);
            }
            foreach (var id in finalAssignedIds)
            {
                assignedIds.Add(id);
                conflictedIds.Remove(id);
            }
            foreach (var id in finalConflictedIds)
            {
                conflictedIds.Add(id);
                assignedIds.Remove(id);
            }
            foreach (var kv in finalTunerAssignment)
                tunerAssignment[kv.Key] = kv.Value;
            AddTrace("FINAL_CONFLICT_PLAN_END", new Reservation { Id = 0, Title = "FinalConflictPlan" },
                $"assigned={finalAssignedIds.Count} conflicted={finalConflictedIds.Count} tunerAssignments={finalTunerAssignment.Count} rule=v0.11.502_settings_genre_editor_spacing_contract");
        }

        ReconcileFinalConflictPlanByUnitKey();

        // 空欄枠から作成されたプログラム予約のうち、完全一致の正規イベント予約が存在するものは
        // 事前のAutoDeleteResolvedProgramGuideMissingDuplicatesで削除済み。
        // ここでは、時刻差・非削除条件などで残った空欄Programだけを競合へ落とす。
        // UI説明は追加せず、競合ラベルの色分けに必要な内部状態だけを維持する。
        var activeForDuplicate = scheduled
            .Where(x => x.IsEnabled)
            .Where(x => x.Status == ReservationStatus.Scheduled || x.Status == ReservationStatus.Recording)
            .ToList();
        foreach (var missing in activeForDuplicate.Where(IsProgramGuideMissingReservation))
        {
            var partner = activeForDuplicate
                .Where(x => x.Id != missing.Id)
                .Where(IsRegularResolvedReservation)
                .Where(x => x.NetworkId == missing.NetworkId
                    && x.TransportStreamId == missing.TransportStreamId
                    && x.ServiceId == missing.ServiceId)
                .Where(x => HasBroadcastTimeOverlap(missing, x))
                .OrderBy(x => x.StartTime)
                .ThenBy(x => x.Id)
                .FirstOrDefault();
            if (partner is null) continue;
            conflictedIds.Add(missing.Id);
            assignedIds.Remove(missing.Id);
            tunerAssignment.Remove(missing.Id);
            AddTrace("MISSING_DUPLICATE_CONFLICT", missing, $"duplicateWith=R{partner.Id} service=[{missing.ServiceName}] start={missing.StartTime:MM/dd HH:mm} end={missing.EndTime:MM/dd HH:mm} origin=ProgramGuideMissingProgramRule identity=TimeIdentity commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.378_programguide_missing_duplicate_cleanup_trace_residue");
        }

        // DBを更新（変化があった予約のみ）・変化内容をログ用に収集
        var changes = new List<(int Id, string ServiceName, string Title, bool Conflicted)>();
        var scheduledIds = scheduled.Select(x => x.Id).ToHashSet();
        foreach (var r in allScheduled.Where(x => x.Source != ReservationSource.Epg))
        {
            var includedInEvaluation = scheduledIds.Contains(r.Id);
            var newConflicted = includedInEvaluation && conflictedIds.Contains(r.Id);
            var newTuner = (!includedInEvaluation || newConflicted) ? "" :
                (tunerAssignment.TryGetValue(r.Id, out var tn) ? tn : "");

            if (r.TunerName != newTuner)
                UpdateTunerName(r.Id, newTuner);
            if (r.IsConflicted != newConflicted)
            {
                UpdateConflicted(r.Id, newConflicted);
                changes.Add((r.Id, r.ServiceName, r.Title, newConflicted));
            }
        }

        WriteTunerAllocationDebugSnapshot(
            allScheduled,
            scheduled,
            tunerProfiles,
            tunerCountByGroup,
            assignedIds,
            conflictedIds,
            tunerAssignment,
            chainPredecessors,
            chainSuccessors,
            laterProgramPriority,
            pseudoContinuous,
            configuredPseudoContinuous,
            chainModeEnabled,
            userChainCandidatePairs,
            preStartMarginSeconds,
            postEndMarginSeconds,
            debugTrace);

        return changes;
    }


    public string GetTunerAllocationDebugPath()
    {
        var dir = Path.Combine(db.DataDirectory, "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "tuner_allocation_debug.json");
    }

    public string? ReadTunerAllocationDebugJson()
    {
        var path = GetTunerAllocationDebugPath();
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private void WriteTunerAllocationDebugSnapshot(
        IReadOnlyList<Reservation> allScheduled,
        IReadOnlyList<Reservation> scheduled,
        IReadOnlyList<Core.TunerProfile> tunerProfiles,
        IReadOnlyDictionary<string, int> tunerCountByGroup,
        IReadOnlySet<int> assignedIds,
        IReadOnlySet<int> conflictedIds,
        IReadOnlyDictionary<int, string> tunerAssignment,
        IReadOnlyDictionary<int, int> chainPredecessors,
        IReadOnlyDictionary<int, int> chainSuccessors,
        bool laterProgramPriority,
        bool pseudoContinuous,
        bool configuredPseudoContinuous,
        bool chainModeEnabled,
        int userChainCandidatePairs,
        int preStartMarginSeconds,
        int postEndMarginSeconds,
        IReadOnlyList<TunerAllocationDebugTraceEntry> trace)
    {
        // 視聴用チューナーは録画・EPG取得の割当対象から外す。
        // デバッグ出力上の上限・候補数にも録画用チューナーだけを反映する。
        var recordingTunerProfiles = tunerProfiles
            .Where(p => !string.Equals(IniSettingsService.NormalizeTunerRole(p.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .ToList();

        static string ResolveGroup(Reservation r)
        {
            if (!string.IsNullOrWhiteSpace(r.ChannelArgument))
                return r.ChannelArgument.Contains("/chspace", StringComparison.OrdinalIgnoreCase)
                    ? "BSCS" : "GR";

            var hint = $"{r.TunerName} {r.Title} {r.ServiceName}";
            if (!string.IsNullOrWhiteSpace(hint))
            {
                if (hint.Contains("BS/CS", StringComparison.OrdinalIgnoreCase)
                    || hint.Contains("ＢＳ", StringComparison.Ordinal)
                    || hint.Contains("ＣＳ", StringComparison.Ordinal))
                {
                    return "BSCS";
                }

                if (hint.Contains("地上波", StringComparison.OrdinalIgnoreCase))
                {
                    return "GR";
                }
            }

            return r.NetworkId == 4 ? "BSCS" : "GR";
        }

        var preMargin = TimeSpan.FromSeconds(preStartMarginSeconds);
        var postMargin = TimeSpan.FromSeconds(postEndMarginSeconds);
        DateTime OccupyStart(Reservation r) => r.StartTime - preMargin;
        DateTime OccupyEnd(Reservation r) => r.EndTime + postMargin;

        var snapshot = new TunerAllocationDebugSnapshot
        {
            GeneratedAt = DateTime.Now,
            Settings = new TunerAllocationDebugSettings
            {
                LaterProgramPriority = laterProgramPriority,
                PseudoContinuousRecording = pseudoContinuous,
                ConfiguredPseudoContinuousRecording = configuredPseudoContinuous,
                ChainModeEnabled = chainModeEnabled,
                UserChainCandidatePairs = userChainCandidatePairs,
                PreStartMarginSeconds = preStartMarginSeconds,
                PostEndMarginSeconds = postEndMarginSeconds
            },
            TunerLimits = tunerCountByGroup.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
        };

        foreach (var tp in recordingTunerProfiles.GroupBy(t => t.Group, StringComparer.OrdinalIgnoreCase))
            snapshot.TunerLimits[tp.Key] = tp.Count();

        snapshot.Trace.AddRange(trace);

        var evaluable = scheduled
            .Where(r => r.Source != ReservationSource.Epg && r.IsEnabled)
            .OrderBy(r => OccupyStart(r))
            .ThenBy(r => r.StartTime)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToList();

        int groupIndex = 1;
        foreach (var grp in evaluable.GroupBy(ResolveGroup))
        {
            TunerAllocationDebugGroup? current = null;
            foreach (var r in grp)
            {
                var oStart = OccupyStart(r);
                var oEnd = OccupyEnd(r);
                if (current == null || oStart >= current.OccupyEnd)
                {
                    current = new TunerAllocationDebugGroup
                    {
                        GroupIndex = groupIndex++,
                        Wave = grp.Key,
                        Limit = snapshot.TunerLimits.TryGetValue(grp.Key, out var limit) ? limit : 0,
                        OccupyStart = oStart,
                        OccupyEnd = oEnd
                    };
                    snapshot.Groups.Add(current);
                }
                else if (oEnd > current.OccupyEnd)
                {
                    current.OccupyEnd = oEnd;
                }

                current.Events.Add(new TunerAllocationDebugEvent
                {
                    ReservationId = r.Id,
                    DisplayNo = r.Id,
                    Source = r.Source,
                    IsEnabled = r.IsEnabled,
                    Status = r.Status.ToString(),
                    Group = grp.Key,
                    Title = r.Title,
                    ServiceName = r.ServiceName,
                    ServiceId = r.ServiceId,
                    NetworkId = r.NetworkId,
                    TransportStreamId = r.TransportStreamId,
                    EventId = r.EventId,
                    StartTime = r.StartTime,
                    EndTime = r.EndTime,
                    OccupyStart = oStart,
                    OccupyEnd = oEnd,
                    CreatedAt = r.CreatedAt,
                    UpdatedAt = r.UpdatedAt,
                    TunerName = tunerAssignment.TryGetValue(r.Id, out var tn) ? tn : string.Empty,
                    Result = conflictedIds.Contains(r.Id) ? "CONFLICT" : assignedIds.Contains(r.Id) ? "ALLOCATED" : "UNRESOLVED",
                    Reason = conflictedIds.Contains(r.Id) ? "tuner_limit_exceeded" : assignedIds.Contains(r.Id) ? "assigned" : "not_assigned",
                    IsConflicted = conflictedIds.Contains(r.Id),
                    ChainPredecessorId = chainPredecessors.TryGetValue(r.Id, out var pred) ? pred : null,
                    ChainSuccessorId = chainSuccessors.TryGetValue(r.Id, out var succ) ? succ : null,
                    SourceRuleId = r.SourceRuleId,
                    SourceRuleName = r.SourceRuleName ?? string.Empty
                });
            }
        }

        foreach (var r in allScheduled
            .Where(r => r.Source == ReservationSource.Epg || !r.IsEnabled)
            .OrderBy(r => r.StartTime).ThenBy(r => r.Id))
        {
            var group = ResolveGroup(r);
            snapshot.Skipped.Add(new TunerAllocationDebugEvent
            {
                ReservationId = r.Id,
                DisplayNo = r.Id,
                Source = r.Source,
                IsEnabled = r.IsEnabled,
                Status = r.Status.ToString(),
                Group = group,
                Title = r.Title,
                ServiceName = r.ServiceName,
                ServiceId = r.ServiceId,
                NetworkId = r.NetworkId,
                TransportStreamId = r.TransportStreamId,
                EventId = r.EventId,
                StartTime = r.StartTime,
                EndTime = r.EndTime,
                OccupyStart = OccupyStart(r),
                OccupyEnd = OccupyEnd(r),
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                TunerName = r.TunerName ?? string.Empty,
                Result = r.Source == ReservationSource.Epg ? "SKIPPED_EPG" : "SKIPPED_DISABLED",
                Reason = r.Source == ReservationSource.Epg ? "epg_source_excluded" : "disabled_by_user",
                IsConflicted = false,
                ChainPredecessorId = chainPredecessors.TryGetValue(r.Id, out var pred) ? pred : null,
                ChainSuccessorId = chainSuccessors.TryGetValue(r.Id, out var succ) ? succ : null,
                SourceRuleId = r.SourceRuleId,
                SourceRuleName = r.SourceRuleName ?? string.Empty
            });
        }

        snapshot.Summary = new TunerAllocationDebugSummary
        {
            EvaluatedCount = evaluable.Count,
            AllocatedCount = assignedIds.Count,
            ConflictCount = conflictedIds.Count,
            SkippedCount = snapshot.Skipped.Count
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        var path = GetTunerAllocationDebugPath();
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, options));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);

        WriteTunerAllocationDebugLog(snapshot);
    }

    private void WriteTunerAllocationDebugLog(TunerAllocationDebugSnapshot snapshot)
    {
        log.Add("TUNER_ALLOC_SUMMARY", "Summary",
            $"evaluated={snapshot.Summary.EvaluatedCount} allocated={snapshot.Summary.AllocatedCount} conflict={snapshot.Summary.ConflictCount} skipped={snapshot.Summary.SkippedCount} laterPriority={snapshot.Settings.LaterProgramPriority} chain={snapshot.Settings.PseudoContinuousRecording} preStart={snapshot.Settings.PreStartMarginSeconds}s postEnd={snapshot.Settings.PostEndMarginSeconds}s");
        log.Add("TUNER_PRIORITY_CONTRACT", "Summary",
            $"sourcePriority=ProgramGuideLike(Manual,Immediate,KeywordSearch)>AutoSearch(Keyword)>Program activeChainExplicitOnly=True overlapUsesMargins=True" +
            $" sameServiceBoundaryPriority=True peerProgramGuideUsesLatestUserDecision=True autoSearchUsesRuleSortOrder=True programUsesRuleOrder=True capacityFirst=True" +
            $" laterPriority={snapshot.Settings.LaterProgramPriority} chain={snapshot.Settings.PseudoContinuousRecording}" +
            $" evaluated={snapshot.Summary.EvaluatedCount} conflict={snapshot.Summary.ConflictCount} rule=v0.11.502_settings_genre_editor_spacing_contract");
        log.Add("TUNER_CONFLICT_VISIBILITY_CONTRACT", "Summary",
            $"candidatePreflight=False conflictVisibleAfterReservation=True programGuideUnreservedAction=reserve" +
            $" finalSource=ReconcileFinalConflictPlanByUnitKey commonRoute=ALLOC_ROUTE/TUNER_ALLOC" +
            $" finalPriorityAxis=rebuilt capacityFirst=True adjacentBoundaryOverridesClickOrder=True evaluated={snapshot.Summary.EvaluatedCount} conflict={snapshot.Summary.ConflictCount} rule=v0.11.502_settings_genre_editor_spacing_contract");

        // v0.11.456:
        // 競合表示は、ユーザーが予約操作を確定して予約レコード化した後の状態表示である。
        // 未予約番組表セルに「予約すると競合」を予防表示する仮想競合投影は作らない。
        // ただし、予約後の判定は ReconcileFinalConflictPlanByUnitKey / ALLOC_ROUTE/TUNER_ALLOC を正本にする。
        // v0.11.455:
        // FINAL_* は ReconcileFinalConflictPlanByUnitKey の最終正本だけを可視化する。
        // 旧 occupancy_unit_evaluator_mirror は中間観測なので OCCUPANCY_* に格下げし、
        // FINAL_UNIT_CONFLICT / FINAL_UNIT_ASSIGN としては出さない。
        foreach (var t in snapshot.Trace.Where(t => string.Equals(t.Stage, "CHAIN_OCCUPANCY_UNIT", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "CHAIN_OCCUPANCY_APPLY", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "CHAIN_UNIT_TUNER_ASSIGN", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "CHAIN_UNIT_TUNER_DEFER", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_UNIT_PLAN_SUMMARY", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_CHAIN_UNIT_ASSIGN", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_UNIT_ASSIGN", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_UNIT_EXTERNAL_OCCUPY", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_UNIT_CONFLICT", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_ADJACENT_BOUNDARY_OVERRIDE", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_ADJACENT_BOUNDARY_SKIP", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_CONFLICT_PLAN_BEGIN", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_CONFLICT_PLAN_END", StringComparison.OrdinalIgnoreCase)))
        {
            log.Add(t.Stage, $"R{t.ReservationId}",
                $"group={t.Group} title={TrimTitleForAudit(t.Title)} rawTitleBlank={RawTitleBlankForAudit(t.Title)} {t.Detail}");
        }

        foreach (var t in snapshot.Trace.Where(t => string.Equals(t.Stage, "CHAIN_ALLOC_LOCK", StringComparison.OrdinalIgnoreCase)))
        {
            log.Add("CHAIN_ALLOC_LOCK", $"R{t.ReservationId}",
                $"group={t.Group} title={TrimTitleForAudit(t.Title)} rawTitleBlank={RawTitleBlankForAudit(t.Title)} {t.Detail} rule=common_allocation_route_contract");
        }

        // v0.3.43: 通常運用ログの静音化。
        // 割当結果の全件 TUNER_ALLOC / TUNER_TRACE は Tick ごとに数十件発生し、
        // 画面ポーリングやEPG取得中の体感負荷・ログ可読性を悪化させるため通常出力しない。
        // 競合だけは実運用上の確認対象なので残す。
        foreach (var group in snapshot.Groups)
        {
            foreach (var e in group.Events.Where(e => e.Result == "CONFLICT"))
            {
                var chain = e.ChainPredecessorId.HasValue || e.ChainSuccessorId.HasValue
                    ? $" chainPrev={e.ChainPredecessorId?.ToString() ?? "-"} chainNext={e.ChainSuccessorId?.ToString() ?? "-"}"
                    : string.Empty;
                var tuner = string.IsNullOrWhiteSpace(e.TunerName) ? "-" : e.TunerName;
                log.Add("TUNER_CONFLICT", "Conflict",
                    $"service={e.ServiceName} title={TrimTitleForAudit(e.Title)} rawTitleBlank={RawTitleBlankForAudit(e.Title)} res=R{e.ReservationId} source={e.Source} enabled={e.IsEnabled} tuner={tuner} result={e.Result} reason={e.Reason} group={e.Group} svcId={e.ServiceId} occupy={e.OccupyStart:MM/dd HH:mm:ss}〜{e.OccupyEnd:MM/dd HH:mm:ss}{chain}");
                log.Add("TUNER_CONFLICT_MIRROR", $"R{e.ReservationId}",
                    $"stage=tuner_conflict_mirror service={e.ServiceName} title={TrimTitleForAudit(e.Title)} rawTitleBlank={RawTitleBlankForAudit(e.Title)} res=R{e.ReservationId} source={e.Source} enabled={e.IsEnabled} tuner={tuner} result={e.Result} reason={e.Reason} group={e.Group} svcId={e.ServiceId} occupy={e.OccupyStart:MM/dd HH:mm:ss}〜{e.OccupyEnd:MM/dd HH:mm:ss}{chain} source=TUNER_CONFLICT_MIRROR rule=v0.11.502_settings_genre_editor_spacing_contract");
            }
        }


        // v0.5.66:
        // 予約ON/OFFを連続操作すると、無効化済み予約の TUNER_SKIP が毎回全件出力され、
        // ログI/Oだけで視聴中のLIVETestへ体感負荷が出る。通常は件数サマリだけにし、
        // disabled_by_user / epg_source_excluded 以外の未知スキップだけ少数サンプルを残す。
        var skipped = snapshot.Skipped.ToList();
        if (skipped.Count > 0)
        {
            var disabled = skipped.Count(e => string.Equals(e.Reason, "disabled_by_user", StringComparison.OrdinalIgnoreCase));
            var epgExcluded = skipped.Count(e => string.Equals(e.Reason, "epg_source_excluded", StringComparison.OrdinalIgnoreCase));
            var other = skipped.Count - disabled - epgExcluded;
            log.Add("TUNER_SKIP_SUMMARY", "Summary",
                $"count={skipped.Count} disabled={disabled} epgExcluded={epgExcluded} other={other} rule=v0.5.66_skip_log_quiet");

            foreach (var e in skipped.Where(e => !string.Equals(e.Reason, "disabled_by_user", StringComparison.OrdinalIgnoreCase)
                                             && !string.Equals(e.Reason, "epg_source_excluded", StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                log.Add("TUNER_SKIP", "Skip",
                    $"service={e.ServiceName} title={TrimTitleForAudit(e.Title)} rawTitleBlank={RawTitleBlankForAudit(e.Title)} res=R{e.ReservationId} source={e.Source} enabled={e.IsEnabled} result={e.Result} reason={e.Reason} group={e.Group} svcId={e.ServiceId} occupy={e.OccupyStart:MM/dd HH:mm:ss}〜{e.OccupyEnd:MM/dd HH:mm:ss} rule=v0.5.78_service_first_skip_log");
            }
        }
    }

    /// <summary>
    /// 同一番組（network_id + ts_id + service_id + event_id）が
    /// 既にscheduled/recordingで登録されているか確認する（重複排除用）。
    /// </summary>
    public bool ExistsByEvent(ushort networkId, ushort tsId, ushort serviceId, ushort eventId)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM reservations
            WHERE network_id = $nid AND transport_stream_id = $tsid
              AND service_id = $sid AND event_id = $eid
              AND status IN ('scheduled','recording');
            """;
        cmd.Parameters.AddWithValue("$nid",  networkId);
        cmd.Parameters.AddWithValue("$tsid", tsId);
        cmd.Parameters.AddWithValue("$sid",  serviceId);
        cmd.Parameters.AddWithValue("$eid",  eventId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    // ─── 自動検索予約の今回限り取消抑止 ───────────────────────────────

    /// <summary>
    /// 自動検索予約をユーザーが「予約取消」した場合、その放送回だけ再自動予約を抑止する。
    /// ルール自体は消さず、番組終了後に抑止キーを自動削除する。
    /// </summary>
    public void AddKeywordCancelOnce(Reservation r)
    {
        AddKeywordSuppression(r, "reservation_cancel_once", emitLog: false);
    }

    public void AddKeywordSuppressionForUserDisabled(Reservation r, string reason)
    {
        AddKeywordSuppression(r, reason, emitLog: true);
    }

    public void RemoveKeywordSuppressionForUserEnabled(Reservation r, string reason)
    {
        if (r.Source != ReservationSource.Keyword || !r.SourceRuleId.HasValue)
            return;

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            DELETE FROM keyword_cancel_once
            WHERE rule_id = $rule
              AND network_id = $nid
              AND transport_stream_id = $tsid
              AND service_id = $sid
              AND start_time = $start
              AND end_time = $end
              AND title_hash = $hash;
            """;
        cmd.Parameters.AddWithValue("$rule", r.SourceRuleId.Value);
        cmd.Parameters.AddWithValue("$nid", r.NetworkId);
        cmd.Parameters.AddWithValue("$tsid", r.TransportStreamId);
        cmd.Parameters.AddWithValue("$sid", r.ServiceId);
        cmd.Parameters.AddWithValue("$start", r.StartTime.ToString("O"));
        cmd.Parameters.AddWithValue("$end", r.EndTime.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", BuildTitleHash(r.Title));
        var deleted = cmd.ExecuteNonQuery();

        log.Add("KEYWORD_SUPPRESS", "UserEnabled",
            $"service={TrimForAudit(r.ServiceName)} title={TrimTitleForAudit(r.Title)} rawTitleBlank={RawTitleBlankForAudit(r.Title)} result={(deleted > 0 ? "DELETE" : "NONE")} reason={reason} ruleId={r.SourceRuleId.Value} res=R{r.Id} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} rule=v0.5.78_keyword_hit_disable_persist_service_first");
    }

    private void AddKeywordSuppression(Reservation r, string reason, bool emitLog)
    {
        if (r.Source != ReservationSource.Keyword || !r.SourceRuleId.HasValue)
            return;

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO keyword_cancel_once
              (rule_id, network_id, transport_stream_id, service_id,
               start_time, end_time, title_hash, expires_at, created_at)
            VALUES
              ($rule, $nid, $tsid, $sid,
               $start, $end, $hash, $expires, $created);
            """;
        cmd.Parameters.AddWithValue("$rule", r.SourceRuleId.Value);
        cmd.Parameters.AddWithValue("$nid", r.NetworkId);
        cmd.Parameters.AddWithValue("$tsid", r.TransportStreamId);
        cmd.Parameters.AddWithValue("$sid", r.ServiceId);
        cmd.Parameters.AddWithValue("$start", r.StartTime.ToString("O"));
        cmd.Parameters.AddWithValue("$end", r.EndTime.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", BuildTitleHash(r.Title));
        cmd.Parameters.AddWithValue("$expires", r.EndTime.ToString("O"));
        cmd.Parameters.AddWithValue("$created", DateTime.Now.ToString("O"));
        var inserted = cmd.ExecuteNonQuery();

        if (emitLog)
        {
            log.Add("KEYWORD_SUPPRESS", "UserDisabled",
                $"service={TrimForAudit(r.ServiceName)} title={TrimTitleForAudit(r.Title)} rawTitleBlank={RawTitleBlankForAudit(r.Title)} result={(inserted > 0 ? "INSERT" : "EXISTS")} reason={reason} ruleId={r.SourceRuleId.Value} res=R{r.Id} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} rule=v0.5.78_keyword_hit_disable_persist_service_first");
        }
    }

    /// <summary>自動検索の再マッチ時に、今回限り取消済みの放送回かどうかを軽量キーで判定する。</summary>
    public bool IsKeywordCancelOnceSuppressed(int ruleId, EpgEvent ev)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM keyword_cancel_once
            WHERE rule_id = $rule
              AND network_id = $nid
              AND transport_stream_id = $tsid
              AND service_id = $sid
              AND start_time = $start
              AND end_time = $end
              AND title_hash = $hash
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$rule", ruleId);
        cmd.Parameters.AddWithValue("$nid", ev.NetworkId);
        cmd.Parameters.AddWithValue("$tsid", ev.TransportStreamId);
        cmd.Parameters.AddWithValue("$sid", ev.ServiceId);
        cmd.Parameters.AddWithValue("$start", ev.Start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", ev.End.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", BuildTitleHash(ev.Title));
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>番組終了済みの今回限り取消キーを削除する。運用ログには出さない。</summary>
    public int PurgeExpiredKeywordCancelOnce(DateTime? now = null)
    {
        var t = (now ?? DateTime.Now).ToString("O");
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM keyword_cancel_once WHERE expires_at < $now;";
        cmd.Parameters.AddWithValue("$now", t);
        return cmd.ExecuteNonQuery();
    }

    private static string BuildTitleHash(string? title)
    {
        var normalized = (title ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    // ─── キーワードルール CRUD ────────────────────────────────────

    public IReadOnlyList<KeywordRule> GetKeywordRules()
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, pattern, exclude_pattern, use_regex,
                   search_fields, search_title, search_outline, search_detail, search_cast,
                   use_all_channels, target_services, target_genres, target_days, use_time_range, start_time, end_time, sort_order,
                   expires_on, enabled, created_at, updated_at
            FROM keyword_rules ORDER BY sort_order, id;
            """;
        return ReadKeywordRules(cmd);
    }

    public int AddKeywordRule(KeywordRule r)
    {
        var now = DateTime.Now.ToString("O");
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO keyword_rules
              (name, pattern, exclude_pattern, use_regex, search_fields,
               search_title, search_outline, search_detail, search_cast,
               use_all_channels, target_services, target_genres, target_days, use_time_range, start_time, end_time, sort_order,
               expires_on, enabled, created_at, updated_at)
            VALUES
              ($name, $pat, $exc, $regex, $fields,
               $stitle, $soutline, $sdetail, $scast,
               $useAllChannels, $svc, $genres, $days, $useTime, $startTime, $endTime,
               COALESCE((SELECT MAX(sort_order) + 1 FROM keyword_rules), 1),
               $exp, $en, $now, $now);
            SELECT last_insert_rowid();
            """;
        BindKeywordRule(cmd, r, now);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateKeywordRule(KeywordRule r)
    {
        var now = DateTime.Now.ToString("O");
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE keyword_rules SET
              name=$name, pattern=$pat, exclude_pattern=$exc, use_regex=$regex,
              search_fields=$fields, search_title=$stitle, search_outline=$soutline,
              search_detail=$sdetail, search_cast=$scast,
              use_all_channels=$useAllChannels,
              target_services=$svc, target_genres=$genres, target_days=$days,
              use_time_range=$useTime, start_time=$startTime, end_time=$endTime,
              expires_on=$exp, enabled=$en, updated_at=$now
            WHERE id=$id;
            """;
        BindKeywordRule(cmd, r, now);
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteKeywordRule(int id)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM keyword_rules WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }


    /// <summary>
    /// 指定した親予約に紐づく録画前EPG確認(SystemEpg)をキャンセルする。
    /// 番組表や予約一覧から親予約を取り消した後に、内部用のEPG確認だけが残って
    /// セル状態やWake計画へ混入することを防ぐ。
    /// </summary>
    public int CancelScheduledPreRecordEpgChildrenForParents(IEnumerable<int> parentIds)
    {
        var ids = parentIds.Distinct().ToList();
        if (ids.Count == 0) return 0;

        using var con = db.Open();
        var total = 0;
        foreach (var parentId in ids)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                UPDATE reservations
                SET status = 'cancelled', updated_at = $now
                WHERE source = 'epg'
                  AND status = 'scheduled'
                  AND source_rule_id = $parentId
                  AND (source_rule_name = 'PreRecEpg' OR title LIKE 'EPG確認%' OR title LIKE '録画前EPG確認%');
                """;
            cmd.Parameters.AddWithValue("$parentId", parentId);
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
            total += cmd.ExecuteNonQuery();
        }
        return total;
    }

    /// <summary>
    /// 指定ルール由来の「scheduled」ステータスの自動検索予約のみを物理削除する。
    /// ルール更新・無効化・削除時に「リアルタイム整合性」を保つためのメソッド。
    /// 
    /// 削除対象は以下の全条件を満たす予約のみ：
    ///   ・source = 'keyword'
    ///   ・status = 'scheduled'
    ///   ・source_rule_id = 指定ID
    /// 
    /// recording / completed / failed / cancelled は一切触らない。
    /// 録画中(recording)の予約はユーザーの能動的な録画行為なのでTvAIrは止めない。
    /// </summary>
    /// <returns>削除した件数</returns>
    public int DeleteScheduledByRuleId(int ruleId)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            DELETE FROM reservations
            WHERE source = 'keyword'
              AND status = 'scheduled'
              AND source_rule_id = $ruleId;
            """;
        cmd.Parameters.AddWithValue("$ruleId", ruleId);
        return cmd.ExecuteNonQuery();
    }

    public int DeleteAllScheduledKeywordReservations()
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            DELETE FROM reservations
            WHERE source = 'keyword'
              AND status = 'scheduled';
            """;
        return cmd.ExecuteNonQuery();
    }

    public void ReplaceKeywordRules(IReadOnlyList<KeywordRule> rules)
    {
        using var con = db.Open();
        using var tx = con.BeginTransaction();

        using (var del = con.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM keyword_rules;";
            del.ExecuteNonQuery();
        }

        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO keyword_rules
                  (id, name, pattern, exclude_pattern, use_regex, search_fields,
                   search_title, search_outline, search_detail, search_cast,
                   use_all_channels, target_services, target_genres, target_days, use_time_range, start_time, end_time, sort_order,
                   expires_on, enabled, created_at, updated_at)
                VALUES
                  ($id, $name, $pat, $exc, $regex, $fields,
                   $stitle, $soutline, $sdetail, $scast,
                   $useAllChannels, $svc, $genres, $days, $useTime, $startTime, $endTime, $sortOrder,
                   $exp, $en, $createdAt, $updatedAt);
                """;
            cmd.Parameters.AddWithValue("$id", r.Id > 0 ? r.Id : i + 1);
            cmd.Parameters.AddWithValue("$name", r.Name ?? "");
            cmd.Parameters.AddWithValue("$pat", r.Pattern ?? "");
            cmd.Parameters.AddWithValue("$exc", r.ExcludePattern ?? "");
            cmd.Parameters.AddWithValue("$regex", r.UseRegex ? 1 : 0);
            cmd.Parameters.AddWithValue("$fields", r.SearchFields ?? "title");
            cmd.Parameters.AddWithValue("$stitle", r.SearchTitle ? 1 : 0);
            cmd.Parameters.AddWithValue("$soutline", r.SearchOutline ? 1 : 0);
            cmd.Parameters.AddWithValue("$sdetail", r.SearchDetail ? 1 : 0);
            cmd.Parameters.AddWithValue("$scast", r.SearchCast ? 1 : 0);
            cmd.Parameters.AddWithValue("$useAllChannels", r.UseAllChannels ? 1 : 0);
            cmd.Parameters.AddWithValue("$svc", r.TargetServices ?? "");
            cmd.Parameters.AddWithValue("$genres", r.TargetGenres ?? "");
            cmd.Parameters.AddWithValue("$days", r.TargetDays ?? "");
            cmd.Parameters.AddWithValue("$useTime", r.UseTimeRange ? 1 : 0);
            cmd.Parameters.AddWithValue("$startTime", string.IsNullOrWhiteSpace(r.StartTime) ? "00:00" : r.StartTime);
            cmd.Parameters.AddWithValue("$endTime", string.IsNullOrWhiteSpace(r.EndTime) ? "23:59" : r.EndTime);
            cmd.Parameters.AddWithValue("$sortOrder", i + 1);
            cmd.Parameters.AddWithValue("$exp", r.ExpiresOn ?? "");
            cmd.Parameters.AddWithValue("$en", r.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$createdAt", (r.CreatedAt == default ? DateTime.Now : r.CreatedAt).ToString("O"));
            cmd.Parameters.AddWithValue("$updatedAt", (r.UpdatedAt == default ? DateTime.Now : r.UpdatedAt).ToString("O"));
            cmd.ExecuteNonQuery();
        }

        using (var seq = con.CreateCommand())
        {
            seq.Transaction = tx;
            seq.CommandText = """
                DELETE FROM sqlite_sequence WHERE name='keyword_rules';
                INSERT INTO sqlite_sequence(name, seq) VALUES('keyword_rules', $seq);
                """;
            seq.Parameters.AddWithValue("$seq", rules.Count == 0 ? 0 : rules.Max(r => r.Id > 0 ? r.Id : 0));
            seq.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void ReorderKeywordRules(IReadOnlyList<int> orderedIds)
    {
        using var con = db.Open();
        using var tx = con.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE keyword_rules SET sort_order=$order, updated_at=$now WHERE id=$id;";
            cmd.Parameters.AddWithValue("$order", i + 1);
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$id", orderedIds[i]);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 有効期限切れのキーワードルールを物理削除し、関連する scheduled 予約も物理削除する。
    /// 
    /// v32.65 で再実装:
    ///   ・旧実装は reservations に source_rule_id カラムが無い前提で、
    ///     期限切れルール1件でも存在すれば全 keyword 予約を一時的に cancelled 化する
    ///     危険なロジックになっていた(他有効ルール由来の予約まで巻き込む)。
    ///   ・v32.55 で source_rule_id カラムが追加されたので、期限切れルール由来の
    ///     予約のみをピンポイントで物理削除するよう修正。
    ///   ・DeleteScheduledByRuleId を再利用して「ルール由来予約解放」ロジックを単一化。
    ///   ・ユーザー確定方針に合わせて物理削除(cancelled化ではない)。
    /// </summary>
    /// <returns>削除したルール件数</returns>
    public int PurgeExpiredKeywordRules()
    {
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        using var con = db.Open();

        // 有効期限切れルールのIDを取得
        using var sel = con.CreateCommand();
        sel.CommandText = "SELECT id FROM keyword_rules WHERE expires_on <> '' AND expires_on < $today;";
        sel.Parameters.AddWithValue("$today", today);
        var expiredIds = new List<int>();
        using (var r = sel.ExecuteReader())
            while (r.Read()) expiredIds.Add(r.GetInt32(0));

        if (expiredIds.Count == 0) return 0;

        // 期限切れルールごとに: 由来する scheduled 予約を物理削除 → ルール本体を削除
        // トランザクションは DeleteScheduledByRuleId 側で個別に張られるため、
        // ここではシンプルにループ処理する(ルール単位でアトミック)。
        foreach (var ruleId in expiredIds)
        {
            DeleteScheduledByRuleId(ruleId);

            using var del = con.CreateCommand();
            del.CommandText = "DELETE FROM keyword_rules WHERE id = $id;";
            del.Parameters.AddWithValue("$id", ruleId);
            del.ExecuteNonQuery();
        }

        return expiredIds.Count;
    }

    // ─── プログラム予約ルール CRUD ────────────────────────────────

    public IReadOnlyList<ProgramRule> GetProgramRules()
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, day_of_week, start_time, end_time,
                   network_id, transport_stream_id, service_id,
                   expires_on, enabled, created_at, updated_at
            FROM program_rules ORDER BY id;
            """;
        return ReadProgramRules(cmd);
    }

    public int AddProgramRule(ProgramRule r)
    {
        var now = DateTime.Now.ToString("O");
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO program_rules
              (name, day_of_week, start_time, end_time,
               network_id, transport_stream_id, service_id,
               expires_on, enabled, created_at, updated_at)
            VALUES
              ($name, $dow, $st, $et, $nid, $tsid, $sid, $exp, $en, $now, $now);
            SELECT last_insert_rowid();
            """;
        BindProgramRule(cmd, r, now);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateProgramRule(ProgramRule r)
    {
        var now = DateTime.Now.ToString("O");
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE program_rules SET
              name=$name, day_of_week=$dow, start_time=$st, end_time=$et,
              network_id=$nid, transport_stream_id=$tsid, service_id=$sid,
              expires_on=$exp, enabled=$en, updated_at=$now
            WHERE id=$id;
            """;
        BindProgramRule(cmd, r, now);
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteProgramRule(int id)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM program_rules WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>有効期限切れのプログラム予約ルールを削除する。</summary>
    public int PurgeExpiredProgramRules()
    {
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM program_rules WHERE expires_on <> '' AND expires_on < $today;";
        cmd.Parameters.AddWithValue("$today", today);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// ProgramGuideBlankクリックから作られた暫定プログラム予約だけを、有効期限切れ後に掃除する。
    /// 通常のプログラム予約は対象外。
    /// </summary>
    public int PurgeExpiredProgramGuideMissingProgramRules()
    {
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        using var con = db.Open();

        var targets = new List<(int Id, string Name, string ExpiresOn)>();
        using (var sel = con.CreateCommand())
        {
            sel.CommandText = """
                SELECT id, name, expires_on
                FROM program_rules
                WHERE expires_on <> ''
                  AND expires_on < $today
                  AND NOT EXISTS (
                      SELECT 1
                      FROM reservations r
                      WHERE r.source = 'program'
                        AND r.source_rule_id = program_rules.id
                        AND r.status = 'recording'
                  );
                """;
            sel.Parameters.AddWithValue("$today", today);
            using var r = sel.ExecuteReader();
            while (r.Read())
            {
                var candidate = new ProgramRule
                {
                    Id = r.GetInt32(0),
                    Name = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    ExpiresOn = r.IsDBNull(2) ? string.Empty : r.GetString(2)
                };
                if (ReservationOriginClassifier.IsProgramGuideMissingProgramRule(candidate))
                {
                    targets.Add((candidate.Id, candidate.Name, candidate.ExpiresOn));
                }
            }
        }

        if (targets.Count == 0) return 0;

        using var tx = con.BeginTransaction();
        var deletedReservations = 0;
        var reservationIds = new List<int>();
        foreach (var target in targets)
        {
            using (var selReservations = con.CreateCommand())
            {
                selReservations.Transaction = tx;
                selReservations.CommandText = """
                    SELECT id
                    FROM reservations
                    WHERE source = 'program'
                      AND source_rule_id = $ruleId
                      AND status <> 'recording';
                    """;
                selReservations.Parameters.AddWithValue("$ruleId", target.Id);
                using var r = selReservations.ExecuteReader();
                while (r.Read()) reservationIds.Add(r.GetInt32(0));
            }

            using (var delReservations = con.CreateCommand())
            {
                delReservations.Transaction = tx;
                delReservations.CommandText = """
                    DELETE FROM reservations
                    WHERE source = 'program'
                      AND source_rule_id = $ruleId
                      AND status <> 'recording';
                    """;
                delReservations.Parameters.AddWithValue("$ruleId", target.Id);
                deletedReservations += delReservations.ExecuteNonQuery();
            }

            using (var delRule = con.CreateCommand())
            {
                delRule.Transaction = tx;
                delRule.CommandText = "DELETE FROM program_rules WHERE id = $ruleId;";
                delRule.Parameters.AddWithValue("$ruleId", target.Id);
                delRule.ExecuteNonQuery();
            }
        }
        tx.Commit();

        log.Add("PROGRAM_PLACEHOLDER_EXPIRED_CLEANUP", "ProgramRule",
            $"result=OK deletedRules={targets.Count} deletedReservations={deletedReservations} ruleIds=[{string.Join(',', targets.Select(x => x.Id))}] reservationIds=[{string.Join(',', reservationIds.Distinct().OrderBy(x => x).Select(x => $"R{x}"))}] today={today} scope=program_guide_missing_only originClassifier=ReservationOriginClassifier normalProgramRules=untouched rule=v0.11.378_programguide_missing_duplicate_cleanup_trace_residue");
        return targets.Count;
    }

    // ─── 内部ヘルパー ────────────────────────────────────────────

    private static IReadOnlyList<Reservation> ReadReservations(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<Reservation>();
        while (r.Read())
        {
            var reservation = new Reservation
            {
                Id                  = r.GetInt32(0),
                NetworkId           = (ushort)r.GetInt32(1),
                TransportStreamId   = (ushort)r.GetInt32(2),
                ServiceId           = (ushort)r.GetInt32(3),
                EventId             = (ushort)r.GetInt32(4),
                Title               = r.IsDBNull(5)  ? "" : r.GetString(5),
                StartTime           = DateTime.Parse(r.GetString(6)).ToLocalTime(),
                EndTime             = DateTime.Parse(r.GetString(7)).ToLocalTime(),
                Status              = ParseStatus(r.IsDBNull(8)  ? "" : r.GetString(8)),
                Source              = ParseSource(r.IsDBNull(9)  ? "" : r.GetString(9)),
                CreatedAt           = r.IsDBNull(10) ? DateTime.MinValue : DateTime.Parse(r.GetString(10)),
                UpdatedAt           = r.IsDBNull(11) ? DateTime.MinValue : DateTime.Parse(r.GetString(11)),
                ChannelArgument     = r.IsDBNull(12) ? "" : r.GetString(12),
                IsConflicted        = !r.IsDBNull(13) && r.GetInt32(13) != 0,
                IsEnabled           = r.IsDBNull(14) || r.GetInt32(14) != 0,
                TunerName           = r.IsDBNull(15) ? "" : r.GetString(15),
                ActualTunerName     = r.IsDBNull(16) ? "" : r.GetString(16),
                RecordingStartedAt  = r.IsDBNull(17) || string.IsNullOrEmpty(r.GetString(17))
                                      ? (DateTime?)null
                                      : DateTime.Parse(r.GetString(17)),
                RecordingFinishedAt = r.IsDBNull(18) || string.IsNullOrEmpty(r.GetString(18))
                                      ? (DateTime?)null
                                      : DateTime.Parse(r.GetString(18)),
                ServiceName         = r.IsDBNull(19) ? "" : r.GetString(19),
                ScheduledStartTime  = r.IsDBNull(20) || string.IsNullOrEmpty(r.GetString(20))
                                      ? (DateTime?)null
                                      : DateTime.Parse(r.GetString(20)),
                SourceRuleId        = r.IsDBNull(21) ? (int?)null : r.GetInt32(21),
                SourceRuleName      = r.IsDBNull(22) ? "" : r.GetString(22),
                IsUserChain         = !r.IsDBNull(23) && r.GetInt32(23) != 0,
                UserChainPreviousId = r.IsDBNull(24) ? (int?)null : r.GetInt32(24),
                UserChainRootId     = r.IsDBNull(25) ? (int?)null : r.GetInt32(25),
            };
            var projectedStatus = ProjectRuntimeStatus(reservation);
            if (projectedStatus == ReservationStatus.Recording)
            {
                reservation.Status = ReservationStatus.Recording;
                reservation.IsConflicted = false;
            }
            list.Add(reservation);
        }
        return list;
    }

    private static IReadOnlyList<KeywordRule> ReadKeywordRules(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<KeywordRule>();
        while (r.Read())
        {
            list.Add(new KeywordRule
            {
                Id             = r.GetInt32(0),
                Name           = r.IsDBNull(1) ? "" : r.GetString(1),
                Pattern        = r.IsDBNull(2) ? "" : r.GetString(2),
                ExcludePattern = r.IsDBNull(3) ? "" : r.GetString(3),
                UseRegex       = r.IsDBNull(4) || r.GetInt32(4) != 0,
                SearchFields   = r.IsDBNull(5) ? "title" : r.GetString(5),
                SearchTitle    = r.IsDBNull(6) || r.GetInt32(6) != 0,
                SearchOutline  = r.IsDBNull(7) || r.GetInt32(7) != 0,
                SearchDetail   = r.IsDBNull(8) || r.GetInt32(8) != 0,
                SearchCast     = r.IsDBNull(9) || r.GetInt32(9) != 0,
                UseAllChannels = r.IsDBNull(10) || r.GetInt32(10) != 0,
                TargetServices = r.IsDBNull(11) ? "" : r.GetString(11),
                TargetGenres   = r.IsDBNull(12) ? "" : r.GetString(12),
                TargetDays     = r.IsDBNull(13) ? "" : r.GetString(13),
                UseTimeRange   = !r.IsDBNull(14) && r.GetInt32(14) != 0,
                StartTime      = r.IsDBNull(15) ? "00:00" : r.GetString(15),
                EndTime        = r.IsDBNull(16) ? "23:59" : r.GetString(16),
                SortOrder      = r.IsDBNull(17) ? 0 : r.GetInt32(17),
                ExpiresOn      = r.IsDBNull(18) ? "" : r.GetString(18),
                Enabled        = r.GetInt32(19) != 0,
                CreatedAt      = r.IsDBNull(20) ? DateTime.MinValue : DateTime.Parse(r.GetString(20)),
                UpdatedAt      = r.IsDBNull(21) ? DateTime.MinValue : DateTime.Parse(r.GetString(21)),
            });
        }
        return list;
    }

    public void SyncProgramRuleReservations()
    {
        PurgeExpiredProgramGuideMissingProgramRules();
        var rules = GetProgramRules();
        var now = DateTime.Now;
        var programGuideBlankProgramRules = rules.Count(ReservationOriginClassifier.IsProgramGuideMissingProgramRule);
        var explicitProgramRules = Math.Max(0, rules.Count - programGuideBlankProgramRules);
        if (rules.Count > 0)
            log.Add("PROGRAM_RULE_ORIGIN_AUDIT", "Sync", $"total={rules.Count} explicitProgramRules={explicitProgramRules} programGuideMissingProgramRules={programGuideBlankProgramRules} classifier=ReservationOriginClassifier commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.378_programguide_missing_duplicate_cleanup_trace_residue");
        var evaluatedProgramRules = 0;
        foreach (var rule in rules)
        {
            evaluatedProgramRules++;
            SyncProgramRuleReservation(rule, now);
        }

        if (evaluatedProgramRules > 0)
            log.Add("RESERVE_ENTRY", "ProgramResolveSummary", $"result=OK evaluated={evaluatedProgramRules} okDetails=diagnostic_only errorsAndFallbacks=regular_log rule=program_projection_channel_resolve_release_candidate_summary");

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            DELETE FROM reservations
            WHERE source = 'program'
              AND source_rule_id IS NOT NULL
              AND source_rule_id NOT IN (SELECT id FROM program_rules);
            """;
        cmd.ExecuteNonQuery();
    }

    private void SyncProgramRuleReservation(ProgramRule rule, DateTime now)
    {
        using var con = db.Open();

        if (!TryBuildNextProgramReservation(rule, now, out var reservation))
        {
            using var delExpired = con.CreateCommand();
            delExpired.CommandText = """
                DELETE FROM reservations
                WHERE source = 'program'
                  AND source_rule_id = $ruleId
                  AND status IN ('scheduled','recording');
                """;
            delExpired.Parameters.AddWithValue("$ruleId", rule.Id);
            var deleted = delExpired.ExecuteNonQuery();
            if (deleted > 0)
                log.Add("RESERVE_ENTRY", "ProgramSync", $"result=REMOVED ruleId={rule.Id} name=[{rule.Name}] reason=no_next_occurrence deleted={deleted} rule=v0.6.39_program_projection_channel_resolve");
            return;
        }

        // v0.6.36:
        // ProgramRule の投影予約は、ALLOC_ROUTE 内や PreRecEpg 再評価から複数回同期される。
        // 従来は毎回 scheduled/recording を削除して再挿入していたため、短時間に R851/R852 のような
        // ID churn が発生し、ログ上は重複生成に見え、Wake 差分再構築も増えやすかった。
        // 同じ ruleId・同じ次回発生時刻の予約が既にある場合は維持し、余分な重複だけ削除する。
        using (var existing = con.CreateCommand())
        {
            existing.CommandText = """
                SELECT id, network_id, transport_stream_id, service_id, title, start_time, end_time, is_enabled, service_name, channel_argument
                FROM reservations
                WHERE source = 'program'
                  AND source_rule_id = $ruleId
                  AND status IN ('scheduled','recording')
                ORDER BY id ASC;
                """;
            existing.Parameters.AddWithValue("$ruleId", rule.Id);
            using var r = existing.ExecuteReader();
            int? keepId = null;
            var duplicateIds = new List<int>();
            while (r.Read())
            {
                var id = r.GetInt32(0);
                var nid = r.GetInt32(1);
                var tsid = r.GetInt32(2);
                var sid = r.GetInt32(3);
                var title = r.IsDBNull(4) ? string.Empty : r.GetString(4);
                var startText = r.IsDBNull(5) ? string.Empty : r.GetString(5);
                var endText = r.IsDBNull(6) ? string.Empty : r.GetString(6);
                var enabled = r.IsDBNull(7) || r.GetInt32(7) != 0;
                var existingServiceName = r.IsDBNull(8) ? string.Empty : r.GetString(8);
                var existingChannelArgument = r.IsDBNull(9) ? string.Empty : r.GetString(9);
                var sameCore = nid == reservation.NetworkId
                    && tsid == reservation.TransportStreamId
                    && sid == reservation.ServiceId
                    && string.Equals(title, reservation.Title, StringComparison.Ordinal)
                    && DateTime.TryParse(startText, out var existingStart)
                    && DateTime.TryParse(endText, out var existingEnd)
                    && existingStart == reservation.StartTime
                    && existingEnd == reservation.EndTime
                    && enabled == reservation.IsEnabled;
                if (sameCore && keepId is null)
                {
                    keepId = id;
                    var metadataChanged = !string.Equals(existingServiceName, reservation.ServiceName, StringComparison.Ordinal)
                        || !string.Equals(existingChannelArgument, reservation.ChannelArgument, StringComparison.Ordinal);
                    if (metadataChanged)
                    {
                        using var fix = con.CreateCommand();
                        fix.CommandText = """
                            UPDATE reservations
                            SET service_name = $svcname,
                                channel_argument = $charg,
                                updated_at = $now
                            WHERE id = $id;
                            """;
                        fix.Parameters.AddWithValue("$svcname", reservation.ServiceName);
                        fix.Parameters.AddWithValue("$charg", reservation.ChannelArgument);
                        fix.Parameters.AddWithValue("$now", now.ToString("O"));
                        fix.Parameters.AddWithValue("$id", id);
                        fix.ExecuteNonQuery();
                        log.Add("RESERVE_ENTRY", "ProgramSync", $"result=METADATA_FIXED keep=R{id} ruleId={rule.Id} name=[{rule.Name}] service=[{reservation.ServiceName}] channel=[{reservation.ChannelArgument}] oldService=[{existingServiceName}] oldChannel=[{existingChannelArgument}] rule=v0.6.39_program_projection_channel_resolve");
                    }
                }
                else
                    duplicateIds.Add(id);
            }

            if (keepId is not null)
            {
                if (duplicateIds.Count > 0)
                {
                    using var delDup = con.CreateCommand();
                    delDup.CommandText = $"DELETE FROM reservations WHERE id IN ({string.Join(',', duplicateIds)})";
                    var removed = delDup.ExecuteNonQuery();
                    log.Add("RESERVE_ENTRY", "ProgramSync", $"result=DEDUP keep=R{keepId.Value} removed={removed} ruleId={rule.Id} name=[{rule.Name}] rule=v0.6.39_program_projection_channel_resolve");
                }
                return;
            }
        }

        using (var crossRuleDup = con.CreateCommand())
        {
            // v0.11.197:
            // Do not create a Program projection just to let the global reservation dedupe cancel it later.
            // ProgramRule projection owns one next reservation per rule, but if the exact same user-visible
            // reservation already exists from another ProgramRule, this rule stays silent instead of churning IDs.
            crossRuleDup.CommandText = """
                SELECT id FROM reservations
                WHERE source = 'program'
                  AND status IN ('scheduled','recording')
                  AND (source_rule_id IS NULL OR source_rule_id <> $ruleId)
                  AND network_id = $nid
                  AND transport_stream_id = $tsid
                  AND service_id = $sid
                  AND title = $title
                  AND start_time = $start
                  AND end_time = $end
                ORDER BY id ASC
                LIMIT 1;
                """;
            crossRuleDup.Parameters.AddWithValue("$ruleId", rule.Id);
            crossRuleDup.Parameters.AddWithValue("$nid", reservation.NetworkId);
            crossRuleDup.Parameters.AddWithValue("$tsid", reservation.TransportStreamId);
            crossRuleDup.Parameters.AddWithValue("$sid", reservation.ServiceId);
            crossRuleDup.Parameters.AddWithValue("$title", reservation.Title);
            crossRuleDup.Parameters.AddWithValue("$start", reservation.StartTime.ToString("O"));
            crossRuleDup.Parameters.AddWithValue("$end", reservation.EndTime.ToString("O"));
            var keepDuplicate = crossRuleDup.ExecuteScalar();
            if (keepDuplicate is not null)
            {
                using var cleanupThisRule = con.CreateCommand();
                cleanupThisRule.CommandText = """
                    DELETE FROM reservations
                    WHERE source = 'program'
                      AND source_rule_id = $ruleId
                      AND status IN ('scheduled','recording');
                    """;
                cleanupThisRule.Parameters.AddWithValue("$ruleId", rule.Id);
                var removed = cleanupThisRule.ExecuteNonQuery();
                log.Add("RESERVE_ENTRY", "ProgramSync",
                    $"result=SKIP_CROSS_RULE_DUPLICATE keep=R{Convert.ToInt32(keepDuplicate)} removedForRule={removed} ruleId={rule.Id} name=[{rule.Name}] title=[{ReservationTitleDisplayContract.ForLog(reservation.Title)}] rawTitleBlank={ReservationTitleDisplayContract.RawBlankFlag(reservation.Title)} start={reservation.StartTime:MM/dd HH:mm} rule=v0.11.197_program_projection_no_churn_before_global_dedupe");
                return;
            }
        }

        using var del = con.CreateCommand();
        del.CommandText = """
            DELETE FROM reservations
            WHERE source = 'program'
              AND source_rule_id = $ruleId
              AND status IN ('scheduled','recording');
            """;
        del.Parameters.AddWithValue("$ruleId", rule.Id);
        del.ExecuteNonQuery();

        using var ins = con.CreateCommand();
        ins.CommandText = """
            INSERT INTO reservations
              (network_id, transport_stream_id, service_id, event_id,
               title, start_time, end_time, status, source, created_at, updated_at,
               channel_argument, is_conflicted, is_enabled, tuner_name, service_name,
               scheduled_start_time, source_rule_id, source_rule_name)
            VALUES
              ($nid, $tsid, $sid, $eid,
               $title, $start, $end, $status, $source, $now, $now,
               $charg, 0, $enabled, '', $svcname,
               $start, $sourceRuleId, $sourceRuleName);
            """;
        ins.Parameters.AddWithValue("$nid", reservation.NetworkId);
        ins.Parameters.AddWithValue("$tsid", reservation.TransportStreamId);
        ins.Parameters.AddWithValue("$sid", reservation.ServiceId);
        ins.Parameters.AddWithValue("$eid", reservation.EventId);
        ins.Parameters.AddWithValue("$title", reservation.Title);
        ins.Parameters.AddWithValue("$start", reservation.StartTime.ToString("O"));
        ins.Parameters.AddWithValue("$end", reservation.EndTime.ToString("O"));
        ins.Parameters.AddWithValue("$status", reservation.Status.ToString().ToLowerInvariant());
        ins.Parameters.AddWithValue("$source", reservation.Source.ToString().ToLowerInvariant());
        ins.Parameters.AddWithValue("$charg", reservation.ChannelArgument);
        ins.Parameters.AddWithValue("$enabled", reservation.IsEnabled ? 1 : 0);
        ins.Parameters.AddWithValue("$svcname", reservation.ServiceName);
        ins.Parameters.AddWithValue("$sourceRuleId", reservation.SourceRuleId!.Value);
        ins.Parameters.AddWithValue("$sourceRuleName", reservation.SourceRuleName);
        ins.Parameters.AddWithValue("$now", now.ToString("O"));
        ins.ExecuteNonQuery();
        using var lastIdCmd = con.CreateCommand();
        lastIdCmd.CommandText = "SELECT last_insert_rowid();";
        var insertedId = Convert.ToInt32(lastIdCmd.ExecuteScalar());
        log.Add("RESERVE_ENTRY", "Program", $"共通入口要求/投影 source=Program id=R{insertedId} ruleId={rule.Id} name=[{rule.Name}] dayOfWeek={rule.DayOfWeek} start={reservation.StartTime:MM/dd HH:mm} end={reservation.EndTime:MM/dd HH:mm} service=[{reservation.ServiceName}] rule=v0.6.39_program_projection_channel_resolve");

        // v0.11.112: ProgramRule projection is a real user-visible reservation route.
        // Emit it through the same UserOperationEvent source classifier as manual/auto/chain reservations,
        // but do not announce disabled rule projections as newly active reservations.
        if (reservation.IsEnabled)
            userEvents.AddReservationAdded(reservation, insertedId);
    }

    private ProgramRuleChannelResolution ResolveProgramRuleChannel(ProgramRule rule)
    {
        try
        {
            var targets = channelLoader.Load().Targets;
            ChannelTarget? target = null;

            if (rule.NetworkId > 0 && rule.TransportStreamId > 0 && rule.ServiceId > 0)
            {
                target = targets.FirstOrDefault(t =>
                    t.OriginalNetworkId == rule.NetworkId
                    && t.TransportStreamId == rule.TransportStreamId
                    && t.ServiceId == rule.ServiceId);
            }

            // 旧ProgramRuleや局名未解決で保存されたルールはSIDだけを持つことがある。
            // SIDが一意ならch2由来の局名・チャンネル引数へ昇格する。
            if (target is null && rule.ServiceId > 0)
            {
                var sidMatches = targets.Where(t => t.ServiceId == rule.ServiceId).ToList();
                if (sidMatches.Count == 1)
                    target = sidMatches[0];
                else if (sidMatches.Count > 1 && rule.TransportStreamId > 0)
                    target = sidMatches.FirstOrDefault(t => t.TransportStreamId == rule.TransportStreamId);
            }

            if (target is not null)
            {
                // v0.10.54: successful ProgramResolve rows are expected on every startup.
                // Keep success details in diagnostic mode conceptually; regular logs use ProgramResolveSummary.
                return new ProgramRuleChannelResolution(
                    target.OriginalNetworkId,
                    target.TransportStreamId,
                    target.ServiceId,
                    target.Name,
                    target.ChannelArgument);
            }
        }
        catch (Exception ex)
        {
            log.Add("RESERVE_ENTRY", "ProgramResolve", $"result=ERROR ruleId={rule.Id} name=[{rule.Name}] sid={rule.ServiceId} error={ex.GetType().Name}:{ex.Message} action=fallback_sid rule=v0.6.39_program_projection_channel_resolve");
        }

        var fallbackName = rule.ServiceId > 0 ? $"SID{rule.ServiceId}" : string.Empty;
        log.Add("RESERVE_ENTRY", "ProgramResolve", $"result=FALLBACK ruleId={rule.Id} name=[{rule.Name}] nid={rule.NetworkId} tsid={rule.TransportStreamId} sid={rule.ServiceId} service=[{fallbackName}] channel=[] action=keep_legacy_sid rule=v0.6.39_program_projection_channel_resolve");
        return new ProgramRuleChannelResolution(rule.NetworkId, rule.TransportStreamId, rule.ServiceId, fallbackName, string.Empty);
    }

    private bool TryBuildNextProgramReservation(ProgramRule rule, DateTime now, out Reservation reservation)
    {
        reservation = new Reservation();

        if (string.IsNullOrWhiteSpace(rule.Name))
            return false;
        if (!TimeSpan.TryParse(rule.StartTime, out var startTod))
            return false;
        if (!TimeSpan.TryParse(rule.EndTime, out var endTod))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.ExpiresOn)
            && DateOnly.TryParse(rule.ExpiresOn, out var expiresOn)
            && expiresOn < DateOnly.FromDateTime(now))
        {
            return false;
        }

        var baseDate = now.Date;
        DateTime? nextStart = null;
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = baseDate.AddDays(offset);
            if (!ProgramRuleMatchesDate(rule, date))
                continue;

            var candidateStart = date + startTod;
            var candidateEnd = date + endTod;
            if (candidateEnd <= candidateStart)
                candidateEnd = candidateEnd.AddDays(1);

            if (candidateEnd <= now)
                continue;

            nextStart = candidateStart;
            var resolved = ResolveProgramRuleChannel(rule);
            reservation = new Reservation
            {
                NetworkId = resolved.NetworkId,
                TransportStreamId = resolved.TransportStreamId,
                ServiceId = resolved.ServiceId,
                EventId = 0,
                Title = rule.Name,
                StartTime = candidateStart,
                EndTime = candidateEnd,
                Status = ReservationStatus.Scheduled,
                Source = ReservationSource.Program,
                ChannelArgument = resolved.ChannelArgument,
                IsConflicted = false,
                IsEnabled = rule.Enabled,
                TunerName = string.Empty,
                ServiceName = resolved.ServiceName,
                ScheduledStartTime = candidateStart,
                SourceRuleId = rule.Id,
                SourceRuleName = rule.Name,
                CreatedAt = now,
                UpdatedAt = now
            };
            break;
        }

        return nextStart.HasValue;
    }

    private sealed record ProgramRuleChannelResolution(
        ushort NetworkId,
        ushort TransportStreamId,
        ushort ServiceId,
        string ServiceName,
        string ChannelArgument);

    private static bool ProgramRuleMatchesDate(ProgramRule rule, DateTime date)
    {
        return rule.DayOfWeek switch
        {
            0 => true,
            1 => date.DayOfWeek == DayOfWeek.Monday,
            2 => date.DayOfWeek == DayOfWeek.Tuesday,
            3 => date.DayOfWeek == DayOfWeek.Wednesday,
            4 => date.DayOfWeek == DayOfWeek.Thursday,
            5 => date.DayOfWeek == DayOfWeek.Friday,
            6 => date.DayOfWeek == DayOfWeek.Saturday,
            7 => date.DayOfWeek == DayOfWeek.Sunday,
            _ => false
        };
    }

    private static IReadOnlyList<ProgramRule> ReadProgramRules(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<ProgramRule>();
        while (r.Read())
        {
            list.Add(new ProgramRule
            {
                Id                  = r.GetInt32(0),
                Name                = r.IsDBNull(1) ? "" : r.GetString(1),
                DayOfWeek           = r.GetInt32(2),
                StartTime           = r.IsDBNull(3) ? "00:00" : r.GetString(3),
                EndTime             = r.IsDBNull(4) ? "00:00" : r.GetString(4),
                NetworkId           = (ushort)r.GetInt32(5),
                TransportStreamId   = (ushort)r.GetInt32(6),
                ServiceId           = (ushort)r.GetInt32(7),
                ExpiresOn           = r.IsDBNull(8) ? "" : r.GetString(8),
                Enabled             = r.GetInt32(9) != 0,
                CreatedAt           = r.IsDBNull(10) ? DateTime.MinValue : DateTime.Parse(r.GetString(10)),
                UpdatedAt           = r.IsDBNull(11) ? DateTime.MinValue : DateTime.Parse(r.GetString(11)),
            });
        }
        return list;
    }

    private static void BindKeywordRule(SqliteCommand cmd, KeywordRule r, string now)
    {
        cmd.Parameters.AddWithValue("$name",      r.Name ?? "");
        cmd.Parameters.AddWithValue("$pat",       r.Pattern ?? "");
        cmd.Parameters.AddWithValue("$exc",       r.ExcludePattern ?? "");
        cmd.Parameters.AddWithValue("$regex",     r.UseRegex ? 1 : 0);
        cmd.Parameters.AddWithValue("$fields",    r.SearchFields ?? "title");
        cmd.Parameters.AddWithValue("$stitle",    r.SearchTitle ? 1 : 0);
        cmd.Parameters.AddWithValue("$soutline",  r.SearchOutline ? 1 : 0);
        cmd.Parameters.AddWithValue("$sdetail",   r.SearchDetail ? 1 : 0);
        cmd.Parameters.AddWithValue("$scast",     r.SearchCast ? 1 : 0);
        cmd.Parameters.AddWithValue("$useAllChannels", r.UseAllChannels ? 1 : 0);
        cmd.Parameters.AddWithValue("$svc",       r.TargetServices ?? "");
        cmd.Parameters.AddWithValue("$genres",    r.TargetGenres ?? "");
        cmd.Parameters.AddWithValue("$days",      r.TargetDays ?? "");
        cmd.Parameters.AddWithValue("$useTime",   r.UseTimeRange ? 1 : 0);
        cmd.Parameters.AddWithValue("$startTime", string.IsNullOrWhiteSpace(r.StartTime) ? "00:00" : r.StartTime);
        cmd.Parameters.AddWithValue("$endTime",   string.IsNullOrWhiteSpace(r.EndTime) ? "23:59" : r.EndTime);
        cmd.Parameters.AddWithValue("$exp",       r.ExpiresOn ?? "");
        cmd.Parameters.AddWithValue("$en",        r.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$now",       now);
    }

    private static void BindProgramRule(SqliteCommand cmd, ProgramRule r, string now)
    {
        cmd.Parameters.AddWithValue("$name",  r.Name);
        cmd.Parameters.AddWithValue("$dow",   r.DayOfWeek);
        cmd.Parameters.AddWithValue("$st",    r.StartTime);
        cmd.Parameters.AddWithValue("$et",    r.EndTime);
        cmd.Parameters.AddWithValue("$nid",   r.NetworkId);
        cmd.Parameters.AddWithValue("$tsid",  r.TransportStreamId);
        cmd.Parameters.AddWithValue("$sid",   r.ServiceId);
        cmd.Parameters.AddWithValue("$exp",   r.ExpiresOn);
        cmd.Parameters.AddWithValue("$en",    r.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$now",   now);
    }

    private static ReservationStatus ParseStatus(string s) => s switch
    {
        "recording"  => ReservationStatus.Recording,
        "completed"  => ReservationStatus.Completed,
        "cancelled"  => ReservationStatus.Cancelled,
        "failed"     => ReservationStatus.Failed,
        _            => ReservationStatus.Scheduled
    };

    private static ReservationSource ParseSource(string s) => s switch
    {
        "immediate"     => ReservationSource.Immediate,
        "keywordsearch" => ReservationSource.KeywordSearch,
        "keyword"       => ReservationSource.Keyword,
        "program"       => ReservationSource.Program,
        "epg"           => ReservationSource.Epg,
        _                => ReservationSource.Manual
    };
}

// if (isChain && isNearStartWindow) conflicted = false; // override

// VIRTUAL_DROP_BYPASSED_FOR_CHAIN
