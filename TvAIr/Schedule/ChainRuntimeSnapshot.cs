using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>
/// release_contract: 稼働中チェーン実行セッションの読み取り専用監査投影。
/// runtime正本はReservationScheduler.RecordingSession / TunerPool lease / ChainControlStateであり、このsnapshotから実行状態を逆生成しない。
/// 計画段階のTuner候補ではなく、shared capture開始後に確定した actualTuner / DID / pid / lease identity / outputPath を追跡する。
/// continuousCapture=True の明示チェーンでは同じBonDriver/OpenTuner/TS-read workerを物理終端まで維持し、予約ごとのSinkへ重複出力する。
/// 論理境界では物理Tunerを解放せず、BonDriver Close/retune/worker restartも行わず、論理Reservation ownerだけを後続へ移す。
/// shared capture未昇格時も同一worker内でmulti-sinkへ昇格する。後続削除時の物理終端はcanonical segment集合から短縮可能である。
/// </summary>
public sealed class ChainRuntimeSnapshot
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
    public int WorkerProcessId { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public DateTime SegmentStartTime { get; init; }
    public DateTime SegmentEndTime { get; init; }
    // Logical segment end and physical shared-capture end are deliberately separate.
    public DateTime PlannedEndTime { get; init; }
    public DateTime PhysicalCaptureEndTime { get; init; }
    public DateTime BoundAt { get; init; } = DateTime.Now;

    public bool ContinuousCapture { get; init; }

    public void AttachNext(Reservation next)
    {
        NextReservationId = next.Id;
        NextServiceName = next.ServiceName;
        NextTitle = next.Title;
    }

    public string ToLogFields(string stage)
        => $"stage={stage} chainRoot=R{ChainRootReservationId} current=R{CurrentReservationId} next={(NextReservationId.HasValue ? $"R{NextReservationId.Value}" : "-")} " +
           $"actualTuner={Safe(ActualTunerName)} did={Safe(Did)} bonDriver={Safe(BonDriverFileName)} pid={WorkerProcessId} " +
           $"outputPath={Safe(OutputPath)} currentService={Safe(CurrentServiceName)} currentTitle={Safe(CurrentTitle)} " +
           $"nextService={Safe(NextServiceName)} nextTitle={Safe(NextTitle)} segmentStart={SegmentStartTime:MM/dd HH:mm:ss} segmentEnd={SegmentEndTime:MM/dd HH:mm:ss} logicalSegmentEnd={PlannedEndTime:MM/dd HH:mm:ss} physicalCaptureEnd={PhysicalCaptureEndTime:MM/dd HH:mm:ss} " +
           $"recordingMode={(ContinuousCapture ? "continuous_capture_multi_sink" : "single_sink_pre_upgrade")} separateTsFile=True continuousCapture={ContinuousCapture}";

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var t = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= 80 ? t : t[..80] + "…";
    }
}
