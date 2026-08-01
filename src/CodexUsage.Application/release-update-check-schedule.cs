namespace CodexUsage.Application;

public sealed class ReleaseUpdateCheckSchedule
{
    private readonly TimeSpan _interval;
    private DateTimeOffset _nextDueUtc;
    private bool _started;
    private bool _cancelled;

    public ReleaseUpdateCheckSchedule(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _interval = interval;
    }

    public bool Start(DateTimeOffset nowUtc)
    {
        if (_cancelled || _started) return false;
        _started = true;
        _nextDueUtc = nowUtc.Add(_interval);
        return true;
    }

    public bool IsDue(DateTimeOffset nowUtc)
    {
        if (_cancelled || !_started || nowUtc < _nextDueUtc) return false;

        do
        {
            _nextDueUtc = _nextDueUtc.Add(_interval);
        }
        while (_nextDueUtc <= nowUtc);

        return true;
    }

    public void Cancel() => _cancelled = true;
}
