using TvAIrPlugin;

namespace TvAIr.Plugin;

internal static class PluginPermissionResolver
{
    public static IReadOnlyCollection<PluginPermission> Resolve(
        IReadOnlyCollection<PluginPermission>? descriptorPermissions)
        => new HashSet<PluginPermission>(descriptorPermissions ?? Array.Empty<PluginPermission>());

    public static bool DeclaresDirectInternet(IReadOnlyCollection<PluginPermission> permissions)
        => permissions.Contains(PluginPermission.UseInternetAccess);

    public static bool DeclaresExternalLookup(IReadOnlyCollection<PluginPermission> permissions)
        => permissions.Contains(PluginPermission.UseExternalLookup);

    public static bool DeclaresAnyInternet(IReadOnlyCollection<PluginPermission> permissions)
        => DeclaresDirectInternet(permissions) || DeclaresExternalLookup(permissions);
}
