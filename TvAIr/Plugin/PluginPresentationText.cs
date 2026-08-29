namespace TvAIr.Plugin;

/// <summary>
/// Generic plain-text normalization for plugin presentation APIs.
/// Security sanitization and display-shape normalization are deliberately separated:
/// preserving or collapsing newlines is a presentation contract, not a security rule.
/// </summary>
internal static class PluginPresentationText
{
    public static string SingleLine(string? value, int maxLength, string fallback)
        => Normalize(value, maxLength, fallback, preserveNewlines: false);

    public static string Multiline(string? value, int maxLength, string fallback)
        => Normalize(value, maxLength, fallback, preserveNewlines: true);

    private static string Normalize(string? value, int maxLength, string fallback, bool preserveNewlines)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var normalized = NormalizeControlCharacters(value, preserveNewlines).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return fallback;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string NormalizeControlCharacters(string value, bool preserveNewlines)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\r')
            {
                if (preserveNewlines)
                {
                    if (i + 1 < value.Length && value[i + 1] == '\n')
                        i++;
                    AppendSingleNewline(sb);
                }
                else
                {
                    AppendSingleSpace(sb);
                }
                continue;
            }
            if (ch == '\n')
            {
                if (preserveNewlines)
                    AppendSingleNewline(sb);
                else
                    AppendSingleSpace(sb);
                continue;
            }
            if (ch == '\t')
            {
                AppendSingleSpace(sb);
                continue;
            }
            if (char.IsControl(ch))
                continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static void AppendSingleSpace(System.Text.StringBuilder sb)
    {
        if (sb.Length == 0 || sb[^1] == ' ' || sb[^1] == '\n') return;
        sb.Append(' ');
    }

    private static void AppendSingleNewline(System.Text.StringBuilder sb)
    {
        if (sb.Length == 0 || sb[^1] == '\n') return;
        sb.Append('\n');
    }
}
