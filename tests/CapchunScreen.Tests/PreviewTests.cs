using CapchunScreen.UI;

namespace CapchunScreen.Tests;

public static class PreviewTests
{
    public static void Run(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        using var form = new PreviewForm();
        form.CreateControl();
        _ = form.Handle;
        foreach (Control child in form.Controls) { child.CreateControl(); _ = child.Handle; child.PerformLayout(); }
        form.PerformLayout();
        check(form is BrandedForm && form.Text == WindowTitle.Create("独立プレビュー"), "独立プレビューは共通のタイトル規則を使う");
        check(form.Icon is { Width: > 0, Height: > 0 }, "独立プレビューに埋め込みアプリアイコンがある");
        check(form.TopMost && form.FormBorderStyle == FormBorderStyle.None, "独立プレビューは最前面の枠なしウィンドウ");
        check(PreviewForm.FitSizeToWorkingArea(new Size(320, 180), new Size(960, 540)) == new Size(320, 180), "小さな独立映像は拡大せず1対1表示する");
        check(PreviewForm.FitSizeToWorkingArea(new Size(3840, 2160), new Size(960, 540)) == new Size(960, 540), "大きな独立映像は画面内へ縮小する");
        check(PreviewForm.FitSizeToWorkingArea(new Size(4000, 2000), new Size(900, 600)) == new Size(900, 450), "独立映像の縮小で縦横比を保つ");
        check(PreviewForm.KeepInsideWorkingArea(new Point(-800, -20), new Size(320, 180), new Rectangle(-1920, 0, 1920, 1040)) == new Point(-800, 0), "独立窓の位置補正は負座標モニターに対応する");
        check(PreviewForm.SnapLocation(new Point(131, 189), new Size(320, 180), new Rectangle(0, 0, 1920, 1040)) == new Point(140, 180), "独立窓の移動は20物理pxの格子へ丸める");
        check(PreviewForm.SnapLocation(new Point(-1811, -631), new Size(320, 180), new Rectangle(-1920, -720, 1920, 1040)) == new Point(-1820, -640), "負座標モニターの原点を基準に20pxへ丸める");
        check(PreviewForm.SnapLocation(new Point(9999, 9999), new Size(323, 187), new Rectangle(0, 0, 1920, 1040)) == new Point(1580, 840), "端でも20px格子を維持して画面の内側に収める");
        var picture = form.Controls.OfType<PictureBox>().Single();
        Bitmap first = new(64, 32);
        first.SetPixel(0, 0, Color.Lime);
        form.SetFrame(first);
        check(ReferenceEquals(picture.Image, first) && form.ClientSize == new Size(64, 32), "独立窓は渡された映像を所有して実寸を反映する");
        Rectangle work = Screen.FromPoint(form.Location).WorkingArea;
        form.Location = new Point(work.Left + 10, work.Top + 10);
        Point previousLocation = form.Location;
        Bitmap second = new(128, 96);
        form.SetFrame(second);
        check(IsDisposedImage(first) && ReferenceEquals(picture.Image, second), "独立窓は差し替えた旧Bitmapを解放する");
        check(form.Location == previousLocation && form.ClientSize == new Size(128, 96), "独立窓は映像寸法が変わっても位置を維持する");
        Rectangle originalBounds = form.Bounds;
        Rectangle monitorBounds = Screen.FromRectangle(originalBounds).Bounds;
        form.ToggleFullScreen();
        check(form.IsFullScreen && form.Bounds == monitorBounds && picture.BackColor == Color.Black && picture.SizeMode == PictureBoxSizeMode.Zoom,
            "独立窓は現在のモニター全体で黒背景と縦横比維持を使う");
        Bitmap fullScreenFrame = new(128, 96);
        form.SetFrame(fullScreenFrame);
        check(form.Bounds == monitorBounds, "全画面中の映像更新でウィンドウが縮まない");
        form.ToggleFullScreen();
        check(!form.IsFullScreen && form.Bounds == originalBounds, "全画面の解除で位置と寸法を復元する");
        form.ClearFrame();
        check(IsDisposedImage(second) && IsDisposedImage(fullScreenFrame) && picture.Image is null, "独立窓の待機復帰で表示Bitmapを解放する");
        Bitmap wrongThread = new(10, 10);
        bool rejected = Task.Run(() =>
        {
            try { form.SetFrame(wrongThread); return false; }
            catch (InvalidOperationException) { return true; }
        }).GetAwaiter().GetResult();
        check(rejected && IsDisposedImage(wrongThread), "独立窓は他スレッドの更新を拒否して受領Bitmapを解放する");
        Bitmap last = new(64, 32);
        form.SetFrame(last);
        check(!form.Visible, "独立プレビューの検査で画面を表示しない");
        form.Dispose();
        check(IsDisposedImage(last), "独立窓の破棄で最後のBitmapを解放する");
        Bitmap afterClose = new(64, 32);
        form.SetFrame(afterClose);
        check(IsDisposedImage(afterClose), "独立窓を閉じた後に届いたBitmapを解放する");
    }

    private static bool IsDisposedImage(Image image)
    {
        try { _ = image.Width; return false; }
        catch (Exception error) when (error is ArgumentException or ObjectDisposedException) { return true; }
    }
}
