using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TvAIr.Core;
using TvAIr.Workers;

internal static class Program
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Dictionary<string, object> Observations = new();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("worker DLL and results directory are required");
        var workerDll = Path.GetFullPath(args[0]);
        var resultsDirectory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(resultsDirectory);
        if (args.Length == 4)
        {
            var name = args[2];
            try
            {
                var desktopName = Native.DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
                Check(desktopName.StartsWith("TvAIrFocusTest_", StringComparison.Ordinal), "test must run on its isolated desktop");
                Application.EnableVisualStyles();
                RunCase(name, workerDll);
                File.WriteAllText(args[3], JsonSerializer.Serialize(new { name, passed = true, desktopName, observations = Observations }));
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(args[3], JsonSerializer.Serialize(new { name, passed = false, error = error.ToString() }));
                return 1;
            }
        }

        var desktop = Native.CreateDesktop("TvAIrFocusTest_" + Guid.NewGuid().ToString("N"), null, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var cases = new[] { "baseline", "policy", "window", "hidden", "epg", "record", "repeated-processes",
            "fallback-selection", "chain-updates", "prerec-record-transition", "parallel-workers", "focus-telemetry" };
        var results = new List<JsonElement>();
        try
        {
            foreach (var name in cases)
            {
                var output = Path.Combine(resultsDirectory, name + ".json");
                using var child = Native.Start(Environment.ProcessPath!,
                    new[] { workerDll, resultsDirectory, name, output }, Native.DesktopName(desktop));
                if (!child.WaitForExit(20000)) { child.Kill(); throw new TimeoutException(name); }
                var result = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(output));
                results.Add(result);
                Console.WriteLine((result.GetProperty("passed").GetBoolean() ? "PASS " : "FAIL ") + name);
                if (!result.GetProperty("passed").GetBoolean()) Console.WriteLine(result.GetProperty("error").GetString());
            }
        }
        finally { Native.CloseDesktop(desktop); }
        File.WriteAllText(Path.Combine(resultsDirectory, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return results.All(x => x.GetProperty("passed").GetBoolean()) ? 0 : 1;
    }

    private static void RunCase(string name, string workerDll)
    {
        if (name == "policy")
        {
            foreach (var kind in Enum.GetValues<TvAIrEpgRecLaunchKind>())
            foreach (var enabled in new[] { false, true })
            {
                var expected = enabled && kind is TvAIrEpgRecLaunchKind.PrimaryRecord or TvAIrEpgRecLaunchKind.EpgTransportStreamWorker;
                var psi = WorkerProcessStartInfoFactory.CreateTvAIrEpgRec("worker.exe", kind, enabled);
                Check(psi.WindowStyle == ProcessWindowStyle.Hidden && psi.CreateNoWindow && !psi.UseShellExecute, "launcher must not show GUI");
                Check(psi.ArgumentList.SequenceEqual(new[] { "--worker-presentation", expected ? "taskbar" : "hidden" }), "single presentation argument");
                Check(WorkerProcessStartInfoFactory.IsTaskbarIconVisible(kind, enabled) == expected, "policy matches launch contract");
            }
            Check(WorkerPresentationContract.Parse(null) == WorkerPresentationPolicy.Hidden, "missing policy is hidden");
            Check(WorkerPresentationContract.Parse("invalid") == WorkerPresentationPolicy.Hidden, "invalid policy is hidden");
            return;
        }

        if (name is "prerec-record-transition" or "parallel-workers" or "focus-telemetry")
        {
            using var input = new Form { Text = "isolated input during worker transitions" };
            input.Show(); Native.ShowWindow(input.Handle, 9); Application.DoEvents();
            var inputHandle = input.Handle;
            Check(Native.GetActiveWindow() == inputHandle, "transition test input must be active before starting workers");
            var directory = Path.Combine(Path.GetDirectoryName(workerDll)!, "transition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var exe = Path.ChangeExtension(workerDll, ".exe");
            var steps = name == "prerec-record-transition"
                ? new[] { ("epg", "taskbar"), ("epg-check", "hidden"), ("record", "taskbar"), ("epg", "taskbar"), ("record", "taskbar") }
                : new[] { ("runtime", "taskbar"), ("runtime", "hidden"), ("runtime", "taskbar") };
            var children = new List<(Process Process, string Policy, string Progress)>();
            var observations = 0;
            try
            {
                foreach (var step in steps)
                {
                    var progress = Path.Combine(directory, "progress-" + children.Count + ".jsonl");
                    // EPG/record execution requires a real TS source. Use contract-only
                    // runtime to test the selected presentation/process lifetime here;
                    // epg/record/chain cases exercise the actual mode callbacks below.
                    var process = Native.Start(exe, new[] { "--mode", "runtime", "--keep-alive-ms", "600",
                        "--worker-presentation", step.Item2, "--progress", progress },
                        Native.DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId())));
                    children.Add((process, step.Item2, progress));
                    if (name != "parallel-workers") Monitor(process, step.Item2);
                }
                if (name == "parallel-workers")
                    while (children.Any(x => !x.Process.HasExited))
                    {
                        foreach (var child in children.Where(x => !x.Process.HasExited)) Inspect(child.Process, child.Policy);
                        Application.DoEvents(); Thread.Sleep(5);
                    }
                foreach (var child in children)
                {
                    Check(Native.ExitCode(child.Process) == 0, "worker CLI failed");
                    var rows = File.ReadAllLines(child.Progress).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => JsonSerializer.Deserialize<JsonElement>(x)).ToArray();
                    Check(rows.Length > 0 && rows.Last().GetProperty("Stage").GetString() == "completed", "canonical terminal stage changed");
                    var focus = rows.Where(x => x.GetProperty("Stage").GetString() == "worker_focus").ToArray();
                    Check(focus.Length > 0, "focus observations absent");
                    Check(focus.All(x => !x.GetProperty("Message").GetString()!.Contains("phase=window_activated")), "worker activation event observed");
                    if (child.Policy == "hidden") Check(focus.All(x => !x.GetProperty("Message").GetString()!.Contains("phase=window_created")), "hidden worker created GUI");
                    else Check(focus.Any(x => x.GetProperty("Message").GetString()!.Contains("phase=window_show_after")), "visible worker not observed");
                }
                Check(observations > 0, "visible workers were never sampled");
                Check(Native.GetActiveWindow() == inputHandle, "input window changed after worker exit");
                Observations["workerProcesses"] = children.Count; Observations["windowSamples"] = observations;
                Observations["progressDirectory"] = directory;
            }
            finally
            {
                foreach (var child in children) { if (!child.Process.HasExited) child.Process.Kill(); child.Process.Dispose(); }
            }
            return;

            void Monitor(Process child, string policy)
            {
                while (!child.HasExited) { Inspect(child, policy); Application.DoEvents(); Thread.Sleep(5); }
            }
            void Inspect(Process child, string policy)
            {
                Check(Native.GetActiveWindow() == inputHandle, "input window changed during worker execution");
                foreach (var hwnd in Native.Windows(child.Id))
                {
                    if (!Native.IsTaskbarWindow(hwnd)) continue;
                    observations++;
                    Check(policy == "taskbar" && Native.IsIconic(hwnd), "unexpected visible worker");
                    Check((Native.ExStyle(hwnd) & 0x08000000) != 0, "background worker can be selected for automatic activation");
                    var info = Native.GuiInfo(hwnd);
                    Check(info.active == IntPtr.Zero && info.focus == IntPtr.Zero, "worker obtained activation/focus");
                }
            }
        }

        if (name == "repeated-processes")
        {
            var exe = Path.ChangeExtension(workerDll, ".exe");
            for (var i = 0; i < 8; i++)
            {
                var visible = i % 2 == 0;
                using var child = Native.Start(exe, new[] { "--mode", "runtime", "--keep-alive-ms", "900", "--worker-presentation", visible ? "taskbar" : "hidden" },
                    Native.DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId())));
                var sawWindow = false;
                while (!child.HasExited)
                {
                    foreach (var hwnd in Native.Windows(child.Id))
                    {
                        if (!Native.IsTaskbarWindow(hwnd)) continue;
                        sawWindow = true;
                        Check(visible, "hidden worker created visible window");
                        Check(Native.IsIconic(hwnd), "worker initial window must be minimized");
                        var info = Native.GuiInfo(hwnd);
                        Check(info.active == IntPtr.Zero && info.focus == IntPtr.Zero, "worker process acquired activation/focus");
                    }
                    Thread.Sleep(5);
                }
                Check(Native.ExitCode(child) == 0, "worker runtime completed");
                Check(sawWindow == visible, "actual worker CLI visibility");
            }
            return;
        }

        using var anchor = new Form { Text = "isolated test input window" };
        anchor.Show();
        Native.ShowWindow(anchor.Handle, 9);
        Application.DoEvents();
        var anchorHandle = anchor.Handle;
        Check(Native.GetActiveWindow() == anchorHandle, "anchor must be active on isolated desktop");
        if (name == "baseline")
        {
            using var baseline = new OldWindow();
            var showCommand = Convert.ToInt32(typeof(Form).GetProperty("ShowParams", PrivateInstance)!.GetValue(baseline));
            Check(showCommand == 2, "minimized WinForms selects SW_SHOWMINIMIZED before ShowWithoutActivation");
            baseline.Show();
            Application.DoEvents();
            Observations["selectedShowCommand"] = showCommand;
            Observations["activationReproducedOnIsolatedDesktop"] = Native.GetActiveWindow() == baseline.Handle;
            return;
        }

        var assembly = Assembly.LoadFrom(workerDll);
        var presentation = assembly.GetType("TvAIrEpgRec.WorkerPresentation", true)!;
        if (name is "window" or "fallback-selection")
        {
            var snapshotType = presentation.GetNestedType("PresentationSnapshot", BindingFlags.NonPublic)!;
            var snapshot = Activator.CreateInstance(snapshotType, "EPG取得 テスト")!;
            var windowType = presentation.GetNestedType("WorkerWindow", BindingFlags.NonPublic)!;
            using var window = (Form)Activator.CreateInstance(windowType, PrivateInstance, null, new[] { snapshot }, null)!;
            var activations = 0;
            window.Activated += (_, _) => activations++;
            windowType.GetMethod("PresentInBackground", PrivateInstance)!.Invoke(window, null);
            Application.DoEvents();
            var hwnd = window.Handle;
            Check(Native.IsWindowVisible(hwnd) && Native.IsIconic(hwnd), "visible minimized window");
            Check(window.Visible, "WinForms visibility must track native visibility");
            Check(activations == 0 && Native.GetActiveWindow() == anchorHandle, "initial show must preserve activation");
            Check(window.ShowInTaskbar && window.Owner is null, "single taskbar window");
            if (name == "fallback-selection")
            {
                Check((Native.ExStyle(hwnd) & 0x08000000) != 0, "worker is eligible for automatic foreground fallback");
                anchor.Close(); Application.DoEvents();
                Check(Native.GetActiveWindow() != hwnd && activations == 0, "closing input window selected background worker");
                return;
            }
            Native.ShowWindow(hwnd, 9); // A deliberate user restore, on the isolated desktop only.
            Application.DoEvents();
            Check(!Native.IsIconic(hwnd) && Native.GetActiveWindow() == hwnd, "user restore remains possible");
            Check(window.Controls.Count == 0, "worker must not create a channel-logo control");
            Native.SetActiveWindow(anchorHandle);
            activations = 0;
            var updated = Activator.CreateInstance(snapshotType, "EPG取得 次の局")!;
            windowType.GetMethod("Apply", PrivateInstance)!.Invoke(window, new[] { updated });
            Application.DoEvents();
            Check(window.Handle == hwnd && window.Text == "EPG取得 次の局", "content update preserves handle");
            Check(!Native.IsIconic(hwnd), "updates must not minimize a user-restored window");
            Check(activations == 0 && Native.GetActiveWindow() == anchorHandle, "title update must not activate");
            Check(window.Controls.Count == 0, "title update must not add channel-logo controls");
            return;
        }

        var mode = name is "record" or "chain-updates" ? "record" : "epg";
        var jobType = assembly.GetType("TvAIrEpgRec.TvAIrEpgRecJob", true)!;
        var job = Activator.CreateInstance(jobType)!;
        jobType.GetProperty("Metadata")!.SetValue(job, new Dictionary<string, string>
        {
            ["taskbarIconVisible"] = name == "hidden" ? "true" : "false",
            ["displayTitle"] = "first service"
        }); // Legacy metadata is intentionally contradictory and cannot override the contract.
        var policyType = assembly.GetType("TvAIr.Workers.WorkerPresentationPolicy", true)!;
        presentation.GetMethod("Start", PrivateStatic)!.Invoke(null, new[] { mode, job, Enum.Parse(policyType, name == "hidden" ? "Hidden" : "Taskbar") });
        var update = presentation.GetMethod("UpdateBase", PrivateStatic)!;
        update.Invoke(null, new[] { mode, job });
        var opened = presentation.GetMethods(PrivateStatic).Single(x => x.Name == "RecordingOpened" && x.GetParameters().Length == 3);
        opened.Invoke(null, new object?[] { 1, "first reservation", "first service" });
        opened.Invoke(null, new object?[] { 2, "second reservation", "second service" });
        presentation.GetMethod("RecordingClosed", PrivateStatic)!.Invoke(null, new object[] { 2 });
        Form? projected = null;
        for (var i = 0; i < 200; i++)
        {
            projected = (Form?)presentation.GetField("_window", PrivateStatic)!.GetValue(null);
            if (projected is not null && projected.IsHandleCreated && Native.IsWindowVisible(projected.Handle)) break;
            Thread.Sleep(5);
        }
        if (name == "hidden")
        {
            Check(projected is null && presentation.GetField("_uiThread", PrivateStatic)!.GetValue(null) is null,
                "hidden progress/recording updates must create no GUI thread/window");
            return;
        }
        Check(projected is not null && projected.IsHandleCreated, "presentation starts");
        var projectedHandle = projected!.Handle;
        var active = Native.GuiInfo(projectedHandle);
        Check(active.active == IntPtr.Zero && active.focus == IntPtr.Zero, "presentation thread must not acquire activation/focus");
        Check(Native.IsIconic(projectedHandle), "presentation remains minimized");
        if (name == "chain-updates")
        {
            for (var reservation = 3; reservation <= 18; reservation++)
            {
                opened.Invoke(null, new object?[] { reservation, "chain " + reservation, "same service" });
                presentation.GetMethod("RecordingClosed", PrivateStatic)!.Invoke(null, new object[] { reservation - 1 });
                Thread.Sleep(10);
                Check(projected.Handle == projectedHandle && Native.IsIconic(projectedHandle), "chain update recreated/restored worker");
                var info = Native.GuiInfo(projectedHandle);
                Check(info.active == IntPtr.Zero && info.focus == IntPtr.Zero, "chain update acquired input");
            }
            Observations["segmentUpdates"] = 16;
        }
        jobType.GetProperty("Metadata")!.SetValue(job, new Dictionary<string, string> { ["displayTitle"] = "next service" });
        update.Invoke(null, new[] { mode, job });
        Thread.Sleep(100);
        Check(projected.Handle == projectedHandle, "updates never recreate presentation");
        Check(Native.GuiInfo(projectedHandle).active == IntPtr.Zero, "updates remain non-activating");
        // WinForms also creates native IME/helper windows. Count the actual taskbar
        // app windows, not those OS-managed surfaces.
        var visibleWindows = Native.Windows(Environment.ProcessId).Where(x => Native.IsTaskbarWindow(x) && x != anchorHandle).ToArray();
        Check(visibleWindows.Length == 1, "exactly one visible worker window: " + string.Join(",", visibleWindows.Select(Native.Caption)));
        projected.Invoke(new Action(projected.Close));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class OldWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        internal OldWindow() { WindowState = FormWindowState.Minimized; ShowInTaskbar = true; }
    }
}

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
    internal static int ExStyle(IntPtr hwnd) => GetWindowLong(hwnd, -20);
}
