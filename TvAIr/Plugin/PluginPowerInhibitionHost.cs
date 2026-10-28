using TvAIr.Core;

namespace TvAIr.Plugin;

/// <summary>Host-owned, per-plugin power leases. Business workflow lifetime remains plugin-owned.</summary>
internal sealed class PluginPowerInhibitionHost
{
    private readonly object _gate = new();
    private readonly SystemSleepInhibitionService _power;
    private readonly Dictionary<string, Dictionary<Guid, IDisposable>> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stopped = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;
    public PluginPowerInhibitionHost(LogRepository log) => _power = new SystemSleepInhibitionService(log);

    public IDisposable Acquire(string pluginId, string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 120 || operation.Any(char.IsControl))
            throw new ArgumentException("A nonempty, printable operation of at most 120 characters is required.", nameof(operation));
        lock (_gate)
        {
            if (_stopped.Contains(pluginId)) throw new ObjectDisposedException(nameof(PluginPowerInhibitionHost));
            if (!_power.TryAcquire($"Plugin:{pluginId}", operation, Interlocked.Increment(ref _generation), out var inner, out var error) || inner is null)
                throw new InvalidOperationException($"System sleep inhibition unavailable: {error}");
            var id = Guid.NewGuid();
            if (!_owners.TryGetValue(pluginId, out var leases)) _owners[pluginId] = leases = new();
            leases.Add(id, inner);
            return new Lease(this, pluginId, id);
        }
    }

    public void Release(string pluginId, Guid id)
    {
        IDisposable? inner = null;
        lock (_gate)
        {
            if (_owners.TryGetValue(pluginId, out var leases) && leases.Remove(id, out inner) && leases.Count == 0)
                _owners.Remove(pluginId);
        }
        inner?.Dispose();
    }

    public void CancelPlugin(string pluginId)
    {
        IDisposable[] leases;
        lock (_gate)
        {
            _stopped.Add(pluginId);
            leases = _owners.TryGetValue(pluginId, out var found) ? found.Values.ToArray() : Array.Empty<IDisposable>();
            _owners.Remove(pluginId);
        }
        foreach (var lease in leases) lease.Dispose();
    }

    private sealed class Lease : IDisposable
    {
        private PluginPowerInhibitionHost? _host;
        private readonly string _pluginId;
        private readonly Guid _id;
        internal Lease(PluginPowerInhibitionHost host, string pluginId, Guid id) { _host = host; _pluginId = pluginId; _id = id; }
        public void Dispose() => Interlocked.Exchange(ref _host, null)?.Release(_pluginId, _id);
    }
}
