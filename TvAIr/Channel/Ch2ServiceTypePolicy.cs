namespace TvAIr.Channel;

/// <summary>
/// TVTest .ch2 service type eligibility. Older channel scans may leave the
/// type column empty even for an enabled, valid television service. Preserve
/// such services without guessing a concrete ARIB service type. Explicit
/// non-television type values remain excluded.
/// </summary>
internal static class Ch2ServiceTypePolicy
{
    internal static bool IsEligible(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return int.TryParse(value.Trim(), out var type) && type is 1 or 161 or 162 or 173;
    }
}
