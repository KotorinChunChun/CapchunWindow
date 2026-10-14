using System.Text.RegularExpressions;

namespace CapchunScreen.UI;

public sealed class RenameForm : BrandedForm
{
    private readonly TextBox _name;
    public string NewBaseName { get; private set; }

    public RenameForm(string currentBaseName)
    {
        ArgumentNullException.ThrowIfNull(currentBaseName);
        NewBaseName = currentBaseName;
        Text = WindowTitle.Create("名前変更");
        AccessibleName = "録画ファイルの名前変更";
        ClientSize = new Size(430, 160);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4, TabIndex = 0
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = "新しい名前（拡張子を除く）(&N)", AutoSize = true, Dock = DockStyle.Fill,
            TabIndex = 0, Margin = new Padding(0, 0, 0, 6)
        }, 0, 0);
        _name = new TextBox
        {
            Text = currentBaseName, Dock = DockStyle.Fill, TabIndex = 1,
            AccessibleName = "拡張子を除く新しいファイル名", Margin = Padding.Empty
        };
        layout.Controls.Add(_name, 0, 1);
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true,
            Dock = DockStyle.Fill, TabIndex = 2, Margin = new Padding(0, 12, 0, 0)
        };
        var cancel = new Button
        {
            Text = "キャンセル(&C)", DialogResult = DialogResult.Cancel, AutoSize = true,
            MinimumSize = new Size(90, 30), TabIndex = 1, AccessibleName = "名前変更を取り消す"
        };
        var save = new Button
        {
            Text = "変更(&S)", AutoSize = true, MinimumSize = new Size(90, 30), TabIndex = 0,
            AccessibleName = "新しい名前を確定する"
        };
        save.Click += (_, _) => ConfirmName();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 3);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private void ConfirmName()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')
            || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            MessageBox.Show(this, "使用できるファイル名を入力してください。拡張子は変更せずに引き継ぎます。",
                WindowTitle.Create("名前変更エラー"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _name.Focus();
            return;
        }
        NewBaseName = name;
        DialogResult = DialogResult.OK;
        Close();
    }
}
