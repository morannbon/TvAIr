namespace TvAIr.Plugin;

using TvAIr.Core;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;

/// <summary>
/// release_contract: Plugin Menu Action Contract spine.
/// TvAIr本体はプラグイン名・プラグインkind・現在存在する3プラグインから挙動を推測しない。
/// Runtime descriptor の MenuActions を正本として正規化し、他の情報からメニュー動作を推測しない。
/// </summary>
public sealed class PluginDefaultMenuActionService
{
    public static string ContractVersion => TvAIrVersionContract.PublicContractName;

    private readonly PluginRegistry _registry;
    private readonly LogRepository _log;

    public PluginDefaultMenuActionService(PluginRegistry registry, LogRepository log)
    {
        _registry = registry;
        _log = log;
    }

    public IReadOnlyList<PluginDefaultMenuActionInfo> ResolveActions(string source = "api")
    {
        var actions = new List<PluginDefaultMenuActionInfo>();
        foreach (var plugin in _registry.GetRuntimePlugins())
        {
            foreach (var action in ResolveRuntimeActions(plugin))
            {
                if (action.Kind.Equals(PluginMenuActionKinds.None, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Add("PLUGIN_MENU_ACTION_RESOLVE", action.Name,
                        $"result=SKIPPED kind=none source={Safe(source)} declaredSource={Safe(action.Source)} route={Safe(action.RouteSegment)} reason={Safe(action.Reason)} contract={ContractVersion} rule=runtime_descriptor_menu_contract");
                    continue;
                }
                actions.Add(action);
                _log.Add("PLUGIN_MENU_ACTION_RESOLVE", action.Name,
                    $"result=OK kind={Safe(action.Kind)} source={Safe(action.Source)} caller={Safe(source)} route={Safe(action.RouteSegment)} label={Safe(action.Label)} showInTaskbar={action.ShowInTaskbar} declared=true contract={ContractVersion} rule=runtime_descriptor_menu_contract");
            }
        }

        return actions
            .GroupBy(BuildMenuIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(x => x.Priority).First())
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public PluginDefaultMenuActionInfo? ResolveActionByRoute(string routeSegment)
    {
        var normalized = NormalizeRoute(routeSegment);
        return ResolveActions("dispatch")
            .FirstOrDefault(x => string.Equals(NormalizeRoute(x.RouteSegment), normalized, StringComparison.OrdinalIgnoreCase));
    }


    private static string BuildMenuIdentityKey(PluginDefaultMenuActionInfo action)
        => string.Join("|", NormalizePluginId(action.PluginId), NormalizeRoute(action.RouteSegment), (action.Label ?? string.Empty).Trim().ToLowerInvariant());



    private IEnumerable<PluginDefaultMenuActionInfo> ResolveRuntimeActions(ITvAirRuntimeCapabilityPlugin plugin)
    {
        var descriptor = plugin.Descriptor;
        var pluginId = PluginIdentity.Normalize(descriptor.PluginId, descriptor.DisplayName);
        foreach (var definition in descriptor.MenuActions ?? Array.Empty<PluginMenuActionDefinition>())
        {
            if (!definition.ShowInMenu) continue;
            var route = NormalizeRoute(definition.Route);
            if (string.IsNullOrWhiteSpace(route)) route = NormalizeRoute(pluginId);
            yield return new PluginDefaultMenuActionInfo
            {
                PluginId = pluginId,
                Name = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? pluginId : descriptor.DisplayName,
                Version = descriptor.Version,
                RouteSegment = route,
                Kind = NormalizeRuntimeKind(definition.Kind),
                Label = string.IsNullOrWhiteSpace(definition.Label) ? descriptor.DisplayName : definition.Label,
                Description = string.Empty,
                Priority = definition.Priority,
                ShowInTaskbar = definition.ShowInTaskbar,
                Source = "runtime.descriptor.menuAction",
                Reason = definition.Kind == PluginMenuActionKind.None ? "runtime_descriptor_none" : string.Empty,
                Declared = true,
                CompatibilityAlias = false,
                ContractVersion = ContractVersion,
                ActionId = definition.ActionId,
                WindowDefinitionId = definition.WindowDefinitionId,
                SurfaceDefinitionId = definition.SurfaceDefinitionId
            };
        }
    }

    private static string NormalizeRuntimeKind(PluginMenuActionKind kind)
        => kind switch
        {
            PluginMenuActionKind.ToolWindow => PluginMenuActionKinds.ToolWindow,
            PluginMenuActionKind.Page => PluginMenuActionKinds.Page,
            PluginMenuActionKind.Settings => PluginMenuActionKinds.Settings,
            PluginMenuActionKind.VersionDialog => PluginMenuActionKinds.VersionDialog,
            PluginMenuActionKind.StatusDialog => PluginMenuActionKinds.StatusDialog,
            _ => PluginMenuActionKinds.None
        };

    private static string NormalizePluginId(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : PluginIdentity.Normalize(value).ToLowerInvariant();

    private static string NormalizeRoute(string? value)
        => (value ?? string.Empty).Trim().Trim('/').ToLowerInvariant();

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace('\r', ' ').Replace('\n', ' ');
}

public static class PluginMenuActionKinds
{
    public const string ToolWindow = "toolWindow";
    public const string Page = "page";
    public const string Settings = "settings";
    public const string VersionDialog = "versionDialog";
    public const string StatusDialog = "statusDialog";
    public const string None = "none";
}

public sealed class PluginDefaultMenuActionInfo
{
    public string PluginId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string RouteSegment { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool ShowInTaskbar { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool Declared { get; set; }
    public bool CompatibilityAlias { get; set; }
    public string ContractVersion { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string WindowDefinitionId { get; set; } = string.Empty;
    public string SurfaceDefinitionId { get; set; } = string.Empty;
}
