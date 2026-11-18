namespace TvAIr.Channel;

/// <summary>
/// TVTest .ch2 television service eligibility. An explicit service type is
/// required after the mandatory TVTest channel scan. Invalid or absent service
/// types are not inferred from service IDs, EPG data, or tuner routes.
/// </summary>
internal static class Ch2ServiceTypePolicy
{
    internal static bool IsEligible(string? value)
        => int.TryParse(value, out var type) && type is 1 or 161 or 162 or 173;
}
