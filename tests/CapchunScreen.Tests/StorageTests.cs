using System.Text.Json;
using CapchunScreen.Storage;

namespace CapchunScreen.Tests;

internal static class StorageTests
{
    internal static void Run(Action<bool, string> check, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string root = Path.Combine(Path.GetFullPath(outputDirectory), $"storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        SettingsTests(Path.Combine(root, "設定"), check);
        FileTests(Path.Combine(root, "録画"), check);
    }

    private static void SettingsTests(string directory, Action<bool, string> check)
    {
        SettingsStore store = new(directory);
        AppSettings defaults = store.Load(out string? warning);
        check(warning is null && defaults.FramesPerSecond == 30 && defaults.RecordInput && !File.Exists(store.FilePath), "設定がない初回は既定値を返しファイルを作らない");
        AppSettings expected = defaults with
        {
            OutputDirectory = Path.Combine(directory, "動画"), FfmpegPath = @"C:\検証\ffmpeg.exe", FramesPerSecond = 60,
            QualityCrf = 18, Container = "mkv", CountdownSeconds = 60, MaximumSeconds = 36_000,
            RecordInput = false, IncludePlainKeys = true, ExclusiveWindow = false, CaptureWindows = false
        };
        store.Save(expected);
        check(store.Load(out warning) == expected && warning is null, "全設定を日本語パスの JSON へ保存して復元");
        store.Save(expected with { FramesPerSecond = 15 });
        check(store.Load(out warning).FramesPerSecond == 15 && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "設定の再保存を確定して一時ファイルを残さない");

        const string broken = "{broken original settings";
        File.WriteAllText(store.FilePath, broken);
        AppSettings recovered = store.Load(out warning);
        check(warning is not null && recovered == new AppSettings() && File.ReadAllText(store.FilePath) == broken, "破損設定は警告して既定値へ戻し原本を保持");
        store.Save(expected);
        string[] backups = Directory.GetFiles(directory, "*.bak");
        check(backups.Length == 1 && File.ReadAllText(backups[0]) == broken && store.Load(out warning) == expected, "破損した原本をバックアップしてから設定を再保存");

        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(new AppSettings
        {
            OutputDirectory = "", FfmpegPath = null!, FramesPerSecond = 0, QualityCrf = 52,
            Container = "gif", CountdownSeconds = 61, MaximumSeconds = 36_001
        }));
        AppSettings normalized = store.Load(out warning);
        check(warning is not null && normalized.OutputDirectory == defaults.OutputDirectory && normalized.FfmpegPath == ""
            && normalized.FramesPerSecond == 30 && normalized.QualityCrf == 23 && normalized.Container == "mp4"
            && normalized.CountdownSeconds == 3 && normalized.MaximumSeconds == 0, "無効な保存設定を実行可能な既定値へ正規化");
    }

    private static void FileTests(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        string oldVideo = Path.Combine(directory, "古い.mp4");
        string newest = Path.Combine(directory, "新しい.MKV");
        File.WriteAllText(oldVideo, "video-one");
        File.WriteAllText(Path.ChangeExtension(oldVideo, ".tsv"), "input-one");
        File.WriteAllText(newest, "video-two");
        File.WriteAllText(Path.Combine(directory, ".capchunscreen-test.partial.mp4"), "partial");
        File.WriteAllText(Path.Combine(directory, "test.partial.mkv"), "partial");
        File.WriteAllText(Path.Combine(directory, "対象外.txt"), "text");
        File.SetLastWriteTime(oldVideo, DateTime.Now.AddHours(-1));
        File.SetLastWriteTime(newest, DateTime.Now);
        IReadOnlyList<RecordingFile> list = RecordingFiles.List(directory);
        check(list.Count == 2 && list[0].VideoPath == newest && list[0].Bytes == 9, "MP4/MKV だけを新しい順に列挙し未完了出力を除外");

        string renamed = RecordingFiles.Rename(oldVideo, "改名後");
        check(Path.GetFileName(renamed) == "改名後.mp4" && File.ReadAllText(renamed) == "video-one"
            && File.ReadAllText(Path.ChangeExtension(renamed, ".tsv")) == "input-one" && !File.Exists(oldVideo), "動画と TSV を一緒に改名し拡張子を維持");
        string collision = Path.Combine(directory, "衝突.mp4");
        File.WriteAllText(collision, "existing-video");
        check(Throws<IOException>(() => RecordingFiles.Rename(renamed, "衝突")) && File.Exists(renamed)
            && File.ReadAllText(collision) == "existing-video", "改名先の動画を上書きしない");
        File.WriteAllText(Path.Combine(directory, "TSV衝突.tsv"), "existing-input");
        check(Throws<IOException>(() => RecordingFiles.Rename(renamed, "TSV衝突")) && File.Exists(renamed), "改名先の TSV との衝突を拒否");
        foreach (string invalid in new[] { "../脱出", "", ".", "CON", "LPT1", "末尾.", "空白 " })
            check(Throws<ArgumentException>(() => RecordingFiles.Rename(renamed, invalid)), "パス・予約名・不正名の改名を拒否");

        bool rollbackFailed = Throws<IOException>(() => RecordingFiles.RenameCore(renamed, "失敗後", (source, destination) =>
        {
            if (Path.GetFileName(destination) == "失敗後.mp4") throw new IOException("意図した動画の移動失敗");
            File.Move(source, destination, overwrite: false);
        }));
        check(rollbackFailed && File.Exists(renamed) && File.Exists(Path.ChangeExtension(renamed, ".tsv"))
            && !File.Exists(Path.Combine(directory, "失敗後.tsv")), "動画の改名失敗時に先行した TSV を元に戻す");

        string caseVideo = Path.Combine(directory, "case.mp4");
        File.WriteAllText(caseVideo, "case-video");
        File.WriteAllText(Path.ChangeExtension(caseVideo, ".tsv"), "case-input");
        string upperCase = RecordingFiles.Rename(caseVideo, "CASE");
        check(Path.GetFileName(upperCase) == "CASE.mp4" && Directory.GetFiles(directory).Any(path => Path.GetFileName(path) == "CASE.mp4")
            && Directory.GetFiles(directory).Any(path => Path.GetFileName(path) == "CASE.tsv"), "大文字小文字だけの改名を動画と TSV へ反映");

        IOException? recycleFailure = null;
        try
        {
            RecordingFiles.RecycleCore(upperCase, path =>
            {
                if (Path.GetExtension(path) == ".tsv") throw new IOException("意図した TSV の移動失敗");
                File.Delete(path); // 専用テストフォルダーのみ。実際のごみ箱 API は呼ばない。
            });
        }
        catch (IOException ex) { recycleFailure = ex; }
        string remainingTsv = Path.ChangeExtension(upperCase, ".tsv");
        check(recycleFailure is not null && recycleFailure.Message.Contains(remainingTsv, StringComparison.Ordinal)
            && !File.Exists(upperCase) && File.Exists(remainingTsv), "ごみ箱処理の途中失敗を残件ファイル付きで通知（API を模擬）");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
