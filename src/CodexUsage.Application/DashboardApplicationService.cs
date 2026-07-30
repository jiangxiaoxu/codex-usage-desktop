using System.Collections.Immutable;
using System.Text;
using CodexUsage.Domain;
using CodexUsage.Infrastructure;
using CodexUsage.Infrastructure.Collection;

namespace CodexUsage.Application;

public sealed class DashboardApplicationService : IUsageDashboardService
{
    private static readonly ProcessEfficiencyModeResult EfficiencyNotAttempted = new(
        false,
        false,
        "Efficiency Mode has not been attempted");

    private readonly IUsageCollector _collector;
    private readonly IProcessEfficiencyMode _efficiencyMode;
    private readonly ProtectedPathPolicy _protectedPathPolicy;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _refreshLock = new();
    private CollectorStatus? _collectorStatus;
    private ProcessEfficiencyModeResult _efficiencyResult = EfficiencyNotAttempted;
    private Task<DashboardSnapshot>? _refreshInFlight;
    private DashboardQueryRequest? _refreshRequest;
    private CancellationTokenSource? _refreshCancellation;
    private UsageRevision? _lastUsageRevision;
    private bool _started;
    private int _disposed;

    public DashboardApplicationService(
        IUsageCollector collector,
        IProcessEfficiencyMode efficiencyMode,
        ProtectedPathPolicy protectedPathPolicy)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _efficiencyMode = efficiencyMode ?? throw new ArgumentNullException(nameof(efficiencyMode));
        _protectedPathPolicy = protectedPathPolicy ?? throw new ArgumentNullException(nameof(protectedPathPolicy));
        _collector.StatusChanged += OnCollectorStatusChanged;
    }

    public event EventHandler<DashboardApplicationStatus>? StatusChanged;

    public event EventHandler? UsageChanged;

    public async Task<DashboardSnapshot> StartAsync(
        DashboardQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRequest(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_started)
            {
                _efficiencyResult = await Task.Run(TryEnableEfficiencyMode, cancellationToken).ConfigureAwait(false);
                PublishStatus("Starting collector");
                _collectorStatus = await _collector.StartAsync(cancellationToken).ConfigureAwait(false);
                _started = true;
            }

            return await QueryCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<DashboardSnapshot> RefreshAsync(
        DashboardQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_refreshLock)
        {
            ThrowIfDisposed();
            if (_refreshInFlight is { IsCompleted: false } active)
            {
                return RequestsEquivalent(_refreshRequest!, request)
                    ? active.WaitAsync(cancellationToken)
                    : QueryAfterRefreshAsync(active, request, cancellationToken);
            }

            var refreshCancellation = new CancellationTokenSource();
            var refresh = RefreshCoreAsync(request, refreshCancellation.Token);
            _refreshInFlight = refresh;
            _refreshRequest = request;
            _refreshCancellation = refreshCancellation;
            _ = refresh.ContinueWith(
                completed => ClearRefresh(completed, refreshCancellation),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return refresh.WaitAsync(cancellationToken);
        }
    }

    private async Task<DashboardSnapshot> RefreshCoreAsync(
        DashboardQueryRequest request,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureStarted();
            var sync = await _collector.RefreshAsync(cancellationToken).ConfigureAwait(false);
            _collectorStatus = sync.Status;
            return await QueryCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<DashboardSnapshot> QueryAfterRefreshAsync(
        Task<DashboardSnapshot> refresh,
        DashboardQueryRequest request,
        CancellationToken cancellationToken)
    {
        await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private void ClearRefresh(
        Task<DashboardSnapshot> completed,
        CancellationTokenSource refreshCancellation)
    {
        lock (_refreshLock)
        {
            if (ReferenceEquals(_refreshInFlight, completed))
            {
                _refreshInFlight = null;
                _refreshRequest = null;
                _refreshCancellation = null;
            }
        }
        refreshCancellation.Dispose();
    }

    public async Task<DashboardSnapshot> QueryAsync(
        DashboardQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRequest(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureStarted();
            return await QueryCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<CsvExportResult> ExportCsvAsync(
        DashboardQueryRequest request,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ExportCsvCoreAsync(request, outputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(CsvExportStatus.Cancelled, null, 0);
        }
    }

    private async Task<CsvExportResult> ExportCsvCoreAsync(
        DashboardQueryRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateRequest(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var normalizedPath = Path.GetFullPath(outputPath);
        _protectedPathPolicy.AssertWritablePath(normalizedPath);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureStarted();
            var storedEvents = await QueryStoredEventsAsync(request, cancellationToken).ConfigureAwait(false);
            var export = await Task.Run(
                () =>
                {
                    var events = storedEvents.Select(ToDomainEvent).ToArray();
                    var filter = ToFilterSpec(request);
                    return new
                    {
                        Csv = UsageAccounting.CsvRows(events, filter),
                        EventCount = events.LongCount(value => UsageAccounting.MatchesFilter(value, filter)),
                    };
                },
                cancellationToken).ConfigureAwait(false);
            _protectedPathPolicy.AssertWritablePath(normalizedPath);
            await File.WriteAllTextAsync(
                normalizedPath,
                export.Csv,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            return new(CsvExportStatus.Completed, normalizedPath, export.EventCount);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_refreshLock)
        {
            _refreshCancellation?.Cancel();
        }
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _collector.StatusChanged -= OnCollectorStatusChanged;
            await _collector.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<DashboardSnapshot> QueryCoreAsync(
        DashboardQueryRequest request,
        CancellationToken cancellationToken)
    {
        var storedEvents = await QueryStoredEventsAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var status = await _collector.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        _collectorStatus = status;
        var result = await Task.Run(
            () => UsageAccounting.Query(
                storedEvents.Select(ToDomainEvent).ToArray(),
                ToScanDiagnostics(status.Diagnostics),
                ToFilterSpec(request)),
            cancellationToken).ConfigureAwait(false);
        return new DashboardSnapshot(status, result, _efficiencyResult);
    }

    private ValueTask<IReadOnlyList<StoredUsageEvent>> QueryStoredEventsAsync(
        DashboardQueryRequest request,
        CancellationToken cancellationToken) =>
        _collector.QueryEventsAsync(
            new UsageEventQuery(
                request.StartUtc.ToUnixTimeMilliseconds(),
                request.EndUtc.ToUnixTimeMilliseconds()),
            cancellationToken);

    private static FilterSpec ToFilterSpec(DashboardQueryRequest request) => new(
        request.StartUtc,
        request.EndUtc,
        request.Models,
        request.Subjects,
        request.PathQuery);

    private ProcessEfficiencyModeResult TryEnableEfficiencyMode()
    {
        try
        {
            return _efficiencyMode.TryEnable();
        }
        catch (Exception error)
        {
            return new(false, false, $"Efficiency Mode failed: {error.Message}");
        }
    }

    private void OnCollectorStatusChanged(object? sender, CollectorStatus status)
    {
        _collectorStatus = status;
        PublishStatus(status.Message);
        var revision = UsageRevision.FromStatus(status);
        var usageChanged = _started
            && status.Phase is CollectorPhase.Watching or CollectorPhase.Degraded
            && _lastUsageRevision is { } previous
            && previous != revision;
        _lastUsageRevision = revision;
        if (usageChanged) PublishUsageChanged();
    }

    private void PublishUsageChanged()
    {
        var handlers = UsageChanged;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // Consumers must not break the collector actor.
            }
        }
    }

    private void PublishStatus(string message)
    {
        var status = new DashboardApplicationStatus(_collectorStatus, _efficiencyResult, message);
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<DashboardApplicationStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, status);
            }
            catch
            {
                // UI subscribers must not break the collector actor.
            }
        }
    }

    private static UsageEvent ToDomainEvent(StoredUsageEvent value) => new(
        value.TimestampUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        value.TokenEventOrdinal,
        value.ConversationId,
        value.RolloutId,
        value.ParentThreadId,
        value.ThreadType,
        value.AgentRole,
        value.AgentPath,
        value.AgentNickname,
        value.Model,
        value.InputTokens,
        value.CachedInputTokens,
        value.OutputTokens,
        value.ReasoningOutputTokens);

    private static ScanDiagnostics ToScanDiagnostics(CollectorDiagnostics value) => new(
        checked((int)Math.Min(value.FilesScanned, int.MaxValue)),
        checked((int)Math.Min(value.MalformedLines, int.MaxValue)),
        checked((int)Math.Min(value.DuplicateSnapshotsSkipped, int.MaxValue)),
        checked((int)Math.Min(value.ZeroBreakdownSnapshotsSkipped, int.MaxValue)),
        checked((int)Math.Min(value.InvalidTokenRelationshipsSkipped, int.MaxValue)));

    private static void ValidateRequest(DashboardQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EndUtc <= request.StartUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "EndUtc must be later than StartUtc.");
        }

        ArgumentNullException.ThrowIfNull(request.PathQuery);
    }

    private static bool RequestsEquivalent(DashboardQueryRequest left, DashboardQueryRequest right) =>
        left.StartUtc == right.StartUtc
        && left.EndUtc == right.EndUtc
        && string.Equals(left.PathQuery, right.PathQuery, StringComparison.Ordinal)
        && NullableSequenceEqual(left.Models, right.Models)
        && NullableSequenceEqual(left.Subjects, right.Subjects);

    private static bool NullableSequenceEqual<T>(ImmutableArray<T>? left, ImmutableArray<T>? right) =>
        left is null
            ? right is null
            : right is not null && left.Value.SequenceEqual(right.Value);

    private void EnsureStarted()
    {
        if (!_started)
        {
            throw new InvalidOperationException("The dashboard service has not started.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record UsageRevision(
        DateTimeOffset? LastSuccessfulInventoryUtc,
        long FilesScanned,
        long ChangedFilesLastSync,
        long Conflicts)
    {
        public static UsageRevision FromStatus(CollectorStatus status) => new(
            status.LastSuccessfulInventoryUtc,
            status.Diagnostics.FilesScanned,
            status.ChangedFilesLastSync,
            status.Conflicts);
    }
}
