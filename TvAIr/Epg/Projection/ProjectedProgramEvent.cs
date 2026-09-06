using TvAIr.Core;

namespace TvAIr.Epg.Projection;

/// <summary>
/// DB読取後の番組利用用イベント。保存正本ではなく、番組表・検索・予約が参照する投影結果。
/// 第1段階ではDB由来だけを通し、外部EPGはまだ混ぜない。
/// </summary>
public sealed class ProjectedProgramEvent
{
    private string? _projectedEventId;

    public ProjectedEventKey Key { get; init; } = null!;
    // ProgramGuide/予約契約で使う安定IDはKeyの不変内容から一度だけ文字列化する。
    // 同一snapshot内の再表示ごとにUri escaping/日時文字列化を繰り返さない。
    public string ProjectedEventId
    {
        get => _projectedEventId ??= Key.Value;
        init => _projectedEventId = value;
    }
    public string ProjectionState { get; init; } = ProjectedEventStates.DbOnly;

    public ushort NetworkId { get; init; }
    public ushort TransportStreamId { get; init; }
    public ushort ServiceId { get; init; }
    public ushort EventId { get; init; }

    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public int DurationSeconds { get; init; }

    // Timeline composition may expose an uncovered segment of one external occurrence.
    // Start/End are the actionable interval; CanonicalStart/CanonicalEnd retain source provenance.
    public DateTime CanonicalStart { get; init; }
    public DateTime CanonicalEnd { get; init; }
    public bool IsTimelineFragment { get; init; }
    public string TimelineFragmentReason { get; init; } = string.Empty;

    public string ServiceName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string ShortText { get; init; } = string.Empty;
    public string ExtendedText { get; init; } = string.Empty;
    public string ExtendedItems { get; init; } = string.Empty;
    public DateTime UpdatedAt { get; init; }
    public string CellText { get; init; } = string.Empty;
    public string Genre { get; init; } = string.Empty;
    public string GenreCodes { get; init; } = string.Empty;

    public bool DbEventExists { get; init; }
    public EpgEvent? DbEvent { get; init; }

    public string SourceKind { get; init; } = ProjectedEventSourceKinds.TvAirDb;
    public string SourcePluginId { get; init; } = string.Empty;
    public string SourceEventKey { get; init; } = string.Empty;

    public bool ProjectionTitleDbPresent { get; init; }
    public bool ProjectionTitleOverlayCandidatePresent { get; init; }
    public string ProjectionTitleSource { get; init; } = string.Empty;

    public bool ProjectionOutlineDbPresent { get; init; }
    public bool ProjectionOutlineOverlayCandidatePresent { get; init; }
    public string ProjectionOutlineSource { get; init; } = string.Empty;

    public bool ProjectionDetailDbPresent { get; init; }
    public bool ProjectionDetailOverlayCandidatePresent { get; init; }
    public string ProjectionDetailSource { get; init; } = string.Empty;
}
