using CapchunScreen.Capture;

namespace CapchunScreen.UI;

internal sealed class ThumbnailPickerForm : BrandedForm
{
    private readonly Func<IReadOnlyList<CaptureTarget>> windows;
    private readonly ThumbnailCatalog catalog;
    private readonly string? current;
    private readonly bool passive;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBox search = new() { Dock = DockStyle.Fill, AccessibleName = "サムネイル検索", PlaceholderText = "タイトルまたは実行ファイル名" };
    private readonly Button close = new() { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label count = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Panel viewport = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TableLayoutPanel grid = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4) };
    private readonly System.Windows.Forms.Timer captureTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer catalogTimer = new() { Interval = 2000 };
    private readonly ToolTip tooltip = new();
    private IReadOnlyList<ThumbnailTile> filtered = [];
    private Task? refreshTask;
    private bool stopped;
    internal CaptureTarget? SelectedEntry { get; private set; }
    internal string FilterText => search.Text;

    internal ThumbnailPickerForm(Func<IReadOnlyList<CaptureTarget>> windows, IThumbnailCapture capture, string? current,
        string filter, bool passive = false)
    {
        this.windows = windows;
        catalog = new ThumbnailCatalog(capture);
        this.current = current;
        this.passive = passive;
        Text = WindowTitle.Create("サムネイル選択");
        Font = new Font("Yu Gothic UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(640, 400);
        ClientSize = new Size(1000, 680);
        ShowInTaskbar = false;
        if (passive) { Opacity = 0; TopMost = false; }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        top.Controls.Add(new Label { Text = "検索", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        top.Controls.Add(search, 1, 0); top.Controls.Add(close, 2, 0);
        viewport.Controls.Add(grid);
        layout.Controls.Add(top, 0, 0); layout.Controls.Add(count, 0, 1); layout.Controls.Add(viewport, 0, 2);
        Controls.Add(layout);
        CancelButton = close;
        close.Click += (_, _) => Close();
        search.Text = filter;
        search.TextChanged += (_, _) => ApplyFilter();
        viewport.Resize += (_, _) => ArrangeCards();
        captureTimer.Tick += (_, _) => { if (refreshTask is null || refreshTask.IsCompleted) refreshTask = RefreshOneAsync(); };
        catalogTimer.Tick += (_, _) => RefreshEntries();
        Shown += (_, _) => { RefreshEntries(); captureTimer.Start(); catalogTimer.Start(); if (!passive) search.Focus(); };
        FormClosed += (_, _) => Stop();
    }

    private void RefreshEntries()
    {
        if (stopped) return;
        try
        {
            catalog.UpdateEntries(windows());
            ApplyFilter();
        }
        catch (Exception ex) { count.Text = "候補を取得できません: " + ex.Message; }
    }

    private void ApplyFilter()
    {
        if (stopped) return;
        var matches = catalog.Filter(search.Text);
        bool same = filtered.Select(tile => tile.Entry.Id).SequenceEqual(matches.Select(tile => tile.Entry.Id));
        filtered = matches;
        count.Text = filtered.Count == 0 ? "該当する対象がありません。検索条件を変更してください。" : $"{filtered.Count}件。画像をクリックすると対象を確定します。";
        if (same)
        {
            foreach (ThumbnailCard card in grid.Controls) { card.AccessibleName = card.Tile.Entry.Name; tooltip.SetToolTip(card, card.Tile.Entry.Name); card.Invalidate(); }
            return;
        }
        grid.SuspendLayout();
        try
        {
            foreach (Control card in grid.Controls.Cast<Control>().ToArray()) { tooltip.SetToolTip(card, null); card.Dispose(); }
            grid.Controls.Clear();
            foreach (var tile in filtered)
            {
                var card = new ThumbnailCard(tile, tile.Entry.Id == current) { Font = Font };
                card.Click += (_, _) => SelectEntry(card.Tile.Entry);
                tooltip.SetToolTip(card, tile.Entry.Name);
                grid.Controls.Add(card);
            }
            ArrangeCards();
        }
        finally { grid.ResumeLayout(true); }
    }

    private void ArrangeCards()
    {
        if (stopped || viewport.ClientSize.Width < 1) return;
        int width = Math.Max(1, viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - grid.Padding.Horizontal);
        int availableHeight = Math.Max(1, viewport.ClientSize.Height - grid.Padding.Vertical);
        int count = Math.Max(1, grid.Controls.Count);
        int maximumColumns = Math.Max(1, width / (220 * DeviceDpi / 96));
        int columns = Math.Min(Math.Min(maximumColumns, count),
            Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)count * width / (availableHeight * 1.4)))));
        int rows = Math.Max(1, (grid.Controls.Count + columns - 1) / columns);
        int cardHeight = Math.Max(190 * DeviceDpi / 96, availableHeight / rows);
        grid.SuspendLayout();
        try
        {
            grid.ColumnCount = columns;
            grid.RowCount = rows;
            grid.ColumnStyles.Clear(); grid.RowStyles.Clear();
            for (int i = 0; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            for (int i = 0; i < grid.RowCount; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, cardHeight));
            for (int i = 0; i < grid.Controls.Count; i++)
            {
                var card = grid.Controls[i];
                grid.SetCellPosition(card, new TableLayoutPanelCellPosition(i % columns, i / columns));
                card.Dock = DockStyle.Fill;
            }
        }
        finally { grid.ResumeLayout(true); }
    }

    private async Task RefreshOneAsync()
    {
        if (stopped) return;
        var visible = grid.Controls.Cast<ThumbnailCard>()
            .Where(card => viewport.ClientRectangle.IntersectsWith(viewport.RectangleToClient(card.RectangleToScreen(card.ClientRectangle))))
            .Select(card => card.Tile.Entry.Id).ToArray();
        var identities = filtered.Select(tile => tile.Entry.Id).ToArray();
        var updated = await catalog.RefreshNextAsync(visible, identities, DateTimeOffset.UtcNow, lifetime.Token);
        if (stopped || IsDisposed || updated is null) return;
        foreach (ThumbnailCard card in grid.Controls)
            if (card.Tile.Entry.Id == updated) { card.Invalidate(); break; }
    }

    private void SelectEntry(CaptureTarget entry)
    {
        if (stopped) return;
        try
        {
            if (!windows().Any(candidate => candidate.Id == entry.Id)) { RefreshEntries(); return; }
            SelectedEntry = entry;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { count.Text = "対象を選択できません: " + ex.Message; }
    }

    private void Stop()
    {
        if (stopped) return;
        stopped = true;
        captureTimer.Stop(); catalogTimer.Stop(); lifetime.Cancel(); catalog.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Stop(); captureTimer.Dispose(); catalogTimer.Dispose(); tooltip.Dispose(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }

    protected override bool ShowWithoutActivation => passive;
}
