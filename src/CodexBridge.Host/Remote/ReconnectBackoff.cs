namespace CodexBridge.Host.Remote;

public sealed class ReconnectBackoff
{
    private readonly TimeSpan _minimum;
    private readonly TimeSpan _maximum;
    private TimeSpan _current;

    public ReconnectBackoff(TimeSpan minimum, TimeSpan maximum)
    {
        if (minimum <= TimeSpan.Zero || maximum < minimum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        _minimum = minimum;
        _maximum = maximum;
        _current = minimum;
    }

    public TimeSpan Next()
    {
        var result = _current;
        _current = TimeSpan.FromTicks(Math.Min(_current.Ticks * 2, _maximum.Ticks));
        return result;
    }

    public void Reset() => _current = _minimum;
}
