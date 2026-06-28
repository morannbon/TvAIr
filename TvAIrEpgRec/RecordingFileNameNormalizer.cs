using System.Text;
using System.Text.RegularExpressions;

namespace TvAIrEpgRec;

internal static class RecordingFileNameNormalizer
{
    public const string Rule = "v0.10.44_record_filename_event_name_normalization_unified";

    public static string NormalizeOutputPathFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return path.Trim(); }

        var dir = Path.GetDirectoryName(full) ?? string.Empty;
        var name = Path.GetFileName(full);
        var normalizedName = SanitizeRenderedFileName(NormalizeEventNameForFileName(name));
        if (string.IsNullOrWhiteSpace(Path.GetExtension(normalizedName)) && !string.IsNullOrWhiteSpace(Path.GetExtension(name)))
            normalizedName += Path.GetExtension(name);

        var candidate = string.IsNullOrWhiteSpace(dir) ? normalizedName : Path.Combine(dir, normalizedName);
        if (string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase)) return full;
        return MakeUniquePath(candidate);
    }

    private static string NormalizeEventNameForFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var source = value.Normalize(NormalizationForm.FormKC);
        var buffer = new StringBuilder(source.Length);
        var prevWasSpace = false;
        foreach (var original in source)
        {
            var c = original switch
            {
                '〜' => '~',
                '―' => '-',
                '‐' => '-',
                '‑' => '-',
                '–' => '-',
                '—' => '-',
                '−' => '-',
                '’' => '\'',
                '‘' => '\'',
                '“' => '"',
                '”' => '"',
                _ => original
            };

            if (char.IsWhiteSpace(c))
            {
                if (prevWasSpace) continue;
                buffer.Append(' ');
                prevWasSpace = true;
                continue;
            }

            buffer.Append(c);
            prevWasSpace = false;
        }
        return buffer.ToString().Trim();
    }

    private static string SanitizeRenderedFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "TvAIr.ts";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        sanitized = Regex.Replace(sanitized, @"_+", "_").Trim(' ', '_', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "TvAIr.ts" : sanitized;
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{baseName}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{baseName}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}");
    }
}
