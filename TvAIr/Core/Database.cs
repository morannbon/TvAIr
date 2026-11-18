using Microsoft.Data.Sqlite;

namespace TvAIr.Core;

/// <summary>
/// SQLiteデータベースの初期化・接続管理。
/// DBファイルは再生成可能な実行時ストアとして扱う。
/// 自動検索ルールの移行はJSON export/importを正本にする。
/// </summary>
public sealed class Database
{
    private readonly string dbPath;

    public string DataDirectory { get; }
    public string DbPath => dbPath;

    public Database(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        DataDirectory = dataDirectory;
        dbPath = Path.Combine(dataDirectory, "tvair.db");
        Initialize();
    }

    /// <summary>接続を開いて返す。呼び出し元でusingすること。</summary>
    public SqliteConnection Open()
    {
        var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return con;
    }

    private SqliteConnection OpenConnection()
    {
        var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();
        return con;
    }

    // ─── 初期化 ──────────────────────────────────────────────────

    private void Initialize()
    {
        using var con = OpenConnection();
        using (var pragma = con.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        CreateTables(con);
        using var bindingTransaction = con.BeginTransaction();
        using var bindingTrigger = con.CreateCommand();
        bindingTrigger.Transaction = bindingTransaction;
        bindingTrigger.CommandText = $$"""
            DROP TRIGGER IF EXISTS trg_keyword_reservation_plugin_binding;
            CREATE TRIGGER trg_keyword_reservation_plugin_binding
            AFTER INSERT ON reservations
            WHEN NEW.recovery_parent_reservation_id IS NOT NULL
              OR (NEW.source_rule_id IS NOT NULL AND lower(NEW.source) = 'keyword')
            BEGIN
                -- Recovery inherits the saved reservation snapshot, even if empty.
                -- Only an original keyword reservation reads the current rule.
                SELECT RAISE(ABORT, 'Recovery binding parent does not exist')
                WHERE NEW.recovery_parent_reservation_id IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM reservations
                                  WHERE id = NEW.recovery_parent_reservation_id);
                UPDATE reservations
                SET plugin_binding_json = CASE
                    WHEN NEW.recovery_parent_reservation_id IS NOT NULL THEN
                        (SELECT plugin_binding_json FROM reservations
                         WHERE id = NEW.recovery_parent_reservation_id)
                    ELSE {{KeywordReservationBindingProjection.RuleValue("NEW.source_rule_id")}}
                    END
                WHERE id = NEW.id;
            END;
            """;
        bindingTrigger.ExecuteNonQuery();
        bindingTransaction.Commit();
    }

    private static void CreateTables(SqliteConnection con)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS epg_events (
                network_id              INTEGER NOT NULL DEFAULT 0,
                transport_stream_id     INTEGER NOT NULL DEFAULT 0,
                service_id              INTEGER NOT NULL,
                event_id                INTEGER NOT NULL,
                service_name            TEXT    NOT NULL DEFAULT '',
                title                   TEXT    NOT NULL DEFAULT '',
                description             TEXT    NOT NULL DEFAULT '',
                genre                   TEXT    NOT NULL DEFAULT '',
                genre_codes             TEXT    NOT NULL DEFAULT '',
                table_id                INTEGER NOT NULL DEFAULT 0,
                section_number          INTEGER NOT NULL DEFAULT 0,
                version_number          INTEGER NOT NULL DEFAULT 0,
                raw_descriptor_loop     TEXT    NOT NULL DEFAULT '',
                raw_short_event_descriptor TEXT NOT NULL DEFAULT '',
                raw_extended_event_descriptor TEXT NOT NULL DEFAULT '',
                raw_content_descriptor  TEXT    NOT NULL DEFAULT '',
                duration_seconds        INTEGER NOT NULL DEFAULT 0,
                start_time              TEXT    NOT NULL,
                end_time                TEXT    NOT NULL,
                updated_at              TEXT    NOT NULL DEFAULT '',
                other_schedule_seen      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (network_id, transport_stream_id, service_id, event_id)
            );

            CREATE INDEX IF NOT EXISTS idx_epg_events_start
                ON epg_events (start_time);

            CREATE INDEX IF NOT EXISTS idx_epg_events_service
                ON epg_events (service_id, start_time);

            CREATE TABLE IF NOT EXISTS reservations (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                network_id          INTEGER NOT NULL DEFAULT 0,
                transport_stream_id INTEGER NOT NULL DEFAULT 0,
                service_id          INTEGER NOT NULL DEFAULT 0,
                event_id            INTEGER NOT NULL DEFAULT 0,
                title               TEXT    NOT NULL DEFAULT '',
                start_time          TEXT    NOT NULL,
                end_time            TEXT    NOT NULL,
                status              TEXT    NOT NULL DEFAULT 'scheduled',
                source              TEXT    NOT NULL DEFAULT 'manual',
                channel_argument    TEXT    NOT NULL DEFAULT '',
                is_conflicted       INTEGER NOT NULL DEFAULT 0,
                is_enabled          INTEGER NOT NULL DEFAULT 1,
                tuner_name          TEXT    NOT NULL DEFAULT '',
                actual_tuner_name   TEXT    NOT NULL DEFAULT '',
                recording_started_at TEXT   NOT NULL DEFAULT '',
                recording_finished_at TEXT  NOT NULL DEFAULT '',
                service_name        TEXT    NOT NULL DEFAULT '',
                scheduled_start_time TEXT   NOT NULL DEFAULT '',
                source_rule_id      INTEGER NULL,
                source_rule_name    TEXT    NOT NULL DEFAULT '',
                plugin_binding_json TEXT NOT NULL DEFAULT '[]',
                is_user_chain       INTEGER NOT NULL DEFAULT 0,
                user_chain_previous_id INTEGER NULL,
                user_chain_root_id  INTEGER NULL,
                recording_recovery_chain_id TEXT NOT NULL DEFAULT '',
                recovery_parent_reservation_id INTEGER NULL,
                created_at          TEXT    NOT NULL DEFAULT '',
                updated_at          TEXT    NOT NULL DEFAULT '',
                data_version        INTEGER NOT NULL DEFAULT 0,
                reservation_intent   TEXT    NOT NULL DEFAULT 'unspecified',
                created_through      TEXT    NOT NULL DEFAULT '',
                created_by_plugin_id TEXT    NOT NULL DEFAULT '',
                chain_finalization_receipt TEXT NOT NULL DEFAULT '',
                chain_stop_receipt TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_reservations_start
                ON reservations (start_time);

            CREATE INDEX IF NOT EXISTS idx_reservations_status
                ON reservations (status, start_time);

            -- Logical PreRec responsibility ownership, independent of the physical
            -- tuner selected when an EPG worker is actually admitted.
            -- One priority can own only one parent, and a parent can have only one owner.
            CREATE TABLE IF NOT EXISTS system_epg_priority_ownership (
                priority_name       TEXT PRIMARY KEY COLLATE NOCASE,
                group_name          TEXT NOT NULL,
                parent_reservation_id INTEGER NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS recording_start_failure_provenance (
                reservation_id      INTEGER PRIMARY KEY,
                failure_kind        TEXT    NOT NULL DEFAULT '',
                reason              TEXT    NOT NULL DEFAULT '',
                failed_at           TEXT    NOT NULL DEFAULT '',
                data_version        INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(reservation_id) REFERENCES reservations(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_recording_start_failure_provenance_kind
                ON recording_start_failure_provenance (failure_kind, failed_at);

            CREATE TABLE IF NOT EXISTS runtime_generations (
                key   TEXT    PRIMARY KEY,
                value INTEGER NOT NULL DEFAULT 0
            );

            INSERT OR IGNORE INTO runtime_generations (key, value)
            VALUES ('allocation', 0);

            CREATE TABLE IF NOT EXISTS reservation_projection_metadata (
                reservation_id              INTEGER PRIMARY KEY,
                projection_state            TEXT    NOT NULL DEFAULT '',
                projection_source_kind      TEXT    NOT NULL DEFAULT '',
                projection_source_plugin_id TEXT    NOT NULL DEFAULT '',
                projection_source_event_key TEXT    NOT NULL DEFAULT '',
                projected_event_id          TEXT    NOT NULL DEFAULT '',
                overlay_network_id          INTEGER NOT NULL DEFAULT 0,
                overlay_transport_stream_id INTEGER NOT NULL DEFAULT 0,
                overlay_service_id          INTEGER NOT NULL DEFAULT 0,
                overlay_event_id            INTEGER NOT NULL DEFAULT 0,
                overlay_start_time          TEXT    NOT NULL DEFAULT '',
                overlay_end_time            TEXT    NOT NULL DEFAULT '',
                overlay_title               TEXT    NOT NULL DEFAULT '',
                matched_db_network_id       INTEGER NOT NULL DEFAULT 0,
                matched_db_transport_stream_id INTEGER NOT NULL DEFAULT 0,
                matched_db_service_id       INTEGER NOT NULL DEFAULT 0,
                matched_db_event_id         INTEGER NOT NULL DEFAULT 0,
                promoted_at                 TEXT    NOT NULL DEFAULT '',
                source_missing_at           TEXT    NOT NULL DEFAULT '',
                created_at                  TEXT    NOT NULL DEFAULT '',
                updated_at                  TEXT    NOT NULL DEFAULT '',
                FOREIGN KEY(reservation_id) REFERENCES reservations(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_reservation_projection_metadata_source
                ON reservation_projection_metadata (projection_source_kind, projection_source_plugin_id, projection_source_event_key);

            CREATE INDEX IF NOT EXISTS idx_reservation_projection_metadata_overlay
                ON reservation_projection_metadata (overlay_network_id, overlay_transport_stream_id, overlay_service_id, overlay_event_id, overlay_start_time, overlay_end_time);

            CREATE TABLE IF NOT EXISTS keyword_rules (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT    NOT NULL DEFAULT '',
                pattern         TEXT    NOT NULL DEFAULT '',
                exclude_pattern TEXT    NOT NULL DEFAULT '',
                use_regex       INTEGER NOT NULL DEFAULT 1,
                search_title    INTEGER NOT NULL DEFAULT 1,
                search_outline  INTEGER NOT NULL DEFAULT 1,
                search_detail   INTEGER NOT NULL DEFAULT 1,
                search_cast     INTEGER NOT NULL DEFAULT 1,
                use_all_channels INTEGER NOT NULL DEFAULT 1,
                target_services TEXT    NOT NULL DEFAULT '',
                target_genres   TEXT    NOT NULL DEFAULT '',
                target_days     TEXT    NOT NULL DEFAULT '',
                use_time_range  INTEGER NOT NULL DEFAULT 0,
                start_time      TEXT    NOT NULL DEFAULT '00:00',
                end_time        TEXT    NOT NULL DEFAULT '23:59',
                sort_order      INTEGER NOT NULL DEFAULT 0,
                expires_on      TEXT    NOT NULL DEFAULT '',
                record_current_service_only INTEGER NOT NULL DEFAULT 1,
                record_subtitles INTEGER NOT NULL DEFAULT 1,
                auto_chain_reservations INTEGER NOT NULL DEFAULT 0,
                plugin_binding_json TEXT NOT NULL DEFAULT '[]',
                enabled         INTEGER NOT NULL DEFAULT 1,
                created_at      TEXT    NOT NULL DEFAULT '',
                updated_at      TEXT    NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS reservation_recording_options (
                reservation_id       INTEGER PRIMARY KEY,
                current_service_only INTEGER NOT NULL DEFAULT 1,
                save_subtitles       INTEGER NOT NULL DEFAULT 1,
                created_at           TEXT    NOT NULL DEFAULT '',
                FOREIGN KEY(reservation_id) REFERENCES reservations(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS keyword_cancel_once (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                rule_id             INTEGER NOT NULL DEFAULT 0,
                network_id          INTEGER NOT NULL DEFAULT 0,
                transport_stream_id INTEGER NOT NULL DEFAULT 0,
                service_id          INTEGER NOT NULL DEFAULT 0,
                start_time          TEXT    NOT NULL DEFAULT '',
                end_time            TEXT    NOT NULL DEFAULT '',
                title_hash          TEXT    NOT NULL DEFAULT '',
                expires_at          TEXT    NOT NULL DEFAULT '',
                created_at          TEXT    NOT NULL DEFAULT '',
                UNIQUE(rule_id, network_id, transport_stream_id, service_id, start_time, end_time, title_hash)
            );

            CREATE INDEX IF NOT EXISTS idx_keyword_cancel_once_expire
                ON keyword_cancel_once (expires_at);

            CREATE TABLE IF NOT EXISTS manual_stopped_occurrences (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                network_id          INTEGER NOT NULL DEFAULT 0,
                transport_stream_id INTEGER NOT NULL DEFAULT 0,
                service_id          INTEGER NOT NULL DEFAULT 0,
                event_id            INTEGER NOT NULL DEFAULT 0,
                start_time          TEXT    NOT NULL DEFAULT '',
                end_time            TEXT    NOT NULL DEFAULT '',
                title_hash          TEXT    NOT NULL DEFAULT '',
                stopped_source      TEXT    NOT NULL DEFAULT '',
                source_rule_id      INTEGER,
                reservation_id      INTEGER NOT NULL DEFAULT 0,
                expires_at          TEXT    NOT NULL DEFAULT '',
                created_at          TEXT    NOT NULL DEFAULT '',
                UNIQUE(network_id, transport_stream_id, service_id, start_time, end_time, title_hash)
            );

            CREATE INDEX IF NOT EXISTS idx_manual_stopped_occurrences_expire
                ON manual_stopped_occurrences (expires_at);

            CREATE TABLE IF NOT EXISTS program_rules (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                name                TEXT    NOT NULL DEFAULT '',
                day_of_week         INTEGER NOT NULL DEFAULT 0,
                start_time          TEXT    NOT NULL DEFAULT '00:00',
                end_time            TEXT    NOT NULL DEFAULT '00:00',
                network_id          INTEGER NOT NULL DEFAULT 0,
                transport_stream_id INTEGER NOT NULL DEFAULT 0,
                service_id          INTEGER NOT NULL DEFAULT 0,
                expires_on          TEXT    NOT NULL DEFAULT '',
                enabled             INTEGER NOT NULL DEFAULT 1,
                created_at          TEXT    NOT NULL DEFAULT '',
                updated_at          TEXT    NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS user_event_logs (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at  TEXT    NOT NULL DEFAULT '',
                severity    TEXT    NOT NULL DEFAULT 'INFO',
                category    TEXT    NOT NULL DEFAULT '',
                result      TEXT    NOT NULL DEFAULT '',
                target      TEXT    NOT NULL DEFAULT '',
                message     TEXT    NOT NULL DEFAULT '',
                code        TEXT    NOT NULL DEFAULT '',
                trace_id    TEXT    NOT NULL DEFAULT '',
                operation_id TEXT    NOT NULL DEFAULT '',
                app_version TEXT    NOT NULL DEFAULT '',
                previous_app_version TEXT NOT NULL DEFAULT '',
                version_changed INTEGER NOT NULL DEFAULT 0,
                story_context TEXT NOT NULL DEFAULT '',
                origin      TEXT    NOT NULL DEFAULT '',
                event_trigger TEXT    NOT NULL DEFAULT '',
                story_role  TEXT    NOT NULL DEFAULT '',
                target_kind TEXT    NOT NULL DEFAULT '',
                actionability TEXT  NOT NULL DEFAULT '',
                reservation_source TEXT NOT NULL DEFAULT '',
                program_title TEXT  NOT NULL DEFAULT '',
                reservation_id TEXT NOT NULL DEFAULT '',
                recording_id TEXT NOT NULL DEFAULT '',
                recording_recovery_chain_id TEXT NOT NULL DEFAULT '',
                power_resume_cycle_id TEXT NOT NULL DEFAULT '',
                service_name TEXT NOT NULL DEFAULT '',
                scheduled_start TEXT NOT NULL DEFAULT '',
                scheduled_end TEXT NOT NULL DEFAULT '',
                actual_start TEXT NOT NULL DEFAULT '',
                actual_end TEXT NOT NULL DEFAULT '',
                drop_count INTEGER NULL,
                error_count INTEGER NULL,
                scramble_count INTEGER NULL,
                file_path TEXT NOT NULL DEFAULT '',
                completion_reason TEXT NOT NULL DEFAULT '',
                details_json TEXT NOT NULL DEFAULT '{}',
                detail      TEXT    NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_user_event_logs_created
                ON user_event_logs (created_at);

            CREATE INDEX IF NOT EXISTS idx_user_event_logs_severity
                ON user_event_logs (severity, created_at);

            CREATE INDEX IF NOT EXISTS idx_user_event_logs_recovery_chain
                ON user_event_logs (recording_recovery_chain_id, created_at);

            CREATE INDEX IF NOT EXISTS idx_user_event_logs_resume_cycle
                ON user_event_logs (power_resume_cycle_id, created_at);

            -- EPG取得ベースライン（TSごとの最大取得件数・平均取得件数を蓄積）
            -- 目的: 各TSが過去どれくらい取れているかを記録し、run完了時に達成率を
            -- ログ出力することで取りこぼし傾向を可視化する。運用中の実測から
            -- 「このTSはこれくらい取れて当たり前」という基準値を育てていく。
            CREATE TABLE IF NOT EXISTS epg_ts_baseline (
                ts_group            TEXT    NOT NULL,    -- 'GR' or 'BSCS'
                transport_stream_id INTEGER NOT NULL,
                depth               TEXT    NOT NULL DEFAULT 'medium', -- shallow/medium/deep別に記録
                max_imported        INTEGER NOT NULL DEFAULT 0,
                last_imported       INTEGER NOT NULL DEFAULT 0,
                run_count           INTEGER NOT NULL DEFAULT 0,
                total_imported      INTEGER NOT NULL DEFAULT 0,   -- 平均算出用の合計
                max_recorded_at     TEXT    NOT NULL DEFAULT '',
                last_recorded_at    TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (ts_group, transport_stream_id, depth)
            );
            """;
        cmd.ExecuteNonQuery();
    }

}
