using System.Collections.Immutable;
using CodexUsage.Application;
using CodexUsage.Domain;
using CodexUsage.Infrastructure;
using CodexUsage.Infrastructure.Collection;
using Xunit;

namespace CodexUsage.Application.Tests;

public sealed class DashboardApplicationServiceTests
{
    [Fact]
    public async Task StartAppliesInactiveEfficiencyModeBeforeCollectorAndBuildsSummary()
    {
        var order = new List<string>();
        var collector = new FakeCollector(order);
        var efficiency = new FakeEfficiencyMode(order);
        await using var service = new DashboardApplicationService(collector, efficiency);
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");

        var snapshot = await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));

        Assert.Equal(["efficiency:Efficiency", "start"], order);
        Assert.Equal(ProcessExecutionMode.Efficiency, snapshot.EfficiencyMode.Mode);
        Assert.True(snapshot.EfficiencyMode.IsFullyApplied);
        Assert.Equal(110, snapshot.Result.Summary.CanonicalTotalTokens);
        Assert.Equal(100, snapshot.Result.Summary.InputTokens);
        Assert.Equal(40, snapshot.Result.Summary.CachedInputTokens);
        Assert.Equal(10, snapshot.Result.Summary.OutputTokens);
        Assert.Equal(5, snapshot.Result.Summary.ReasoningOutputTokens);
    }

    [Fact]
    public async Task EfficiencyFailureDoesNotPreventCollectorStartup()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new ThrowingEfficiencyMode());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");

        var snapshot = await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));

        Assert.False(snapshot.EfficiencyMode.IsFullyApplied);
        Assert.Contains("access denied", snapshot.EfficiencyMode.Message, StringComparison.Ordinal);
        Assert.Equal(CollectorPhase.Watching, snapshot.Collector.Phase);
    }

    [Fact]
    public async Task InteractiveRequestBeforeStartupIsRetainedWhenCollectorStarts()
    {
        var order = new List<string>();
        var collector = new FakeCollector(order);
        await using var service = new DashboardApplicationService(
            collector,
            new FakeEfficiencyMode(order));
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");

        await service.SetProcessExecutionModeAsync(ProcessExecutionMode.Interactive);
        var snapshot = await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));

        Assert.Equal(["efficiency:Interactive", "start"], order);
        Assert.Equal(ProcessExecutionMode.Interactive, snapshot.EfficiencyMode.Mode);
    }

    [Fact]
    public void WindowActivityReducerOnlyUsesInteractiveSchedulingForVisibleActivatedRestoredWindow()
    {
        var state = DashboardWindowActivity.Hidden;

        state = state.Reduce(DashboardWindowActivitySignal.Shown);
        Assert.Equal(ProcessExecutionMode.Efficiency, state.ExecutionMode);
        state = state.Reduce(DashboardWindowActivitySignal.Activated);
        Assert.Equal(ProcessExecutionMode.Interactive, state.ExecutionMode);
        state = state.Reduce(DashboardWindowActivitySignal.Minimized);
        Assert.Equal(ProcessExecutionMode.Efficiency, state.ExecutionMode);
        state = state.Reduce(DashboardWindowActivitySignal.Activated);
        Assert.Equal(ProcessExecutionMode.Efficiency, state.ExecutionMode);
        state = state.Reduce(DashboardWindowActivitySignal.Restored);
        Assert.Equal(ProcessExecutionMode.Interactive, state.ExecutionMode);
        state = state.Reduce(DashboardWindowActivitySignal.Deactivated);
        Assert.Equal(ProcessExecutionMode.Efficiency, state.ExecutionMode);
        state = state.Reduce(DashboardWindowActivitySignal.ShutdownStarted);

        var afterLateEvents = state
            .Reduce(DashboardWindowActivitySignal.Shown)
            .Reduce(DashboardWindowActivitySignal.Restored)
            .Reduce(DashboardWindowActivitySignal.Activated);
        Assert.Equal(state, afterLateEvents);
        Assert.True(afterLateEvents.IsShuttingDown);
        Assert.Equal(ProcessExecutionMode.Efficiency, afterLateEvents.ExecutionMode);
    }

    [Fact]
    public async Task ExecutionModeTransitionsAreSerializedCoalescedAndPublishLatestDiagnostics()
    {
        var collector = new FakeCollector([]);
        var efficiency = new ControlledEfficiencyMode();
        await using var service = new DashboardApplicationService(collector, efficiency);
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));
        var statuses = new List<DashboardApplicationStatus>();
        service.StatusChanged += (_, status) => statuses.Add(status);

        var interactive = service.SetProcessExecutionModeAsync(ProcessExecutionMode.Interactive);
        await efficiency.InteractiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var duplicate = service.SetProcessExecutionModeAsync(ProcessExecutionMode.Interactive);
        var inactive = service.SetProcessExecutionModeAsync(ProcessExecutionMode.Efficiency);
        efficiency.ReleaseInteractive.SetResult(true);

        await Task.WhenAll(interactive, duplicate, inactive);
        await service.SetProcessExecutionModeAsync(ProcessExecutionMode.Efficiency);

        Assert.Equal(
            [ProcessExecutionMode.Efficiency, ProcessExecutionMode.Interactive, ProcessExecutionMode.Efficiency],
            efficiency.AppliedModes);
        Assert.Equal(1, efficiency.MaximumConcurrency);
        Assert.True(statuses.Select(value => value.EfficiencyMode.Revision).SequenceEqual(
            statuses.Select(value => value.EfficiencyMode.Revision).Order()));
        Assert.Equal(ProcessExecutionMode.Efficiency, statuses[^1].EfficiencyMode.Mode);
        Assert.Contains("Efficiency", statuses[^1].EfficiencyMode.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAppliesModelFilterWhileKeepingDateFacets()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]));
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));

        var snapshot = await service.QueryAsync(new DashboardQueryRequest(
            end.AddHours(-1),
            end,
            Models: ImmutableArray.Create("gpt-5.6-terra")));

        Assert.Equal(0, snapshot.Result.Summary.CanonicalTotalTokens);
        Assert.Contains(snapshot.Result.Facets.Models, value => value.Model == "gpt-5.6-sol");
    }

    [Fact]
    public async Task ChangedCollectorUsageRevisionRaisesUsageChanged()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]));
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));
        var changes = 0;
        service.UsageChanged += (_, _) => changes++;

        collector.EmitUsageChange();

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ChangedCollectorUsageRevisionDuringRetryingRaisesUsageChanged()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]));
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));
        var changes = 0;
        service.UsageChanged += (_, _) => changes++;

        collector.EmitUsageChange(CollectorPhase.Retrying);

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task StatusOnlyCollectorChangesDoNotRaiseUsageChanged()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]));
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));
        var statuses = 0;
        var changes = 0;
        service.StatusChanged += (_, _) => statuses++;
        service.UsageChanged += (_, _) => changes++;

        collector.EmitStatusOnlyChange();

        Assert.Equal(1, statuses);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task UnavailablePlatformServicesReturnStableDiagnostics()
    {
        var startup = new UnavailableStartupRegistrationService("startup unavailable");
        var updates = new UnconfiguredReleaseUpdateService();

        var startupState = await startup.SetEnabledAsync(true);
        var updateState = await updates.CheckAsync();

        Assert.False(startupState.IsAvailable);
        Assert.False(startupState.IsEnabled);
        Assert.Equal("startup unavailable", startupState.Message);
        Assert.False(updates.IsAvailable);
        Assert.False(updateState.IsAvailable);
        Assert.False(updateState.IsUpdateAvailable);
        Assert.Equal(UnconfiguredReleaseUpdateService.DiagnosticMessage, updateState.Message);
    }

    [Fact]
    public void StartupStatusKeepsConfiguredReleaseFeedWhenManualCheckIsInFlight()
    {
        var startup = new PlatformFeatureResult(true, true, "开机自启动已开启");

        var status = DashboardPlatformStatusText.ForStartup(
            startup,
            isReleaseUpdateAvailable: true);

        Assert.Equal(startup.Message, status);
        Assert.DoesNotContain(UnconfiguredReleaseUpdateService.DiagnosticMessage, status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupCoordinatorOnlyReturnsLatestRapidToggle()
    {
        var platform = new ControlledStartupRegistrationService();
        var coordinator = new StartupRegistrationCoordinator(platform);

        var enable = coordinator.SetLatestStateAsync(true);
        await platform.FirstCallStarted.Task;
        var disable = coordinator.SetLatestStateAsync(false);
        platform.ReleaseFirstCall.SetResult(true);

        Assert.Null(await enable);
        var latest = await disable;
        Assert.NotNull(latest);
        Assert.False(latest.IsEnabled);
        Assert.Equal([true, false], platform.Requests);
    }

    [Fact]
    public void StartupLaunchContractBuildsQuotedRunCommandAndRecognizesStartupActivation()
    {
        const string executablePath = @"C:\Program Files\Codex Usage Desktop\Codex Usage Desktop.exe";

        var command = StartupLaunchContract.CreateRunCommand(executablePath);

        Assert.Equal(
            "\"C:\\Program Files\\Codex Usage Desktop\\Codex Usage Desktop.exe\" --startup",
            command);
        Assert.True(StartupLaunchContract.IsOwnedRunCommand(command, executablePath));
        Assert.True(StartupLaunchContract.IsStartupLaunch(["--startup"]));
        Assert.True(StartupLaunchContract.IsStartupLaunch("--startup"));
        Assert.False(StartupLaunchContract.IsStartupLaunch("--start"));
        Assert.False(StartupLaunchContract.IsStartupLaunch(Array.Empty<string>()));
    }

    private sealed class FakeEfficiencyMode(List<string> order) : IProcessEfficiencyMode
    {
        public ProcessEfficiencyModeResult TryApply(ProcessExecutionMode mode)
        {
            order.Add($"efficiency:{mode}");
            return new(mode, true, true, $"{mode} enabled");
        }
    }

    private sealed class ThrowingEfficiencyMode : IProcessEfficiencyMode
    {
        public ProcessEfficiencyModeResult TryApply(ProcessExecutionMode mode) =>
            throw new InvalidOperationException("access denied");
    }

    private sealed class ControlledEfficiencyMode : IProcessEfficiencyMode
    {
        private int _concurrency;

        public TaskCompletionSource<bool> InteractiveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseInteractive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ProcessExecutionMode> AppliedModes { get; } = [];

        public int MaximumConcurrency { get; private set; }

        public ProcessEfficiencyModeResult TryApply(ProcessExecutionMode mode)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            try
            {
                AppliedModes.Add(mode);
                if (mode == ProcessExecutionMode.Interactive)
                {
                    InteractiveStarted.TrySetResult(true);
                    ReleaseInteractive.Task.GetAwaiter().GetResult();
                }

                return new(mode, true, true, $"{mode} applied");
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private sealed class ControlledStartupRegistrationService : IStartupRegistrationService
    {
        public TaskCompletionSource<bool> FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<bool> Requests { get; } = [];

        public Task<PlatformFeatureResult> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlatformFeatureResult(true, false, "disabled"));

        public async Task<PlatformFeatureResult> SetEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(enabled);
            if (Requests.Count == 1)
            {
                FirstCallStarted.SetResult(true);
                await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
            }
            return new(true, enabled, enabled ? "enabled" : "disabled");
        }
    }

    private sealed class FakeCollector : IUsageCollector
    {
        private readonly List<string> _order;
        private readonly CollectorStatus _status = CreateStatus();

        public FakeCollector(List<string> order)
        {
            _order = order;
        }

        public event EventHandler<CollectorStatus>? StatusChanged;

        public int QueryCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask<CollectorStatus> StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _order.Add("start");
            StatusChanged?.Invoke(this, _status);
            return ValueTask.FromResult(_status);
        }

        public ValueTask<CollectorSyncResult> RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CollectorSyncResult(_status, true));
        }

        public ValueTask<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_status);
        }

        public ValueTask<IReadOnlyList<StoredUsageEvent>> QueryEventsAsync(
            UsageEventQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCount++;
            IReadOnlyList<StoredUsageEvent> events =
            [
                new(
                    DateTimeOffset.Parse("2026-07-30T03:30:00Z"),
                    "conversation",
                    "rollout",
                    string.Empty,
                    ThreadType.Main,
                    "root",
                    "/root",
                    string.Empty,
                    "gpt-5.6-sol",
                    100,
                    40,
                    10,
                    5,
                    0,
                    DateTimeOffset.Parse("2026-07-30T03:30:00Z").ToUnixTimeMilliseconds(),
                    "signature"),
            ];
            return ValueTask.FromResult(events);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void EmitUsageChange(CollectorPhase phase = CollectorPhase.Watching) => StatusChanged?.Invoke(this, _status with
        {
            Phase = phase,
            UsageRevision = _status.UsageRevision + 1,
        });

        public void EmitStatusOnlyChange() => StatusChanged?.Invoke(this, _status with
        {
            LastSuccessfulInventoryUtc = _status.LastSuccessfulInventoryUtc?.AddMinutes(5),
            ChangedFilesLastSync = 2,
            Diagnostics = _status.Diagnostics with { FilesScanned = 2 },
        });

        private static CollectorStatus CreateStatus() => new(
            CollectorPhase.Watching,
            "usage.sqlite",
            DateTimeOffset.Parse("2026-07-30T03:00:00Z"),
            DateTimeOffset.Parse("2026-07-30T03:59:00Z"),
            DateTimeOffset.Parse("2026-07-30T03:59:30Z"),
            1,
            0,
            0,
            1,
            0,
            ObservationCoverage.Continuous,
            null,
            "watching",
            new CollectorDiagnostics(1, 0, 0, 0, 0, 1, 0, 0, 0),
            0);
    }
}
