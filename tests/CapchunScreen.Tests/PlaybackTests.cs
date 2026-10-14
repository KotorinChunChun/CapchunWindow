using System.Diagnostics;
using System.Collections;
using System.Reflection;
using CapchunScreen.Playback;
using CapchunScreen.UI;

namespace CapchunScreen.Tests;

internal static class PlaybackTests
{
    internal static void Run(Action<bool, string> check)
    {
        double now = 10;
        PlaybackTimeline timeline = new(() => now);
        timeline.Play(); now += 2.5;
        check(Math.Abs(timeline.Position - 2.5) < .001, "再生時刻を描画回数ではなく単調時計から計算");
        timeline.Pause(); now += 3;
        check(Math.Abs(timeline.Position - 2.5) < .001, "一時停止中は動画時刻を維持");
        timeline.Seek(8); timeline.Play(); now += 1;
        check(Math.Abs(timeline.Position - 9) < .001, "シーク位置から再生時刻を再開");
        PlaybackMediaInfo media = FFmpegVideoPlayer.ParseMediaInfo("Duration: 00:01:02.50, start: 0.00\n Stream #0:0: Video: h264 (High), yuv420p(progressive), 1920x1080 [SAR 1:1 DAR 16:9], 30 fps");
        check(media.Width == 1920 && media.Height == 1080 && media.DurationSeconds == 62.5, "FFmpeg 出力から動画情報を取得");
        bool malformedRejected = false;
        try { FFmpegVideoPlayer.ParseMediaInfo("Duration: N/A"); }
        catch (IOException) { malformedRejected = true; }
        check(malformedRejected, "情報を取得できない動画を明示的に拒否");
        check(MultiPlaybackForm.GetGridSize(6) == new Size(3, 2) && MultiPlaybackForm.GetGridSize(16) == new Size(4, 4), "動画本数に応じた最大 16 枠のグリッド");
        using MultiPlaybackForm form = new("検証では起動しない.exe", Enumerable.Range(0, 16).Select(index => $"動画{index}.mp4").ToArray());
        form.Size = form.MinimumSize;
        form.CreateControl();
        _ = form.Handle;
        for (int pass = 0; pass < 3; pass++) Layout(form);
        Button[] buttons = Descendants(form).OfType<Button>().ToArray();
        string[] invalidButtons = buttons.Where(button => button.Width < 25 || button.Height < 20
            || !button.Parent!.ClientRectangle.Contains(button.Bounds))
            .Select(button => $"{button.Name}/{button.Text}={button.Bounds} 親={button.Parent!.ClientRectangle}").ToArray();
        string detail = invalidButtons.Length == 0 ? "" : $"：{string.Join(" / ", invalidButtons)}";
        check(buttons.Length == 19 && invalidButtons.Length == 0, $"16 本再生画面の最小サイズでも全操作ボタンを配置（ボタン数 {buttons.Length}）{detail}");
        check(!form.Visible, "複数再生の構造検査は画面を表示・動画を起動しない");
        CheckIndividualOperations(form, check);
    }

    internal static async Task RunAsync(string ffmpegPath, string outputDirectory, Action<bool, string> check)
    {
        string directory = Path.Combine(outputDirectory, $"playback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string redPath = Path.Combine(directory, "赤い短い動画.mp4");
        string bluePath = Path.Combine(directory, "青い縦動画.mkv");
        await Program.ProcessOutput(ffmpegPath,
            ["-hide_banner", "-loglevel", "error", "-n", "-f", "lavfi", "-i", "color=c=red:s=160x90:r=30:d=0.3",
                "-f", "lavfi", "-i", "color=c=blue:s=160x90:r=30:d=0.3", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]",
                "-map", "[v]", "-c:v", "libx264", "-pix_fmt", "yuv420p", redPath]);
        foreach ((string path, string color, string size, string duration) in new[]
                 { (bluePath, "blue", "120x160", "1.2") })
            await Program.ProcessOutput(ffmpegPath,
                ["-hide_banner", "-loglevel", "error", "-n", "-f", "lavfi", "-i", $"color=c={color}:s={size}:r=30", "-t", duration, "-c:v", "libx264", "-pix_fmt", "yuv420p", path]);

        await using FFmpegVideoPlayer red = new(ffmpegPath, redPath);
        await using FFmpegVideoPlayer blue = new(ffmpegPath, bluePath);
        await Task.WhenAll(red.InitializeAsync(CancellationToken.None), blue.InitializeAsync(CancellationToken.None));
        check(red.Info!.DurationSeconds >= .59 && blue.Info!.DurationSeconds >= 1.19, "長さの違う MP4/MKV を個別に読み込む");
        Bitmap[] first = await Task.WhenAll(red.ReadFrameAsync(0, CancellationToken.None), blue.ReadFrameAsync(0, CancellationToken.None));
        using (first[0])
        using (first[1])
        {
            Color redPixel = first[0].GetPixel(first[0].Width / 2, first[0].Height / 2);
            Color bluePixel = first[1].GetPixel(first[1].Width / 2, first[1].Height / 2);
            check(redPixel.R > 200 && redPixel.B < 50 && bluePixel.B > 200 && bluePixel.R < 50, "複数 FFmpeg から別動画の BGRA フレームを同時取得");
        }
        using (Bitmap forward = await red.ReadFrameAsync(.4, CancellationToken.None))
        using (Bitmap backward = await red.ReadFrameAsync(.1, CancellationToken.None))
        {
            Color forwardPixel = forward.GetPixel(forward.Width / 2, forward.Height / 2);
            Color backwardPixel = backward.GetPixel(backward.Width / 2, backward.Height / 2);
            check(forwardPixel.B > 200 && forwardPixel.R < 50 && backwardPixel.R > 200 && backwardPixel.B < 50,
                "前進シークでは後半の青、後退シークでは前半の赤を実画像で確認");
        }
        using (Bitmap last = await red.ReadFrameAsync(10, CancellationToken.None))
            check(last.Width > 0, "短い動画の終端を超える共通時刻では最終画像を維持");

        string brokenPath = Path.Combine(directory, "破損.mp4");
        File.WriteAllText(brokenPath, "This is not a video.");
        await using FFmpegVideoPlayer broken = new(ffmpegPath, brokenPath);
        bool brokenRejected = false;
        try { await broken.InitializeAsync(CancellationToken.None); }
        catch (IOException) { brokenRejected = true; }
        using (Bitmap continuing = await blue.ReadFrameAsync(.5, CancellationToken.None))
            check(brokenRejected && continuing.Height == 160, "壊れた 1 本の失敗がほかの動画再生へ波及しない");

        int? redProcess = red.DecoderProcessId;
        int? blueProcess = blue.DecoderProcessId;
        await Task.WhenAll(red.DisposeAsync().AsTask(), blue.DisposeAsync().AsTask());
        check(redProcess.HasValue && blueProcess.HasValue && HasExited(redProcess.Value) && HasExited(blueProcess.Value), "再生終了で自分の FFmpeg プロセスを回収");

        FFmpegVideoPlayer[] sixteen = Enumerable.Range(0, 16).Select(_ => new FFmpegVideoPlayer(ffmpegPath, redPath)).ToArray();
        int[] processIds = [];
        try
        {
            await Task.WhenAll(sixteen.Select(player => player.InitializeAsync(CancellationToken.None)));
            bool[] decoded = await Task.WhenAll(sixteen.Select(async player =>
            {
                using Bitmap image = await player.ReadFrameAsync(0, CancellationToken.None);
                Color pixel = image.GetPixel(image.Width / 2, image.Height / 2);
                return pixel.R > 200 && pixel.B < 50;
            }));
            processIds = sixteen.Select(player => player.DecoderProcessId!.Value).ToArray();
            check(decoded.All(value => value) && processIds.Distinct().Count() == 16, "最大16個の独立デコーダーで最初の実フレームを同時取得");
        }
        finally { await Task.WhenAll(sixteen.Select(player => player.DisposeAsync().AsTask())); }
        check(processIds.Length == 16 && processIds.All(HasExited), "最大16個の再生デコーダーをすべて回収");
    }

    private static bool HasExited(int processId)
    {
        try { using Process process = Process.GetProcessById(processId); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static void CheckIndividualOperations(MultiPlaybackForm form, Action<bool, string> check)
    {
        const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        object tile = ((IEnumerable)typeof(MultiPlaybackForm).GetField("_tiles", privateInstance)!.GetValue(form)!).Cast<object>().First();
        Type tileType = tile.GetType();
        FFmpegVideoPlayer player = (FFmpegVideoPlayer)tileType.GetProperty("Player")!.GetValue(tile)!;
        // 動画プロセスを起動せず、UI の再生状態だけを既知の長さで検査する。
        typeof(FFmpegVideoPlayer).GetProperty("Info")!.SetValue(player, new PlaybackMediaInfo(160, 90, 10));
        tileType.GetProperty("Ready")!.SetValue(tile, true);
        PlaybackTimeline global = (PlaybackTimeline)typeof(MultiPlaybackForm).GetField("_global", privateInstance)!.GetValue(form)!;
        PlaybackTimeline clock = (PlaybackTimeline)tileType.GetProperty("Clock")!.GetValue(tile)!;
        MethodInfo toggle = typeof(MultiPlaybackForm).GetMethod("ToggleIndividual", privateInstance)!;
        global.Seek(2); global.Play();
        toggle.Invoke(form, [tile]);
        check(!global.IsPlaying && !clock.IsPlaying, "全体再生中の個別一時停止で勝手に再開しない");
        toggle.Invoke(form, [tile]);
        check(!global.IsPlaying && clock.IsPlaying, "個別一時停止の後は同じ操作で個別再生を開始");
        global.Seek(2); global.Play();
        TrackBar seek = (TrackBar)tileType.GetProperty("Seek")!.GetValue(tile)!;
        seek.Value = 7000;
        typeof(MultiPlaybackForm).GetMethod("SeekIndividual", privateInstance)!.Invoke(form, [tile]);
        check(!global.IsPlaying && Math.Abs(clock.Position - 7) < .001, "全体再生中の個別シークは要求した位置を保持");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Layout(Control root)
    {
        root.CreateControl();
        _ = root.Handle;
        root.PerformLayout();
        foreach (Control child in root.Controls) Layout(child);
        root.PerformLayout();
    }
}
