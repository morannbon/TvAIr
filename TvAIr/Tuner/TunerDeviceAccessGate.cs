using System.Threading;
using TvAIr.Core;

namespace TvAIr.Tuner;

/// <summary>
/// BonDriver / TVTest の Open/Close 相当操作をプロセス内で完全直列化するゲート。
///
/// 目的:
/// - 録画停止(taskkill/Close)中に別録画/EPGのOpenが重なることを防ぐ
/// - BonDriver内部ロック競合による視聴プロセス巻き込みを避ける
/// - STOPフェーズの解放待機・クールダウン完了まで同一デバイスを占有扱いにする
///
/// AsyncLocal により同一非同期フロー内の再入は許可する。
/// これにより、上位でTUNER_DEVICE_LOCKを保持したまま下位の起動/停止補助処理が同じゲートを通っても
/// 自己デッドロックしない。
/// </summary>
internal static class TunerDeviceAccessGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly AsyncLocal<int> Depth = new();

    public static async Task<IDisposable> EnterAsync(string owner, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (Depth.Value > 0)
        {
            Depth.Value++;
            log?.Invoke($"TUNER_DEVICE_LOCK_REENTER owner={owner} depth={Depth.Value}");
            return new Releaser(owner, log, ownsSemaphore: false);
        }

        log?.Invoke($"TUNER_DEVICE_LOCK_WAIT owner={owner}");
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Depth.Value = 1;
        log?.Invoke($"TUNER_DEVICE_LOCK_ENTER owner={owner}");
        return new Releaser(owner, log, ownsSemaphore: true);
    }

    public static IDisposable Enter(string owner, Action<string>? log = null)
    {
        if (Depth.Value > 0)
        {
            Depth.Value++;
            log?.Invoke($"TUNER_DEVICE_LOCK_REENTER owner={owner} depth={Depth.Value}");
            return new Releaser(owner, log, ownsSemaphore: false);
        }

        log?.Invoke($"TUNER_DEVICE_LOCK_WAIT owner={owner}");
        Gate.Wait();
        Depth.Value = 1;
        log?.Invoke($"TUNER_DEVICE_LOCK_ENTER owner={owner}");
        return new Releaser(owner, log, ownsSemaphore: true);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly string _owner;
        private readonly Action<string>? _log;
        private readonly bool _ownsSemaphore;
        private int _disposed;

        public Releaser(string owner, Action<string>? log, bool ownsSemaphore)
        {
            _owner = owner;
            _log = log;
            _ownsSemaphore = ownsSemaphore;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            var depth = Depth.Value;
            if (depth > 1)
            {
                Depth.Value = depth - 1;
                _log?.Invoke($"TUNER_DEVICE_LOCK_REEXIT owner={_owner} depth={Depth.Value}");
                return;
            }

            Depth.Value = 0;
            if (_ownsSemaphore)
            {
                Gate.Release();
                _log?.Invoke($"TUNER_DEVICE_LOCK_EXIT owner={_owner}");
            }
        }
    }
}
