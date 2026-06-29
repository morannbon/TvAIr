using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>
/// release_contract: チェーン録画本体に入る前のセッション土台。
/// この型はまだDirectRecorderBridgeの継続やファイル切替を実行しない。
/// 共通割り当てルートで成立したチェーンについて、実録画で掴んだ
/// actualTuner / DID / pid / outputPath を後続セグメントへ渡せる形で観測・保持する。
/// release_contract: 実行方式は worker 内ファイル切替ではなく、境界で停止→同一チューナー再取得→別TS起動。ログ上も stop-restart 方針を主語にする。
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
    public string SegmentPlanPath { get; init; } = string.Empty;
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
           $"outputPath={Safe(OutputPath)} segmentPlan={Safe(SegmentPlanPath)} currentService={Safe(CurrentServiceName)} currentTitle={Safe(CurrentTitle)} " +
           $"nextService={Safe(NextServiceName)} nextTitle={Safe(NextTitle)} segmentStart={SegmentStartTime:MM/dd HH:mm:ss} segmentEnd={SegmentEndTime:MM/dd HH:mm:ss} plannedEnd={PlannedEndTime:MM/dd HH:mm:ss} " +
           $"recordingMode=stop_restart separateTsFile=True stopRestartImplemented={StopRestartImplemented}";

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var t = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= 80 ? t : t[..80] + "…";
    }
}
