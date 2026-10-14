using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using CapchunScreen.VirtualCamera;

namespace CapchunScreen.VirtualCameraTests;

internal static class Program
{
    private static int passed;
    private static int Main()
    {
        try
        {
            using var bitmap = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
            bitmap.SetPixel(0, 0, Color.Red); bitmap.SetPixel(1, 0, Color.Lime);
            bitmap.SetPixel(0, 1, Color.Blue); bitmap.SetPixel(1, 1, Color.Yellow);
            byte[] expected = [0, 0, 255, 255, 255, 255, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255];
            byte[] converted = new byte[16];
            UnityCaptureSender.ConvertFrame(bitmap, converted, CancellationToken.None);
            Check(converted.SequenceEqual(expected), "bottom-up RGBA8の上下とチャンネル順");
            using (var rgb = bitmap.Clone(new Rectangle(0, 0, 2, 2), PixelFormat.Format24bppRgb))
            {
                UnityCaptureSender.ConvertFrame(rgb, converted, CancellationToken.None);
                Check(converted.SequenceEqual(expected), "24bit入力の変換");
            }
            Throws<ArgumentOutOfRangeException>(() => UnityCaptureSender.ValidateSize(new Size(16384, 16384)), "巨大画像の拒否");

            string prefix = @"Local\CapchunScreen.Tests.UnityCapture." + Guid.NewGuid().ToString("N") + ".";
            var names = new UnityCaptureChannelNames(prefix + "Mutex", prefix + "Want", prefix + "Sent", prefix + "Data");
            using var sender = new UnityCaptureSender(names);
            Check(sender.Send(bitmap) == VirtualCameraSendResult.WaitingForReceiver, "受信アプリがない場合は待機");
            Throws<FileNotFoundException>(() => { using var unexpected = MemoryMappedFile.OpenExisting(names.Data); }, "送信側は受信データ領域を勝手に作らない");

            using var receiverMutex = new Mutex(false, names.Mutex);
            Check(sender.Send(bitmap) == VirtualCameraSendResult.WaitingForReceiver, "相互接続の途中も待機");
            using var want = EventWaitHandle.OpenExisting(names.Want);
            using var sent = new EventWaitHandle(false, EventResetMode.AutoReset, names.Sent);
            using var mapping = MemoryMappedFile.CreateNew(names.Data, UnityCaptureSender.HeaderSize + 4096);
            using var view = mapping.CreateViewAccessor();
            view.Write(0, 4096u);
            want.Set();
            Check(sender.Send(bitmap) == VirtualCameraSendResult.Sent, "受信要求を確認して送信");
            Check(sent.WaitOne(100), "フレーム通知イベント");
            Check(view.ReadUInt32(0) == 4096u, "受信側の容量ヘッダーを保持");
            Check(view.ReadInt32(4) == 2 && view.ReadInt32(8) == 2 && view.ReadInt32(12) == 2, "サイズとピクセルstride");
            Check(view.ReadInt32(16) == 0 && view.ReadInt32(20) == 1 && view.ReadInt32(24) == 0, "RGBA8・拡縮・鏡像なし");
            byte[] received = new byte[16];
            view.ReadArray(UnityCaptureSender.HeaderSize, received, 0, received.Length);
            Check(received.SequenceEqual(expected), "共有メモリの画素を受信側で検証");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Throws<OperationCanceledException>(() => sender.Send(bitmap, cancelled.Token), "送信前のキャンセル");
            }
            using var locked = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                receiverMutex.WaitOne();
                try { locked.Set(); release.Wait(); }
                finally { receiverMutex.ReleaseMutex(); }
            });
            holder.Start();
            locked.Wait();
            try
            {
                var elapsed = Stopwatch.StartNew();
                Throws<TimeoutException>(() => sender.Send(bitmap), "受信側mutexの待機期限");
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(1), "受信側停止で無期限に待たない");
                using var cancelled = new CancellationTokenSource(20);
                Throws<OperationCanceledException>(() => sender.Send(bitmap, cancelled.Token), "mutex待機中のキャンセル");
            }
            finally { release.Set(); holder.Join(); }
            view.Write(0, 8u);
            Throws<InvalidOperationException>(() => sender.Send(bitmap), "容量不足の拒否");
            view.Write(0, 4096u);
            want.Set();
            Check(sender.SendNoSignal() == VirtualCameraSendResult.Sent, "終了時のNo Signal送信");
            sender.Dispose();
            Throws<ObjectDisposedException>(() => sender.Send(bitmap), "破棄後の送信拒否");

            using (var relay = new VirtualCameraRelay(() => new UnityCaptureSender(names)))
            {
                sent.WaitOne(0);
                want.Set();
                relay.Submit((Bitmap)bitmap.Clone());
                Check(sent.WaitOne(2000), "配信ワーカーが受領フレームを送信する");
                var firstStop = relay.StopAsync();
                var secondStop = relay.StopAsync();
                Check(ReferenceEquals(firstStop, secondStop), "停止操作と画面終了が同じ終了Taskを待つ");
                Check(firstStop.Wait(2000) && !relay.IsRunning, "配信ワーカーとNo Signal送信を終了まで待つ");
                using var afterStop = (Bitmap)bitmap.Clone();
                relay.Submit(afterStop);
                Throws<ArgumentException>(() => _ = afterStop.Width, "停止後に届いたフレームを解放する");
            }

            var status = UnityCaptureProvider.Probe();
            Console.WriteLine("PROVIDER: " + status.Message);
            Check(status.Available == (status.Devices.Count > 0), "導入状態とデバイス一覧の整合");
            if (!status.Available) Throws<InvalidOperationException>(() => { using var unavailable = new UnityCaptureSender(); }, "未導入時の正確な理由");
            Console.WriteLine($"PASS: {passed}件（合成受信プロトコル。実ドライバー受信ではありません）");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidDataException(name);
        passed++;
    }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { passed++; return; }
        throw new InvalidDataException(name + "が期待した例外になりませんでした。");
    }
}
