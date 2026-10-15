using CapchunScreen.Capture;

namespace CapchunScreen.UI;

/// <summary>既存の取得器を1枚だけ使い、閉じたら録画用資源を残さない。</summary>
internal sealed class FrameThumbnailCapture(Func<CaptureTarget, bool, IFrameSource>? factory = null) : IThumbnailCapture
{
    public Task<ThumbnailCaptureResult> CaptureAsync(CaptureTarget target, Size maximumSize, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = (factory ?? CaptureFactory.Create)(target, true);
            using var frame = source.Capture(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Size size = PreviewForm.FitSizeToWorkingArea(frame.Image.Size, maximumSize);
            Bitmap? image = new(size.Width, size.Height);
            try
            {
                using (var graphics = Graphics.FromImage(image)) graphics.DrawImage(frame.Image, new Rectangle(Point.Empty, size));
                cancellationToken.ThrowIfCancellationRequested();
                var result = new ThumbnailCaptureResult(image, ""); image = null; return result;
            }
            finally { image?.Dispose(); }
        }, cancellationToken);
    public void Dispose() { }
}
