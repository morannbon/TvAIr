using TvAIr.Core;

namespace TvAIr.Epg.Projection;

/// <summary>
/// 番組表・検索・予約が参照する番組イベント入口。
/// 第1段階ではDB由来だけを返し、後段で外部EPG投影を統合する。
/// </summary>
public interface IProgramEventSource
{
    IReadOnlyList<ProjectedProgramEvent> ProjectCommittedDbEvents(IReadOnlyList<EpgEvent> committedEvents);
    IReadOnlyList<ProjectedProgramEvent> GetAll();
    IReadOnlyList<ProjectedProgramEvent> GetByRange(DateTime from, DateTime to);
    ProjectedProgramEvent? GetByEventKey(ushort networkId, ushort transportStreamId, ushort serviceId, ushort eventId);
    ProjectedProgramEvent? GetByProjectedKey(ProjectedEventKey key);
}
