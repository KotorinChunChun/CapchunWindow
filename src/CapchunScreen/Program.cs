namespace CapchunScreen;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--desktop-status"))
        {
            // 最終UIテスト前の読取確認。通常起動・ミューテックス・入力フックは開始しない。
            try
            {
                var store = new Storage.SettingsStore(Option(args, "--settings-dir"));
                var guard = new Capture.DesktopAccessGuard(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "diagnostics"));
                var state = guard.Check(force: true);
                string json = System.Text.Json.JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, state.Available,
                    state.SessionId, state.SessionState, state.CurrentDesktop, state.InputDesktop, state.Operation, state.NativeError,
                    guard.DiagnosticPath, guard.DiagnosticWriteError });
                if (Option(args, "--diagnostic-output") is { } output)
                { string path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json); }
                Console.WriteLine(json); Environment.ExitCode = state.Available ? 0 : 3;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        using var singleInstance = new Mutex(true, @"Local\CapchunScreen", out var firstInstance);
        if (!firstInstance)
        {
            KotorinToys.Integration.ManagementEndpoint.ShowExisting("capchun-screen");
            return;
        }
        try
        {
            var store = new Storage.SettingsStore(Option(args, "--settings-dir"));
            var settings = store.Load(out var warning);
            var ffmpeg = Option(args, "--ffmpeg");
            if (!string.IsNullOrWhiteSpace(ffmpeg)) settings = settings with { FfmpegPath = Path.GetFullPath(ffmpeg) };
            var window = new UI.MainForm(store, settings, warning, args.Contains("--passive"));
            using var context = new KotorinToys.Integration.ManagedToolContext("capchun-screen", window,
                args.Contains("--startup"), () => window.ManagementStopBlockReason);
            Application.Run(context);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, UI.WindowTitle.Create("起動エラー"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { singleInstance.ReleaseMutex(); }
    }

    private static string? Option(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{key} の値がありません。");
        return args[index + 1];
    }
}
