namespace CapchunScreen.Storage;

public sealed record AppSettings
{
    public string OutputDirectory { get; init; } = DefaultOutputDirectory;
    public string FfmpegPath { get; init; } = "";
    public int FramesPerSecond { get; init; } = 30;
    public int QualityCrf { get; init; } = 23;
    public string Container { get; init; } = "mp4";
    public int CountdownSeconds { get; init; } = 3;
    public int MaximumSeconds { get; init; } = 0;
    public bool RecordInput { get; init; } = true;
    public bool IncludePlainKeys { get; init; } = false;
    public bool ExclusiveWindow { get; init; } = true;
    public bool CaptureWindows { get; init; } = true;

    internal static string DefaultOutputDirectory
    {
        get
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos))
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            return Path.Combine(videos, "CapchunScreen");
        }
    }
}
