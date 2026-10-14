namespace CapchunScreen.UI;

public static class WindowTitle
{
    public static string Create(string? screen = null)
    {
        Version version = typeof(WindowTitle).Assembly.GetName().Version!;
        string product = $"CapchunScreen - ver {version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        return string.IsNullOrWhiteSpace(screen) ? product : $"{screen.Trim()} - {product}";
    }
}
