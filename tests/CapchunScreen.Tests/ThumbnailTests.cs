using System.Reflection;
using CapchunScreen.Capture;
using CapchunScreen.UI;

namespace CapchunScreen.Tests;

internal static class ThumbnailTests
{
    private static readonly CaptureTarget First = new("first", "検証A", CaptureKind.Window, 10, new Rectangle(0, 0, 320, 200), "AlphaApp");
    private static readonly CaptureTarget Second = new("second", "検証B", CaptureKind.Window, 20, new Rectangle(0, 0, 320, 200), "BetaApp");

    internal static void Run(Action<bool, string> check)
    {
        Task.Run(async () =>
        {
            var capture = new FakeCapture();
            using var catalog = new ThumbnailCatalog(capture);
            catalog.UpdateEntries([First, Second]);
            check(catalog.Filter("betaapp").Single().Entry.Id == Second.Id, "サムネイルは実行ファイル名でも絞り込める");
            check(capture.Calls == 0, "候補の列挙と検索だけでは撮影しない");
            var now = DateTimeOffset.UtcNow;
            var selected = await catalog.RefreshNextAsync([Second.Id], [First.Id, Second.Id], now, CancellationToken.None);
            check(selected == Second.Id && capture.Calls == 1, "サムネイルは画面内の候補を優先して1件だけ撮影する");
            var tile = catalog.Filter("検証B").Single(); Bitmap original = tile.Image!;
            check(original.Width == 10 && tile.Status == "", "サムネイル取得画像をメモリ内で保持する");
            selected = await catalog.RefreshNextAsync([Second.Id], [Second.Id], now.AddSeconds(1), CancellationToken.None);
            check(selected is null && capture.Calls == 1, "取得済みサムネイルの更新間隔を5秒空ける");
            capture.Fail = true;
            await catalog.RefreshNextAsync([Second.Id], [Second.Id], now.AddSeconds(6), CancellationToken.None);
            check(ReferenceEquals(original, tile.Image) && tile.Status.Contains("取得不可"), "取得失敗時は前の画像と失敗状態を保持する");
            catalog.UpdateEntries([First]);
            check(IsDisposed(original) && catalog.Filter("").Count == 1, "終了した候補の画像を解放して一覧から除く");

            var slow = new FakeCapture { Delay = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var closing = new ThumbnailCatalog(slow); closing.UpdateEntries([First]);
            var pending = closing.RefreshNextAsync([First.Id], [First.Id], now, CancellationToken.None);
            var duplicate = await closing.RefreshNextAsync([First.Id], [First.Id], now, CancellationToken.None);
            check(duplicate is null && slow.Calls == 1, "更新中の次回呼び出しで撮影を並列起動しない");
            closing.Dispose(); Bitmap late = new(10, 10); slow.Delay.SetResult(new(late, ""));
            await pending;
            check(IsDisposed(late) && slow.Disposed, "一覧を閉じた後に届いた画像と取得器も解放する");

            var source = new FakeSource();
            using var adapter = new FrameThumbnailCapture((_, exclusive) => { check(exclusive, "ウィンドウのサムネイルは専用取得を使う"); return source; });
            using var frame = await adapter.CaptureAsync(First, new Size(160, 100), CancellationToken.None);
            check(frame.Image?.Size == new Size(160, 80) && source.Disposed, "サムネイル縮小で縦横比を維持し取得資源を直ちに解放する");
        }).GetAwaiter().GetResult();

        var fake = new FakeCapture();
        using var picker = new ThumbnailPickerForm(() => [First, Second], fake, First.Id, "", passive: true);
        picker.CreateControl(); _ = picker.Handle;
        typeof(ThumbnailPickerForm).GetMethod("RefreshEntries", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(picker, null);
        var cards = Descendants(picker).OfType<ThumbnailCard>().ToArray();
        check(cards.Length == 2 && cards.Single(card => card.IsCurrent).Tile.Entry.Id == First.Id,
            "サムネイル選択は現在の対象を青枠のカードで示す");
        check(!picker.Visible && fake.Calls == 0 && picker.SelectedEntry is null, "選択画面の生成だけでは表示・撮影・対象変更をしない");
        var search = Descendants(picker).OfType<TextBox>().Single(); search.Text = "betaapp";
        cards = Descendants(picker).OfType<ThumbnailCard>().ToArray();
        check(cards.Length == 1 && cards[0].Tile.Entry.Id == Second.Id, "選択画面の検索は撮影を待たずに反映する");
        typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cards[0], [new KeyEventArgs(Keys.Enter)]);
        check(picker.SelectedEntry?.Id == Second.Id && picker.DialogResult == DialogResult.OK, "サムネイルのEnterで対象を確定する");
    }
    private static bool IsDisposed(Image image)
    { try { _ = image.Width; return false; } catch (Exception error) when (error is ArgumentException or ObjectDisposedException) { return true; } }
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private sealed class FakeCapture : IThumbnailCapture
    {
        internal int Calls; internal bool Fail; internal bool Disposed;
        internal TaskCompletionSource<ThumbnailCaptureResult>? Delay;
        public Task<ThumbnailCaptureResult> CaptureAsync(CaptureTarget target, Size size, CancellationToken token)
        {
            Calls++; if (Fail) throw new IOException("検証用の取得失敗");
            return Delay?.Task ?? Task.FromResult(new ThumbnailCaptureResult(new Bitmap(10, 10), ""));
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeSource : IFrameSource
    {
        internal bool Disposed;
        public CapturedFrame Capture(CancellationToken token) => new(new Bitmap(400, 200), new Rectangle(0, 0, 400, 200));
        public void Dispose() => Disposed = true;
    }
}
