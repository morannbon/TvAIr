using TvAIrPlugin;

namespace TvAIr.Plugin;

internal static class PluginPermissionResolver
{
    public static IReadOnlyCollection<PluginPermission> Resolve(
        IReadOnlyCollection<PluginPermission>? descriptorPermissions)
        => new HashSet<PluginPermission>(descriptorPermissions ?? Array.Empty<PluginPermission>());
}
