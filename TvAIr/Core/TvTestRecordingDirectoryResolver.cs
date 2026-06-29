namespace TvAIr.Core;

/// <summary>
/// 通常録画の保存先を TVTest.ini の RecordFolder から解決する。
///
/// release_contract 方針:
/// - 録画直前に TVTest.ini 全体を毎回推測探索しない。
/// - TvAIr 起動時に TVTest.ini の RecordFolder を1回だけ直読みにし、ランタイム固定する。
/// - RecordFolder が存在したら DriverDirectory / UseDirectWrite / ProgramGuide 等は一切見ない。
/// - フォルダ名だけで用途を推測せず、TVTest.ini の RecordFolder を録画保存先として扱う。
/// </summary>
public static class TvTestRecordingDirectoryResolver
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static bool _success;
    private static string _directory = string.Empty;
    private static string _evidence = string.Empty;
    private static string _initializedTvTestExe = string.Empty;

    /// <summary>
    /// TvAIr起動時に1回だけ呼ぶ。録画時はこの結果だけを使う。
    /// </summary>
    public static void Initialize(string tvTestExecutablePath, LogRepository? log = null)
    {
        lock (Gate)
        {
            if (_initialized && string.Equals(_initializedTvTestExe, tvTestExecutablePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                log?.Add("APP_RECORD_FOLDER_INIT", "SKIP", $"already_initialized result={(_success ? "OK" : "FAIL")} folder={Safe(_directory)} evidence={Safe(_evidence)}");
                return;
            }

            _initializedTvTestExe = tvTestExecutablePath ?? string.Empty;
            _success = TryReadRecordFolderCore(_initializedTvTestExe, out _directory, out _evidence);
            _initialized = true;

            if (_success)
            {
                log?.Add("APP_RECORD_FOLDER_INIT", "OK", $"result=OK folder={_directory} evidence={_evidence}");
            }
            else
            {
                log?.Add("APP_RECORD_FOLDER_INIT", "FAIL", $"result=FAIL evidence={_evidence}");
            }
        }
    }

    public static bool TryResolve(string tvTestExecutablePath, out string directory, out string evidence)
    {
        lock (Gate)
        {
            if (!_initialized || !string.Equals(_initializedTvTestExe, tvTestExecutablePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                // 起動時初期化漏れの保険。ここでも推測探索はせず RecordFolder 直読みに限定する。
                _initializedTvTestExe = tvTestExecutablePath ?? string.Empty;
                _success = TryReadRecordFolderCore(_initializedTvTestExe, out _directory, out _evidence);
                _initialized = true;
                _evidence += " fallback=lazy_initialize";
            }

            directory = _directory;
            evidence = _evidence;
            return _success;
        }
    }

    private static bool TryReadRecordFolderCore(string tvTestExecutablePath, out string directory, out string evidence)
    {
        directory = string.Empty;
        evidence = string.Empty;

        if (string.IsNullOrWhiteSpace(tvTestExecutablePath))
        {
            evidence = "tvtest_executable_empty key=RecordFolder";
            return false;
        }

        string tvTestDir;
        try
        {
            tvTestDir = Path.GetDirectoryName(Path.GetFullPath(tvTestExecutablePath)) ?? string.Empty;
        }
        catch (Exception ex)
        {
            evidence = $"tvtest_executable_invalid type={ex.GetType().Name} message={Trim(ex.Message, 160)} key=RecordFolder";
            return false;
        }

        if (string.IsNullOrWhiteSpace(tvTestDir))
        {
            evidence = "tvtest_directory_empty key=RecordFolder";
            return false;
        }

        var iniPath = Path.Combine(tvTestDir, "TVTest.ini");
        if (!File.Exists(iniPath))
        {
            evidence = $"tvtest_ini_not_found ini={iniPath} key=RecordFolder";
            return false;
        }

        string? section = null;
        string? rawValue = null;
        var lineNumber = 0;

        try
        {
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
                if (!string.Equals(key, "RecordFolder", StringComparison.OrdinalIgnoreCase)) continue;

                rawValue = raw[(eq + 1)..].Trim();
                lineNumber = i + 1;
                break;
            }
        }
        catch (Exception ex)
        {
            evidence = $"tvtest_ini_read_error ini={iniPath} type={ex.GetType().Name} message={Trim(ex.Message, 160)} key=RecordFolder";
            return false;
        }

        if (rawValue is null)
        {
            evidence = $"record_folder_key_not_found ini={iniPath} key=RecordFolder rule=startup_exact_key_only";
            return false;
        }

        var token = NormalizeRecordFolderValue(rawValue);
        if (string.IsNullOrWhiteSpace(token))
        {
            evidence = $"record_folder_empty ini={iniPath} section={Safe(section)} key=RecordFolder line={lineNumber} value={Safe(rawValue)}";
            return false;
        }

        string resolved;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(token);
            resolved = Path.IsPathFullyQualified(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(Path.Combine(tvTestDir, expanded));
        }
        catch (Exception ex)
        {
            evidence = $"record_folder_invalid ini={iniPath} section={Safe(section)} key=RecordFolder line={lineNumber} value={Safe(rawValue)} type={ex.GetType().Name} message={Trim(ex.Message, 160)}";
            return false;
        }

        if (!Directory.Exists(resolved))
        {
            evidence = $"record_folder_not_exists ini={iniPath} section={Safe(section)} key=RecordFolder line={lineNumber} value={Safe(rawValue)} resolved={resolved}";
            return false;
        }

        directory = resolved;
        evidence = $"ini={iniPath} section={Safe(section)} key=RecordFolder line={lineNumber} value={Safe(rawValue)} rule=startup_exact_key_only";
        return true;
    }

    private static string NormalizeRecordFolderValue(string value)
    {
        var v = (value ?? string.Empty).Trim().Trim('"');
        if (v.Length == 0) return string.Empty;

        // TVTestのRecordFolderは単一フォルダ指定として扱う。
        // コメントや複数候補を推測しない。明示的な区切りがあれば先頭のみ。
        var semi = v.IndexOf(';');
        if (semi > 0) v = v[..semi].Trim().Trim('"');
        var pipe = v.IndexOf('|');
        if (pipe > 0) v = v[..pipe].Trim().Trim('"');
        return v;
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string Trim(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
