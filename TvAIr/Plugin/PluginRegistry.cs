using TvAIrPlugin;
using TvAIrPlugin.Runtime;


namespace TvAIr.Plugin;

/// <summary>
/// ロード済みプラグインの安全な参照口。
/// 本体各機能はここ経由で、必要な種別だけ読み取り利用する。
/// </summary>
public sealed class PluginRegistry
{
    private readonly object _sync = new();
    private readonly List<ITvAirRuntimeCapabilityPlugin> _runtimePlugins = new();
    internal void RegisterRuntime(ITvAirRuntimeCapabilityPlugin plugin)
    {
        lock (_sync)
        {
            _runtimePlugins.Add(plugin);
        }
    }

    public IReadOnlyList<ITvAirRuntimeCapabilityPlugin> GetRuntimePlugins()
    {
        lock (_sync)
        {
            return _runtimePlugins.ToList();
        }
    }

    public ITvAirRuntimeUiPlugin? FindRuntimeUiPluginNative(string pluginIdOrRoute, out RuntimeUiDefinition? uiDefinition)
    {
        var normalizedPluginId = PluginIdentity.Normalize(pluginIdOrRoute);
        var normalizedRoute = NormalizeRouteAlias(pluginIdOrRoute);
        lock (_sync)
        {
            foreach (var runtime in _runtimePlugins)
            {
                if (runtime is not ITvAirRuntimeUiPlugin uiPlugin)
                    continue;

                var descriptor = runtime.Descriptor;
                var definition = descriptor.UiDefinitions.FirstOrDefault(ui =>
                    string.Equals(PluginIdentity.Normalize(descriptor.PluginId), normalizedPluginId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(NormalizeRouteAlias(ui.Route), normalizedRoute, StringComparison.OrdinalIgnoreCase));
                if (definition is null)
                    continue;

                uiDefinition = definition;
                return uiPlugin;
            }
        }

        uiDefinition = null;
        return null;
    }

    public ITvAirRuntimeCapabilityPlugin? FindRuntimePlugin(string pluginIdOrRoute)
    {
        var normalized = PluginIdentity.Normalize(pluginIdOrRoute);
        var route = NormalizeRouteAlias(pluginIdOrRoute);
        lock (_sync)
        {
            return _runtimePlugins.FirstOrDefault(plugin =>
                string.Equals(PluginIdentity.Normalize(plugin.Descriptor.PluginId), normalized, StringComparison.OrdinalIgnoreCase)
                || plugin.Descriptor.MenuActions.Any(action =>
                    string.Equals(NormalizeRouteAlias(action.Route), route, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public IReadOnlyList<ITvAirRuntimeAnalysisPlugin> GetRuntimeAnalysisPlugins()
    {
        lock (_sync)
        {
            return _runtimePlugins.OfType<ITvAirRuntimeAnalysisPlugin>().ToList();
        }
    }

    private static string NormalizeRouteAlias(string? value)
    {
        var route = (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (route.StartsWith("plugin/", StringComparison.OrdinalIgnoreCase))
            route = route["plugin/".Length..].Trim('/');
        return route;
    }

}


