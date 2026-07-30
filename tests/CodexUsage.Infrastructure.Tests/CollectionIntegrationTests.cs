using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsage.Domain;
using CodexUsage.Infrastructure.Collection;
using Xunit;

namespace CodexUsage.Infrastructure.Tests;

public sealed class CollectionIntegrationTests
{
    [Fact]
    public async Task InitialInventoryParsesAndQueriesUsage()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        WriteRollout(Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl"),
            Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(collector);
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.Equal(CollectorPhase.Watching, status.Phase);
        var usage = Assert.Single(events);
        Assert.Equal("rollout-one", usage.RolloutId);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal("gpt-5.6-sol", usage.Model);
    }

    [Fact]
    public async Task StableCanonicalSelfRewriteReplacesOnlyThatRollout()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var firstPath = Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl");
        var siblingPath = Path.Combine(codexHome, "sessions", "rollout-test-two.jsonl");
        WriteRollout(firstPath, Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        WriteRollout(siblingPath, Rollout("rollout-two", Token([7, 1, 3, 1, 10], [7, 1, 3, 1, 10])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(firstPath, Rollout("rollout-one", Token([20, 3, 6, 2, 26], [20, 3, 6, 2, 26])));

        var sync = await collector.RefreshAsync();
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.True(sync.UsageChanged);
        Assert.Equal(2, events.Count);
        Assert.Equal(20, events.Single(value => value.RolloutId == "rollout-one").InputTokens);
        Assert.Equal(7, events.Single(value => value.RolloutId == "rollout-two").InputTokens);
        Assert.Equal(0, sync.Status.Conflicts);
    }

    [Fact]
    public async Task LongerArchiveCandidatePromotesWithoutDoubleCounting()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        WriteRollout(Path.Combine(codexHome, "sessions", "rollout-live-one.jsonl"),
            Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(Path.Combine(codexHome, "archived_sessions", "rollout-archive-one.jsonl"),
            Rollout("rollout-one",
                Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]),
                Token([5, 1, 2, 1, 7], [15, 3, 6, 2, 21], "2026-07-15T01:03:03.004Z")));

        await collector.RefreshAsync();
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.Equal(2, events.Count);
        Assert.Equal([10L, 5L], events.Select(value => value.InputTokens));
    }

    [Fact]
    public async Task StableMalformedCanonicalPreservesLedgerAndReportsConflict()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl");
        WriteRollout(rolloutPath, Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(rolloutPath, "{not-json}\n");

        var sync = await collector.RefreshAsync();
        var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));

        Assert.Equal(CollectorPhase.Degraded, sync.Status.Phase);
        Assert.Equal(1, sync.Status.Conflicts);
        Assert.Equal(10, usage.InputTokens);
    }

    [Fact]
    public async Task UnchangedConflictIsNotReparsedOnNextStartup()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-conflict.jsonl");
        WriteRollout(rolloutPath, Rollout("conflict", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));

        await using (var first = CreateCollector(codexHome, temporary.Path))
        {
            await StartAndWaitForInventoryAsync(first);
            WriteRollout(rolloutPath, "{not-json}\n");
            var conflict = await first.RefreshAsync();
            Assert.Equal(1, conflict.Status.Conflicts);
        }

        await using var restarted = CreateCollector(codexHome, temporary.Path);
        var status = await StartAndWaitForInventoryAsync(restarted);

        Assert.Equal(1, status.Conflicts);
        Assert.Equal(0, status.ChangedFilesLastSync);
        Assert.Equal(0, status.Diagnostics.FilesScanned);
    }

    [Fact]
    public async Task ManualRequestQueuedDuringInventoryRunsFreshTrailingInventory()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        for (var index = 0; index < 40; index++)
        {
            WriteRollout(Path.Combine(codexHome, "sessions", $"rollout-test-{index}.jsonl"),
                Rollout($"rollout-{index}", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        }

        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        await using (var collector = CreateCollector(codexHome, temporary.Path))
        {
            await StartAndWaitForInventoryAsync(collector);
            var first = collector.RefreshAsync().AsTask();
            var trailing = collector.RefreshAsync().AsTask();
            await Task.WhenAll(first, trailing);
        }

        using var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        Assert.Equal("3", store.GetCollectorState("full_inventory_run_count"));
    }

    [Fact]
    public async Task WatcherDebouncesRepeatedNotificationsAndProcessesAppendedUsage()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl");
        var first = Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]);
        WriteRollout(rolloutPath, Rollout("rollout-one", first));
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            WatcherDebounce = TimeSpan.FromMilliseconds(25),
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        });
        await StartAndWaitForInventoryAsync(collector);

        WriteRollout(rolloutPath, Rollout("rollout-one", first,
            Token([5, 1, 2, 1, 7], [15, 3, 6, 2, 21], "2026-07-15T01:03:03.004Z")));
        File.SetLastWriteTimeUtc(rolloutPath, DateTime.UtcNow.AddMilliseconds(10));

        IReadOnlyList<StoredUsageEvent> events = [];
        for (var attempt = 0; attempt < 100; attempt++)
        {
            events = await collector.QueryEventsAsync(AllTimeQuery());
            if (events.Count == 2) break;
            await Task.Delay(25);
        }

        Assert.Equal(2, events.Count);
        var status = await collector.GetStatusAsync();
        Assert.InRange(status.ChangedFilesLastSync, 1, 1);
    }

    [Fact]
    public async Task ParserRevisionRebuildsExistingCanonicalFromObservedSource()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl");
        WriteRollout(rolloutPath, Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        var file = new FileInfo(rolloutPath);
        using (var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome)))
        {
            var metadata = new RolloutMetadata(
                "rollout-one", "rollout-one", "", ThreadType.Main, "main", "/root", "");
            var source = new CandidateSourceInput(
                rolloutPath, file.Length, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                file.Length, "old-prefix", PrefixStatus.Matches, CanonicalStatus.Canonical, true, 1, null);
            store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(metadata,
                [new UsageEventInput(0, DateTimeOffset.Parse("2026-07-15T01:02:03.004Z").ToUnixTimeMilliseconds(),
                    "unknown", 1, 0, 1, 0, "old-signature")],
                new CanonicalSourceInput(
                    source.FilePath, source.SizeBytes, source.ModifiedAtEpochMs, source.ByteOffset,
                    source.PrefixHash, source.PrefixStatus, source.LastScannedAtEpochMs, source.LastError),
                1));
            store.SetCollectorState("rollout_parser_revision", "5", 1);
        }

        await using var collector = CreateCollector(codexHome, temporary.Path);
        await StartAndWaitForInventoryAsync(collector);
        var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));

        Assert.Equal("gpt-5.6-sol", usage.Model);
        Assert.Equal(10, usage.InputTokens);
    }

    [Fact]
    public async Task IncrementalCommitUsesEventsAndBoundaryFromOneStableSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl");
        var first = Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14]);
        var versionA = Rollout("rollout-one", first,
            Token([15, 1, 2, 1, 17], [25, 3, 6, 2, 31], "2026-07-15T01:03:03.004Z"));
        var versionB = Rollout("rollout-one", first,
            Token([19, 1, 2, 1, 21], [29, 3, 6, 2, 35], "2026-07-15T01:03:03.004Z"));
        Assert.Equal(Encoding.UTF8.GetByteCount(versionA), Encoding.UTF8.GetByteCount(versionB));
        WriteRollout(rolloutPath, Rollout("rollout-one", first));
        var replacementPath = Path.Combine(temporary.Path, "replacement.jsonl");
        var replaced = 0;
        var hooks = new CollectorTestHooks(AfterStableAppendSnapshotCapturedAsync: (path, _) =>
        {
            if (Interlocked.Exchange(ref replaced, 1) != 0) return ValueTask.CompletedTask;
            var timestamp = File.GetLastWriteTimeUtc(path);
            WriteRollout(replacementPath, versionB);
            File.SetLastWriteTimeUtc(replacementPath, timestamp);
            File.Move(replacementPath, path, overwrite: true);
            return ValueTask.CompletedTask;
        });
        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        await StartAndWaitForInventoryAsync(collector);
        WriteRollout(rolloutPath, versionA);

        await collector.RefreshAsync();
        var events = await collector.QueryEventsAsync(AllTimeQuery());

        Assert.Equal([10L, 15L], events.Select(value => value.InputTokens));
        await collector.DisposeAsync();
        using var store = new UsageStore(
            Path.Combine(temporary.Path, "usage.sqlite"),
            protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        var source = Assert.Single(store.ListSourceFiles());
        Assert.Equal(HashBoundary(versionA), source.PrefixHash);
    }

    [Fact]
    public async Task CanceledInFlightManualRunSkipsCanceledBacklog()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        WriteRollout(Path.Combine(codexHome, "sessions", "rollout-test-one.jsonl"),
            Rollout("rollout-one", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        var inventoryCalls = 0;
        var manualEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queriesExecuted = 0;
        using var dispatchEntered = new ManualResetEventSlim();
        using var releaseDispatch = new ManualResetEventSlim();
        var hooks = new CollectorTestHooks(
            BeforeInventoryEnumerationAsync: async token =>
            {
                if (Interlocked.Increment(ref inventoryCalls) == 1) return;
                manualEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            BeforeQuery: () => Interlocked.Increment(ref queriesExecuted),
            BeforeInteractiveDispatch: () =>
            {
                if (!manualEntered.Task.IsCompleted) return;
                dispatchEntered.Set();
                if (!releaseDispatch.Wait(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException("Test did not release interactive dispatch.");
            });
        await using var collector = CreateCollector(codexHome, temporary.Path, hooks);
        await StartAndWaitForInventoryAsync(collector);
        using var manualCancellation = new CancellationTokenSource();
        var manual = collector.RefreshAsync(manualCancellation.Token).AsTask();
        await manualEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var backlogCancellations = Enumerable.Range(0, 64).Select(_ => new CancellationTokenSource()).ToArray();
        var backlog = backlogCancellations
            .Select(cancellation => collector.QueryEventsAsync(AllTimeQuery(), cancellation.Token).AsTask())
            .ToArray();
        Assert.True(dispatchEntered.Wait(TimeSpan.FromSeconds(2)));
        foreach (var cancellation in backlogCancellations) cancellation.Cancel();
        releaseDispatch.Set();
        manualCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);
        foreach (var request in backlog)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, Volatile.Read(ref queriesExecuted));
        foreach (var cancellation in backlogCancellations) cancellation.Dispose();
    }

    [Fact]
    public async Task DisposeCancelsInFlightInventoryAndReturnsPromptly()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var inventoryCalls = 0;
        var manualEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new CollectorTestHooks(BeforeInventoryEnumerationAsync: async token =>
        {
            if (Interlocked.Increment(ref inventoryCalls) == 1) return;
            manualEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var collector = CreateCollector(codexHome, temporary.Path, hooks);
        await StartAndWaitForInventoryAsync(collector);
        var manual = collector.RefreshAsync().AsTask();
        await manualEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();

        await collector.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);
    }

    [Fact]
    public async Task InventoryAndWatcherRejectJunctionsOutsideScopeAndCycles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var sessions = Path.Combine(codexHome, "sessions");
        var outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(outside);
        WriteRollout(Path.Combine(sessions, "rollout-inside.jsonl"),
            Rollout("inside", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        WriteRollout(Path.Combine(outside, "rollout-outside.jsonl"),
            Rollout("outside", Token([20, 2, 4, 1, 24], [20, 2, 4, 1, 24])));
        var outsideLink = Path.Combine(sessions, "outside-link");
        var cycleLink = Path.Combine(sessions, "cycle-link");
        var outsideLinkCreated = false;
        var cycleLinkCreated = false;
        try
        {
            CreateJunction(outsideLink, outside);
            outsideLinkCreated = true;
            CreateJunction(cycleLink, sessions);
            cycleLinkCreated = true;
            await using var collector = new UsageCollector(new CollectorOptions
            {
                CodexHome = codexHome,
                DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
                WatcherDebounce = TimeSpan.FromMilliseconds(10),
                FullInventoryInterval = TimeSpan.FromHours(1),
                EnableWatchers = false,
            });
            await StartAndWaitForInventoryAsync(collector);
            collector.EnqueueWatcherObservationForTest(Path.Combine(outsideLink, "rollout-outside.jsonl"));
            await Task.Delay(50);
            var usage = Assert.Single(await collector.QueryEventsAsync(AllTimeQuery()));
            Assert.Equal("inside", usage.RolloutId);
            Assert.Equal(1, (await collector.GetStatusAsync()).FilesKnown);
        }
        finally
        {
            if (cycleLinkCreated) Directory.Delete(cycleLink);
            if (outsideLinkCreated) Directory.Delete(outsideLink);
        }
    }

    [Fact]
    public async Task StartReturnsAndQueriesPreexistingLedgerWhileInitialInventoryIsBlocked()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var databasePath = Path.Combine(temporary.Path, "usage.sqlite");
        var rolloutPath = Path.Combine(codexHome, "sessions", "rollout-seeded.jsonl");
        WriteRollout(rolloutPath, Rollout("seeded", Token([21, 2, 3, 1, 24], [21, 2, 3, 1, 24])));
        SeedLedger(databasePath, codexHome, rolloutPath, "seeded", 21);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = databasePath,
            EnableWatchers = false,
            FullInventoryInterval = TimeSpan.FromHours(1),
        }, new CollectorTestHooks(BeforeInventoryEnumerationAsync: async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }));

        var startStatus = await collector.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CollectorPhase.Syncing, startStatus.Phase);
        Assert.False(release.Task.IsCompleted);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var query = collector.QueryEventsAsync(AllTimeQuery()).AsTask();
        var usage = Assert.Single(await query.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(21, usage.InputTokens);
        Assert.False(release.Task.IsCompleted);
        await collector.GetStatusAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        release.TrySetResult();
        await WaitForInventoryAsync(collector);
    }

    [Fact]
    public async Task TimerTicksDuringInventoryAreDroppedAndNextRunWaitsAFullInterval()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var interval = TimeSpan.FromMilliseconds(200);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompleted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inventoryCalls = 0;
        var completions = 0;
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            FullInventoryInterval = interval,
        }, new CollectorTestHooks(
            BeforeInventoryEnumerationAsync: async token =>
            {
                var call = Interlocked.Increment(ref inventoryCalls);
                if (call == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                }
                else if (call == 2)
                {
                    secondStarted.TrySetResult(Stopwatch.GetTimestamp());
                }
            },
            AfterInventoryCompleted: () =>
            {
                if (Interlocked.Increment(ref completions) == 1)
                    firstCompleted.TrySetResult(Stopwatch.GetTimestamp());
            }));

        await collector.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(650));
        Assert.Equal(1, Volatile.Read(ref inventoryCalls));

        releaseFirst.TrySetResult();
        var completedAt = await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var startedAt = await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(Stopwatch.GetElapsedTime(completedAt, startedAt) >= interval);
        await WaitForInventoryAsync(collector);
    }

    [Fact]
    public async Task LargeInitialInventoryPublishesProgressAndKeepsQueriesResponsive()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        for (var index = 0; index < 96; index++)
            WriteRollout(Path.Combine(codexHome, "sessions", $"rollout-progress-{index}.jsonl"),
                Rollout($"progress-{index}", Token([10, 2, 4, 1, 14], [10, 2, 4, 1, 14])));
        var progress = new List<string>();
        await using var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            CooperativeItemLimit = 1,
            FullInventoryInterval = TimeSpan.FromHours(1),
        });
        collector.StatusChanged += (_, status) =>
        {
            lock (progress) progress.Add(status.Message);
        };

        await collector.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await collector.QueryEventsAsync(AllTimeQuery()).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(40);
        }
        var status = await WaitForInventoryAsync(collector, TimeSpan.FromSeconds(10));

        Assert.Equal(96, status.FilesKnown);
        lock (progress) Assert.Contains(progress, message => message.Contains("sources", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WatcherStormStaysBoundedWhileInitialInventoryIsBlocked()
    {
        using var temporary = new TemporaryDirectory();
        var codexHome = CreateCodexHome(temporary.Path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporary.Path, "usage.sqlite"),
            EnableWatchers = false,
            FullInventoryInterval = TimeSpan.FromHours(1),
        }, new CollectorTestHooks(BeforeInventoryEnumerationAsync: async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }));
        await collector.StartAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var path = Path.Combine(codexHome, "sessions", "rollout-storm.jsonl");

        for (var index = 0; index < 100_000; index++) collector.EnqueueWatcherObservationForTest(path);

        var metrics = collector.GetWatcherBufferMetricsForTest();
        Assert.InRange(metrics.UniquePaths, 0, 1);
        Assert.InRange(metrics.WakeSignals, 0, 1);
        release.TrySetResult();
        await WaitForInventoryAsync(collector);
        await collector.DisposeAsync();
    }

    private static UsageCollector CreateCollector(string codexHome, string temporaryRoot) => new(new CollectorOptions
    {
        CodexHome = codexHome,
        DatabasePath = Path.Combine(temporaryRoot, "usage.sqlite"),
        EnableWatchers = false,
        RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
        FullInventoryInterval = TimeSpan.FromHours(1),
    });

    private static UsageCollector CreateCollector(
        string codexHome,
        string temporaryRoot,
        CollectorTestHooks hooks) => new(new CollectorOptions
        {
            CodexHome = codexHome,
            DatabasePath = Path.Combine(temporaryRoot, "usage.sqlite"),
            EnableWatchers = false,
            RecoverySnapshotDelay = TimeSpan.FromMilliseconds(1),
            FullInventoryInterval = TimeSpan.FromHours(1),
        }, hooks);

    private static UsageEventQuery AllTimeQuery() => new(0, 4_102_444_800_000);

    private static async Task<CollectorStatus> StartAndWaitForInventoryAsync(UsageCollector collector)
    {
        await collector.StartAsync();
        return await WaitForInventoryAsync(collector);
    }

    private static async Task<CollectorStatus> WaitForInventoryAsync(
        UsageCollector collector,
        TimeSpan? timeout = null)
    {
        var expires = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        while (expires.Elapsed < limit)
        {
            var status = await collector.GetStatusAsync();
            if (status.Phase is CollectorPhase.Watching or CollectorPhase.Degraded) return status;
            await Task.Delay(10);
        }
        throw new TimeoutException("Collector inventory did not complete within the test deadline.");
    }

    private static void SeedLedger(
        string databasePath,
        string codexHome,
        string rolloutPath,
        string rolloutId,
        long inputTokens)
    {
        var file = new FileInfo(rolloutPath);
        using var store = new UsageStore(databasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(codexHome));
        var metadata = new RolloutMetadata(rolloutId, rolloutId, "", ThreadType.Main, "main", "/root", "");
        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            metadata,
            [new UsageEventInput(0, DateTimeOffset.Parse("2026-07-15T01:02:03.004Z").ToUnixTimeMilliseconds(),
                "gpt-5.6-sol", inputTokens, 2, 3, 1, $"seed-{rolloutId}")],
            new CanonicalSourceInput(
                rolloutPath,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                file.Length,
                HashBoundary(File.ReadAllText(rolloutPath)),
                PrefixStatus.Matches,
                1,
                null),
            1));
        store.SetCollectorState("rollout_parser_revision", "6", 1);
    }

    private static string CreateCodexHome(string temporaryRoot)
    {
        var codexHome = Path.Combine(temporaryRoot, ".codex");
        Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(codexHome, "archived_sessions"));
        Directory.CreateDirectory(Path.Combine(codexHome, "agents"));
        return codexHome;
    }

    private static void WriteRollout(string filePath, string content) => File.WriteAllText(filePath, content);

    private static string HashBoundary(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var start = Math.Max(0, bytes.Length - 64 * 1024);
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(start))).ToLowerInvariant();
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start junction creation process.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static string Rollout(string rolloutId, params string[] tokens) => string.Join('\n', new[]
    {
        Line("session_meta", new { session_id = rolloutId, id = rolloutId, thread_source = "user" }),
        Line("turn_context", new { turn_id = "turn-a", model = "gpt-5.6-sol" }),
    }.Concat(tokens)) + "\n";

    private static string Token(long[] last, long[] total, string timestamp = "2026-07-15T01:02:03.004Z") =>
        Line("event_msg", new
        {
            type = "token_count",
            info = new
            {
                last_token_usage = Tuple(last),
                total_token_usage = Tuple(total),
            },
        }, timestamp);

    private static string Line(string type, object payload, string timestamp = "2026-07-15T01:02:03.004Z") =>
        JsonSerializer.Serialize(new { timestamp, type, payload });

    private static object Tuple(long[] values) => new
    {
        input_tokens = values[0],
        cached_input_tokens = values[1],
        output_tokens = values[2],
        reasoning_output_tokens = values[3],
        total_tokens = values[4],
    };
}
