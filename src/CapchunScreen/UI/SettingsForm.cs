using System.Text.RegularExpressions;
using CapchunScreen.Recording;
using CapchunScreen.Storage;

namespace CapchunScreen.UI;

public sealed class SettingsForm : BrandedForm
{
    private readonly AppSettings _original;
    private readonly TextBox _outputDirectory;
    private readonly TextBox _ffmpegPath;
    private readonly CheckBox _includePlainKeys;

    public AppSettings Settings { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _original = settings;
        Settings = settings;
        Text = WindowTitle.Create("設定");
        AccessibleName = "CapchunScreenの設定";
        ClientSize = new Size(550, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 9,
            AutoScroll = true, TabIndex = 0
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        for (int row = 0; row < 7; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        var outputLabel = CreateLabel("録画の保存先(&D)", 0);
        layout.Controls.Add(outputLabel, 0, 0);
        layout.SetColumnSpan(outputLabel, 2);
        _outputDirectory = new TextBox
        {
            Text = settings.OutputDirectory, Dock = DockStyle.Fill, TabIndex = 1,
            AccessibleName = "録画の保存先", Margin = new Padding(0, 3, 8, 12)
        };
        var outputBrowse = CreateButton("参照(&B)", "保存先フォルダーを選択", 2);
        outputBrowse.Margin = new Padding(0, 0, 0, 9);
        outputBrowse.Click += (_, _) => BrowseOutputDirectory();
        layout.Controls.Add(_outputDirectory, 0, 1);
        layout.Controls.Add(outputBrowse, 1, 1);

        var ffmpegLabel = CreateLabel("FFmpeg の実行ファイル(&F)", 3);
        layout.Controls.Add(ffmpegLabel, 0, 2);
        layout.SetColumnSpan(ffmpegLabel, 2);
        _ffmpegPath = new TextBox
        {
            Text = FfmpegLocator.Find(settings.FfmpegPath) ?? settings.FfmpegPath,
            Dock = DockStyle.Fill, TabIndex = 4, AccessibleName = "FFmpegの実行ファイル",
            Margin = new Padding(0, 3, 8, 5)
        };
        var ffmpegBrowse = CreateButton("参照(&R)", "FFmpegの実行ファイルを選択", 5);
        ffmpegBrowse.Click += (_, _) => BrowseFfmpeg();
        layout.Controls.Add(_ffmpegPath, 0, 3);
        layout.Controls.Add(ffmpegBrowse, 1, 3);

        var ffmpegHelp = CreateLabel("録画には ffmpeg.exe が必要です。未設定の場合は「参照」で指定してください。", 6);
        ffmpegHelp.Margin = new Padding(0, 0, 0, 16);
        layout.Controls.Add(ffmpegHelp, 0, 4);
        layout.SetColumnSpan(ffmpegHelp, 2);
        _includePlainKeys = new CheckBox
        {
            Text = "単独の文字・数字キーも記録する(&K)", Checked = settings.IncludePlainKeys,
            AutoSize = true, Dock = DockStyle.Fill, TabIndex = 7,
            AccessibleName = "単独の文字・数字キーも記録する", Margin = new Padding(0, 0, 0, 5)
        };
        layout.Controls.Add(_includePlainKeys, 0, 5);
        layout.SetColumnSpan(_includePlainKeys, 2);
        var inputHelp = CreateLabel("OFFでもショートカットキー、クリック、ホイールは記録します。", 8);
        layout.Controls.Add(inputHelp, 0, 6);
        layout.SetColumnSpan(inputHelp, 2);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = new Padding(0, 12, 0, 0), TabIndex = 9
        };
        var cancel = CreateButton("キャンセル(&C)", "設定の変更を取り消す", 1);
        cancel.DialogResult = DialogResult.Cancel;
        var save = CreateButton("保存(&S)", "設定を保存する", 0);
        save.Click += (_, _) => SaveSettings();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 8);
        layout.SetColumnSpan(buttons, 2);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static Label CreateLabel(string text, int tabIndex) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill, TabIndex = tabIndex,
        Margin = new Padding(0, 0, 0, 3), UseMnemonic = true
    };

    private static Button CreateButton(string text, string accessibleName, int tabIndex) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(80, 30), TabIndex = tabIndex,
        AccessibleName = accessibleName, Margin = new Padding(6, 0, 0, 0), UseVisualStyleBackColor = true
    };

    private void BrowseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "録画の保存先を選択してください。", UseDescriptionForTitle = true,
            ShowNewFolderButton = true, SelectedPath = Directory.Exists(_outputDirectory.Text) ? _outputDirectory.Text : ""
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputDirectory.Text = dialog.SelectedPath;
    }

    private void BrowseFfmpeg()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "FFmpeg の実行ファイルを選択", Filter = "FFmpeg (ffmpeg.exe)|ffmpeg.exe|実行ファイル (*.exe)|*.exe",
            CheckFileExists = true, Multiselect = false, RestoreDirectory = true,
            FileName = File.Exists(_ffmpegPath.Text) ? _ffmpegPath.Text : "ffmpeg.exe"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ffmpegPath.Text = dialog.FileName;
    }

    private void SaveSettings()
    {
        string output;
        try { output = NormalizeOutputDirectory(_outputDirectory.Text); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowValidationError(ex.Message, _outputDirectory);
            return;
        }
        string ffmpeg = _ffmpegPath.Text.Trim().Trim('"');
        if (!File.Exists(ffmpeg))
        {
            ShowValidationError("FFmpeg の実行ファイルが見つかりません。「参照」で ffmpeg.exe を指定してください。", _ffmpegPath);
            return;
        }
        Settings = _original with
        {
            OutputDirectory = output, FfmpegPath = Path.GetFullPath(ffmpeg), IncludePlainKeys = _includePlainKeys.Checked
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string NormalizeOutputDirectory(string value)
    {
        string path = value.Trim().Trim('"');
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("保存先フォルダーは絶対パスで指定してください。");
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        foreach (string part in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' ')
                || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new ArgumentException("保存先フォルダーの名前に使用できない文字または名前が含まれています。");
        }
        if (File.Exists(fullPath)) throw new ArgumentException("保存先にはファイルではなくフォルダーを指定してください。");
        return fullPath;
    }

    private void ShowValidationError(string message, Control control)
    {
        MessageBox.Show(this, message, WindowTitle.Create("設定エラー"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        control.Focus();
    }
}
