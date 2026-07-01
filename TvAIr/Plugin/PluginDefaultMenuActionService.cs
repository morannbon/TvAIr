namespace TvAIr.Plugin;

using TvAIr.Core;
using TvAIrPlugin;

/// <summary>
/// release_contract: Plugin Menu Action Contract spine.
/// TvAIr本体はプラグイン名・プラグインkind・現在存在する3プラグインから挙動を推測しない。
/// manifest / descriptor の宣言を正規化し、未宣言互換だけを compat alias source として隔離する。
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
        foreach (var plugin in _registry.GetAll())
        {
            var action = ResolveAction(plugin);
            if (action.Kind.Equals(PluginMenuActionKinds.None, StringComparison.OrdinalIgnoreCase))
            {
                _log.Add("PLUGIN_MENU_ACTION_RESOLVE", action.Name, $"result=SKIPPED kind=none source={Safe(source)} declaredSource={Safe(action.Source)} route={Safe(action.RouteSegment)} reason={Safe(action.Reason)} contract={ContractVersion} rule={TvAIrVersionContract.PublicContractName}");
                continue;
            }

            actions.Add(action);
            _log.Add("PLUGIN_MENU_ACTION_RESOLVE", action.Name, $"result=OK kind={Safe(action.Kind)} source={Safe(action.Source)} caller={Safe(source)} route={Safe(action.RouteSegment)} label={Safe(action.Label)} showInTaskbar={action.ShowInTaskbar} compatAlias={action.CompatibilityAlias} declared={action.Declared} contract={ContractVersion} rule={TvAIrVersionContract.PublicContractName}");
        }

        return actions
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

    private PluginDefaultMenuActionInfo ResolveAction(ITvAIrPlugin plugin)
    {
        var manifest = (plugin as IManifestPlugin)?.Manifest;
        var ui = (plugin as IUiPlugin)?.Ui;

        var route = NormalizeRoute(ui?.RouteSegment ?? manifest?.Route ?? string.Empty);
        if (route.StartsWith("plugin/", StringComparison.OrdinalIgnoreCase)) route = route[7..];
        if (route.StartsWith("plugin-ui/", StringComparison.OrdinalIgnoreCase)) route = route[10..];
        if (string.IsNullOrWhiteSpace(route)) route = NormalizeRoute(manifest?.Id ?? plugin.Name);

        var displayName = string.IsNullOrWhiteSpace(manifest?.Name) ? plugin.Name : manifest!.Name;
        var label = manifest?.DefaultMenuActionLabel ?? ui?.DefaultMenuActionLabel ?? string.Empty;
        var declaredKindRaw = manifest?.DefaultMenuActionKind ?? ui?.DefaultMenuActionKind ?? string.Empty;
        var preferredRaw = manifest?.PreferredOpenMode ?? ui?.PreferredOpenMode ?? string.Empty;
        var kind = NormalizeKind(declaredKindRaw);
        var source = "manifest.defaultMenuAction";
        var declared = !string.IsNullOrWhiteSpace(kind);
        var compatibilityAlias = false;
        var priority = manifest?.DefaultMenuActionPriority ?? ui?.DefaultMenuActionPriority ?? 1000;
        var showInTaskbar = manifest?.ToolWindowShowInTaskbar ?? ui?.ToolWindowShowInTaskbar ?? false;

        if (string.IsNullOrWhiteSpace(kind))
        {
            kind = NormalizeKind(preferredRaw);
            if (!string.IsNullOrWhiteSpace(kind))
            {
                source = "compat_alias_preferredOpenMode";
                compatibilityAlias = true;
            }
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            // 未宣言互換の補助だけをここに閉じ込める。
            // これは正式なプラグイン意思決定ではないため、ログ/APIに互換aliasとして出す。
            kind = plugin is IUiPlugin ? PluginMenuActionKinds.Page : PluginMenuActionKinds.VersionDialog;
            source = plugin is IUiPlugin ? "compat_alias_ui_page" : "compat_alias_non_ui_versionDialog";
            compatibilityAlias = true;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            label = displayName;
        }

        return new PluginDefaultMenuActionInfo
        {
            PluginId = string.IsNullOrWhiteSpace(manifest?.Id) ? route : manifest!.Id,
            Name = displayName,
            Version = string.IsNullOrWhiteSpace(manifest?.Version) ? plugin.Version : manifest!.Version,
            RouteSegment = route,
            Kind = kind,
            Label = label,
            Description = manifest?.Description ?? ui?.Description ?? string.Empty,
            Priority = priority,
            ShowInTaskbar = showInTaskbar,
            Source = source,
            Reason = kind.Equals(PluginMenuActionKinds.None, StringComparison.OrdinalIgnoreCase) ? source : string.Empty,
            Declared = declared,
            CompatibilityAlias = compatibilityAlias,
            ContractVersion = ContractVersion
        };
    }

    private static string NormalizeKind(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(v)) return string.Empty;
        if (v.Equals("tool", StringComparison.OrdinalIgnoreCase) || v.Equals("toolWindow", StringComparison.OrdinalIgnoreCase) || v.Equals("openToolWindow", StringComparison.OrdinalIgnoreCase)) return PluginMenuActionKinds.ToolWindow;
        if (v.Equals("settings", StringComparison.OrdinalIgnoreCase) || v.Equals("openSettings", StringComparison.OrdinalIgnoreCase)) return PluginMenuActionKinds.Settings;
        if (v.Equals("page", StringComparison.OrdinalIgnoreCase) || v.Equals("openPage", StringComparison.OrdinalIgnoreCase) || v.Equals("browser", StringComparison.OrdinalIgnoreCase)) return PluginMenuActionKinds.Page;
        if (v.Equals("info", StringComparison.OrdinalIgnoreCase) || v.Equals("showInfo", StringComparison.OrdinalIgnoreCase) || v.Equals("version", StringComparison.OrdinalIgnoreCase) || v.Equals("versionInfo", StringComparison.OrdinalIgnoreCase) || v.Equals("versionDialog", StringComparison.OrdinalIgnoreCase)) return PluginMenuActionKinds.VersionDialog;
        if (v.Equals("status", StringComparison.OrdinalIgnoreCase) || v.Equals("statusDialog", StringComparison.OrdinalIgnoreCase)) return PluginMenuActionKinds.StatusDialog;
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase) || v.Equals("hidden", StringComparison.OrdinalIgnoreCase)) return PluginMenuActionKinds.None;
        return v;
    }

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
}
