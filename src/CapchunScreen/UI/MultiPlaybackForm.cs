using System.ComponentModel;
using System.Drawing.Drawing2D;
using CapchunScreen.Playback;

namespace CapchunScreen.UI;

public sealed class MultiPlaybackForm : BrandedForm
{
    private readonly List<PlayerTile> _tiles = [];
    private readonly PlaybackTimeline _global = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly TrackBar _seek = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 10_000, TickStyle = TickStyle.None, AutoSize = false, Name = "GlobalSeek" };
    private readonly Label _time = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "00:00 / 00:00" };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "動画を読み込んでいます…", AutoEllipsis = true };
    private readonly Button _play = new() { Text = "すべて再生", Dock = DockStyle.Fill, Enabled = false, Name = "PlayAll" };
    private readonly Button _pause = new() { Text = "一時停止", Dock = DockStyle.Fill, Enabled = false, Name = "PauseAll" };
    private readonly Button _start = new() { Text = "先頭へ", Dock = DockStyle.Fill, Enabled = false, Name = "SeekStart" };
    private Task? _loadTask;
    private Task? _renderTask;
    private bool _renderBusy;
    private bool _closing;
    private bool _closeReady;
    private bool _seeking;
    private bool _resumeAfterSeek;
    private bool _resourcesDisposed;
    private double Duration => _tiles.Where(tile => tile.Ready).Select(tile => tile.Player.Info!.DurationSeconds).DefaultIfEmpty(0).Max();

    public MultiPlaybackForm(string ffmpegPath, IReadOnlyList<string> videoPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(videoPaths);
        if (videoPaths.Count is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(videoPaths), "同時再生する動画は 1 ～ 16 本を選択してください。");
        Text = WindowTitle.Create("複数動画の同時再生");
        StartPosition = FormStartPosition.CenterParent;
        Size = new(1120, 740);
        MinimumSize = videoPaths.Count > 9 ? new(960, 640) : new(760, 500);
        TableLayoutPanel root = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        root.RowStyles.Add(new(SizeType.Absolute, 48));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 28));
        TableLayoutPanel controls = new() { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 5 };
        controls.RowStyles.Add(new(SizeType.Percent, 100));
        controls.ColumnStyles.Add(new(SizeType.Absolute, 108));
        controls.ColumnStyles.Add(new(SizeType.Absolute, 100));
        controls.ColumnStyles.Add(new(SizeType.Absolute, 80));
        controls.ColumnStyles.Add(new(SizeType.Percent, 100));
        controls.ColumnStyles.Add(new(SizeType.Absolute, 130));
        controls.Controls.Add(_play, 0, 0);
        controls.Controls.Add(_pause, 1, 0);
        controls.Controls.Add(_start, 2, 0);
        controls.Controls.Add(_seek, 3, 0);
        controls.Controls.Add(_time, 4, 0);
        Size gridSize = GetGridSize(videoPaths.Count);
        TableLayoutPanel grid = new() { Dock = DockStyle.Fill, ColumnCount = gridSize.Width, RowCount = gridSize.Height, Name = "PlayerGrid" };
        for (int i = 0; i < gridSize.Width; i++) grid.ColumnStyles.Add(new(SizeType.Percent, 100f / gridSize.Width));
        for (int i = 0; i < gridSize.Height; i++) grid.RowStyles.Add(new(SizeType.Percent, 100f / gridSize.Height));
        for (int i = 0; i < videoPaths.Count; i++)
        {
            PlayerTile tile = new(ffmpegPath, videoPaths[i], i);
            _tiles.Add(tile);
            grid.Controls.Add(tile.Container, i % gridSize.Width, i / gridSize.Width);
            tile.Play.Click += (_, _) => ToggleIndividual(tile);
            tile.Seek.MouseDown += (_, _) =>
            {
                if (_global.IsPlaying) PauseAll();
                tile.ResumeAfterSeek = tile.Clock.IsPlaying;
                tile.Clock.Pause();
                tile.Seeking = true;
            };
            tile.Seek.MouseUp += (_, _) =>
            {
                tile.Seeking = false;
                SeekIndividual(tile);
                if (tile.ResumeAfterSeek) tile.Clock.Play();
            };
            tile.Seek.Scroll += (_, _) => { if (!tile.Seeking) SeekIndividual(tile); };
        }
        root.Controls.Add(controls, 0, 0);
        root.Controls.Add(grid, 0, 1);
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);
        _play.Click += (_, _) => PlayAll();
        _pause.Click += (_, _) => PauseAll();
        _start.Click += (_, _) => SeekAll(0);
        _seek.MouseDown += (_, _) => { _resumeAfterSeek = _global.IsPlaying; PauseAll(); _seeking = true; };
        _seek.MouseUp += (_, _) => { _seeking = false; SeekAll(_seek.Value / 10_000d); if (_resumeAfterSeek) PlayAll(); };
        _seek.Scroll += (_, _) => { if (!_seeking) SeekAll(_seek.Value / 10_000d); };
        Shown += (_, _) => { _loadTask = LoadAsync(); };
        _timer.Tick += (_, _) => { if (!_renderBusy && !_closing) _renderTask = RenderAsync(); };
    }

    internal static Size GetGridSize(int count) => count switch
    {
        1 => new(1, 1), <= 2 => new(2, 1), <= 4 => new(2, 2), <= 6 => new(3, 2), <= 9 => new(3, 3), <= 12 => new(4, 3), _ => new(4, 4)
    };

    private async Task LoadAsync()
    {
        try
        {
            await Task.WhenAll(_tiles.Select(async tile =>
            {
                try
                {
                    await tile.Player.InitializeAsync(_lifetime.Token);
                    Bitmap first = await tile.Player.ReadFrameAsync(0, _lifetime.Token);
                    if (_closing) { first.Dispose(); return; }
                    tile.Surface.SetFrame(first);
                    tile.Ready = true;
                    tile.LastFrame = 0;
                    tile.Play.Enabled = tile.Seek.Enabled = true;
                    tile.Surface.Status = null;
                    tile.Surface.Invalidate();
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception ex) { if (!_closing) MarkFailed(tile, ex); }
            }));
            if (_closing) return;
            bool anyReady = _tiles.Any(tile => tile.Ready);
            _play.Enabled = _pause.Enabled = _start.Enabled = _seek.Enabled = anyReady;
            int failures = _tiles.Count(tile => !tile.Ready);
            _status.Text = failures == 0
                ? "全体のシークは最長動画の秒位置に同期します。各枠から個別にも操作できます。"
                : $"{failures} 本を読み込めませんでした。読み込めた動画は再生できます。";
            UpdateControls();
            _timer.Start();
        }
        catch (Exception ex)
        {
            if (!_closing) _status.Text = $"再生の準備に失敗しました: {ex.Message}";
        }
    }

    private void PlayAll()
    {
        if (Duration <= 0) return;
        if (_global.Position >= Duration) _global.Seek(0);
        foreach (PlayerTile tile in _tiles)
        {
            tile.Clock.Pause();
            tile.Clock.Seek(_global.Position);
        }
        _global.Play();
        UpdateControls();
    }

    private void PauseAll()
    {
        bool globalWasPlaying = _global.IsPlaying;
        _global.Pause();
        foreach (PlayerTile tile in _tiles)
        {
            if (globalWasPlaying) tile.Clock.Seek(Math.Min(_global.Position, tile.Player.Info?.DurationSeconds ?? 0));
            tile.Clock.Pause();
        }
        UpdateControls();
    }

    private void SeekAll(double ratio)
    {
        bool resume = _global.IsPlaying;
        PauseAll();
        double seconds = Duration * Math.Clamp(ratio, 0, 1);
        _global.Seek(seconds);
        foreach (PlayerTile tile in _tiles) tile.Clock.Seek(Math.Min(seconds, tile.Player.Info?.DurationSeconds ?? 0));
        if (resume) _global.Play();
        UpdateControls();
    }

    private void ToggleIndividual(PlayerTile tile)
    {
        if (!tile.Ready) return;
        double currentPosition = _global.IsPlaying ? _global.Position : tile.Clock.Position;
        bool wasPlaying = (_global.IsPlaying || tile.Clock.IsPlaying) && currentPosition < tile.Player.Info!.DurationSeconds;
        if (_global.IsPlaying) PauseAll();
        if (wasPlaying) tile.Clock.Pause();
        else
        {
            if (tile.Clock.Position >= tile.Player.Info!.DurationSeconds) tile.Clock.Seek(0);
            tile.Clock.Play();
        }
        UpdateControls();
    }

    private void SeekIndividual(PlayerTile tile)
    {
        if (!tile.Ready) return;
        double requestedSeconds = tile.Player.Info!.DurationSeconds * tile.Seek.Value / 10_000d;
        if (_global.IsPlaying) PauseAll();
        tile.Clock.Seek(requestedSeconds);
        UpdateControls();
    }

    private async Task RenderAsync()
    {
        _renderBusy = true;
        try
        {
            if (_global.IsPlaying && _global.Position >= Duration)
            {
                PauseAll();
                _global.Seek(Duration);
            }
            double globalTime = _global.Position;
            await Task.WhenAll(_tiles.Where(tile => tile.Ready).Select(async tile =>
            {
                double duration = tile.Player.Info!.DurationSeconds;
                double seconds = Math.Min(_global.IsPlaying ? globalTime : tile.Clock.Position, duration);
                if (!_global.IsPlaying && tile.Clock.IsPlaying && seconds >= duration)
                {
                    tile.Clock.Pause();
                    tile.Clock.Seek(duration);
                }
                long frame = (long)Math.Floor(seconds * 30);
                if (frame != tile.LastFrame && !tile.Seeking)
                {
                    try
                    {
                        Bitmap image = await tile.Player.ReadFrameAsync(seconds, _lifetime.Token);
                        if (_closing) { image.Dispose(); return; }
                        tile.Surface.SetFrame(image);
                        tile.LastFrame = frame;
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        if (!_closing) MarkFailed(tile, ex);
                        return;
                    }
                }
                if (!_closing)
                {
                    tile.Surface.Status = seconds >= duration ? "再生終了" : null;
                    tile.Surface.Invalidate();
                }
            }));
            if (!_closing) UpdateControls();
        }
        catch (Exception ex)
        {
            if (!_closing) _status.Text = $"再生中にエラーが発生しました: {ex.Message}";
        }
        finally { _renderBusy = false; }
    }

    private void UpdateControls()
    {
        double duration = Duration;
        _play.Enabled = _pause.Enabled = _start.Enabled = _seek.Enabled = duration > 0 && !_closing;
        double globalPosition = Math.Min(_global.Position, duration);
        _time.Text = $"{FormatTime(globalPosition)} / {FormatTime(duration)}";
        if (!_seeking) _seek.Value = Ratio(globalPosition, duration);
        foreach (PlayerTile tile in _tiles.Where(tile => tile.Ready))
        {
            double length = tile.Player.Info!.DurationSeconds;
            double position = Math.Min(_global.IsPlaying ? globalPosition : tile.Clock.Position, length);
            if (!tile.Seeking) tile.Seek.Value = Ratio(position, length);
            tile.Play.Text = (_global.IsPlaying || tile.Clock.IsPlaying) && position < length ? "Ⅱ" : "▶";
            tile.Time.Text = $"{FormatTime(position)} / {FormatTime(length)}";
        }
    }

    private void MarkFailed(PlayerTile tile, Exception error)
    {
        tile.Ready = false;
        tile.Clock.Pause();
        tile.Play.Enabled = tile.Seek.Enabled = false;
        tile.Surface.Status = $"再生できません{Environment.NewLine}{error.Message}";
        tile.Surface.Invalidate();
        _status.Text = $"{tile.Name}: {error.Message}";
    }

    private static int Ratio(double value, double total) => total <= 0 ? 0 : (int)Math.Clamp(value / total * 10_000, 0, 10_000);
    private static string FormatTime(double seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closeReady)
        {
            e.Cancel = true;
            if (!_closing)
            {
                _closing = true;
                _timer.Stop();
                _lifetime.Cancel();
                Enabled = false;
                _ = ClosePlayersAsync();
            }
        }
        base.OnFormClosing(e);
    }

    private async Task ClosePlayersAsync()
    {
        try
        {
            if (_loadTask is not null) await _loadTask;
            if (_renderTask is not null) await _renderTask;
            await Task.WhenAll(_tiles.Select(tile => tile.Player.DisposeAsync().AsTask()));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"再生用プロセスの終了を確認してください。{Environment.NewLine}{ex.Message}", WindowTitle.Create("終了エラー"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _closeReady = true;
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _closing = true;
            _timer.Dispose();
            _lifetime.Cancel();
            try { Task.WhenAll(_tiles.Select(tile => tile.Player.DisposeAsync().AsTask())).GetAwaiter().GetResult(); }
            finally { _lifetime.Dispose(); }
        }
        base.Dispose(disposing);
    }

    private sealed class PlayerTile
    {
        public string Name { get; }
        public FFmpegVideoPlayer Player { get; }
        public PlaybackTimeline Clock { get; } = new();
        public TableLayoutPanel Container { get; }
        public PlaybackSurface Surface { get; } = new() { Dock = DockStyle.Fill, Status = "読み込み中…" };
        public Button Play { get; } = new() { Dock = DockStyle.Fill, Text = "▶", Enabled = false, Margin = new Padding(1) };
        public TrackBar Seek { get; } = new() { Dock = DockStyle.Fill, Maximum = 10_000, TickStyle = TickStyle.None, AutoSize = false, Enabled = false, Margin = new Padding(0) };
        public Label Time { get; } = new() { Dock = DockStyle.Fill, Text = "00:00 / 00:00", TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0) };
        public bool Ready { get; set; }
        public bool Seeking { get; set; }
        public bool ResumeAfterSeek { get; set; }
        public long LastFrame { get; set; } = -1;

        public PlayerTile(string ffmpeg, string path, int index)
        {
            Name = Path.GetFileName(path);
            Player = new(ffmpeg, path);
            Container = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(3), CellBorderStyle = TableLayoutPanelCellBorderStyle.Single, Name = $"Player{index}" };
            Container.RowStyles.Add(new(SizeType.Absolute, 24));
            Container.RowStyles.Add(new(SizeType.Percent, 100));
            Container.RowStyles.Add(new(SizeType.Absolute, 34));
            Label title = new() { Text = Name, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            TableLayoutPanel controls = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
            controls.RowStyles.Add(new(SizeType.Percent, 100));
            controls.ColumnStyles.Add(new(SizeType.Absolute, 36));
            controls.ColumnStyles.Add(new(SizeType.Percent, 100));
            controls.ColumnStyles.Add(new(SizeType.Absolute, 104));
            controls.Controls.Add(Play, 0, 0);
            controls.Controls.Add(Seek, 1, 0);
            controls.Controls.Add(Time, 2, 0);
            Container.Controls.Add(title, 0, 0);
            Container.Controls.Add(Surface, 0, 1);
            Container.Controls.Add(controls, 0, 2);
        }
    }

    private sealed class PlaybackSurface : Control
    {
        private Bitmap? _image;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? Status { get; set; }
        public PlaybackSurface() { DoubleBuffered = true; BackColor = Color.Black; ForeColor = Color.White; }
        public void SetFrame(Bitmap image)
        {
            Bitmap? previous = _image;
            _image = image;
            previous?.Dispose();
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_image is not null && ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                double ratio = Math.Min(ClientSize.Width / (double)_image.Width, ClientSize.Height / (double)_image.Height);
                int width = Math.Max(1, (int)(_image.Width * ratio));
                int height = Math.Max(1, (int)(_image.Height * ratio));
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                e.Graphics.DrawImage(_image, new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height));
            }
            if (Status is not null)
            {
                using SolidBrush background = new(Color.FromArgb(180, 0, 0, 0));
                e.Graphics.FillRectangle(background, ClientRectangle);
                TextRenderer.DrawText(e.Graphics, Status, Font, Rectangle.Inflate(ClientRectangle, -8, -6), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _image?.Dispose(); _image = null; }
            base.Dispose(disposing);
        }
    }
}
