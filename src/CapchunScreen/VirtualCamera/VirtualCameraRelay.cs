namespace CapchunScreen.VirtualCamera;

/// <summary>UIから受け取った最新フレームだけを、専用ワーカーで送る。</summary>
internal sealed class VirtualCameraRelay : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private Bitmap? pending;
    private bool disposed;
    private Task? stopTask;
    private string status = "受信アプリの接続待ち";
    internal string Status => Volatile.Read(ref status);
    internal bool IsRunning => !worker.IsCompleted;

    internal VirtualCameraRelay(Func<UnityCaptureSender>? senderFactory = null) =>
        worker = Task.Run(() => RunAsync(senderFactory ?? (() => new UnityCaptureSender())));

    internal void Submit(Bitmap ownedFrame)
    {
        lock (gate)
        {
            if (disposed || worker.IsCompleted) { ownedFrame.Dispose(); return; }
            var old = pending; pending = ownedFrame; old?.Dispose();
        }
    }

    private async Task RunAsync(Func<UnityCaptureSender> senderFactory)
    {
        try
        {
            using var sender = senderFactory();
            bool noSignal = false;
            var lastFrame = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    Bitmap? frame;
                    lock (gate) { frame = pending; pending = null; }
                    if (frame is not null)
                    {
                        using (frame)
                        {
                            var result = sender.Send(frame, cancellation.Token);
                            Volatile.Write(ref status, result == VirtualCameraSendResult.Sent ? "Unity Capture 配信中" : "受信アプリの接続待ち");
                        }
                        noSignal = false; lastFrame.Restart();
                    }
                    else if (!noSignal && lastFrame.ElapsedMilliseconds >= 500)
                    {
                        sender.SendNoSignal(cancellation.Token);
                        Volatile.Write(ref status, "映像待機中：プレビューか録画を開始");
                        noSignal = true;
                    }
                    await Task.Delay(50, cancellation.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                try { sender.SendNoSignal(); }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException) { }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { Volatile.Write(ref status, "配信停止: " + error.Message); }
        finally { lock (gate) { pending?.Dispose(); pending = null; } }
    }

    internal Task StopAsync()
    {
        lock (gate) return stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        disposed = true;
        cancellation.Cancel();
        await worker.ConfigureAwait(false);
        cancellation.Dispose();
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
