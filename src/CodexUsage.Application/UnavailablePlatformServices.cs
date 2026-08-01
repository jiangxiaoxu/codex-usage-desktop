namespace CodexUsage.Application;

public sealed class UnavailableStartupRegistrationService(string message) : IStartupRegistrationService
{
    private readonly string _message = string.IsNullOrWhiteSpace(message)
        ? throw new ArgumentException("A diagnostic message is required.", nameof(message))
        : message;

    public Task<PlatformFeatureResult> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlatformFeatureResult(false, false, _message));
    }

    public Task<PlatformFeatureResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        GetStateAsync(cancellationToken);
}

public sealed class UnconfiguredReleaseUpdateService : IReleaseUpdateService
{
    public const string DiagnosticMessage = "Release feed 未配置; 当前版本不执行联网更新检查";

    public bool IsAvailable => false;

    public Task<ReleaseUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ReleaseUpdateCheckResult(false, false, DiagnosticMessage));
    }

    public Task<ReleaseUpdateDownloadResult> DownloadAsync(
        ReleaseUpdatePackage package,
        IProgress<ReleaseUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ReleaseUpdateDownloadResult(
            ReleaseUpdateDownloadStatus.Failed,
            DiagnosticMessage));
    }

    public Task<ReleaseUpdateInstallerVerificationResult> VerifyDownloadedInstallerAsync(
        ReleaseUpdatePackage package,
        string installerPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ReleaseUpdateInstallerVerificationResult(false, DiagnosticMessage));
    }

}
