namespace TvAIr.Plugin;

/// <summary>
/// Canonical plugin identity used for ownership and plugin-scoped persistence.
/// Keep this projection stable across legacy and capability entry points.
/// </summary>
internal static class PluginIdentity
{
    public static string Normalize(string? value, string fallback = "Plugin")
    {
        var raw = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var chars = raw.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray();
        return new string(chars);
    }
}
