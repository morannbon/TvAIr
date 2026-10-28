using System.Text.RegularExpressions;
using TvAIr.Core;

namespace TvAIr.Schedule;

internal static class ManualStopOccurrencePolicy
{
    public static bool IsSameOccurrence(Reservation a, Reservation b)
    {
        if (a.NetworkId != b.NetworkId || a.TransportStreamId != b.TransportStreamId || a.ServiceId != b.ServiceId)
            return false;

        if (a.EventId != 0 && b.EventId != 0 && a.EventId == b.EventId)
            return true;

        var exactTime = a.StartTime == b.StartTime && a.EndTime == b.EndTime;
        if (exactTime) return true;

        var overlaps = a.StartTime < b.EndTime && b.StartTime < a.EndTime;
        if (!overlaps) return false;

        return string.Equals(NormalizeTitleForOccurrenceCompare(a.Title), NormalizeTitleForOccurrenceCompare(b.Title), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTitleForOccurrenceCompare(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        return Regex.Replace(title.Trim(), @"\s+", " ");
    }

}
