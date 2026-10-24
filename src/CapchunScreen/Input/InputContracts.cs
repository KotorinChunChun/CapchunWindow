namespace CapchunScreen.Input;

public sealed record InputSample(int X, int Y, string Click, string Keys);

public interface IInputSource
{
    InputSample Consume(Rectangle bounds, nint targetWindow);
}

/// <summary>録画後に利用者へ伝える、入力取得の品質情報。</summary>
public interface IInputHealth
{
    string? Warning { get; }
}
