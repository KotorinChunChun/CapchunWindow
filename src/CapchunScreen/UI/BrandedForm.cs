namespace CapchunScreen.UI;

/// <summary>独自画面ごとにアプリアイコンを所有し、フォームと共に解放する。</summary>
public abstract class BrandedForm : Form
{
    private readonly Icon _appIcon;
    private bool _iconDisposed;

    protected BrandedForm()
    {
        using Stream stream = typeof(BrandedForm).Assembly.GetManifestResourceStream("CapchunScreen.AppIcon.ico")
            ?? throw new InvalidOperationException("アプリアイコンを読み込めません。");
        _appIcon = new Icon(stream, 32, 32);
        Icon = _appIcon;
        Text = WindowTitle.Create();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally
        {
            if (disposing && !_iconDisposed)
            {
                _iconDisposed = true;
                _appIcon?.Dispose();
            }
        }
    }
}
