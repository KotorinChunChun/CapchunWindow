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
    private readonly DesktopAccessGuard desktopAccess;
    private readonly Func<CaptureTarget, bool, IFrameSource> frameSourceFactory;
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
    private readonly Button inputSettingsButton = Button("操作記録の設定…");
    private readonly ToolTip optionHelp = new() { AutoPopDelay = 15000, InitialDelay = 500, ReshowDelay = 150, ShowAlways = true };
    private readonly PictureBox picture = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 32, 38), SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "録画対象プレビュー" };
    private readonly Label status = new() { Text = "対象を選ぶとプレビューを表示します。", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "録画状態" };
    private readonly Button savedFolderButton = Button("保存先を開く");
    private string? lastSavedVideo;
    private readonly Label elapsed = new() { Text = "00:00:00", AutoSize = true, Margin = new Padding(12, 8, 12, 0) };
    private readonly Button recordButton = Button("● 録画開始");
    private readonly Button previewButton = Button("プレビュー開始");
    private readonly Button refreshButton = Button("対象を更新");
    private readonly Button thumbnailButton = Button("サムネイル選択");
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
    private bool previewReady;
    private bool refreshingTargets;
    private int previewRevision;

    private readonly bool passive;
    internal string? ManagementStopBlockReason => busy || closing || previewToggleBusy ? "録画・プレビュー切替・終了処理中のため停止できません。" : null;

    internal MainForm(SettingsStore store, AppSettings settings, string? warning, bool passive = false,
        Func<CaptureTarget, bool, IFrameSource>? frameSourceFactory = null, DesktopAccessGuard? desktopAccess = null)
    {
        this.passive = passive;
        this.desktopAccess = desktopAccess ?? new DesktopAccessGuard(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "diagnostics"));
        this.frameSourceFactory = frameSourceFactory ?? ((selected, windowOnly) => CaptureFactory.Create(selected, windowOnly, this.desktopAccess));
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
        ConfigureDesktopMonitoring();
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
        foreach (int height in new[] { 40, 40, 35, 80, 38 }) recordLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        recordLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        recordLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
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
        var targetRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        thumbnailButton.Dock = DockStyle.Fill; thumbnailButton.AutoSize = false; thumbnailButton.Margin = new Padding(3, 0, 3, 0);
        targetRow.Controls.Add(target, 0, 0); targetRow.Controls.Add(thumbnailButton, 1, 0);
        recordLayout.Controls.Add(targetRow, 0, 1);
        recordLayout.Controls.Add(exclusive, 0, 2);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        AddOption(options, "FPS", fps); AddOption(options, "画質", quality); AddOption(options, "形式", container);
        options.SetFlowBreak(options.Controls[options.Controls.Count - 1], true);
        AddOption(options, "開始まで(秒)", countdown); AddOption(options, "最長(秒・0=無制限)", maximum);
        recordLayout.Controls.Add(options, 0, 3);
        var inputActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        inputSettingsButton.AutoSize = false; inputSettingsButton.Size = new Size(160, 30); inputSettingsButton.Margin = new Padding(3, 0, 5, 0);
        recordInput.Margin = new Padding(3, 9, 12, 0);
        inputActions.Controls.AddRange([recordInput, inputSettingsButton]);
        recordLayout.Controls.Add(inputActions, 0, 4);
        recordLayout.Controls.Add(picture, 0, 5);
        recordLayout.Controls.Add(BuildExtraActions(), 0, 6);
        optionHelp.SetToolTip(exclusive, "Windows Graphics Captureで選択したウィンドウだけを取得します。ほかのウィンドウが重なっても映り込みません。最小化中や取得に対応しないアプリでは映像を取得できない場合があります。");
        optionHelp.SetToolTip(recordInput, "動画と同名のTSVに、映像内の時刻・フレーム番号・マウス座標・クリック・キー操作を保存します。動画には焼き込みません。記録対象は「操作記録の設定」で選べます。");
        optionHelp.SetToolTip(countdown, "録画開始まで待つ秒数です。0ならすぐに開始します。");
        optionHelp.SetToolTip(maximum, "この秒数で録画を自動停止して保存します。0は時間制限なしです。");
        optionHelp.SetToolTip(independent, "選択中の映像を別ウィンドウで表示します。20px単位で移動でき、ダブルクリックで現在のモニター全体へ表示・解除します。");
        recordingOptions.AddRange([kind, filter, refreshButton, thumbnailButton, target, exclusive, fps, quality, container, countdown, maximum, recordInput, settingsButton, inputSettingsButton]);

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
        var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        statusRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        savedFolderButton.Dock = DockStyle.Fill;
        savedFolderButton.AutoSize = false;
        savedFolderButton.Enabled = false;
        statusRow.Controls.Add(status, 0, 0);
        statusRow.Controls.Add(savedFolderButton, 1, 0);
        root.Controls.Add(statusRow, 0, 2);
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
        kind.SelectedIndexChanged += async (_, _) => { exclusive.Enabled = kind.SelectedIndex == 0; RefreshTargets(); await RestartPreviewAsync(); };
        filter.TextChanged += async (_, _) => { RefreshTargets(); await RestartPreviewAsync(); };
        target.SelectedIndexChanged += async (_, _) => { if (!refreshingTargets) await RestartPreviewAsync(); };
        exclusive.CheckedChanged += async (_, _) => await RestartPreviewAsync();
        refreshButton.Click += async (_, _) => { RefreshTargets(); await RestartPreviewAsync(); };
        thumbnailButton.Click += async (_, _) => await OpenThumbnailPickerAsync();
        Shown += async (_, _) => { previewReady = !passive; await RestartPreviewAsync(); };
        Activated += (_, _) => { if (modalDepth == 0 && Enabled && previewWindow is not null) BringToFront(); };
        previewButton.Click += async (_, _) =>
        {
            if (busy || previewToggleBusy) return;
            previewRevision++;
            previewToggleBusy = true;
            try { if (previewTask is { IsCompleted: false }) await StopPreviewAsync(); else StartPreview(); }
            finally { previewToggleBusy = false; }
        };
        recordButton.Click += async (_, _) => await ToggleRecordingAsync();
        settingsButton.Click += async (_, _) =>
        {
            previewRevision++;
            await StopPreviewAsync();
            if (busy || closing) return;
            using var dialog = new SettingsForm(ReadValues());
            try
            {
                if (WithPreviewHidden(() => dialog.ShowDialog(this)) == DialogResult.OK)
                { store.Save(dialog.Settings); settings = dialog.Settings; outputLabel.Text = settings.OutputDirectory; RefreshFiles(); status.Text = "設定を保存しました。"; }
            }
            catch (Exception error) { ShowError(error); }
            finally { await RestartPreviewAsync(); }
        };
        savedFolderButton.Click += (_, _) => Guard(OpenSavedFolder);
        inputSettingsButton.Click += async (_, _) =>
        {
            previewRevision++;
            await StopPreviewAsync();
            if (busy || closing) return;
            using var dialog = new InputSettingsForm(ReadValues(), desktopAccess);
            try
            {
                if (WithPreviewHidden(() => dialog.ShowDialog(this)) == DialogResult.OK)
                { store.Save(dialog.Settings); settings = dialog.Settings; status.Text = "操作記録の設定を保存しました。"; }
            }
            catch (Exception error) { ShowError(error); }
            finally { await RestartPreviewAsync(); }
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
        refreshingTargets = true;
        try
        {
            string? previous = (target.SelectedItem as CaptureTarget)?.Id;
            target.Items.Clear();
            target.Items.AddRange(CaptureCatalog.GetTargets(kind.SelectedIndex == 0 ? CaptureKind.Window : CaptureKind.Monitor, filter.Text, includeMinimized: true).Cast<object>().ToArray());
            target.SelectedItem = target.Items.Cast<CaptureTarget>().FirstOrDefault(t => t.Id == previous);
            if (target.SelectedIndex < 0 && target.Items.Count > 0) target.SelectedIndex = 0;
        }
        catch (Exception error) { ShowError(error); }
        finally { refreshingTargets = false; }
    }

    private async Task RestartPreviewAsync()
    {
        int revision = ++previewRevision;
        await StopPreviewAsync();
        if (revision != previewRevision || !previewReady || closing || busy || previewToggleBusy || modalDepth > 0 || IsDisposed) return;
        if (target.SelectedItem is CaptureTarget) StartPreview();
        else status.Text = "録画対象を選択してください。";
    }

    private async Task OpenThumbnailPickerAsync()
    {
        if (busy || closing || modalDepth > 0) return;
        previewRevision++;
        await StopPreviewAsync();
        if (busy || closing || IsDisposed || modalDepth > 0) return;
        var selectedKind = kind.SelectedIndex == 0 ? CaptureKind.Window : CaptureKind.Monitor;
        using var picker = new ThumbnailPickerForm(() => CaptureCatalog.GetTargets(selectedKind, includeMinimized: true),
            new FrameThumbnailCapture(frameSourceFactory), (target.SelectedItem as CaptureTarget)?.Id, filter.Text);
        picker.Bounds = Screen.FromControl(this).WorkingArea;
        try
        {
            if (WithPreviewHidden(() => picker.ShowDialog(this)) == DialogResult.OK && picker.SelectedEntry is { } selected)
            {
                filter.Text = picker.FilterText;
                RefreshTargets();
                target.SelectedItem = target.Items.Cast<CaptureTarget>().FirstOrDefault(item => item.Id == selected.Id);
                if (target.SelectedItem is null) status.Text = "選んだ対象は終了しました。選び直してください。";
            }
        }
        catch (Exception error) { ShowError(error); }
        finally { await RestartPreviewAsync(); }
    }

    private void StartPreview()
    {
        if (closing || busy || modalDepth > 0 || previewTask is { IsCompleted: false }) return;
        if (target.SelectedItem is not CaptureTarget selected) { status.Text = "録画対象を選択してください。"; return; }
        var cancellation = new CancellationTokenSource();
        previewCancellation = cancellation;
        bool windowOnly = exclusive.Checked;
        previewButton.Text = "プレビュー停止";
        status.Text = "プレビュー中";
        previewTask = Task.Run(async () =>
        {
            using var source = frameSourceFactory(selected, windowOnly);
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
            using var source = CaptureFactory.Create(selected, settings.ExclusiveWindow, desktopAccess);
            using var input = settings.RecordInput ? new InputMonitor(settings.IncludePlainKeys, settings.InputRecordOverrides, desktopAccess) : null;
            recordButton.Text = "■ 録画停止"; status.Text = "録画中  /  Ctrl+Shift+F9で停止";
            recordingClock.Restart();
            var result = await new RecordingSession().RunAsync(source, input, selected.WindowHandle,
                new RecordingOptions { OutputDirectory = settings.OutputDirectory, FfmpegPath = ffmpeg,
                    FramesPerSecond = settings.FramesPerSecond, QualityCrf = settings.QualityCrf, Container = settings.Container,
                    MaximumDuration = TimeSpan.FromSeconds(settings.MaximumSeconds), RecordInput = settings.RecordInput },
                cancellation.Token, PublishFrame);
            SetSavedRecording(result.VideoPath, result.DurationSeconds);
            if (result.InterruptionReason is not null)
            {
                status.Text = $"録画中断：{result.DurationSeconds:F1}秒まで保存しました。{result.InterruptionReason}";
                optionHelp.SetToolTip(status, status.Text + Environment.NewLine + "診断ログ: " + (result.DiagnosticPath ?? desktopAccess.DiagnosticWriteError));
            }
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
        if (!tsv)
        {
            string? ffmpeg = FfmpegLocator.Find(settings.FfmpegPath);
            if (ffmpeg is null) { status.Text = "動画再生には「設定」でFFmpegを指定してください。"; return; }
            new MultiPlaybackForm(ffmpeg, [path]).Show(this);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private void RenameSelected()
    {
        var file = SelectedFile(); if (file is null) return;
        using var dialog = new RenameForm(Path.GetFileNameWithoutExtension(file.VideoPath));
        if (WithPreviewHidden(() => dialog.ShowDialog(this)) != DialogResult.OK) return;
        var path = RecordingFiles.Rename(file.VideoPath, dialog.NewBaseName);
        RefreshFiles(path); status.Text = "動画と対応する操作履歴の名前を変更しました。";
    }
    private void RecycleSelected()
    {
        var file = SelectedFile(); if (file is null) return;
        if (WithPreviewHidden(() => MessageBox.Show(this, $"{file.Name}\n動画と対応する操作履歴を、ごみ箱へ移動しますか？", WindowTitle.Create("削除確認"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question)) != DialogResult.OK) return;
        try { RecordingFiles.Recycle(file.VideoPath); status.Text = "ごみ箱へ移動しました。"; }
        finally { RefreshFiles(); }
    }
    private void OpenFolder()
    {
        Directory.CreateDirectory(settings.OutputDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { settings.OutputDirectory } });
    }

    private void SetSavedRecording(string videoPath, double duration)
    {
        lastSavedVideo = Path.GetFullPath(videoPath);
        savedFolderButton.Enabled = true;
        status.Text = $"保存完了: {Path.GetFileName(videoPath)}（{duration:F1}秒）";
        optionHelp.SetToolTip(status, null);
    }

    internal string? SavedRecordingDirectory => lastSavedVideo is null ? null : Path.GetDirectoryName(lastSavedVideo);

    private void OpenSavedFolder()
    {
        string? directory = SavedRecordingDirectory;
        if (directory is null || !Directory.Exists(directory)) throw new DirectoryNotFoundException("最後に保存した動画のフォルダーが見つかりません。");
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { directory } });
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
        if (passive) return; // 連携テストではグローバルホットキーを取得しない。
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
            closing = true;
            previewRevision++;
            previewCancellation?.Cancel();
            frameTimer.Stop(); frameTimer.Dispose();
            optionHelp.Dispose();
            desktopTimer.Dispose();
            DisposeExtras();
            Interlocked.Exchange(ref pendingFrame, null)?.Dispose();
            picture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
    private void ShowError(Exception error)
    {
        status.Text = $"エラー: {error.Message}";
        if (error is DesktopAccessException desktop)
            optionHelp.SetToolTip(status, status.Text + Environment.NewLine + "診断ログ: " + (desktop.DiagnosticPath ?? desktopAccess.DiagnosticWriteError));
    }
    private void Guard(Action action) { try { action(); } catch (Exception error) { ShowError(error); } }
    private void AddAction(FlowLayoutPanel panel, string text, Action action)
    { var button = Button(text); button.Click += (_, _) => Guard(action); panel.Controls.Add(button); }
    private static Button Button(string text) => new() { Text = text, AutoSize = true, Height = 34, Margin = new Padding(3, 4, 5, 4) };
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
