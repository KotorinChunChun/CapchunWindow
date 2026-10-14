using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CapchunScreen.Capture;
using CapchunScreen.Input;
using CapchunScreen.Recording;

namespace CapchunScreen.Tests;

internal static class Program
{
    private static int passed;
    private static int failed;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            InputTests.Run(Check);
            var mapped = RecordingSession.MapInputToOutput(new InputSample(20, 30, "L-Click", "Ctrl,C"), new Size(100, 100), new Size(100, 100), new Size(200, 100));
            Check(mapped.X == 70 && mapped.Y == 30, "リサイズ時のTSV座標を余白と縮尺へ合わせる");
            var ffmpeg = Argument(args, "--ffmpeg");
            if (ffmpeg is not null)
            {
                var output = Argument(args, "--output") ?? throw new ArgumentException("--outputが必要です。");
                Directory.CreateDirectory(output);
                MediaTests(ffmpeg, Argument(args, "--ffprobe"), output).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) { failed++; Console.WriteLine($"FAIL 未処理例外: {ex}"); }
        Console.WriteLine($"結果: 成功 {passed} / 失敗 {failed}");
        return failed == 0 ? 0 : 1;
    }

    internal static void Check(bool condition, string name)
    {
        if (condition) passed++; else failed++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    }

    private static string? Argument(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static async Task MediaTests(string ffmpeg, string? ffprobe, string root)
    {
        var options = new RecordingOptions
        {
            OutputDirectory = Path.Combine(root, "日本語の録画先"), FfmpegPath = ffmpeg,
            FramesPerSecond = 15, MaximumDuration = TimeSpan.FromSeconds(1.2), RecordInput = true
        };
        using var synthetic = new SyntheticSource();
        var result = await new RecordingSession().RunAsync(synthetic, new SyntheticInput(), 0, options, CancellationToken.None);
        Check(File.Exists(result.VideoPath) && new FileInfo(result.VideoPath).Length > 0, "日本語パスで実MP4を生成");
        Check(result.TsvPath is not null && File.Exists(result.TsvPath), "動画と同名の操作TSVを生成");
        var lines = File.ReadAllLines(result.TsvPath!);
        Check(lines[0] == "timestamp\tframe\tx\ty\tclick\tkeys", "Python互換TSVヘッダー");
        Check(lines.Length == result.Frames + 1, "映像フレームとTSV行数の一致");
        Check(result.Frames >= 18 && result.Frames <= 20, "最大録画時間で自動停止");
        Check(lines.Skip(1).Select((line, i) => line.Split('\t')[1] == i.ToString(CultureInfo.InvariantCulture)).All(x => x), "0開始の連続フレーム番号");
        Check(lines.Skip(1).All(line => line.Split('\t').Length == 6), "TSVは常に6列");
        await Decode(ffmpeg, result.VideoPath);
        Check(true, "MP4の全フレームを実デコード");
        if (ffprobe is not null) await Probe(ffprobe, result.VideoPath, result.Frames, 15);

        using var slow = new SyntheticSource(110);
        var slowResult = await new RecordingSession().RunAsync(slow, null, 0, options with
        { Container = "mkv", RecordInput = false, MaximumDuration = TimeSpan.FromSeconds(1) }, CancellationToken.None);
        Check(slowResult.TsvPath is null, "操作記録OFFではTSVを作らない");
        Check(slowResult.DurationSeconds >= .9 && slowResult.DurationSeconds <= 1.2, "遅い取得でも映像時間を保持");
        await Decode(ffmpeg, slowResult.VideoPath);
        Check(true, "MKVを実デコード");

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        using var source = new SyntheticSource();
        var stopped = await new RecordingSession().RunAsync(source, null, 0, options with
        { MaximumDuration = TimeSpan.Zero, RecordInput = false }, stop.Token);
        Check(File.Exists(stopped.VideoPath) && stopped.Frames > 0, "手動停止相当のcancelで正常保存");
        await Decode(ffmpeg, stopped.VideoPath);
        using var broken = new FailingSource();
        var failure = false;
        try { await new RecordingSession().RunAsync(broken, new SyntheticInput(), 0, options, CancellationToken.None); }
        catch (InvalidOperationException) { failure = true; }
        Check(failure, "取得中のエラーを呼出側へ通知");
        Check(Directory.GetFiles(options.OutputDirectory).Count(p => p.EndsWith(".mp4") && !Path.GetFileName(p).StartsWith('.')) == 2,
            "失敗を完成MP4として一覧に公開しない");

        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var canceledBeforeStart = false;
        try { await new RecordingSession().RunAsync(source, null, 0, options, canceled.Token); }
        catch (OperationCanceledException) { canceledBeforeStart = true; }
        Check(canceledBeforeStart, "開始前の中止を録画成功にしない");
        var missingInputRejected = false;
        try { await new RecordingSession().RunAsync(source, null, 0, options, CancellationToken.None); }
        catch (ArgumentException) { missingInputRejected = true; }
        Check(missingInputRejected, "入力記録を指定したのに入力ソースがなければ拒否");
        foreach (var invalid in new[] { options with { FramesPerSecond = 0 }, options with { QualityCrf = 52 }, options with { Container = "gif" }, options with { MaximumDuration = TimeSpan.FromSeconds(-1) } })
        {
            bool rejected = false;
            try { await new RecordingSession().RunAsync(source, new SyntheticInput(), 0, invalid, CancellationToken.None); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "範囲外の録画設定を開始前に拒否");
        }
    }

    private static async Task Decode(string executable, string path)
        => _ = await ProcessOutput(executable, ["-hide_banner", "-v", "error", "-i", path, "-f", "null", "-"]);

    private static async Task Probe(string executable, string path, long frames, int fps)
    {
        var text = await ProcessOutput(executable, ["-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=width,height,nb_read_frames,r_frame_rate,codec_name:format=duration", "-of", "json", path]);
        using var doc = JsonDocument.Parse(text);
        var stream = doc.RootElement.GetProperty("streams")[0];
        Check(stream.GetProperty("width").GetInt32() % 2 == 0 && stream.GetProperty("height").GetInt32() % 2 == 0, "奇数の取得寸法を偶数で出力");
        Check(stream.GetProperty("codec_name").GetString() == "h264", "H.264出力");
        Check(long.Parse(stream.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture) == frames, "ffprobeによる実フレーム数一致");
        Check(stream.GetProperty("r_frame_rate").GetString() == $"{fps}/1", "指定FPSを動画に反映");
    }

    internal static async Task<string> ProcessOutput(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("検証プロセスを開始できません。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new IOException(await error);
        return await output;
    }

    private sealed class SyntheticSource(int delay = 0) : IFrameSource
    {
        private int frame;
        public CapturedFrame Capture(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delay > 0) Thread.Sleep(delay);
            var image = new Bitmap(321, 241);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Navy);
            graphics.FillRectangle(Brushes.Lime, frame++ * 4 % 270, 50, 40, 70);
            return new(image, new Rectangle(-100, 40, 321, 241));
        }
        public void Dispose() { }
    }
    private sealed class FailingSource : IFrameSource
    {
        private readonly SyntheticSource source = new();
        private int count;
        public CapturedFrame Capture(CancellationToken cancellationToken)
            => count++ < 3 ? source.Capture(cancellationToken) : throw new InvalidOperationException("意図的な取得失敗");
        public void Dispose() => source.Dispose();
    }
    private sealed class SyntheticInput : IInputSource
    {
        public InputSample Consume(Rectangle bounds, nint targetWindow) => new(10, 20, "L-Click", "Ctrl,C");
    }
}
