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
    public async Task StartEnablesEfficiencyBeforeCollectorAndBuildsSummary()
    {
        var order = new List<string>();
        var collector = new FakeCollector(order);
        var efficiency = new FakeEfficiencyMode(order);
        await using var service = new DashboardApplicationService(collector, efficiency, CreatePolicy());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");

        var snapshot = await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));

        Assert.Equal(["efficiency", "start"], order);
        Assert.True(snapshot.EfficiencyMode.IsFullyEnabled);
        Assert.Equal(110, snapshot.Result.Summary.CanonicalTotalTokens);
        Assert.Equal(100, snapshot.Result.Summary.InputTokens);
        Assert.Equal(40, snapshot.Result.Summary.CachedInputTokens);
        Assert.Equal(10, snapshot.Result.Summary.OutputTokens);
        Assert.Equal(5, snapshot.Result.Summary.ReasoningOutputTokens);
    }

    [Fact]
    public async Task RefreshRunsManualInventoryBeforeQuery()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]), CreatePolicy());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        var request = new DashboardQueryRequest(end.AddHours(-1), end);
        await service.StartAsync(request);

        await service.RefreshAsync(request);

        Assert.Equal(1, collector.RefreshCount);
        Assert.Equal(2, collector.QueryCount);
    }

    [Fact]
    public async Task EfficiencyFailureDoesNotPreventCollectorStartup()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new ThrowingEfficiencyMode(), CreatePolicy());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");

        var snapshot = await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));

        Assert.False(snapshot.EfficiencyMode.IsFullyEnabled);
        Assert.Contains("access denied", snapshot.EfficiencyMode.Message, StringComparison.Ordinal);
        Assert.Equal(CollectorPhase.Watching, snapshot.Collector.Phase);
    }

    [Fact]
    public async Task QueryAppliesModelFilterWhileKeepingDateFacets()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]), CreatePolicy());
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
    public async Task ExportWritesCsvAfterProtectedPathValidation()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]), CreatePolicy());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        var request = new DashboardQueryRequest(end.AddHours(-1), end);
        await service.StartAsync(request);
        var outputPath = Path.Combine(Path.GetTempPath(), $"codex-usage-{Guid.NewGuid():N}.csv");
        try
        {
            var result = await service.ExportCsvAsync(request, outputPath);

            Assert.Equal(CsvExportStatus.Completed, result.Status);
            Assert.Equal(1, result.EventCount);
            Assert.Equal([0xEF, 0xBB, 0xBF], (await File.ReadAllBytesAsync(outputPath))[..3]);
            Assert.StartsWith("timestamp_sgt", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ExportRejectsProtectedCodexSourcePath()
    {
        var codexHome = Path.Combine(Path.GetTempPath(), $"codex-protected-{Guid.NewGuid():N}");
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(
            collector,
            new FakeEfficiencyMode([]),
            ProtectedPathPolicy.ForCodexHome(codexHome));
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        var request = new DashboardQueryRequest(end.AddHours(-1), end);
        await service.StartAsync(request);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportCsvAsync(
            request,
            Path.Combine(codexHome, "sessions", "forbidden.csv")));
    }

    [Fact]
    public async Task ExportReturnsCancelledOutcomeWithoutWriting()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]), CreatePolicy());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        var request = new DashboardQueryRequest(end.AddHours(-1), end);
        await service.StartAsync(request);
        var outputPath = Path.Combine(Path.GetTempPath(), $"codex-usage-{Guid.NewGuid():N}.csv");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.ExportCsvAsync(request, outputPath, cancellation.Token);

        Assert.Equal(CsvExportStatus.Cancelled, result.Status);
        Assert.Null(result.OutputPath);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ExportCountUsesFilteredDataRows()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]), CreatePolicy());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        var all = new DashboardQueryRequest(end.AddHours(-1), end);
        await service.StartAsync(all);
        var filtered = all with { Models = ImmutableArray.Create("gpt-5.6-terra") };
        var outputPath = Path.Combine(Path.GetTempPath(), $"codex-usage-{Guid.NewGuid():N}.csv");
        try
        {
            var result = await service.ExportCsvAsync(filtered, outputPath);

            Assert.Equal(CsvExportStatus.Completed, result.Status);
            Assert.Equal(0, result.EventCount);
            Assert.DoesNotContain("\"conversation\"", await File.ReadAllTextAsync(outputPath), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ConcurrentEquivalentRefreshesShareOneInventory()
    {
        var collector = new FakeCollector([])
        {
            RefreshBlock = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]), CreatePolicy());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        var request = new DashboardQueryRequest(end.AddHours(-1), end);
        await service.StartAsync(request);

        var first = service.RefreshAsync(request);
        var second = service.RefreshAsync(request);
        await Task.Delay(20);

        Assert.Equal(1, collector.RefreshCount);
        collector.RefreshBlock.SetResult(true);
        await Task.WhenAll(first, second);
        Assert.Equal(1, collector.RefreshCount);
    }

    [Fact]
    public async Task CancellingOneRefreshWaiterDoesNotCancelSharedInventory()
    {
        var collector = new FakeCollector([])
        {
            RefreshBlock = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]), CreatePolicy());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        var request = new DashboardQueryRequest(end.AddHours(-1), end);
        await service.StartAsync(request);
        using var waiterCancellation = new CancellationTokenSource();

        var detachedWaiter = service.RefreshAsync(request, waiterCancellation.Token);
        await collector.RefreshStarted.Task;
        var remainingWaiter = service.RefreshAsync(request);
        waiterCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detachedWaiter);
        Assert.False(collector.LastRefreshCancellationToken.IsCancellationRequested);
        collector.RefreshBlock.SetResult(true);
        await remainingWaiter;
        Assert.Equal(1, collector.RefreshCount);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task DisposeCancelsOwnedSharedRefreshBeforeWaitingForGate()
    {
        var collector = new FakeCollector([])
        {
            RefreshBlock = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]), CreatePolicy());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        var request = new DashboardQueryRequest(end.AddHours(-1), end);
        await service.StartAsync(request);
        var refresh = service.RefreshAsync(request);
        await collector.RefreshStarted.Task;

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(collector.LastRefreshCancellationToken.IsCancellationRequested);
        Assert.Equal(1, collector.DisposeCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
    }

    [Fact]
    public async Task StableCollectorRevisionRaisesUsageChanged()
    {
        var collector = new FakeCollector([]);
        await using var service = new DashboardApplicationService(collector, new FakeEfficiencyMode([]), CreatePolicy());
        var end = DateTimeOffset.Parse("2026-07-30T04:00:00Z");
        await service.StartAsync(new DashboardQueryRequest(end.AddHours(-1), end));
        var changes = 0;
        service.UsageChanged += (_, _) => changes++;

        collector.EmitUsageChange();

        Assert.Equal(1, changes);
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

    private static ProtectedPathPolicy CreatePolicy() => new(
        [Path.Combine(Path.GetTempPath(), "codex-usage-application-tests-protected")]);

    private sealed class FakeEfficiencyMode(List<string> order) : IProcessEfficiencyMode
    {
        public ProcessEfficiencyModeResult TryEnable()
        {
            order.Add("efficiency");
            return new(true, true, "enabled");
        }
    }

    private sealed class ThrowingEfficiencyMode : IProcessEfficiencyMode
    {
        public ProcessEfficiencyModeResult TryEnable() => throw new InvalidOperationException("access denied");
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

        public int RefreshCount { get; private set; }

        public int QueryCount { get; private set; }

        public TaskCompletionSource<bool>? RefreshBlock { get; init; }

        public TaskCompletionSource<bool> RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken LastRefreshCancellationToken { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask<CollectorStatus> StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _order.Add("start");
            StatusChanged?.Invoke(this, _status);
            return ValueTask.FromResult(_status);
        }

        public async ValueTask<CollectorSyncResult> RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            LastRefreshCancellationToken = cancellationToken;
            RefreshStarted.TrySetResult(true);
            if (RefreshBlock is not null)
            {
                await RefreshBlock.Task.WaitAsync(cancellationToken);
            }
            return new CollectorSyncResult(_status, true);
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

        public void EmitUsageChange() => StatusChanged?.Invoke(this, _status with
        {
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
            1,
            0,
            ObservationCoverage.Continuous,
            null,
            "watching",
            new CollectorDiagnostics(1, 0, 0, 0, 0, 1));
    }
}
