using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using CapchunScreen.Capture;
using CapchunScreen.Storage;
using CapchunScreen.UI;

namespace CapchunScreen.Tests;

internal static class PreviewSelectionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<bool, string> check, string root)
    {
        var probe = new CaptureProbe();
        using var form = new MainForm(new SettingsStore(Path.Combine(root, "preview-selection")), new AppSettings(), null,
            passive: true, frameSourceFactory: (target, _) => probe.Create(target.Id));
        _ = form.Handle;
        ComboBox targets = Field<ComboBox>(form, "target");
        Set(form, "refreshingTargets", true);
        targets.Items.Clear();
        foreach (string name in new[] { "first", "skipped", "latest" })
            targets.Items.Add(new CaptureTarget(name, name, CaptureKind.Monitor, 0, new Rectangle(0, 0, 32, 24)));
        targets.SelectedIndex = 0;
        Set(form, "refreshingTargets", false);
        check(probe.Started.IsEmpty, "非表示・passiveの初期選択では取得を開始しない");
        Set(form, "previewReady", true);
        Await((Task)typeof(MainForm).GetMethod("RestartPreviewAsync", Private)!.Invoke(form, null)!);
        Until(() => probe.Started.Contains("first"));
        check(!Field<CheckBox>(form, "independent").Checked && Field<Task?>(form, "previewTask") is { IsCompleted: false },
            "独立プレビューOFFでも選択対象の取得を開始する");
        targets.SelectedIndex = 1;
        targets.SelectedIndex = 2;
        Until(() => probe.Started.Contains("latest"));
        check(!probe.Started.Contains("skipped") && probe.MaximumActive == 1,
            "連続選択時は古い対象を破棄し、取得の終了後に最新対象だけを開始する");
        check(Field<Label>(form, "status").Text == "プレビュー中", "対象変更後は再操作せずプレビュー中になる");
        targets.Items.Clear();
        Await((Task)typeof(MainForm).GetMethod("RestartPreviewAsync", Private)!.Invoke(form, null)!);
        Until(() => Volatile.Read(ref probe.Active) == 0);
        check(Field<PictureBox>(form, "picture").Image is null && Volatile.Read(ref probe.Active) == 0,
            "対象がなくなったら取得と旧映像を回収する");
        var filterTimer = Field<System.Windows.Forms.Timer>(form, "filterTimer");
        var filter = Field<TextBox>(form, "filter");
        int startedBeforeTyping = probe.Started.Count;
        foreach (string text in new[] { "f", "fi", "fil" }) filter.Text = text;
        check(filterTimer.Enabled && probe.Started.Count == startedBeforeTyping, "検索欄の入力中は対象の再取得とプレビューの作り直しを待つ");
        typeof(MainForm).GetMethod("RefreshTargets", Private)!.Invoke(form, null);
        check(!filterTimer.Enabled, "対象を取得し直したら待機中の検索更新を取り消す");
        check(!form.Visible, "自動取得の回帰テストは合成映像だけを使い本体を表示しない");
    }

    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("プレビュー回帰検査が完了しません。");
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Application.DoEvents();
    }
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Private)!.GetValue(form)!;
    private static void Set(MainForm form, string name, object value) => typeof(MainForm).GetField(name, Private)!.SetValue(form, value);

    private sealed class CaptureProbe
    {
        internal ConcurrentQueue<string> Started { get; } = new();
        internal int Active;
        internal int MaximumActive;
        internal IFrameSource Create(string id)
        {
            int active = Interlocked.Increment(ref Active);
            MaximumActive = Math.Max(MaximumActive, active);
            Started.Enqueue(id);
            return new Source(this);
        }
        private sealed class Source(CaptureProbe owner) : IFrameSource
        {
            public CapturedFrame Capture(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = new Bitmap(32, 24);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Lime);
                return new(bitmap, new Rectangle(0, 0, 32, 24));
            }
            public void Dispose() => Interlocked.Decrement(ref owner.Active);
        }
    }
}
