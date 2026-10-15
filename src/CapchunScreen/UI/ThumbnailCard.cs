using CapchunScreen.Capture;
using System.Drawing.Drawing2D;

namespace CapchunScreen.UI;

internal sealed class ThumbnailCard : Control
{
    internal ThumbnailTile Tile { get; }
    internal bool IsCurrent { get; }

    internal ThumbnailCard(ThumbnailTile tile, bool isCurrent)
    {
        Tile = tile;
        IsCurrent = isCurrent;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = tile.Entry.Name;
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(8);
        BackColor = Color.FromArgb(28, 31, 38);
        ForeColor = Color.WhiteSmoke;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; OnClick(EventArgs.Empty); }
        else base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = DeviceDpi / 96f;
        int titleHeight = (int)(44 * scale);
        var imageArea = new Rectangle(6, 6, Math.Max(1, Width - 12), Math.Max(1, Height - titleHeight - 12));
        if (Tile.Image is { } image)
        {
            double ratio = Math.Min((double)imageArea.Width / image.Width, (double)imageArea.Height / image.Height);
            int width = Math.Max(1, (int)(image.Width * ratio)), height = Math.Max(1, (int)(image.Height * ratio));
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            e.Graphics.DrawImage(image, imageArea.X + (imageArea.Width - width) / 2,
                imageArea.Y + (imageArea.Height - height) / 2, width, height);
        }
        if (Tile.Image is null || Tile.Status.Length != 0)
        {
            var textArea = Tile.Image is null ? imageArea : new Rectangle(imageArea.X, imageArea.Bottom - (int)(26 * scale), imageArea.Width, (int)(26 * scale));
            using var background = new SolidBrush(Color.FromArgb(210, 28, 31, 38));
            e.Graphics.FillRectangle(background, textArea);
            TextRenderer.DrawText(e.Graphics, Tile.Status, Font, textArea, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        TextRenderer.DrawText(e.Graphics, Tile.Entry.Name, Font,
            new Rectangle(6, Height - titleHeight, Math.Max(1, Width - 12), titleHeight - 4), ForeColor,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter);
        using var border = new Pen(IsCurrent || Focused ? Color.DodgerBlue : Color.FromArgb(65, 70, 80), IsCurrent || Focused ? 3 : 1);
        e.Graphics.DrawRectangle(border, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
    }
}
