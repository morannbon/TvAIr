using System.Diagnostics;
using TvAIr.Workers;

namespace TvAIr.Core;

/// <summary>
/// Selects the worker's immutable presentation contract. The worker owns all GUI
/// creation and visibility; the launcher never requests an activating show state.
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
        var policy = ResolvePresentation(launchKind, showTaskbarIconSetting);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--" + WorkerPresentationContract.ArgumentName);
        startInfo.ArgumentList.Add(WorkerPresentationContract.Serialize(policy));
        return startInfo;
    }


    /// <summary>
    /// BonDriver_PTx は物理DIDをjob JSONからは認識せず、workerプロセスの生コマンドライン上の /DID を参照する。
    /// TvAIrEpgRecがBonDriverを直接LoadLibraryする全物理Tuner経路は、この共通入口からjobと同じDIDを投影する。
    /// </summary>
    public static void AppendPhysicalTunerDidArgument(ProcessStartInfo startInfo, string? did)
    {
        if (startInfo is null) throw new ArgumentNullException(nameof(startInfo));
        var normalized = (did ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) return;

        // BonDriver側がGetCommandLineを読む契約のため、TvAIrEpgRec独自の --did ではなく
        // TVTest互換の単一スラッシュ形式をそのままプロセス引数へ載せる。
        startInfo.ArgumentList.Add("/DID");
        startInfo.ArgumentList.Add(normalized);
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
        return ResolvePresentation(launchKind, showTaskbarIconSetting) == WorkerPresentationPolicy.Taskbar;
    }

    private static WorkerPresentationPolicy ResolvePresentation(TvAIrEpgRecLaunchKind launchKind, bool enabled)
        => WorkerPresentationContract.Select(
            launchKind is TvAIrEpgRecLaunchKind.PrimaryRecord or TvAIrEpgRecLaunchKind.EpgTransportStreamWorker,
            enabled);
}
