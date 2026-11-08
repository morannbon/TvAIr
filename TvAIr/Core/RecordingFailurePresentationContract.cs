namespace TvAIr.Core;

// The source of the failed lifecycle transition supplies this cause. The public
// message never infers it from a free-form exception, diagnostic text or allocation flag.
public enum RecordingFailureCause
{
    Unspecified,
    ChainPredecessorFailed,
    StartDeadlinePassed,
    ServiceGroupUnavailable,
    TargetServiceUnavailable,
    ChannelArgumentUnavailable,
    AssignedTunerChannelUnavailable,
    AssignedTunerTuningUnavailable,
    ViewingTunerProtected,
    BonDriverMissing,
    WorkerExecutableMissing,
    WorkerLaunchFailed,
    WorkerOpenTunerFailed,
    WorkerSetChannelFailed,
    WorkerResourceMissing,
    WorkerLibraryLoadFailed,
    WorkerStreamUnavailable,
    WorkerReportedFailure,
    WorkerExitedBeforeRecording,
    WorkerStartupTimedOut,
    WorkerResultMissing,
    WorkerReportedStopFailure,
    RecordingEvidenceInsufficient,
    RecordingStopFailed
}

internal enum RecordingFailureStage { BeforeStart, DuringRecording, Completion }

internal static class RecordingFailurePresentationContract
{
    internal const string CauseMetadataKey = "recordingFailureCause";

    internal static string Code(RecordingFailureCause cause) => cause.ToString();

    internal static RecordingFailureCause FromMetadata(IReadOnlyDictionary<string, string?> metadata)
    {
        if (!metadata.TryGetValue(CauseMetadataKey, out var raw) || string.IsNullOrWhiteSpace(raw)
            || !Enum.TryParse<RecordingFailureCause>(raw, ignoreCase: false, out var cause)
            || !Enum.IsDefined(cause) || !string.Equals(raw, cause.ToString(), StringComparison.Ordinal))
            return RecordingFailureCause.Unspecified;
        return cause;
    }

    internal static void WriteCause(IDictionary<string, string?> metadata, RecordingFailureCause cause)
    {
        if (cause != RecordingFailureCause.Unspecified)
            metadata[CauseMetadataKey] = Code(cause);
    }

    internal static RecordingFailureCause FromWorkerResult(bool responseExists, bool responseSuccess)
        => !responseExists ? RecordingFailureCause.WorkerResultMissing
            : !responseSuccess ? RecordingFailureCause.WorkerReportedStopFailure
            : RecordingFailureCause.RecordingEvidenceInsufficient;

    internal static string Message(RecordingFailureCause cause, RecordingFailureStage stage)
        => cause switch
        {
            RecordingFailureCause.ChainPredecessorFailed => "前の番組を録画できなかったため、チェーン録画を開始できませんでした",
            RecordingFailureCause.StartDeadlinePassed => "録画開始の期限までに録画を開始できませんでした",
            RecordingFailureCause.ServiceGroupUnavailable => "録画対象の放送波を特定できませんでした",
            RecordingFailureCause.TargetServiceUnavailable => "録画対象のチャンネルを確認できませんでした",
            RecordingFailureCause.ChannelArgumentUnavailable => "録画対象の選局情報を作成できませんでした",
            RecordingFailureCause.AssignedTunerChannelUnavailable => "割当済みチューナーのチャンネル設定に録画対象がありません",
            RecordingFailureCause.AssignedTunerTuningUnavailable => "割当済みチューナーの選局情報を確認できませんでした",
            RecordingFailureCause.ViewingTunerProtected => "視聴用チューナーを保護するため録画を開始できませんでした",
            RecordingFailureCause.BonDriverMissing => "録画に必要なBonDriverが見つかりませんでした",
            RecordingFailureCause.WorkerExecutableMissing => "録画用プログラムが見つかりませんでした",
            RecordingFailureCause.WorkerLaunchFailed => "録画用プログラムを起動できませんでした",
            RecordingFailureCause.WorkerOpenTunerFailed => "録画用チューナーを開けませんでした",
            RecordingFailureCause.WorkerSetChannelFailed => "録画対象のチャンネルに切り替えられませんでした",
            RecordingFailureCause.WorkerResourceMissing => "録画に必要なファイルが見つかりませんでした",
            RecordingFailureCause.WorkerLibraryLoadFailed => "録画に必要なライブラリを読み込めませんでした",
            RecordingFailureCause.WorkerStreamUnavailable => "録画用の放送データを取得できませんでした",
            RecordingFailureCause.WorkerReportedFailure => "録画用プログラムから失敗が報告されました",
            RecordingFailureCause.WorkerExitedBeforeRecording => "録画開始前に録画用プログラムが終了しました",
            RecordingFailureCause.WorkerStartupTimedOut => "録画用プログラムの開始を確認できませんでした",
            RecordingFailureCause.WorkerResultMissing => "録画用プログラムの終了結果を確認できませんでした",
            RecordingFailureCause.WorkerReportedStopFailure => "録画用プログラムが正常終了できませんでした",
            RecordingFailureCause.RecordingEvidenceInsufficient => "録画ファイルの完了を確認できませんでした",
            RecordingFailureCause.RecordingStopFailed => "録画の終了処理に失敗しました",
            _ => stage switch
            {
                RecordingFailureStage.BeforeStart => "録画を開始できませんでした",
                RecordingFailureStage.DuringRecording => "録画が途中で終了しました",
                _ => "録画を完了できませんでした"
            }
        };

    // Recovery tells us when the interruption was found, not why the process stopped.
    internal static string InterruptedMessage(string? trigger) => trigger switch
    {
        "StartupRecordingRecovery" => "TvAIr起動時に中断した録画を確認しました",
        "PowerResumeRecordingRecovery" => "PC復帰後に中断した録画を確認しました",
        "RuntimeRecordingRecovery" => "録画処理が途中で終了しました",
        _ => "録画が途中で終了しました"
    };
}
