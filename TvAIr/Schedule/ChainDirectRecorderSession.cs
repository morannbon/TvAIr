using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>
/// release_contract: チェーン境界のstop-restart handoffで使用する実行セッション正本。
/// 共通割り当てルートで確定した固定物理Tunerと、実録画で取得した
/// actualTuner / DID / pid / outputPath を保持し、境界停止後の同一Tuner再取得と別TS起動を監査可能にする。
/// worker内ファイル切替や別Tunerへのフォールバックは行わない。
/// </summary>
public sealed class ChainDirectRecorderSession
{
    public int ChainRootReservationId { get; init; }
    public int CurrentReservationId { get; init; }
    public int? NextReservationId { get; private set; }
    public string CurrentServiceName { get; init; } = string.Empty;
    public string CurrentTitle { get; init; } = string.Empty;
    public string? NextServiceName { get; private set; }
    public string? NextTitle { get; private set; }
    public string ActualTunerName { get; init; } = string.Empty;
    public string Did { get; init; } = string.Empty;
    public string BonDriverFileName { get; init; } = string.Empty;
    public int BridgeProcessId { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public DateTime SegmentStartTime { get; init; }
    public DateTime SegmentEndTime { get; init; }
    public DateTime PlannedEndTime { get; init; }
    public DateTime BoundAt { get; init; } = DateTime.Now;

    public bool StopRestartImplemented => true;

    public void AttachNext(Reservation next)
    {
        NextReservationId = next.Id;
        NextServiceName = next.ServiceName;
        NextTitle = next.Title;
    }

    public string ToLogFields(string stage)
        => $"stage={stage} chainRoot=R{ChainRootReservationId} current=R{CurrentReservationId} next={(NextReservationId.HasValue ? $"R{NextReservationId.Value}" : "-")} " +
           $"actualTuner={Safe(ActualTunerName)} did={Safe(Did)} bonDriver={Safe(BonDriverFileName)} pid={BridgeProcessId} " +
           $"outputPath={Safe(OutputPath)} currentService={Safe(CurrentServiceName)} currentTitle={Safe(CurrentTitle)} " +
           $"nextService={Safe(NextServiceName)} nextTitle={Safe(NextTitle)} segmentStart={SegmentStartTime:MM/dd HH:mm:ss} segmentEnd={SegmentEndTime:MM/dd HH:mm:ss} plannedEnd={PlannedEndTime:MM/dd HH:mm:ss} " +
           $"recordingMode=stop_restart separateTsFile=True stopRestartImplemented={StopRestartImplemented}";

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var t = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= 80 ? t : t[..80] + "…";
    }
}
