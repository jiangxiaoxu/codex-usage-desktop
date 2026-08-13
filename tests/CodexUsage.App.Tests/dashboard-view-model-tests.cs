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

    [Fact]
    public async Task SnapshotMapsModelAndSubjectCostSlicesAndAddsSubagentAggregate()
    {
        var root = Usage(
            uncachedInputTokens: 200,
            cachedInputTokens: 800,
            reasoningOutputTokens: 100,
            otherOutputTokens: 50,
            cost: new CostBreakdown(20, 60, 15, 5, 100, Priced: true));
        var worker = Usage(
            uncachedInputTokens: 100,
            cachedInputTokens: 400,
            reasoningOutputTokens: 50,
            otherOutputTokens: 50,
            cost: new CostBreakdown(10, 30, 5, 5, 50, Priced: true));
        var unknown = Usage(
            uncachedInputTokens: 0,
            cachedInputTokens: 0,
            reasoningOutputTokens: 0,
            otherOutputTokens: 0,
            cost: CostBreakdown.PricedZero);
        var total = Usage(
            uncachedInputTokens: 300,
            cachedInputTokens: 1200,
            reasoningOutputTokens: 150,
            otherOutputTokens: 100,
            cost: new CostBreakdown(30, 90, 20, 10, 150, Priced: true));
        var result = new QueryResult(
            total,
            [new GroupRow(["gpt-5.6-sol"], total)],
            [
                new RoleUsageRow(ThreadType.Main, "root", 1, root),
                new RoleUsageRow(ThreadType.Subagent, "worker", 2, worker),
                new RoleUsageRow(ThreadType.Unknown, "unknown", 1, unknown),
            ],
            ImmutableArray<GroupRow>.Empty,
            new QueryFacets(ImmutableArray<ModelFacetOption>.Empty, ImmutableArray<SubjectFacetOption>.Empty),
            ScanDiagnostics.Empty);
        var service = new FakeUsageDashboardService(Snapshot("full", [], result));
        using var viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync();

        var model = Assert.Single(viewModel.Models);
        Assert.Equal(("gpt-5.6-sol", "$150.0", "100.0%"), (model.Model, model.Cost, model.Share));
        Assert.Collection(
            model.CostSlices,
            slice => Assert.Equal(("gpt-5.6-sol", "无缓存输入", 30m, 300L), (slice.EntityLabel, slice.Label, slice.CostAmount, slice.TokenCount)),
            slice => Assert.Equal(("gpt-5.6-sol", "缓存输入", 90m, 1200L), (slice.EntityLabel, slice.Label, slice.CostAmount, slice.TokenCount)),
            slice => Assert.Equal(("gpt-5.6-sol", "思考输出", 20m, 150L), (slice.EntityLabel, slice.Label, slice.CostAmount, slice.TokenCount)),
            slice => Assert.Equal(("gpt-5.6-sol", "其他输出", 10m, 100L), (slice.EntityLabel, slice.Label, slice.CostAmount, slice.TokenCount)));

        Assert.Collection(
            viewModel.Subjects,
            row =>
            {
                Assert.Equal(SubjectUsageRowKind.Role, row.Kind);
                Assert.Equal(("主线程", "root", "$100.0", "66.7%"), (row.ThreadType, row.Role, row.Cost, row.Share));
                Assert.Equal(4, row.CostSlices.Count);
                Assert.Equal(new decimal[] { 20m, 60m, 15m, 5m }, row.CostSlices.Select(slice => slice.CostAmount));
                Assert.All(row.CostSlices, slice => Assert.Equal("主线程 · root", slice.EntityLabel));
            },
            row =>
            {
                Assert.Equal(SubjectUsageRowKind.SubagentAggregate, row.Kind);
                Assert.Equal(("子代理", "合计", "$50.0", "33.3%"), (row.ThreadType, row.Role, row.Cost, row.Share));
                Assert.Equal(new decimal[] { 10m, 30m, 5m, 5m }, row.CostSlices.Select(slice => slice.CostAmount));
                Assert.All(row.CostSlices, slice => Assert.Equal("子代理合计", slice.EntityLabel));
            },
            row =>
            {
                Assert.Equal(SubjectUsageRowKind.Role, row.Kind);
                Assert.Equal(("子代理", "worker", "$50.0", "33.3%"), (row.ThreadType, row.Role, row.Cost, row.Share));
                Assert.Equal(new decimal[] { 10m, 30m, 5m, 5m }, row.CostSlices.Select(slice => slice.CostAmount));
                Assert.All(row.CostSlices, slice => Assert.Equal("子代理 · worker", slice.EntityLabel));
            },
            row =>
            {
                Assert.Equal(SubjectUsageRowKind.Role, row.Kind);
                Assert.Equal(("unknown", "unknown", "$0.0", "0.0%"), (row.ThreadType, row.Role, row.Cost, row.Share));
                Assert.Equal(4, row.CostSlices.Count);
            });

        var rootRow = viewModel.Subjects[0];
        var aggregateRow = viewModel.Subjects[1];
        var workerRow = viewModel.Subjects[2];
        var unknownRow = viewModel.Subjects[3];
        service.EnqueueQuerySnapshot(Snapshot("refreshed", [], result));

        await ApplySnapshotAsync(viewModel, "refreshed", viewModel.ClearMainThreadFilter);

        Assert.Same(model, viewModel.Models[0]);
        Assert.Same(rootRow, viewModel.Subjects[0]);
        Assert.Same(aggregateRow, viewModel.Subjects[1]);
        Assert.Same(workerRow, viewModel.Subjects[2]);
        Assert.Same(unknownRow, viewModel.Subjects[3]);
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
        QueryResult? result = null) => new(
        new CollectorStatus(
            CollectorPhase.Watching,
            "usage.sqlite",
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            0,
            ObservationCoverage.Baseline,
            null,
            message,
            new CollectorDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0),
            0),
        result ?? new QueryResult(
            new UsageSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, CostBreakdown.PricedZero),
            ImmutableArray<GroupRow>.Empty,
            ImmutableArray<RoleUsageRow>.Empty,
            ImmutableArray<GroupRow>.Empty,
            new QueryFacets(ImmutableArray<ModelFacetOption>.Empty, ImmutableArray<SubjectFacetOption>.Empty),
            ScanDiagnostics.Empty),
        mainThreads,
        new ProcessEfficiencyModeResult(ProcessExecutionMode.Efficiency, false, false, "Not attempted"));

    private static UsageSummary Usage(
        long uncachedInputTokens,
        long cachedInputTokens,
        long reasoningOutputTokens,
        long otherOutputTokens,
        CostBreakdown cost) => new(
            Calls: 1,
            InputTokens: checked(uncachedInputTokens + cachedInputTokens),
            CachedInputTokens: cachedInputTokens,
            UncachedInputTokens: uncachedInputTokens,
            OutputTokens: checked(reasoningOutputTokens + otherOutputTokens),
            ReasoningOutputTokens: reasoningOutputTokens,
            OtherOutputTokens: otherOutputTokens,
            CanonicalTotalTokens: checked(uncachedInputTokens + cachedInputTokens + reasoningOutputTokens + otherOutputTokens),
            UnpricedTokens: 0,
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
