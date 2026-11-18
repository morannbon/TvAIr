namespace TvAIr.Core;

public static class ChainModeContract
{
    public const string Disabled = "disabled";
    public const string Manual = "manual";
    public const string Automatic = "automatic";

    public static bool IsManualEnabled(bool laterProgramPriority, string? mode)
        => laterProgramPriority && mode == Manual;

    public static string Normalize(string? value, string defaultMode)
        => string.IsNullOrWhiteSpace(value) ? defaultMode : value.Trim().ToLowerInvariant() switch
        {
            Disabled => Disabled,
            Manual => Manual,
            Automatic => Automatic,
            _ => throw new ArgumentException("チェーン予約の方式が不正です。"),
        };
}
