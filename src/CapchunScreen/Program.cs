namespace CapchunScreen;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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
