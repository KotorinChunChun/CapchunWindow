using System.Reflection;
using CapchunScreen.Input;
using CapchunScreen.Storage;
using CapchunScreen.UI;

namespace CapchunScreen.Tests;

internal static class InputSettingsTests
{
    internal static void Run(Action<bool, string> check, string output)
    {
        var bounds = new Rectangle(0, 0, 400, 300);
        InputSample Read(InputBuffer buffer) => buffer.Consume(bounds, new Point(20, 20), 10, 10, (a, b) => a == b);
        var rules = new Dictionary<string, bool> { ["A"] = true, ["Ctrl+C"] = false, ["Ctrl+L-Click"] = false, ["Wheel-Up"] = false, ["Ctrl+Shift+F9"] = true };
        var input = new InputBuffer(false, rules);
        input.KeyChanged(0x41, true, 10); input.KeyChanged(0x41, false, 10);
        check(Read(input).Keys == "A", "個別の有効指定は単独文字の既定OFFに優先する");
        input.KeyChanged(0x11, true, 10); input.KeyChanged(0x43, true, 10);
        check(Read(input).Keys == "None", "無効なショートカットは押下中もTSVへ出さない");
        input.KeyChanged(0x43, false, 10);
        input.MouseButtonChanged("L-Click", true, new Point(20, 20), 10);
        check(Read(input).Click == "None", "修飾キー付きマウス操作の個別OFFを押下中にも適用する");
        input.MouseButtonChanged("L-Click", false, new Point(20, 20), 10); input.KeyChanged(0x11, false, 10);
        input.MouseButtonChanged("L-Click", true, new Point(20, 20), 10); input.MouseButtonChanged("L-Click", false, new Point(20, 20), 10);
        check(Read(input).Click == "L-Click", "Ctrl付きクリックを無効にしても単独クリックは記録する");
        input.WheelMoved(false, 120, new Point(20, 20), 10);
        input.WheelMoved(false, -120, new Point(20, 20), 10);
        check(Read(input).Click == "Wheel-Down", "ホイールを方向ごとに選別し他の方向を保持する");
        input.KeyChanged(0x11, true, 10); input.KeyChanged(0x10, true, 10); input.KeyChanged(0x78, true, 10);
        check(Read(input).Keys == "None", "個別設定で有効にしても録画ホットキー自身は記録しない");
        check(InputShortcutCatalog.Normalize("alt+CTRL+c") == "Ctrl+Alt+C" && InputShortcutCatalog.Normalize("Ctrl+Ctrl+A") is null,
            "旧設定の表記を正規化し重複修飾キーを拒否する");
        string directory = Path.Combine(output, "input-settings"); Directory.CreateDirectory(directory);
        string legacy = Path.Combine(directory, "shortcuts.tsv");
        File.WriteAllText(legacy, "Key\tRecordEnabled\tPlayEnabled\nCtrl+C\t0\t1\nA\t1\t0\nCtrl+Shift+F9\t1\t1\n");
        string before = File.ReadAllText(legacy);
        var imported = InputShortcutCatalog.ImportRecording(legacy);
        check(imported.Count == 2 && !imported["Ctrl+C"] && imported["A"] && before == File.ReadAllText(legacy),
            "旧TSVの記録列だけを読み、予約キーと再生列を使わず原本を保持する");
        File.WriteAllText(legacy, "Key\tEnabled\nCtrl+V\t0\n");
        check(!InputShortcutCatalog.ImportRecording(legacy)["Ctrl+V"], "旧Enabled形式も取り込める");
        string description = Path.Combine(directory, "アプリ.tsv");
        File.WriteAllText(description, "Ctrl+C\t\"コピー\t選択範囲\"\nCtrl+V\t貼り付け\n");
        var descriptions = InputShortcutCatalog.ImportDescriptions(description);
        check(descriptions["Ctrl+C"] == "コピー\t選択範囲", "アプリ説明の引用符とタブを正しく読み込む");
        File.WriteAllText(legacy, "Key\tRecordEnabled\nCtrl+C\tmaybe\n");
        bool rejected = false;
        try { _ = InputShortcutCatalog.ImportRecording(legacy); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "不正な記録設定を黙って有効化しない");

        var original = new AppSettings { InputRecordOverrides = rules, InputDescriptions = new() { ["アプリ"] = descriptions } };
        using (var dialog = new InputSettingsForm(original))
        {
            dialog.CreateControl(); _ = dialog.Handle;
            foreach (Control child in Descendants(dialog)) { child.CreateControl(); _ = child.Handle; child.PerformLayout(); }
            check(dialog.Text == WindowTitle.Create("操作記録の設定") && dialog is BrandedForm, "操作記録画面に共通タイトルとアイコンを使う");
            var controls = Descendants(dialog).ToArray();
            var search = controls.OfType<TextBox>().Single(control => control.AccessibleName == "操作記録を検索");
            search.Text = "Ctrl+C";
            var grid = controls.OfType<DataGridView>().Single();
            var copy = grid.Rows.Cast<DataGridViewRow>().Single(row => (string?)row.Cells[0].Value == "Ctrl+C");
            check(copy.Cells[1].Value is false && ((string?)copy.Cells[2].Value)?.Contains("コピー") == true, "検索で個別設定とアプリの説明を表示する");
            typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(
                controls.OfType<Button>().Single(button => button.Text == "表示分をすべて有効"), [EventArgs.Empty]);
            check(dialog.BuildSettings().InputRecordOverrides!["Ctrl+C"] && !rules["Ctrl+C"], "一括変更は作業用コピーだけへ反映する");
            check(ReferenceEquals(dialog.Settings, original), "保存前の変更・キャンセルは元の設定を変更しない");
            check(!dialog.Visible && !dialog.Testing, "操作記録画面の生成だけでは表示・入力監視を開始しない");
        }
        var store = new SettingsStore(Path.Combine(directory, "設定")); store.Save(original);
        var loaded = store.Load(out var warning);
        check(warning is null && loaded.InputRecordOverrides!["A"] && !loaded.InputRecordOverrides["Ctrl+C"]
            && loaded.InputDescriptions!["アプリ"]["Ctrl+V"] == "貼り付け", "操作ごとの設定とアプリ説明を保存・再読込できる");
        check(InputShortcutCatalog.All.Select(item => item.Combination).Distinct().Count() == InputShortcutCatalog.All.Count,
            "操作一覧にはキーの組み合わせの重複がない");
    }

    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
}
