using System.Globalization;

namespace TvAIr.Tuner;

/// <summary>
/// The single TVTest viewer command-line projection. The recording/EPG worker's
/// /chspace /chi route is kept unchanged at its own boundary.
/// TVTest /ch is a physical channel; /chi is a channel-table index. They are not interchangeable.
/// </summary>
internal static class TvTestViewerChannelArgument
{
    internal static string Build(string? channelArgument, ushort? authoritativeServiceId)
    {
        var tokens = (channelArgument ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!tokens[i].StartsWith("/", StringComparison.Ordinal))
                continue;
            if (tokens[i + 1].StartsWith("/", StringComparison.Ordinal))
                continue;
            if (!options.ContainsKey(tokens[i]))
                options.Add(tokens[i], tokens[i + 1]);
            i++;
        }

        var sid = authoritativeServiceId is > 0
            ? authoritativeServiceId.Value
            : ParseUshort(options, "/sid");
        var sidArg = sid > 0 ? $" /sid {sid}" : string.Empty;

        var space = ParseNonnegativeInt(options, "/chspace");
        var index = ParseNonnegativeInt(options, "/chi");
        if (space.HasValue && index.HasValue)
            return $"/chspace {space.Value} /chi {index.Value}{sidArg}";

        // TVTest's /ch takes a physical channel number, not an index.
        var physical = ParsePositiveInt(options, "/ch");
        if (physical.HasValue)
            return $"/ch {physical.Value}{sidArg}";

        // Identity-only channel selection is supported by stock TVTest and is
        // useful for callers that have no physical channel or channel-table index.
        var networkId = ParseUshort(options, "/nid");
        var transportStreamId = ParseUshort(options, "/tsid");
        if (networkId > 0 && transportStreamId > 0 && sid > 0)
            return $"/nid {networkId} /tsid {transportStreamId} /sid {sid}";

        // Never start a viewer on whichever station TVTest previously used.
        return string.Empty;
    }

    private static ushort ParseUshort(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value)
            && ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result : (ushort)0;

    private static int? ParseNonnegativeInt(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result : null;

    private static int? ParsePositiveInt(IReadOnlyDictionary<string, string> options, string key)
    {
        var result = ParseNonnegativeInt(options, key);
        return result is > 0 ? result : null;
    }
}
