#if TVAIR_DEVELOPER_DIAGNOSTICS
using System.Runtime.InteropServices;

namespace TvAIr.Workers;

// Numeric window/process identities only. No external titles, executable paths,
// process enumeration, activation, input attachment or focus restoration.
internal static class WorkerFocusSnapshot
{
    internal static string Capture(IntPtr workerWindow = default)
    {
        try
        {
            var foreground = GetForegroundWindow();
            _ = GetWindowThreadProcessId(foreground, out var foregroundPid);
            var ownPid = Environment.ProcessId;
            var active = IntPtr.Zero;
            var focus = IntPtr.Zero;
            if (workerWindow != IntPtr.Zero)
            {
                var thread = GetWindowThreadProcessId(workerWindow, out var windowPid);
                if (windowPid == ownPid)
                {
                    var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
                    if (GetGUIThreadInfo(thread, ref info))
                    {
                        active = info.Active;
                        focus = info.Focus;
                    }
                }
            }
            return $"observerPid={ownPid} foregroundHwnd=0x{foreground.ToInt64():X} foregroundPid={foregroundPid} workerHwnd=0x{workerWindow.ToInt64():X} workerActiveHwnd=0x{active.ToInt64():X} workerFocusHwnd=0x{focus.ToInt64():X} foregroundIsObserver={foregroundPid == ownPid}";
        }
        catch { return "focusSnapshot=unavailable"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
#endif
