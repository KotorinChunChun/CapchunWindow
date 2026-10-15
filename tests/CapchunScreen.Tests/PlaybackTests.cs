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
        check(buttons.Length == 20 && invalidButtons.Length == 0, $"16 本再生画面の最小サイズでも全操作ボタンを配置（ボタン数 {buttons.Length}）{detail}");
        check(!form.Visible, "複数再生の構造検査は画面を表示・動画を起動しない");
        CheckIndividualOperations(form, check);
        CheckKeyboardOperations(form, check);
        using var single = new MultiPlaybackForm("検証では起動しない.exe", ["単体.mp4"]);
        check(single.Text == WindowTitle.Create("動画の再生"), "単体再生も同じ内蔵プレイヤーとキー操作を使う");
        double speedNow = 0;
        var speedClock = new PlaybackTimeline(() => speedNow);
        speedClock.Seek(10); speedClock.Play(); speedNow += 2;
        speedClock.Rate = 2; speedNow += 3;
        check(Math.Abs(speedClock.Position - 18) < .001, "再生速度変更時に位置を保ち、その後だけ新しい速度を適用する");
        speedClock.Pause(); speedNow += 4;
        check(Math.Abs(speedClock.Position - 18) < .001, "速度変更後も一時停止中は時刻を維持する");
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
        object[] tiles = ((IEnumerable)typeof(MultiPlaybackForm).GetField("_tiles", privateInstance)!.GetValue(form)!).Cast<object>().Take(2).ToArray();
        object tile = tiles[0];
        Type tileType = tile.GetType();
        FFmpegVideoPlayer player = (FFmpegVideoPlayer)tileType.GetProperty("Player")!.GetValue(tile)!;
        // 動画プロセスを起動せず、UI の再生状態だけを既知の長さで検査する。
        foreach (object item in tiles)
        {
            typeof(FFmpegVideoPlayer).GetProperty("Info")!.SetValue(tileType.GetProperty("Player")!.GetValue(item), new PlaybackMediaInfo(160, 90, 100));
            tileType.GetProperty("Ready")!.SetValue(item, true);
        }
        PlaybackTimeline clock = (PlaybackTimeline)tileType.GetProperty("Clock")!.GetValue(tile)!;
        PlaybackTimeline second = (PlaybackTimeline)tileType.GetProperty("Clock")!.GetValue(tiles[1])!;
        MethodInfo toggle = typeof(MultiPlaybackForm).GetMethod("ToggleIndividual", privateInstance)!;
        MethodInfo playAll = typeof(MultiPlaybackForm).GetMethod("PlayAll", privateInstance)!;
        MethodInfo pauseAll = typeof(MultiPlaybackForm).GetMethod("PauseAll", privateInstance)!;
        clock.Seek(2); second.Seek(7);
        playAll.Invoke(form, null);
        check(clock.IsPlaying && second.IsPlaying && clock.Position >= 2 && clock.Position < 2.1 && second.Position >= 7 && second.Position < 7.1,
            "すべて再生は個別に設定した2秒・7秒から始め、開始位置を上書きしない");
        playAll.Invoke(form, null);
        check(Math.Abs(second.Position - clock.Position - 5) < .05, "再生中にすべて再生を押しても5秒の位置差を維持する");
        pauseAll.Invoke(form, null);
        check(!clock.IsPlaying && !second.IsPlaying && Math.Abs(second.Position - clock.Position - 5) < .05,
            "全体の一時停止も各動画の位置差を維持する");
        playAll.Invoke(form, null);
        toggle.Invoke(form, [tile]);
        check(!clock.IsPlaying && !second.IsPlaying, "全体再生中の個別一時停止で勝手に再開しない");
        toggle.Invoke(form, [tile]);
        check(clock.IsPlaying && !second.IsPlaying, "個別一時停止の後は同じ操作で個別再生を開始");
        playAll.Invoke(form, null);
        TrackBar seek = (TrackBar)tileType.GetProperty("Seek")!.GetValue(tile)!;
        seek.Value = 7000;
        typeof(MultiPlaybackForm).GetMethod("SeekIndividual", privateInstance)!.Invoke(form, [tile]);
        check(!clock.IsPlaying && Math.Abs(clock.Position - 70) < .001, "全体再生中の個別シークは要求した位置を保持");
        clock.Seek(100); second.Seek(100);
        playAll.Invoke(form, null);
        check(!clock.IsPlaying && !second.IsPlaying && clock.Position == 100 && second.Position == 100,
            "終端でのすべて再生は勝手に先頭へ戻さない");
        typeof(MultiPlaybackForm).GetMethod("SeekAll", privateInstance)!.Invoke(form, [0d]);
        check(clock.Position == 0 && second.Position == 0, "先頭への明示操作で両方を0秒へ戻す");
    }

    private static void CheckKeyboardOperations(MultiPlaybackForm form, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var tiles = ((IEnumerable)typeof(MultiPlaybackForm).GetField("_tiles", flags)!.GetValue(form)!).Cast<object>().Take(2).ToArray();
        Type tileType = tiles[0].GetType();
        var clocks = tiles.Select(tile => (PlaybackTimeline)tileType.GetProperty("Clock")!.GetValue(tile)!).ToArray();
        MethodInfo handle = typeof(MultiPlaybackForm).GetMethod("HandlePlaybackKey", flags)!;
        bool Key(Keys keys, object? tile = null) => (bool)handle.Invoke(form, [keys, tile])!;
        clocks[0].Seek(20); clocks[1].Seek(40);
        Key(Keys.L);
        check(clocks[0].Position == 30 && clocks[1].Position == 50, "Lは全体を10秒進めて位置差を維持する");
        Key(Keys.Left);
        check(clocks[0].Position == 25 && clocks[1].Position == 45, "左矢印は全体を5秒戻して位置差を維持する");
        Key(Keys.K); Key(Keys.Space);
        check(!clocks[0].IsPlaying && !clocks[1].IsPlaying && Math.Abs(clocks[1].Position - clocks[0].Position - 20) < .05,
            "Kで全体再生、Spaceで一時停止しても位置差が残る");
        Key(Keys.D7, tiles[0]);
        check(clocks[0].Position == 70 && clocks[1].Position < 46, "動画枠の7はその動画だけ70%へ移動する");
        Key(Keys.J, tiles[0]); Key(Keys.Right, tiles[0]);
        check(clocks[0].Position == 65, "動画枠のJと右矢印でその動画だけ10秒戻し・5秒送りする");
        Key(Keys.OemPeriod, tiles[0]);
        check(Math.Abs(clocks[0].Position - (65 + 1d / 30)) < .00001, "一時停止中のピリオドで次の30fps表示フレームへ進む");
        Key(Keys.Oemcomma, tiles[0]);
        check(Math.Abs(clocks[0].Position - 65) < .00001, "一時停止中のカンマで前の表示フレームへ戻る");
        Key(Keys.K, tiles[0]);
        double beforeStep = clocks[0].Position;
        Key(Keys.OemPeriod, tiles[0]);
        check(clocks[0].IsPlaying && clocks[0].Position - beforeStep < .02, "再生中のコマ送りは時刻を飛ばさない");
        Key(Keys.K, tiles[0]);
        Key(Keys.Shift | Keys.OemPeriod, tiles[0]);
        check(clocks[0].Rate == 1.25 && clocks[1].Rate == 1, "Shift+ピリオドで選択動画だけ再生速度を上げる");
        Key(Keys.Home, tiles[0]); Key(Keys.J, tiles[0]);
        check(clocks[0].Position == 0, "先頭からの巻戻しを0秒で止める");
        Key(Keys.End, tiles[0]); Key(Keys.L, tiles[0]);
        check(clocks[0].Position == 100, "終端からの早送りを動画の長さで止める");
        Key(Keys.NumPad5);
        check(clocks.All(clock => clock.Position == 50), "全体のテンキー5は全動画を共通の50%秒位置へ移す");
        check(!Key(Keys.Control | Keys.Left) && !Key(Keys.Shift | Keys.D5), "修飾キー付きの別操作を再生ショートカットとして奪わない");
        Rectangle original = form.Bounds;
        Key(Keys.F);
        bool fullScreen = form.FormBorderStyle == FormBorderStyle.None;
        Key(Keys.Escape);
        check(fullScreen && form.FormBorderStyle == FormBorderStyle.Sizable && form.Bounds == original,
            "Fで全画面、Escで元の位置と寸法へ戻す");
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
