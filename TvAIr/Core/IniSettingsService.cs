namespace TvAIr.Core;

/// <summary>
/// TvAIr.ini を読み書きするサービス。
/// ini が存在しない場合は IsFirstRun = true を返す。
/// </summary>
public sealed class IniSettingsService
{
    private readonly string _iniPath;
    private readonly string _baseDirectory;

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
    public string SystemTheme          { get; private set; } = "current";
    public int    Port                 { get; private set; } = 55884;
    public bool   EpgEnabled           { get; private set; } = true;
    public int    EpgHour              { get; private set; } = 3;
    public int    EpgMinute            { get; private set; } = 0;
    public string EpgDepth             { get; private set; } = "medium";
    public int    EpgPreRecordMinutes       { get; private set; } = 15;
    public bool   LaterProgramPriority  { get; private set; } = false;
    public bool   PseudoContinuousRecording      { get; private set; } = false;
    public int    PseudoContinuousMarginSeconds  { get; private set; } = 60;
    public int    PreStartMarginSeconds { get; private set; } = 30;
    public int    PostEndMarginSeconds  { get; private set; } = 30;
    public int    RecDelaySeconds       { get; private set; } = 10;
    public int    WakeMinutesBefore     { get; private set; } = 10;
    /// <summary>新設: スリープ復帰の余裕秒数。EPG確認起床と録画起床の両方に加算される。</summary>
    public int    WakeAdditionalSeconds { get; private set; } = 30;
    /// <summary>同一物理チューナースロットを連続で確保する際の最小間隔（ミリ秒）。0で無効。
    /// BonDriverのClose/Open競合を緩和するための TunerSlotCooldownMs。</summary>
    public int    TunerSlotCooldownMs   { get; private set; } = 15000;
    public bool   UseMinOption          { get; private set; } = true;
    public bool   UseNodshowOption      { get; private set; } = true;
    /// <summary>TVTest録画設定相当: 現在のサービスのみ保存する。デフォルトtrue。</summary>
    public bool   TvTestRecordCurServiceOnly { get; private set; } = true;
    /// <summary>TVTest録画設定相当: 字幕データを保存する。デフォルトtrue。</summary>
    public bool   TvTestRecordSubtitle { get; private set; } = true;
    /// <summary>TVTest録画設定相当: データ放送を保存する。デフォルトfalse。</summary>
    public bool   TvTestRecordDataCarrousel { get; private set; } = false;
    /// <summary>TvAIrEpgRec worker をタスクバーに表示する。false の場合はTvAIrトレイ点滅を代表インジケータにする。</summary>
    public bool   ShowTvAIrEpgRecTaskbarIcon { get; private set; } = true;
    public bool   StartupEnabled        { get; private set; } = false;
    public string RecordingAfterAction  { get; private set; } = "none";
    public int    RecordingAfterActionDelayMinutes { get; private set; } = 1;

    /// <summary>番組表ジャンル別セル背景色。キーは g-news 等、値は #RRGGBB。未設定はTvRock準拠色。</summary>
    public Dictionary<string, string> GenreColors { get; private set; } = CreateDefaultGenreColors();

    /// <summary>テーマ別ジャンル色。light=TvRock標準色、dark=ダークテーマ用色。</summary>
    public Dictionary<string, Dictionary<string, string>> ThemeGenrePalettes { get; private set; } = CreateDefaultThemeGenrePalettes();

    // ─── EPG worker launch timing / tuner cooldown policy ───
    /// <summary>EPG用TVTestプロセスをBelowNormal優先度で起動するか（true=有効、デフォルトtrue）。
    /// LIVE視聴TVTestと同優先度競合によるカクつきを軽減。</summary>
    public bool   EpgUseBelowNormalPriority    { get; private set; } = true;
    /// <summary>同時並列起動するチューナー間のジョブ投入間隔（ミリ秒）。
    /// 並列上限内であってもこの間隔ずつ起動を遅らせて初期化集中を分散。0で無効。デフォルト2000ms。</summary>
    public int    EpgLaunchStaggerMs           { get; private set; } = 2000;
    /// <summary>TVTest起動成功後にチャンネル安定化を待つ時間（ミリ秒）。
    /// 起動直後の連続CmdSetCh発火を抑制。0で無効。デフォルト4000ms。</summary>
    public int    EpgPostLaunchStabilizeMs     { get; private set; } = 4000;
    /// <summary>LIVE視聴中(視聴用TVTest=/recなしで起動された)チューナーをEPG取得対象から除外する。
    /// true=プロセス一覧でTVTest.exeを検出し /d と /DID から該当チューナーを除外。デフォルトtrue。</summary>
    public bool   EpgExcludeLiveTvTest         { get; private set; } = true;
    /// <summary>同一TSのattempt即時リトライを無効化する。
    /// true=失敗局は再巡回パスのみで対応（即時の負荷スパイク回避）。デフォルトtrue。</summary>
    public bool   EpgDisableImmediateRetry     { get; private set; } = true;

    /// <summary>タスクスケジューラー登録用ユーザー名（空=資格情報なし・InteractiveToken方式）</summary>
    public string TaskUserName          { get; private set; } = "";
    /// <summary>タスクスケジューラー登録用パスワード（DPAPI暗号化済みBase64。空=未設定）</summary>
    public string TaskPasswordEncrypted { get; private set; } = "";

    /// <summary>チューナー個別設定リスト（iniの[Tuner]セクションから読み込む）</summary>
    public List<TunerProfileDto> Tuners { get; private set; } = new();

    /// <summary>ini ファイルが存在しなかった（初回起動）場合 true</summary>
    public bool IsFirstRun { get; private set; } = false;

    public IniSettingsService(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        _iniPath = Path.Combine(baseDirectory, "TvAIr.ini");
        Load();
    }

    // ── BonDriver一覧取得 ────────────────────────────────────────────
    /// <summary>BonDriverDirectory 内の .dll ファイル名一覧を返す。</summary>
    public IReadOnlyList<string> GetBonDriverList()
    {
        if (string.IsNullOrWhiteSpace(BonDriverDirectory) || !Directory.Exists(BonDriverDirectory))
            return Array.Empty<string>();

        return Directory.GetFiles(BonDriverDirectory, "*.dll")
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Select(f => f!)
            .OrderBy(f => f)
            .ToList();
    }

    // ── 読み込み ────────────────────────────────────────────────────
    private void Load()
    {
        if (!File.Exists(_iniPath))
        {
            IsFirstRun = true;
            return;
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(_iniPath))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.StartsWith('#') || !line.Contains('=')) continue;
            var eq  = line.IndexOf('=');
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            dict[key] = val;
        }

        TvTestExecutablePath = Get(dict, "TvTestExecutablePath", TvTestExecutablePath);
        BonDriverDirectory   = Get(dict, "BonDriverDirectory",   BonDriverDirectory);
        ViewingTvTestExecutablePath = Get(dict, "ViewingTvTestExecutablePath", ViewingTvTestExecutablePath);
        GrChannelFilePath    = Get(dict, "GrChannelFilePath",    GrChannelFilePath);
        GrChSetFilePath      = Get(dict, "GrChSetFilePath",      GrChSetFilePath);
        BscsChannelFilePath  = Get(dict, "BscsChannelFilePath",  BscsChannelFilePath);
        BscsChSetFilePath    = Get(dict, "BscsChSetFilePath",    BscsChSetFilePath);
        DataDirectory        = Get(dict, "DataDirectory",        DataDirectory);
        SystemTheme          = NormalizeSystemTheme(GetStr(dict, "SystemTheme", SystemTheme));
        Port                 = GetInt(dict,  "Port",                Port);
        EpgEnabled           = GetBool(dict, "EpgEnabled",          EpgEnabled);
        EpgHour              = GetInt(dict,  "EpgHour",             EpgHour);
        EpgMinute            = GetInt(dict,  "EpgMinute",           EpgMinute);
        EpgDepth             = GetStr(dict,  "EpgDepth",            EpgDepth);
        EpgPreRecordMinutes       = GetInt(dict, "EpgPreRecordMinutes",       EpgPreRecordMinutes);
        LaterProgramPriority = GetBool(dict, "LaterProgramPriority", LaterProgramPriority);
        PseudoContinuousRecording     = GetBool(dict, "PseudoContinuousRecording",     PseudoContinuousRecording);
        PseudoContinuousMarginSeconds = GetInt(dict,  "PseudoContinuousMarginSeconds", PseudoContinuousMarginSeconds);
        PreStartMarginSeconds = GetInt(dict,  "PreStartMarginSeconds", PreStartMarginSeconds);
        PostEndMarginSeconds  = GetInt(dict,  "PostEndMarginSeconds",  PostEndMarginSeconds);
        RecDelaySeconds       = GetInt(dict,  "RecDelaySeconds",       RecDelaySeconds);
        WakeMinutesBefore     = GetInt(dict,  "WakeMinutesBefore",     WakeMinutesBefore);
        WakeAdditionalSeconds = GetInt(dict,  "WakeAdditionalSeconds", WakeAdditionalSeconds);
        TunerSlotCooldownMs   = GetInt(dict,  "TunerSlotCooldownMs",   TunerSlotCooldownMs);
        UseMinOption         = GetBool(dict, "UseMinOption",         UseMinOption);
        UseNodshowOption     = GetBool(dict, "UseNodshowOption",     UseNodshowOption);
        TvTestRecordCurServiceOnly = GetBool(dict, "TvTestRecordCurServiceOnly", TvTestRecordCurServiceOnly);
        TvTestRecordSubtitle = GetBool(dict, "TvTestRecordSubtitle", TvTestRecordSubtitle);
        TvTestRecordDataCarrousel = GetBool(dict, "TvTestRecordDataCarrousel", TvTestRecordDataCarrousel);
        ShowTvAIrEpgRecTaskbarIcon = GetBool(dict, "ShowTvAIrEpgRecTaskbarIcon", ShowTvAIrEpgRecTaskbarIcon);
        StartupEnabled       = GetBool(dict, "StartupEnabled",       StartupEnabled);
        RecordingAfterAction = NormalizeRecordingAfterAction(GetStr(dict, "RecordingAfterAction", RecordingAfterAction));
        RecordingAfterActionDelayMinutes = NormalizeRecordingAfterActionDelayMinutes(GetInt(dict, "RecordingAfterActionDelayMinutes", RecordingAfterActionDelayMinutes));
        ThemeGenrePalettes = LoadThemeGenrePalettes(dict);
        GenreColors = new Dictionary<string, string>(ThemeGenrePalettes["light"], StringComparer.OrdinalIgnoreCase);

        // EPG worker launch timing / tuner cooldown policy
        EpgUseBelowNormalPriority = GetBool(dict, "EpgUseBelowNormalPriority", EpgUseBelowNormalPriority);
        EpgLaunchStaggerMs        = GetInt(dict,  "EpgLaunchStaggerMs",        EpgLaunchStaggerMs);
        EpgPostLaunchStabilizeMs  = GetInt(dict,  "EpgPostLaunchStabilizeMs",  EpgPostLaunchStabilizeMs);
        EpgExcludeLiveTvTest      = GetBool(dict, "EpgExcludeLiveTvTest",      EpgExcludeLiveTvTest);
        EpgDisableImmediateRetry  = GetBool(dict, "EpgDisableImmediateRetry",  EpgDisableImmediateRetry);


        TaskUserName         = GetStr(dict,  "TaskUserName",         TaskUserName);
        TaskPasswordEncrypted = GetStr(dict, "TaskPasswordEncrypted", TaskPasswordEncrypted);

        // チューナー個別設定
        // 形式: Tuner1 = 名前, BonDriverファイル名, GR/BSCS/HYBRID, DID
        Tuners = new List<TunerProfileDto>();
        var count = GetInt(dict, "TunerCount", 0);

        if (count > 0)
        {
            // TunerCount が明示されている場合はその数だけ読む
            for (var i = 1; i <= count; i++)
            {
                if (!dict.TryGetValue($"Tuner{i}", out var val)) continue;
                var parts = val.Split(',', StringSplitOptions.TrimEntries);
                Tuners.Add(new TunerProfileDto
                {
                    Name              = TunerDisplayName.ForUi(parts.Length > 0 ? parts[0] : "", parts.Length > 2 ? parts[2] : "", parts.Length > 3 ? parts[3] : ""),
                    BonDriverFileName = parts.Length > 1 ? parts[1] : "",
                    Group             = TunerDisplayName.NormalizeGroup(parts.Length > 2 ? parts[2] : ""),
                    Did               = (parts.Length > 3 ? parts[3] : "").Trim().ToUpperInvariant(),
                    Role              = NormalizeTunerRole(parts.Length > 4 ? parts[4] : ""),
                });
            }
        }
        else
        {
            // TunerCount がない/0 の場合は Tuner1, Tuner2, ... を直接スキャンする
            for (var i = 1; i <= 64; i++)
            {
                if (!dict.TryGetValue($"Tuner{i}", out var val)) break;
                var parts = val.Split(',', StringSplitOptions.TrimEntries);
                Tuners.Add(new TunerProfileDto
                {
                    Name              = TunerDisplayName.ForUi(parts.Length > 0 ? parts[0] : "", parts.Length > 2 ? parts[2] : "", parts.Length > 3 ? parts[3] : ""),
                    BonDriverFileName = parts.Length > 1 ? parts[1] : "",
                    Group             = TunerDisplayName.NormalizeGroup(parts.Length > 2 ? parts[2] : ""),
                    Did               = (parts.Length > 3 ? parts[3] : "").Trim().ToUpperInvariant(),
                    Role              = NormalizeTunerRole(parts.Length > 4 ? parts[4] : ""),
                });
            }
        }
    }

    // ── 書き込み ────────────────────────────────────────────────────
    public void Save(IniSettingsDto dto, bool applyTunerTopologyToRuntime = true)
    {
        var runtimeTvTestExecutablePath = TvTestExecutablePath;
        var runtimeViewingTvTestExecutablePath = ViewingTvTestExecutablePath;
        var runtimeBonDriverDirectory = BonDriverDirectory;
        // release_contract: ch2/ChSet はチューナーRuntimeTopologyではなくChannelMap契約。
        // チューナー変更でRuntimeTopology反映を保留する場合でも、保存済みChannelMapはChannelFileLoader側でcache key/invalidateにより反映する。
        var runtimeUseMinOption = UseMinOption;
        var runtimeUseNodshowOption = UseNodshowOption;

        TvTestExecutablePath = dto.TvTestExecutablePath;
        BonDriverDirectory   = dto.BonDriverDirectory;
        ViewingTvTestExecutablePath = dto.ViewingTvTestExecutablePath ?? "";
        GrChannelFilePath    = dto.GrChannelFilePath;
        GrChSetFilePath      = dto.GrChSetFilePath;
        BscsChannelFilePath  = dto.BscsChannelFilePath;
        BscsChSetFilePath    = dto.BscsChSetFilePath;
        DataDirectory        = dto.DataDirectory;
        SystemTheme          = NormalizeSystemTheme(dto.SystemTheme);
        Port                 = dto.Port;
        EpgEnabled           = dto.EpgEnabled;
        EpgHour              = Math.Clamp(dto.EpgHour, 0, 23);
        EpgMinute            = Math.Clamp(dto.EpgMinute, 0, 59);
        EpgDepth             = dto.EpgDepth is "shallow" or "medium" or "deep" or "deeper" ? dto.EpgDepth : "medium";
        EpgPreRecordMinutes       = dto.EpgPreRecordMinutes is 0 or 5 or 10 or 15 or 20 ? dto.EpgPreRecordMinutes : 15;
        LaterProgramPriority  = dto.LaterProgramPriority;
        PseudoContinuousRecording     = dto.PseudoContinuousRecording;
        PseudoContinuousMarginSeconds = Math.Max(1, dto.PseudoContinuousMarginSeconds);
        PreStartMarginSeconds = Math.Max(0, dto.PreStartMarginSeconds);
        PostEndMarginSeconds  = Math.Max(0, dto.PostEndMarginSeconds);
        RecDelaySeconds       = Math.Clamp(dto.RecDelaySeconds, 0, 60);
        WakeMinutesBefore     = Math.Max(0, dto.WakeMinutesBefore);
        WakeAdditionalSeconds = Math.Clamp(dto.WakeAdditionalSeconds, 0, 300);
        TunerSlotCooldownMs   = Math.Clamp(dto.TunerSlotCooldownMs, 0, 60000);
        UseMinOption         = dto.UseMinOption;
        UseNodshowOption     = dto.UseNodshowOption;
        TvTestRecordCurServiceOnly = dto.TvTestRecordCurServiceOnly;
        TvTestRecordSubtitle = dto.TvTestRecordSubtitle;
        TvTestRecordDataCarrousel = dto.TvTestRecordDataCarrousel;
        ShowTvAIrEpgRecTaskbarIcon = dto.ShowTvAIrEpgRecTaskbarIcon;
        StartupEnabled       = dto.StartupEnabled;
        RecordingAfterAction = NormalizeRecordingAfterAction(dto.RecordingAfterAction);
        RecordingAfterActionDelayMinutes = NormalizeRecordingAfterActionDelayMinutes(dto.RecordingAfterActionDelayMinutes);
        ThemeGenrePalettes = NormalizeThemeGenrePalettes(dto.ThemeGenrePalettes, dto.GenreColors);
        GenreColors = new Dictionary<string, string>(ThemeGenrePalettes["light"], StringComparer.OrdinalIgnoreCase);

        // EPG worker launch timing / tuner cooldown policy
        EpgUseBelowNormalPriority = dto.EpgUseBelowNormalPriority;
        EpgLaunchStaggerMs        = Math.Clamp(dto.EpgLaunchStaggerMs, 0, 10000);
        EpgPostLaunchStabilizeMs  = Math.Clamp(dto.EpgPostLaunchStabilizeMs, 0, 30000);
        EpgExcludeLiveTvTest      = dto.EpgExcludeLiveTvTest;
        EpgDisableImmediateRetry  = dto.EpgDisableImmediateRetry;


        TaskUserName         = dto.TaskUserName ?? "";
        // パスワードが平文で送られてきた場合はDPAPIで暗号化して保存
        // 空文字の場合はそのまま（クリア）、既に暗号化済みの場合はそのまま保持
        if (!string.IsNullOrEmpty(dto.TaskPasswordPlain))
            TaskPasswordEncrypted = CredentialProtector.Encrypt(dto.TaskPasswordPlain);
        else if (dto.TaskPasswordPlain == "")
            TaskPasswordEncrypted = "";
        // dto.TaskPasswordPlainがnullの場合は既存の暗号化済み値を保持
        var runtimeTunersBeforeSave = Tuners.Select(t => new TunerProfileDto
        {
            Name = t.Name,
            BonDriverFileName = t.BonDriverFileName,
            Group = t.Group,
            Did = t.Did,
            Role = t.Role,
        }).ToList();
        var persistedTuners = (dto.Tuners ?? new()).Select(t =>
        {
            var group = TunerDisplayName.NormalizeGroup(t.Group);
            var did = (t.Did ?? string.Empty).Trim().ToUpperInvariant();
            return new TunerProfileDto
            {
                Name = TunerDisplayName.ForUi(t.Name, group, did),
                BonDriverFileName = t.BonDriverFileName ?? string.Empty,
                Group = group,
                Did = did,
                Role = NormalizeTunerRole(t.Role),
            };
        }).ToList();
        // release_contract: チューナー変更は再起動必須。
        // iniファイルへは保存するが、稼働中のRuntimeTopology(TunerPool/Wake/EPG/PluginUiContext/ExternalTuner)へは即時反映しない。
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
            lines.Add($"Tuner{i + 1} = {t.Name}, {t.BonDriverFileName}, {t.Group}, {t.Did}, {NormalizeTunerRole(t.Role)}");
        }

        lines.AddRange(new[]
        {
            "",
            "[App]",
            $"DataDirectory        = {DataDirectory}",
            $"SystemTheme          = {SystemTheme}",
            $"Port                 = {Port}",
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
            $"RecDelaySeconds       = {RecDelaySeconds}",
            $"WakeMinutesBefore     = {WakeMinutesBefore}",
            $"WakeAdditionalSeconds = {WakeAdditionalSeconds}",
            $"TunerSlotCooldownMs   = {TunerSlotCooldownMs}",
            $"PseudoContinuousRecording     = {(PseudoContinuousRecording ? "true" : "false")}",
            $"PseudoContinuousMarginSeconds = {PseudoContinuousMarginSeconds}",
            $"RecordingAfterAction = {RecordingAfterAction}",
            $"RecordingAfterActionDelayMinutes = {RecordingAfterActionDelayMinutes}",
            "",
            "[TvTestOptions]",
            $"UseMinOption         = {(UseMinOption     ? "true" : "false")}",
            $"UseNodshowOption     = {(UseNodshowOption ? "true" : "false")}",
            $"TvTestRecordCurServiceOnly = {(TvTestRecordCurServiceOnly ? "true" : "false")}",
            $"TvTestRecordSubtitle = {(TvTestRecordSubtitle ? "true" : "false")}",
            $"TvTestRecordDataCarrousel = {(TvTestRecordDataCarrousel ? "true" : "false")}",
            $"ShowTvAIrEpgRecTaskbarIcon = {(ShowTvAIrEpgRecTaskbarIcon ? "true" : "false")}",
            "",
            "[EpgPerformance]",
            "; EPG worker launch timing / tuner cooldown policy",
            $"EpgUseBelowNormalPriority = {(EpgUseBelowNormalPriority ? "true" : "false")}",
            $"EpgLaunchStaggerMs        = {EpgLaunchStaggerMs}",
            $"EpgPostLaunchStabilizeMs  = {EpgPostLaunchStabilizeMs}",
            $"EpgExcludeLiveTvTest      = {(EpgExcludeLiveTvTest ? "true" : "false")}",
            $"EpgDisableImmediateRetry  = {(EpgDisableImmediateRetry ? "true" : "false")}",
            "",
            "[UiGenreColors]",
            "; release_contract: ジャンル色はテーマ連動。LightはTvRock標準色、Darkはダークテーマ専用色。",
        });

        foreach (var kv in ThemeGenrePalettes["light"].OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"GenreColor_Light_{kv.Key} = {kv.Value}");
        foreach (var kv in ThemeGenrePalettes["dark"].OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"GenreColor_Dark_{kv.Key} = {kv.Value}");

        lines.AddRange(new[]
        {
            "",
            "[App2]",
            $"StartupEnabled       = {(StartupEnabled   ? "true" : "false")}",
            $"TaskUserName         = {TaskUserName}",
            $"TaskPasswordEncrypted = {TaskPasswordEncrypted}",
        });

        File.WriteAllLines(_iniPath, lines);

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
    }

    // ── DTO変換（APIレスポンス用） ──────────────────────────────────
    public string ResolveDataDirectory(string? rawValue = null)
    {
        var raw = string.IsNullOrWhiteSpace(rawValue) ? "data" : rawValue.Trim();
        return Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(_baseDirectory, raw));
    }

    public IniSettingsDto ToDto() => new()
    {
        TvTestExecutablePath = TvTestExecutablePath,
        BonDriverDirectory   = BonDriverDirectory,
        ViewingTvTestExecutablePath = ViewingTvTestExecutablePath,
        GrChannelFilePath    = GrChannelFilePath,
        GrChSetFilePath      = GrChSetFilePath,
        BscsChannelFilePath  = BscsChannelFilePath,
        BscsChSetFilePath    = BscsChSetFilePath,
        DataDirectory        = DataDirectory,
        SystemTheme          = SystemTheme,
        EffectiveDataDirectory = ResolveDataDirectory(DataDirectory),
        Port                 = Port,
        EpgEnabled           = EpgEnabled,
        EpgHour              = EpgHour,
        EpgMinute            = EpgMinute,
        EpgDepth             = EpgDepth,
        EpgPreRecordMinutes       = EpgPreRecordMinutes,
        LaterProgramPriority  = LaterProgramPriority,
        PseudoContinuousRecording     = PseudoContinuousRecording,
        PseudoContinuousMarginSeconds = PseudoContinuousMarginSeconds,
        PreStartMarginSeconds = PreStartMarginSeconds,
        PostEndMarginSeconds  = PostEndMarginSeconds,
        RecDelaySeconds       = RecDelaySeconds,
        WakeMinutesBefore     = WakeMinutesBefore,
        WakeAdditionalSeconds = WakeAdditionalSeconds,
        TunerSlotCooldownMs   = TunerSlotCooldownMs,
        UseMinOption         = UseMinOption,
        UseNodshowOption     = UseNodshowOption,
        TvTestRecordCurServiceOnly = TvTestRecordCurServiceOnly,
        TvTestRecordSubtitle = TvTestRecordSubtitle,
        TvTestRecordDataCarrousel = TvTestRecordDataCarrousel,
        ShowTvAIrEpgRecTaskbarIcon = ShowTvAIrEpgRecTaskbarIcon,
        StartupEnabled       = StartupEnabled,
        RecordingAfterAction = RecordingAfterAction,
        RecordingAfterActionDelayMinutes = RecordingAfterActionDelayMinutes,
        GenreColors = new Dictionary<string, string>(GenreColors, StringComparer.OrdinalIgnoreCase),
        DefaultGenreColors = CreateDefaultGenreColors(),
        LightGenreColors = new Dictionary<string, string>(ThemeGenrePalettes["light"], StringComparer.OrdinalIgnoreCase),
        DarkGenreColors = new Dictionary<string, string>(ThemeGenrePalettes["dark"], StringComparer.OrdinalIgnoreCase),
        ThemeGenrePalettes = CloneThemeGenrePalettes(ThemeGenrePalettes),

        // EPG worker launch timing / tuner cooldown policy
        EpgUseBelowNormalPriority = EpgUseBelowNormalPriority,
        EpgLaunchStaggerMs        = EpgLaunchStaggerMs,
        EpgPostLaunchStabilizeMs  = EpgPostLaunchStabilizeMs,
        EpgExcludeLiveTvTest      = EpgExcludeLiveTvTest,
        EpgDisableImmediateRetry  = EpgDisableImmediateRetry,


        TaskUserName         = TaskUserName,
        TaskPasswordPlain    = null, // セキュリティ上、パスワードはAPIレスポンスに含めない
        TaskHasPassword      = !string.IsNullOrEmpty(TaskPasswordEncrypted),
        Tuners               = Tuners,
        BonDriverList        = GetBonDriverList().ToList(),
        IsFirstRun           = IsFirstRun,
    };

    
    public static Dictionary<string, string> CreateDefaultGenreColors() => CreateLightGenreColors();

    public static Dictionary<string, string> CreateLightGenreColors() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["g-news"]    = "#d3ffcb",
        ["g-sports"]  = "#ffcbee",
        ["g-info"]    = "#b8f0ac",
        ["g-drama"]   = "#ffbbbb",
        ["g-music"]   = "#b4f2ff",
        ["g-variety"] = "#faffb4",
        ["g-movie"]   = "#cbfcf4",
        ["g-anime"]   = "#dcdcfe",
        ["g-docu"]    = "#f0f0f0",
        ["g-other"]   = "#f0f0f0",
    };

    public static Dictionary<string, string> CreateDarkGenreColors() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["g-news"]    = "#1f5a45",
        ["g-sports"]  = "#245c7a",
        ["g-info"]    = "#2d6f61",
        ["g-drama"]   = "#6b3341",
        ["g-music"]   = "#286b78",
        ["g-variety"] = "#6b5a24",
        ["g-movie"]   = "#563a73",
        ["g-anime"]   = "#394f95",
        ["g-docu"]    = "#3f5366",
        ["g-other"]   = "#4a5058",
    };

    public static Dictionary<string, Dictionary<string, string>> CreateDefaultThemeGenrePalettes() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["light"] = CreateLightGenreColors(),
        ["dark"] = CreateDarkGenreColors(),
    };

    private static Dictionary<string, Dictionary<string, string>> CloneThemeGenrePalettes(Dictionary<string, Dictionary<string, string>> source)
    {
        var normalized = NormalizeThemeGenrePalettes(source, null);
        return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["light"] = new Dictionary<string, string>(normalized["light"], StringComparer.OrdinalIgnoreCase),
            ["dark"] = new Dictionary<string, string>(normalized["dark"], StringComparer.OrdinalIgnoreCase),
        };
    }


    private static readonly IReadOnlyList<Dictionary<string, string>> LegacyDarkGenreDefaults = new[]
    {
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["g-news"] = "#2f6b3f", ["g-sports"] = "#7a3a63", ["g-info"] = "#2f6540", ["g-drama"] = "#7a3c3c", ["g-music"] = "#2f6878",
            ["g-variety"] = "#756d2f", ["g-movie"] = "#2f6d66", ["g-anime"] = "#55579a", ["g-docu"] = "#56616d", ["g-other"] = "#4b5563",
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["g-news"] = "#244c3a", ["g-sports"] = "#204b63", ["g-info"] = "#2f5e46", ["g-drama"] = "#5a2a32", ["g-music"] = "#284e5a",
            ["g-variety"] = "#5a4a22", ["g-movie"] = "#4d365e", ["g-anime"] = "#343f73", ["g-docu"] = "#2f465a", ["g-other"] = "#3f4248",
        },
    };

    private static bool IsLegacyDarkGenreDefault(Dictionary<string, string> colors)
        => LegacyDarkGenreDefaults.Any(legacy => legacy.Keys.All(key => colors.TryGetValue(key, out var v) && string.Equals(v, legacy[key], StringComparison.OrdinalIgnoreCase)));

    private static Dictionary<string, string> MigrateDarkGenreDefault(Dictionary<string, string> colors)
        => IsLegacyDarkGenreDefault(colors) ? CreateDarkGenreColors() : colors;

    private static Dictionary<string, Dictionary<string, string>> LoadThemeGenrePalettes(Dictionary<string, string> dict)
    {
        var palettes = CreateDefaultThemeGenrePalettes();
        foreach (var key in palettes["light"].Keys.ToList())
        {
            if (dict.TryGetValue($"GenreColor_Light_{key}", out var lightRaw))
                palettes["light"][key] = NormalizeGenreColor(lightRaw, palettes["light"][key]);
            else if (dict.TryGetValue($"GenreColor_{key}", out var legacyRaw))
                palettes["light"][key] = NormalizeGenreColor(legacyRaw, palettes["light"][key]);

            if (dict.TryGetValue($"GenreColor_Dark_{key}", out var darkRaw))
                palettes["dark"][key] = NormalizeGenreColor(darkRaw, palettes["dark"][key]);
        }
        palettes["dark"] = MigrateDarkGenreDefault(palettes["dark"]);
        return palettes;
    }

    private static Dictionary<string, string> LoadGenreColors(Dictionary<string, string> dict)
    {
        var palettes = LoadThemeGenrePalettes(dict);
        return new Dictionary<string, string>(palettes["light"], StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> NormalizeGenreColors(Dictionary<string, string>? input) => NormalizeGenreColorMap(input, CreateLightGenreColors());

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

    private static Dictionary<string, Dictionary<string, string>> NormalizeThemeGenrePalettes(Dictionary<string, Dictionary<string, string>>? input, Dictionary<string, string>? legacyLight)
    {
        var palettes = CreateDefaultThemeGenrePalettes();
        if (input is not null)
        {
            if (input.TryGetValue("light", out var light))
                palettes["light"] = NormalizeGenreColorMap(light, CreateLightGenreColors());
            if (input.TryGetValue("dark", out var dark))
                palettes["dark"] = MigrateDarkGenreDefault(NormalizeGenreColorMap(dark, CreateDarkGenreColors()));
        }
        else if (legacyLight is not null)
        {
            palettes["light"] = NormalizeGenreColorMap(legacyLight, CreateLightGenreColors());
        }
        return palettes;
    }

    private static string NormalizeGenreColor(string? raw, string fallback)
    {
        var v = (raw ?? string.Empty).Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$"))
            return v.ToLowerInvariant();
        return fallback;
    }

    public static string NormalizeSystemTheme(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v is "current" or "light" or "dark" ? v : "current";
    }

    public static string NormalizeRecordingAfterAction(string? action)
    {
        var a = (action ?? string.Empty).Trim().ToLowerInvariant();
        return a switch
        {
            "sleep" or "スリープ" => "sleep",
            "shutdown" or "シャットダウン" => "shutdown",
            _ => "none",
        };
    }

    public static int NormalizeRecordingAfterActionDelayMinutes(int minutes)
    {
        return Math.Clamp(minutes, 1, 5);
    }

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
}

/// <summary>設定値の転送オブジェクト（API入出力兼用）</summary>
public sealed class IniSettingsDto
{
    public string TvTestExecutablePath { get; set; } = "";
    public string BonDriverDirectory   { get; set; } = "";
    public string ViewingTvTestExecutablePath { get; set; } = "";
    public string GrChannelFilePath    { get; set; } = "";
    public string GrChSetFilePath      { get; set; } = "";
    public string BscsChannelFilePath  { get; set; } = "";
    public string BscsChSetFilePath    { get; set; } = "";
    public string DataDirectory        { get; set; } = "";
    public string SystemTheme          { get; set; } = "current";
    public string EffectiveDataDirectory { get; set; } = "";
    public int    Port                 { get; set; } = 55884;
    public bool   EpgEnabled           { get; set; } = true;
    public int    EpgHour              { get; set; } = 3;
    public int    EpgMinute            { get; set; } = 0;
    public string EpgDepth             { get; set; } = "medium";
    public int    EpgPreRecordMinutes       { get; set; } = 15;
    public bool   LaterProgramPriority  { get; set; } = false;
    public bool   PseudoContinuousRecording      { get; set; } = false;
    public int    PseudoContinuousMarginSeconds  { get; set; } = 60;
    public int    PreStartMarginSeconds { get; set; } = 30;
    public int    PostEndMarginSeconds  { get; set; } = 30;
    public int    RecDelaySeconds       { get; set; } = 10;
    public int    WakeMinutesBefore     { get; set; } = 10;
    /// <summary>新設: スリープ復帰の余裕秒数。</summary>
    public int    WakeAdditionalSeconds { get; set; } = 30;
    /// <summary>同一物理チューナースロットを連続で確保する際の最小間隔（ミリ秒）。0で無効。</summary>
    public int    TunerSlotCooldownMs   { get; set; } = 15000;
    public bool   UseMinOption          { get; set; } = true;
    public bool   UseNodshowOption     { get; set; } = false;
    public bool   TvTestRecordCurServiceOnly { get; set; } = true;
    public bool   TvTestRecordSubtitle { get; set; } = true;
    public bool   TvTestRecordDataCarrousel { get; set; } = false;
    public bool   ShowTvAIrEpgRecTaskbarIcon { get; set; } = true;
    public bool   StartupEnabled        { get; set; } = false;
    public string RecordingAfterAction  { get; set; } = "none";
    public int    RecordingAfterActionDelayMinutes { get; set; } = 1;
    public Dictionary<string, string> GenreColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DefaultGenreColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> LightGenreColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DarkGenreColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> ThemeGenrePalettes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // EPG worker launch timing / tuner cooldown policy
    /// <summary>EPG用TVTestをBelowNormal優先度で起動</summary>
    public bool   EpgUseBelowNormalPriority { get; set; } = true;
    /// <summary>並列チューナー間のジョブ投入インターバル(ms)</summary>
    public int    EpgLaunchStaggerMs        { get; set; } = 2000;
    /// <summary>TVTest起動後の安定化待機(ms)</summary>
    public int    EpgPostLaunchStabilizeMs  { get; set; } = 4000;
    /// <summary>LIVE視聴中チューナーをEPGから除外</summary>
    public bool   EpgExcludeLiveTvTest      { get; set; } = true;
    /// <summary>同一TS内 attempt即時リトライ無効化(再巡回パスのみ使用)</summary>
    public bool   EpgDisableImmediateRetry  { get; set; } = true;


    /// <summary>タスクスケジューラー用ユーザー名</summary>
    public string TaskUserName          { get; set; } = "";
    /// <summary>タスクスケジューラー用パスワード平文（保存時のみ使用。nullの場合は既存値を保持）</summary>
    public string? TaskPasswordPlain    { get; set; } = null;
    /// <summary>パスワードが設定済みかどうか（読み取り専用・UIでマスク表示用）</summary>
    public bool   TaskHasPassword       { get; set; } = false;
    public List<TunerProfileDto> Tuners { get; set; } = new();
    /// <summary>BonDriverDirectory内の.dllファイル名一覧（読み取り専用・UI用）</summary>
    public List<string> BonDriverList  { get; set; } = new();
    public bool   IsFirstRun           { get; set; } = false;
}
