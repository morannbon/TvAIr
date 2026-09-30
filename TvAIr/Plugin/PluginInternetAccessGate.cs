using System.Collections.Concurrent;
using TvAIr.Core;

namespace TvAIr.Plugin;

/// <summary>
/// Single Host-owned permission/cancellation boundary for all Plugin Internet traffic.
/// Effective access requires: descriptor declaration + per-plugin user grant + master NetworkUsage ON.
/// </summary>
public sealed class PluginInternetAccessGate : IDisposable
{
    private readonly PluginInternetAccessPermissionStore _permissions;
    private readonly NetworkUsageGate _networkUsage;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pluginEpochs = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public PluginInternetAccessGate(PluginInternetAccessPermissionStore permissions, NetworkUsageGate networkUsage)
    {
        _permissions = permissions;
        _networkUsage = networkUsage;
    }

    public bool NetworkUsageEnabled => _networkUsage.Enabled;
    public bool IsUserAllowed(string pluginId) => _permissions.IsAllowed(pluginId);

    public bool IsEffective(string pluginId, bool descriptorPermission)
        => descriptorPermission && _networkUsage.Enabled && _permissions.IsAllowed(pluginId);

    public CancellationTokenSource CreateLinkedCancellation(string pluginId, bool descriptorPermission, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = PluginIdentity.Normalize(pluginId);
        if (!descriptorPermission) throw new UnauthorizedAccessException("UseInternetAccess permission is required.");
        if (!_permissions.IsAllowed(id)) throw new UnauthorizedAccessException("Internet access is disabled for this plugin in TvAIr settings.");
        var epoch = _pluginEpochs.GetOrAdd(id, _ => new CancellationTokenSource());
        return _networkUsage.CreateLinkedCancellation(cancellationToken, epoch.Token);
    }

    public void OnPermissionChanged(string pluginId, bool allowed)
    {
        var id = PluginIdentity.Normalize(pluginId);
        if (!allowed)
        {
            if (_pluginEpochs.TryRemove(id, out var old)) { try { old.Cancel(); } finally { old.Dispose(); } }
            return;
        }
        _pluginEpochs.TryAdd(id, new CancellationTokenSource());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var pair in _pluginEpochs) { try { pair.Value.Cancel(); } catch { } pair.Value.Dispose(); }
        _pluginEpochs.Clear();
    }
}
