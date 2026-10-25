using CapchunScreen.Input;

namespace CapchunScreen.Tests;

/// <summary>パスワード入力中のキーを記録・表示しない判定を、合成した時系列で検査する。実際の入力は使わない。</summary>
internal static class InputProtectionTests
{
    internal static void Run(Action<bool, string> check)
    {
        RunTimeline(check);
        RunBuffer(check);
        RunMonitor(check);
    }

    private static void RunTimeline(Action<bool, string> check)
    {
        long now = 1_000;
        var timeline = new InputProtectionTimeline(() => now);
        long Key() => timeline.NextSequence();
        void Evaluate(bool isProtected) => timeline.EvaluationCompleted(timeline.NextSequence(), isProtected);

        long typed = Key();
        check(timeline.Classify(typed, now) == InputProtectionVerdict.Pending, "押した後の保護判定が終わるまでキーを保留する");
        Evaluate(false);
        check(timeline.Classify(typed, now) == InputProtectionVerdict.Allowed, "押した後の判定がパスワード欄でなければ記録する");

        long secret = Key();
        Evaluate(true);
        check(timeline.Classify(secret, now) == InputProtectionVerdict.Protected, "押した後の判定がパスワード欄ならキーを記録しない");
        long next = Key();
        Evaluate(false);
        check(timeline.Classify(next, now) == InputProtectionVerdict.Protected,
            "同じ区間で直前に保護中だったキーは、後の判定が記録可でも記録しない");

        // パスワード欄で押した直後に、判定より先にTabで移動した場合。
        long leaving = Key();
        timeline.Boundary(timeline.NextSequence());
        Evaluate(false);
        check(timeline.Classify(leaving, now) == InputProtectionVerdict.Protected,
            "押した後の判定より先にフォーカスを動かしたキーは、移動後の判定では記録しない");
        long afterBoundary = Key();
        Evaluate(false);
        check(timeline.Classify(afterBoundary, now) == InputProtectionVerdict.Allowed, "区切りの後は新しい判定だけでキーを判定する");

        long waiting = Key();
        now += InputProtectionTimeline.PendingLimitMilliseconds;
        check(timeline.Classify(waiting, now - InputProtectionTimeline.PendingLimitMilliseconds) == InputProtectionVerdict.Protected,
            "判定が上限時間内に終わらないキーは判定不能として記録しない");

        long awaited = Key();
        var worker = new Thread(() => { Thread.Sleep(30); Evaluate(false); });
        worker.Start();
        bool completed = timeline.WaitForEvaluationAfter(awaited, 2_000);
        worker.Join();
        check(completed && timeline.Classify(awaited, now) == InputProtectionVerdict.Allowed, "フレーム内でキーの後の判定完了を待つ");
        check(!timeline.WaitForEvaluationAfter(Key(), 20), "判定が来なければ上限時間で待機をやめる");
    }

    private static void RunBuffer(Action<bool, string> check)
    {
        Rectangle bounds = new(0, 0, 200, 100);
        Point inside = new(10, 10);
        var verdicts = new Dictionary<long, InputProtectionVerdict>();
        InputProtectionVerdict Classify(long sequence, long time) => verdicts.GetValueOrDefault(sequence, InputProtectionVerdict.Allowed);
        var buffer = new InputBuffer(true);
        InputSample Read() => buffer.Consume(bounds, inside, 10, 10, (_, _) => true, null, Classify);
        void Tap(int key, long sequence) { buffer.KeyChanged(key, true, 10, sequence); buffer.KeyChanged(key, false, 10, sequence); }

        verdicts[1] = InputProtectionVerdict.Pending;
        Tap(0x41, 1);
        check(Read().Keys == "None" && buffer.LatestUnresolvedKeySequence == 1, "判定待ちのキーはそのフレームに出さず保持する");
        verdicts[1] = InputProtectionVerdict.Allowed;
        check(Read().Keys == "A" && buffer.LatestUnresolvedKeySequence is null, "判定が済んだら保留したキーを記録する");

        verdicts[2] = InputProtectionVerdict.Protected;
        Tap(0x42, 2);
        check(Read().Keys == "None" && buffer.LatestUnresolvedKeySequence is null && buffer.DroppedProtectedKeys,
            "パスワード入力中のキーは記録せず、保持もしない");
        verdicts[3] = InputProtectionVerdict.Protected;
        buffer.KeyChanged(0x25, true, 10, 3);
        check(Read().Keys == "None", "パスワード入力中に押し続けたキーも記録しない");
        buffer.KeyChanged(0x25, false, 10, 3);

        var outside = new InputBuffer(true);
        outside.KeyChanged(0x43, true, 30, 4); outside.KeyChanged(0x43, false, 30, 4);
        var outsideSample = outside.Consume(bounds, inside, 30, 10, (window, target) => window == target, null,
            (_, _) => InputProtectionVerdict.Protected);
        check(outsideSample.Keys == "None" && !outside.DroppedProtectedKeys && outside.LatestUnresolvedKeySequence is null,
            "録画対象の外のキーは保護判定にかけず、パスワード保護の注意も出さない");

        var plain = new InputBuffer();
        check(!plain.IsFocusBoundary(0x41) && plain.IsFocusBoundary(0x09) && plain.IsFocusBoundary(0x0D), "文字入力は区切りにせず、TabとEnterを区切りにする");
        plain.KeyChanged(0xA0, true, 10);
        check(!plain.IsFocusBoundary(0x41), "Shiftだけの文字入力は区切りにしない");
        plain.KeyChanged(0xA2, true, 10);
        check(plain.IsFocusBoundary(0x41), "Ctrl付きのキーはフォーカスを動かし得るため区切りにする");
    }

    private static void RunMonitor(Action<bool, string> check)
    {
        var failing = new InputProtectionTimeline();
        using (var monitor = new InputProtectionMonitor(() => throw new InvalidOperationException("意図的に準備できない判定"), failing))
        {
            long key = failing.NextSequence();
            monitor.Wake();
            bool evaluated = failing.WaitForEvaluationAfter(key, 2_000);
            check(evaluated && monitor.Unavailable && failing.Classify(key, failing.Now) == InputProtectionVerdict.Protected,
                "判定機能を準備できなければ、すべてのキーを記録しない");
        }

        var throwing = new InputProtectionTimeline();
        using (var monitor = new InputProtectionMonitor(() => new Inspector(() => throw new InvalidOperationException("要素が消えた")), throwing))
        {
            long key = throwing.NextSequence();
            monitor.Wake();
            check(throwing.WaitForEvaluationAfter(key, 2_000) && !monitor.Unavailable
                && throwing.Classify(key, throwing.Now) == InputProtectionVerdict.Protected, "判定中の例外は判定不能としてキーを記録しない");
        }

        var normal = new InputProtectionTimeline();
        using (var monitor = new InputProtectionMonitor(() => new Inspector(() => false), normal))
        {
            long key = normal.NextSequence();
            monitor.Wake();
            check(normal.WaitForEvaluationAfter(key, 2_000) && normal.Classify(key, normal.Now) == InputProtectionVerdict.Allowed,
                "保護対象がなければ専用スレッドの判定でキーを記録する");
        }
    }

    private sealed class Inspector(Func<bool> result) : IInputProtectionInspector
    {
        public bool IsProtected() => result();
    }
}
