using System.Diagnostics;
using CapchunScreen.VirtualCamera;

namespace CapchunScreen.UI;

internal sealed class UnityCaptureGuideForm : BrandedForm
{
    internal const string DownloadUrl = "https://github.com/schellingb/UnityCapture/archive/master.zip";
    internal const string Instructions = """
        1. 下の「公式配布先」を開き、GitHubの Code → Download ZIP で取得します。
        2. ZIPを展開し、Installフォルダーを今後も移動・削除しない場所へ置きます。
           一時フォルダーやダウンロード直後のZIP内からは実行しないでください。
        3. Installフォルダーの Install.bat を実行し、Unity Video Captureを登録します。
           登録に失敗する場合は、公式説明に従い管理者として実行します。
        4. カメラを使うアプリを再起動し、「Unity Video Capture」を選びます。
           CapchunScreenで対象を選び、「仮想カメラ開始」を押します。
        5. 導入後は下の「導入状態を再確認」を押してください。

        ファイルを移動・削除する前に、同じInstallフォルダーのUninstall.batで登録を解除します。
        このツールは先頭のデバイス（番号0）を使用します。通常はInstall.batの1台分で使えます。
        同じデバイスへ他の送信ツールから同時に配信しないでください。
        """;

    private readonly Label _state = new() { Dock = DockStyle.Fill, AutoEllipsis = true, AccessibleName = "導入状態", TextAlign = ContentAlignment.MiddleLeft };

    internal UnityCaptureGuideForm()
    {
        Text = WindowTitle.Create("Unity Capture 導入ガイド");
        Font = new Font("Yu Gothic UI", 10F);
        ClientSize = new(700, 500);
        MinimumSize = new(640, 470);
        StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14) };
        root.RowStyles.Add(new(SizeType.Absolute, 48));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 52));
        root.RowStyles.Add(new(SizeType.Absolute, 44));
        root.Controls.Add(new Label { Text = "Unity Captureを導入すると、取得映像を通話・配信アプリへ渡せます。\nこの画面から公式配布先と登録手順を確認できます。", Dock = DockStyle.Fill }, 0, 0);
        var instructions = new TextBox { Text = Instructions.ReplaceLineEndings(), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "Unity Captureの導入手順" };
        root.Controls.Add(instructions, 0, 1);
        root.Controls.Add(_state, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        AddButton(actions, "公式配布先", () => Open(UnityCaptureProvider.ProjectUrl));
        AddButton(actions, "ZIPを取得", () => Open(DownloadUrl));
        AddButton(actions, "導入状態を再確認", RefreshStatus);
        var close = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel, Height = 32 };
        actions.Controls.Add(close);
        CancelButton = close;
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
        Shown += (_, _) => { instructions.Select(0, 0); close.Focus(); };
        RefreshStatus();
    }

    private void RefreshStatus() => _state.Text = UnityCaptureProvider.Probe().Message;
    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void AddButton(Control parent, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32 };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception error) { _state.Text = $"操作できませんでした: {error.Message}"; }
        };
        parent.Controls.Add(button);
    }
}
