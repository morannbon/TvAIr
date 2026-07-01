using System.Drawing;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace TvAIr.Core;

internal sealed class SettingsWindow : Form
{

    private static class SettingsNumericRange
    {
        public const int PortMin = 1024;
        public const int PortMax = 65535;
        public const int PortWidth = 100;
        public const int PreStartMarginSecondsMin = 0;
        public const int PreStartMarginSecondsMax = 300;
        public const int PostEndMarginSecondsMin = 0;
        public const int PostEndMarginSecondsMax = 300;
        public const int MarginSecondsWidth = 100;
        public const int RecordingAfterActionDelayMinutesMin = 1;
        public const int RecordingAfterActionDelayMinutesMax = 5;
        public const int RecordingAfterActionDelayWidth = 100;
        public const int ScheduleHourMin = 0;
        public const int ScheduleHourMax = 23;
        public const int ScheduleMinuteMin = 0;
        public const int ScheduleMinuteMax = 59;
        public const int ScheduleTimeFieldWidth = 70;
    }
    private readonly int _port;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SettingsThemePalette _theme = SettingsThemePalette.Light();
    private readonly SettingsLayoutSpec _layout = SettingsLayoutSpec.Default();

    private SettingsApiDto? _loaded;
    private string _lastSavedSettingsSnapshot = string.Empty;
    private List<string> _bonDriverList = new();

    private readonly Panel _contentHost = new();
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.OrdinalIgnoreCase);
    private string _currentPage = "general";

    private readonly CheckBox _chkEpgEnabled = new();
    private readonly TextBox _txtEpgHour = new();
    private readonly TextBox _txtEpgMinute = new();
    private readonly SettingsSliderControl _sldEpgDepth;
    private readonly CheckBox _chkEpgPreRecordEnabled = new();
    private readonly SettingsSliderControl _sldEpgPreRecord;
    private readonly TextBox _txtDataDirectory = new();
    private readonly TextBox _txtPort = new();
    private readonly CheckBox _chkStartupEnabled = new();
    private readonly TextBox _txtTaskUserName = new();
    private readonly TextBox _txtTaskPassword = new();
    private readonly Label _lblTaskPasswordStatus = new();

    private readonly TextBox _txtTvTestExe = new();
    private readonly TextBox _txtViewingTvTestExe = new();
    private readonly TextBox _txtBonDriverDir = new();
    private readonly TextBox _txtGrCh2 = new();
    private readonly TextBox _txtGrChSet = new();
    private readonly TextBox _txtBscsCh2 = new();
    private readonly TextBox _txtBscsChSet = new();

    private readonly DataGridView _gridTuners = new();
    private bool _refreshingTunerGridNames;

    private readonly CheckBox _chkTvTestRecordCurServiceOnly = new();
    private readonly CheckBox _chkTvTestRecordSubtitle = new();
    private readonly CheckBox _chkTvTestRecordDataCarrousel = new();
    private readonly CheckBox _chkShowTvAIrEpgRecTaskbarIcon = new();
    private readonly TextBox _txtPreStart = new();
    private readonly TextBox _txtPostEnd = new();
    private readonly ComboBox _cmbRecordingAfterAction = new();
    private readonly TextBox _txtRecordingAfterActionDelay = new();
    private readonly RadioButton _radPriorityBefore = new();
    private readonly RadioButton _radPriorityAfter = new();
    private readonly CheckBox _chkPseudoContinuous = new();

    private readonly DataGridView _gridGenreColors = new();
    private readonly Button _btnResetGenreColors = new();
    private readonly RadioButton _radThemeCurrent = new();
    private readonly RadioButton _radThemeLight = new();
    private readonly RadioButton _radThemeDark = new();
    private readonly TableLayoutPanel _genreColorList = new();
    private readonly Dictionary<string, Button> _genreColorButtons = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingSystemThemeSelection;

    private readonly Button _btnApply = new();
    private readonly Button _btnOk = new();
    private readonly Button _btnCancel = new();

    public SettingsWindow(int port)
    {
        _port = port;
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        _sldEpgDepth = new SettingsSliderControl(
            new[] { "浅 120秒", "中 180秒", "深 240秒", "最深 300秒" },
            new[] { "shallow", "medium", "deep", "deeper" },
            _theme);
        _sldEpgPreRecord = new SettingsSliderControl(
            new[] { "5分", "10分", "15分", "20分" },
            new[] { "5", "10", "15", "20" },
            _theme);

        Text = "TvAIr 設定";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 660);
        Size = new Size(1040, 720);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        Font = _layout.Font;
        BackColor = _theme.Page;

        BuildUi();
        Shown += async (_, _) => await LoadSettingsAsync();
    }

    private void BuildUi()
    {
        Controls.Clear();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = _theme.Page,
            Padding = _layout.WindowPadding,
            Margin = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = _theme.Page,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _layout.NavWidth));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(body, 0, 0);

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = _theme.Page,
            Padding = new Padding(0, 2, 12, 0),
            Margin = new Padding(0)
        };
        body.Controls.Add(nav, 0, 0);

        AddNavButton(nav, "general", "全般");
        AddNavButton(nav, "tvtest", "TVTest");
        AddNavButton(nav, "tuner", "チューナー");
        AddNavButton(nav, "recording", "録画");
        AddNavButton(nav, "appearance", "表示");

        _contentHost.Dock = DockStyle.Fill;
        _contentHost.BackColor = _theme.Page;
        _contentHost.Padding = new Padding(0, 2, 0, 0);
        _contentHost.Margin = new Padding(0);
        body.Controls.Add(_contentHost, 1, 0);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = _theme.Page,
            Margin = new Padding(0, 10, 0, 0)
        };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.Controls.Add(new Panel { Dock = DockStyle.Fill, Height = 1, BackColor = _theme.BorderSoft, Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = _theme.Page,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        footer.Controls.Add(buttons, 0, 1);
        root.Controls.Add(footer, 0, 1);

        ConfigureActionButton(_btnOk, "OK", true);
        _btnOk.Click += async (_, _) =>
        {
            if (!HasUnsavedChanges())
            {
                Close();
                return;
            }
            if (await SaveSettingsAsync(showSuccessMessage: false)) Close();
        };
        ConfigureActionButton(_btnCancel, "キャンセル", false);
        _btnCancel.Click += (_, _) => Close();
        ConfigureActionButton(_btnApply, "適用", false);
        _btnApply.Click += async (_, _) => await SaveSettingsAsync(showSuccessMessage: true);
        buttons.Controls.Add(_btnOk);
        buttons.Controls.Add(_btnCancel);
        buttons.Controls.Add(_btnApply);

        ShowPage("general");
    }

    private void AddNavButton(FlowLayoutPanel nav, string key, string text)
    {
        var button = new Button
        {
            Text = text,
            Width = _layout.NavButtonWidth,
            Height = _layout.NavButtonHeight,
            Margin = new Padding(0, 0, 0, 6),
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 0, 0),
            Font = _layout.Font,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 1;
        button.Click += (_, _) => ShowPage(key);
        _navButtons[key] = button;
        nav.Controls.Add(button);
    }

    private void ShowPage(string key)
    {
        _currentPage = key;
        foreach (var item in _navButtons)
        {
            var selected = string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase);
            item.Value.BackColor = selected ? _theme.MenuSelected : _theme.Menu;
            item.Value.ForeColor = selected ? _theme.Text : _theme.TextSub;
            item.Value.FlatAppearance.BorderColor = selected ? _theme.Border : _theme.Page;
        }

        _contentHost.SuspendLayout();
        _contentHost.Controls.Clear();
        _contentHost.Controls.Add(key switch
        {
            "tvtest" => BuildTvTestPage(),
            "tuner" => BuildTunerPage(),
            "recording" => BuildRecordingPage(),
            "appearance" => BuildAppearancePage(),
            _ => BuildGeneralPage(),
        });
        _contentHost.ResumeLayout();
    }

    private Control BuildGeneralPage()
    {
        var panel = CreateScrollPanel();
        panel.Controls.Add(CreateSection("その他", new Control[]
        {
            LabeledControl("データ保存先フォルダ", CreateBrowseTextBox(_txtDataDirectory, BrowseTarget.Folder)),
            LabeledControl("ポート番号", ConfigureNumeric(_txtPort, SettingsNumericRange.PortMin, SettingsNumericRange.PortMax, SettingsNumericRange.PortWidth)),
            CheckControl(_chkStartupEnabled, "Windows起動時にTvAIrを自動起動する"),
            LabeledControl("タスクスケジューラー ユーザー名", ConfigureText(_txtTaskUserName)),
            LabeledControl("タスクスケジューラー パスワード", CreatePasswordWithStatusRow()),
        }));
        panel.Controls.Add(CreateSection("EPG取得", new Control[]
        {
            CheckControl(_chkEpgEnabled, "定期取得を有効にする"),
            LabeledControl("定期取得時刻", CreateHourMinuteRow(_txtEpgHour, _txtEpgMinute)),
            LabeledControl("取得深度", _sldEpgDepth),
            CheckControl(_chkEpgPreRecordEnabled, "放送前EPG確認を有効にする"),
            LabeledControl("放送前EPG確認（時間追従）", _sldEpgPreRecord),
            NoteControl("録画開始の何分前にEPG確認を行うかを決めます。同時刻に始まる録画数が多い場合は長めにします。目安は、1〜2本は5分前、3〜6本は10分前、7〜12本は15分前、13本以上は20分前です。録画開始に近い場合は、番組表確認より録画開始を優先します。"),
        }));
        return panel;
    }

    private Control BuildTvTestPage()
    {
        var panel = CreateScrollPanel();
        panel.Controls.Add(CreateSection("TVTest", new Control[]
        {
            LabeledControl("TVTest.exe のフルパス", CreateBrowseTextBox(_txtTvTestExe, BrowseTarget.TvTestExe)),
            LabeledControl("視聴用TVTest.exe のフルパス", CreateBrowseTextBox(_txtViewingTvTestExe, BrowseTarget.TvTestExe)),
            NoteControl("視聴用TVTestはAIrConなどの視聴操作で使用します。視聴用にしたチューナーは録画・EPG取得の候補から除外されます。"),
            LabeledControl("BonDriver フォルダ", CreateBrowseTextBox(_txtBonDriverDir, BrowseTarget.Folder)),
            LabeledControl("地上波 ch2 ファイルパス", CreateBrowseTextBox(_txtGrCh2, BrowseTarget.Ch2File)),
            LabeledControl("地上波 ChSet.txt ファイルパス", CreateBrowseTextBox(_txtGrChSet, BrowseTarget.ChSetFile)),
            LabeledControl("BS/CS ch2 ファイルパス", CreateBrowseTextBox(_txtBscsCh2, BrowseTarget.Ch2File)),
            LabeledControl("BS/CS ChSet.txt ファイルパス", CreateBrowseTextBox(_txtBscsChSet, BrowseTarget.ChSetFile)),
            NoteControl("ch2 と ChSet は設定画面で明示したパスだけを使用します。TvAIr側で推定探索や代用は行いません。"),
        }));
        return panel;
    }

    private Control BuildTunerPage()
    {
        var panel = CreateScrollPanel();
        BuildTunerGrid();
        var gridHost = new Panel { Dock = DockStyle.Top, Height = 300, BackColor = _theme.Panel, Padding = new Padding(0), Margin = new Padding(0) };
        gridHost.Controls.Add(_gridTuners);
        panel.Controls.Add(CreateSection("チューナー", new Control[]
        {
            gridHost,
            NoteControl("用途を「視聴用」にしたチューナーは、録画・EPG取得の候補から除外し、TvAIr管理の視聴用TVTestとして扱います。"),
            NoteControl("表示されるチューナー名は、予約時の優先順位を分かりやすくするための名前です。"),
            NoteControl("BonDriver が未設定の行は使用されません。"),
        }));
        return panel;
    }

    private Control BuildRecordingPage()
    {
        var panel = CreateScrollPanel();
        panel.Controls.Add(CreateSection("録画", new Control[]
        {
            CheckControl(_chkTvTestRecordCurServiceOnly, "現在のサービスのみ保存する"),
            CheckControl(_chkTvTestRecordSubtitle, "字幕データを保存する"),
            CheckControl(_chkTvTestRecordDataCarrousel, "データ放送を保存する"),
            CheckControl(_chkShowTvAIrEpgRecTaskbarIcon, "TvAIrEpgRecのタスクバーアイコンを表示する"),
            LabeledControl("録画開始マージン（秒前）", ConfigureNumeric(_txtPreStart, SettingsNumericRange.PreStartMarginSecondsMin, SettingsNumericRange.PreStartMarginSecondsMax, SettingsNumericRange.MarginSecondsWidth)),
            LabeledControl("録画終了マージン（秒後）", ConfigureNumeric(_txtPostEnd, SettingsNumericRange.PostEndMarginSecondsMin, SettingsNumericRange.PostEndMarginSecondsMax, SettingsNumericRange.MarginSecondsWidth)),
            LabeledControl("録画終了後アクション", ConfigureRecordingAfterActionCombo()),
            LabeledControl("録画終了後アクション待機", ConfigureNumeric(_txtRecordingAfterActionDelay, SettingsNumericRange.RecordingAfterActionDelayMinutesMin, SettingsNumericRange.RecordingAfterActionDelayMinutesMax, SettingsNumericRange.RecordingAfterActionDelayWidth)),
            LabeledControl("前後番組の優先", CreatePriorityModeControl()),
            CheckControl(_chkPseudoContinuous, "チェーン予約を実行する"),
            NoteControl("チェーン予約は、後番組の完全性を優先する最終救済手段です。"),
            NoteControl("スリープ復帰やTVTest起動後の待機は自動で調整されます。"),
        }));
        return panel;
    }

    private Control BuildAppearancePage()
    {
        var panel = CreateScrollPanel();

        var genreHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = _layout.GenreColorRowHeight * _layout.GenreColorRowsPerColumn,
            BackColor = _theme.Panel,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        ConfigureGenreColorListHost();
        genreHost.Controls.Add(_genreColorList);
        ConfigureActionButton(_btnResetGenreColors, "現在テーマを標準に戻す", false);
        _btnResetGenreColors.Width = 170;
        _btnResetGenreColors.Click -= ResetGenreColorsClick;
        _btnResetGenreColors.Click += ResetGenreColorsClick;

        // DockStyle.Top stacks the last-added section at the top. Add genre first, then system color,
        // so the WinForms settings tab follows the Web modal order: System color -> Genre colors.
        panel.Controls.Add(CreateSection("番組表ジャンル色", new Control[]
        {
            NoteControl("ジャンル色はテーマ別に自動切替せず、番組表共通の色設定として保持します。"),
            NoteControl("この画面では現在適用中テーマのジャンル色だけを編集します。テーマを切り替えると編集対象も切り替わります。"),
            genreHost,
            _btnResetGenreColors,
        }));

        panel.Controls.Add(CreateSection("システム色設定", new Control[]
        {
            NoteControl("現在の設定はWindowsのアプリテーマに追従します。ライト/ダークはTvAIr固定です。ジャンル色は別設定として保持します。"),
            CreateSystemThemeOptions(),
        }));
        return panel;
    }

    private void ResetGenreColorsClick(object? sender, EventArgs e) => ResetGenreColorsToDefault();

    private async Task LoadSettingsAsync()
    {
        try
        {
            Enabled = false;
            var dto = await _http.GetFromJsonAsync<SettingsApiDto>("api/settings", _jsonOptions);
            if (dto is null)
            {
                TvAIrNotificationDialog.ShowError(this, "設定の取得に失敗しました。");
                return;
            }
            _loaded = dto;
            _bonDriverList = dto.BonDriverList ?? new List<string>();
            ApplyDtoToControls(dto);
            _lastSavedSettingsSnapshot = CreateSettingsSnapshot(CollectDtoFromControls());
        }
        catch (Exception ex)
        {
            TvAIrNotificationDialog.ShowError(this, "設定の取得に失敗しました。", ex.Message);
        }
        finally
        {
            Enabled = true;
        }
    }

    private async Task<bool> SaveSettingsAsync(bool showSuccessMessage)
    {
        try
        {
            var dto = CollectDtoFromControls();
            var snapshot = CreateSettingsSnapshot(dto);
            if (string.Equals(snapshot, _lastSavedSettingsSnapshot, StringComparison.Ordinal)) return true;

            Enabled = false;
            var response = await _http.PutAsJsonAsync("api/settings", dto, _jsonOptions);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                TvAIrNotificationDialog.ShowError(this, string.IsNullOrWhiteSpace(body) ? "設定の保存に失敗しました。" : body);
                return false;
            }

            var message = "設定を保存しました。";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var msgEl)) message = msgEl.GetString() ?? message;
            }
            catch { }

            _loaded = dto;
            _lastSavedSettingsSnapshot = snapshot;
            _txtTaskPassword.Clear();
            if (showSuccessMessage) TvAIrNotificationDialog.ShowInfo(this, message);
            return true;
        }
        catch (Exception ex)
        {
            TvAIrNotificationDialog.ShowError(this, "設定の保存に失敗しました。", ex.Message);
            return false;
        }
        finally
        {
            Enabled = true;
        }
    }

    private bool HasUnsavedChanges()
    {
        if (_loaded is null) return false;
        return !string.Equals(CreateSettingsSnapshot(CollectDtoFromControls()), _lastSavedSettingsSnapshot, StringComparison.Ordinal);
    }

    private string CreateSettingsSnapshot(SettingsApiDto dto) => JsonSerializer.Serialize(dto, _jsonOptions);

    private void ApplyDtoToControls(SettingsApiDto dto)
    {
        _chkEpgEnabled.Checked = dto.EpgEnabled;
        SetNumber(_txtEpgHour, dto.EpgHour);
        SetNumber(_txtEpgMinute, dto.EpgMinute);
        _sldEpgDepth.SelectedValue = NormalizeEpgDepth(dto.EpgDepth);
        _chkEpgPreRecordEnabled.Checked = dto.EpgPreRecordMinutes > 0;
        _sldEpgPreRecord.SelectedValue = Math.Clamp(dto.EpgPreRecordMinutes <= 0 ? 5 : dto.EpgPreRecordMinutes, 5, 20).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ApplySystemThemeToControls(dto.SystemTheme);

        _txtDataDirectory.Text = dto.DataDirectory ?? string.Empty;
        SetNumber(_txtPort, dto.Port);
        _chkStartupEnabled.Checked = dto.StartupEnabled;
        _txtTaskUserName.Text = dto.TaskUserName ?? string.Empty;
        _txtTaskPassword.Clear();
        _lblTaskPasswordStatus.Text = dto.TaskHasPassword ? "パスワード設定済み" : "パスワード未設定";

        _txtTvTestExe.Text = dto.TvTestExecutablePath ?? string.Empty;
        _txtViewingTvTestExe.Text = dto.ViewingTvTestExecutablePath ?? string.Empty;
        _txtBonDriverDir.Text = dto.BonDriverDirectory ?? string.Empty;
        _txtGrCh2.Text = dto.GrChannelFilePath ?? string.Empty;
        _txtGrChSet.Text = dto.GrChSetFilePath ?? string.Empty;
        _txtBscsCh2.Text = dto.BscsChannelFilePath ?? string.Empty;
        _txtBscsChSet.Text = dto.BscsChSetFilePath ?? string.Empty;

        PopulateTunerGrid(dto.Tuners ?? new List<TunerProfileDto>());

        _chkTvTestRecordCurServiceOnly.Checked = dto.TvTestRecordCurServiceOnly;
        _chkTvTestRecordSubtitle.Checked = dto.TvTestRecordSubtitle;
        _chkTvTestRecordDataCarrousel.Checked = dto.TvTestRecordDataCarrousel;
        _chkShowTvAIrEpgRecTaskbarIcon.Checked = dto.ShowTvAIrEpgRecTaskbarIcon;
        SetNumber(_txtPreStart, dto.PreStartMarginSeconds);
        SetNumber(_txtPostEnd, dto.PostEndMarginSeconds);
        _cmbRecordingAfterAction.SelectedItem = RecordingAfterActionToDisplay(dto.RecordingAfterAction);
        SetNumber(_txtRecordingAfterActionDelay, Math.Clamp(dto.RecordingAfterActionDelayMinutes <= 0 ? 1 : dto.RecordingAfterActionDelayMinutes, 1, 5));
        _radPriorityBefore.Checked = dto.LaterProgramPriority != true;
        _radPriorityAfter.Checked = dto.LaterProgramPriority == true;
        _chkPseudoContinuous.Checked = dto.LaterProgramPriority == true && dto.PseudoContinuousRecording == true;
        UpdatePriorityDependentControls();

        PopulateGenreColorGrid(GetActiveGenreColors(dto), GetDefaultGenreColorsForCurrentTheme());
    }

    private SettingsApiDto CollectDtoFromControls()
    {
        var dto = _loaded is null ? new SettingsApiDto() : _loaded.Clone();

        dto.EpgEnabled = _chkEpgEnabled.Checked;
        dto.EpgHour = ReadNumber(_txtEpgHour);
        dto.EpgMinute = ReadNumber(_txtEpgMinute);
        dto.EpgDepth = _sldEpgDepth.SelectedValue;
        dto.EpgPreRecordMinutes = _chkEpgPreRecordEnabled.Checked ? ReadSliderInt(_sldEpgPreRecord, 5) : 0;
        dto.SystemTheme = CollectSystemThemeFromControls();
        dto.DataDirectory = _txtDataDirectory.Text.Trim();
        dto.Port = ReadNumber(_txtPort);
        dto.StartupEnabled = _chkStartupEnabled.Checked;
        dto.TaskUserName = _txtTaskUserName.Text.Trim();
        dto.TaskPasswordPlain = string.IsNullOrEmpty(_txtTaskPassword.Text) ? null : (_txtTaskPassword.Text == " " ? string.Empty : _txtTaskPassword.Text);

        dto.TvTestExecutablePath = _txtTvTestExe.Text.Trim();
        dto.ViewingTvTestExecutablePath = _txtViewingTvTestExe.Text.Trim();
        dto.BonDriverDirectory = _txtBonDriverDir.Text.Trim();
        dto.GrChannelFilePath = _txtGrCh2.Text.Trim();
        dto.GrChSetFilePath = _txtGrChSet.Text.Trim();
        dto.BscsChannelFilePath = _txtBscsCh2.Text.Trim();
        dto.BscsChSetFilePath = _txtBscsChSet.Text.Trim();

        dto.Tuners = CollectTunerRows();

        dto.TvTestRecordCurServiceOnly = _chkTvTestRecordCurServiceOnly.Checked;
        dto.TvTestRecordSubtitle = _chkTvTestRecordSubtitle.Checked;
        dto.TvTestRecordDataCarrousel = _chkTvTestRecordDataCarrousel.Checked;
        dto.ShowTvAIrEpgRecTaskbarIcon = _chkShowTvAIrEpgRecTaskbarIcon.Checked;
        dto.PreStartMarginSeconds = ReadNumber(_txtPreStart);
        dto.PostEndMarginSeconds = ReadNumber(_txtPostEnd);
        dto.RecordingAfterAction = RecordingAfterActionToValue(_cmbRecordingAfterAction.SelectedItem?.ToString());
        dto.RecordingAfterActionDelayMinutes = Math.Clamp(ReadNumber(_txtRecordingAfterActionDelay), 1, 5);
        dto.LaterProgramPriority = _radPriorityAfter.Checked;
        dto.PseudoContinuousRecording = _radPriorityAfter.Checked && _chkPseudoContinuous.Checked;

        var editedGenreColors = CollectGenreColorRows();
        var editingTheme = CollectSystemThemeFromControls() == "dark" ? "dark" : "light";
        if (dto.ThemeGenrePalettes is null || dto.ThemeGenrePalettes.Count == 0)
            dto.ThemeGenrePalettes = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!dto.ThemeGenrePalettes.ContainsKey("light"))
            dto.ThemeGenrePalettes["light"] = new Dictionary<string, string>(dto.LightGenreColors.Count > 0 ? dto.LightGenreColors : dto.GenreColors, StringComparer.OrdinalIgnoreCase);
        if (!dto.ThemeGenrePalettes.ContainsKey("dark"))
            dto.ThemeGenrePalettes["dark"] = new Dictionary<string, string>(dto.DarkGenreColors, StringComparer.OrdinalIgnoreCase);
        dto.ThemeGenrePalettes[editingTheme] = new Dictionary<string, string>(editedGenreColors, StringComparer.OrdinalIgnoreCase);
        dto.LightGenreColors = new Dictionary<string, string>(dto.ThemeGenrePalettes["light"], StringComparer.OrdinalIgnoreCase);
        dto.DarkGenreColors = new Dictionary<string, string>(dto.ThemeGenrePalettes["dark"], StringComparer.OrdinalIgnoreCase);
        dto.GenreColors = new Dictionary<string, string>(dto.LightGenreColors, StringComparer.OrdinalIgnoreCase);
        return dto;
    }

    private Panel CreateScrollPanel()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0),
            BackColor = _theme.Page,
            Margin = new Padding(0)
        };
    }

    private Panel CreateSection(string title, IEnumerable<Control> controls)
    {
        var card = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = _layout.SectionPadding,
            Margin = new Padding(0, 0, 0, _layout.SectionGap),
            BackColor = _theme.Panel,
            BorderStyle = BorderStyle.None
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            BackColor = _theme.Panel,
            Margin = new Padding(0)
        };
        card.Controls.Add(layout);
        var titleLabel = new Label
        {
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = _layout.SectionTitleFont,
            ForeColor = _theme.Text,
            Margin = new Padding(0, 0, 0, 12)
        };
        layout.Controls.Add(titleLabel);
        foreach (var control in controls)
        {
            control.Dock = DockStyle.Top;
            control.Margin = new Padding(0, 0, 0, 10);
            layout.Controls.Add(control);
        }
        return card;
    }

    private Control LabeledControl(string label, Control control)
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _theme.Panel,
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _layout.LabelWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var labelControl = new Label
        {
            Text = label,
            AutoSize = true,
            Padding = new Padding(0, 5, 12, 0),
            ForeColor = _theme.TextSub,
            Font = new Font("Meiryo", 9F),
            Margin = new Padding(0)
        };
        layout.Controls.Add(labelControl, 0, 0);
        control.Anchor = AnchorStyles.Left | (ShouldStretchSettingsField(control) ? AnchorStyles.Right : AnchorStyles.None);
        control.Margin = new Padding(0);
        layout.Controls.Add(control, 1, 0);
        return layout;
    }

    private static bool ShouldStretchSettingsField(Control control)
    {
        // SettingsFieldWidthContract: only path/browse rows and range sliders should fill the
        // available width.  Numeric inputs, combo boxes, and policy choice rows keep their
        // natural width so right-click settings match the Web modal instead of becoming
        // unnecessarily wide.
        if (control is TrackBar) return true;
        if (control is TableLayoutPanel table && table.Dock == DockStyle.Fill) return true;
        return false;
    }

    private Control CheckControl(CheckBox checkBox, string text)
    {
        checkBox.Text = text;
        checkBox.AutoSize = true;
        checkBox.Margin = new Padding(0, 2, 0, 2);
        checkBox.ForeColor = _theme.Text;
        checkBox.BackColor = _theme.Panel;
        return checkBox;
    }

    private Label NoteControl(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(_layout.NoteWidth, 0),
            Dock = DockStyle.Top,
            ForeColor = _theme.TextMuted,
            Font = new Font("Meiryo", 8.5F),
            Padding = new Padding(0, 0, 0, 0),
            Margin = new Padding(0, 0, 0, 8),
            BackColor = _theme.Panel
        };
    }

    private Control CreateHourMinuteRow(TextBox hour, TextBox minute)
    {
        ConfigureNumeric(hour, SettingsNumericRange.ScheduleHourMin, SettingsNumericRange.ScheduleHourMax, SettingsNumericRange.ScheduleTimeFieldWidth);
        ConfigureNumeric(minute, SettingsNumericRange.ScheduleMinuteMin, SettingsNumericRange.ScheduleMinuteMax, SettingsNumericRange.ScheduleTimeFieldWidth);
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = _theme.Panel };
        panel.Controls.Add(hour);
        panel.Controls.Add(new Label { Text = "時", AutoSize = true, Padding = new Padding(6, 6, 8, 0), ForeColor = _theme.Text });
        panel.Controls.Add(minute);
        panel.Controls.Add(new Label { Text = "分", AutoSize = true, Padding = new Padding(6, 6, 0, 0), ForeColor = _theme.Text });
        return panel;
    }

    private Control CreatePasswordWithStatusRow()
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = _theme.Panel };
        ConfigurePasswordBox(_txtTaskPassword);
        _txtTaskPassword.Width = 220;
        _lblTaskPasswordStatus.AutoSize = true;
        _lblTaskPasswordStatus.Padding = new Padding(10, 6, 0, 0);
        _lblTaskPasswordStatus.ForeColor = _theme.TextMuted;
        panel.Controls.Add(_txtTaskPassword);
        panel.Controls.Add(_lblTaskPasswordStatus);
        return panel;
    }

    private Control CreateBrowseTextBox(TextBox textBox, BrowseTarget target)
    {
        ConfigureText(textBox);
        var panel = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = _theme.Panel,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _layout.BrowseColumnWidth));
        var button = new Button
        {
            Text = "参照",
            Width = 58,
            Height = 25,
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonSecondary,
            ForeColor = _theme.Text,
            Font = new Font("Meiryo", 9F)
        };
        button.FlatAppearance.BorderColor = _theme.Border;
        button.Click += (_, _) => BrowseIntoTextBox(textBox, target);
        panel.Controls.Add(textBox, 0, 0);
        panel.Controls.Add(button, 1, 0);
        return panel;
    }

    private void BrowseIntoTextBox(TextBox textBox, BrowseTarget target)
    {
        if (target == BrowseTarget.Folder)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "フォルダを選択してください",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(textBox.Text) ? textBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) textBox.Text = dialog.SelectedPath;
            return;
        }

        var directory = Path.GetDirectoryName(textBox.Text);
        using var fileDialog = new OpenFileDialog
        {
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = Path.GetFileName(textBox.Text),
            InitialDirectory = Directory.Exists(directory) ? directory! : Environment.GetFolderPath(Environment.SpecialFolder.MyComputer),
            Filter = target == BrowseTarget.TvTestExe
                ? "TVTest.exe|TVTest.exe|実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*"
                : target == BrowseTarget.ChSetFile
                    ? "ChSet.txt (*.txt)|*.txt|すべてのファイル (*.*)|*.*"
                    : "ch2 ファイル (*.ch2)|*.ch2|すべてのファイル (*.*)|*.*"
        };
        if (fileDialog.ShowDialog(this) == DialogResult.OK) textBox.Text = fileDialog.FileName;
    }

    private TextBox ConfigureNumeric(TextBox box, int min, int max, int width)
    {
        box.Width = width;
        box.BorderStyle = BorderStyle.FixedSingle;
        // SettingsNumericInput: numeric fields are not path/text fields.  Keep them visually
        // centered in both the Web modal contract and this WinForms settings surface.
        box.TextAlign = HorizontalAlignment.Center;
        box.Tag = Tuple.Create(min, max);
        box.Margin = new Padding(0);
        box.BackColor = _theme.Input;
        box.ForeColor = _theme.Text;
        return box;
    }

    private TextBox ConfigureText(TextBox box)
    {
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Margin = new Padding(0);
        box.BackColor = _theme.Input;
        box.ForeColor = _theme.Text;
        box.Width = _layout.TextBoxWidth;
        return box;
    }

    private TextBox ConfigurePasswordBox(TextBox box)
    {
        ConfigureText(box);
        box.UseSystemPasswordChar = true;
        return box;
    }

    private ComboBox ConfigureRecordingAfterActionCombo()
    {
        _cmbRecordingAfterAction.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbRecordingAfterAction.Items.Clear();
        _cmbRecordingAfterAction.Items.AddRange(new object[] { "何もしない", "スリープ", "シャットダウン" });
        _cmbRecordingAfterAction.Width = 180;
        _cmbRecordingAfterAction.BackColor = _theme.Input;
        _cmbRecordingAfterAction.ForeColor = _theme.Text;
        if (_cmbRecordingAfterAction.SelectedIndex < 0) _cmbRecordingAfterAction.SelectedIndex = 0;
        return _cmbRecordingAfterAction;
    }

    private Control CreatePriorityModeControl()
    {
        _radPriorityBefore.Text = "前番組優先（デフォルト）";
        _radPriorityAfter.Text = "後番組を優先する";
        _radPriorityBefore.AutoSize = true;
        _radPriorityAfter.AutoSize = true;
        _radPriorityBefore.ForeColor = _theme.Text;
        _radPriorityAfter.ForeColor = _theme.Text;
        _radPriorityBefore.BackColor = _theme.Panel;
        _radPriorityAfter.BackColor = _theme.Panel;
        _radPriorityBefore.Margin = new Padding(0);
        _radPriorityAfter.Margin = new Padding(0);
        _radPriorityBefore.CheckedChanged -= PriorityChanged;
        _radPriorityAfter.CheckedChanged -= PriorityChanged;
        _radPriorityBefore.CheckedChanged += PriorityChanged;
        _radPriorityAfter.CheckedChanged += PriorityChanged;

        // SettingsRecordingPriorityPolicy: this is a recording policy selector, not the
        // reservation-list priority column and not an AutoSearch rule toggle.  Lay it out
        // as one policy field so the tray/right-click settings surface matches the Web modal.
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = _theme.Panel,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        const int beforeWidth = 210;
        const int afterWidth = 190;
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, beforeWidth));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, afterWidth));
        panel.Controls.Add(CenterHostedControl(_radPriorityBefore, beforeWidth), 0, 0);
        panel.Controls.Add(CenterHostedControl(_radPriorityAfter, afterWidth), 1, 0);
        return panel;
    }

    private Control CenterHostedControl(Control control, int hostWidth)
    {
        var host = new Panel
        {
            Width = hostWidth,
            Height = Math.Max(26, control.Height + 4),
            Margin = new Padding(0),
            BackColor = _theme.Panel,
        };
        control.Anchor = AnchorStyles.None;
        host.Controls.Add(control);
        host.Resize += (_, _) => CenterChildInHost(host, control);
        control.SizeChanged += (_, _) => CenterChildInHost(host, control);
        CenterChildInHost(host, control);
        return host;
    }

    private static void CenterChildInHost(Control host, Control child)
    {
        child.Left = Math.Max(0, (host.ClientSize.Width - child.Width) / 2);
        child.Top = Math.Max(0, (host.ClientSize.Height - child.Height) / 2);
    }

    private void PriorityChanged(object? sender, EventArgs e) => UpdatePriorityDependentControls();

    private void UpdatePriorityDependentControls()
    {
        var afterPriority = _radPriorityAfter.Checked;
        _chkPseudoContinuous.Enabled = afterPriority;
        if (!afterPriority) _chkPseudoContinuous.Checked = false;
    }

    private void BuildTunerGrid()
    {
        if (_gridTuners.Columns.Count > 0) return;
        _gridTuners.Dock = DockStyle.Fill;
        _gridTuners.AllowUserToAddRows = true;
        _gridTuners.AllowUserToDeleteRows = false;
        _gridTuners.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridTuners.RowHeadersVisible = false;
        _gridTuners.BackgroundColor = _theme.Panel;
        _gridTuners.BorderStyle = BorderStyle.None;
        _gridTuners.GridColor = _theme.BorderSoft;
        _gridTuners.EnableHeadersVisualStyles = false;
        _gridTuners.ColumnHeadersDefaultCellStyle.BackColor = _theme.SubPanel;
        _gridTuners.ColumnHeadersDefaultCellStyle.ForeColor = _theme.Text;
        _gridTuners.ColumnHeadersDefaultCellStyle.Font = new Font("Meiryo", 9F, FontStyle.Bold);
        _gridTuners.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _gridTuners.DefaultCellStyle.BackColor = _theme.Panel;
        _gridTuners.DefaultCellStyle.ForeColor = _theme.Text;
        _gridTuners.DefaultCellStyle.SelectionBackColor = _theme.MenuSelected;
        _gridTuners.DefaultCellStyle.SelectionForeColor = _theme.Text;
        _gridTuners.CellContentClick += TunerGridCellContentClick;
        _gridTuners.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_gridTuners.IsCurrentCellDirty) _gridTuners.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _gridTuners.CellValueChanged += (_, e) =>
        {
            if (!_refreshingTunerGridNames && e.RowIndex >= 0) RefreshTunerGridVirtualNames();
        };
        _gridTuners.RowsRemoved += (_, _) => RefreshTunerGridVirtualNames();
        _gridTuners.UserAddedRow += (_, _) => RefreshTunerGridVirtualNames();

        // SettingsTunerAssignmentTable: keep the same semantic column contract as the
        // Web settings modal.  This is not CSS; WinForms needs the column contract here.
        _gridTuners.Columns.Add(CreateTunerTextColumn("Name", "仮想枠", 70, SettingsFieldIntent.VirtualSlot));
        _gridTuners.Columns.Add(CreateTunerComboColumn("BonDriverFileName", "BonDriver", 138, SettingsFieldIntent.BonDriver, Array.Empty<string>()));
        _gridTuners.Columns.Add(CreateTunerComboColumn("Group", "放送波", 104, SettingsFieldIntent.BroadcastWave, new[] { "地上波", "BS/CS", "地デジ/BS/CS" }));
        _gridTuners.Columns.Add(CreateTunerComboColumn("Role", "用途", 54, SettingsFieldIntent.TunerPurpose, new[] { "録画用", "視聴用" }));
        _gridTuners.Columns.Add(CreateTunerOperationColumn());
        _gridTuners.Columns.Add(CreateTunerTextColumn("Did", "ID", 10, SettingsFieldIntent.InternalPhysicalId));
        _gridTuners.Columns["Did"]!.Visible = false;
    }

    private DataGridViewTextBoxColumn CreateTunerTextColumn(string name, string header, float fillWeight, SettingsFieldIntent intent)
    {
        var column = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = fillWeight, Tag = intent };
        ApplySettingsColumnContract(column, intent);
        return column;
    }

    private DataGridViewComboBoxColumn CreateTunerComboColumn(string name, string header, float fillWeight, SettingsFieldIntent intent, string[] values)
    {
        var column = new DataGridViewComboBoxColumn { Name = name, HeaderText = header, FlatStyle = FlatStyle.Flat, FillWeight = fillWeight, Tag = intent };
        if (values.Length > 0) column.DataSource = values;
        ApplySettingsColumnContract(column, intent);
        return column;
    }


    private DataGridViewButtonColumn CreateTunerOperationColumn()
    {
        var column = new DataGridViewButtonColumn
        {
            Name = "Operation",
            HeaderText = "操作",
            Text = "削除",
            UseColumnTextForButtonValue = true,
            FillWeight = 44,
            Tag = SettingsFieldIntent.OperationAction,
            FlatStyle = FlatStyle.Flat
        };
        ApplySettingsColumnContract(column, SettingsFieldIntent.OperationAction);
        column.DefaultCellStyle.BackColor = _theme.ActionDangerSoftBack;
        column.DefaultCellStyle.ForeColor = _theme.ActionDanger;
        column.DefaultCellStyle.SelectionBackColor = _theme.ActionDangerSoftBack;
        column.DefaultCellStyle.SelectionForeColor = _theme.ActionDanger;
        return column;
    }

    private void TunerGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_gridTuners.Columns[e.ColumnIndex].Name != "Operation") return;
        var row = _gridTuners.Rows[e.RowIndex];
        if (row.IsNewRow) return;
        _gridTuners.Rows.RemoveAt(e.RowIndex);
        RefreshTunerGridVirtualNames();
    }

    private void RefreshTunerGridVirtualNames()
    {
        if (_refreshingTunerGridNames) return;
        _refreshingTunerGridNames = true;
        try
        {
            var didIndex = 0;
            foreach (DataGridViewRow row in _gridTuners.Rows)
            {
                if (row.IsNewRow) continue;
                var group = TunerGroupToValue(row.Cells["Group"].Value?.ToString());
                var did = (row.Cells["Did"].Value?.ToString()?.Trim() ?? string.Empty).ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(did))
                {
                    did = NextDid(didIndex);
                    row.Cells["Did"].Value = did;
                }
                row.Cells["Name"].Value = TunerDisplayName.Build(group, did);
                didIndex++;
            }
        }
        finally
        {
            _refreshingTunerGridNames = false;
        }
    }

    private static void ApplySettingsColumnContract(DataGridViewColumn column, SettingsFieldIntent intent)
    {
        var alignment = intent switch
        {
            SettingsFieldIntent.VirtualSlot => DataGridViewContentAlignment.MiddleCenter,
            SettingsFieldIntent.BroadcastWave => DataGridViewContentAlignment.MiddleCenter,
            SettingsFieldIntent.TunerPurpose => DataGridViewContentAlignment.MiddleCenter,
            SettingsFieldIntent.InternalPhysicalId => DataGridViewContentAlignment.MiddleCenter,
            SettingsFieldIntent.OperationAction => DataGridViewContentAlignment.MiddleCenter,
            _ => DataGridViewContentAlignment.MiddleLeft,
        };
        column.DefaultCellStyle.Alignment = alignment;
        column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        column.DefaultCellStyle.Padding = intent == SettingsFieldIntent.BonDriver ? new Padding(4, 0, 4, 0) : new Padding(0);
    }

    private void PopulateTunerGrid(List<TunerProfileDto> tuners)
    {
        BuildTunerGrid();
        if (_gridTuners.Columns[1] is DataGridViewComboBoxColumn bonCol)
        {
            bonCol.DataSource = null;
            bonCol.Items.Clear();
            foreach (var item in _bonDriverList) bonCol.Items.Add(item);
        }
        _gridTuners.Rows.Clear();
        foreach (var tuner in tuners)
        {
            var group = TunerDisplayName.NormalizeGroup(tuner.Group);
            var did = (tuner.Did ?? string.Empty).Trim().ToUpperInvariant();
            _gridTuners.Rows.Add(TunerDisplayName.ForUi(tuner.Name, group, did), tuner.BonDriverFileName, TunerGroupToDisplay(group), TunerRoleToDisplay(tuner.Role), null, did);
        }
        RefreshTunerGridVirtualNames();
    }

    private List<TunerProfileDto> CollectTunerRows()
    {
        var list = new List<TunerProfileDto>();
        foreach (DataGridViewRow row in _gridTuners.Rows)
        {
            if (row.IsNewRow) continue;
            var name = row.Cells["Name"].Value?.ToString()?.Trim() ?? string.Empty;
            var bon = row.Cells["BonDriverFileName"].Value?.ToString()?.Trim() ?? string.Empty;
            var group = TunerGroupToValue(row.Cells["Group"].Value?.ToString());
            var role = TunerRoleToValue(row.Cells["Role"].Value?.ToString());
            var did = (row.Cells["Did"].Value?.ToString()?.Trim() ?? string.Empty).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(bon) && string.IsNullOrWhiteSpace(did)) continue;
            list.Add(new TunerProfileDto { Name = TunerDisplayName.ForUi(name, group, did), Group = group, Did = did, BonDriverFileName = bon, Role = role });
        }
        return list;
    }

    private Control CreateSystemThemeOptions()
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _theme.Panel,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
        grid.Controls.Add(CreateThemeCard("current", _radThemeCurrent, "現在の設定", "Windowsに合わせる"), 0, 0);
        grid.Controls.Add(CreateThemeCard("light", _radThemeLight, "ライト", string.Empty), 1, 0);
        grid.Controls.Add(CreateThemeCard("dark", _radThemeDark, "ダーク", string.Empty), 2, 0);
        _radThemeCurrent.CheckedChanged -= SystemThemeChanged;
        _radThemeLight.CheckedChanged -= SystemThemeChanged;
        _radThemeDark.CheckedChanged -= SystemThemeChanged;
        _radThemeCurrent.CheckedChanged += SystemThemeChanged;
        _radThemeLight.CheckedChanged += SystemThemeChanged;
        _radThemeDark.CheckedChanged += SystemThemeChanged;
        return grid;
    }

    private Panel CreateThemeCard(string value, RadioButton radio, string title, string subtitle)
    {
        radio.Margin = new Padding(0, 0, 8, 0);
        radio.Tag = value;
        radio.AutoSize = true;
        radio.BackColor = Color.Transparent;
        radio.Cursor = Cursors.Hand;
        var card = new Panel
        {
            Height = 58,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 10, 0),
            Padding = new Padding(12, 8, 12, 8),
            BackColor = _theme.SubPanel,
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand,
            Tag = radio,
        };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoSize = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        var preview = new ThemePreviewBox(value, _theme)
        {
            Width = 36,
            Height = 20,
            Margin = new Padding(0, 2, 10, 0),
        };
        var label = new Label
        {
            Text = string.IsNullOrWhiteSpace(subtitle) ? title : title + Environment.NewLine + subtitle,
            AutoSize = true,
            ForeColor = _theme.Text,
            Font = _layout.Font,
            Margin = new Padding(0, 0, 0, 0),
        };
        flow.Controls.Add(radio);
        flow.Controls.Add(preview);
        flow.Controls.Add(label);
        card.Controls.Add(flow);
        card.Click += (_, _) => radio.Checked = true;
        flow.Click += (_, _) => radio.Checked = true;
        preview.Click += (_, _) => radio.Checked = true;
        label.Click += (_, _) => radio.Checked = true;
        return card;
    }

    private void SystemThemeChanged(object? sender, EventArgs e)
    {
        if (_updatingSystemThemeSelection) return;
        if (sender is not RadioButton changed || !changed.Checked)
        {
            UpdateSystemThemeCards();
            return;
        }

        // Each WinForms card owns its child RadioButton, so native RadioButton grouping
        // does not work here. Keep the Web-modal contract: current/light/dark are
        // exclusive state choices, not independent checkboxes.
        _updatingSystemThemeSelection = true;
        try
        {
            foreach (var radio in new[] { _radThemeCurrent, _radThemeLight, _radThemeDark })
            {
                if (!ReferenceEquals(radio, changed)) radio.Checked = false;
            }
        }
        finally
        {
            _updatingSystemThemeSelection = false;
        }

        UpdateSystemThemeCards();
        if (_loaded is not null && _genreColorList.Controls.Count > 0)
            PopulateGenreColorGrid(GetActiveGenreColors(_loaded), GetDefaultGenreColorsForCurrentTheme());
    }

    private void ApplySystemThemeToControls(string? value)
    {
        var normalized = NormalizeSystemTheme(value);
        _updatingSystemThemeSelection = true;
        try
        {
            _radThemeCurrent.Checked = normalized == "current";
            _radThemeLight.Checked = normalized == "light";
            _radThemeDark.Checked = normalized == "dark";
        }
        finally
        {
            _updatingSystemThemeSelection = false;
        }
        UpdateSystemThemeCards();
    }

    private string CollectSystemThemeFromControls()
    {
        if (_radThemeDark.Checked) return "dark";
        if (_radThemeLight.Checked) return "light";
        return "current";
    }

    private Dictionary<string, string> GetActiveGenreColors(SettingsApiDto dto)
    {
        if (_radThemeDark.Checked)
            return new Dictionary<string, string>(dto.DarkGenreColors.Count > 0 ? dto.DarkGenreColors : (dto.ThemeGenrePalettes.TryGetValue("dark", out var dark) ? dark : dto.GenreColors), StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, string>(dto.LightGenreColors.Count > 0 ? dto.LightGenreColors : dto.GenreColors, StringComparer.OrdinalIgnoreCase);
    }

    private void UpdateSystemThemeCards()
    {
        foreach (var card in ControlsOfType<Panel>(this).Where(p => p.Tag is RadioButton))
        {
            var radio = (RadioButton)card.Tag!;
            card.BackColor = radio.Checked ? Color.FromArgb(224, 244, 255) : _theme.SubPanel;
        }
    }

    private static IEnumerable<T> ControlsOfType<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T t) yield return t;
            foreach (var nested in ControlsOfType<T>(child)) yield return nested;
        }
    }

    private void ConfigureGenreColorListHost()
    {
        if (_genreColorList.ColumnCount == _layout.GenreColorColumns) return;
        _genreColorList.Dock = DockStyle.Fill;
        _genreColorList.ColumnCount = _layout.GenreColorColumns;
        _genreColorList.RowCount = 0;
        _genreColorList.BackColor = _theme.Panel;
        _genreColorList.Margin = new Padding(0);
        _genreColorList.Padding = new Padding(0);
        _genreColorList.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        _genreColorList.CellBorderStyle = TableLayoutPanelCellBorderStyle.None;
        _genreColorList.ColumnStyles.Clear();
        for (var i = 0; i < _layout.GenreColorColumns; i++)
            _genreColorList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / _layout.GenreColorColumns));
    }

    private Control CreateGenreColorRow(string key, string colorText)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            BackColor = _theme.Panel,
            Margin = new Padding(0, 0, _layout.GenreColorColumnGap, 0),
            Padding = _layout.GenreColorRowPadding,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _layout.GenreColorButtonColumnWidth));
        var label = new Label
        {
            Text = GenreDisplayName(key),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = _theme.Text,
            BackColor = _theme.Panel,
            Font = _layout.Font,
            Margin = new Padding(0),
        };
        var button = new Button
        {
            Text = "",
            Width = _layout.GenreColorButtonWidth,
            Height = _layout.GenreColorButtonHeight,
            Anchor = AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0),
            Tag = colorText,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderColor = _theme.Border;
        button.Click += (_, _) => PickGenreColor(key, button);
        UpdateGenreColorButton(button, colorText);
        _genreColorButtons[key] = button;
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(button, 1, 0);
        return row;
    }

    private void PickGenreColor(string key, Button button)
    {
        var current = NormalizeHexColor(button.Tag?.ToString(), "#CCCCCC") ?? "#CCCCCC";
        var editingTheme = CollectSystemThemeFromControls() == "dark" ? "dark" : "light";
        using var dialog = new GenrePresetPaletteDialog(GenrePresetPaletteForTheme(editingTheme), current, _theme);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        UpdateGenreColorButton(button, dialog.SelectedHexColor);
    }

    private void UpdateGenreColorButton(Button button, string colorText)
    {
        var normalized = NormalizeHexColor(colorText, "#CCCCCC") ?? "#CCCCCC";
        button.Tag = normalized;
        if (TryParseColor(normalized, out var color))
        {
            button.BackColor = color;
            button.ForeColor = GetReadableTextColor(color);
        }
        button.Text = string.Empty;
        button.Font = new Font("Meiryo", 7.2F, FontStyle.Regular);
        button.AccessibleName = normalized;
    }

    private static IReadOnlyList<string> GenrePresetPaletteForTheme(string theme)
    {
        return string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
            ? DarkGenrePresetColors
            : LightGenrePresetColors;
    }

    private Dictionary<string, string> GetDefaultGenreColorsForCurrentTheme()
    {
        return _radThemeDark.Checked
            ? IniSettingsService.CreateDarkGenreColors()
            : IniSettingsService.CreateLightGenreColors();
    }

    private static readonly string[] LightGenrePresetColors = new[]
    {
        "#D3FFCB", "#FFCBEE", "#B8F0AC", "#FFBBBB",
        "#B4F2FF", "#FAFFB4", "#CBFCF4", "#DCDCFE",
        "#F0F0F0", "#FFF0C2", "#FFD8B4", "#C8E0FF",
        "#E6D0FF", "#C9FFD9", "#FFE1EC", "#E8E8E8",
    };

    private static readonly string[] DarkGenrePresetColors = new[]
    {
        "#1F5A45", "#245C7A", "#2D6F61", "#6B3341",
        "#286B78", "#6B5A24", "#563A73", "#394F95",
        "#3F5366", "#7A5F2A", "#704456", "#2F6670",
        "#664985", "#35705E", "#7A3F4E", "#4A5058",
    };

    private void BuildGenreColorGrid()
    {
        if (_gridGenreColors.Columns.Count > 0) return;
        _gridGenreColors.Dock = DockStyle.Fill;
        _gridGenreColors.AllowUserToAddRows = false;
        _gridGenreColors.AllowUserToDeleteRows = false;
        _gridGenreColors.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridGenreColors.RowHeadersVisible = false;
        _gridGenreColors.BackgroundColor = _theme.Panel;
        _gridGenreColors.BorderStyle = BorderStyle.None;
        _gridGenreColors.GridColor = _theme.BorderSoft;
        _gridGenreColors.EnableHeadersVisualStyles = false;
        _gridGenreColors.ColumnHeadersDefaultCellStyle.BackColor = _theme.SubPanel;
        _gridGenreColors.ColumnHeadersDefaultCellStyle.ForeColor = _theme.Text;
        _gridGenreColors.ColumnHeadersDefaultCellStyle.Font = new Font("Meiryo", 9F, FontStyle.Bold);
        _gridGenreColors.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _gridGenreColors.DefaultCellStyle.BackColor = _theme.Panel;
        _gridGenreColors.DefaultCellStyle.ForeColor = _theme.Text;
        _gridGenreColors.DefaultCellStyle.SelectionBackColor = _theme.MenuSelected;
        _gridGenreColors.DefaultCellStyle.SelectionForeColor = _theme.Text;
        _gridGenreColors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", HeaderText = "Key", Visible = false });
        _gridGenreColors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Genre", HeaderText = "ジャンル", ReadOnly = true, FillWeight = 160 });
        _gridGenreColors.Columns.Add(new DataGridViewTextBoxColumn { Name = "Color", HeaderText = "色（#RRGGBB）", FillWeight = 90 });
        _gridGenreColors.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _gridGenreColors.Columns[e.ColumnIndex].Name != "Color") return;
            var value = _gridGenreColors.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString() ?? string.Empty;
            if (!TryParseColor(value, out var color) || e.CellStyle is null) return;
            e.CellStyle.BackColor = color;
            e.CellStyle.ForeColor = GetReadableTextColor(color);
        };
    }

    private void PopulateGenreColorGrid(Dictionary<string, string> colors, Dictionary<string, string> defaults)
    {
        ConfigureGenreColorListHost();
        _genreColorList.SuspendLayout();
        _genreColorList.Controls.Clear();
        _genreColorButtons.Clear();
        var keys = GenreOrder(colors, defaults);
        var rows = Math.Max(_layout.GenreColorRowsPerColumn, (int)Math.Ceiling(keys.Count / (double)_layout.GenreColorColumns));
        _genreColorList.RowCount = rows;
        _genreColorList.RowStyles.Clear();
        for (var i = 0; i < rows; i++) _genreColorList.RowStyles.Add(new RowStyle(SizeType.Absolute, _layout.GenreColorRowHeight));
        if (_genreColorList.Parent is Control host)
            host.Height = rows * _layout.GenreColorRowHeight;
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var value = colors.TryGetValue(key, out var current) ? current : (defaults.TryGetValue(key, out var def) ? def : "#CCCCCC");
            var row = CreateGenreColorRow(key, NormalizeHexColor(value, "#CCCCCC") ?? "#CCCCCC");
            _genreColorList.Controls.Add(row, i / rows, i % rows);
        }
        _genreColorList.ResumeLayout();
    }

    private static List<string> GenreOrder(Dictionary<string, string> colors, Dictionary<string, string> defaults)
    {
        // Match the Web modal two-column visual order: left = News/Info/Music/Movie/Documentary,
        // right = Sports/Drama/Variety/Anime/Other.
        var preferred = new[] { "g-news", "g-info", "g-music", "g-movie", "g-docu", "g-sports", "g-drama", "g-variety", "g-anime", "g-other" };
        var all = colors.Keys.Concat(defaults.Keys).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = preferred.Where(p => all.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        result.AddRange(all.Where(k => !result.Contains(k, StringComparer.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private Dictionary<string, string> CollectGenreColorRows()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_genreColorButtons.Count > 0)
        {
            foreach (var item in _genreColorButtons)
            {
                var value = NormalizeHexColor(item.Value.Tag?.ToString(), null);
                if (value is not null) dict[item.Key] = value;
            }
            return dict;
        }

        foreach (DataGridViewRow row in _gridGenreColors.Rows)
        {
            if (row.IsNewRow) continue;
            var key = row.Cells[0].Value?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) continue;
            var value = NormalizeHexColor(row.Cells[2].Value?.ToString(), null);
            if (value is not null) dict[key] = value;
        }
        return dict;
    }

    private void ResetGenreColorsToDefault()
    {
        if (_loaded is null) return;
        var defaults = GetDefaultGenreColorsForCurrentTheme();
        PopulateGenreColorGrid(defaults, defaults);
    }

    private void ConfigureActionButton(Button button, string text, bool primary)
    {
        button.Text = text;
        button.Width = 104;
        button.Height = 30;
        button.Margin = new Padding(8, 0, 0, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.Font = _layout.Font;
        button.BackColor = primary ? _theme.ButtonPrimary : _theme.ButtonSecondary;
        button.ForeColor = primary ? _theme.ButtonPrimaryText : _theme.Text;
        button.FlatAppearance.BorderColor = primary ? _theme.Accent : _theme.Border;
    }

    private void SetNumber(TextBox box, int value)
    {
        var range = GetRange(box);
        box.Text = Math.Max(range.Min, Math.Min(range.Max, value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private int ReadNumber(TextBox box)
    {
        var range = GetRange(box);
        if (!int.TryParse((box.Text ?? string.Empty).Trim(), out var value)) value = range.Min;
        value = Math.Max(range.Min, Math.Min(range.Max, value));
        box.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return value;
    }

    private static (int Min, int Max) GetRange(TextBox box)
        => box.Tag is Tuple<int, int> range ? (range.Item1, range.Item2) : (int.MinValue, int.MaxValue);

    private static int ReadSliderInt(SettingsSliderControl slider, int fallback)
        => int.TryParse(slider.SelectedValue, out var value) ? value : fallback;

    private static string NormalizeEpgDepth(string? value)
        => (value ?? "medium").Trim().ToLowerInvariant() switch
        {
            "shallow" => "shallow",
            "deep" => "deep",
            "deeper" => "deeper",
            _ => "medium",
        };

    private static string NormalizeSystemTheme(string? value)
        => (value ?? "current").Trim().ToLowerInvariant() switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "current",
        };

    private static string RecordingAfterActionToDisplay(string? value)
        => (value ?? "none").Trim().ToLowerInvariant() switch
        {
            "sleep" => "スリープ",
            "shutdown" => "シャットダウン",
            _ => "何もしない",
        };

    private static string RecordingAfterActionToValue(string? display)
        => (display ?? "何もしない").Trim() switch
        {
            "スリープ" => "sleep",
            "シャットダウン" => "shutdown",
            _ => "none",
        };

    private static string NextDid(int index)
    {
        var i = Math.Max(0, index);
        return ((char)('A' + (i % 26))).ToString();
    }

    private static string TunerGroupToDisplay(string? group)
        => TunerDisplayName.NormalizeGroup(group) switch
        {
            "GR" => "地上波",
            "BSCS" => "BS/CS",
            "HYBRID" => "地デジ/BS/CS",
            var raw => raw ?? string.Empty,
        };

    private static string TunerGroupToValue(string? display) => TunerDisplayName.NormalizeGroup(display);

    private static string TunerRoleToDisplay(string? role)
        => IniSettingsService.NormalizeTunerRole(role) switch
        {
            "Recording" => "録画用",
            "Viewing" => "視聴用",
            var raw => raw ?? string.Empty,
        };

    private static string TunerRoleToValue(string? display)
        => (display ?? string.Empty).Trim() switch
        {
            "録画用" => "Recording",
            "視聴用" => "Viewing",
            var raw => raw,
        };

    private static string GenreDisplayName(string key)
    {
        var normalized = (key ?? string.Empty).Trim();
        return normalized.ToLowerInvariant() switch
        {
            "g-anime" => "アニメ",
            "g-docu" => "ドキュメンタリー",
            "g-drama" => "ドラマ",
            "g-info" => "情報",
            "g-movie" => "映画",
            "g-music" => "音楽",
            "g-news" => "ニュース",
            "g-other" => "その他",
            "g-sports" => "スポーツ",
            "g-variety" => "バラエティ",
            _ => normalized,
        };
    }

    private static string? NormalizeHexColor(string? value, string? fallback)
    {
        var text = (value ?? string.Empty).Trim();
        if (Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$")) return text.ToUpperInvariant();
        if (Regex.IsMatch(text, "^[0-9a-fA-F]{6}$")) return ("#" + text).ToUpperInvariant();
        return fallback;
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = Color.Empty;
        var normalized = NormalizeHexColor(value, null);
        if (normalized is null) return false;
        try
        {
            color = ColorTranslator.FromHtml(normalized);
            return true;
        }
        catch { return false; }
    }

    private static Color GetReadableTextColor(Color color)
    {
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luminance < 0.55 ? Color.White : Color.FromArgb(31, 41, 55);
    }

    private enum BrowseTarget { Folder, TvTestExe, Ch2File, ChSetFile }
}

internal enum SettingsFieldIntent
{
    VirtualSlot,
    BonDriver,
    BroadcastWave,
    TunerPurpose,
    InternalPhysicalId,
    OperationAction,
}

internal sealed class SettingsLayoutSpec
{
    public Padding WindowPadding { get; private init; }
    public int NavWidth { get; private init; }
    public int NavButtonWidth { get; private init; }
    public int NavButtonHeight { get; private init; }
    public int LabelWidth { get; private init; }
    public int TextBoxWidth { get; private init; }
    public int BrowseColumnWidth { get; private init; }
    public int SectionGap { get; private init; }
    public int NoteWidth { get; private init; }
    public int GenreColorColumns { get; private init; }
    public int GenreColorRowsPerColumn { get; private init; }
    public int GenreColorRowHeight { get; private init; }
    public int GenreColorColumnGap { get; private init; }
    public int GenreColorButtonColumnWidth { get; private init; }
    public int GenreColorButtonWidth { get; private init; }
    public int GenreColorButtonHeight { get; private init; }
    public Padding GenreColorRowPadding { get; private init; }
    public Padding SectionPadding { get; private init; }
    public Font Font { get; private init; } = new("Meiryo", 9F);
    public Font SectionTitleFont { get; private init; } = new("Meiryo", 9.2F, FontStyle.Bold);

    public static SettingsLayoutSpec Default() => new()
    {
        WindowPadding = new Padding(12, 12, 12, 10),
        NavWidth = 154,
        NavButtonWidth = 136,
        NavButtonHeight = 32,
        LabelWidth = 190,
        TextBoxWidth = 520,
        BrowseColumnWidth = 68,
        SectionGap = 10,
        NoteWidth = 720,
        GenreColorColumns = 2,
        GenreColorRowsPerColumn = 5,
        GenreColorRowHeight = 34,
        GenreColorColumnGap = 24,
        GenreColorButtonColumnWidth = 64,
        GenreColorButtonWidth = 48,
        GenreColorButtonHeight = 26,
        GenreColorRowPadding = new Padding(6, 3, 4, 3),
        SectionPadding = new Padding(14, 12, 14, 12),
        Font = new Font("Meiryo", 9F),
        SectionTitleFont = new Font("Meiryo", 9.2F, FontStyle.Bold),
    };
}

internal sealed class ThemePreviewBox : Control
{
    private readonly string _mode;
    private readonly SettingsThemePalette _theme;

    public ThemePreviewBox(string mode, SettingsThemePalette theme)
    {
        _mode = mode;
        _theme = theme;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var rect = ClientRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var borderPen = new Pen(_theme.ThemeSampleFrame);
        using var frameSoftPen = new Pen(_theme.ThemeSampleFrameSoft);
        using var dividerPen = new Pen(_theme.ThemeSampleDivider, 2F);
        using var light = new SolidBrush(_theme.ThemeSampleLightSurface);
        using var dark = new SolidBrush(_theme.ThemeSampleDarkSurface);
        if (_mode == "current")
        {
            e.Graphics.FillRectangle(light, 0, 0, rect.Width / 2, rect.Height);
            e.Graphics.FillRectangle(dark, rect.Width / 2, 0, rect.Width - rect.Width / 2, rect.Height);
            var dividerX = rect.Width / 2;
            e.Graphics.DrawLine(dividerPen, dividerX, 2, dividerX, rect.Height - 3);
        }
        else if (_mode == "dark")
        {
            e.Graphics.FillRectangle(dark, rect);
        }
        else
        {
            e.Graphics.FillRectangle(light, rect);
        }
        var inner = new Rectangle(3, 3, Math.Max(0, rect.Width - 7), Math.Max(0, rect.Height - 7));
        if (inner.Width > 0 && inner.Height > 0) e.Graphics.DrawRectangle(frameSoftPen, inner);
        e.Graphics.DrawRectangle(borderPen, 0, 0, rect.Width - 1, rect.Height - 1);
    }
}

internal sealed class GenrePresetPaletteDialog : Form
{
    private readonly SettingsThemePalette _theme;
    public string SelectedHexColor { get; private set; }

    public GenrePresetPaletteDialog(IReadOnlyList<string> colors, string currentHexColor, SettingsThemePalette theme)
    {
        _theme = theme;
        SelectedHexColor = NormalizeHexColor(currentHexColor, colors.Count > 0 ? colors[0] : "#CCCCCC") ?? "#CCCCCC";
        Text = "ジャンル色の選択";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(184, 150);
        BackColor = _theme.Panel;
        ForeColor = _theme.Text;
        Font = new Font("Meiryo UI", 9F, FontStyle.Regular);

        var title = new Label
        {
            Text = "テーマ別プリセット色",
            Dock = DockStyle.Top,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            ForeColor = _theme.Text,
            BackColor = _theme.Panel,
        };
        Controls.Add(title);

        var grid = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = 4,
            Dock = DockStyle.Top,
            Height = 112,
            Padding = new Padding(8, 4, 8, 8),
            BackColor = _theme.Panel,
        };
        for (var i = 0; i < 4; i++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        }

        for (var i = 0; i < Math.Min(16, colors.Count); i++)
        {
            var normalized = NormalizeHexColor(colors[i], "#CCCCCC") ?? "#CCCCCC";
            var button = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(3),
                FlatStyle = FlatStyle.Flat,
                BackColor = ColorTranslator.FromHtml(normalized),
                Tag = normalized,
                Text = string.Empty,
                Cursor = Cursors.Hand,
                AccessibleName = normalized,
            };
            button.FlatAppearance.BorderColor = string.Equals(normalized, SelectedHexColor, StringComparison.OrdinalIgnoreCase)
                ? _theme.Text
                : _theme.Border;
            button.FlatAppearance.BorderSize = string.Equals(normalized, SelectedHexColor, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            button.Click += (_, _) =>
            {
                SelectedHexColor = normalized;
                DialogResult = DialogResult.OK;
                Close();
            };
            grid.Controls.Add(button, i % 4, i / 4);
        }
        Controls.Add(grid);
    }

    private static string? NormalizeHexColor(string? value, string? fallback)
    {
        var raw = (value ?? string.Empty).Trim();
        var match = Regex.Match(raw, "^#?([0-9a-fA-F]{6})$");
        if (match.Success) return "#" + match.Groups[1].Value.ToUpperInvariant();
        return fallback;
    }
}

internal sealed class SettingsSliderControl : Control
{
    private readonly string[] _labels;
    private readonly string[] _values;
    private readonly SettingsThemePalette _theme;
    private int _selectedIndex;

    public SettingsSliderControl(string[] labels, string[] values, SettingsThemePalette theme)
    {
        _labels = labels;
        _values = values;
        _theme = theme;
        if (_labels.Length == 0 || _labels.Length != _values.Length) throw new ArgumentException("Slider labels and values must be non-empty and have the same length.");
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Height = 70;
        MinimumSize = new Size(360, 70);
        Margin = new Padding(0);
        Font = new Font("Meiryo", 8.5F);
        BackColor = theme.Panel;
        ForeColor = theme.TextSub;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public string SelectedValue
    {
        get => _values[Math.Max(0, Math.Min(_selectedIndex, _values.Length - 1))];
        set
        {
            var idx = Array.FindIndex(_values, v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            if (_selectedIndex == idx) return;
            _selectedIndex = idx;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Capture = true;
        SetIndexFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Capture && e.Button == MouseButtons.Left) SetIndexFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!Capture) return;
        SetIndexFromX(e.X);
        Capture = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down)
        {
            _selectedIndex = Math.Max(0, _selectedIndex - 1);
            Invalidate();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up)
        {
            _selectedIndex = Math.Min(_values.Length - 1, _selectedIndex + 1);
            Invalidate();
            e.Handled = true;
        }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData);
    }

    private Rectangle GetTrackBounds()
    {
        const int thumbRadius = 9;
        var left = thumbRadius + 4;
        var width = Math.Max(160, Width - (thumbRadius * 2) - 12);
        return new Rectangle(left, 26, width, 6);
    }

    private void SetIndexFromX(int x)
    {
        var track = GetTrackBounds();
        var ratio = track.Width <= 0 ? 0 : (double)(x - track.Left) / track.Width;
        var idx = (int)Math.Round(Math.Max(0, Math.Min(1, ratio)) * (_values.Length - 1));
        idx = Math.Max(0, Math.Min(_values.Length - 1, idx));
        if (_selectedIndex == idx) return;
        _selectedIndex = idx;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var track = GetTrackBounds();
        var x = PositionX(track, _selectedIndex);
        using (var restBrush = new SolidBrush(_theme.SliderRest))
        using (var activeBrush = new SolidBrush(_theme.SliderActive))
        {
            e.Graphics.FillRectangle(restBrush, track);
            e.Graphics.FillRectangle(activeBrush, new Rectangle(track.Left, track.Top, Math.Max(0, x - track.Left), track.Height));
        }
        using (var borderPen = new Pen(_theme.BorderSoft))
        {
            e.Graphics.DrawRectangle(borderPen, track.Left, track.Top, track.Width, track.Height);
        }

        var thumbRect = new Rectangle(x - 9, track.Top - 7, 18, 20);
        using (var thumbBrush = new SolidBrush(_theme.SliderThumb))
        using (var thumbPen = new Pen(_theme.SliderActive, 2))
        {
            e.Graphics.FillEllipse(thumbBrush, thumbRect);
            e.Graphics.DrawEllipse(thumbPen, thumbRect);
        }

        for (var i = 0; i < _labels.Length; i++)
        {
            var lx = PositionX(track, i);
            var labelRect = new Rectangle(lx - 54, track.Bottom + 12, 108, 22);
            TextRenderer.DrawText(e.Graphics, _labels[i], Font, labelRect, _theme.TextSub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        var currentRect = new Rectangle(track.Left, 2, track.Width, 20);
        using var bold = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, _labels[_selectedIndex], bold, currentRect, _theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private int PositionX(Rectangle track, int index)
    {
        if (_values.Length <= 1) return track.Left;
        return track.Left + (int)Math.Round(track.Width * ((double)index / (_values.Length - 1)));
    }
}

internal sealed class SettingsThemePalette
{
    public Color Page { get; private init; }
    public Color Sidebar { get; private init; }
    public Color Panel { get; private init; }
    public Color SubPanel { get; private init; }
    public Color Input { get; private init; }
    public Color Border { get; private init; }
    public Color BorderSoft { get; private init; }
    public Color Text { get; private init; }
    public Color TextSub { get; private init; }
    public Color TextMuted { get; private init; }
    public Color Accent { get; private init; }
    public Color AccentText { get; private init; }
    public Color Focus { get; private init; }
    public Color Menu { get; private init; }
    public Color MenuSelected { get; private init; }
    public Color ButtonPrimary { get; private init; }
    public Color ButtonPrimaryText { get; private init; }
    public Color ButtonSecondary { get; private init; }
    public Color ActionDanger { get; private init; }
    public Color ActionDangerSoftBack { get; private init; }
    public Color SliderActive { get; private init; }
    public Color SliderRest { get; private init; }
    public Color SliderThumb { get; private init; }
    public Color ThemeSampleLightSurface { get; private init; }
    public Color ThemeSampleDarkSurface { get; private init; }
    public Color ThemeSampleFrame { get; private init; }
    public Color ThemeSampleFrameSoft { get; private init; }
    public Color ThemeSampleDivider { get; private init; }
    public Color ThemeSampleInset { get; private init; }

    // WinForms cannot consume CSS variables directly. This palette mirrors the
    // neutral light-theme meaning used by TvAIr Web UI: surface, text, border,
    // selected, active. No saturated fixed blue is used here.
    public static SettingsThemePalette Light() => new()
    {
        Page = Color.FromArgb(246, 247, 249),
        Sidebar = Color.FromArgb(246, 247, 249),
        Panel = Color.FromArgb(255, 255, 255),
        SubPanel = Color.FromArgb(241, 243, 246),
        Input = Color.FromArgb(255, 255, 255),
        Border = Color.FromArgb(198, 204, 213),
        BorderSoft = Color.FromArgb(226, 230, 235),
        Text = Color.FromArgb(32, 38, 46),
        TextSub = Color.FromArgb(72, 80, 91),
        TextMuted = Color.FromArgb(116, 124, 136),
        Accent = Color.FromArgb(91, 103, 118),
        AccentText = Color.FromArgb(45, 54, 66),
        Focus = Color.FromArgb(165, 174, 186),
        Menu = Color.FromArgb(246, 247, 249),
        MenuSelected = Color.FromArgb(229, 235, 244),
        ButtonPrimary = Color.FromArgb(88, 98, 112),
        ButtonPrimaryText = Color.White,
        ButtonSecondary = Color.FromArgb(246, 247, 249),
        ActionDanger = Color.FromArgb(150, 54, 54),
        ActionDangerSoftBack = Color.FromArgb(255, 244, 244),
        SliderActive = Color.FromArgb(88, 98, 112),
        SliderRest = Color.FromArgb(225, 229, 235),
        SliderThumb = Color.FromArgb(255, 255, 255),
        ThemeSampleLightSurface = Color.FromArgb(255, 255, 255),
        ThemeSampleDarkSurface = Color.FromArgb(32, 31, 30),
        ThemeSampleFrame = Color.FromArgb(198, 204, 213),
        ThemeSampleFrameSoft = Color.FromArgb(116, 124, 136),
        ThemeSampleDivider = Color.FromArgb(0, 120, 212),
        ThemeSampleInset = Color.FromArgb(246, 247, 249),
    };
}

internal sealed class SettingsApiDto
{
    public string TvTestExecutablePath { get; set; } = "";
    public string BonDriverDirectory { get; set; } = "";
    public string ViewingTvTestExecutablePath { get; set; } = "";
    public string GrChannelFilePath { get; set; } = "";
    public string GrChSetFilePath { get; set; } = "";
    public string BscsChannelFilePath { get; set; } = "";
    public string BscsChSetFilePath { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string SystemTheme { get; set; } = "current";
    public int Port { get; set; } = 55884;
    public bool EpgEnabled { get; set; }
    public int EpgHour { get; set; }
    public int EpgMinute { get; set; }
    public string EpgDepth { get; set; } = "medium";
    public int EpgPreRecordMinutes { get; set; }
    public bool LaterProgramPriority { get; set; }
    public bool PseudoContinuousRecording { get; set; }
    public int PseudoContinuousMarginSeconds { get; set; }
    public int PreStartMarginSeconds { get; set; }
    public int PostEndMarginSeconds { get; set; }
    public int RecDelaySeconds { get; set; }
    public int WakeMinutesBefore { get; set; }
    public int WakeAdditionalSeconds { get; set; }
    public int TunerSlotCooldownMs { get; set; }
    public bool UseMinOption { get; set; }
    public bool UseNodshowOption { get; set; }
    public bool TvTestRecordCurServiceOnly { get; set; } = true;
    public bool TvTestRecordSubtitle { get; set; } = true;
    public bool TvTestRecordDataCarrousel { get; set; } = false;
    public bool ShowTvAIrEpgRecTaskbarIcon { get; set; } = true;
    public bool StartupEnabled { get; set; }
    public string RecordingAfterAction { get; set; } = "none";
    public int RecordingAfterActionDelayMinutes { get; set; } = 1;
    public bool EpgUseBelowNormalPriority { get; set; }
    public int EpgLaunchStaggerMs { get; set; }
    public int EpgPostLaunchStabilizeMs { get; set; }
    public bool EpgExcludeLiveTvTest { get; set; }
    public bool EpgDisableImmediateRetry { get; set; }
    public string TaskUserName { get; set; } = "";
    public string? TaskPasswordPlain { get; set; }
    public bool TaskHasPassword { get; set; }
    public Dictionary<string, string> GenreColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DefaultGenreColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> LightGenreColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DarkGenreColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> ThemeGenrePalettes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string EffectiveDataDirectory { get; set; } = "";
    public List<TunerProfileDto> Tuners { get; set; } = new();
    public List<string> BonDriverList { get; set; } = new();
    public bool IsFirstRun { get; set; }

    public SettingsApiDto Clone()
    {
        return new SettingsApiDto
        {
            TvTestExecutablePath = TvTestExecutablePath,
            BonDriverDirectory = BonDriverDirectory,
            ViewingTvTestExecutablePath = ViewingTvTestExecutablePath,
            GrChannelFilePath = GrChannelFilePath,
            GrChSetFilePath = GrChSetFilePath,
            BscsChannelFilePath = BscsChannelFilePath,
            BscsChSetFilePath = BscsChSetFilePath,
            DataDirectory = DataDirectory,
            SystemTheme = SystemTheme,
            EffectiveDataDirectory = EffectiveDataDirectory,
            Port = Port,
            EpgEnabled = EpgEnabled,
            EpgHour = EpgHour,
            EpgMinute = EpgMinute,
            EpgDepth = EpgDepth,
            EpgPreRecordMinutes = EpgPreRecordMinutes,
            LaterProgramPriority = LaterProgramPriority,
            PseudoContinuousRecording = PseudoContinuousRecording,
            PseudoContinuousMarginSeconds = PseudoContinuousMarginSeconds,
            PreStartMarginSeconds = PreStartMarginSeconds,
            PostEndMarginSeconds = PostEndMarginSeconds,
            RecDelaySeconds = RecDelaySeconds,
            WakeMinutesBefore = WakeMinutesBefore,
            WakeAdditionalSeconds = WakeAdditionalSeconds,
            TunerSlotCooldownMs = TunerSlotCooldownMs,
            UseMinOption = UseMinOption,
            UseNodshowOption = UseNodshowOption,
            TvTestRecordCurServiceOnly = TvTestRecordCurServiceOnly,
            TvTestRecordSubtitle = TvTestRecordSubtitle,
            TvTestRecordDataCarrousel = TvTestRecordDataCarrousel,
            ShowTvAIrEpgRecTaskbarIcon = ShowTvAIrEpgRecTaskbarIcon,
            StartupEnabled = StartupEnabled,
            RecordingAfterAction = RecordingAfterAction,
            RecordingAfterActionDelayMinutes = RecordingAfterActionDelayMinutes,
            EpgUseBelowNormalPriority = EpgUseBelowNormalPriority,
            EpgLaunchStaggerMs = EpgLaunchStaggerMs,
            EpgPostLaunchStabilizeMs = EpgPostLaunchStabilizeMs,
            EpgExcludeLiveTvTest = EpgExcludeLiveTvTest,
            EpgDisableImmediateRetry = EpgDisableImmediateRetry,
            TaskUserName = TaskUserName,
            TaskPasswordPlain = TaskPasswordPlain,
            TaskHasPassword = TaskHasPassword,
            GenreColors = new Dictionary<string, string>(GenreColors, StringComparer.OrdinalIgnoreCase),
            DefaultGenreColors = new Dictionary<string, string>(DefaultGenreColors, StringComparer.OrdinalIgnoreCase),
            LightGenreColors = new Dictionary<string, string>(LightGenreColors, StringComparer.OrdinalIgnoreCase),
            DarkGenreColors = new Dictionary<string, string>(DarkGenreColors, StringComparer.OrdinalIgnoreCase),
            ThemeGenrePalettes = ThemeGenrePalettes.ToDictionary(
                kv => kv.Key,
                kv => new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
            Tuners = Tuners.Select(t => new TunerProfileDto { Name = t.Name, Group = t.Group, Did = t.Did, BonDriverFileName = t.BonDriverFileName, Role = t.Role }).ToList(),
            BonDriverList = new List<string>(BonDriverList),
            IsFirstRun = IsFirstRun,
        };
    }
}
