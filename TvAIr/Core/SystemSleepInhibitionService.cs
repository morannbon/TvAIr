using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TvAIr.Core;

/// <summary>
/// Windows の Power Request API を使い、TvAIr 自身が長時間処理のライフサイクル全体を
/// システムスリープから保護するための共通サービス。
///
/// 重要:
/// - ワーカー/代表プロセスの生存をスリープ抑止の正本にしない。
/// - Acquire した実処理 owner が Dispose するまで PowerRequestSystemRequired を保持する。
/// - async/await で実行スレッドが変わっても request handle はプロセス内で有効なため、
///   SetThreadExecutionState のような thread affinity に依存しない。
/// </summary>
public sealed class SystemSleepInhibitionService
{
    private readonly LogRepository _log;

    public SystemSleepInhibitionService(LogRepository log)
    {
        _log = log;
    }

    public bool TryAcquire(string owner, string operation, long generation, out IDisposable? lease, out string error)
    {
        lease = null;
        error = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            error = "platform_not_windows";
            _log.Add("SYSTEM_SLEEP_INHIBIT", "FAILED",
                $"result=FAILED owner={Safe(owner)} operation={Safe(operation)} generation={generation} reason={error} api=PowerCreateRequest/PowerSetRequest requestType=SystemRequired rule=operation_lifecycle_power_request_contract");
            return false;
        }

        var reasonText = $"TvAIr {operation} ({owner})";
        IntPtr reasonPtr = IntPtr.Zero;
        SafeFileHandle? requestHandle = null;
        try
        {
            reasonPtr = Marshal.StringToHGlobalUni(reasonText);
            var context = new ReasonContext
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                ReasonString = reasonPtr
            };

            requestHandle = PowerCreateRequest(ref context);
            if (requestHandle is null || requestHandle.IsInvalid)
            {
                var win32 = Marshal.GetLastWin32Error();
                error = $"PowerCreateRequest_failed:{win32}:{new Win32Exception(win32).Message}";
                requestHandle?.Dispose();
                requestHandle = null;
                _log.Add("SYSTEM_SLEEP_INHIBIT", "FAILED",
                    $"result=FAILED owner={Safe(owner)} operation={Safe(operation)} generation={generation} reason={Safe(error)} api=PowerCreateRequest requestType=SystemRequired rule=operation_lifecycle_power_request_contract");
                return false;
            }

            if (!PowerSetRequest(requestHandle, PowerRequestType.SystemRequired))
            {
                var win32 = Marshal.GetLastWin32Error();
                error = $"PowerSetRequest_failed:{win32}:{new Win32Exception(win32).Message}";
                requestHandle.Dispose();
                requestHandle = null;
                _log.Add("SYSTEM_SLEEP_INHIBIT", "FAILED",
                    $"result=FAILED owner={Safe(owner)} operation={Safe(operation)} generation={generation} reason={Safe(error)} api=PowerSetRequest requestType=SystemRequired rule=operation_lifecycle_power_request_contract");
                return false;
            }

            lease = new PowerRequestLease(_log, requestHandle, owner, operation, generation, DateTime.Now);
            requestHandle = null; // lease owns the handle
            _log.Add("SYSTEM_SLEEP_INHIBIT", "ACQUIRED",
                $"result=ACQUIRED owner={Safe(owner)} operation={Safe(operation)} generation={generation} requestType=SystemRequired lifecycle=owner_begin_to_owner_terminal_no_worker_proxy rule=operation_lifecycle_power_request_contract");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}:{ex.Message}";
            requestHandle?.Dispose();
            _log.Add("SYSTEM_SLEEP_INHIBIT", "FAILED",
                $"result=FAILED owner={Safe(owner)} operation={Safe(operation)} generation={generation} reason={Safe(error)} api=PowerRequest rule=operation_lifecycle_power_request_contract");
            return false;
        }
        finally
        {
            if (reasonPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(reasonPtr);
        }
    }

    private sealed class PowerRequestLease : IDisposable
    {
        private readonly LogRepository _log;
        private SafeFileHandle? _handle;
        private readonly string _owner;
        private readonly string _operation;
        private readonly long _generation;
        private readonly DateTime _acquiredAt;
        private int _disposed;

        public PowerRequestLease(LogRepository log, SafeFileHandle handle, string owner, string operation, long generation, DateTime acquiredAt)
        {
            _log = log;
            _handle = handle;
            _owner = owner;
            _operation = operation;
            _generation = generation;
            _acquiredAt = acquiredAt;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var handle = Interlocked.Exchange(ref _handle, null);
            if (handle is null)
                return;

            var clearOk = false;
            var clearError = 0;
            try
            {
                clearOk = !handle.IsInvalid && PowerClearRequest(handle, PowerRequestType.SystemRequired);
                if (!clearOk)
                    clearError = Marshal.GetLastWin32Error();
            }
            catch
            {
                clearOk = false;
                clearError = Marshal.GetLastWin32Error();
            }
            finally
            {
                handle.Dispose();
            }

            var heldMs = Math.Max(0L, (long)(DateTime.Now - _acquiredAt).TotalMilliseconds);
            _log.Add("SYSTEM_SLEEP_INHIBIT", clearOk ? "RELEASED" : "RELEASE_WARN",
                $"result={(clearOk ? "RELEASED" : "RELEASE_WARN")} owner={Safe(_owner)} operation={Safe(_operation)} generation={_generation} requestType=SystemRequired heldMs={heldMs} clearWin32={(clearOk ? 0 : clearError)} lifecycle=owner_terminal_after_finalize rule=operation_lifecycle_power_request_contract");
        }
    }

    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr ReasonString;
    }

    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeFileHandle powerRequest, PowerRequestType requestType);

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '_');
}
