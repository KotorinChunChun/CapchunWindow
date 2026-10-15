using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CapchunScreen.Capture;
using CapchunScreen.Input;

namespace CapchunScreen.Recording;

public sealed class RecordingSession
{
    public Task<RecordingResult> RunAsync(
        IFrameSource source,
        IInputSource? input,
        nint targetWindow,
        RecordingOptions options,
        CancellationToken stopToken,
        Action<Bitmap>? preview = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => RunCoreAsync(source, input, targetWindow, options, stopToken, preview));
    }

    private static async Task<RecordingResult> RunCoreAsync(
        IFrameSource source, IInputSource? input, nint targetWindow, RecordingOptions options,
        CancellationToken stopToken, Action<Bitmap>? preview)
    {
        stopToken.ThrowIfCancellationRequested();
        Validate(options);
        if (options.RecordInput && input is null)
            throw new ArgumentException("操作記録が有効ですが、入力の取得元がありません。", nameof(input));
        options = options with { Container = options.Container.ToLowerInvariant() };
        string directory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(directory);
        string unique = Guid.NewGuid().ToString("N");
        string name = $"CapchunWindow_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{unique[..8]}";
        string video = Path.Combine(directory, $"{name}.{options.Container}");
        string? tsv = options.RecordInput ? Path.Combine(directory, $"{name}.tsv") : null;
        string partialVideo = Path.Combine(directory, $".capchunscreen-{unique}.partial.{options.Container}");
        string partialTsv = Path.Combine(directory, $".capchunscreen-{unique}.partial.tsv");
        bool tsvPublished = false;
        try
        {
            using CapturedFrame first = source.Capture(stopToken);
            stopToken.ThrowIfCancellationRequested();
            Size size = OutputSize(first.Image.Size);
            using Bitmap canvas = new(size.Width, size.Height, PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[checked(size.Width * size.Height * 4)];
            CopyFrame(first.Image, canvas, pixels);
            InputSample lastInput = options.RecordInput
                ? MapInputToOutput(input?.Consume(first.Bounds, targetWindow) ?? EmptyInput(), first.Bounds.Size, first.Image.Size, size)
                : EmptyInput();

            long frames = 0;
            double previewTime = double.NegativeInfinity;
            Stopwatch clock = new();
            await using (FfmpegEncoder encoder = FfmpegEncoder.Start(options, size, partialVideo))
            {
                await using StreamWriter? writer = tsv is null ? null : new StreamWriter(
                    new FileStream(partialTsv, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 65536);
                if (writer is not null)
                    await writer.WriteLineAsync("timestamp\tframe\tx\ty\tclick\tkeys").ConfigureAwait(false);

                async Task WriteFrameAsync(InputSample sample)
                {
                    await encoder.WriteAsync(pixels).ConfigureAwait(false);
                    if (writer is not null)
                    {
                        string row = string.Join('\t',
                            (frames / (double)options.FramesPerSecond).ToString("F3", CultureInfo.InvariantCulture),
                            frames.ToString(CultureInfo.InvariantCulture),
                            sample.X.ToString(CultureInfo.InvariantCulture), sample.Y.ToString(CultureInfo.InvariantCulture),
                            TsvField(sample.Click), TsvField(sample.Keys));
                        await writer.WriteLineAsync(row).ConfigureAwait(false);
                    }
                    frames++;
                }

                void PublishPreview()
                {
                    if (preview is null || clock.Elapsed.TotalSeconds - previewTime < 0.05)
                        return;
                    previewTime = clock.Elapsed.TotalSeconds;
                    Bitmap copy = (Bitmap)canvas.Clone();
                    try
                    {
                        preview(copy);
                    }
                    catch
                    {
                        copy.Dispose();
                        throw;
                    }
                }

                double limit = options.MaximumDuration > TimeSpan.Zero ? options.MaximumDuration.TotalSeconds : double.PositiveInfinity;
                clock.Start();
                long startedTimestamp = Stopwatch.GetTimestamp();
                long stoppedTimestamp = long.MaxValue;
                using CancellationTokenRegistration stopRegistration = stopToken.Register(() =>
                    Interlocked.CompareExchange(ref stoppedTimestamp, Stopwatch.GetTimestamp(), long.MaxValue));
                await WriteFrameAsync(lastInput).ConfigureAwait(false);
                PublishPreview();
                while (!stopToken.IsCancellationRequested && clock.Elapsed.TotalSeconds < limit)
                {
                    double delay = Math.Min(frames / (double)options.FramesPerSecond, limit) - clock.Elapsed.TotalSeconds;
                    if (delay > 0)
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(delay), stopToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                    if (stopToken.IsCancellationRequested || clock.Elapsed.TotalSeconds >= limit)
                        break;

                    CapturedFrame captured;
                    try
                    {
                        captured = source.Capture(stopToken);
                    }
                    catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                    {
                        break;
                    }
                    using (captured)
                    {
                        double capturedTime = clock.Elapsed.TotalSeconds;
                        if (stopToken.IsCancellationRequested || capturedTime >= limit)
                            break;
                        long dueFrames = (long)Math.Floor(capturedTime * options.FramesPerSecond) + 1;
                        CheckBacklog(dueFrames - frames, options.FramesPerSecond);
                        // 遅い取得や一時的な処理落ちでは、直前画像を不足時刻へ埋める。
                        // 入力イベントは複製せず、実際に消費したフレームにだけ記録する。
                        InputSample repeated = lastInput with { Click = "None", Keys = "None" };
                        while (frames + 1 < dueFrames)
                            await WriteFrameAsync(repeated).ConfigureAwait(false);
                        CopyFrame(captured.Image, canvas, pixels);
                        lastInput = options.RecordInput
                            ? MapInputToOutput(input?.Consume(captured.Bounds, targetWindow) ?? EmptyInput(), captured.Bounds.Size, captured.Image.Size, size)
                            : EmptyInput();
                        await WriteFrameAsync(lastInput).ConfigureAwait(false);
                        PublishPreview();
                    }
                }

                double stoppedAt = Math.Min(clock.Elapsed.TotalSeconds, limit);
                long cancellationTimestamp = Volatile.Read(ref stoppedTimestamp);
                if (cancellationTimestamp != long.MaxValue)
                    stoppedAt = Math.Min(stoppedAt, Math.Max(0, Stopwatch.GetElapsedTime(startedTimestamp, cancellationTimestamp).TotalSeconds));
                clock.Stop();
                long finalFrames = Math.Max(1, (long)Math.Ceiling(stoppedAt * options.FramesPerSecond));
                CheckBacklog(finalFrames - frames, options.FramesPerSecond);
                InputSample finalRepeated = lastInput with { Click = "None", Keys = "None" };
                while (frames < finalFrames)
                    await WriteFrameAsync(finalRepeated).ConfigureAwait(false);
                if (writer is not null)
                    await writer.FlushAsync().ConfigureAwait(false);
                await encoder.CompleteAsync().ConfigureAwait(false);
            }
            if (!File.Exists(partialVideo) || new FileInfo(partialVideo).Length == 0)
                throw new IOException("FFmpeg が録画ファイルを作成しませんでした。");

            // 動画を最後に公開し、TSV の保存失敗を成功した録画として一覧へ出さない。
            if (tsv is not null)
            {
                File.Move(partialTsv, tsv, overwrite: false);
                tsvPublished = true;
            }
            File.Move(partialVideo, video, overwrite: false);
            return new RecordingResult(video, tsv, frames, frames / (double)options.FramesPerSecond);
        }
        catch
        {
            DeletePartial(partialVideo);
            DeletePartial(partialTsv);
            if (tsvPublished && tsv is not null)
                DeletePartial(tsv);
            throw;
        }
    }

    private static void Validate(RecordingOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            throw new ArgumentException("録画の保存先を指定してください。", nameof(options));
        if (string.IsNullOrWhiteSpace(options.FfmpegPath) || !File.Exists(options.FfmpegPath))
            throw new FileNotFoundException("FFmpeg の実行ファイルが見つかりません。設定から ffmpeg.exe を指定してください。", options.FfmpegPath);
        if (options.FramesPerSecond is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(options), "FPS は 1 ～ 120 で指定してください。");
        if (options.QualityCrf is < 0 or > 51)
            throw new ArgumentOutOfRangeException(nameof(options), "画質 CRF は 0 ～ 51 で指定してください。");
        if (!string.Equals(options.Container, "mp4", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Container, "mkv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("保存形式は MP4 または MKV を指定してください。", nameof(options));
        if (options.MaximumDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "最長時間を負の値にはできません。");
    }

    private static Size OutputSize(Size source)
    {
        if (source.Width < 1 || source.Height < 1 || source.Width > 16_384 || source.Height > 16_384)
            throw new ArgumentOutOfRangeException(nameof(source), "取得画像の大きさが録画できる範囲外です。");
        int width = checked((source.Width + 1) / 2 * 2);
        int height = checked((source.Height + 1) / 2 * 2);
        _ = checked(width * height * 4);
        return new Size(width, height);
    }

    private static void CopyFrame(Bitmap source, Bitmap canvas, byte[] bytes)
    {
        using (Graphics graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Black);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            if (source.Size == canvas.Size)
            {
                graphics.DrawImageUnscaled(source, 0, 0);
            }
            else
            {
                Rectangle destination = GetDestinationRectangle(source.Size, canvas.Size);
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(source, destination, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            }
        }
        BitmapData data = canvas.LockBits(new Rectangle(Point.Empty, canvas.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = canvas.Width * 4;
            for (int row = 0; row < canvas.Height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, bytes, row * rowBytes, rowBytes);
        }
        finally
        {
            canvas.UnlockBits(data);
        }
    }

    internal static Rectangle GetDestinationRectangle(Size source, Size output)
    {
        double scale = Math.Min(output.Width / (double)source.Width, output.Height / (double)source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return new Rectangle((output.Width - width) / 2, (output.Height - height) / 2, width, height);
    }

    internal static InputSample MapInputToOutput(InputSample sample, Size inputBounds, Size imageSize, Size outputSize)
    {
        if (sample.X < 0 || sample.Y < 0 || inputBounds.Width < 1 || inputBounds.Height < 1)
            return sample with { X = -1, Y = -1 };
        Rectangle destination = GetDestinationRectangle(imageSize, outputSize);
        int x = Math.Clamp((int)Math.Round(sample.X * (double)destination.Width / inputBounds.Width), 0, destination.Width - 1);
        int y = Math.Clamp((int)Math.Round(sample.Y * (double)destination.Height / inputBounds.Height), 0, destination.Height - 1);
        return sample with { X = destination.X + x, Y = destination.Y + y };
    }

    private static void CheckBacklog(long pendingFrames, int framesPerSecond)
    {
        if (pendingFrames > framesPerSecond * 5L)
            throw new InvalidOperationException("録画処理が 5 秒以上遅延したため中断しました。FPS を下げるか、録画範囲を小さくしてください。");
    }

    private static InputSample EmptyInput() => new(-1, -1, "None", "None");
    private static string TsvField(string value) => string.IsNullOrEmpty(value) ? "None" : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static void DeletePartial(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一時名のファイルは成果物として列挙しない。元の失敗理由を保持する。
        }
    }
}
