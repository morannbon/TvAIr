using System.Text.RegularExpressions;

namespace TvAIr.Plugin;

internal static partial class PluginHtmlSanitizer
{
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var sanitized = ScriptBlockRegex().Replace(html, string.Empty);
        sanitized = EventHandlerRegex().Replace(sanitized, string.Empty);
        sanitized = JavaScriptUrlRegex().Replace(sanitized, "$1#");
        sanitized = ExternalResourceRegex().Replace(sanitized, "$1#");
        sanitized = IframeRegex().Replace(sanitized, string.Empty);
        sanitized = ObjectEmbedRegex().Replace(sanitized, string.Empty);
        return sanitized;
    }

    [GeneratedRegex("<\\s*script\\b[^>]*>.*?<\\s*/\\s*script\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptBlockRegex();

    [GeneratedRegex("\\s+on[a-zA-Z]+\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();

    [GeneratedRegex("(href|src)\\s*=\\s*(\"|')\\s*javascript:[^\"']*(\"|')", RegexOptions.IgnoreCase)]
    private static partial Regex JavaScriptUrlRegex();

    [GeneratedRegex("(href|src)\\s*=\\s*(\"|')\\s*https?://[^\"']*(\"|')", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalResourceRegex();

    [GeneratedRegex("<\\s*iframe\\b[^>]*>.*?<\\s*/\\s*iframe\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IframeRegex();

    [GeneratedRegex("<\\s*(object|embed)\\b[^>]*>.*?<\\s*/\\s*\\1\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ObjectEmbedRegex();
}
