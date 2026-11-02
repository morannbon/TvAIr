using TvAIr.Core;

namespace TvAIr.Tuner;

/// <summary>
/// One tuner-profile normalization path for saved rows, startup fallback,
/// tuner pool allocation, and viewer selection. Ordered topology is preserved.
/// </summary>
public static class TunerRuntimeProfileSource
{
    public static IReadOnlyList<TunerProfile> Build(
        IniSettingsService ini,
        IReadOnlyList<TunerProfile>? fallback)
    {
        var saved = ini?.GetEffectiveTuners();
        return saved is { Count: > 0 }
            ? FromPersistedRows(saved)
            : fallback ?? Array.Empty<TunerProfile>();
    }

    /// <summary>Apply exactly the same normalization to every saved tuner row.</summary>
    public static List<TunerProfile> FromPersistedRows(IEnumerable<TunerProfileDto> rows)
        => rows.Select(t => Create(
                t.Name, t.BonDriverFileName, t.ChannelCh2Path, t.Group, t.Did,
                t.Role, t.DeviceNumber, t.LogicalViewerSlotId))
            .Where(t => !string.IsNullOrWhiteSpace(t.BonDriverFileName))
            .ToList();

    /// <summary>Normalize explicit appsettings fallback only at startup.</summary>
    public static List<TunerProfile> FromStartupFallback(IEnumerable<TunerProfile> rows)
        => rows.Select(t => Create(
                t.Name, t.BonDriverFileName, t.ChannelCh2Path, t.Group, t.Did,
                t.Role, t.DeviceNumber, t.LogicalViewerSlotId))
            .Where(t => !string.IsNullOrWhiteSpace(t.BonDriverFileName))
            .ToList();

    private static TunerProfile Create(
        string? name, string? bonDriver, string? ch2Path, string? group,
        string? did, string? role, int deviceNumber, string? logicalViewerSlotId)
    {
        var normalizedGroup = TunerDisplayName.NormalizeGroup(group);
        var normalizedDid = (did ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedRole = IniSettingsService.NormalizeTunerRole(role);
        return new TunerProfile
        {
            Name = TunerDisplayName.ForUi(name, normalizedGroup, normalizedDid),
            BonDriverFileName = TunerIsolationPolicy.NormalizeBonDriverForRole(
                bonDriver, normalizedGroup, normalizedRole),
            ChannelCh2Path = (ch2Path ?? string.Empty).Trim(),
            Group = normalizedGroup,
            Did = normalizedDid,
            Role = normalizedRole,
            DeviceNumber = deviceNumber,
            LogicalViewerSlotId = (logicalViewerSlotId ?? string.Empty).Trim(),
        };
    }
}
