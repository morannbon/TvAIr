namespace TvAIr.Plugin;

/// <summary>
/// Canonical identity normalization for Host-owned tool window placement and session state.
/// Keep storage keys and open-session keys identical.
/// </summary>
internal static class PluginWindowDefinitionIdentity
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray());
    }
}
