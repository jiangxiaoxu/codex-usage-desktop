namespace CodexUsage.Application;

public readonly record struct ReleaseUpdateDownloadTicket(long Generation);

public sealed class ReleaseUpdateDownloadCoordinator
{
    private readonly object _gate = new();
    private long _generation;
    private bool _isInFlight;
    private bool _cancelled;

    public bool IsInFlight
    {
        get
        {
            lock (_gate)
            {
                return _isInFlight;
            }
        }
    }

    public bool TryBegin(out ReleaseUpdateDownloadTicket ticket)
    {
        lock (_gate)
        {
            ticket = default;
            if (_cancelled || _isInFlight) return false;

            _isInFlight = true;
            ticket = new ReleaseUpdateDownloadTicket(_generation);
            return true;
        }
    }

    public bool IsCurrent(ReleaseUpdateDownloadTicket ticket)
    {
        lock (_gate)
        {
            return !_cancelled && ticket.Generation == _generation;
        }
    }

    public void Complete(ReleaseUpdateDownloadTicket ticket)
    {
        lock (_gate)
        {
            _isInFlight = false;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cancelled = true;
            _generation++;
            _isInFlight = false;
        }
    }
}
