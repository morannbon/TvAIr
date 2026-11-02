namespace TvAIr.Core;

/// <summary>
/// TVTest.ini の RecordFileName を、RecordingWorker 側の録画ファイル名生成に使うため起動時基準で読む。
///
/// - TVTest.ini を書き換えない。
/// - RecordFolder と同じく、TvAIr 側で勝手な保存先・命名規則を既定化しない。
/// - 空欄時だけ録画直前に同じキーを直読みにする保険を持つ。
/// </summary>
public static class TvTestRecordFileNameTemplateResolver
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static bool _success;
    private static string _template = string.Empty;
    private static string _evidence = string.Empty;
    private static string _initializedTvTestExe = string.Empty;

    public static void Initialize(string tvTestExecutablePath, LogRepository? log = null)
    {
        lock (Gate)
        {
            if (_initialized && string.Equals(_initializedTvTestExe, tvTestExecutablePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                log?.Add("APP_RECORD_FILENAME_TEMPLATE_INIT", "SKIP",
                    $"already_initialized result={(_success ? "OK" : "FAIL")} template={Safe(_template)} evidence={Safe(_evidence)}");
                return;
            }

            _initializedTvTestExe = tvTestExecutablePath ?? string.Empty;
            _success = TryReadRecordFileNameCore(_initializedTvTestExe, out _template, out _evidence);
            _initialized = true;

            if (_success)
            {
                log?.Add("APP_RECORD_FILENAME_TEMPLATE_INIT", "OK", $"result=OK template={Safe(_template)} evidence={_evidence}");
            }
            else
            {
                log?.Add("APP_RECORD_FILENAME_TEMPLATE_INIT", "FAIL", $"result=FAIL evidence={_evidence}");
            }
        }
    }

    public static bool TryResolve(string tvTestExecutablePath, out string template, out string evidence)
    {
        lock (Gate)
        {
            if (!_initialized || !string.Equals(_initializedTvTestExe, tvTestExecutablePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                _initializedTvTestExe = tvTestExecutablePath ?? string.Empty;
                _success = TryReadRecordFileNameCore(_initializedTvTestExe, out _template, out _evidence);
                _initialized = true;
                _evidence += " fallback=lazy_initialize";
            }

            template = _template;
            evidence = _evidence;
            return _success;
        }
    }

    private static bool TryReadRecordFileNameCore(string tvTestExecutablePath, out string template, out string evidence)
    {
        template = string.Empty;
        evidence = string.Empty;

        if (!TvTestIniExactKeyReader.TryResolveLocation(
            tvTestExecutablePath, "RecordFileName", out _, out var iniPath, out evidence))
            return false;

        string? section = null;
        string? rawValue = null;
        var lineNumber = 0;

        try
        {
            TvTestIniExactKeyReader.TryReadFirst(iniPath, "RecordFileName",
                out section, out rawValue, out lineNumber);
        }
        catch (Exception ex)
        {
            evidence = $"tvtest_ini_read_error ini={iniPath} type={ex.GetType().Name} message={TvTestIniExactKeyReader.TrimErrorMessage(ex.Message, 160)} key=RecordFileName";
            return false;
        }

        if (rawValue is null)
        {
            evidence = $"record_filename_key_not_found ini={iniPath} key=RecordFileName rule=startup_exact_key_only";
            return false;
        }

        var value = NormalizeTemplateValue(rawValue);
        if (string.IsNullOrWhiteSpace(value))
        {
            evidence = $"record_filename_empty ini={iniPath} section={Safe(section)} key=RecordFileName line={lineNumber} value={Safe(rawValue)}";
            return false;
        }

        template = value;
        evidence = $"ini={iniPath} section={Safe(section)} key=RecordFileName line={lineNumber} value={Safe(rawValue)} rule=startup_exact_key_only";
        return true;
    }

    private static string NormalizeTemplateValue(string value)
    {
        var v = (value ?? string.Empty).Trim().Trim('"');
        if (v.Length == 0) return string.Empty;

        // TVTestのRecordFileNameは単一テンプレートとして扱う。
        // コメント/複数候補を推測しない。明示的な区切りがあれば先頭だけを採用する。
        var pipe = v.IndexOf('|');
        if (pipe > 0) v = v[..pipe].Trim().Trim('"');
        return v;
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace("\r", " ").Replace("\n", " ");

}
