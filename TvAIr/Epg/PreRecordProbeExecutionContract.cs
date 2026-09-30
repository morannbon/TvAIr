namespace TvAIr.Epg;

/// <summary>
/// PreRec 実行の唯一の正本。
/// 親録画予約から一度だけ固定し、Scheduler -> EpgCapture -> TvAIrEpgRec -> Host照合まで
/// 同じ target identity / timing / deadline を引き回す。channels[] は同一TS観測範囲であり、
/// target identity の代替正本にしてはならない。
/// </summary>
public sealed record PreRecordProbeExecutionContract(
    string TargetScope,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort? EventId,
    DateTime ExpectedStartTime,
    DateTime ExpectedEndTime,
    DateTimeOffset HardDeadline,
    string RuntimeTunerName)
{
    public string IdentityText => $"{NetworkId}/{TransportStreamId}/{ServiceId}/{(EventId?.ToString() ?? "-")}";
}
