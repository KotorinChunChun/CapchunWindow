using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CapchunScreen.Input;

/// <summary>録画中だけUIスレッドに設置する低レベル入力フック。</summary>
public sealed class InputMonitor : IInputSource, IDisposable
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly InputBuffer _buffer;
    private readonly Native.HookProc _keyboardCallback;
    private readonly Native.HookProc _mouseCallback;
    private nint _keyboardHook;
    private nint _mouseHook;
    private Exception? _callbackFailure;
    private volatile bool _disposeRequested;
    private volatile bool _disposed;

    public InputMonitor(bool includePlainKeys = false)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("入力記録はメッセージループのあるUIスレッドで開始してください。");
        _buffer = new(includePlainKeys);
        _keyboardCallback = KeyboardCallback;
        _mouseCallback = MouseCallback;
        nint module = Native.GetModuleHandle(null);
        if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "入力記録用モジュールを取得できません。");
        try
        {
            _keyboardHook = Native.SetWindowsHookEx(13, _keyboardCallback, module, 0);
            if (_keyboardHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "キーボード入力記録を開始できません。");
            _mouseHook = Native.SetWindowsHookEx(14, _mouseCallback, module, 0);
            if (_mouseHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "マウス入力記録を開始できません。");
            // 開始ホットキーを押したままの状態でも左右の修飾キーを正しく引き継ぐ。
            foreach (int key in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) _buffer.KeyChanged(key, true, Native.GetForegroundWindow());
        }
        catch
        {
            ReleaseHooks();
            throw;
        }
    }

    public InputSample Consume(Rectangle bounds, nint targetWindow)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        Exception? failure = Volatile.Read(ref _callbackFailure);
        if (failure is not null) throw new InvalidOperationException("入力操作の記録中にエラーが発生しました。", failure);
        if (!Native.GetCursorPos(out var point))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "マウス位置を取得できません。");
        return _buffer.Consume(bounds, new(point.X, point.Y), Native.GetForegroundWindow(), targetWindow,
            (foreground, target) => target == 0 ? IsForegroundInsideBounds(foreground, bounds) : IsTargetOrOwnedWindow(foreground, target));
    }

    private static bool IsForegroundInsideBounds(nint foreground, Rectangle bounds)
    {
        if (foreground == 0 || !Native.GetWindowRect(foreground, out var rectangle)) return false;
        return InputScope.ContainsWindowCenter(bounds, Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom));
    }

    private static bool IsTargetOrOwnedWindow(nint foreground, nint target)
    {
        for (int depth = 0; foreground != 0 && depth < 64; depth++)
        {
            if (foreground == target || Native.IsChild(target, foreground)) return true;
            foreground = Native.GetWindow(foreground, 4); // GW_OWNER
        }
        return false;
    }

    private nint KeyboardCallback(int code, nuint message, nint data)
    {
        if (code >= 0 && !_disposeRequested)
        {
            try
            {
                if (message is 0x100 or 0x104 or 0x101 or 0x105)
                {
                    var input = Marshal.PtrToStructure<Native.KeyboardInput>(data);
                    _buffer.KeyChanged((int)input.VirtualKey, message is 0x100 or 0x104, Native.GetForegroundWindow());
                }
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref _callbackFailure, ex, null); }
        }
        return Native.CallNextHookEx(0, code, message, data);
    }

    private nint MouseCallback(int code, nuint message, nint data)
    {
        if (code >= 0 && !_disposeRequested)
        {
            try
            {
                var input = Marshal.PtrToStructure<Native.MouseInput>(data);
                Point point = new(input.Position.X, input.Position.Y);
                nint foreground = Native.GetForegroundWindow();
                string? action = message switch
                {
                    0x201 or 0x202 => "L-Click", 0x204 or 0x205 => "R-Click", 0x207 or 0x208 => "M-Click",
                    0x20B or 0x20C => (input.MouseData >> 16) == 1 ? "X1-Click" : "X2-Click", _ => null
                };
                if (action is not null)
                    _buffer.MouseButtonChanged(action, message is 0x201 or 0x204 or 0x207 or 0x20B, point, foreground);
                else if (message is 0x20A or 0x20E)
                    _buffer.WheelMoved(message == 0x20E, unchecked((short)(input.MouseData >> 16)), point, foreground);
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref _callbackFailure, ex, null); }
        }
        return Native.CallNextHookEx(0, code, message, data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownerThreadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("入力記録は開始したUIスレッドで終了してください。");
        _disposeRequested = true;
        int error = ReleaseHooks();
        _disposed = _mouseHook == 0 && _keyboardHook == 0;
        if (_disposed) GC.SuppressFinalize(this);
        if (error != 0) throw new Win32Exception(error, "入力記録フックを解除できません。");
    }

    private int ReleaseHooks()
    {
        int error = 0;
        void Release(ref nint hook)
        {
            if (hook == 0) return;
            if (Native.UnhookWindowsHookEx(hook)) hook = 0;
            else
            {
                int lastError = Marshal.GetLastWin32Error();
                // タイムアウト等でOSが既に除去したフックは解放済みとして扱う。
                if (lastError == 1404) hook = 0; // ERROR_INVALID_HOOK_HANDLE
                else error = lastError == 0 ? 31 : lastError;
            }
        }
        Release(ref _mouseHook);
        Release(ref _keyboardHook);
        GC.KeepAlive(_mouseCallback);
        GC.KeepAlive(_keyboardCallback);
        return error;
    }

    ~InputMonitor() => ReleaseHooks();

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint HookProc(int code, nuint message, nint data);
        [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { internal int X; internal int Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct NativeRectangle
        { internal int Left; internal int Top; internal int Right; internal int Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
        { internal uint VirtualKey; internal uint ScanCode; internal uint Flags; internal uint Time; internal nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        { internal NativePoint Position; internal uint MouseData; internal uint Flags; internal uint Time; internal nuint ExtraInfo; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint GetModuleHandle(string? moduleName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetWindowsHookEx(int hookId, HookProc callback, nint module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsChild(nint parent, nint child);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    }
}
