using System.Diagnostics;

namespace TvAIr.Core;

/// <summary>
/// Centralized TvAIrEpgRec process launch window policy.
/// EPG transport-stream workers are not user UI windows, but when the user setting requests
/// TvAIrEpgRec taskbar visibility they must appear as active worker icons for the currently
/// running tuner workers.  The separate logo/display host window is controlled by job metadata
/// and is not enabled for EPG TS workers.
/// </summary>
public enum TvAIrEpgRecLaunchKind
{
    PrimaryRecord,
    EpgTransportStreamWorker,
    PreRecordEpgCheckWorker,
    AuxiliaryPostProcess,
    DiagnosticProbe
}

public static class WorkerProcessStartInfoFactory
{
    public static ProcessStartInfo CreateTvAIrEpgRec(
        string executablePath,
        TvAIrEpgRecLaunchKind launchKind,
        bool showTaskbarIconSetting)
    {
        var showWindow = IsTaskbarIconVisible(launchKind, showTaskbarIconSetting);

        return new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = !showWindow,
            WindowStyle = showWindow ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Hidden
        };
    }

    public static string GetWindowPolicy(TvAIrEpgRecLaunchKind launchKind, bool showTaskbarIconSetting)
    {
        return launchKind switch
        {
            TvAIrEpgRecLaunchKind.PrimaryRecord when showTaskbarIconSetting => "setting_visible_record_worker",
            TvAIrEpgRecLaunchKind.PrimaryRecord => "setting_hidden_record_worker",
            TvAIrEpgRecLaunchKind.EpgTransportStreamWorker when showTaskbarIconSetting => "setting_visible_active_epg_worker_by_running_tuner",
            TvAIrEpgRecLaunchKind.EpgTransportStreamWorker => "setting_hidden_epg_worker_by_setting",
            TvAIrEpgRecLaunchKind.PreRecordEpgCheckWorker => "hidden_pre_record_epg_check_worker",
            TvAIrEpgRecLaunchKind.AuxiliaryPostProcess => "hidden_auxiliary_post_process_worker",
            TvAIrEpgRecLaunchKind.DiagnosticProbe => "hidden_diagnostic_probe_worker",
            _ => "hidden_worker"
        };
    }

    public static bool IsTaskbarIconVisible(TvAIrEpgRecLaunchKind launchKind, bool showTaskbarIconSetting)
    {
        return showTaskbarIconSetting && launchKind is TvAIrEpgRecLaunchKind.PrimaryRecord or TvAIrEpgRecLaunchKind.EpgTransportStreamWorker;
    }
}
