using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexUsage.App.Models;
using CodexUsage.App.Services;
using CodexUsage.Application;
using CodexUsage.Domain;
using CodexUsage.Infrastructure.Collection;

namespace CodexUsage.App.ViewModels;

public sealed class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(350);
    private readonly IUsageDashboardService _service;
    private readonly IUiDispatcher _dispatcher;
    private readonly StartupRegistrationCoordinator _startupTask;
    private readonly IReleaseUpdateService _packageUpdate;
    private readonly IExportDestinationPicker _exportPicker;
    private CancellationTokenSource? _filterDebounce;
    private bool _isStartupEnabled;
    private bool _isStartupAvailable;
    private bool _suppressStartupUpdate;
    private bool _initialized;
    private double _rangeHours = 12;
    private string _searchText = string.Empty;
    private string _lastReconciliationText = "正在建立账目...";
    private string _coverageText = "等待首次对账";
    private string _collectorStatusText = "正在启动采集器";
    private string _platformStatusText;
    private string _conflictTitle = string.Empty;
    private string _conflictMessage = string.Empty;
    private bool _hasConflicts;
    private int _busyCount;
    private int _queryInFlight;
    private int _queryPending;
    private int _syncing;
    private int _disposed;

    public DashboardViewModel(
        IUsageDashboardService service,
        IUiDispatcher dispatcher,
        IStartupRegistrationService startupTask,
        IReleaseUpdateService packageUpdate,
        IExportDestinationPicker exportPicker)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _startupTask = new StartupRegistrationCoordinator(startupTask ?? throw new ArgumentNullException(nameof(startupTask)));
        _packageUpdate = packageUpdate ?? throw new ArgumentNullException(nameof(packageUpdate));
        _exportPicker = exportPicker ?? throw new ArgumentNullException(nameof(exportPicker));
        _platformStatusText = packageUpdate.IsAvailable
            ? "Release feed 可用"
            : UnconfiguredReleaseUpdateService.DiagnosticMessage;
        _service.StatusChanged += OnStatusChanged;
        _service.UsageChanged += OnUsageChanged;

        Metrics = [new("总 tokens", "0"), new("输入", "0"), new("输出", "0"), new("未定价", "0"), new("费用", "$0.0")];
        CostSlices =
        [
            new("无缓存输入", 0, "$0.0 · 0.0%", "PrimaryBrush"),
            new("缓存输入", 0, "$0.0 · 0.0%", "SuccessBrush"),
            new("思考输出", 0, "$0.0 · 0.0%", "WarningBrush"),
            new("其他输出", 0, "$0.0 · 0.0%", "PurpleBrush"),
        ];
        Models = [];
        Subjects = [];
        Diagnostics = [];
        RunStatistics = [new("扫描文件", "0"), new("重复累计快照", "0"), new("无拆分快照", "0"), new("关系无效", "0")];
        ModelOptions = [];
        AgentOptions = [];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MetricCard> Metrics { get; }
    public ObservableCollection<CostSlice> CostSlices { get; }
    public ObservableCollection<ModelUsageRow> Models { get; }
    public ObservableCollection<SubjectUsageRow> Subjects { get; }
    public ObservableCollection<DiagnosticRow> Diagnostics { get; }
    public ObservableCollection<RunStatistic> RunStatistics { get; }
    public ObservableCollection<ModelFilterOption> ModelOptions { get; }
    public ObservableCollection<SubjectFilterOption> AgentOptions { get; }

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

    public double RangeHours
    {
        get => _rangeHours;
        set
        {
            if (SetProperty(ref _rangeHours, value)) ScheduleQuery();
        }
    }

    public string RangeLabel => $"{RangeHours:F1}小时";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) ScheduleQuery();
        }
    }

    public string LastReconciliationText { get => _lastReconciliationText; private set => SetProperty(ref _lastReconciliationText, value); }
    public string CoverageText { get => _coverageText; private set => SetProperty(ref _coverageText, value); }
    public string CollectorStatusText { get => _collectorStatusText; private set => SetProperty(ref _collectorStatusText, value); }
    public string PlatformStatusText { get => _platformStatusText; private set => SetProperty(ref _platformStatusText, value); }
    public string ConflictTitle { get => _conflictTitle; private set => SetProperty(ref _conflictTitle, value); }
    public string ConflictMessage { get => _conflictMessage; private set => SetProperty(ref _conflictMessage, value); }
    public bool HasConflicts { get => _hasConflicts; private set => SetProperty(ref _hasConflicts, value); }
    public bool IsBusy => Volatile.Read(ref _busyCount) > 0;
    public bool IsSyncing => Volatile.Read(ref _syncing) != 0;
    public bool CanSync => !IsSyncing;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var request = CreateRequest();
        var startup = LoadStartupStateAsync(cancellationToken);
        await ExecuteSnapshotAsync(token => _service.StartAsync(request, token), cancellationToken).ConfigureAwait(false);
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
            await ExecuteSnapshotAsync(token => _service.RefreshAsync(request, token), cancellationToken).ConfigureAwait(false);
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
            QueueUi(() => PlatformStatusText = result.Status == CsvExportStatus.Cancelled
                ? "CSV 导出已取消"
                : $"已导出 {result.EventCount:N0} 条记录: {result.OutputPath}");
        }
        catch (OperationCanceledException)
        {
            QueueUi(() => PlatformStatusText = "CSV 导出已取消");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            QueueUi(() => PlatformStatusText = $"导出失败: {error.Message}");
        }
        finally
        {
            if (busyStarted) EndBusy();
        }
    }

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        BeginBusy();
        try
        {
            var result = await _packageUpdate.CheckAsync(cancellationToken).ConfigureAwait(false);
            QueueUi(() => PlatformStatusText = result.Message);
        }
        finally
        {
            EndBusy();
        }
    }

    public void ResetFilters()
    {
        RangeHours = 12;
        SearchText = string.Empty;
        foreach (var option in ModelOptions) option.IsSelected = true;
        foreach (var option in AgentOptions) option.IsSelected = true;
        ScheduleQuery();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        _service.StatusChanged -= OnStatusChanged;
        _service.UsageChanged -= OnUsageChanged;
        UnsubscribeOptions();
    }

    private async Task ExecuteSnapshotAsync(
        Func<CancellationToken, Task<DashboardSnapshot>> operation,
        CancellationToken cancellationToken)
    {
        BeginBusy();
        try
        {
            var snapshot = await operation(cancellationToken).ConfigureAwait(false);
            QueueUi(() => ApplySnapshot(snapshot));
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

    private void ScheduleQuery()
    {
        if (!_initialized || Volatile.Read(ref _disposed) != 0) return;
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        var debounce = new CancellationTokenSource();
        _filterDebounce = debounce;
        var request = CreateRequest();
        _ = QueryAfterDelayAsync(request, debounce.Token);
    }

    private async Task QueryAfterDelayAsync(DashboardQueryRequest request, CancellationToken cancellationToken)
    {
        var ownsQuery = false;
        try
        {
            await Task.Delay(FilterDebounce, cancellationToken).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _queryInFlight, 1, 0) != 0)
            {
                Interlocked.Exchange(ref _queryPending, 1);
                return;
            }
            ownsQuery = true;
            await ExecuteSnapshotAsync(token => _service.QueryAsync(request, token), cancellationToken).ConfigureAwait(false);
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
                QueueUi(ScheduleQuery);
            }
        }
    }

    private DashboardQueryRequest CreateRequest()
    {
        var end = DateTimeOffset.UtcNow;
        ImmutableArray<string>? models = ModelOptions.Count == 0 || ModelOptions.All(value => value.IsSelected)
            ? null
            : ModelOptions.Where(value => value.IsSelected).Select(value => value.Model).ToImmutableArray();
        ImmutableArray<SubjectFilter>? subjects = AgentOptions.Count == 0 || AgentOptions.All(value => value.IsSelected)
            ? null
            : AgentOptions.Where(value => value.IsSelected).Select(value => value.Subject).ToImmutableArray();
        return new(end.AddHours(-RangeHours), end, models, subjects, SearchText);
    }

    private void ApplySnapshot(DashboardSnapshot snapshot)
    {
        var summary = snapshot.Result.Summary;
        Replace(Metrics,
        [
            new("总 tokens", FormatTokens(summary.CanonicalTotalTokens)), new("输入", FormatTokens(summary.InputTokens)),
            new("输出", FormatTokens(summary.OutputTokens)), new("未定价", FormatTokens(summary.UnpricedTokens)),
            new("费用", FormatCost(summary.Cost.Total)),
        ]);
        var totalCost = summary.Cost.Total;
        Replace(CostSlices,
        [
            CostSliceFor("无缓存输入", summary.Cost.UncachedInput, totalCost, "PrimaryBrush"),
            CostSliceFor("缓存输入", summary.Cost.CachedInput, totalCost, "SuccessBrush"),
            CostSliceFor("思考输出", summary.Cost.ReasoningOutput, totalCost, "WarningBrush"),
            CostSliceFor("其他输出", summary.Cost.OtherOutput, totalCost, "PurpleBrush"),
        ]);
        Replace(Models, snapshot.Result.ByModel.Select(row => new ModelUsageRow(
            row.Key[0], FormatTokens(row.Summary.CanonicalTotalTokens), FormatTokens(row.Summary.CachedInputTokens),
            FormatTokens(row.Summary.OutputTokens), FormatCost(row.Summary.Cost.Total),
            FormatPercentage(row.Summary.Cost.Total, totalCost))));
        Replace(Subjects, snapshot.Result.ByRole.Select(row => new SubjectUsageRow(
            row.Key[0], row.Key[1], FormatTokens(row.Summary.CanonicalTotalTokens), FormatTokens(row.Summary.OutputTokens),
            FormatCost(row.Summary.Cost.Total), FormatPercentage(row.Summary.Cost.Total, totalCost))));
        Replace(RunStatistics,
        [
            new("扫描文件", snapshot.Collector.Diagnostics.FilesScanned.ToString("N0", CultureInfo.CurrentCulture)),
            new("重复累计快照", snapshot.Collector.Diagnostics.DuplicateSnapshotsSkipped.ToString("N0", CultureInfo.CurrentCulture)),
            new("无拆分快照", snapshot.Collector.Diagnostics.ZeroBreakdownSnapshotsSkipped.ToString("N0", CultureInfo.CurrentCulture)),
            new("关系无效", snapshot.Collector.Diagnostics.InvalidTokenRelationshipsSkipped.ToString("N0", CultureInfo.CurrentCulture)),
        ]);
        Replace(Diagnostics,
        [
            new("Collector phase", snapshot.Collector.Phase.ToString(), snapshot.Collector.Message),
            new("Pending files", snapshot.Collector.PendingFiles.ToString("N0", CultureInfo.CurrentCulture), $"已知 {snapshot.Collector.FilesKnown:N0} 个源文件"),
            new("Conflicts", snapshot.Collector.Conflicts.ToString("N0", CultureInfo.CurrentCulture), "canonical 冲突保持旧账"),
            new("Cooperative yields", snapshot.Collector.Diagnostics.CooperativeYieldCount.ToString("N0", CultureInfo.CurrentCulture), "分片主动让出执行权"),
            new("Malformed lines", snapshot.Result.Diagnostics.MalformedLines.ToString("N0", CultureInfo.CurrentCulture), "不可信 JSONL 已跳过"),
            new("Efficiency Mode", snapshot.EfficiencyMode.IsFullyEnabled ? "Enabled" : "Partial/unavailable", snapshot.EfficiencyMode.Message),
        ]);
        UpdateFacetOptions(snapshot.Result.Facets);
        ApplyStatus(snapshot.Collector, snapshot.EfficiencyMode);
    }

    private void UpdateFacetOptions(QueryFacets facets)
    {
        var previousModels = ModelOptions.ToDictionary(value => value.Model, value => value.IsSelected, StringComparer.Ordinal);
        var previousSubjects = AgentOptions.ToDictionary(value => value.Subject, value => value.IsSelected);
        UnsubscribeOptions();
        Replace(ModelOptions, facets.Models.Select(value => new ModelFilterOption(
            value.Model, previousModels.GetValueOrDefault(value.Model, true))));
        Replace(AgentOptions, facets.Subjects.Select(value => new SubjectFilterOption(
            value.Subject, previousSubjects.GetValueOrDefault(value.Subject, true))));
        SubscribeOptions();
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
        if (args.PropertyName == nameof(ModelFilterOption.IsSelected)) ScheduleQuery();
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
        PlatformStatusText = CanCheckUpdates
            ? state.Message
            : $"{state.Message} · {UnconfiguredReleaseUpdateService.DiagnosticMessage}";
        _suppressStartupUpdate = false;
    }

    private void OnStatusChanged(object? sender, DashboardApplicationStatus status) => QueueUi(() =>
    {
        if (status.Collector is not null) ApplyStatus(status.Collector, status.EfficiencyMode);
        else CollectorStatusText = $"{status.Message} · {status.EfficiencyMode.Message}";
    });

    private void OnUsageChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _syncing) == 0) QueueUi(ScheduleQuery);
    }

    private void ApplyStatus(CollectorStatus status, ProcessEfficiencyModeResult efficiency)
    {
        LastReconciliationText = status.LastSuccessfulInventoryUtc is { } reconciled ? $"最近对账 {reconciled.ToLocalTime():HH:mm:ss}" : "尚未完成首次对账";
        CoverageText = status.ObservationCoverage switch
        {
            ObservationCoverage.Gap when status.ObservationGap is { } gap => $"{gap.StartUtc.ToLocalTime():HH:mm:ss} - {gap.EndUtc.ToLocalTime():HH:mm:ss} 未观测",
            ObservationCoverage.Continuous => "持续观测中",
            _ => "已建立本次运行基线",
        };
        CollectorStatusText = $"{status.Message} · {efficiency.Message}";
        HasConflicts = status.Conflicts > 0;
        ConflictTitle = $"{status.Conflicts:N0} 个源文件冲突";
        ConflictMessage = "现有账目已保护；自动恢复仅接受稳定的 canonical 同路径改写";
    }

    private void BeginBusy()
    {
        Interlocked.Increment(ref _busyCount);
        QueuePropertyChanged(nameof(IsBusy));
    }

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

    private static CostSlice CostSliceFor(string label, decimal value, decimal total, string brushKey)
    {
        var percentage = total > 0 ? decimal.ToDouble(value / total * 100) : 0;
        return new(label, percentage, $"{FormatCost(value)} · {percentage:F1}%", brushKey);
    }

    private static string FormatPercentage(decimal value, decimal total) => total > 0 ? $"{value / total:P1}" : "0.0%";
    private static string FormatCost(decimal value) => $"${value:N1}";
    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:F1}B",
        >= 1_000_000 => $"{value / 1_000_000d:F1}M",
        >= 1_000 => $"{value / 1_000d:F1}K",
        _ => value.ToString("N0", CultureInfo.CurrentCulture),
    };

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}
