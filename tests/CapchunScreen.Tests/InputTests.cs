using CapchunScreen.Input;
using System.Drawing;

namespace CapchunScreen.Tests;

public static class InputTests
{
    public static void Run(Action<bool, string> check)
    {
        Rectangle bounds = new(-100, 20, 200, 120);
        Point inside = new(-50, 30);
        static bool Matches(nint foreground, nint target) => target == 0 || foreground == target || foreground == 20 && target == 10;
        InputSample Read(InputBuffer buffer, Point? position = null, nint foreground = 10, nint target = 10) =>
            buffer.Consume(bounds, position ?? inside, foreground, target, Matches);
        static void Tap(InputBuffer buffer, int key, nint foreground = 10)
        { buffer.KeyChanged(key, true, foreground); buffer.KeyChanged(key, false, foreground); }

        var plain = new InputBuffer();
        Tap(plain, 0x41);
        check(Read(plain).Keys == "None", "単独文字を既定で記録しない");
        var optedIn = new InputBuffer(true);
        Tap(optedIn, 0x41);
        check(Read(optedIn).Keys == "A", "明示設定時は短い単独文字も記録する");
        check(Read(optedIn).Keys == "None", "消費済み入力を再出力しない");

        foreach (int shift in new[] { 0xA0, 0xA1 })
        {
            foreach (int key in new[] { 0x41, 0x31, 0xBA, 0x61 })
            {
                var shiftedText = new InputBuffer();
                shiftedText.KeyChanged(shift, true, 10);
                shiftedText.KeyChanged(key, true, 10);
                check(Read(shiftedText).Keys == "None", $"Shift単独修飾の通常文字を押下中も既定で記録しない {shift:X2}/{key:X2}");
                shiftedText.KeyChanged(key, false, 10);
                shiftedText.KeyChanged(shift, false, 10);
                Tap(shiftedText, key);
                check(Read(shiftedText).Keys == "None", $"Shiftを離した通常文字も既定で記録しない {shift:X2}/{key:X2}");
            }
        }

        var shiftedTap = new InputBuffer();
        shiftedTap.KeyChanged(0xA0, true, 10);
        Tap(shiftedTap, 0x41);
        shiftedTap.KeyChanged(0xA0, false, 10);
        check(Read(shiftedTap).Keys == "None", "フレーム間で離したShift文字も履歴に残さない");

        var shiftedOptIn = new InputBuffer(true);
        shiftedOptIn.KeyChanged(0xA0, true, 10);
        Tap(shiftedOptIn, 0x41);
        shiftedOptIn.KeyChanged(0xA0, false, 10);
        check(Read(shiftedOptIn).Keys == "Shift,A", "明示した通常文字記録ONはShift文字も記録する");

        foreach (var modifier in new[] { (Key: 0xA2, Expected: "Ctrl,Shift,A"), (Key: 0xA4, Expected: "Shift,Alt,A"), (Key: 0x5B, Expected: "Shift,Win,A") })
        {
            var shiftedShortcut = new InputBuffer();
            shiftedShortcut.KeyChanged(modifier.Key, true, 10);
            shiftedShortcut.KeyChanged(0xA0, true, 10);
            Tap(shiftedShortcut, 0x41);
            shiftedShortcut.KeyChanged(0xA0, false, 10);
            shiftedShortcut.KeyChanged(modifier.Key, false, 10);
            check(Read(shiftedShortcut).Keys == modifier.Expected, $"Shiftを併用する操作ショートカットを維持する {modifier.Expected}");
        }

        var shiftedFunction = new InputBuffer();
        shiftedFunction.KeyChanged(0xA0, true, 10);
        Tap(shiftedFunction, 0x70);
        check(Read(shiftedFunction).Keys == "Shift,F1", "Shiftと非文字キーのショートカットを維持する");

        var chord = new InputBuffer();
        chord.KeyChanged(0xA2, true, 10);
        Tap(chord, 0x43);
        chord.KeyChanged(0xA2, false, 10);
        check(Read(chord).Keys == "Ctrl,C", "フレーム間で離された修飾キー付き入力を保持する");
        check(Read(chord).Keys == "None", "離したキーを残さない");
        chord.KeyChanged(0xA2, true, 10);
        chord.KeyChanged(0xA3, true, 10);
        chord.KeyChanged(0xA2, false, 10);
        Tap(chord, 0x56);
        check(Read(chord).Keys == "Ctrl,V", "左右修飾キーの片側を離しても他方を保持する");

        var hotkey = new InputBuffer();
        hotkey.KeyChanged(0xA2, true, 10);
        hotkey.KeyChanged(0xA0, true, 10);
        Tap(hotkey, 0x78);
        check(Read(hotkey).Keys == "None", "Ctrl+Shift+F9と単独修飾キーを履歴から除外する");
        hotkey.KeyChanged(0xA0, false, 10);
        Tap(hotkey, 0x78);
        check(Read(hotkey).Keys == "Ctrl,F9", "停止用ではないCtrl+F9は記録する");

        foreach (var release in new[] { (First: 0xA0, Last: 0xA2, Name: "Shift先行"), (First: 0xA2, Last: 0xA0, Name: "Ctrl先行") })
        {
            var latched = new InputBuffer();
            latched.KeyChanged(0xA2, true, 10);
            latched.KeyChanged(0xA0, true, 10);
            latched.KeyChanged(0x78, true, 10);
            latched.KeyChanged(release.First, false, 10);
            check(Read(latched).Keys == "None", $"録画ホットキーは{release.Name}の解放後もF9を除外する");
            latched.KeyChanged(0x78, true, 10);
            check(Read(latched).Keys == "None", $"録画ホットキーは{release.Name}の解放後の自動リピートを除外する");
            latched.KeyChanged(release.Last, false, 10);
            latched.KeyChanged(0x78, true, 10);
            check(Read(latched).Keys == "None", $"{release.Name}から全修飾キー解放後もF9リピートを除外する");
            Tap(latched, 0x0D);
            check(Read(latched).Keys == "Enter", $"{release.Name}のF9抑制中も他キーは記録する");
            latched.KeyChanged(0x78, false, 10);
            Tap(latched, 0x78);
            check(Read(latched).Keys == "F9", $"{release.Name}からF9を離して押し直すと通常入力へ戻る");
        }
        var lateModifiers = new InputBuffer();
        lateModifiers.KeyChanged(0x78, true, 10);
        lateModifiers.KeyChanged(0xA2, true, 10);
        lateModifiers.KeyChanged(0xA0, true, 10);
        lateModifiers.KeyChanged(0xA0, false, 10);
        lateModifiers.KeyChanged(0xA2, false, 10);
        lateModifiers.KeyChanged(0x78, false, 10);
        check(Read(lateModifiers).Keys == "None", "同じF9押下が録画ホットキーになった場合は未消費履歴も除外する");

        var filtering = new InputBuffer();
        Tap(filtering, 0x0D, 30);
        check(Read(filtering).Keys == "None", "対象外のforegroundのキーを記録しない");
        Tap(filtering, 0x0D, 20);
        check(Read(filtering).Keys == "Enter", "対象のowned foregroundのキーを記録する");
        Tap(filtering, 0x09, 30);
        check(Read(filtering, target: 0).Keys == "Tab", "モニター録画でもスコープ判定を通したキーを記録する");
        Tap(filtering, 0x09, 30);
        var otherMonitor = filtering.Consume(bounds, inside, 30, 0, (foreground, _) => foreground == 10);
        check(otherMonitor.Keys == "None", "モニター録画で範囲外foregroundのキーを除外する");
        check(InputScope.ContainsWindowCenter(bounds, new(-150, 10, 200, 40)), "負座標のforeground中心が録画範囲内か判定する");
        check(!InputScope.ContainsWindowCenter(bounds, new(50, 20, 100, 40)), "foreground中心が右端境界なら除外する");
        check(!InputScope.ContainsWindowCenter(bounds, Rectangle.Empty), "取得できないforeground矩形を除外する");

        var held = new InputBuffer();
        held.KeyChanged(0x25, true, 10);
        check(Read(held).Keys == "Left" && Read(held).Keys == "Left", "押下中キーは次のフレームでも記録する");
        check(Read(held, foreground: 30).Keys == "None", "foregroundが対象外へ移ったら押下中キーを除外する");
        held.KeyChanged(0x25, false, 10);
        check(Read(held).Keys == "None", "キーを離した後は記録しない");

        var mouse = new InputBuffer();
        mouse.MouseButtonChanged("L-Click", true, inside, 10);
        mouse.MouseButtonChanged("L-Click", false, inside, 10);
        var click = Read(mouse, new(500, 500));
        check(click.Click == "L-Click" && click.X == 50 && click.Y == 10, "短いクリックは負座標画面の相対位置を保持する");
        var after = Read(mouse, new(500, 500));
        check(after.Click == "None" && after.X == -1 && after.Y == -1, "範囲外カーソルは座標とクリックを除外する");
        mouse.MouseButtonChanged("R-Click", true, new(100, 30), 10);
        mouse.MouseButtonChanged("R-Click", false, new(100, 30), 10);
        check(Read(mouse).Click == "None", "右端境界のクリックを記録範囲から除外する");

        var buttons = new InputBuffer();
        foreach (string action in new[] { "R-Click", "M-Click", "X1-Click", "X2-Click" })
        { buttons.MouseButtonChanged(action, true, inside, 10); buttons.MouseButtonChanged(action, false, inside, 10); }
        check(Read(buttons).Click == "R-Click,M-Click,X1-Click,X2-Click", "右・中・X1・X2の短いクリックを保持する");
        var wheels = new InputBuffer();
        wheels.WheelMoved(false, 120, inside, 10);
        wheels.WheelMoved(false, -120, inside, 10);
        wheels.WheelMoved(true, -120, inside, 10);
        wheels.WheelMoved(true, 120, inside, 10);
        check(Read(wheels).Click == "Wheel-Up,Wheel-Down,Wheel-Left,Wheel-Right", "縦横両方向ホイールを記録する");
        check(Read(wheels).Click == "None", "ホイールを一度だけ消費する");

        var modifiedMouse = new InputBuffer();
        modifiedMouse.KeyChanged(0xA2, true, 10);
        modifiedMouse.WheelMoved(false, 120, inside, 10);
        modifiedMouse.KeyChanged(0xA2, false, 10);
        check(Read(modifiedMouse).Keys == "Ctrl", "マウス操作時点の修飾キーを保持する");
        modifiedMouse.KeyChanged(0xA2, true, 30);
        modifiedMouse.WheelMoved(false, 120, inside, 30);
        var unrelatedMouse = Read(modifiedMouse, foreground: 30);
        check(unrelatedMouse.Click == "None" && unrelatedMouse.Keys == "None", "専用窓が覆われたとき対象外foregroundのマウス操作を除外する");
        modifiedMouse.MouseButtonChanged("L-Click", true, inside, 10);
        Read(modifiedMouse);
        check(Read(modifiedMouse, foreground: 30).Click == "None", "押下中マウスもforegroundが専用窓の対象外になれば除外する");
        modifiedMouse.MouseButtonChanged("L-Click", false, inside, 30);
        modifiedMouse.WheelMoved(false, 120, inside, 30);
        var monitorMouse = modifiedMouse.Consume(bounds, inside, 30, 0, (_, _) => false);
        check(monitorMouse.Click == "Wheel-Up" && monitorMouse.Keys == "None", "モニター録画の範囲内マウスはforeground中心によらず記録する");
        modifiedMouse.WheelMoved(true, 120, inside, 20);
        check(Read(modifiedMouse, foreground: 20).Click == "Wheel-Right", "専用窓のowned foregroundでマウスを記録する");
    }
}
