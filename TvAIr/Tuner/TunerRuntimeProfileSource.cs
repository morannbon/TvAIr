using TvAIr.Core;

namespace TvAIr.Tuner;

/// <summary>
/// Runtime tuner topology source shared by the tuner pool and viewer profile projection.
/// Saved INI tuner rows are authoritative when present; startup fallback profiles are used only when no saved rows exist.
/// </summary>
public static class TunerRuntimeProfileSource
{
    public static IReadOnlyList<TunerProfile> Build(
        IniSettingsService ini,
        IReadOnlyList<TunerProfile>? fallback)
    {
        if (ini?.Tuners is { Count: > 0 })
        {
            return ini.Tuners
                .Select(t =>
                {
                    var group = TunerDisplayName.NormalizeGroup(t.Group);
                    var role = IniSettingsService.NormalizeTunerRole(t.Role);
                    var did = (t.Did ?? string.Empty).Trim();
                    return new TunerProfile
                    {
                        Name = TunerDisplayName.ForUi(t.Name, group, did),
                        BonDriverFileName = TunerIsolationPolicy.NormalizeBonDriverForRole(t.BonDriverFileName, group, role),
                        Group = group,
                        Did = did,
                        Role = role,
                        DeviceNumber = t.DeviceNumber,
                        LogicalViewerSlotId = (t.LogicalViewerSlotId ?? string.Empty).Trim(),
                    };
                })
                .Where(t => !string.IsNullOrWhiteSpace(t.BonDriverFileName))
                .ToList();
        }

        return fallback ?? Array.Empty<TunerProfile>();
    }
}
