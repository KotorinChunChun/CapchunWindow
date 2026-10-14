namespace CapchunScreen.Input;

internal static class InputScope
{
    internal static bool ContainsWindowCenter(Rectangle recordingBounds, Rectangle windowBounds)
    {
        if (recordingBounds.Width <= 0 || recordingBounds.Height <= 0 || windowBounds.Width <= 0 || windowBounds.Height <= 0)
            return false;
        double x = windowBounds.X + windowBounds.Width / 2.0;
        double y = windowBounds.Y + windowBounds.Height / 2.0;
        return x >= recordingBounds.Left && x < recordingBounds.Right && y >= recordingBounds.Top && y < recordingBounds.Bottom;
    }
}
