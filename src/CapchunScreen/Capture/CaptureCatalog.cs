using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CapchunScreen.Capture;

public static class CaptureCatalog
{
    public static IReadOnlyList<CaptureTarget> GetTargets(CaptureKind kind, string filter = "", bool includeMinimized = false)
    {
        ArgumentNullException.ThrowIfNull(filter);
        IEnumerable<CaptureTarget> targets = kind switch
        {
            CaptureKind.Monitor => Screen.AllScreens.Select((screen, index) => new CaptureTarget(screen.DeviceName,
                $"モニター {index + 1} ({screen.Bounds.Width} × {screen.Bounds.Height}){(screen.Primary ? " メイン" : "")}",
                CaptureKind.Monitor, 0, screen.Bounds)),
            CaptureKind.Window => CaptureNative.GetWindows(includeMinimized),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return targets.Where(target => target.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
            || target.ProcessName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}

internal static class CaptureNative
{
    private delegate bool WindowCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    internal static IReadOnlyList<CaptureTarget> GetWindows(bool includeMinimized = false)
    {
        var windows = new List<CaptureTarget>();
        var ownProcess = (uint)Environment.ProcessId;
        WindowCallback callback = (window, _) =>
        {
            if (!IsWindowVisible(window) || !includeMinimized && IsIconic(window) || IsCloaked(window)) return true;
            uint threadId = GetWindowThreadProcessId(window, out uint processId);
            if (processId == ownProcess || window == GetShellWindow()) return true;
            int length = GetWindowTextLength(window);
            if (length <= 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(window, title, title.Capacity);
            if (title.Length == 0 || !TryGetBounds(window, out var bounds)) return true;
            string processName = "";
            try { using var process = Process.GetProcessById((int)processId); processName = process.ProcessName; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
            windows.Add(new CaptureTarget($"window:{window:X}:{processId}:{threadId}", title.ToString(), CaptureKind.Window, window, bounds, processName));
            return true;
        };
        if (!EnumWindows(callback, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "ウィンドウ一覧を取得できません。");
        return windows.OrderBy(window => window.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static (uint ProcessId, uint ThreadId) Identity(nint window)
    {
        uint thread = GetWindowThreadProcessId(window, out uint process);
        if (thread == 0 || !IsWindow(window)) throw new InvalidOperationException("選択したウィンドウは終了しています。選び直してください。");
        return (process, thread);
    }

    internal static Rectangle RequireWindowBounds(nint window, (uint ProcessId, uint ThreadId) identity)
    {
        if (!IsWindow(window) || Identity(window) != identity)
            throw new InvalidOperationException("選択したウィンドウは終了しています。選び直してください。");
        if (IsIconic(window)) throw new InvalidOperationException("選択したウィンドウが最小化されています。元に戻してください。");
        if (!IsWindowVisible(window) || IsCloaked(window))
            throw new InvalidOperationException("選択したウィンドウは表示されていません。");
        if (!TryGetBounds(window, out var bounds)) throw new InvalidOperationException("ウィンドウの描画範囲を取得できません。");
        return bounds;
    }

    private static bool IsCloaked(nint window) => DwmGetWindowAttributeInt(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool TryGetBounds(nint window, out Rectangle bounds)
    {
        bool found = DwmGetWindowAttribute(window, 9, out NativeRect rect, Marshal.SizeOf<NativeRect>()) == 0 || GetWindowRect(window, out rect);
        bounds = found ? rect.Rectangle : Rectangle.Empty;
        return found && bounds.Width > 0 && bounds.Height > 0;
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(nint window, int attribute, out int value, int size);
}
