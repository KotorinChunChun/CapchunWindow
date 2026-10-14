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
            MessageBox.Show("CapchunScreenは既に起動しています。開いている画面を使用してください。", UI.WindowTitle.Create(), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var store = new Storage.SettingsStore(Option(args, "--settings-dir"));
            var settings = store.Load(out var warning);
            var ffmpeg = Option(args, "--ffmpeg");
            if (!string.IsNullOrWhiteSpace(ffmpeg)) settings = settings with { FfmpegPath = Path.GetFullPath(ffmpeg) };
            Application.Run(new UI.MainForm(store, settings, warning));
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
