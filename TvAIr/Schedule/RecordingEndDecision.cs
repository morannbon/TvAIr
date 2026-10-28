using TvAIr.Epg.Shared;
namespace TvAIr.Schedule;

internal enum RecordingProgressStatus { Unknown, Waiting, Present, Suspended, Ended }
internal sealed record RecordingProgressDecision(RecordingProgressStatus Status, DateTimeOffset? End,
    bool Hold, bool Seen, DateTimeOffset LastTargetObservation, string Reason, DateTimeOffset? EndedObservedAt = null);

internal static class RecordingEndDecision
{
    // Freshness and the bounded missing-data hold are product policy, not ARIB normative timers.
    internal static RecordingProgressDecision Evaluate(int networkId,int transportId,int serviceId,int eventId,
        DateTimeOffset start,DateTimeOffset now,RecordingPfSection? present,RecordingPfSection? following,
        RecordingProgressDecision? previous)
    {
        bool Valid(RecordingPfSection? s,int number)=>s is not null && s.NetworkId==networkId
            && s.TransportStreamId==transportId && s.ServiceId==serviceId && s.SectionNumber==number && s.IsFresh(now);
        var p=Valid(present,0)?present:null;var f=Valid(following,1)?following:null;
        var seen=previous?.Seen??false;var last=previous?.LastTargetObservation??default;
        var current=p?.Events.FirstOrDefault(e=>e.EventId==eventId);
        if(current is not null && current.Start.HasValue && current.Start<=now.AddSeconds(5))
            return new(RecordingProgressStatus.Present,current.End,true,true,p!.ObservedAt,
                current.End.HasValue?"target_present":"target_present_duration_undefined");
        var next=f?.Events.FirstOrDefault(e=>e.EventId==eventId);
        if(next is not null)
        {
            var suspended=seen && next.Start.HasValue && next.Start<=now;
            return new(suspended?RecordingProgressStatus.Suspended:RecordingProgressStatus.Waiting,next.End,true,seen,last,
                suspended?"target_in_following_after_start":"target_waiting");
        }
        if(previous?.Status==RecordingProgressStatus.Ended) return previous;
        // A missing packet/section is not an explicit, valid empty section. Both updates must
        // follow the last target observation before disappearance can establish an end.
        if(seen && p is not null && f is not null && p.ObservedAt>last && f.ObservedAt>=p.ObservedAt)
        {
            var successor=p.Events.FirstOrDefault(e=>e.Start>start && e.Start<=now);
            var boundary=successor?.Start??previous?.End;
            if(boundary>now) boundary=null;
            return new(RecordingProgressStatus.Ended,boundary,false,true,last,"target_absent_from_updated_pf",now);
        }
        return new(RecordingProgressStatus.Unknown,null,seen && now-last<=TimeSpan.FromSeconds(30),seen,last,"pf_unavailable_or_incomplete");
    }
}
