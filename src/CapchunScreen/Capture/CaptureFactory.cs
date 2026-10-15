using System.Drawing.Imaging;

namespace CapchunScreen.Capture;

public static class CaptureFactory
{
    public static IFrameSource Create(CaptureTarget target, bool exclusiveWindow)
        => Create(target, exclusiveWindow, DesktopAccessGuard.Shared);

    internal static IFrameSource Create(CaptureTarget target, bool exclusiveWindow, DesktopAccessGuard guard)
    {
        ArgumentNullException.ThrowIfNull(target);
        guard.RequireAccess();
        try
        {
            IFrameSource source = target.Kind switch
            {
                CaptureKind.Window when exclusiveWindow => new WindowFrameSource(target),
                CaptureKind.Window or CaptureKind.Monitor => new DesktopFrameSource(target),
                _ => throw new ArgumentOutOfRangeException(nameof(target))
            };
            return new DesktopGuardedFrameSource(source, guard);
        }
        catch (Exception error) when (error is not DesktopAccessException && DesktopAccessGuard.IsAccessDenied(error))
        { throw guard.Failure(error); }
    }
}

internal sealed class DesktopFrameSource : IFrameSource
{
    private readonly object gate = new();
    private readonly CaptureTarget target;
    private readonly (uint ProcessId, uint ThreadId) identity;
    private int disposed;

    internal DesktopFrameSource(CaptureTarget target)
    {
        this.target = target;
        if (target.Kind == CaptureKind.Window) identity = CaptureNative.Identity(target.WindowHandle);
    }

    public CapturedFrame Capture(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var bounds = target.Kind == CaptureKind.Window
                ? CaptureNative.RequireWindowBounds(target.WindowHandle, identity)
                : Screen.AllScreens.SingleOrDefault(screen => screen.DeviceName == target.Id)?.Bounds
                    ?? throw new InvalidOperationException("選択したモニターが接続されていません。選び直してください。");
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 32766 || bounds.Height > 32766)
                throw new InvalidOperationException("取得範囲の大きさが対応範囲外です。");
            // GDIの未定義alphaを除き、返却するBGRAを常に不透明にする。
            using var rgb = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(rgb))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var bitmap = rgb.Clone(new Rectangle(Point.Empty, bounds.Size), PixelFormat.Format32bppArgb);
            return new CapturedFrame(bitmap, bounds);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        lock (gate) { }
    }
}
