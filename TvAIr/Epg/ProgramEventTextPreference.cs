namespace TvAIr.Epg;

/// <summary>
/// Preserves DB-first / overlay-second first nonempty text selection for both
/// ProgramGuide display and EPG-derived metadata. Whitespace is trimmed once.
/// </summary>
internal static class ProgramEventTextPreference
{
    public static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length > 0) return text;
        }
        return string.Empty;
    }
}
