namespace CapchunScreen.Recording;

public sealed record RecordingOptions
{
    public string OutputDirectory { get; init; } = "";
    public string FfmpegPath { get; init; } = "";
    public int FramesPerSecond { get; init; } = 30;
    public int QualityCrf { get; init; } = 23;
    public string Container { get; init; } = "mp4";
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.Zero;
    public bool RecordInput { get; init; } = true;
}

public sealed record RecordingResult(string VideoPath, string? TsvPath, long Frames, double DurationSeconds,
    string? InterruptionReason = null, string? DiagnosticPath = null);
