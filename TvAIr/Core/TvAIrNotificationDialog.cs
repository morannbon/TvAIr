using System.Drawing;
using System.Windows.Forms;

namespace TvAIr.Core;


internal enum PowerActionCountdownDecision
{
    Elapsed,
    ExecuteNow,
    Cancel,
    Closed,
}

/// <summary>
/// TvAIr共通の無音・短文通知ダイアログ。
/// EPG開始不可、タスクトレイ操作、設定保存など、ユーザー操作結果だけを短く返す用途に限定する。
/// 確認/削除/危険操作/進捗表示には使わない。
/// </summary>
internal static class TvAIrNotificationDialog
{
    public static void ShowInfo(IWin32Window? owner, string message, string? subMessage = null, string? systemTheme = null)
        => Show(owner, message, subMessage, systemTheme);

    public static void ShowError(IWin32Window? owner, string message, string? subMessage = null, string? systemTheme = null)
        => Show(owner, message, subMessage, systemTheme);

    public static void Show(string message, string? subMessage = null, string? systemTheme = null)
        => Show(null, message, subMessage, systemTheme);

    public static Task<PowerActionCountdownDecision> ShowPowerActionCountdownAsync(
        string action,
        TimeSpan remaining,
        string? systemTheme,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<PowerActionCountdownDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = CreatePowerActionCountdownForm(action, remaining, systemTheme, tcs, cancellationToken);
                Application.Run(form);
                if (!tcs.Task.IsCompleted)
                    tcs.TrySetResult(PowerActionCountdownDecision.Closed);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "TvAIr recording-after-action notification"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static Form CreatePowerActionCountdownForm(
        string action,
        TimeSpan remaining,
        string? systemTheme,
        TaskCompletionSource<PowerActionCountdownDecision> completion,
        CancellationToken cancellationToken)
    {
        var palette = NativeDialogThemePalette.Resolve(systemTheme);
        var isShutdown = string.Equals(action, "shutdown", StringComparison.OrdinalIgnoreCase);
        var actionText = isShutdown ? "シャットダウン" : "スリープ";
        var deadline = DateTime.Now.Add(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);

        var form = new Form
        {
            Text = "TvAIr",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = true,
            ClientSize = new Size(430, 178),
            BackColor = palette.Panel,
            Font = new Font("Meiryo UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            TopMost = true,
        };

        var message = new Label
        {
            AutoSize = false,
            Left = 26,
            Top = 24,
            Width = 378,
            Height = 30,
            Text = $"まもなく{actionText}します。",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = palette.Text,
            BackColor = palette.Panel,
            Font = new Font(form.Font, FontStyle.Bold),
        };
        var countdown = new Label
        {
            AutoSize = false,
            Left = 26,
            Top = 60,
            Width = 378,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = palette.TextSub,
            BackColor = palette.Panel,
        };
        var cancel = new Button
        {
            Text = "中止",
            Width = 104,
            Height = 32,
            Left = 188,
            Top = 116,
            DialogResult = DialogResult.Cancel,
            BackColor = palette.ButtonSecondary,
            ForeColor = palette.Text,
            FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = palette.Border;
        var execute = new Button
        {
            Text = "今すぐ実行",
            Width = 112,
            Height = 32,
            Left = 304,
            Top = 116,
            BackColor = palette.ButtonPrimary,
            ForeColor = palette.ButtonPrimaryText,
            FlatStyle = FlatStyle.Flat,
        };
        execute.FlatAppearance.BorderColor = palette.Border;

        var decided = false;
        void Finish(PowerActionCountdownDecision decision)
        {
            if (decided) return;
            decided = true;
            completion.TrySetResult(decision);
            if (!form.IsDisposed) form.Close();
        }

        cancel.Click += (_, _) => Finish(PowerActionCountdownDecision.Cancel);
        execute.Click += (_, _) => Finish(PowerActionCountdownDecision.ExecuteNow);
        form.FormClosing += (_, e) =>
        {
            if (decided) return;
            e.Cancel = true;
            Finish(PowerActionCountdownDecision.Closed);
        };

        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            var left = deadline - DateTime.Now;
            if (left <= TimeSpan.Zero)
            {
                timer.Stop();
                Finish(PowerActionCountdownDecision.Elapsed);
                return;
            }
            var seconds = Math.Max(1, (int)Math.Ceiling(left.TotalSeconds));
            countdown.Text = $"{seconds}秒後に実行します。";
        };
        form.Shown += (_, _) =>
        {
            var left = deadline - DateTime.Now;
            countdown.Text = $"{Math.Max(1, (int)Math.Ceiling(left.TotalSeconds))}秒後に実行します。";
            timer.Start();
        };
        form.FormClosed += (_, _) => timer.Dispose();

        void CloseForCancellation()
        {
            try
            {
                if (form.IsDisposed) return;
                if (form.IsHandleCreated)
                    form.BeginInvoke(new Action(() => Finish(PowerActionCountdownDecision.Closed)));
            }
            catch
            {
                completion.TrySetResult(PowerActionCountdownDecision.Closed);
            }
        }

        form.HandleCreated += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
                Finish(PowerActionCountdownDecision.Closed);
        };

        var registration = cancellationToken.Register(() =>
        {
            completion.TrySetResult(PowerActionCountdownDecision.Closed);
            CloseForCancellation();
        });
        form.FormClosed += (_, _) => registration.Dispose();

        form.Controls.Add(message);
        form.Controls.Add(countdown);
        form.Controls.Add(cancel);
        form.Controls.Add(execute);
        form.AcceptButton = execute;
        form.CancelButton = cancel;
        return form;
    }

    public static void Show(IWin32Window? owner, string message, string? subMessage = null, string? systemTheme = null)
    {
        var palette = NativeDialogThemePalette.Resolve(systemTheme);
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
            BackColor = palette.Panel,
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
            BackColor = palette.Accent
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
            ForeColor = palette.ButtonPrimaryText,
            BackColor = palette.Accent,
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
            ForeColor = palette.ButtonPrimaryText,
            BackColor = palette.Accent,
            TabStop = false
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = palette.Focus;
        close.FlatAppearance.MouseDownBackColor = palette.MenuSelected;

        var messageLabel = new Label
        {
            AutoSize = false,
            Left = 28,
            Top = 62,
            Width = form.ClientSize.Width - 56,
            Height = 22,
            Text = main,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = palette.Text,
            BackColor = palette.Panel,
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
            ForeColor = palette.TextSub,
            BackColor = palette.Panel
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
            using var pen = new Pen(palette.Border);
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
