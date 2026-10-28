using System.Text.Json.Serialization;
namespace TvAIr.Epg.Shared;

// Immutable, service-scoped actual EIT p/f observation. Empty and unreceived are distinct.
public sealed record RecordingPfEvent(int EventId, DateTimeOffset? Start, DateTimeOffset? End);
public sealed record RecordingPfSection(int NetworkId, int TransportStreamId, int ServiceId,
    int SectionNumber, int Version, DateTimeOffset ObservedAt, RecordingPfEvent[] Events)
{
    public bool IsFresh(DateTimeOffset now) => ObservedAt <= now.AddSeconds(5) && now - ObservedAt <= TimeSpan.FromSeconds(30);
    public static RecordingPfSection? Parse(ReadOnlySpan<byte> section, bool crcValid, DateTimeOffset observedAt)
    {
        if (!crcValid || section.Length < 18 || section[0] != 0x4E || (section[5] & 1) == 0 || section[6] > 1) return null;
        var length = 3 + ((section[1] & 15) << 8 | section[2]);
        if (length != section.Length) return null;
        var end = length - 4; var pos = 14; var events = new List<RecordingPfEvent>();
        while (pos < end)
        {
            if (pos + 12 > end) return null;
            var next = pos + 12 + ((section[pos + 10] & 15) << 8 | section[pos + 11]);
            if (next > end) return null;
            var time = section.Slice(pos + 2, 5); var duration = section.Slice(pos + 7, 3);
            DateTimeOffset? start = null; DateTimeOffset? finish = null;
            var unknownStart = AllOnes(time); var unknownDuration = AllOnes(duration);
            if (!unknownStart)
            {
                var h = Bcd(time[2]); var m = Bcd(time[3]); var sec = Bcd(time[4]);
                if (h < 0 || h > 23 || m < 0 || m > 59 || sec < 0 || sec > 59) return null;
                var date = new DateTime(1858,11,17).AddDays((time[0] << 8) | time[1]);
                start = new DateTimeOffset(date.Year,date.Month,date.Day,h,m,sec,TimeSpan.FromHours(9));
            }
            if (!unknownDuration)
            {
                var h=Bcd(duration[0]);var m=Bcd(duration[1]);var sec=Bcd(duration[2]);
                if (h < 0 || m < 0 || m > 59 || sec < 0 || sec > 59) return null;
                if(start.HasValue) finish=start.Value.AddSeconds(h*3600+m*60+sec);
            }
            events.Add(new RecordingPfEvent((section[pos]<<8)|section[pos+1],start,finish));pos=next;
        }
        return new RecordingPfSection((section[10]<<8)|section[11],(section[8]<<8)|section[9],
            (section[3]<<8)|section[4],section[6],(section[5]>>1)&31,observedAt,events.ToArray());
    }
    private static bool AllOnes(ReadOnlySpan<byte> bytes){foreach(var b in bytes)if(b!=255)return false;return true;}
    private static int Bcd(byte b)=> (b>>4)>9 || (b&15)>9 ? -1 : (b>>4)*10+(b&15);
}
