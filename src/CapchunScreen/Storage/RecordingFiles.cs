using Microsoft.VisualBasic.FileIO;

namespace CapchunScreen.Storage;

public sealed record RecordingFile(string VideoPath, DateTime Modified, long Bytes)
{
    public string Name => Path.GetFileName(VideoPath);
    public string TsvPath => Path.ChangeExtension(VideoPath, ".tsv");
}

public static class RecordingFiles
{
    public static IReadOnlyList<RecordingFile> List(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        List<RecordingFile> recordings = [];
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                if (!IsRecording(path))
                    continue;
                try
                {
                    FileInfo file = new(path);
                    recordings.Add(new(file.FullName, file.LastWriteTime, file.Length));
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // 列挙中に別の操作で削除されたファイルは一覧に残さない。
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        return recordings.OrderByDescending(file => file.Modified).ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string Rename(string videoPath, string newBaseName) => RenameCore(videoPath, newBaseName,
        (source, destination) => File.Move(source, destination, overwrite: false));

    internal static string RenameCore(string videoPath, string newBaseName, Action<string, string> move)
    {
        string sourceVideo = ValidateVideo(videoPath);
        ValidateBaseName(newBaseName);
        string directory = Path.GetDirectoryName(sourceVideo)!;
        string destinationVideo = Path.Combine(directory, newBaseName + Path.GetExtension(sourceVideo));
        string sourceTsv = Path.ChangeExtension(sourceVideo, ".tsv");
        string destinationTsv = Path.ChangeExtension(destinationVideo, ".tsv");
        if (string.Equals(sourceVideo, destinationVideo, StringComparison.Ordinal))
            return sourceVideo;

        EnsureDestinationAvailable(sourceVideo, destinationVideo);
        // TSV が元になくても、改名先にある別録画の TSV と関連付けない。
        EnsureDestinationAvailable(sourceTsv, destinationTsv);
        List<(string Source, string Destination)> operations = [];
        if (File.Exists(sourceTsv))
            AddMoveOperations(sourceTsv, destinationTsv, operations);
        AddMoveOperations(sourceVideo, destinationVideo, operations);
        List<(string Source, string Destination)> completed = [];
        try
        {
            foreach (var operation in operations)
            {
                move(operation.Source, operation.Destination);
                completed.Add(operation);
            }
        }
        catch (Exception failure)
        {
            List<Exception> errors = [failure];
            List<string> remaining = [];
            for (int i = completed.Count - 1; i >= 0; i--)
            {
                var operation = completed[i];
                try { move(operation.Destination, operation.Source); }
                catch (Exception rollbackFailure)
                {
                    errors.Add(rollbackFailure);
                    remaining.Add($"{operation.Destination} → {operation.Source}");
                }
            }
            string message = remaining.Count == 0
                ? "録画の改名に失敗しました。変更したファイル名は元に戻しました。"
                : $"録画の改名に失敗し、一部を元に戻せませんでした。次の復元が必要です。{Environment.NewLine}{string.Join(Environment.NewLine, remaining)}";
            throw new IOException(message, errors.Count == 1 ? failure : new AggregateException(errors));
        }
        return destinationVideo;
    }

    public static void Recycle(string videoPath) => RecycleCore(videoPath, path =>
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException));

    internal static void RecycleCore(string videoPath, Action<string> recycle)
    {
        string video = ValidateVideo(videoPath);
        string tsv = Path.ChangeExtension(video, ".tsv");
        string[] files = File.Exists(tsv) ? [video, tsv] : [video];
        List<string> completed = [];
        try
        {
            foreach (string file in files)
            {
                recycle(file);
                completed.Add(file);
            }
        }
        catch (Exception failure)
        {
            string remaining = string.Join(Environment.NewLine, files.Where(File.Exists));
            string completedText = completed.Count == 0 ? "なし" : string.Join(Environment.NewLine, completed);
            throw new IOException($"ごみ箱への移動を完了できませんでした。{Environment.NewLine}移動済み:{Environment.NewLine}{completedText}{Environment.NewLine}残っているファイル:{Environment.NewLine}{(remaining.Length == 0 ? "なし（元の場所を確認してください）" : remaining)}", failure);
        }
    }

    private static bool IsRecording(string path)
    {
        string extension = Path.GetExtension(path);
        string name = Path.GetFileName(path);
        return (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            && !name.StartsWith('.') && !name.Contains(".partial.", StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateVideo(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        string fullPath = Path.GetFullPath(videoPath);
        if (!IsRecording(fullPath))
            throw new ArgumentException("完成した MP4 または MKV ファイルを指定してください。", nameof(videoPath));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("録画ファイルが見つかりません。", fullPath);
        return fullPath;
    }

    private static void ValidateBaseName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (GetBaseNameError(name) is { } error)
            throw new ArgumentException(error, nameof(name));
    }

    /// <summary>改名に使えない名前なら理由を返す。名前変更画面も同じ規則で事前に確認する。</summary>
    internal static string? GetBaseNameError(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "新しい名前を入力してください。";
        if (name != name.Trim() || name is "." or ".." || name.StartsWith('.') || name.EndsWith('.')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(name) != name || name.Length > 251)
            return "拡張子を除く単純なファイル名を指定してください。先頭のピリオド、末尾の空白・ピリオド、パス、251文字を超える名前は使用できません。";
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3]))
            return "Windows の予約された名前は使用できません。";
        return null;
    }

    private static void EnsureDestinationAvailable(string source, string destination)
    {
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            && (File.Exists(destination) || Directory.Exists(destination)))
            throw new IOException($"同じ名前のファイルまたはフォルダーが既にあります: {destination}");
    }

    private static void AddMoveOperations(string source, string destination, List<(string Source, string Destination)> operations)
    {
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            string temporary = Path.Combine(Path.GetDirectoryName(source)!, $".capchunscreen-rename-{Guid.NewGuid():N}.tmp");
            operations.Add((source, temporary));
            operations.Add((temporary, destination));
        }
        else
        {
            operations.Add((source, destination));
        }
    }
}
