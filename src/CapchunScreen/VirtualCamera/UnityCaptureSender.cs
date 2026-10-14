using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace CapchunScreen.VirtualCamera;

public enum VirtualCameraSendResult { Sent, WaitingForReceiver }

internal sealed record UnityCaptureChannelNames(string Mutex, string Want, string Sent, string Data)
{
    internal static UnityCaptureChannelNames Primary { get; } = new("UnityCapture_Mutx", "UnityCapture_Want", "UnityCapture_Sent", "UnityCapture_Data");
}

/// <summary>UnityCaptureの共有メモリ送信プロトコルをC#で実装。Python・送信用ネイティブDLLは不要。</summary>
public sealed class UnityCaptureSender : IDisposable
{
    internal const int HeaderSize = 32;
    internal const int MaximumDataSize = 3840 * 2160 * 4 * sizeof(short);
    private readonly object gate = new();
    private readonly UnityCaptureChannelNames names;
    private Mutex? mutex;
    private EventWaitHandle? want;
    private EventWaitHandle? sent;
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private byte[] rgba = [];
    private Size lastSize = new(640, 480);
    private long lastReceiverRequest;
    private bool disposed;

    public UnityCaptureSender(int deviceIndex = 0)
    {
        if (deviceIndex != 0) throw new ArgumentOutOfRangeException(nameof(deviceIndex), "この版は最初のUnity Video Captureに対応しています。");
        var status = UnityCaptureProvider.Probe();
        if (!status.Available) throw new InvalidOperationException(status.Message);
        names = UnityCaptureChannelNames.Primary;
    }

    // テストは製品名とは異なる専用オブジェクトだけを使用する。
    internal UnityCaptureSender(UnityCaptureChannelNames testNames) => names = testNames;

    public VirtualCameraSendResult Send(Bitmap frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            int dataSize = ValidateSize(frame.Size);
            if (!TryConnect(cancellationToken)) return VirtualCameraSendResult.WaitingForReceiver;
            if (rgba.Length != dataSize) rgba = new byte[dataSize];
            ConvertFrame(frame, rgba, cancellationToken);
            WithMutex(cancellationToken, () =>
            {
                long capacity = Math.Min(view!.Capacity - HeaderSize, view.ReadUInt32(0));
                if (capacity < dataSize || capacity > MaximumDataSize)
                    throw new InvalidOperationException("Unity Captureの共有映像領域とフレームサイズが一致しません。");
                view.Write(4, frame.Width);
                view.Write(8, frame.Height);
                view.Write(12, frame.Width); // strideはバイト数ではなくピクセル数
                view.Write(16, 0); // FORMAT_UINT8: RGBA 8bit
                view.Write(20, 1); // RESIZEMODE_LINEAR（受信側のアスペクト比維持拡縮）
                view.Write(24, 0); // MIRRORMODE_DISABLED
                view.Write(28, int.MaxValue - 200); // 終了時のNo Signalも受信側に保持する
                view.WriteArray(HeaderSize, rgba, 0, dataSize);
            });
            sent!.Set();
            if (want!.WaitOne(0)) lastReceiverRequest = Stopwatch.GetTimestamp();
            lastSize = frame.Size;
            return lastReceiverRequest != 0 && Stopwatch.GetElapsedTime(lastReceiverRequest) < TimeSpan.FromSeconds(1)
                ? VirtualCameraSendResult.Sent : VirtualCameraSendResult.WaitingForReceiver;
        }
    }

    public VirtualCameraSendResult SendNoSignal(CancellationToken cancellationToken = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(250);
        Size size;
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); size = lastSize; }
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font(FontFamily.GenericSansSerif, Math.Max(14, Math.Min(size.Width, size.Height) / 10f), FontStyle.Bold))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.Clear(Color.Black);
            graphics.DrawString("No Signal", font, Brushes.White, new RectangleF(0, 0, size.Width, size.Height), format);
        }
        return Send(bitmap, bounded.Token);
    }

    private bool TryConnect(CancellationToken cancellationToken)
    {
        if (view is not null) return true;
        try
        {
            mutex ??= Mutex.OpenExisting(names.Mutex);
            bool connected = false;
            WithMutex(cancellationToken, () =>
            {
                // 受信側が最初のmutexを作り、送信側がWantイベントを作る相互接続手順。
                want ??= new EventWaitHandle(false, EventResetMode.AutoReset, names.Want);
                sent ??= EventWaitHandle.OpenExisting(names.Sent);
                mapping ??= MemoryMappedFile.OpenExisting(names.Data, MemoryMappedFileRights.ReadWrite);
                view ??= mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                if (view.Capacity < HeaderSize) throw new InvalidOperationException("Unity Captureの共有ヘッダーが不正です。");
                connected = true;
            });
            return connected;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    private void WithMutex(CancellationToken cancellationToken, Action action)
    {
        bool acquired = false;
        try
        {
            int result;
            try { result = WaitHandle.WaitAny([mutex!, cancellationToken.WaitHandle], 100); }
            catch (AbandonedMutexException) { result = 0; }
            acquired = result == 0;
            if (result == 1) cancellationToken.ThrowIfCancellationRequested();
            if (!acquired) throw new TimeoutException("Unity Captureの受信側が映像領域を使用中のため送信を中断しました。");
            cancellationToken.ThrowIfCancellationRequested();
            action();
        }
        finally { if (acquired) mutex!.ReleaseMutex(); }
    }

    internal static int ValidateSize(Size size)
    {
        if (size.Width < 1 || size.Height < 1 || size.Width > 16384 || size.Height > 16384)
            throw new ArgumentOutOfRangeException(nameof(size), "仮想カメラのフレームサイズが対応範囲外です。");
        long bytes = (long)size.Width * size.Height * 4;
        if (bytes > MaximumDataSize) throw new ArgumentOutOfRangeException(nameof(size), "仮想カメラの共有映像領域に収まらないサイズです。");
        return checked((int)bytes);
    }

    internal static void ConvertFrame(Bitmap frame, byte[] destination, CancellationToken cancellationToken)
    {
        int expected = ValidateSize(frame.Size);
        if (destination.Length != expected) throw new ArgumentException("変換先のバッファサイズが一致しません。", nameof(destination));
        using var normalized = frame.PixelFormat == PixelFormat.Format32bppArgb ? null
            : frame.Clone(new Rectangle(Point.Empty, frame.Size), PixelFormat.Format32bppArgb);
        var source = normalized ?? frame;
        var bits = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowSize = source.Width * 4;
            for (int y = 0; y < source.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int offset = (source.Height - 1 - y) * rowSize;
                Marshal.Copy(bits.Scan0 + y * bits.Stride, destination, offset, rowSize);
                for (int x = 0; x < rowSize; x += 4)
                {
                    (destination[offset + x], destination[offset + x + 2]) = (destination[offset + x + 2], destination[offset + x]);
                    destination[offset + x + 3] = 255;
                }
            }
        }
        finally { source.UnlockBits(bits); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            view?.Dispose(); mapping?.Dispose(); sent?.Dispose(); want?.Dispose(); mutex?.Dispose();
            view = null; mapping = null; sent = null; want = null; mutex = null;
            rgba = [];
        }
    }
}
