using System.Diagnostics;
using System.Threading;

namespace TvAIr.Tuner;

/// <summary>
/// BonDriverのOpen/Close相当操作を放送波単位で直列化する。
/// GRとBS/CSは独立して同時実行できる。放送波を特定できない既存操作は両方を取得する。
/// 特殊な兼用チューナーは設計対象外。
/// </summary>
internal static class TunerDeviceAccessGate
{
    private const string AllKey = "ALL";
    private const string GrKey = "GR";
    private const string BscsKey = "BSCS";

    private static readonly SemaphoreSlim GrGate = new(1, 1);
    private static readonly SemaphoreSlim BscsGate = new(1, 1);
    private static readonly AsyncLocal<ScopeState?> Current = new();
    private static readonly object DrainGate = new();
    private static readonly Dictionary<string, Task> PendingProcessDrains = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// startup境界でowner identityを安全に確定できず、その場でworkerをretireできなかった場合の
    /// 物理device drain barrier。PIDの存在を後から再検索するのではなく、登録時にProcess handleを
    /// 捕捉してその実processのexitを待つ。次の同波Open/Close境界はdrain完了まで進めない。
    /// </summary>
    public static void RegisterProcessDrain(string gateKey, int processId, Action<string>? log = null)
    {
        if (processId <= 0) return;
        var normalized = NormalizeKey(gateKey);
        Task drainTask;
        try
        {
            var process = Process.GetProcessById(processId);
            drainTask = WaitAndDisposeProcessAsync(process, processId, normalized, log);
        }
        catch (ArgumentException)
        {
            log?.Invoke($"TUNER_DEVICE_DRAIN_REGISTER result=ALREADY_EXITED gateKey={normalized} pid={processId}");
            return;
        }
        catch (Exception ex)
        {
            log?.Invoke($"TUNER_DEVICE_DRAIN_REGISTER result=FAILED gateKey={normalized} pid={processId} error={ex.GetType().Name}:{ex.Message}");
            return;
        }

        lock (DrainGate)
        {
            if (normalized == AllKey)
            {
                PendingProcessDrains[GrKey] = CombineDrainUnsafe(GrKey, drainTask);
                PendingProcessDrains[BscsKey] = CombineDrainUnsafe(BscsKey, drainTask);
            }
            else
            {
                PendingProcessDrains[normalized] = CombineDrainUnsafe(normalized, drainTask);
            }
        }
        log?.Invoke($"TUNER_DEVICE_DRAIN_REGISTER result=BLOCKED gateKey={normalized} pid={processId}");
    }

    private static Task CombineDrainUnsafe(string key, Task next)
        => PendingProcessDrains.TryGetValue(key, out var existing) && !existing.IsCompleted
            ? Task.WhenAll(existing, next)
            : next;

    private static async Task WaitAndDisposeProcessAsync(Process process, int processId, string gateKey, Action<string>? log)
    {
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync().ConfigureAwait(false);
            log?.Invoke($"TUNER_DEVICE_DRAIN_EXIT gateKey={gateKey} pid={processId}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"TUNER_DEVICE_DRAIN_EXIT result=OBSERVE_FAILED gateKey={gateKey} pid={processId} error={ex.GetType().Name}:{ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private static Task[] SnapshotPendingDrainTasks(string normalized)
    {
        lock (DrainGate)
        {
            if (normalized == AllKey)
            {
                return new[] { GrKey, BscsKey }
                    .Select(key => PendingProcessDrains.TryGetValue(key, out var task) ? task : Task.CompletedTask)
                    .Where(task => !task.IsCompleted)
                    .ToArray();
            }
            return PendingProcessDrains.TryGetValue(normalized, out var pending) && !pending.IsCompleted
                ? new[] { pending }
                : Array.Empty<Task>();
        }
    }

    private static async Task WaitForPendingDrainAsync(string owner, string normalized, Action<string>? log, CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = SnapshotPendingDrainTasks(normalized);
            if (pending.Length == 0) return;
            log?.Invoke($"TUNER_DEVICE_DRAIN_WAIT owner={owner} gateKey={normalized} pending={pending.Length}");
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public static Task<IDisposable> EnterAsync(
        string owner,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
        => EnterAsync(owner, AllKey, log, cancellationToken);

    public static async Task<IDisposable> EnterAsync(
        string owner,
        string gateKey,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKey(gateKey);
        var current = Current.Value;
        if (current is not null && (current.Key == AllKey || current.Key == normalized))
        {
            current.Depth++;
            log?.Invoke($"TUNER_DEVICE_LOCK_REENTER owner={owner} gateKey={normalized} depth={current.Depth}");
            return new Releaser(owner, normalized, log, Array.Empty<SemaphoreSlim>(), ownsSemaphore: false);
        }

        var acquired = new List<SemaphoreSlim>(2);
        log?.Invoke($"TUNER_DEVICE_LOCK_WAIT owner={owner} gateKey={normalized}");
        try
        {
            if (normalized == AllKey || normalized == GrKey)
            {
                await GrGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired.Add(GrGate);
            }
            if (normalized == AllKey || normalized == BscsKey)
            {
                await BscsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired.Add(BscsGate);
            }
        }
        catch
        {
            for (var i = acquired.Count - 1; i >= 0; i--) acquired[i].Release();
            throw;
        }

        try
        {
            await WaitForPendingDrainAsync(owner, normalized, log, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            for (var i = acquired.Count - 1; i >= 0; i--) acquired[i].Release();
            throw;
        }

        Current.Value = new ScopeState(normalized);
        log?.Invoke($"TUNER_DEVICE_LOCK_ENTER owner={owner} gateKey={normalized}");
        return new Releaser(owner, normalized, log, acquired.ToArray(), ownsSemaphore: true);
    }

    public static IDisposable Enter(string owner, Action<string>? log = null)
        => Enter(owner, AllKey, log);

    public static IDisposable Enter(string owner, string gateKey, Action<string>? log = null)
    {
        var normalized = NormalizeKey(gateKey);
        var current = Current.Value;
        if (current is not null && (current.Key == AllKey || current.Key == normalized))
        {
            current.Depth++;
            log?.Invoke($"TUNER_DEVICE_LOCK_REENTER owner={owner} gateKey={normalized} depth={current.Depth}");
            return new Releaser(owner, normalized, log, Array.Empty<SemaphoreSlim>(), ownsSemaphore: false);
        }

        var acquired = new List<SemaphoreSlim>(2);
        log?.Invoke($"TUNER_DEVICE_LOCK_WAIT owner={owner} gateKey={normalized}");
        try
        {
            if (normalized == AllKey || normalized == GrKey)
            {
                GrGate.Wait();
                acquired.Add(GrGate);
            }
            if (normalized == AllKey || normalized == BscsKey)
            {
                BscsGate.Wait();
                acquired.Add(BscsGate);
            }
        }
        catch
        {
            for (var i = acquired.Count - 1; i >= 0; i--) acquired[i].Release();
            throw;
        }

        try
        {
            WaitForPendingDrainAsync(owner, normalized, log, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            for (var i = acquired.Count - 1; i >= 0; i--) acquired[i].Release();
            throw;
        }

        Current.Value = new ScopeState(normalized);
        log?.Invoke($"TUNER_DEVICE_LOCK_ENTER owner={owner} gateKey={normalized}");
        return new Releaser(owner, normalized, log, acquired.ToArray(), ownsSemaphore: true);
    }

    private static string NormalizeKey(string? gateKey)
        => string.Equals(gateKey, BscsKey, StringComparison.OrdinalIgnoreCase)
            ? BscsKey
            : string.Equals(gateKey, GrKey, StringComparison.OrdinalIgnoreCase)
                ? GrKey
                : AllKey;

    private sealed class ScopeState
    {
        public ScopeState(string key)
        {
            Key = key;
            Depth = 1;
        }

        public string Key { get; }
        public int Depth { get; set; }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly string _owner;
        private readonly string _key;
        private readonly Action<string>? _log;
        private readonly SemaphoreSlim[] _owned;
        private readonly bool _ownsSemaphore;
        private int _disposed;

        public Releaser(string owner, string key, Action<string>? log, SemaphoreSlim[] owned, bool ownsSemaphore)
        {
            _owner = owner;
            _key = key;
            _log = log;
            _owned = owned;
            _ownsSemaphore = ownsSemaphore;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            var current = Current.Value;
            if (current is not null && current.Depth > 1)
            {
                current.Depth--;
                _log?.Invoke($"TUNER_DEVICE_LOCK_REEXIT owner={_owner} gateKey={_key} depth={current.Depth}");
                return;
            }

            Current.Value = null;
            if (_ownsSemaphore)
            {
                for (var i = _owned.Length - 1; i >= 0; i--) _owned[i].Release();
                _log?.Invoke($"TUNER_DEVICE_LOCK_EXIT owner={_owner} gateKey={_key}");
            }
        }
    }
}
