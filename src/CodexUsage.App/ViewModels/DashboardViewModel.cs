using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexUsage.App.Services;
using CodexUsage.Application;
using CodexUsage.Domain;
using CodexUsage.Infrastructure.Collection;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexUsage.App.ViewModels;

public sealed class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(350);
    private readonly IUsageDashboardService _service;
    private readonly IUiDispatcher _dispatcher;
    private readonly StartupRegistrationCoordinator _startupTask;
    private readonly IReleaseUpdateService _packageUpdate;
    private readonly IExportDestinationPicker _exportPicker;
    private readonly TimeProvider _timeProvider;
    private readonly ReleaseUpdateCheckCoordinator _updateCheckCoordinator = new();
    private readonly ReleaseUpdateCheckSchedule _automaticUpdateSchedule = new(TimeSpan.FromHours(6));
    private readonly ReleaseUpdateDownloadCoordinator _updateDownloadCoordinator = new();
    private readonly ReleaseUpdateInstallerLaunchCoordinator _installerLaunchCoordinator = new();
    private readonly DashboardPresentationCollections _presentation = new();
    private readonly DashboardSnapshotApplicationLifecycle _snapshotApplicationLifecycle = new();
    private CancellationTokenSource? _filterDebounce;
    private CancellationTokenSource? _automaticUpdateCancellation;
    private Task? _automaticUpdateLoop;
    private bool _isStartupEnabled;
    private bool _isStartupAvailable;
    private bool _suppressStartupUpdate;
    private bool _initialized;
    private double _rangeHours = 12;
    private double _rangeScalePosition = DashboardTimeRangeScale.HoursToPosition(12);
    private DashboardCustomRange? _customRange;
    private string _searchText = string.Empty;
    private string _healthStatusText = "正在启动";
    private string _lastReconciliationText = "—";
    private string _sourceFilesText = "0";
    private string _realtimeVoiceSessionsText = "0";
    private string _retryQueueText = "0";
    private string _watcherStatusText = "启动中";
    private string _headerStatusText = "启动中 · 上次对账 —";
    private string _headerStatusGlyph = "\uE895";
    private Brush _headerStatusBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x82, 0x90, 0xA3));
    private string _coverageText = "等待首次对账";
    private string _collectorStatusText = "正在启动采集器";
    private string _platformStatusText;
    private ReleaseUpdatePackage? _availableUpdate;
    private string? _downloadedUpdateInstallerPath;
    private ReleaseUpdateDownloadTicket? _downloadedUpdateTicket;
    private ReleaseUpdateDownloadTicket? _activeUpdateDownloadTicket;
    private bool _isDownloading;
    private double _updateDownloadProgressPercent;
    private bool _isUpdateDownloadIndeterminate = true;
    private string _updateDownloadProgressText = "下载中";
    private long _updateStateGeneration;
    private int _busyCount;
    private int _queryInFlight;
    private int _queryPending;
    private int _pendingQueryPurpose = (int)DashboardSnapshotApplyPurpose.UserFilter;
    private int _syncing;
    private int _disposed;

    public DashboardViewModel(
        IUsageDashboardService service,
        IUiDispatcher dispatcher,
        IStartupRegistrationService startupTask,
        IReleaseUpdateService packageUpdate,
        IExportDestinationPicker exportPicker,
        TimeProvider? timeProvider = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _startupTask = new StartupRegistrationCoordinator(startupTask ?? throw new ArgumentNullException(nameof(startupTask)));
        _packageUpdate = packageUpdate ?? throw new ArgumentNullException(nameof(packageUpdate));
        _exportPicker = exportPicker ?? throw new ArgumentNullException(nameof(exportPicker));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _platformStatusText = packageUpdate.IsAvailable
            ? "Release feed 可用"
            : UnconfiguredReleaseUpdateService.DiagnosticMessage;
        _service.StatusChanged += OnStatusChanged;
        _service.UsageChanged += OnUsageChanged;

    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DashboardSnapshotApplicationEventArgs>? SnapshotApplying;
    public event EventHandler<DashboardSnapshotApplicationEventArgs>? SnapshotApplied;

    public ObservableCollection<MetricCard> Metrics => _presentation.Metrics;
    public ObservableCollection<CostSlice> CostSlices => _presentation.CostSlices;
    public ObservableCollection<ModelUsageRow> Models => _presentation.Models;
    public ObservableCollection<SubjectUsageRow> Subjects => _presentation.Subjects;
    public ObservableCollection<DiagnosticRow> Diagnostics => _presentation.Diagnostics;
    public ObservableCollection<ModelFilterOption> ModelOptions => _presentation.ModelOptions;
    public ObservableCollection<SubjectFilterOption> AgentOptions => _presentation.AgentOptions;

    public bool IsStartupEnabled
    {
        get => _isStartupEnabled;
        set
        {
            if (!SetProperty(ref _isStartupEnabled, value) || _suppressStartupUpdate) return;
            _ = SetStartupEnabledAsync(value);
        }
    }

    public bool IsStartupAvailable
    {
        get => _isStartupAvailable;
        private set => SetProperty(ref _isStartupAvailable, value);
    }

    public bool CanCheckUpdates => _packageUpdate.IsAvailable;
    public bool CanDownloadUpdate => _availableUpdate is not null
        && _downloadedUpdateInstallerPath is null
        && !_updateDownloadCoordinator.IsInFlight;
    public bool CanRunDownloadedUpdate => _downloadedUpdateInstallerPath is not null
        && _downloadedUpdateTicket is { } ticket
        && _updateDownloadCoordinator.IsCurrent(ticket);
    public bool IsDownloading { get => _isDownloading; private set => SetProperty(ref _isDownloading, value); }
    public double UpdateDownloadProgressPercent
    {
        get => _updateDownloadProgressPercent;
        private set => SetProperty(ref _updateDownloadProgressPercent, value);
    }
    public bool IsUpdateDownloadIndeterminate
    {
        get => _isUpdateDownloadIndeterminate;
        private set => SetProperty(ref _isUpdateDownloadIndeterminate, value);
    }
    public string UpdateDownloadProgressText
    {
        get => _updateDownloadProgressText;
        private set => SetProperty(ref _updateDownloadProgressText, value);
    }
    public string DownloadUpdateLabel => _availableUpdate is { } update
        ? $"下载并校验 {update.Version}"
        : "下载更新";
    public string RunUpdateLabel => _availableUpdate is { } update
        ? $"运行安装器 {update.Version}"
        : "运行安装器";

    public double RangeHours => _rangeHours;

    public double RangeScalePosition
    {
        get => _rangeScalePosition;
        set
        {
            var transition = DashboardTimeRangeTransition.FromUserPosition(
                _rangeHours,
                value,
                _customRange is not null);
            var rangeChanged = transition.HoursChanged;
            var positionChanged = !NearlyEquals(_rangeScalePosition, transition.Selection.Position);
            var requiresCanonicalPosition = !NearlyEquals(value, transition.Selection.Position);

            _rangeHours = transition.Selection.Hours;
            _rangeScalePosition = transition.Selection.Position;

            if (rangeChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeHours)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeLabel)));
            }

            if (positionChanged || requiresCanonicalPosition)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeScalePosition)));
            }

            if (transition.ClearCustomRange) ClearCustomRange();
            if (transition.QueryRequired) ScheduleQuery(DashboardSnapshotApplyPurpose.UserFilter);
        }
    }

    public string RangeLabel => _customRange?.Label ?? DashboardTimeRangeScale.FormatHours(RangeHours);

    public DateTimeOffset? CustomStartDateSgt => _customRange?.StartDateSgt;

    public DateTimeOffset? CustomEndDateSgt => _customRange?.EndDateSgt;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ScheduleQuery(DashboardSnapshotApplyPurpose.UserFilter);
        }
    }

    public string HealthStatusText { get => _healthStatusText; private set => SetProperty(ref _healthStatusText, value); }
    public string LastReconciliationText { get => _lastReconciliationText; private set => SetProperty(ref _lastReconciliationText, value); }
    public string SourceFilesText { get => _sourceFilesText; private set => SetProperty(ref _sourceFilesText, value); }
    public string RealtimeVoiceSessionsText { get => _realtimeVoiceSessionsText; private set => SetProperty(ref _realtimeVoiceSessionsText, value); }
    public string RetryQueueText { get => _retryQueueText; private set => SetProperty(ref _retryQueueText, value); }
    public string WatcherStatusText { get => _watcherStatusText; private set => SetProperty(ref _watcherStatusText, value); }
    public string HeaderStatusText { get => _headerStatusText; private set => SetProperty(ref _headerStatusText, value); }
    public string HeaderStatusGlyph { get => _headerStatusGlyph; private set => SetProperty(ref _headerStatusGlyph, value); }
    public Brush HeaderStatusBrush { get => _headerStatusBrush; private set => SetProperty(ref _headerStatusBrush, value); }
    public string CoverageText { get => _coverageText; private set => SetProperty(ref _coverageText, value); }
    public string CollectorStatusText { get => _collectorStatusText; private set => SetProperty(ref _collectorStatusText, value); }
    public string PlatformStatusText { get => _platformStatusText; private set => SetProperty(ref _platformStatusText, value); }
    public bool IsBusy => Volatile.Read(ref _busyCount) > 0;
    public bool IsSyncing => Volatile.Read(ref _syncing) != 0;
    public bool CanSync => !IsSyncing;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var request = CreateRequest();
        var startup = LoadStartupStateAsync(cancellationToken);
        await ExecuteSnapshotAsync(
            token => _service.StartAsync(request, token),
            DashboardSnapshotApplyPurpose.InitialLoad,
            cancellationToken).ConfigureAwait(false);
        await startup.ConfigureAwait(false);
        _initialized = true;
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0) return;
        QueuePropertyChanged(nameof(IsSyncing));
        QueuePropertyChanged(nameof(CanSync));
        var request = CreateRequest();
        try
        {
            await ExecuteSnapshotAsync(
                token => _service.RefreshAsync(request, token),
                DashboardSnapshotApplyPurpose.DataRefresh,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _syncing, 0);
            QueuePropertyChanged(nameof(IsSyncing));
            QueuePropertyChanged(nameof(CanSync));
        }
    }

    public async Task ExportCsvAsync(CancellationToken cancellationToken = default)
    {
        var busyStarted = false;
        try
        {
            var outputPath = await _exportPicker.PickCsvPathAsync(cancellationToken);
            if (outputPath is null) return;
            BeginBusy();
            busyStarted = true;
            var result = await _service.ExportCsvAsync(CreateRequest(), outputPath, cancellationToken).ConfigureAwait(false);
            QueueUi(() => SetPlatformStatus(result.Status == CsvExportStatus.Cancelled
                ? "CSV 导出已取消"
                : $"已导出 {result.EventCount:N0} 条记录: {result.OutputPath}"));
        }
        catch (OperationCanceledException)
        {
            QueueUi(() => SetPlatformStatus("CSV 导出已取消"));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            QueueUi(() => SetPlatformStatus($"导出失败: {error.Message}"));
        }
        finally
        {
            if (busyStarted) EndBusy();
        }
    }

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        _ = await CheckForUpdatesCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public void StartAutomaticUpdateChecks(CancellationToken applicationLifetime)
    {
        if (!_packageUpdate.IsAvailable || Volatile.Read(ref _disposed) != 0) return;
        if (_automaticUpdateLoop is not null) return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime);
        _automaticUpdateCancellation = cancellation;
        _automaticUpdateLoop = RunAutomaticUpdateChecksAsync(cancellation.Token);
    }

    private async Task<bool> CheckForUpdatesCoreAsync(CancellationToken cancellationToken)
    {
        if (!_updateCheckCoordinator.TryBegin()) return false;
        BeginBusy();
        try
        {
            var result = await _packageUpdate.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (_installerLaunchCoordinator.IsInFlight)
            {
                return true;
            }

            var generation = Interlocked.Increment(ref _updateStateGeneration);
            _updateDownloadCoordinator.Invalidate();
            QueuePropertyChanged(nameof(CanDownloadUpdate));
            QueuePropertyChanged(nameof(CanRunDownloadedUpdate));
            QueueUi(() => ApplyUpdateCheck(result, generation));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            QueueUi(() => SetPlatformStatus("检查更新已取消"));
            return true;
        }
        catch (Exception error)
        {
            QueueUi(() => SetPlatformStatus($"检查更新失败: {error.Message}"));
            return true;
        }
        finally
        {
            EndBusy();
            _updateCheckCoordinator.Complete();
        }
    }

    public async Task DownloadUpdateAsync(CancellationToken cancellationToken = default)
    {
        var update = _availableUpdate;
        if (update is null
            || _downloadedUpdateInstallerPath is not null
            || !_updateDownloadCoordinator.TryBegin(out var ticket))
        {
            return;
        }

        BeginBusy();
        QueuePropertyChanged(nameof(CanDownloadUpdate));
        QueueUi(() => StartUpdateDownload(ticket));
        try
        {
            var progress = new Progress<ReleaseUpdateDownloadProgress>(
                value => QueueUi(() => ApplyUpdateDownloadProgress(value, ticket)));
            var result = await _packageUpdate.DownloadAsync(update, progress, cancellationToken).ConfigureAwait(false);
            QueueUi(() => ApplyUpdateDownload(result, ticket));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            QueueUi(() => ApplyUpdateDownload(
                new ReleaseUpdateDownloadResult(
                    ReleaseUpdateDownloadStatus.Cancelled,
                    "更新下载已取消; 未启动安装器"),
                ticket));
        }
        catch (Exception error)
        {
            QueueUi(() => ApplyUpdateDownload(
                new ReleaseUpdateDownloadResult(
                    ReleaseUpdateDownloadStatus.Failed,
                    $"更新下载失败: {error.Message}"),
                ticket));
        }
        finally
        {
            _updateDownloadCoordinator.Complete(ticket);
            QueuePropertyChanged(nameof(CanDownloadUpdate));
            EndBusy();
        }
    }

    public bool TryGetDownloadedUpdateInstaller(
        out string installerPath,
        out ReleaseUpdatePackage package,
        out long updateStateGeneration)
    {
        if (_downloadedUpdateInstallerPath is { } path
            && _downloadedUpdateTicket is { } ticket
            && _availableUpdate is { } availableUpdate
            && _updateDownloadCoordinator.IsCurrent(ticket)
            && File.Exists(path))
        {
            installerPath = path;
            package = availableUpdate;
            updateStateGeneration = Volatile.Read(ref _updateStateGeneration);
            return true;
        }

        installerPath = string.Empty;
        package = null!;
        updateStateGeneration = 0;
        return false;
    }

    public bool TryBeginInstallerLaunch(
        out string installerPath,
        out ReleaseUpdatePackage package,
        out long updateStateGeneration)
    {
        installerPath = string.Empty;
        package = null!;
        updateStateGeneration = 0;
        if (!_installerLaunchCoordinator.TryBegin()) return false;
        if (TryGetDownloadedUpdateInstaller(out installerPath, out package, out updateStateGeneration)) return true;

        _installerLaunchCoordinator.Complete();
        return false;
    }

    public void CompleteInstallerLaunch() => _installerLaunchCoordinator.Complete();

    public bool IsDownloadedUpdateCurrent(
        string installerPath,
        ReleaseUpdatePackage package,
        long updateStateGeneration) =>
        updateStateGeneration == Volatile.Read(ref _updateStateGeneration)
        && ReferenceEquals(_availableUpdate, package)
        && string.Equals(_downloadedUpdateInstallerPath, installerPath, StringComparison.Ordinal);

    public void ReportUpdateInstallerLaunchFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        SetPlatformStatus($"无法启动更新安装器: {error.Message}");
    }

    public void ReportUpdateInstallerLaunchBlocked(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        SetPlatformStatus(message);
    }

    public void ResetFilters()
    {
        ClearCustomRange();
        ApplyProgrammaticRangeHours(12);
        SearchText = string.Empty;
        SelectAllModels();
        SelectAllAgents();
        ScheduleQuery(DashboardSnapshotApplyPurpose.UserFilter);
    }

    public void SelectAllModels()
    {
        foreach (var option in ModelOptions) option.IsSelected = true;
    }

    public void SelectAllAgents()
    {
        foreach (var option in AgentOptions) option.IsSelected = true;
    }

    public bool TryApplyCustomDateRange(
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        out string validationMessage)
    {
        if (!DashboardCustomRange.TryCreateFromSgtDates(
                startDate,
                endDate,
                out var range,
                out validationMessage))
        {
            return false;
        }

        _customRange = range;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomStartDateSgt)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomEndDateSgt)));
        ScheduleQuery(DashboardSnapshotApplyPurpose.UserFilter);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelAutomaticUpdateChecks();
        _updateDownloadCoordinator.Cancel();
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        _service.StatusChanged -= OnStatusChanged;
        _service.UsageChanged -= OnUsageChanged;
        UnsubscribeOptions();
    }

    public async Task StopAutomaticUpdateChecksAsync()
    {
        CancelAutomaticUpdateChecks();
        var loop = _automaticUpdateLoop;
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _automaticUpdateCancellation?.Dispose();
        _automaticUpdateCancellation = null;
    }

    private async Task RunAutomaticUpdateChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_automaticUpdateSchedule.Start(_timeProvider.GetUtcNow()))
            {
                _ = await CheckForUpdatesCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(TimeSpan.FromHours(6), _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_automaticUpdateSchedule.IsDue(_timeProvider.GetUtcNow()))
                {
                    _ = await CheckForUpdatesCoreAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CancelAutomaticUpdateChecks()
    {
        _automaticUpdateSchedule.Cancel();
        _updateCheckCoordinator.Cancel();
        _automaticUpdateCancellation?.Cancel();
    }

    private async Task ExecuteSnapshotAsync(
        Func<CancellationToken, Task<DashboardSnapshot>> operation,
        DashboardSnapshotApplyPurpose purpose,
        CancellationToken cancellationToken)
    {
        BeginBusy();
        try
        {
            var snapshot = await operation(cancellationToken).ConfigureAwait(false);
            QueueUi(() => ApplySnapshot(snapshot, purpose));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            QueueUi(() => CollectorStatusText = $"采集失败: {error.Message}");
        }
        finally
        {
            EndBusy();
        }
    }

    private void ScheduleQuery(DashboardSnapshotApplyPurpose purpose)
    {
        if (!_initialized || Volatile.Read(ref _disposed) != 0) return;
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        var debounce = new CancellationTokenSource();
        _filterDebounce = debounce;
        var request = CreateRequest();
        _ = QueryAfterDelayAsync(request, purpose, debounce.Token);
    }

    private async Task QueryAfterDelayAsync(
        DashboardQueryRequest request,
        DashboardSnapshotApplyPurpose purpose,
        CancellationToken cancellationToken)
    {
        var ownsQuery = false;
        try
        {
            await Task.Delay(FilterDebounce, cancellationToken).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _queryInFlight, 1, 0) != 0)
            {
                Volatile.Write(ref _pendingQueryPurpose, (int)purpose);
                Interlocked.Exchange(ref _queryPending, 1);
                return;
            }
            ownsQuery = true;
            await ExecuteSnapshotAsync(
                token => _service.QueryAsync(request, token),
                purpose,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ownsQuery
                && Interlocked.Exchange(ref _queryInFlight, 0) != 0
                && Interlocked.Exchange(ref _queryPending, 0) != 0)
            {
                var pendingPurpose = (DashboardSnapshotApplyPurpose)Volatile.Read(ref _pendingQueryPurpose);
                QueueUi(() => ScheduleQuery(pendingPurpose));
            }
        }
    }

    private DashboardQueryRequest CreateRequest()
    {
        var customRange = _customRange;
        var end = customRange?.EndUtc ?? _timeProvider.GetUtcNow();
        var start = customRange?.StartUtc ?? end.AddHours(-RangeHours);
        ImmutableArray<string>? models = ModelOptions.Count == 0 || ModelOptions.All(value => value.IsSelected)
            ? null
            : ModelOptions.Where(value => value.IsSelected).Select(value => value.Model).ToImmutableArray();
        ImmutableArray<SubjectFilter>? subjects = AgentOptions.Count == 0 || AgentOptions.All(value => value.IsSelected)
            ? null
            : AgentOptions.Where(value => value.IsSelected).Select(value => value.Subject).ToImmutableArray();
        return new(start, end, models, subjects, SearchText);
    }

    private void ApplySnapshot(
        DashboardSnapshot snapshot,
        DashboardSnapshotApplyPurpose purpose)
    {
        ApplyStatus(snapshot.Collector, synchronizeDiagnostics: false);
        var summary = snapshot.Result.Summary;
        var totalCost = summary.Cost.Total;
        var input = new DashboardPresentationInput(
            [
                new("总 tokens", FormatTokens(summary.CanonicalTotalTokens)), new("输入", FormatTokens(summary.InputTokens)),
                new("输出", FormatTokens(summary.OutputTokens)), new("未定价", FormatTokens(summary.UnpricedTokens)),
                new("费用", FormatCost(summary.Cost.Total)),
            ],
            DashboardCostComposition.From(summary.Cost),
            snapshot.Result.ByModel
                .OrderBy(row => ModelDisplayOrder(row.Key[0]))
                .ThenBy(row => row.Key[0], StringComparer.Ordinal)
                .Select(row =>
                {
                    var cost = DashboardModelCostPresentation.From(
                        row.Summary.Cost.Total,
                        totalCost,
                        row.Summary.UnpricedTokens == 0);
                    return new ModelUsageRow(
                        row.Key[0], FormatTokens(row.Summary.CanonicalTotalTokens), FormatTokens(row.Summary.UncachedInputTokens),
                        FormatTokens(row.Summary.CachedInputTokens), FormatTokens(row.Summary.OutputTokens),
                        FormatTokens(row.Summary.ReasoningOutputTokens), cost.Cost, cost.Share);
                })
                .ToArray(),
            DashboardSubjectOrdering.SortByDescendingCost(snapshot.Result.ByRole)
                .Select(row => new SubjectUsageRow(
                    SubjectTypeLabel(row.Key[0]), row.Key[1], FormatTokens(row.Summary.CanonicalTotalTokens),
                    FormatTokens(row.Summary.UncachedInputTokens), FormatTokens(row.Summary.CachedInputTokens),
                    FormatTokens(row.Summary.OutputTokens), FormatTokens(row.Summary.ReasoningOutputTokens),
                    FormatCost(row.Summary.Cost.Total), FormatPercentage(row.Summary.Cost.Total, totalCost)))
                .ToArray(),
            [
                .. CreateStatusDiagnostics(),
                CreatePlatformStatusDiagnostic(),
                new("Collector phase", snapshot.Collector.Phase.ToString(), snapshot.Collector.Message),
                new("Pending files", snapshot.Collector.PendingFiles.ToString("N0", CultureInfo.CurrentCulture), $"已知 {snapshot.Collector.FilesKnown:N0} 个源文件"),
                new("Cooperative yields", snapshot.Collector.Diagnostics.CooperativeYieldCount.ToString("N0", CultureInfo.CurrentCulture), "分片主动让出执行权"),
                new("Malformed lines", snapshot.Result.Diagnostics.MalformedLines.ToString("N0", CultureInfo.CurrentCulture), "不可信 JSONL 已跳过"),
                new("扫描文件", snapshot.Collector.Diagnostics.FilesScanned.ToString("N0", CultureInfo.CurrentCulture), "扫描阶段累计处理的源文件"),
                new("重复累计快照", snapshot.Collector.Diagnostics.DuplicateSnapshotsSkipped.ToString("N0", CultureInfo.CurrentCulture), "已跳过的相邻累计快照"),
                new("无拆分快照", snapshot.Collector.Diagnostics.ZeroBreakdownSnapshotsSkipped.ToString("N0", CultureInfo.CurrentCulture), "已跳过的无拆分累计快照"),
                new("关系无效", snapshot.Collector.Diagnostics.InvalidTokenRelationshipsSkipped.ToString("N0", CultureInfo.CurrentCulture), "已跳过的无效 token 关系"),
                new("部分解析源 / 安全跳过",
                    $"{snapshot.Collector.Diagnostics.PartialSources:N0} / {snapshot.Collector.Diagnostics.SafeOpaqueOversizedRecordsSkipped:N0}",
                    "部分解析源 / 超大安全 opaque record"),
            ],
            snapshot.Result.Facets.Models
                .OrderBy(value => ModelDisplayOrder(value.Model))
                .ThenBy(value => value.Model, StringComparer.Ordinal)
                .Select(value => new ModelFilterOption(value.Model))
                .ToArray(),
            snapshot.Result.Facets.Subjects
                .OrderBy(value => DashboardSubjectOrdering.SemanticOrder(
                    UsageAccounting.ThreadTypeText(value.Subject.ThreadType),
                    value.Subject.AgentRole))
                .ThenBy(value => value.Subject.AgentRole, StringComparer.Ordinal)
                .Select(value => new SubjectFilterOption(value.Subject))
                .ToArray());
        var application = _snapshotApplicationLifecycle.Begin(
            purpose,
            _presentation.WouldApplyHaveStructuralChanges(input));
        SnapshotApplying?.Invoke(this, application);
        UnsubscribeOptions();
        var result = _presentation.Apply(input);
        SubscribeOptions();
        SnapshotApplied?.Invoke(this, DashboardSnapshotApplicationLifecycle.Complete(application, result));
    }

    private void SubscribeOptions()
    {
        foreach (var option in ModelOptions) option.PropertyChanged += OnFilterOptionChanged;
        foreach (var option in AgentOptions) option.PropertyChanged += OnFilterOptionChanged;
    }

    private void UnsubscribeOptions()
    {
        foreach (var option in ModelOptions) option.PropertyChanged -= OnFilterOptionChanged;
        foreach (var option in AgentOptions) option.PropertyChanged -= OnFilterOptionChanged;
    }

    private void OnFilterOptionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ModelFilterOption.IsSelected))
            ScheduleQuery(DashboardSnapshotApplyPurpose.UserFilter);
    }

    private async Task LoadStartupStateAsync(CancellationToken cancellationToken)
    {
        var state = await _startupTask.GetLatestStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is not null) QueueUi(() => ApplyStartupState(state));
    }

    private async Task SetStartupEnabledAsync(bool enabled)
    {
        try
        {
            var state = await _startupTask.SetLatestStateAsync(enabled).ConfigureAwait(false);
            if (state is not null) QueueUi(() => ApplyStartupState(state));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyStartupState(PlatformFeatureResult state)
    {
        _suppressStartupUpdate = true;
        IsStartupAvailable = state.IsAvailable;
        IsStartupEnabled = state.IsEnabled;
        SetPlatformStatus(CanCheckUpdates
            ? state.Message
            : $"{state.Message} · {UnconfiguredReleaseUpdateService.DiagnosticMessage}");
        _suppressStartupUpdate = false;
    }

    private void OnStatusChanged(object? sender, DashboardApplicationStatus status) => QueueUi(() =>
    {
        if (status.Collector is not null) ApplyStatus(status.Collector);
        else CollectorStatusText = "正在启动采集器";
    });

    private void OnUsageChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _syncing) == 0)
            QueueUi(() => ScheduleQuery(DashboardSnapshotApplyPurpose.DataRefresh));
    }

    private void ApplyStatus(CollectorStatus status, bool synchronizeDiagnostics = true)
    {
        HealthStatusText = status.Phase switch
        {
            CollectorPhase.Watching => "正常",
            CollectorPhase.Partial => "部分解析",
            CollectorPhase.Syncing => "同步中",
            CollectorPhase.Degraded => "重试中",
            CollectorPhase.Stopped => "已停止",
            _ => "初始化",
        };
        LastReconciliationText = status.LastSuccessfulInventoryUtc is { } reconciled
            ? reconciled.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : "—";
        SourceFilesText = status.FilesKnown.ToString("N0", CultureInfo.CurrentCulture);
        RealtimeVoiceSessionsText = status.RealtimeVoiceSessions.ToString("N0", CultureInfo.CurrentCulture);
        RetryQueueText = status.PendingFiles.ToString("N0", CultureInfo.CurrentCulture);
        WatcherStatusText = status.Phase switch
        {
            CollectorPhase.Watching => "运行中",
            CollectorPhase.Partial => "运行中 · 部分解析",
            CollectorPhase.Syncing => "同步中",
            CollectorPhase.Degraded => "重试中",
            CollectorPhase.Stopped => "已停止",
            _ => "启动中",
        };
        var coverage = CoveragePresentation.From(status.ObservationCoverage, status.ObservationGap);
        CoverageText = coverage.Text;
        CollectorStatusText = status.Message;
        HeaderStatusText = $"{WatcherStatusText} · 上次对账 {LastReconciliationText}";
        var headerPresentation = DashboardHeaderStatusPresentation.From(status.Phase);
        HeaderStatusGlyph = headerPresentation.Glyph;
        HeaderStatusBrush = HeaderStatusBrushFor(headerPresentation.Tone);
        if (synchronizeDiagnostics) SynchronizeStatusDiagnostics();
    }

    private DiagnosticRow[] CreateStatusDiagnostics() =>
    [
        new("健康状态", HealthStatusText, $"Watcher: {WatcherStatusText}"),
        new("Watcher", WatcherStatusText, CollectorStatusText),
        new("上次对账", LastReconciliationText, "最近完成的全量对账"),
        new("源文件", SourceFilesText, "已发现的 rollout JSONL"),
        new("重试队列", RetryQueueText, "等待处理的源文件"),
        new("观察覆盖", CoverageText, "本次运行的数据观察范围"),
    ];

    private void SynchronizeStatusDiagnostics()
    {
        _presentation.UpdateDiagnosticsSubset(CreateStatusDiagnostics());
    }

    private DiagnosticRow CreatePlatformStatusDiagnostic() => new(
        "操作状态",
        PlatformStatusText,
        "检查更新和 CSV 导出结果");

    private void SetPlatformStatus(string value)
    {
        PlatformStatusText = value;
        _presentation.UpdateDiagnosticsSubset([CreatePlatformStatusDiagnostic()]);
    }

    private void ApplyUpdateCheck(ReleaseUpdateCheckResult result, long generation)
    {
        if (generation != Volatile.Read(ref _updateStateGeneration)) return;
        _availableUpdate = result.IsUpdateAvailable ? result.Package : null;
        _downloadedUpdateInstallerPath = null;
        _downloadedUpdateTicket = null;
        ResetUpdateDownloadPresentation();

        QueuePropertyChanged(nameof(CanDownloadUpdate));
        QueuePropertyChanged(nameof(CanRunDownloadedUpdate));
        QueuePropertyChanged(nameof(DownloadUpdateLabel));
        QueuePropertyChanged(nameof(RunUpdateLabel));
        SetPlatformStatus(result.Message);
    }

    private void ApplyUpdateDownload(
        ReleaseUpdateDownloadResult result,
        ReleaseUpdateDownloadTicket ticket)
    {
        if (!_updateDownloadCoordinator.IsCurrent(ticket)
            || _activeUpdateDownloadTicket != ticket)
        {
            return;
        }

        _activeUpdateDownloadTicket = null;
        IsDownloading = false;

        if (result.Status == ReleaseUpdateDownloadStatus.Completed
            && !string.IsNullOrWhiteSpace(result.InstallerPath))
        {
            _downloadedUpdateInstallerPath = result.InstallerPath;
            _downloadedUpdateTicket = ticket;
            QueuePropertyChanged(nameof(CanDownloadUpdate));
            QueuePropertyChanged(nameof(CanRunDownloadedUpdate));
        }

        SetPlatformStatus(result.Message);
    }

    private void StartUpdateDownload(ReleaseUpdateDownloadTicket ticket)
    {
        if (!_updateDownloadCoordinator.IsCurrent(ticket)) return;

        _activeUpdateDownloadTicket = ticket;
        IsDownloading = true;
        UpdateDownloadProgressPercent = 0;
        IsUpdateDownloadIndeterminate = true;
        UpdateDownloadProgressText = "下载中";
    }

    private void ApplyUpdateDownloadProgress(
        ReleaseUpdateDownloadProgress progress,
        ReleaseUpdateDownloadTicket ticket)
    {
        if (_activeUpdateDownloadTicket != ticket
            || !_updateDownloadCoordinator.IsCurrent(ticket))
        {
            return;
        }

        if (progress.TotalBytes is not > 0)
        {
            IsUpdateDownloadIndeterminate = true;
            UpdateDownloadProgressText = "下载中";
            return;
        }

        var received = Math.Clamp(progress.BytesReceived, 0, progress.TotalBytes.Value);
        var percent = received * 100d / progress.TotalBytes.Value;
        UpdateDownloadProgressPercent = percent;
        IsUpdateDownloadIndeterminate = false;
        UpdateDownloadProgressText = $"下载中 {percent:F0}%";
    }

    private void ResetUpdateDownloadPresentation()
    {
        _activeUpdateDownloadTicket = null;
        IsDownloading = false;
        UpdateDownloadProgressPercent = 0;
        IsUpdateDownloadIndeterminate = true;
        UpdateDownloadProgressText = "下载中";
    }


    private static Brush HeaderStatusBrushFor(DashboardHeaderStatusTone tone) => new SolidColorBrush(tone switch
    {
        DashboardHeaderStatusTone.Accent => Color.FromArgb(0xFF, 0x5B, 0x91, 0xE8),
        DashboardHeaderStatusTone.Success => Color.FromArgb(0xFF, 0x63, 0xC5, 0xA6),
        DashboardHeaderStatusTone.Warning => Color.FromArgb(0xFF, 0xE2, 0xA4, 0x4F),
        DashboardHeaderStatusTone.Danger => Color.FromArgb(0xFF, 0xCF, 0x69, 0x7C),
        _ => Color.FromArgb(0xFF, 0x82, 0x90, 0xA3),
    });

    private void BeginBusy()
    {
        Interlocked.Increment(ref _busyCount);
        QueuePropertyChanged(nameof(IsBusy));
    }

    private bool ClearCustomRange()
    {
        if (_customRange is null) return false;
        _customRange = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomStartDateSgt)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomEndDateSgt)));
        return true;
    }

    private void ApplyProgrammaticRangeHours(double value)
    {
        var transition = DashboardTimeRangeTransition.FromProgrammaticHours(_rangeHours, value);
        var positionChanged = !NearlyEquals(_rangeScalePosition, transition.Selection.Position);

        _rangeHours = transition.Selection.Hours;
        _rangeScalePosition = transition.Selection.Position;

        if (transition.HoursChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeHours)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeLabel)));
        }

        if (positionChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeScalePosition)));
        }

    }

    private static bool NearlyEquals(double left, double right) => Math.Abs(left - right) < 0.0000001;

    private void EndBusy()
    {
        Interlocked.Decrement(ref _busyCount);
        QueuePropertyChanged(nameof(IsBusy));
    }

    private void QueueUi(Action action)
    {
        if (Volatile.Read(ref _disposed) == 0) _dispatcher.TryEnqueue(action);
    }

    private void QueuePropertyChanged(string propertyName) => QueueUi(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(RangeHours)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RangeLabel)));
        return true;
    }

    private static string FormatPercentage(decimal value, decimal total) => total > 0 ? $"{value / total:P1}" : "0.0%";
    private static string FormatCost(decimal value) => $"${value:N1}";
    private static int ModelDisplayOrder(string model) => model switch
    {
        "gpt-5.6-sol" => 0,
        "gpt-5.6-terra" => 1,
        "gpt-5.6-luna" => 2,
        "codex-auto-review" => 3,
        "Others" => 4,
        _ => 5,
    };

    private static string SubjectTypeLabel(string threadType) => threadType switch
    {
        "main" => "主线程",
        "subagent" => "子代理",
        _ => threadType,
    };

    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:F1}B",
        >= 1_000_000 => $"{value / 1_000_000d:F1}M",
        >= 1_000 => $"{value / 1_000d:F1}K",
        _ => value.ToString("N0", CultureInfo.CurrentCulture),
    };

}
