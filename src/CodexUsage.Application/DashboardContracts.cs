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

public sealed record ProcessEfficiencyModeResult(
    bool EcoQosEnabled,
    bool BelowNormalPriorityEnabled,
    string Message)
{
    public bool IsFullyEnabled => EcoQosEnabled && BelowNormalPriorityEnabled;
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
    ProcessEfficiencyModeResult TryEnable();
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

    Task<CsvExportResult> ExportCsvAsync(
        DashboardQueryRequest request,
        string outputPath,
        CancellationToken cancellationToken = default);
}
