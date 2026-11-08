namespace TvAIr.Core;

// EPG user-facing causes are decided by the event producer. Diagnostic text is
// never parsed to identify a user-facing failure or its severity.
public enum EpgUserFailureReason
{
    Unspecified,
    ChannelConfiguration,
    NoTargetChannels,
    ChannelRouteUnavailable,
    TunerUnavailable,
    WorkerUnavailable,
    WorkerExecutionFailed,
    CaptureFileMissing,
    CaptureFileEmpty,
    CaptureFileUnreadable,
    ImportUnavailable,
    FileAccessFailed,
    InputOutputFailed,
    DatabaseAccessFailed
}

// Capture failure classification belongs to its producer, not to its
// diagnostic message. The reason code is a projection of this enum.
internal enum EpgCaptureFailureReason
{
    TunerUnavailable,
    ChannelRouteUnavailable,
    WorkerProcessMissing,
    WorkerStartupNotReady,
    WorkerTaskException,
    CaptureFileMissing,
    CaptureFileEmpty,
    CaptureFileUnreadable,
    ImportUnavailable
}

public enum PreRecordEpgFailureReason
{
    Unspecified,
    NoFreeRecordingTuner,
    DeadlineTooClose,
    NormalEpgRunning,
    AdmissionClaimLost,
    ChannelUnavailable,
    ServiceNotFound,
    ServiceAmbiguous,
    TargetNotObserved,
    ProbeNotCompleted,
    ProbeFailed,
    ExecutionFailed
}

internal static class EpgFailurePresentationContract
{
    internal static EpgUserFailureReason FromCaptureFailure(EpgCaptureFailureReason cause, Exception? exception = null)
    {
        if (exception is not null)
        {
            var exceptionCause = FromException(exception);
            if (exceptionCause != EpgUserFailureReason.Unspecified)
                return exceptionCause;
        }
        return cause switch
        {
            EpgCaptureFailureReason.TunerUnavailable => EpgUserFailureReason.TunerUnavailable,
            EpgCaptureFailureReason.ChannelRouteUnavailable => EpgUserFailureReason.ChannelRouteUnavailable,
            EpgCaptureFailureReason.WorkerProcessMissing or EpgCaptureFailureReason.WorkerStartupNotReady
                => EpgUserFailureReason.WorkerUnavailable,
            EpgCaptureFailureReason.WorkerTaskException => EpgUserFailureReason.WorkerExecutionFailed,
            EpgCaptureFailureReason.CaptureFileMissing => EpgUserFailureReason.CaptureFileMissing,
            EpgCaptureFailureReason.CaptureFileEmpty => EpgUserFailureReason.CaptureFileEmpty,
            EpgCaptureFailureReason.CaptureFileUnreadable => EpgUserFailureReason.CaptureFileUnreadable,
            EpgCaptureFailureReason.ImportUnavailable => EpgUserFailureReason.ImportUnavailable,
            _ => EpgUserFailureReason.Unspecified
        };
    }

    internal static string CaptureCode(EpgCaptureFailureReason reason) => reason switch
    {
        EpgCaptureFailureReason.TunerUnavailable => "epg_tuner_unavailable",
        EpgCaptureFailureReason.ChannelRouteUnavailable => "epg_bondriver_route_unavailable",
        EpgCaptureFailureReason.WorkerProcessMissing => "worker_process_missing",
        EpgCaptureFailureReason.WorkerStartupNotReady => "worker_startup_not_ready",
        EpgCaptureFailureReason.WorkerTaskException => "worker_task_exception",
        EpgCaptureFailureReason.CaptureFileMissing => "no_ts_file",
        EpgCaptureFailureReason.CaptureFileEmpty => "empty_ts_file",
        EpgCaptureFailureReason.CaptureFileUnreadable => "import_failed_unknown_file_state",
        EpgCaptureFailureReason.ImportUnavailable => "import_failed_with_ts_file",
        _ => "unknown"
    };

    internal static EpgUserFailureReason FromException(Exception exception)
    {
        if (exception is System.Data.Common.DbException)
            return EpgUserFailureReason.DatabaseAccessFailed;
        if (exception is UnauthorizedAccessException)
            return EpgUserFailureReason.FileAccessFailed;
        if (exception is System.IO.IOException)
            return EpgUserFailureReason.InputOutputFailed;
        return EpgUserFailureReason.Unspecified;
    }

    internal static EpgUserFailureReason Combine(IEnumerable<EpgUserFailureReason> reasons)
    {
        var distinct = reasons.Distinct().ToHashSet();
        foreach (var candidate in new[]
        {
            EpgUserFailureReason.DatabaseAccessFailed,
            EpgUserFailureReason.FileAccessFailed,
            EpgUserFailureReason.InputOutputFailed,
            EpgUserFailureReason.ChannelConfiguration,
            EpgUserFailureReason.ChannelRouteUnavailable,
            EpgUserFailureReason.NoTargetChannels,
            EpgUserFailureReason.TunerUnavailable,
            EpgUserFailureReason.WorkerUnavailable,
            EpgUserFailureReason.WorkerExecutionFailed,
            EpgUserFailureReason.CaptureFileUnreadable,
            EpgUserFailureReason.CaptureFileMissing,
            EpgUserFailureReason.CaptureFileEmpty,
            EpgUserFailureReason.ImportUnavailable
        })
            if (distinct.Contains(candidate)) return candidate;
        return EpgUserFailureReason.Unspecified;
    }

    internal static bool IsCritical(EpgUserFailureReason reason)
        => reason is EpgUserFailureReason.FileAccessFailed or EpgUserFailureReason.InputOutputFailed
            or EpgUserFailureReason.DatabaseAccessFailed;

    internal static string Message(EpgUserFailureReason reason) => reason switch
    {
        EpgUserFailureReason.FileAccessFailed => "EPG取得に必要な操作へのアクセスが拒否されました",
        EpgUserFailureReason.InputOutputFailed => "EPG取得中に入出力エラーが発生しました",
        EpgUserFailureReason.DatabaseAccessFailed => "番組表データを保存できませんでした",
        EpgUserFailureReason.ChannelConfiguration => "EPG取得に必要なチャンネル設定がありません",
        EpgUserFailureReason.NoTargetChannels => "選択した放送波にEPG取得対象のチャンネルがありません",
        EpgUserFailureReason.ChannelRouteUnavailable => "録画用チューナーのチャンネル設定から対象を選局できませんでした",
        EpgUserFailureReason.TunerUnavailable => "EPG取得に使用できるチューナーがありませんでした",
        EpgUserFailureReason.WorkerUnavailable => "EPG取得処理を開始できませんでした",
        EpgUserFailureReason.WorkerExecutionFailed => "EPG取得処理を完了できませんでした",
        EpgUserFailureReason.CaptureFileMissing => "EPG取得ファイルを作成できませんでした",
        EpgUserFailureReason.CaptureFileEmpty => "EPG取得ファイルにデータがありませんでした",
        EpgUserFailureReason.CaptureFileUnreadable => "EPG取得ファイルの状態を確認できませんでした",
        EpgUserFailureReason.ImportUnavailable => "EPG情報を取り込めませんでした",
        _ => "EPG取得を完了できませんでした"
    };

    // First line is identical for EPG status, user log, and typed completion.
    // Counts belong to the status detail, not to the cause description.
    internal static string RunMessage(string? runResult, EpgUserFailureReason reason,
        int completedGroups = 0, int totalGroups = 0, string? blockedReason = null)
    {
        return (runResult ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "OK" => "EPG取得を完了しました",
            "BLOCKED" => EpgStartBlockMessage.FromReason(blockedReason),
            "PARTIAL" or "INCOMPLETE" => "EPGを一部取得しました",
            "FAILED" when completedGroups > 0 && completedGroups < totalGroups
                => "EPGを一部取得しましたが、" + Message(reason),
            _ => Message(reason)
        };
    }

    internal static string ExtractBlockedReason(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
        foreach (var part in detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index > 0 && string.Equals(part[..index].Trim(), "blockedReason", StringComparison.OrdinalIgnoreCase))
                return part[(index + 1)..].Trim();
        }
        return string.Empty;
    }

    internal static string Code(EpgUserFailureReason reason) => reason switch
    {
        EpgUserFailureReason.ChannelConfiguration => "channel_configuration",
        EpgUserFailureReason.NoTargetChannels => "no_target_channels",
        EpgUserFailureReason.ChannelRouteUnavailable => "channel_route_unavailable",
        EpgUserFailureReason.TunerUnavailable => "tuner_unavailable",
        EpgUserFailureReason.WorkerUnavailable => "worker_unavailable",
        EpgUserFailureReason.WorkerExecutionFailed => "worker_execution_failed",
        EpgUserFailureReason.CaptureFileMissing => "capture_file_missing",
        EpgUserFailureReason.CaptureFileEmpty => "capture_file_empty",
        EpgUserFailureReason.CaptureFileUnreadable => "capture_file_unreadable",
        EpgUserFailureReason.ImportUnavailable => "import_unavailable",
        EpgUserFailureReason.FileAccessFailed => "access_denied",
        EpgUserFailureReason.InputOutputFailed => "input_output_failed",
        EpgUserFailureReason.DatabaseAccessFailed => "database_access_failed",
        _ => "unknown"
    };

    internal static string Code(PreRecordEpgFailureReason reason) => reason switch
    {
        PreRecordEpgFailureReason.NoFreeRecordingTuner => "no_free_recording_tuner",
        PreRecordEpgFailureReason.DeadlineTooClose => "deadline_too_close",
        PreRecordEpgFailureReason.NormalEpgRunning => "normal_epg_running",
        PreRecordEpgFailureReason.AdmissionClaimLost => "admission_claim_lost",
        PreRecordEpgFailureReason.ChannelUnavailable => "no_prerec_channels",
        PreRecordEpgFailureReason.ServiceNotFound => "target_service_identity_not_found",
        PreRecordEpgFailureReason.ServiceAmbiguous => "target_service_identity_ambiguous",
        PreRecordEpgFailureReason.TargetNotObserved => "target_event_not_observed",
        PreRecordEpgFailureReason.ProbeNotCompleted => "probe_no_completion_evidence",
        PreRecordEpgFailureReason.ProbeFailed => "probe_failed",
        PreRecordEpgFailureReason.ExecutionFailed => "execution_failed",
        _ => "unknown"
    };

    internal static string PreRecordMessage(PreRecordEpgFailureReason reason) => reason switch
    {
        PreRecordEpgFailureReason.NoFreeRecordingTuner => "録画前EPG確認に使えるチューナーがありませんでした。予約時刻は変更しません",
        PreRecordEpgFailureReason.DeadlineTooClose => "録画開始が近いためEPG確認を終了しました。予約時刻は変更しません",
        PreRecordEpgFailureReason.NormalEpgRunning => "EPG取得中のため録画前の確認を行えませんでした。予約時刻は変更しません",
        PreRecordEpgFailureReason.AdmissionClaimLost => "録画前EPG確認に使用するチューナーを確保できませんでした。予約時刻は変更しません",
        PreRecordEpgFailureReason.ChannelUnavailable => "EPG確認対象のチャンネル情報がありませんでした。予約時刻は変更しません",
        PreRecordEpgFailureReason.ServiceNotFound => "録画対象の放送局を特定できませんでした。予約時刻は変更しません",
        PreRecordEpgFailureReason.ServiceAmbiguous => "録画対象の放送局を一つに特定できませんでした。予約時刻は変更しません",
        PreRecordEpgFailureReason.TargetNotObserved => "EPGを取得しましたが対象番組を確認できませんでした。予約時刻は変更しません",
        PreRecordEpgFailureReason.ProbeNotCompleted => "録画前EPG確認が途中で終了しました。予約時刻は変更しません",
        PreRecordEpgFailureReason.ProbeFailed => "録画前EPG確認を完了できませんでした。予約時刻は変更しません",
        PreRecordEpgFailureReason.ExecutionFailed => "録画前EPG確認でエラーが発生しました。予約時刻は変更しません",
        _ => "録画前EPG確認を完了できませんでした。予約時刻は変更しません"
    };
}
