using System.Drawing.Imaging;
using CapchunScreen.Capture;

namespace CapchunScreen.CaptureSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("使用方法: CapchunScreen.CaptureSmoke.exe <証跡フォルダー>");
            return 2;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var directory = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(directory);
        int exitCode = 1;
        using var target = new Form
        {
            Text = "キャプチャ検証用 - CapchunScreen - ver 0.1.0",
            BackColor = Color.FromArgb(30, 190, 70), ClientSize = new Size(480, 300),
            StartPosition = FormStartPosition.Manual, Location = new Point(80, 100),
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false
        };
        using var occluder = new Form
        {
            Text = "重なり検証用 - CapchunScreen - ver 0.1.0", BackColor = Color.Magenta,
            ClientSize = new Size(300, 180), StartPosition = FormStartPosition.Manual,
            Location = new Point(140, 150), FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false
        };
        target.Shown += async (_, _) =>
        {
            try
            {
                var captureTarget = new CaptureTarget($"window:{target.Handle:X}", target.Text, CaptureKind.Window, target.Handle, target.Bounds);
                using var source = CaptureFactory.Create(captureTarget, true);
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await Task.Delay(200);
                using (var frame = await Task.Run(() => source.Capture(cancellation.Token)))
                {
                    RequireGreen(frame.Image, "初回WGC");
                    frame.Image.Save(Path.Combine(directory, "wgc-initial.png"), ImageFormat.Png);
                }
                occluder.Show();
                occluder.BringToFront();
                await Task.Delay(250);
                using (var exclusive = await Task.Run(() => source.Capture(cancellation.Token)))
                {
                    RequireGreen(exclusive.Image, "他窓が重なったWGC");
                    exclusive.Image.Save(Path.Combine(directory, "wgc-occluded.png"), ImageFormat.Png);
                }
                using (var desktop = CaptureFactory.Create(captureTarget, false))
                using (var frame = desktop.Capture(cancellation.Token))
                {
                    var color = frame.Image.GetPixel(150, 100);
                    if (color.R < 180 || color.B < 180 || color.G > 100) throw new InvalidDataException("GDI対照画像に重ねた窓がありません。");
                    frame.Image.Save(Path.Combine(directory, "gdi-occluded.png"), ImageFormat.Png);
                }
                occluder.Hide();
                target.Location = new Point(180, 180);
                target.ClientSize = new Size(560, 340);
                await Task.Delay(300);
                CapturedFrame? resized = null;
                try
                {
                    for (int attempt = 0; attempt < 15; attempt++)
                    {
                        resized?.Dispose();
                        resized = await Task.Run(() => source.Capture(cancellation.Token));
                        if (resized.Image.Size == target.ClientSize) break;
                        await Task.Delay(50);
                    }
                    if (resized!.Image.Size != target.ClientSize || resized.Bounds.Location != target.Location)
                        throw new InvalidDataException("WGCのリサイズまたは位置追従が一致しません。");
                    RequireGreen(resized.Image, "リサイズWGC");
                    resized.Image.Save(Path.Combine(directory, "wgc-resized.png"), ImageFormat.Png);
                }
                finally { resized?.Dispose(); }
                // 再描画を続けない静止窓でも、縮小と再拡大の両方で新サイズを取得する。
                foreach (var size in new[] { new Size(360, 220), new Size(620, 380) })
                {
                    target.ClientSize = size;
                    await Task.Delay(180);
                    using var frame = await Task.Run(() => source.Capture(cancellation.Token));
                    if (frame.Image.Size != size) throw new InvalidDataException($"静止窓の再リサイズが反映されません: {frame.Image.Size} / {size}");
                    RequireGreen(frame.Image, "静止窓の再リサイズWGC");
                    frame.Image.Save(Path.Combine(directory, $"wgc-resized-{size.Width}x{size.Height}.png"), ImageFormat.Png);
                }
                target.WindowState = FormWindowState.Minimized;
                await Task.Delay(100);
                bool minimizedRejected = false;
                try { using var unexpected = await Task.Run(() => source.Capture(cancellation.Token)); }
                catch (InvalidOperationException) { minimizedRejected = true; }
                if (!minimizedRejected) throw new InvalidDataException("最小化時に明示エラーになりませんでした。");
                target.WindowState = FormWindowState.Normal;
                await Task.Delay(100);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                bool cancellationRejected = false;
                try { using var unexpected = source.Capture(cancelled.Token); }
                catch (OperationCanceledException) { cancellationRejected = true; }
                if (!cancellationRejected) throw new InvalidDataException("キャンセル要求が反映されませんでした。");
                using (var closedTarget = new Form { Text = "終了検証用", ClientSize = new Size(160, 120), ShowInTaskbar = false })
                {
                    closedTarget.Show();
                    var closedCaptureTarget = new CaptureTarget("closed", closedTarget.Text, CaptureKind.Window, closedTarget.Handle, closedTarget.Bounds);
                    using var closedSource = CaptureFactory.Create(closedCaptureTarget, true);
                    closedTarget.Close();
                    await Task.Delay(100);
                    bool closedRejected = false;
                    try { using var unexpected = await Task.Run(() => closedSource.Capture(cancellation.Token)); }
                    catch (InvalidOperationException) { closedRejected = true; }
                    if (!closedRejected) throw new InvalidDataException("対象終了時に明示エラーになりませんでした。");
                }
                source.Dispose();
                bool disposedRejected = false;
                try { using var unexpected = source.Capture(CancellationToken.None); }
                catch (ObjectDisposedException) { disposedRejected = true; }
                if (!disposedRejected) throw new InvalidDataException("終了後に取得できてしまいました。");
                Console.WriteLine("PASS: WGC初回・他窓分離/GDI対照・移動/静止窓の拡大縮小・最小化拒否・キャンセル・対象終了・終了後拒否");
                exitCode = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { target.Close(); }
        };
        Application.Run(target);
        return exitCode;
    }

    private static void RequireGreen(Bitmap bitmap, string name)
    {
        var color = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        if (color.G < 140 || color.R > 100 || color.B > 120)
            throw new InvalidDataException($"{name}の中心色が合成窓と一致しません: {color}");
    }
}
