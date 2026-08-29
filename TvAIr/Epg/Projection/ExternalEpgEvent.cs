namespace TvAIr.Epg.Projection;

/// <summary>
/// 外部EPGソースから受け取った番組イベント。epg_eventsへ保存する正本ではなく、
/// ProjectedProgramEventへ合成するための入力DTO。
/// </summary>
public sealed class ExternalEpgEvent
{
    public string SourcePluginId { get; init; } = string.Empty;
    public string SourceKind { get; init; } = "TVTestEpgData";
    public string SourceEventKey { get; init; } = string.Empty;

    public ushort NetworkId { get; init; }
    public ushort TransportStreamId { get; init; }
    public ushort ServiceId { get; init; }
    public ushort EventId { get; init; }

    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public int DurationSeconds => End > Start ? (int)(End - Start).TotalSeconds : 0;

    public string ServiceName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string ShortText { get; init; } = string.Empty;
    public string ExtendedText { get; init; } = string.Empty;
    public string ExtendedItems { get; init; } = string.Empty;
    public string Genre { get; init; } = string.Empty;
    public string GenreCodes { get; init; } = string.Empty;

    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}
