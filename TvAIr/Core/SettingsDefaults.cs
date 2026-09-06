namespace TvAIr.Core;

/// <summary>
/// TvAIr settings defaults shared by persistence, API DTOs, UI projection, and runtime compatibility settings.
/// </summary>
public static class SettingsDefaults
{
    public const int Port = 55884;
    public const string SystemTheme = "current";

    public const bool EpgEnabled = true;
    public const int EpgHour = 3;
    public const int EpgMinute = 0;
    public const string EpgDepth = "medium";
    public const int EpgPreRecordMinutes = 15;
    public const bool EpgUseBelowNormalPriority = true;
    public const bool EpgDisableImmediateRetry = true;

    public const bool LaterProgramPriority = false;
    public const bool PseudoContinuousRecording = false;
    public const int PreStartMarginSeconds = 30;
    public const int PostEndMarginSeconds = 30;
    public const int WakeMinutesBefore = 10;
    public const int WakeAdditionalSeconds = 30;

    public const bool UseMinOption = true;
    public const bool UseNodshowOption = true;
    public const bool ShowTvAIrEpgRecTaskbarIcon = true;
    public const bool StartupEnabled = false;

    // NETWORK_USAGE_MASTER_INVARIANT
    // true=通常のネットワーク設定を評価する。false=完全閉域モードとしてloopback以外の通信を実効停止する。
    // 下位のLAN/Plugin設定値は保持し、NetworkUsageEnabled=trueへ戻した時だけ再評価する。
    public const bool NetworkUsageEnabled = false;

    // NETWORK_ACCESS_SETTINGS_INVARIANT
    // 待受範囲、認証、設定画面、保存、読込、標準に戻すはこの正本を共有する。
    public const bool NetworkLanAccessEnabled = false;
    public const int NetworkSessionLifetimeMinutes = 480;
    public const int NetworkSessionLifetimeMinutesMin = 15;
    public const int NetworkSessionLifetimeMinutesMax = 1440;
    public const int NetworkPasswordMinLength = 12;

    public const string RecordingAfterAction = "none";
    public const int RecordingAfterActionDelayMinutes = 1;

    // USER_LOG_DETAIL_SETTINGS_TOKEN_INVARIANT
    // 標準表示は常に1行。詳細表示は有効時に選択項目だけを2行目以降へ投影する。
    // default / DTO / INI / 設定画面 / 保存 / 読込 / 標準に戻す / 表示投影は、この UserLogDetail* 正本を共有する。
    public const bool UserLogDetailEnabled = false;
    public const bool UserLogDetailReservationSource = true;
    public const bool UserLogDetailScheduledTime = true;
    public const bool UserLogDetailActualRecordingTime = true;
    public const bool UserLogDetailRecordingQuality = true;
    public const bool UserLogDetailStateChange = true;
    public const bool UserLogDetailEndOrFailureReason = true;

    // GENRE_PALETTE_DEFAULT_SINGLE_SOURCE
    // 初期値 / INI欠落補完 / API DefaultThemeGenrePalettes / 設定画面「標準に戻す」はこの配色だけを正本とする。
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
        ["g-drama"]   = "#704332",
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

    public const int PortMin = 1024;
    public const int PortMax = 65535;
    public const int EpgHourMin = 0;
    public const int EpgHourMax = 23;
    public const int EpgMinuteMin = 0;
    public const int EpgMinuteMax = 59;
    public static readonly int[] EpgHourOptions = Enumerable.Range(EpgHourMin, EpgHourMax - EpgHourMin + 1).ToArray();
    public static readonly int[] EpgMinuteOptions = Enumerable.Range(EpgMinuteMin, EpgMinuteMax - EpgMinuteMin + 1).ToArray();
    public static readonly string[] EpgDepthOptions = ["shallow", "medium", "deep", "deeper"];
    public static readonly string[] EpgDepthDisplayLabels = ["浅", "中", "深", "最深"];
    public static readonly int[] EpgPreRecordMinuteOptions = [5, 10, 15, 20];
    public static readonly int[] PreStartMarginSecondOptions = [0, 5, 10, 15, 20, 30, 45, 60];
    public static readonly int[] PostEndMarginSecondOptions = [0, 5, 10, 15, 20, 30];
    public static readonly int[] RecordingAfterActionDelayMinuteOptions = [1, 2, 3, 4, 5];
    public const int PreStartMarginSecondsMin = 0;
    public const int PreStartMarginSecondsMax = 60;
    public const int PostEndMarginSecondsMin = 0;
    public const int PostEndMarginSecondsMax = 30;
    public const int WakeMinutesBeforeMin = 0;
    public const int WakeAdditionalSecondsMin = 0;
    public const int WakeAdditionalSecondsMax = 300;
    public const int RecordingAfterActionDelayMinutesMin = 1;
    public const int RecordingAfterActionDelayMinutesMax = 5;

    public static int NormalizePort(int value) => Math.Clamp(value, PortMin, PortMax);
    public static int NormalizeEpgHour(int value) => Math.Clamp(value, EpgHourMin, EpgHourMax);
    public static int NormalizeEpgMinute(int value) => Math.Clamp(value, EpgMinuteMin, EpgMinuteMax);
    public static string NormalizeEpgDepth(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "shallow" => "shallow",
            "deep" => "deep",
            "deeper" => "deeper",
            _ => EpgDepth,
        };

    public static int NormalizeEpgPreRecordMinutes(int value)
        => value == 0 || EpgPreRecordMinuteOptions.Contains(value) ? value : EpgPreRecordMinutes;

    /// <summary>PreRecが有効な経路で使うlead time。無効値は標準値へ戻す。</summary>
    public static int ResolveEnabledEpgPreRecordMinutes(int value)
        => EpgPreRecordMinuteOptions.Contains(value) ? value : EpgPreRecordMinutes;

    public static string NormalizeSystemTheme(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => SystemTheme,
        };

    public static bool IsPreStartMarginSecondsAllowed(int value) => PreStartMarginSecondOptions.Contains(value);
    public static bool IsPostEndMarginSecondsAllowed(int value) => PostEndMarginSecondOptions.Contains(value);
    public static bool IsRecordingAfterActionDelayMinutesAllowed(int value) => RecordingAfterActionDelayMinuteOptions.Contains(value);
    public static int NormalizePreStartMarginSeconds(int value) => IsPreStartMarginSecondsAllowed(value) ? value : PreStartMarginSeconds;
    public static int NormalizePostEndMarginSeconds(int value) => IsPostEndMarginSecondsAllowed(value) ? value : PostEndMarginSeconds;
    public static int NormalizeWakeMinutesBefore(int value) => Math.Max(WakeMinutesBeforeMin, value);
    public static int NormalizeWakeAdditionalSeconds(int value) => Math.Clamp(value, WakeAdditionalSecondsMin, WakeAdditionalSecondsMax);

    public static string NormalizeRecordingAfterAction(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "sleep" or "スリープ" => "sleep",
            "shutdown" or "シャットダウン" => "shutdown",
            _ => RecordingAfterAction,
        };

    public static int NormalizeRecordingAfterActionDelayMinutes(int value)
        => IsRecordingAfterActionDelayMinutesAllowed(value) ? value : RecordingAfterActionDelayMinutes;

    public static int NormalizeNetworkSessionLifetimeMinutes(int value)
        => Math.Clamp(value, NetworkSessionLifetimeMinutesMin, NetworkSessionLifetimeMinutesMax);
}
