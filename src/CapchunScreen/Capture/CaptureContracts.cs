namespace CapchunScreen.Capture;

public enum CaptureKind { Monitor, Window }

public sealed record CaptureTarget(string Id, string Name, CaptureKind Kind, nint WindowHandle, Rectangle Bounds)
{
    public override string ToString() => Name;
}

public sealed record CapturedFrame(Bitmap Image, Rectangle Bounds) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

public interface IFrameSource : IDisposable
{
    CapturedFrame Capture(CancellationToken cancellationToken);
}
