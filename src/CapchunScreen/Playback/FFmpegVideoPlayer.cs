using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CapchunScreen.Playback;

internal sealed record PlaybackMediaInfo(int Width, int Height, double DurationSeconds);

internal sealed class FFmpegVideoPlayer(string ffmpegPath, string videoPath) : IAsyncDisposable
{
    private const int PlaybackFps = 30;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private DecoderProcess? _decoder;
    private byte[]? _pixels;
    private Size _size;
    private long _frameIndex = -1;
    private bool _ended;
    private bool _hasFrame;
    public PlaybackMediaInfo? Info { get; private set; }
    internal int? DecoderProcessId => _decoder?.Id;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (Info is not null) return;
            if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("FFmpeg が見つかりません。", ffmpegPath);
            if (!File.Exists(videoPath)) throw new FileNotFoundException("再生する動画が見つかりません。", videoPath);
            await using DecoderProcess probe = DecoderProcess.Start(ffmpegPath,
                ["-hide_banner", "-nostdin", "-threads", "1", "-i", videoPath, "-map", "0:v:0", "-an", "-frames:v", "1", "-f", "null", "-"]);
            await probe.WaitForSuccessAsync(linked.Token).ConfigureAwait(false);
            Info = ParseMediaInfo(probe.Errors);
            double scale = Math.Min(1, Math.Min(640d / Info.Width, 360d / Info.Height));
            _size = new(Math.Max(2, (int)(Info.Width * scale) / 2 * 2), Math.Max(2, (int)(Info.Height * scale) / 2 * 2));
            _pixels = new byte[checked(_size.Width * _size.Height * 4)];
        }
        finally { _gate.Release(); }
    }

    public async Task<Bitmap> ReadFrameAsync(double seconds, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            PlaybackMediaInfo info = Info ?? throw new InvalidOperationException("動画の準備が完了していません。");
            seconds = Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, Math.Max(0, info.DurationSeconds - 1d / PlaybackFps));
            long target = (long)Math.Floor(seconds * PlaybackFps);
            if (_decoder is null || target < _frameIndex || target - _frameIndex > PlaybackFps)
                await StartAtAsync(target, linked.Token).ConfigureAwait(false);

            while (_frameIndex < target && !_ended)
            {
                bool read = await _decoder!.ReadFrameAsync(_pixels!, linked.Token).ConfigureAwait(false);
                if (!read)
                {
                    if (!_hasFrame) throw new IOException("指定した位置に再生できるフレームがありません。");
                    _ended = true;
                    break;
                }
                _hasFrame = true;
                _frameIndex++;
            }
            return CreateBitmap(_pixels!, _size);
        }
        catch
        {
            if (_decoder is not null)
            {
                await _decoder.DisposeAsync().ConfigureAwait(false);
                _decoder = null;
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task StartAtAsync(long frameIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_decoder is not null) await _decoder.DisposeAsync().ConfigureAwait(false);
        _decoder = DecoderProcess.Start(ffmpegPath,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-filter_threads", "1",
            "-ss", (frameIndex / (double)PlaybackFps).ToString("F6", CultureInfo.InvariantCulture),
            "-i", videoPath, "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"fps={PlaybackFps},scale={_size.Width}:{_size.Height}",
            "-threads:v", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
        ]);
        _frameIndex = frameIndex - 1;
        _ended = false;
        _hasFrame = false;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_decoder is not null)
            {
                await _decoder.DisposeAsync().ConfigureAwait(false);
                _decoder = null;
            }
        }
        finally
        {
            _gate.Release();
            // UI の終了と重なった遅延要求にもキャンセル済みのトークンを返す。
        }
    }

    internal static PlaybackMediaInfo ParseMediaInfo(string text)
    {
        Match duration = Regex.Match(text, @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant);
        Match video = Regex.Match(text, @"Video:[^\r\n]*?[, ](\d{1,6})x(\d{1,6})(?:[, \[])", RegexOptions.CultureInvariant);
        if (!duration.Success || !video.Success)
            throw new IOException("動画の長さ・画像サイズを取得できませんでした。ファイル形式または破損を確認してください。");
        double seconds = double.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
            + double.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture) * 60
            + double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture);
        int width = int.Parse(video.Groups[1].Value, CultureInfo.InvariantCulture);
        int height = int.Parse(video.Groups[2].Value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(seconds) || seconds <= 0 || width is < 1 or > 16_384 || height is < 1 or > 16_384)
            throw new IOException("再生できる動画の長さ・画像サイズではありません。");
        return new(width, height, seconds);
    }

    private static Bitmap CreateBitmap(byte[] pixels, Size size)
    {
        Bitmap image = new(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            BitmapData data = image.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowSize = size.Width * 4;
                for (int y = 0; y < size.Height; y++)
                    Marshal.Copy(pixels, y * rowSize, data.Scan0 + y * data.Stride, rowSize);
            }
            finally { image.UnlockBits(data); }
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private sealed class DecoderProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _errors = new();
        private readonly CancellationTokenSource _readCancellation = new();
        private readonly Task _readErrors;
        private bool _disposed;
        private DecoderProcess(Process process)
        {
            _process = process;
            _readErrors = ReadErrorsAsync();
        }
        public string Errors { get { lock (_errors) return _errors.ToString(); } }
        public int Id => _process.Id;

        public static DecoderProcess Start(string executable, string[] arguments)
        {
            ProcessStartInfo start = new(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            Process process = new() { StartInfo = start };
            try
            {
                if (!process.Start()) throw new IOException("動画のデコーダーを起動できませんでした。");
                return new(process);
            }
            catch { process.Dispose(); throw; }
        }

        public async Task<bool> ReadFrameAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            int offset = 0;
            try
            {
                while (offset < buffer.Length)
                {
                    int count = await _process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(offset), timeout.Token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        await WaitForSuccessAsync(timeout.Token).ConfigureAwait(false);
                        if (offset != 0) throw new IOException("動画のフレームが途中で途切れました。");
                        return false;
                    }
                    offset += count;
                }
                return true;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("動画フレームの取得が 15 秒以内に完了しませんでした。", ex);
            }
        }

        public async Task WaitForSuccessAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("動画情報の取得が 15 秒以内に完了しませんでした。", ex); }
            await _readErrors.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            if (_process.ExitCode != 0)
                throw new IOException($"動画を読み込めませんでした（終了コード {_process.ExitCode}）。{Environment.NewLine}{Errors}");
        }

        private async Task ReadErrorsAsync()
        {
            char[] buffer = new char[2048];
            try
            {
                while (true)
                {
                    int count = await _process.StandardError.ReadAsync(buffer, _readCancellation.Token).ConfigureAwait(false);
                    if (count == 0) return;
                    lock (_errors)
                    {
                        _errors.Append(buffer, 0, count);
                        if (_errors.Length > 32_768) _errors.Remove(0, _errors.Length - 32_768);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    try { _process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) when (_process.HasExited) { }
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            finally
            {
                _readCancellation.Cancel();
                try { await _readErrors.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                _process.Dispose();
                _readCancellation.Dispose();
            }
        }
    }
}
