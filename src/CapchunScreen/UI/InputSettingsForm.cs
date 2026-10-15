using CapchunScreen.Input;
using CapchunScreen.Storage;
using CapchunScreen.Capture;

namespace CapchunScreen.UI;

internal sealed class InputSettingsForm : BrandedForm
{
    private readonly AppSettings original;
    private readonly DesktopAccessGuard desktopAccess;
    private readonly Dictionary<string, bool> overrides;
    private readonly Dictionary<string, Dictionary<string, string>> descriptions;
    private readonly TextBox search = new() { Width = 250, PlaceholderText = "キー名・説明を検索", AccessibleName = "操作記録を検索" };
    private readonly ComboBox state = Choice("記録状態", "すべて", "有効", "無効");
    private readonly ComboBox app = Choice("説明のアプリ", "すべての説明", "標準");
    private readonly CheckBox plain = new() { Text = "単独の文字・数字も既定で記録", AutoSize = true };
    private readonly CheckBox describedOnly = new() { Text = "説明がある操作のみ", AutoSize = true };
    private readonly List<(CheckBox Box, InputModifiers Modifier)> modifiers = [];
    private readonly CheckBox noModifier = new() { Text = "修飾なし", AutoSize = true };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AccessibleName = "操作記録対象一覧", VirtualMode = true
    };
    private readonly Label message = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly TextBox detected = new() { ReadOnly = true, Width = 280, AccessibleName = "検出された入力" };
    private readonly Button testButton = ActionButton("入力テスト開始");
    private readonly System.Windows.Forms.Timer testTimer = new() { Interval = 100 };
    private readonly ToolTip help = new() { AutoPopDelay = 15000 };
    private List<InputShortcut> visible = [];
    private InputMonitor? monitor;
    private string? latestInput;
    internal AppSettings Settings { get; private set; }
    internal bool Testing => monitor is not null;

    internal InputSettingsForm(AppSettings settings, DesktopAccessGuard? desktopAccess = null)
    {
        this.desktopAccess = desktopAccess ?? DesktopAccessGuard.Shared;
        original = settings;
        Settings = settings;
        overrides = new(settings.InputRecordOverrides ?? [], StringComparer.Ordinal);
        descriptions = (settings.InputDescriptions ?? []).Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => new Dictionary<string, string>(pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        Text = WindowTitle.Create("操作記録の設定");
        ClientSize = new Size(980, 720); MinimumSize = new Size(820, 650);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false;
        plain.Checked = settings.IncludePlainKeys;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(12) };
        foreach (int height in new[] { 44, 40, 34, 34 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 42, 42, 44, 44 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = "TSVへ保存するキー・マウス操作を選びます。個別設定は既定値より優先します。\n入力テストは開始中だけ入力名を確認し、ファイルには保存しません。", Dock = DockStyle.Fill }, 0, 0);
        root.Controls.Add(Row(new Label { Text = "検索", AutoSize = true }, search, state, app), 0, 1);
        var filters = Row();
        foreach (var (text, flag) in new[] { ("Ctrl", InputModifiers.Control), ("Shift", InputModifiers.Shift), ("Alt", InputModifiers.Alt), ("Win", InputModifiers.Windows) })
        {
            var box = new CheckBox { Text = text, AutoSize = true };
            modifiers.Add((box, flag)); filters.Controls.Add(box);
            box.CheckedChanged += (_, _) => RefreshRows();
        }
        filters.Controls.AddRange([noModifier, describedOnly]); root.Controls.Add(filters, 0, 2);
        root.Controls.Add(plain, 0, 3);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", HeaderText = "キー・操作", FillWeight = 36, ReadOnly = true });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Record", HeaderText = "記録", FillWeight = 12 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", HeaderText = "説明", FillWeight = 52, ReadOnly = true });
        grid.CellValueNeeded += (_, args) =>
        {
            if (args.RowIndex < 0 || args.RowIndex >= visible.Count) return;
            var item = visible[args.RowIndex];
            args.Value = args.ColumnIndex switch { 0 => item.Combination, 1 => EnabledFor(item), _ => DescriptionFor(item) };
        };
        grid.CellValuePushed += (_, args) =>
        {
            if (args.RowIndex < 0 || args.RowIndex >= visible.Count || args.ColumnIndex != 1) return;
            SetEnabled(visible[args.RowIndex], args.Value is true); grid.Invalidate();
        };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        root.Controls.Add(grid, 0, 4);
        var actions = Row();
        Add(actions, "選択を有効", () => ChangeSelected(true)); Add(actions, "選択を無効", () => ChangeSelected(false));
        Add(actions, "表示分をすべて有効", () => ChangeVisible(true)); Add(actions, "表示分をすべて無効", () => ChangeVisible(false));
        Add(actions, "個別設定をリセット", () => { overrides.Clear(); RefreshRows(); }); root.Controls.Add(actions, 0, 5);
        var imports = Row();
        Add(imports, "旧shortcuts.tsvを取り込む…", ImportRecording);
        Add(imports, "アプリ別の説明TSVを追加…", ImportDescriptions); root.Controls.Add(imports, 0, 6);
        testButton.Click += (_, _) => Guard(ToggleTest);
        root.Controls.Add(Row(testButton, new Label { Text = "検出した入力", AutoSize = true }, detected), 0, 7);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(message, 0, 0);
        var close = Row(); var save = ActionButton("保存"); var cancel = ActionButton("キャンセル");
        cancel.DialogResult = DialogResult.Cancel;
        save.Click += (_, _) => Guard(() => { StopTest(); Settings = BuildSettings(); DialogResult = DialogResult.OK; Close(); });
        close.Controls.AddRange([save, cancel]); footer.Controls.Add(close, 1, 0); root.Controls.Add(footer, 0, 8);
        AcceptButton = save; CancelButton = cancel;
        foreach (string name in descriptions.Keys.Order(StringComparer.Ordinal)) app.Items.Add(name);
        search.TextChanged += (_, _) => RefreshRows(); state.SelectedIndexChanged += (_, _) => RefreshRows(); app.SelectedIndexChanged += (_, _) => RefreshRows();
        plain.CheckedChanged += (_, _) => RefreshRows(); noModifier.CheckedChanged += (_, _) => RefreshRows(); describedOnly.CheckedChanged += (_, _) => RefreshRows();
        help.SetToolTip(plain, "個別に指定していない文字・数字キーの既定値です。個別の有効・無効設定を優先します。");
        help.SetToolTip(app, "説明の参照先です。録画対象のアプリを自動判定する設定ではありません。");
        help.SetToolTip(grid, "記録欄をクリックして切り替えます。Ctrl／Shiftで複数行を選び、一括変更できます。Ctrl+Shift+F9は録画操作用なので記録しません。");
        testTimer.Tick += (_, _) =>
        {
            if (!this.desktopAccess.Check().Available)
            { StopTest(); message.Text = DesktopAccessGuard.UnavailableMessage; return; }
            if (Volatile.Read(ref latestInput) is { } value) detected.Text = value;
        };
        FormClosing += (_, _) => StopTest(); RefreshRows();
    }

    internal AppSettings BuildSettings() => original with
    {
        IncludePlainKeys = plain.Checked, InputRecordOverrides = new(overrides, StringComparer.Ordinal),
        InputDescriptions = descriptions.ToDictionary(pair => pair.Key, pair => new Dictionary<string, string>(pair.Value), StringComparer.Ordinal)
    };

    private bool EnabledFor(InputShortcut item) => item.Combination != InputShortcutCatalog.ReservedHotkey
        && (overrides.TryGetValue(item.Combination, out bool enabled) ? enabled : item.DefaultEnabled(plain.Checked));

    private string DescriptionFor(InputShortcut item)
    {
        if (item.Combination == InputShortcutCatalog.ReservedHotkey) return "録画開始／停止専用（記録対象外）";
        string standard = InputShortcutCatalog.MouseDescription(item.Action);
        if (standard.Length == 0) standard = item.Combination switch
        { "Ctrl+C" => "コピー", "Ctrl+V" => "貼り付け", "Ctrl+X" => "切り取り", "Ctrl+Z" => "元に戻す", "Ctrl+S" => "保存", "Ctrl+A" => "すべて選択", _ => "" };
        string selected = (string?)app.SelectedItem ?? "すべての説明";
        if (selected == "標準") return standard;
        if (selected != "すべての説明") return descriptions.TryGetValue(selected, out var entries) && entries.TryGetValue(item.Combination, out var desc) ? desc : "";
        return string.Join(" / ", descriptions.Values.Select(values => values.GetValueOrDefault(item.Combination, "")).Prepend(standard).Where(value => !string.IsNullOrEmpty(value)).Distinct());
    }

    internal void RefreshRows()
    {
        if (grid.Columns.Count == 0) return;
        string text = search.Text.Trim();
        bool hasModifierFilter = noModifier.Checked || modifiers.Any(pair => pair.Box.Checked);
        visible = InputShortcutCatalog.All.Where(item =>
            (text.Length == 0 || item.Combination.Contains(text, StringComparison.OrdinalIgnoreCase) || DescriptionFor(item).Contains(text, StringComparison.OrdinalIgnoreCase))
            && (state.SelectedIndex == 0 || EnabledFor(item) == (state.SelectedIndex == 1))
            && (!describedOnly.Checked || DescriptionFor(item).Length > 0)
            && (!hasModifierFilter || noModifier.Checked && item.Modifiers == InputModifiers.None || modifiers.Any(pair => pair.Box.Checked && item.Modifiers.HasFlag(pair.Modifier)))).ToList();
        grid.RowCount = visible.Count; grid.Invalidate(); message.Text = $"{visible.Count}件表示 / 個別設定 {overrides.Count}件";
    }

    private void SetEnabled(InputShortcut item, bool enabled)
    { if (item.Combination != InputShortcutCatalog.ReservedHotkey) overrides[item.Combination] = enabled; }
    private void ChangeSelected(bool enabled)
    { foreach (DataGridViewRow row in grid.SelectedRows) if (row.Index < visible.Count) SetEnabled(visible[row.Index], enabled); RefreshRows(); }
    private void ChangeVisible(bool enabled)
    { foreach (var item in visible) SetEnabled(item, enabled); RefreshRows(); }
    private void ImportRecording() => Guard(() =>
    {
        using var picker = new OpenFileDialog { Filter = "ショートカット設定 (*.tsv)|*.tsv", Title = "旧版のshortcuts.tsvを選択", RestoreDirectory = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var imported = InputShortcutCatalog.ImportRecording(picker.FileName);
        foreach (var pair in imported) overrides[pair.Key] = pair.Value;
        RefreshRows(); message.Text = $"記録設定{imported.Count}件を取り込みました。保存で確定します。";
    });
    private void ImportDescriptions() => Guard(() =>
    {
        using var picker = new OpenFileDialog { Filter = "アプリ別説明 (*.tsv)|*.tsv", Title = "キー名と説明のTSVを選択", Multiselect = true, RestoreDirectory = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var imported = picker.FileNames.Select(path => (Name: Path.GetFileNameWithoutExtension(path), Entries: InputShortcutCatalog.ImportDescriptions(path))).ToArray();
        foreach (var item in imported)
        {
            descriptions[item.Name] = item.Entries;
            if (!app.Items.Contains(item.Name)) app.Items.Add(item.Name);
        }
        RefreshRows(); message.Text = "説明を追加しました。保存で確定します。";
    });

    internal void ToggleTest()
    {
        if (monitor is not null) { StopTest(); return; }
        Volatile.Write(ref latestInput, null); detected.Text = "";
        monitor = new InputMonitor(true, null, desktopAccess);
        monitor.InputDetected += value => Volatile.Write(ref latestInput, value);
        testTimer.Start(); testButton.Text = "入力テスト停止";
    }
    private void StopTest()
    {
        testTimer.Stop(); monitor?.Dispose(); monitor = null; testButton.Text = "入力テスト開始";
    }
    private void Guard(Action action)
    { try { action(); } catch (Exception error) { MessageBox.Show(this, error.Message, WindowTitle.Create("操作記録の設定"), MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
    private static ComboBox Choice(string name, params string[] values)
    { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, AccessibleName = name }; box.Items.AddRange(values); box.SelectedIndex = 0; return box; }
    private static Button ActionButton(string text) => new() { Text = text, AutoSize = true, Height = 32, Margin = new Padding(3) };
    private static FlowLayoutPanel Row(params Control[] controls)
    { var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false }; row.Controls.AddRange(controls); return row; }
    private static void Add(FlowLayoutPanel row, string text, Action action)
    { var button = ActionButton(text); button.Click += (_, _) => action(); row.Controls.Add(button); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { StopTest(); testTimer.Dispose(); help.Dispose(); }
        base.Dispose(disposing);
    }
}
