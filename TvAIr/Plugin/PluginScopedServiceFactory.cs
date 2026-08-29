using System.Collections.Concurrent;
using TvAIr.Core;

namespace TvAIr.Plugin;

/// <summary>
/// Creates plugin-scoped owners from one identity and data-directory contract.
/// Runtime capability contexts must not rebuild plugin identity, log ownership,
/// or file ownership independently.
/// </summary>
internal sealed class PluginScopedServiceFactory
{
    private readonly LogRepository _log;
    private readonly string _dataDirectory;
    private readonly ConcurrentDictionary<string, TvAIr.Plugin.RuntimeHost.PersistentPluginStorage> _runtimeStorageByPlugin =
        new(StringComparer.OrdinalIgnoreCase);

    public PluginScopedServiceFactory(LogRepository log, Database database)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _dataDirectory = database?.DataDirectory ?? throw new ArgumentNullException(nameof(database));
    }

    public string DataDirectory => _dataDirectory;

    public string NormalizePluginId(string? pluginId, string fallback = "UnknownPlugin")
        => PluginIdentity.Normalize(pluginId, fallback);

    public PluginLogService CreateLog(string? pluginId, string? pluginDisplayName = null)
    {
        var normalizedId = NormalizePluginId(pluginId);
        return new PluginLogService(_log, normalizedId, pluginDisplayName);
    }

    public PluginOwnedFileStore CreateFiles(string? pluginId)
        => new(_dataDirectory, NormalizePluginId(pluginId));

    public TvAIr.Plugin.RuntimeHost.PersistentPluginStorage CreateRuntimeStorage(string? pluginId)
    {
        var normalizedId = NormalizePluginId(pluginId);
        return _runtimeStorageByPlugin.GetOrAdd(
            normalizedId,
            id => new TvAIr.Plugin.RuntimeHost.PersistentPluginStorage(id, _dataDirectory, _log));
    }
}
