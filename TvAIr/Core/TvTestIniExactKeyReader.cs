namespace TvAIr.Core;

/// <summary>
/// Read exactly the first occurrence of a named TVTest.ini key. Both recording
/// folder and file name use the same section/comment/line-number semantics.
/// No fallback, deduced section, or mutation of TVTest.ini is allowed here.
/// </summary>
internal static class TvTestIniExactKeyReader
{
    internal static bool TryResolveLocation(string? tvTestExecutablePath, string key,
        out string tvTestDirectory, out string iniPath, out string error)
    {
        tvTestDirectory = string.Empty;
        iniPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(tvTestExecutablePath))
        {
            error = $"tvtest_executable_empty key={key}";
            return false;
        }

        try
        {
            tvTestDirectory = Path.GetDirectoryName(Path.GetFullPath(tvTestExecutablePath)) ?? string.Empty;
        }
        catch (Exception ex)
        {
            var message = TrimErrorMessage(ex.Message, 160);
            error = $"tvtest_executable_invalid type={ex.GetType().Name} message={message} key={key}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(tvTestDirectory))
        {
            error = $"tvtest_directory_empty key={key}";
            return false;
        }

        iniPath = Path.Combine(tvTestDirectory, "TVTest.ini");
        if (!File.Exists(iniPath))
        {
            error = $"tvtest_ini_not_found ini={iniPath} key={key}";
            return false;
        }
        return true;
    }

    // Preserve the exact exception-message length policy for both recording settings.
    internal static string TrimErrorMessage(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    internal static bool TryReadFirst(string iniPath, string expectedKey,
        out string? section, out string? rawValue, out int lineNumber)
    {
        section = null;
        rawValue = null;
        lineNumber = 0;
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
            if (!string.Equals(key, expectedKey, StringComparison.OrdinalIgnoreCase)) continue;

            rawValue = raw[(eq + 1)..].Trim();
            lineNumber = i + 1;
            return true;
        }
        return false;
    }
}
