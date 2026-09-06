using System.Threading;

namespace TvAIr.Core;

/// <summary>
/// TvAIr network usage master boundary. OFF means closed-network mode: lower LAN/Plugin settings
/// remain persisted but are not effective, and in-flight Host-managed external traffic is cancelled.
/// Loopback/local IPC is intentionally outside this non-loopback network boundary.
/// </summary>
public sealed class NetworkUsageGate : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource _networkEpoch = new();
    private int _enabled;
    private bool _disposed;

    public NetworkUsageGate(IniSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _enabled = settings.NetworkUsageEnabled ? 1 : 0;
        if (_enabled == 0) _networkEpoch.Cancel();
    }

    public bool Enabled => Volatile.Read(ref _enabled) != 0;

    public void Apply(bool enabled)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (enabled == (_enabled != 0)) return;

            if (!enabled)
            {
                Volatile.Write(ref _enabled, 0);
                _networkEpoch.Cancel();
                return;
            }

            _networkEpoch.Dispose();
            _networkEpoch = new CancellationTokenSource();
            Volatile.Write(ref _enabled, 1);
        }
    }

    public CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled == 0)
                throw new OperationCanceledException("Network usage is disabled.", _networkEpoch.Token);
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _networkEpoch.Token);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _enabled, 0);
            _networkEpoch.Cancel();
            _networkEpoch.Dispose();
        }
    }
}
