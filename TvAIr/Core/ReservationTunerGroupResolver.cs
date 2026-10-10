using System.Text.RegularExpressions;

namespace TvAIr.Core;

/// <summary>
/// Reservation の放送波グループを、保存済みの正規チャンネル／チューナー Identity から解決する。
/// 番組タイトル、局名、表示文言は判定材料にしない。
/// </summary>
public static class ReservationTunerGroupResolver
{
    public const string Gr = "GR";
    public const string Bscs = "BSCS";
    public const string Unknown = "UNKNOWN";

    private static readonly Regex ChTokenRegex = new(
        @"(?:^|\s)/ch(?:\s+|=)(?<value>-?\d+)(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Resolve(Reservation reservation, IReadOnlyList<TunerProfile>? tunerProfiles = null)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        if (TryResolveFromNetworkId(reservation.NetworkId, out var networkGroup))
            return networkGroup;

        if (TryResolveFromAssignedTuner(reservation, tunerProfiles, out var tunerGroup))
            return tunerGroup;

        // /ch は地上波用の正規引数。/chspace は GR/BS/CS のいずれにも現れ得るため、
        // その存在や値だけでは判定しない。
        if (!string.IsNullOrWhiteSpace(reservation.ChannelArgument)
            && ChTokenRegex.IsMatch(reservation.ChannelArgument))
        {
            return Gr;
        }

        return Unknown;
    }

    public static bool TryResolveFromNetworkId(ushort networkId, out string group)
    {
        if (BroadcastNetworkIdentity.TryResolveWave(networkId, out group))
            return true;

        group = Unknown;
        return false;
    }

    private static bool TryResolveFromAssignedTuner(
        Reservation reservation,
        IReadOnlyList<TunerProfile>? tunerProfiles,
        out string group)
    {
        group = Unknown;
        if (tunerProfiles is null || tunerProfiles.Count == 0)
            return false;

        foreach (var tunerName in new[] { reservation.ActualTunerName, reservation.TunerName })
        {
            if (string.IsNullOrWhiteSpace(tunerName))
                continue;

            var profile = tunerProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, tunerName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
                continue;

            var normalized = (profile.Group ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized is Gr or Bscs or "HYBRID")
            {
                group = normalized;
                return true;
            }

            // Unrecognized physical groups do not establish a broadcast identity.
            return false;
        }

        return false;
    }
}
