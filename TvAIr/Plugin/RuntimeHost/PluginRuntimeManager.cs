using TvAIrPlugin.Runtime;

namespace TvAIr.Plugin.RuntimeHost;

internal sealed class PluginRuntimeManager : ITvAirRuntimeApi, IDisposable
{
    private int _disposed;

    public PluginRuntimeManager(string pluginId, string sdkContractVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        PluginId = pluginId;
        SdkContractVersion = string.IsNullOrWhiteSpace(sdkContractVersion) ? "1.1" : sdkContractVersion.Trim();
        RuntimeSessionId = Guid.NewGuid().ToString("N");
    }

    public string PluginId { get; }
    public string RuntimeSessionId { get; }
    public string SdkContractVersion { get; }
    public bool IsConnected => Volatile.Read(ref _disposed) == 0;

    public void ThrowIfDisconnected()
    {
        if (!IsConnected)
            throw new ObjectDisposedException(nameof(PluginRuntimeManager), "Plugin runtime is disconnected.");
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
