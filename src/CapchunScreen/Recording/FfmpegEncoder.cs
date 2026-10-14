using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CapchunScreen.Recording;

internal sealed class FfmpegEncoder : IAsyncDisposable
{
    private const int MaximumErrorCharacters = 16_384;
    private readonly Process _process;
    private readonly StringBuilder _errors = new();
    private readonly object _errorLock = new();
    private readonly CancellationTokenSource _readCancellation = new();
    private readonly Task _readTask;

    private FfmpegEncoder(Process process)
    {
        _process = process;
        _readTask = DrainErrorsAsync();
    }

    public static FfmpegEncoder Start(RecordingOptions options, Size size, string output)
    {
        ProcessStartInfo start = new(options.FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
            "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{size.Width}x{size.Height}",
            "-framerate", options.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "veryfast",
            "-crf", options.QualityCrf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p"
        ];
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        if (options.Container == "mp4")
        {
            start.ArgumentList.Add("-movflags");
            start.ArgumentList.Add("+faststart");
        }
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add(options.Container == "mkv" ? "matroska" : "mp4");
        start.ArgumentList.Add(output);

        Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("FFmpeg を起動できませんでした。");
            return new FfmpegEncoder(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(byte[] bytes)
    {
        if (_process.HasExited)
            throw Failure($"FFmpeg が録画中に終了しました（終了コード {_process.ExitCode}）。");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        try
        {
            // 利用者の停止はフレームの途中でパイプを切らず、次の境界で扱う。
            await _process.StandardInput.BaseStream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(ErrorText("FFmpeg への映像送信が 15 秒以内に完了しませんでした。"), ex);
        }
        catch (IOException ex)
        {
            throw Failure("FFmpeg への映像送信に失敗しました。", ex);
        }
    }

    public async Task CompleteAsync()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException ex)
        {
            throw Failure("FFmpeg の映像入力を終了できませんでした。", ex);
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(ErrorText("FFmpeg が 30 秒以内に録画ファイルを確定できませんでした。"), ex);
        }
        await FinishErrorReadAsync().ConfigureAwait(false);
        if (_process.ExitCode != 0)
            throw Failure($"FFmpeg が録画を完了できませんでした（終了コード {_process.ExitCode}）。");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 確認から Kill までの間に終了した場合。
                }
                catch (Win32Exception) when (_process.HasExited)
                {
                }
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    throw new TimeoutException("録画用 FFmpeg の強制終了を確認できませんでした。", ex);
                }
            }
        }
        finally
        {
            _readCancellation.Cancel();
            await FinishErrorReadAsync().ConfigureAwait(false);
            _readCancellation.Dispose();
            _process.Dispose();
        }
    }

    private async Task DrainErrorsAsync()
    {
        char[] buffer = new char[2048];
        try
        {
            while (true)
            {
                int count = await _process.StandardError.ReadAsync(buffer, _readCancellation.Token).ConfigureAwait(false);
                if (count == 0)
                    return;
                lock (_errorLock)
                {
                    _errors.Append(buffer, 0, count);
                    if (_errors.Length > MaximumErrorCharacters)
                        _errors.Remove(0, _errors.Length - MaximumErrorCharacters);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 終了処理中のパイプ閉鎖で stderr の読み取りも完了する。
        }
    }

    private async Task FinishErrorReadAsync()
    {
        try
        {
            await _readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _readCancellation.Cancel();
        }
    }

    private InvalidOperationException Failure(string message, Exception? inner = null) => new(ErrorText(message), inner);

    private string ErrorText(string message)
    {
        lock (_errorLock)
            return _errors.Length == 0 ? message : $"{message}{Environment.NewLine}{_errors}";
    }
}
