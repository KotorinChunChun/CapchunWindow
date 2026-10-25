using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CapchunScreen.Capture;

namespace CapchunScreen.Input;

/// <summary>
/// 録画中だけ設置する低レベル入力フック。UIスレッドの描画負荷でPC全体の入力が遅れたり、
/// 応答遅延でWindowsにフックを外されたりしないよう、メッセージループだけを回す専用スレッドで受ける。
/// パスワード入力中と判定したキー、判定できなかったキーはTSV・入力テストのどちらにも渡さない。
/// </summary>
public sealed class InputMonitor : IInputSource, IInputHealth, IDisposable
{
    // システムが入力を受けているのにフックへ届かない時間がこれを超えたら、外されたとみなして再設置する。
    internal const int HookSilenceMilliseconds = 3000;
    private const uint QuitMessage = 0x0012; // WM_QUIT
    private const uint ReinstallMessage = 0x8001; // WM_APP + 1
    // 押したキーの保護判定を、そのフレームの中で待つ上限。超えたキーは次のフレームで記録するか破棄する。
    internal const int ConsumeWaitMilliseconds = 80;
    internal const string ProtectedDetectionText = "（パスワード入力中または判定不能のため表示しません）";
    private readonly InputBuffer _buffer;
    private readonly InputProtectionMonitor _protection;
    private readonly ConcurrentQueue<(string Text, long Sequence, long Time, bool Key)> _detected = new();
    private readonly Native.WinEventProc _focusCallback;
    private nint _foregroundEventHook;
    private nint _focusEventHook;
    private volatile bool _focusTrackingFailed;
    private readonly DesktopAccessGuard _desktopAccess;
    private readonly Native.HookProc _keyboardCallback;
    private readonly Native.HookProc _mouseCallback;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<uint> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly uint _threadId;
    // 以下2つのフックハンドルはフック用スレッドだけが読み書きする。
    private nint _keyboardHook;
    private nint _mouseHook;
    private int _releaseError;
    private int _lastHookEvent;
    private int _reconnects;
    private Exception? _callbackFailure;
    private volatile bool _disposeRequested;
    private int _disposed;

    public InputMonitor(bool includePlainKeys = false, IReadOnlyDictionary<string, bool>? recordOverrides = null)
        : this(includePlainKeys, recordOverrides, DesktopAccessGuard.Shared) { }

    internal InputMonitor(bool includePlainKeys, IReadOnlyDictionary<string, bool>? recordOverrides, DesktopAccessGuard desktopAccess,
        InputProtectionMonitor? protection = null)
    {
        _desktopAccess = desktopAccess;
        _desktopAccess.RequireAccess();
        _buffer = new(includePlainKeys, recordOverrides);
        _keyboardCallback = KeyboardCallback;
        _mouseCallback = MouseCallback;
        _focusCallback = FocusCallback;
        _protection = protection ?? new InputProtectionMonitor();
        _thread = new Thread(RunHookThread) { IsBackground = true, Name = "CapchunWindow input hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        try
        {
            if (!_started.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("入力記録を開始できません。フック用スレッドが応答しません。");
            _threadId = _started.Task.Result;
        }
        catch (Exception error)
        {
            _disposeRequested = true;
            _protection.Dispose();
            Exception cause = error is AggregateException { InnerException: { } inner } ? inner : error;
            if (DesktopAccessGuard.IsAccessDenied(cause)) throw _desktopAccess.Failure(cause);
            ExceptionDispatchInfo.Throw(cause);
            throw;
        }
    }

    /// <summary>フックの再接続やパスワード入力の保護で、TSVに記録しなかった操作があり得ることを伝える。</summary>
    public string? Warning
    {
        get
        {
            List<string> messages = [];
            if (Volatile.Read(ref _reconnects) is int count and > 0)
                messages.Add($"操作記録のフックが応答しなくなったため{count}回再接続しました。その前後の操作がTSVに残っていない可能性があります。");
            if (_protection.Unavailable || _focusTrackingFailed)
                messages.Add("パスワード入力欄を判定できない環境のため、キー操作をTSVに記録していません。");
            else if (_buffer.DroppedProtectedKeys)
                messages.Add("パスワード入力欄を検出した間、または判定できなかった間のキー操作はTSVに記録していません。");
            return messages.Count == 0 ? null : string.Join(" ", messages);
        }
    }

    internal int Reconnects => Volatile.Read(ref _reconnects);

    public InputSample Consume(Rectangle bounds, nint targetWindow)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        _desktopAccess.RequireAccess();
        Exception? failure = Volatile.Read(ref _callbackFailure);
        if (failure is not null) throw new InvalidOperationException("入力操作の記録中にエラーが発生しました。", failure);
        CheckHookAlive();
        if (!Native.GetCursorPos(out var point))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error(), "マウス位置を取得できません。");
            if (DesktopAccessGuard.IsAccessDenied(error)) throw _desktopAccess.Failure(error);
            throw error;
        }
        // 押したキーの保護判定はフレーム内で少し待ち、通常はキーを押したフレームの行へ記録する。
        if (_buffer.LatestUnresolvedKeySequence is { } latest)
            _protection.Timeline.WaitForEvaluationAfter(latest, ConsumeWaitMilliseconds);
        return _buffer.Consume(bounds, new(point.X, point.Y), Native.GetForegroundWindow(), targetWindow,
            (foreground, target) => target == 0 ? IsForegroundInsideBounds(foreground, bounds) : IsTargetOrOwnedWindow(foreground, target),
            Native.IsKeyDown, Classify);
    }

    private InputProtectionVerdict Classify(long sequence, long time) =>
        _focusTrackingFailed ? InputProtectionVerdict.Protected : _protection.Timeline.Classify(sequence, time);

    /// <summary>
    /// 入力テスト用に、最後に検出した入力名を返す。キーは保護判定が済んでから返し、
    /// パスワード入力中または判定不能のキーは名前の代わりに保護中の表示を返す。判定待ちの間はnull。
    /// </summary>
    internal string? TakeDetectedInput()
    {
        string? latest = null;
        while (_detected.TryPeek(out var item))
        {
            if (item.Key)
            {
                var verdict = Classify(item.Sequence, item.Time);
                if (verdict == InputProtectionVerdict.Pending) break;
                latest = verdict == InputProtectionVerdict.Allowed ? item.Text : ProtectedDetectionText;
            }
            else latest = item.Text;
            _detected.TryDequeue(out _);
        }
        return latest;
    }

    private void Detected(string text, long sequence, bool key)
    {
        _detected.Enqueue((text, sequence, _protection.Timeline.Now, key));
        // 入力テストを読まない録画中も、検出名を溜め続けない。
        while (_detected.Count > 64) _detected.TryDequeue(out _);
    }

    /// <summary>フォーカスを動かし得る入力・変化を区切りとして記録し、すぐに保護判定を始めさせる。</summary>
    private void MarkBoundary(long sequence)
    {
        _protection.Timeline.Boundary(sequence);
        _protection.Wake();
    }

    /// <summary>
    /// Windowsは応答の遅い低レベルフックを通知なしで外す。システムの最終入力時刻とフックの受信時刻を比べ、
    /// 入力が続いているのにフックへ届かなくなっていれば、フック用スレッドへ再設置を依頼する。
    /// </summary>
    private void CheckHookAlive()
    {
        var info = new Native.LastInputInfo { Size = (uint)Marshal.SizeOf<Native.LastInputInfo>() };
        if (!Native.GetLastInputInfo(ref info)) return;
        if (!IsHookSilent(info.Time, (uint)Volatile.Read(ref _lastHookEvent))) return;
        // 次の判定は再設置後から数え、同じ欠落で再設置を繰り返さない。
        Volatile.Write(ref _lastHookEvent, unchecked((int)info.Time));
        RequestReinstall();
    }

    internal void RequestReinstall()
    {
        Interlocked.Increment(ref _reconnects);
        Native.PostThreadMessage(_threadId, ReinstallMessage, 0, 0);
    }

    // GetTickCountの32bit値は約49日で一周するため、差を符号付きで比べる。
    internal static bool IsHookSilent(uint lastSystemInput, uint lastHookEvent) =>
        unchecked((int)(lastSystemInput - lastHookEvent)) >= HookSilenceMilliseconds;

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

    private void RunHookThread()
    {
        try
        {
            // PostThreadMessageを受けられるよう、フック設置前にメッセージキューを作る。
            _ = Native.PeekMessage(out _, 0, 0, 0, 0);
            InstallHooks();
            InstallFocusTracking();
            // 開始ホットキーを押したままの状態でも左右の修飾キーを正しく引き継ぐ。
            foreach (int key in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
                if (Native.IsKeyDown(key)) _buffer.KeyChanged(key, true, Native.GetForegroundWindow());
            MarkHookEvent();
        }
        catch (Exception error)
        {
            ReleaseHooks();
            ReleaseFocusTracking();
            _started.TrySetException(error);
            return;
        }
        _started.TrySetResult(Native.GetCurrentThreadId());
        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            if (message.Message == ReinstallMessage) ReinstallHooks();
            else Native.DispatchMessage(in message);
        }
        Volatile.Write(ref _releaseError, ReleaseHooks());
        ReleaseFocusTracking();
    }

    private void InstallHooks()
    {
        nint module = Native.GetModuleHandle(null);
        if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "入力記録用モジュールを取得できません。");
        _keyboardHook = Native.SetWindowsHookEx(13, _keyboardCallback, module, 0);
        if (_keyboardHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "キーボード入力記録を開始できません。");
        _mouseHook = Native.SetWindowsHookEx(14, _mouseCallback, module, 0);
        if (_mouseHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "マウス入力記録を開始できません。");
    }

    /// <summary>前面ウィンドウとフォーカスの変化を受け取り、パスワード判定の区切りにする。</summary>
    private void InstallFocusTracking()
    {
        const uint outOfContext = 0; // WINEVENT_OUTOFCONTEXT
        _foregroundEventHook = Native.SetWinEventHook(0x0003, 0x0003, 0, _focusCallback, 0, 0, outOfContext); // EVENT_SYSTEM_FOREGROUND
        _focusEventHook = Native.SetWinEventHook(0x8005, 0x8005, 0, _focusCallback, 0, 0, outOfContext); // EVENT_OBJECT_FOCUS
        // 区切りを受け取れないと離れた後の判定で記録してしまうため、キーを記録しない側へ倒す。
        if (_foregroundEventHook == 0 || _focusEventHook == 0) _focusTrackingFailed = true;
    }

    private void FocusCallback(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposeRequested) return;
        try { MarkBoundary(_protection.Timeline.NextSequence()); }
        catch (Exception ex) { Interlocked.CompareExchange(ref _callbackFailure, ex, null); }
    }

    private void ReinstallHooks()
    {
        if (_disposeRequested) return;
        try
        {
            int error = ReleaseHooks();
            if (error != 0) throw new Win32Exception(error, "入力記録フックを再設置のために解除できません。");
            InstallHooks();
            MarkHookEvent();
        }
        catch (Exception ex) { Interlocked.CompareExchange(ref _callbackFailure, ex, null); }
    }

    private void MarkHookEvent() => Volatile.Write(ref _lastHookEvent, unchecked((int)Native.GetTickCount()));

    private nint KeyboardCallback(int code, nuint message, nint data)
    {
        if (code >= 0 && !_disposeRequested)
        {
            MarkHookEvent();
            try
            {
                if (message is 0x100 or 0x104 or 0x101 or 0x105)
                {
                    var input = Marshal.PtrToStructure<Native.KeyboardInput>(data);
                    int key = (int)input.VirtualKey;
                    bool pressed = message is 0x100 or 0x104;
                    long sequence = _protection.Timeline.NextSequence();
                    _buffer.KeyChanged(key, pressed, Native.GetForegroundWindow(), sequence);
                    if (pressed)
                    {
                        if (_buffer.IsFocusBoundary(key)) _protection.Timeline.Boundary(sequence);
                        // 押した後に始まる保護判定でだけ記録するため、押すたびに判定を起こす。
                        _protection.Wake();
                        if (InputBuffer.GetKeyName(key) is { } name) Detected(_buffer.Describe(name), sequence, key: true);
                    }
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
            MarkHookEvent();
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
                {
                    bool pressed = message is 0x201 or 0x204 or 0x207 or 0x20B;
                    _buffer.MouseButtonChanged(action, pressed, point, foreground);
                    if (pressed)
                    {
                        // クリックでフォーカスが移るため、押す前のキーと押した後のキーを別の区間で判定する。
                        long sequence = _protection.Timeline.NextSequence();
                        MarkBoundary(sequence);
                        Detected(_buffer.Describe(action), sequence, key: false);
                    }
                }
                else if (message is 0x20A or 0x20E)
                {
                    _buffer.WheelMoved(message == 0x20E, unchecked((short)(input.MouseData >> 16)), point, foreground);
                    bool positive = unchecked((short)(input.MouseData >> 16)) > 0;
                    Detected(_buffer.Describe(message == 0x20E ? positive ? "Wheel-Right" : "Wheel-Left" : positive ? "Wheel-Up" : "Wheel-Down"),
                        _protection.Timeline.NextSequence(), key: false);
                }
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref _callbackFailure, ex, null); }
        }
        return Native.CallNextHookEx(0, code, message, data);
    }

    /// <summary>どのスレッドからでも呼べる。フック用スレッドが解除を終えるまで待つ。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _disposeRequested = true;
        if (!_started.Task.IsCompletedSuccessfully) return;
        if (!Native.PostThreadMessage(_threadId, QuitMessage, 0, 0) && _thread.IsAlive)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "入力記録フックの終了を要求できません。");
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("入力記録フックの終了を確認できません。");
        _protection.Dispose();
        _detected.Clear();
        int error = Volatile.Read(ref _releaseError);
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

    // 低レベルフックの再設置では外さず、フック用スレッドの終了時だけ解除する。
    private void ReleaseFocusTracking()
    {
        if (_foregroundEventHook != 0 && Native.UnhookWinEvent(_foregroundEventHook)) _foregroundEventHook = 0;
        if (_focusEventHook != 0 && Native.UnhookWinEvent(_focusEventHook)) _focusEventHook = 0;
        GC.KeepAlive(_focusCallback);
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint HookProc(int code, nuint message, nint data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
        [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { internal int X; internal int Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct NativeRectangle
        { internal int Left; internal int Top; internal int Right; internal int Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
        { internal uint VirtualKey; internal uint ScanCode; internal uint Flags; internal uint Time; internal nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        { internal NativePoint Position; internal uint MouseData; internal uint Flags; internal uint Time; internal nuint ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] internal struct NativeMessage
        { internal nint Window; internal uint Message; internal nuint WParam; internal nint LParam; internal uint Time; internal NativePoint Point; internal uint Private; }
        [StructLayout(LayoutKind.Sequential)] internal struct LastInputInfo { internal uint Size; internal uint Time; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint GetModuleHandle(string? moduleName);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] internal static extern uint GetTickCount();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetWindowsHookEx(int hookId, HookProc callback, nint module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
        [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DispatchMessage(in NativeMessage message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetLastInputInfo(ref LastInputInfo info);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsChild(nint parent, nint child);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        internal static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    }
}
