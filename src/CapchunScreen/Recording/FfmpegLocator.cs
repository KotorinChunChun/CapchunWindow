namespace CapchunScreen.Recording;

public static class FfmpegLocator
{
    public static string? Find(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        string[] bundled =
        [
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, ".tools", "ffmpeg", "bin", "ffmpeg.exe")
        ];
        foreach (string candidate in bundled)
            if (File.Exists(candidate))
                return candidate;

        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (directory.Length == 0)
                continue;
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(directory);
                // 「.」などの相対項目は起動時のカレントフォルダーに置かれた別の ffmpeg.exe を拾うため除外する。
                if (!Path.IsPathFullyQualified(expanded))
                    continue;
                string candidate = Path.Combine(expanded, "ffmpeg.exe");
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // PATH 内の不正な項目はほかの探索候補に影響させない。
            }
        }
        return null;
    }
}
