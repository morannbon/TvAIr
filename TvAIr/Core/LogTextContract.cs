namespace TvAIr.Core;

/// <summary>
/// Shared log text projections. Each method preserves its original
/// truncation and line-control policy; a caller must select the applicable
/// format rather than reimplement it locally.
/// </summary>
internal static class LogTextContract
{
    internal static string TrimOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    internal static string TruncateWithDots(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    internal static string SanitizePersistedError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160];
    }
}
