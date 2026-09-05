using System.Collections.Immutable;
using System.ComponentModel;
using CodexUsage.App.Services;
using CodexUsage.App.ViewModels;
using CodexUsage.Application;
using CodexUsage.Domain;
using CodexUsage.Infrastructure.Collection;
using Xunit;

namespace CodexUsage.App.Tests;

public sealed class DashboardViewModelTests
{
    private const string DirectMainThreadId = "019fe0d7-dd64-7412-8fa0-ea96334569dd";
    private const string FirstCollidingMainThreadId = "019fe0d7-dd65-7412-8fa0-ea96334569dd";
    private const string SecondCollidingMainThreadId = "019fe0d7-dd66-7412-8fa0-ea96334569dd";

    [Fact]
    public async Task SnapshotPresentsBaselineActualCostAndLongContextRateMetrics()
    {
        var cost = new CostBreakdown(2m, 0.8m, 3.15m, 1.35m, 7.3m, 4.4m, 2.9m, Priced: true);
        var service = new FakeUsageDashboardService(Snapshot("priced", [], cost));
        using var viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync();

        Assert.Collection(
            viewModel.Metrics,
            metric => Assert.Equal(("总 tokens", "0"), (metric.Label, metric.Value)),
            metric => Assert.Equal(("输入", "0"), (metric.Label, metric.Value)),
            metric => Assert.Equal(("输出", "0"), (metric.Label, metric.Value)),
            metric => Assert.Equal(("基准费用", "$4.4"), (metric.Label, metric.Value)),
            metric => Assert.Equal(("实际费用", "$7.3"), (metric.Label, metric.Value)),
            metric => Assert.Equal(("长上下文费用率", "×1.66"), (metric.Label, metric.Value)));
    }

    [Fact]
    public async Task SnapshotSuppressesMultiplierWhenThereIsNoPricedBaseline()
    {
        var service = new FakeUsageDashboardService(Snapshot("unpriced", [], CostBreakdown.UnpricedZero));
        using var viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync();

        Assert.Equal("$0.0", viewModel.Metrics.Single(metric => metric.Label == "基准费用").Value);
        Assert.Equal("$0.0", viewModel.Metrics.Single(metric => metric.Label == "实际费用").Value);
        Assert.Equal("—", viewModel.Metrics.Single(metric => metric.Label == "长上下文费用率").Value);
    }

    [Fact]
    public async Task SnapshotPresentsLongContextRateAndActualShareForModelAndRoleRows()
    {
        var modelCost = new CostBreakdown(1m, 1m, 1m, 0m, 6m, 4m, 2m, Priced: true);
        var unpricedCost = CostBreakdown.UnpricedZero;
        var roleCost = new CostBreakdown(1m, 1m, 1m, 0m, 4m, 4m, 0m, Priced: true);
        var service = new FakeUsageDashboardService(Snapshot(
            "rows",
            [],
            new CostBreakdown(2m, 2m, 2m, 0m, 10m, 8m, 2m, Priced: true),
            [
                new GroupRow(["gpt-5.6-sol"], Summary(modelCost)),
                new GroupRow(["unknown"], Summary(unpricedCost, unpricedTokens: 2)),
            ],
            [
                new RoleUsageRow(ThreadType.Main, "root", 1, Summary(roleCost, unpricedTokens: 1)),
                new RoleUsageRow(ThreadType.Unknown, "unknown", 1, Summary(unpricedCost, unpricedTokens: 2)),
            ]));
        using var viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync();

        Assert.Collection(
            viewModel.Models,
            row =>
            {
                Assert.Equal("×1.50", row.LongContextRate);
                Assert.Equal("60.0%", row.Share);
            },
            row =>
            {
                Assert.Equal("—", row.LongContextRate);
                Assert.Equal("—", row.Share);
            });
        Assert.Collection(
            viewModel.Subjects,
            row =>
            {
                Assert.Equal("×1.00", row.LongContextRate);
                Assert.Equal("40.0%", row.Share);
            },
            row =>
            {
                Assert.Equal("—", row.LongContextRate);
                Assert.Equal("—", row.Share);
            });
    }

    [Fact]
    public async Task SnapshotOrdersAstraBeforeOtherPricedModels()
    {
        var modelCost = new CostBreakdown(1m, 1m, 1m, 0m, 3m, 3m, 0m, Priced: true);
        var service = new FakeUsageDashboardService(Snapshot(
            "model-order",
            [],
            modelCost,
            [
                new GroupRow(["gpt-5.6-luna"], Summary(modelCost)),
                new GroupRow(["Others"], Summary(CostBreakdown.UnpricedZero, unpricedTokens: 2)),
                new GroupRow(["gpt-5.6-sol"], Summary(modelCost)),
                new GroupRow(["gpt-6-astra"], Summary(modelCost)),
                new GroupRow(["gpt-5.6-terra"], Summary(modelCost)),
            ]));
        using var viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync();

        Assert.Collection(
            viewModel.Models,
            row => Assert.Equal("gpt-6-astra", row.Model),
            row => Assert.Equal("gpt-5.6-sol", row.Model),
            row => Assert.Equal("gpt-5.6-terra", row.Model),
            row => Assert.Equal("gpt-5.6-luna", row.Model),
            row => Assert.Equal("Others", row.Model));
    }

    [Fact]
    public async Task SnapshotPresentsGuardianReviewAsItsOwnThreadType()
    {
        var service = new FakeUsageDashboardService(Snapshot(
            "guardian",
            [],
            CostBreakdown.UnpricedZero,
            byRole:
            [
                new RoleUsageRow(
                    ThreadType.GuardianReview,
                    "guardian",
                    1,
                    Summary(CostBreakdown.UnpricedZero, unpricedTokens: 2)),
            ]));
        using var viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync();

        var row = Assert.Single(viewModel.Subjects);
        Assert.Equal("guardian_review", row.ThreadType);
        Assert.Equal("guardian", row.Role);
    }

    [Fact]
    public async Task SnapshotSurfacesCollectorConflictsInHealthAndHeaderDiagnostics()
    {
        var service = new FakeUsageDashboardService(Snapshot(
            "Watcher changes are current",
            [],
            phase: CollectorPhase.Degraded,
            conflicts: 3));
        using var viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync();

        Assert.Contains("冲突 3", viewModel.HealthStatusText);
        Assert.Contains("冲突 3", viewModel.HeaderStatusText);
        Assert.Contains(viewModel.Diagnostics, row =>
            row.Label == "健康状态"
            && row.Value.Contains("冲突 3", StringComparison.Ordinal)
            && row.Detail.Contains("source conflict", StringComparison.Ordinal));
        Assert.Contains(viewModel.Diagnostics, row =>
            row.Label == "Watcher"
            && row.Detail.Contains("3 个 source conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MainThreadFilterKeepsSelectionWhenAnOptionDisappearsAndReselectsRemainingOptionAfterClear()
    {
        var firstThread = MainThread(FirstCollidingMainThreadId, 100);
        var secondThread = MainThread(SecondCollidingMainThreadId, 99);
        var service = new FakeUsageDashboardService(Snapshot("initial", [firstThread, secondThread]));
        service.EnqueueQuerySnapshot(Snapshot("entered", [firstThread, secondThread]));
        var replacementThreads = Enumerable.Range(0, 21)
            .Select(index => MainThread($"019fe0d7-{index:D4}-7412-8fa0-ea9633456{index:D3}", index))
            .ToArray();
        service.EnqueueQuerySnapshot(Snapshot("selected", replacementThreads));
        service.EnqueueQuerySnapshot(Snapshot("selected-replacement", replacementThreads));
        service.EnqueueQuerySnapshot(Snapshot("cleared", replacementThreads));
        service.EnqueueQuerySnapshot(Snapshot("reselected", replacementThreads));
        using var viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync();
        await ApplySnapshotAsync(viewModel, "entered", () => viewModel.MainThreadInputText = DirectMainThreadId);

        Assert.Equal(DirectMainThreadId, viewModel.MainThreadInputText);
        Assert.Equal(DirectMainThreadId, viewModel.SelectedMainThreadId);

        var firstOption = viewModel.MainThreadOptions[0];
        var secondOption = viewModel.MainThreadOptions[1];
        Assert.Equal(firstOption.DisplayLabel, secondOption.DisplayLabel);
        await ApplySnapshotAsync(viewModel, "selected", () => viewModel.SelectMainThreadOption(secondOption));

        Assert.Equal(secondOption.DisplayLabel, viewModel.MainThreadInputText);
        Assert.Equal(SecondCollidingMainThreadId, viewModel.SelectedMainThreadId);
        Assert.Equal(20, viewModel.MainThreadOptions.Count);
        Assert.DoesNotContain(viewModel.MainThreadOptions, value => value.ConversationId == SecondCollidingMainThreadId);
        Assert.Same(secondOption, viewModel.SelectedMainThreadOption);
        Assert.Equal(SecondCollidingMainThreadId, viewModel.SelectedMainThreadId);

        viewModel.MainThreadInputText = "not-a-thread-id";

        Assert.Equal("not-a-thread-id", viewModel.MainThreadInputText);
        Assert.True(viewModel.HasMainThreadInputError);
        Assert.NotEmpty(viewModel.MainThreadInputValidationMessage);
        Assert.Same(secondOption, viewModel.SelectedMainThreadOption);
        Assert.Equal(SecondCollidingMainThreadId, viewModel.SelectedMainThreadId);
        Assert.Equal(2, service.QueryRequests.Count);

        viewModel.MainThreadInputText = secondOption.DisplayLabel;

        Assert.False(viewModel.HasMainThreadInputError);
        Assert.Equal(secondOption.DisplayLabel, viewModel.MainThreadInputText);
        Assert.Equal(2, service.QueryRequests.Count);

        viewModel.MainThreadInputText = "not-a-thread-id";
        viewModel.SelectMainThreadOption(secondOption);

        Assert.False(viewModel.HasMainThreadInputError);
        Assert.Equal(string.Empty, viewModel.MainThreadInputValidationMessage);
        Assert.Equal(secondOption.DisplayLabel, viewModel.MainThreadInputText);
        Assert.Equal(2, service.QueryRequests.Count);

        var replacementOption = viewModel.MainThreadOptions[0];
        await ApplySnapshotAsync(viewModel, "selected-replacement", () => viewModel.SelectMainThreadOption(replacementOption));

        await ApplySnapshotAsync(viewModel, "cleared", () => viewModel.MainThreadInputText = string.Empty);

        Assert.Null(viewModel.SelectedMainThreadId);
        Assert.Same(replacementOption, viewModel.MainThreadOptions[0]);
        await ApplySnapshotAsync(viewModel, "reselected", () => viewModel.SelectMainThreadOption(replacementOption));

        Assert.Equal(replacementOption.ConversationId, viewModel.SelectedMainThreadId);

        Assert.Collection(
            service.QueryRequests,
            value => Assert.Equal(DirectMainThreadId, value.MainThreadConversationId),
            value => Assert.Equal(SecondCollidingMainThreadId, value.MainThreadConversationId),
            value => Assert.Equal(replacementOption.ConversationId, value.MainThreadConversationId),
            value => Assert.Null(value.MainThreadConversationId),
            value => Assert.Equal(replacementOption.ConversationId, value.MainThreadConversationId));
    }

    [Fact]
    public async Task MainThreadInputCanonicalizesValidUuidAndRejectsInvalidTextWithoutChangingAppliedFilter()
    {
        var service = new FakeUsageDashboardService(Snapshot("initial", []));
        service.EnqueueQuerySnapshot(Snapshot("canonical", []));
        service.EnqueueQuerySnapshot(Snapshot("cleared", []));
        using var viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync();
        viewModel.MainThreadInputText = "not-a-thread-id";

        Assert.True(viewModel.HasMainThreadInputError);
        Assert.NotEmpty(viewModel.MainThreadInputValidationMessage);
        Assert.Null(viewModel.SelectedMainThreadId);
        Assert.Null(viewModel.SelectedMainThreadOption);
        Assert.Empty(service.QueryRequests);

        viewModel.MainThreadInputText = "  \t  ";

        Assert.Equal(string.Empty, viewModel.MainThreadInputText);
        Assert.False(viewModel.HasMainThreadInputError);
        Assert.Empty(service.QueryRequests);

        await ApplySnapshotAsync(
            viewModel,
            "canonical",
            () => viewModel.MainThreadInputText = $"  {DirectMainThreadId.ToUpperInvariant()}  ");

        Assert.Equal(DirectMainThreadId, viewModel.MainThreadInputText);
        Assert.Equal(DirectMainThreadId, viewModel.SelectedMainThreadId);
        Assert.False(viewModel.HasMainThreadInputError);
        Assert.Equal(string.Empty, viewModel.MainThreadInputValidationMessage);

        viewModel.MainThreadInputText = "still-not-a-thread-id";

        Assert.True(viewModel.HasMainThreadInputError);
        Assert.Equal(DirectMainThreadId, viewModel.SelectedMainThreadId);
        Assert.Single(service.QueryRequests);

        await ApplySnapshotAsync(viewModel, "cleared", () => viewModel.MainThreadInputText = "  \t  ");

        Assert.Equal(string.Empty, viewModel.MainThreadInputText);
        Assert.Null(viewModel.SelectedMainThreadId);
        Assert.Null(viewModel.SelectedMainThreadOption);
        Assert.False(viewModel.HasMainThreadInputError);
        Assert.Equal(string.Empty, viewModel.MainThreadInputValidationMessage);
        Assert.Collection(
            service.QueryRequests,
            value => Assert.Equal(DirectMainThreadId, value.MainThreadConversationId),
            value => Assert.Null(value.MainThreadConversationId));
    }

    [Fact]
    public async Task DataRefreshInterleavedWithThreadSelectionsAppliesOnlyLatestSelection()
    {
        var firstQuery = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshQuery = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeUsageDashboardService(Snapshot("initial", []));
        service.EnqueueQueryTask(firstQuery.Task);
        service.EnqueueQueryTask(refreshQuery.Task);
        service.EnqueueQuerySnapshot(Snapshot("selected-b", []));
        using var viewModel = CreateViewModel(service);
        var applied = 0;

        await viewModel.InitializeAsync();
        var selectedBSnapshotApplied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SnapshotApplied += (_, _) =>
        {
            applied++;
            if (viewModel.CollectorStatusText == "selected-b") selectedBSnapshotApplied.TrySetResult(true);
        };
        viewModel.MainThreadInputText = FirstCollidingMainThreadId;
        await service.FirstQueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        service.RaiseUsageChanged();
        Assert.True(service.QueryCancellationTokens[0].IsCancellationRequested);

        firstQuery.SetResult(Snapshot("stale-first", []));
        await service.SecondQueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        viewModel.MainThreadInputText = SecondCollidingMainThreadId;
        Assert.True(service.QueryCancellationTokens[1].IsCancellationRequested);

        refreshQuery.SetResult(Snapshot("stale-refresh", []));
        await service.ThirdQueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await selectedBSnapshotApplied.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, applied);
        Assert.Equal("selected-b", viewModel.CollectorStatusText);
        Assert.Equal(SecondCollidingMainThreadId, viewModel.SelectedMainThreadId);
        Assert.Collection(
            service.QueryRequests,
            value => Assert.Equal(FirstCollidingMainThreadId, value.MainThreadConversationId),
            value => Assert.Equal(FirstCollidingMainThreadId, value.MainThreadConversationId),
            value => Assert.Equal(SecondCollidingMainThreadId, value.MainThreadConversationId));
        Assert.Collection(
            service.QueryCancellationTokens,
            value => Assert.True(value.IsCancellationRequested),
            value => Assert.True(value.IsCancellationRequested),
            value => Assert.False(value.IsCancellationRequested));
    }

    [Fact]
    public async Task CancelledSnapshotCannotApplyAfterANewerRequest()
    {
        var firstQuery = new TaskCompletionSource<DashboardSnapshot>();
        var service = new FakeUsageDashboardService(Snapshot("initial", []));
        service.EnqueueQueryTask(firstQuery.Task);
        service.EnqueueQuerySnapshot(Snapshot("fresh", []));
        using var viewModel = CreateViewModel(service);
        var applied = 0;
        var freshSnapshotApplied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SnapshotApplied += (_, _) => applied++;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DashboardViewModel.CollectorStatusText)
                && viewModel.CollectorStatusText == "fresh")
            {
                freshSnapshotApplied.TrySetResult(true);
            }
        };

        await viewModel.InitializeAsync();
        viewModel.ClearMainThreadFilter();
        await service.FirstQueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        viewModel.ClearMainThreadFilter();

        Assert.Single(service.QueryCancellationTokens);
        Assert.True(service.QueryCancellationTokens[0].IsCancellationRequested);

        firstQuery.SetResult(Snapshot("stale", []));
        await freshSnapshotApplied.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, applied);
        Assert.Equal("fresh", viewModel.CollectorStatusText);
        Assert.Equal(2, service.QueryRequests.Count);
    }

    [Fact]
    public async Task FilterChangedDuringStartupSupersedesStartSnapshotAndQueriesCurrentState()
    {
        var pendingStart = new TaskCompletionSource<DashboardSnapshot>();
        var service = new FakeUsageDashboardService(pendingStart.Task);
        service.EnqueueQuerySnapshot(Snapshot("fresh", []));
        using var viewModel = CreateViewModel(service);
        var applied = 0;
        viewModel.SnapshotApplied += (_, _) => applied++;

        var initialize = viewModel.InitializeAsync();
        await service.StartRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        viewModel.MainThreadInputText = DirectMainThreadId;
        var freshSnapshotApplied = ObserveSnapshotApplication(viewModel, "fresh");
        pendingStart.SetResult(Snapshot("stale", []));

        await initialize;
        await freshSnapshotApplied.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, applied);
        Assert.Equal("fresh", viewModel.CollectorStatusText);
        Assert.Collection(
            service.QueryRequests,
            value => Assert.Equal(DirectMainThreadId, value.MainThreadConversationId));
    }

    private static DashboardViewModel CreateViewModel(FakeUsageDashboardService service) => new(
        service,
        new InlineUiDispatcher(),
        new UnavailableStartupRegistrationService("Startup is unavailable in tests."),
        new UnconfiguredReleaseUpdateService(),
        TimeProvider.System,
        TimeSpan.Zero);

    private static MainThreadOption MainThread(
        string conversationId,
        int activityOffset,
        string projectName = "project",
        string title = "thread") => new(
        conversationId,
        projectName,
        title,
        new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero).AddMinutes(activityOffset));

    private static async Task ApplySnapshotAsync(
        DashboardViewModel viewModel,
        string expectedCollectorStatus,
        Action apply)
    {
        var snapshotApplied = ObserveSnapshotApplication(viewModel, expectedCollectorStatus);
        apply();
        await snapshotApplied.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static Task ObserveSnapshotApplication(
        DashboardViewModel viewModel,
        string expectedCollectorStatus)
    {
        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName != nameof(DashboardViewModel.CollectorStatusText)
                || viewModel.CollectorStatusText != expectedCollectorStatus)
            {
                return;
            }

            viewModel.PropertyChanged -= handler;
            applied.TrySetResult(true);
        };
        viewModel.PropertyChanged += handler;
        return applied.Task;
    }

    private static DashboardSnapshot Snapshot(
        string message,
        IReadOnlyList<MainThreadOption> mainThreads,
        CostBreakdown? cost = null,
        IReadOnlyList<GroupRow>? byModel = null,
        IReadOnlyList<RoleUsageRow>? byRole = null,
        CollectorPhase phase = CollectorPhase.Watching,
        long conflicts = 0) => new(
        new CollectorStatus(
            phase,
            "usage.sqlite",
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            conflicts,
            ObservationCoverage.Baseline,
            null,
            message,
            new CollectorDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0),
            0),
        new QueryResult(
            new UsageSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, cost ?? CostBreakdown.PricedZero),
            byModel?.ToImmutableArray() ?? ImmutableArray<GroupRow>.Empty,
            byRole?.ToImmutableArray() ?? ImmutableArray<RoleUsageRow>.Empty,
            ImmutableArray<GroupRow>.Empty,
            new QueryFacets(ImmutableArray<ModelFacetOption>.Empty, ImmutableArray<SubjectFacetOption>.Empty),
            ScanDiagnostics.Empty),
        mainThreads,
        new ProcessEfficiencyModeResult(ProcessExecutionMode.Efficiency, false, false, "Not attempted"));

    private static UsageSummary Summary(CostBreakdown cost, long unpricedTokens = 0) => new(
        Calls: 1,
        InputTokens: 1,
        CachedInputTokens: 0,
        UncachedInputTokens: 1,
        OutputTokens: 1,
        ReasoningOutputTokens: 0,
        OtherOutputTokens: 1,
        CanonicalTotalTokens: 2,
        UnpricedTokens: unpricedTokens,
        Cost: cost);

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return true;
        }
    }

    private sealed class FakeUsageDashboardService : IUsageDashboardService
    {
        private readonly Queue<Task<DashboardSnapshot>> _queryTasks = [];

        public FakeUsageDashboardService(DashboardSnapshot startupSnapshot)
            : this(Task.FromResult(startupSnapshot))
        {
        }

        public FakeUsageDashboardService(Task<DashboardSnapshot> startupTask)
        {
            StartupTask = startupTask ?? throw new ArgumentNullException(nameof(startupTask));
        }

        public event EventHandler<DashboardApplicationStatus>? StatusChanged
        {
            add { }
            remove { }
        }
        public event EventHandler? UsageChanged;

        public List<DashboardQueryRequest> QueryRequests { get; } = [];
        public List<CancellationToken> QueryCancellationTokens { get; } = [];
        public Task<DashboardSnapshot> StartupTask { get; }
        public TaskCompletionSource<DashboardQueryRequest> StartRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DashboardQueryRequest> FirstQueryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DashboardQueryRequest> SecondQueryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DashboardQueryRequest> ThirdQueryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnqueueQuerySnapshot(DashboardSnapshot snapshot) =>
            _queryTasks.Enqueue(Task.FromResult(snapshot));

        public void EnqueueQueryTask(Task<DashboardSnapshot> snapshot) =>
            _queryTasks.Enqueue(snapshot ?? throw new ArgumentNullException(nameof(snapshot)));

        public void RaiseUsageChanged() => UsageChanged?.Invoke(this, EventArgs.Empty);

        public Task<DashboardSnapshot> StartAsync(
            DashboardQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            StartRequested.TrySetResult(request);
            return StartupTask;
        }

        public Task<DashboardSnapshot> QueryAsync(
            DashboardQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            QueryRequests.Add(request);
            QueryCancellationTokens.Add(cancellationToken);
            switch (QueryRequests.Count)
            {
                case 1:
                    FirstQueryStarted.TrySetResult(request);
                    break;
                case 2:
                    SecondQueryStarted.TrySetResult(request);
                    break;
                case 3:
                    ThirdQueryStarted.TrySetResult(request);
                    break;
            }
            return _queryTasks.Dequeue();
        }

        public Task<ProcessEfficiencyModeResult> SetProcessExecutionModeAsync(ProcessExecutionMode mode) =>
            Task.FromResult(new ProcessEfficiencyModeResult(mode, false, false, "Not attempted"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
