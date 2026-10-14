namespace CapchunScreen.UI;

/// <summary>親が取得した映像だけを表示する。キャプチャや録画は所有しない。</summary>
public sealed class PreviewForm : BrandedForm
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly PictureBox _picture = new()
    {
        Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom,
        AccessibleName = "独立プレビュー映像", TabStop = false
    };
    private readonly Label _waiting = new()
    {
        Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = Color.White,
        Text = "プレビュー待機中\n左ドラッグで移動・Escで閉じる", TextAlign = ContentAlignment.MiddleCenter,
        AccessibleName = "独立プレビューの操作案内", TabStop = false
    };
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false };
    private bool _dragging;
    private Point _dragStartCursor;
    private Point _dragStartLocation;

    public PreviewForm()
    {
        Text = WindowTitle.Create("独立プレビュー");
        AccessibleName = "独立プレビュー";
        AccessibleDescription = "映像を実寸で表示します。画面に収まらない映像だけ縮小します。左ドラッグで移動し、Escapeまたは右クリックメニューで閉じます。";
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = Color.Black;
        ClientSize = new Size(480, 270);
        Controls.Add(_picture);
        Controls.Add(_waiting);
        _menu.Items.Add("閉じる(&C)", null, (_, _) => Close());
        ContextMenuStrip = _menu;
        foreach (Control control in new Control[] { _picture, _waiting })
        {
            control.ContextMenuStrip = _menu;
            control.MouseDown += BeginDrag;
            control.MouseMove += ContinueDrag;
            control.MouseUp += EndDrag;
            control.MouseCaptureChanged += (_, _) =>
            {
                if (_dragging && !control.Capture) FinishDrag();
            };
        }
        KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Escape) return;
            args.Handled = true;
            args.SuppressKeyPress = true;
            Close();
        };
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// UIスレッドから呼ぶ。渡されたBitmapの所有権を受け取り、次の映像または終了時に解放する。
    /// 呼出側が画像を継続利用する場合はCloneを渡す。閉じた後に届いた画像も解放する。
    /// </summary>
    public void SetFrame(Bitmap ownedFrame)
    {
        ArgumentNullException.ThrowIfNull(ownedFrame);
        if (_ownerThreadId != Environment.CurrentManagedThreadId)
        {
            ownedFrame.Dispose();
            throw new InvalidOperationException("独立プレビューの映像は作成したUIスレッドで渡してください。");
        }
        if (IsDisposed || Disposing)
        {
            ownedFrame.Dispose();
            return;
        }
        if (ReferenceEquals(_picture.Image, ownedFrame)) return;
        try
        {
            Rectangle workingArea = Screen.FromPoint(Location).WorkingArea;
            Size displaySize = FitSizeToWorkingArea(ownedFrame.Size, workingArea.Size);
            if (ClientSize != displaySize)
            {
                ClientSize = displaySize;
                if (!_dragging) Location = KeepInsideWorkingArea(Location, displaySize, workingArea);
            }
        }
        catch
        {
            ownedFrame.Dispose();
            throw;
        }
        Image? previous = _picture.Image;
        _picture.Image = ownedFrame;
        _waiting.Visible = false;
        previous?.Dispose();
    }

    /// <summary>表示画像だけを解放し、待機表示へ戻す。録画・取得の停止は親が判断する。</summary>
    public void ClearFrame()
    {
        if (_ownerThreadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("独立プレビューは作成したUIスレッドで操作してください。");
        if (IsDisposed || Disposing) return;
        ReleaseFrame();
        _waiting.Visible = true;
    }

    internal static Size FitSizeToWorkingArea(Size source, Size available)
    {
        if (source.Width < 1 || source.Height < 1 || available.Width < 1 || available.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(source), "プレビューの映像と表示領域には正の寸法が必要です。");
        double scale = Math.Min(1, Math.Min(available.Width / (double)source.Width, available.Height / (double)source.Height));
        return new Size(Math.Max(1, (int)Math.Floor(source.Width * scale)), Math.Max(1, (int)Math.Floor(source.Height * scale)));
    }

    internal static Point KeepInsideWorkingArea(Point location, Size size, Rectangle workingArea) => new(
        Math.Clamp(location.X, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - size.Width)),
        Math.Clamp(location.Y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - size.Height)));

    private void BeginDrag(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left || sender is not Control control) return;
        _dragging = true;
        _dragStartCursor = Cursor.Position;
        _dragStartLocation = Location;
        control.Capture = true;
        Cursor = Cursors.SizeAll;
    }

    private void ContinueDrag(object? sender, MouseEventArgs args)
    {
        if (!_dragging || !args.Button.HasFlag(MouseButtons.Left)) return;
        Point cursor = Cursor.Position;
        Location = new Point(_dragStartLocation.X + cursor.X - _dragStartCursor.X,
            _dragStartLocation.Y + cursor.Y - _dragStartCursor.Y);
    }

    private void EndDrag(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left || !_dragging) return;
        FinishDrag();
        if (sender is Control control) control.Capture = false;
    }

    private void FinishDrag()
    {
        _dragging = false;
        Cursor = Cursors.Default;
        Rectangle workingArea = Screen.FromPoint(Location).WorkingArea;
        Size displaySize = _picture.Image is { } image ? FitSizeToWorkingArea(image.Size, workingArea.Size)
            : FitSizeToWorkingArea(ClientSize, workingArea.Size);
        ClientSize = displaySize;
        Location = KeepInsideWorkingArea(Location, displaySize, workingArea);
    }

    private void ReleaseFrame()
    {
        Image? previous = _picture.Image;
        _picture.Image = null;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseFrame();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
