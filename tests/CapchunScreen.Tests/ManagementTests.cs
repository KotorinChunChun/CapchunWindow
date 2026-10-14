using System.Reflection;

namespace CapchunScreen.Tests;

internal static class ManagementTests
{
    public static void Run(string directory)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new CapchunScreen.UI.MainForm(new CapchunScreen.Storage.SettingsStore(directory), new CapchunScreen.Storage.AppSettings(), null, passive: true);
                const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                var reason = window.GetType().GetProperty("ManagementStopBlockReason", hidden)!;
                if (reason.GetValue(window) is not null) throw new Exception("待機中の停止が拒否されました。");
                foreach (var name in new[] { "busy", "closing", "previewToggleBusy" })
                {
                    var flag = window.GetType().GetField(name, hidden)!;
                    flag.SetValue(window, true);
                    if (reason.GetValue(window) is not string { Length: > 0 }) throw new Exception(name + "中の管理停止を拒否しませんでした。");
                    flag.SetValue(window, false);
                }
                if (window.Visible || reason.GetValue(window) is not null) throw new Exception("検証後の状態が不正です。");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("管理停止のガード検証に失敗しました。", failure);
        Console.WriteLine("PASS 管理連携: 待機時の停止許可・3状態の停止拒否・画面非表示");
    }
}
