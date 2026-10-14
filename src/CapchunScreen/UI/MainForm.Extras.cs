using CapchunScreen.Recording;
using CapchunScreen.Storage;
using CapchunScreen.VirtualCamera;

namespace CapchunScreen.UI;

internal sealed partial class MainForm
{
    private readonly CheckBox independent = new() { Text = "独立プレビュー", AccessibleName = "独立プレビュー", AutoSize = true, Margin = new Padding(3, 9, 12, 0) };
    private readonly Button cameraButton = Button("仮想カメラ開始");
    private readonly Label cameraStatus = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, AccessibleName = "仮想カメラ状態", Text = "Unity Capture" };
    private PreviewForm? previewWindow;
    private Point? previewLocation;
    private VirtualCameraRelay? cameraRelay;
    private bool cameraChanging;

    private Control BuildExtraActions()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cameraButton.Dock = DockStyle.Fill;
        cameraButton.AutoSize = false;
        cameraButton.Margin = new Padding(3, 2, 5, 2);
        panel.Controls.Add(independent, 0, 0);
        panel.Controls.Add(cameraButton, 1, 0);
        panel.Controls.Add(cameraStatus, 2, 0);
        independent.CheckedChanged += (_, _) => Guard(ToggleIndependentPreview);
        cameraButton.Click += async (_, _) => await ToggleCameraAsync();
        return panel;
    }

    private void ToggleIndependentPreview()
    {
        if (!independent.Checked)
        {
            previewWindow?.Close();
            return;
        }
        if (closing || previewWindow is not null) return;
        var window = new PreviewForm();
        previewWindow = window;
        var area = Screen.FromControl(this).WorkingArea;
        window.Location = previewLocation ?? new Point(area.Right - window.Width - 20, area.Bottom - window.Height - 20);
        window.FormClosed += (_, _) =>
        {
            previewLocation = window.Location;
            if (previewWindow == window) previewWindow = null;
            independent.Checked = false;
        };
        if (picture.Image is Bitmap bitmap) window.SetFrame((Bitmap)bitmap.Clone());
        window.Show(this);
        if (!busy && previewTask is not { IsCompleted: false }) StartPreview();
    }

    private void DistributeFrame(Bitmap frame)
    {
        previewWindow?.SetFrame((Bitmap)frame.Clone());
        cameraRelay?.Submit((Bitmap)frame.Clone());
    }

    private void UpdateCameraStatus()
    {
        if (cameraRelay is not null) cameraStatus.Text = cameraRelay.Status;
        if (cameraRelay is { IsRunning: false }) cameraButton.Text = "仮想カメラ終了";
    }

    private async Task ToggleCameraAsync()
    {
        if (closing || cameraChanging) return;
        cameraChanging = true; cameraButton.Enabled = false;
        try
        {
            if (cameraRelay is not null)
            {
                var relay = cameraRelay;
                await relay.StopAsync();
                if (cameraRelay == relay) cameraRelay = null;
                if (IsDisposed || closing) return;
                cameraStatus.Text = "配信停止";
                cameraButton.Text = "仮想カメラ開始";
                return;
            }
            var available = UnityCaptureProvider.Probe();
            if (!available.Available)
            {
                cameraStatus.Text = "Unity Capture 未導入・使用不可";
                status.Text = available.Message;
                return;
            }
            cameraRelay = new VirtualCameraRelay();
            cameraButton.Text = "仮想カメラ停止";
            if (!busy && previewTask is not { IsCompleted: false }) StartPreview();
        }
        catch (Exception error) { ShowError(error); }
        finally { cameraChanging = false; if (!IsDisposed) cameraButton.Enabled = !closing; }
    }

    private void OpenMultiPlayback()
    {
        if (busy || closing) return;
        string? ffmpeg = FfmpegLocator.Find(settings.FfmpegPath);
        if (ffmpeg is null) { status.Text = "複数再生には「設定」でFFmpegを指定してください。"; return; }
        string[] paths = files.SelectedItems.Cast<ListViewItem>().Select(row => ((RecordingFile)row.Tag!).VideoPath).ToArray();
        if (paths.Length < 2)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "同時再生する動画を選択（最大16本）", Multiselect = true,
                Filter = "動画|*.mp4;*.mkv;*.avi;*.mov;*.webm|すべてのファイル|*.*",
                InitialDirectory = Directory.Exists(settings.OutputDirectory) ? settings.OutputDirectory : ""
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            paths = dialog.FileNames;
        }
        if (paths.Length > 16) { status.Text = "同時再生は最大16本です。"; return; }
        new MultiPlaybackForm(ffmpeg, paths).Show(this);
    }

    private async Task CloseExtrasAsync()
    {
        independent.Enabled = false; cameraButton.Enabled = false;
        previewWindow?.Close();
        if (cameraRelay is not null)
        {
            var relay = cameraRelay; cameraRelay = null;
            await relay.StopAsync();
        }
        var closingWindows = OwnedForms.OfType<MultiPlaybackForm>().Select(window =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FormClosed += (_, _) => completion.TrySetResult();
            window.Close();
            if (window.IsDisposed) completion.TrySetResult();
            return completion.Task;
        }).ToArray();
        await Task.WhenAll(closingWindows);
    }

    private void DisposeExtras()
    {
        previewWindow?.Dispose(); previewWindow = null;
        cameraRelay?.Dispose(); cameraRelay = null;
    }

    private void ClearPreviewFrames()
    {
        Interlocked.Exchange(ref pendingFrame, null)?.Dispose();
        var previous = picture.Image; picture.Image = null; previous?.Dispose();
        previewWindow?.ClearFrame();
    }
}
