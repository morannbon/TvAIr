using TvAIr.Core;

namespace TvAIr.Epg.Projection;

public static class ProjectedProgramEventExtensions
{
    public static EpgEvent ToEpgEvent(this ProjectedProgramEvent source)
    {
        if (source.DbEvent is { } db)
        {
            return new EpgEvent
            {
                NetworkId = source.NetworkId,
                TransportStreamId = source.TransportStreamId,
                ServiceId = source.ServiceId,
                EventId = source.EventId,
                ServiceName = source.ServiceName,
                Title = source.Title,
                Description = source.ShortText,
                Genre = source.Genre,
                GenreCodes = source.GenreCodes,
                TableId = db.TableId,
                SectionNumber = db.SectionNumber,
                VersionNumber = db.VersionNumber,
                RawDescriptorLoopHex = db.RawDescriptorLoopHex,
                RawShortEventDescriptorHex = db.RawShortEventDescriptorHex,
                RawExtendedEventDescriptorHex = db.RawExtendedEventDescriptorHex,
                RawContentDescriptorHex = db.RawContentDescriptorHex,
                DurationSeconds = source.DurationSeconds,
                Start = source.Start,
                End = source.End,
                UpdatedAt = db.UpdatedAt
            };
        }

        return new EpgEvent
        {
            NetworkId = source.NetworkId,
            TransportStreamId = source.TransportStreamId,
            ServiceId = source.ServiceId,
            EventId = source.EventId,
            ServiceName = source.ServiceName,
            Title = source.Title,
            Description = source.ShortText,
            Genre = source.Genre,
            GenreCodes = source.GenreCodes,
            DurationSeconds = source.DurationSeconds,
            Start = source.Start,
            End = source.End,
            UpdatedAt = DateTime.Now
        };
    }
}
