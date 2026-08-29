using System.Globalization;
using System.Text.RegularExpressions;

namespace TvAIr.Epg.Projection;

/// <summary>
/// 番組利用層の安定キー。DB上のevent_idだけではなく、将来の外部EPG投影イベントも区別する。
/// </summary>
public sealed record ProjectedEventKey(
    string SourceKind,
    string SourcePluginId,
    string SourceEventKey,
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort EventId,
    DateTime Start,
    DateTime End,
    string TitleKey)
{
    public string Value => string.Join(":",
        Escape(SourceKind),
        Escape(SourcePluginId),
        Escape(SourceEventKey),
        NetworkId.ToString(CultureInfo.InvariantCulture),
        TransportStreamId.ToString(CultureInfo.InvariantCulture),
        ServiceId.ToString(CultureInfo.InvariantCulture),
        EventId.ToString(CultureInfo.InvariantCulture),
        Start.ToString("O", CultureInfo.InvariantCulture),
        End.ToString("O", CultureInfo.InvariantCulture),
        Escape(TitleKey));

    public static ProjectedEventKey FromDb(TvAIr.Core.EpgEvent e)
    {
        var sourceEventKey = $"db:{e.NetworkId}:{e.TransportStreamId}:{e.ServiceId}:{e.EventId}";
        return new ProjectedEventKey(
            ProjectedEventSourceKinds.TvAirDb,
            string.Empty,
            sourceEventKey,
            e.NetworkId,
            e.TransportStreamId,
            e.ServiceId,
            e.EventId,
            e.Start,
            e.End,
            NormalizeTitleKey(e.Title));
    }


    public static ProjectedEventKey FromExternal(ExternalEpgEvent e)
    {
        return new ProjectedEventKey(
            ProjectedEventSourceKinds.ExternalEpg,
            e.SourcePluginId ?? string.Empty,
            e.SourceEventKey ?? string.Empty,
            e.NetworkId,
            e.TransportStreamId,
            e.ServiceId,
            e.EventId,
            e.Start,
            e.End,
            NormalizeTitleKey(e.Title));
    }

    public static bool TryParse(string? value, out ProjectedEventKey key)
    {
        key = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Regex.Match(value,
            @"^(?<sourceKind>[^:]*):(?<plugin>[^:]*):(?<sourceKey>[^:]*):(?<nid>\d+):(?<tsid>\d+):(?<sid>\d+):(?<eid>\d+):(?<start>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}(?:Z|[+-]\d{2}:\d{2})?):(?<end>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}(?:Z|[+-]\d{2}:\d{2})?):(?<title>.*)$",
            RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        try
        {
            if (!ushort.TryParse(match.Groups["nid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nid)) return false;
            if (!ushort.TryParse(match.Groups["tsid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tsid)) return false;
            if (!ushort.TryParse(match.Groups["sid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid)) return false;
            if (!ushort.TryParse(match.Groups["eid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eid)) return false;
            if (!DateTime.TryParse(match.Groups["start"].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start)) return false;
            if (!DateTime.TryParse(match.Groups["end"].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end)) return false;
            key = new ProjectedEventKey(
                Uri.UnescapeDataString(match.Groups["sourceKind"].Value),
                Uri.UnescapeDataString(match.Groups["plugin"].Value),
                Uri.UnescapeDataString(match.Groups["sourceKey"].Value),
                nid, tsid, sid, eid, start, end, Uri.UnescapeDataString(match.Groups["title"].Value));
            return end > start;
        }
        catch
        {
            key = null!;
            return false;
        }
    }

    private static string NormalizeTitleKey(string? value)
        => (value ?? string.Empty).Trim();

    private static string Escape(string? value)
        => Uri.EscapeDataString(value ?? string.Empty);
}

public static class ProjectedEventSourceKinds
{
    public const string TvAirDb = "TvAirDb";
    public const string ExternalEpg = "ExternalEpg";
}

public static class ProjectedEventStates
{
    public const string DbOnly = "DbOnly";
    public const string DbWithOverlay = "DbWithOverlay";
    public const string OverlayOnly = "OverlayOnly";
}
