using System.Threading;

namespace TvAIr.Core;

/// <summary>
/// Explicit user exit must not leave TvAIr.exe holding its own binaries indefinitely.
/// The normal host shutdown route is always attempted first.  This watchdog is only a
/// final bound for an explicit app-exit request when a hosted service / native callback
/// fails to converge in time.
/// </summary>
public static class ApplicationExitWatchdog
{
    private static int _armed;
    private static readonly TimeSpan GracefulDeadline = TimeSpan.FromSeconds(5);

    public static void Arm(LogRepository log, string source)
    {
        if (Interlocked.Exchange(ref _armed, 1) != 0)
            return;

        var safeSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();
        var thread = new Thread(() =>
        {
            Thread.Sleep(GracefulDeadline);
            try
            {
                log.Add("APP_EXIT_WATCHDOG", "FORCE_EXIT",
                    $"result=DEADLINE_REACHED source={safeSource} deadlineMs={(int)GracefulDeadline.TotalMilliseconds} action=Environment.Exit rule=explicit_app_exit_process_termination_contract");
            }
            catch { }

            // This is reached only when the ordinary StopApplication -> ApplicationStopping ->
            // hosted-service StopAsync/Dispose path did not terminate the process within the bound.
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "TvAIr.ExplicitExitWatchdog"
        };
        thread.Start();
    }
}
