using System.Collections.Immutable;
using CodexUsage.Domain;
using CodexUsage.Infrastructure;
using CodexUsage.Infrastructure.Collection;

namespace CodexUsage.Application;

public sealed class DashboardApplicationService : IUsageDashboardService
{
    private static readonly ProcessEfficiencyModeResult EfficiencyNotAttempted = new(
        ProcessExecutionMode.Efficiency,
        false,
        false,
        "Efficiency Mode has not been attempted");

    private readonly IUsageCollector _collector;
    private readonly IProcessEfficiencyMode _efficiencyMode;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _efficiencyGate = new(1, 1);
    private readonly object _efficiencyStateLock = new();
    private CollectorStatus? _collectorStatus;
    private ProcessEfficiencyModeResult _efficiencyResult = EfficiencyNotAttempted;
    private ProcessExecutionMode _requestedExecutionMode = ProcessExecutionMode.Efficiency;
    private long _efficiencyRevision;
    private long? _lastUsageRevision;
    private bool _started;
    private bool _efficiencyAttempted;
    private int _disposed;

    public DashboardApplicationService(
        IUsageCollector collector,
        IProcessEfficiencyMode efficiencyMode)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _efficiencyMode = efficiencyMode ?? throw new ArgumentNullException(nameof(efficiencyMode));
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
                await ApplyRequestedExecutionModeAsync().ConfigureAwait(false);
                PublishStatus("Starting collector");
                _collectorStatus = await _collector.StartAsync(cancellationToken).ConfigureAwait(false);
                _lastUsageRevision = _collectorStatus.UsageRevision;
                _started = true;
            }

            return await QueryCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
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

    public Task<ProcessEfficiencyModeResult> SetProcessExecutionModeAsync(ProcessExecutionMode mode)
    {
        ThrowIfDisposed();
        if (mode is not ProcessExecutionMode.Interactive and not ProcessExecutionMode.Efficiency)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        lock (_efficiencyStateLock)
        {
            _requestedExecutionMode = mode;
        }

        return ApplyRequestedExecutionModeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _efficiencyGate.WaitAsync().ConfigureAwait(false);
            _efficiencyGate.Release();
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
        return new DashboardSnapshot(status, result, GetEfficiencyResult());
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

    private async Task<ProcessEfficiencyModeResult> ApplyRequestedExecutionModeAsync()
    {
        await _efficiencyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            while (true)
            {
                ProcessExecutionMode requestedMode;
                lock (_efficiencyStateLock)
                {
                    requestedMode = _requestedExecutionMode;
                    if (_efficiencyAttempted && _efficiencyResult.Mode == requestedMode)
                    {
                        return _efficiencyResult;
                    }
                }

                var applied = await Task.Run(() => TryApplyEfficiencyMode(requestedMode)).ConfigureAwait(false);
                ProcessEfficiencyModeResult result;
                bool converged;
                lock (_efficiencyStateLock)
                {
                    result = applied with { Revision = checked(++_efficiencyRevision) };
                    _efficiencyResult = result;
                    _efficiencyAttempted = true;
                    converged = _requestedExecutionMode == requestedMode;
                }
                PublishStatus(result.Message);
                if (converged)
                {
                    return result;
                }
            }
        }
        finally
        {
            _efficiencyGate.Release();
        }
    }

    private ProcessEfficiencyModeResult TryApplyEfficiencyMode(ProcessExecutionMode mode)
    {
        try
        {
            return _efficiencyMode.TryApply(mode);
        }
        catch (Exception error)
        {
            return new(mode, false, false, $"{mode} transition failed: {error.Message}");
        }
    }

    private ProcessEfficiencyModeResult GetEfficiencyResult()
    {
        lock (_efficiencyStateLock)
        {
            return _efficiencyResult;
        }
    }

    private void OnCollectorStatusChanged(object? sender, CollectorStatus status)
    {
        _collectorStatus = status;
        PublishStatus(status.Message);
        var usageChanged = _started
            && status.Phase is CollectorPhase.Watching or CollectorPhase.Partial or CollectorPhase.Retrying or CollectorPhase.Degraded
            && _lastUsageRevision is { } previous
            && previous != status.UsageRevision;
        _lastUsageRevision = status.UsageRevision;
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
        var status = new DashboardApplicationStatus(_collectorStatus, GetEfficiencyResult(), message);
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

    private void EnsureStarted()
    {
        if (!_started)
        {
            throw new InvalidOperationException("The dashboard service has not started.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

}
