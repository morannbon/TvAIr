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
    private readonly Dictionary<string, TvAirPluginSelectionFeatureDto> _selectionFeatures = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TvAirPluginSelectionFeatureDto> ListSelectionFeatures()
    {
        lock (_sync) return _selectionFeatures.Values.OrderBy(x => x.PluginId).ThenBy(x => x.FeatureId).ToArray();
    }

    internal void PublishSelectionFeature(string ownerId, string featureId, string label, bool enabled,
        IReadOnlyList<TvAirPluginSelectionOptionDto> options)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(featureId) || featureId.Length > 128)
            throw new ArgumentException("Plugin selection owner/feature is invalid.");
        if (options is null || options.Count > 256 || options.Any(o => o is null || string.IsNullOrWhiteSpace(o.SelectionId)
            || o.SelectionId.Length > 256 || string.IsNullOrWhiteSpace(o.DisplayName)))
            throw new ArgumentException("Plugin selection options are invalid.");
        var list = options.Select(o => new TvAirPluginSelectionOptionDto { SelectionId = o.SelectionId, DisplayName = o.DisplayName }).ToArray();
        if (list.Select(o => o.SelectionId).Distinct(StringComparer.Ordinal).Count() != list.Length)
            throw new ArgumentException("Duplicate selection identifiers.");
        lock (_sync) _selectionFeatures[ownerId + ":" + featureId] = new TvAirPluginSelectionFeatureDto
        {
            PluginId = ownerId, FeatureId = featureId, DisplayName = label ?? "", Enabled = enabled, Options = list
        };
    }

    internal void RemoveSelectionFeature(string ownerId, string featureId)
    {
        lock (_sync) _selectionFeatures.Remove(ownerId + ":" + featureId);
    }

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


