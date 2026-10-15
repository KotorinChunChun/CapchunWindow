using CapchunScreen.Capture;

namespace CapchunScreen.UI;

internal sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer desktopTimer = new() { Interval = 500 };
    private bool desktopUnavailable;
    private bool checkingDesktop;
    private bool restoreIndependentAfterAccess;

    private void ConfigureDesktopMonitoring()
    {
        Shown += (_, _) => { if (!passive) desktopTimer.Start(); };
        desktopTimer.Tick += async (_, _) => await CheckDesktopAsync();
    }

    internal async Task CheckDesktopAsync()
    {
            if (checkingDesktop || closing || IsDisposed) return;
            checkingDesktop = true;
            try
            {
                bool available = desktopAccess.Check(force: true).Available;
                if (!available && !desktopUnavailable)
                {
                    desktopUnavailable = true; previewRevision++;
                    restoreIndependentAfterAccess = previewWindow is { Visible: true };
                    previewWindow?.Hide();
                    await StopPreviewAsync();
                    if (cameraRelay is not null && !cameraChanging)
                    {
                        var relay = cameraRelay; cameraRelay = null;
                        await relay.StopAsync(); cameraButton.Text = "仮想カメラ開始"; cameraStatus.Text = "アクセス中断・配信停止";
                    }
                    if (!busy) ShowError(new DesktopAccessException(DesktopAccessGuard.UnavailableMessage, desktopAccess.DiagnosticPath));
                }
                else if (available && desktopUnavailable)
                {
                    desktopUnavailable = false;
                    if (restoreIndependentAfterAccess && independent.Checked && previewWindow is { IsDisposed: false } window && modalDepth == 0)
                    { window.Show(); BringToFront(); }
                    restoreIndependentAfterAccess = false;
                    if (!busy) status.Text = "デスクトップへのアクセスが復旧しました。対象を選び直すか「プレビュー開始」で接続し直してください。録画・入力テストは自動再開しません。";
                }
            }
            catch (Exception error) { if (!IsDisposed) ShowError(error); }
            finally { checkingDesktop = false; }
    }
}
