namespace CapchunScreen.Input;

public sealed record InputSample(int X, int Y, string Click, string Keys);

public interface IInputSource
{
    InputSample Consume(Rectangle bounds, nint targetWindow);
}
