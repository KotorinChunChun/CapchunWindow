using System.Text.Json;

namespace CapchunScreen.Storage;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();

    public SettingsStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapchunScreen");
        FilePath = Path.Combine(Path.GetFullPath(directory), "settings.json");
    }

    public string FilePath { get; }

    public AppSettings Load(out string? warning)
    {
        lock (_gate)
        {
            warning = null;
            try
            {
                AppSettings settings = ReadSettings();
                AppSettings normalized = Normalize(settings);
                if (settings != normalized)
                    warning = "保存された設定に範囲外または無効な値があり、既定値に戻すか表記を整えました。";
                return normalized;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                warning = $"設定を読み込めなかったため既定値を使用します。元の設定はそのまま残しています。{Environment.NewLine}{ex.Message}";
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Normalize(settings);
        lock (_gate)
        {
            string directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
            try
            {
                using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, settings, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(FilePath))
                {
                    // 読み込み後に外部から変更された場合も、破損した原本を失わない。
                    try { _ = ReadSettings(); }
                    catch (JsonException)
                    {
                        string backup = Path.Combine(directory, $"settings.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json.bak");
                        File.Copy(FilePath, backup, overwrite: false);
                    }
                    File.Replace(temporary, FilePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporary, FilePath, overwrite: false);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }

    private AppSettings ReadSettings()
    {
        using FileStream stream = new(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions)
            ?? throw new JsonException("設定の内容が null です。");
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        string output = settings.OutputDirectory?.Trim() ?? "";
        try
        {
            if (output.Length == 0 || output.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || output.IndexOfAny(['*', '?', '<', '>', '|', '"']) >= 0)
                output = AppSettings.DefaultOutputDirectory;
            else
                output = Path.GetFullPath(output);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            output = AppSettings.DefaultOutputDirectory;
        }
        string container = settings.Container?.Trim().ToLowerInvariant() ?? "mp4";
        return settings with
        {
            OutputDirectory = output,
            FfmpegPath = settings.FfmpegPath?.Trim() ?? "",
            FramesPerSecond = settings.FramesPerSecond is >= 1 and <= 120 ? settings.FramesPerSecond : 30,
            QualityCrf = settings.QualityCrf is >= 0 and <= 51 ? settings.QualityCrf : 23,
            Container = container is "mp4" or "mkv" ? container : "mp4",
            CountdownSeconds = settings.CountdownSeconds is >= 0 and <= 60 ? settings.CountdownSeconds : 3,
            MaximumSeconds = settings.MaximumSeconds is >= 0 and <= 36_000 ? settings.MaximumSeconds : 0
        };
    }
}
