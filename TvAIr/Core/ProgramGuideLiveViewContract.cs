using TvAIr.Epg.Projection;
namespace TvAIr.Core;
// The clicked programme must still be on air in the Host event source.
internal static class ProgramGuideLiveViewContract
{
    internal static bool IsOnAir(LiveProgramViewerRequest request, ProjectedProgramEvent? current, DateTime now)
        => request.EventId >= 0 && request.EventId <= ushort.MaxValue && current is not null
        && current.NetworkId == request.NetworkId && current.TransportStreamId == request.TransportStreamId
        && current.ServiceId == request.ServiceId && current.EventId == request.EventId
        && !current.IsTimelineFragment && current.Start <= now && now < current.End;
}
