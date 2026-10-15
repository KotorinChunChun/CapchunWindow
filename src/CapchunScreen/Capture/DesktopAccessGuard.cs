using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CapchunScreen.Capture;

internal sealed record DesktopAccessState(bool Available, int SessionId, string CurrentDesktop, string InputDesktop, string Operation, int NativeError,
    string SessionState = "Unknown");

internal sealed class DesktopAccessException(string message, string? diagnosticPath = null, Exception? inner = null)
    : IOException(message, inner)
{
    internal string? DiagnosticPath { get; } = diagnosticPath;
}

/// <summary>保護デスクトップへ切り替えず、通常デスクトップへのアクセスを読み取り確認する。</summary>
internal sealed class DesktopAccessGuard
{
    internal const string UnavailableMessage = "デスクトップへのアクセスが中断されました。ロック画面や権限確認を閉じ、通常の画面へ戻ってから対象を選び直してください。録画は自動再開しません。";
    private readonly object gate = new();
    private readonly Func<DesktopAccessState> probe;
    private readonly string directory;
    private DesktopAccessState? previous;
    private long checkedAt;
    internal string? DiagnosticPath { get; private set; }
    internal string? DiagnosticWriteError { get; private set; }
    internal static DesktopAccessGuard Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapchunScreen", "diagnostics"));

    internal DesktopAccessGuard(string directory, Func<DesktopAccessState>? probe = null)
    { this.directory = directory; this.probe = probe ?? Probe; }

    internal DesktopAccessState Check(bool force = false)
    {
        lock (gate)
        {
            if (!force && previous is not null && Environment.TickCount64 - checkedAt < 100) return previous;
            var state = probe(); checkedAt = Environment.TickCount64;
            if (previous != state) WriteDiagnostic(state, previous is null ? "initial" : state.Available ? "recovered" : "unavailable");
            previous = state;
            return state;
        }
    }

    internal void RequireAccess()
    { if (!Check().Available) throw new DesktopAccessException(UnavailableMessage, DiagnosticPath); }

    internal DesktopAccessException Failure(Exception error)
    {
        lock (gate)
        {
            var state = Check(force: true);
            int native = NativeError(error);
            WriteDiagnostic(state with { Available = false, Operation = "capture-or-input", NativeError = native }, "failure");
            return new DesktopAccessException(UnavailableMessage, DiagnosticPath, error);
        }
    }

    internal static bool IsAccessDenied(Exception error) => NativeError(error) == 5;
    private static int NativeError(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception native && native.NativeErrorCode == 5 || current is UnauthorizedAccessException
                || current.HResult == unchecked((int)0x80070005)) return 5;
        }
        return error is Win32Exception win32 ? win32.NativeErrorCode : 0;
    }

    private void WriteDiagnostic(DesktopAccessState state, string transition)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"desktop-access-{DateTime.Now:yyyyMMdd}.jsonl");
            File.AppendAllText(path, JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, processId = Environment.ProcessId,
                transition, state.SessionId, state.SessionState, state.Available, state.CurrentDesktop, state.InputDesktop, state.Operation, state.NativeError }) + Environment.NewLine, Encoding.UTF8);
            DiagnosticPath = path; DiagnosticWriteError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { DiagnosticWriteError = "診断ログを保存できませんでした: " + error.Message; }
    }

    internal static DesktopAccessState Probe()
    {
        int sessionId; using (var process = Process.GetCurrentProcess()) sessionId = process.SessionId;
        string sessionState = SessionState(sessionId);
        string current = DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
        if (sessionState is "Disconnected" or "Down" or "Reset") return new(false, sessionId, current, "", "session-inactive", 0, sessionState);
        nint input = Native.OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTSのみ。権限や接続先を変更しない。
        if (input == 0) return new(false, sessionId, current, "", "OpenInputDesktop", Marshal.GetLastWin32Error(), sessionState);
        try
        {
            string name = DesktopName(input);
            if (name.Length == 0 || current.Length == 0 || !name.Equals(current, StringComparison.Ordinal))
                return new(false, sessionId, current, name, "desktop-mismatch", 0, sessionState);
            if (!Native.GetCursorPos(out _)) return new(false, sessionId, current, name, "GetCursorPos", Marshal.GetLastWin32Error(), sessionState);
            return new(true, sessionId, current, name, "ready", 0, sessionState);
        }
        finally { Native.CloseDesktop(input); }
    }

    private static string DesktopName(nint desktop)
    {
        if (desktop == 0) return "";
        var name = new StringBuilder(256);
        return Native.GetUserObjectInformation(desktop, 2, name, name.Capacity * sizeof(char), out _) ? name.ToString() : "";
    }

    private static string SessionState(int sessionId)
    {
        if (!Native.WTSQuerySessionInformation(0, sessionId, 8, out nint buffer, out int length)) return "Unknown";
        try { return length < sizeof(int) ? "Unknown" : Marshal.ReadInt32(buffer) switch
            { 0 => "Active", 1 => "Connected", 2 => "ConnectQuery", 3 => "Shadow", 4 => "Disconnected", 5 => "Idle", 6 => "Listen", 7 => "Reset", 8 => "Down", 9 => "Init", _ => "Unknown" }; }
        finally { Native.WTSFreeMemory(buffer); }
    }

    private static class Native
    {
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseDesktop(nint desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder name, int length, out int needed);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformation(nint server, int sessionId, int informationClass, out nint buffer, out int bytes);
        [DllImport("wtsapi32.dll")] internal static extern void WTSFreeMemory(nint buffer);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    }
}

internal sealed class DesktopGuardedFrameSource(IFrameSource inner, DesktopAccessGuard guard) : IFrameSource
{
    public CapturedFrame Capture(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); guard.RequireAccess();
        try { return inner.Capture(cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException && error is not DesktopAccessException
            && (DesktopAccessGuard.IsAccessDenied(error) || !guard.Check(force: true).Available))
        { throw guard.Failure(error); }
    }
    public void Dispose() => inner.Dispose();
}
