using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CapchunScreen.Input;

internal enum InputProtectionVerdict { Allowed, Protected, Pending }

/// <summary>
/// キー操作がパスワード入力中だったかを、観測順の連番で判定する時系列。
/// ShortcutKeyCounterのSecurity Gateと同じく、判定できないときは記録しない（Fail Closed）。
/// </summary>
/// <remarks>
/// キーは「押した後に始まった保護判定」が記録可を返した場合だけ記録する。フォーカスを動かし得る操作
/// （フォーカス・前面ウィンドウの変更、クリック、Tab・Enterや修飾キー付きのキー）を区切りとし、
/// キーと次の区切りの間に判定が始まらなかったキーは、パスワード欄から離れた後の判定しかないため記録しない。
/// </remarks>
internal sealed class InputProtectionTimeline(Func<long>? clock = null)
{
    // 判定を待つ上限。超えたキーは判定不能として記録しない。
    internal const long PendingLimitMilliseconds = 1000;
    private const long RetentionMilliseconds = 10_000;
    private readonly object _gate = new();
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly List<(long Sequence, long Time)> _boundaries = [];
    private readonly List<(long Sequence, long Time, bool Protected)> _evaluations = [];
    private long _sequence;

    /// <summary>入力・区切り・判定開始の観測順を表す連番を払い出す。</summary>
    internal long NextSequence() => Interlocked.Increment(ref _sequence);

    internal long Now => _clock();

    internal void Boundary(long sequence)
    {
        lock (_gate)
        {
            // 観測と記録の間に別スレッドが割り込んでも、連番順に並べる。
            int index = _boundaries.FindLastIndex(item => item.Sequence < sequence) + 1;
            _boundaries.Insert(index, (sequence, _clock()));
            Prune();
        }
    }

    internal void EvaluationCompleted(long startSequence, bool isProtected)
    {
        lock (_gate)
        {
            int index = _evaluations.FindLastIndex(item => item.Sequence < startSequence) + 1;
            _evaluations.Insert(index, (startSequence, _clock(), isProtected));
            Prune();
            Monitor.PulseAll(_gate);
        }
    }

    internal InputProtectionVerdict Classify(long sequence, long time)
    {
        lock (_gate)
        {
            long from = long.MinValue, until = long.MaxValue;
            foreach (var boundary in _boundaries)
            {
                if (boundary.Sequence <= sequence) from = boundary.Sequence;
                else { until = boundary.Sequence; break; }
            }
            bool? before = null, after = null;
            foreach (var evaluation in _evaluations)
            {
                if (evaluation.Sequence <= from) continue;
                if (evaluation.Sequence >= until) break;
                if (evaluation.Sequence < sequence) before = evaluation.Protected;
                else if (after is null) after = evaluation.Protected;
            }
            // 同じ区切りの中で直前に保護中だった場合は、後の判定が記録可でも記録しない。
            if (before == true || after == true) return InputProtectionVerdict.Protected;
            if (after == false) return InputProtectionVerdict.Allowed;
            if (until != long.MaxValue) return InputProtectionVerdict.Protected;
            return _clock() - time < PendingLimitMilliseconds ? InputProtectionVerdict.Pending : InputProtectionVerdict.Protected;
        }
    }

    /// <summary>指定した連番より後に始まった判定が完了するまで待つ。</summary>
    internal bool WaitForEvaluationAfter(long sequence, int timeoutMilliseconds)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        lock (_gate)
        {
            while (!_evaluations.Exists(item => item.Sequence > sequence))
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || !Monitor.Wait(_gate, (int)remaining)) return _evaluations.Exists(item => item.Sequence > sequence);
            }
            return true;
        }
    }

    private void Prune()
    {
        long cutoff = _clock() - RetentionMilliseconds;
        // 判定の区間の始点に使うため、保持期間より古い区切りも最後の1件は残す。
        while (_boundaries.Count > 1 && _boundaries[1].Time < cutoff) _boundaries.RemoveAt(0);
        long oldestBoundary = _boundaries.Count == 0 ? long.MinValue : _boundaries[0].Sequence;
        while (_evaluations.Count > 1 && _evaluations[0].Time < cutoff && _evaluations[0].Sequence < oldestBoundary) _evaluations.RemoveAt(0);
    }
}

/// <summary>前面の入力がパスワード等の保護対象か調べる。例外は判定不能として保護扱いにする。</summary>
internal interface IInputProtectionInspector
{
    bool IsProtected();
}

/// <summary>
/// ShortcutKeyCounterの判定と同じ条件をWindows UI Automationで調べる。
/// 自分より高い権限の前面プロセス、パスワード属性のフォーカス要素、前面ウィンドウ内のパスワード属性要素のいずれかで保護する。
/// ウィンドウ名や入力値は取得・保持しない。
/// </summary>
/// <remarks>
/// ウィンドウ全体の探索は相手アプリのUIスレッドで全要素を調べるため重い。入力のたびに行うと、一覧の多い画面では
/// 相手アプリが応答しなくなる。フォーカス要素の確認は毎回行い、全体の探索は前面ウィンドウが変わったときと
/// <see cref="WindowSearchIntervalMilliseconds"/>ごとに限る。
/// </remarks>
internal sealed class WindowsInputProtectionInspector : IInputProtectionInspector
{
    internal const long WindowSearchIntervalMilliseconds = 5000;
    private static readonly Condition PasswordCondition = new PropertyCondition(AutomationElement.IsPasswordProperty, true);
    private readonly bool _appIsElevated = ProcessElevation.IsElevated((uint)Environment.ProcessId);
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private nint _searchedWindow;
    private long _searchedAt;
    private bool _windowHasPassword;

    public bool IsProtected()
    {
        nint foreground = Native.GetForegroundWindow();
        if (foreground == 0) return true;
        if (Native.GetWindowThreadProcessId(foreground, out uint processId) == 0) return true;
        // 本アプリの画面にパスワード欄はない。自分のUIスレッドを判定で塞がないよう、UI Automationを呼ばない。
        if (processId == _ownProcessId) return false;
        if (!_appIsElevated && ProcessElevation.IsElevated(processId)) return true;
        // フォーカス要素は前面ウィンドウのツリー外にあることもあるため、独立して調べる。
        if (AutomationElement.FocusedElement is { } focused && focused.Current.IsPassword) return true;
        long now = Environment.TickCount64;
        if (foreground != _searchedWindow || now - _searchedAt >= WindowSearchIntervalMilliseconds)
        {
            // 探索中の失敗は例外として返し、古い結果を使わない。
            _searchedWindow = 0;
            AutomationElement root = AutomationElement.FromHandle(foreground);
            _windowHasPassword = root.Current.IsPassword || root.FindFirst(TreeScope.Descendants, PasswordCondition) is not null;
            _searchedWindow = foreground;
            _searchedAt = now;
        }
        return _windowHasPassword;
    }

    private static class Native
    {
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    }
}

internal static class ProcessElevation
{
    /// <summary>プロセスが昇格済みか返す。調べられない場合は例外にして、呼出側で判定不能として扱う。</summary>
    internal static bool IsElevated(uint processId)
    {
        nint process = Native.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (process == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "前面プロセスの権限を確認できません。");
        try
        {
            if (!Native.OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out nint token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "前面プロセスの権限を確認できません。");
            try
            {
                if (!Native.GetTokenInformation(token, 20 /* TokenElevation */, out int elevated, sizeof(int), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "前面プロセスの権限を確認できません。");
                return elevated != 0;
            }
            finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(process); }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(nint process, uint access, out nint token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(nint token, int informationClass, out int information, int length, out int returned);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    }
}

/// <summary>
/// 保護判定を専用スレッドで繰り返す。入力やフォーカスの変化で直ちに判定し、変化がなくても1秒ごとに判定する。
/// UI Automationの呼出しをフック用スレッドやUIスレッドで行わず、入力の遅延やフックの除去を避ける。
/// </summary>
internal sealed class InputProtectionMonitor : IDisposable
{
    internal const int PeriodicMilliseconds = 1000;
    private readonly InputProtectionTimeline _timeline;
    private readonly Func<IInputProtectionInspector> _inspectorFactory;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private int _unavailable;

    internal InputProtectionMonitor(Func<IInputProtectionInspector>? inspectorFactory = null, InputProtectionTimeline? timeline = null)
    {
        _inspectorFactory = inspectorFactory ?? (() => new WindowsInputProtectionInspector());
        _timeline = timeline ?? new InputProtectionTimeline();
        _thread = new Thread(Run) { IsBackground = true, Name = "CapchunWindow input protection" };
        _thread.Start();
    }

    internal InputProtectionTimeline Timeline => _timeline;

    /// <summary>判定機能そのものを準備できず、すべてのキーを記録しない状態か。</summary>
    internal bool Unavailable => Volatile.Read(ref _unavailable) != 0;

    /// <summary>入力やフォーカスの変化を知らせ、待たずに次の判定を始めさせる。</summary>
    internal void Wake() => _wake.Set();

    private void Run()
    {
        IInputProtectionInspector? inspector = null;
        try { inspector = _inspectorFactory(); }
        catch (Exception) { Volatile.Write(ref _unavailable, 1); }
        while (!_stopping)
        {
            long start = _timeline.NextSequence();
            bool isProtected;
            try { isProtected = inspector?.IsProtected() ?? true; }
            catch (Exception) { isProtected = true; } // 要素の消失や応答なしも判定不能として記録しない。
            _timeline.EvaluationCompleted(start, isProtected);
            _wake.WaitOne(PeriodicMilliseconds);
        }
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _wake.Set();
        // UI Automationの応答待ちで終われない場合も、背景スレッドとして録画終了を妨げない。
        if (_thread.Join(TimeSpan.FromSeconds(3))) _wake.Dispose();
    }
}
