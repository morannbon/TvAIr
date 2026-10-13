using System.Threading;

namespace TvAIr.Core;

/// <summary>
/// TvAIr network usage master boundary. OFF means closed-network mode.
///
/// NETWORK_USAGE_MASTER_CLOSED_NETWORK_INVARIANT — 変更禁止:
/// 脅威境界はTvAIr内部ではなく外部（非loopback）である。OFF時はLAN/Internetの内向き・外向き通信を
/// すべて実効停止し、進行中の外部通信をcancelし、非loopback listenerも閉じる。下位LAN/Plugin設定値は
/// 保存したまま非実効化する。一方、localhost/loopbackはTvAIr内部IPCとして境界外に置き、完全閉域でも
/// EPG・録画・予約・ProgramGuide・Plugin Host等のローカル本体機能を継続させる。
/// 「0.0.0.0で待受けたまま403を返す」は外部socket到達面を残すため、この契約を満たさない。
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
        => CreateLinkedCancellation(cancellationToken, CancellationToken.None);

    public CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken, CancellationToken additionalToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled == 0)
                throw new OperationCanceledException("Network usage is disabled.", _networkEpoch.Token);
            return additionalToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, additionalToken, _networkEpoch.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _networkEpoch.Token);
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
