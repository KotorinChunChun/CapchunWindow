using System.Diagnostics;
using System.Runtime.InteropServices;
using CapchunScreen.Capture;
using CapchunScreen.Input;
using CapchunScreen.Recording;
using CapchunScreen.Storage;

namespace CapchunScreen.UI;

internal sealed partial class MainForm : BrandedForm
{
    private readonly SettingsStore store;
    private AppSettings settings;
    private readonly ComboBox kind = Choice("対象の種類", "ウィンドウ", "モニター");
    private readonly TextBox filter = new() { PlaceholderText = "ウィンドウ名を検索", Dock = DockStyle.Fill, AccessibleName = "対象名検索" };
    private readonly ComboBox target = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "録画対象" };
    private readonly CheckBox exclusive = new() { Text = "選択ウィンドウのみ（WGC）", AutoSize = true };
    private readonly ComboBox fps = Choice("FPS", "15", "30", "60");
    private readonly ComboBox quality = Choice("画質", "最高", "高", "標準", "低");
    private readonly ComboBox container = Choice("保存形式", "MP4", "MKV");
    private readonly NumericUpDown countdown = Number("開始までの秒数", 60);
    private readonly NumericUpDown maximum = Number("最長録画秒数", 36000);
    private readonly CheckBox recordInput = new() { Text = "操作履歴をTSVへ保存", AutoSize = true };
    private readonly PictureBox picture = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 32, 38), SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "録画対象プレビュー" };
    private readonly Label status = new() { Text = "対象を選び、プレビューまたは録画を開始してください。", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "録画状態" };
    private readonly Label elapsed = new() { Text = "00:00:00", AutoSize = true, Margin = new Padding(12, 8, 12, 0) };
    private readonly Button recordButton = Button("● 録画開始");
    private readonly Button previewButton = Button("プレビュー開始");
    private readonly Button refreshButton = Button("対象を更新");
    private readonly Button settingsButton = Button("設定");
    private readonly Label outputLabel = new() { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ListView files = new() { View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, Dock = DockStyle.Fill, AccessibleName = "録画ファイル一覧" };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 50 };
    private readonly Stopwatch recordingClock = new();
    private readonly List<Control> recordingOptions = [];
    private CancellationTokenSource? recordingCancellation;
    private CancellationTokenSource? previewCancellation;
    private Task? previewTask;
    private Bitmap? pendingFrame;
    private bool busy;
    private bool allowClose;
    private bool closeAfterRecording;
    private bool hotkeyRegistered;
    private bool previewToggleBusy;
    private bool closing;

    internal MainForm(SettingsStore store, AppSettings settings, string? warning)
    {
        this.store = store;
        this.settings = settings;
        Text = WindowTitle.Create();
        Font = new Font("Yu Gothic UI", 10F);
        ClientSize = new Size(900, 720);
        MinimumSize = new Size(740, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        LoadValues();
        WireEvents();
        RefreshTargets();
        RefreshFiles();
        if (!string.IsNullOrWhiteSpace(warning)) status.Text = warning;
        frameTimer.Tick += (_, _) =>
        {
            var frame = Interlocked.Exchange(ref pendingFrame, null);
            if (frame is not null)
            {
                var old = picture.Image; picture.Image = frame; old?.Dispose();
                DistributeFrame(frame);
            }
            UpdateCameraStatus();
            elapsed.Text = recordingClock.Elapsed.ToString(@"hh\:mm\:ss");
        };
        frameTimer.Start();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);
        root.Controls.Add(tabs, 0, 0);
        var recording = new TabPage("録画") { Padding = new Padding(8) };
        var library = new TabPage("録画ファイル") { Padding = new Padding(8) };
        tabs.TabPages.AddRange([recording, library]);
        var recordLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1 };
        foreach (int height in new[] { 40, 40, 35, 80, 32, 42 }) recordLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        recordLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        recording.Controls.Add(recordLayout);
        var search = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        search.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 114));
        kind.Dock = DockStyle.Fill;
        refreshButton.Dock = DockStyle.Fill;
        refreshButton.AutoSize = false;
        refreshButton.Margin = new Padding(3, 0, 3, 0);
        search.Controls.Add(kind, 0, 0); search.Controls.Add(filter, 1, 0); search.Controls.Add(refreshButton, 2, 0);
        recordLayout.Controls.Add(search, 0, 0);
        recordLayout.Controls.Add(target, 0, 1);
        recordLayout.Controls.Add(exclusive, 0, 2);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        AddOption(options, "FPS", fps); AddOption(options, "画質", quality); AddOption(options, "形式", container);
        AddOption(options, "開始まで(秒)", countdown); AddOption(options, "最長(秒・0=無制限)", maximum);
        recordLayout.Controls.Add(options, 0, 3);
        recordLayout.Controls.Add(recordInput, 0, 4);
        recordLayout.Controls.Add(BuildExtraActions(), 0, 5);
        recordLayout.Controls.Add(picture, 0, 6);
        recordingOptions.AddRange([kind, filter, refreshButton, target, exclusive, fps, quality, container, countdown, maximum, recordInput, settingsButton]);

        var libraryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        libraryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        libraryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        libraryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        library.Controls.Add(libraryLayout);
        outputLabel.Text = settings.OutputDirectory;
        libraryLayout.Controls.Add(outputLabel, 0, 0);
        files.Columns.Add("ファイル名", 460); files.Columns.Add("更新日時", 165); files.Columns.Add("容量", 90);
        libraryLayout.Controls.Add(files, 0, 1);
        var libraryButtons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        AddAction(libraryButtons, "再生", () => OpenSelected(false));
        AddAction(libraryButtons, "TSVを開く", () => OpenSelected(true));
        AddAction(libraryButtons, "名前変更", RenameSelected);
        AddAction(libraryButtons, "ごみ箱へ", RecycleSelected);
        AddAction(libraryButtons, "フォルダー", OpenFolder);
        AddAction(libraryButtons, "更新", RefreshFiles);
        AddAction(libraryButtons, "複数再生…", OpenMultiPlayback);
        libraryLayout.Controls.Add(libraryButtons, 0, 2);
        recordingOptions.Add(libraryButtons);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        recordButton.Width = 160; recordButton.BackColor = Color.FromArgb(255, 221, 221);
        previewButton.Width = 150;
        actions.Controls.AddRange([recordButton, previewButton, settingsButton, elapsed]);
        root.Controls.Add(actions, 0, 1);
        root.Controls.Add(status, 0, 2);
    }

    private void LoadValues()
    {
        kind.SelectedIndex = settings.CaptureWindows ? 0 : 1;
        fps.SelectedItem = settings.FramesPerSecond.ToString();
        if (fps.SelectedIndex < 0) fps.SelectedIndex = 1;
        quality.SelectedIndex = settings.QualityCrf <= 18 ? 0 : settings.QualityCrf <= 21 ? 1 : settings.QualityCrf <= 23 ? 2 : 3;
        container.SelectedIndex = settings.Container.Equals("mkv", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        countdown.Value = settings.CountdownSeconds; maximum.Value = settings.MaximumSeconds;
        recordInput.Checked = settings.RecordInput; exclusive.Checked = settings.ExclusiveWindow;
        exclusive.Enabled = settings.CaptureWindows;
    }

    private void WireEvents()
    {
        kind.SelectedIndexChanged += async (_, _) => { await StopPreviewAsync(); exclusive.Enabled = kind.SelectedIndex == 0; RefreshTargets(); };
        filter.TextChanged += async (_, _) => { await StopPreviewAsync(); RefreshTargets(); };
        target.SelectedIndexChanged += async (_, _) => await StopPreviewAsync();
        exclusive.CheckedChanged += async (_, _) => await StopPreviewAsync();
        refreshButton.Click += async (_, _) => { await StopPreviewAsync(); RefreshTargets(); };
        previewButton.Click += async (_, _) =>
        {
            if (busy || previewToggleBusy) return;
            previewToggleBusy = true;
            try { if (previewTask is { IsCompleted: false }) await StopPreviewAsync(); else StartPreview(); }
            finally { previewToggleBusy = false; }
        };
        recordButton.Click += async (_, _) => await ToggleRecordingAsync();
        settingsButton.Click += async (_, _) =>
        {
            await StopPreviewAsync();
            if (busy || closing) return;
            using var dialog = new SettingsForm(ReadValues());
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { store.Save(dialog.Settings); settings = dialog.Settings; outputLabel.Text = settings.OutputDirectory; RefreshFiles(); status.Text = "設定を保存しました。"; }
            catch (Exception error) { ShowError(error); }
        };
        files.DoubleClick += (_, _) => Guard(() => OpenSelected(false));
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedIndex == 1) RefreshFiles(); };
        FormClosing += ClosingAsync;
    }

    private AppSettings ReadValues() => settings with
    {
        CaptureWindows = kind.SelectedIndex == 0, ExclusiveWindow = exclusive.Checked,
        FramesPerSecond = int.Parse((string)fps.SelectedItem!), QualityCrf = new[] { 18, 21, 23, 28 }[quality.SelectedIndex],
        Container = container.SelectedIndex == 1 ? "mkv" : "mp4", CountdownSeconds = (int)countdown.Value,
        MaximumSeconds = (int)maximum.Value, RecordInput = recordInput.Checked
    };

    private void RefreshTargets()
    {
        try
        {
            string? previous = (target.SelectedItem as CaptureTarget)?.Id;
            target.Items.Clear();
            target.Items.AddRange(CaptureCatalog.GetTargets(kind.SelectedIndex == 0 ? CaptureKind.Window : CaptureKind.Monitor, filter.Text).Cast<object>().ToArray());
            target.SelectedItem = target.Items.Cast<CaptureTarget>().FirstOrDefault(t => t.Id == previous);
            if (target.SelectedIndex < 0 && target.Items.Count > 0) target.SelectedIndex = 0;
        }
        catch (Exception error) { ShowError(error); }
    }

    private void StartPreview()
    {
        if (closing || busy) return;
        if (target.SelectedItem is not CaptureTarget selected) { status.Text = "録画対象を選択してください。"; return; }
        var cancellation = new CancellationTokenSource();
        previewCancellation = cancellation;
        bool windowOnly = exclusive.Checked;
        previewButton.Text = "プレビュー停止";
        status.Text = "プレビュー中";
        previewTask = Task.Run(async () =>
        {
            using var source = CaptureFactory.Create(selected, windowOnly);
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                using var frame = source.Capture(cancellation.Token);
                PublishFrame((Bitmap)frame.Image.Clone());
                await Task.Delay(50, cancellation.Token).ConfigureAwait(false);
            }
        });
        _ = ObservePreviewAsync(previewTask, cancellation);
    }

    private async Task ObservePreviewAsync(Task task, CancellationTokenSource cancellation)
    {
        try { await task; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!busy && !IsDisposed) ShowError(error); }
        finally
        {
            if (previewCancellation == cancellation)
            {
                previewCancellation = null; previewTask = null;
                if (!IsDisposed)
                {
                    previewButton.Text = "プレビュー開始";
                    if (!busy) ClearPreviewFrames();
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task StopPreviewAsync()
    {
        var task = previewTask;
        previewCancellation?.Cancel();
        if (task is not null)
        {
            try { await task; } catch (Exception) { /* 観測側が失敗理由を表示する。 */ }
        }
        if (!IsDisposed)
        {
            previewButton.Text = "プレビュー開始";
            if (previewTask is null || ReferenceEquals(previewTask, task)) ClearPreviewFrames();
        }
    }

    private void PublishFrame(Bitmap frame)
    {
        if (IsDisposed || Disposing) { frame.Dispose(); return; }
        Interlocked.Exchange(ref pendingFrame, frame)?.Dispose();
    }

    private async Task ToggleRecordingAsync()
    {
        if (closing) return;
        if (recordingCancellation is not null)
        {
            recordingCancellation.Cancel(); recordButton.Enabled = false; status.Text = "停止・保存完了を待っています…"; return;
        }
        if (busy || target.SelectedItem is not CaptureTarget selected) return;
        settings = ReadValues();
        string? ffmpeg = FfmpegLocator.Find(settings.FfmpegPath);
        if (ffmpeg is null) { status.Text = "FFmpegが見つかりません。「設定」からffmpeg.exeを指定してください。"; return; }
        using var cancellation = new CancellationTokenSource();
        recordingCancellation = cancellation;
        SetBusy(true);
        try
        {
            await StopPreviewAsync();
            settings = settings with { FfmpegPath = ffmpeg };
            store.Save(settings);
            for (int seconds = settings.CountdownSeconds; seconds > 0; seconds--)
            {
                status.Text = $"録画開始まで {seconds} 秒"; recordButton.Text = "カウントダウン中止";
                await Task.Delay(1000, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            using var source = CaptureFactory.Create(selected, settings.ExclusiveWindow);
            using var input = settings.RecordInput ? new InputMonitor(settings.IncludePlainKeys) : null;
            recordButton.Text = "■ 録画停止"; status.Text = "録画中  /  Ctrl+Shift+F9で停止";
            recordingClock.Restart();
            var result = await new RecordingSession().RunAsync(source, input, selected.WindowHandle,
                new RecordingOptions { OutputDirectory = settings.OutputDirectory, FfmpegPath = ffmpeg,
                    FramesPerSecond = settings.FramesPerSecond, QualityCrf = settings.QualityCrf, Container = settings.Container,
                    MaximumDuration = TimeSpan.FromSeconds(settings.MaximumSeconds), RecordInput = settings.RecordInput },
                cancellation.Token, PublishFrame);
            status.Text = $"保存完了: {Path.GetFileName(result.VideoPath)}（{result.DurationSeconds:F1}秒）";
            RefreshFiles(result.VideoPath);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { status.Text = "録画開始を中止しました。"; }
        catch (Exception error) { ShowError(error); closeAfterRecording = false; }
        finally
        {
            recordingClock.Stop(); recordingCancellation = null; SetBusy(false); recordButton.Text = "● 録画開始";
            ClearPreviewFrames();
            if (closeAfterRecording) Close();
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        foreach (var control in recordingOptions) control.Enabled = !value;
        exclusive.Enabled = !value && kind.SelectedIndex == 0;
        previewButton.Enabled = !value; recordButton.Enabled = true;
    }

    private void RefreshFiles() => RefreshFiles(null);
    private void RefreshFiles(string? selectedPath)
    {
        try
        {
            files.BeginUpdate(); files.Items.Clear();
            foreach (var file in RecordingFiles.List(settings.OutputDirectory))
            {
                var row = new ListViewItem([file.Name, file.Modified.ToString("yyyy/MM/dd HH:mm:ss"), $"{file.Bytes / 1024d / 1024d:F1} MB"]) { Tag = file };
                files.Items.Add(row); if (file.VideoPath == selectedPath) row.Selected = true;
            }
        }
        catch (Exception error) { ShowError(error); }
        finally { files.EndUpdate(); }
    }

    private RecordingFile? SelectedFile() => files.SelectedItems.Count == 1 ? files.SelectedItems[0].Tag as RecordingFile : null;
    private void OpenSelected(bool tsv)
    {
        var file = SelectedFile(); if (file is null) { status.Text = "録画ファイルを選択してください。"; return; }
        var path = tsv ? file.TsvPath : file.VideoPath;
        if (!File.Exists(path)) throw new FileNotFoundException("選択したファイルが見つかりません。", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private void RenameSelected()
    {
        var file = SelectedFile(); if (file is null) return;
        using var dialog = new RenameForm(Path.GetFileNameWithoutExtension(file.VideoPath));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var path = RecordingFiles.Rename(file.VideoPath, dialog.NewBaseName);
        RefreshFiles(path); status.Text = "動画と対応する操作履歴の名前を変更しました。";
    }
    private void RecycleSelected()
    {
        var file = SelectedFile(); if (file is null) return;
        if (MessageBox.Show(this, $"{file.Name}\n動画と対応する操作履歴を、ごみ箱へ移動しますか？", WindowTitle.Create("削除確認"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try { RecordingFiles.Recycle(file.VideoPath); status.Text = "ごみ箱へ移動しました。"; }
        finally { RefreshFiles(); }
    }
    private void OpenFolder()
    {
        Directory.CreateDirectory(settings.OutputDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { settings.OutputDirectory } });
    }

    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        if (busy) { closeAfterRecording = true; recordingCancellation?.Cancel(); status.Text = "録画を保存して終了します…"; return; }
        closing = true;
        SetBusy(true); recordButton.Enabled = false;
        await StopPreviewAsync();
        await CloseExtrasAsync();
        try { store.Save(ReadValues()); allowClose = true; Close(); }
        catch (Exception error)
        {
            closing = false; SetBusy(false);
            independent.Enabled = true; cameraButton.Enabled = !cameraChanging;
            cameraButton.Text = "仮想カメラ開始"; cameraStatus.Text = "配信停止";
            ShowError(error);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hotkeyRegistered = RegisterHotKey(Handle, 1, 0x4000 | 0x2 | 0x4, (uint)Keys.F9);
        if (!hotkeyRegistered) status.Text = "Ctrl+Shift+F9は他のアプリで使用中です。画面の録画ボタンを使用してください。";
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (hotkeyRegistered) UnregisterHotKey(Handle, 1);
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312 && m.WParam == 1)
        {
            if (Enabled && !OwnedForms.Any(form => form.Visible && form.Modal)) _ = ToggleRecordingAsync();
            return;
        }
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            frameTimer.Stop(); frameTimer.Dispose();
            DisposeExtras();
            Interlocked.Exchange(ref pendingFrame, null)?.Dispose();
            picture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
    private void ShowError(Exception error) => status.Text = $"エラー: {error.Message}";
    private void Guard(Action action) { try { action(); } catch (Exception error) { ShowError(error); } }
    private void AddAction(FlowLayoutPanel panel, string text, Action action)
    { var button = Button(text); button.Click += (_, _) => Guard(action); panel.Controls.Add(button); }
    private static Button Button(string text) => new() { Text = text, AutoSize = true, Height = 34, Margin = new Padding(3, 4, 5, 4), AccessibleName = text };
    private static ComboBox Choice(string name, params string[] values)
    { var choice = new ComboBox { AccessibleName = name, DropDownStyle = ComboBoxStyle.DropDownList, Width = 88 }; choice.Items.AddRange(values); return choice; }
    private static NumericUpDown Number(string name, int max) => new() { AccessibleName = name, Minimum = 0, Maximum = max, Width = 80 };
    private static void AddOption(FlowLayoutPanel panel, string label, Control input)
    {
        var group = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 3, 12, 3) };
        group.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 5, 0) });
        group.Controls.Add(input); panel.Controls.Add(group);
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
}
