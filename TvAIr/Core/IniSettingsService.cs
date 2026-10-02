namespace TvAIr.Core;

/// <summary>
/// TvAIr.ini を読み書きするサービス。
/// ini が存在しない場合は IsFirstRun = true を返す。
/// </summary>
public sealed class IniSettingsService
{
    private const int CurrentSettingsSchemaVersion = 1;
    // SETTINGS_MIGRATION_SOURCE_CONTRACT
    // 公開版 v1.2.0 / v1.2.1 / v1.2.2 は SettingsSchemaVersion を持たないため schema=0 として扱う。
    // 製品バージョン文字列では判定せず、INI schema と既知の旧キー/旧セクションを移行元の識別子にする。
    // 将来版でもこの schema=0 -> 現行schema の直接移行経路を累積維持し、中間版の順次起動を要求しない。
    private const int LegacyPublicReleaseSettingsSchemaVersion = 0;
    private const string PendingLegacyCleanupState = "PendingLegacyCleanup";
    private readonly string _iniPath;
    private readonly string _baseDirectory;

    // SETTINGS_PERSISTED_RUNTIME_TOPOLOGY_SEPARATION
    // Public topology properties represent the configuration currently used by the running process.
    // These fields retain the normalized values already written to TvAIr.ini so the settings API does not
    // overwrite a restart-pending edit with the old runtime topology.
    private string _persistedTvTestExecutablePath = "";
    private string _persistedViewingTvTestExecutablePath = "";
    private string _persistedBonDriverDirectory = "";
    private bool _persistedUseMinOption = SettingsDefaults.UseMinOption;
    private bool _persistedUseNodshowOption = SettingsDefaults.UseNodshowOption;
    private List<TunerProfileDto> _persistedTuners = new();
    private string _persistedDataDirectory = "";
    private int _persistedPort = SettingsDefaults.Port;

    // ── 設定値（読み込み後に公開） ──────────────────────────────────
    public string TvTestExecutablePath { get; private set; } = "";
    public string BonDriverDirectory   { get; private set; } = "";
    public string ViewingTvTestExecutablePath { get; private set; } = "";
    public string GrChannelFilePath    { get; private set; } = "";
    public string GrChSetFilePath      { get; private set; } = "";
    public string BscsChannelFilePath  { get; private set; } = "";
    public string BscsChSetFilePath    { get; private set; } = "";
    public string DataDirectory        { get; private set; } = "";
    /// <summary>UI表示テーマ。current=Windowsに合わせる、light/dark=固定。</summary>
    public string SystemTheme          { get; private set; } = SettingsDefaults.SystemTheme;
    public int    Port                 { get; private set; } = SettingsDefaults.Port;
    public bool   EpgEnabled           { get; private set; } = SettingsDefaults.EpgEnabled;
    public int    EpgHour              { get; private set; } = SettingsDefaults.EpgHour;
    public int    EpgMinute            { get; private set; } = SettingsDefaults.EpgMinute;
    public string EpgDepth             { get; private set; } = SettingsDefaults.EpgDepth;
    public int    EpgPreRecordMinutes       { get; private set; } = SettingsDefaults.EpgPreRecordMinutes;
    public bool   LaterProgramPriority  { get; private set; } = SettingsDefaults.LaterProgramPriority;
    public bool   ChainRecordingEnabled      { get; private set; } = SettingsDefaults.ChainRecordingEnabled;
    /// <summary>ユーザー明示チェーン機能の有効状態。</summary>
    public bool   UserChainRecordingEnabled => ChainReservationContract.IsFeatureEnabled(LaterProgramPriority, ChainRecordingEnabled);
    public int    PreStartMarginSeconds { get; private set; } = SettingsDefaults.PreStartMarginSeconds;
    public int    PostEndMarginSeconds  { get; private set; } = SettingsDefaults.PostEndMarginSeconds;
    public int    WakeMinutesBefore     { get; private set; } = SettingsDefaults.WakeMinutesBefore;
    /// <summary>新設: スリープ復帰の余裕秒数。EPG確認起床と録画起床の両方に加算される。</summary>
    public int    WakeAdditionalSeconds { get; private set; } = SettingsDefaults.WakeAdditionalSeconds;
    public bool   UseMinOption          { get; private set; } = SettingsDefaults.UseMinOption;
    public bool   UseNodshowOption      { get; private set; } = SettingsDefaults.UseNodshowOption;
    /// <summary>TvAIrEpgRec worker をタスクバーに表示する。false の場合はTvAIrトレイ点滅を代表インジケータにする。</summary>
    public bool   ShowTvAIrEpgRecTaskbarIcon { get; private set; } = SettingsDefaults.ShowTvAIrEpgRecTaskbarIcon;
    public bool   StartupEnabled        { get; private set; } = SettingsDefaults.StartupEnabled;

    // NETWORK_USAGE_MASTER_INVARIANT
    public bool NetworkUsageEnabled { get; private set; } = SettingsDefaults.NetworkUsageEnabled;

    // NETWORK_ACCESS_SETTINGS_INVARIANT
    public bool NetworkLanAccessEnabled { get; private set; } = SettingsDefaults.NetworkLanAccessEnabled;
    public int NetworkSessionLifetimeMinutes { get; private set; } = SettingsDefaults.NetworkSessionLifetimeMinutes;
    /// <summary>LAN接続用パスワード。Windows DPAPIで暗号化した値を正本として保存する。</summary>
    public string NetworkPasswordEncrypted { get; private set; } = "";

    public string RecordingAfterAction  { get; private set; } = SettingsDefaults.RecordingAfterAction;
    public int    RecordingAfterActionDelayMinutes { get; private set; } = SettingsDefaults.RecordingAfterActionDelayMinutes;

    // User operation log display. Standard is always one line; detail is enabled explicitly and contains selected fields only.
    public bool UserLogDetailEnabled { get; private set; } = SettingsDefaults.UserLogDetailEnabled;
    public bool UserLogDetailReservationSource { get; private set; } = SettingsDefaults.UserLogDetailReservationSource;
    public bool UserLogDetailScheduledTime { get; private set; } = SettingsDefaults.UserLogDetailScheduledTime;
    public bool UserLogDetailActualRecordingTime { get; private set; } = SettingsDefaults.UserLogDetailActualRecordingTime;
    public bool UserLogDetailRecordingQuality { get; private set; } = SettingsDefaults.UserLogDetailRecordingQuality;
    public bool UserLogDetailStateChange { get; private set; } = SettingsDefaults.UserLogDetailStateChange;
    public bool UserLogDetailEndOrFailureReason { get; private set; } = SettingsDefaults.UserLogDetailEndOrFailureReason;

    /// <summary>テーマ別ジャンル色。light=TvRock標準色、dark=ダークテーマ用色。</summary>
    public Dictionary<string, Dictionary<string, string>> ThemeGenrePalettes { get; private set; } = SettingsDefaults.CreateDefaultThemeGenrePalettes();

    // ─── EPG worker launch policy ───
    /// <summary>EPG用TVTestプロセスをBelowNormal優先度で起動するか（true=有効、デフォルトtrue）。
    /// LIVE視聴TVTestと同優先度競合によるカクつきを軽減。</summary>
    public bool   EpgUseBelowNormalPriority    { get; private set; } = SettingsDefaults.EpgUseBelowNormalPriority;
    /// <summary>同一TSのattempt即時リトライを無効化する。
    /// true=失敗局は再巡回パスのみで対応（即時の負荷スパイク回避）。デフォルトtrue。</summary>
    public bool   EpgDisableImmediateRetry     { get; private set; } = SettingsDefaults.EpgDisableImmediateRetry;

    /// <summary>タスクスケジューラー登録用ユーザー名（空=資格情報なし・InteractiveToken方式）</summary>
    public string TaskUserName          { get; private set; } = "";
    /// <summary>タスクスケジューラー登録用パスワード（DPAPI暗号化済みBase64。空=未設定）</summary>
    public string TaskPasswordEncrypted { get; private set; } = "";

    /// <summary>チューナー個別設定リスト（iniの[Tuner]セクションから読み込む）</summary>
    public List<TunerProfileDto> Tuners { get; private set; } = new();

    /// <summary>ini ファイルが存在しなかった（初回起動）場合 true</summary>
    public bool IsFirstRun { get; private set; } = false;

    /// <summary>旧設定から現行キーの作成が完了し、1回だけ再起動が必要な場合 true。</summary>
    public bool SettingsMigrationRestartRequired { get; private set; } = false;

    public IniSettingsService(string baseDirectory, string? firstRunDataDirectory, int firstRunPort)
    {
        _baseDirectory = baseDirectory;
        _iniPath = Path.Combine(baseDirectory, "TvAIr.ini");
        Load();

        // SETTINGS_FIRST_RUN_RUNTIME_HOST_SNAPSHOT_CONTRACT
        // INI未作成の初回起動だけはHost/DBの稼働値をApp設定から確定する。
        // SettingsChangeApplicationServiceの再起動判定も同じRuntime snapshotを見る必要があるため、
        // Program側だけで別に解決せずIniSettingsServiceのRuntime正本へ取り込む。
        if (IsFirstRun)
        {
            DataDirectory = NormalizePathValue(firstRunDataDirectory);
            Port = SettingsDefaults.NormalizePort(firstRunPort);
        }

        CapturePersistedTunerTopologyFromRuntime();
        CapturePersistedHostSettingsFromRuntime();
    }

    // ── BonDriver一覧取得 ────────────────────────────────────────────
    /// <summary>BonDriverDirectory 内の .dll ファイル名一覧を返す。</summary>
    public IReadOnlyList<string> GetBonDriverList() => GetBonDriverList(BonDriverDirectory);

    private static IReadOnlyList<string> GetBonDriverList(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<string>();

        return Directory.GetFiles(directory, "*.dll")
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Select(f => f!)
            .OrderBy(f => f)
            .ToList();
    }

    // ── 読み込み ────────────────────────────────────────────────────
    internal static int ResolvePersistedPort(string baseDirectory, int fallbackPort)
    {
        var iniPath = Path.Combine(baseDirectory, "TvAIr.ini");
        if (!File.Exists(iniPath))
            return SettingsDefaults.NormalizePort(fallbackPort);

        var values = ReadRawIniValues(iniPath);
        return SettingsDefaults.NormalizePort(GetInt(values, "Port", fallbackPort));
    }

    private static Dictionary<string, string> ReadRawIniValues(string iniPath)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(iniPath))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.StartsWith('#') || !line.Contains('=')) continue;
            var eq = line.IndexOf('=');
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            dict[key] = val;
        }
        return dict;
    }

    private static Dictionary<string, string> ReadCurrentIniValues(string iniPath)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        foreach (var raw in File.ReadAllLines(iniPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                section = line[1..^1].Trim();
                continue;
            }
            if (!line.Contains('=')) continue;
            var eq = line.IndexOf('=');
            var key = line[..eq].Trim();
            if (!IsCurrentIniKey(section, key)) continue;
            dict[key] = line[(eq + 1)..].Trim();
        }
        return dict;
    }

    private static bool IsCurrentIniKey(string section, string key)
    {
        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(key)) return false;

        return section.Trim().ToLowerInvariant() switch
        {
            "tvtest" => key.Equals("TvTestExecutablePath", StringComparison.OrdinalIgnoreCase)
                || key.Equals("BonDriverDirectory", StringComparison.OrdinalIgnoreCase)
                || key.Equals("ViewingTvTestExecutablePath", StringComparison.OrdinalIgnoreCase),
            "channel" => key.Equals("GrChannelFilePath", StringComparison.OrdinalIgnoreCase)
                || key.Equals("GrChSetFilePath", StringComparison.OrdinalIgnoreCase)
                || key.Equals("BscsChannelFilePath", StringComparison.OrdinalIgnoreCase)
                || key.Equals("BscsChSetFilePath", StringComparison.OrdinalIgnoreCase),
            "tuner" => key.Equals("TunerCount", StringComparison.OrdinalIgnoreCase) || IsCurrentTunerKey(key),
            "app" => key.Equals("SettingsSchemaVersion", StringComparison.OrdinalIgnoreCase)
                || key.Equals("DataDirectory", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SystemTheme", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Port", StringComparison.OrdinalIgnoreCase),
            "network" => key.Equals("NetworkUsageEnabled", StringComparison.OrdinalIgnoreCase)
                || key.Equals("NetworkLanAccessEnabled", StringComparison.OrdinalIgnoreCase)
                || key.Equals("NetworkSessionLifetimeMinutes", StringComparison.OrdinalIgnoreCase)
                || key.Equals("NetworkPasswordEncrypted", StringComparison.OrdinalIgnoreCase),
            "epg" => key.Equals("EpgEnabled", StringComparison.OrdinalIgnoreCase)
                || key.Equals("EpgHour", StringComparison.OrdinalIgnoreCase)
                || key.Equals("EpgMinute", StringComparison.OrdinalIgnoreCase)
                || key.Equals("EpgDepth", StringComparison.OrdinalIgnoreCase)
                || key.Equals("EpgPreRecordMinutes", StringComparison.OrdinalIgnoreCase),
            "recording" => key.Equals("LaterProgramPriority", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PreStartMarginSeconds", StringComparison.OrdinalIgnoreCase)
                || key.Equals("PostEndMarginSeconds", StringComparison.OrdinalIgnoreCase)
                || key.Equals("WakeMinutesBefore", StringComparison.OrdinalIgnoreCase)
                || key.Equals("WakeAdditionalSeconds", StringComparison.OrdinalIgnoreCase)
                || key.Equals("ChainRecordingEnabled", StringComparison.OrdinalIgnoreCase)
                || key.Equals("RecordingAfterAction", StringComparison.OrdinalIgnoreCase)
                || key.Equals("RecordingAfterActionDelayMinutes", StringComparison.OrdinalIgnoreCase),
            "log" => key.StartsWith("UserLogDetail", StringComparison.OrdinalIgnoreCase),
            "tvtestoptions" => key.Equals("UseMinOption", StringComparison.OrdinalIgnoreCase)
                || key.Equals("UseNodshowOption", StringComparison.OrdinalIgnoreCase)
                || key.Equals("ShowTvAIrEpgRecTaskbarIcon", StringComparison.OrdinalIgnoreCase),
            "epgperformance" => key.Equals("EpgUseBelowNormalPriority", StringComparison.OrdinalIgnoreCase)
                || key.Equals("EpgDisableImmediateRetry", StringComparison.OrdinalIgnoreCase),
            "uigenrecolors" => key.Equals("GenrePaletteSchemaVersion", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("GenreColor_Light_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("GenreColor_Dark_", StringComparison.OrdinalIgnoreCase),
            "startup" => key.Equals("StartupEnabled", StringComparison.OrdinalIgnoreCase)
                || key.Equals("TaskUserName", StringComparison.OrdinalIgnoreCase)
                || key.Equals("TaskPasswordEncrypted", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsCurrentTunerKey(string key)
    {
        if (!key.StartsWith("Tuner", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(key[5..], out var index) && index > 0;
    }

    private static void EnsureCurrentSettingsSchema(Dictionary<string, string> values)
    {
        var schemaVersion = GetInt(values, "SettingsSchemaVersion", 0);
        if (schemaVersion != CurrentSettingsSchemaVersion)
            throw new InvalidDataException($"TvAIr.ini の設定形式を確認できません。expectedSchema={CurrentSettingsSchemaVersion} actualSchema={schemaVersion}");
    }

    private void Load()
    {
        if (!File.Exists(_iniPath))
        {
            IsFirstRun = true;
            return;
        }

        var rawValues = ReadRawIniValues(_iniPath);

        // SETTINGS_TWO_PHASE_MIGRATION_CONTRACT
        // 旧設定は1回目の起動で現行キーを追加するだけに留め、旧キーを同時に消さない。
        // そのプロセスは通常起動へ進まず、DB・予約・EPG・allocation等へ一切触れない。
        // 2回目の起動で現行キーが読み戻せることを確認してから旧キー/旧セクションを削除する。
        var persistedSchemaVersion = GetInt(rawValues, "SettingsSchemaVersion", LegacyPublicReleaseSettingsSchemaVersion);
        if (persistedSchemaVersion > CurrentSettingsSchemaVersion)
            throw new InvalidDataException($"TvAIr.ini の設定形式はこのTvAIrより新しいため起動できません。currentSchema={CurrentSettingsSchemaVersion} iniSchema={persistedSchemaVersion}");

        var migrationState = GetStr(rawValues, "SettingsMigrationState", "");
        if (string.Equals(migrationState, PendingLegacyCleanupState, StringComparison.OrdinalIgnoreCase))
        {
            // 1回目の移行後に中間版を起動せず、さらに新しいTvAIrへ差し替えられる場合もある。
            // その場合は旧キーをまだ保持しているため、古いtarget schemaのままcleanupせず、
            // 旧設定を正本として「そのTvAIrの現行schema」へもう一度直接stageする。
            if (persistedSchemaVersion < CurrentSettingsSchemaVersion)
            {
                StageLegacySettingsMigration(rawValues);
                SettingsMigrationRestartRequired = true;
                return;
            }

            // 2回目起動では、旧キーを消す前に「現行キーだけで旧設定と同じ意味になる」ことを検証する。
            // 検証に失敗した場合は旧キーを残したまま停止し、通常起動へ進めない。
            VerifyPendingLegacyMigrationAgainstCurrentSchema(rawValues);
            CleanupLegacySettingsAfterVerifiedMigration();
        }
        else if (NeedsLegacySettingsMigration(rawValues) || HasIniSection(_iniPath, "App2"))
        {
            StageLegacySettingsMigration(rawValues);
            SettingsMigrationRestartRequired = true;
            return;
        }

        var dict = ReadCurrentIniValues(_iniPath);
        EnsureCurrentSettingsSchema(dict);

        TvTestExecutablePath = NormalizePathValue(Get(dict, "TvTestExecutablePath", TvTestExecutablePath));
        BonDriverDirectory   = NormalizePathValue(Get(dict, "BonDriverDirectory",   BonDriverDirectory));
        ViewingTvTestExecutablePath = NormalizePathValue(Get(dict, "ViewingTvTestExecutablePath", ViewingTvTestExecutablePath));
        GrChannelFilePath    = NormalizePathValue(Get(dict, "GrChannelFilePath",    GrChannelFilePath));
        GrChSetFilePath      = NormalizePathValue(Get(dict, "GrChSetFilePath",      GrChSetFilePath));
        BscsChannelFilePath  = NormalizePathValue(Get(dict, "BscsChannelFilePath",  BscsChannelFilePath));
        BscsChSetFilePath    = NormalizePathValue(Get(dict, "BscsChSetFilePath",    BscsChSetFilePath));
        DataDirectory        = NormalizePathValue(Get(dict, "DataDirectory",        DataDirectory));
        SystemTheme          = NormalizeSystemTheme(GetStr(dict, "SystemTheme", SystemTheme));
        Port                 = SettingsDefaults.NormalizePort(GetInt(dict,  "Port", Port));
        EpgEnabled           = GetBool(dict, "EpgEnabled",          EpgEnabled);
        EpgHour              = SettingsDefaults.NormalizeEpgHour(GetInt(dict,  "EpgHour", EpgHour));
        EpgMinute            = SettingsDefaults.NormalizeEpgMinute(GetInt(dict, "EpgMinute", EpgMinute));
        // SETTINGS_LOAD_NORMALIZATION_CONTRACT
        // INI読込も保存/API差分判定と同じSettingsDefaults正本を通す。
        // 読込値をRuntime/UIへ生値のまま投影し、各利用側で再補正する別ルートを作らない。
        EpgDepth             = SettingsDefaults.NormalizeEpgDepth(GetStr(dict, "EpgDepth", EpgDepth));
        EpgPreRecordMinutes  = SettingsDefaults.NormalizeEpgPreRecordMinutes(GetInt(dict, "EpgPreRecordMinutes", EpgPreRecordMinutes));
        LaterProgramPriority = GetBool(dict, "LaterProgramPriority", LaterProgramPriority);
        ChainRecordingEnabled     = GetBool(dict, "ChainRecordingEnabled",     ChainRecordingEnabled);
        PreStartMarginSeconds = SettingsDefaults.NormalizePreStartMarginSeconds(GetInt(dict, "PreStartMarginSeconds", PreStartMarginSeconds));
        PostEndMarginSeconds  = SettingsDefaults.NormalizePostEndMarginSeconds(GetInt(dict, "PostEndMarginSeconds", PostEndMarginSeconds));
        WakeMinutesBefore     = SettingsDefaults.NormalizeWakeMinutesBefore(GetInt(dict, "WakeMinutesBefore", WakeMinutesBefore));
        WakeAdditionalSeconds = SettingsDefaults.NormalizeWakeAdditionalSeconds(GetInt(dict, "WakeAdditionalSeconds", WakeAdditionalSeconds));
        UseMinOption         = GetBool(dict, "UseMinOption",         UseMinOption);
        UseNodshowOption     = GetBool(dict, "UseNodshowOption",     UseNodshowOption);
        ShowTvAIrEpgRecTaskbarIcon = GetBool(dict, "ShowTvAIrEpgRecTaskbarIcon", ShowTvAIrEpgRecTaskbarIcon);
        StartupEnabled       = GetBool(dict, "StartupEnabled",       StartupEnabled);
        NetworkUsageEnabled = GetBool(dict, "NetworkUsageEnabled", NetworkUsageEnabled);
        NetworkLanAccessEnabled = GetBool(dict, "NetworkLanAccessEnabled", NetworkLanAccessEnabled);
        NetworkSessionLifetimeMinutes = SettingsDefaults.NormalizeNetworkSessionLifetimeMinutes(GetInt(dict, "NetworkSessionLifetimeMinutes", NetworkSessionLifetimeMinutes));
        NetworkPasswordEncrypted = GetStr(dict, "NetworkPasswordEncrypted", NetworkPasswordEncrypted);
        RecordingAfterAction = NormalizeRecordingAfterAction(GetStr(dict, "RecordingAfterAction", RecordingAfterAction));
        RecordingAfterActionDelayMinutes = NormalizeRecordingAfterActionDelayMinutes(GetInt(dict, "RecordingAfterActionDelayMinutes", RecordingAfterActionDelayMinutes));
        // USER_LOG_DETAIL_SETTINGS_TOKEN_INVARIANT
        // 詳細表示の設定正本は UserLogDetail* に統一する。
        UserLogDetailEnabled = GetBool(dict, "UserLogDetailEnabled", UserLogDetailEnabled);
        UserLogDetailReservationSource = GetBool(dict, "UserLogDetailReservationSource", UserLogDetailReservationSource);
        UserLogDetailScheduledTime = GetBool(dict, "UserLogDetailScheduledTime", UserLogDetailScheduledTime);
        UserLogDetailActualRecordingTime = GetBool(dict, "UserLogDetailActualRecordingTime", UserLogDetailActualRecordingTime);
        UserLogDetailRecordingQuality = GetBool(dict, "UserLogDetailRecordingQuality", UserLogDetailRecordingQuality);
        UserLogDetailStateChange = GetBool(dict, "UserLogDetailStateChange", UserLogDetailStateChange);
        UserLogDetailEndOrFailureReason = GetBool(dict, "UserLogDetailEndOrFailureReason", UserLogDetailEndOrFailureReason);
        ThemeGenrePalettes = LoadThemeGenrePalettes(dict);

        // EPG worker launch policy
        EpgUseBelowNormalPriority = GetBool(dict, "EpgUseBelowNormalPriority", EpgUseBelowNormalPriority);
        EpgDisableImmediateRetry  = GetBool(dict, "EpgDisableImmediateRetry",  EpgDisableImmediateRetry);


        TaskUserName         = NormalizeTaskUserName(GetStr(dict,  "TaskUserName",         TaskUserName));
        TaskPasswordEncrypted = GetStr(dict, "TaskPasswordEncrypted", TaskPasswordEncrypted);

        // チューナー個別設定
        // 形式: Tuner1 = 名前, BonDriverファイル名, GR/BSCS/HYBRID, DID, Role, LogicalViewerSlotId, DeviceNumber
        Tuners = new List<TunerProfileDto>();
        var count = GetInt(dict, "TunerCount", 0);

        // Current schema always persists TunerCount and full tuner metadata explicitly.
        for (var i = 1; i <= count; i++)
        {
            if (!dict.TryGetValue($"Tuner{i}", out var val))
                throw new InvalidDataException($"TvAIr.ini の[Tuner]設定が不完全です。missing=Tuner{i}");

            var parts = val.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 7)
                throw new InvalidDataException($"TvAIr.ini の[Tuner]設定が現行形式ではありません。key=Tuner{i}");

            Tuners.Add(new TunerProfileDto
            {
                Name              = TunerDisplayName.ForUi(parts[0], parts[2], parts[3]),
                BonDriverFileName = NormalizeBonDriverFileName(parts[1]),
                Group             = TunerDisplayName.NormalizeGroup(parts[2]),
                Did               = parts[3].Trim().ToUpperInvariant(),
                Role              = NormalizeTunerRole(parts[4]),
                LogicalViewerSlotId = LogicalViewerSlotIdentity.Resolve(parts[5], parts[2], parts[3], parts[4], i),
                DeviceNumber      = int.TryParse(parts[6], out var deviceNumber) ? deviceNumber : 0,
            });
        }

        NormalizeTunerDeviceNumbers();
    }

    private static readonly IReadOnlyDictionary<string, string> LegacySettingKeyRenames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PseudoContinuousRecording"] = "ChainRecordingEnabled",
            ["UserLogExtendedEnabled"] = "UserLogDetailEnabled",
            ["UserLogShowReservationSource"] = "UserLogDetailReservationSource",
            ["UserLogShowSchedule"] = "UserLogDetailScheduledTime",
            ["UserLogShowActualTime"] = "UserLogDetailActualRecordingTime",
            ["UserLogShowQuality"] = "UserLogDetailRecordingQuality",
            ["UserLogShowStateChange"] = "UserLogDetailStateChange",
            ["UserLogShowReason"] = "UserLogDetailEndOrFailureReason",
        };

    private static bool NeedsLegacySettingsMigration(Dictionary<string, string> values)
    {
        // v1.2.0～v1.2.2を含むschema未導入の公開版は0として直接現行schemaへ上げる。
        // 将来CurrentSettingsSchemaVersionが進んでも、version文字列による隣接版限定にはしない。
        if (GetInt(values, "SettingsSchemaVersion", LegacyPublicReleaseSettingsSchemaVersion) < CurrentSettingsSchemaVersion)
            return true;
        if (LegacySettingKeyRenames.Keys.Any(values.ContainsKey))
            return true;
        return false;
    }

    private static bool HasIniSection(string iniPath, string sectionName)
        => File.ReadLines(iniPath).Any(line => line.Trim().Equals($"[{sectionName}]", StringComparison.OrdinalIgnoreCase));

    private void StageLegacySettingsMigration(Dictionary<string, string> legacyValues)
    {
        var lines = File.ReadAllLines(_iniPath).ToList();

        // 公開版 v1.2.0～v1.2.2 のschema=0を含め、旧schemaから「この実行版の現行schema」へ直接stageする。
        // 1.2.2→1.2.4のような飛び越し更新でも中間版を要求しないことが契約。
        // 旧schemaで起動している以上、旧キーの実効値を移行元の正本とする。
        // 以前の移行試行等で同名の現行キーが残っていても信用せず、旧キーから必ず再生成する。
        // 旧キー自体はrollback用に次回起動まで保持する。
        foreach (var pair in LegacySettingKeyRenames)
        {
            if (!legacyValues.TryGetValue(pair.Key, out var legacyValue)) continue;
            UpsertKeyInSection(lines, ResolveCanonicalSection(pair.Value), pair.Value, legacyValue);
        }

        // [App2] のStartup系はキー名自体は現行と同じため、現行 [Startup] セクションへ複製する。
        // 1回目では [App2] を削除しない。
        var sectionValues = ReadIniSectionValues(lines);
        if (sectionValues.TryGetValue("App2", out var app2))
        {
            foreach (var key in new[] { "StartupEnabled", "TaskUserName", "TaskPasswordEncrypted" })
            {
                if (app2.TryGetValue(key, out var value))
                    UpsertKeyInSection(lines, "Startup", key, value);
            }
        }

        UpsertKeyInSection(lines, "App", "SettingsSchemaVersion", CurrentSettingsSchemaVersion.ToString());
        UpsertKeyInSection(lines, "App", "SettingsMigrationState", PendingLegacyCleanupState);
        WriteIniAtomically(lines);

        // 書込み後に現行キーが実際に読めることを確認する。失敗時は旧キーを残したまま停止する。
        var verify = ReadRawIniValues(_iniPath);
        if (GetInt(verify, "SettingsSchemaVersion", 0) != CurrentSettingsSchemaVersion
            || !string.Equals(GetStr(verify, "SettingsMigrationState", ""), PendingLegacyCleanupState, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TvAIr.ini の設定形式更新を確認できませんでした。旧設定は削除していません。");

        foreach (var pair in LegacySettingKeyRenames)
        {
            if (!legacyValues.TryGetValue(pair.Key, out var legacyValue)) continue;
            if (!verify.TryGetValue(pair.Value, out var canonicalValue)
                || !string.Equals(canonicalValue.Trim(), legacyValue.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"TvAIr.ini の現行設定キー {pair.Value} を確認できませんでした。旧設定は削除していません。");
        }
    }

    private void VerifyPendingLegacyMigrationAgainstCurrentSchema(Dictionary<string, string> legacyAndCurrentValues)
    {
        var currentValues = ReadCurrentIniValues(_iniPath);
        EnsureCurrentSettingsSchema(currentValues);

        // 名前変更された設定は文字列表現ではなく設定型として比較し、旧/新で実効値が同じことを確認する。
        foreach (var pair in LegacySettingKeyRenames)
        {
            if (!legacyAndCurrentValues.TryGetValue(pair.Key, out var legacyValue)) continue;
            if (!currentValues.TryGetValue(pair.Value, out var currentValue))
                throw new InvalidOperationException($"TvAIr.ini の現行設定キー {pair.Value} がないため、旧設定は削除しませんでした。");

            if (IsBooleanMigrationKey(pair.Key, pair.Value))
            {
                var legacyBool = ParseBoolStrict(legacyValue, pair.Key);
                var currentBool = ParseBoolStrict(currentValue, pair.Value);
                if (legacyBool != currentBool)
                    throw new InvalidOperationException($"TvAIr.ini の設定移行結果が一致しません。key={pair.Value} 旧設定は削除していません。");
            }
            else if (!string.Equals(currentValue.Trim(), legacyValue.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"TvAIr.ini の設定移行結果が一致しません。key={pair.Value} 旧設定は削除していません。");
            }
        }

        // チェーン予約は単一キーではなく LaterProgramPriority との組合せが実効状態の正本。
        if (legacyAndCurrentValues.TryGetValue("PseudoContinuousRecording", out var legacyChainRaw))
        {
            var legacyLater = GetBool(legacyAndCurrentValues, "LaterProgramPriority", SettingsDefaults.LaterProgramPriority);
            var legacyChain = ParseBoolStrict(legacyChainRaw, "PseudoContinuousRecording");
            var currentLater = GetBool(currentValues, "LaterProgramPriority", SettingsDefaults.LaterProgramPriority);
            var currentChain = GetBool(currentValues, "ChainRecordingEnabled", SettingsDefaults.ChainRecordingEnabled);
            var legacyEffective = ChainReservationContract.IsFeatureEnabled(legacyLater, legacyChain);
            var currentEffective = ChainReservationContract.IsFeatureEnabled(currentLater, currentChain);
            if (legacyEffective != currentEffective)
                throw new InvalidOperationException("TvAIr.ini のチェーン予約設定を同じ状態で移行できないため、旧設定は削除していません。");
        }

        var sections = ReadIniSectionValues(File.ReadAllLines(_iniPath));
        if (sections.TryGetValue("App2", out var oldStartup))
        {
            if (!sections.TryGetValue("Startup", out var currentStartup))
                throw new InvalidOperationException("TvAIr.ini の [Startup] を確認できないため、旧 [App2] は削除しませんでした。");
            foreach (var key in new[] { "StartupEnabled", "TaskUserName", "TaskPasswordEncrypted" })
            {
                if (!oldStartup.TryGetValue(key, out var oldValue)) continue;
                if (!currentStartup.TryGetValue(key, out var currentValue)
                    || !string.Equals(oldValue.Trim(), currentValue.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException($"TvAIr.ini の起動設定を同じ状態で移行できないため、旧設定は削除していません。key={key}");
            }
        }
    }

    private static bool IsBooleanMigrationKey(string legacyKey, string currentKey)
        => legacyKey.Equals("PseudoContinuousRecording", StringComparison.OrdinalIgnoreCase)
           || legacyKey.StartsWith("UserLog", StringComparison.OrdinalIgnoreCase)
           || currentKey.Equals("ChainRecordingEnabled", StringComparison.OrdinalIgnoreCase)
           || currentKey.StartsWith("UserLogDetail", StringComparison.OrdinalIgnoreCase);

    private static bool ParseBoolStrict(string raw, string key)
    {
        if (bool.TryParse(raw.Trim(), out var value)) return value;
        if (raw.Trim() == "1") return true;
        if (raw.Trim() == "0") return false;
        throw new InvalidOperationException($"TvAIr.ini の設定値を確認できません。key={key}");
    }

    private void CleanupLegacySettingsAfterVerifiedMigration()
    {
        var lines = File.ReadAllLines(_iniPath).ToList();
        var values = ReadRawIniValues(_iniPath);
        if (GetInt(values, "SettingsSchemaVersion", 0) != CurrentSettingsSchemaVersion)
            throw new InvalidOperationException("TvAIr.ini の現行設定形式を確認できないため、旧設定は削除しませんでした。");

        // 現行キーが揃っていることを確認してから旧名だけを消す。
        foreach (var pair in LegacySettingKeyRenames)
        {
            if (!values.ContainsKey(pair.Key)) continue;
            if (!values.ContainsKey(pair.Value))
                throw new InvalidOperationException($"TvAIr.ini の現行設定キー {pair.Value} がないため、旧設定 {pair.Key} は削除しませんでした。");
        }

        var sections = ReadIniSectionValues(lines);
        if (sections.TryGetValue("App2", out var oldStartup))
        {
            if (!sections.TryGetValue("Startup", out var currentStartup))
                throw new InvalidOperationException("TvAIr.ini の [Startup] を確認できないため、旧 [App2] は削除しませんでした。");
            foreach (var key in new[] { "StartupEnabled", "TaskUserName", "TaskPasswordEncrypted" })
            {
                if (oldStartup.ContainsKey(key) && !currentStartup.ContainsKey(key))
                    throw new InvalidOperationException($"TvAIr.ini の [Startup] {key} を確認できないため、旧 [App2] は削除しませんでした。");
            }
        }

        RemoveKeys(lines, LegacySettingKeyRenames.Keys);
        RemoveSection(lines, "App2");
        RemoveKeys(lines, new[] { "SettingsMigrationState" });
        ReplaceExactComment(lines,
            "; ジャンル色はSettingsDefaultsのテーマ共通正本から投影し、schema更新時だけ一度移行する。",
            "; ジャンル色はSettingsDefaultsのテーマ共通正本を基準に保存する。");
        WriteIniAtomically(lines);
    }

    private static string ResolveCanonicalSection(string key)
        => key.StartsWith("UserLogDetail", StringComparison.OrdinalIgnoreCase) ? "Log" : "Recording";

    private static Dictionary<string, Dictionary<string, string>> ReadIniSectionValues(IReadOnlyList<string> lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                section = line[1..^1].Trim();
                if (!result.ContainsKey(section))
                    result[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (line.StartsWith(';') || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0 || string.IsNullOrWhiteSpace(section)) continue;
            if (!result.TryGetValue(section, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[section] = values;
            }
            values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    private static void UpsertKeyInSection(List<string> lines, string sectionName, string key, string value)
    {
        var sectionStart = lines.FindIndex(line => line.Trim().Equals($"[{sectionName}]", StringComparison.OrdinalIgnoreCase));
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.Add($"[{sectionName}]");
            lines.Add($"{key} = {value}");
            return;
        }

        var sectionEnd = sectionStart + 1;
        while (sectionEnd < lines.Count && !lines[sectionEnd].TrimStart().StartsWith('[')) sectionEnd++;
        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            var eq = lines[i].IndexOf('=');
            if (eq <= 0) continue;
            if (!lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = $"{key} = {value}";
            return;
        }
        lines.Insert(sectionEnd, $"{key} = {value}");
    }

    private static void RemoveKeys(List<string> lines, IEnumerable<string> keys)
    {
        var remove = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var eq = lines[i].IndexOf('=');
            if (eq <= 0) continue;
            if (remove.Contains(lines[i][..eq].Trim()))
                lines.RemoveAt(i);
        }
    }

    private static void RemoveSection(List<string> lines, string sectionName)
    {
        var start = lines.FindIndex(line => line.Trim().Equals($"[{sectionName}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return;
        var end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
        lines.RemoveRange(start, end - start);
        while (start < lines.Count && start > 0 && string.IsNullOrWhiteSpace(lines[start]) && string.IsNullOrWhiteSpace(lines[start - 1]))
            lines.RemoveAt(start);
    }

    private static void ReplaceExactComment(List<string> lines, string before, string after)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(before, StringComparison.Ordinal))
                lines[i] = after;
        }
    }

    private void NormalizeTunerDeviceNumbers()
    {
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tuner in Tuners)
        {
            var group = TunerDisplayName.NormalizeGroup(tuner.Group);
            counters.TryGetValue(group, out var ordinal);
            ordinal++;
            counters[group] = ordinal;
            tuner.DeviceNumber = ordinal;
        }
    }

    // ── 書き込み ────────────────────────────────────────────────────
    // SETTINGS_SAVE_SINGLE_EXIT_CONTRACT
    // 設定保存はSettingsChangeApplicationServiceだけが呼ぶ。既定値付き公開入口を残すと、
    // 差分分類・再起動待ち・Runtime反映抑止・EPG/Allocation/Wake適用を迂回できるため禁止する。
    internal void SaveFromApplicationService(IniSettingsUpdateDto dto, bool applyTunerTopologyToRuntime, bool applyHostSettingsToRuntime)
    {
        var rollbackState = CaptureSaveState();
        var runtimeTvTestExecutablePath = TvTestExecutablePath;
        var runtimeViewingTvTestExecutablePath = ViewingTvTestExecutablePath;
        var runtimeBonDriverDirectory = BonDriverDirectory;
        var runtimeDataDirectory = DataDirectory;
        var runtimePort = Port;
        // release_contract: ch2/ChSet はチューナーRuntimeTopologyではなくChannelMap契約。
        // チューナー変更でRuntimeTopology反映を保留する場合でも、保存済みChannelMapはChannelFileLoader側でcache key/invalidateにより反映する。
        var runtimeUseMinOption = UseMinOption;
        var runtimeUseNodshowOption = UseNodshowOption;
        var runtimeTunersBeforeSave = CloneTuners(Tuners);

        try
        {
        // SETTINGS_PATH_CANONICALIZATION_CONTRACT
        // 差分判定と永続化で同じ正規化を使う。前後空白・不要な末尾区切りだけの入力を、
        // 別項目の保存に便乗してINIへ書き戻してはならない。ドライブ/共有ルートは保持する。
        TvTestExecutablePath = NormalizePathValue(dto.TvTestExecutablePath);
        BonDriverDirectory   = NormalizePathValue(dto.BonDriverDirectory);
        ViewingTvTestExecutablePath = NormalizePathValue(dto.ViewingTvTestExecutablePath);
        GrChannelFilePath    = NormalizePathValue(dto.GrChannelFilePath);
        GrChSetFilePath      = NormalizePathValue(dto.GrChSetFilePath);
        BscsChannelFilePath  = NormalizePathValue(dto.BscsChannelFilePath);
        BscsChSetFilePath    = NormalizePathValue(dto.BscsChSetFilePath);
        DataDirectory        = NormalizePathValue(dto.DataDirectory);
        SystemTheme          = NormalizeSystemTheme(dto.SystemTheme);
        Port                 = SettingsDefaults.NormalizePort(dto.Port);
        EpgEnabled           = dto.EpgEnabled;
        EpgHour              = SettingsDefaults.NormalizeEpgHour(dto.EpgHour);
        EpgMinute            = SettingsDefaults.NormalizeEpgMinute(dto.EpgMinute);
        EpgDepth             = SettingsDefaults.NormalizeEpgDepth(dto.EpgDepth);
        EpgPreRecordMinutes       = SettingsDefaults.NormalizeEpgPreRecordMinutes(dto.EpgPreRecordMinutes);
        LaterProgramPriority  = dto.LaterProgramPriority;
        ChainRecordingEnabled     = dto.ChainRecordingEnabled;
        PreStartMarginSeconds = SettingsDefaults.NormalizePreStartMarginSeconds(dto.PreStartMarginSeconds);
        PostEndMarginSeconds  = SettingsDefaults.NormalizePostEndMarginSeconds(dto.PostEndMarginSeconds);
        WakeMinutesBefore     = SettingsDefaults.NormalizeWakeMinutesBefore(dto.WakeMinutesBefore);
        WakeAdditionalSeconds = SettingsDefaults.NormalizeWakeAdditionalSeconds(dto.WakeAdditionalSeconds);
        UseMinOption         = dto.UseMinOption;
        UseNodshowOption     = dto.UseNodshowOption;
        ShowTvAIrEpgRecTaskbarIcon = dto.ShowTvAIrEpgRecTaskbarIcon;
        StartupEnabled       = dto.StartupEnabled;
        NetworkUsageEnabled = dto.NetworkUsageEnabled;
        NetworkLanAccessEnabled = dto.NetworkLanAccessEnabled;
        NetworkSessionLifetimeMinutes = SettingsDefaults.NormalizeNetworkSessionLifetimeMinutes(dto.NetworkSessionLifetimeMinutes);
        if (dto.ClearNetworkPassword)
        {
            NetworkPasswordEncrypted = "";
        }
        else if (!string.IsNullOrWhiteSpace(dto.NetworkPasswordPlain))
        {
            NetworkPasswordEncrypted = CredentialProtector.Encrypt(dto.NetworkPasswordPlain);
        }
        RecordingAfterAction = NormalizeRecordingAfterAction(dto.RecordingAfterAction);
        RecordingAfterActionDelayMinutes = NormalizeRecordingAfterActionDelayMinutes(dto.RecordingAfterActionDelayMinutes);
        UserLogDetailEnabled = dto.UserLogDetailEnabled;
        UserLogDetailReservationSource = dto.UserLogDetailReservationSource;
        UserLogDetailScheduledTime = dto.UserLogDetailScheduledTime;
        UserLogDetailActualRecordingTime = dto.UserLogDetailActualRecordingTime;
        UserLogDetailRecordingQuality = dto.UserLogDetailRecordingQuality;
        UserLogDetailStateChange = dto.UserLogDetailStateChange;
        UserLogDetailEndOrFailureReason = dto.UserLogDetailEndOrFailureReason;
        ThemeGenrePalettes = NormalizeThemeGenrePalettes(dto.ThemeGenrePalettes);

        // EPG worker launch policy
        EpgUseBelowNormalPriority = dto.EpgUseBelowNormalPriority;
        EpgDisableImmediateRetry  = dto.EpgDisableImmediateRetry;


        TaskUserName         = NormalizeTaskUserName(dto.TaskUserName);
        // パスワード更新は明示契約に統一する。
        // UI上の空欄はClearTaskPassword=trueとして送られ、nullだけが変更なしを表す。
        if (dto.ClearTaskPassword)
            TaskPasswordEncrypted = "";
        else if (!string.IsNullOrEmpty(dto.TaskPasswordPlain))
            TaskPasswordEncrypted = CredentialProtector.Encrypt(dto.TaskPasswordPlain);
        var deviceNumberCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var persistedTuners = (dto.Tuners ?? new()).Select((t, index) =>
        {
            var group = TunerDisplayName.NormalizeGroup(t.Group);
            var did = (t.Did ?? string.Empty).Trim().ToUpperInvariant();
            deviceNumberCounters.TryGetValue(group, out var deviceNumber);
            deviceNumber++;
            deviceNumberCounters[group] = deviceNumber;
            return new TunerProfileDto
            {
                Name = TunerDisplayName.ForUi(t.Name, group, did),
                BonDriverFileName = NormalizeBonDriverFileName(t.BonDriverFileName),
                Group = group,
                Did = did,
                Role = NormalizeTunerRole(t.Role),
                LogicalViewerSlotId = LogicalViewerSlotIdentity.Resolve(t.LogicalViewerSlotId, group, did, t.Role, index + 1),
                DeviceNumber = deviceNumber,
            };
        }).ToList();

        _persistedTvTestExecutablePath = TvTestExecutablePath;
        _persistedViewingTvTestExecutablePath = ViewingTvTestExecutablePath;
        _persistedBonDriverDirectory = BonDriverDirectory;
        _persistedUseMinOption = UseMinOption;
        _persistedUseNodshowOption = UseNodshowOption;
        _persistedTuners = CloneTuners(persistedTuners);
        _persistedDataDirectory = DataDirectory;
        _persistedPort = Port;

        // release_contract: チューナー変更は再起動必須。
        // iniファイルへは保存するが、稼働中のRuntimeTopology(TunerPool/Wake/EPG/RuntimeUiRenderContext/ExternalTuner)へは即時反映しない。
        Tuners = applyTunerTopologyToRuntime ? persistedTuners : runtimeTunersBeforeSave;
        IsFirstRun           = false;

        var lines = new List<string>
        {
            "; TvAIr 設定ファイル",
            "; このファイルを TvAIr.exe と同じフォルダに置いてください。",
            "",
            "[TvTest]",
            $"TvTestExecutablePath = {TvTestExecutablePath}",
            $"BonDriverDirectory   = {BonDriverDirectory}",
            $"ViewingTvTestExecutablePath = {ViewingTvTestExecutablePath}",
            "",
            "[Channel]",
            $"GrChannelFilePath    = {GrChannelFilePath}",
            $"GrChSetFilePath      = {GrChSetFilePath}",
            $"BscsChannelFilePath  = {BscsChannelFilePath}",
            $"BscsChSetFilePath    = {BscsChSetFilePath}",
            "",
            "[Tuner]",
            $"TunerCount           = {persistedTuners.Count}",
        };

        for (var i = 0; i < persistedTuners.Count; i++)
        {
            var t = persistedTuners[i];
            lines.Add($"Tuner{i + 1} = {t.Name}, {t.BonDriverFileName}, {t.Group}, {t.Did}, {NormalizeTunerRole(t.Role)}, {LogicalViewerSlotIdentity.Resolve(t.LogicalViewerSlotId, t.Group, t.Did, t.Role, i + 1)}, {t.DeviceNumber}");
        }

        lines.AddRange(new[]
        {
            "",
            "[App]",
            $"SettingsSchemaVersion = {CurrentSettingsSchemaVersion}",
            $"DataDirectory        = {_persistedDataDirectory}",
            $"SystemTheme          = {SystemTheme}",
            $"Port                 = {_persistedPort}",
            "",
            "[Network]",
            $"NetworkUsageEnabled = {(NetworkUsageEnabled ? "true" : "false")}",
            $"NetworkLanAccessEnabled = {(NetworkLanAccessEnabled ? "true" : "false")}",
            $"NetworkSessionLifetimeMinutes = {NetworkSessionLifetimeMinutes}",
            $"NetworkPasswordEncrypted = {NetworkPasswordEncrypted}",
            "",
            "[Epg]",
            $"EpgEnabled           = {(EpgEnabled ? "true" : "false")}",
            $"EpgHour              = {EpgHour}",
            $"EpgMinute            = {EpgMinute}",
            $"EpgDepth             = {EpgDepth}",
            $"EpgPreRecordMinutes       = {EpgPreRecordMinutes}",
            "",
            "[Recording]",
            $"LaterProgramPriority  = {(LaterProgramPriority ? "true" : "false")}",
            $"PreStartMarginSeconds = {PreStartMarginSeconds}",
            $"PostEndMarginSeconds  = {PostEndMarginSeconds}",
            $"WakeMinutesBefore     = {WakeMinutesBefore}",
            $"WakeAdditionalSeconds = {WakeAdditionalSeconds}",
            $"ChainRecordingEnabled     = {(ChainRecordingEnabled ? "true" : "false")}",
            $"RecordingAfterAction = {RecordingAfterAction}",
            $"RecordingAfterActionDelayMinutes = {RecordingAfterActionDelayMinutes}",
            "",
            "[Log]",
            $"UserLogDetailEnabled = {(UserLogDetailEnabled ? "true" : "false")}",
            $"UserLogDetailReservationSource = {(UserLogDetailReservationSource ? "true" : "false")}",
            $"UserLogDetailScheduledTime = {(UserLogDetailScheduledTime ? "true" : "false")}",
            $"UserLogDetailActualRecordingTime = {(UserLogDetailActualRecordingTime ? "true" : "false")}",
            $"UserLogDetailRecordingQuality = {(UserLogDetailRecordingQuality ? "true" : "false")}",
            $"UserLogDetailStateChange = {(UserLogDetailStateChange ? "true" : "false")}",
            $"UserLogDetailEndOrFailureReason = {(UserLogDetailEndOrFailureReason ? "true" : "false")}",
            "",
            "[TvTestOptions]",
            $"UseMinOption         = {(UseMinOption     ? "true" : "false")}",
            $"UseNodshowOption     = {(UseNodshowOption ? "true" : "false")}",
            $"ShowTvAIrEpgRecTaskbarIcon = {(ShowTvAIrEpgRecTaskbarIcon ? "true" : "false")}",
            "",
            "[EpgPerformance]",
            "; EPG worker launch policy",
            $"EpgUseBelowNormalPriority = {(EpgUseBelowNormalPriority ? "true" : "false")}",
            $"EpgDisableImmediateRetry  = {(EpgDisableImmediateRetry ? "true" : "false")}",
            "",
            "[UiGenreColors]",
            "; ジャンル色はSettingsDefaultsのテーマ共通正本を基準に保存する。",
            $"GenrePaletteSchemaVersion = {SettingsDefaults.GenrePaletteSchemaVersion}",
        });

        foreach (var kv in ThemeGenrePalettes["light"].OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"GenreColor_Light_{kv.Key} = {kv.Value}");
        foreach (var kv in ThemeGenrePalettes["dark"].OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"GenreColor_Dark_{kv.Key} = {kv.Value}");

        lines.AddRange(new[]
        {
            "",
            "[Startup]",
            $"StartupEnabled       = {(StartupEnabled   ? "true" : "false")}",
            $"TaskUserName         = {TaskUserName}",
            $"TaskPasswordEncrypted = {TaskPasswordEncrypted}",
        });

        WriteIniAtomically(lines);
        }
        catch
        {
            // SETTINGS_SAVE_ROLLBACK_CONTRACT
            // ディスク確定前に失敗した保存値をRuntime/Persisted正本へ残してはならない。
            // topologyだけでなく、同一保存操作で更新した全設定を保存開始前へ戻す。
            RestoreSaveState(rollbackState);
            throw;
        }

        // SETTINGS_SAVE_COMMIT_BOUNDARY_CONTRACT
        // WriteIniAtomically成功後はINIを永続正本とし、rollback境界は書込み確定前で閉じる。
        // 再起動待ちTopologyは確定済みPersisted値と分離してRuntime値を維持する。
        if (!applyTunerTopologyToRuntime)
        {
            TvTestExecutablePath = runtimeTvTestExecutablePath;
            ViewingTvTestExecutablePath = runtimeViewingTvTestExecutablePath;
            BonDriverDirectory = runtimeBonDriverDirectory;
            // ChannelMapは再起動待ちに戻さない。
            UseMinOption = runtimeUseMinOption;
            UseNodshowOption = runtimeUseNodshowOption;
            Tuners = runtimeTunersBeforeSave;
        }

        // SETTINGS_PERSISTED_RUNTIME_HOST_SEPARATION
        // DataDirectory/Portは起動時にHost/DBが確定するため、保存値と稼働値を分離する。
        // 現在Runtimeと一致する値へ戻した場合だけ保留を解消し、それ以外は再起動まで稼働値を維持する。
        if (!applyHostSettingsToRuntime)
        {
            DataDirectory = runtimeDataDirectory;
            Port = runtimePort;
        }
    }

    private void WriteIniAtomically(IReadOnlyCollection<string> lines)
    {
        var directory = Path.GetDirectoryName(_iniPath) ?? _baseDirectory;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_iniPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(tempPath, lines, new System.Text.UTF8Encoding(false));

            // SETTINGS_INI_REPLACE_CONTRACT
            // 既存INI更新ではFile.Replaceを使用し、置換対象が持つ属性・ACLのマージ契約を維持する。
            // 初回作成時だけMoveする。一時ファイルは必ず同一ディレクトリへ作成しているため、
            // 既存INIを削除してから移動する非原子的な経路へフォールバックしてはならない。
            if (File.Exists(_iniPath))
                File.Replace(tempPath, _iniPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
            else
                File.Move(tempPath, _iniPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* 元の保存例外を隠さない */ }
            }
        }
    }

    private SaveState CaptureSaveState() => new(
        TvTestExecutablePath, BonDriverDirectory, ViewingTvTestExecutablePath,
        GrChannelFilePath, GrChSetFilePath, BscsChannelFilePath, BscsChSetFilePath,
        DataDirectory, SystemTheme, Port, EpgEnabled, EpgHour, EpgMinute, EpgDepth, EpgPreRecordMinutes,
        LaterProgramPriority, ChainRecordingEnabled,
        PreStartMarginSeconds, PostEndMarginSeconds, WakeMinutesBefore,
        WakeAdditionalSeconds, UseMinOption, UseNodshowOption,
        ShowTvAIrEpgRecTaskbarIcon, StartupEnabled, NetworkUsageEnabled, NetworkLanAccessEnabled,
        NetworkSessionLifetimeMinutes, NetworkPasswordEncrypted, RecordingAfterAction,
        RecordingAfterActionDelayMinutes, UserLogDetailEnabled, UserLogDetailReservationSource,
        UserLogDetailScheduledTime, UserLogDetailActualRecordingTime, UserLogDetailRecordingQuality,
        UserLogDetailStateChange, UserLogDetailEndOrFailureReason, CloneThemeGenrePalettes(ThemeGenrePalettes),
        EpgUseBelowNormalPriority, EpgDisableImmediateRetry, TaskUserName, TaskPasswordEncrypted,
        CloneTuners(Tuners), IsFirstRun, _persistedTvTestExecutablePath,
        _persistedViewingTvTestExecutablePath, _persistedBonDriverDirectory, _persistedUseMinOption,
        _persistedUseNodshowOption, CloneTuners(_persistedTuners), _persistedDataDirectory, _persistedPort);

    private void RestoreSaveState(SaveState state)
    {
        TvTestExecutablePath = state.TvTestExecutablePath;
        BonDriverDirectory = state.BonDriverDirectory;
        ViewingTvTestExecutablePath = state.ViewingTvTestExecutablePath;
        GrChannelFilePath = state.GrChannelFilePath;
        GrChSetFilePath = state.GrChSetFilePath;
        BscsChannelFilePath = state.BscsChannelFilePath;
        BscsChSetFilePath = state.BscsChSetFilePath;
        DataDirectory = state.DataDirectory;
        SystemTheme = state.SystemTheme;
        Port = state.Port;
        EpgEnabled = state.EpgEnabled;
        EpgHour = state.EpgHour;
        EpgMinute = state.EpgMinute;
        EpgDepth = state.EpgDepth;
        EpgPreRecordMinutes = state.EpgPreRecordMinutes;
        LaterProgramPriority = state.LaterProgramPriority;
        ChainRecordingEnabled = state.ChainRecordingEnabled;
        PreStartMarginSeconds = state.PreStartMarginSeconds;
        PostEndMarginSeconds = state.PostEndMarginSeconds;
        WakeMinutesBefore = state.WakeMinutesBefore;
        WakeAdditionalSeconds = state.WakeAdditionalSeconds;
        UseMinOption = state.UseMinOption;
        UseNodshowOption = state.UseNodshowOption;
        ShowTvAIrEpgRecTaskbarIcon = state.ShowTvAIrEpgRecTaskbarIcon;
        StartupEnabled = state.StartupEnabled;
        NetworkUsageEnabled = state.NetworkUsageEnabled;
        NetworkLanAccessEnabled = state.NetworkLanAccessEnabled;
        NetworkSessionLifetimeMinutes = state.NetworkSessionLifetimeMinutes;
        NetworkPasswordEncrypted = state.NetworkPasswordEncrypted;
        RecordingAfterAction = state.RecordingAfterAction;
        RecordingAfterActionDelayMinutes = state.RecordingAfterActionDelayMinutes;
        UserLogDetailEnabled = state.UserLogDetailEnabled;
        UserLogDetailReservationSource = state.UserLogDetailReservationSource;
        UserLogDetailScheduledTime = state.UserLogDetailScheduledTime;
        UserLogDetailActualRecordingTime = state.UserLogDetailActualRecordingTime;
        UserLogDetailRecordingQuality = state.UserLogDetailRecordingQuality;
        UserLogDetailStateChange = state.UserLogDetailStateChange;
        UserLogDetailEndOrFailureReason = state.UserLogDetailEndOrFailureReason;
        ThemeGenrePalettes = CloneThemeGenrePalettes(state.ThemeGenrePalettes);
        EpgUseBelowNormalPriority = state.EpgUseBelowNormalPriority;
        EpgDisableImmediateRetry = state.EpgDisableImmediateRetry;
        TaskUserName = state.TaskUserName;
        TaskPasswordEncrypted = state.TaskPasswordEncrypted;
        Tuners = CloneTuners(state.Tuners);
        IsFirstRun = state.IsFirstRun;
        _persistedTvTestExecutablePath = state.PersistedTvTestExecutablePath;
        _persistedViewingTvTestExecutablePath = state.PersistedViewingTvTestExecutablePath;
        _persistedBonDriverDirectory = state.PersistedBonDriverDirectory;
        _persistedUseMinOption = state.PersistedUseMinOption;
        _persistedUseNodshowOption = state.PersistedUseNodshowOption;
        _persistedTuners = CloneTuners(state.PersistedTuners);
        _persistedDataDirectory = state.PersistedDataDirectory;
        _persistedPort = state.PersistedPort;
    }

    private sealed record SaveState(
        string TvTestExecutablePath, string BonDriverDirectory, string ViewingTvTestExecutablePath,
        string GrChannelFilePath, string GrChSetFilePath, string BscsChannelFilePath, string BscsChSetFilePath,
        string DataDirectory, string SystemTheme, int Port, bool EpgEnabled, int EpgHour, int EpgMinute,
        string EpgDepth, int EpgPreRecordMinutes, bool LaterProgramPriority, bool ChainRecordingEnabled,
        int PreStartMarginSeconds, int PostEndMarginSeconds,
        int WakeMinutesBefore, int WakeAdditionalSeconds,
        bool UseMinOption, bool UseNodshowOption, bool ShowTvAIrEpgRecTaskbarIcon, bool StartupEnabled,
        bool NetworkUsageEnabled, bool NetworkLanAccessEnabled, int NetworkSessionLifetimeMinutes, string NetworkPasswordEncrypted,
        string RecordingAfterAction, int RecordingAfterActionDelayMinutes, bool UserLogDetailEnabled,
        bool UserLogDetailReservationSource, bool UserLogDetailScheduledTime, bool UserLogDetailActualRecordingTime,
        bool UserLogDetailRecordingQuality, bool UserLogDetailStateChange, bool UserLogDetailEndOrFailureReason,
        Dictionary<string, Dictionary<string, string>> ThemeGenrePalettes, bool EpgUseBelowNormalPriority,
        bool EpgDisableImmediateRetry, string TaskUserName, string TaskPasswordEncrypted, List<TunerProfileDto> Tuners,
        bool IsFirstRun, string PersistedTvTestExecutablePath, string PersistedViewingTvTestExecutablePath,
        string PersistedBonDriverDirectory, bool PersistedUseMinOption, bool PersistedUseNodshowOption,
        List<TunerProfileDto> PersistedTuners, string PersistedDataDirectory, int PersistedPort);

    // ── DTO変換（APIレスポンス用） ──────────────────────────────────
    public string ResolveDataDirectory(string? rawValue = null)
    {
        // rawValue省略時は、現在プロセスが使用中のRuntime設定を解決する。
        // 設定APIが保存済み値を表示する場合だけ、ToDtoから明示的にPersisted値を渡す。
        var source = rawValue ?? DataDirectory;
        var raw = string.IsNullOrWhiteSpace(source) ? "data" : source.Trim();
        return Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(_baseDirectory, raw));
    }

    public IniSettingsDto ToDto() => new()
    {
        TvTestExecutablePath = _persistedTvTestExecutablePath,
        BonDriverDirectory   = _persistedBonDriverDirectory,
        ViewingTvTestExecutablePath = _persistedViewingTvTestExecutablePath,
        GrChannelFilePath    = GrChannelFilePath,
        GrChSetFilePath      = GrChSetFilePath,
        BscsChannelFilePath  = BscsChannelFilePath,
        BscsChSetFilePath    = BscsChSetFilePath,
        DataDirectory        = _persistedDataDirectory,
        SystemTheme          = SystemTheme,
        EffectiveDataDirectory = ResolveDataDirectory(_persistedDataDirectory),
        Port                 = _persistedPort,
        EpgEnabled           = EpgEnabled,
        EpgHour              = EpgHour,
        EpgMinute            = EpgMinute,
        EpgDepth             = EpgDepth,
        EpgPreRecordMinutes       = EpgPreRecordMinutes,
        LaterProgramPriority  = LaterProgramPriority,
        ChainRecordingEnabled     = ChainRecordingEnabled,
        PreStartMarginSeconds = PreStartMarginSeconds,
        PostEndMarginSeconds  = PostEndMarginSeconds,
        WakeMinutesBefore     = WakeMinutesBefore,
        WakeAdditionalSeconds = WakeAdditionalSeconds,
        UseMinOption         = _persistedUseMinOption,
        UseNodshowOption     = _persistedUseNodshowOption,
        ShowTvAIrEpgRecTaskbarIcon = ShowTvAIrEpgRecTaskbarIcon,
        StartupEnabled       = StartupEnabled,
        NetworkUsageEnabled = NetworkUsageEnabled,
        NetworkLanAccessEnabled = NetworkLanAccessEnabled,
        NetworkSessionLifetimeMinutes = NetworkSessionLifetimeMinutes,
        NetworkHasPassword = !string.IsNullOrWhiteSpace(NetworkPasswordEncrypted),
        NetworkPasswordLength = GetNetworkPasswordLength(),
        RecordingAfterAction = RecordingAfterAction,
        RecordingAfterActionDelayMinutes = RecordingAfterActionDelayMinutes,
        UserLogDetailEnabled = UserLogDetailEnabled,
        UserLogDetailReservationSource = UserLogDetailReservationSource,
        UserLogDetailScheduledTime = UserLogDetailScheduledTime,
        UserLogDetailActualRecordingTime = UserLogDetailActualRecordingTime,
        UserLogDetailRecordingQuality = UserLogDetailRecordingQuality,
        UserLogDetailStateChange = UserLogDetailStateChange,
        UserLogDetailEndOrFailureReason = UserLogDetailEndOrFailureReason,
        ThemeGenrePalettes = CloneThemeGenrePalettes(ThemeGenrePalettes),
        DefaultThemeGenrePalettes = SettingsDefaults.CreateDefaultThemeGenrePalettes(),
        ThemeGenrePresetColors = SettingsDefaults.CreateThemeGenrePresetColors(),

        // EPG worker launch policy
        EpgUseBelowNormalPriority = EpgUseBelowNormalPriority,
        EpgDisableImmediateRetry  = EpgDisableImmediateRetry,


        TaskUserName         = TaskUserName,
        TaskHasPassword      = !string.IsNullOrEmpty(TaskPasswordEncrypted),
        TaskPasswordLength   = GetTaskPasswordLength(),
        Tuners               = CloneTuners(_persistedTuners),
        BonDriverList        = GetBonDriverList(_persistedBonDriverDirectory).ToList(),
        IsFirstRun           = IsFirstRun,
    };


    public bool IsTunerTopologyRestartPending() =>
        !MatchesRuntimeTunerTopology(
            _persistedTvTestExecutablePath,
            _persistedViewingTvTestExecutablePath,
            _persistedBonDriverDirectory,
            _persistedUseMinOption,
            _persistedUseNodshowOption,
            _persistedTuners);

    public WebSettingsDto ToWebDto()
    {
        var current = ToDto();
        return new WebSettingsDto
        {
            TvTestExecutablePath = current.TvTestExecutablePath,
            BonDriverDirectory = current.BonDriverDirectory,
            ViewingTvTestExecutablePath = current.ViewingTvTestExecutablePath,
            GrChannelFilePath = current.GrChannelFilePath,
            GrChSetFilePath = current.GrChSetFilePath,
            BscsChannelFilePath = current.BscsChannelFilePath,
            BscsChSetFilePath = current.BscsChSetFilePath,
            DataDirectory = current.DataDirectory,
            SystemTheme = current.SystemTheme,
            Port = current.Port,
            EpgEnabled = current.EpgEnabled,
            EpgHour = current.EpgHour,
            EpgMinute = current.EpgMinute,
            EpgDepth = current.EpgDepth,
            EpgPreRecordMinutes = current.EpgPreRecordMinutes,
            LaterProgramPriority = current.LaterProgramPriority,
            ChainRecordingEnabled = current.ChainRecordingEnabled,
            PreStartMarginSeconds = current.PreStartMarginSeconds,
            PostEndMarginSeconds = current.PostEndMarginSeconds,
            ShowTvAIrEpgRecTaskbarIcon = current.ShowTvAIrEpgRecTaskbarIcon,
            StartupEnabled = current.StartupEnabled,
            NetworkUsageEnabled = current.NetworkUsageEnabled,
            NetworkLanAccessEnabled = current.NetworkLanAccessEnabled,
            NetworkSessionLifetimeMinutes = current.NetworkSessionLifetimeMinutes,
            RecordingAfterAction = current.RecordingAfterAction,
            RecordingAfterActionDelayMinutes = current.RecordingAfterActionDelayMinutes,
            UserLogDetailEnabled = current.UserLogDetailEnabled,
            UserLogDetailReservationSource = current.UserLogDetailReservationSource,
            UserLogDetailScheduledTime = current.UserLogDetailScheduledTime,
            UserLogDetailActualRecordingTime = current.UserLogDetailActualRecordingTime,
            UserLogDetailRecordingQuality = current.UserLogDetailRecordingQuality,
            UserLogDetailStateChange = current.UserLogDetailStateChange,
            UserLogDetailEndOrFailureReason = current.UserLogDetailEndOrFailureReason,
            ThemeGenrePalettes = current.ThemeGenrePalettes,
            TaskUserName = current.TaskUserName,
            Tuners = current.Tuners,
            EffectiveDataDirectory = current.EffectiveDataDirectory,
            NetworkHasPassword = current.NetworkHasPassword,
            NetworkPasswordLength = current.NetworkPasswordLength,
            TaskHasPassword = current.TaskHasPassword,
            TaskPasswordLength = current.TaskPasswordLength,
            DefaultThemeGenrePalettes = current.DefaultThemeGenrePalettes,
            ThemeGenrePresetColors = current.ThemeGenrePresetColors,
            BonDriverList = current.BonDriverList,
            IsFirstRun = current.IsFirstRun
        };
    }

    public bool MatchesRuntimeTunerTopology(IniSettingsUpdateDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return MatchesRuntimeTunerTopology(
            dto.TvTestExecutablePath,
            dto.ViewingTvTestExecutablePath,
            dto.BonDriverDirectory,
            dto.UseMinOption,
            dto.UseNodshowOption,
            dto.Tuners);
    }

    private bool MatchesRuntimeTunerTopology(
        string? tvTestExecutablePath,
        string? viewingTvTestExecutablePath,
        string? bonDriverDirectory,
        bool useMinOption,
        bool useNodshowOption,
        IEnumerable<TunerProfileDto>? tuners)
    {
        return EqualsPathValue(TvTestExecutablePath, tvTestExecutablePath)
            && EqualsPathValue(ViewingTvTestExecutablePath, viewingTvTestExecutablePath)
            && EqualsPathValue(BonDriverDirectory, bonDriverDirectory)
            && UseMinOption == useMinOption
            && UseNodshowOption == useNodshowOption
            && string.Equals(BuildTunerTopologySignature(Tuners), BuildTunerTopologySignature(tuners), StringComparison.OrdinalIgnoreCase);
    }

    private void CapturePersistedTunerTopologyFromRuntime()
    {
        _persistedTvTestExecutablePath = TvTestExecutablePath;
        _persistedViewingTvTestExecutablePath = ViewingTvTestExecutablePath;
        _persistedBonDriverDirectory = BonDriverDirectory;
        _persistedUseMinOption = UseMinOption;
        _persistedUseNodshowOption = UseNodshowOption;
        _persistedTuners = CloneTuners(Tuners);
    }

    private void CapturePersistedHostSettingsFromRuntime()
    {
        _persistedDataDirectory = DataDirectory;
        _persistedPort = Port;
    }

    public bool IsHostSettingsRestartPending() =>
        !EqualsPathValue(_persistedDataDirectory, DataDirectory) ||
        _persistedPort != Port;

    public bool MatchesRuntimeHostSettings(IniSettingsUpdateDto dto) =>
        EqualsPathValue(dto.DataDirectory, DataDirectory) &&
        SettingsDefaults.NormalizePort(dto.Port) == Port;

    private static List<TunerProfileDto> CloneTuners(IEnumerable<TunerProfileDto>? tuners) =>
        (tuners ?? Enumerable.Empty<TunerProfileDto>()).Select((t, index) => new TunerProfileDto
        {
            Name = t.Name,
            BonDriverFileName = NormalizeBonDriverFileName(t.BonDriverFileName),
            Group = t.Group,
            Did = t.Did,
            Role = t.Role,
            LogicalViewerSlotId = LogicalViewerSlotIdentity.Resolve(t.LogicalViewerSlotId, t.Group, t.Did, t.Role, index + 1),
            DeviceNumber = t.DeviceNumber,
        }).ToList();

    private static bool EqualsPathValue(string? left, string? right) =>
        string.Equals(NormalizePathValue(left), NormalizePathValue(right), StringComparison.OrdinalIgnoreCase);

    internal static string NormalizePathValue(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;

        // SETTINGS_PATH_COMPARISON_ROOT_CONTRACT
        // 単純なTrimEndはWindowsドライブroot（例: C:\\）をC:へ変えて意味を変える。
        // Path.TrimEndingDirectorySeparatorでrootを保持し、末尾区切りだけを比較上正規化する。
        return Path.TrimEndingDirectorySeparator(trimmed);
    }

    // TUNER_TOPOLOGY_ORDERED_IDENTITY_CONTRACT
    // Tuner配列の順序は物理slot/T1..Snの割当順であり、並べ替えて比較してはならない。
    // LogicalViewerSlotIdもViewer profileの永続identityなので、他項目と同じTopology差分に含める。
    internal static string BuildTunerTopologySignature(IEnumerable<TunerProfileDto>? tuners)
    {
        return string.Join(";", (tuners ?? Enumerable.Empty<TunerProfileDto>())
            .Select((t, index) =>
            {
                var group = TunerDisplayName.NormalizeGroup(t.Group);
                var did = (t.Did ?? string.Empty).Trim().ToUpperInvariant();
                var role = NormalizeTunerRole(t.Role);
                var bon = NormalizeBonDriverFileName(t.BonDriverFileName);
                var name = TunerDisplayName.ForUi(t.Name, group, did);
                var logicalViewerSlotId = LogicalViewerSlotIdentity.Resolve(
                    t.LogicalViewerSlotId, group, did, role, index + 1);
                return $"{index}|{name}|{bon}|{group}|{did}|{role}|{logicalViewerSlotId}";
            }));
    }

    private int GetNetworkPasswordLength()
    {
        if (string.IsNullOrEmpty(NetworkPasswordEncrypted)) return 0;
        return CredentialProtector.Decrypt(NetworkPasswordEncrypted)?.Length ?? 0;
    }

    private int GetTaskPasswordLength()
    {
        if (string.IsNullOrEmpty(TaskPasswordEncrypted)) return 0;
        try
        {
            return CredentialProtector.Decrypt(TaskPasswordEncrypted)?.Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static Dictionary<string, Dictionary<string, string>> CloneThemeGenrePalettes(Dictionary<string, Dictionary<string, string>> source)
    {
        var normalized = NormalizeThemeGenrePalettes(source);
        return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["light"] = new Dictionary<string, string>(normalized["light"], StringComparer.OrdinalIgnoreCase),
            ["dark"] = new Dictionary<string, string>(normalized["dark"], StringComparer.OrdinalIgnoreCase),
        };
    }


    private static Dictionary<string, Dictionary<string, string>> LoadThemeGenrePalettes(
        Dictionary<string, string> dict)
    {
        var palettes = SettingsDefaults.CreateDefaultThemeGenrePalettes();
        foreach (var key in palettes["light"].Keys.ToList())
        {
            if (dict.TryGetValue($"GenreColor_Light_{key}", out var lightRaw))
                palettes["light"][key] = NormalizeGenreColor(lightRaw, palettes["light"][key]);
            if (dict.TryGetValue($"GenreColor_Dark_{key}", out var darkRaw))
                palettes["dark"][key] = NormalizeGenreColor(darkRaw, palettes["dark"][key]);
        }
        return palettes;
    }

    private static Dictionary<string, string> NormalizeGenreColorMap(Dictionary<string, string>? input, Dictionary<string, string> fallback)
    {
        var colors = new Dictionary<string, string>(fallback, StringComparer.OrdinalIgnoreCase);
        if (input is null) return colors;
        foreach (var key in colors.Keys.ToList())
        {
            if (input.TryGetValue(key, out var raw))
                colors[key] = NormalizeGenreColor(raw, colors[key]);
        }
        return colors;
    }

    private static Dictionary<string, Dictionary<string, string>> NormalizeThemeGenrePalettes(Dictionary<string, Dictionary<string, string>>? input)
    {
        var palettes = SettingsDefaults.CreateDefaultThemeGenrePalettes();
        if (input is null) return palettes;
        if (input.TryGetValue("light", out var light))
            palettes["light"] = NormalizeGenreColorMap(light, SettingsDefaults.CreateLightGenreColors());
        if (input.TryGetValue("dark", out var dark))
            palettes["dark"] = NormalizeGenreColorMap(dark, SettingsDefaults.CreateDarkGenreColors());
        return palettes;
    }

    internal static string BuildThemeGenrePaletteSignature(Dictionary<string, Dictionary<string, string>>? input)
    {
        // SETTINGS_GENRE_PALETTE_CANONICAL_DIFF_CONTRACT
        // 差分判定も保存処理と同じ正規化後の配色で比較する。
        // 欠落テーマ／欠落ジャンル／不正色は保存時に既定値へ補完されるため、
        // 生DTOの形だけを比較して実効値不変のINI再保存を起動してはならない。
        var normalized = NormalizeThemeGenrePalettes(input);
        return string.Join(";", normalized
            .OrderBy(theme => theme.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(theme => theme.Value
                .OrderBy(color => color.Key, StringComparer.OrdinalIgnoreCase)
                .Select(color => $"{theme.Key.Trim().ToLowerInvariant()}|{color.Key.Trim().ToLowerInvariant()}|{color.Value.Trim().ToLowerInvariant()}")));
    }

    private static string NormalizeGenreColor(string? raw, string fallback)
    {
        var v = (raw ?? string.Empty).Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$"))
            return v.ToLowerInvariant();
        return fallback;
    }

    public static string NormalizeSystemTheme(string? value)
        => SettingsDefaults.NormalizeSystemTheme(value);

    public static string NormalizeRecordingAfterAction(string? action)
        => SettingsDefaults.NormalizeRecordingAfterAction(action);

    internal static string NormalizeTaskUserName(string? value)
        => (value ?? string.Empty).Trim();

    internal static string NormalizeBonDriverFileName(string? value)
        => (value ?? string.Empty).Trim();

    public static int NormalizeRecordingAfterActionDelayMinutes(int minutes)
        => SettingsDefaults.NormalizeRecordingAfterActionDelayMinutes(minutes);

    public static string NormalizeTunerRole(string? role)
    {
        var raw = (role ?? string.Empty).Trim();
        return raw switch
        {
            "録画用" or "Recording" or "RECORDING" => "Recording",
            "視聴用" or "Viewing" or "VIEWING" => "Viewing",
            "Shared" or "SHARED" => "Recording",
            _ => raw,
        };
    }

    public static bool IsKnownTunerRole(string? role)
    {
        var normalized = NormalizeTunerRole(role);
        return normalized is "Recording" or "Viewing";
    }

    // ── ヘルパー ────────────────────────────────────────────────────
    private static string Get(Dictionary<string, string> d, string key, string def)
        => d.TryGetValue(key, out var v) ? v : def;

    private static int GetInt(Dictionary<string, string> d, string key, int def)
        => d.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : def;

    private static bool GetBool(Dictionary<string, string> d, string key, bool def)
        => d.TryGetValue(key, out var v) ? v.Trim().ToLowerInvariant() is "true" or "1" or "yes" : def;

    private static string GetStr(Dictionary<string, string> d, string key, string def)
        => d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : def;
}

/// <summary>チューナー1本分の設定DTO（API入出力・ini読み書き兼用）</summary>
public sealed class TunerProfileDto
{
    public string Name              { get; set; } = "";
    public string BonDriverFileName { get; set; } = "";
    public string Group             { get; set; } = ""; // GR / BSCS / HYBRID
    public string Did               { get; set; } = "";
    public string Role              { get; set; } = "";
    public string LogicalViewerSlotId { get; set; } = "";
    public int DeviceNumber { get; set; }
}

/// <summary>設定の読取／保存で共有する永続化値。書込み命令や表示補助値は含めない。</summary>
public class IniSettingsValuesDto
{
    public string TvTestExecutablePath { get; set; } = "";
    public string BonDriverDirectory   { get; set; } = "";
    public string ViewingTvTestExecutablePath { get; set; } = "";
    public string GrChannelFilePath    { get; set; } = "";
    public string GrChSetFilePath      { get; set; } = "";
    public string BscsChannelFilePath  { get; set; } = "";
    public string BscsChSetFilePath    { get; set; } = "";
    public string DataDirectory        { get; set; } = "";
    public string SystemTheme          { get; set; } = SettingsDefaults.SystemTheme;
    public int    Port                 { get; set; } = SettingsDefaults.Port;
    public bool   EpgEnabled           { get; set; } = SettingsDefaults.EpgEnabled;
    public int    EpgHour              { get; set; } = SettingsDefaults.EpgHour;
    public int    EpgMinute            { get; set; } = SettingsDefaults.EpgMinute;
    public string EpgDepth             { get; set; } = SettingsDefaults.EpgDepth;
    public int    EpgPreRecordMinutes  { get; set; } = SettingsDefaults.EpgPreRecordMinutes;
    public bool   LaterProgramPriority { get; set; } = SettingsDefaults.LaterProgramPriority;
    public bool   ChainRecordingEnabled { get; set; } = SettingsDefaults.ChainRecordingEnabled;
    public int    PreStartMarginSeconds { get; set; } = SettingsDefaults.PreStartMarginSeconds;
    public int    PostEndMarginSeconds  { get; set; } = SettingsDefaults.PostEndMarginSeconds;
    public int    WakeMinutesBefore     { get; set; } = SettingsDefaults.WakeMinutesBefore;
    public int    WakeAdditionalSeconds { get; set; } = SettingsDefaults.WakeAdditionalSeconds;
    public bool   UseMinOption          { get; set; } = SettingsDefaults.UseMinOption;
    public bool   UseNodshowOption      { get; set; } = SettingsDefaults.UseNodshowOption;
    public bool   ShowTvAIrEpgRecTaskbarIcon { get; set; } = SettingsDefaults.ShowTvAIrEpgRecTaskbarIcon;
    public bool   StartupEnabled        { get; set; } = SettingsDefaults.StartupEnabled;
    public bool NetworkUsageEnabled { get; set; } = SettingsDefaults.NetworkUsageEnabled;
    public bool NetworkLanAccessEnabled { get; set; } = SettingsDefaults.NetworkLanAccessEnabled;
    public int NetworkSessionLifetimeMinutes { get; set; } = SettingsDefaults.NetworkSessionLifetimeMinutes;
    public string RecordingAfterAction  { get; set; } = SettingsDefaults.RecordingAfterAction;
    public int    RecordingAfterActionDelayMinutes { get; set; } = SettingsDefaults.RecordingAfterActionDelayMinutes;
    public bool   UserLogDetailEnabled { get; set; } = SettingsDefaults.UserLogDetailEnabled;
    public bool   UserLogDetailReservationSource { get; set; } = SettingsDefaults.UserLogDetailReservationSource;
    public bool   UserLogDetailScheduledTime { get; set; } = SettingsDefaults.UserLogDetailScheduledTime;
    public bool   UserLogDetailActualRecordingTime { get; set; } = SettingsDefaults.UserLogDetailActualRecordingTime;
    public bool   UserLogDetailRecordingQuality { get; set; } = SettingsDefaults.UserLogDetailRecordingQuality;
    public bool   UserLogDetailStateChange { get; set; } = SettingsDefaults.UserLogDetailStateChange;
    public bool   UserLogDetailEndOrFailureReason { get; set; } = SettingsDefaults.UserLogDetailEndOrFailureReason;
    public Dictionary<string, Dictionary<string, string>> ThemeGenrePalettes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool   EpgUseBelowNormalPriority { get; set; } = SettingsDefaults.EpgUseBelowNormalPriority;
    public bool   EpgDisableImmediateRetry  { get; set; } = SettingsDefaults.EpgDisableImmediateRetry;
    public string TaskUserName { get; set; } = "";
    public List<TunerProfileDto> Tuners { get; set; } = new();
}


/// <summary>Web設定画面が表示・編集する永続化値。画面にない内部設定は含めない。</summary>
public class WebSettingsValuesDto
{
    public string TvTestExecutablePath { get; set; } = "";
    public string BonDriverDirectory { get; set; } = "";
    public string ViewingTvTestExecutablePath { get; set; } = "";
    public string GrChannelFilePath { get; set; } = "";
    public string GrChSetFilePath { get; set; } = "";
    public string BscsChannelFilePath { get; set; } = "";
    public string BscsChSetFilePath { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string SystemTheme { get; set; } = SettingsDefaults.SystemTheme;
    public int Port { get; set; } = SettingsDefaults.Port;
    public bool EpgEnabled { get; set; } = SettingsDefaults.EpgEnabled;
    public int EpgHour { get; set; } = SettingsDefaults.EpgHour;
    public int EpgMinute { get; set; } = SettingsDefaults.EpgMinute;
    public string EpgDepth { get; set; } = SettingsDefaults.EpgDepth;
    public int EpgPreRecordMinutes { get; set; } = SettingsDefaults.EpgPreRecordMinutes;
    public bool LaterProgramPriority { get; set; } = SettingsDefaults.LaterProgramPriority;
    public bool ChainRecordingEnabled { get; set; } = SettingsDefaults.ChainRecordingEnabled;
    public int PreStartMarginSeconds { get; set; } = SettingsDefaults.PreStartMarginSeconds;
    public int PostEndMarginSeconds { get; set; } = SettingsDefaults.PostEndMarginSeconds;
    public bool ShowTvAIrEpgRecTaskbarIcon { get; set; } = SettingsDefaults.ShowTvAIrEpgRecTaskbarIcon;
    public bool StartupEnabled { get; set; } = SettingsDefaults.StartupEnabled;
    public bool NetworkUsageEnabled { get; set; } = SettingsDefaults.NetworkUsageEnabled;
    public bool NetworkLanAccessEnabled { get; set; } = SettingsDefaults.NetworkLanAccessEnabled;
    public int NetworkSessionLifetimeMinutes { get; set; } = SettingsDefaults.NetworkSessionLifetimeMinutes;
    public string RecordingAfterAction { get; set; } = SettingsDefaults.RecordingAfterAction;
    public int RecordingAfterActionDelayMinutes { get; set; } = SettingsDefaults.RecordingAfterActionDelayMinutes;
    public bool UserLogDetailEnabled { get; set; } = SettingsDefaults.UserLogDetailEnabled;
    public bool UserLogDetailReservationSource { get; set; } = SettingsDefaults.UserLogDetailReservationSource;
    public bool UserLogDetailScheduledTime { get; set; } = SettingsDefaults.UserLogDetailScheduledTime;
    public bool UserLogDetailActualRecordingTime { get; set; } = SettingsDefaults.UserLogDetailActualRecordingTime;
    public bool UserLogDetailRecordingQuality { get; set; } = SettingsDefaults.UserLogDetailRecordingQuality;
    public bool UserLogDetailStateChange { get; set; } = SettingsDefaults.UserLogDetailStateChange;
    public bool UserLogDetailEndOrFailureReason { get; set; } = SettingsDefaults.UserLogDetailEndOrFailureReason;
    public Dictionary<string, Dictionary<string, string>> ThemeGenrePalettes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string TaskUserName { get; set; } = "";
    public List<TunerProfileDto> Tuners { get; set; } = new();
}

/// <summary>Web設定画面の保存要求。資格情報は明示更新命令だけを追加する。</summary>
public sealed class WebSettingsUpdateDto : WebSettingsValuesDto
{
    public string? NetworkPasswordPlain { get; set; } = null;
    public bool ClearNetworkPassword { get; set; } = false;
    public string? TaskPasswordPlain { get; set; } = null;
    public bool ClearTaskPassword { get; set; } = false;
}

/// <summary>Web設定画面の取得応答。保存不能な表示補助情報だけを追加する。</summary>
public sealed class WebSettingsDto : WebSettingsValuesDto
{
    public string EffectiveDataDirectory { get; set; } = "";
    public bool NetworkHasPassword { get; set; } = false;
    public int NetworkPasswordLength { get; set; } = 0;
    public bool TaskHasPassword { get; set; } = false;
    public int TaskPasswordLength { get; set; } = 0;
    public Dictionary<string, Dictionary<string, string>> DefaultThemeGenrePalettes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> ThemeGenrePresetColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> BonDriverList { get; set; } = new();
    public bool IsFirstRun { get; set; } = false;
}

/// <summary>設定保存要求。永続化値に、明示的な資格情報更新命令だけを追加する。</summary>
public sealed class IniSettingsUpdateDto : IniSettingsValuesDto
{
    public string? NetworkPasswordPlain { get; set; } = null;
    public bool ClearNetworkPassword { get; set; } = false;
    public string? TaskPasswordPlain { get; set; } = null;
    public bool ClearTaskPassword { get; set; } = false;
}

/// <summary>設定取得応答。永続化値に読取専用の表示補助情報だけを追加する。</summary>
public sealed class IniSettingsDto : IniSettingsValuesDto
{
    public string EffectiveDataDirectory { get; set; } = "";
    public bool NetworkHasPassword { get; set; } = false;
    public int NetworkPasswordLength { get; set; } = 0;
    public bool TaskHasPassword { get; set; } = false;
    public int TaskPasswordLength { get; set; } = 0;
    public Dictionary<string, Dictionary<string, string>> DefaultThemeGenrePalettes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> ThemeGenrePresetColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> BonDriverList { get; set; } = new();
    public bool IsFirstRun { get; set; } = false;
}

