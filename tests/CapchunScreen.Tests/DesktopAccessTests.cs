using System.ComponentModel;
using System.Text.Json;
using CapchunScreen.Capture;
using CapchunScreen.Input;
using CapchunScreen.Recording;
using CapchunScreen.Storage;
using CapchunScreen.UI;

namespace CapchunScreen.Tests;

internal static class DesktopAccessTests
{
    internal static void Run(Action<bool, string> check, string root)
    {
        string directory = Path.Combine(root, "desktop-access");
        var state = new DesktopAccessState(true, 7, "Default", "Default", "ready", 0, "Active");
        var guard = new DesktopAccessGuard(directory, () => state);
        check(guard.DiagnosticPath is null && !Directory.Exists(directory), "アクセス確認器の生成だけでは診断ファイルを作らない");
        guard.RequireAccess(); string path = guard.DiagnosticPath!;
        state = state with { Available = false, InputDesktop = "", Operation = "OpenInputDesktop", NativeError = 5 };
        guard.Check(force: true);
        bool blocked = false;
        try { guard.RequireAccess(); } catch (DesktopAccessException error) { blocked = error.DiagnosticPath == path; }
        guard.Check(force: true);
        check(blocked && File.ReadAllLines(path).Length == 2, "アクセス拒否を検知し同じ状態のログを連続出力しない");
        using var json = JsonDocument.Parse(File.ReadAllLines(path)[1]); var row = json.RootElement;
        check(row.GetProperty("NativeError").GetInt32() == 5 && row.GetProperty("SessionId").GetInt32() == 7
            && row.GetProperty("SessionState").GetString() == "Active" && row.TryGetProperty("timestamp", out _),
            "診断ログに時刻・セッション状態・デスクトップ名・ネイティブエラーを残す");
        check(!row.TryGetProperty("keys", out _) && !row.TryGetProperty("title", out _) && !row.TryGetProperty("position", out _),
            "診断ログへ入力内容・ウィンドウ名・カーソル座標を保存しない");
        using var source = new CountingSource();
        using (var protectedSource = new DesktopGuardedFrameSource(source, guard))
        {
            try { using var ignored = protectedSource.Capture(CancellationToken.None); } catch (DesktopAccessException) { }
            check(source.Captures == 0, "アクセス拒否中は映像の取得器へ処理を渡さない");
            state = state with { Available = true, InputDesktop = "Default", Operation = "ready", NativeError = 0 };
            guard.Check(force: true);
            using var frame = protectedSource.Capture(CancellationToken.None);
            check(source.Captures == 1 && File.ReadAllLines(path).Any(line => line.Contains("recovered")), "復旧を記録してから明示的に取得を再開できる");
        }
        check(source.Disposed, "アクセス中断後も取得器を解放する");
        check(DesktopAccessGuard.IsAccessDenied(new IOException("外側", new Win32Exception(5)))
            && !DesktopAccessGuard.IsAccessDenied(new Win32Exception(6)), "例外の内側のアクセス拒否と別のエラーを区別する");
        string invalidDirectory = Path.Combine(root, "desktop-diagnostic-file"); File.WriteAllText(invalidDirectory, "検証");
        var noLog = new DesktopAccessGuard(invalidDirectory, () => state with { Available = false });
        blocked = false; try { noLog.RequireAccess(); } catch (DesktopAccessException) { blocked = true; }
        check(blocked && noLog.DiagnosticWriteError is not null, "診断ログを書けなくてもアクセス拒否を握りつぶさない");
    }

    internal static void RunUi(Action<bool, string> check, string root)
    {
        var state = new DesktopAccessState(false, 7, "Default", "", "session-inactive", 0, "Disconnected");
        var guard = new DesktopAccessGuard(Path.Combine(root, "desktop-ui"), () => state);
        int captures = 0;
        using var form = new MainForm(new SettingsStore(Path.Combine(root, "desktop-ui-settings")), new AppSettings(), null,
            passive: true, frameSourceFactory: (_, _) => { captures++; return new CountingSource(); }, desktopAccess: guard);
        form.CheckDesktopAsync().GetAwaiter().GetResult();
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var label = (Label)typeof(MainForm).GetField("status", hidden)!.GetValue(form)!;
        check(label.Text.Contains("アクセスが中断") && captures == 0, "切断状態では本体に中断理由を表示して取得を開始しない");
        state = state with { Available = true, InputDesktop = "Default", Operation = "ready", SessionState = "Active" };
        form.CheckDesktopAsync().GetAwaiter().GetResult();
        check(label.Text.Contains("復旧") && label.Text.Contains("自動再開しません") && captures == 0
            && typeof(MainForm).GetField("recordingCancellation", hidden)!.GetValue(form) is null,
            "復旧しても録画・プレビューを自動再開せず明示操作を案内する");
    }

    internal static async Task RunMediaAsync(string ffmpeg, string root, Action<bool, string> check)
    {
        var options = new RecordingOptions { OutputDirectory = Path.Combine(root, "中断保存"), FfmpegPath = ffmpeg, FramesPerSecond = 15,
            MaximumDuration = TimeSpan.FromSeconds(1), RecordInput = true };
        using var source = new CountingSource { FailAt = 4 };
        var result = await new RecordingSession().RunAsync(source, new EmptyInput(), 0, options, CancellationToken.None);
        check(result.InterruptionReason is not null && Path.GetFileName(result.VideoPath).Contains("_中断.") && result.Frames >= 3,
            "録画途中のアクセス拒否では保存済み部分を中断名で確定する");
        check(File.ReadAllLines(result.TsvPath!).LongLength == result.Frames + 1, "中断保存でも映像とTSVのフレーム数を一致させる");
        await Program.ProcessOutput(ffmpeg, ["-v", "error", "-i", result.VideoPath, "-f", "null", "-"]);
        check(true, "アクセス拒否で中断した動画を末尾まで実デコードできる");
        using var inputFailure = new CountingSource();
        var inputResult = await new RecordingSession().RunAsync(inputFailure, new FailingInput(), 0, options, CancellationToken.None);
        check(inputResult.InterruptionReason is not null && File.ReadAllLines(inputResult.TsvPath!).LongLength == inputResult.Frames + 1,
            "入力取得のアクセス拒否でも不完全なTSV行を追加せず中断保存する");
        using var beforeStart = new CountingSource { FailAt = 1 };
        int existing = Directory.GetFiles(options.OutputDirectory).Length; bool rejected = false;
        try { await new RecordingSession().RunAsync(beforeStart, new EmptyInput(), 0, options, CancellationToken.None); }
        catch (DesktopAccessException) { rejected = true; }
        check(rejected && Directory.GetFiles(options.OutputDirectory).Length == existing, "1枚も取得できないアクセス拒否を録画成功や空の動画にしない");
    }

    private sealed class CountingSource : IFrameSource
    {
        internal int Captures; internal int FailAt = int.MaxValue; internal bool Disposed;
        public CapturedFrame Capture(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (++Captures >= FailAt) throw new DesktopAccessException("検証用のアクセス拒否");
            return new(new Bitmap(80, 60), new Rectangle(0, 0, 80, 60));
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class EmptyInput : IInputSource
    { public InputSample Consume(Rectangle bounds, nint target) => new(-1, -1, "None", "None"); }
    private sealed class FailingInput : IInputSource
    {
        private int calls;
        public InputSample Consume(Rectangle bounds, nint target)
        { if (++calls >= 3) throw new DesktopAccessException("検証用の入力拒否"); return new(1, 1, "None", "A"); }
    }
}
