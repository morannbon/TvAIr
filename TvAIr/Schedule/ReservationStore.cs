using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Epg.Projection;
using TvAIr.Channel;
using TvAIr.Plugin;
using TvAIrPlugin;

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
    int ParentUserChainChild,
    string ParentIds);

public sealed record AllocationCommittedRowChange(
    int Id,
    string ServiceName,
    string Title,
    string PreviousTunerName,
    string CurrentTunerName,
    bool PreviousConflicted,
    bool CurrentConflicted,
    long PreviousDataVersion,
    long CurrentDataVersion);

public sealed record ReservationAllocationEvaluationResult(
    IReadOnlyList<(int Id, string ServiceName, string Title, bool Conflicted)> Changes,
    IReadOnlyList<AllocationCommittedRowChange> RowChanges,
    long AllocationGeneration,
    bool RetryRequired,
    string Reason,
    string ReservationSnapshotVersion,
    long TunerSnapshotVersion);

public sealed record NormalEpgConflictBlockerEvidence(
    int ReservationId,
    string Group,
    long RunGeneration,
    long OccupationRevision);


public sealed record ReservationEnabledUpdateResult(
    bool Found,
    bool Changed,
    string Reason,
    int ReservationId,
    bool? PreviousEnabled,
    bool? CurrentEnabled,
    long PreviousDataVersion,
    long CurrentDataVersion,
    Reservation? Before,
    Reservation? After);


public sealed record UserChainMutationResult(
    bool Applied,
    bool Added,
    string Reason,
    int ReservationId,
    Reservation? Reservation);

public sealed record ReservationLifecycleTransitionResult(
    bool Applied,
    string Reason,
    int ReservationId,
    ReservationStatus? PreviousStatus,
    ReservationStatus? CurrentStatus,
    long PreviousDataVersion,
    long CurrentDataVersion,
    Reservation? Reservation);

/// <summary>
/// 予約・キーワードルール・プログラム予約ルールのDB操作。
/// </summary>
public sealed class ReservationStore
{
    private readonly Database db;
    private readonly LogRepository log;
    private readonly ChainDirectRecorderSessionRegistry chainSessionRegistry;
    private readonly ChannelFileLoader channelLoader;
    private readonly ReservationMutationJournal mutationJournal;
    private readonly NormalEpgWaveOccupation normalEpgWaveOccupation;
#if TVAIR_DEVELOPER_DIAGNOSTICS
    private readonly object finalUnitAssignmentLogGate = new();
    private Dictionary<int, string> lastFinalUnitAssignmentLogState = new();
    private DateTime lastFinalUnitAssignmentSummaryAt = DateTime.MinValue;
    private static readonly TimeSpan FinalUnitAssignmentSummaryInterval = TimeSpan.FromMinutes(10);
#endif
    private readonly object normalEpgConflictEvidenceGate = new();
    private Dictionary<int, NormalEpgConflictBlockerEvidence> normalEpgConflictEvidenceByReservationId = new();
    // release_contract: 通常競合ポリシーから「自動救済1回だけ」の足かせを撤去。
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
        public bool RequiresPinnedTuner { get; init; }
        public bool HasPinnedTunerConflict { get; init; }
        public string? PinnedTuner { get; init; }
        public string? ContinuityPreferredTuner { get; init; }
        public string PinCandidates { get; init; } = string.Empty;
        // release_contract: チェーンは外部予約との競合では番組単位で採否を再計算する。
        // ChainRootだけをKeyにすると全子予約が一つの長大な占有区間へ潰れ、
        // 最初に競合した子以降だけを可逆に競合化できないため、予約IDまで含める。
        public string UnitKey => ChainRootId.HasValue
            ? $"{Group}:CHAIN:{ChainRootId.Value}:R{PriorityReservation.Id}"
            : $"{Group}:UNIT:{UnitId}";
        public string MemberIds => string.Join(">", Reservations.Select(r => $"R{r.Id}"));
    }

    /// <summary>時間追従の変化検出閾値（秒）。この秒数以上ずれていれば更新対象とみなす。</summary>
    private const int TimeFollowThresholdSeconds = 30;

    // Completed は物理占有ではない。明示チェーンの直接後続が境界再試行中である場合だけ、
    // 後続開始30秒前から後続予約終了までを同一物理Tuner継承のhandoff anchor範囲とする。
    // 任意の5分/3分quiet windowを再導入せず、時間追従後のStartTimeと予約EndTimeを正本にする。
    private const int CompletedHandoffAnchorFrontCutSeconds = 30;

    public ReservationStore(Database db, LogRepository log, ChainDirectRecorderSessionRegistry chainSessionRegistry, ChannelFileLoader channelLoader, ReservationMutationJournal mutationJournal, NormalEpgWaveOccupation normalEpgWaveOccupation)
    {
        this.db = db;
        this.log = log;
        this.chainSessionRegistry = chainSessionRegistry;
        this.channelLoader = channelLoader;
        this.mutationJournal = mutationJournal;
        this.normalEpgWaveOccupation = normalEpgWaveOccupation;
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
        // release_contract: 一覧上のReservation照合ではなく、DB上の予約実体で完全一致を確認する。
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
        var removedReservations = new List<Reservation>();

        foreach (var target in targets)
        {
            if (deletedReservationIds.Contains(target.MissingId)) continue;

            Reservation? candidate = null;
            using (var selectReservation = writeCon.CreateCommand())
            {
                selectReservation.Transaction = tx;
                selectReservation.CommandText = """
                    SELECT id, network_id, transport_stream_id, service_id, event_id,
                           title, start_time, end_time, status, source, created_at, updated_at,
                           channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                           recording_started_at, recording_finished_at, service_name,
                           scheduled_start_time, source_rule_id, source_rule_name,
                           is_user_chain, user_chain_previous_id, user_chain_root_id,
                           recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                           reservation_intent, created_through, created_by_plugin_id
                    FROM reservations
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
                selectReservation.Parameters.AddWithValue("$id", target.MissingId);
                candidate = ReadReservations(selectReservation, projectRuntimeStatus: false).SingleOrDefault();
            }
            if (candidate is null) continue;

            using (var delReservation = writeCon.CreateCommand())
            {
                delReservation.Transaction = tx;
                delReservation.CommandText = """
                    DELETE FROM reservations
                    WHERE id = $id
                      AND source = 'program'
                      AND status = 'scheduled'
                      AND data_version = $dataVersion;
                    """;
                delReservation.Parameters.AddWithValue("$id", candidate.Id);
                delReservation.Parameters.AddWithValue("$dataVersion", candidate.DataVersion);
                var affected = delReservation.ExecuteNonQuery();
                if (affected <= 0) continue;
            }

            deletedReservationIds.Add(target.MissingId);
            removedReservations.Add(candidate);

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
                $"result=DELETED missing=R{target.MissingId} resolved=R{target.ResolvedId} service={TrimForAudit(target.ServiceName)} start={TrimForAudit(target.StartText)} end={TrimForAudit(target.EndText)} origin=ProgramGuideMissingProgramRule identity=TimeIdentity resolvedSource={target.ResolvedSource} commonRoute=ALLOC_ROUTE/TUNER_ALLOC ui=none rule=release_contract");
        }

        tx.Commit();
        RecordProjectedReservationRemovals(removedReservations, "program_guide_missing_duplicate_resolved");

        if (deletedReservationIds.Count > 0)
        {
            allScheduled.RemoveAll(r => deletedReservationIds.Contains(r.Id));
            scheduled.RemoveAll(r => deletedReservationIds.Contains(r.Id));
            log.Add("PROGRAM_GUIDE_MISSING_DUPLICATE_CLEANUP", "Summary",
                $"result=OK deletedReservations=[{string.Join(',', deletedReservationIds.OrderBy(x => x).Select(x => $"R{x}"))}] deletedRules=[{string.Join(',', deletedRuleIds.OrderBy(x => x))}] mode=db_same_service_exact_time_resolved_event_only ui=none commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
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
                   is_user_chain, user_chain_previous_id, user_chain_root_id,
                   recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
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
                   is_user_chain, user_chain_previous_id, user_chain_root_id,
                   recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
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
                   is_user_chain, user_chain_previous_id, user_chain_root_id,
                   recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
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
                   is_user_chain, user_chain_previous_id, user_chain_root_id,
                   recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
            FROM reservations WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return ReadReservations(cmd).FirstOrDefault();
    }

    /// <summary>
    /// SQLiteに保存された業務状態をRuntime表示投影なしで取得する。
    /// Lifecycle CAS・録画owner attach・回復処理専用。UI/API表示にはGetByIdを使用する。
    /// </summary>
    private Reservation? GetByIdPersisted(int id)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                   recording_started_at, recording_finished_at, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id,
                   recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
            FROM reservations WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return ReadReservations(cmd, projectRuntimeStatus: false).FirstOrDefault();
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
                   is_user_chain, user_chain_previous_id, user_chain_root_id,
                   recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
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

    // ChainReservationContract.CHAIN_DEVELOPER_APPROVAL_REQUIRED: 以下の保存トポロジー契約変更は開発者の明示承認が必須。
    // CHAIN_ROOT_SINGLE_SOURCE_INVARIANT — 変更禁止:
    // ChainRootはチェーン作成時に確定した永続トポロジーの識別子であり、予約状態、競合、録画完了、
    // 残存メンバー、実行中アンカーから再推定してはならない。前段リンクを辿るのは保存値の整合性検証だけに使い、
    // Root決定は保存済みUserChainRootId（先頭だけは自身のId）を唯一の正本とする。
    private static bool TryResolveCanonicalStoredChainRoot(
        Reservation reservation,
        IReadOnlyDictionary<int, Reservation> allById,
        out int rootId,
        out string failureReason,
        out int depth)
    {
        rootId = reservation.UserChainRootId
            ?? (reservation.UserChainPreviousId.HasValue ? 0 : reservation.Id);
        failureReason = string.Empty;
        depth = 0;

        var visited = new HashSet<int>();
        var cursor = reservation;
        while (cursor.UserChainPreviousId.HasValue)
        {
            if (!visited.Add(cursor.Id))
            {
                failureReason = "chain_cycle_detected";
                return false;
            }
            if (++depth > 32)
            {
                failureReason = "chain_depth_exceeded";
                return false;
            }
            if (!allById.TryGetValue(cursor.UserChainPreviousId.Value, out var predecessor))
            {
                failureReason = "chain_broken_predecessor";
                return false;
            }
            cursor = predecessor;
        }

        // 起動時復旧などで先頭実体が置換されても、保存済みの歴史的Root識別子は変えない。
        var lineageRootId = cursor.UserChainRootId ?? cursor.Id;
        if (reservation.UserChainPreviousId.HasValue && !reservation.UserChainRootId.HasValue)
        {
            failureReason = "chain_root_missing";
            return false;
        }
        if (rootId == 0) rootId = lineageRootId;
        if (rootId != lineageRootId)
        {
            failureReason = "chain_root_mismatch";
            return false;
        }
        return true;
    }

    private static bool TryResolveCanonicalRootForSuccessor(
        Reservation predecessor,
        IReadOnlyDictionary<int, Reservation> allById,
        out int rootId,
        out string failureReason,
        out int depth)
    {
        if (!TryResolveCanonicalStoredChainRoot(predecessor, allById, out rootId, out failureReason, out depth))
            return false;
        return true;
    }

    /// <summary>
    /// ユーザー明示チェーンの追加または既存予約昇格を、チェーン構造検証と同一Transactionで確定する。
    /// 同一前番組への複数後続、既存後続の別前番組付替え、循環、壊れたroot、深度超過を拒否する。
    /// </summary>
    public UserChainMutationResult AddOrPromoteUserChain(Reservation requested, int predecessorId, int rootId, bool featureEnabled)
    {
        requested.Title = NormalizeReservationTitleForStorage(requested.Title, requested.ServiceName, requested.Id);
        using var con = db.Open();
        using var tx = con.BeginTransaction(System.Data.IsolationLevel.Serializable);

        List<Reservation> active;
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT id, network_id, transport_stream_id, service_id, event_id,
                       title, start_time, end_time, status, source, created_at, updated_at,
                       channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                       recording_started_at, recording_finished_at, service_name,
                       scheduled_start_time, source_rule_id, source_rule_name,
                       is_user_chain, user_chain_previous_id, user_chain_root_id,
                       recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                FROM reservations
                WHERE status IN ('scheduled','starting','recording','stopping','completed')
                ORDER BY id;
                """;
            active = ReadReservations(read).ToList();
        }

        var predecessor = active.FirstOrDefault(x => x.Id == predecessorId);
        if (predecessor is null)
            return new UserChainMutationResult(false, false, "predecessor_missing", 0, null);

        var eligibility = ChainReservationEligibilityContract.EvaluatePair(
            predecessor,
            requested.NetworkId,
            requested.TransportStreamId,
            requested.ServiceId,
            requested.StartTime,
            featureEnabled);
        if (!eligibility.IsEligible)
            return new UserChainMutationResult(
                false,
                false,
                eligibility.ReasonToken,
                predecessor.Id,
                predecessor);

        var existing = active
            .Where(x => x.NetworkId == requested.NetworkId
                && x.TransportStreamId == requested.TransportStreamId
                && x.ServiceId == requested.ServiceId
                && x.EventId == requested.EventId
                && x.Status is ReservationStatus.Scheduled or ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping)
            .OrderByDescending(x => x.Id)
            .FirstOrDefault();
        var successorId = existing?.Id ?? 0;

        var competingSuccessor = active.FirstOrDefault(x => x.IsUserChain
            && x.UserChainPreviousId == predecessorId
            && x.Id != successorId
            && x.Status is ReservationStatus.Scheduled or ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping);
        if (competingSuccessor is not null)
            return new UserChainMutationResult(false, false, "predecessor_already_has_successor", competingSuccessor.Id, competingSuccessor);

        if (existing is not null
            && existing.IsUserChain
            && existing.UserChainPreviousId.HasValue
            && existing.UserChainPreviousId.Value != predecessorId)
        {
            return new UserChainMutationResult(false, false, "successor_already_has_predecessor", existing.Id, existing);
        }

        var byId = active.ToDictionary(x => x.Id);
        if (successorId != 0)
        {
            var cursor = predecessor;
            var cycleGuard = new HashSet<int>();
            while (cursor.UserChainPreviousId.HasValue)
            {
                if (!cycleGuard.Add(cursor.Id) || cursor.UserChainPreviousId.Value == successorId)
                    return new UserChainMutationResult(false, false, "chain_cycle_detected", successorId, existing);
                if (!byId.TryGetValue(cursor.UserChainPreviousId.Value, out var previousCursor))
                    break;
                cursor = previousCursor;
            }
        }

        if (!TryResolveCanonicalRootForSuccessor(predecessor, byId, out var resolvedRootId, out var rootFailure, out var depth))
            return new UserChainMutationResult(false, false, rootFailure, predecessorId, predecessor);
        if (rootId != resolvedRootId)
            return new UserChainMutationResult(false, false, "chain_root_mismatch", predecessorId, predecessor);

        var now = DateTime.Now.ToString("O");
        int id;
        bool added;
        if (existing is not null)
        {
            using var update = con.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE reservations
                SET is_user_chain = 1,
                    user_chain_previous_id = $predecessorId,
                    user_chain_root_id = $rootId,
                    is_enabled = 1,
                    data_version = data_version + 1,
                    updated_at = $updated
                WHERE id = $id
                  AND data_version = $dataVersion;
                """;
            update.Parameters.AddWithValue("$id", existing.Id);
            update.Parameters.AddWithValue("$predecessorId", predecessorId);
            update.Parameters.AddWithValue("$rootId", resolvedRootId);
            update.Parameters.AddWithValue("$dataVersion", existing.DataVersion);
            update.Parameters.AddWithValue("$updated", now);
            if (update.ExecuteNonQuery() != 1)
                return new UserChainMutationResult(false, false, "compare_and_set_failed", existing.Id, existing);
            id = existing.Id;
            added = false;
        }
        else
        {
            using var insert = con.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO reservations
                  (network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id, data_version,
                   reservation_intent, created_through, created_by_plugin_id)
                VALUES
                  ($nid, $tsid, $sid, $eid,
                   $title, $start, $end, $status, $source, $now, $now,
                   $charg, 0, 1, $tuner, $svcname,
                   $start, $sourceRuleId, $sourceRuleName,
                   1, $predecessorId, $rootId, 0,
                   $reservationIntent, $createdThrough, $createdByPluginId);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$nid", requested.NetworkId);
            insert.Parameters.AddWithValue("$tsid", requested.TransportStreamId);
            insert.Parameters.AddWithValue("$sid", requested.ServiceId);
            insert.Parameters.AddWithValue("$eid", requested.EventId);
            insert.Parameters.AddWithValue("$title", requested.Title);
            insert.Parameters.AddWithValue("$start", requested.StartTime.ToString("O"));
            insert.Parameters.AddWithValue("$end", requested.EndTime.ToString("O"));
            insert.Parameters.AddWithValue("$status", requested.Status.ToString().ToLowerInvariant());
            insert.Parameters.AddWithValue("$source", requested.Source.ToString().ToLowerInvariant());
            insert.Parameters.AddWithValue("$charg", requested.ChannelArgument);
            insert.Parameters.AddWithValue("$tuner", requested.TunerName ?? string.Empty);
            insert.Parameters.AddWithValue("$svcname", requested.ServiceName ?? string.Empty);
            if (requested.SourceRuleId.HasValue) insert.Parameters.AddWithValue("$sourceRuleId", requested.SourceRuleId.Value); else insert.Parameters.AddWithValue("$sourceRuleId", DBNull.Value);
            insert.Parameters.AddWithValue("$sourceRuleName", requested.SourceRuleName ?? string.Empty);
            AddReservationProvenanceParameters(insert, requested);
            insert.Parameters.AddWithValue("$predecessorId", predecessorId);
            insert.Parameters.AddWithValue("$rootId", resolvedRootId);
            insert.Parameters.AddWithValue("$now", now);
            id = Convert.ToInt32(insert.ExecuteScalar());
            added = true;
        }

        tx.Commit();
        var result = GetById(id);
        if (!added)
        {
            LogStartingDataVersionMutation(
                "AddOrPromoteUserChain",
                existing,
                result,
                nameof(Reservation.IsUserChain),
                nameof(Reservation.UserChainPreviousId),
                nameof(Reservation.UserChainRootId),
                nameof(Reservation.IsEnabled),
                nameof(Reservation.DataVersion));
        }
        log.Add("CHAIN_MUTATION", $"R{predecessorId}->R{id}",
            $"result=APPLIED added={added} root=R{resolvedRootId} validation=unique_linear_acyclic depth={depth + 1} rule=release_contract");
        if (result is not null)
        {
            if (added)
            {
                mutationJournal.Record(ReservationMutationKind.Added, null, result);
            }
            else
            {
                mutationJournal.Record(ReservationMutationKind.Updated, existing, result, nameof(Reservation.IsUserChain), nameof(Reservation.UserChainPreviousId), nameof(Reservation.UserChainRootId));
            }
        }
        return new UserChainMutationResult(true, added, "applied", id, result);
    }

    private void LogStartingDataVersionMutation(
        string mutationOwner,
        Reservation? before,
        Reservation? after,
        params string[] changedFields)
    {
        if (before is null || after is null) return;
        if (before.Status != ReservationStatus.Starting && after.Status != ReservationStatus.Starting) return;
        if (before.DataVersion == after.DataVersion) return;

        var fields = changedFields is { Length: > 0 }
            ? string.Join(",", changedFields)
            : "-";
        log.Add("STARTING_DATA_VERSION_MUTATION", $"R{before.Id}",
            $"result=OBSERVED mutationOwner={mutationOwner} beforeStatus={before.Status} afterStatus={after.Status} " +
            $"beforeVersion={before.DataVersion} afterVersion={after.DataVersion} changedFields=[{fields}] " +
            $"beforeUpdatedAt={before.UpdatedAt:O} afterUpdatedAt={after.UpdatedAt:O} " +
            $"enabled={before.IsEnabled}->{after.IsEnabled} conflicted={before.IsConflicted}->{after.IsConflicted} " +
            $"userChain={before.IsUserChain}->{after.IsUserChain} " +
            $"chainPrev={(before.UserChainPreviousId.HasValue ? $"R{before.UserChainPreviousId.Value}" : "-")}->{(after.UserChainPreviousId.HasValue ? $"R{after.UserChainPreviousId.Value}" : "-")} " +
            $"chainRoot={(before.UserChainRootId.HasValue ? $"R{before.UserChainRootId.Value}" : "-")}->{(after.UserChainRootId.HasValue ? $"R{after.UserChainRootId.Value}" : "-")} " +
            $"rule=starting_data_version_mutation_owner_diagnostic");
    }

    private static bool IsActiveParentReservation(Reservation r)
    {
        // 定時EPG取得と録画前EPG確認は独立したSystemEpg運用予約であり、
        // ユーザー予約やチェーン親の候補には含めない。
        if (r.Source == ReservationSource.Epg) return false;
        if (r.Status is not (ReservationStatus.Scheduled or ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping)) return false;
        if (!r.IsEnabled) return false;
        return true;
    }

    private static string ReservationDedupeKey(Reservation r)
    {
        if (r.EventId != 0)
            return $"event:{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{r.EventId}";

        var end = r.EndTime.ToString("O");
        var title = NormalizeDedupeText(r.Title);
        if (r.Source == ReservationSource.Immediate)
        {
            // Immediate rewrites StartTime to the actual request time. Repeated delivery must not create
            // a different identity merely because the second request arrived a few milliseconds later.
            return $"immediate:{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{end}:{title}";
        }

        var start = r.StartTime.ToString("O");
        return $"time:{r.NetworkId}:{r.TransportStreamId}:{r.ServiceId}:{start}:{end}:{title}";
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

        var suppressTargets = new Dictionary<int, long>();
        foreach (var group in activeParents)
        {
            var keep = group
                .OrderByDescending(r => r.Status == ReservationStatus.Recording)
                .ThenBy(r => r.Id)
                .First();

            foreach (var duplicate in group.Where(r => r.Id != keep.Id && r.Status == ReservationStatus.Scheduled).OrderBy(r => r.Id))
            {
                suppressTargets[duplicate.Id] = duplicate.DataVersion;
                log.Add("RESERVATION_DEDUPE", "SUPPRESS_DUPLICATE",
                    $"result=CANCEL_DUPLICATE source={source} action={action} keep=R{keep.Id} duplicate=R{duplicate.Id} service={TrimForAudit(duplicate.ServiceName)} title={TrimTitleForAudit(duplicate.Title)} rawTitleBlank={RawTitleBlankForAudit(duplicate.Title)} start={duplicate.StartTime:MM/dd HH:mm:ss} end={duplicate.EndTime:MM/dd HH:mm:ss} key={group.Key} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
            }
        }

        if (suppressTargets.Count == 0)
            return 0;

        // RESERVATION_DEDUPE_VERSION_CAS_INVARIANT:
        // 重複判定後に開始・編集された予約を古い判定で取消さない。
        // 判定時のDataVersionをCAS条件にし、成功した取消だけ世代を進める。
        using var con = db.Open();
        using var tx = con.BeginTransaction();
        var affected = 0;
        foreach (var target in suppressTargets.OrderBy(x => x.Key))
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE reservations
                SET status = 'cancelled',
                    is_conflicted = 0,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'scheduled'
                  AND data_version = $dataVersion;
                """;
            cmd.Parameters.AddWithValue("$id", target.Key);
            cmd.Parameters.AddWithValue("$dataVersion", target.Value);
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
            affected += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return affected;
    }

    public sealed record AddOrGetActiveParentResult(
        int ReservationId,
        bool Added,
        bool Reactivated,
        Reservation Reservation);

    /// <summary>
    /// Active parent duplicate detection and INSERT are performed under one immediate SQLite transaction.
    /// HTTP retries, plugin retries and Immediate button repetition therefore converge on one reservation id.
    /// </summary>
    public AddOrGetActiveParentResult AddOrGetActiveParent(Reservation r)
    {
        r.Title = NormalizeReservationTitleForStorage(r.Title, r.ServiceName, r.Id);
        var key = ReservationDedupeKey(r);
        var now = DateTime.Now.ToString("O");

        using var con = db.Open();
        using var tx = con.BeginTransaction(deferred: false);

        Reservation? duplicate;
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT id, network_id, transport_stream_id, service_id, event_id,
                       title, start_time, end_time, status, source, created_at, updated_at,
                       channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                       recording_started_at, recording_finished_at, service_name,
                       scheduled_start_time, source_rule_id, source_rule_name,
                       is_user_chain, user_chain_previous_id, user_chain_root_id,
                       recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                FROM reservations
                WHERE source <> 'epg'
                  AND is_enabled = 1
                  AND status IN ('scheduled', 'starting', 'recording', 'stopping')
                ORDER BY CASE WHEN status IN ('recording', 'stopping') THEN 0 ELSE 1 END, id;
                """;
            duplicate = ReadReservations(read)
                .FirstOrDefault(existing => string.Equals(ReservationDedupeKey(existing), key, StringComparison.Ordinal));
        }

        if (duplicate is not null)
        {
            tx.Commit();
            log.Add("RESERVATION_DEDUPE", "ATOMIC_ADD",
                $"result=REUSE_EXISTING existing=R{duplicate.Id} requestedSource={r.Source} status={duplicate.Status} dataVersion={duplicate.DataVersion} key={key} rule=release_contract");
            return new AddOrGetActiveParentResult(duplicate.Id, false, false, duplicate);
        }

        // IMMEDIATE_RETRY_SINGLE_ID_CONTRACT (developer-approved repair):
        // A repeated Record Now click for the same on-air event must not create a new reservation id
        // merely because the previous attempt already reached Failed. Re-arm that same Immediate row
        // atomically; Cancelled remains terminal so an explicit cancellation permits a later new request.
        if (r.Source == ReservationSource.Immediate)
        {
            Reservation? failedImmediate;
            using (var readFailed = con.CreateCommand())
            {
                readFailed.Transaction = tx;
                readFailed.CommandText = """
                    SELECT id, network_id, transport_stream_id, service_id, event_id,
                           title, start_time, end_time, status, source, created_at, updated_at,
                           channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                           recording_started_at, recording_finished_at, service_name,
                           scheduled_start_time, source_rule_id, source_rule_name,
                           is_user_chain, user_chain_previous_id, user_chain_root_id,
                           recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                    FROM reservations
                    WHERE source = 'immediate'
                      AND status = 'failed'
                    ORDER BY id DESC;
                    """;
                failedImmediate = ReadReservations(readFailed)
                    .FirstOrDefault(existing => string.Equals(ReservationDedupeKey(existing), key, StringComparison.Ordinal));
            }

            if (failedImmediate is not null)
            {
                using var reactivate = con.CreateCommand();
                reactivate.Transaction = tx;
                reactivate.CommandText = """
                    UPDATE reservations
                    SET network_id = $nid,
                        transport_stream_id = $tsid,
                        service_id = $sid,
                        event_id = $eid,
                        title = $title,
                        start_time = $start,
                        end_time = $end,
                        scheduled_start_time = $start,
                        status = 'scheduled',
                        source = 'immediate',
                        updated_at = $now,
                        channel_argument = $charg,
                        is_conflicted = 0,
                        is_enabled = 1,
                        tuner_name = '',
                        actual_tuner_name = '',
                        recording_started_at = NULL,
                        recording_finished_at = NULL,
                        service_name = $svcname,
                        source_rule_id = NULL,
                        source_rule_name = '',
                        is_user_chain = 0,
                        user_chain_previous_id = NULL,
                        user_chain_root_id = NULL,
                        recording_recovery_chain_id = NULL,
                        recovery_parent_reservation_id = NULL,
                        data_version = data_version + 1
                    WHERE id = $id
                      AND status = 'failed'
                      AND source = 'immediate'
                      AND data_version = $dataVersion;
                    """;
                reactivate.Parameters.AddWithValue("$nid", r.NetworkId);
                reactivate.Parameters.AddWithValue("$tsid", r.TransportStreamId);
                reactivate.Parameters.AddWithValue("$sid", r.ServiceId);
                reactivate.Parameters.AddWithValue("$eid", r.EventId);
                reactivate.Parameters.AddWithValue("$title", r.Title);
                reactivate.Parameters.AddWithValue("$start", r.StartTime.ToString("O"));
                reactivate.Parameters.AddWithValue("$end", r.EndTime.ToString("O"));
                reactivate.Parameters.AddWithValue("$now", now);
                reactivate.Parameters.AddWithValue("$charg", r.ChannelArgument);
                reactivate.Parameters.AddWithValue("$svcname", r.ServiceName);
                reactivate.Parameters.AddWithValue("$id", failedImmediate.Id);
                reactivate.Parameters.AddWithValue("$dataVersion", failedImmediate.DataVersion);

                if (reactivate.ExecuteNonQuery() == 1)
                {
                    tx.Commit();
                    var reactivated = GetById(failedImmediate.Id)
                        ?? throw new InvalidOperationException($"Reactivated Immediate reservation R{failedImmediate.Id} could not be reloaded.");
                    log.Add("RESERVATION_DEDUPE", "IMMEDIATE_RETRY",
                        $"result=REACTIVATED existing=R{reactivated.Id} previousStatus=Failed requestedSource={r.Source} dataVersion={failedImmediate.DataVersion}->{reactivated.DataVersion} key={key} cancelledReuse=False action=rearm_same_reservation_id rule=release_contract");
                    mutationJournal.Record(ReservationMutationKind.Updated, failedImmediate, reactivated,
                        nameof(Reservation.Status), nameof(Reservation.StartTime), nameof(Reservation.EndTime),
                        nameof(Reservation.IsConflicted), nameof(Reservation.TunerName), nameof(Reservation.ActualTunerName));
                    return new AddOrGetActiveParentResult(reactivated.Id, false, true, reactivated);
                }
            }
        }

        int newId;
        using (var cmd = con.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO reservations
                  (network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id, data_version,
                   reservation_intent, created_through, created_by_plugin_id)
                VALUES
                  ($nid, $tsid, $sid, $eid,
                   $title, $start, $end, $status, $source, $now, $now,
                   $charg, 0, 1, '', $svcname,
                   $start, $sourceRuleId, $sourceRuleName,
                   $isUserChain, $userChainPreviousId, $userChainRootId, 0,
                   $reservationIntent, $createdThrough, $createdByPluginId);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$nid", r.NetworkId);
            cmd.Parameters.AddWithValue("$tsid", r.TransportStreamId);
            cmd.Parameters.AddWithValue("$sid", r.ServiceId);
            cmd.Parameters.AddWithValue("$eid", r.EventId);
            cmd.Parameters.AddWithValue("$title", r.Title);
            cmd.Parameters.AddWithValue("$start", r.StartTime.ToString("O"));
            cmd.Parameters.AddWithValue("$end", r.EndTime.ToString("O"));
            cmd.Parameters.AddWithValue("$status", r.Status.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$source", r.Source.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$charg", r.ChannelArgument);
            cmd.Parameters.AddWithValue("$svcname", r.ServiceName);
            if (r.SourceRuleId.HasValue) cmd.Parameters.AddWithValue("$sourceRuleId", r.SourceRuleId.Value); else cmd.Parameters.AddWithValue("$sourceRuleId", DBNull.Value);
            cmd.Parameters.AddWithValue("$sourceRuleName", r.SourceRuleName ?? "");
            cmd.Parameters.AddWithValue("$isUserChain", r.IsUserChain ? 1 : 0);
            if (r.UserChainPreviousId.HasValue) cmd.Parameters.AddWithValue("$userChainPreviousId", r.UserChainPreviousId.Value); else cmd.Parameters.AddWithValue("$userChainPreviousId", DBNull.Value);
            if (r.UserChainRootId.HasValue) cmd.Parameters.AddWithValue("$userChainRootId", r.UserChainRootId.Value); else cmd.Parameters.AddWithValue("$userChainRootId", DBNull.Value);
            AddReservationProvenanceParameters(cmd, r);
            cmd.Parameters.AddWithValue("$now", now);
            newId = Convert.ToInt32(cmd.ExecuteScalar());
        }

        tx.Commit();
        var added = GetById(newId) ?? throw new InvalidOperationException($"Inserted reservation R{newId} could not be reloaded.");
        log.Add("RESERVATION_AUDIT", "ADD_ATOMIC",
            $"service={TrimForAudit(r.ServiceName)} title={TrimTitleForAudit(r.Title)} rawTitleBlank={RawTitleBlankForAudit(r.Title)} id=R{newId} status={r.Status} source={r.Source} enabled={r.IsEnabled} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} key={key} rule=release_contract");
        mutationJournal.Record(ReservationMutationKind.Added, null, added);
        return new AddOrGetActiveParentResult(newId, true, false, added);
    }

    // RESERVATION_PROVENANCE_PARAMETER_INVARIANT:
    // reservation_intent / created_through / created_by_plugin_id は予約INSERTの共通必須列。
    // 新しいINSERT入口を追加する際に個別バインドを複製せず、必ずこの正本を使用する。
    // Plugin IDは上流のRuntime Contextで確定済みの値だけを保存し、SQL層で推測・上書きしない。
    private static void AddReservationProvenanceParameters(SqliteCommand command, Reservation reservation)
    {
        command.Parameters.AddWithValue("$reservationIntent", reservation.Intent.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$createdThrough", reservation.CreatedThrough ?? string.Empty);
        command.Parameters.AddWithValue("$createdByPluginId", reservation.CreatedByPluginId ?? string.Empty);
    }

    public int Add(Reservation r)
    {
        // release_contract: 予約入口の最終防衛。EPG DB 側で修復できたタイトルだけでなく、
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
               is_user_chain, user_chain_previous_id, user_chain_root_id,
               reservation_intent, created_through, created_by_plugin_id)
            VALUES
              ($nid, $tsid, $sid, $eid,
               $title, $start, $end, $status, $source, $now, $now,
               $charg, 0, 1, '', $svcname,
               $start, $sourceRuleId, $sourceRuleName,
               $isUserChain, $userChainPreviousId, $userChainRootId,
               $reservationIntent, $createdThrough, $createdByPluginId);
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
        AddReservationProvenanceParameters(cmd, r);
        cmd.Parameters.AddWithValue("$now",     now);
        var newId = Convert.ToInt32(cmd.ExecuteScalar());
        log.Add("RESERVATION_AUDIT", "ADD",
            $"service={TrimForAudit(r.ServiceName)} title={TrimTitleForAudit(r.Title)} rawTitleBlank={RawTitleBlankForAudit(r.Title)} id=R{newId} status={r.Status} source={r.Source} enabled={r.IsEnabled} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} svcId={r.ServiceId} ch={r.ChannelArgument} tuner={SafeTuner(r.TunerName)} userChain={r.IsUserChain} chainPrev={(r.UserChainPreviousId.HasValue ? $"R{r.UserChainPreviousId.Value}" : "-")} chainRoot={(r.UserChainRootId.HasValue ? $"R{r.UserChainRootId.Value}" : "-")}");
        var added = GetById(newId);
        if (added is not null) mutationJournal.Record(ReservationMutationKind.Added, null, added);
        return newId;
    }


    /// <summary>
    /// 録画開始に失敗して終端化した明示チェーン前段に対し、Scheduledの後続範囲も同一TransactionでFailedへ閉じる。
    /// 壊れたチェーン後続を通常の独立予約として再割当してはならない。チェーン構造とTuner証拠は診断用に保持する。
    /// この終端伝播契約を変更する場合は、チェーン録画仕様の開発者承認を必要とする。
    /// </summary>
    public IReadOnlyList<Reservation> FailScheduledUserChainSuccessorsAfterPredecessorFailure(int predecessorId, string reason)
    {
        using var con = db.Open();
        using var tx = con.BeginTransaction(System.Data.IsolationLevel.Serializable);

        var active = new List<Reservation>();
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT id, network_id, transport_stream_id, service_id, event_id,
                       title, start_time, end_time, status, source, created_at, updated_at,
                       channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                       recording_started_at, recording_finished_at, service_name,
                       scheduled_start_time, source_rule_id, source_rule_name,
                       is_user_chain, user_chain_previous_id, user_chain_root_id,
                       recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                FROM reservations
                WHERE status = 'scheduled'
                  AND is_user_chain = 1
                ORDER BY start_time, id;
                """;
            active = ReadReservations(read, projectRuntimeStatus: false).ToList();
        }

        var byPrevious = active
            .Where(x => x.UserChainPreviousId.HasValue)
            .GroupBy(x => x.UserChainPreviousId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.StartTime).ThenBy(x => x.Id).ToList());
        var targets = new List<Reservation>();
        var queue = new Queue<int>();
        var visited = new HashSet<int>();
        queue.Enqueue(predecessorId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (!byPrevious.TryGetValue(current, out var children)) continue;
            foreach (var child in children)
            {
                targets.Add(child);
                queue.Enqueue(child.Id);
            }
        }

        if (targets.Count == 0)
        {
            tx.Commit();
            return Array.Empty<Reservation>();
        }

        var now = DateTime.Now;
        foreach (var target in targets)
        {
            using var update = con.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE reservations
                SET status = 'failed',
                    is_conflicted = 1,
                    recording_finished_at = CASE WHEN COALESCE(recording_finished_at, '') = '' THEN $finished ELSE recording_finished_at END,
                    data_version = data_version + 1,
                    updated_at = $updated
                WHERE id = $id
                  AND status = 'scheduled'
                  AND is_user_chain = 1
                  AND user_chain_previous_id = $previousId
                  AND data_version = $dataVersion;
                """;
            update.Parameters.AddWithValue("$finished", now.ToString("O"));
            update.Parameters.AddWithValue("$updated", now.ToString("O"));
            update.Parameters.AddWithValue("$id", target.Id);
            update.Parameters.AddWithValue("$previousId", target.UserChainPreviousId!.Value);
            update.Parameters.AddWithValue("$dataVersion", target.DataVersion);
            if (update.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                log.Add("CHAIN_PREDECESSOR_FAILURE", $"R{predecessorId}",
                    $"result=CAS_REJECTED target=R{target.Id} reason={reason} action=rollback_without_partial_terminalization rule=recording_lifecycle_cas_contract");
                return Array.Empty<Reservation>();
            }
        }

        tx.Commit();
        var after = targets.Select(x => GetById(x.Id)).Where(x => x is not null).Cast<Reservation>().ToList();
        foreach (var before in targets)
        {
            var current = after.FirstOrDefault(x => x.Id == before.Id);
            if (current is null) continue;
            mutationJournal.Record(ReservationMutationKind.RecordingFailed, before, current,
                new Dictionary<string, string?>
                {
                    ["reason"] = "chain_predecessor_failed",
                    ["predecessorId"] = predecessorId.ToString(),
                    ["detail"] = reason
                },
                nameof(Reservation.Status), nameof(Reservation.IsConflicted),
                nameof(Reservation.RecordingFinishedAt), nameof(Reservation.DataVersion));
        }
        log.Add("CHAIN_PREDECESSOR_FAILURE", $"R{predecessorId}",
            $"result=SUCCESS failedSuccessors={after.Count} targets=[{string.Join(',', after.Select(x => $"R{x.Id}"))}] reason={reason} action=terminalize_chain_range_without_independent_reallocation tunerEvidencePreserved=True rule=recording_lifecycle_cas_contract");
        return after;
    }

    /// <summary>
    /// 競合のまま録画開始時刻へ到達した予約を、再起動後にも復活しない終端状態へ確定する。
    /// REC_SKIPPED_BY_CONFLICT のユーザー運用イベントは呼び出し側で発行し、ここでは予約本体だけを閉じる。
    ///
    /// CONFLICT_TERMINAL_TUNER_EVIDENCE_INVARIANT:
    /// TunerName は共通割当が確定した計画証拠、ActualTunerName は実行実績証拠である。
    /// 競合終端化は状態と競合フラグを所有するが、診断・チェーン検証に必要なTuner証拠は所有しない。
    /// Failed化を理由に TunerName / ActualTunerName を空へ消す処理を再導入しない。
    /// </summary>
    public bool FinalizeSkippedByConflictAtDue(int id, long expectedDataVersion, string group, string reason)
    {
        var before = GetById(id);
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET status = 'failed',
                is_conflicted = 1,
                recording_finished_at = CASE WHEN COALESCE(recording_finished_at, '') = '' THEN $finished ELSE recording_finished_at END,
                data_version = data_version + 1,
                updated_at = $now
            WHERE id = $id
              AND status = 'scheduled'
              AND data_version = $dataVersion;
            """;
        var now = DateTime.Now;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$finished", now.ToString("O"));
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$dataVersion", expectedDataVersion);
        var affected = cmd.ExecuteNonQuery();

        log.Add("RESERVATION_AUDIT", "SKIP_CONFLICT_FINALIZE",
            $"service={TrimForAudit(before?.ServiceName)} title={TrimTitleForAudit(before?.Title)} rawTitleBlank={RawTitleBlankForAudit(before?.Title)} id=R{id} affected={affected} from={(before is null ? "<missing>" : before.Status.ToString())} to=Failed reason={reason} group={group} enabled={(before?.IsEnabled.ToString() ?? "-")} conflicted={(before?.IsConflicted.ToString() ?? "-")} start={(before is null ? "-" : before.StartTime.ToString("MM/dd HH:mm:ss"))} end={(before is null ? "-" : before.EndTime.ToString("MM/dd HH:mm:ss"))} tuner={SafeTuner(EffectiveTunerName(before))} rule=release_contract");
        if (affected > 0)
        {
            mutationJournal.Record(
                ReservationMutationKind.RecordingFailed,
                before,
                GetById(id),
                new Dictionary<string, string?>
                {
                    ["suppressUserEvent"] = "recording_skipped_by_conflict_specialized",
                    ["reason"] = reason,
                    ["group"] = group
                },
                nameof(Reservation.Status), nameof(Reservation.IsConflicted),
                nameof(Reservation.RecordingFinishedAt), nameof(Reservation.DataVersion));
        }
        return affected > 0;
    }

    /// <summary>
    /// 予約の業務状態をDataVersion付きCompare-and-Setで遷移させる。
    /// Starting/Stopping導入中の正規入口。イベント配送や割当再評価は上位Application Serviceが所有する。
    /// </summary>
    public ReservationLifecycleTransitionResult TryTransitionLifecycle(
        int id,
        ReservationStatus expectedStatus,
        long expectedDataVersion,
        ReservationStatus nextStatus,
        bool requireEnabled = false)
    {
        using var con = db.Open();
        using var tx = con.BeginTransaction();

        Reservation? before;
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT id, network_id, transport_stream_id, service_id, event_id,
                       title, start_time, end_time, status, source, created_at, updated_at,
                       channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                       recording_started_at, recording_finished_at, service_name,
                       scheduled_start_time, source_rule_id, source_rule_name,
                       is_user_chain, user_chain_previous_id, user_chain_root_id,
                       recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                FROM reservations
                WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", id);
            before = ReadReservations(read, projectRuntimeStatus: false).FirstOrDefault();
        }

        if (before is null)
        {
            tx.Rollback();
            return new(false, "not_found", id, null, null, expectedDataVersion, expectedDataVersion, null);
        }

        if (before.Status != expectedStatus)
        {
            tx.Rollback();
            return new(false, "status_mismatch", id, before.Status, before.Status, before.DataVersion, before.DataVersion, before);
        }

        if (before.DataVersion != expectedDataVersion)
        {
            tx.Rollback();
            return new(false, "data_version_mismatch", id, before.Status, before.Status, before.DataVersion, before.DataVersion, before);
        }

        if (requireEnabled && !before.IsEnabled)
        {
            tx.Rollback();
            return new(false, "disabled", id, before.Status, before.Status, before.DataVersion, before.DataVersion, before);
        }

        var now = DateTime.Now;
        using (var update = con.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = requireEnabled
                ? """
                  UPDATE reservations
                  SET status = $next, data_version = data_version + 1, updated_at = $now
                  WHERE id = $id
                    AND status = $expected
                    AND data_version = $version
                    AND is_enabled = 1;
                  """
                : """
                  UPDATE reservations
                  SET status = $next, data_version = data_version + 1, updated_at = $now
                  WHERE id = $id
                    AND status = $expected
                    AND data_version = $version;
                  """;
            update.Parameters.AddWithValue("$next", nextStatus.ToString().ToLowerInvariant());
            update.Parameters.AddWithValue("$expected", expectedStatus.ToString().ToLowerInvariant());
            update.Parameters.AddWithValue("$version", expectedDataVersion);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            if (update.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                var latest = GetByIdPersisted(id);
                return new(false, "compare_and_set_failed", id, before.Status, latest?.Status, before.DataVersion, latest?.DataVersion ?? before.DataVersion, latest);
            }
        }

        tx.Commit();
        var after = GetByIdPersisted(id);
        LogStartingDataVersionMutation(
            $"TryTransitionLifecycle:{expectedStatus}->{nextStatus}",
            before,
            after,
            nameof(Reservation.Status),
            nameof(Reservation.DataVersion));
        log.Add("RESERVATION_LIFECYCLE_CAS", $"R{id}",
            $"result=APPLIED from={expectedStatus} to={nextStatus} dataVersion={expectedDataVersion}->{after?.DataVersion ?? expectedDataVersion + 1} enabledRequired={requireEnabled} rule=release_contract");
        return new(true, "applied", id, expectedStatus, after?.Status ?? nextStatus, expectedDataVersion, after?.DataVersion ?? expectedDataVersion + 1, after);
    }

    /// <summary>
    /// 起動時またはResume時に生存録画workerを再Attachする際、Recording行の実チューナー情報だけをCASで収束させる。
    /// 既に別のActualTunerが保存されている場合は上書きせず拒否する。
    /// </summary>
    public ReservationLifecycleTransitionResult TryAttachRecordingOwner(
        int id,
        long expectedDataVersion,
        string actualTunerName,
        DateTime attachedAt)
    {
        var before = GetByIdPersisted(id);
        if (before is null)
            return new(false, "not_found", id, null, null, expectedDataVersion, expectedDataVersion, null);
        if (before.Status != ReservationStatus.Recording)
            return new(false, "status_mismatch", id, before.Status, before.Status, expectedDataVersion, before.DataVersion, before);
        if (before.DataVersion != expectedDataVersion)
            return new(false, "data_version_mismatch", id, before.Status, before.Status, expectedDataVersion, before.DataVersion, before);
        if (!string.IsNullOrWhiteSpace(before.ActualTunerName)
            && !string.Equals(before.ActualTunerName, actualTunerName, StringComparison.OrdinalIgnoreCase))
            return new(false, "actual_tuner_mismatch", id, before.Status, before.Status, expectedDataVersion, before.DataVersion, before);

        // RECORDING_OWNER_ATTACH_IDEMPOTENCY_INVARIANT:
        // 同じowner証拠が既に永続化済みなら、DataVersionを進める偽更新やMutationを発生させない。
        if (string.Equals(before.ActualTunerName, actualTunerName, StringComparison.OrdinalIgnoreCase)
            && before.RecordingStartedAt.HasValue)
            return new(true, "already_attached", id, before.Status, before.Status, expectedDataVersion, before.DataVersion, before);

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
UPDATE reservations
SET actual_tuner_name = $actualTuner,
    recording_started_at = COALESCE(recording_started_at, $attachedAt),
    data_version = data_version + 1,
    updated_at = $updatedAt
WHERE id = $id
  AND status = 'Recording'
  AND data_version = $expectedVersion
  AND (actual_tuner_name IS NULL OR actual_tuner_name = '' OR actual_tuner_name = $actualTuner);";
        cmd.Parameters.AddWithValue("$actualTuner", actualTunerName);
        cmd.Parameters.AddWithValue("$attachedAt", attachedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$expectedVersion", expectedDataVersion);
        var affected = cmd.ExecuteNonQuery();
        if (affected != 1)
        {
            tx.Rollback();
            var current = GetByIdPersisted(id);
            return new(false, "compare_and_set_failed", id, before.Status, current?.Status, expectedDataVersion, current?.DataVersion ?? expectedDataVersion, current);
        }
        tx.Commit();
        var after = GetByIdPersisted(id);
        // RECORDING_OWNER_ATTACH_MUTATION_INVARIANT:
        // owner再AttachでActualTunerName・RecordingStartedAt・DataVersionを更新した場合も、
        // DB更新だけで終わらせず、実変更フィールドを同じ成功条件でMutationへ公開する。
        mutationJournal.Record(
            ReservationMutationKind.Updated,
            before,
            after,
            new Dictionary<string, string?>
            {
                ["source"] = "recording_owner_attach",
                ["runtimeOwnerEvidence"] = "true"
            },
            nameof(Reservation.ActualTunerName),
            nameof(Reservation.RecordingStartedAt),
            nameof(Reservation.DataVersion));
        return new(true, "applied", id, before.Status, after!.Status, expectedDataVersion, after.DataVersion, after);
    }

    /// <summary>
    /// Scheduled内部予約・ユーザー予約を終端状態へ移す正規CAS入口。
    /// MANUAL/SYSTEM_TERMINAL_VERSION_CAS_INVARIANT:
    /// 判定時DataVersionとScheduled状態をcommit時に再確認し、無条件UpdateStatusへ戻さない。
    /// </summary>
    public ReservationLifecycleTransitionResult TryFinalizeScheduledReservation(
        int id,
        long expectedDataVersion,
        ReservationStatus finalStatus,
        string source,
        string? failureReason = null)
    {
        if (finalStatus is not (ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed))
            return new(false, "invalid_final_status", id, ReservationStatus.Scheduled, null, expectedDataVersion, expectedDataVersion, GetByIdPersisted(id));

        var before = GetByIdPersisted(id);
        var result = TryTransitionLifecycle(id, ReservationStatus.Scheduled, expectedDataVersion, finalStatus);
        if (!result.Applied)
            return result;

        var mutationKind = finalStatus switch
        {
            ReservationStatus.Completed => ReservationMutationKind.RecordingCompleted,
            ReservationStatus.Failed => ReservationMutationKind.RecordingFailed,
            _ => ReservationMutationKind.Updated
        };
        var terminalMetadata = new Dictionary<string, string?>
        {
            ["source"] = source,
            ["terminalCas"] = "true"
        };
        if (finalStatus == ReservationStatus.Failed && !string.IsNullOrWhiteSpace(failureReason))
            terminalMetadata["reason"] = failureReason;

        mutationJournal.Record(
            mutationKind,
            before,
            result.Reservation,
            terminalMetadata,
            nameof(Reservation.Status),
            nameof(Reservation.DataVersion));
        log.Add("RESERVATION_SCHEDULED_TERMINAL_CAS", $"R{id}",
            $"result=APPLIED from=Scheduled to={finalStatus} dataVersion={expectedDataVersion}->{result.CurrentDataVersion} source={source} rule=release_contract");
        return result;
    }

    /// <summary>
    /// worker起動commit前のStarting予約に対する手動取消専用CAS。
    /// 起動処理はcommit拒否を検知して未commit workerを停止・lease解放する。
    /// </summary>
    public ReservationLifecycleTransitionResult TryCancelStartingReservation(int id, long expectedDataVersion, string source)
    {
        var before = GetByIdPersisted(id);
        var result = TryTransitionLifecycle(id, ReservationStatus.Starting, expectedDataVersion, ReservationStatus.Cancelled);
        if (!result.Applied)
            return result;

        mutationJournal.Record(
            ReservationMutationKind.Updated,
            before,
            result.Reservation,
            new Dictionary<string, string?>
            {
                ["source"] = source,
                ["terminalCas"] = "true"
            },
            nameof(Reservation.Status),
            nameof(Reservation.DataVersion));
        log.Add("RESERVATION_STARTING_CANCEL_CAS", $"R{id}",
            $"result=APPLIED from=Starting to=Cancelled dataVersion={expectedDataVersion}->{result.CurrentDataVersion} source={source} rule=release_contract");
        return result;
    }

    public ReservationLifecycleTransitionResult TryBeginRecordingStart(int id, long expectedDataVersion)
        => TryTransitionLifecycle(id, ReservationStatus.Scheduled, expectedDataVersion, ReservationStatus.Starting, requireEnabled: true);

    public ReservationLifecycleTransitionResult TryBeginRecordingStop(int id, long expectedDataVersion)
        => TryTransitionLifecycle(id, ReservationStatus.Recording, expectedDataVersion, ReservationStatus.Stopping);

    /// <summary>
    /// 停止処理を完了したStopping予約を、終了時刻と同じTransactionで終端状態へ確定する。
    /// </summary>
    public ReservationLifecycleTransitionResult TryCompleteRecordingStop(
        int id,
        long expectedDataVersion,
        ReservationStatus finalStatus,
        string? failureReason = null)
    {
        if (finalStatus is not (ReservationStatus.Completed or ReservationStatus.Failed or ReservationStatus.Cancelled))
            return new(false, "invalid_final_status", id, ReservationStatus.Stopping, null, expectedDataVersion, expectedDataVersion, GetByIdPersisted(id));

        using var con = db.Open();
        using var tx = con.BeginTransaction();

        Reservation? before;
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT id, network_id, transport_stream_id, service_id, event_id,
                       title, start_time, end_time, status, source, created_at, updated_at,
                       channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                       recording_started_at, recording_finished_at, service_name,
                       scheduled_start_time, source_rule_id, source_rule_name,
                       is_user_chain, user_chain_previous_id, user_chain_root_id,
                       recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                FROM reservations
                WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", id);
            before = ReadReservations(read, projectRuntimeStatus: false).FirstOrDefault();
        }

        if (before is null)
        {
            tx.Rollback();
            return new(false, "not_found", id, null, null, expectedDataVersion, expectedDataVersion, null);
        }

        if (before.Status != ReservationStatus.Stopping)
        {
            tx.Rollback();
            return new(false, "status_mismatch", id, before.Status, before.Status, before.DataVersion, before.DataVersion, before);
        }

        if (before.DataVersion != expectedDataVersion)
        {
            tx.Rollback();
            return new(false, "data_version_mismatch", id, before.Status, before.Status, before.DataVersion, before.DataVersion, before);
        }

        var now = DateTime.Now;
        using (var update = con.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE reservations
                SET status = $status,
                    recording_finished_at = $finished,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'stopping'
                  AND data_version = $version;
                """;
            update.Parameters.AddWithValue("$status", finalStatus.ToString().ToLowerInvariant());
            update.Parameters.AddWithValue("$finished", now.ToString("O"));
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$version", expectedDataVersion);
            if (update.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                var latest = GetByIdPersisted(id);
                return new(false, "compare_and_set_failed", id, before.Status, latest?.Status, before.DataVersion, latest?.DataVersion ?? before.DataVersion, latest);
            }
        }

        tx.Commit();
        var after = GetByIdPersisted(id);
        log.Add("RESERVATION_LIFECYCLE_CAS", $"R{id}",
            $"result=APPLIED from=Stopping to={finalStatus} dataVersion={expectedDataVersion}->{after?.DataVersion ?? expectedDataVersion + 1} finishedAt={now:O} rule=release_contract");

        // RECORDING_STOP_TERMINAL_MUTATION_INVARIANT:
        // Stopping -> terminal status and RecordingFinishedAt are one committed lifecycle mutation.
        // Do not publish the terminal status from a later result-file, quality, or cleanup branch;
        // those side effects may fail independently after the reservation transaction has committed.
        // The mutation must therefore be recorded exactly once here, from the same applied CAS result.
        var mutationKind = finalStatus switch
        {
            ReservationStatus.Completed => ReservationMutationKind.RecordingCompleted,
            ReservationStatus.Failed => ReservationMutationKind.RecordingFailed,
            _ => ReservationMutationKind.Updated
        };
        var terminalMetadata = finalStatus == ReservationStatus.Failed && !string.IsNullOrWhiteSpace(failureReason)
            ? new Dictionary<string, string?> { ["reason"] = failureReason }
            : null;
        mutationJournal.Record(
            mutationKind,
            before,
            after,
            terminalMetadata,
            nameof(Reservation.Status),
            nameof(Reservation.RecordingFinishedAt),
            nameof(Reservation.DataVersion));

        return new(true, "applied", id, ReservationStatus.Stopping, after?.Status ?? finalStatus, expectedDataVersion, after?.DataVersion ?? expectedDataVersion + 1, after);
    }

    /// <summary>
    /// worker起動成功後のStarting予約を、実チューナー・開始実績と同じTransactionでRecordingへ確定する。
    /// </summary>
    public ReservationLifecycleTransitionResult TryCompleteRecordingStart(int id, long expectedDataVersion, string actualTunerName)
    {
        using var con = db.Open();
        using var tx = con.BeginTransaction();

        Reservation? before;
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT id, network_id, transport_stream_id, service_id, event_id,
                       title, start_time, end_time, status, source, created_at, updated_at,
                       channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                       recording_started_at, recording_finished_at, service_name,
                       scheduled_start_time, source_rule_id, source_rule_name,
                       is_user_chain, user_chain_previous_id, user_chain_root_id,
                       recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                FROM reservations
                WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", id);
            before = ReadReservations(read, projectRuntimeStatus: false).FirstOrDefault();
        }

        if (before is null)
        {
            tx.Rollback();
            return new(false, "not_found", id, null, null, expectedDataVersion, expectedDataVersion, null);
        }

        if (before.Status != ReservationStatus.Starting)
        {
            tx.Rollback();
            return new(false, "status_mismatch", id, before.Status, before.Status, before.DataVersion, before.DataVersion, before);
        }

        if (before.DataVersion != expectedDataVersion)
        {
            tx.Rollback();
            log.Add("STARTING_DATA_VERSION_MISMATCH", $"R{id}",
                $"result=DETECTED mutationOwner=unknown_at_commit expectedVersion={expectedDataVersion} actualVersion={before.DataVersion} " +
                $"status={before.Status} updatedAt={before.UpdatedAt:O} enabled={before.IsEnabled} conflicted={before.IsConflicted} " +
                $"userChain={before.IsUserChain} chainPrev={(before.UserChainPreviousId.HasValue ? $"R{before.UserChainPreviousId.Value}" : "-")} " +
                $"chainRoot={(before.UserChainRootId.HasValue ? $"R{before.UserChainRootId.Value}" : "-")} " +
                $"actualTuner={before.ActualTunerName ?? "-"} recordingStartedAt={(before.RecordingStartedAt.HasValue ? before.RecordingStartedAt.Value.ToString("O") : "-")} " +
                $"rule=starting_data_version_mutation_owner_diagnostic");
            return new(false, "data_version_mismatch", id, before.Status, before.Status, before.DataVersion, before.DataVersion, before);
        }

        var actual = actualTunerName ?? string.Empty;
        var now = DateTime.Now;
        using (var update = con.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE reservations
                SET status = 'recording',
                    is_conflicted = 0,
                    actual_tuner_name = $tuner,
                    recording_started_at = CASE WHEN COALESCE(recording_started_at, '') = '' THEN $now ELSE recording_started_at END,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'starting'
                  AND data_version = $version;
                """;
            update.Parameters.AddWithValue("$tuner", actual);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$version", expectedDataVersion);
            if (update.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                var latest = GetByIdPersisted(id);
                return new(false, "compare_and_set_failed", id, before.Status, latest?.Status, before.DataVersion, latest?.DataVersion ?? before.DataVersion, latest);
            }
        }

        tx.Commit();
        var after = GetByIdPersisted(id);
        log.Add("RESERVATION_LIFECYCLE_CAS", $"R{id}",
            $"result=APPLIED from=Starting to=Recording dataVersion={expectedDataVersion}->{after?.DataVersion ?? expectedDataVersion + 1} actualTuner={SafeTuner(actual)} atomicStartEvidence=True rule=release_contract");
        // RECORDING_START_MUTATION_FIELD_INVARIANT:
        // Recording開始CASが実際に変更したStatus・競合解除・実Tuner・開始実績・DataVersionを漏れなく通知する。
        // DataVersionだけ、またはIsConflictedだけを通知から落とす実装へ戻してはならない。
        mutationJournal.Record(
            ReservationMutationKind.RecordingStarted,
            before,
            after,
            nameof(Reservation.Status),
            nameof(Reservation.IsConflicted),
            nameof(Reservation.ActualTunerName),
            nameof(Reservation.RecordingStartedAt),
            nameof(Reservation.DataVersion));
        return new(true, "applied", id, ReservationStatus.Starting, after?.Status ?? ReservationStatus.Recording, expectedDataVersion, after?.DataVersion ?? expectedDataVersion + 1, after);
    }

    public ReservationLifecycleTransitionResult TryRollbackRecordingStart(int id, long expectedDataVersion)
        => TryTransitionLifecycle(id, ReservationStatus.Starting, expectedDataVersion, ReservationStatus.Scheduled);

    public ReservationLifecycleTransitionResult TryFailRecordingStart(
        int id,
        long expectedDataVersion,
        string? reason = null,
        string? failureKind = null)
    {
        var before = GetByIdPersisted(id);

        // INTERRUPTED_START_FAILURE_PROVENANCE_CRASH_INVARIANT:
        // OpenTuner後のworker外部消失は、Failed commit直後にHostまで失われると通常のStarting/Recording回収から外れる。
        // provenanceを先に永続化し、Hostがこの直後に落ちてもStarting orphan回収、Failedまで進めばstartup rearmの
        // どちらかへ必ず到達できるようにする。CAS不成立時のstale provenanceはstatus/dataVersion一致条件で無効化される。
        if (!string.IsNullOrWhiteSpace(failureKind))
        {
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO recording_start_failure_provenance
                    (reservation_id, failure_kind, reason, failed_at, data_version)
                VALUES
                    ($id, $kind, $reason, $failedAt, $dataVersion)
                ON CONFLICT(reservation_id) DO UPDATE SET
                    failure_kind = excluded.failure_kind,
                    reason = excluded.reason,
                    failed_at = excluded.failed_at,
                    data_version = excluded.data_version;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$kind", failureKind.Trim());
            cmd.Parameters.AddWithValue("$reason", reason ?? string.Empty);
            cmd.Parameters.AddWithValue("$failedAt", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$dataVersion", expectedDataVersion + 1);
            cmd.ExecuteNonQuery();
        }

        var result = TryTransitionLifecycle(id, ReservationStatus.Starting, expectedDataVersion, ReservationStatus.Failed);
        if (!result.Applied) return result;

        mutationJournal.Record(
            ReservationMutationKind.RecordingFailed,
            before,
            result.Reservation,
            string.IsNullOrWhiteSpace(reason) ? null : new Dictionary<string, string?>
            {
                ["reason"] = reason,
                ["failureKind"] = failureKind
            },
            nameof(Reservation.Status),
            nameof(Reservation.DataVersion));
        return result;
    }

    public IReadOnlyList<Reservation> RearmInterruptedRecordingStartFailuresAtStartup(DateTime now)
    {
        const string recoverableFailureKind = "worker_exited_after_open_before_recording";
        var rearmed = new List<Reservation>();

        using var con = db.Open();
        using var tx = con.BeginTransaction();
        using var read = con.CreateCommand();
        read.Transaction = tx;
        read.CommandText = """
            SELECT r.id, r.data_version
            FROM reservations r
            INNER JOIN recording_start_failure_provenance p ON p.reservation_id = r.id
            WHERE r.status = 'failed'
              AND r.is_enabled = 1
              AND r.source <> 'epg'
              AND r.end_time > $now
              AND p.failure_kind = $kind
              AND p.data_version = r.data_version
            ORDER BY r.start_time, r.id;
            """;
        read.Parameters.AddWithValue("$now", now.ToString("O"));
        read.Parameters.AddWithValue("$kind", recoverableFailureKind);
        var candidates = new List<(int Id, long DataVersion)>();
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
                candidates.Add((reader.GetInt32(0), reader.GetInt64(1)));
        }

        foreach (var candidate in candidates)
        {
            using var update = con.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE reservations
                SET status = 'scheduled',
                    actual_tuner_name = '',
                    recording_started_at = '',
                    recording_finished_at = '',
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'failed'
                  AND data_version = $version;
                """;
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", candidate.Id);
            update.Parameters.AddWithValue("$version", candidate.DataVersion);
            if (update.ExecuteNonQuery() != 1)
                continue;

            using var delete = con.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM recording_start_failure_provenance WHERE reservation_id = $id;";
            delete.Parameters.AddWithValue("$id", candidate.Id);
            delete.ExecuteNonQuery();
        }

        tx.Commit();

        foreach (var candidate in candidates)
        {
            var after = GetByIdPersisted(candidate.Id);
            if (after?.Status != ReservationStatus.Scheduled || after.DataVersion != candidate.DataVersion + 1)
                continue;
            rearmed.Add(after);
        }

        return rearmed;
    }

    /// <summary>
    /// 生存workerが失われ、再起動にも失敗したRecording予約を終了実績と同じCASでFailedへ確定する。
    /// </summary>
    public ReservationLifecycleTransitionResult TryFinalizeRecordingRuntimeFailure(
        int id,
        long expectedDataVersion,
        DateTime finishedAt,
        string? reason = null)
    {
        // RECORDING_RUNTIME_FAILURE_TERMINAL_CAS_INVARIANT:
        // worker消失・再起動失敗を理由にUpdateStatus(force:true)へ戻してはならない。
        // 判定に使ったRecording世代だけを、RecordingFinishedAtとDataVersionを含む単一CASで終端化する。
        // Stoppingは通常停止のTryCompleteRecordingStopが所有するため、この入口では触らない。
        using var con = db.Open();
        using var tx = con.BeginTransaction();

        Reservation? before;
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT id, network_id, transport_stream_id, service_id, event_id,
                       title, start_time, end_time, status, source, created_at, updated_at,
                       channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                       recording_started_at, recording_finished_at, service_name,
                       scheduled_start_time, source_rule_id, source_rule_name,
                       is_user_chain, user_chain_previous_id, user_chain_root_id,
                       recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                FROM reservations
                WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", id);
            before = ReadReservations(read, projectRuntimeStatus: false).FirstOrDefault();
        }

        if (before is null)
        {
            tx.Rollback();
            return new(false, "not_found", id, null, null, expectedDataVersion, expectedDataVersion, null);
        }
        if (before.Status != ReservationStatus.Recording)
        {
            tx.Rollback();
            return new(false, "status_mismatch", id, before.Status, before.Status, expectedDataVersion, before.DataVersion, before);
        }
        if (before.DataVersion != expectedDataVersion)
        {
            tx.Rollback();
            return new(false, "data_version_mismatch", id, before.Status, before.Status, expectedDataVersion, before.DataVersion, before);
        }

        using (var update = con.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE reservations
                SET status = 'failed',
                    recording_finished_at = COALESCE(recording_finished_at, $finishedAt),
                    data_version = data_version + 1,
                    updated_at = $updatedAt
                WHERE id = $id
                  AND status = 'recording'
                  AND data_version = $expectedVersion;
                """;
            update.Parameters.AddWithValue("$finishedAt", finishedAt.ToString("O"));
            update.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$expectedVersion", expectedDataVersion);
            if (update.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                var latest = GetByIdPersisted(id);
                return new(false, "compare_and_set_failed", id, before.Status, latest?.Status, expectedDataVersion, latest?.DataVersion ?? expectedDataVersion, latest);
            }
        }

        tx.Commit();
        var after = GetByIdPersisted(id);
        mutationJournal.Record(
            ReservationMutationKind.RecordingFailed,
            before,
            after,
            string.IsNullOrWhiteSpace(reason) ? null : new Dictionary<string, string?> { ["reason"] = reason },
            nameof(Reservation.Status),
            nameof(Reservation.RecordingFinishedAt),
            nameof(Reservation.DataVersion));
        log.Add("RESERVATION_LIFECYCLE_CAS", $"R{id}",
            $"result=APPLIED from=Recording to=Failed dataVersion={expectedDataVersion}->{after?.DataVersion ?? expectedDataVersion + 1} finishedAt={finishedAt:O} source=worker_runtime_failure reason={TrimForAudit(reason)} rule=recording_lifecycle_cas_contract");
        return new(true, "applied", id, ReservationStatus.Recording, after?.Status ?? ReservationStatus.Failed, expectedDataVersion, after?.DataVersion ?? expectedDataVersion + 1, after);
    }

    /// <summary>
    /// 生存中の録画workerを正本として、誤ってFailedへ退化した予約だけをRecordingへ復旧する。
    /// 通常の録画ライフサイクルと同じくDataVersionを進め、Stopping以降には触れない。
    /// </summary>
    public ReservationLifecycleTransitionResult TryRestoreFailedRecordingRuntime(int id, long expectedDataVersion)
        => TryTransitionLifecycle(id, ReservationStatus.Failed, expectedDataVersion, ReservationStatus.Recording);

    // RESERVATION_STATUS_DIRECT_WRITE_REMOVED_INVARIANT:
    // 状態遷移は期待Status＋判定時DataVersionを持つ専用CAS入口だけが所有する。
    // UpdateStatus(force)のような無条件共通出口や、時刻窓で失敗を抑止する補正を再導入してはならない。

    private static bool HasOpenRecordingRuntime(Reservation? reservation)
    {
        if (reservation is null) return false;
        if (reservation.Source == ReservationSource.Epg) return false;
        if (!reservation.RecordingStartedAt.HasValue) return false;
        if (reservation.RecordingFinishedAt.HasValue) return false;
        if (reservation.Status is ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed) return false;

        // release_contract:
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



    /// <summary>
    /// 録画中断を検出した元予約を終端化する。
    /// Recording のまま予約一覧へ残さず、既存部分ファイルを保護したうえで失敗終端へ落とす。
    /// </summary>
    public bool FinalizeInterruptedRecording(Reservation snapshot, DateTime finishedAt, string reason, string? fileEvidence = null, string trigger = "InterruptedRecordingRecovery")
    {
        // INTERRUPTED_RECORDING_RECOVERY_FINALIZE_CAS_INVARIANT:
        // 判定時に読んだRecordingスナップショットだけを終端化する。
        // 判定後に再接続・状態遷移・予約編集が入った場合、古い中断判定でFailedへ上書きしてはならない。
        // status=Recording / DataVersion一致を同じUPDATEで検証し、CAS失敗時は復旧予約の追加へ進まない。
        var before = snapshot;
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET status = 'failed',
                is_conflicted = 0,
                recording_finished_at = CASE WHEN COALESCE(recording_finished_at, '') = '' THEN $finished ELSE recording_finished_at END,
                data_version = data_version + 1,
                updated_at = $now
            WHERE id = $id
              AND status = 'recording'
              AND data_version = $dataVersion;
            """;
        cmd.Parameters.AddWithValue("$id", snapshot.Id);
        cmd.Parameters.AddWithValue("$finished", finishedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$dataVersion", snapshot.DataVersion);
        var affected = cmd.ExecuteNonQuery();
        log.Add("RESERVATION_AUDIT", "INTERRUPTED_RECORDING_FINALIZE",
            $"service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} id=R{snapshot.Id} affected={affected} from={before.Status} to=Failed reason={reason} started={(before.RecordingStartedAt.HasValue ? before.RecordingStartedAt.Value.ToString("MM/dd HH:mm:ss") : "-")} finished={finishedAt:MM/dd HH:mm:ss} end={before.EndTime:MM/dd HH:mm:ss} tuner={SafeTuner(EffectiveTunerName(before))} expectedDataVersion={snapshot.DataVersion} rule=release_contract");
        if (affected != 1)
            return false;

        var after = GetById(snapshot.Id);
        mutationJournal.Record(
            ReservationMutationKind.RecordingFailed,
            before,
            after,
            new Dictionary<string, string?>
            {
                ["userEvent"] = "recording_interrupted",
                ["reason"] = reason,
                ["fileEvidence"] = fileEvidence,
                ["finishedAt"] = finishedAt.ToString("O"),
                ["trigger"] = trigger
            },
            nameof(Reservation.Status), nameof(Reservation.RecordingFinishedAt), nameof(Reservation.DataVersion));
        return true;
    }

    /// <summary>
    /// 中断録画の復旧用に、元予約と同じ番組を別予約として再投入する。
    /// 既存部分ファイルを上書きしないため、録画ファイル名は録画開始時の一意化で (1)/(2) 側へ逃がす。
    /// </summary>
    public int FinalizeAndAddInterruptedRecordingRecoveryReservation(Reservation original, DateTime now, string reason, string? fileEvidence = null, string trigger = "InterruptedRecordingRecovery")
    {
        var chainBefore = GetAll()
            .Where(r => r.IsUserChain && (r.Id == original.Id || r.UserChainPreviousId == original.Id || (original.UserChainRootId.HasValue && r.UserChainRootId == original.UserChainRootId)))
            .ToDictionary(r => r.Id);

        using var con = db.Open();
        using var tx = con.BeginTransaction();

        var recoveryChainId = string.IsNullOrWhiteSpace(original.RecordingRecoveryChainId)
            ? Guid.NewGuid().ToString("N")
            : original.RecordingRecoveryChainId.Trim();

        // INTERRUPTED_RECORDING_RECOVERY_ATOMIC_SOURCE_INVARIANT:
        // recoverableな録画中断では、元予約のFailed終端化・復旧予約追加・チェーン付替えを
        // 必ず同一SQLiteトランザクションで確定する。元予約だけを先にFailedへ落としてはならない。
        // 判定時のRecording/DataVersionをCAS条件にし、再接続・予約編集・別状態遷移が先行した場合は全体rollbackする。
        using (var finalizeOriginal = con.CreateCommand())
        {
            finalizeOriginal.Transaction = tx;
            finalizeOriginal.CommandText = """
                UPDATE reservations
                SET status = 'failed',
                    is_conflicted = 0,
                    recording_finished_at = CASE WHEN COALESCE(recording_finished_at, '') = '' THEN $finished ELSE recording_finished_at END,
                    recording_recovery_chain_id = CASE
                        WHEN COALESCE(recording_recovery_chain_id, '') = '' THEN $chainId
                        ELSE recording_recovery_chain_id
                    END,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'recording'
                  AND data_version = $dataVersion;
                """;
            finalizeOriginal.Parameters.AddWithValue("$finished", now.ToString("O"));
            finalizeOriginal.Parameters.AddWithValue("$chainId", recoveryChainId);
            finalizeOriginal.Parameters.AddWithValue("$now", now.ToString("O"));
            finalizeOriginal.Parameters.AddWithValue("$id", original.Id);
            finalizeOriginal.Parameters.AddWithValue("$dataVersion", original.DataVersion);
            if (finalizeOriginal.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Recovery source reservation changed before atomic recovery commit: R{original.Id}");
        }

        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO reservations
              (network_id, transport_stream_id, service_id, event_id,
               title, start_time, end_time, status, source, created_at, updated_at,
               channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
               recording_started_at, recording_finished_at, service_name,
               scheduled_start_time, source_rule_id, source_rule_name,
               is_user_chain, user_chain_previous_id, user_chain_root_id,
               recording_recovery_chain_id, recovery_parent_reservation_id,
               reservation_intent, created_through, created_by_plugin_id)
            VALUES
              ($nid, $tsid, $sid, $eid,
               $title, $start, $end, 'scheduled', $source, $now, $now,
               $charg, 0, 1, $tuner, '',
               '', '', $svcname,
               $scheduledStart, $sourceRuleId, $sourceRuleName,
               $isUserChain, $userChainPreviousId, $userChainRootId,
               $recoveryChainId, $recoveryParentReservationId,
               $reservationIntent, $createdThrough, $createdByPluginId);
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
        var originalParticipatesInChain = original.IsUserChain
            || chainBefore.Values.Any(x => x.UserChainPreviousId == original.Id || x.UserChainRootId == original.Id);
        var immutableRecoveryRootId = original.UserChainRootId
            ?? (originalParticipatesInChain ? original.Id : (int?)null);
        cmd.Parameters.AddWithValue("$isUserChain", original.IsUserChain ? 1 : 0);
        if (original.UserChainPreviousId.HasValue) cmd.Parameters.AddWithValue("$userChainPreviousId", original.UserChainPreviousId.Value); else cmd.Parameters.AddWithValue("$userChainPreviousId", DBNull.Value);
        if (immutableRecoveryRootId.HasValue) cmd.Parameters.AddWithValue("$userChainRootId", immutableRecoveryRootId.Value); else cmd.Parameters.AddWithValue("$userChainRootId", DBNull.Value);
        cmd.Parameters.AddWithValue("$recoveryChainId", recoveryChainId);
        cmd.Parameters.AddWithValue("$recoveryParentReservationId", original.Id);
        AddReservationProvenanceParameters(cmd, original);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        var newId = Convert.ToInt32(cmd.ExecuteScalar());

        // INTERRUPTED_RECORDING_RECOVERY_CHAIN_REPARENT_CAS_INVARIANT:
        // 復旧予約へ直接後続を付け替える際も、ChainRoot識別子は変更しない。
        // 変更できるのは直後リンクだけであり、user_chain_root_idの一括付替えは禁止する。
        foreach (var member in chainBefore.Values
                     .Where(x => x.Id != original.Id && x.UserChainPreviousId == original.Id)
                     .OrderBy(x => x.StartTime)
                     .ThenBy(x => x.Id))
        {
            using var updateMember = con.CreateCommand();
            updateMember.Transaction = tx;
            updateMember.CommandText = """
                UPDATE reservations
                SET user_chain_previous_id = $newPredecessor,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'scheduled'
                  AND is_enabled = 1
                  AND is_user_chain = 1
                  AND data_version = $dataVersion
                  AND user_chain_previous_id = $oldPredecessor
                  AND (($storedRoot IS NULL AND user_chain_root_id IS NULL) OR user_chain_root_id = $storedRoot);
                """;
            updateMember.Parameters.AddWithValue("$newPredecessor", newId);
            updateMember.Parameters.AddWithValue("$oldPredecessor", original.Id);
            updateMember.Parameters.AddWithValue("$storedRoot", member.UserChainRootId.HasValue ? member.UserChainRootId.Value : DBNull.Value);
            updateMember.Parameters.AddWithValue("$now", now.ToString("O"));
            updateMember.Parameters.AddWithValue("$id", member.Id);
            updateMember.Parameters.AddWithValue("$dataVersion", member.DataVersion);
            if (updateMember.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Recovery chain member changed before reparent commit: R{member.Id}");
        }

        tx.Commit();

        var sourceAfter = GetById(original.Id);
        log.Add("RESERVATION_AUDIT", "INTERRUPTED_RECORDING_RECOVERY_ADD",
            $"source=R{original.Id} recovery=R{newId} recoveryChainId={recoveryChainId} service={TrimForAudit(original.ServiceName)} title={TrimTitleForAudit(original.Title)} rawTitleBlank={RawTitleBlankForAudit(original.Title)} start={original.StartTime:MM/dd HH:mm:ss} end={original.EndTime:MM/dd HH:mm:ss} tuner={SafeTuner(!string.IsNullOrWhiteSpace(original.ActualTunerName) ? original.ActualTunerName : original.TunerName)} userChainCopied={original.IsUserChain} chainPrev={(original.UserChainPreviousId.HasValue ? $"R{original.UserChainPreviousId.Value}" : "-")} chainRoot={(original.UserChainRootId.HasValue ? $"R{original.UserChainRootId.Value}" : "-")} reason=interrupted_recording_recovery rule=release_contract");
        if (sourceAfter is not null)
        {
            mutationJournal.Record(
                ReservationMutationKind.RecordingFailed,
                original,
                sourceAfter,
                new Dictionary<string, string?>
                {
                    ["userEvent"] = "recording_interrupted",
                    ["reason"] = reason,
                    ["fileEvidence"] = fileEvidence,
                    ["finishedAt"] = now.ToString("O"),
                    ["trigger"] = trigger,
                    ["recoveryReservationId"] = newId.ToString()
                },
                nameof(Reservation.Status), nameof(Reservation.RecordingFinishedAt),
                nameof(Reservation.RecordingRecoveryChainId), nameof(Reservation.DataVersion));
        }
        var recovery = GetById(newId);
        if (recovery is not null) mutationJournal.Record(ReservationMutationKind.Added, null, recovery);
        foreach (var before in chainBefore.Values.Where(x => x.Id != original.Id))
        {
            var after = GetById(before.Id);
            if (after is null) continue;
            if (before.UserChainPreviousId == after.UserChainPreviousId && before.UserChainRootId == after.UserChainRootId && before.DataVersion == after.DataVersion)
                continue;
            mutationJournal.Record(ReservationMutationKind.Updated, before, after,
                nameof(Reservation.UserChainPreviousId), nameof(Reservation.UserChainRootId), nameof(Reservation.DataVersion));
        }
        return newId;
    }

    /// <summary>
    /// 録画中予約の時間追従時刻を、判定に使用したDataVersionでCompare-and-Set更新する。
    /// scheduled_start_timeは元の予約時刻として変更しない。
    /// RECORDING_FOLLOW_TIME_CAS_INVARIANT:
    /// 時間追従判定後に予約編集・無効化・状態遷移が入った場合、古い時刻で上書きしてはならない。
    /// status=Recording / enabled / DataVersion一致を同じUPDATEで検証し、実変更と同時にDataVersionを進める。
    /// 固定waitや即時再試行で補正せず、CAS失敗時は次の通常監視巡回で最新スナップショットを再読込する。
    /// </summary>
    public bool TryUpdateRecordingFollowTimeCas(
        int id,
        long expectedDataVersion,
        DateTime startTime,
        DateTime endTime)
    {
        var before = GetById(id);
        if (before is null || before.DataVersion != expectedDataVersion)
            return false;

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET start_time = $start,
                end_time = $end,
                data_version = data_version + 1,
                updated_at = $now
            WHERE id = $id
              AND status = 'recording'
              AND is_enabled = 1
              AND data_version = $dataVersion;
            """;
        cmd.Parameters.AddWithValue("$start", startTime.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endTime.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$dataVersion", expectedDataVersion);
        if (cmd.ExecuteNonQuery() != 1)
            return false;

        mutationJournal.Record(ReservationMutationKind.Updated, before, GetById(id),
            nameof(Reservation.StartTime), nameof(Reservation.EndTime), nameof(Reservation.DataVersion));
        return true;
    }

    /// <summary>
    /// 録画中前段の時間追従境界を、前段時刻と明示チェーン直接後続の開始時刻へ同一トランザクションで反映する。
    /// CHAIN_FOLLOW_ATOMIC_BOUNDARY_INVARIANT:
    /// 前段EndTimeだけ、または後続StartTimeだけが保存された中間状態を公開してはならない。
    /// 30秒前停止・10秒緊急投入・Completed handoff pin・FinalConflictPlanは同じ境界を参照する。
    /// CHAIN_FOLLOW_ATOMIC_CAS_INVARIANT:
    /// commit時点でも前段が録画中、後続が有効な明示チェーン直接後続かつ同一サービスであることをDB上で再検証する。
    /// CHAIN_FOLLOW_ATOMIC_VERSION_CAS_INVARIANT:
    /// Schedulerが時間追従判定に使用した前段・後続スナップショットのDataVersionを、そのままCAS条件として受け取る。
    /// Store内でDataVersionを再読込して期待値を作り直してはならない。それでは判定後からStore呼出しまでに入った予約編集を検出できない。
    /// 前段・後続とも呼出元スナップショットのDataVersionをCAS条件に含め、実変更と同時に世代を進める。
    /// 後続EndTimeは時間追従対象ではないため書き直さず、実際に変更したStartTimeとDataVersionだけをmutationへ記録する。
    /// </summary>
    public bool UpdateRecordingFollowChainBoundaryAtomic(
        int predecessorId,
        long expectedPredecessorDataVersion,
        DateTime predecessorStart,
        DateTime predecessorEnd,
        int successorId,
        long expectedSuccessorDataVersion,
        DateTime successorStart)
    {
        var beforePredecessor = GetById(predecessorId);
        var beforeSuccessor = GetById(successorId);
        if (beforePredecessor is null || beforeSuccessor is null)
            return false;

        using var con = db.Open();
        using var tx = con.BeginTransaction();
        var now = DateTime.Now.ToString("O");

        using (var predecessor = con.CreateCommand())
        {
            predecessor.Transaction = tx;
            predecessor.CommandText = """
                UPDATE reservations
                SET start_time = $start,
                    end_time = $end,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'recording'
                  AND is_enabled = 1
                  AND data_version = $dataVersion;
                """;
            predecessor.Parameters.AddWithValue("$start", predecessorStart.ToString("O"));
            predecessor.Parameters.AddWithValue("$end", predecessorEnd.ToString("O"));
            predecessor.Parameters.AddWithValue("$now", now);
            predecessor.Parameters.AddWithValue("$id", predecessorId);
            predecessor.Parameters.AddWithValue("$dataVersion", expectedPredecessorDataVersion);
            if (predecessor.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                return false;
            }
        }

        using (var successor = con.CreateCommand())
        {
            successor.Transaction = tx;
            successor.CommandText = """
                UPDATE reservations
                SET start_time = $start,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'scheduled'
                  AND is_enabled = 1
                  AND is_user_chain = 1
                  AND user_chain_previous_id = $predecessorId
                  AND network_id = $networkId
                  AND transport_stream_id = $transportStreamId
                  AND service_id = $serviceId
                  AND data_version = $dataVersion;
                """;
            successor.Parameters.AddWithValue("$start", successorStart.ToString("O"));
            successor.Parameters.AddWithValue("$now", now);
            successor.Parameters.AddWithValue("$id", successorId);
            successor.Parameters.AddWithValue("$predecessorId", predecessorId);
            successor.Parameters.AddWithValue("$networkId", beforePredecessor.NetworkId);
            successor.Parameters.AddWithValue("$transportStreamId", beforePredecessor.TransportStreamId);
            successor.Parameters.AddWithValue("$serviceId", beforePredecessor.ServiceId);
            successor.Parameters.AddWithValue("$dataVersion", expectedSuccessorDataVersion);
            if (successor.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                return false;
            }
        }

        tx.Commit();
        var afterPredecessor = GetById(predecessorId);
        var afterSuccessor = GetById(successorId);
        mutationJournal.Record(ReservationMutationKind.Updated, beforePredecessor, afterPredecessor,
            nameof(Reservation.StartTime), nameof(Reservation.EndTime), nameof(Reservation.DataVersion));
        mutationJournal.Record(ReservationMutationKind.Updated, beforeSuccessor, afterSuccessor,
            nameof(Reservation.StartTime), nameof(Reservation.DataVersion));
        return true;
    }

    /// <summary>
    /// Scheduled予約のEPG時間追従を、判定に使用したDataVersionでCompare-and-Set更新する。
    /// SCHEDULED_FOLLOW_VERSION_CAS_INVARIANT:
    /// EPG照合後から保存までに予約編集・無効化・状態遷移・チェーン化が入った場合、古い照合結果で上書きしてはならない。
    /// status=Scheduled / enabled / 非チェーン / DataVersion一致を同じUPDATEで検証し、実変更と同時にDataVersionを進める。
    /// CAS失敗時は固定waitや即時再試行を追加せず、次回の通常EPG追従巡回で最新スナップショットを再読込する。
    /// </summary>
    public bool TryUpdateScheduledTitleStartEndTimeCas(
        int id,
        long expectedDataVersion,
        string title,
        string serviceName,
        DateTime startTime,
        DateTime endTime)
    {
        var before = GetById(id);
        if (before is null || before.DataVersion != expectedDataVersion)
            return false;

        var safeTitle = NormalizeReservationTitleForStorage(title, serviceName, id);
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET title = $title,
                start_time = $start,
                end_time = $end,
                data_version = data_version + 1,
                updated_at = $now
            WHERE id = $id
              AND status = 'scheduled'
              AND is_enabled = 1
              AND is_user_chain = 0
              AND data_version = $dataVersion;
            """;
        cmd.Parameters.AddWithValue("$title", safeTitle);
        cmd.Parameters.AddWithValue("$start", startTime.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endTime.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$dataVersion", expectedDataVersion);
        if (cmd.ExecuteNonQuery() != 1)
            return false;

        mutationJournal.Record(ReservationMutationKind.Updated, before, GetById(id),
            nameof(Reservation.Title), nameof(Reservation.StartTime), nameof(Reservation.EndTime), nameof(Reservation.DataVersion));
        return true;
    }

    public bool UpdateTitleIfBlank(int id, string title, string serviceName)
    {
        var before = GetById(id);
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
        var affected = cmd.ExecuteNonQuery();
        if (affected > 0 && before is not null)
            mutationJournal.Record(ReservationMutationKind.Updated, before, GetById(id), nameof(Reservation.Title));
        return affected > 0;
    }


    // PROJECTION_RESERVATION_VERSION_CAS_INVARIANT:
    // 投影元の再結合とDBイベント昇格は、予約identityと実行時刻を同時に書き換える。
    // 判定に使用したDataVersionをそのままCAS条件にし、同じUPDATEで世代を進めること。
    // UI／プラグイン／EPGの並行編集を古い投影結果で上書きする無条件UPDATEへ戻してはならない。
    public bool RebindProjectionToExternalEvent(int id, ExternalEpgEvent externalEvent)
    {
        var before = GetById(id);
        if (before is null || before.Status != ReservationStatus.Scheduled) return false;

        var safeTitle = NormalizeReservationTitleForStorage(externalEvent.Title, externalEvent.ServiceName, id);
        var serviceName = externalEvent.ServiceName ?? string.Empty;
        var changed = before.NetworkId != externalEvent.NetworkId
            || before.TransportStreamId != externalEvent.TransportStreamId
            || before.ServiceId != externalEvent.ServiceId
            || before.EventId != externalEvent.EventId
            || before.StartTime != externalEvent.Start
            || before.EndTime != externalEvent.End
            || (!string.IsNullOrWhiteSpace(safeTitle) && !string.Equals(before.Title, safeTitle, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(serviceName) && !string.Equals(before.ServiceName, serviceName, StringComparison.Ordinal));
        if (!changed) return false;

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET network_id = $networkId,
                transport_stream_id = $transportStreamId,
                service_id = $serviceId,
                event_id = $eventId,
                title = CASE WHEN $title <> '' THEN $title ELSE title END,
                service_name = CASE WHEN $serviceName <> '' THEN $serviceName ELSE service_name END,
                start_time = $start,
                end_time = $end,
                data_version = data_version + 1,
                updated_at = $now
            WHERE id = $id
              AND status = 'scheduled'
              AND data_version = $dataVersion;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$networkId", externalEvent.NetworkId);
        cmd.Parameters.AddWithValue("$transportStreamId", externalEvent.TransportStreamId);
        cmd.Parameters.AddWithValue("$serviceId", externalEvent.ServiceId);
        cmd.Parameters.AddWithValue("$eventId", externalEvent.EventId);
        cmd.Parameters.AddWithValue("$title", safeTitle);
        cmd.Parameters.AddWithValue("$serviceName", serviceName);
        cmd.Parameters.AddWithValue("$start", externalEvent.Start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", externalEvent.End.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$dataVersion", before.DataVersion);
        var affected = cmd.ExecuteNonQuery();
        if (affected > 0)
        {
            log.Add("RESERVATION_PROJECTION_REBIND", $"R{id}",
                $"result=UPDATED reservation=R{id} sourcePluginId={externalEvent.SourcePluginId} nid={externalEvent.NetworkId} tsid={externalEvent.TransportStreamId} sid={externalEvent.ServiceId} eventId={externalEvent.EventId} title={TrimTitleForAudit(safeTitle)} start={externalEvent.Start:MM/dd HH:mm:ss} end={externalEvent.End:MM/dd HH:mm:ss} scheduledStartPreserved=True rule=release_contract");
            mutationJournal.Record(ReservationMutationKind.Updated, before, GetById(id), nameof(Reservation.Title), nameof(Reservation.ServiceName), nameof(Reservation.StartTime), nameof(Reservation.EndTime), nameof(Reservation.NetworkId), nameof(Reservation.TransportStreamId), nameof(Reservation.ServiceId), nameof(Reservation.EventId), nameof(Reservation.DataVersion));
        }
        return affected > 0;
    }

    public bool PromoteProjectionToDbEvent(int id, ProjectedProgramEvent dbEvent)
    {
        var before = GetById(id);
        if (before is null || before.Status != ReservationStatus.Scheduled) return false;

        var safeTitle = NormalizeReservationTitleForStorage(dbEvent.Title, dbEvent.ServiceName, id);
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservations
            SET network_id = $networkId,
                transport_stream_id = $transportStreamId,
                service_id = $serviceId,
                event_id = $eventId,
                title = CASE WHEN $title <> '' THEN $title ELSE title END,
                service_name = CASE WHEN $serviceName <> '' THEN $serviceName ELSE service_name END,
                start_time = $start,
                end_time = $end,
                data_version = data_version + 1,
                updated_at = $now
            WHERE id = $id
              AND status = 'scheduled'
              AND data_version = $dataVersion;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$networkId", dbEvent.NetworkId);
        cmd.Parameters.AddWithValue("$transportStreamId", dbEvent.TransportStreamId);
        cmd.Parameters.AddWithValue("$serviceId", dbEvent.ServiceId);
        cmd.Parameters.AddWithValue("$eventId", dbEvent.EventId);
        cmd.Parameters.AddWithValue("$title", safeTitle);
        cmd.Parameters.AddWithValue("$serviceName", dbEvent.ServiceName ?? string.Empty);
        cmd.Parameters.AddWithValue("$start", dbEvent.Start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", dbEvent.End.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$dataVersion", before.DataVersion);
        var affected = cmd.ExecuteNonQuery();
        if (affected > 0)
        {
            log.Add("RESERVATION_PROJECTION_PROMOTE", $"R{id}",
                $"result=UPDATED reservation=R{id} nid={dbEvent.NetworkId} tsid={dbEvent.TransportStreamId} sid={dbEvent.ServiceId} eventId={dbEvent.EventId} title={TrimTitleForAudit(safeTitle)} start={dbEvent.Start:MM/dd HH:mm:ss} end={dbEvent.End:MM/dd HH:mm:ss} source=db_event_detected rule=release_contract");
            mutationJournal.Record(ReservationMutationKind.Updated, before, GetById(id), nameof(Reservation.Title), nameof(Reservation.ServiceName), nameof(Reservation.StartTime), nameof(Reservation.EndTime), nameof(Reservation.NetworkId), nameof(Reservation.TransportStreamId), nameof(Reservation.ServiceId), nameof(Reservation.EventId), nameof(Reservation.DataVersion));
        }
        return affected > 0;
    }

    /// <summary>
    /// 終端予約を物理削除し、必要なチェーントポロジー修復を同一トランザクションで行う。
    /// PHYSICAL_DELETE_CHAIN_TOPOLOGY_ATOMIC_CAS_INVARIANT:
    /// - 物理削除対象は Completed / Failed / Cancelled の終端予約だけとする。
    /// - 削除対象と修復対象の Status / DataVersion / 旧 previous / 旧 root を全件CASし、1件でも変化していれば全体をrollbackする。
    /// - チェーントポロジー修復は TunerName / ActualTunerName を所有しない。特に ActualTunerName は録画実績証拠なので消去しない。
    /// - 部分的な再接続・root再構成・物理削除を公開しない。
    /// </summary>
    public bool TryDeleteTerminalReservationAtomicCas(Reservation expected, out IReadOnlyList<Reservation> repairedReservations)
    {
        repairedReservations = Array.Empty<Reservation>();
        if (expected is null) return false;
        if (expected.Status is not (ReservationStatus.Completed or ReservationStatus.Failed or ReservationStatus.Cancelled))
            return false;

        var all = GetAll();
        var before = all.FirstOrDefault(r => r.Id == expected.Id);
        if (before is null
            || before.Status != expected.Status
            || before.DataVersion != expected.DataVersion)
            return false;

        var chainBefore = all
            .Where(r => r.Id == expected.Id
                || r.UserChainPreviousId == expected.Id
                || (before.UserChainRootId.HasValue && r.UserChainRootId == before.UserChainRootId))
            .ToDictionary(r => r.Id);

        var directChildren = all
            .Where(r => r.UserChainPreviousId == expected.Id)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();
        var byPrevious = all
            .Where(r => r.UserChainPreviousId.HasValue)
            .GroupBy(r => r.UserChainPreviousId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.StartTime).ThenBy(x => x.Id).ToList());

        var previousId = before.UserChainPreviousId;
        var previousExists = previousId.HasValue && all.Any(r => r.Id == previousId.Value && r.Id != expected.Id);
        var nowText = DateTime.Now.ToString("O");

        using var con = db.Open();
        using var tx = con.BeginTransaction();

        bool ExecuteTopologyCas(
            Reservation snapshot,
            bool isUserChain,
            int? newPreviousId,
            int? newRootId)
        {
            using var update = con.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE reservations
                SET is_user_chain = $isUserChain,
                    user_chain_previous_id = $newPreviousId,
                    user_chain_root_id = $newRootId,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = $status
                  AND data_version = $dataVersion
                  AND is_user_chain = $oldIsUserChain
                  AND (($oldPreviousId IS NULL AND user_chain_previous_id IS NULL) OR user_chain_previous_id = $oldPreviousId)
                  AND (($oldRootId IS NULL AND user_chain_root_id IS NULL) OR user_chain_root_id = $oldRootId);
                """;
            update.Parameters.AddWithValue("$isUserChain", isUserChain ? 1 : 0);
            update.Parameters.AddWithValue("$newPreviousId", (object?)newPreviousId ?? DBNull.Value);
            update.Parameters.AddWithValue("$newRootId", (object?)newRootId ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", nowText);
            update.Parameters.AddWithValue("$id", snapshot.Id);
            update.Parameters.AddWithValue("$status", snapshot.Status.ToString().ToLowerInvariant());
            update.Parameters.AddWithValue("$dataVersion", snapshot.DataVersion);
            update.Parameters.AddWithValue("$oldIsUserChain", snapshot.IsUserChain ? 1 : 0);
            update.Parameters.AddWithValue("$oldPreviousId", (object?)snapshot.UserChainPreviousId ?? DBNull.Value);
            update.Parameters.AddWithValue("$oldRootId", (object?)snapshot.UserChainRootId ?? DBNull.Value);
            return update.ExecuteNonQuery() == 1;
        }

        foreach (var directChild in directChildren)
        {
            var subtree = new List<Reservation>();
            var visited = new HashSet<int>();
            var queue = new Queue<Reservation>();
            queue.Enqueue(directChild);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current.Id)) continue;
                subtree.Add(current);
                if (byPrevious.TryGetValue(current.Id, out var nextChildren))
                {
                    foreach (var child in nextChildren)
                        queue.Enqueue(child);
                }
            }

            var hasDownstream = subtree.Any(x => x.UserChainPreviousId == directChild.Id);
            if (directChildren.Count == 1 && previousExists)
            {
                if (!ExecuteTopologyCas(directChild, directChild.IsUserChain, previousId, directChild.UserChainRootId))
                {
                    tx.Rollback();
                    return false;
                }
                continue;
            }

            if (!hasDownstream)
            {
                if (!ExecuteTopologyCas(directChild, false, null, null))
                {
                    tx.Rollback();
                    return false;
                }
                continue;
            }

            if (!ExecuteTopologyCas(directChild, true, null, directChild.Id))
            {
                tx.Rollback();
                return false;
            }

            foreach (var descendant in subtree.Where(x => x.Id != directChild.Id))
            {
                if (!ExecuteTopologyCas(descendant, descendant.IsUserChain, descendant.UserChainPreviousId, directChild.Id))
                {
                    tx.Rollback();
                    return false;
                }
            }
        }

        using (var delete = con.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = """
                DELETE FROM reservations
                WHERE id = $id
                  AND status = $status
                  AND data_version = $dataVersion;
                """;
            delete.Parameters.AddWithValue("$id", expected.Id);
            delete.Parameters.AddWithValue("$status", expected.Status.ToString().ToLowerInvariant());
            delete.Parameters.AddWithValue("$dataVersion", expected.DataVersion);
            if (delete.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                return false;
            }
        }

        tx.Commit();

        var repaired = new List<Reservation>();
        foreach (var oldState in chainBefore.Values.Where(x => x.Id != expected.Id))
        {
            var after = GetById(oldState.Id);
            if (after is null) continue;
            if (oldState.IsUserChain == after.IsUserChain
                && oldState.UserChainPreviousId == after.UserChainPreviousId
                && oldState.UserChainRootId == after.UserChainRootId
                && oldState.DataVersion == after.DataVersion)
                continue;

            repaired.Add(after);
            mutationJournal.Record(ReservationMutationKind.Updated, oldState, after,
                nameof(Reservation.IsUserChain), nameof(Reservation.UserChainPreviousId),
                nameof(Reservation.UserChainRootId), nameof(Reservation.DataVersion));
        }

        repairedReservations = repaired;
        mutationJournal.Record(ReservationMutationKind.Removed, before, null);
        log.Add("RESERVATION_AUDIT", "DELETE",
            $"service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} id=R{expected.Id} affected=1 status={before.Status} source={before.Source} enabled={before.IsEnabled} start={before.StartTime:MM/dd HH:mm:ss} end={before.EndTime:MM/dd HH:mm:ss} userChain={before.IsUserChain} chainPrev={(before.UserChainPreviousId.HasValue ? $"R{before.UserChainPreviousId.Value}" : "-")} repairedChildren={directChildren.Count} topologyCas=True tunerEvidencePreserved=True rule=release_contract");
        return true;
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
    /// Scheduled / Starting / Recording を同じチェーントポロジーとして読み、開始境界で後続探索を切らない。
    /// Recording を含む範囲の拒否判断はAPI入口が所有し、探索側で黙って範囲を縮めない。
    /// </summary>
    public IReadOnlyList<Reservation> GetUserChainCancelTargets(int id)
    {
        // CHAIN_CANCEL_TOPOLOGY_SINGLE_SOURCE_INVARIANT:
        // 取消対象は表示用Runtime投影や競合候補集合から組み立てない。
        // SQLiteに保存された user_chain_previous_id / user_chain_root_id を唯一の正本として読み、
        // 末尾=単体、途中=その地点以降、先頭=全体を決定する。
        // 競合中・Starting中でもトポロジー探索を縮めない。Recordingを含む場合の拒否はAPI入口が所有する。
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                   recording_started_at, recording_finished_at, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id,
                   recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                   reservation_intent, created_through, created_by_plugin_id
            FROM reservations
            WHERE status IN ('scheduled', 'starting', 'recording')
            ORDER BY start_time, id;
            """;
        var active = ReadReservations(cmd, projectRuntimeStatus: false).ToList();

        var start = active.FirstOrDefault(r => r.Id == id);
        if (start is null) return Array.Empty<Reservation>();

        // 先頭予約は通常の予約入口で作成されるため IsUserChain=false / root=null のままになり得る。
        // 自身の属性だけで単体予約と判定せず、SQLite正本上で後続から previous/root 参照されていれば
        // 明示チェーンのRootとして扱う。これにより先頭取消はチェーン全体へ展開される。
        var referencedAsRoot = active.Any(r => r.UserChainPreviousId == start.Id || r.UserChainRootId == start.Id);
        var rootId = start.UserChainRootId
            ?? (start.UserChainPreviousId.HasValue || start.IsUserChain || referencedAsRoot ? start.Id : (int?)null);
        if (!rootId.HasValue)
            return new[] { start };

        var sameRoot = active
            .Where(r => r.Id == rootId.Value || r.UserChainRootId == rootId.Value)
            .ToList();

        var byPrevious = sameRoot
            .Where(r => r.UserChainPreviousId.HasValue)
            .GroupBy(r => r.UserChainPreviousId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.StartTime).ThenBy(x => x.Id).ToList());

        var result = new List<Reservation>();
        var visited = new HashSet<int>();
        var queue = new Queue<Reservation>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current.Id)) continue;
            result.Add(current);

            if (!byPrevious.TryGetValue(current.Id, out var children)) continue;
            foreach (var child in children)
                queue.Enqueue(child);
        }

        var ordered = result.OrderBy(r => r.StartTime).ThenBy(r => r.Id).ToList();
        log.Add("CHAIN_CANCEL_TARGET_RESOLUTION", $"R{id}",
            $"result=RESOLVED root=R{rootId.Value} start=R{id} targetCount={ordered.Count} targets=[{string.Join(',', ordered.Select(x => $"R{x.Id}"))}] activeRootMembers=[{string.Join(',', sameRoot.OrderBy(x => x.StartTime).ThenBy(x => x.Id).Select(x => $"R{x.Id}:prev={(x.UserChainPreviousId.HasValue ? $"R{x.UserChainPreviousId.Value}" : "-")}:status={x.Status}:conflicted={x.IsConflicted}"))}] topologySource=sqlite_previous_root runtimeProjection=disabled rule=release_contract");
        return ordered;
    }

    /// <summary>
    /// MANUAL_STOP_CHAIN_DETACH_ATOMIC_CAS_INVARIANT:
    /// 録画中の手動停止では、後続チェーンをキャンセルせず通常予約として残す。
    /// 停止対象の直後以降だけをチェーンから切り離し、対象全件の判定時Status・DataVersion・
    /// previous/root関係を同一Transactionで再確認する。1件でも変化していれば全体rollbackし、
    /// 部分的なチェーン切離しを公開しない。
    ///
    /// TunerNameは未開始予約の再割当計画なので切離し時に空へ戻してよいが、ActualTunerNameは
    /// 録画実績証拠であり、このトポロジー変更処理は所有しない。ActualTunerNameを消去する処理を
    /// ここへ再導入しない。
    /// </summary>
    public bool TryDetachUserChainSuccessorsForManualStopAtomicCas(
        int predecessorId,
        out IReadOnlyList<Reservation> detachedSuccessors)
    {
        detachedSuccessors = Array.Empty<Reservation>();

        var all = GetAll()
            .Where(r => r.Status == ReservationStatus.Scheduled || r.Status == ReservationStatus.Recording)
            .ToList();

        var directChildren = all
            .Where(r => r.Status == ReservationStatus.Scheduled
                && r.IsUserChain
                && r.UserChainPreviousId == predecessorId)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        var predecessor = all.FirstOrDefault(r => r.Id == predecessorId) ?? GetById(predecessorId);
        var predecessorService = TrimForAudit(predecessor?.ServiceName);
        var predecessorTitle = TrimTitleForAudit(predecessor?.Title);
        var predecessorRawTitleBlank = RawTitleBlankForAudit(predecessor?.Title);

        if (directChildren.Count == 0)
        {
            log.Add("CHAIN_STOP_POLICY", $"R{predecessorId}",
                $"operation=ManualStop service={predecessorService} title={predecessorTitle} rawTitleBlank={predecessorRawTitleBlank} result=NO_SUCCESSOR_TO_DETACH cancelledSuccessors=0 detachedSuccessors=0 rule=release_contract");
            return true;
        }

        var byPrevious = all
            .Where(r => r.Status == ReservationStatus.Scheduled
                && r.IsUserChain
                && r.UserChainPreviousId.HasValue)
            .GroupBy(r => r.UserChainPreviousId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.StartTime).ThenBy(x => x.Id).ToList());

        var targets = new Dictionary<int, Reservation>();
        var directRootByReservationId = new Dictionary<int, int>();
        var directRootIds = new HashSet<int>(directChildren.Select(x => x.Id));
        foreach (var directChild in directChildren)
        {
            var visited = new HashSet<int>();
            var queue = new Queue<Reservation>();
            queue.Enqueue(directChild);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current.Id)) continue;
                targets[current.Id] = current;
                directRootByReservationId[current.Id] = directChild.Id;
                if (byPrevious.TryGetValue(current.Id, out var nextChildren))
                {
                    foreach (var child in nextChildren)
                        queue.Enqueue(child);
                }
            }
        }

        using var con = db.Open();
        using var tx = con.BeginTransaction();
        var nowText = DateTime.Now.ToString("O");

        foreach (var target in targets.Values.OrderBy(x => x.Id))
        {
            var directRootId = directRootByReservationId[target.Id];
            var hasDownstream = targets.Values.Any(x => x.UserChainPreviousId == directRootId);
            var isDirectRoot = directRootIds.Contains(target.Id);

            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            if (isDirectRoot && !hasDownstream)
            {
                cmd.CommandText = """
                    UPDATE reservations
                    SET is_user_chain = 0,
                        user_chain_previous_id = NULL,
                        user_chain_root_id = NULL,
                        tuner_name = '',
                        data_version = data_version + 1,
                        updated_at = $now
                    WHERE id = $id
                      AND status = 'scheduled'
                      AND data_version = $dataVersion
                      AND is_user_chain = 1
                      AND user_chain_previous_id = $previousId
                      AND (($rootId IS NULL AND user_chain_root_id IS NULL) OR user_chain_root_id = $rootId);
                    """;
            }
            else if (isDirectRoot)
            {
                cmd.CommandText = """
                    UPDATE reservations
                    SET is_user_chain = 1,
                        user_chain_previous_id = NULL,
                        user_chain_root_id = $newRoot,
                        tuner_name = '',
                        data_version = data_version + 1,
                        updated_at = $now
                    WHERE id = $id
                      AND status = 'scheduled'
                      AND data_version = $dataVersion
                      AND is_user_chain = 1
                      AND user_chain_previous_id = $previousId
                      AND (($rootId IS NULL AND user_chain_root_id IS NULL) OR user_chain_root_id = $rootId);
                    """;
                cmd.Parameters.AddWithValue("$newRoot", directRootId);
            }
            else
            {
                cmd.CommandText = """
                    UPDATE reservations
                    SET is_user_chain = 1,
                        user_chain_root_id = $newRoot,
                        tuner_name = '',
                        data_version = data_version + 1,
                        updated_at = $now
                    WHERE id = $id
                      AND status = 'scheduled'
                      AND data_version = $dataVersion
                      AND is_user_chain = 1
                      AND user_chain_previous_id = $previousId
                      AND (($rootId IS NULL AND user_chain_root_id IS NULL) OR user_chain_root_id = $rootId);
                    """;
                cmd.Parameters.AddWithValue("$newRoot", directRootId);
            }

            cmd.Parameters.AddWithValue("$now", nowText);
            cmd.Parameters.AddWithValue("$id", target.Id);
            cmd.Parameters.AddWithValue("$dataVersion", target.DataVersion);
            cmd.Parameters.AddWithValue("$previousId", target.UserChainPreviousId!.Value);
            cmd.Parameters.AddWithValue("$rootId", (object?)target.UserChainRootId ?? DBNull.Value);

            if (cmd.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                log.Add("CHAIN_STOP_POLICY", $"R{predecessorId}",
                    $"operation=ManualStop result=DETACH_CAS_REJECTED target=R{target.Id} expectedStatus={target.Status} expectedDataVersion={target.DataVersion} expectedPrevious={target.UserChainPreviousId} expectedRoot={target.UserChainRootId} action=keep_latest_chain_without_partial_detach rule=recording_lifecycle_cas_contract");
                return false;
            }
        }

        tx.Commit();

        var changedAfter = targets.Keys
            .Select(GetById)
            .Where(x => x is not null)
            .Cast<Reservation>()
            .OrderBy(x => x.StartTime)
            .ThenBy(x => x.Id)
            .ToList();

        foreach (var before in targets.Values.OrderBy(x => x.StartTime).ThenBy(x => x.Id))
        {
            var after = changedAfter.FirstOrDefault(x => x.Id == before.Id);
            if (after is null) continue;
            mutationJournal.Record(ReservationMutationKind.Updated, before, after,
                nameof(Reservation.IsUserChain), nameof(Reservation.UserChainPreviousId), nameof(Reservation.UserChainRootId),
                nameof(Reservation.TunerName), nameof(Reservation.DataVersion));
        }

        detachedSuccessors = changedAfter;
        var targetText = string.Join(",", changedAfter.Select(x => $"R{x.Id}:{TrimForAudit(x.ServiceName)}:{TrimTitleForAudit(x.Title)}:rawTitleBlank={RawTitleBlankForAudit(x.Title)}"));
        log.Add("CHAIN_STOP_POLICY", $"R{predecessorId}",
            $"operation=ManualStop service={predecessorService} title={predecessorTitle} rawTitleBlank={predecessorRawTitleBlank} result=REBASE_SUCCESSOR_CHAIN cancelledSuccessors=0 changedReservations={changedAfter.Count} action=detach_only_predecessor_edge_and_preserve_downstream_chain targets=[{targetText}] actualTunerEvidencePreserved=True rule=recording_lifecycle_cas_contract");
        return true;
    }

    /// <summary>
    /// RESERVATION_CANCEL_BATCH_ATOMIC_CAS_INVARIANT:
    /// ユーザー操作による単体／チェーン範囲取消は、判定時に読んだ各予約のStatusとDataVersionを
    /// 同一Transactionで再確認して一括確定する。1件でも変化していれば全体rollbackし、
    /// 古いチェーン範囲で一部だけCancelledへ進めない。無条件の一括状態更新入口を再導入しない。
    /// </summary>
    public bool TryCancelReservationsAtomicCas(
        IReadOnlyCollection<Reservation> snapshots,
        IReadOnlyDictionary<string, string?>? mutationMetadata,
        out IReadOnlyList<Reservation> cancelled)
    {
        cancelled = Array.Empty<Reservation>();
        var targets = snapshots
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderBy(x => x.Id)
            .ToList();
        if (targets.Count == 0) return true;

        if (targets.Any(x => x.Status is not (ReservationStatus.Scheduled or ReservationStatus.Starting)))
        {
            log.Add("RESERVATION_CANCEL_BATCH_CAS", "Batch",
                $"result=REJECTED reason=invalid_snapshot_status targets=[{string.Join(",", targets.Select(x => $"R{x.Id}:{x.Status}:v{x.DataVersion}"))}] rule=release_contract");
            return false;
        }

        using var con = db.Open();
        using var tx = con.BeginTransaction();
        var nowText = DateTime.Now.ToString("O");

        foreach (var target in targets)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE reservations
                SET status = 'cancelled',
                    is_conflicted = 0,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = $status
                  AND data_version = $dataVersion;
                """;
            cmd.Parameters.AddWithValue("$now", nowText);
            cmd.Parameters.AddWithValue("$id", target.Id);
            cmd.Parameters.AddWithValue("$status", target.Status.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$dataVersion", target.DataVersion);
            if (cmd.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                log.Add("RESERVATION_CANCEL_BATCH_CAS", $"R{target.Id}",
                    $"result=REJECTED reason=snapshot_changed expectedStatus={target.Status} expectedDataVersion={target.DataVersion} " +
                    $"targets=[{string.Join(",", targets.Select(x => $"R{x.Id}:{x.Status}:v{x.DataVersion}"))}] action=rollback_entire_batch rule=release_contract");
                return false;
            }
        }

        tx.Commit();
        var afterList = targets
            .Select(x => GetById(x.Id))
            .Where(x => x is not null)
            .Cast<Reservation>()
            .OrderBy(x => x.Id)
            .ToList();

        if (afterList.Count != targets.Count || afterList.Any(x => x.Status != ReservationStatus.Cancelled))
        {
            log.Add("RESERVATION_CANCEL_BATCH_CAS", "Batch",
                $"result=COMMITTED_WITH_READBACK_MISMATCH expected={targets.Count} actual={afterList.Count} rule=release_contract");
        }

        foreach (var before in targets)
        {
            var after = afterList.FirstOrDefault(x => x.Id == before.Id);
            if (after is null) continue;
            LogStartingDataVersionMutation(
                "TryCancelReservationsAtomicCas",
                before,
                after,
                nameof(Reservation.Status),
                nameof(Reservation.IsConflicted),
                nameof(Reservation.DataVersion));
            mutationJournal.Record(
                ReservationMutationKind.Updated,
                before,
                after,
                mutationMetadata,
                nameof(Reservation.Status),
                nameof(Reservation.IsConflicted),
                nameof(Reservation.DataVersion));
        }

        cancelled = afterList;
        log.Add("RESERVATION_CANCEL_BATCH_CAS", "Batch",
            $"result=APPLIED count={afterList.Count} targets=[{string.Join(",", afterList.Select(x => $"R{x.Id}:v{x.DataVersion}"))}] rule=release_contract");
        return true;
    }

    /// <summary>
    /// RESERVATION_CONFLICT_VERSION_CAS_INVARIANT:
    /// 競合フラグは共通割当の判定スナップショットと同じDataVersionをCAS条件にして更新する。
    /// 判定後の予約編集・状態遷移・別割当結果を、古い競合判定で上書きしてはならない。
    /// 成功時はDataVersionも同じUPDATEで進め、直接無条件UPDATEを再導入しない。
    /// </summary>
    public bool TryUpdateConflictedCas(int id, long expectedDataVersion, bool conflicted, string reason)
    {
        var before = GetById(id);
        if (before is null || before.DataVersion != expectedDataVersion)
            return false;

        if (before.IsConflicted == conflicted)
            return true;

        if (conflicted && HasOpenRecordingRuntime(before))
        {
            log.Add("RESERVATION_STATUS_WRITE_GUARD", $"R{id}",
                $"result=SUPPRESSED field=IsConflicted attempted=True kept=False reason=open_recording_runtime source=TryUpdateConflictedCas " +
                $"status={before.Status} started={before.RecordingStartedAt:MM/dd HH:mm:ss} end={before.EndTime:MM/dd HH:mm:ss} " +
                $"tuner={SafeTuner(EffectiveTunerName(before))} service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} " +
                $"rule=release_contract");
            return false;
        }

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
            UPDATE reservations
            SET is_conflicted = $v,
                data_version = data_version + 1,
                updated_at = $now
            WHERE id = $id
              AND data_version = $version
              AND is_conflicted = $expected
              AND status = 'scheduled';
            """;
        cmd.Parameters.AddWithValue("$v", conflicted ? 1 : 0);
        cmd.Parameters.AddWithValue("$expected", before.IsConflicted ? 1 : 0);
        cmd.Parameters.AddWithValue("$version", expectedDataVersion);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        var affected = cmd.ExecuteNonQuery();
        var after = affected == 1 ? GetById(id) : null;
        log.Add("RESERVATION_AUDIT", "CONFLICT_FLAG_CAS",
            $"service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} id=R{id} affected={affected} from={before.IsConflicted} to={conflicted} dataVersion={expectedDataVersion}->{after?.DataVersion.ToString() ?? "-"} status={before.Status} enabled={before.IsEnabled} reason={reason} start={before.StartTime:MM/dd HH:mm:ss} end={before.EndTime:MM/dd HH:mm:ss}");
        if (affected != 1 || after is null)
            return false;

        mutationJournal.Record(ReservationMutationKind.ConflictChanged, before, after,
            new[] { nameof(Reservation.IsConflicted), nameof(Reservation.DataVersion) });
        return true;
    }

    /// <summary>
    /// release_contract: Completed / Cancelled に残った is_conflicted を整理する。
    /// TERMINAL_CONFLICT_RESIDUE_VERSION_CAS_INVARIANT:
    /// 候補取得後の別更新を古い清掃巡回で上書きしないため、予約単位でDataVersionをCASする。
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
        var affected = 0;
        foreach (var target in targets)
        {
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"""
                UPDATE reservations
                SET is_conflicted = 0,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND data_version = $version
                  AND is_conflicted = 1
                  AND source != 'epg'
                  AND (status = 'completed' OR status = 'cancelled');
                """;
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            cmd.Parameters.AddWithValue("$id", target.Id);
            cmd.Parameters.AddWithValue("$version", target.DataVersion);
            if (cmd.ExecuteNonQuery() != 1)
                continue;

            affected++;
            var after = GetById(target.Id);
            if (after is not null)
                mutationJournal.Record(ReservationMutationKind.ConflictChanged, target, after,
                    new[] { nameof(Reservation.IsConflicted), nameof(Reservation.DataVersion) });
        }

        var sample = string.Join(";", targets
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(5)
            .Select(x => $"R{x.Id}:{TrimForAudit(x.ServiceName)}:{x.Status}"));

        log.Add("RESERVATION_AUDIT", "TERMINAL_CONFLICT_CLEANUP",
            $"affected={affected} candidates={targets.Count} reason={reason} statuses=Completed,Cancelled failedPreserved=True versionCas=True sample={sample} rule=release_contract");

        return affected;
    }

    /// <summary>
    /// is_enabledを実差分時だけ更新する。DataVersion・イベント・キーワード抑止も実変更時だけ進める。
    /// </summary>
    public ReservationEnabledUpdateResult UpdateEnabledIfChanged(int id, bool enabled)
    {
        using var con = db.Open();
        using var tx = con.BeginTransaction();

        Reservation? before;
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"""
                SELECT id, network_id, transport_stream_id, service_id, event_id,
                       title, start_time, end_time, status, source, created_at, updated_at,
                       channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                       recording_started_at, recording_finished_at, service_name,
                       scheduled_start_time, source_rule_id, source_rule_name,
                       is_user_chain, user_chain_previous_id, user_chain_root_id,
                       recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                FROM reservations
                WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", id);
            before = ReadReservations(read).FirstOrDefault();
        }

        if (before is null)
        {
            tx.Rollback();
            return new(false, false, "not_found", id, null, null, 0, 0, null, null);
        }

        if (before.IsEnabled == enabled)
        {
            tx.Rollback();
            log.Add("RESERVATION_AUDIT", "ENABLED_FLAG",
                $"service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} id=R{id} changed=False reason=already_in_requested_state enabled={enabled} dataVersion={before.DataVersion} status={before.Status} conflicted={before.IsConflicted} start={before.StartTime:MM/dd HH:mm:ss} end={before.EndTime:MM/dd HH:mm:ss} userChain={before.IsUserChain} chainPrev={(before.UserChainPreviousId.HasValue ? $"R{before.UserChainPreviousId.Value}" : "-")} rule=release_contract");
            return new(true, false, "already_in_requested_state", id, before.IsEnabled, before.IsEnabled, before.DataVersion, before.DataVersion, before, before);
        }

        var now = DateTime.Now;
        using (var update = con.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = $"""
                UPDATE reservations
                SET is_enabled = $v,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND data_version = $version
                  AND is_enabled = $expected;
                """;
            update.Parameters.AddWithValue("$v", enabled ? 1 : 0);
            update.Parameters.AddWithValue("$expected", before.IsEnabled ? 1 : 0);
            update.Parameters.AddWithValue("$version", before.DataVersion);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            if (update.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                var latest = GetById(id);
                return new(true, false, "compare_and_set_failed", id, before.IsEnabled, latest?.IsEnabled, before.DataVersion, latest?.DataVersion ?? before.DataVersion, before, latest);
            }
        }

        Reservation? after;
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"""
                SELECT id, network_id, transport_stream_id, service_id, event_id,
                       title, start_time, end_time, status, source, created_at, updated_at,
                       channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                       recording_started_at, recording_finished_at, service_name,
                       scheduled_start_time, source_rule_id, source_rule_name,
                       is_user_chain, user_chain_previous_id, user_chain_root_id,
                       recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                FROM reservations
                WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", id);
            after = ReadReservations(read).FirstOrDefault();
        }

        tx.Commit();

        LogStartingDataVersionMutation(
            "UpdateEnabledIfChanged",
            before,
            after,
            nameof(Reservation.IsEnabled),
            nameof(Reservation.DataVersion));

        if (before.Source == ReservationSource.Keyword)
        {
            if (!enabled)
                AddKeywordSuppressionForUserDisabled(before, "enabled_toggle_off");
            else
                RemoveKeywordSuppressionForUserEnabled(before, "enabled_toggle_on");
        }

        log.Add("RESERVATION_AUDIT", "ENABLED_FLAG",
            $"service={TrimForAudit(before.ServiceName)} title={TrimTitleForAudit(before.Title)} rawTitleBlank={RawTitleBlankForAudit(before.Title)} id=R{id} changed=True from={before.IsEnabled} to={enabled} dataVersion={before.DataVersion}->{after?.DataVersion ?? before.DataVersion + 1} status={before.Status} conflicted={before.IsConflicted} start={before.StartTime:MM/dd HH:mm:ss} end={before.EndTime:MM/dd HH:mm:ss} userChain={before.IsUserChain} chainPrev={(before.UserChainPreviousId.HasValue ? $"R{before.UserChainPreviousId.Value}" : "-")} rule=release_contract");

        mutationJournal.Record(enabled ? ReservationMutationKind.Enabled : ReservationMutationKind.Disabled, before, after, nameof(Reservation.IsEnabled));
        return new(true, true, "changed", id, before.IsEnabled, after?.IsEnabled ?? enabled, before.DataVersion, after?.DataVersion ?? before.DataVersion + 1, before, after);
    }

    /// <summary>
    /// 放送終了時刻を過ぎても scheduled のまま残っているユーザー予約を Cancelled に移行する。
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

        // EXPIRED_SCHEDULED_VERSION_CAS_INVARIANT:
        // 期限切れ判定後に開始・編集された予約を古い巡回結果で取消へ落とさない。
        // 判定時DataVersionをCAS条件にし、成功した状態遷移と同じUPDATEで世代を進める。
        using var con = db.Open();
        using var tx = con.BeginTransaction();
        var affected = 0;
        foreach (var r in candidates)
        {
            log.Add("RESERVATION_AUDIT", "EXPIRE_CANDIDATE",
                $"service={TrimForAudit(r.ServiceName)} title={TrimTitleForAudit(r.Title)} rawTitleBlank={RawTitleBlankForAudit(r.Title)} id=R{r.Id} status={r.Status} source={r.Source} enabled={r.IsEnabled} conflicted={r.IsConflicted} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} tuner={SafeTuner(r.TunerName)} userChain={r.IsUserChain} chainPrev={(r.UserChainPreviousId.HasValue ? $"R{r.UserChainPreviousId.Value}" : "-")} reason=end_time_past_without_recording rule=release_contract");

            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE reservations
                SET status = 'cancelled',
                    is_conflicted = 0,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'scheduled'
                  AND source != 'epg'
                  AND (recording_started_at IS NULL OR recording_started_at = '')
                  AND end_time < $cutoff
                  AND data_version = $dataVersion;
                """;
            cmd.Parameters.AddWithValue("$id", r.Id);
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            cmd.Parameters.AddWithValue("$cutoff", now.ToString("O"));
            cmd.Parameters.AddWithValue("$dataVersion", r.DataVersion);
            var rows = cmd.ExecuteNonQuery();
            affected += rows;
            if (rows == 0)
                log.Add("RESERVATION_AUDIT", "EXPIRE_CAS_REJECTED",
                    $"id=R{r.Id} expectedDataVersion={r.DataVersion} reason=state_or_version_changed action=preserve_newer_runtime_or_edit rule=release_contract");
        }
        tx.Commit();

        if (affected > 0)
            log.Add("RESERVATION_AUDIT", "EXPIRE_APPLY", $"affected={affected} reason=end_time_past_without_recording dataVersion=incremented");
        return affected;
    }

    /// <summary>
    /// 中断復旧時に終了済み証拠が確認できたRecording予約を、終了実績と同じTransactionでCompletedへ収束させる。
    /// </summary>
    public ReservationLifecycleTransitionResult TryFinalizePastEndedRecordingAsCompleted(
        int id,
        long expectedDataVersion,
        DateTime finishedAt)
    {
        // INTERRUPTED_RECOVERY_COMPLETED_FINALIZE_ATOMIC_CAS_INVARIANT:
        // 終了実績の記録とCompleted遷移を別UPDATEに分けない。
        // 中断復旧判定に使ったRecordingスナップショット世代をCAS条件とし、
        // 判定後にworker再接続・状態遷移・予約編集が入った場合は全体を拒否する。
        using var con = db.Open();
        using var tx = con.BeginTransaction();

        Reservation? before;
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"""
                SELECT id, network_id, transport_stream_id, service_id, event_id,
                       title, start_time, end_time, status, source, created_at, updated_at,
                       channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                       recording_started_at, recording_finished_at, service_name,
                       scheduled_start_time, source_rule_id, source_rule_name,
                       is_user_chain, user_chain_previous_id, user_chain_root_id,
                       recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                       reservation_intent, created_through, created_by_plugin_id
                FROM reservations
                WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", id);
            before = ReadReservations(read, projectRuntimeStatus: false).FirstOrDefault();
        }

        if (before is null)
        {
            tx.Rollback();
            return new(false, "not_found", id, null, null, expectedDataVersion, expectedDataVersion, null);
        }
        if (before.Status != ReservationStatus.Recording)
        {
            tx.Rollback();
            return new(false, "status_mismatch", id, before.Status, before.Status, expectedDataVersion, before.DataVersion, before);
        }
        if (before.DataVersion != expectedDataVersion)
        {
            tx.Rollback();
            return new(false, "data_version_mismatch", id, before.Status, before.Status, expectedDataVersion, before.DataVersion, before);
        }
        if (before.RecordingFinishedAt.HasValue)
        {
            tx.Rollback();
            return new(false, "recording_finished_at_already_set", id, before.Status, before.Status, expectedDataVersion, before.DataVersion, before);
        }

        var now = DateTime.Now;
        using (var update = con.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = $"""
                UPDATE reservations
                SET status = 'completed',
                    is_conflicted = 0,
                    recording_finished_at = $finished,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND status = 'recording'
                  AND data_version = $dataVersion
                  AND (recording_finished_at IS NULL OR recording_finished_at = '');
                """;
            update.Parameters.AddWithValue("$finished", finishedAt.ToString("O"));
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$dataVersion", expectedDataVersion);
            if (update.ExecuteNonQuery() != 1)
            {
                tx.Rollback();
                var latest = GetByIdPersisted(id);
                return new(false, "compare_and_set_failed", id, before.Status, latest?.Status, expectedDataVersion, latest?.DataVersion ?? expectedDataVersion, latest);
            }
        }

        tx.Commit();
        var after = GetByIdPersisted(id);
        log.Add("RESERVATION_LIFECYCLE_CAS", $"R{id}",
            $"result=APPLIED from=Recording to=Completed dataVersion={expectedDataVersion}->{after?.DataVersion ?? expectedDataVersion + 1} finishedAt={finishedAt:O} source=StartupPastEndedEvidence atomicFinishEvidence=True rule=release_contract");
        mutationJournal.Record(ReservationMutationKind.RecordingCompleted, before, after,
            nameof(Reservation.Status), nameof(Reservation.IsConflicted),
            nameof(Reservation.RecordingFinishedAt), nameof(Reservation.DataVersion));
        return new(true, "applied", id, ReservationStatus.Recording, after?.Status ?? ReservationStatus.Completed, expectedDataVersion, after?.DataVersion ?? expectedDataVersion + 1, after);
    }

    /// <summary>
    /// 定時EPGのSystem責務エントリを、物理録画Tunerごとに登録・更新する。
    /// titleで同一Tuner行を識別し、Plannerが確定枠を持つ場合はその枠へ、
    /// UNPLACED_PENDING_DEADLINE中は設定時刻(canonical)の責務表示へ更新する。
    /// 実行開始の正本はEpgSchedulerの可動枠Plannerであり、この行自体を通常録画として実行しない。
    /// 予約一覧ではSystemEpgResponsibilityPlanServiceが各Tunerの先頭PreRec有無を決め、
    /// PreRec責務が無いTunerだけこのDaily責務をfallback表示する。
    /// 定時EPG取得と録画前EPG確認は別実行契約であり、互いを実行代替しない。
    /// 通常録画の競合計画には含めない。
    /// </summary>
    public bool UpsertEpgScheduleEntry(string group, DateTime start, DateTime end, string title, string tunerName = "")
    {
        using var con = db.Open();
        using var sel = con.CreateCommand();
        sel.CommandText = $"""
            SELECT id, data_version, start_time, end_time, tuner_name FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND reservation_intent = '{ReservationIntentContract.SystemDailyEpgStorageValue}'
              AND title = $title
            LIMIT 1;
            """;
        sel.Parameters.AddWithValue("$title", title);

        int? existingId = null;
        long existingDataVersion = 0;
        DateTime existingStart = default;
        DateTime existingEnd = default;
        string existingTuner = string.Empty;
        using (var rdr = sel.ExecuteReader())
        {
            if (rdr.Read())
            {
                existingId = rdr.GetInt32(0);
                existingDataVersion = rdr.GetInt64(1);
                DateTime.TryParse(rdr.GetString(2), out existingStart);
                DateTime.TryParse(rdr.GetString(3), out existingEnd);
                existingTuner = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4);
            }
        }

        if (existingId is not null
            && existingStart == start
            && existingEnd == end
            && string.Equals(existingTuner, tunerName, StringComparison.Ordinal))
            return false;

        var now = DateTime.Now.ToString("O");
        if (existingId is not null)
        {
            // SYSTEM_EPG_ENTRY_VERSION_CAS_INVARIANT:
            // 定時EPG内部予約も実行直前に Scheduled から遷移し得る。
            // 選択後の状態遷移や再設定を古い時刻で上書きしないため、選択時世代でCASし、同じUPDATEで世代を進める。
            using var upd = con.CreateCommand();
            upd.CommandText = $"""
                UPDATE reservations
                SET start_time = $start,
                    end_time = $end,
                    tuner_name = $tuner,
                    reservation_intent = '{ReservationIntentContract.SystemDailyEpgStorageValue}',
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND source = 'epg'
                  AND status = 'scheduled'
                  AND data_version = $dataVersion;
                """;
            upd.Parameters.AddWithValue("$start", start.ToString("O"));
            upd.Parameters.AddWithValue("$end",   end.ToString("O"));
            upd.Parameters.AddWithValue("$tuner", tunerName);
            upd.Parameters.AddWithValue("$now",   now);
            upd.Parameters.AddWithValue("$id",    existingId.Value);
            upd.Parameters.AddWithValue("$dataVersion", existingDataVersion);
            return upd.ExecuteNonQuery() > 0;
        }
        else
        {
            using var ins = con.CreateCommand();
            ins.CommandText = $"""
                INSERT INTO reservations
                  (network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source,
                   channel_argument, is_conflicted, is_enabled, tuner_name, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name, created_at, updated_at,
                   reservation_intent, created_through, created_by_plugin_id)
                VALUES
                  (0, 0, 0, 0,
                   $title, $start, $end, 'scheduled', 'epg',
                   '', 0, 1, $tuner, '',
                   '', NULL, '', $now, $now,
                   '{ReservationIntentContract.SystemDailyEpgStorageValue}', 'System', '');
                """;
            ins.Parameters.AddWithValue("$title", title);
            ins.Parameters.AddWithValue("$start", start.ToString("O"));
            ins.Parameters.AddWithValue("$end",   end.ToString("O"));
            ins.Parameters.AddWithValue("$tuner", tunerName);
            ins.Parameters.AddWithValue("$now",   now);
            return ins.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// 単一Daily Allの定時EPG取得が正常完了した時点で、実行時刻までに到来している定時EPG行だけをcompletedに変更する。
    /// 独立した録画前EPG確認行と、翌日以降の定時EPG行は対象にしない。
    /// </summary>
    public int CompleteDueDailyEpgScheduleEntries(DateTime completedAt)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
            UPDATE reservations
            SET status = 'completed', is_conflicted = 0, data_version = data_version + 1, updated_at = $now
            WHERE source = 'epg'
              AND status = 'scheduled'
              AND source_rule_id IS NULL
              AND reservation_intent = '{ReservationIntentContract.SystemDailyEpgStorageValue}'
              AND julianday(start_time) <= julianday($completedAt);
            """;
        cmd.Parameters.AddWithValue("$now", completedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Scheduled状態の定時EPG取得行だけを削除する。
    /// 録画前EPG確認は定時EPG設定から独立しているため変更しない。
    /// </summary>
    public int DeleteScheduledDailyEpgEntries()
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM reservations
            WHERE source = 'epg'
              AND status = 'scheduled'
              AND source_rule_id IS NULL
              AND reservation_intent = '{ReservationIntentContract.SystemDailyEpgStorageValue}';
            """;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 指定した録画用チューナーの定時EPG行だけを削除する。録画前EPG確認は対象外。
    /// 可動枠がskip/cancel/failで当日利用されない場合の予約一覧投影整理に使用する。
    /// </summary>
    public int DeleteScheduledDailyEpgEntriesForTuner(string tunerName)
    {
        if (string.IsNullOrWhiteSpace(tunerName)) return 0;
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM reservations
            WHERE source = 'epg'
              AND status = 'scheduled'
              AND source_rule_id IS NULL
              AND reservation_intent = '{ReservationIntentContract.SystemDailyEpgStorageValue}'
              AND tuner_name = $tuner;
            """;
        cmd.Parameters.AddWithValue("$tuner", tunerName);
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
        cmd.CommandText = $"""
            DELETE FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND tuner_name = $tuner;
            """;
        cmd.Parameters.AddWithValue("$tuner", tunerName);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 正規対象親以外に残ったScheduled録画前EPG確認を削除する。
    /// 定時EPG行は対象にしない。
    /// </summary>
    public int DeleteScheduledPreRecordEpgEntriesExceptParents(IEnumerable<int> parentReservationIds)
    {
        var keep = parentReservationIds.Where(id => id > 0).ToHashSet();
        using var con = db.Open();
        var targets = new List<int>();
        using (var sel = con.CreateCommand())
        {
            sel.CommandText = $"""
                SELECT id, source_rule_id
                FROM reservations
                WHERE source = 'epg' AND status = 'scheduled'
                  AND reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}';
                """;
            using var rdr = sel.ExecuteReader();
            while (rdr.Read())
            {
                var id = rdr.GetInt32(0);
                var parentId = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                if (!keep.Contains(parentId)) targets.Add(id);
            }
        }

        if (targets.Count == 0) return 0;
        using var tx = con.BeginTransaction();
        var deleted = 0;
        foreach (var id in targets)
        {
            using var del = con.CreateCommand();
            del.Transaction = tx;
            del.CommandText = $"""
                DELETE FROM reservations
                WHERE id = $id AND source = 'epg' AND status = 'scheduled'
                  AND reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}';
                """;
            del.Parameters.AddWithValue("$id", id);
            deleted += del.ExecuteNonQuery();
        }
        tx.Commit();
        return deleted;
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
        cmd.CommandText = $"""
            DELETE FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND source_rule_id = $parent
              AND reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}';
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
            sel.CommandText = $"""
                SELECT child.id,
                       child.source_rule_id,
                       parent.id,
                       COALESCE(parent.status, ''),
                       COALESCE(parent.is_enabled, 0),
                       COALESCE(parent.is_conflicted, 0),
                       COALESCE(parent.is_user_chain, 0),
                       parent.user_chain_previous_id
                FROM reservations child
                LEFT JOIN reservations parent ON parent.id = child.source_rule_id
                WHERE child.source = 'epg'
                  AND child.status = 'scheduled'
                  AND child.reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}';
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
                var chainPreviousId = rdr.IsDBNull(7) ? (int?)null : rdr.GetInt32(7);
                var reason = string.Empty;
                if (!parentExists) reason = "parent_missing";
                else if (!enabled) reason = "parent_disabled";
                else if (conflicted) reason = "parent_conflicted";
                else if (userChain && chainPreviousId.HasValue) reason = "parent_user_chain_child";
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
                del.CommandText = $"""
                    DELETE FROM reservations
                    WHERE id = $id
                      AND source = 'epg'
                      AND status = 'scheduled'
                      AND reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}';
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
            Count("parent_user_chain_child"),
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
        cmd.CommandText = $"""
            DELETE FROM reservations
            WHERE status IN ('completed', 'failed', 'cancelled')
              AND updated_at <> '' AND updated_at < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    private static bool IsExactContinuousPair(Reservation predecessor, Reservation successor)
    {
        // CHAIN_DEVELOPER_APPROVAL_REQUIRED: チェーン境界の同一サービス/隣接判定は
        // ChainReservationEligibilityContract / ChainReservationContract を唯一の正本とする。
        // Allocation側でチャンネル名・ChannelArgument・独自時間窓へフォールバックしてはならない。
        return ChainReservationEligibilityContract.IsSameServiceAndAdjacent(predecessor, successor);
    }

    /// <summary>
    /// 実際に同一チューナーで引き継がれる連続番組ペアを返す。
    /// 条件: 同一 service（network_id / ts_id / service_id 完全一致）かつ
    ///       前番組 EndTime と後番組 StartTime が共通隣接契約内（-5秒〜+120秒）かつ
    ///       同一 TunerName が確定していること。
    /// key=後続ID, value=前番組ID
    /// </summary>
    public Dictionary<int, int> GetChainPredecessors()
    {
        // CHAIN_TOPOLOGY_SINGLE_SOURCE_INVARIANT:
        // チェーン構造は、チェーンボタンで保存された
        // is_user_chain / user_chain_previous_id だけを正本とする。
        // Scheduled/Recordingなど現在の状態集合からRootや所属を再推論しない。
        //
        // 後続が実行対象である間は、前段がCompleted/Stoppingになってもリンクを保持する。
        // これにより R1(Completed) -> R2(Stopping) -> R3(Scheduled) の途中で
        // R2が新しいRootとして扱われることを防ぐ。
        //
        // 同一サービス・隣接時刻・同一Tunerからの自動チェーン生成は禁止。
        //
        // release_contract:
        // 高優先度のチェーン境界監視は500ms周期でこのトポロジーだけを必要とする。
        // Reservation全列をGetAll()でmaterializeせず、同じreservations正本から
        // 保存トポロジー判定に必要な後続ID/前段IDだけを直接読む。キャッシュは持たず、
        // 削除・無効化・status遷移を次の監視周期でそのまま観測する。
        // ParseStatusがterminal以外をactive側へ投影する契約とsource!=epg条件も従来GetAll経路と同一。
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT successor.id, successor.user_chain_previous_id
            FROM reservations AS successor
            INNER JOIN reservations AS predecessor
                    ON predecessor.id = successor.user_chain_previous_id
                   AND predecessor.source <> 'epg'
            WHERE successor.source <> 'epg'
              AND successor.is_enabled = 1
              AND successor.is_user_chain = 1
              AND successor.user_chain_previous_id IS NOT NULL
              AND successor.status NOT IN ('completed','cancelled','failed')
            ORDER BY successor.start_time;
            """;

        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<int, int>();
        while (reader.Read())
        {
            result[reader.GetInt32(0)] = reader.GetInt32(1);
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
    /// 親予約ごとの録画前EPG確認Intentを取得する。
    /// PRE_RECORD_EPG_INTENT_SINGLE_OWNER_INVARIANT:
    /// 物理Tuner・EventId・確認時間窓は識別子にしない。親予約1件につき有効Intentは1件だけとし、
    /// Scheduledなら現在の親予約と設定から導いた時間窓へ同じ行を更新する。
    /// Completedは「この親予約について確認済み」の終端証拠であり、同一親へ新規Intentを再生成しない。
    /// </summary>
    public bool TryFindReusablePreRecordEpgEntry(
        int parentReservationId,
        out int existingId,
        out ReservationStatus existingStatus,
        out string existingTunerName,
        out long existingDataVersion)
    {
        existingId = 0;
        existingStatus = ReservationStatus.Scheduled;
        existingTunerName = string.Empty;
        existingDataVersion = 0;
        if (parentReservationId <= 0)
            return false;

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, status, COALESCE(tuner_name, ''), data_version FROM reservations
            WHERE source = 'epg'
              AND status IN ('scheduled', 'recording', 'completed')
              AND source_rule_id = $parent
              AND reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}'
            ORDER BY
              CASE status
                WHEN 'recording' THEN 0
                WHEN 'completed' THEN 1
                WHEN 'scheduled' THEN 2
                ELSE 9
              END,
              id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$parent", parentReservationId);

        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read())
            return false;

        existingId = rdr.GetInt32(0);
        existingStatus = ParseStatus(rdr.GetString(1));
        existingTunerName = rdr.GetString(2);
        existingDataVersion = rdr.GetInt64(3);
        return true;
    }

    /// <summary>
    /// Scheduledの録画前EPG確認Intentを、現在の親予約・確認時間窓へCAS更新する。
    /// 物理TunerはIntentの正本ではないため常に空欄へ戻す。
    /// </summary>
    public bool RebindScheduledPreRecordEpgEntry(
        int epgReservationId,
        long expectedDataVersion,
        int parentReservationId,
        DateTime start,
        DateTime end)
    {
        if (epgReservationId <= 0 || expectedDataVersion < 0 || parentReservationId <= 0)
            return false;

        var parent = GetById(parentReservationId);
        var title = BuildPreRecordEpgStorageTitle();
        var now = DateTime.Now.ToString("O");

        using var con = db.Open();
        using var upd = con.CreateCommand();
        upd.CommandText = $"""
            UPDATE reservations
            SET title = $title,
                start_time = $start,
                end_time = $end,
                tuner_name = '',
                source_rule_id = $parent,
                network_id = $nid,
                transport_stream_id = $tsid,
                service_id = $sid,
                event_id = $eventId,
                channel_argument = $channelArg,
                service_name = $serviceName,
                source_rule_name = '',
                reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}',
                data_version = data_version + 1,
                updated_at = $now
            WHERE id = $id
              AND source = 'epg'
              AND status = 'scheduled'
              AND source_rule_id = $parent
              AND data_version = $dataVersion
              AND reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}';
            """;
        BindPreRecordEpgParameters(upd, title, start, end, parentReservationId, parent, now);
        upd.Parameters.AddWithValue("$id", epgReservationId);
        upd.Parameters.AddWithValue("$dataVersion", expectedDataVersion);
        var updated = upd.ExecuteNonQuery();
        if (updated != 1)
            return false;

        DeleteDuplicateScheduledPreRecordEpgEntries(con, epgReservationId, parentReservationId);
        return true;
    }

    /// <summary>
    /// 録画前EPG確認Intentを親予約単位で登録・更新する。
    /// PRE_RECORD_EPG_INTENT_SINGLE_OWNER_INVARIANT:
    /// 親予約1件につきScheduled Intentは1件だけ。設定変更・時刻追従・再割当では同じ行を更新し、
    /// 旧時間窓の子予約を残さない。物理Tunerは実行時にTunerPoolから選ぶため保存しない。
    /// </summary>
    public void UpsertPreRecordEpgEntry(int reservationId, DateTime start, DateTime end)
    {
        var parent = GetById(reservationId);
        var title = BuildPreRecordEpgStorageTitle();
        using var con = db.Open();

        using var sel = con.CreateCommand();
        sel.CommandText = $"""
            SELECT id, data_version FROM reservations
            WHERE source = 'epg' AND status = 'scheduled'
              AND source_rule_id = $parent
              AND reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}'
            ORDER BY id DESC
            LIMIT 1;
            """;
        sel.Parameters.AddWithValue("$parent", reservationId);
        int? existingId = null;
        long existingDataVersion = 0;
        using (var rdr = sel.ExecuteReader())
        {
            if (rdr.Read())
            {
                existingId = rdr.GetInt32(0);
                existingDataVersion = rdr.GetInt64(1);
            }
        }

        var now = DateTime.Now.ToString("O");
        if (existingId is not null)
        {
            using var upd = con.CreateCommand();
            upd.CommandText = $"""
                UPDATE reservations
                SET title = $title,
                    start_time = $start,
                    end_time = $end,
                    tuner_name = '',
                    source_rule_id = $parent,
                    network_id = $nid,
                    transport_stream_id = $tsid,
                    service_id = $sid,
                    event_id = $eventId,
                    channel_argument = $channelArg,
                    service_name = $serviceName,
                    source_rule_name = '',
                    reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}',
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND source = 'epg'
                  AND status = 'scheduled'
                  AND data_version = $dataVersion;
                """;
            BindPreRecordEpgParameters(upd, title, start, end, reservationId, parent, now);
            upd.Parameters.AddWithValue("$id", existingId.Value);
            upd.Parameters.AddWithValue("$dataVersion", existingDataVersion);
            if (upd.ExecuteNonQuery() == 1)
                DeleteDuplicateScheduledPreRecordEpgEntries(con, existingId.Value, reservationId);
        }
        else
        {
            using var ins = con.CreateCommand();
            ins.CommandText = $"""
                INSERT INTO reservations
                  (network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source,
                   channel_argument, is_conflicted, is_enabled, tuner_name, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name, created_at, updated_at,
                   reservation_intent, created_through, created_by_plugin_id)
                VALUES
                  ($nid, $tsid, $sid, $eventId,
                   $title, $start, $end, 'scheduled', 'epg',
                   $channelArg, 0, 1, '', $serviceName,
                   '', $parent, '', $now, $now,
                   '{ReservationIntentContract.SystemPreRecordEpgStorageValue}', 'System', '');
                """;
            BindPreRecordEpgParameters(ins, title, start, end, reservationId, parent, now);
            ins.ExecuteNonQuery();
            using var last = con.CreateCommand();
            last.CommandText = "SELECT last_insert_rowid();";
            var insertedId = Convert.ToInt32(last.ExecuteScalar());
            DeleteDuplicateScheduledPreRecordEpgEntries(con, insertedId, reservationId);
        }
    }

    private static void DeleteDuplicateScheduledPreRecordEpgEntries(
        Microsoft.Data.Sqlite.SqliteConnection con,
        int keepId,
        int parentReservationId)
    {
        using var del = con.CreateCommand();
        del.CommandText = $"""
            DELETE FROM reservations
            WHERE source = 'epg'
              AND status = 'scheduled'
              AND source_rule_id = $parent
              AND reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}'
              AND id <> $keepId;
            """;
        del.Parameters.AddWithValue("$parent", parentReservationId);
        del.Parameters.AddWithValue("$keepId", keepId);
        del.ExecuteNonQuery();
    }


    private static string BuildPreRecordEpgStorageTitle()
    {
        // 用途の正本は Reservation.Intent=SystemPreRecordEpg。Titleはユーザー表示の基礎文言に限定する。
        // 予約一覧の「EPG確認（T2）」等はSystemEpgResponsibilityPlanのPriorityNameからPresentation層が組み立てる。
        // PriorityNameは論理優先度、実行物理TunerはEpgScheduler/TunerPoolの別責務であり混在させない。
        return "EPG確認";
    }

    private static void BindPreRecordEpgParameters(
        Microsoft.Data.Sqlite.SqliteCommand cmd,
        string title,
        DateTime start,
        DateTime end,
        int parentId,
        Reservation? parent,
        string now)
    {
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        cmd.Parameters.AddWithValue("$tuner", string.Empty);
        cmd.Parameters.AddWithValue("$parent", parentId);
        cmd.Parameters.AddWithValue("$nid", parent?.NetworkId ?? 0);
        cmd.Parameters.AddWithValue("$tsid", parent?.TransportStreamId ?? 0);
        cmd.Parameters.AddWithValue("$sid", parent?.ServiceId ?? 0);
        cmd.Parameters.AddWithValue("$eventId", parent?.EventId ?? 0);
        cmd.Parameters.AddWithValue("$channelArg", parent?.ChannelArgument ?? "");
        cmd.Parameters.AddWithValue("$serviceName", parent?.ServiceName ?? "");
        cmd.Parameters.AddWithValue("$now", now);
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
        IProgramEventSource programEvents)
        => ApplyTimeFollowingDetailed(targets, programEvents)
            .Where(r => r.Updated)
            .Select(r => r.ReservationId)
            .ToList();

    /// <summary>
    /// 時間追従の詳細適用。
    /// release_contract: 完成確認用に、更新/未更新/EPG未検出/保護スキップを呼び出し側で監査できる形で返す。
    /// </summary>
    public IReadOnlyList<TimeFollowApplyResult> ApplyTimeFollowingDetailed(
        IEnumerable<Reservation> targets,
        IProgramEventSource programEvents,
        bool allowUserChainSeriesFollow = false)
        => ApplyTimeFollowingDetailedCore(
            targets,
            r => programEvents.GetByEventKey(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId)?.ToEpgEvent(),
            allowUserChainSeriesFollow);

    /// <summary>
    /// 録画前EPG確認で実際に観測したprobe snapshotだけを使用して時間追従する。
    /// 通常EPG DB/IProgramEventSourceへ書き戻して再読込することを禁止し、
    /// 「今回のprobeで目的EventIdentityを観測できた」ことを追従証拠の正本にする。
    /// </summary>
    public IReadOnlyList<TimeFollowApplyResult> ApplyTimeFollowingDetailedFromObservedEvents(
        IEnumerable<Reservation> targets,
        IReadOnlyList<EpgEvent> observedEvents,
        bool allowUserChainSeriesFollow = false)
    {
        var byIdentity = observedEvents
            .GroupBy(e => (e.NetworkId, e.TransportStreamId, e.ServiceId, e.EventId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.UpdatedAt).First());

        return ApplyTimeFollowingDetailedCore(
            targets,
            r => byIdentity.TryGetValue((r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId), out var ev) ? ev : null,
            allowUserChainSeriesFollow);
    }

    private IReadOnlyList<TimeFollowApplyResult> ApplyTimeFollowingDetailedCore(
        IEnumerable<Reservation> targets,
        Func<Reservation, EpgEvent?> resolveEvent,
        bool allowUserChainSeriesFollow)
    {
        var results = new List<TimeFollowApplyResult>();
        foreach (var r in targets)
        {
            if (r.Source == ReservationSource.Epg)
                continue;

            // PROGRAM_TIME_SLOT_TIME_FOLLOW_INVARIANT:
            // Program reservations are explicit clock-time slots. Their configured time is authoritative;
            // EPG/EIT must never move them, even if an EventId was populated by projection/import code.
            // Enforce this again in the core so every caller shares the same protection.
            if (r.Source == ReservationSource.Program)
            {
                results.Add(BuildTimeFollowResult(r, false, "SKIP_SOURCE_PROGRAM_TIME_SLOT", null));
                continue;
            }

            // USER_CHAIN_TIME_FOLLOW_OWNER_INVARIANT:
            // 通常の全件EPG追従からは明示チェーンを除外する。チェーン系列の更新ownerは、
            // root親の録画前EPG確認、または同一物理Tunerで稼働中の録画セッションだけである。
            // ownerが系列全体を明示して呼んだ場合に限り、各memberを自身のEventIdentityで追従する。
            // 子ごとのEPG workerは起動せず、同一取得結果を再利用するためTuner負荷は増えない。
            if (r.IsUserChain && !allowUserChainSeriesFollow)
            {
                results.Add(BuildTimeFollowResult(r, false, "SKIP_USER_CHAIN_NON_OWNER", null));
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

            // release_contract:
            // 競合中の予約も EventIdentity を持つ限り EIT 更新追従の対象にする。
            // ここで SKIP_CONFLICT すると、EPG側で時刻変更されて競合が解消可能になっても、
            // 古い時刻のまま共通割り当てルートへ戻り、競合が固定化する。
            // disabled/user-chain/system EPG/program time-slot は従来どおり保護し、event_id=0 の TimeIdentity は追従しない。
            if (r.ServiceId == 0 || r.EventId == 0)
            {
                results.Add(BuildTimeFollowResult(r, false, "NO_SERVICE_OR_EVENT_ID", null));
                continue;
            }

            var ev = resolveEvent(r);
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

            // release_contract: 録画前EPG/通常EPGの時刻追従で、既存予約タイトルを空タイトルへ戻さない。
            // EPGの生値が空であること自体は許容するが、予約表示メタデータの正本は
            // 「ユーザーが予約した時点の表示タイトル」または既に解決済みの予約タイトルを保持する。
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

            if (!TryUpdateScheduledTitleStartEndTimeCas(
                    r.Id,
                    r.DataVersion,
                    effectiveTitle,
                    ev.ServiceName,
                    ev.Start,
                    ev.End))
            {
                results.Add(BuildTimeFollowResult(r, false, "REJECTED_SCHEDULED_FOLLOW_CAS", ev, effectiveTitle));
                continue;
            }

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
    private sealed record AllocationSnapshotToken(int Count, string Hash);
    private sealed record AllocationRowUpdate(
        int Id,
        long ExpectedDataVersion,
        string? TunerName,
        bool? Conflicted,
        string PreviousTunerName,
        string ServiceName,
        string Title,
        bool PreviousConflicted);

    private AllocationSnapshotToken ReadAllocationSnapshotToken(SqliteConnection? existingConnection = null, SqliteTransaction? transaction = null)
    {
        var ownsConnection = existingConnection is null;
        using var ownedConnection = ownsConnection ? db.Open() : null;
        var con = existingConnection ?? ownedConnection!;
        using var cmd = con.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT * FROM reservations ORDER BY id;";
        using var reader = cmd.ExecuteReader();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        while (reader.Read())
        {
            count++;
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? "<NULL>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var bytes = Encoding.UTF8.GetBytes(string.Join("|", values) + "\n");
            hash.AppendData(bytes);
        }
        return new AllocationSnapshotToken(count, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private long ReadAllocationGeneration(SqliteConnection? existingConnection = null, SqliteTransaction? transaction = null)
    {
        var ownsConnection = existingConnection is null;
        using var ownedConnection = ownsConnection ? db.Open() : null;
        var con = existingConnection ?? ownedConnection!;
        using var cmd = con.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT value FROM runtime_generations WHERE key = 'allocation';";
        var value = cmd.ExecuteScalar();
        return value is null || value is DBNull ? 0L : Convert.ToInt64(value);
    }

    // ========================================================================
    // 【開発者の明示承認なしに変更禁止】共通割り当てDB commit保護契約
    // この入口のCAS条件、DataVersion、allocation generation、TunerName、
    // IsConflicted、Mutation／Wake連携を変更する場合は、着手前に必ず開発者の
    // 明示承認を得ること。最適化・整理・不具合修正名目の無断変更も禁止する。
    // この保護コメント自体も、開発者の明示承認なしに改変・削除・弱体化禁止。
    // 過去のTVTest録画コア／旧録画起動ルートを復活させないこと。
    // 固定wait、sleep、delay、cooldown、settle、quiet windowで競合や解放を
    // 隠さず、状態・物理解放・worker終了の証拠で判定すること。
    // DBのテーブル、カラム、Trigger、Index、PRAGMA、別DB、一時DB、shadow table、
    // 補正値、隠し状態、二重保存、移行処理は、開発者の明示承認なしに追加・変更禁止。
    // ========================================================================
    private (bool Applied, long AllocationGeneration, string Reason, IReadOnlyList<(int Id, string ServiceName, string Title, bool Conflicted)> Changes, IReadOnlyList<AllocationCommittedRowChange> RowChanges)
        TryCommitAllocationPlan(
            IReadOnlyList<AllocationRowUpdate> updates,
            AllocationSnapshotToken expectedReservationSnapshot,
            long expectedAllocationGeneration,
            Tuner.TunerPool? tunerPool,
            long expectedTunerSnapshotVersion)
    {
        (bool Applied, long AllocationGeneration, string Reason, IReadOnlyList<(int Id, string ServiceName, string Title, bool Conflicted)> Changes, IReadOnlyList<AllocationCommittedRowChange> RowChanges) CommitCore()
        {
            using var con = db.Open();
            using var tx = con.BeginTransaction();

            var currentSnapshot = ReadAllocationSnapshotToken(con, tx);
            if (currentSnapshot != expectedReservationSnapshot)
            {
                tx.Rollback();
                return (false, expectedAllocationGeneration, "reservation_snapshot_changed", Array.Empty<(int, string, string, bool)>(), Array.Empty<AllocationCommittedRowChange>());
            }

            var currentGeneration = ReadAllocationGeneration(con, tx);
            if (currentGeneration != expectedAllocationGeneration)
            {
                tx.Rollback();
                return (false, currentGeneration, "allocation_generation_changed", Array.Empty<(int, string, string, bool)>(), Array.Empty<AllocationCommittedRowChange>());
            }

            // ALLOCATION_NOOP_GENERATION_INVARIANT:
            // FinalConflictPlanに実差分がない場合、予約行もallocation generationも変更しない。
            // generationは実際にTunerNameまたはIsConflictedをcommitした世代であり、監査巡回回数ではない。
            // updates=0の巡回で世代だけを進める旧動作へ戻してはならない。
            if (updates.Count == 0)
            {
                tx.Commit();
                return (true, currentGeneration, "no_changes", Array.Empty<(int, string, string, bool)>(), Array.Empty<AllocationCommittedRowChange>());
            }

            var now = DateTime.Now.ToString("O");
            var changes = new List<(int Id, string ServiceName, string Title, bool Conflicted)>();
            var rowChanges = new List<AllocationCommittedRowChange>();
            foreach (var update in updates)
            {
                using var cmd = con.CreateCommand();
                cmd.Transaction = tx;
                var setParts = new List<string>();
                if (update.TunerName is not null) setParts.Add("tuner_name = $tuner");
                if (update.Conflicted.HasValue) setParts.Add("is_conflicted = $conflicted");
                setParts.Add("data_version = data_version + 1");
                setParts.Add("updated_at = $now");
                // ALLOCATION_SCHEDULED_ROW_CAS_INVARIANT:
                // 共通割当commitはScheduled予約だけを所有する。開始・録画・終端へ遷移した行を
                // スナップショット時の計画で書き換えないため、DataVersionに加えてStatusも同じUPDATEで検証する。
                cmd.CommandText = $"UPDATE reservations SET {string.Join(", ", setParts)} WHERE id = $id AND status = 'scheduled' AND data_version = $version;";
                if (update.TunerName is not null) cmd.Parameters.AddWithValue("$tuner", update.TunerName);
                if (update.Conflicted.HasValue) cmd.Parameters.AddWithValue("$conflicted", update.Conflicted.Value ? 1 : 0);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$id", update.Id);
                cmd.Parameters.AddWithValue("$version", update.ExpectedDataVersion);
                if (cmd.ExecuteNonQuery() != 1)
                {
                    tx.Rollback();
                    return (false, currentGeneration, $"reservation_version_changed:R{update.Id}", Array.Empty<(int, string, string, bool)>(), Array.Empty<AllocationCommittedRowChange>());
                }
                if (update.Conflicted.HasValue && update.PreviousConflicted != update.Conflicted.Value)
                    changes.Add((update.Id, update.ServiceName, update.Title, update.Conflicted.Value));

                var currentTunerName = update.TunerName ?? update.PreviousTunerName;
                var currentConflicted = update.Conflicted ?? update.PreviousConflicted;
                rowChanges.Add(new AllocationCommittedRowChange(
                    update.Id,
                    update.ServiceName,
                    update.Title,
                    update.PreviousTunerName,
                    currentTunerName,
                    update.PreviousConflicted,
                    currentConflicted,
                    update.ExpectedDataVersion,
                    update.ExpectedDataVersion + 1));
            }

            using (var generation = con.CreateCommand())
            {
                generation.Transaction = tx;
                generation.CommandText = """
                    UPDATE runtime_generations
                    SET value = value + 1
                    WHERE key = 'allocation' AND value = $expected;
                    """;
                generation.Parameters.AddWithValue("$expected", expectedAllocationGeneration);
                if (generation.ExecuteNonQuery() != 1)
                {
                    tx.Rollback();
                    return (false, currentGeneration, "allocation_generation_compare_and_set_failed", Array.Empty<(int, string, string, bool)>(), Array.Empty<AllocationCommittedRowChange>());
                }
            }

            tx.Commit();
            return (true, expectedAllocationGeneration + 1, "applied", changes, rowChanges);
        }

        if (tunerPool is null)
            return CommitCore();

        if (!tunerPool.TryExecuteAtSnapshotVersion(expectedTunerSnapshotVersion, CommitCore, out var result))
            return (false, expectedAllocationGeneration, "tuner_snapshot_changed", Array.Empty<(int, string, string, bool)>(), Array.Empty<AllocationCommittedRowChange>());
        return result;
    }

    public bool TryGetNormalEpgConflictBlockerEvidence(int reservationId, out NormalEpgConflictBlockerEvidence evidence)
    {
        lock (normalEpgConflictEvidenceGate)
            return normalEpgConflictEvidenceByReservationId.TryGetValue(reservationId, out evidence!);
    }

    private void ReplaceNormalEpgConflictBlockerEvidence(IReadOnlyDictionary<int, NormalEpgConflictBlockerEvidence> evidence)
    {
        lock (normalEpgConflictEvidenceGate)
            normalEpgConflictEvidenceByReservationId = new Dictionary<int, NormalEpgConflictBlockerEvidence>(evidence);
    }

    public ReservationAllocationEvaluationResult ReevaluateConflicts(
        IReadOnlyList<Core.TunerProfile> tunerProfiles, bool laterProgramPriority,
        bool pseudoContinuous, int postEndMarginSeconds,
        Tuner.TunerPool? tunerPool,
        int preStartMarginSeconds)
    {
        var reservationSnapshot = ReadAllocationSnapshotToken();
        var allocationGenerationBefore = ReadAllocationGeneration();
        var tunerSlots = tunerPool?.GetStatus();
        var tunerSnapshotVersion = tunerSlots is { Count: > 0 }
            ? tunerSlots[0].SnapshotVersion
            : tunerPool?.SnapshotVersion ?? 0L;
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

        // 放送波グループは正規チャンネル／チューナーIdentityだけから解決する。
        string ResolveGroup(Reservation r) => ReservationTunerGroupResolver.Resolve(r, recordingTunerProfiles);

        static bool SupportsReservationGroup(string tunerGroup, string reservationGroup)
        {
            var tg = (tunerGroup ?? string.Empty).Trim().ToUpperInvariant();
            var rg = (reservationGroup ?? string.Empty).Trim().ToUpperInvariant();
            if (tg == "HYBRID") return rg is "GR" or "BSCS" or "HYBRID";
            return tg == rg;
        }

        // release_contract:
        // 予約リストの「優先度」列は、物理チューナー名でも BonDriver/DID でもなく、
        // 設定画面の優先度順に対応する仮想枠名(T1/S1/...)を表示正本にする。
        // Recording role だけを group 別 capacity として使う点は維持するが、
        // RoleBinding の物理識別寄り表示名を予約リストへ出さない。
        var roleBindingRecordingTunerNamesByGroup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var configuredTunerNameToPrioritySlotName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in new[] { "GR", "BSCS" })
        {
            var names = new List<string>();
            var ordinal = 0;
            foreach (var profile in recordingTunerProfiles.Where(p => SupportsReservationGroup(p.Group, g)))
            {
                ordinal++;
                var displayName = TunerDisplayName.PrioritySlotName(g, ordinal);
                names.Add(displayName);
                var configuredName = (profile.Name ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(configuredName)) configuredTunerNameToPrioritySlotName[configuredName] = displayName;
                var did = (profile.Did ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(did)) configuredTunerNameToPrioritySlotName[$"{TunerDisplayName.GroupLabel(profile.Group)}-{did.ToUpperInvariant()}"] = displayName;
            }
            roleBindingRecordingTunerNamesByGroup[g] = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

            if (configuredTunerNameToPrioritySlotName.TryGetValue(value, out var priorityName))
                return priorityName;

            // 旧予約などが保持する名前が現在の優先度表に無い場合だけ原値を残す。
            // 現在設定に存在する物理名/過去の論理名は上で T/S の優先度名へ戻す。
            return value;
        }

        // 評価対象：scheduledのユーザー予約のみ
        var allScheduled = GetByStatus(ReservationStatus.Scheduled).ToList();
        var scheduled    = allScheduled
            .Where(r => r.Status == ReservationStatus.Scheduled)
            .Where(r => r.Source != ReservationSource.Epg)
            .Where(r => r.IsEnabled)
            .Where(r => !IsManualStoppedOccurrenceSuppressed(r))
            .ToList();

        // release_contract: 空欄ProgramRuleと同一局・同一時刻の正規イベント予約が存在する場合のみ、空欄側を自動削除する。
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

        // CHAIN_ALLOCATION_INVARIANT:
        // 物理解放後から終端確定までの前段はStoppingだが、明示チェーンの占有単位と確定Tunerを保持するアンカーである。
        // Stoppingを評価対象外にすると後続がchain_broken_predecessorとなり、単体予約への再配置で同一Tuner契約が壊れる。
        // この状態を通常予約へフォールバックさせたり、別Tunerへ再割当したりしてはならない。
        var stoppingReservations = GetByStatus(ReservationStatus.Stopping)
            .Where(r => r.Source != ReservationSource.Epg)
            .Where(r => r.IsEnabled)
            .ToList();
        var stoppingReservationIds = stoppingReservations.Select(r => r.Id).ToHashSet();

        // チェーンhandoff直後は、前番組が既にCompletedへ移行しているため
        // scheduled + recording だけを見ると UserChainPreviousId の参照先が評価対象外になり、
        // 後続チェーンの TunerName が空に戻って競合化する。
        // そのため、明示チェーンの直前予約は Completed でも境界再試行中の「hard pinチェーンアンカー」として扱い、
        // 直前予約の実チューナーを後続へ必ず継承する。弱い希望や通常再配置へ落としてはならない。
        var completedChainAnchorById = new Dictionary<int, Reservation>();
        var completedHandoffAuditAt = DateTime.Now;
        foreach (var forced in scheduled.Concat(recordingReservations).Concat(stoppingReservations)
            .Where(r => r.IsUserChain && r.UserChainPreviousId.HasValue)
            .GroupBy(r => r.Id)
            .Select(g => g.First()))
        {
            var pred = GetById(forced.UserChainPreviousId!.Value);
            var handoffWindowStart = forced.StartTime.AddSeconds(-CompletedHandoffAnchorFrontCutSeconds);
            var handoffWindowEnd = forced.EndTime;
            var handoffWindowOpen = completedHandoffAuditAt >= handoffWindowStart
                && completedHandoffAuditAt < handoffWindowEnd;
            if (pred != null
                && pred.Source != ReservationSource.Epg
                && pred.IsEnabled
                && pred.Status == ReservationStatus.Completed
                && handoffWindowOpen
                && !string.IsNullOrWhiteSpace(pred.ActualTunerName)
                && IsExactContinuousPair(pred, forced))
            {
                completedChainAnchorById[pred.Id] = pred;
            }
            else if (pred?.Status == ReservationStatus.Completed && !handoffWindowOpen)
            {
                log.Add("CHAIN_HANDOFF_ANCHOR", $"R{pred.Id}",
                    $"result=INACTIVE successor=R{forced.Id} now={completedHandoffAuditAt:MM/dd HH:mm:ss} boundaryStart={handoffWindowStart:MM/dd HH:mm:ss} successorEnd={handoffWindowEnd:MM/dd HH:mm:ss} action=do_not_hold_completed_anchor_outside_successor_execution_window rule=release_contract");
            }
            else if (pred?.Status == ReservationStatus.Completed
                && handoffWindowOpen
                && string.IsNullOrWhiteSpace(pred.ActualTunerName))
            {
                log.Add("CHAIN_HANDOFF_ANCHOR", $"R{pred.Id}",
                    $"result=REJECTED successor=R{forced.Id} reason=missing_predecessor_actual_tuner action=do_not_fallback_to_planned_tuner rule=chain_same_physical_tuner_contract");
            }
        }
        var completedChainAnchorIds = completedChainAnchorById.Keys.ToHashSet();

        // CHAIN_TUNER_ASSIGNMENT_PHASE_INVARIANT:
        // まだ前段の実録画Tunerが存在しない予約作成・通常計画段階ではLogicalTunerDisplayNameを候補にする。
        // ただし前段がRecording／Stopping／境界直後Completedになった時点ではActualTunerNameが正本となり、
        // 後続を同一物理Tunerへhard pinする。計画段階の説明をhandoff実行段階へ適用してはならない。
        // release_contract: チェーンは「後番組優先ON＋チェーン録画ON」を利用条件にしたうえで、
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
                    $"result=IGNORED_AT_ALLOC_ROUTE reason=invalid_user_chain_contract predecessor={predecessorLabel} sameSid={sameSid} gapSec={gapText} rule=release_contract");
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
        var chainPredecessors = new Dictionary<int, int>(); // key=後続ID, value=前番組ID
        var chainSuccessors = new Dictionary<int, int>();   // key=前番組ID, value=後続ID
        var invalidChainReservationIds = new HashSet<int>();

        // CHAIN_TOPOLOGY_SINGLE_SOURCE_INVARIANT:
        // 割当候補の状態集合からチェーン構造を作り直さない。
        // チェーンボタンで保存された IsUserChain / UserChainPreviousId / UserChainRootId を唯一の正本とし、
        // Scheduled / Recording / Stopping / Completed の遷移はトポロジー変更として扱わない。
        var allChainReservationsById = GetAll()
            .Where(r => r.Source != ReservationSource.Epg)
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var activeChainSuccessorStatuses = new HashSet<ReservationStatus>
        {
            ReservationStatus.Scheduled,
            ReservationStatus.Starting,
            ReservationStatus.Recording,
            ReservationStatus.Stopping
        };

        if (pseudoContinuous)
        {
            var forcedCandidates = allChainReservationsById.Values
                .Where(r => r.IsEnabled)
                .Where(r => r.IsUserChain && r.UserChainPreviousId.HasValue)
                .Where(r => activeChainSuccessorStatuses.Contains(r.Status))
                .OrderBy(r => r.StartTime)
                .ThenBy(r => r.Id)
                .ToList();

            foreach (var branch in forcedCandidates
                .GroupBy(r => r.UserChainPreviousId!.Value)
                .Where(g => g.Count() > 1))
            {
                var memberIds = branch.Select(x => x.Id).OrderBy(x => x).ToArray();
                invalidChainReservationIds.UnionWith(memberIds);
                invalidChainReservationIds.Add(branch.Key);
                log.Add("CHAIN_CONTRACT_AUDIT", $"R{branch.Key}",
                    $"result=REJECTED_AT_ALLOC_ROUTE reason=chain_branch_detected predecessor=R{branch.Key} successors={string.Join(',', memberIds.Select(x => $"R{x}"))} rule=release_contract");
            }

            foreach (var forced in forcedCandidates)
            {
                var predId = forced.UserChainPreviousId!.Value;
                if (invalidChainReservationIds.Contains(forced.Id) || invalidChainReservationIds.Contains(predId))
                    continue;

                if (!allChainReservationsById.TryGetValue(predId, out var pred))
                {
                    invalidChainReservationIds.Add(forced.Id);
                    log.Add("CHAIN_CONTRACT_AUDIT", $"R{forced.Id}",
                        $"result=REJECTED_AT_ALLOC_ROUTE reason=chain_broken_predecessor predecessor=R{predId} rule=release_contract");
                    continue;
                }

                if (pred.Status == ReservationStatus.Cancelled)
                {
                    invalidChainReservationIds.Add(forced.Id);
                    log.Add("CHAIN_CONTRACT_AUDIT", $"R{forced.Id}",
                        $"result=REJECTED_AT_ALLOC_ROUTE reason=predecessor_cancelled predecessor=R{predId} rule=release_contract");
                    continue;
                }

                if (!IsExactContinuousPair(pred, forced))
                {
                    invalidChainReservationIds.Add(forced.Id);
                    log.Add("CHAIN_CONTRACT_AUDIT", $"R{forced.Id}",
                        $"result=REJECTED_AT_ALLOC_ROUTE reason=not_same_sid_adjacent predecessor=R{predId} prevNid={pred.NetworkId} prevTsid={pred.TransportStreamId} prevSid={pred.ServiceId} nextNid={forced.NetworkId} nextTsid={forced.TransportStreamId} nextSid={forced.ServiceId} gapSec={(int)Math.Round((forced.StartTime - pred.EndTime).TotalSeconds)} rule=release_contract");
                    continue;
                }

                chainPredecessors[forced.Id] = predId;
            }

            foreach (var branch in chainPredecessors.GroupBy(kv => kv.Value).Where(g => g.Count() > 1))
            {
                foreach (var member in branch)
                    invalidChainReservationIds.Add(member.Key);
            }

            foreach (var successorId in chainPredecessors.Keys.ToList())
            {
                if (!allChainReservationsById.TryGetValue(successorId, out var successor))
                    continue;

                // 完了済み先祖をactive mapから落としてRootを付け替える旧誤りを禁止する。
                // 永続化された全状態のpredecessorリンクで検証し、active mapは今回実行するedge選択だけに使う。
                if (TryResolveCanonicalStoredChainRoot(successor, allChainReservationsById,
                        out var canonicalRootId, out var invalidReason, out var depth))
                    continue;

                invalidChainReservationIds.Add(successorId);
                log.Add("CHAIN_CONTRACT_AUDIT", $"R{successorId}",
                    $"result=REJECTED_AT_ALLOC_ROUTE reason={invalidReason} storedRoot=R{(successor.UserChainRootId.HasValue ? successor.UserChainRootId.Value.ToString() : "-")} canonicalRoot=R{canonicalRootId} depth={depth} topologySource=persisted_all_statuses rule=release_contract");
            }

            if (invalidChainReservationIds.Count > 0)
            {
                foreach (var invalidId in invalidChainReservationIds)
                    chainPredecessors.Remove(invalidId);
            }

            chainSuccessors = chainPredecessors
                .Where(kv => !invalidChainReservationIds.Contains(kv.Value))
                .ToDictionary(kv => kv.Value, kv => kv.Key);
        }

        // EPGエントリのis_conflictedは常にfalse。Final Planと同じTransactionで確定する。

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

        int PriorityRuleOrder(Reservation reservation)
        {
            if (IsAutoSearchSource(reservation))
                return reservation.SourceRuleId.HasValue && keywordRuleSortOrderById.TryGetValue(reservation.SourceRuleId.Value, out var keywordOrder)
                    ? keywordOrder
                    : int.MaxValue;
            if (IsProgramReservationSource(reservation))
                return reservation.SourceRuleId.HasValue && programRuleOrderById.TryGetValue(reservation.SourceRuleId.Value, out var programOrder)
                    ? programOrder
                    : int.MaxValue;
            return int.MaxValue;
        }

        int CompareReservationPriorityCore(
            Reservation a,
            Reservation b,
            DateTime aDecisionTime,
            DateTime bDecisionTime,
            int aStableId,
            int bStableId,
            bool adjacentBoundaryEligible)
        {
            // RESERVATION_PRIORITY_SINGLE_SOURCE_INVARIANT:
            // 予約種別、ルール順、同一サービス境界、ユーザー能動操作時刻による勝敗はここだけで決める。
            // チェーンであることを外部予約への優先度へ加点してはならない。
            var source = SourcePriorityRank(a).CompareTo(SourcePriorityRank(b));
            if (source != 0) return source;

            if (IsAutoSearchSource(a) && IsAutoSearchSource(b))
            {
                var aOrder = PriorityRuleOrder(a);
                var bOrder = PriorityRuleOrder(b);
                if (aOrder != bOrder)
                    return bOrder.CompareTo(aOrder); // sort_order が小さいルールを優先
            }

            if (IsProgramReservationSource(a) && IsProgramReservationSource(b))
            {
                var aOrder = PriorityRuleOrder(a);
                var bOrder = PriorityRuleOrder(b);
                if (aOrder != bOrder)
                    return bOrder.CompareTo(aOrder); // 一覧上位（小さい順）を優先
            }

            // 前番組優先/後番組優先は、同一サービスの隣接境界で勝敗方向を決めるだけ。
            // 前番組末尾30秒の欠落や通常マージン短縮には使用しない。
            if (adjacentBoundaryEligible
                && IsSameService(a, b)
                && a.StartTime != b.StartTime
                && (a.EndTime == b.StartTime || b.EndTime == a.StartTime))
            {
                var aIsLater = a.StartTime > b.StartTime;
                if (laterProgramPriority)
                    return aIsLater ? 1 : -1;
                return aIsLater ? -1 : 1;
            }

            var decision = aDecisionTime.CompareTo(bDecisionTime);
            if (decision != 0) return decision; // ユーザー能動予約同士を含め、後から確定したものを優先

            return aStableId.CompareTo(bStableId);
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
#if TVAIR_DEVELOPER_DIAGNOSTICS
        var debugTrace = new List<TunerAllocationDebugTraceEntry>();
#else
        List<TunerAllocationDebugTraceEntry>? debugTrace = null;
#endif
        // チェーン占有終了調整用（ループ内DBアクセス回避）
        var scheduledById = scheduled.ToDictionary(s => s.Id);

        // USER_CHAIN_PHYSICAL_SLOT_SOURCE_INVARIANT — 変更禁止:
        // 物理Tuner所有はchain root単位、競合採否は予約イベント単位である。
        // 将来予約のRoot/TunerNameは直前FinalConflictPlanの弱い安定候補であり、hard pinではない。
        // hard pinにできるのはRecording/Stopping/境界直後CompletedのActualTunerNameだけである。
        // FinalConflictPlanはイベント評価へ入る前に、全chain rootを共通物理時間軸上へ一度だけ配置し、
        // 同一rootの各イベントが処理順や再計算passによって別Tunerを選び直すことを禁止する。
        var chainPlannedTunerPreferenceByRoot = new Dictionary<int, string>();
        // CHAIN_SLOT_SINGLE_SOURCE:
        // 今回のFinalConflictPlanで解決したroot単位の物理Tuner正本。
        // 最終投影・PreRecEpg・Wake・実行handoffは、このroot単位Mapから投影されたTunerNameを参照する。
        var chainResolvedTunerByRoot = new Dictionary<int, string>();
        foreach (var chainGroup in scheduled
            .Where(x => chainModeEnabled && (x.IsUserChain || chainSuccessors.ContainsKey(x.Id)))
            .GroupBy(ResolveStoredChainRootId))
        {
            var rootId = chainGroup.Key;
            var rootReservation = GetById(rootId) ?? chainGroup.OrderBy(x => x.StartTime).ThenBy(x => x.Id).First();
            var fixedTuner = ToRoleBindingTunerName(EffectiveTunerName(rootReservation), ResolveGroup(rootReservation));
            if (string.IsNullOrWhiteSpace(fixedTuner) || fixedTuner == "-")
            {
                fixedTuner = chainGroup
                    .OrderBy(x => x.StartTime)
                    .ThenBy(x => x.Id)
                    .Select(x => ToRoleBindingTunerName(EffectiveTunerName(x), ResolveGroup(x)))
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "-") ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(fixedTuner) && fixedTuner != "-")
            {
                chainPlannedTunerPreferenceByRoot[rootId] = fixedTuner;
                AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "CHAIN_PLANNED_TUNER_PREFERENCE", rootReservation,
                    $"chainRoot=R{rootId} preferredTuner={fixedTuner} source=previous_final_assignment strength=weak_reselectable action=root_preplan_capacity_check rule=chain_fixed_physical_tuner_contract");
            }
            else
            {
                AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "CHAIN_PLANNED_TUNER_PREFERENCE", rootReservation,
                    $"chainRoot=R{rootId} preferredTuner=- source=none strength=weak_reselectable action=root_preplan_select rule=chain_fixed_physical_tuner_contract");
            }
        }

        foreach (var locked in userChainLockedTunerById.OrderBy(x => x.Key))
        {
            var lockedReservation = scheduledById.TryGetValue(locked.Key, out var lr) ? lr : GetById(locked.Key);
            if (lockedReservation != null)
                AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "CHAIN_GROUP_LOCK_REBUILD", lockedReservation, $"id=R{locked.Key} lockTuner={locked.Value} source=db_tuner_before_reallocation");
        }

        int ResolveStoredChainRootId(Reservation reservation)
        {
            if (reservation.UserChainRootId.HasValue)
                return reservation.UserChainRootId.Value;
            // 先頭予約だけはroot列を持たず自身がRoot。後続のmissing rootをpredecessorで補完してはならない。
            return reservation.Id;
        }

        // CHAIN_EXECUTING_TUNER_INVARIANT:
        // 録画開始後の残存チェーンは、現在段のActualTunerまたはhandoff証拠へhard pinする。
        // ただし外部予約との採否は通常の共通優先計算で再評価し、チェーンであることを優先度には使わない。
        var chainActivePinnedTunersByRoot = recordingReservations
            .Concat(stoppingReservations)
            .Concat(completedChainAnchorById.Values)
            .Where(r => r.IsUserChain || chainSuccessors.ContainsKey(r.Id) || chainPredecessors.ContainsKey(r.Id))
            .Select(r => new
            {
                RootId = ResolveStoredChainRootId(r),
                Tuner = ToRoleBindingTunerName(r.ActualTunerName, ResolveGroup(r))
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Tuner) && x.Tuner != "-")
            .GroupBy(x => x.RootId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Tuner).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        List<ConflictOccupancyUnit> BuildConflictOccupancyUnitsForGroup(string groupKey, IReadOnlyList<Reservation> groupReservations)
        {
            var units = new List<ConflictOccupancyUnit>();
            var groupedReservationIds = new HashSet<int>();
            var nextUnitId = 1;

            ConflictOccupancyUnit CreateUnit(List<Reservation> members, bool isUserChain, int? chainRootId)
            {
                var ordered = members
                    .OrderBy(x => x.StartTime)
                    .ThenBy(x => x.Id)
                    .ToList();
                var head = ordered.First();
                var resolvedChainRootId = isUserChain ? (chainRootId ?? ResolveStoredChainRootId(head)) : (int?)null;
                var recordingMembers = ordered
                    .Where(x => x.Status == ReservationStatus.Recording)
                    .ToList();
                var handoffAnchorMembers = ordered
                    .Where(x => x.Status == ReservationStatus.Stopping && stoppingReservationIds.Contains(x.Id))
                    .ToList();
                var completedHandoffAnchorMembers = ordered
                    .Where(x => x.Status == ReservationStatus.Completed && completedChainAnchorIds.Contains(x.Id))
                    .ToList();
                var activeOrHandoffMembers = recordingMembers
                    .Concat(handoffAnchorMembers)
                    .Concat(completedHandoffAnchorMembers)
                    .GroupBy(x => x.Id)
                    .Select(g => g.First())
                    .ToList();

                // CHAIN_TUNER_PIN_INVARIANT:
                // final planの固定Tunerは、現在の実録画所有またはhandoff中のStopping前段が保持するActualTunerを正本にする。
                // 将来予約の希望Tunerや再計算した空きTunerで、この物理Tuner継承を上書きしてはならない。
                var pinCandidates = new List<string>();
                if (tunerSlots != null)
                {
                    var recordingMemberIds = recordingMembers.Select(x => x.Id).ToHashSet();
                    pinCandidates.AddRange(tunerSlots
                        .Where(slot => slot.UsageKind == Tuner.TunerUsageKind.Recording
                            && slot.ReservationId.HasValue
                            && recordingMemberIds.Contains(slot.ReservationId.Value)
                            && SupportsReservationGroup(slot.Group, groupKey))
                        .Select(slot => ToRoleBindingTunerName(slot.Name, groupKey)));
                }
                pinCandidates.AddRange(recordingMembers
                    .Concat(handoffAnchorMembers)
                    .Select(x => ToRoleBindingTunerName(
                        !string.IsNullOrWhiteSpace(x.ActualTunerName) ? x.ActualTunerName : x.TunerName,
                        groupKey)));
                // Completed handoff anchorは計画Tunerへフォールバックしない。
                // 前段録画で確定したActualTunerNameだけをhard pin証拠として採用する。
                pinCandidates.AddRange(completedHandoffAnchorMembers
                    .Select(x => ToRoleBindingTunerName(x.ActualTunerName, groupKey)));
                if (resolvedChainRootId.HasValue
                    && chainActivePinnedTunersByRoot.TryGetValue(resolvedChainRootId.Value, out var activeRootPins))
                {
                    pinCandidates.AddRange(activeRootPins);
                }

                var distinctPins = pinCandidates
                    .Where(x => !string.IsNullOrWhiteSpace(x) && x != "-")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // CHAIN_COMPLETED_HANDOFF_PIN_INVARIANT:
                // 物理解放後に前段がCompletedへ終端しても、境界後続がScheduledのまま再試行中である限り、
                // completedChainAnchorIdsに入った直前前段は同一物理Tuner継承のhard pinである。
                // ここを弱い希望へ落とすと、前段Completed直後の再評価で別Tunerへ通常再配置されるため禁止する。
                // hard pinの有効範囲は、時間追従後の後続開始30秒前から後続終了までの正確な直接後続だけに限定する。
                // Completed前段ではActualTunerName以外を継承根拠にしてはならない。
                var continuityPreferredTuner = resolvedChainRootId.HasValue
                    && chainActivePinnedTunersByRoot.TryGetValue(resolvedChainRootId.Value, out var rootPins)
                    && rootPins.Count == 1
                        ? rootPins[0]
                        : completedHandoffAnchorMembers
                            .Select(x => ToRoleBindingTunerName(x.ActualTunerName, groupKey))
                            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "-");

                if (string.IsNullOrWhiteSpace(continuityPreferredTuner)
                    && isUserChain
                    && userChainLockedTunerById.TryGetValue(head.Id, out var storedChainTuner))
                {
                    continuityPreferredTuner = storedChainTuner;
                }

                // CHAIN_CONFLICT_FULL_MARGIN_INVARIANT — 変更禁止:
                // 競合の勝敗はチェーン/通常を区別せず、各イベントの開始・終了マージン全量と共通優先順位で行う。
                // チェーンであることを優先加点、容量救済、マージン短縮、敗者救済に使用してはならない。
                // ただし、同じ可動予約を別の空きRecording Tunerへ配置できる場合に固定チェーンTunerを
                // 不必要に選ばないことは優先順位変更ではなく、FinalConflictPlan内の物理Tuner配置規則である。
                // 全候補で同時成立できない場合は、この配置規則で勝敗を覆さず共通優先順位へ戻す。
                // 前番組末尾30秒の欠落は、前後イベントがともに有効・非競合で、保存済みチェーントポロジーと
                // 固定物理Tuner継承が成立し、チェーン境界を正当に実行する場合だけ適用する実行時処理である。
                // それ以外の通常終了、競合落ち、取消、無効化、開始/取得失敗、復旧、容量調整では一切行わない。
                // したがって予約段階では必ず通常の OccupyStart / OccupyEnd を使用する。
                var unitOccupyEnd = ordered.Max(OccupyEnd);

                var hasRootActivePin = resolvedChainRootId.HasValue
                    && chainActivePinnedTunersByRoot.TryGetValue(resolvedChainRootId.Value, out var rootActivePins)
                    && rootActivePins.Count > 0;
                // 保存済みTunerNameはroot preplanの弱い候補としてだけ使用する。
                // active/handoffのActualTunerNameはdistinctPinsに入り、root全体のhard pinになる。
                if (isUserChain
                    && resolvedChainRootId.HasValue
                    && string.IsNullOrWhiteSpace(continuityPreferredTuner)
                    && chainPlannedTunerPreferenceByRoot.TryGetValue(resolvedChainRootId.Value, out var plannedTuner)
                    && !string.IsNullOrWhiteSpace(plannedTuner))
                {
                    continuityPreferredTuner = plannedTuner;
                }

                return new ConflictOccupancyUnit
                {
                    UnitId = nextUnitId++,
                    Reservations = ordered,
                    PriorityReservation = head,
                    Group = groupKey,
                    OccupyStart = ordered.Min(OccupyStart),
                    OccupyEnd = unitOccupyEnd,
                    IsUserChain = isUserChain,
                    ChainRootId = resolvedChainRootId,
                    // 外部予約との優先度は、将来子が実行中チェーンに属することだけでは上げない。
                    // 現在段そのものだけをactive anchorとして扱い、将来子は通常優先計算へ流す。
                    HasActiveAnchor = activeOrHandoffMembers.Count > 0,
                    RequiresPinnedTuner = activeOrHandoffMembers.Count > 0 || hasRootActivePin,
                    HasPinnedTunerConflict = distinctPins.Count > 1,
                    PinnedTuner = distinctPins.Count == 1 ? distinctPins[0] : null,
                    ContinuityPreferredTuner = continuityPreferredTuner,
                    PinCandidates = distinctPins.Count == 0 ? "-" : string.Join(",", distinctPins)
                };
            }

            // CHAIN_EVENT_UNIT_INVARIANT:
            // 外部予約との共通競合は、チェーン全体を一つの長大な占有区間へ潰さず、番組単位で再計算する。
            // チェーンの特殊性は、同一Tuner継承と内部境界の前段終了調整だけに限定する。
            // 最初に競合した子以降の伝播はFinal Plan後の可逆な派生結果として扱い、DBトポロジーは変更しない。
            foreach (var r in groupReservations.OrderBy(r => r.StartTime).ThenBy(r => r.Id))
            {
                var isChainMember = pseudoContinuous
                    && (chainPredecessors.ContainsKey(r.Id) || chainSuccessors.ContainsKey(r.Id));
                var rootId = isChainMember ? ResolveStoredChainRootId(r) : (int?)null;
                units.Add(CreateUnit(new List<Reservation> { r }, isChainMember, rootId));
                groupedReservationIds.Add(r.Id);
            }

            return units
                .GroupBy(u => u.UnitKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(u => u.OccupyStart)
                .ThenBy(u => u.PriorityReservation.StartTime)
                .ThenBy(u => u.PriorityReservation.Id)
                .ToList();
        }

        var allConflictOccupancyUnits = new List<ConflictOccupancyUnit>();

        // FINAL_CONFLICT_PLAN_SINGLE_SOURCE_INVARIANT — 変更禁止:
        // ConflictOccupancyUnitの構築と監査表示だけをここで行う。
        // 旧occupancy mirrorでassigned/conflictedを先に決め、その後Final Planで全量上書きする
        // 二段判定は禁止する。イベント単位の共通優先順位、チェーン固定物理Tuner、
        // 最初の競合イベント以降の伝播はReconcileFinalConflictPlanByUnitKey()だけが確定する。
        foreach (var grpKey in scheduled.GroupBy(r => ResolveGroup(r)).Select(g => g.Key).Distinct())
        {
            var recordingCapacityTuners = roleBindingRecordingTunerNamesByGroup.TryGetValue(grpKey, out var capacityTunerList)
                ? string.Join(",", capacityTunerList)
                : "-";
            var inGroup = scheduled
                .Where(r => ResolveGroup(r) == grpKey)
                .OrderBy(r => r.StartTime)
                .ThenBy(r => r.Id)
                .ToList();

            var occupancyUnits = BuildConflictOccupancyUnitsForGroup(grpKey, inGroup);
            allConflictOccupancyUnits.AddRange(occupancyUnits);
            var representative = occupancyUnits.FirstOrDefault()?.PriorityReservation
                ?? new Reservation { Id = 0, Title = "FinalConflictPlan" };
            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "OCCUPANCY_UNIT_PLAN_SUMMARY", representative,
                $"stage=occupancy_units_built group={grpKey} units={occupancyUnits.Count} recordingTuners={recordingCapacityTuners} displaySource=LogicalTunerDisplayName source=final_conflict_plan_single_source result={(occupancyUnits.Count == 0 ? "EMPTY" : "VISIBLE")} rule=release_contract");
            foreach (var unit in occupancyUnits)
            {
                AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, unit.IsUserChain ? "CHAIN_OCCUPANCY_UNIT" : "OCCUPANCY_UNIT", unit.PriorityReservation,
                    $"unit={unit.UnitId} members={unit.MemberIds} chainRoot=R{(unit.ChainRootId.HasValue ? unit.ChainRootId.Value.ToString() : "-")} cost=1 occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} capacitySource=RoleBinding/Recording recordingTuners={recordingCapacityTuners} displaySource=LogicalTunerDisplayName decisionOwner=FinalConflictPlan rule=release_contract");
            }
        }

        // チューナー名の割り当て（assignedIdsに含まれる予約に LogicalTunerDisplayName を割り当てる）
        // LogicalTunerDisplayName は優先順位用であり、LogicalTunerIdentity を未来日予約へ固定しない。
        // FINAL_TUNER_ASSIGNMENT_SINGLE_SOURCE_INVARIANT — 変更禁止:
        // 物理Tunerの最終選択、チェーン固定Tunerの容量判定、通常予約とのイベント単位競合、
        // 競合開始地点以降の伝播、競合解除時の同一固定Tuner復帰は、
        // ReconcileFinalConflictPlanByUnitKey() だけが決定する。
        // 可動予約が別の空きTunerを使える場合の固定チェーンTuner回避もFinalConflictPlan内部で
        // 全Unit・共通優先順位・代替候補を参照して決定し、代替不能時は全候補へ戻す。
        // 最終競合状態はConflictOccupancyUnit(UnitKey)を正本として一度だけ合算し、
        // active recording / active chain / normal reservationを同じUnitKeyタイムラインで評価する。
        var tunerAssignment = new Dictionary<int, string>(); // Final Plan projection only
        var finalConflictBlockersByReservationId = new Dictionary<int, string>();
        // NORMAL_EPG_BLOCKER_EVIDENCE_INVARIANT:
        // 録画開始側は「同じ波にEPGがいる」ことから競合原因を再推測しない。
        // FinalConflictPlanでnormal EPG intervalだけを除外すれば成立することを確認できた予約だけ、
        // 現在のRunGeneration + OccupationRevisionを因果証拠として保持する。
        // 第2passのチェーン伝播では第1passの直接証拠を維持し、commit成功時にfinal conflicted集合へ絞って全置換する。
        var normalEpgBlockerEvidence = new Dictionary<int, NormalEpgConflictBlockerEvidence>();

        void ReconcileFinalConflictPlanByUnitKey(IReadOnlySet<int>? forcedChainConflictIds = null)
        {
            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_CONFLICT_PLAN_BEGIN", new Reservation { Id = 0, Title = "FinalConflictPlan" },
                $"scheduled={scheduled.Count} recording={recordingReservations.Count} occupancyUnits={allConflictOccupancyUnits.Count} groups={string.Join(',', allConflictOccupancyUnits.Select(u => u.Group).Distinct(StringComparer.OrdinalIgnoreCase))} rule=release_contract");
            var evaluatedIds = scheduled.Select(x => x.Id).ToHashSet();
            if (forcedChainConflictIds is not null)
            {
                // 第2passで通常再評価される予約は、第1passの因果証拠を持ち越さない。
                // forced chain propagation対象だけは、最初の直接競合headから引き継いだ証拠を維持する。
                foreach (var id in evaluatedIds.Where(id => !forcedChainConflictIds.Contains(id)))
                    normalEpgBlockerEvidence.Remove(id);
            }
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
            var unitByMemberId = unitsByKey
                .SelectMany(unit => unit.Reservations.Select(member => (member.Id, Unit: unit)))
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First().Unit);

            // 各passでroot物理枠を全量再構築する。前passの部分結果を再利用してはならない。
            chainResolvedTunerByRoot.Clear();

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
                            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_UNIT_CONFLICT", member,
                                $"unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} reason=no_candidate_tuner_for_group group={grpKey} rule=release_contract");
                        }
                    }
                    continue;
                }

                // 通常EPGはFinalConflictPlanの外部intervalとして理由を保持する。
                // active recording等と一つのfinalFreeAtへ潰すと、後段で「EPGだけ除けば成立するか」を判定できないため、
                // normal_epg_wave intervalは別に保持し、Process/Leaseの物理正本自体は変更しない。
                NormalEpgWaveOccupationSnapshot? epgOccupationForGroup = null;
                var normalEpgIntervals = new List<(string TunerName, DateTime Start, DateTime End)>();
                if (normalEpgWaveOccupation.TryGet(grpKey, out var epgOccupation))
                {
                    epgOccupationForGroup = epgOccupation;
                    var mappedTailTuners = epgOccupation.PhysicalTailOnly
                        ? epgOccupation.PhysicalTailTuners
                            .Select(name => ToRoleBindingTunerName(name, grpKey))
                            .Where(name => !string.IsNullOrWhiteSpace(name) && finalFreeAt.ContainsKey(name))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                        : Array.Empty<string>();
                    var tailMappingComplete = !epgOccupation.PhysicalTailOnly
                        || mappedTailTuners.Length == epgOccupation.PhysicalTailTuners.Count;
                    var occupiedTuners = epgOccupation.PhysicalTailOnly && tailMappingComplete && mappedTailTuners.Length > 0
                        ? mappedTailTuners
                        : candidateNames.ToArray();
                    var effectiveEpgEnd = epgOccupation.PhysicalTailOnly
                        ? DateTime.MaxValue
                        : (epgOccupation.PlannedEndAt > DateTime.Now ? epgOccupation.PlannedEndAt : DateTime.Now);
                    foreach (var tunerName in occupiedTuners)
                        normalEpgIntervals.Add((tunerName, epgOccupation.StartedAt, effectiveEpgEnd));
                    AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_EPG_WAVE_OCCUPY", new Reservation { Id = 0, Title = "NormalEpgWaveOccupation" },
                        $"group={grpKey} tuners={string.Join(",", occupiedTuners)} mode={(epgOccupation.PhysicalTailOnly ? "physical_tail" : "wave_wide")} plannedEnd={epgOccupation.PlannedEndAt:MM/dd HH:mm:ss} activeUntilClear=True source={epgOccupation.Source} silent={epgOccupation.Silent} runGeneration={epgOccupation.RunGeneration} revision={epgOccupation.Revision} tailMappingComplete={tailMappingComplete} physicalLeaseMutation=False rule=normal_epg_wave_occupation_contract");
                }

                if (tunerSlots != null)
                {
                    foreach (var slot in tunerSlots.Where(s =>
                        s.UsageKind == Tuner.TunerUsageKind.Recording
                        && s.PlannedEndTime.HasValue
                        && !string.IsNullOrEmpty(s.Name)
                        && SupportsReservationGroup(s.Group, grpKey)))
                    {
                        var virtualName = ToRoleBindingTunerName(slot.Name, grpKey);
                        if (slot.ReservationId.HasValue
                            && unitByMemberId.TryGetValue(slot.ReservationId.Value, out var ownedUnit))
                        {
                            if (!string.IsNullOrWhiteSpace(ownedUnit.PinnedTuner)
                                && string.Equals(ownedUnit.PinnedTuner, virtualName, StringComparison.OrdinalIgnoreCase))
                            {
                                // 同一実録画占有をUnitと外部占有へ二重計上しない。
                                continue;
                            }

                            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_ACTIVE_RECORDING_IDENTITY_MISMATCH", ownedUnit.PriorityReservation,
                                $"unitKey={ownedUnit.UnitKey} member=R{slot.ReservationId.Value} poolTuner={virtualName} pinnedTuner={(ownedUnit.PinnedTuner ?? "-")} pinCandidates={ownedUnit.PinCandidates} reason=pool_slot_does_not_match_unit_pin rule=release_contract");
                        }
                        if (!finalFreeAt.ContainsKey(virtualName)) continue;
                        var externalOccupyEnd = slot.ReservationId.HasValue
                            && chainHandoffByReservationId.TryGetValue(slot.ReservationId.Value, out var chainHandoffAt)
                                ? chainHandoffAt
                                : slot.PlannedEndTime!.Value;
                        var current = finalFreeAt[virtualName];
                        if (externalOccupyEnd > current)
                            finalFreeAt[virtualName] = externalOccupyEnd;
                        AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_UNIT_EXTERNAL_OCCUPY", new Reservation { Id = slot.ReservationId ?? 0, Title = "ExternalOrActiveRecording" },
                            $"group={grpKey} tuner={virtualName} reservation=R{(slot.ReservationId.HasValue ? slot.ReservationId.Value.ToString() : "-")} occupyUntil={externalOccupyEnd:MM/dd HH:mm:ss} reason={(slot.ReservationId.HasValue && chainHandoffByReservationId.ContainsKey(slot.ReservationId.Value) ? "active_chain_handoff_boundary" : "active_recording_outside_unit")} rule=release_contract");
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
                        var recEnd = chainHandoffByReservationId.TryGetValue(rec.Id, out var chainHandoffAt)
                            ? chainHandoffAt
                            : rec.EndTime + postMargin;
                        if (recEnd > finalFreeAt[virtualName])
                            finalFreeAt[virtualName] = recEnd;
                        AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_UNIT_EXTERNAL_OCCUPY", rec,
                            $"group={grpKey} tuner={virtualName} reservation=R{rec.Id} occupyUntil={recEnd:MM/dd HH:mm:ss} reason={(chainHandoffByReservationId.ContainsKey(rec.Id) ? "active_chain_handoff_boundary" : "db_recording_outside_unit")} rule=release_contract");
                    }
                }

                bool UnitOverlaps(ConflictOccupancyUnit a, ConflictOccupancyUnit b)
                    => a.OccupyStart < b.OccupyEnd && b.OccupyStart < a.OccupyEnd;

                bool IntervalOverlaps(DateTime start, DateTime end, DateTime otherStart, DateTime otherEnd)
                    => start < otherEnd && otherStart < end;

                DateTime UnitUserDecisionTime(ConflictOccupancyUnit unit)
                    => unit.Reservations.Count == 0 ? DateTime.MinValue : unit.Reservations.Max(r => r.CreatedAt);

                int CompareFinalUnitPriority(ConflictOccupancyUnit a, ConflictOccupancyUnit b)
                {
                    // 録画中・handoff中の実体は移動できないため、通常の予約優先順位より先に固定する。
                    if (a.HasActiveAnchor != b.HasActiveAnchor)
                        return a.HasActiveAnchor ? 1 : -1;

                    return CompareReservationPriorityCore(
                        a.PriorityReservation,
                        b.PriorityReservation,
                        UnitUserDecisionTime(a),
                        UnitUserDecisionTime(b),
                        a.UnitId,
                        b.UnitId,
                        adjacentBoundaryEligible: UnitOverlaps(a, b));
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
                foreach (var epgInterval in normalEpgIntervals)
                {
                    if (finalIntervals.TryGetValue(epgInterval.TunerName, out var intervals))
                        intervals.Add((epgInterval.Start, epgInterval.End, null, "normal_epg_wave"));
                }

                // CHAIN_ROOT_PHYSICAL_SLOT_PREPLAN_INVARIANT — 変更禁止:
                // root物理枠はイベント処理順より前に一度だけ決める。競合判定用UnitKeyはイベント単位のまま維持し、
                // 物理Tunerだけをroot単位で共有する。これにより途中子だけの競合を保ちながら、同一rootの
                // T1→T2→T3分裂、別rootの同一Tuner重複、single-flight再計算順による揺れを同時に禁止する。
                var finalChainTunerByRoot = chainResolvedTunerByRoot;
                var chainRootClaims = new List<(int RootId, string TunerName, DateTime Start, DateTime End)>();
                var chainRootPlans = unitsByKey
                    .Where(u => string.Equals(u.Group, grpKey, StringComparison.OrdinalIgnoreCase)
                        && u.IsUserChain
                        && u.ChainRootId.HasValue
                        && (forcedChainConflictIds == null || !u.Reservations.Any(m => forcedChainConflictIds.Contains(m.Id))))
                    .GroupBy(u => u.ChainRootId!.Value)
                    .Select(g =>
                    {
                        var members = g.OrderBy(u => u.OccupyStart).ThenBy(u => u.PriorityReservation.Id).ToList();
                        var representative = members
                            .OrderBy(u => u, Comparer<ConflictOccupancyUnit>.Create((a, b) => -CompareFinalUnitPriority(a, b)))
                            .ThenBy(u => u.OccupyStart)
                            .ThenBy(u => u.PriorityReservation.Id)
                            .First();
                        var activePins = members
                            .Where(u => u.RequiresPinnedTuner && !string.IsNullOrWhiteSpace(u.PinnedTuner))
                            .Select(u => u.PinnedTuner!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        return new
                        {
                            RootId = g.Key,
                            Members = members,
                            Representative = representative,
                            Start = members.Min(u => u.OccupyStart),
                            End = members.Max(u => u.OccupyEnd),
                            ActivePins = activePins,
                            HasInvalidActivePin = members.Any(u => u.RequiresPinnedTuner && (u.HasPinnedTunerConflict || string.IsNullOrWhiteSpace(u.PinnedTuner))) || activePins.Count > 1
                        };
                    })
                    .OrderByDescending(p => p.ActivePins.Count == 1)
                    .ThenBy(p => p.Representative, Comparer<ConflictOccupancyUnit>.Create((a, b) => -CompareFinalUnitPriority(a, b)))
                    .ThenBy(p => p.Start)
                    .ThenBy(p => p.RootId)
                    .ToList();

                bool RootTunerAvailable(string tunerName, DateTime start, DateTime end)
                    => candidateNames.Contains(tunerName, StringComparer.OrdinalIgnoreCase)
                       && finalIntervals.TryGetValue(tunerName, out var intervals)
                       && !intervals.Any(x => IntervalOverlaps(start, end, x.Start, x.End))
                       && !chainRootClaims.Any(x => string.Equals(x.TunerName, tunerName, StringComparison.OrdinalIgnoreCase)
                           && IntervalOverlaps(start, end, x.Start, x.End));

                foreach (var plan in chainRootPlans)
                {
                    if (plan.HasInvalidActivePin)
                    {
                        AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "CHAIN_ROOT_SLOT_PREPLAN_INVALID_PIN", plan.Representative.PriorityReservation,
                            $"chainRoot=R{plan.RootId} activePins=[{string.Join(',', plan.ActivePins)}] occupy={plan.Start:MM/dd HH:mm:ss}〜{plan.End:MM/dd HH:mm:ss} action=event_conflict_no_reassignment rule=chain_fixed_physical_tuner_contract");
                        continue;
                    }

                    string? selected = null;
                    var source = "none";
                    if (plan.ActivePins.Count == 1)
                    {
                        selected = plan.ActivePins[0];
                        source = "active_actual_tuner_hard_pin";
                        if (!RootTunerAvailable(selected, plan.Start, plan.End))
                        {
                            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "CHAIN_ROOT_SLOT_PREPLAN_BLOCKED", plan.Representative.PriorityReservation,
                                $"chainRoot=R{plan.RootId} requiredTuner={selected} source={source} occupy={plan.Start:MM/dd HH:mm:ss}〜{plan.End:MM/dd HH:mm:ss} action=event_conflict_no_reassignment rule=chain_fixed_physical_tuner_contract");
                            continue;
                        }
                    }
                    else if (chainPlannedTunerPreferenceByRoot.TryGetValue(plan.RootId, out var preferredRootTuner)
                        && RootTunerAvailable(preferredRootTuner, plan.Start, plan.End))
                    {
                        selected = preferredRootTuner;
                        source = "previous_final_assignment_weak_reused";
                    }
                    else
                    {
                        selected = candidateNames
                            .Where(name => RootTunerAvailable(name, plan.Start, plan.End))
                            .OrderBy(name => chainRootClaims
                                .Where(x => string.Equals(x.TunerName, name, StringComparison.OrdinalIgnoreCase) && x.End <= plan.Start)
                                .Select(x => x.End)
                                .DefaultIfEmpty(DateTime.MinValue)
                                .Max())
                            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
                        source = "common_root_timeline_selection";
                    }

                    if (string.IsNullOrWhiteSpace(selected))
                    {
                        AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "CHAIN_ROOT_SLOT_PREPLAN_UNAVAILABLE", plan.Representative.PriorityReservation,
                            $"chainRoot=R{plan.RootId} occupy={plan.Start:MM/dd HH:mm:ss}〜{plan.End:MM/dd HH:mm:ss} candidates={recordingCapacityTuners} action=event_priority_conflict_without_mid_chain_switch rule=chain_fixed_physical_tuner_contract");
                        continue;
                    }

                    finalChainTunerByRoot[plan.RootId] = selected;
                    chainRootClaims.Add((plan.RootId, selected, plan.Start, plan.End));
                    AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "CHAIN_ROOT_SLOT_PREPLAN", plan.Representative.PriorityReservation,
                        $"chainRoot=R{plan.RootId} tuner={selected} source={source} occupy={plan.Start:MM/dd HH:mm:ss}〜{plan.End:MM/dd HH:mm:ss} members=[{string.Join(',', plan.Members.SelectMany(u => u.Reservations).Select(r => $"R{r.Id}").Distinct())}] result=COMMITTED_BEFORE_EVENT_PLAN rule=chain_fixed_physical_tuner_contract");
                }

                // FINAL_TUNER_PLACEMENT_CHAIN_AVOIDANCE_INVARIANT:
                // これはチェーンへの優先加点・絶対保護ではなく、可動な非チェーン予約の物理Tuner配置規則である。
                // 固定チェーン区間と重なるTunerを選ばなくても同じ予約を成立させられる場合は、
                // その非置換候補だけから選び、空いている別Tunerを使わず固定枠を崩す配置を禁止する。
                // 非置換候補が一つも無い場合は固定Tunerを候補から封鎖せず全候補へ戻し、
                // 共通優先順位どおり通常予約が勝てる状態を維持する。
                // この判断はFinalConflictPlan内部だけで行い、チェーン側の優先順位は変更しない。
                var fixedChainPlacementClaims = unitsByKey
                    .Where(u => string.Equals(u.Group, grpKey, StringComparison.OrdinalIgnoreCase)
                        && u.IsUserChain
                        && (forcedChainConflictIds == null || !u.Reservations.Any(m => forcedChainConflictIds.Contains(m.Id)))
                        && u.ChainRootId.HasValue
                        && finalChainTunerByRoot.TryGetValue(u.ChainRootId.Value, out var fixedName)
                        && !string.IsNullOrWhiteSpace(fixedName)
                        && candidateNames.Contains(fixedName, StringComparer.OrdinalIgnoreCase))
                    .Select(u => (
                        TunerName: finalChainTunerByRoot[u.ChainRootId!.Value],
                        u.OccupyStart,
                        u.OccupyEnd,
                        u.UnitKey))
                    .ToList();

                foreach (var unit in unitsByKey.Where(u => string.Equals(u.Group, grpKey, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(u => u, Comparer<ConflictOccupancyUnit>.Create((a, b) => -CompareFinalUnitPriority(a, b)))
                    .ThenBy(u => u.OccupyStart)
                    .ThenBy(u => u.PriorityReservation.StartTime)
                    .ThenBy(u => u.PriorityReservation.Id))
                {
                    if (forcedChainConflictIds != null
                        && unit.Reservations.Any(m => forcedChainConflictIds.Contains(m.Id)))
                    {
                        foreach (var member in unit.Reservations.Where(m => evaluatedIds.Contains(m.Id)))
                        {
                            finalConflictedIds.Add(member.Id);
                            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_CHAIN_CONFLICT_PROPAGATED", member,
                                $"unitKey={unit.UnitKey} chainRoot=R{(unit.ChainRootId.HasValue ? unit.ChainRootId.Value.ToString() : "-")} reason=predecessor_chain_member_conflicted result=CONFLICT rule=release_contract");
                        }
                        continue;
                    }

                    // final planは前段plannerの一時tunerAssignmentや、過去MutationでDBへ残った通常未来予約の
                    // TunerNameを配置入力にしない。同一の現在予約集合・active ownership・設定からは、
                    // 操作履歴（Disable/Enable、Conflict/復帰）に依存せず同じ物理配置へ収束させる。
                    // hard pinは現在の実録画所有を正本にし、明示チェーンroot/continuityだけを固定継承候補とする。
                    // 通常未来予約は capacity / 固定チェーン非破壊 / 論理Tuner名の決定順で選ぶ。
                    // 過去区間の previousFreeAt は診断値に留め、別の時間成分へ物理配置を伝播させる tie-break には使わない。
                    // 予約優先順位、隣接境界、チェーン固定、active recording pinの契約をこの安定化規則で変更してはならない。
                    if (unit.RequiresPinnedTuner
                        && (unit.HasPinnedTunerConflict
                            || string.IsNullOrWhiteSpace(unit.PinnedTuner)
                            || !candidateNames.Contains(unit.PinnedTuner, StringComparer.OrdinalIgnoreCase)))
                    {
                        foreach (var member in unit.Reservations.Where(m => evaluatedIds.Contains(m.Id)))
                        {
                            finalConflictedIds.Add(member.Id);
                            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_ACTIVE_RECORDING_PIN_INVALID", member,
                                $"unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} pinnedTuner={(unit.PinnedTuner ?? "-")} pinCandidates={unit.PinCandidates} conflict={unit.HasPinnedTunerConflict} candidates={recordingCapacityTuners} reason=active_recording_identity_unavailable rule=release_contract");
                        }
                        continue;
                    }

                    string? rootTuner = null;
                    var hasRootTuner = unit.IsUserChain
                        && unit.ChainRootId.HasValue
                        && finalChainTunerByRoot.TryGetValue(unit.ChainRootId.Value, out rootTuner);
                    string? preferred = !string.IsNullOrWhiteSpace(unit.PinnedTuner)
                        ? unit.PinnedTuner
                        : hasRootTuner
                            ? rootTuner
                            : !string.IsNullOrWhiteSpace(unit.ContinuityPreferredTuner)
                                ? unit.ContinuityPreferredTuner
                                : null;

                    bool IsDirectChainBoundaryPair(ConflictOccupancyUnit current, ConflictOccupancyUnit existing)
                    {
                        if (!current.IsUserChain || !existing.IsUserChain
                            || !current.ChainRootId.HasValue || !existing.ChainRootId.HasValue
                            || current.ChainRootId.Value != existing.ChainRootId.Value)
                            return false;

                        var currentId = current.PriorityReservation.Id;
                        var existingId = existing.PriorityReservation.Id;
                        return (chainSuccessors.TryGetValue(currentId, out var currentSuccessor) && currentSuccessor == existingId)
                            || (chainSuccessors.TryGetValue(existingId, out var existingSuccessor) && existingSuccessor == currentId);
                    }

                    bool CanUseTuner(string name)
                        => finalIntervals.TryGetValue(name, out var intervals)
                           && !intervals.Any(x => IntervalOverlaps(unit.OccupyStart, unit.OccupyEnd, x.Start, x.End)
                               && (x.Unit == null || !IsDirectChainBoundaryPair(unit, x.Unit)));

                    bool CanUseTunerWithoutNormalEpg(string name)
                        => finalIntervals.TryGetValue(name, out var intervals)
                           && !intervals.Any(x => !string.Equals(x.Reason, "normal_epg_wave", StringComparison.OrdinalIgnoreCase)
                               && IntervalOverlaps(unit.OccupyStart, unit.OccupyEnd, x.Start, x.End)
                               && (x.Unit == null || !IsDirectChainBoundaryPair(unit, x.Unit)));

                    bool HasOverlappingNormalEpg(string name)
                        => finalIntervals.TryGetValue(name, out var intervals)
                           && intervals.Any(x => string.Equals(x.Reason, "normal_epg_wave", StringComparison.OrdinalIgnoreCase)
                               && IntervalOverlaps(unit.OccupyStart, unit.OccupyEnd, x.Start, x.End));

                    void RecordNormalEpgBlockerIfCausal(IEnumerable<string> tunerNames)
                    {
                        if (epgOccupationForGroup is null)
                            return;
                        var causal = tunerNames
                            .Where(name => !string.IsNullOrWhiteSpace(name) && candidateNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Any(name => HasOverlappingNormalEpg(name)
                                && !CanUseTuner(name)
                                && CanUseTunerWithoutNormalEpg(name));
                        if (!causal)
                            return;

                        foreach (var member in unit.Reservations.Where(m => evaluatedIds.Contains(m.Id)))
                        {
                            normalEpgBlockerEvidence[member.Id] = new NormalEpgConflictBlockerEvidence(
                                member.Id,
                                grpKey,
                                epgOccupationForGroup.RunGeneration,
                                epgOccupationForGroup.Revision);
                        }
                    }

                    bool OverlapsOtherFixedChainPlacement(string name)
                    {
                        if (unit.IsUserChain || unit.RequiresPinnedTuner)
                            return false;

                        return fixedChainPlacementClaims.Any(c =>
                            !string.Equals(c.UnitKey, unit.UnitKey, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(c.TunerName, name, StringComparison.OrdinalIgnoreCase)
                            && IntervalOverlaps(unit.OccupyStart, unit.OccupyEnd, c.OccupyStart, c.OccupyEnd));
                    }

                    List<string> FreeCandidatesPreferNonDisplacingChainTuner()
                    {
                        var free = candidateNames.Where(CanUseTuner).ToList();
                        if (unit.IsUserChain || unit.RequiresPinnedTuner || free.Count <= 1)
                            return free;

                        var nonDisplacing = free.Where(name => !OverlapsOtherFixedChainPlacement(name)).ToList();
                        return nonDisplacing.Count > 0 ? nonDisplacing : free;
                    }

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

                    string? TryRepackMovableUnits(ConflictOccupancyUnit challenger)
                    {
                        // FINAL_CAPACITY_REPACK_INVARIANT:
                        // Recording RoleBinding の候補数・予約前後マージンは設定生値から既に構築済みの
                        // candidateNames / OccupyStart / OccupyEnd をそのまま使用する。物理環境固有の本数や秒数を
                        // ここで仮定してはならない。
                        //
                        // 実録画/active anchor、明示チェーン、pin必須Unitは物理Tunerを動かせない。
                        // それ以外の未来通常Unitだけを、challengerと時間的につながる局所成分内で再配置する。
                        // 優先順位そのものはこの関数では変更せず、既にFinal Planへ受理済みの上位Unitと
                        // challengerを全て成立させる物理配置が存在するかだけを解く。
                        if (challenger.IsUserChain || challenger.RequiresPinnedTuner || challenger.HasActiveAnchor)
                            return null;

                        var assignedMovable = finalIntervals
                            .SelectMany(kv => kv.Value
                                .Where(x => x.Unit != null)
                                .Select(x => (Tuner: kv.Key, Unit: x.Unit!)))
                            .Where(x => !x.Unit.IsUserChain && !x.Unit.RequiresPinnedTuner && !x.Unit.HasActiveAnchor)
                            .GroupBy(x => x.Unit.UnitKey, StringComparer.OrdinalIgnoreCase)
                            .Select(g => g.First())
                            .ToList();

                        var componentUnits = new List<ConflictOccupancyUnit>();
                        var componentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var frontier = new Queue<ConflictOccupancyUnit>();
                        frontier.Enqueue(challenger);
                        componentKeys.Add(challenger.UnitKey);

                        while (frontier.Count > 0)
                        {
                            var currentUnit = frontier.Dequeue();
                            foreach (var assigned in assignedMovable)
                            {
                                if (componentKeys.Contains(assigned.Unit.UnitKey))
                                    continue;
                                if (!UnitOverlaps(currentUnit, assigned.Unit))
                                    continue;
                                componentKeys.Add(assigned.Unit.UnitKey);
                                componentUnits.Add(assigned.Unit);
                                frontier.Enqueue(assigned.Unit);
                            }
                        }

                        // 既配置Unitと全くつながらない場合は通常の空き判定で十分。
                        if (componentUnits.Count == 0)
                            return null;

                        var searchUnits = componentUnits
                            .Append(challenger)
                            .GroupBy(x => x.UnitKey, StringComparer.OrdinalIgnoreCase)
                            .Select(g => g.First())
                            .ToList();
                        var searchUnitByKey = searchUnits.ToDictionary(x => x.UnitKey, StringComparer.OrdinalIgnoreCase);

                        var currentTunerByUnitKey = assignedMovable
                            .Where(x => componentKeys.Contains(x.Unit.UnitKey))
                            .ToDictionary(x => x.Unit.UnitKey, x => x.Tuner, StringComparer.OrdinalIgnoreCase);

                        bool OverlapsFixedInterval(ConflictOccupancyUnit moving, string tunerName)
                        {
                            if (!finalIntervals.TryGetValue(tunerName, out var intervals))
                                return true;
                            return intervals.Any(x =>
                                (x.Unit == null || !componentKeys.Contains(x.Unit.UnitKey))
                                && IntervalOverlaps(moving.OccupyStart, moving.OccupyEnd, x.Start, x.End));
                        }

                        bool OverlapsFixedChainClaim(ConflictOccupancyUnit moving, string tunerName)
                            => fixedChainPlacementClaims.Any(c =>
                                !string.Equals(c.UnitKey, moving.UnitKey, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(c.TunerName, tunerName, StringComparison.OrdinalIgnoreCase)
                                && IntervalOverlaps(moving.OccupyStart, moving.OccupyEnd, c.OccupyStart, c.OccupyEnd));

                        var placement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        List<string> AvailableTuners(ConflictOccupancyUnit moving)
                        {
                            var available = candidateNames
                                .Where(name => !OverlapsFixedInterval(moving, name))
                                .Where(name => !placement.Any(kv =>
                                    string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)
                                    && searchUnitByKey.TryGetValue(kv.Key, out var other)
                                    && UnitOverlaps(moving, other)))
                                .ToList();

                            currentTunerByUnitKey.TryGetValue(moving.UnitKey, out var currentTuner);
                            return available
                                // 固定チェーン枠を避けても成立するなら、既存契約どおりそちらを先に試す。
                                .OrderBy(name => OverlapsFixedChainClaim(moving, name) ? 1 : 0)
                                // 同じ成立性なら不要なTuner移動を避ける。
                                .ThenBy(name => string.Equals(name, currentTuner, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                                .ToList();
                        }

                        bool SearchPlacement()
                        {
                            if (placement.Count == searchUnits.Count)
                                return true;

                            ConflictOccupancyUnit? next = null;
                            List<string>? nextCandidates = null;
                            foreach (var candidateUnit in searchUnits.Where(x => !placement.ContainsKey(x.UnitKey)))
                            {
                                var candidates = AvailableTuners(candidateUnit);
                                if (candidates.Count == 0)
                                    return false;
                                if (next == null
                                    || candidates.Count < nextCandidates!.Count
                                    || (candidates.Count == nextCandidates!.Count && candidateUnit.OccupyStart < next.OccupyStart)
                                    || (candidates.Count == nextCandidates!.Count && candidateUnit.OccupyStart == next.OccupyStart && candidateUnit.UnitId < next.UnitId))
                                {
                                    next = candidateUnit;
                                    nextCandidates = candidates;
                                }
                            }

                            if (next == null || nextCandidates == null)
                                return false;

                            foreach (var tunerName in nextCandidates)
                            {
                                placement[next.UnitKey] = tunerName;
                                if (SearchPlacement())
                                    return true;
                                placement.Remove(next.UnitKey);
                            }
                            return false;
                        }

                        if (!SearchPlacement() || !placement.TryGetValue(challenger.UnitKey, out var challengerTuner))
                            return null;

                        // 探索成功後にだけ既配置Unitを全量置換する。探索途中の部分結果を正本へ反映しない。
                        foreach (var name in candidateNames)
                        {
                            if (!finalIntervals.TryGetValue(name, out var intervals))
                                continue;
                            intervals.RemoveAll(x => x.Unit != null && componentKeys.Contains(x.Unit.UnitKey));
                        }

                        var moved = 0;
                        foreach (var existing in componentUnits)
                        {
                            if (!placement.TryGetValue(existing.UnitKey, out var tunerName))
                                continue;
                            finalIntervals[tunerName].Add((existing.OccupyStart, existing.OccupyEnd, existing, "final_capacity_repack"));
                            finalIntervals[tunerName].Sort((a, b) => a.Start.CompareTo(b.Start));

                            currentTunerByUnitKey.TryGetValue(existing.UnitKey, out var previousTuner);
                            if (!string.Equals(previousTuner, tunerName, StringComparison.OrdinalIgnoreCase))
                            {
                                moved++;
                                foreach (var member in existing.Reservations.Where(m => evaluatedIds.Contains(m.Id)))
                                    finalTunerAssignment[member.Id] = tunerName;
                                AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_UNIT_REPACK", existing.PriorityReservation,
                                    $"unitKey={existing.UnitKey} unit={existing.UnitId} members={existing.MemberIds} from={previousTuner ?? "-"} to={tunerName} occupy={existing.OccupyStart:MM/dd HH:mm:ss}〜{existing.OccupyEnd:MM/dd HH:mm:ss} group={grpKey} reason=capacity_feasible_repack priorityChanged=False candidates={recordingCapacityTuners} rule=release_contract");
                            }
                        }

                        AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_CAPACITY_REPACK", challenger.PriorityReservation,
                            $"unitKey={challenger.UnitKey} unit={challenger.UnitId} members={challenger.MemberIds} selectedTuner={challengerTuner} componentUnits={searchUnits.Count} movedExisting={moved} occupy={challenger.OccupyStart:MM/dd HH:mm:ss}〜{challenger.OccupyEnd:MM/dd HH:mm:ss} group={grpKey} candidates={recordingCapacityTuners} result=FEASIBLE priorityChanged=False source=settings_rolebinding_and_margin_intervals rule=release_contract");
                        return challengerTuner;
                    }

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
                                    AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_ADJACENT_BOUNDARY_SKIP", challenger.PriorityReservation,
                                        $"challenger={UnitTraceId(challenger)} incumbent={UnitTraceId(victim)} tuner={name} group={grpKey} sameService={IsSameService(challenger.PriorityReservation, victim.PriorityReservation)} boundary=True marginOverlap=True laterPriority={laterProgramPriority} clickOrderWinner={UnitTraceId(clickWinner)} boundaryPriorityWinner={UnitTraceId(boundaryWinner)} overridden=False reason={skipReason} rule=release_contract");
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
                                    AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_UNIT_CONFLICT", member,
                                        $"unitKey={victim.UnitKey} unit={victim.UnitId} members={victim.MemberIds} occupy={victim.OccupyStart:MM/dd HH:mm:ss}〜{victim.OccupyEnd:MM/dd HH:mm:ss} group={grpKey} reason=adjacent_boundary_priority_override loserAgainstUnit={challenger.UnitId} candidates={recordingCapacityTuners} displaySource=LogicalTunerDisplayName prioritySourceRank={SourcePriorityRank(victim.PriorityReservation)} userDecision={UnitUserDecisionTime(victim):MM/dd HH:mm:ss} ruleOrder={PriorityRuleOrder(victim.PriorityReservation)} rule=release_contract");
                                }
                            }
                            var overriddenClickWinners = string.Join(",", victims
                                .Where(v => UnitUserDecisionTime(v) > UnitUserDecisionTime(challenger))
                                .Select(UnitTraceId));
                            if (string.IsNullOrWhiteSpace(overriddenClickWinners))
                                overriddenClickWinners = "-";
                            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_ADJACENT_BOUNDARY_OVERRIDE", challenger.PriorityReservation,
                                $"winner={UnitTraceId(challenger)} losers={string.Join(",", victims.Select(UnitTraceId))} tuner={name} group={grpKey} sameService=True boundary=True marginOverlap=True laterPriority={laterProgramPriority} clickOrderWinnerOverridden={overriddenClickWinners} boundaryPriorityWinner={UnitTraceId(challenger)} overridden=True reason=front_back_priority_overrides_manual_click_order rule=release_contract");
                            return name;
                        }
                        return null;
                    }

                    string? chosen = null;
                    var preferredIsCandidate = !string.IsNullOrWhiteSpace(preferred) && candidateNames.Contains(preferred, StringComparer.OrdinalIgnoreCase);
                    var preferredWouldDisplaceFixedChain = preferredIsCandidate
                        && CanUseTuner(preferred!)
                        && OverlapsOtherFixedChainPlacement(preferred!)
                        && FreeCandidatesPreferNonDisplacingChainTuner().Any(name => !string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase));
                    if (preferredIsCandidate && CanUseTuner(preferred!) && !preferredWouldDisplaceFixedChain)
                        chosen = preferred;

                    if (hasRootTuner && chosen == null)
                    {
                        if (!string.IsNullOrWhiteSpace(rootTuner))
                            RecordNormalEpgBlockerIfCausal(new[] { rootTuner! });
                        foreach (var member in unit.Reservations.Where(m => evaluatedIds.Contains(m.Id)))
                        {
                            finalConflictedIds.Add(member.Id);
                            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_CHAIN_TUNER_CONTINUITY_CONFLICT", member,
                                $"unitKey={unit.UnitKey} chainRoot=R{unit.ChainRootId!.Value} requiredTuner={rootTuner ?? "-"} occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} reason=assigned_chain_root_tuner_unavailable_no_mid_chain_switch rule=release_contract");
                        }
                        continue;
                    }

                    if (unit.RequiresPinnedTuner && chosen == null)
                    {
                        if (!string.IsNullOrWhiteSpace(unit.PinnedTuner))
                            RecordNormalEpgBlockerIfCausal(new[] { unit.PinnedTuner! });
                        foreach (var member in unit.Reservations.Where(m => evaluatedIds.Contains(m.Id)))
                        {
                            finalConflictedIds.Add(member.Id);
                            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_ACTIVE_RECORDING_PIN_BLOCKED", member,
                                $"unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} pinnedTuner={unit.PinnedTuner} occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} reason=pinned_tuner_not_available_no_reassignment rule=release_contract");
                        }
                        continue;
                    }

                    if (chosen == null)
                    {
                        // 空きTunerが複数ある場合、過去の非重複区間でどのTunerが最後に使われたかは
                        // 現在Unitの成立性とは無関係。ここで previousFreeAt を選択軸にすると、局所Mutationが
                        // 時間的に独立した未来予約へ連鎖し、不要な物理Tuner再配置を生む。
                        // したがって固定チェーンを避けた空き候補から論理Tuner名だけで決定し、
                        // 真に重なる成分で成立性が必要な場合は後段の局所repackに任せる。
                        chosen = FreeCandidatesPreferNonDisplacingChainTuner()
                            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
                    }

                    if (chosen != null
                        && !unit.IsUserChain
                        && !unit.RequiresPinnedTuner
                        && !OverlapsOtherFixedChainPlacement(chosen)
                        && candidateNames.Any(name => CanUseTuner(name) && OverlapsOtherFixedChainPlacement(name)))
                    {
                        AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_UNIT_FIXED_CHAIN_TUNER_AVOIDED", unit.PriorityReservation,
                            $"unitKey={unit.UnitKey} unit={unit.UnitId} selectedTuner={chosen} occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} reason=non_displacing_free_tuner_available priorityChanged=False chainPriorityBonus=False rule=release_contract");
                    }

                    if (chosen != null
                        && !unit.IsUserChain
                        && !unit.RequiresPinnedTuner
                        && fixedChainPlacementClaims.Any(c =>
                            string.Equals(c.TunerName, chosen, StringComparison.OrdinalIgnoreCase)
                            && IntervalOverlaps(unit.OccupyStart, unit.OccupyEnd, c.OccupyStart, c.OccupyEnd)))
                    {
                        AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_UNIT_FIXED_CHAIN_TUNER_FALLBACK", unit.PriorityReservation,
                            $"unitKey={unit.UnitKey} unit={unit.UnitId} tuner={chosen} occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} reason=no_non_displacing_free_tuner priorityPreserved=True rule=release_contract");
                    }

                    if (chosen == null)
                        chosen = TryRepackMovableUnits(unit);

                    if (chosen == null)
                        chosen = TryReplaceAdjacentBoundaryVictim(unit);

                    if (chosen == null)
                    {
                        RecordNormalEpgBlockerIfCausal(candidateNames);
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
                                AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_ADJACENT_BOUNDARY_OVERRIDE", unit.PriorityReservation,
                                    $"challenger={UnitTraceId(unit)} incumbent={UnitTraceId(victim)} tuner={blocker.Tuner} group={grpKey} sameService=True boundary=True marginOverlap=True laterPriority={laterProgramPriority} clickOrderWinner={clickWinnerId} boundaryPriorityWinner={boundaryWinnerId} overridden={overridden} reason={reason} rule=release_contract");
                            }
                            else
                            {
                                var reason = !sameService
                                    ? "different_service"
                                    : "different_priority_layer";
                                AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_ADJACENT_BOUNDARY_SKIP", unit.PriorityReservation,
                                    $"challenger={UnitTraceId(unit)} incumbent={UnitTraceId(victim)} tuner={blocker.Tuner} group={grpKey} sameService={sameService} boundary=True marginOverlap=True laterPriority={laterProgramPriority} clickOrderWinner={clickWinnerId} boundaryPriorityWinner={boundaryWinnerId} overridden=False reason={reason} rule=release_contract");
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
                            finalConflictBlockersByReservationId[member.Id] = overlapSummary;
                            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_UNIT_CONFLICT", member,
                                $"unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} group={grpKey} reason=no_free_recording_tuner_after_priority_plan candidates={recordingCapacityTuners} displaySource=LogicalTunerDisplayName prioritySourceRank={SourcePriorityRank(unit.PriorityReservation)} userDecision={UnitUserDecisionTime(unit):MM/dd HH:mm:ss} ruleOrder={PriorityRuleOrder(unit.PriorityReservation)} overlaps={overlapSummary} rule=release_contract");
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
                    AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, unit.IsUserChain ? "FINAL_CHAIN_UNIT_ASSIGN" : "FINAL_UNIT_ASSIGN", unit.PriorityReservation,
                        $"unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} chainRoot=R{(unit.ChainRootId.HasValue ? unit.ChainRootId.Value.ToString() : "-")} tuner={chosen} previousFreeAt={previousFreeAt:MM/dd HH:mm:ss} occupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} group={grpKey} capacitySource=RoleBinding/Recording recordingTuners={recordingCapacityTuners} displaySource=LogicalTunerDisplayName prioritySourceRank={SourcePriorityRank(unit.PriorityReservation)} userDecision={UnitUserDecisionTime(unit):MM/dd HH:mm:ss} ruleOrder={PriorityRuleOrder(unit.PriorityReservation)} result=ASSIGNED rule=release_contract");
                    if (unit.IsUserChain)
                    {
                        if (unit.ChainRootId.HasValue)
                            finalChainTunerByRoot[unit.ChainRootId.Value] = chosen;
                        AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "CHAIN_UNIT_TUNER_ASSIGN", unit.PriorityReservation,
                            $"unitKey={unit.UnitKey} unit={unit.UnitId} members={unit.MemberIds} chainRoot=R{(unit.ChainRootId.HasValue ? unit.ChainRootId.Value.ToString() : "-")} tuner={chosen} unitOccupy={unit.OccupyStart:MM/dd HH:mm:ss}〜{unit.OccupyEnd:MM/dd HH:mm:ss} capacitySource=RoleBinding/Recording fixedTuner={unit.PinnedTuner ?? "-"} displaySource=LogicalTunerDisplayName reason={(string.IsNullOrWhiteSpace(unit.PinnedTuner) ? "chain_slot_initial_tuner_committed" : "chain_fixed_tuner_capacity_accepted")} result=ASSIGNED source=chain_slot_single_source rule=chain_fixed_physical_tuner_contract");
                    }
                }
            }

            ApplyFinalConflictProjection(
                evaluatedIds,
                finalAssignedIds,
                finalConflictedIds,
                finalTunerAssignment);
        }

        void ApplyFinalConflictProjection(
            IReadOnlySet<int> evaluatedIds,
            IReadOnlySet<int> finalAssignedIds,
            IReadOnlySet<int> finalConflictedIds,
            IReadOnlyDictionary<int, string> finalTunerAssignment)
        {
            // FINAL_ALLOCATION_PROJECTION_INVARIANT:
            // 計算結果の反映はこの関数だけが行う。割当計算中に正本の assigned/conflicted/tunerAssignment を
            // 部分更新してはならない。第1パスとチェーン伝播後の第2パスは、どちらも全量置換で確定する。
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

        }

        // USER_CHAIN_ALLOCATION_INVARIANT — 変更禁止:
        // 1. チェーン専用ボタンで成立した連続予約は、作成時に確定した同一物理録画Tunerを
        //    Rootから全後続へ継承し、チェーン終了まで別Tunerへ変更しない。
        // 2. 固定されるのは物理Tunerであり、予約優先順位ではない。各イベントは通常予約と同じ
        //    予約元・ルール順・同一サービス境界・ユーザー確定時刻の共通優先順位で採否を決める。
        // 3. 競合判定はイベント単位で、開始・終了マージン全量を使用する。最初に競合した
        //    チェーンイベントが確定した場合、そのイベント以降だけを保存済みトポロジーに沿って競合化する。
        // 4. 競合原因が消えた再計算では、競合開始地点以降を同じ固定物理Tuner上のチェーン区間として復帰させる。
        //    個別イベントだけの復帰、別Tunerへの再選択、チェーン優先加点は禁止する。
        // 5. 前番組末尾30秒の欠落は、前後イベントがともに有効・非競合で、同一チェーン・同一固定物理Tunerの
        //    境界を正当に実行する場合だけ行い、後番組を開始時刻から完全録画するために使用する。
        //    通常終了、競合落ち、取消、無効化、開始/取得失敗、復旧、容量調整では一切行わない。
        // この5条件を、Tuner再選択、チェーン絶対優先、マージン救済、部分録画救済、イベント単独復活へ改変してはならない。
        ApplyFinalPlanWithChainPropagation();

        void ApplyFinalPlanWithChainPropagation()
        {
            // FINAL_CHAIN_TWO_PASS_INVARIANT:
            // 第1パスは全イベントを通常優先順位・通常マージンで評価する。そこで最初に競合した
            // チェーンイベントを境界として確定し、第2パスではそのイベント以降だけを除外して再計算する。
            // 伝播状態は保存しないため、空きTunerが戻った次回計算では必ず第1パスから復帰判定される。
            ReconcileFinalConflictPlanByUnitKey();

            var propagatedChainConflictIds = CollectPropagatedChainConflictIds();
            if (propagatedChainConflictIds.Any(id => !conflictedIds.Contains(id)))
                ReconcileFinalConflictPlanByUnitKey(propagatedChainConflictIds);

            // FINAL_CONFLICT_PLAN_LOG_INVARIANT:
            // 第1パスの直接競合と、第2パスのチェーン伝播後結果を同じ END 名で二重出力しない。
            // FINAL_CONFLICT_PLAN_END は、全伝播を反映した確定投影を1回だけ記録する。
            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_CONFLICT_PLAN_END", new Reservation { Id = 0, Title = "FinalConflictPlan" },
                $"assigned={assignedIds.Count} conflicted={conflictedIds.Count} tunerAssignments={tunerAssignment.Count} phase=final_after_chain_propagation rule=release_contract");
        }

        HashSet<int> CollectPropagatedChainConflictIds()
        {
            // CHAIN_CONFLICT_PROPAGATION_INVARIANT:
            // 通常の共通優先計算で最初に競合したチェーン子が確定した後、その子以降をチェーン枠として可逆に競合化する。
            // 競合判定そのものはイベント単位だが、復帰単位は個別イベントではない。トポロジーを保持し、
            // 競合原因の予約取消後は次回再計算で、競合開始地点以降の成立可能区間を同一Tunerのチェーン枠として復活させる。
            var propagated = new HashSet<int>();
            var firstChainConflictIds = conflictedIds
                .Where(id => chainPredecessors.ContainsKey(id))
                .OrderBy(id => allChainReservationsById.TryGetValue(id, out var r) ? r.StartTime : DateTime.MaxValue)
                .ThenBy(id => id)
                .ToList();

            foreach (var firstConflictId in firstChainConflictIds)
            {
                if (allChainReservationsById.TryGetValue(firstConflictId, out var firstConflict))
                {
                    var blockerSummary = finalConflictBlockersByReservationId.TryGetValue(firstConflict.Id, out var blockers)
                        ? blockers
                        : "not_captured";
                    AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_CHAIN_CONFLICT_HEAD", firstConflict,
                        $"chainRoot=R{ResolveStoredChainRootId(firstConflict)} predecessor=R{firstConflict.UserChainPreviousId!.Value} result=CONFLICT action=propagate_to_successors topology=preserved blockers={blockerSummary} rule=release_contract");
                }

                var cursorId = firstConflictId;
                var visited = new HashSet<int>();
                while (visited.Add(cursorId))
                {
                    if (scheduledById.ContainsKey(cursorId))
                    {
                        propagated.Add(cursorId);
                        if (normalEpgBlockerEvidence.TryGetValue(firstConflictId, out var headEvidence))
                        {
                            normalEpgBlockerEvidence[cursorId] = headEvidence with { ReservationId = cursorId };
                        }
                    }
                    if (!chainSuccessors.TryGetValue(cursorId, out var successorId))
                        break;
                    cursorId = successorId;
                }
            }

            return propagated;
        }

        // CHAIN_CONFLICT_RECOVERY_AUDIT_INVARIANT:
        // 競合原因の予約取消後は、保存済みトポロジーを変更せず通常再計算だけで復活する。
        // IsConflictedの解除をチェーン再作成やRoot付替えと混同しないよう、復活を専用監査で可視化する。
        foreach (var restored in scheduled
            .Where(r => r.IsUserChain && r.UserChainPreviousId.HasValue)
            .Where(r => r.IsConflicted && !conflictedIds.Contains(r.Id))
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id))
        {
            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_CHAIN_CONFLICT_RESTORED", restored,
                $"chainRoot=R{ResolveStoredChainRootId(restored)} predecessor=R{restored.UserChainPreviousId!.Value} result=RESTORED source=common_reallocation topology=preserved rule=release_contract");
        }

        // CHAIN_PARTIAL_RECOVERY_AUDIT_INVARIANT:
        // 一つの競合原因を取り消しても別の通常予約が残る場合、前半だけが復活し、
        // 最初の残存競合子以降は競合を維持する。これは復活漏れではなく再計算結果である。
        foreach (var chainGroup in scheduled
            .Where(r => r.IsUserChain || chainSuccessors.ContainsKey(r.Id))
            .GroupBy(ResolveStoredChainRootId))
        {
            var previouslyConflicted = chainGroup.Where(r => r.IsConflicted).OrderBy(r => r.StartTime).ThenBy(r => r.Id).ToList();
            if (previouslyConflicted.Count == 0) continue;

            var restoredIds = previouslyConflicted.Where(r => !conflictedIds.Contains(r.Id)).Select(r => $"R{r.Id}").ToList();
            var remaining = chainGroup.Where(r => conflictedIds.Contains(r.Id)).OrderBy(r => r.StartTime).ThenBy(r => r.Id).ToList();
            if (restoredIds.Count == 0 || remaining.Count == 0) continue;

            var firstRemaining = remaining[0];
            var blockers = finalConflictBlockersByReservationId.TryGetValue(firstRemaining.Id, out var residualBlockers)
                ? residualBlockers
                : "not_captured";
            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "FINAL_CHAIN_CONFLICT_PARTIAL_RESTORE", firstRemaining,
                $"chainRoot=R{chainGroup.Key} restored=[{string.Join(',', restoredIds)}] firstRemaining=R{firstRemaining.Id} remaining=[{string.Join(',', remaining.Select(r => $"R{r.Id}"))}] blockers={blockers} result=PARTIAL_RESTORE topology=preserved rule=release_contract");
        }

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
            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "MISSING_DUPLICATE_CONFLICT", missing, $"duplicateWith=R{partner.Id} service=[{missing.ServiceName}] start={missing.StartTime:MM/dd HH:mm} end={missing.EndTime:MM/dd HH:mm} origin=ProgramGuideMissingProgramRule identity=TimeIdentity commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
        }

        // Final Planの全差分を、Reservation/Tuner Snapshot世代を検証して一括Commitする。
        var updates = new List<AllocationRowUpdate>();
        var scheduledIds = scheduled.Select(x => x.Id).ToHashSet();
        foreach (var r in allScheduled)
        {
            if (r.Source == ReservationSource.Epg)
            {
                if (r.IsConflicted)
                    updates.Add(new AllocationRowUpdate(r.Id, r.DataVersion, null, false, r.TunerName ?? string.Empty, r.ServiceName, r.Title, r.IsConflicted));
                continue;
            }

            var includedInEvaluation = scheduledIds.Contains(r.Id);
            var newConflicted = includedInEvaluation && conflictedIds.Contains(r.Id);
            var isChainSlotMember = pseudoContinuous && (r.IsUserChain || chainSuccessors.ContainsKey(r.Id));
            var resolvedChainRootId = isChainSlotMember ? ResolveStoredChainRootId(r) : 0;
            var fixedChainTuner = isChainSlotMember
                && chainResolvedTunerByRoot.TryGetValue(resolvedChainRootId, out var chainTuner)
                    ? chainTuner
                    : isChainSlotMember
                        && chainPlannedTunerPreferenceByRoot.TryGetValue(resolvedChainRootId, out var plannedChainTuner)
                            ? plannedChainTuner
                            : string.Empty;
            // root preplanで成立した全メンバーは、競合イベントを含め同じroot Tunerを保持する。
            // root枠を確定できなかった場合だけ前回計画値を表示上保持するが、次回計画では弱い候補として扱う。
            var newTuner = isChainSlotMember
                ? fixedChainTuner
                : (!includedInEvaluation || newConflicted) ? "" :
                    (tunerAssignment.TryGetValue(r.Id, out var tn) ? tn : "");
            var tunerChanged = !string.Equals(r.TunerName ?? string.Empty, newTuner, StringComparison.Ordinal);
            var conflictChanged = r.IsConflicted != newConflicted;
            if (tunerChanged || conflictChanged)
                updates.Add(new AllocationRowUpdate(r.Id, r.DataVersion, tunerChanged ? newTuner : null, conflictChanged ? newConflicted : null, r.TunerName ?? string.Empty, r.ServiceName, r.Title, r.IsConflicted));
        }

        foreach (var resolvedSlot in chainResolvedTunerByRoot.OrderBy(x => x.Key))
        {
            var slotMembers = allScheduled
                .Where(x => ResolveStoredChainRootId(x) == resolvedSlot.Key)
                .Where(x => x.IsUserChain || chainSuccessors.ContainsKey(x.Id) || x.Id == resolvedSlot.Key)
                .OrderBy(x => x.StartTime)
                .ThenBy(x => x.Id)
                .ToList();
            if (slotMembers.Count == 0) continue;
            var root = slotMembers.FirstOrDefault(x => x.Id == resolvedSlot.Key) ?? slotMembers[0];
            AddTunerAllocationDebugTrace(debugTrace!, recordingTunerProfiles, "CHAIN_SLOT_COMMIT", root,
                $"chainRoot=R{resolvedSlot.Key} fixedTuner={resolvedSlot.Value} members=[{string.Join(',', slotMembers.Select(x => $"R{x.Id}"))}] action=commit_root_and_members_once topology=preserved rule=chain_slot_single_source_contract");
        }

        var commit = TryCommitAllocationPlan(
            updates,
            reservationSnapshot,
            allocationGenerationBefore,
            tunerPool,
            tunerSnapshotVersion);

        if (!commit.Applied)
        {
            // 未commitのFinalConflictPlanから因果証拠を残さない。前回commit済み証拠も、
            // 現在の予約/Tuner snapshotに対して再確認できていないため一旦失効させる。
            ReplaceNormalEpgConflictBlockerEvidence(new Dictionary<int, NormalEpgConflictBlockerEvidence>());
            log.Add("FINAL_ALLOCATION_COMMIT", "Rejected",
                $"result=RETRY_REQUIRED reason={commit.Reason} reservationSnapshot={reservationSnapshot.Hash} tunerSnapshotVersion={tunerSnapshotVersion} allocationGeneration={allocationGenerationBefore} updates={updates.Count} rule=release_contract");
            return new ReservationAllocationEvaluationResult(
                Changes: Array.Empty<(int, string, string, bool)>(),
                RowChanges: Array.Empty<AllocationCommittedRowChange>(),
                AllocationGeneration: commit.AllocationGeneration,
                RetryRequired: true,
                Reason: commit.Reason,
                ReservationSnapshotVersion: reservationSnapshot.Hash,
                TunerSnapshotVersion: tunerSnapshotVersion);
        }

        var committedNormalEpgEvidence = normalEpgBlockerEvidence
            .Where(x => conflictedIds.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value);
        ReplaceNormalEpgConflictBlockerEvidence(committedNormalEpgEvidence);

        log.Add("FINAL_ALLOCATION_COMMIT", "Applied",
            $"result=APPLIED changes={commit.Changes.Count} updates={updates.Count} allocationGeneration={allocationGenerationBefore}->{commit.AllocationGeneration} reservationSnapshot={reservationSnapshot.Hash} tunerSnapshotVersion={tunerSnapshotVersion} rule=release_contract");

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
            debugTrace!);

        foreach (var change in commit.Changes)
        {
            var before = allScheduled.FirstOrDefault(x => x.Id == change.Id);
            log.Add("RESERVATION_AUDIT", "CONFLICT_FLAG",
                $"service={TrimForAudit(before?.ServiceName)} title={TrimTitleForAudit(before?.Title)} rawTitleBlank={RawTitleBlankForAudit(before?.Title)} id=R{change.Id} affected=1 from={(before?.IsConflicted.ToString() ?? "-")} to={change.Conflicted} status={(before?.Status.ToString() ?? "-")} enabled={(before?.IsEnabled.ToString() ?? "-")} allocationGeneration={commit.AllocationGeneration} eventDelivery=deferred_to_final_single_flight rule=release_contract");
        }

        return new ReservationAllocationEvaluationResult(
            Changes: commit.Changes,
            RowChanges: commit.RowChanges,
            AllocationGeneration: commit.AllocationGeneration,
            RetryRequired: false,
            Reason: commit.Reason,
            ReservationSnapshotVersion: reservationSnapshot.Hash,
            TunerSnapshotVersion: tunerSnapshotVersion);
    }


    public (int ProjectedCount, int SupersededCount, long WakePlanVersion) PublishAllocationMutationEvents(
        IReadOnlyList<AllocationCommittedRowChange> changes,
        long allocationGeneration,
        bool queueWakeFallbackWhenCompleted)
    {
        var projectedCount = 0;
        var supersededCount = 0;
        // ALLOCATION_MUTATION_WAKE_BATCH_INVARIANT:
        // 同一allocationGenerationの行差分は、予約Mutation/Plugin通知の順序を維持したまま、
        // Wake影響世代だけをbatch集約する。Wake本体は共通割当の最終投影が所有し、
        // 予約1件ごとのschtasks再評価は禁止する。
        using var wakeBatch = mutationJournal.BeginWakeRefreshBatch(
            "AllocationDerivedProjection",
            $"generation={allocationGeneration}",
            queueWakeFallbackWhenCompleted);
        foreach (var change in changes)
        {
            var after = GetById(change.Id);
            if (after is null || after.DataVersion != change.CurrentDataVersion)
            {
                supersededCount++;
                log.Add("ALLOC_EVENT", "ReservationAllocationChanged",
                    $"id=R{change.Id} result=SUPERSEDED allocationGeneration={allocationGeneration} committedDataVersion={change.CurrentDataVersion} currentDataVersion={(after?.DataVersion.ToString() ?? "missing")} action=do_not_publish_stale_allocation_snapshot rule=allocation_mutation_projection_generation_contract");
                continue;
            }

            var beforeJson = JsonSerializer.Serialize(after);
            var before = JsonSerializer.Deserialize<Reservation>(beforeJson);
            if (before is null) continue;
            before.TunerName = change.PreviousTunerName;
            before.IsConflicted = change.PreviousConflicted;
            before.DataVersion = change.PreviousDataVersion;

            var changedFields = new List<string>();
            if (!string.Equals(change.PreviousTunerName, change.CurrentTunerName, StringComparison.Ordinal))
                changedFields.Add(nameof(Reservation.TunerName));
            if (change.PreviousConflicted != change.CurrentConflicted)
                changedFields.Add(nameof(Reservation.IsConflicted));
            changedFields.Add(nameof(Reservation.DataVersion));

            // ALLOCATION_MUTATION_PROJECTION_INVARIANT:
            // 共通割当commitが変更するTunerName／IsConflicted／DataVersionは、最終single-flight差分だけを
            // 同じmutationとして外部へ投影する。Tuner割当変更をDBだけに閉じ込めたり、途中周回を重複配送しない。
            var kind = change.PreviousConflicted != change.CurrentConflicted
                ? ReservationMutationKind.ConflictChanged
                : ReservationMutationKind.Updated;
            mutationJournal.Record(kind, before, after, changedFields.ToArray());
            projectedCount++;

            log.Add("ALLOC_EVENT", "ReservationAllocationChanged",
                $"id=R{change.Id} allocationGeneration={allocationGeneration} tuner={TrimForAudit(change.PreviousTunerName)}->{TrimForAudit(change.CurrentTunerName)} conflicted={change.PreviousConflicted}->{change.CurrentConflicted} fields={string.Join(',', changedFields)} delivery=final_single_flight rule=release_contract");
        }

        var batchWakePlanVersion = mutationJournal.CurrentWakeRefreshBatchVersion;
        return (projectedCount, supersededCount, batchWakePlanVersion);
    }

    /// <summary>
    /// Developer Diagnostics 専用の割当trace正本。
    /// Conditional 呼び出しにより、公開版ではdetail文字列の評価自体をコンパイル時に除去する。
    /// </summary>
    [System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
    private void AddTunerAllocationDebugTrace(
        List<TunerAllocationDebugTraceEntry> trace,
        IReadOnlyList<Core.TunerProfile> tunerProfiles,
        string stage,
        Reservation reservation,
        string detail)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        trace.Add(new TunerAllocationDebugTraceEntry
        {
            Stage = stage,
            Group = ReservationTunerGroupResolver.Resolve(reservation, tunerProfiles),
            ReservationId = reservation.Id,
            Title = reservation.Title,
            Detail = detail
        });
#endif
    }

    public string GetTunerAllocationDebugPath()
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        var dir = Path.Combine(db.DataDirectory, "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "tuner_allocation_debug.json");
#else
        return string.Empty;
#endif
    }

    public string? ReadTunerAllocationDebugJson()
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        var path = GetTunerAllocationDebugPath();
        return File.Exists(path) ? File.ReadAllText(path) : null;
#else
        return null;
#endif
    }

    [System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
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
#if TVAIR_DEVELOPER_DIAGNOSTICS
        // 視聴用チューナーは録画・EPG取得の割当対象から外す。
        // デバッグ出力上の上限・候補数にも録画用チューナーだけを反映する。
        var recordingTunerProfiles = tunerProfiles
            .Where(p => !string.Equals(IniSettingsService.NormalizeTunerRole(p.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string ResolveGroup(Reservation r) => ReservationTunerGroupResolver.Resolve(r, recordingTunerProfiles);

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
#endif
    }

#if TVAIR_DEVELOPER_DIAGNOSTICS
    private void WriteTunerAllocationDebugLog(TunerAllocationDebugSnapshot snapshot)
    {
        log.Add("TUNER_ALLOC_SUMMARY", "Summary",
            $"evaluated={snapshot.Summary.EvaluatedCount} allocated={snapshot.Summary.AllocatedCount} conflict={snapshot.Summary.ConflictCount} skipped={snapshot.Summary.SkippedCount} laterPriority={snapshot.Settings.LaterProgramPriority} chain={snapshot.Settings.PseudoContinuousRecording} preStart={snapshot.Settings.PreStartMarginSeconds}s postEnd={snapshot.Settings.PostEndMarginSeconds}s");
        log.Add("TUNER_PRIORITY_CONTRACT", "Summary",
            $"sourcePriority=ProgramGuideLike(Manual,Immediate,KeywordSearch)>AutoSearch(Keyword)>Program activeChainExplicitOnly=True overlapUsesMargins=True" +
            $" sameServiceBoundaryPriority=True peerProgramGuideUsesLatestUserDecision=True autoSearchUsesRuleSortOrder=True programUsesRuleOrder=True capacityFirst=True" +
            $" laterPriority={snapshot.Settings.LaterProgramPriority} chain={snapshot.Settings.PseudoContinuousRecording}" +
            $" evaluated={snapshot.Summary.EvaluatedCount} conflict={snapshot.Summary.ConflictCount} rule=release_contract");
        log.Add("TUNER_CONFLICT_VISIBILITY_CONTRACT", "Summary",
            $"candidatePreflight=False conflictVisibleAfterReservation=True programGuideUnreservedAction=reserve" +
            $" finalSource=ReconcileFinalConflictPlanByUnitKey commonRoute=ALLOC_ROUTE/TUNER_ALLOC" +
            $" finalPriorityAxis=rebuilt capacityFirst=True adjacentBoundaryOverridesClickOrder=True evaluated={snapshot.Summary.EvaluatedCount} conflict={snapshot.Summary.ConflictCount} rule=release_contract");

        // release_contract:
        // 競合表示は、ユーザーが予約操作を確定して予約レコード化した後の状態表示である。
        // 未予約番組表セルに「予約すると競合」を予防表示する仮想競合投影は作らない。
        // ただし、予約後の判定は ReconcileFinalConflictPlanByUnitKey / ALLOC_ROUTE/TUNER_ALLOC を正本にする。
        // release_contract:
        // FINAL_* は ReconcileFinalConflictPlanByUnitKey の最終正本だけを可視化する。
        // 旧 occupancy_unit_evaluator_mirror は中間観測なので OCCUPANCY_* に格下げし、
        // FINAL_UNIT_CONFLICT / FINAL_UNIT_ASSIGN としては出さない。
        foreach (var t in snapshot.Trace.Where(t => string.Equals(t.Stage, "CHAIN_OCCUPANCY_UNIT", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "CHAIN_OCCUPANCY_APPLY", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "CHAIN_UNIT_TUNER_ASSIGN", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "CHAIN_UNIT_TUNER_DEFER", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_UNIT_PLAN_SUMMARY", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(t.Stage, "FINAL_CHAIN_UNIT_ASSIGN", StringComparison.OrdinalIgnoreCase)
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

        // release_contract:
        // 最終割当の正本計算と完全なJSONスナップショットは毎回維持する。
        // 通常ログだけは、同一割当の全件再列挙を避け、変更分と10分ごとの要約へ集約する。
        // 競合・チェーン・境界判断は診断上重要なので従来どおり毎回出力する。
        var currentFinalAssignments = snapshot.Trace
            .Where(t => string.Equals(t.Stage, "FINAL_UNIT_ASSIGN", StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => t.ReservationId)
            .ToDictionary(
                g => g.Key,
                g => g.Last(),
                EqualityComparer<int>.Default);

        static string BuildFinalUnitAssignmentLogState(TunerAllocationDebugTraceEntry entry)
        {
            // release_contract:
            // FINAL_UNIT_ASSIGN_SUMMARY の changed/stable は「物理Tuner割当が変わった予約数」を示す。
            // Detail には previousFreeAt など診断専用の可変値も含まれるため、全文比較してはならない。
            // 正式割当の識別に必要な group + tuner だけを比較し、診断値の変動を割当変更として数えない。
            const string tunerMarker = " tuner=";
            var markerIndex = entry.Detail.IndexOf(tunerMarker, StringComparison.Ordinal);
            var tuner = "-";
            if (markerIndex >= 0)
            {
                var valueStart = markerIndex + tunerMarker.Length;
                var valueEnd = entry.Detail.IndexOf(' ', valueStart);
                if (valueEnd < 0)
                    valueEnd = entry.Detail.Length;
                if (valueEnd > valueStart)
                    tuner = entry.Detail[valueStart..valueEnd];
            }

            return $"{entry.Group}|{tuner}";
        }

        lock (finalUnitAssignmentLogGate)
        {
            var changedAssignments = currentFinalAssignments
                .Where(kv => !lastFinalUnitAssignmentLogState.TryGetValue(kv.Key, out var previous)
                    || !string.Equals(previous, BuildFinalUnitAssignmentLogState(kv.Value), StringComparison.Ordinal))
                .Select(kv => kv.Value)
                .OrderBy(t => t.ReservationId)
                .ToList();
            var removedCount = lastFinalUnitAssignmentLogState.Keys.Count(id => !currentFinalAssignments.ContainsKey(id));
            var now = DateTime.UtcNow;
            var shouldWriteSummary = lastFinalUnitAssignmentLogState.Count == 0
                || changedAssignments.Count > 0
                || removedCount > 0
                || now - lastFinalUnitAssignmentSummaryAt >= FinalUnitAssignmentSummaryInterval;

            foreach (var t in changedAssignments)
            {
                log.Add("FINAL_UNIT_ASSIGN", $"R{t.ReservationId}",
                    $"group={t.Group} title={TrimTitleForAudit(t.Title)} rawTitleBlank={RawTitleBlankForAudit(t.Title)} {t.Detail}");
            }

            if (shouldWriteSummary)
            {
                log.Add("FINAL_UNIT_ASSIGN_SUMMARY", "Summary",
                    $"total={currentFinalAssignments.Count} changed={changedAssignments.Count} removed={removedCount} stable={Math.Max(0, currentFinalAssignments.Count - changedAssignments.Count)} detailSnapshot=tuner_allocation_debug.json rule=release_contract");
                lastFinalUnitAssignmentSummaryAt = now;
            }

            lastFinalUnitAssignmentLogState = currentFinalAssignments
                .ToDictionary(kv => kv.Key, kv => BuildFinalUnitAssignmentLogState(kv.Value));
        }

        foreach (var t in snapshot.Trace.Where(t => string.Equals(t.Stage, "CHAIN_ALLOC_LOCK", StringComparison.OrdinalIgnoreCase)))
        {
            log.Add("CHAIN_ALLOC_LOCK", $"R{t.ReservationId}",
                $"group={t.Group} title={TrimTitleForAudit(t.Title)} rawTitleBlank={RawTitleBlankForAudit(t.Title)} {t.Detail} rule=common_allocation_route_contract");
        }

        // release_contract: 通常運用ログの静音化。
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
                    $"stage=tuner_conflict_mirror service={e.ServiceName} title={TrimTitleForAudit(e.Title)} rawTitleBlank={RawTitleBlankForAudit(e.Title)} res=R{e.ReservationId} source={e.Source} enabled={e.IsEnabled} tuner={tuner} result={e.Result} reason={e.Reason} group={e.Group} svcId={e.ServiceId} occupy={e.OccupyStart:MM/dd HH:mm:ss}〜{e.OccupyEnd:MM/dd HH:mm:ss}{chain} source=TUNER_CONFLICT_MIRROR rule=release_contract");
            }
        }


        // release_contract:
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
                $"count={skipped.Count} disabled={disabled} epgExcluded={epgExcluded} other={other} rule=release_contract");

            foreach (var e in skipped.Where(e => !string.Equals(e.Reason, "disabled_by_user", StringComparison.OrdinalIgnoreCase)
                                             && !string.Equals(e.Reason, "epg_source_excluded", StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                log.Add("TUNER_SKIP", "Skip",
                    $"service={e.ServiceName} title={TrimTitleForAudit(e.Title)} rawTitleBlank={RawTitleBlankForAudit(e.Title)} res=R{e.ReservationId} source={e.Source} enabled={e.IsEnabled} result={e.Result} reason={e.Reason} group={e.Group} svcId={e.ServiceId} occupy={e.OccupyStart:MM/dd HH:mm:ss}〜{e.OccupyEnd:MM/dd HH:mm:ss} rule=release_contract");
            }
        }
    }
#endif

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


    // ─── 録画停止後の同一発生回再録画抑止 ─────────────────────────────

    /// <summary>
    /// ユーザーが録画停止した発生回を、番組終了後まで自動経路から再生成・再開始しないために記録する。
    /// 予約IDではなく発生回キーで保持するが、ユーザーの新しい明示予約はSource判定で常に優先する。
    /// </summary>
    public void AddManualStoppedOccurrence(Reservation r, DateTime expiresAt, string reason)
    {
        if (r.Source == ReservationSource.Epg)
            return;

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO manual_stopped_occurrences
              (network_id, transport_stream_id, service_id, event_id, start_time, end_time, title_hash,
               stopped_source, source_rule_id, reservation_id, expires_at, created_at)
            VALUES
              ($nid, $tsid, $sid, $eventId, $start, $end, $hash,
               $source, $rule, $reservationId, $expires, $created);
            """;
        cmd.Parameters.AddWithValue("$nid", r.NetworkId);
        cmd.Parameters.AddWithValue("$tsid", r.TransportStreamId);
        cmd.Parameters.AddWithValue("$sid", r.ServiceId);
        cmd.Parameters.AddWithValue("$eventId", r.EventId);
        cmd.Parameters.AddWithValue("$start", r.StartTime.ToString("O"));
        cmd.Parameters.AddWithValue("$end", r.EndTime.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", BuildTitleHash(r.Title));
        cmd.Parameters.AddWithValue("$source", r.Source.ToString().ToLowerInvariant());
        if (r.SourceRuleId.HasValue) cmd.Parameters.AddWithValue("$rule", r.SourceRuleId.Value); else cmd.Parameters.AddWithValue("$rule", DBNull.Value);
        cmd.Parameters.AddWithValue("$reservationId", r.Id);
        cmd.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
        cmd.Parameters.AddWithValue("$created", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();

        log.Add("MANUAL_STOP_OCCURRENCE", $"R{r.Id}",
            $"result=REGISTER reason={reason} service={TrimForAudit(r.ServiceName)} title={TrimTitleForAudit(r.Title)} rawTitleBlank={RawTitleBlankForAudit(r.Title)} source={r.Source} ruleId={(r.SourceRuleId?.ToString() ?? "-")} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} eventId={r.EventId} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} expires={expiresAt:MM/dd HH:mm:ss} scope=automatic_sources_only explicitSources=Manual,Immediate,KeywordSearch rule=release_contract");
    }

    public bool IsManualStoppedOccurrenceSuppressed(Reservation r)
    {
        // ユーザーが明示的に作成した予約は、過去の停止意思より新しい意思として必ず優先する。
        // 抑止キー自体は番組終了まで保持し、自動生成経路（Keyword / Program）だけを止め続ける。
        if (IsExplicitUserReservationSource(r.Source) || r.Source == ReservationSource.Epg)
            return false;
        return IsManualStoppedOccurrenceSuppressed(r.NetworkId, r.TransportStreamId, r.ServiceId, r.EventId, r.StartTime, r.EndTime, r.Title);
    }

    private static bool IsExplicitUserReservationSource(ReservationSource source)
        => source is ReservationSource.Manual or ReservationSource.Immediate or ReservationSource.KeywordSearch;

    public bool IsManualStoppedOccurrenceSuppressed(EpgEvent ev)
        => IsManualStoppedOccurrenceSuppressed(ev.NetworkId, ev.TransportStreamId, ev.ServiceId, ev.EventId, ev.Start, ev.End, ev.Title);

    public bool IsManualStoppedOccurrenceSuppressed(ushort networkId, ushort tsId, ushort serviceId, ushort eventId, DateTime start, DateTime end, string? title)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM manual_stopped_occurrences
            WHERE network_id = $nid
              AND transport_stream_id = $tsid
              AND service_id = $sid
              AND expires_at >= $now
              AND (
                    ($eventId <> 0 AND event_id = $eventId)
                 OR (($eventId = 0 OR event_id = 0)
                     AND start_time < $end AND end_time > $start
                     AND title_hash = $hash)
              )
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$nid", networkId);
        cmd.Parameters.AddWithValue("$tsid", tsId);
        cmd.Parameters.AddWithValue("$sid", serviceId);
        cmd.Parameters.AddWithValue("$eventId", eventId);
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", BuildTitleHash(title));
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        return cmd.ExecuteScalar() is not null;
    }

    public int PurgeExpiredManualStoppedOccurrences(DateTime? now = null)
    {
        var t = (now ?? DateTime.Now).ToString("O");
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM manual_stopped_occurrences WHERE expires_at < $now;";
        cmd.Parameters.AddWithValue("$now", t);
        return cmd.ExecuteNonQuery();
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
            $"service={TrimForAudit(r.ServiceName)} title={TrimTitleForAudit(r.Title)} rawTitleBlank={RawTitleBlankForAudit(r.Title)} result={(deleted > 0 ? "DELETE" : "NONE")} reason={reason} ruleId={r.SourceRuleId.Value} res=R{r.Id} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} rule=release_contract");
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
                $"service={TrimForAudit(r.ServiceName)} title={TrimTitleForAudit(r.Title)} rawTitleBlank={RawTitleBlankForAudit(r.Title)} result={(inserted > 0 ? "INSERT" : "EXISTS")} reason={reason} ruleId={r.SourceRuleId.Value} res=R{r.Id} nid={r.NetworkId} tsid={r.TransportStreamId} sid={r.ServiceId} start={r.StartTime:MM/dd HH:mm:ss} end={r.EndTime:MM/dd HH:mm:ss} rule=release_contract");
        }
    }

    /// <summary>自動検索の再マッチ時に、今回限り取消済みの放送回かどうかを軽量キーで判定する。</summary>
    public bool IsKeywordCancelOnceSuppressed(int ruleId, EpgEvent ev)
        => IsKeywordCancelOnceSuppressed(ruleId, ev.NetworkId, ev.TransportStreamId, ev.ServiceId, ev.Start, ev.End, ev.Title);

    public bool IsKeywordCancelOnceSuppressed(int ruleId, ushort networkId, ushort transportStreamId, ushort serviceId, DateTime start, DateTime end, string? title)
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
        cmd.Parameters.AddWithValue("$nid", networkId);
        cmd.Parameters.AddWithValue("$tsid", transportStreamId);
        cmd.Parameters.AddWithValue("$sid", serviceId);
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", BuildTitleHash(title));
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
        cmd.CommandText = $"""
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

        // PRERECORD_EPG_CANCEL_VERSION_INVARIANT:
        // 内部EPG子予約の取消も状態遷移である。開始済み行は対象外とし、成功時にDataVersionを進める。
        using var con = db.Open();
        using var tx = con.BeginTransaction();
        var total = 0;
        foreach (var parentId in ids)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE reservations
                SET status = 'cancelled',
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE source = 'epg'
                  AND status = 'scheduled'
                  AND source_rule_id = $parentId
                  AND reservation_intent = '{ReservationIntentContract.SystemPreRecordEpgStorageValue}';
                """;
            cmd.Parameters.AddWithValue("$parentId", parentId);
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
            total += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return total;
    }

    public int UpdateScheduledKeywordReservationRuleName(int ruleId, string ruleName)
    {
        using var con = db.Open();
        using var tx = con.BeginTransaction();

        var candidates = new List<(int Id, long DataVersion)>();
        using (var sel = con.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT id, data_version
                FROM reservations
                WHERE source = 'keyword'
                  AND status = 'scheduled'
                  AND source_rule_id = $ruleId;
                """;
            sel.Parameters.AddWithValue("$ruleId", ruleId);
            using var reader = sel.ExecuteReader();
            while (reader.Read())
                candidates.Add((reader.GetInt32(0), reader.GetInt64(1)));
        }

        // KEYWORD_RULE_NAME_VERSION_CAS_INVARIANT:
        // 自動検索ルール名同期も予約更新である。選択後に開始・編集された行へ古いルール名を上書きしない。
        var total = 0;
        foreach (var candidate in candidates)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE reservations
                SET source_rule_name = $ruleName,
                    data_version = data_version + 1,
                    updated_at = $now
                WHERE id = $id
                  AND source = 'keyword'
                  AND status = 'scheduled'
                  AND source_rule_id = $ruleId
                  AND data_version = $dataVersion;
                """;
            cmd.Parameters.AddWithValue("$ruleName", ruleName ?? string.Empty);
            cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$id", candidate.Id);
            cmd.Parameters.AddWithValue("$ruleId", ruleId);
            cmd.Parameters.AddWithValue("$dataVersion", candidate.DataVersion);
            total += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return total;
    }

    public int DeleteScheduledKeywordReservationsByIds(int ruleId, IReadOnlyCollection<int> reservationIds)
    {
        if (reservationIds.Count == 0) return 0;

        var ids = reservationIds.Where(id => id > 0).Distinct().ToHashSet();
        if (ids.Count == 0) return 0;

        using var con = db.Open();
        using var tx = con.BeginTransaction();
        var candidates = ReadScheduledKeywordReservations(con, tx, ruleId)
            .Where(r => ids.Contains(r.Id))
            .ToList();
        var removed = DeleteScheduledReservations(con, tx, candidates, ReservationSource.Keyword, ruleId, requireDataVersionMatch: true);
        tx.Commit();
        RecordProjectedReservationRemovals(removed, "keyword_rule_reconcile");
        return removed.Count;
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
        using var tx = con.BeginTransaction();
        var candidates = ReadScheduledKeywordReservations(con, tx, ruleId);
        var removed = DeleteScheduledReservations(con, tx, candidates, ReservationSource.Keyword, ruleId, requireDataVersionMatch: false);
        tx.Commit();
        RecordProjectedReservationRemovals(removed, "keyword_rule_removed");
        return removed.Count;
    }


    private static IReadOnlyList<Reservation> ReadScheduledKeywordReservations(
        SqliteConnection con,
        SqliteTransaction tx,
        int ruleId)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                   recording_started_at, recording_finished_at, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id,
                   recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                   reservation_intent, created_through, created_by_plugin_id
            FROM reservations
            WHERE source = 'keyword'
              AND status = 'scheduled'
              AND source_rule_id = $ruleId;
            """;
        cmd.Parameters.AddWithValue("$ruleId", ruleId);
        return ReadReservations(cmd, projectRuntimeStatus: false);
    }

    private static List<Reservation> DeleteScheduledReservations(
        SqliteConnection con,
        SqliteTransaction tx,
        IReadOnlyCollection<Reservation> candidates,
        ReservationSource source,
        int ruleId,
        bool requireDataVersionMatch)
    {
        var removed = new List<Reservation>();
        var sourceValue = source.ToString().ToLowerInvariant();
        foreach (var candidate in candidates)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = requireDataVersionMatch
                ? """
                    DELETE FROM reservations
                    WHERE id = $id
                      AND source = $source
                      AND status = 'scheduled'
                      AND source_rule_id = $ruleId
                      AND data_version = $dataVersion;
                    """
                : """
                    DELETE FROM reservations
                    WHERE id = $id
                      AND source = $source
                      AND status = 'scheduled'
                      AND source_rule_id = $ruleId;
                    """;
            cmd.Parameters.AddWithValue("$id", candidate.Id);
            cmd.Parameters.AddWithValue("$source", sourceValue);
            cmd.Parameters.AddWithValue("$ruleId", ruleId);
            if (requireDataVersionMatch)
                cmd.Parameters.AddWithValue("$dataVersion", candidate.DataVersion);
            if (cmd.ExecuteNonQuery() == 1)
                removed.Add(candidate);
        }
        return removed;
    }

    private void RecordProjectedReservationRemovals(IEnumerable<Reservation> removed, string reason)
    {
        var metadata = new Dictionary<string, string?>
        {
            ["suppressUserEvent"] = "projection_cleanup",
            ["reason"] = reason
        };
        foreach (var reservation in removed)
            mutationJournal.Record(ReservationMutationKind.Removed, reservation, null, metadata);
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
    /// 有効期限切れのキーワードルールと、そのルール由来のScheduled自動検索予約を
    /// 同一Connection・同一Transactionで物理削除する。
    /// 
    /// source_rule_idで期限切れルール由来の行だけを限定し、他の有効ルール、
    /// recording / completed / failed / cancelled の予約には触れない。
    /// 予約削除とルール削除の片方だけが確定する中間状態を残してはならないため、
    /// 全対象を一つのTransactionで確定する。
    /// </summary>
    /// <returns>削除したルール件数</returns>
    public int PurgeExpiredKeywordRules()
    {
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        using var con = db.Open();
        using var tx = con.BeginTransaction();

        var expiredIds = new List<int>();
        using (var sel = con.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT id FROM keyword_rules WHERE expires_on <> '' AND expires_on < $today;";
            sel.Parameters.AddWithValue("$today", today);
            using var reader = sel.ExecuteReader();
            while (reader.Read()) expiredIds.Add(reader.GetInt32(0));
        }

        if (expiredIds.Count == 0)
        {
            tx.Commit();
            return 0;
        }

        var removedReservations = new List<Reservation>();
        foreach (var ruleId in expiredIds)
        {
            var candidates = ReadScheduledKeywordReservations(con, tx, ruleId);
            removedReservations.AddRange(DeleteScheduledReservations(con, tx, candidates, ReservationSource.Keyword, ruleId, requireDataVersionMatch: false));

            using var deleteRule = con.CreateCommand();
            deleteRule.Transaction = tx;
            deleteRule.CommandText = "DELETE FROM keyword_rules WHERE id = $ruleId;";
            deleteRule.Parameters.AddWithValue("$ruleId", ruleId);
            deleteRule.ExecuteNonQuery();
        }

        tx.Commit();
        RecordProjectedReservationRemovals(removedReservations, "keyword_rule_expired");
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
    /// ProgramGuideBlankクリックから作られた暫定プログラム予約ルールを、有効期限切れ後に掃除する。
    /// 運用中のScheduled予約だけを削除し、完了・失敗・取消済みの履歴は通常の履歴保持へ委ねる。
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
                    targets.Add((candidate.Id, candidate.Name, candidate.ExpiresOn));
            }
        }

        if (targets.Count == 0) return 0;

        using var tx = con.BeginTransaction();
        var removedReservations = new List<Reservation>();
        foreach (var target in targets)
        {
            using (var selReservations = con.CreateCommand())
            {
                selReservations.Transaction = tx;
                selReservations.CommandText = """
                    SELECT id, network_id, transport_stream_id, service_id, event_id,
                           title, start_time, end_time, status, source, created_at, updated_at,
                           channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                           recording_started_at, recording_finished_at, service_name,
                           scheduled_start_time, source_rule_id, source_rule_name,
                           is_user_chain, user_chain_previous_id, user_chain_root_id,
                           recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                           reservation_intent, created_through, created_by_plugin_id
                    FROM reservations
                    WHERE source = 'program'
                      AND source_rule_id = $ruleId
                      AND status = 'scheduled';
                    """;
                selReservations.Parameters.AddWithValue("$ruleId", target.Id);
                var candidates = ReadReservations(selReservations, projectRuntimeStatus: false);
                foreach (var candidate in candidates)
                {
                    using var delReservation = con.CreateCommand();
                    delReservation.Transaction = tx;
                    delReservation.CommandText = """
                        DELETE FROM reservations
                        WHERE id = $id
                          AND source = 'program'
                          AND status = 'scheduled'
                          AND source_rule_id = $ruleId
                          AND data_version = $dataVersion;
                        """;
                    delReservation.Parameters.AddWithValue("$id", candidate.Id);
                    delReservation.Parameters.AddWithValue("$ruleId", target.Id);
                    delReservation.Parameters.AddWithValue("$dataVersion", candidate.DataVersion);
                    if (delReservation.ExecuteNonQuery() == 1)
                        removedReservations.Add(candidate);
                }
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
        RecordProjectedReservationRemovals(removedReservations, "program_guide_missing_rule_expired");

        log.Add("PROGRAM_PLACEHOLDER_EXPIRED_CLEANUP", "ProgramRule",
            $"result=OK deletedRules={targets.Count} deletedScheduledReservations={removedReservations.Count} ruleIds=[{string.Join(',', targets.Select(x => x.Id))}] reservationIds=[{string.Join(',', removedReservations.Select(x => x.Id).Distinct().OrderBy(x => x).Select(x => $"R{x}"))}] terminalHistory=preserved today={today} scope=program_guide_missing_only originClassifier=ReservationOriginClassifier normalProgramRules=untouched rule=release_contract");
        return targets.Count;
    }

    // ─── 内部ヘルパー ────────────────────────────────────────────

    private static IReadOnlyList<Reservation> ReadReservations(
        SqliteCommand cmd,
        bool projectRuntimeStatus = true)
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
                RecordingRecoveryChainId = r.IsDBNull(26) ? "" : r.GetString(26),
                RecoveryParentReservationId = r.IsDBNull(27) ? (int?)null : r.GetInt32(27),
                DataVersion = r.FieldCount > 28 && !r.IsDBNull(28) ? r.GetInt64(28) : 0L,
                Intent = r.FieldCount > 29 && !r.IsDBNull(29) ? ParseIntent(r.GetString(29)) : ReservationIntent.Unspecified,
                CreatedThrough = r.FieldCount > 30 && !r.IsDBNull(30) ? r.GetString(30) : string.Empty,
                CreatedByPluginId = r.FieldCount > 31 && !r.IsDBNull(31) ? r.GetString(31) : string.Empty,
            };
            // 永続ライフサイクル状態と表示用Runtime状態を混在させない。
            // Lifecycle/CAS callers pass false and always observe the exact SQLite status.
            if (projectRuntimeStatus)
            {
                var projectedStatus = ProjectRuntimeStatus(reservation);
                if (projectedStatus == ReservationStatus.Recording)
                {
                    reservation.Status = ReservationStatus.Recording;
                    reservation.IsConflicted = false;
                }
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
            log.Add("PROGRAM_RULE_ORIGIN_AUDIT", "Sync", $"total={rules.Count} explicitProgramRules={explicitProgramRules} programGuideMissingProgramRules={programGuideBlankProgramRules} classifier=ReservationOriginClassifier commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=release_contract");
        var evaluatedProgramRules = 0;
        foreach (var rule in rules)
        {
            evaluatedProgramRules++;
            SyncProgramRuleReservation(rule, now);
        }

        if (evaluatedProgramRules > 0)
            log.Add("RESERVE_ENTRY", "ProgramResolveSummary", $"result=OK evaluated={evaluatedProgramRules} okDetails=diagnostic_only errorsAndFallbacks=regular_log rule=program_projection_channel_resolve_release_summary");

        // ProgramRule同期は未開始の投影予約だけを再構成する。Recording以降は実行正本であり、
        // ルール削除・再計算・重複整理を理由に削除または上書きしてはならない。
        DeleteScheduledProgramReservations(
            "source_rule_id IS NOT NULL AND source_rule_id NOT IN (SELECT id FROM program_rules)",
            bind: null,
            reason: "program_rule_removed");
    }

    private void SyncProgramRuleReservation(ProgramRule rule, DateTime now)
    {
        using var con = db.Open();

        if (!TryBuildNextProgramReservation(rule, now, out var reservation, out var serviceResolutionUnavailable))
        {
            if (serviceResolutionUnavailable)
            {
                // Legacy/incomplete service identity that cannot be resolved unambiguously is not
                // authority to delete an already-scheduled Program reservation. Preserve current
                // rows and wait for channel metadata or an explicit user edit to resolve identity.
                log.Add("RESERVE_ENTRY", "ProgramSync",
                    $"result=PRESERVED ruleId={rule.Id} name=[{rule.Name}] reason=service_identity_unresolved nid={rule.NetworkId} tsid={rule.TransportStreamId} sid={rule.ServiceId} action=no_new_projection_no_delete rule=service_identity_contract");
                return;
            }

            var deleted = DeleteScheduledProgramReservations(
                "source_rule_id = $ruleId",
                cmd => cmd.Parameters.AddWithValue("$ruleId", rule.Id),
                "program_rule_no_next_occurrence");
            if (deleted > 0)
                log.Add("RESERVE_ENTRY", "ProgramSync", $"result=REMOVED ruleId={rule.Id} name=[{rule.Name}] reason=no_next_occurrence deleted={deleted} rule=release_contract");
            return;
        }

        if (IsManualStoppedOccurrenceSuppressed(reservation))
        {
            var removed = DeleteScheduledProgramReservations(
                "source_rule_id = $ruleId",
                cmd => cmd.Parameters.AddWithValue("$ruleId", rule.Id),
                "program_manual_stop_suppression");
            log.Add("RESERVE_ENTRY", "ProgramSync",
                $"result=SKIP_AUTOMATIC_RERECORD_AFTER_MANUAL_STOP removed={removed} ruleId={rule.Id} name=[{rule.Name}] service=[{reservation.ServiceName}] title=[{ReservationTitleDisplayContract.ForLog(reservation.Title)}] rawTitleBlank={ReservationTitleDisplayContract.RawBlankFlag(reservation.Title)} start={reservation.StartTime:MM/dd HH:mm} end={reservation.EndTime:MM/dd HH:mm} rule=release_contract");
            return;
        }

        // release_contract:
        // ProgramRule の投影予約は、ALLOC_ROUTE 内や PreRecEpg 再評価から複数回同期される。
        // 従来は毎回 scheduled/recording を削除して再挿入していたため、短時間に R851/R852 のような
        // ID churn が発生し、ログ上は重複生成に見え、Wake 差分再構築も増えやすかった。
        // 同じ ruleId・同じ次回発生時刻の予約が既にある場合は維持し、余分な重複だけ削除する。
        using (var existing = con.CreateCommand())
        {
            existing.CommandText = """
                SELECT id, network_id, transport_stream_id, service_id, title, start_time, end_time, is_enabled, service_name, channel_argument, status, data_version
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
                var existingStatus = ParseStatus(r.IsDBNull(10) ? string.Empty : r.GetString(10));
                var existingDataVersion = r.IsDBNull(11) ? 0L : r.GetInt64(11);
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
                    if (metadataChanged && existingStatus == ReservationStatus.Scheduled)
                    {
                        // PROGRAM_PROJECTION_VERSION_CAS_INVARIANT:
                        // Program投影の補助メタデータであっても、判定後に開始・編集された予約へ
                        // 古い同期結果を上書きしない。Recording以降は一切変更しない。
                        using var fix = con.CreateCommand();
                        fix.CommandText = """
                            UPDATE reservations
                            SET service_name = $svcname,
                                channel_argument = $charg,
                                data_version = data_version + 1,
                                updated_at = $now
                            WHERE id = $id
                              AND source = 'program'
                              AND status = 'scheduled'
                              AND data_version = $dataVersion;
                            """;
                        fix.Parameters.AddWithValue("$svcname", reservation.ServiceName);
                        fix.Parameters.AddWithValue("$charg", reservation.ChannelArgument);
                        fix.Parameters.AddWithValue("$now", now.ToString("O"));
                        fix.Parameters.AddWithValue("$id", id);
                        fix.Parameters.AddWithValue("$dataVersion", existingDataVersion);
                        var fixedRows = fix.ExecuteNonQuery();
                        log.Add("RESERVE_ENTRY", "ProgramSync",
                            fixedRows > 0
                                ? $"result=METADATA_FIXED keep=R{id} ruleId={rule.Id} name=[{rule.Name}] service=[{reservation.ServiceName}] channel=[{reservation.ChannelArgument}] oldService=[{existingServiceName}] oldChannel=[{existingChannelArgument}] rule=release_contract"
                                : $"result=METADATA_CAS_REJECTED keep=R{id} ruleId={rule.Id} name=[{rule.Name}] reason=state_or_version_changed action=preserve_runtime_or_newer_edit rule=release_contract");
                    }
                }
                else if (existingStatus == ReservationStatus.Scheduled)
                {
                    duplicateIds.Add(id);
                }
                else
                {
                    // Recording以降のProgram予約は重複整理でも削除しない。
                    log.Add("RESERVE_ENTRY", "ProgramSync",
                        $"result=RUNTIME_PRESERVED id=R{id} status={existingStatus} ruleId={rule.Id} name=[{rule.Name}] reason=program_projection_must_not_delete_runtime rule=release_contract");
                }
            }
            r.Dispose();

            if (keepId is not null)
            {
                if (duplicateIds.Count > 0)
                {
                    var removed = DeleteScheduledProgramReservations(
                        $"source_rule_id = $ruleId AND id IN ({string.Join(',', duplicateIds)})",
                        cmd => cmd.Parameters.AddWithValue("$ruleId", rule.Id),
                        "program_rule_deduplicate");
                    log.Add("RESERVE_ENTRY", "ProgramSync", $"result=DEDUP keep=R{keepId.Value} removed={removed} ruleId={rule.Id} name=[{rule.Name}] rule=release_contract");
                }
                return;
            }
        }

        using (var crossRuleDup = con.CreateCommand())
        {
            // release_contract:
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
                var removed = DeleteScheduledProgramReservations(
                    "source_rule_id = $ruleId",
                    cmd => cmd.Parameters.AddWithValue("$ruleId", rule.Id),
                    "program_cross_rule_duplicate");
                log.Add("RESERVE_ENTRY", "ProgramSync",
                    $"result=SKIP_CROSS_RULE_DUPLICATE keep=R{Convert.ToInt32(keepDuplicate)} removedForRule={removed} ruleId={rule.Id} name=[{rule.Name}] title=[{ReservationTitleDisplayContract.ForLog(reservation.Title)}] rawTitleBlank={ReservationTitleDisplayContract.RawBlankFlag(reservation.Title)} start={reservation.StartTime:MM/dd HH:mm} rule=release_contract");
                return;
            }
        }

        DeleteScheduledProgramReservations(
            "source_rule_id = $ruleId",
            cmd => cmd.Parameters.AddWithValue("$ruleId", rule.Id),
            "program_projection_replaced");

        using var ins = con.CreateCommand();
        ins.CommandText = """
            INSERT INTO reservations
              (network_id, transport_stream_id, service_id, event_id,
               title, start_time, end_time, status, source, created_at, updated_at,
               channel_argument, is_conflicted, is_enabled, tuner_name, service_name,
               scheduled_start_time, source_rule_id, source_rule_name,
               reservation_intent, created_through, created_by_plugin_id)
            VALUES
              ($nid, $tsid, $sid, $eid,
               $title, $start, $end, $status, $source, $now, $now,
               $charg, 0, $enabled, '', $svcname,
               $start, $sourceRuleId, $sourceRuleName,
               'programtimeslot', 'Host', '');
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
        log.Add("RESERVE_ENTRY", "Program", $"共通入口要求/投影 source=Program id=R{insertedId} ruleId={rule.Id} name=[{rule.Name}] dayOfWeek={rule.DayOfWeek} start={reservation.StartTime:MM/dd HH:mm} end={reservation.EndTime:MM/dd HH:mm} service=[{reservation.ServiceName}] rule=release_contract");

        // ProgramRule projection also records its committed mutation through the common journal.
        var addedProgramReservation = GetById(insertedId);
        if (addedProgramReservation is not null)
            mutationJournal.Record(ReservationMutationKind.Added, null, addedProgramReservation);
    }


    private int DeleteScheduledProgramReservations(
        string predicate,
        Action<SqliteCommand>? bind,
        string reason)
    {
        using var con = db.Open();
        using var tx = con.BeginTransaction();
        using var select = con.CreateCommand();
        select.Transaction = tx;
        select.CommandText = $"""
            SELECT id, network_id, transport_stream_id, service_id, event_id,
                   title, start_time, end_time, status, source, created_at, updated_at,
                   channel_argument, is_conflicted, is_enabled, tuner_name, actual_tuner_name,
                   recording_started_at, recording_finished_at, service_name,
                   scheduled_start_time, source_rule_id, source_rule_name,
                   is_user_chain, user_chain_previous_id, user_chain_root_id,
                   recording_recovery_chain_id, recovery_parent_reservation_id, data_version,
                   reservation_intent, created_through, created_by_plugin_id
            FROM reservations
            WHERE source = 'program'
              AND status = 'scheduled'
              AND ({predicate});
            """;
        bind?.Invoke(select);
        var candidates = ReadReservations(select, projectRuntimeStatus: false);
        var removed = new List<Reservation>();
        foreach (var candidate in candidates)
        {
            using var delete = con.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = """
                DELETE FROM reservations
                WHERE id = $id
                  AND source = 'program'
                  AND status = 'scheduled'
                  AND data_version = $dataVersion;
                """;
            delete.Parameters.AddWithValue("$id", candidate.Id);
            delete.Parameters.AddWithValue("$dataVersion", candidate.DataVersion);
            if (delete.ExecuteNonQuery() == 1)
                removed.Add(candidate);
        }
        tx.Commit();
        RecordProjectedReservationRemovals(removed, reason);
        return removed.Count;
    }

    private ProgramRuleChannelResolution ResolveProgramRuleChannel(ProgramRule rule)
    {
        try
        {
            var targets = channelLoader.Load().Targets;
            ChannelTarget? target = null;

            if (rule.NetworkId > 0 && rule.TransportStreamId > 0 && rule.ServiceId > 0)
            {
                target = ServiceIdentityContract.ResolveTarget(
                    targets,
                    rule.NetworkId,
                    rule.TransportStreamId,
                    rule.ServiceId);
            }

            // Legacy ProgramRule compatibility only: older rows may carry SID without a complete triplet.
            // New/current rules use exact NID/TSID/SID. Promote a legacy SID only when current metadata resolves it unambiguously.
            if (target is null && rule.ServiceId > 0)
            {
                // Legacy compatibility may use whichever identity components actually exist in the row,
                // but it must still resolve to exactly one current ChannelTarget. Never pick the first
                // TSID/SID match when NID is absent or contradictory.
                var legacyMatches = targets
                    .Where(t => t.ServiceId == rule.ServiceId)
                    .Where(t => rule.NetworkId == 0 || t.OriginalNetworkId == rule.NetworkId)
                    .Where(t => rule.TransportStreamId == 0 || t.TransportStreamId == rule.TransportStreamId)
                    .Take(2)
                    .ToList();
                if (legacyMatches.Count == 1)
                    target = legacyMatches[0];
            }

            if (target is not null)
            {
                // release_contract: successful ProgramResolve rows are expected on every startup.
                // Keep success details in diagnostic mode conceptually; regular logs use ProgramResolveSummary.
                return new ProgramRuleChannelResolution(
                    target.OriginalNetworkId,
                    target.TransportStreamId,
                    target.ServiceId,
                    target.Name,
                    target.ChannelArgument,
                    true);
            }
        }
        catch (Exception ex)
        {
            log.Add("RESERVE_ENTRY", "ProgramResolve", $"result=ERROR ruleId={rule.Id} name=[{rule.Name}] sid={rule.ServiceId} error={ex.GetType().Name}:{ex.Message} action=no_guess_no_new_projection rule=service_identity_contract");
        }

        var fallbackName = rule.ServiceId > 0 ? $"SID{rule.ServiceId}" : string.Empty;
        log.Add("RESERVE_ENTRY", "ProgramResolve", $"result=UNRESOLVED ruleId={rule.Id} name=[{rule.Name}] nid={rule.NetworkId} tsid={rule.TransportStreamId} sid={rule.ServiceId} service=[{fallbackName}] channel=[] action=no_guess_no_new_projection rule=service_identity_contract");
        return new ProgramRuleChannelResolution(rule.NetworkId, rule.TransportStreamId, rule.ServiceId, fallbackName, string.Empty, false);
    }

    private bool TryBuildNextProgramReservation(ProgramRule rule, DateTime now, out Reservation reservation, out bool serviceResolutionUnavailable)
    {
        reservation = new Reservation();
        serviceResolutionUnavailable = false;

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
            if (!resolved.IsResolved)
            {
                serviceResolutionUnavailable = true;
                return false;
            }

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
        string ChannelArgument,
        bool IsResolved);

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
        "starting"   => ReservationStatus.Starting,
        "recording"  => ReservationStatus.Recording,
        "stopping"   => ReservationStatus.Stopping,
        "completed"  => ReservationStatus.Completed,
        "cancelled"  => ReservationStatus.Cancelled,
        "failed"     => ReservationStatus.Failed,
        _            => ReservationStatus.Scheduled
    };

    private static ReservationIntent ParseIntent(string s)
        => Enum.TryParse<ReservationIntent>(s, true, out var v) ? v : ReservationIntent.Unspecified;

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

