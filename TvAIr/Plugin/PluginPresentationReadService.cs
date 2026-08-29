using TvAIr.Core;

namespace TvAIr.Plugin;

internal sealed record PluginThemeSnapshot(
    string Appearance,
    string AccentColor,
    string CssScopeRoot);

/// <summary>
/// Runtime capability APIs share this authoritative source for host presentation settings.
/// </summary>
internal sealed class PluginPresentationReadService
{
    private readonly IniSettingsService _ini;

    public PluginPresentationReadService(IniSettingsService ini)
    {
        _ini = ini;
    }

    public PluginThemeSnapshot GetTheme()
        => new(
            Appearance: _ini.SystemTheme,
            AccentColor: string.Empty,
            CssScopeRoot: "tvair");
}
