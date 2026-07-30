using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using CodexUsage.Domain;

[assembly: InternalsVisibleTo("CodexUsage.Infrastructure.Tests")]

namespace CodexUsage.Infrastructure.Collection;

public sealed class UsageCollector : IUsageCollector
{
    private const int BoundaryWindowBytes = 64 * 1024;
    private const int ParserRevision = 6;
    private const string ParserRevisionStateKey = "rollout_parser_revision";
    private const string LastInventoryStateKey = "last_successful_inventory_epoch_ms";
    private const string InventoryRunCountStateKey = "full_inventory_run_count";
    private const string InventoryYieldCountStateKey = "full_inventory_last_yield_count";

    private readonly CollectorOptions _options;
    private readonly string[] _observationRoots;
    private readonly Channel<CollectorCommand> _commands;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _ownerTask;
    private readonly Dictionary<string, SourceRuntime> _runtimeByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceFileRecord> _sourcesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _sourceKeysByRollout = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _canonicalByRollout = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _watcherInbox = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _retryAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _conflictsAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknownModelsAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Queue<CollectorCommand> _deferredCommands = [];
    private readonly MutableDiagnostics _diagnostics = new();

    private UsageStore? _store;
    private PeriodicTimer? _inventoryTimer;
    private PeriodicTimer? _heartbeatTimer;
    private Task? _inventoryTimerTask;
    private Task? _heartbeatTimerTask;
    private CancellationTokenSource? _debounce;
    private long _debounceGeneration;
    private string? _runId;
    private long _runStartedEpochMs;
    private long? _lastSuccessfulInventoryEpochMs;
    private long? _lastHeartbeatEpochMs;
    private long _changedFilesLastSync;
    private long _inventoryPathsEnumerated;
    private long _inventoryPathsProcessed;
    private long _lastInventoryProgressPublishedTimestamp;
    private CollectorPhase _phase = CollectorPhase.Initializing;
    private string _message = "Collector has not started";
    private ObservationCoverage _coverage = ObservationCoverage.Baseline;
    private ObservationGap? _gap;
    private bool _started;
    private bool _stopping;
    private bool _watcherHealthy = true;
    private int _timerInventoryQueued;
    private bool _inventoryActive;
    private long _lastInventoryCompletedTimestamp;
    private int _watcherWakeQueued;
    private string? _watcherErrorInbox;
    private int _disposeStarted;
    private int _lifetimeDisposed;
    private readonly CollectorTestHooks? _testHooks;

    public UsageCollector(CollectorOptions options) : this(options, null)
    {
    }

    internal UsageCollector(CollectorOptions options, CollectorTestHooks? testHooks)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options with
        {
            CodexHome = Path.GetFullPath(options.CodexHome),
            DatabasePath = options.DatabasePath == ":memory:" ? options.DatabasePath : Path.GetFullPath(options.DatabasePath),
        };
        _testHooks = testHooks;
        _observationRoots =
        [
            Path.Combine(_options.CodexHome, "sessions"),
            Path.Combine(_options.CodexHome, "archived_sessions"),
        ];
        _commands = Channel.CreateUnbounded<CollectorCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _ownerTask = RunOwnerAsync(_lifetime.Token);
    }

    public event EventHandler<CollectorStatus>? StatusChanged;

    public ValueTask<CollectorStatus> StartAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<CollectorStatus>((completion, token) => new StartCommand(completion, token), cancellationToken);

    public ValueTask<CollectorSyncResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<CollectorSyncResult>((completion, token) => new ManualInventoryCommand(completion, token), cancellationToken);

    public ValueTask<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<CollectorStatus>((completion, token) => new StatusCommand(completion, token), cancellationToken);

    public ValueTask<IReadOnlyList<StoredUsageEvent>> QueryEventsAsync(
        UsageEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RequestAsync<IReadOnlyList<StoredUsageEvent>>(
            (completion, token) => new QueryCommand(query, completion, token), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await WaitForOwnerBoundedAsync().ConfigureAwait(false);
            return;
        }
        _lifetime.Cancel();
        _commands.Writer.TryComplete();
        await WaitForOwnerBoundedAsync().ConfigureAwait(false);
    }

    private async Task WaitForOwnerBoundedAsync()
    {
        try
        {
            await _ownerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            DisposeLifetime();
        }
        catch (TimeoutException)
        {
            _ = DisposeLifetimeAfterOwnerStopsAsync();
        }
    }

    private async Task DisposeLifetimeAfterOwnerStopsAsync()
    {
        try
        {
            await _ownerTask.ConfigureAwait(false);
        }
        catch
        {
            // The timed-out caller has already returned; observe the owner failure during deferred cleanup.
        }
        finally
        {
            DisposeLifetime();
        }
    }

    private void DisposeLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0) _lifetime.Dispose();
    }

    private async ValueTask<T> RequestAsync<T>(
        Func<TaskCompletionSource<T>, CancellationToken, CollectorCommand> commandFactory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposeStarted) != 0) throw new ObjectDisposedException(nameof(UsageCollector));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(commandFactory(completion, cancellationToken)))
            throw new ObjectDisposedException(nameof(UsageCollector));
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunOwnerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                CollectorCommand command;
                if (_deferredCommands.Count > 0)
                    command = _deferredCommands.Dequeue();
                else
                {
                    if (!await _commands.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) break;
                    if (!_commands.Reader.TryRead(out command!)) continue;
                }
                switch (command)
                {
                    case StartCommand start when start.CancellationToken.IsCancellationRequested:
                        start.Completion.TrySetCanceled(start.CancellationToken);
                        break;
                    case StartCommand start:
                        await CompleteAsync(start.Completion, StartCoreAsync, cancellationToken, start.CancellationToken).ConfigureAwait(false);
                        break;
                    case ManualInventoryCommand inventory when inventory.CancellationToken.IsCancellationRequested:
                        inventory.Completion.TrySetCanceled(inventory.CancellationToken);
                        break;
                    case ManualInventoryCommand inventory:
                        await CompleteAsync(inventory.Completion, RunManualInventoryAsync, cancellationToken, inventory.CancellationToken).ConfigureAwait(false);
                        break;
                    case TimerInventoryCommand:
                        Interlocked.Exchange(ref _timerInventoryQueued, 0);
                        if (_started && !_stopping && ScheduledInventoryIsDue())
                            await RunBackgroundInventoryAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case InitialInventoryCommand:
                        if (_started && !_stopping) await RunBackgroundInventoryAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case WatcherWakeCommand:
                        DrainWatcherInbox();
                        break;
                    case DrainWatcherCommand drain when drain.Generation == _debounceGeneration:
                        _debounce?.Dispose();
                        _debounce = null;
                        await DrainWatcherPathsAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case RetryPathCommand retry:
                        if (_retryAttempts.TryGetValue(NormalizeKey(retry.FilePath), out var attempt)
                            && attempt == retry.Attempt)
                        {
                            AddPendingPath(retry.FilePath);
                            ScheduleDebounce(TimeSpan.Zero);
                        }
                        break;
                    case HeartbeatCommand:
                        Heartbeat();
                        break;
                    case StatusCommand status when status.CancellationToken.IsCancellationRequested:
                        status.Completion.TrySetCanceled(status.CancellationToken);
                        break;
                    case StatusCommand status:
                        status.Completion.TrySetResult(CreateStatus());
                        break;
                    case QueryCommand query when query.CancellationToken.IsCancellationRequested:
                        query.Completion.TrySetCanceled(query.CancellationToken);
                        break;
                    case QueryCommand query:
                        CompleteQuery(query);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_stopping) await StopCoreAsync().ConfigureAwait(false);
            while (_deferredCommands.TryDequeue(out var deferred)) deferred.CancelCompletion();
            while (_commands.Reader.TryRead(out var abandoned)) abandoned.CancelCompletion();
        }
    }

    private Task<CollectorStatus> StartCoreAsync(CancellationToken _)
    {
        if (_started) throw new InvalidOperationException("Collector is already started.");
        _store = new UsageStore(_options.DatabasePath, protectedPathPolicy: ProtectedPathPolicy.ForCodexHome(_options.CodexHome));
        _runStartedEpochMs = NowEpochMs();
        var previousRun = _store.GetLatestCollectorRun();
        if (previousRun is not null)
        {
            var gapStart = previousRun.CompletedAtEpochMs ?? previousRun.HeartbeatAtEpochMs;
            if (gapStart < _runStartedEpochMs)
            {
                _coverage = ObservationCoverage.Gap;
                _gap = new ObservationGap(FromEpoch(gapStart), FromEpoch(_runStartedEpochMs));
            }
            else
            {
                _coverage = ObservationCoverage.Continuous;
            }
        }

        if (long.TryParse(_store.GetCollectorState(LastInventoryStateKey), out var lastInventory))
            _lastSuccessfulInventoryEpochMs = lastInventory;
        foreach (var source in _store.ListSourceFiles()) RememberSource(ToInput(source));
        foreach (var rolloutId in _sourcesByPath.Values.Select(value => value.RolloutId).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var canonical = _store.GetCanonicalSourcePath(rolloutId);
            if (canonical is not null) _canonicalByRollout[rolloutId] = canonical;
        }

        _runId = Guid.NewGuid().ToString();
        _store.BeginCollectorRun(new CollectorRunStartInput(_runId, "application-session", _runStartedEpochMs));
        _started = true;
        if (_options.EnableWatchers) StartWatchers();
        StartTimers();
        _phase = CollectorPhase.Syncing;
        _message = "Ledger ready; initial inventory queued";
        PublishStatus();
        if (!_commands.Writer.TryWrite(new InitialInventoryCommand()))
            throw new ObjectDisposedException(nameof(UsageCollector));
        return Task.FromResult(CreateStatus());
    }

    private void StartWatchers()
    {
        foreach (var root in _observationRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root, "rollout-*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = false,
                };
                watcher.Created += OnWatcherChanged;
                watcher.Changed += OnWatcherChanged;
                watcher.Deleted += OnWatcherChanged;
                watcher.Renamed += OnWatcherRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception error)
            {
                _watcherHealthy = false;
                AddDiagnostic(root, "watcher-start-failed", error.Message, DiagnosticSeverity.Warning);
            }
        }
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs args) => EnqueueWatcherObservation(args.FullPath);

    private void OnWatcherRenamed(object sender, RenamedEventArgs args)
    {
        EnqueueWatcherObservation(args.OldFullPath);
        EnqueueWatcherObservation(args.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        Interlocked.Exchange(ref _watcherErrorInbox, args.GetException().Message);
        SignalWatcherInbox();
    }

    internal void EnqueueWatcherObservationForTest(string filePath) => EnqueueWatcherObservation(filePath);

    internal (int UniquePaths, int WakeSignals) GetWatcherBufferMetricsForTest() =>
        (_watcherInbox.Count, Volatile.Read(ref _watcherWakeQueued));

    private void EnqueueWatcherObservation(string filePath)
    {
        if (Volatile.Read(ref _disposeStarted) != 0 || !IsLexicallyObservedRollout(filePath)) return;
        var fullPath = Path.GetFullPath(filePath);
        _watcherInbox[NormalizeKey(fullPath)] = fullPath;
        SignalWatcherInbox();
    }

    private void SignalWatcherInbox()
    {
        if (Interlocked.CompareExchange(ref _watcherWakeQueued, 1, 0) != 0) return;
        if (!_commands.Writer.TryWrite(new WatcherWakeCommand())) Interlocked.Exchange(ref _watcherWakeQueued, 0);
    }

    private void DrainWatcherInbox()
    {
        var watcherError = Interlocked.Exchange(ref _watcherErrorInbox, null);
        if (watcherError is not null)
        {
            _watcherHealthy = false;
            Degrade($"Watcher error: {watcherError}");
        }
        foreach (var item in _watcherInbox.ToArray())
        {
            if (_watcherInbox.TryRemove(item.Key, out var filePath)) QueueWatcherPath(filePath);
        }
        Interlocked.Exchange(ref _watcherWakeQueued, 0);
        if (!_watcherInbox.IsEmpty || Volatile.Read(ref _watcherErrorInbox) is not null) SignalWatcherInbox();
    }

    private void StartTimers()
    {
        _inventoryTimer = new PeriodicTimer(_options.FullInventoryInterval);
        _heartbeatTimer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _inventoryTimerTask = PumpInventoryTimerAsync(_inventoryTimer, _lifetime.Token);
        _heartbeatTimerTask = PumpTimerAsync(_heartbeatTimer, static () => new HeartbeatCommand(), _lifetime.Token);
    }

    private async Task PumpInventoryTimerAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Interlocked.CompareExchange(ref _timerInventoryQueued, 1, 0) != 0) continue;
                if (!_commands.Writer.TryWrite(new TimerInventoryCommand())) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PumpTimerAsync(
        PeriodicTimer timer,
        Func<CollectorCommand> commandFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                if (!_commands.Writer.TryWrite(commandFactory())) return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunBackgroundInventoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunScheduledInventoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Degrade(error.Message);
        }
    }

    private async Task<CollectorSyncResult> RunManualInventoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunScheduledInventoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            Degrade("Manual inventory canceled");
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Degrade(error.Message);
            throw;
        }
    }

    private async Task<CollectorSyncResult> RunScheduledInventoryAsync(CancellationToken cancellationToken)
    {
        if (_inventoryActive) throw new InvalidOperationException("An inventory is already active.");
        _inventoryActive = true;
        try
        {
            return await RunFullInventoryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inventoryActive = false;
            _lastInventoryCompletedTimestamp = Stopwatch.GetTimestamp();
            _testHooks?.AfterInventoryCompleted?.Invoke();
        }
    }

    private bool ScheduledInventoryIsDue() =>
        !_inventoryActive
        && (_lastInventoryCompletedTimestamp == 0
            || Stopwatch.GetElapsedTime(_lastInventoryCompletedTimestamp) >= _options.FullInventoryInterval);

    private async Task<CollectorSyncResult> RunFullInventoryAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        _phase = CollectorPhase.Syncing;
        _message = "Reconciling local rollouts";
        PublishStatus();
        var store = RequireStore();
        var yields = new InventoryYieldTracker();
        var inventorySucceeded = true;
        var usageChanged = false;
        long changedFiles = 0;

        var currentCount = long.TryParse(store.GetCollectorState(InventoryRunCountStateKey), out var count) ? count : 0;
        store.SetCollectorState(InventoryRunCountStateKey, checked(currentCount + 1).ToString(), NowEpochMs());
        if (_testHooks?.BeforeInventoryEnumerationAsync is { } inventoryHook)
            await AwaitWhileServingInteractiveAsync(inventoryHook(cancellationToken), cancellationToken).ConfigureAwait(false);
        var inventory = await ListRolloutsAsync(yields, cancellationToken).ConfigureAwait(false);
        _inventoryPathsEnumerated = inventory.Paths.Count;
        _inventoryPathsProcessed = 0;
        PublishInventoryProgress("Inventory discovered");
        inventorySucceeded &= inventory.Succeeded;
        var present = inventory.Paths.ToDictionary(NormalizeKey, path => path, StringComparer.OrdinalIgnoreCase);
        var slice = new CooperativeSlice(_options, yields, YieldToMailboxAsync);

        foreach (var source in _sourcesByPath.Values.ToArray())
        {
            if (source.IsPresent && !present.ContainsKey(NormalizeKey(source.FilePath)))
            {
                MarkMissing(source.FilePath);
                changedFiles++;
            }
            await slice.ItemProcessedAsync(cancellationToken).ConfigureAwait(false);
        }

        var revisionAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storedParserRevision = store.GetCollectorState(ParserRevisionStateKey);
        if (!string.Equals(storedParserRevision, ParserRevision.ToString(), StringComparison.Ordinal)
            && _sourcesByPath.Count == 0)
        {
            store.SetCollectorState(ParserRevisionStateKey, ParserRevision.ToString(), NowEpochMs());
        }
        else if (!string.Equals(storedParserRevision, ParserRevision.ToString(), StringComparison.Ordinal))
        {
            var candidates = new Dictionary<string, List<RevisionCandidate>>(StringComparer.Ordinal);
            var discoveredRollouts = new HashSet<string>(StringComparer.Ordinal);
            var revisionSucceeded = true;
            foreach (var filePath in inventory.Paths)
            {
                try
                {
                    var candidate = await DiscoverRevisionSourceAsync(filePath, yields, cancellationToken).ConfigureAwait(false);
                    discoveredRollouts.Add(candidate.RolloutId);
                    if (candidate.Viable)
                    {
                        if (!candidates.TryGetValue(candidate.RolloutId, out var rolloutCandidates))
                        {
                            rolloutCandidates = [];
                            candidates.Add(candidate.RolloutId, rolloutCandidates);
                        }
                        rolloutCandidates.Add(candidate);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    RecordSourceFailure(filePath, error);
                    revisionSucceeded = false;
                    inventorySucceeded = false;
                }
                await slice.ItemProcessedAsync(cancellationToken).ConfigureAwait(false);
            }

            if (discoveredRollouts.Any(rolloutId => !candidates.ContainsKey(rolloutId))) revisionSucceeded = false;
            foreach (var (rolloutId, rolloutCandidates) in candidates)
            {
                var canonical = GetCanonical(rolloutId);
                var selected = rolloutCandidates.FirstOrDefault(candidate =>
                    canonical is not null && PathsEqual(candidate.FilePath, canonical))
                    ?? rolloutCandidates.OrderByDescending(candidate => candidate.ByteOffset)
                        .ThenByDescending(candidate => candidate.SizeBytes)
                        .ThenByDescending(candidate => candidate.ModifiedAtEpochMs)
                        .ThenBy(candidate => candidate.FilePath, PathComparer()).First();
                revisionAttempted.Add(NormalizeKey(selected.FilePath));
                changedFiles++;
                var processed = await ProcessFileAsync(
                    selected.FilePath,
                    new FullParseContext(ParseReason.ParserRevision, rolloutId),
                    yields,
                    cancellationToken).ConfigureAwait(false);
                usageChanged |= processed.Changed;
                revisionSucceeded &= processed.Succeeded;
                inventorySucceeded &= processed.Succeeded;
                await slice.ItemProcessedAsync(cancellationToken).ConfigureAwait(false);
            }
            if (revisionSucceeded)
                store.SetCollectorState(ParserRevisionStateKey, ParserRevision.ToString(), NowEpochMs());
        }

        var sourcesWithUnknownModels = store.ListCanonicalSourcesWithUnknownModels()
            .Select(NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var discoveredPath in inventory.Paths)
        {
            var key = NormalizeKey(discoveredPath);
            if (revisionAttempted.Contains(key))
            {
                await slice.ItemProcessedAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            var filePath = _sourcesByPath.TryGetValue(key, out var known) ? known.FilePath : discoveredPath;
            try
            {
                var stat = GetFileStat(filePath);
                var canonicalUnavailable = known?.RolloutId is { } rolloutId
                    && GetCanonical(rolloutId) is { } canonical
                    && !present.ContainsKey(NormalizeKey(canonical));
                var changed = known is null || !known.IsPresent || known.SizeBytes != stat.Size
                    || known.ModifiedAtEpochMs != stat.ModifiedAtEpochMs || known.ByteOffset < stat.Size
                    || (sourcesWithUnknownModels.Contains(key) && !_unknownModelsAttempted.Contains(key))
                    || canonicalUnavailable;
                if (changed)
                {
                    changedFiles++;
                    var processed = await ProcessFileAsync(
                        filePath, FullParseContext.Inventory, yields, cancellationToken).ConfigureAwait(false);
                    usageChanged |= processed.Changed;
                    inventorySucceeded &= processed.Succeeded;
                    if (processed.Succeeded) _retryAttempts.Remove(key);
                }
            }
            catch (FileNotFoundException)
            {
                if (known?.IsPresent == true) MarkMissing(known.FilePath);
            }
            catch (DirectoryNotFoundException)
            {
                if (known?.IsPresent == true) MarkMissing(known.FilePath);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                RecordSourceFailure(filePath, error);
                inventorySucceeded = false;
            }
            _inventoryPathsProcessed++;
            await slice.ItemProcessedAsync(cancellationToken).ConfigureAwait(false);
        }

        _changedFilesLastSync = changedFiles;
        _diagnostics.CooperativeYieldCount += yields.Count;
        if (inventorySucceeded)
        {
            _lastSuccessfulInventoryEpochMs = NowEpochMs();
            store.SetCollectorState(LastInventoryStateKey, _lastSuccessfulInventoryEpochMs.Value.ToString(), _lastSuccessfulInventoryEpochMs.Value);
            store.SetCollectorState(InventoryYieldCountStateKey, yields.Count.ToString(), _lastSuccessfulInventoryEpochMs.Value);
        }
        _phase = inventorySucceeded && _watcherHealthy && store.CountSourceConflicts() == 0 && _retryAttempts.Count == 0
            ? CollectorPhase.Watching
            : CollectorPhase.Degraded;
        _message = !inventorySucceeded
            ? $"Inventory incomplete after processing {changedFiles} changed sources"
            : changedFiles == 0 ? "Inventory is current" : $"Processed {changedFiles} changed sources";
        ServiceInteractiveCommands();
        var status = CreateStatus();
        PublishStatus(status);
        return new CollectorSyncResult(status, usageChanged);
    }

    private async Task<InventoryResult> ListRolloutsAsync(
        InventoryYieldTracker yields,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        var directories = new Queue<DirectoryInventoryWork>();
        foreach (var root in _observationRoots) directories.Enqueue(new DirectoryInventoryWork(root, root));
        var visitedLexicalDirectories = new HashSet<string>(PathComparer());
        var visitedResolvedDirectories = new HashSet<string>(PathComparer());
        var succeeded = true;
        var slice = new CooperativeSlice(_options, yields, YieldToMailboxAsync);
        while (directories.TryDequeue(out var work))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetFullPath(work.DirectoryPath);
            IEnumerable<FileSystemInfo> entries;
            try
            {
                var directoryInfo = new DirectoryInfo(directory);
                directoryInfo.Refresh();
                if (!directoryInfo.Exists) continue;
                if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    RecordEnumerationFailure(directory, new IOException("Reparse-point directories are outside collector scope."));
                    succeeded = false;
                    continue;
                }
                var resolvedDirectory = directoryInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? directoryInfo.FullName;
                if (!IsWithin(resolvedDirectory, work.ScopeRoot)
                    || !visitedLexicalDirectories.Add(directory)
                    || !visitedResolvedDirectories.Add(Path.GetFullPath(resolvedDirectory))) continue;
                entries = directoryInfo.EnumerateFileSystemInfos();
                using var enumerator = entries.GetEnumerator();
                while (true)
                {
                    FileSystemInfo entry;
                    try
                    {
                        if (!enumerator.MoveNext()) break;
                        entry = enumerator.Current;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        if (!_observationRoots.Contains(directory, PathComparer())) succeeded = false;
                        break;
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        RecordEnumerationFailure(directory, error);
                        succeeded = false;
                        break;
                    }

                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        await slice.ItemProcessedAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                        directories.Enqueue(new DirectoryInventoryWork(entry.FullName, work.ScopeRoot));
                    else if (entry.Name.StartsWith("rollout-", StringComparison.Ordinal)
                        && entry.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                        paths.Add(Path.GetFullPath(entry.FullName));
                    await slice.ItemProcessedAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (DirectoryNotFoundException)
            {
                if (!_observationRoots.Contains(directory, PathComparer())) succeeded = false;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                RecordEnumerationFailure(directory, error);
                succeeded = false;
            }
        }
        paths.Sort(PathComparer());
        return new InventoryResult(paths, succeeded);
    }

    private async Task DrainWatcherPathsAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        var succeeded = true;
        var usageChanged = false;
        while (_pendingPaths.Count > 0)
        {
            var batch = _pendingPaths.Take(_options.WatcherBatchSize).ToArray();
            foreach (var (key, _) in batch) _pendingPaths.Remove(key);
            foreach (var (_, observedPath) in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = NormalizeKey(observedPath);
                var path = _sourcesByPath.TryGetValue(key, out var known) ? known.FilePath : observedPath;
                try
                {
                    _ = GetFileStat(path);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    if (known?.IsPresent == true)
                    {
                        var wasCanonical = known.RolloutId is { } rolloutId
                            && GetCanonical(rolloutId) is { } canonical && PathsEqual(path, canonical);
                        MarkMissing(path);
                        processed++;
                        if (wasCanonical)
                        {
                            if (_sourceKeysByRollout.TryGetValue(known.RolloutId!, out var candidateKeys))
                            {
                                foreach (var candidateKey in candidateKeys)
                                {
                                    var candidate = _sourcesByPath[candidateKey];
                                    if (candidate.IsPresent && !PathsEqual(candidate.FilePath, path)) AddPendingPath(candidate.FilePath);
                                }
                            }
                        }
                    }
                    continue;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    RecordSourceFailure(path, error);
                    ScheduleRetry(path);
                    succeeded = false;
                    continue;
                }

                var result = await ProcessFileAsync(path, FullParseContext.Inventory, null, cancellationToken).ConfigureAwait(false);
                usageChanged |= result.Changed;
                processed++;
                if (result.Succeeded) _retryAttempts.Remove(key);
                else
                {
                    ScheduleRetry(path);
                    succeeded = false;
                }
            }
            if (_pendingPaths.Count > 0) await YieldToMailboxAsync(cancellationToken).ConfigureAwait(false);
        }
        _changedFilesLastSync = processed;
        _phase = succeeded && _watcherHealthy && RequireStore().CountSourceConflicts() == 0 && _retryAttempts.Count == 0
            ? CollectorPhase.Watching : CollectorPhase.Degraded;
        _message = !succeeded
            ? $"Watcher retry scheduled after processing {processed} paths"
            : processed == 0 ? "Watcher changes are current" : $"Processed {processed} watcher paths";
        if (processed > 0 || usageChanged || !succeeded) PublishStatus();
    }

    private void QueueWatcherPath(string filePath)
    {
        if (_stopping || !IsResolvedObservedRollout(filePath)) return;
        var key = NormalizeKey(filePath);
        _retryAttempts.Remove(key);
        _pendingPaths[key] = Path.GetFullPath(filePath);
        ScheduleDebounce(_options.WatcherDebounce);
    }

    private void AddPendingPath(string filePath)
    {
        if (!IsResolvedObservedRollout(filePath)) return;
        _pendingPaths[NormalizeKey(filePath)] = Path.GetFullPath(filePath);
    }

    private void ScheduleDebounce(TimeSpan delay)
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation = ++_debounceGeneration;
        _ = EnqueueAfterDelayAsync(new DrainWatcherCommand(generation), delay, _debounce.Token);
    }

    private void ScheduleRetry(string filePath)
    {
        var key = NormalizeKey(filePath);
        var attempt = _retryAttempts.GetValueOrDefault(key) + 1;
        _retryAttempts[key] = attempt;
        if (attempt > _options.RetryAttempts) return;
        var multiplier = 1L << (attempt - 1);
        var delayMs = Math.Min(_options.RetryBaseDelay.TotalMilliseconds * multiplier, 4_000);
        _ = EnqueueAfterDelayAsync(
            new RetryPathCommand(Path.GetFullPath(filePath), attempt),
            TimeSpan.FromMilliseconds(delayMs),
            _lifetime.Token);
    }

    private async Task EnqueueAfterDelayAsync(CollectorCommand command, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _commands.Writer.TryWrite(command);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<ProcessResult> ProcessFileAsync(
        string filePath,
        FullParseContext context,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        if (!IsResolvedObservedRollout(filePath)) return new ProcessResult(false, true);
        try
        {
            var key = NormalizeKey(filePath);
            _conflictsAttempted.Add(key);
            _unknownModelsAttempted.Add(key);
            var changed = context.Reason == ParseReason.ParserRevision || !_runtimeByPath.TryGetValue(key, out var runtime)
                ? await ProcessFullFileAsync(filePath, context, yields, cancellationToken).ConfigureAwait(false)
                : await ProcessIncrementalFileAsync(filePath, runtime, yields, cancellationToken).ConfigureAwait(false);
            _diagnostics.FilesScanned++;
            return new ProcessResult(changed, true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RecordSourceFailure(filePath, error);
            return new ProcessResult(false, false);
        }
    }

    private async Task<bool> ProcessIncrementalFileAsync(
        string filePath,
        SourceRuntime runtime,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        var canonical = GetCanonical(runtime.RolloutId);
        if (canonical is null || !PathsEqual(canonical, filePath))
            return await ProcessFullFileAsync(filePath, FullParseContext.Inventory, yields, cancellationToken).ConfigureAwait(false);
        if (GetFileStat(filePath).Size < runtime.ByteOffset)
            return await ProcessFullFileAsync(
                filePath, new FullParseContext(ParseReason.CanonicalPrefixRewrite, runtime.RolloutId), yields, cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadStableAppendSnapshotAsync(filePath, runtime.ByteOffset, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(snapshot.OldBoundaryHash, runtime.BoundaryHash, StringComparison.Ordinal))
            return await ProcessFullFileAsync(
                filePath, new FullParseContext(ParseReason.CanonicalPrefixRewrite, runtime.RolloutId), yields, cancellationToken).ConfigureAwait(false);
        if (snapshot.AppendedBytes.Length == 0) return false;
        if (_testHooks?.AfterStableAppendSnapshotCapturedAsync is { } hook)
            await hook(filePath, cancellationToken).ConfigureAwait(false);
        var result = await ParseAsync(snapshot.AppendedBytes, runtime.RolloutId, runtime.State, yields, cancellationToken).ConfigureAwait(false);
        RejectInternalDamage(filePath, result);
        var resolvedTurns = result.State.TurnModels.Keys.ToHashSet(StringComparer.Ordinal);
        if (runtime.State.UnresolvedTurnIds.Concat(runtime.State.ProvisionalTurnIds).Any(resolvedTurns.Contains))
            return await ProcessFullFileAsync(
                filePath, new FullParseContext(ParseReason.LateModelResolution, runtime.RolloutId), yields, cancellationToken).ConfigureAwait(false);
        AddDiagnostics(result.Diagnostics);
        if (result.StableByteLength == 0) return false;
        var newOffset = runtime.ByteOffset + result.StableByteLength;
        var hash = snapshot.BoundaryHashAt(result.StableByteLength);
        var source = SourceFrom(filePath, snapshot.Stat, newOffset, hash, CanonicalStatus.Canonical, PrefixStatus.Matches, null);
        var appended = RequireStore().AppendRolloutSource(new AppendRolloutSourceInput(
            result.Metadata, UsageInputs(result), source, NowEpochMs()));
        RememberSource(new SourceFileInput(
            source.FilePath, result.Metadata.RolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
            source.ByteOffset, source.PrefixHash, source.PrefixStatus, source.CanonicalStatus,
            source.IsPresent, source.LastScannedAtEpochMs, source.LastError));
        _runtimeByPath[NormalizeKey(filePath)] = new SourceRuntime(result.Metadata.RolloutId, newOffset, hash, result.State);
        return appended.Inserted > 0;
    }

    private async Task<AppendSnapshot> ReadStableAppendSnapshotAsync(
        string filePath,
        long byteOffset,
        CancellationToken cancellationToken)
    {
        var before = GetFileStat(filePath);
        if (before.Size < byteOffset) throw new IOException("Source was truncated before reading appended bytes.");
        var start = Math.Max(0, byteOffset - BoundaryWindowBytes);
        var snapshotLength = before.Size - start;
        if (snapshotLength > int.MaxValue) throw new IOException("Appended rollout snapshot is too large to parse in memory.");
        var bytes = new byte[checked((int)snapshotLength)];
        await using var stream = OpenReadOnlyShared(filePath);
        if (stream.Length != before.Size) throw new IOException("Source changed before reading appended bytes.");
        stream.Position = start;
        await ReadStreamCooperativelyAsync(stream, bytes, null, cancellationToken).ConfigureAwait(false);
        var after = GetFileStat(filePath);
        if (before != after || stream.Length != before.Size)
            throw new IOException("Source changed while reading appended bytes.");
        var prefixLength = checked((int)(byteOffset - start));
        var oldBoundaryHash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, prefixLength))).ToLowerInvariant();
        var appended = bytes.AsSpan(prefixLength).ToArray();
        return new AppendSnapshot(before, bytes, prefixLength, appended, oldBoundaryHash);
    }

    private async Task<bool> ProcessFullFileAsync(
        string filePath,
        FullParseContext context,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        var store = RequireStore();
        var snapshot = await ReadStableFullSnapshotAsync(filePath, yields, cancellationToken).ConfigureAwait(false);
        _sourcesByPath.TryGetValue(NormalizeKey(filePath), out var known);
        var isKnownCanonical = known?.RolloutId is { } knownRollout
            && GetCanonical(knownRollout) is { } knownCanonical && PathsEqual(knownCanonical, filePath);
        if (snapshot is UnsafeSnapshot unsafeSnapshot)
        {
            var confirmed = await ConfirmStableSnapshotAsync(filePath, snapshot, yields, cancellationToken).ConfigureAwait(false);
            if (confirmed is not UnsafeSnapshot confirmedUnsafe)
                throw new IOException("Canonical source safety classification changed between recovery snapshots.");
            if (isKnownCanonical)
            {
                RecordCanonicalConflict(filePath, known!, confirmedUnsafe, "canonical-source-malformed", confirmedUnsafe.Message);
                return false;
            }
            throw new InvalidDataException(confirmedUnsafe.Message);
        }

        var parsed = (ParsedSnapshot)snapshot;
        var result = parsed.Result;
        AddDiagnostics(result.Diagnostics);
        if (known?.RolloutId is { } previousRollout && previousRollout != result.Metadata.RolloutId && isKnownCanonical)
        {
            var confirmed = await ConfirmStableSnapshotAsync(filePath, parsed, yields, cancellationToken).ConfigureAwait(false);
            if (confirmed is not ParsedSnapshot confirmedParsed || confirmedParsed.Result.Metadata.RolloutId != result.Metadata.RolloutId)
                throw new IOException("Canonical source identity changed between recovery snapshots.");
            var message = $"Canonical source rollout changed from {previousRollout} to {result.Metadata.RolloutId}.";
            RecordCanonicalConflict(filePath, known, confirmedParsed, "canonical-source-rollout-changed", message);
            return false;
        }

        var observedAt = NowEpochMs();
        var candidateIdentities = result.Events.Select(EventIdentity).ToArray();
        var existingIdentities = store.GetRolloutEventIdentities(result.Metadata.RolloutId);
        var relation = SignatureRelation(existingIdentities, candidateIdentities);
        if (context.Reason == ParseReason.ParserRevision)
        {
            if (result.Metadata.RolloutId != context.ExpectedRolloutId)
                throw new InvalidDataException($"Canonical source rollout changed from {context.ExpectedRolloutId} to {result.Metadata.RolloutId}.");
            var source = SourceFrom(filePath, parsed.Stat, result.StableByteLength, parsed.BoundaryHash, CanonicalStatus.Canonical, PrefixStatus.Matches, null);
            store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
                result.Metadata,
                UsageInputs(result),
                new CanonicalSourceInput(
                    source.FilePath, source.SizeBytes, source.ModifiedAtEpochMs, source.ByteOffset,
                    source.PrefixHash, source.PrefixStatus, source.LastScannedAtEpochMs, source.LastError),
                observedAt));
            RememberPromotion(result.Metadata.RolloutId, source);
            RememberRuntime(filePath, result, parsed.BoundaryHash);
            return true;
        }

        var canonicalPath = GetCanonical(result.Metadata.RolloutId);
        var isCurrentCanonical = canonicalPath is not null && PathsEqual(canonicalPath, filePath);
        var semanticRelation = SignatureRelation(
            store.GetRolloutSemanticSignatures(result.Metadata.RolloutId),
            result.Events.Select(EventSemanticSignature).ToArray());
        var metadataMatches = SameMetadata(store.GetRolloutMetadata(result.Metadata.RolloutId), result.Metadata);
        var canonicalRewrite = isCurrentCanonical && (context.Reason == ParseReason.CanonicalPrefixRewrite
            || relation is SignatureRelationship.Shorter or SignatureRelationship.Diverged
            || !metadataMatches
            || semanticRelation is SignatureRelationship.Shorter or SignatureRelationship.Diverged);
        if (canonicalRewrite)
        {
            await RecoverCanonicalRewriteAsync(filePath, result.Metadata.RolloutId, parsed, yields, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (relation == SignatureRelationship.Diverged)
        {
            var source = SourceFrom(filePath, parsed.Stat, result.StableByteLength, parsed.BoundaryHash,
                CanonicalStatus.Conflict, PrefixStatus.Diverged, "Candidate diverges from the canonical event prefix.");
            UpsertSource(source, result.Metadata.RolloutId);
            RecordConflict(filePath, "source-diverged", "Rollout source diverges from the canonical event prefix.", result.Metadata.RolloutId);
            _runtimeByPath.Remove(NormalizeKey(filePath));
            return false;
        }
        if (relation == SignatureRelationship.Shorter)
        {
            UpsertSource(SourceFrom(filePath, parsed.Stat, result.StableByteLength, parsed.BoundaryHash,
                CanonicalStatus.Candidate, PrefixStatus.Matches, null), result.Metadata.RolloutId);
            _runtimeByPath.Remove(NormalizeKey(filePath));
            return false;
        }

        var attributionMatches = metadataMatches && semanticRelation is SignatureRelationship.Equal or SignatureRelationship.Extension;
        if (existingIdentities.Count > 0 && !isCurrentCanonical && !attributionMatches)
        {
            var message = "Candidate metadata or model attribution differs from the canonical rollout.";
            UpsertSource(SourceFrom(filePath, parsed.Stat, result.StableByteLength, parsed.BoundaryHash,
                CanonicalStatus.Conflict, PrefixStatus.Diverged, message), result.Metadata.RolloutId);
            RecordConflict(filePath, "source-attribution-diverged", message, result.Metadata.RolloutId);
            _runtimeByPath.Remove(NormalizeKey(filePath));
            return false;
        }

        var canonicalPresent = canonicalPath is not null
            && _sourcesByPath.TryGetValue(NormalizeKey(canonicalPath), out var canonicalSource) && canonicalSource.IsPresent;
        var shouldPromote = relation == SignatureRelationship.Extension || canonicalPath is null || !canonicalPresent || isCurrentCanonical;
        var candidateSource = SourceFrom(filePath, parsed.Stat, result.StableByteLength, parsed.BoundaryHash,
            shouldPromote ? CanonicalStatus.Canonical : CanonicalStatus.Candidate, PrefixStatus.Matches, null);
        if (!shouldPromote)
        {
            UpsertSource(candidateSource, result.Metadata.RolloutId);
            _runtimeByPath.Remove(NormalizeKey(filePath));
            return false;
        }
        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            result.Metadata,
            UsageInputs(result),
            new CanonicalSourceInput(
                candidateSource.FilePath, candidateSource.SizeBytes, candidateSource.ModifiedAtEpochMs,
                candidateSource.ByteOffset, candidateSource.PrefixHash, candidateSource.PrefixStatus,
                candidateSource.LastScannedAtEpochMs, candidateSource.LastError),
            observedAt));
        RememberPromotion(result.Metadata.RolloutId, candidateSource);
        RememberRuntime(filePath, result, parsed.BoundaryHash);
        return relation == SignatureRelationship.Extension || existingIdentities.Count == 0;
    }

    private async Task RecoverCanonicalRewriteAsync(
        string filePath,
        string rolloutId,
        ParsedSnapshot first,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        var second = await ConfirmStableSnapshotAsync(filePath, first, yields, cancellationToken).ConfigureAwait(false);
        if (second is not ParsedSnapshot parsed) throw new InvalidDataException(((UnsafeSnapshot)second).Message);
        if (parsed.Result.Metadata.RolloutId != rolloutId)
            throw new InvalidDataException($"Canonical source rollout changed from {rolloutId} to {parsed.Result.Metadata.RolloutId}.");
        var observedAt = NowEpochMs();
        RequireStore().RecoverDivergedCanonicalSource(new RecoverDivergedCanonicalSourceInput(
            parsed.Result.Metadata,
            UsageInputs(parsed.Result),
            new RecoverableCanonicalSourceInput(filePath, parsed.Stat.Size, parsed.Stat.ModifiedAtEpochMs,
                parsed.Result.StableByteLength, parsed.BoundaryHash, observedAt),
            observedAt));
        var source = SourceFrom(filePath, parsed.Stat, parsed.Result.StableByteLength, parsed.BoundaryHash,
            CanonicalStatus.Canonical, PrefixStatus.Matches, null);
        RememberSource(new SourceFileInput(
            source.FilePath, rolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
            source.ByteOffset, source.PrefixHash, source.PrefixStatus, source.CanonicalStatus,
            source.IsPresent, source.LastScannedAtEpochMs, source.LastError));
        RememberRuntime(filePath, parsed.Result, parsed.BoundaryHash);
    }

    private async Task<FullSnapshot> ReadStableFullSnapshotAsync(
        string filePath,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        var before = GetFileStat(filePath);
        if (before.Size > int.MaxValue) throw new IOException("Rollout source is too large to parse in memory.");
        var buffer = new byte[checked((int)before.Size)];
        await ReadRangeAsync(filePath, 0, buffer, yields, cancellationToken).ConfigureAwait(false);
        var after = GetFileStat(filePath);
        if (before != after) throw new IOException("Source changed while reading a full snapshot.");
        var result = await ParseAsync(buffer, FallbackRolloutId(filePath), null, yields, cancellationToken).ConfigureAwait(false);
        var contentHash = await CooperativeSha256Async(buffer, yields, cancellationToken).ConfigureAwait(false);
        var unsafeContent = result.Diagnostics.MalformedLines > 0 || result.Diagnostics.NonObjectLines > 0
            || result.Diagnostics.OversizedRecordsSkipped > 0;
        var hashLength = unsafeContent
            ? buffer.Length : result.StableByteLength;
        var boundaryHash = ComputeBoundaryHash(buffer, hashLength);
        return unsafeContent
            ? new UnsafeSnapshot(after, contentHash, boundaryHash, $"Stable JSONL content is malformed: {filePath}")
            : new ParsedSnapshot(after, contentHash, boundaryHash, result);
    }

    private async Task<FullSnapshot> ConfirmStableSnapshotAsync(
        string filePath,
        FullSnapshot first,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        await Task.Delay(_options.RecoverySnapshotDelay, cancellationToken).ConfigureAwait(false);
        var second = await ReadStableFullSnapshotAsync(filePath, yields, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(first.ContentHash, second.ContentHash, StringComparison.Ordinal))
            throw new IOException("Canonical source changed between recovery snapshots.");
        return second;
    }

    private async Task<RolloutChunkParseResult> ParseAsync(
        byte[] buffer,
        string fallbackRolloutId,
        RolloutParserState? priorState,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken) =>
        await RolloutParser.ParseChunkCooperativelyAsync(
            buffer,
            fallbackRolloutId,
            new CooperativeParseOptions(
                _options.ParserSliceBytes,
                _options.ParserSliceRecords,
                _options.CooperativeTimeBudget,
                RolloutParser.CooperativeHardMaximumRecordBytes,
                async token =>
                {
                    yields?.Increment();
                    await YieldToMailboxAsync(token).ConfigureAwait(false);
                }),
            priorState,
            cancellationToken).ConfigureAwait(false);

    private async Task<string> CooperativeSha256Async(
        byte[] buffer,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < buffer.Length; offset += _options.ParserSliceBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(_options.ParserSliceBytes, buffer.Length - offset);
            hash.AppendData(buffer, offset, length);
            if (offset + length < buffer.Length)
            {
                yields?.Increment();
                await YieldToMailboxAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task ReadRangeAsync(
        string filePath,
        long offset,
        Memory<byte> buffer,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        await using var stream = OpenReadOnlyShared(filePath);
        stream.Position = offset;
        await ReadStreamCooperativelyAsync(stream, buffer, yields, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadStreamCooperativelyAsync(
        FileStream stream,
        Memory<byte> buffer,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < buffer.Length; offset += _options.ParserSliceBytes)
        {
            var length = Math.Min(_options.ParserSliceBytes, buffer.Length - offset);
            await stream.ReadExactlyAsync(buffer.Slice(offset, length), cancellationToken).ConfigureAwait(false);
            if (offset + length >= buffer.Length) continue;
            yields?.Increment();
            await YieldToMailboxAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static FileStream OpenReadOnlyShared(string filePath) => new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private async Task<RevisionCandidate> DiscoverRevisionSourceAsync(
        string filePath,
        InventoryYieldTracker yields,
        CancellationToken cancellationToken)
    {
        if (!IsResolvedObservedRollout(filePath))
            throw new IOException("Rollout source resolves through a reparse point outside collector scope.");
        var snapshot = await ReadStableFullSnapshotAsync(filePath, yields, cancellationToken).ConfigureAwait(false);
        if (snapshot is UnsafeSnapshot unsafeSnapshot) throw new InvalidDataException(unsafeSnapshot.Message);
        var parsed = (ParsedSnapshot)snapshot;
        _sourcesByPath.TryGetValue(NormalizeKey(filePath), out var known);
        if (known?.RolloutId is { } rolloutId && rolloutId != parsed.Result.Metadata.RolloutId)
            throw new InvalidDataException($"Known source rollout changed from {rolloutId} to {parsed.Result.Metadata.RolloutId}.");
        var viable = known?.CanonicalStatus != CanonicalStatus.Conflict
            || (known?.RolloutId is { } knownRollout && GetCanonical(knownRollout) is { } canonical && PathsEqual(canonical, known.FilePath));
        return new RevisionCandidate(known?.FilePath ?? filePath, parsed.Result.Metadata.RolloutId,
            parsed.Result.StableByteLength, parsed.Stat.Size, parsed.Stat.ModifiedAtEpochMs, viable);
    }

    private void UpsertSource(CandidateSourceInput source, string rolloutId)
    {
        var input = new SourceFileInput(
            source.FilePath, rolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
            source.ByteOffset, source.PrefixHash, source.PrefixStatus, source.CanonicalStatus,
            source.IsPresent, source.LastScannedAtEpochMs, source.LastError);
        RequireStore().UpsertSourceFile(input);
        RememberSource(input);
    }

    private void RememberPromotion(string rolloutId, CandidateSourceInput source)
    {
        _canonicalByRollout[rolloutId] = source.FilePath;
        if (_sourceKeysByRollout.TryGetValue(rolloutId, out var rolloutSourceKeys))
        {
            foreach (var sourceKey in rolloutSourceKeys)
            {
                var existing = _sourcesByPath[sourceKey];
                if (existing.CanonicalStatus == CanonicalStatus.Canonical && !PathsEqual(existing.FilePath, source.FilePath))
                    _sourcesByPath[sourceKey] = existing with { CanonicalStatus = CanonicalStatus.Candidate };
            }
        }
        RememberSource(new SourceFileInput(
            source.FilePath, rolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
            source.ByteOffset, source.PrefixHash, source.PrefixStatus, source.CanonicalStatus,
            source.IsPresent, source.LastScannedAtEpochMs, source.LastError));
    }

    private void RememberRuntime(string filePath, RolloutChunkParseResult result, string boundaryHash) =>
        _runtimeByPath[NormalizeKey(filePath)] = new SourceRuntime(
            result.Metadata.RolloutId, result.StableByteLength, boundaryHash, result.State);

    private void RememberSource(SourceFileInput source)
    {
        var key = NormalizeKey(source.FilePath);
        if (_sourcesByPath.TryGetValue(key, out var previous) && previous.RolloutId is { } previousRollout
            && previousRollout != source.RolloutId && _sourceKeysByRollout.TryGetValue(previousRollout, out var previousKeys))
        {
            previousKeys.Remove(key);
            if (previousKeys.Count == 0) _sourceKeysByRollout.Remove(previousRollout);
        }
        _sourcesByPath[key] = new SourceFileRecord(
            source.FilePath, source.RolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
            source.ByteOffset, source.PrefixHash, source.PrefixStatus, source.CanonicalStatus,
            source.IsPresent, source.LastScannedAtEpochMs, source.LastError);
        if (source.RolloutId is { } rolloutId)
        {
            if (!_sourceKeysByRollout.TryGetValue(rolloutId, out var keys))
            {
                keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _sourceKeysByRollout.Add(rolloutId, keys);
            }
            keys.Add(key);
        }
    }

    private void MarkMissing(string filePath)
    {
        var key = NormalizeKey(filePath);
        if (RequireStore().MarkSourceMissing(filePath, NowEpochMs()) && _sourcesByPath.TryGetValue(key, out var known))
            _sourcesByPath[key] = known with { IsPresent = false, LastScannedAtEpochMs = NowEpochMs() };
        _runtimeByPath.Remove(key);
    }

    private void RecordCanonicalConflict(
        string filePath,
        SourceFileRecord known,
        FullSnapshot snapshot,
        string code,
        string message)
    {
        var updated = known with
        {
            SizeBytes = snapshot.Stat.Size,
            ModifiedAtEpochMs = snapshot.Stat.ModifiedAtEpochMs,
            ByteOffset = snapshot.Stat.Size,
            PrefixHash = snapshot.BoundaryHash,
            PrefixStatus = PrefixStatus.Diverged,
            CanonicalStatus = CanonicalStatus.Conflict,
            IsPresent = true,
            LastScannedAtEpochMs = NowEpochMs(),
            LastError = message,
        };
        RequireStore().UpsertSourceFile(ToInput(updated));
        _sourcesByPath[NormalizeKey(filePath)] = updated;
        RecordConflict(filePath, code, message, known.RolloutId);
        _runtimeByPath.Remove(NormalizeKey(filePath));
    }

    private void RecordConflict(string filePath, string code, string message, string? rolloutId) =>
        RequireStore().RecordSourceConflict(new SourceConflictInput(
            _runId, filePath, code, message,
            JsonSerializer.Serialize(new { rolloutId }), NowEpochMs()));

    private void RecordSourceFailure(string filePath, Exception error)
    {
        var key = NormalizeKey(filePath);
        if (_sourcesByPath.TryGetValue(key, out var known))
        {
            var updated = known with { IsPresent = true, LastScannedAtEpochMs = NowEpochMs(), LastError = error.Message };
            RequireStore().UpsertSourceFile(ToInput(updated));
            _sourcesByPath[key] = updated;
        }
        AddDiagnostic(filePath, "source-read-retry", error.Message, DiagnosticSeverity.Warning);
    }

    private void RecordEnumerationFailure(string directory, Exception error) =>
        AddDiagnostic(directory, "inventory-enumeration-failed", error.Message, DiagnosticSeverity.Warning);

    private void AddDiagnostic(string? path, string code, string message, DiagnosticSeverity severity) =>
        RequireStore().AddDiagnostic(new CollectorDiagnosticInput(
            _runId, path, severity, code, message, null, NowEpochMs()));

    private void AddDiagnostics(RolloutParseDiagnostics value)
    {
        _diagnostics.MalformedLines += value.MalformedLines + value.NonObjectLines + value.OversizedRecordsSkipped;
        _diagnostics.DuplicateSnapshotsSkipped += value.DuplicateSnapshotsSkipped;
        _diagnostics.ZeroBreakdownSnapshotsSkipped += value.ZeroBreakdownSnapshotsSkipped;
        _diagnostics.InvalidTokenRelationshipsSkipped += value.InvalidTokenRelationshipsSkipped;
    }

    private void Heartbeat()
    {
        if (!_started || _runId is null || _store is null) return;
        _lastHeartbeatEpochMs = NowEpochMs();
        _store.HeartbeatCollector(new CollectorRunHeartbeatInput(
            _runId, _lastHeartbeatEpochMs.Value,
            new Dictionary<string, string> { ["phase"] = _phase.ToString().ToLowerInvariant() }));
    }

    private void CompleteQuery(QueryCommand query)
    {
        try
        {
            EnsureStarted();
            _testHooks?.BeforeQuery?.Invoke();
            query.Completion.TrySetResult(RequireStore().QueryEvents(query.Query));
        }
        catch (Exception error)
        {
            query.Completion.TrySetException(error);
        }
    }

    private async ValueTask AwaitWhileServingInteractiveAsync(
        ValueTask operation,
        CancellationToken cancellationToken)
    {
        if (operation.IsCompletedSuccessfully)
        {
            await operation.ConfigureAwait(false);
            return;
        }
        var task = operation.AsTask();
        while (!task.IsCompleted)
        {
            var mailboxReady = _commands.Reader.WaitToReadAsync(cancellationToken).AsTask();
            if (await Task.WhenAny(task, mailboxReady).ConfigureAwait(false) == task) break;
            cancellationToken.ThrowIfCancellationRequested();
            if (!await mailboxReady.ConfigureAwait(false)) break;
            ServiceInteractiveCommands();
            PublishInventoryProgress("Inventory enumeration pending");
        }
        await task.ConfigureAwait(false);
    }

    private void ServiceInteractiveCommands()
    {
        if (_commands.Reader.TryPeek(out _)) _testHooks?.BeforeInteractiveDispatch?.Invoke();
        while (_commands.Reader.TryRead(out var command))
        {
            switch (command)
            {
                case StatusCommand status when status.CancellationToken.IsCancellationRequested:
                    status.Completion.TrySetCanceled(status.CancellationToken);
                    break;
                case StatusCommand status:
                    status.Completion.TrySetResult(CreateStatus());
                    break;
                case QueryCommand query when query.CancellationToken.IsCancellationRequested:
                    query.Completion.TrySetCanceled(query.CancellationToken);
                    break;
                case QueryCommand query:
                    CompleteQuery(query);
                    break;
                case HeartbeatCommand:
                    Heartbeat();
                    break;
                case WatcherWakeCommand:
                    DrainWatcherInbox();
                    break;
                case TimerInventoryCommand:
                    Interlocked.Exchange(ref _timerInventoryQueued, 0);
                    break;
                default:
                    _deferredCommands.Enqueue(command);
                    break;
            }
        }
    }

    private async ValueTask YieldToMailboxAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ServiceInteractiveCommands();
        if (_phase == CollectorPhase.Syncing) PublishInventoryProgress("Reconciling local rollouts");
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void PublishInventoryProgress(string stage)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastInventoryProgressPublishedTimestamp != 0
            && Stopwatch.GetElapsedTime(_lastInventoryProgressPublishedTimestamp, now) < TimeSpan.FromMilliseconds(250)) return;
        _lastInventoryProgressPublishedTimestamp = now;
        _message = _inventoryPathsEnumerated > 0
            ? $"{stage}: {_inventoryPathsProcessed}/{_inventoryPathsEnumerated} sources"
            : stage;
        if (_lastHeartbeatEpochMs is null
            || DateTimeOffset.UtcNow - FromEpoch(_lastHeartbeatEpochMs.Value) >= TimeSpan.FromSeconds(1))
            Heartbeat();
        PublishStatus();
    }

    private async Task StopCoreAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _debounce?.Cancel();
        _debounce?.Dispose();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _inventoryTimer?.Dispose();
        _heartbeatTimer?.Dispose();
        if (_inventoryTimerTask is not null) await _inventoryTimerTask.ConfigureAwait(false);
        if (_heartbeatTimerTask is not null) await _heartbeatTimerTask.ConfigureAwait(false);
        _commands.Writer.TryComplete();
        if (_store is not null)
        {
            if (_runId is not null)
            {
                var completedAt = NowEpochMs();
                _store.FinishCollectorRun(new CollectorRunFinishInput(
                    _runId, CollectorRunStatus.Succeeded, completedAt,
                    _diagnostics.FilesScanned, 0,
                    _diagnostics.MalformedLines, null));
            }
            _store.Dispose();
            _store = null;
        }
        _phase = CollectorPhase.Stopped;
        _message = "Collector stopped";
        PublishStatus();
    }

    private CollectorStatus CreateStatus()
    {
        var store = _store;
        var conflicts = store?.CountSourceConflicts() ?? 0;
        var phase = conflicts > 0 && _phase == CollectorPhase.Watching ? CollectorPhase.Degraded : _phase;
        return new CollectorStatus(
            phase,
            _options.DatabasePath,
            _runStartedEpochMs == 0 ? null : FromEpoch(_runStartedEpochMs),
            _lastSuccessfulInventoryEpochMs is { } inventory ? FromEpoch(inventory) : null,
            _lastHeartbeatEpochMs is { } heartbeat ? FromEpoch(heartbeat) : null,
            store?.CountPresentSources() ?? 0,
            _pendingPaths.Count + _watcherInbox.Count,
            _changedFilesLastSync,
            conflicts,
            _coverage,
            _gap,
            _message,
            new CollectorDiagnostics(
                _diagnostics.FilesScanned,
                _diagnostics.MalformedLines,
                _diagnostics.DuplicateSnapshotsSkipped,
                _diagnostics.ZeroBreakdownSnapshotsSkipped,
                _diagnostics.InvalidTokenRelationshipsSkipped,
                _diagnostics.CooperativeYieldCount));
    }

    private void PublishStatus() => PublishStatus(CreateStatus());

    private void PublishStatus(CollectorStatus status)
    {
        var handlers = StatusChanged;
        if (handlers is null) return;
        foreach (EventHandler<CollectorStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, status);
            }
            catch
            {
                // UI observers cannot terminate the collector actor.
            }
        }
    }

    private void Degrade(string message)
    {
        _phase = CollectorPhase.Degraded;
        _message = message;
        PublishStatus();
    }

    private string? GetCanonical(string rolloutId) =>
        _canonicalByRollout.TryGetValue(rolloutId, out var canonical) ? canonical : null;

    private bool IsLexicallyObservedRollout(string filePath)
    {
        if (!Path.GetFileName(filePath).StartsWith("rollout-", StringComparison.Ordinal)
            || !filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        var fullPath = Path.GetFullPath(filePath);
        return _observationRoots.Any(root => IsWithin(fullPath, root));
    }

    private bool IsResolvedObservedRollout(string filePath)
    {
        if (!IsLexicallyObservedRollout(filePath)) return false;
        var fullPath = Path.GetFullPath(filePath);
        var root = _observationRoots.First(candidate => IsWithin(fullPath, candidate));
        var relative = Path.GetRelativePath(root, fullPath);
        var current = Path.GetFullPath(root);
        try
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    break;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static CandidateSourceInput SourceFrom(
        string filePath,
        SourceStat stat,
        long byteOffset,
        string boundaryHash,
        CanonicalStatus canonicalStatus,
        PrefixStatus prefixStatus,
        string? lastError) =>
        new(filePath, stat.Size, stat.ModifiedAtEpochMs, byteOffset, boundaryHash,
            prefixStatus, canonicalStatus, true, NowEpochMs(), lastError);

    private static IReadOnlyList<UsageEventInput> UsageInputs(RolloutChunkParseResult result) =>
        result.Events.Select(value => new UsageEventInput(
            value.TokenEventOrdinal,
            DateTimeOffset.Parse(value.TimestampUtc).ToUnixTimeMilliseconds(),
            value.Model,
            value.InputTokens,
            value.CachedInputTokens,
            value.OutputTokens,
            value.ReasoningOutputTokens,
            value.DeterministicSignature)).ToArray();

    private static string EventIdentity(ParsedRolloutUsageEvent value) => JsonSerializer.Serialize(new object[]
    {
        DateTimeOffset.Parse(value.TimestampUtc).ToUnixTimeMilliseconds(),
        value.InputTokens,
        value.CachedInputTokens,
        value.OutputTokens,
        value.ReasoningOutputTokens,
    });

    private static string EventSemanticSignature(ParsedRolloutUsageEvent value) => JsonSerializer.Serialize(new object[]
    {
        DateTimeOffset.Parse(value.TimestampUtc).ToUnixTimeMilliseconds(),
        value.Model,
        value.InputTokens,
        value.CachedInputTokens,
        value.OutputTokens,
        value.ReasoningOutputTokens,
    });

    private static SignatureRelationship SignatureRelation(
        IReadOnlyList<string> existing,
        IReadOnlyList<string> candidate)
    {
        var common = Math.Min(existing.Count, candidate.Count);
        for (var index = 0; index < common; index++)
            if (!string.Equals(existing[index], candidate[index], StringComparison.Ordinal)) return SignatureRelationship.Diverged;
        if (existing.Count == candidate.Count) return SignatureRelationship.Equal;
        return candidate.Count > existing.Count ? SignatureRelationship.Extension : SignatureRelationship.Shorter;
    }

    private static bool SameMetadata(RolloutMetadata? left, RolloutMetadata right) =>
        left is not null && left.RolloutId == right.RolloutId && left.ConversationId == right.ConversationId
        && left.ParentThreadId == right.ParentThreadId && left.ThreadType == right.ThreadType
        && left.AgentRole == right.AgentRole && left.AgentPath == right.AgentPath
        && left.AgentNickname == right.AgentNickname;

    private static void RejectInternalDamage(string filePath, RolloutChunkParseResult result)
    {
        if (result.Diagnostics.MalformedLines > 0 || result.Diagnostics.NonObjectLines > 0
            || result.Diagnostics.OversizedRecordsSkipped > 0)
            throw new InvalidDataException($"Stable JSONL content is malformed: {filePath}");
    }

    private static string ComputeBoundaryHash(byte[] buffer, int stableByteLength)
    {
        var start = Math.Max(0, stableByteLength - BoundaryWindowBytes);
        return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(start, stableByteLength - start))).ToLowerInvariant();
    }

    private static string FallbackRolloutId(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (!name.StartsWith("rollout-", StringComparison.Ordinal)) return name;
        var separators = 0;
        for (var index = 0; index < name.Length; index++)
        {
            if (name[index] != '-') continue;
            separators++;
            if (separators == 2) return name[(index + 1)..];
        }
        return name;
    }

    private static SourceStat GetFileStat(string filePath)
    {
        var info = new FileInfo(filePath);
        info.Refresh();
        if (!info.Exists) throw new FileNotFoundException("Rollout source does not exist.", filePath);
        return new SourceStat(info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
    }

    private static SourceFileInput ToInput(SourceFileRecord value) => new(
        value.FilePath, value.RolloutId, value.SizeBytes, value.ModifiedAtEpochMs,
        value.ByteOffset, value.PrefixHash, value.PrefixStatus, value.CanonicalStatus,
        value.IsPresent, value.LastScannedAtEpochMs, value.LastError);

    private static long NowEpochMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromEpoch(long epochMs) => DateTimeOffset.FromUnixTimeMilliseconds(epochMs);

    private static string NormalizeKey(string filePath)
    {
        var path = Path.GetFullPath(filePath);
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private UsageStore RequireStore() => _store ?? throw new InvalidOperationException("Collector is not started.");

    private void EnsureStarted()
    {
        if (!_started || _store is null) throw new InvalidOperationException("Collector is not started.");
    }

    private static async Task CompleteAsync<T>(
        TaskCompletionSource<T> completion,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ownerCancellationToken,
        CancellationToken requestCancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ownerCancellationToken, requestCancellationToken);
        try
        {
            completion.TrySetResult(await operation(linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(requestCancellationToken);
        }
        catch (OperationCanceledException) when (ownerCancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(ownerCancellationToken);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }

    private static void ValidateOptions(CollectorOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CodexHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        if (options.WatcherDebounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.WatcherDebounce));
        if (options.FullInventoryInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.FullInventoryInterval));
        if (options.RecoverySnapshotDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.RecoverySnapshotDelay));
        if (options.RetryBaseDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.RetryBaseDelay));
        if (options.RetryAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(options.RetryAttempts));
        if (options.WatcherBatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(options.WatcherBatchSize));
        if (options.CooperativeItemLimit <= 0) throw new ArgumentOutOfRangeException(nameof(options.CooperativeItemLimit));
        if (options.CooperativeTimeBudget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.CooperativeTimeBudget));
        if (options.ParserSliceBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.ParserSliceBytes));
        if (options.ParserSliceRecords <= 0) throw new ArgumentOutOfRangeException(nameof(options.ParserSliceRecords));
    }

    private abstract record CollectorCommand
    {
        public virtual void CancelCompletion()
        {
        }
    }

    private sealed record StartCommand(
        TaskCompletionSource<CollectorStatus> Completion,
        CancellationToken CancellationToken) : CollectorCommand
    {
        public override void CancelCompletion() => Completion.TrySetCanceled();
    }

    private sealed record ManualInventoryCommand(
        TaskCompletionSource<CollectorSyncResult> Completion,
        CancellationToken CancellationToken) : CollectorCommand
    {
        public override void CancelCompletion() => Completion.TrySetCanceled();
    }

    private sealed record TimerInventoryCommand : CollectorCommand;

    private sealed record InitialInventoryCommand : CollectorCommand;

    private sealed record WatcherWakeCommand : CollectorCommand;

    private sealed record DrainWatcherCommand(long Generation) : CollectorCommand;

    private sealed record RetryPathCommand(string FilePath, int Attempt) : CollectorCommand;

    private sealed record HeartbeatCommand : CollectorCommand;

    private sealed record StatusCommand(
        TaskCompletionSource<CollectorStatus> Completion,
        CancellationToken CancellationToken) : CollectorCommand
    {
        public override void CancelCompletion() => Completion.TrySetCanceled();
    }

    private sealed record QueryCommand(
        UsageEventQuery Query,
        TaskCompletionSource<IReadOnlyList<StoredUsageEvent>> Completion,
        CancellationToken CancellationToken) : CollectorCommand
    {
        public override void CancelCompletion() => Completion.TrySetCanceled();
    }

    private sealed record SourceRuntime(
        string RolloutId,
        long ByteOffset,
        string BoundaryHash,
        RolloutParserState State);

    private readonly record struct SourceStat(long Size, long ModifiedAtEpochMs);

    private sealed record AppendSnapshot(
        SourceStat Stat,
        byte[] SnapshotBytes,
        int PrefixLength,
        byte[] AppendedBytes,
        string OldBoundaryHash)
    {
        public string BoundaryHashAt(int stableAppendedBytes)
        {
            if (stableAppendedBytes < 0 || stableAppendedBytes > AppendedBytes.Length)
                throw new ArgumentOutOfRangeException(nameof(stableAppendedBytes));
            return ComputeBoundaryHash(SnapshotBytes, checked(PrefixLength + stableAppendedBytes));
        }
    }

    private abstract record FullSnapshot(SourceStat Stat, string ContentHash, string BoundaryHash);

    private sealed record ParsedSnapshot(
        SourceStat Stat,
        string ContentHash,
        string BoundaryHash,
        RolloutChunkParseResult Result) : FullSnapshot(Stat, ContentHash, BoundaryHash);

    private sealed record UnsafeSnapshot(
        SourceStat Stat,
        string ContentHash,
        string BoundaryHash,
        string Message) : FullSnapshot(Stat, ContentHash, BoundaryHash);

    private sealed record FullParseContext(ParseReason Reason, string? ExpectedRolloutId)
    {
        public static FullParseContext Inventory { get; } = new(ParseReason.Inventory, null);
    }

    private enum ParseReason
    {
        Inventory,
        CanonicalPrefixRewrite,
        LateModelResolution,
        ParserRevision,
    }

    private enum SignatureRelationship
    {
        Equal,
        Extension,
        Shorter,
        Diverged,
    }

    private sealed record ProcessResult(bool Changed, bool Succeeded);

    private sealed record RevisionCandidate(
        string FilePath,
        string RolloutId,
        long ByteOffset,
        long SizeBytes,
        long ModifiedAtEpochMs,
        bool Viable);

    private sealed record InventoryResult(IReadOnlyList<string> Paths, bool Succeeded);

    private sealed record DirectoryInventoryWork(string DirectoryPath, string ScopeRoot);

    private sealed class InventoryYieldTracker
    {
        public long Count { get; private set; }

        public void Increment() => Count++;
    }

    private sealed class CooperativeSlice(
        CollectorOptions options,
        InventoryYieldTracker yields,
        Func<CancellationToken, ValueTask> yieldAsync)
    {
        private int _items;
        private long _started = Stopwatch.GetTimestamp();

        public async ValueTask ItemProcessedAsync(CancellationToken cancellationToken)
        {
            _items++;
            if (_items < options.CooperativeItemLimit
                && Stopwatch.GetElapsedTime(_started) < options.CooperativeTimeBudget) return;
            yields.Increment();
            await yieldAsync(cancellationToken).ConfigureAwait(false);
            _items = 0;
            _started = Stopwatch.GetTimestamp();
        }
    }

    private sealed class MutableDiagnostics
    {
        public long FilesScanned { get; set; }
        public long MalformedLines { get; set; }
        public long DuplicateSnapshotsSkipped { get; set; }
        public long ZeroBreakdownSnapshotsSkipped { get; set; }
        public long InvalidTokenRelationshipsSkipped { get; set; }
        public long CooperativeYieldCount { get; set; }
    }

}

internal sealed record CollectorTestHooks(
    Func<string, CancellationToken, ValueTask>? AfterStableAppendSnapshotCapturedAsync = null,
    Func<CancellationToken, ValueTask>? BeforeInventoryEnumerationAsync = null,
    Action? BeforeQuery = null,
    Action? BeforeInteractiveDispatch = null,
    Action? AfterInventoryCompleted = null);
