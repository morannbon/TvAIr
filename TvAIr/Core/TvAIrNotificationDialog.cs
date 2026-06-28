using System.Drawing;
using System.Windows.Forms;

namespace TvAIr.Core;

/// <summary>
/// TvAIr共通の無音・短文通知ダイアログ。
/// EPG開始不可、タスクトレイ操作、設定保存など、ユーザー操作結果だけを短く返す用途に限定する。
/// 確認/削除/危険操作/進捗表示には使わない。
/// </summary>
internal static class TvAIrNotificationDialog
{
    private static readonly SettingsThemePalette NotificationPalette = SettingsThemePalette.Light();
    private static readonly Color TitleBackColor = NotificationPalette.Accent;
    private static readonly Color TitleForeColor = NotificationPalette.ButtonPrimaryText;
    private static readonly Color BodyBackColor = NotificationPalette.Panel;
    private static readonly Color BodyForeColor = NotificationPalette.Text;
    private static readonly Color SubForeColor = NotificationPalette.TextSub;
    private static readonly Color BorderColor = NotificationPalette.Border;

    public static void ShowInfo(IWin32Window? owner, string message, string? subMessage = null)
        => Show(owner, message, subMessage);

    public static void ShowError(IWin32Window? owner, string message, string? subMessage = null)
        => Show(owner, message, subMessage);

    public static void Show(string message, string? subMessage = null)
        => Show(null, message, subMessage);

    public static void Show(IWin32Window? owner, string message, string? subMessage = null)
    {
        var main = string.IsNullOrWhiteSpace(message) ? "処理できませんでした" : message.Trim();
        var sub = string.IsNullOrWhiteSpace(subMessage) ? string.Empty : subMessage.Trim();

        using var form = new Form
        {
            Text = "TvAIr",
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(410, 178),
            BackColor = BodyBackColor,
            Font = new Font("Meiryo UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            KeyPreview = true
        };

        var titleBar = new Panel
        {
            Left = 0,
            Top = 0,
            Width = form.ClientSize.Width,
            Height = 34,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            BackColor = TitleBackColor
        };

        var title = new Label
        {
            AutoSize = false,
            Left = 14,
            Top = 0,
            Width = form.ClientSize.Width - 54,
            Height = titleBar.Height,
            Text = "TvAIr",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TitleForeColor,
            BackColor = TitleBackColor,
            Font = new Font(form.Font, FontStyle.Bold)
        };

        var close = new Button
        {
            Text = "×",
            FlatStyle = FlatStyle.Flat,
            Width = 30,
            Height = 30,
            Left = form.ClientSize.Width - 36,
            Top = 2,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            ForeColor = TitleForeColor,
            BackColor = TitleBackColor,
            TabStop = false
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = NotificationPalette.Focus;
        close.FlatAppearance.MouseDownBackColor = NotificationPalette.MenuSelected;

        var messageLabel = new Label
        {
            AutoSize = false,
            Left = 28,
            Top = 62,
            Width = form.ClientSize.Width - 56,
            Height = 22,
            Text = main,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = BodyForeColor,
            BackColor = BodyBackColor,
            Font = new Font(form.Font, FontStyle.Bold)
        };

        var subLabel = new Label
        {
            AutoSize = false,
            Left = 28,
            Top = 90,
            Width = form.ClientSize.Width - 56,
            Height = string.IsNullOrWhiteSpace(sub) ? 0 : 28,
            Text = sub,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SubForeColor,
            BackColor = BodyBackColor
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Width = 96,
            Height = 30,
            Left = form.ClientSize.Width - 122,
            Top = 128,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom
        };

        close.Click += (_, _) => form.DialogResult = DialogResult.OK;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                form.DialogResult = DialogResult.OK;
            }
        };
        form.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawRectangle(pen, 0, 0, form.ClientSize.Width - 1, form.ClientSize.Height - 1);
        };

        titleBar.Controls.Add(title);
        titleBar.Controls.Add(close);
        form.Controls.Add(titleBar);
        form.Controls.Add(messageLabel);
        if (!string.IsNullOrWhiteSpace(sub)) form.Controls.Add(subLabel);
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        form.CancelButton = ok;

        try
        {
            if (owner is null) form.ShowDialog();
            else form.ShowDialog(owner);
        }
        catch
        {
            // 共通通知が失敗した場合に標準MessageBoxへ戻すと、通知UIの横串が崩れる。
            // ここでは例外を飲み、呼び出し元の処理本線を妨げない。
        }
    }
}
