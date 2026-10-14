using System.Diagnostics;

namespace CapchunScreen.CaptureSmoke;

/// <summary>録画の動きと入力を目視確認するための合成窓。入力・設定は永続化しない。</summary>
internal sealed class SceneForm : Form
{
    private readonly System.Windows.Forms.Timer animationTimer = new() { Interval = 67 };
    private readonly MotionPanel motion = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 6) };
    private readonly Label counter = new() { AutoSize = true, Text = "カウント: 0", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label keyboard = new() { AutoSize = false, Text = "最後のキー: なし", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label inputPreview = new() { AutoSize = false, Text = "入力: (空欄)", Dock = DockStyle.Fill, AutoEllipsis = true };
    private int count;
    private int keyCount;

    internal SceneForm()
    {
        Text = "録画テスト用シーン - CapchunScreen - ver 0.1.0";
        Name = "RecordingTestScene";
        AccessibleName = "録画テスト用シーン";
        ClientSize = new Size(420, 300);
        MinimumSize = new Size(340, 300);
        StartPosition = FormStartPosition.Manual;
        TopMost = false;
        KeyPreview = true;
        BackColor = Color.FromArgb(248, 252, 244);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6,
            BackColor = BackColor
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.Controls.Add(new Label { Text = "動く四角・クリック・キー入力の確認", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        layout.Controls.Add(motion, 0, 1);

        var controls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        var increment = new Button { Text = "カウント +1", Name = "IncrementCount", Dock = DockStyle.Fill, UseVisualStyleBackColor = true };
        increment.Click += (_, _) =>
        {
            counter.Text = $"カウント: {++count}";
            motion.Count = count;
            motion.Invalidate();
        };
        controls.Controls.Add(increment, 0, 0);
        controls.Controls.Add(counter, 1, 0);
        layout.Controls.Add(controls, 0, 2);

        var textInput = new TextBox
        {
            Name = "TestTextInput", AccessibleName = "テスト文字入力", PlaceholderText = "テスト文字だけを入力",
            Dock = DockStyle.Fill, MaxLength = 120
        };
        textInput.TextChanged += (_, _) => inputPreview.Text = "入力: " + (textInput.Text.Length == 0 ? "(空欄)" : textInput.Text);
        layout.Controls.Add(textInput, 0, 3);
        layout.Controls.Add(inputPreview, 0, 4);
        layout.Controls.Add(keyboard, 0, 5);
        Controls.Add(layout);

        KeyDown += (_, args) => keyboard.Text = $"最後のキー: {args.KeyData}  /  押下 {++keyCount} 回";
        animationTimer.Tick += (_, _) => motion.Invalidate();
        Shown += (_, _) =>
        {
            // 他アプリには触れず、実画面の左下1/4に収まる位置を選ぶ。
            var working = (Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
            Location = new Point(working.Left + 16, Math.Max(working.Top, working.Bottom - Height - 16));
            animationTimer.Start();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) animationTimer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class MotionPanel : Panel
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int Count { get; set; }

        internal MotionPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(232, 246, 255);
            AccessibleName = "移動する四角";
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            double seconds = elapsed.Elapsed.TotalSeconds;
            int travel = Math.Max(1, ClientSize.Width - 44);
            int x = 6 + (int)(seconds * 85) % travel;
            int y = Math.Max(24, (ClientSize.Height - 34) / 2 + (int)(Math.Sin(seconds * 2) * 12));
            args.Graphics.FillRectangle(Brushes.RoyalBlue, x, y, 30, 30);
            args.Graphics.FillEllipse(Brushes.OrangeRed, Math.Max(5, ClientSize.Width - x - 24), Math.Max(24, y - 12), 14, 14);
            TextRenderer.DrawText(args.Graphics, $"経過 {seconds:0.0} 秒  |  カウント {Count}", Font, new Point(6, 4), Color.FromArgb(24, 52, 76));
        }
    }
}
