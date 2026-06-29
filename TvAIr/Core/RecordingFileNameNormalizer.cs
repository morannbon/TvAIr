using System.Text;
using System.Text.RegularExpressions;

namespace TvAIr.Core;

/// <summary>
/// 録画ファイル名へ流し込む %event-name% / %service-name% 専用の正規化。
/// EPG本文・番組表表示・自動検索判定・予約タイトル表示には使わない。
/// </summary>
public static class RecordingFileNameNormalizer
{
    public const string Rule = "release_contract";

    public static string NormalizeEventNameForFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        // Compatibility正規化で、全角英数字・全角スペース・全角#等をまず半角相当へ寄せる。
        // 漢字・かな・番組固有の日本語表記は変換対象にしない。
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

    public static string SanitizeFileNamePart(string? value)
    {
        var normalized = NormalizeEventNameForFileName(value);
        if (string.IsNullOrWhiteSpace(normalized)) return "TvAIr";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = normalized.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();

        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        sanitized = Regex.Replace(sanitized, @"_+", "_").Trim(' ', '_', '.');

        return string.IsNullOrWhiteSpace(sanitized) ? "TvAIr" : sanitized;
    }

    public static string SanitizeRenderedFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "TvAIr.ts";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();

        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        sanitized = Regex.Replace(sanitized, @"_+", "_").Trim(' ', '_', '.');

        return string.IsNullOrWhiteSpace(sanitized) ? "TvAIr.ts" : sanitized;
    }

    public static bool ContainsFullWidthAsciiOrSpace(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            if (c == '　') return true;
            if (c >= '！' && c <= '～') return true;
        }
        return false;
    }
}
