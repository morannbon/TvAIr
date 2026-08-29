using TvAIr.Core;
using TvAIrPlugin;

namespace TvAIr.Plugin;

/// <summary>
/// Plugin-originated log writes. Runtime capability APIs share this owner so
/// category, level, plugin identity and audit/timeline projection cannot diverge.
/// </summary>
internal sealed class PluginLogService
{
    private readonly LogRepository _log;
    private readonly string _pluginId;
    private readonly string _pluginDisplayName;

    public PluginLogService(LogRepository log, string pluginId, string? pluginDisplayName = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _pluginId = PluginIdentity.Normalize(pluginId, "plugin");
        _pluginDisplayName = string.IsNullOrWhiteSpace(pluginDisplayName)
            ? _pluginId
            : pluginDisplayName.Trim();
    }

    public void Write(PluginLogLevel level, string message)
        => _log.Add("Plugin", _pluginDisplayName, $"[{level}] {message ?? string.Empty}");

    public void Write(string? level, string? category, string? message)
    {
        var normalizedLevel = NormalizeLevel(level);
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "Plugin" : category.Trim();
        _log.Add(normalizedCategory, _pluginDisplayName, $"[{normalizedLevel}] {message ?? string.Empty}");
    }

    public void AddTimeline(string? title, string? message)
        => _log.Add("PLUGIN_TIMELINE", title ?? string.Empty, $"plugin={_pluginId} {message ?? string.Empty}");

    public void AddAudit(string? action, string? message)
        => _log.Add("PLUGIN_AUDIT", action ?? string.Empty, $"plugin={_pluginId} {message ?? string.Empty}");

    private static string NormalizeLevel(string? level)
    {
        if (Enum.TryParse<PluginLogLevel>(level, ignoreCase: true, out var parsed))
            return parsed.ToString();
        return PluginLogLevel.Info.ToString();
    }
}
