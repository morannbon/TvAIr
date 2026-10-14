using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
internal static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int size;
        public string? reserved, desktop, title;
        public int x, y, width, height, xChars, yChars, fill, flags;
        public short show, reservedSize;
        public IntPtr reservedPtr, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public int size, flags;
        public IntPtr active, focus, capture, menu, move, caret;
        public int left, top, right, bottom;
    }
    private delegate bool EnumWindow(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateDesktop(string name, string? device, IntPtr mode, int flags, uint access, IntPtr security);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] internal static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder name, int length, out int needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr handle, out int code);
    [DllImport("user32.dll")] private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindow callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] internal static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    internal static string DesktopName(IntPtr desktop)
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _)) throw new System.ComponentModel.Win32Exception();
        return name.ToString();
    }
    internal static Process Start(string exe, IEnumerable<string> args, string desktop)
    {
        var startup = new StartupInfo { size = Marshal.SizeOf<StartupInfo>(), desktop = desktop, flags = 1, show = 0 };
        var command = new StringBuilder(string.Join(" ", new[] { exe }.Concat(args).Select(x => "\"" + x.Replace("\"", "\\\"") + "\"")));
        if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, Path.GetDirectoryName(exe), ref startup, out var info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var process = Process.GetProcessById(info.pid);
            _ = process.Handle;
            return process;
        }
        finally { CloseHandle(info.process); CloseHandle(info.thread); }
    }
    internal static int ExitCode(Process process)
    {
        if (!GetExitCodeProcess(process.Handle, out var code)) throw new System.ComponentModel.Win32Exception();
        return code;
    }
    internal static List<IntPtr> Windows(int pid)
    {
        var windows = new List<IntPtr>();
        EnumDesktopWindows(GetThreadDesktop(GetCurrentThreadId()), (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid) windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }
    internal static GuiThreadInfo GuiInfo(IntPtr hwnd)
    {
        var thread = GetWindowThreadProcessId(hwnd, out _);
        var info = new GuiThreadInfo { size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info)) throw new System.ComponentModel.Win32Exception();
        return info;
    }
    internal static string Caption(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetWindowText(hwnd, text, text.Capacity);
        return hwnd + ":" + text;
    }
    internal static bool IsTaskbarWindow(IntPtr hwnd)
        => IsWindowVisible(hwnd) && (GetWindowLong(hwnd, -20) & 0x00040000) != 0;
}
