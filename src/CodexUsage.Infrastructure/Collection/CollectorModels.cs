using CodexUsage.Domain;

namespace CodexUsage.Infrastructure.Collection;

public sealed record CollectorOptions
{
    public required string CodexHome { get; init; }

    public required string DatabasePath { get; init; }

    public TimeSpan WatcherDebounce { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan FullInventoryInterval { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan RecoverySnapshotDelay { get; init; } = TimeSpan.FromMilliseconds(25);

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public int RetryAttempts { get; init; } = 5;

    public int WatcherBatchSize { get; init; } = 16;

    public int CooperativeItemLimit { get; init; } = 32;

    public TimeSpan CooperativeTimeBudget { get; init; } = TimeSpan.FromMilliseconds(8);

    public int ParserSliceBytes { get; init; } = 256 * 1024;

    public int ParserSliceRecords { get; init; } = 256;

    public bool EnableWatchers { get; init; } = true;
}

public enum CollectorPhase
{
    Initializing,
    Syncing,
    Retrying,
    Watching,
    Partial,
    Degraded,
    Stopped,
}

public enum ObservationCoverage
{
    Baseline,
    Continuous,
    Gap,
}

public sealed record ObservationGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public sealed record CollectorDiagnostics(
    long FilesScanned,
    long MalformedLines,
    long DuplicateSnapshotsSkipped,
    long ZeroBreakdownSnapshotsSkipped,
    long InvalidTokenRelationshipsSkipped,
    long CooperativeYieldCount,
    long PartialSources,
    long SafeOpaqueOversizedRecordsSkipped,
    long SafeNullPaddingRecordsSkipped);

public sealed record CollectorStatus(
    CollectorPhase Phase,
    string DatabasePath,
    DateTimeOffset? RunStartedUtc,
    DateTimeOffset? LastSuccessfulInventoryUtc,
    DateTimeOffset? LastHeartbeatUtc,
    long FilesKnown,
    long RealtimeVoiceSessions,
    int PendingFiles,
    long ChangedFilesLastSync,
    long Conflicts,
    ObservationCoverage ObservationCoverage,
    ObservationGap? ObservationGap,
    string Message,
    CollectorDiagnostics Diagnostics,
    long UsageRevision);

public sealed record CollectorSyncResult(CollectorStatus Status, bool UsageChanged);

public interface IUsageCollector : IAsyncDisposable
{
    event EventHandler<CollectorStatus>? StatusChanged;

    ValueTask<CollectorStatus> StartAsync(CancellationToken cancellationToken = default);

    ValueTask<CollectorSyncResult> RefreshAsync(CancellationToken cancellationToken = default);

    ValueTask<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredUsageEvent>> QueryEventsAsync(
        UsageEventQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MainThreadOption>> QueryRecentMainThreadsAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
}
