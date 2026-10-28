using System.Diagnostics;

namespace TvAIr.Tuner;

// One completion decision shared by explicit Stop, Restart, rollback and Host shutdown.
internal static class ViewerProcessStopContract
{
    internal static ViewerProcessStopResult Stop(IViewerProcessStopTarget process)
    {
        bool closeIssued = false;
        try
        {
            if (process.HasExited) return new(true, "ALREADY_EXITED", false, "already exited");
            try { closeIssued = process.CloseMainWindow(); } catch { }
            if (closeIssued && process.WaitForExit(3000))
                return new(true, "CLOSED", true, "closed");
            process.Kill();
            if (!process.WaitForExit(3000))
                return new(false, "STOP_TIMEOUT", closeIssued, "process_exit_not_confirmed");
            return new(true, "KILLED_SINGLE_PID", closeIssued, "killed single pid");
        }
        catch (Exception ex)
        {
            // Exit can race a close/kill request, but an observation error is never "not found".
            try { if (process.HasExited) return new(true, "ALREADY_EXITED", closeIssued, "already exited"); } catch { }
            return new(false, "STOP_FAILED", closeIssued, ex.Message);
        }
    }
}
internal interface IViewerProcessStopTarget
{
    bool HasExited { get; }
    bool CloseMainWindow();
    bool WaitForExit(int milliseconds);
    void Kill();
}
internal sealed class ViewerProcessStopTarget(Process process) : IViewerProcessStopTarget
{
    public bool HasExited => process.HasExited;
    public bool CloseMainWindow() => process.CloseMainWindow();
    public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);
    public void Kill() => process.Kill(entireProcessTree: false);
}
internal sealed record ViewerProcessStopResult(bool Success, string Outcome, bool CloseIssued, string Message);
