using System.Diagnostics;

namespace CapchunScreen.Playback;

internal sealed class PlaybackTimeline(Func<double>? now = null)
{
    private readonly Func<double> _now = now ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private double _position;
    private double _started;
    private double _rate = 1;
    public bool IsPlaying { get; private set; }
    public double Position => _position + (IsPlaying ? Math.Max(0, _now() - _started) * _rate : 0);
    public double Rate
    {
        get => _rate;
        set
        {
            if (!double.IsFinite(value) || value is < .25 or > 2) throw new ArgumentOutOfRangeException(nameof(value));
            _position = Position;
            _started = _now();
            _rate = value;
        }
    }

    public void Play()
    {
        if (IsPlaying) return;
        _started = _now();
        IsPlaying = true;
    }

    public void Pause()
    {
        _position = Position;
        IsPlaying = false;
    }

    public void Seek(double seconds)
    {
        _position = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        _started = _now();
    }
}
