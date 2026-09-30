using Microsoft.Win32;

namespace TvAIr.Core;

/// <summary>
/// Host-owned theme state resolver.
/// SelectedTheme is persisted by IniSettingsService; EffectiveTheme resolution is owned here.
/// Web pages, Runtime Plugin UI, ToolWindows and native projections must not maintain another
/// Windows-theme resolver or another selected/effective state machine.
/// </summary>
internal static class HostThemeStateContract
{
    public static string NormalizeSelected(string? value)
        => IniSettingsService.NormalizeSystemTheme(value);

    public static string ResolveEffective(string? selectedTheme)
    {
        var selected = NormalizeSelected(selectedTheme);
        if (string.Equals(selected, "dark", StringComparison.OrdinalIgnoreCase)) return "dark";
        if (string.Equals(selected, "light", StringComparison.OrdinalIgnoreCase)) return "light";
        return ResolveWindowsEffectiveTheme();
    }

    public static string ResolveWindowsEffectiveTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            var light = value is int i ? i != 0 : value?.ToString() != "0";
            return light ? "light" : "dark";
        }
        catch
        {
            return "light";
        }
    }
}
