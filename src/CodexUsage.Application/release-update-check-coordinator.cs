namespace CodexUsage.Application;

public sealed class ReleaseUpdateCheckCoordinator
{
    private int _cancelled;
    private int _inFlight;

    public bool TryBegin() =>
        Volatile.Read(ref _cancelled) == 0
        && Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0;

    public void Complete() => Interlocked.Exchange(ref _inFlight, 0);

    public void Cancel() => Interlocked.Exchange(ref _cancelled, 1);
}
