using System.ComponentModel;
using System.Drawing.Drawing2D;
using CapchunScreen.Playback;

namespace CapchunScreen.UI;

public sealed class MultiPlaybackForm : BrandedForm
{
    private readonly List<PlayerTile> _tiles = [];
    private bool _allPlaying;
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
    private bool _fullScreen;
    private Rectangle _normalBounds;
    private FormWindowState _normalWindowState;
    private double GlobalPosition => _tiles.Where(tile => tile.Ready).Select(Position).DefaultIfEmpty(0).Max();
    private static double Position(PlayerTile tile) => Math.Min(tile.Clock.Position, tile.Player.Info?.DurationSeconds ?? 0);
    private double Duration => _tiles.Where(tile => tile.Ready).Select(tile => tile.Player.Info!.DurationSeconds).DefaultIfEmpty(0).Max();

    public MultiPlaybackForm(string ffmpegPath, IReadOnlyList<string> videoPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(videoPaths);
        if (videoPaths.Count is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(videoPaths), "同時再生する動画は 1 ～ 16 本を選択してください。");
        Text = WindowTitle.Create(videoPaths.Count == 1 ? "動画の再生" : "複数動画の同時再生");
        StartPosition = FormStartPosition.CenterParent;
        Size = new(1120, 740);
        MinimumSize = videoPaths.Count > 9 ? new(960, 640) : new(760, 500);
        TableLayoutPanel root = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        root.RowStyles.Add(new(SizeType.Absolute, 48));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 28));
        TableLayoutPanel controls = new() { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 6 };
        controls.RowStyles.Add(new(SizeType.Percent, 100));
        controls.ColumnStyles.Add(new(SizeType.Absolute, 108));
        controls.ColumnStyles.Add(new(SizeType.Absolute, 100));
        controls.ColumnStyles.Add(new(SizeType.Absolute, 80));
        controls.ColumnStyles.Add(new(SizeType.Percent, 100));
        controls.ColumnStyles.Add(new(SizeType.Absolute, 130));
        controls.ColumnStyles.Add(new(SizeType.Absolute, 100));
        controls.Controls.Add(_play, 0, 0);
        controls.Controls.Add(_pause, 1, 0);
        controls.Controls.Add(_start, 2, 0);
        controls.Controls.Add(_seek, 3, 0);
        controls.Controls.Add(_time, 4, 0);
        var help = new Button { Text = "キー操作…", Dock = DockStyle.Fill, Name = "ShortcutHelp" };
        help.Click += (_, _) => ShowShortcutHelp();
        controls.Controls.Add(help, 5, 0);
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
                tile.ResumeAfterSeek = tile.Clock.IsPlaying;
                if (_allPlaying) PauseAll();
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
        _seek.MouseDown += (_, _) => { _resumeAfterSeek = _allPlaying; PauseAll(); _seeking = true; };
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
                ? "各動画の現在位置から再生します。全体シークは同じ秒位置へ移動。キー操作は上部または動画枠を選択。"
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
        foreach (PlayerTile tile in _tiles.Where(tile => tile.Ready && Position(tile) < tile.Player.Info!.DurationSeconds))
            tile.Clock.Play();
        _allPlaying = _tiles.Any(tile => tile.Ready && tile.Clock.IsPlaying);
        UpdateControls();
    }

    private void PauseAll()
    {
        _allPlaying = false;
        foreach (PlayerTile tile in _tiles)
            tile.Clock.Pause();
        UpdateControls();
    }

    private void SeekAll(double ratio)
    {
        bool resume = _allPlaying;
        PauseAll();
        double seconds = Duration * Math.Clamp(ratio, 0, 1);
        foreach (PlayerTile tile in _tiles) tile.Clock.Seek(Math.Min(seconds, tile.Player.Info?.DurationSeconds ?? 0));
        if (resume) PlayAll();
        UpdateControls();
    }

    private void ToggleIndividual(PlayerTile tile)
    {
        if (!tile.Ready) return;
        bool wasPlaying = tile.Clock.IsPlaying && Position(tile) < tile.Player.Info!.DurationSeconds;
        if (_allPlaying) PauseAll();
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
        if (_allPlaying) PauseAll();
        tile.Clock.Seek(requestedSeconds);
        UpdateControls();
    }

    private async Task RenderAsync()
    {
        _renderBusy = true;
        try
        {
            await Task.WhenAll(_tiles.Where(tile => tile.Ready).Select(async tile =>
            {
                double duration = tile.Player.Info!.DurationSeconds;
                double seconds = Position(tile);
                if (tile.Clock.IsPlaying && seconds >= duration)
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
            if (!_closing)
            {
                if (!_tiles.Any(tile => tile.Ready && tile.Clock.IsPlaying)) _allPlaying = false;
                UpdateControls();
            }
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
        double globalPosition = GlobalPosition;
        _time.Text = $"{FormatTime(globalPosition)} / {FormatTime(duration)}";
        if (!_seeking) _seek.Value = Ratio(globalPosition, duration);
        foreach (PlayerTile tile in _tiles.Where(tile => tile.Ready))
        {
            double length = tile.Player.Info!.DurationSeconds;
            double position = Position(tile);
            if (!tile.Seeking) tile.Seek.Value = Ratio(position, length);
            tile.Play.Text = tile.Clock.IsPlaying && position < length ? "Ⅱ" : "▶";
            tile.Time.Text = $"{FormatTime(position)} / {FormatTime(length)}";
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        PlayerTile? tile = _tiles.FirstOrDefault(item => item.Container.ContainsFocus);
        if (HandlePlaybackKey(keyData, tile)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool HandlePlaybackKey(Keys keyData, PlayerTile? tile)
    {
        if (_closing || (keyData & (Keys.Control | Keys.Alt)) != Keys.None) return false;
        Keys key = keyData & Keys.KeyCode;
        if (key == Keys.Escape && _fullScreen) { ToggleFullScreen(); return true; }
        if (key == Keys.F && (keyData & Keys.Modifiers) == Keys.None) { ToggleFullScreen(); return true; }
        if (key == Keys.F1 || keyData == (Keys.Shift | Keys.OemQuestion)) { ShowShortcutHelp(); return true; }
        if (Duration <= 0 || (tile is not null && !tile.Ready)) return false;
        if (keyData is (Keys.Shift | Keys.OemPeriod) or (Keys.Shift | Keys.Oemcomma))
        {
            var targets = tile is null ? _tiles.Where(item => item.Ready) : [tile];
            foreach (var item in targets) item.Clock.Rate = Math.Clamp(item.Clock.Rate + (key == Keys.OemPeriod ? .25 : -.25), .25, 2);
            _status.Text = $"{(tile is null ? "全体" : tile.Name)}の再生速度: {(tile ?? _tiles.First(item => item.Ready)).Clock.Rate:0.##}x";
            return true;
        }
        if ((keyData & Keys.Shift) != Keys.None) return false;
        if (key is Keys.K or Keys.Space or Keys.MediaPlayPause)
        {
            if (tile is not null) ToggleIndividual(tile);
            else if (_tiles.Any(item => item.Ready && item.Clock.IsPlaying)) PauseAll();
            else PlayAll();
            return true;
        }
        double? delta = key switch { Keys.J => -10, Keys.L => 10, Keys.Left => -5, Keys.Right => 5, _ => null };
        bool step = key is Keys.Oemcomma or Keys.OemPeriod;
        if (step)
        {
            bool playing = tile?.Clock.IsPlaying ?? _tiles.Any(item => item.Ready && item.Clock.IsPlaying);
            if (playing) return true;
            delta = key == Keys.OemPeriod ? 1d / 30 : -1d / 30;
        }
        double? ratio = key switch
        {
            Keys.Home => 0, Keys.End => 1,
            >= Keys.D0 and <= Keys.D9 => ((int)key - (int)Keys.D0) / 10d,
            >= Keys.NumPad0 and <= Keys.NumPad9 => ((int)key - (int)Keys.NumPad0) / 10d,
            _ => null
        };
        if (delta is null && ratio is null) return false;
        if (tile is not null)
        {
            bool resume = tile.Clock.IsPlaying;
            if (_allPlaying) PauseAll();
            double seconds = ratio is { } fraction ? tile.Player.Info!.DurationSeconds * fraction : Position(tile) + delta!.Value;
            tile.Clock.Seek(Math.Clamp(seconds, 0, tile.Player.Info!.DurationSeconds));
            if (resume && Position(tile) < tile.Player.Info.DurationSeconds) tile.Clock.Play();
        }
        else if (ratio is { } fraction) SeekAll(fraction);
        else
        {
            // 相対移動は開始位置の差を保つ。端に到達した動画だけをクランプする。
            foreach (var item in _tiles.Where(item => item.Ready))
                item.Clock.Seek(Math.Clamp(Position(item) + delta!.Value, 0, item.Player.Info!.DurationSeconds));
        }
        UpdateControls();
        return true;
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _normalWindowState = WindowState;
            _normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            _fullScreen = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _normalBounds;
            WindowState = _normalWindowState;
            _fullScreen = false;
        }
    }

    private void ShowShortcutHelp()
    {
        const string text = "上部の操作欄を選ぶと全体、動画の映像・操作欄を選ぶとその動画を操作します。\n\n"
            + "Space / K：再生・一時停止\nJ / L：10秒戻る・進む\n← / →：5秒戻る・進む\n"
            + "Home / End：先頭・最後\n0～9：0～90%へ移動\n, / .：一時停止中に前・次の表示フレーム（30fps）\n"
            + "< / >（Shift + , / .）：再生速度を0.25倍ずつ変更（0.25～2倍）\nF：全画面切替、Esc：全画面終了\nF1 / ?：この案内\n\n"
            + "「すべて再生」は各動画の現在位置から再開します。\n「先頭へ」と全体の数字・Home/End・シークバーは同じ秒位置にそろえます。";
        DialogResult Show() => MessageBox.Show(this, text, WindowTitle.Create("動画のキー操作"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (Owner is MainForm main) main.WithPreviewHidden(Show);
        else Show();
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
            Surface.TabStop = true;
            Surface.MouseDown += (_, _) => Surface.Focus();
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
