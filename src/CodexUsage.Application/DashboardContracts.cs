using System.Collections.Immutable;
using CodexUsage.Domain;
using CodexUsage.Infrastructure.Collection;

namespace CodexUsage.Application;

public sealed record DashboardQueryRequest(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    ImmutableArray<string>? Models = null,
    ImmutableArray<SubjectFilter>? Subjects = null,
    string PathQuery = "");

public sealed record PlatformFeatureResult(
    bool IsAvailable,
    bool IsEnabled,
    string Message);

public sealed record ReleaseUpdateCheckResult(
    bool IsAvailable,
    bool IsUpdateAvailable,
    string Message,
    ReleaseUpdatePackage? Package = null);

public sealed record ReleaseUpdatePackage(
    string Version,
    string ReleaseTag,
    Uri DownloadUri,
    string Sha256,
    long SizeBytes,
    DateTimeOffset PublishedUtc);

public enum ReleaseUpdateDownloadStatus
{
    Completed,
    Cancelled,
    Failed,
}

public sealed record ReleaseUpdateDownloadResult(
    ReleaseUpdateDownloadStatus Status,
    string Message,
    string? InstallerPath = null);

public sealed record ReleaseUpdateInstallerVerificationResult(
    bool IsValid,
    string Message);

public enum CsvExportStatus
{
    Completed,
    Cancelled,
}

public sealed record CsvExportResult(
    CsvExportStatus Status,
    string? OutputPath,
    long EventCount);

public enum ProcessExecutionMode
{
    Interactive,
    Efficiency,
}

public enum DashboardWindowActivitySignal
{
    Shown,
    Hidden,
    Activated,
    Deactivated,
    Minimized,
    Restored,
    ShutdownStarted,
}

public readonly record struct DashboardWindowActivity(
    bool IsVisible,
    bool IsActivated,
    bool IsMinimized,
    bool IsShuttingDown)
{
    public static DashboardWindowActivity Hidden { get; } = new(false, false, false, false);

    public ProcessExecutionMode ExecutionMode =>
        IsVisible && IsActivated && !IsMinimized && !IsShuttingDown
            ? ProcessExecutionMode.Interactive
            : ProcessExecutionMode.Efficiency;

    public DashboardWindowActivity Reduce(DashboardWindowActivitySignal signal)
    {
        if (IsShuttingDown)
        {
            return this;
        }

        return signal switch
        {
            DashboardWindowActivitySignal.Shown => this with { IsVisible = true },
            DashboardWindowActivitySignal.Hidden => this with { IsVisible = false, IsActivated = false },
            DashboardWindowActivitySignal.Activated => this with { IsActivated = true },
            DashboardWindowActivitySignal.Deactivated => this with { IsActivated = false },
            DashboardWindowActivitySignal.Minimized => this with { IsMinimized = true },
            DashboardWindowActivitySignal.Restored => this with { IsMinimized = false },
            DashboardWindowActivitySignal.ShutdownStarted => new(false, false, IsMinimized, true),
            _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null),
        };
    }
}

public sealed record ProcessEfficiencyModeResult(
    ProcessExecutionMode Mode,
    bool PowerThrottlingApplied,
    bool PriorityClassApplied,
    string Message,
    long Revision = 0)
{
    public bool IsFullyApplied => PowerThrottlingApplied && PriorityClassApplied;
}

public sealed record DashboardApplicationStatus(
    CollectorStatus? Collector,
    ProcessEfficiencyModeResult EfficiencyMode,
    string Message);

public sealed record DashboardSnapshot(
    CollectorStatus Collector,
    QueryResult Result,
    ProcessEfficiencyModeResult EfficiencyMode);

public interface IProcessEfficiencyMode
{
    ProcessEfficiencyModeResult TryApply(ProcessExecutionMode mode);
}

public interface IStartupRegistrationService
{
    Task<PlatformFeatureResult> GetStateAsync(CancellationToken cancellationToken = default);

    Task<PlatformFeatureResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);
}

public interface IReleaseUpdateService
{
    bool IsAvailable { get; }

    Task<ReleaseUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    Task<ReleaseUpdateDownloadResult> DownloadAsync(
        ReleaseUpdatePackage package,
        CancellationToken cancellationToken = default);

    Task<ReleaseUpdateInstallerVerificationResult> VerifyDownloadedInstallerAsync(
        ReleaseUpdatePackage package,
        string installerPath,
        CancellationToken cancellationToken = default);

}

public interface IExportDestinationPicker
{
    Task<string?> PickCsvPathAsync(CancellationToken cancellationToken = default);
}

public interface IUsageDashboardService : IAsyncDisposable
{
    event EventHandler<DashboardApplicationStatus>? StatusChanged;

    event EventHandler? UsageChanged;

    Task<DashboardSnapshot> StartAsync(
        DashboardQueryRequest request,
        CancellationToken cancellationToken = default);

    Task<DashboardSnapshot> RefreshAsync(
        DashboardQueryRequest request,
        CancellationToken cancellationToken = default);

    Task<DashboardSnapshot> QueryAsync(
        DashboardQueryRequest request,
        CancellationToken cancellationToken = default);

    Task<ProcessEfficiencyModeResult> SetProcessExecutionModeAsync(ProcessExecutionMode mode);

    Task<CsvExportResult> ExportCsvAsync(
        DashboardQueryRequest request,
        string outputPath,
        CancellationToken cancellationToken = default);
}
