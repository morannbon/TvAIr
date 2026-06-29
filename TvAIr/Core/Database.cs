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
        var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return con;
    }

    // ─── 初期化 ──────────────────────────────────────────────────

    private void Initialize()
    {
        using var con = Open();
        CreateTables(con);
        Migrate(con);
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
                is_user_chain       INTEGER NOT NULL DEFAULT 0,
                user_chain_previous_id INTEGER NULL,
                user_chain_root_id  INTEGER NULL,
                created_at          TEXT    NOT NULL DEFAULT '',
                updated_at          TEXT    NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_reservations_start
                ON reservations (start_time);

            CREATE INDEX IF NOT EXISTS idx_reservations_status
                ON reservations (status, start_time);

            CREATE TABLE IF NOT EXISTS keyword_rules (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT    NOT NULL DEFAULT '',
                pattern         TEXT    NOT NULL DEFAULT '',
                exclude_pattern TEXT    NOT NULL DEFAULT '',
                use_regex       INTEGER NOT NULL DEFAULT 1,
                search_fields   TEXT    NOT NULL DEFAULT 'title',
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
                enabled         INTEGER NOT NULL DEFAULT 1,
                created_at      TEXT    NOT NULL DEFAULT '',
                updated_at      TEXT    NOT NULL DEFAULT ''
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
                detail      TEXT    NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_user_event_logs_created
                ON user_event_logs (created_at);

            CREATE INDEX IF NOT EXISTS idx_user_event_logs_severity
                ON user_event_logs (severity, created_at);

            -- EPG取得ベースライン（TSごとの最大取得件数・平均取得件数を蓄積）
            -- 目的: 各TSが過去どれくらい取れているかを記録し、run完了時に達成率を
            -- ログ出力することで取りこぼし傾向を可視化する。運用中の実測から
            -- 「このTSはこれくらい取れて当たり前」という基準値を育てていく。
            CREATE TABLE IF NOT EXISTS service_logos (
                network_id              INTEGER NOT NULL DEFAULT 0,
                transport_stream_id     INTEGER NOT NULL DEFAULT 0,
                service_id              INTEGER NOT NULL DEFAULT 0,
                service_name            TEXT    NOT NULL DEFAULT '',
                logo_id                 INTEGER NOT NULL DEFAULT -1,
                logo_version            INTEGER NOT NULL DEFAULT -1,
                download_data_id        INTEGER NOT NULL DEFAULT -1,
                logo_path               TEXT    NOT NULL DEFAULT '',
                source                  TEXT    NOT NULL DEFAULT '',
                updated_at              TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (network_id, transport_stream_id, service_id)
            );

            CREATE INDEX IF NOT EXISTS idx_service_logos_service
                ON service_logos (service_id);


            CREATE TABLE IF NOT EXISTS service_logo_inventory (
                network_id              INTEGER NOT NULL DEFAULT 0,
                transport_stream_id     INTEGER NOT NULL DEFAULT 0,
                service_id              INTEGER NOT NULL DEFAULT 0,
                service_name            TEXT    NOT NULL DEFAULT '',
                logo_id                 INTEGER NOT NULL DEFAULT -1,
                logo_type               INTEGER NOT NULL DEFAULT -1,
                logo_version            INTEGER NOT NULL DEFAULT -1,
                download_data_id        INTEGER NOT NULL DEFAULT -1,
                logo_path               TEXT    NOT NULL DEFAULT '',
                source                  TEXT    NOT NULL DEFAULT '',
                updated_at              TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (network_id, transport_stream_id, service_id, logo_id, logo_type)
            );

            CREATE INDEX IF NOT EXISTS idx_service_logo_inventory_service
                ON service_logo_inventory (service_id, logo_type);

            CREATE INDEX IF NOT EXISTS idx_service_logo_inventory_logo
                ON service_logo_inventory (network_id, logo_id, logo_type);

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

    /// <summary>
    /// カラム追加などの後方互換マイグレーション。
    /// テーブルが存在する前提で、不足カラムのみADD COLUMNする。
    /// ※ CreateTables() で定義済みのカラムはここに記載不要だが、
    ///    旧バージョンDBからの移行のため残している。
    /// </summary>
    private static void Migrate(SqliteConnection con)
    {


        // EPG DB is a disposable capture cache. Ensure the current pure-EPG schema even when a previous cache file remains.
        EnsureColumn(con, "epg_events", "network_id", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "epg_events", "transport_stream_id", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "epg_events", "service_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "epg_events", "genre", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "epg_events", "genre_codes", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "epg_events", "table_id", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "epg_events", "section_number", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "epg_events", "version_number", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "epg_events", "raw_descriptor_loop", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "epg_events", "raw_short_event_descriptor", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "epg_events", "raw_extended_event_descriptor", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "epg_events", "raw_content_descriptor", "TEXT NOT NULL DEFAULT ''");

        // 実行時ストアの不足カラムを補う。DBファイル自体の持ち越しは前提にしない。
        EnsureColumn(con, "reservations", "network_id",          "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "reservations", "transport_stream_id", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "reservations", "source",              "TEXT NOT NULL DEFAULT 'manual'");
        EnsureColumn(con, "reservations", "channel_argument",    "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "reservations", "is_conflicted",       "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "reservations", "is_enabled",          "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(con, "reservations", "tuner_name",          "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "reservations", "actual_tuner_name",   "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "reservations", "recording_started_at","TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "reservations", "recording_finished_at","TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "reservations", "service_name",        "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "reservations", "scheduled_start_time","TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "reservations", "source_rule_id",     "INTEGER NULL");
        EnsureColumn(con, "reservations", "source_rule_name",   "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "reservations", "is_user_chain",     "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "reservations", "user_chain_previous_id", "INTEGER NULL");
        EnsureColumn(con, "reservations", "user_chain_root_id", "INTEGER NULL");

        // release_contract: ユーザー運用ログは /api/log からの推測ではなく、
        // UserOperationEvent 正本として報告用メタ属性を保持する。
        EnsureColumn(con, "user_event_logs", "operation_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "app_version", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "previous_app_version", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "version_changed", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "user_event_logs", "story_context", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "origin", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "event_trigger", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "story_role", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "target_kind", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "actionability", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "reservation_source", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "program_title", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "user_event_logs", "detail", "TEXT NOT NULL DEFAULT ''");
        // keyword_rules はJSON export/importで持ち越すユーザー資産。
        EnsureColumn(con, "keyword_rules", "exclude_pattern", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "keyword_rules", "search_fields",   "TEXT NOT NULL DEFAULT 'title'");
        EnsureColumn(con, "keyword_rules", "search_title",    "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(con, "keyword_rules", "search_outline",  "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(con, "keyword_rules", "search_detail",   "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(con, "keyword_rules", "search_cast",     "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(con, "keyword_rules", "use_all_channels","INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(con, "keyword_rules", "target_genres",   "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "keyword_rules", "target_services", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "keyword_rules", "target_days",     "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "keyword_rules", "use_time_range",  "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "keyword_rules", "start_time",      "TEXT NOT NULL DEFAULT '00:00'");
        EnsureColumn(con, "keyword_rules", "end_time",        "TEXT NOT NULL DEFAULT '23:59'");
        EnsureColumn(con, "keyword_rules", "sort_order",      "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "keyword_rules", "expires_on",      "TEXT NOT NULL DEFAULT ''");

        using (var fillSort = con.CreateCommand())
        {
            fillSort.CommandText = "UPDATE keyword_rules SET sort_order = id WHERE sort_order = 0;";
            fillSort.ExecuteNonQuery();
        }
    }

    /// <summary>指定カラムが存在しない場合のみADD COLUMN</summary>
    internal static void EnsureColumn(SqliteConnection con, string table, string column, string definition)
    {
        var existing = GetColumns(con, table);
        if (existing.Contains(column)) return;
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        cmd.ExecuteNonQuery();
    }

    private static HashSet<string> GetColumns(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            cols.Add(reader.GetString(1));
        return cols;
    }
}
