using System.Drawing;

namespace TvAIr.Core;

/// <summary>
/// WinFormsで表示する共通ダイアログのテーマ値。
/// 設定画面の実装には所属せず、ライト／ダークで同じ意味の色だけを切り替える。
/// </summary>
internal sealed class NativeDialogThemePalette
{
    public Color Panel { get; private init; }
    public Color Border { get; private init; }
    public Color Text { get; private init; }
    public Color TextSub { get; private init; }
    public Color Accent { get; private init; }
    public Color ButtonPrimary { get; private init; }
    public Color ButtonPrimaryText { get; private init; }
    public Color ButtonSecondary { get; private init; }
    public Color Focus { get; private init; }
    public Color MenuSelected { get; private init; }

    public static NativeDialogThemePalette Light() => FromTheme("light");

    public static NativeDialogThemePalette Dark() => FromTheme("dark");

    private static NativeDialogThemePalette FromTheme(string theme) => new()
    {
        Panel = FromRole(theme, "--tvair-native-dialog-panel"),
        Border = FromRole(theme, "--tvair-native-dialog-border"),
        Text = FromRole(theme, "--tvair-native-dialog-text"),
        TextSub = FromRole(theme, "--tvair-native-dialog-text-sub"),
        Accent = FromRole(theme, "--tvair-native-dialog-accent"),
        ButtonPrimary = FromRole(theme, "--tvair-native-dialog-button-primary"),
        ButtonPrimaryText = FromRole(theme, "--tvair-native-dialog-button-primary-text"),
        ButtonSecondary = FromRole(theme, "--tvair-native-dialog-button-secondary"),
        Focus = FromRole(theme, "--tvair-native-dialog-focus"),
        MenuSelected = FromRole(theme, "--tvair-native-dialog-menu-selected"),
    };

    private static Color FromRole(string theme, string token)
        => ColorTranslator.FromHtml(UiThemeRoleContract.Get(theme, token));

    public static NativeDialogThemePalette Resolve(string? requestedTheme)
        => HostThemeStateContract.ResolveEffective(requestedTheme) == "dark" ? Dark() : Light();
}
