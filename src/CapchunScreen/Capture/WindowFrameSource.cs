using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace CapchunScreen.Capture;

/// <summary>選択HWNDだけのWGC取得。失敗時も画面取得には切り替えない。</summary>
internal sealed class WindowFrameSource : IFrameSource
{
    private readonly object gate = new();
    private readonly nint window;
    private readonly (uint ProcessId, uint ThreadId) identity;
    private CaptureDirect3D? graphics;
    private GraphicsCaptureItem? item;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private SizeInt32 poolSize;
    private Bitmap? latest;
    private int disposed;
    private int closed;

    internal WindowFrameSource(CaptureTarget target)
    {
        window = target.WindowHandle;
        identity = CaptureNative.Identity(window);
        _ = CaptureNative.RequireWindowBounds(window, identity);
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                throw new NotSupportedException("この環境ではウィンドウ専用取得（Windows Graphics Capture）を利用できません。");
            item = CaptureDirect3D.CreateWindowItem(window);
            item.Closed += ItemClosed;
            poolSize = item.Size;
            ValidateSize(poolSize);
            graphics = new CaptureDirect3D();
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(graphics.ProjectedDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            session.StartCapture();
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch { /* 作成時の原因を、後始末の例外で隠さない。 */ }
            throw new InvalidOperationException("ウィンドウ専用取得を開始できません。対象とWindowsのキャプチャ設定を確認してください。", error);
        }
    }

    private void ItemClosed(GraphicsCaptureItem sender, object args) => Interlocked.Exchange(ref closed, 1);

    public CapturedFrame Capture(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            void Check()
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                if (Volatile.Read(ref closed) != 0) throw new InvalidOperationException("選択したウィンドウの取得が終了しました。選び直してください。");
            }
            Check();
            long deadline = Environment.TickCount64 + 3000;
            while (true)
            {
                Check();
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException("ウィンドウの映像を取得できません。最小化・保護された映像・対象アプリの状態を確認してください。");
                var bounds = CaptureNative.RequireWindowBounds(window, identity);
                Direct3D11CaptureFrame? frame = null;
                try
                {
                    // 先着の古いフレームを捨て、プール内の最新フレームを取得する。
                    for (int count = 0; count < 3; count++)
                    {
                        var next = pool!.TryGetNextFrame();
                        if (next is null) break;
                        frame?.Dispose();
                        frame = next;
                    }
                    if (frame is not null)
                    {
                        var size = frame.ContentSize;
                        ValidateSize(size);
                        if (size.Width != poolSize.Width || size.Height != poolSize.Height)
                        {
                            frame.Dispose();
                            frame = null;
                            latest?.Dispose();
                            latest = null;
                            poolSize = size;
                            // 静止窓はRecreateだけでは次の描画が来ないことがある。
                            // 同じ取得対象のセッションを作り直して、完全サイズの初回フレームを要求する。
                            session!.Dispose();
                            session = null;
                            pool!.Dispose();
                            pool = null;
                            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(graphics!.ProjectedDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
                            session = pool.CreateCaptureSession(item!);
                            session.IsCursorCaptureEnabled = false;
                            session.StartCapture();
                            continue;
                        }
                        using var surface = frame.Surface;
                        var bitmap = graphics!.ReadBitmap(surface, size.Width, size.Height, Check);
                        latest?.Dispose();
                        latest = bitmap;
                    }
                    if (latest is not null)
                    {
                        Check();
                        bounds = CaptureNative.RequireWindowBounds(window, identity);
                        return new CapturedFrame((Bitmap)latest.Clone(), new Rectangle(bounds.Location, latest.Size));
                    }
                }
                finally { frame?.Dispose(); }
                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException("ウィンドウの映像を取得できません。最小化・保護された映像・対象アプリの状態を確認してください。");
                if (cancellationToken.WaitHandle.WaitOne(10)) cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static void ValidateSize(SizeInt32 size)
    {
        if (size.Width < 1 || size.Height < 1 || size.Width > 32766 || size.Height > 32766)
            throw new InvalidOperationException("ウィンドウの描画サイズが対応範囲外です。");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lock (gate)
        {
            if (item is not null) item.Closed -= ItemClosed;
            try { session?.Dispose(); }
            finally
            {
                session = null;
                try { pool?.Dispose(); }
                finally
                {
                    pool = null;
                    latest?.Dispose();
                    latest = null;
                    graphics?.Dispose();
                    graphics = null;
                    item = null;
                }
            }
        }
    }
}
