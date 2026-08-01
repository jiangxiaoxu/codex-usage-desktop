namespace CodexUsage.Application;

public sealed class ReleaseUpdateInstallerLaunchCoordinator
{
    private int _isInFlight;

    public bool IsInFlight => Volatile.Read(ref _isInFlight) != 0;

    public bool TryBegin() => Interlocked.CompareExchange(ref _isInFlight, 1, 0) == 0;

    public void Complete() => Interlocked.Exchange(ref _isInFlight, 0);
}
