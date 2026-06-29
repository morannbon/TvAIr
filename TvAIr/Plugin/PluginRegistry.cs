using TvAIrPlugin;


namespace TvAIr.Plugin;

/// <summary>
/// ロード済みプラグインの安全な参照口。
/// 本体各機能はここ経由で、必要な種別だけ読み取り利用する。
/// </summary>
public sealed class PluginRegistry
{
    private readonly object _sync = new();
    private readonly List<ITvAIrPlugin> _plugins = new();
    private readonly Dictionary<ITvAIrPlugin, PluginExternalManifestContract> _externalManifestContracts = new();

    internal void Register(ITvAIrPlugin plugin, PluginExternalManifestContract? externalManifestContract = null)
    {
        lock (_sync)
        {
            _plugins.Add(plugin);
            if (externalManifestContract is not null)
            {
                _externalManifestContracts[plugin] = externalManifestContract;
            }
        }
    }

    public PluginExternalManifestContract? GetExternalManifestContract(ITvAIrPlugin plugin)
    {
        lock (_sync)
        {
            return _externalManifestContracts.TryGetValue(plugin, out var contract) ? contract : null;
        }
    }

    public IReadOnlyList<ITvAIrPlugin> GetAll()
    {
        lock (_sync)
        {
            return _plugins.ToList();
        }
    }

    public IReadOnlyList<IAnalysisPlugin> GetAnalysisPlugins()
    {
        lock (_sync)
        {
            return _plugins.OfType<IAnalysisPlugin>().ToList();
        }
    }

    public IReadOnlyList<IViewerPlugin> GetViewerPlugins()
    {
        lock (_sync)
        {
            return _plugins.OfType<IViewerPlugin>().ToList();
        }
    }

    public IReadOnlyList<IManifestPlugin> GetManifestPlugins()
    {
        lock (_sync)
        {
            return _plugins.OfType<IManifestPlugin>().ToList();
        }
    }

    public IReadOnlyList<IUiPlugin> GetUiPlugins()
    {
        lock (_sync)
        {
            return _plugins.OfType<IUiPlugin>().ToList();
        }
    }

    private static bool IsAirhythmRouteCandidate(string? value)
    {
        var v = (value ?? string.Empty).Trim().Trim('/').ToLowerInvariant();
        return v.Equals("airhythm", StringComparison.OrdinalIgnoreCase)
            || v.Equals("airithm", StringComparison.OrdinalIgnoreCase) // legacy alias
            || v.Equals("ai-rhythm", StringComparison.OrdinalIgnoreCase)
            || v.Equals("ai-rithm", StringComparison.OrdinalIgnoreCase) // legacy alias
            || v.Contains("airhythm", StringComparison.OrdinalIgnoreCase)
            || v.Contains("airithm", StringComparison.OrdinalIgnoreCase); // legacy alias
    }

    private static string NormalizeRouteAlias(string? value)
        => IsAirhythmRouteCandidate(value) ? "airhythm" : (value ?? string.Empty).Trim().Trim('/');

    public IUiPlugin? FindUiPlugin(string routeSegment)
    {
        var normalized = NormalizeRouteAlias(routeSegment);
        lock (_sync)
        {
            return _plugins.OfType<IUiPlugin>().FirstOrDefault(p =>
                string.Equals(NormalizeRouteAlias(p.Ui.RouteSegment), normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void NotifyRecordingStarted(PluginRecordingInfo info)
    {
        foreach (var plugin in GetAll())
        {
            try { plugin.OnRecordingStarted(info); } catch { }
        }
    }

    public void NotifyRecordingStopped(PluginRecordingInfo info)
    {
        foreach (var plugin in GetAll())
        {
            try { plugin.OnRecordingStopped(info); } catch { }
        }
    }

    public void NotifyPlaybackStarted(PluginPlaybackInfo info)
    {
        foreach (var plugin in GetAll())
        {
            try { plugin.OnPlaybackStarted(info); } catch { }
        }
    }

    public void NotifyPlaybackStopped(PluginPlaybackInfo info)
    {
        foreach (var plugin in GetAll())
        {
            try { plugin.OnPlaybackStopped(info); } catch { }
        }
    }

    public void NotifyPlaybackPositionChanged(PluginPlaybackPosition position)
    {
        foreach (var plugin in GetAll())
        {
            try { plugin.OnPlaybackPositionChanged(position); } catch { }
        }
    }
}


public sealed class PluginExternalManifestContract
{
    public string SourcePath { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string Entry { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string HostContractVersion { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public IReadOnlyList<string> Kind { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Capabilities { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Permissions { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public int ToolWindowWidth { get; set; }
    public int ToolWindowHeight { get; set; }
    public int ToolWindowMinWidth { get; set; }
    public int ToolWindowMinHeight { get; set; }
    public string ToolWindowTitle { get; set; } = string.Empty;
    public string DefaultMenuActionKind { get; set; } = string.Empty;
    public string DefaultMenuActionLabel { get; set; } = string.Empty;
    public int DefaultMenuActionPriority { get; set; }
    public bool? ToolWindowShowInTaskbar { get; set; }
}
