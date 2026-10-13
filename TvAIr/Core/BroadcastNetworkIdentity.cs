namespace TvAIr.Core;

/// <summary>
/// Identify only broadcast waves that are explicitly recognized by the host.
/// Physical tuner capability (HYBRID) and BonDriver tuning coordinates are not
/// broadcast identities. In particular, an unknown ISDB-C network must not be
/// inferred as terrestrial merely because its ONID is nonzero.
/// </summary>
internal static class BroadcastNetworkIdentity
{
    /// <summary>
    /// Display classification shared by channel APIs and programme-guide ordering.
    /// Mirrors the browser's resolveProgramGuideWaveGroup; it is not tuner capability.
    /// </summary>
    internal static string ResolveDisplayWave(string? group, ushort originalNetworkId)
    {
        var value = (group ?? string.Empty).Trim().ToUpperInvariant();
        if (value is "GR" or "地上波" or "TERRESTRIAL") return "GR";
        if (value == "BS") return "BS";
        if (value == "CS") return "CS";
        if (value is "BSCS" or "BS/CS" or "S" or "SATELLITE")
            return originalNetworkId == 4 ? "BS" : "CS";
        if (value is "HYBRID" or "CATV") return "CATV";
        return ResolveLegacyChannelGroup("CATV", originalNetworkId);
    }

    internal static bool TryResolveWave(ushort originalNetworkId, out string wave)
    {
        if (originalNetworkId is 4 or 6 or 7 or 10)
        {
            wave = "BSCS";
            return true;
        }
        if (originalNetworkId is >= 0x7880 and <= 0x7FE8)
        {
            wave = "GR";
            return true;
        }
        wave = string.Empty;
        return false;
    }

    internal static string ResolveLegacyChannelGroup(string fileGroup, ushort originalNetworkId)
    {
        if (originalNetworkId == 4) return "BS";
        if (originalNetworkId is 6 or 7 or 10) return "CS";
        if (TryResolveWave(originalNetworkId, out var wave) && wave == "GR") return "GR";
        // Preserve explicit legacy .ch2 input-group fallback; this is not evidence
        // that a physically HYBRID tuner can receive an unknown broadcast network.
        return fileGroup;
    }
}
