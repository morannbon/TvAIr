namespace TvAIr.Core;

/// <summary>
/// TVTest の録画関連設定を TvAIr 起動時に1回だけ監査する。
///
/// release_contract 方針:
/// - 録画直前の推測・設定探索は行わない。
/// - TVTest.exe と同階層の TVTest.ini を起動時に読み、録画に影響しそうな設定だけログへ明示する。
/// - 特に「現在のサービスのみ保存する」に相当するキーが実際に ini 上でどうなっているかを確認できるようにする。
/// - このクラスは診断専用。TVTest の設定値を書き換えない。
/// </summary>
public static class TvTestRecordingOptionsInspector
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static string _summary = "not_initialized";
    private static string _initializedTvTestExe = string.Empty;
    private static bool _recordCurServiceOnlyEnabled;

    public static void Initialize(string tvTestExecutablePath, LogRepository? log = null)
    {
        lock (Gate)
        {
            if (_initialized && string.Equals(_initializedTvTestExe, tvTestExecutablePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _initializedTvTestExe = tvTestExecutablePath ?? string.Empty;
            _summary = InspectCore(_initializedTvTestExe);
            _initialized = true;
            log?.Add("APP_TVTEST_RECORD_OPTIONS_INIT", "INFO", _summary);
        }
    }

    public static string GetSummary()
    {
        lock (Gate)
        {
            return _summary;
        }
    }

    public static bool IsRecordCurServiceOnlyEnabled()
    {
        lock (Gate)
        {
            return _recordCurServiceOnlyEnabled;
        }
    }

    private static string InspectCore(string tvTestExecutablePath)
    {
        _recordCurServiceOnlyEnabled = false;

        if (string.IsNullOrWhiteSpace(tvTestExecutablePath))
            return "result=FAIL evidence=tvtest_executable_empty";

        string tvTestDir;
        try
        {
            tvTestDir = Path.GetDirectoryName(Path.GetFullPath(tvTestExecutablePath)) ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"result=FAIL evidence=tvtest_executable_invalid type={ex.GetType().Name} message={Trim(ex.Message, 160)}";
        }

        if (string.IsNullOrWhiteSpace(tvTestDir))
            return "result=FAIL evidence=tvtest_directory_empty";

        var iniPath = Path.Combine(tvTestDir, "TVTest.ini");
        if (!File.Exists(iniPath))
            return $"result=FAIL evidence=tvtest_ini_not_found ini={iniPath}";

        var matches = new List<IniEntry>();
        try
        {
            string? section = null;
            var lines = File.ReadAllLines(iniPath);
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i].Trim();
                if (raw.Length == 0 || raw.StartsWith(';') || raw.StartsWith('#')) continue;

                if (raw.StartsWith('[') && raw.EndsWith(']') && raw.Length > 2)
                {
                    section = raw[1..^1].Trim();
                    continue;
                }

                var eq = raw.IndexOf('=');
                if (eq <= 0) continue;

                var key = raw[..eq].Trim();
                var value = raw[(eq + 1)..].Trim();
                if (IsRecordingOptionKey(key))
                    matches.Add(new IniEntry(section ?? "-", key, value, i + 1));
            }
        }
        catch (Exception ex)
        {
            return $"result=FAIL evidence=tvtest_ini_read_error ini={iniPath} type={ex.GetType().Name} message={Trim(ex.Message, 160)}";
        }

        var currentService = matches
            .Where(x => IsCurrentServiceOnlyKey(x.Key))
            .ToList();

        var strictCurrentService = currentService
            .Where(x => IsStrictCurrentServiceOnlyKey(x.Key))
            .ToList();
        _recordCurServiceOnlyEnabled = strictCurrentService.Any(x => IsTruthy(x.Value));

        var currentServiceText = currentService.Count == 0
            ? "current_service_only=NOT_FOUND"
            : "current_service_only=" + string.Join(" | ", currentService.Select(FormatEntry));

        var effectiveCurrentServiceText = $" effective_record_cur_service_only={(_recordCurServiceOnlyEnabled ? "yes" : "no")}";

        var optionText = matches.Count == 0
            ? "recording_related_keys=0"
            : "recording_related_keys=" + matches.Count;

        var truncated = matches.Count > 30 ? $" truncated=True truncatedCount={matches.Count - 30}" : " truncated=False";
        return $"result=OK tvTestIni=OK {currentServiceText}{effectiveCurrentServiceText} {optionText}{truncated} rule=startup_record_options_release_trim";
    }

    private static bool IsRecordingOptionKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var k = key.Trim();
        var lower = k.ToLowerInvariant();

        if (IsCurrentServiceOnlyKey(k)) return true;

        return lower.Contains("record")
            || lower.Contains("rec")
            || lower.Contains("service")
            || lower.Contains("save")
            || lower.Contains("ts")
            || lower.Contains("字幕")
            || lower.Contains("データ")
            || lower.Contains("録画")
            || lower.Contains("保存");
    }

    private static bool IsStrictCurrentServiceOnlyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var lower = key.Trim().ToLowerInvariant();
        // release_contract: TVTest.ini の実キーは RecordCurServiceOnly。
        // release_contractではログ候補としては拾えていたが、厳密判定キーに
        // recordcurserviceonly が無かったため effective_record_cur_service_only=no になっていた。
        return lower == "recordcurserviceonly"
            || lower == "recordcurservice"
            || lower == "reccurservice"
            || lower == "recordcurrentservice"
            || lower == "recordcurrentserviceonly"
            || lower == "savecurservice"
            || lower == "savecurserviceonly"
            || lower == "savecurrentservice"
            || lower == "savecurrentserviceonly";
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().ToLowerInvariant();
        return v == "yes" || v == "true" || v == "1" || v == "on" || v == "enable" || v == "enabled";
    }

    private static bool IsCurrentServiceOnlyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var lower = key.Trim().ToLowerInvariant();

        // TVTestのバージョン・派生設定名差異を想定し、現在サービス限定保存らしいキーを広めにログへ出す。
        // 書き換えはしないため、候補を広く拾っても録画動作には影響しない。
        return lower == "recordcurservice"
            || lower == "reccurservice"
            || lower == "recordcurrentservice"
            || lower == "savecurservice"
            || lower == "savecurrentservice"
            || lower.Contains("curservice")
            || lower.Contains("currentservice")
            || lower.Contains("serviceonly")
            || lower.Contains("onlyservice")
            || lower.Contains("singleprogram")
            || lower.Contains("single-service")
            || lower.Contains("current_service")
            || lower.Contains("現在のサービス")
            || lower.Contains("現在サービス");
    }

    private static string FormatEntry(IniEntry entry)
        => $"section={Safe(entry.Section)} key={Safe(entry.Key)} line={entry.Line} value={Safe(entry.Value)}";

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var v = value.Trim().Replace("\r", " ").Replace("\n", " ");
        return v.Length <= 120 ? v : v[..120];
    }

    private static string Trim(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    private sealed record IniEntry(string Section, string Key, string Value, int Line);
}
