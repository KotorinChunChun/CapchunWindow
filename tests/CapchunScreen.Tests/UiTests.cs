using System.Reflection;
using CapchunScreen.Storage;
using CapchunScreen.UI;

namespace CapchunScreen.Tests;

public static class UiTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run(Action<bool, string> check, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("UI構造検査はSTAスレッドで実行してください。");

        string root = Path.Combine(Path.GetFullPath(outputDirectory), $"ui-{Guid.NewGuid():N}");
        var settings = new AppSettings
        {
            OutputDirectory = Path.Combine(root, "未作成の録画先"),
            FfmpegPath = "", CountdownSeconds = 0, CaptureWindows = false
        };
        check(!Directory.Exists(settings.OutputDirectory), "UI検査の録画先は未作成から開始する");

        foreach (Size size in new[] { new Size(900, 720), new Size(740, 620) })
        {
            using var form = new MainForm(new SettingsStore(Path.Combine(root, $"settings-{size.Width}")), settings, null);
            form.Size = size;
            CreateHiddenHandle(form);
            CheckBrand(form, WindowTitle.Create(), "メイン画面", check);
            check(form.Size == size, $"メイン画面を{size.Width}×{size.Height}で検査する");
            var tabs = Descendants(form).OfType<TabControl>().Single();
            for (int tab = 0; tab < tabs.TabPages.Count; tab++)
            {
                tabs.SelectedIndex = tab;
                PerformLayoutTree(form);
                string label = $"{size.Width}×{size.Height}・{tabs.SelectedTab!.Text}";
                string[] required = tab == 0
                    ? ["対象の種類", "対象名検索", "録画対象", "対象を更新", "FPS", "画質", "保存形式", "開始までの秒数", "最長録画秒数", "録画対象プレビュー", "選択ウィンドウのみ（WGC）", "操作履歴をTSVへ保存", "独立プレビュー", "仮想カメラ開始", "導入ガイド", "仮想カメラ状態"]
                    : ["録画ファイル一覧", "再生", "複数再生…", "TSVを開く", "名前変更", "ごみ箱へ", "フォルダー", "更新"];
                string[] common = ["● 録画開始", "プレビュー開始", "設定", "録画状態", "保存先を開く"];
                var controls = Descendants(form).Where(IsOnSelectedPage).ToArray();
                string[] missing = required.Concat(common).Where(name => !controls.Any(control => HasName(control, name))).ToArray();
                check(missing.Length == 0, $"{label}に必須操作がある{Details(missing)}");
                CheckGeometry(form, label, check);
                if (tab == 0)
                {
                    var thumbnail = Field<PictureBox>(form, "picture");
                    var extra = Field<CheckBox>(form, "independent");
                    check(extra.PointToScreen(Point.Empty).Y >= thumbnail.PointToScreen(new Point(0, thumbnail.Height)).Y,
                        $"{label}で独立プレビュー等をサムネイルの下へ配置する");
                    var start = Field<NumericUpDown>(form, "countdown");
                    var max = Field<NumericUpDown>(form, "maximum");
                    check(start.PointToScreen(Point.Empty).Y == max.PointToScreen(Point.Empty).Y && max.PointToScreen(Point.Empty).X > start.PointToScreen(Point.Empty).X,
                        $"{label}で最長時間を開始までの右へ同じ行に配置する");
                    var help = Field<ToolTip>(form, "optionHelp");
                    check(help.GetToolTip(Field<CheckBox>(form, "exclusive"))?.Contains("重なっても") == true && help.GetToolTip(Field<CheckBox>(form, "recordInput"))?.Contains("TSV") == true,
                        "対象ウィンドウのみとTSV保存にツールチップがある");
                }
            }
            var recordButton = Field<Button>(form, "recordButton");
            recordButton.Text = "■ 録画停止";
            check(recordButton.AccessibilityObject.Name == recordButton.Text,
                $"{size.Width}×{size.Height}で読み上げ名が録画停止の表示に追従する");
            check(!form.Visible, $"{size.Width}×{size.Height}の構造検査で画面を表示しない");
            form.TopMost = true;
            bool modalTopMost = form.WithPreviewHidden(() => form.TopMost);
            check(!modalTopMost && form.TopMost, "モーダル操作中は本体の最前面を解除し、終了時に元へ戻す");
            bool failed = false;
            try { form.WithPreviewHidden<bool>(() => throw new IOException("意図的なダイアログ失敗")); }
            catch (IOException) { failed = true; }
            check(failed && form.TopMost && Field<int>(form, "modalDepth") == 0, "ダイアログの失敗時も最前面とモーダル状態を復元する");
            form.TopMost = false;
            check(!Field<Button>(form, "savedFolderButton").Enabled && form.SavedRecordingDirectory is null, "保存前は保存先ボタンを無効にする");
            string savedPath = Path.Combine(root, "実際の保存先", "検証.mp4");
            typeof(MainForm).GetMethod("SetSavedRecording", PrivateInstance)!.Invoke(form, [savedPath, 32.6]);
            typeof(MainForm).GetField("settings", PrivateInstance)!.SetValue(form, settings with { OutputDirectory = Path.Combine(root, "変更後の保存先") });
            check(Field<Button>(form, "savedFolderButton").Enabled && form.SavedRecordingDirectory == Path.GetDirectoryName(savedPath),
                "保存先設定を変えても最後に保存した動画のフォルダーを保持する");
        }

        using (var guide = new UnityCaptureGuideForm())
        {
            CreateHiddenHandle(guide);
            PerformLayoutTree(guide);
            CheckBrand(guide, WindowTitle.Create("Unity Capture 導入ガイド"), "導入ガイド", check);
            CheckGeometry(guide, "導入ガイド", check);
            var text = Descendants(guide).OfType<TextBox>().Single();
            check(Enumerable.Range(1, 5).All(number => text.Lines.Any(line => line.StartsWith($"{number}. ", StringComparison.Ordinal)))
                && text.Text.Contains(Environment.NewLine + "2. ", StringComparison.Ordinal),
                "導入ガイドはWindowsの複数行入力欄で各手順の改行を保持する");
            check(text.ReadOnly && text.Text.Contains("Install.bat", StringComparison.Ordinal)
                && text.Text.Contains("Uninstall.bat", StringComparison.Ordinal)
                && Descendants(guide).OfType<Button>().Any(button => button.Text == "導入状態を再確認"),
                "導入ガイドから登録・解除と再確認の操作を確認できる");
            check(!guide.Visible, "導入ガイドの構造検査では配布先やダイアログを開かない");
        }

        PreviewSelectionTests.Run(check, root);

        using (var dialog = new SettingsForm(settings))
        {
            CreateHiddenHandle(dialog);
            PerformLayoutTree(dialog);
            CheckBrand(dialog, WindowTitle.Create("設定"), "設定画面", check);
            CheckGeometry(dialog, "設定画面", check);
            var controls = Descendants(dialog).ToArray();
            check(controls.OfType<TextBox>().Any(control => control.AccessibleName == "録画の保存先")
                && controls.OfType<TextBox>().Any(control => control.AccessibleName == "FFmpegの実行ファイル"), "設定画面に保存先とFFmpegの入力欄がある");
            check(controls.OfType<CheckBox>().Single().Checked == settings.IncludePlainKeys, "単独文字記録の設定を画面へ反映する");
            check(dialog.AcceptButton is Button && dialog.CancelButton is Button { DialogResult: DialogResult.Cancel }, "設定画面のEnterとEscapeに確定・キャンセルを割り当てる");
            check(dialog.Settings == settings && !dialog.Visible, "設定画面の生成だけでは設定を変更・画面表示しない");
        }
        using (var dialog = new RenameForm("合成動画_名前変更前"))
        {
            CreateHiddenHandle(dialog);
            PerformLayoutTree(dialog);
            CheckBrand(dialog, WindowTitle.Create("名前変更"), "名前変更画面", check);
            CheckGeometry(dialog, "名前変更画面", check);
            check(dialog.NewBaseName == "合成動画_名前変更前", "名前変更画面の生成だけでは名前を変更しない");
            check(dialog.AcceptButton is Button && dialog.CancelButton is Button { DialogResult: DialogResult.Cancel }, "名前変更画面のEnterとEscapeに確定・キャンセルを割り当てる");
            check(!dialog.Visible, "名前変更の構造検査で画面を表示しない");
        }

        CheckClosingRace(settings, new SettingsStore(Path.Combine(root, "closing-settings")), check);
        CheckClosingSaveFailure(settings, new SettingsStore(Path.Combine(root, "locked-settings")), check);
        check(!Directory.Exists(settings.OutputDirectory), "UI構造検査と終了処理で録画先を作成・録画しない");
    }

    private static void CreateHiddenHandle(Form form)
    {
        form.CreateControl();
        _ = form.Handle; // 非表示のまま、TabControl等のネイティブ寸法を確定する。
        PerformLayoutTree(form);
    }

    private static void PerformLayoutTree(Control root)
    {
        void Layout(Control control)
        {
            control.CreateControl();
            _ = control.Handle; // 非表示のTabPageも初期サイズではなく実際のnative配置で検査する。
            control.PerformLayout();
            foreach (Control child in control.Controls) Layout(child);
            control.PerformLayout();
        }
        for (int pass = 0; pass < 3; pass++) Layout(root);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static bool IsOnSelectedPage(Control control)
    {
        for (Control? current = control; current is not null; current = current.Parent)
            if (current is TabPage page && page.Parent is TabControl tabs && tabs.SelectedTab != page) return false;
        return true;
    }

    private static bool HasName(Control control, string name) => control.AccessibleName == name || control.Text == name;

    private static void CheckBrand(Form form, string expected, string label, Action<bool, string> check)
    {
        check(form is BrandedForm && form.Text == expected, $"{label}は共通のタイトル規則を使う");
        check(form.Icon is { Width: > 0, Height: > 0 }, $"{label}に埋め込みアプリアイコンがある");
    }

    private static void CheckGeometry(Form form, string label, Action<bool, string> check)
    {
        var controls = Descendants(form).Where(IsOnSelectedPage)
            .Where(control => control is ButtonBase or Label or TextBox or ComboBox or NumericUpDown or PictureBox or ListView)
            .Where(control => control.Parent is not UpDownBase).ToArray();
        List<string> outside = [];
        foreach (Control control in controls)
        {
            Rectangle bounds = control.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                outside.Add($"{ControlName(control)}の寸法が0");
                continue;
            }
            for (Control? parent = control.Parent; parent is not null; parent = parent.Parent)
            {
                Rectangle container = parent.ClientRectangle;
                container.Inflate(1, 1); // DPIの端数丸め1pxだけを許容する。
                if (!container.Contains(bounds))
                {
                    outside.Add($"{ControlName(control)}={bounds} が {parent.GetType().Name}={parent.ClientRectangle} の外");
                    break;
                }
                bounds.Offset(parent.Location);
            }
        }
        check(outside.Count == 0, $"{label}の操作と表示が親領域内に収まる{Details(outside)}");

        Button[] buttons = controls.OfType<Button>().ToArray();
        List<string> overlaps = [];
        for (int first = 0; first < buttons.Length; first++)
            for (int second = first + 1; second < buttons.Length; second++)
            {
                Rectangle intersection = Rectangle.Intersect(RelativeToForm(buttons[first], form), RelativeToForm(buttons[second], form));
                if (intersection.Width > 0 && intersection.Height > 0)
                    overlaps.Add($"{ControlName(buttons[first])} と {ControlName(buttons[second])}");
            }
        check(overlaps.Count == 0, $"{label}の操作ボタンが重ならない{Details(overlaps)}");
    }

    private static Rectangle RelativeToForm(Control control, Form form)
    {
        Rectangle bounds = control.Bounds;
        for (Control? parent = control.Parent; parent is not null && parent != form; parent = parent.Parent)
            bounds.Offset(parent.Location);
        return bounds;
    }

    private static string ControlName(Control control) => control.AccessibleName ?? (control.Text.Length > 0 ? control.Text : control.GetType().Name);
    private static string Details(IEnumerable<string> details)
    {
        string[] values = details.ToArray();
        return values.Length == 0 ? "" : $"：{string.Join(" / ", values)}";
    }

    private static void CheckClosingRace(AppSettings settings, SettingsStore store, Action<bool, string> check)
    {
        using var form = new MainForm(store, settings, null);
        CreateHiddenHandle(form);
        var previewCompletion = new TaskCompletionSource();
        var context = new SynchronousTestContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            typeof(MainForm).GetField("previewTask", PrivateInstance)!.SetValue(form, previewCompletion.Task);
            var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
            typeof(MainForm).GetMethod("ClosingAsync", PrivateInstance)!.Invoke(form, [form, closing]);
            check(closing.Cancel && Field<bool>(form, "closing") && Field<bool>(form, "busy"), "プレビュー停止待機中の終了処理はclosingとbusyを同期的に設定する");
            var toggle = (Task)typeof(MainForm).GetMethod("ToggleRecordingAsync", PrivateInstance)!.Invoke(form, null)!;
            check(toggle.IsCompletedSuccessfully && Field<object?>(form, "recordingCancellation") is null, "終了待機中の録画操作は即座に戻り新しい録画を開始しない");
            check(!File.Exists(store.FilePath), "プレビュー停止前は終了時の設定保存へ進まない");
        }
        finally
        {
            // 実メッセージループを回さず、同じSTAで未完了処理を解放してフォームを閉じる。
            try
            {
                previewCompletion.TrySetResult();
                context.Drain();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
        check(form.IsDisposed && File.Exists(store.FilePath), "停止待機の解放後は隔離した設定を保存しフォームを破棄する");
    }

    private static void CheckClosingSaveFailure(AppSettings settings, SettingsStore store, Action<bool, string> check)
    {
        store.Save(settings);
        using var form = new MainForm(store, settings, null);
        CreateHiddenHandle(form);
        // この検査が作った設定だけをロックし、終了時の保存失敗を再現する。
        using var lockedSettings = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
        typeof(MainForm).GetMethod("ClosingAsync", PrivateInstance)!.Invoke(form, [form, closing]);
        check(closing.Cancel && !form.IsDisposed && !Field<bool>(form, "closing") && !Field<bool>(form, "busy")
            && Field<Label>(form, "status").Text.StartsWith("エラー:", StringComparison.Ordinal),
            "終了時の設定保存失敗を通知し、画面と通常操作を復旧する");
        check(Field<CheckBox>(form, "independent").Enabled && Field<Button>(form, "cameraButton").Enabled
            && Field<Button>(form, "cameraButton").Text == "仮想カメラ開始",
            "終了時の保存失敗後も独立プレビューと仮想カメラを再操作できる");
    }

    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, PrivateInstance)!.GetValue(form)!;

    private sealed class SynchronousTestContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = [];

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_pending) _pending.Enqueue((callback, state));
        }

        internal void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_pending)
                {
                    if (_pending.Count == 0) return;
                    item = _pending.Dequeue();
                }
                item.Callback(item.State);
            }
        }
    }
}
