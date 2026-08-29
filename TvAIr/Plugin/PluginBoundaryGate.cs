namespace TvAIr.Plugin;

using TvAIr.Core;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;

/// <summary>
/// TvAIr Plugin API boundary.
/// The host narrows plugin behavior only at host-entry boundaries and only for:
/// security violations, missing permissions, and contract-breaking writes to host-owned surfaces.
/// UI shape, screen purpose, action names, storage format, and capability combinations are not policy inputs.
/// </summary>
public sealed class PluginBoundaryGate
{
    private readonly LogRepository _log;

    public PluginBoundaryGate(LogRepository log)
    {
        _log = log;
    }

    public PluginBoundaryDecision CheckAction(
        ITvAirRuntimeCapabilityPlugin plugin,
        string pluginId,
        string actionName,
        string endpoint)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            return Deny(plugin.Descriptor.DisplayName, "action", actionName, "missing_action", endpoint, PluginBoundaryFailureKind.InvalidRequest);
        }

        if (IsUnsafeActionName(actionName))
        {
            return Deny(plugin.Descriptor.DisplayName, "action", actionName, "unsafe_action_name", endpoint, PluginBoundaryFailureKind.InvalidRequest);
        }

        if (!HasPermission(plugin, PluginPermission.UseActionApi))
        {
            return Deny(plugin.Descriptor.DisplayName, "action", actionName, "missing_UseActionApi_permission", endpoint, PluginBoundaryFailureKind.PermissionDenied);
        }


        return PluginBoundaryDecision.Allow();
    }

    public PluginBoundaryDecision CheckWindow(
        ITvAirRuntimeCapabilityPlugin plugin,
        string actionName,
        string endpoint)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            return Deny(plugin.Descriptor.DisplayName, "window", actionName, "missing_action", endpoint, PluginBoundaryFailureKind.InvalidRequest);
        }

        if (IsUnsafeActionName(actionName))
        {
            return Deny(plugin.Descriptor.DisplayName, "window", actionName, "unsafe_action_name", endpoint, PluginBoundaryFailureKind.InvalidRequest);
        }

        if (!HasPermission(plugin, PluginPermission.ShowUi))
        {
            return Deny(plugin.Descriptor.DisplayName, "window", actionName, "missing_ShowUi_permission", endpoint, PluginBoundaryFailureKind.PermissionDenied);
        }

        if (!HasPermission(plugin, PluginPermission.UseWindowApi))
        {
            return Deny(plugin.Descriptor.DisplayName, "window", actionName, "missing_UseWindowApi_permission", endpoint, PluginBoundaryFailureKind.PermissionDenied);
        }

        if ((actionName.Equals("openWindow", StringComparison.OrdinalIgnoreCase) || actionName.Equals("open", StringComparison.OrdinalIgnoreCase))
            && !HasPermission(plugin, PluginPermission.OpenToolWindow))
        {
            return Deny(plugin.Descriptor.DisplayName, "window", actionName, "missing_OpenToolWindow_permission", endpoint, PluginBoundaryFailureKind.PermissionDenied);
        }

        return PluginBoundaryDecision.Allow();
    }

    public PluginBoundaryDecision CheckAsset(ITvAirRuntimeCapabilityPlugin plugin, string assetName, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return Deny(plugin.Descriptor.DisplayName, "asset", assetName, "missing_asset", endpoint, PluginBoundaryFailureKind.InvalidRequest);
        }

        if (IsUnsafeActionName(assetName))
        {
            return Deny(plugin.Descriptor.DisplayName, "asset", assetName, "unsafe_asset_name", endpoint, PluginBoundaryFailureKind.InvalidRequest);
        }

        if (!HasPermission(plugin, PluginPermission.UseAssetApi))
        {
            return Deny(plugin.Descriptor.DisplayName, "asset", assetName, "missing_UseAssetApi_permission", endpoint, PluginBoundaryFailureKind.PermissionDenied);
        }

        return PluginBoundaryDecision.Allow();
    }

    public PluginBoundaryDecision CheckRender(ITvAirRuntimeCapabilityPlugin plugin, bool hostManagedToolWindowContent, string endpoint)
    {
        if (!HasPermission(plugin, PluginPermission.ShowUi))
        {
            return Deny(plugin.Descriptor.DisplayName, "render", hostManagedToolWindowContent ? "toolWindowContent" : "page", "missing_ShowUi_permission", endpoint, PluginBoundaryFailureKind.PermissionDenied);
        }

        if (hostManagedToolWindowContent)
        {
            if (!HasPermission(plugin, PluginPermission.OpenToolWindow))
            {
                return Deny(plugin.Descriptor.DisplayName, "render", "toolWindowContent", "missing_OpenToolWindow_permission", endpoint, PluginBoundaryFailureKind.PermissionDenied);
            }
        }
        else if (!HasPermission(plugin, PluginPermission.OpenPage))
        {
            return Deny(plugin.Descriptor.DisplayName, "render", "page", "missing_OpenPage_permission", endpoint, PluginBoundaryFailureKind.PermissionDenied);
        }

        return PluginBoundaryDecision.Allow();
    }

    public bool HasPermission(ITvAirRuntimeCapabilityPlugin plugin, PluginPermission permission)
        => PluginPermissionResolver.Resolve(plugin.Descriptor.RequiredPermissions).Contains(permission);

    private PluginBoundaryDecision Deny(string pluginName, string boundary, string actionName, string reason, string endpoint, PluginBoundaryFailureKind failureKind)
    {
        _log.Add("PLUGIN_BOUNDARY_GATE", pluginName, $"boundary={Safe(boundary)} action={Safe(actionName)} result=DENIED failureKind={failureKind} reason={Safe(reason)} endpoint={Safe(endpoint)} rule=plugin_boundary_gate");
        return PluginBoundaryDecision.Deny(failureKind, reason);
    }

    private static bool IsUnsafeActionName(string actionName)
    {
        var value = (actionName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Contains('\0')) return true;
        if (value.Contains('/') || value.Contains('\\')) return true;
        if (value.Contains("..", StringComparison.Ordinal)) return true;
        return false;
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().Replace('\r', ' ').Replace('\n', ' ');
}

public enum PluginBoundaryFailureKind
{
    None = 0,
    InvalidRequest = 1,
    PermissionDenied = 2
}

public readonly record struct PluginBoundaryDecision(bool Allowed, PluginBoundaryFailureKind FailureKind, string Reason)
{
    public static PluginBoundaryDecision Allow() => new(true, PluginBoundaryFailureKind.None, string.Empty);
    public static PluginBoundaryDecision Deny(PluginBoundaryFailureKind failureKind, string reason) => new(false, failureKind, reason);
}
