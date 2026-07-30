namespace CodexUsage.Application;

public sealed class StartupRegistrationCoordinator
{
    private readonly IStartupRegistrationService _service;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation;

    public StartupRegistrationCoordinator(IStartupRegistrationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public Task<PlatformFeatureResult?> GetLatestStateAsync(CancellationToken cancellationToken = default) =>
        RunLatestAsync(null, cancellationToken);

    public Task<PlatformFeatureResult?> SetLatestStateAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        RunLatestAsync(enabled, cancellationToken);

    private async Task<PlatformFeatureResult?> RunLatestAsync(
        bool? enabled,
        CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation != Interlocked.Read(ref _generation)) return null;
            var result = enabled is { } requested
                ? await _service.SetEnabledAsync(requested, cancellationToken).ConfigureAwait(false)
                : await _service.GetStateAsync(cancellationToken).ConfigureAwait(false);
            return generation == Interlocked.Read(ref _generation) ? result : null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
