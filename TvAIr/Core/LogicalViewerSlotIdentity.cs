using System;
using System.Linq;

namespace TvAIr.Core;

internal static class LogicalViewerSlotIdentity
{
    public static string Normalize(string? value)
    {
        var raw = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.StartsWith("slot-", StringComparison.Ordinal)) raw = raw[5..];
        return new string(raw.Where(ch => char.IsLetterOrDigit(ch) || ch == '-').ToArray()).Trim('-');
    }

    public static string Resolve(
        string? value,
        string? group,
        string? did,
        string? role,
        int rowIndex = 0)
    {
        var normalized = Normalize(value);
        var deterministic = BuildDeterministic(group, did, role, rowIndex);

        // Previous builds generated bare 32-character hexadecimal GUIDs for missing IDs.
        // Migrate only that exact legacy shape when a complete deterministic topology exists.
        if (!string.IsNullOrWhiteSpace(normalized) &&
            !(IsLegacyGeneratedGuid(normalized) && !deterministic.StartsWith("tuner-", StringComparison.Ordinal)))
        {
            return normalized;
        }

        return deterministic;
    }

    private static string BuildDeterministic(string? group, string? did, string? role, int rowIndex)
    {
        var stableGroup = TunerDisplayName.NormalizeGroup(group).ToLowerInvariant();
        var stableRole = IniSettingsService.NormalizeTunerRole(role).ToLowerInvariant();
        var stableDid = Normalize(did);
        if (!string.IsNullOrWhiteSpace(stableGroup) &&
            !string.IsNullOrWhiteSpace(stableRole) &&
            !string.IsNullOrWhiteSpace(stableDid))
        {
            return $"{stableGroup}-{stableRole}-{stableDid}";
        }

        return rowIndex > 0 ? $"tuner-row-{rowIndex}" : "tuner-unresolved";
    }

    private static bool IsLegacyGeneratedGuid(string value)
        => value.Length == 32 && value.All(Uri.IsHexDigit);
}
