using System.Diagnostics;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexUsage.Domain;

[assembly: InternalsVisibleTo("CodexUsage.Infrastructure.Tests")]

namespace CodexUsage.Infrastructure.Collection;

public sealed class UsageCollector : IUsageCollector
{
    private const int BoundaryWindowBytes = 64 * 1024;
    private const long ReverseReconciliationMaximumBytes = 64L * 1024 * 1024;
    private const int ParserRevision = 17;
    private const string PartialSourceErrorPrefix = "partial-opaque-oversized:";
    private static readonly TimeSpan RepeatedFailureDiagnosticInterval = TimeSpan.FromMinutes(5);
    private const string ParserRevisionStateKey = "rollout_parser_revision";
    private const string LastInventoryStateKey = "last_successful_inventory_epoch_ms";
    private const string InventoryRunCountStateKey = "full_inventory_run_count";
    private const string InventoryYieldCountStateKey = "full_inventory_last_yield_count";

    private readonly CollectorOptions _options;
    private readonly string[] _observationRoots;
    private readonly string _sessionIndexPath;
    private readonly Channel<CollectorCommand> _commands;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _ownerTask;
    private readonly Dictionary<string, SourceRuntime> _runtimeByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceFileRecord> _sourcesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _sourceKeysByRollout = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _canonicalByRollout = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _watcherInbox = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sessionIndexTitles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetryState> _retryStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FailureDiagnosticState> _failureDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _partialSourceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _conflictsAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknownModelsAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Queue<CollectorCommand> _deferredCommands = [];
    private readonly MutableDiagnostics _diagnostics = new();
    private IReadOnlyList<string> _latestInventoryPaths = [];
    private readonly Dictionary<string, RecoverySnapshotCacheEntry> _recoverySnapshotsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<RecoverySeed>> _fullRecoveryIndexByRollout =
        new(StringComparer.Ordinal);
    private bool _fullRecoveryIndexBuilt;

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
    private long _usageRevision;
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
    private long _fullReconcileBytesRead;
    private long _appendBytesRead;
    private int _watcherWakeQueued;
    private int _sessionIndexWatcherPending;
    private bool _sessionIndexRefreshPending;
    private int _sessionIndexConsecutiveFailures;
    private string? _watcherErrorInbox;
    private int _disposeStarted;
    private int _lifetimeDisposed;
    private readonly CollectorTestHooks? _testHooks;
    private readonly ISourceIdentityReader _sourceIdentityReader;

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
        _sourceIdentityReader = testHooks?.SourceIdentityReader ?? new WindowsSourceIdentityReader();
        _observationRoots =
        [
            Path.Combine(_options.CodexHome, "sessions"),
            Path.Combine(_options.CodexHome, "archived_sessions"),
        ];
        _sessionIndexPath = Path.Combine(_options.CodexHome, "session_index.jsonl");
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

    public ValueTask<IReadOnlyList<MainThreadOption>> QueryRecentMainThreadsAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        return RequestAsync<IReadOnlyList<MainThreadOption>>(
            (completion, token) => new QueryRecentMainThreadsCommand(maximumCount, completion, token), cancellationToken);
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
                        if (_retryStates.TryGetValue(NormalizeKey(retry.FilePath), out var retryState)
                            && retryState.Generation == retry.Generation
                            && retryState.Scheduled)
                        {
                            retryState.Scheduled = false;
                            retryState.InFlight = AddPendingPath(retry.FilePath);
                            if (retryState.InFlight) ScheduleDebounce(TimeSpan.Zero);
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
                    case QueryRecentMainThreadsCommand query when query.CancellationToken.IsCancellationRequested:
                        query.Completion.TrySetCanceled(query.CancellationToken);
                        break;
                    case QueryRecentMainThreadsCommand query:
                        CompleteRecentMainThreadsQuery(query);
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

    private async Task<CollectorStatus> StartCoreAsync(CancellationToken cancellationToken)
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
        await RehydrateCheckpointsAsync(cancellationToken).ConfigureAwait(false);
        _started = true;
        if (_options.EnableWatchers) StartWatchers();
        StartTimers();
        _phase = CollectorPhase.Syncing;
        _message = "Ledger ready; initial inventory queued";
        PublishStatus();
        if (!_commands.Writer.TryWrite(new InitialInventoryCommand()))
            throw new ObjectDisposedException(nameof(UsageCollector));
        return CreateStatus();
    }

    private async Task RehydrateCheckpointsAsync(CancellationToken cancellationToken)
    {
        var store = RequireStore();
        var hits = 0;
        var misses = 0;
        long bytesRead = 0;
        foreach (var checkpoint in store.ListRolloutCheckpoints())
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckpointRehydrateResult result;
            try
            {
                result = await TryRehydrateCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsCheckpointSourceAccessFailure(exception))
            {
                result = CheckpointRehydrateResult.Miss($"Checkpoint source is unavailable: {exception.Message}");
            }
            bytesRead = checked(bytesRead + result.BytesRead);
            if (result.Runtime is { } runtime)
            {
                _runtimeByPath[NormalizeKey(checkpoint.FilePath)] = runtime;
                hits++;
                continue;
            }

            misses++;
            store.DeleteRolloutCheckpoint(checkpoint.FilePath);
            AddDiagnostic(
                checkpoint.FilePath,
                "checkpoint-invalidated",
                result.Reason ?? "Checkpoint validation failed.",
                DiagnosticSeverity.Info);
        }

        if (hits > 0 || misses > 0)
        {
            store.AddDiagnostic(new CollectorDiagnosticInput(
                _runId,
                null,
                DiagnosticSeverity.Info,
                "checkpoint-rehydrate-summary",
                $"Rehydrated {hits} rollout checkpoints; invalidated {misses}; read {bytesRead} source bytes.",
                JsonSerializer.Serialize(new { hits, misses, bytesRead }),
                NowEpochMs()));
        }
    }

    private static bool IsCheckpointSourceAccessFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or Win32Exception;

    private async Task<CheckpointRehydrateResult> TryRehydrateCheckpointAsync(
        RolloutCheckpointRecord checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.CheckpointFormatRevision != RolloutParserStateCodec.FormatRevision)
            return CheckpointRehydrateResult.Miss("Checkpoint format revision changed.");
        if (checkpoint.ParserRevision != ParserRevision)
            return CheckpointRehydrateResult.Miss("Parser revision changed.");
        if (checkpoint.SourceIdentity.Kind != SourceIdentityKind.WindowsFileId)
            return CheckpointRehydrateResult.Miss("Source filesystem identity is unavailable; full reconciliation is required.");
        if (!_sourcesByPath.TryGetValue(NormalizeKey(checkpoint.FilePath), out var source)
            || !source.IsPresent
            || source.CanonicalStatus != CanonicalStatus.Canonical
            || !string.Equals(source.RolloutId, checkpoint.RolloutId, StringComparison.Ordinal)
            || source.ByteOffset != checkpoint.StableCompleteOffset
            || !string.Equals(source.PrefixHash, checkpoint.BoundaryHash, StringComparison.Ordinal)
            || GetCanonical(checkpoint.RolloutId) is not { } canonical
            || !PathsEqual(canonical, checkpoint.FilePath))
            return CheckpointRehydrateResult.Miss("Checkpoint no longer matches the canonical source ledger row.");
        if (!IsResolvedObservedRollout(checkpoint.FilePath))
            return CheckpointRehydrateResult.Miss("Checkpoint source is outside the resolved observation boundary.");

        var parserStateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkpoint.ParserStateJson)))
            .ToLowerInvariant();
        if (!string.Equals(parserStateHash, checkpoint.ParserStateHash, StringComparison.Ordinal))
            return CheckpointRehydrateResult.Miss("Checkpoint parser state hash changed.");
        if (!RolloutParserStateCodec.TryDeserialize(checkpoint.ParserStateJson, out var state, out var stateError)
            || state is null)
            return CheckpointRehydrateResult.Miss($"Checkpoint parser state is invalid: {stateError}");
        if (!state.HasMetadata || !string.Equals(state.Metadata.RolloutId, checkpoint.RolloutId, StringComparison.Ordinal))
            return CheckpointRehydrateResult.Miss("Checkpoint parser metadata does not match the rollout.");
        if (state.ForkReplay.Status == ForkReplayStatus.Unproven)
            return CheckpointRehydrateResult.Miss("Checkpoint fork replay state is unproven.");
        var eventCursor = RequireStore().GetRolloutEventCursor(checkpoint.RolloutId);
        if (eventCursor.EventCount != state.NextTokenEventOrdinal
            || eventCursor.NextTokenEventOrdinal != state.NextTokenEventOrdinal)
            return CheckpointRehydrateResult.Miss("Checkpoint parser ordinal does not match the ledger event cursor.");
        if (state.NextTokenEventOrdinal > 0 && state.PreviousSnapshot is null)
            return CheckpointRehydrateResult.Miss("Checkpoint parser cumulative token snapshot is missing.");

        var before = GetFileStat(checkpoint.FilePath);
        if (before.Size < checkpoint.ObservedSizeBytes
            || before.Size < checkpoint.StableCompleteOffset
            || before.Size == checkpoint.ObservedSizeBytes
            && before.ModifiedAtEpochMs != checkpoint.ObservedModifiedAtEpochMs)
            return CheckpointRehydrateResult.Miss("Checkpoint source was truncated or rewritten.");
        var boundaryLength = checked((int)Math.Min(BoundaryWindowBytes, checkpoint.StableCompleteOffset));
        var boundaryBytes = new byte[boundaryLength];
        await using var stream = OpenReadOnlyShared(checkpoint.FilePath);
        if (stream.Length != before.Size)
            return CheckpointRehydrateResult.Miss("Checkpoint source changed before validation.");
        var identity = _sourceIdentityReader.Read(stream, checkpoint.FilePath, before.Size, before.ModifiedAtEpochMs);
        if (identity != checkpoint.SourceIdentity)
            return CheckpointRehydrateResult.Miss("Checkpoint source file identity changed.");
        stream.Position = checkpoint.StableCompleteOffset - boundaryLength;
        await ReadStreamCooperativelyAsync(stream, boundaryBytes, null, cancellationToken).ConfigureAwait(false);
        long validationBytes = boundaryLength;
        if (checkpoint.StableCompleteOffset > 0 && boundaryBytes[^1] != (byte)'\n')
            return new CheckpointRehydrateResult(null, "Checkpoint stable offset is not newline-terminated.", validationBytes);
        var reverse = await ReconcileLatestTokenAsync(
            stream,
            checkpoint.StableCompleteOffset,
            checkpoint.RolloutId,
            state,
            RequireStore().GetRolloutLedgerTail(checkpoint.RolloutId),
            cancellationToken).ConfigureAwait(false);
        validationBytes = checked(validationBytes + reverse.BytesRead);
        if (!reverse.Succeeded)
            return new CheckpointRehydrateResult(null, reverse.Reason, validationBytes);
        var after = GetFileStat(checkpoint.FilePath);
        if (before != after || stream.Length != before.Size)
            return new CheckpointRehydrateResult(null, "Checkpoint source changed during validation.", validationBytes);
        var boundaryHash = Convert.ToHexString(SHA256.HashData(boundaryBytes)).ToLowerInvariant();
        if (!string.Equals(boundaryHash, checkpoint.BoundaryHash, StringComparison.Ordinal))
            return new CheckpointRehydrateResult(null, "Checkpoint boundary hash changed.", validationBytes);

        return new CheckpointRehydrateResult(
            new SourceRuntime(
                checkpoint.RolloutId,
                checkpoint.StableCompleteOffset,
                checkpoint.BoundaryHash,
                state,
                checkpoint.SafeOpaqueOversizedRecords,
                checkpoint.SafeNullPaddingRecords,
                checkpoint.SourceIdentity,
                checkpoint.ObservedModifiedAtEpochMs),
            null,
            validationBytes);
    }

    private async Task<ReverseTokenReconciliationResult> ReconcileLatestTokenAsync(
        FileStream stream,
        long stableCompleteOffset,
        string fallbackRolloutId,
        RolloutParserState state,
        RolloutLedgerTail? ledgerTail,
        CancellationToken cancellationToken)
    {
        if (state.NextTokenEventOrdinal == 0)
        {
            return state.PreviousSnapshot is null && ledgerTail is null
                ? new ReverseTokenReconciliationResult(true, null, 0)
                : ReverseTokenReconciliationResult.Failure("Empty parser state does not match the ledger tail.");
        }
        else if (ledgerTail is null || ledgerTail.TokenEventOrdinal != state.NextTokenEventOrdinal - 1)
        {
            return ReverseTokenReconciliationResult.Failure("Checkpoint ordinal does not match the latest ledger event.");
        }

        long bytesRead = 0;
        var lineEnd = stableCompleteOffset == 0 ? 0 : stableCompleteOffset - 1;
        while (lineEnd > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var boundary = await FindPreviousLineBoundaryAsync(
                stream, lineEnd, bytesRead, cancellationToken).ConfigureAwait(false);
            bytesRead = checked(bytesRead + boundary.BytesRead);
            if (boundary.BudgetExceeded)
                return new ReverseTokenReconciliationResult(false,
                    "Reverse token reconciliation exceeded its bounded read budget.", bytesRead);
            var lineLength = lineEnd - boundary.LineStart;
            if (lineLength > 0 && lineLength <= RolloutParser.CooperativeHardMaximumRecordBytes)
            {
                if (bytesRead + lineLength > ReverseReconciliationMaximumBytes)
                    return new ReverseTokenReconciliationResult(false,
                        "Reverse token reconciliation exceeded its bounded read budget.", bytesRead);
                var line = new byte[checked((int)lineLength)];
                stream.Position = boundary.LineStart;
                await stream.ReadExactlyAsync(line, cancellationToken).ConfigureAwait(false);
                _testHooks?.SourceBytesRead?.Invoke(line.Length);
                bytesRead = checked(bytesRead + line.Length);
                var parsed = ParseReverseTokenLine(line, fallbackRolloutId);
                if (parsed.Disposition == ReverseLineDisposition.Malformed)
                    return new ReverseTokenReconciliationResult(false,
                        "Reverse token reconciliation encountered malformed stable JSONL.", bytesRead);
                if (parsed.Token is { } token)
                {
                    if (state.PreviousSnapshot is null)
                        return new ReverseTokenReconciliationResult(false,
                            "Source contains token usage but checkpoint state does not.", bytesRead);
                    if (!string.Equals(token.CumulativeSnapshot, state.PreviousSnapshot, StringComparison.Ordinal))
                        return new ReverseTokenReconciliationResult(false,
                            "Latest source cumulative token snapshot differs from the checkpoint.", bytesRead);
                    if (ledgerTail is not null && TokenTailMatches(token, ledgerTail))
                        return new ReverseTokenReconciliationResult(true, null, bytesRead);
                }
            }
            lineEnd = boundary.PreviousLineEnd;
        }

        return state.PreviousSnapshot is null && ledgerTail is null
            ? new ReverseTokenReconciliationResult(true, null, bytesRead)
            : new ReverseTokenReconciliationResult(false,
                "Latest checkpoint token snapshot was not found in the bounded source tail.", bytesRead);
    }

    private async Task<ReverseLineBoundaryResult> FindPreviousLineBoundaryAsync(
        FileStream stream,
        long lineEnd,
        long bytesAlreadyRead,
        CancellationToken cancellationToken)
    {
        var searchEnd = lineEnd;
        long callBytesRead = 0;
        while (searchEnd > 0)
        {
            var chunkLength = checked((int)Math.Min(BoundaryWindowBytes, searchEnd));
            if (bytesAlreadyRead + callBytesRead + chunkLength > ReverseReconciliationMaximumBytes)
                return new ReverseLineBoundaryResult(0, 0, 0, true);
            var chunkStart = searchEnd - chunkLength;
            var chunk = new byte[chunkLength];
            stream.Position = chunkStart;
            await stream.ReadExactlyAsync(chunk, cancellationToken).ConfigureAwait(false);
            _testHooks?.SourceBytesRead?.Invoke(chunkLength);
            callBytesRead = checked(callBytesRead + chunkLength);
            var newline = Array.LastIndexOf(chunk, (byte)'\n');
            if (newline >= 0)
            {
                var previousLineEnd = chunkStart + newline;
                return new ReverseLineBoundaryResult(previousLineEnd + 1, previousLineEnd, callBytesRead, false);
            }
            searchEnd = chunkStart;
        }
        return new ReverseLineBoundaryResult(0, 0, callBytesRead, false);
    }

    private static ReverseLineParseResult ParseReverseTokenLine(byte[] line, string fallbackRolloutId)
    {
        if (line.All(value => value is (byte)' ' or (byte)'\t' or (byte)'\r'))
            return new ReverseLineParseResult(ReverseLineDisposition.NonToken, null);
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ReverseLineParseResult(ReverseLineDisposition.Malformed, null);
            if (!root.TryGetProperty("type", out var outerType)
                || outerType.ValueKind != JsonValueKind.String
                || outerType.GetString() != "event_msg"
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("type", out var payloadType)
                || payloadType.ValueKind != JsonValueKind.String
                || payloadType.GetString() != "token_count")
                return new ReverseLineParseResult(ReverseLineDisposition.NonToken, null);
        }
        catch (JsonException)
        {
            return new ReverseLineParseResult(ReverseLineDisposition.Malformed, null);
        }

        var record = new byte[line.Length + 1];
        line.CopyTo(record, 0);
        record[^1] = (byte)'\n';
        var parsed = RolloutParser.Parse(record, fallbackRolloutId);
        if (parsed.Diagnostics.MalformedLines > 0 || parsed.Diagnostics.NonObjectLines > 0
            || parsed.Diagnostics.HasUnsafeOversizedRecords)
            return new ReverseLineParseResult(ReverseLineDisposition.Malformed, null);
        if (parsed.Events.Length == 0)
            return new ReverseLineParseResult(ReverseLineDisposition.NonToken, null);
        var usageEvent = parsed.Events.Single();
        return new ReverseLineParseResult(
            ReverseLineDisposition.Token,
            new ReverseTokenRecord(
                usageEvent.CumulativeSnapshot,
                DateTimeOffset.Parse(usageEvent.TimestampUtc).ToUnixTimeMilliseconds(),
                usageEvent.InputTokens,
                usageEvent.CachedInputTokens,
                usageEvent.OutputTokens,
                usageEvent.ReasoningOutputTokens));
    }

    private static bool TokenTailMatches(ReverseTokenRecord token, RolloutLedgerTail ledger) =>
        token.TimestampEpochMs == ledger.TimestampEpochMs
        && token.InputTokens == ledger.InputTokens
        && token.CachedInputTokens == ledger.CachedInputTokens
        && token.OutputTokens == ledger.OutputTokens
        && token.ReasoningOutputTokens == ledger.ReasoningOutputTokens;

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

        if (!Directory.Exists(_options.CodexHome)) return;
        try
        {
            var watcher = new FileSystemWatcher(_options.CodexHome, "session_index.jsonl")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = false,
            };
            watcher.Created += OnSessionIndexWatcherChanged;
            watcher.Changed += OnSessionIndexWatcherChanged;
            watcher.Deleted += OnSessionIndexWatcherChanged;
            watcher.Renamed += OnSessionIndexWatcherRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception error)
        {
            _watcherHealthy = false;
            AddDiagnostic(_options.CodexHome, "session-index-watcher-start-failed", error.Message, DiagnosticSeverity.Warning);
        }

    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs args) => EnqueueWatcherObservation(args.FullPath);

    private void OnWatcherRenamed(object sender, RenamedEventArgs args)
    {
        EnqueueWatcherObservation(args.OldFullPath);
        EnqueueWatcherObservation(args.FullPath);
    }

    private void OnSessionIndexWatcherChanged(object sender, FileSystemEventArgs args) =>
        EnqueueSessionIndexObservation(args.FullPath);

    private void OnSessionIndexWatcherRenamed(object sender, RenamedEventArgs args)
    {
        EnqueueSessionIndexObservation(args.OldFullPath);
        EnqueueSessionIndexObservation(args.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        Interlocked.Exchange(ref _watcherErrorInbox, args.GetException().Message);
        SignalWatcherInbox();
    }

    internal void EnqueueWatcherObservationForTest(string filePath) => EnqueueWatcherObservation(filePath);

    internal void EnqueueSessionIndexObservationForTest() => EnqueueSessionIndexObservation(_sessionIndexPath);

    internal (int UniquePaths, int WakeSignals) GetWatcherBufferMetricsForTest() =>
        (_watcherInbox.Count, Volatile.Read(ref _watcherWakeQueued));

    internal TimeSpan? GetRetryRemainingDelayForTest(string filePath) =>
        _retryStates.TryGetValue(NormalizeKey(filePath), out var state)
            ? RemainingRetryDelay(state.NextAllowedRetryTimestamp)
            : null;

    private void EnqueueWatcherObservation(string filePath)
    {
        if (Volatile.Read(ref _disposeStarted) != 0 || !IsLexicallyObservedRollout(filePath)) return;
        var fullPath = Path.GetFullPath(filePath);
        _watcherInbox[NormalizeKey(fullPath)] = fullPath;
        SignalWatcherInbox();
    }

    private void EnqueueSessionIndexObservation(string filePath)
    {
        if (Volatile.Read(ref _disposeStarted) != 0 || !IsLexicallyObservedSessionIndex(filePath)) return;
        Interlocked.Exchange(ref _sessionIndexWatcherPending, 1);
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
        if (Interlocked.Exchange(ref _sessionIndexWatcherPending, 0) != 0)
        {
            _sessionIndexRefreshPending = true;
            ScheduleDebounce(_options.WatcherDebounce);
        }
        Interlocked.Exchange(ref _watcherWakeQueued, 0);
        if (!_watcherInbox.IsEmpty || Volatile.Read(ref _watcherErrorInbox) is not null
            || Volatile.Read(ref _sessionIndexWatcherPending) != 0) SignalWatcherInbox();
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
            ClearRecoveryInventoryCache();
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
        var fullBytesBefore = _fullReconcileBytesRead;
        var appendBytesBefore = _appendBytesRead;
        _conflictsAttempted.Clear();
        ClearRecoveryInventoryCache();

        var currentCount = long.TryParse(store.GetCollectorState(InventoryRunCountStateKey), out var count) ? count : 0;
        store.SetCollectorState(InventoryRunCountStateKey, checked(currentCount + 1).ToString(), NowEpochMs());
        if (_testHooks?.BeforeInventoryEnumerationAsync is { } inventoryHook)
            await AwaitWhileServingInteractiveAsync(inventoryHook(cancellationToken), cancellationToken).ConfigureAwait(false);
        var inventory = await ListRolloutsAsync(yields, cancellationToken).ConfigureAwait(false);
        _latestInventoryPaths = inventory.Paths;
        _inventoryPathsEnumerated = inventory.Paths.Count;
        _inventoryPathsProcessed = 0;
        PublishInventoryProgress("Inventory discovered");
        inventorySucceeded &= inventory.Succeeded;
        try
        {
            usageChanged |= await RefreshSessionIndexAsync(yields, cancellationToken).ConfigureAwait(false);
            _sessionIndexConsecutiveFailures = 0;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AddDiagnostic(_sessionIndexPath, "session-index-read-failed", error.Message, DiagnosticSeverity.Warning);
            inventorySucceeded = false;
        }
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
                var canonicalNeedsCheckpoint = known?.CanonicalStatus == CanonicalStatus.Canonical
                    && !_runtimeByPath.ContainsKey(key);
                var changed = known is null || !known.IsPresent || known.SizeBytes != stat.Size
                    || known.ModifiedAtEpochMs != stat.ModifiedAtEpochMs
                    || (known.CanonicalStatus == CanonicalStatus.Conflict && !_conflictsAttempted.Contains(key))
                    || (sourcesWithUnknownModels.Contains(key) && !_unknownModelsAttempted.Contains(key))
                    || canonicalUnavailable || canonicalNeedsCheckpoint;
                if (changed)
                {
                    changedFiles++;
                    var processed = await ProcessFileAsync(
                        filePath, FullParseContext.Inventory, yields, cancellationToken).ConfigureAwait(false);
                    usageChanged |= processed.Changed;
                    inventorySucceeded &= processed.Succeeded;
                    if (processed.Succeeded) ClearSourceFailure(key);
                }
            }
            catch (FileNotFoundException)
            {
                if (known?.IsPresent == true) MarkMissing(known.FilePath);
                ClearSourceFailure(key);
            }
            catch (DirectoryNotFoundException)
            {
                if (known?.IsPresent == true) MarkMissing(known.FilePath);
                ClearSourceFailure(key);
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
        var fullBytesRead = _fullReconcileBytesRead - fullBytesBefore;
        var appendBytesRead = _appendBytesRead - appendBytesBefore;
        store.AddDiagnostic(new CollectorDiagnosticInput(
            _runId,
            null,
            DiagnosticSeverity.Info,
            "checkpoint-inventory-io",
            $"Inventory read {fullBytesRead} full-reconciliation bytes and {appendBytesRead} append bytes.",
            JsonSerializer.Serialize(new { fullBytesRead, appendBytesRead, changedFiles }),
            NowEpochMs()));
        _diagnostics.CooperativeYieldCount += yields.Count;
        var inventoryFullyCovered = inventorySucceeded && _partialSourceKeys.Count == 0;
        if (inventoryFullyCovered)
        {
            _lastSuccessfulInventoryEpochMs = NowEpochMs();
            store.SetCollectorState(LastInventoryStateKey, _lastSuccessfulInventoryEpochMs.Value.ToString(), _lastSuccessfulInventoryEpochMs.Value);
            store.SetCollectorState(InventoryYieldCountStateKey, yields.Count.ToString(), _lastSuccessfulInventoryEpochMs.Value);
        }
        _phase = ResolvePostInventoryPhase(
            inventorySucceeded,
            inventoryFullyCovered,
            store.CountSourceConflicts());
        _message = !inventorySucceeded
            ? $"Inventory incomplete after processing {changedFiles} changed sources"
            : _partialSourceKeys.Count > 0
                ? $"Inventory current with {_partialSourceKeys.Count} partially parsed sources"
            : changedFiles == 0 ? "Inventory is current" : $"Processed {changedFiles} changed sources";
        AdvanceUsageRevision(usageChanged);
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
        if (_sessionIndexRefreshPending)
        {
            _sessionIndexRefreshPending = false;
            try
            {
                usageChanged |= await RefreshSessionIndexAsync(null, cancellationToken).ConfigureAwait(false);
                _sessionIndexConsecutiveFailures = 0;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                AddDiagnostic(_sessionIndexPath, "session-index-read-failed", error.Message, DiagnosticSeverity.Warning);
                ScheduleSessionIndexRetry();
                succeeded = false;
            }
        }
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
                    ClearSourceFailure(key);
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
                if (result.Succeeded) ClearSourceFailure(key);
                else
                {
                    ScheduleRetry(path);
                    succeeded = false;
                }
            }
            if (_pendingPaths.Count > 0) await YieldToMailboxAsync(cancellationToken).ConfigureAwait(false);
        }
        _changedFilesLastSync = processed;
        _phase = ResolvePostWatcherPhase(RequireStore().CountSourceConflicts());
        _message = _phase == CollectorPhase.Retrying
            ? "Watcher is processing the latest change in the background"
            : !succeeded
                ? $"Watcher could not process {processed} paths"
            : _partialSourceKeys.Count > 0
                ? $"Watcher current with {_partialSourceKeys.Count} partially parsed sources"
                : processed == 0 ? "Watcher changes are current" : $"Processed {processed} watcher paths";
        AdvanceUsageRevision(usageChanged);
        if (processed > 0 || usageChanged || !succeeded) PublishStatus();
    }

    private CollectorPhase ResolvePostInventoryPhase(
        bool inventorySucceeded,
        bool inventoryFullyCovered,
        long conflicts)
    {
        if (!inventorySucceeded || !_watcherHealthy || conflicts > 0) return CollectorPhase.Degraded;
        if (HasScheduledRecoverableRetries()) return CollectorPhase.Retrying;
        if (_retryStates.Count > 0) return CollectorPhase.Degraded;
        return inventoryFullyCovered ? CollectorPhase.Watching : CollectorPhase.Partial;
    }

    private CollectorPhase ResolvePostWatcherPhase(long conflicts)
    {
        if (!_watcherHealthy || conflicts > 0) return CollectorPhase.Degraded;
        if (HasScheduledRecoverableRetries()) return CollectorPhase.Retrying;
        if (_retryStates.Count > 0) return CollectorPhase.Degraded;
        return _partialSourceKeys.Count == 0 ? CollectorPhase.Watching : CollectorPhase.Partial;
    }

    private bool HasScheduledRecoverableRetries()
    {
        return _watcherHealthy
            && _retryStates.Count > 0
            && _retryStates.Values.All(retry =>
                (retry.Scheduled || retry.InFlight)
                && retry.ConsecutiveFailures > 0
                && retry.ConsecutiveFailures <= _options.RetryAttempts);
    }

    private void QueueWatcherPath(string filePath)
    {
        if (_stopping || !IsResolvedObservedRollout(filePath)) return;
        var key = NormalizeKey(filePath);
        if (_retryStates.TryGetValue(key, out var retryState))
        {
            ScheduleObservedRetry(Path.GetFullPath(filePath), retryState);
            return;
        }
        _pendingPaths[key] = Path.GetFullPath(filePath);
        ScheduleDebounce(_options.WatcherDebounce);
    }

    private bool AddPendingPath(string filePath)
    {
        if (!IsResolvedObservedRollout(filePath)) return false;
        _pendingPaths[NormalizeKey(filePath)] = Path.GetFullPath(filePath);
        return true;
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
        if (!_retryStates.TryGetValue(key, out var state))
        {
            state = new RetryState();
            _retryStates.Add(key, state);
        }
        state.InFlight = false;
        state.ConsecutiveFailures = checked(state.ConsecutiveFailures + 1);
        var exponent = Math.Min(Math.Min(state.ConsecutiveFailures, _options.RetryAttempts) - 1, 30);
        var multiplier = 1L << exponent;
        var delayMs = Math.Min(_options.RetryBaseDelay.TotalMilliseconds * multiplier, 4_000);
        state.NextAllowedRetryTimestamp = RetryDeadlineAfter(TimeSpan.FromMilliseconds(delayMs));
        if (state.ConsecutiveFailures > _options.RetryAttempts) return;
        ScheduleRetryCommand(Path.GetFullPath(filePath), state, TimeSpan.FromMilliseconds(delayMs));
    }

    private void ScheduleObservedRetry(string filePath, RetryState state)
    {
        if (state.Scheduled) return;
        var remaining = RemainingRetryDelay(state.NextAllowedRetryTimestamp);
        var delay = remaining > _options.WatcherDebounce ? remaining : _options.WatcherDebounce;
        ScheduleRetryCommand(filePath, state, delay);
    }

    private void ScheduleRetryCommand(string filePath, RetryState state, TimeSpan delay)
    {
        state.Generation = checked(state.Generation + 1);
        state.Scheduled = true;
        _ = EnqueueAfterDelayAsync(
            new RetryPathCommand(filePath, state.Generation),
            delay,
            _lifetime.Token);
    }

    private void ScheduleSessionIndexRetry()
    {
        _sessionIndexConsecutiveFailures = checked(_sessionIndexConsecutiveFailures + 1);
        if (_sessionIndexConsecutiveFailures > _options.RetryAttempts) return;
        var exponent = Math.Min(_sessionIndexConsecutiveFailures - 1, 30);
        var multiplier = 1L << exponent;
        var delayMs = Math.Min(_options.RetryBaseDelay.TotalMilliseconds * multiplier, 4_000);
        _sessionIndexRefreshPending = true;
        ScheduleDebounce(TimeSpan.FromMilliseconds(delayMs));
    }

    private long RetryDeadlineAfter(TimeSpan delay)
    {
        var frequency = RetryTimestampFrequency();
        var delta = checked((long)Math.Ceiling(delay.TotalSeconds * frequency));
        return checked(RetryTimestamp() + delta);
    }

    private TimeSpan RemainingRetryDelay(long deadline)
    {
        var remaining = Math.Max(0, deadline - RetryTimestamp());
        return TimeSpan.FromSeconds(remaining / (double)RetryTimestampFrequency());
    }

    private long RetryTimestamp() =>
        _testHooks?.GetMonotonicTimestamp?.Invoke() ?? Stopwatch.GetTimestamp();

    private long RetryTimestampFrequency() =>
        _testHooks?.MonotonicTimestampFrequency is > 0 and var frequency
            ? frequency
            : Stopwatch.Frequency;

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
            var unresolvedConflict = _sourcesByPath.TryGetValue(key, out var processedSource)
                && processedSource.CanonicalStatus == CanonicalStatus.Conflict;
            return new ProcessResult(changed, !unresolvedConflict);
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
        if (runtime.SourceIdentity != snapshot.SourceIdentity
            || snapshot.Stat.Size == runtime.ByteOffset
            && snapshot.Stat.ModifiedAtEpochMs != runtime.ObservedModifiedAtEpochMs)
            return await ProcessFullFileAsync(
                filePath, new FullParseContext(ParseReason.CanonicalPrefixRewrite, runtime.RolloutId), yields, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(snapshot.OldBoundaryHash, runtime.BoundaryHash, StringComparison.Ordinal))
            return await ProcessFullFileAsync(
                filePath, new FullParseContext(ParseReason.CanonicalPrefixRewrite, runtime.RolloutId), yields, cancellationToken).ConfigureAwait(false);
        if (snapshot.AppendedBytes.Length == 0) return false;
        if (_testHooks?.AfterStableAppendSnapshotCapturedAsync is { } hook)
            await hook(filePath, cancellationToken).ConfigureAwait(false);
        var result = await ParseAsync(snapshot.AppendedBytes, runtime.RolloutId, runtime.State, yields, cancellationToken).ConfigureAwait(false);
        var metadata = ApplyThreadPickerMetadata(result.Metadata);
        RejectInternalDamage(filePath, result);
        var resolvedTurns = result.State.TurnModels.Keys.ToHashSet(StringComparer.Ordinal);
        if (runtime.State.UnresolvedTurnIds.Concat(runtime.State.ProvisionalTurnIds).Any(resolvedTurns.Contains))
            return await ProcessFullFileAsync(
                filePath, new FullParseContext(ParseReason.LateModelResolution, runtime.RolloutId), yields, cancellationToken).ConfigureAwait(false);
        AddDiagnostics(filePath, result.Diagnostics);
        if (result.StableByteLength == 0)
        {
            var partialSource = SourceFrom(
                filePath, snapshot.Stat, runtime.ByteOffset, runtime.BoundaryHash,
                CanonicalStatus.Canonical, PrefixStatus.Matches,
                runtime.SafeOpaqueOversizedRecordsSkipped > 0 || runtime.SafeNullPaddingRecordsSkipped > 0
                    ? PartialSourceMessage(runtime.SafeOpaqueOversizedRecordsSkipped, runtime.SafeNullPaddingRecordsSkipped)
                    : null);
            var partialCheckpoint = CreateCheckpoint(
                filePath, snapshot.Stat, snapshot.SourceIdentity, runtime.ByteOffset, runtime.BoundaryHash,
                result.State, runtime.SafeOpaqueOversizedRecordsSkipped, runtime.SafeNullPaddingRecordsSkipped,
                snapshot.Stat.Size - runtime.ByteOffset);
            RequireStore().AppendRolloutSource(new AppendRolloutSourceInput(
                metadata, [], partialSource, NowEpochMs(), partialCheckpoint));
            RememberSource(new SourceFileInput(
                partialSource.FilePath, result.Metadata.RolloutId, partialSource.SizeBytes,
                partialSource.ModifiedAtEpochMs, partialSource.ByteOffset, partialSource.PrefixHash,
                partialSource.PrefixStatus, partialSource.CanonicalStatus, partialSource.IsPresent,
                partialSource.LastScannedAtEpochMs, partialSource.LastError));
            _runtimeByPath[NormalizeKey(filePath)] = runtime with
            {
                State = result.State,
                SourceIdentity = snapshot.SourceIdentity,
                ObservedModifiedAtEpochMs = snapshot.Stat.ModifiedAtEpochMs,
            };
            AddDiagnostic(filePath, "checkpoint-partial-tail",
                $"Deferred {snapshot.Stat.Size - runtime.ByteOffset} trailing bytes until a complete JSONL record is available.",
                DiagnosticSeverity.Info);
            return false;
        }
        var newOffset = runtime.ByteOffset + result.StableByteLength;
        var hash = snapshot.BoundaryHashAt(result.StableByteLength);
        var safeOpaqueSkipped = checked(runtime.SafeOpaqueOversizedRecordsSkipped
            + result.Diagnostics.SafeOpaqueOversizedRecordsSkipped);
        var safeNullPaddingSkipped = checked(runtime.SafeNullPaddingRecordsSkipped
            + result.Diagnostics.SafeNullPaddingRecordsSkipped);
        var isPartial = safeOpaqueSkipped > 0 || safeNullPaddingSkipped > 0;
        var source = SourceFrom(filePath, snapshot.Stat, newOffset, hash, CanonicalStatus.Canonical, PrefixStatus.Matches,
            isPartial ? PartialSourceMessage(safeOpaqueSkipped, safeNullPaddingSkipped) : null);
        var checkpoint = CreateCheckpoint(
            filePath, snapshot.Stat, snapshot.SourceIdentity, newOffset, hash, result.State,
            safeOpaqueSkipped, safeNullPaddingSkipped, snapshot.Stat.Size - newOffset);
        var appended = RequireStore().AppendRolloutSource(new AppendRolloutSourceInput(
            metadata, UsageInputs(result), source, NowEpochMs(), checkpoint));
        RememberSource(new SourceFileInput(
            source.FilePath, result.Metadata.RolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
            source.ByteOffset, source.PrefixHash, source.PrefixStatus, source.CanonicalStatus,
            source.IsPresent, source.LastScannedAtEpochMs, source.LastError));
        _runtimeByPath[NormalizeKey(filePath)] = new SourceRuntime(
            result.Metadata.RolloutId, newOffset, hash, result.State, safeOpaqueSkipped,
            safeNullPaddingSkipped,
            snapshot.SourceIdentity, snapshot.Stat.ModifiedAtEpochMs);
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
        var sourceIdentity = _sourceIdentityReader.Read(stream, filePath, before.Size, before.ModifiedAtEpochMs);
        stream.Position = start;
        await ReadStreamCooperativelyAsync(stream, bytes, null, cancellationToken).ConfigureAwait(false);
        _appendBytesRead = checked(_appendBytesRead + bytes.Length);
        var after = GetFileStat(filePath);
        if (before != after || stream.Length != before.Size)
            throw new IOException("Source changed while reading appended bytes.");
        var prefixLength = checked((int)(byteOffset - start));
        var oldBoundaryHash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, prefixLength))).ToLowerInvariant();
        var appended = bytes.AsSpan(prefixLength).ToArray();
        return new AppendSnapshot(before, sourceIdentity, bytes, prefixLength, appended, oldBoundaryHash);
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
                _recoverySnapshotsByPath[NormalizeKey(filePath)] = new RecoverySnapshotCacheEntry(null);
                RecordCanonicalConflict(filePath, known!, confirmedUnsafe, "canonical-source-malformed", confirmedUnsafe.Message);
                var recovery = await TryRecoverConflictedCanonicalAsync(
                    known!.RolloutId!, filePath, null, yields, cancellationToken).ConfigureAwait(false);
                return recovery.UsageChanged;
            }
            throw new InvalidDataException(confirmedUnsafe.Message);
        }

        var parsed = (ParsedSnapshot)snapshot;
        var result = parsed.Result;
        var metadata = ApplyThreadPickerMetadata(result.Metadata);
        var partialSourceMessage = result.Diagnostics.SafeOpaqueOversizedRecordsSkipped > 0
                || result.Diagnostics.SafeNullPaddingRecordsSkipped > 0
            ? PartialSourceMessage(
                result.Diagnostics.SafeOpaqueOversizedRecordsSkipped,
                result.Diagnostics.SafeNullPaddingRecordsSkipped)
            : null;
        AddDiagnostics(filePath, result.Diagnostics);
        if (known?.RolloutId is { } legacyRolloutId
            && legacyRolloutId != result.Metadata.RolloutId
            && isKnownCanonical
            && known.CanonicalStatus == CanonicalStatus.Canonical
            && context.Reason == ParseReason.ParserRevision
            && context.ExpectedRolloutId == legacyRolloutId
            && IsStrictLegacyIdentityChange(filePath, legacyRolloutId, result.Metadata.RolloutId)
            && TryRekeyLegacyCanonical(filePath, legacyRolloutId, parsed, partialSourceMessage))
        {
            return true;
        }
        if (known?.RolloutId is { } previousRollout && previousRollout != result.Metadata.RolloutId && isKnownCanonical)
        {
            var confirmed = await ConfirmStableSnapshotAsync(filePath, parsed, yields, cancellationToken).ConfigureAwait(false);
            if (confirmed is not ParsedSnapshot confirmedParsed || confirmedParsed.Result.Metadata.RolloutId != result.Metadata.RolloutId)
                throw new IOException("Canonical source identity changed between recovery snapshots.");
            _recoverySnapshotsByPath[NormalizeKey(filePath)] = new RecoverySnapshotCacheEntry(confirmedParsed);
            var message = $"Canonical source rollout changed from {previousRollout} to {result.Metadata.RolloutId}.";
            RecordCanonicalConflict(filePath, known, confirmedParsed, "canonical-source-rollout-changed", message);
            if (context.Reason == ParseReason.ParserRevision
                && context.ExpectedRolloutId == previousRollout
                && known.CanonicalStatus == CanonicalStatus.Conflict
                && IsStrictLegacyIdentityChange(filePath, previousRollout, result.Metadata.RolloutId)
                && TryRekeyLegacyCanonical(filePath, previousRollout, confirmedParsed, partialSourceMessage))
                return true;
            var recovery = await TryRecoverConflictedCanonicalAsync(
                previousRollout, filePath, null, yields, cancellationToken).ConfigureAwait(false);
            if (!recovery.Recovered) return false;
            var changedIdentity = await ProcessFullFileAsync(
                filePath, FullParseContext.Inventory, yields, cancellationToken).ConfigureAwait(false);
            return recovery.UsageChanged || changedIdentity;
        }

        var observedAt = NowEpochMs();
        var candidateIdentities = result.Events.Select(EventIdentity).ToArray();
        var existingIdentities = store.GetRolloutEventIdentities(result.Metadata.RolloutId);
        var relation = SignatureRelation(existingIdentities, candidateIdentities);
        var candidateSemanticSignatures = result.Events.Select(EventSemanticSignature).ToArray();
        var existingSemanticSignatures = store.GetRolloutSemanticSignatures(result.Metadata.RolloutId);
        var semanticRelation = SignatureRelation(existingSemanticSignatures, candidateSemanticSignatures);
        var storedMetadata = store.GetRolloutMetadata(result.Metadata.RolloutId);
        var threadPickerChanged = !metadata.IsRealtimeVoice
            && !SameThreadPickerMetadata(storedMetadata, metadata);
        var usageChanged = semanticRelation != SignatureRelationship.Equal
            || (existingSemanticSignatures.Count > 0 || candidateSemanticSignatures.Length > 0)
            && !SameDashboardUsageMetadata(storedMetadata, metadata)
            || threadPickerChanged;
        if (context.Reason == ParseReason.ParserRevision)
        {
            if (result.Metadata.RolloutId != context.ExpectedRolloutId)
                throw new InvalidDataException($"Canonical source rollout changed from {context.ExpectedRolloutId} to {result.Metadata.RolloutId}.");
            var source = SourceFrom(filePath, parsed.Stat, result.StableByteLength, parsed.BoundaryHash,
                CanonicalStatus.Canonical, PrefixStatus.Matches, partialSourceMessage);
            var parserRevisionCheckpoint = CreateCheckpoint(
                filePath, parsed.Stat, parsed.SourceIdentity, result.StableByteLength, parsed.BoundaryHash,
                result.State, result.Diagnostics.SafeOpaqueOversizedRecordsSkipped,
                result.Diagnostics.SafeNullPaddingRecordsSkipped,
                parsed.Stat.Size - result.StableByteLength);
            store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
                metadata,
                UsageInputs(result),
                new CanonicalSourceInput(
                    source.FilePath, source.SizeBytes, source.ModifiedAtEpochMs, source.ByteOffset,
                    source.PrefixHash, source.PrefixStatus, source.LastScannedAtEpochMs, source.LastError),
                observedAt,
                null,
                parserRevisionCheckpoint));
            RememberPromotion(result.Metadata.RolloutId, source);
            RememberRuntime(filePath, result, parsed.BoundaryHash, parsed.SourceIdentity, parsed.Stat.ModifiedAtEpochMs);
            return usageChanged;
        }

        var canonicalPath = GetCanonical(result.Metadata.RolloutId);
        var isCurrentCanonical = canonicalPath is not null && PathsEqual(canonicalPath, filePath);
        var metadataMatches = SameMetadata(storedMetadata, metadata);
        var canonicalIsConflicted = canonicalPath is not null
            && _sourcesByPath.TryGetValue(NormalizeKey(canonicalPath), out var conflictedCanonicalSource)
            && conflictedCanonicalSource.CanonicalStatus == CanonicalStatus.Conflict;
        if (!isCurrentCanonical && canonicalIsConflicted)
        {
            var recovery = await TryRecoverConflictedCanonicalAsync(
                result.Metadata.RolloutId, canonicalPath!, new RecoverySeed(filePath, parsed), yields, cancellationToken)
                .ConfigureAwait(false);
            if (recovery.Recovered) return recovery.UsageChanged;
        }
        var canonicalRewrite = isCurrentCanonical && (context.Reason == ParseReason.CanonicalPrefixRewrite
            || relation is SignatureRelationship.Shorter or SignatureRelationship.Diverged
            || !metadataMatches
            || semanticRelation is SignatureRelationship.Shorter or SignatureRelationship.Diverged);
        if (canonicalRewrite)
        {
            return await RecoverCanonicalRewriteAsync(
                filePath, result.Metadata.RolloutId, parsed, yields, cancellationToken).ConfigureAwait(false);
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
                CanonicalStatus.Candidate, PrefixStatus.Matches, partialSourceMessage), result.Metadata.RolloutId);
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
            shouldPromote ? CanonicalStatus.Canonical : CanonicalStatus.Candidate, PrefixStatus.Matches,
            partialSourceMessage);
        if (!shouldPromote)
        {
            UpsertSource(candidateSource, result.Metadata.RolloutId);
            _runtimeByPath.Remove(NormalizeKey(filePath));
            return false;
        }
        var checkpoint = CreateCheckpoint(
            filePath, parsed.Stat, parsed.SourceIdentity, result.StableByteLength, parsed.BoundaryHash,
            result.State, result.Diagnostics.SafeOpaqueOversizedRecordsSkipped,
            result.Diagnostics.SafeNullPaddingRecordsSkipped,
            parsed.Stat.Size - result.StableByteLength);
        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            metadata,
            UsageInputs(result),
            new CanonicalSourceInput(
                candidateSource.FilePath, candidateSource.SizeBytes, candidateSource.ModifiedAtEpochMs,
                candidateSource.ByteOffset, candidateSource.PrefixHash, candidateSource.PrefixStatus,
                candidateSource.LastScannedAtEpochMs, candidateSource.LastError),
            observedAt,
            null,
            checkpoint));
        RememberPromotion(result.Metadata.RolloutId, candidateSource);
        RememberRuntime(filePath, result, parsed.BoundaryHash, parsed.SourceIdentity, parsed.Stat.ModifiedAtEpochMs);
        return usageChanged;
    }

    private bool TryRekeyLegacyCanonical(
        string filePath,
        string legacyRolloutId,
        ParsedSnapshot parsed,
        string? partialSourceMessage)
    {
        var result = parsed.Result;
        var metadata = ApplyThreadPickerMetadata(result.Metadata);
        var source = SourceFrom(filePath, parsed.Stat, result.StableByteLength, parsed.BoundaryHash,
            CanonicalStatus.Canonical, PrefixStatus.Matches, partialSourceMessage);
        var checkpoint = CreateCheckpoint(
            filePath, parsed.Stat, parsed.SourceIdentity, result.StableByteLength, parsed.BoundaryHash,
            result.State, result.Diagnostics.SafeOpaqueOversizedRecordsSkipped,
            result.Diagnostics.SafeNullPaddingRecordsSkipped,
            parsed.Stat.Size - result.StableByteLength);
        try
        {
            RequireStore().RekeyLegacyCanonicalRollout(new RekeyLegacyCanonicalRolloutInput(
                legacyRolloutId,
                metadata,
                UsageInputs(result),
                new CanonicalSourceInput(
                    source.FilePath, source.SizeBytes, source.ModifiedAtEpochMs, source.ByteOffset,
                    source.PrefixHash, source.PrefixStatus, source.LastScannedAtEpochMs, source.LastError),
                NowEpochMs(),
                checkpoint));
            _canonicalByRollout.Remove(legacyRolloutId);
            RememberPromotion(result.Metadata.RolloutId, source);
            RememberRuntime(filePath, result, parsed.BoundaryHash, parsed.SourceIdentity, parsed.Stat.ModifiedAtEpochMs);
            return true;
        }
        catch (InvalidOperationException error)
        {
            AddDiagnostic(filePath, "legacy-rollout-rekey-rejected", error.Message, DiagnosticSeverity.Warning);
            return false;
        }
    }

    private static bool IsStrictLegacyIdentityChange(string filePath, string legacyRolloutId, string actualRolloutId) =>
        string.Equals(
            legacyRolloutId,
            RolloutFileIdentity.LegacyFallbackRolloutId(filePath),
            StringComparison.Ordinal)
        && RolloutFileIdentity.TryGetTrailingUuidV7(filePath, out var filenameRolloutId)
        && string.Equals(filenameRolloutId, actualRolloutId, StringComparison.Ordinal);

    private async Task<bool> RecoverCanonicalRewriteAsync(
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
        var metadata = ApplyThreadPickerMetadata(parsed.Result.Metadata);
        var observedAt = NowEpochMs();
        var store = RequireStore();
        var existingSemanticSignatures = store.GetRolloutSemanticSignatures(rolloutId);
        var candidateSemanticSignatures = parsed.Result.Events.Select(EventSemanticSignature).ToArray();
        var storedMetadata = store.GetRolloutMetadata(rolloutId);
        var threadPickerChanged = !metadata.IsRealtimeVoice
            && !SameThreadPickerMetadata(storedMetadata, metadata);
        var usageChanged = SignatureRelation(existingSemanticSignatures, candidateSemanticSignatures)
                != SignatureRelationship.Equal
            || (existingSemanticSignatures.Count > 0 || candidateSemanticSignatures.Length > 0)
            && !SameDashboardUsageMetadata(storedMetadata, metadata)
            || threadPickerChanged;
        var checkpoint = CreateCheckpoint(
            filePath, parsed.Stat, parsed.SourceIdentity, parsed.Result.StableByteLength, parsed.BoundaryHash,
            parsed.Result.State, parsed.Result.Diagnostics.SafeOpaqueOversizedRecordsSkipped,
            parsed.Result.Diagnostics.SafeNullPaddingRecordsSkipped,
            parsed.Stat.Size - parsed.Result.StableByteLength);
        store.RecoverDivergedCanonicalSource(new RecoverDivergedCanonicalSourceInput(
            metadata,
            UsageInputs(parsed.Result),
            new RecoverableCanonicalSourceInput(filePath, parsed.Stat.Size, parsed.Stat.ModifiedAtEpochMs,
                parsed.Result.StableByteLength, parsed.BoundaryHash, observedAt),
            observedAt,
            checkpoint));
        var source = SourceFrom(filePath, parsed.Stat, parsed.Result.StableByteLength, parsed.BoundaryHash,
            CanonicalStatus.Canonical, PrefixStatus.Matches,
            parsed.Result.Diagnostics.SafeOpaqueOversizedRecordsSkipped > 0
                    || parsed.Result.Diagnostics.SafeNullPaddingRecordsSkipped > 0
                ? PartialSourceMessage(
                    parsed.Result.Diagnostics.SafeOpaqueOversizedRecordsSkipped,
                    parsed.Result.Diagnostics.SafeNullPaddingRecordsSkipped)
                : null);
        RequireStore().UpsertSourceFile(new SourceFileInput(
            source.FilePath, rolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
            source.ByteOffset, source.PrefixHash, source.PrefixStatus, source.CanonicalStatus,
            source.IsPresent, source.LastScannedAtEpochMs, source.LastError));
        RememberSource(new SourceFileInput(
            source.FilePath, rolloutId, source.SizeBytes, source.ModifiedAtEpochMs,
            source.ByteOffset, source.PrefixHash, source.PrefixStatus, source.CanonicalStatus,
            source.IsPresent, source.LastScannedAtEpochMs, source.LastError));
        RememberRuntime(filePath, parsed.Result, parsed.BoundaryHash, parsed.SourceIdentity, parsed.Stat.ModifiedAtEpochMs);
        return usageChanged;
    }

    private async Task<ConflictRecoveryResult> TryRecoverConflictedCanonicalAsync(
        string rolloutId,
        string conflictPath,
        RecoverySeed? currentCandidate,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        var store = RequireStore();
        var metadata = store.GetRolloutMetadata(rolloutId);
        if (metadata is null) return new ConflictRecoveryResult(false, false);
        var existingSignatures = store.GetRolloutSemanticSignatures(rolloutId);
        var candidatePaths = new Dictionary<string, RecoverySeed?>(StringComparer.OrdinalIgnoreCase);
        if (_sourceKeysByRollout.TryGetValue(rolloutId, out var sourceKeys))
        {
            foreach (var sourceKey in sourceKeys)
            {
                var storedSource = _sourcesByPath[sourceKey];
                if (storedSource.IsPresent && !PathsEqual(storedSource.FilePath, conflictPath))
                    candidatePaths[storedSource.FilePath] = null;
            }
        }
        if (currentCandidate is not null && !PathsEqual(currentCandidate.FilePath, conflictPath))
            candidatePaths[currentCandidate.FilePath] = currentCandidate;

        var candidates = new List<RecoveryCandidate>();
        foreach (var (candidatePath, seed) in candidatePaths)
        {
            var parsed = await GetConfirmedRecoverySnapshotAsync(
                candidatePath, seed, yields, cancellationToken).ConfigureAwait(false);
            AddRecoveryCandidate(candidates, candidatePath, parsed, metadata, existingSignatures);
        }

        if (_inventoryActive)
        {
            await EnsureFullRecoveryIndexAsync(yields, cancellationToken).ConfigureAwait(false);
            if (_fullRecoveryIndexByRollout.TryGetValue(rolloutId, out var indexedCandidates))
            {
                foreach (var seed in indexedCandidates)
                {
                    if (!PathsEqual(seed.FilePath, conflictPath) && !candidatePaths.ContainsKey(seed.FilePath))
                        AddRecoveryCandidate(candidates, seed.FilePath, seed.Snapshot, metadata, existingSignatures);
                }
            }
        }

        var freshCandidates = new List<RecoveryCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var fresh = await RevalidateRecoveryCandidateAsync(
                candidate.FilePath,
                metadata,
                existingSignatures,
                yields,
                cancellationToken).ConfigureAwait(false);
            if (fresh is not null) freshCandidates.Add(fresh);
        }
        var selected = freshCandidates
            .OrderByDescending(candidate => candidate.Relation == SignatureRelationship.Extension)
            .ThenByDescending(candidate => candidate.Snapshot.Result.StableByteLength)
            .ThenByDescending(candidate => candidate.Snapshot.Stat.ModifiedAtEpochMs)
            .ThenBy(candidate => candidate.FilePath, PathComparer())
            .FirstOrDefault();
        if (selected is null) return new ConflictRecoveryResult(false, false);

        var observedAt = NowEpochMs();
        var selectedMetadata = ApplyThreadPickerMetadata(selected.Snapshot.Result.Metadata);
        var source = SourceFrom(
            selected.FilePath,
            selected.Snapshot.Stat,
            selected.Snapshot.Result.StableByteLength,
            selected.Snapshot.BoundaryHash,
            CanonicalStatus.Canonical,
            PrefixStatus.Matches,
            selected.Snapshot.Result.Diagnostics.SafeOpaqueOversizedRecordsSkipped > 0
                    || selected.Snapshot.Result.Diagnostics.SafeNullPaddingRecordsSkipped > 0
                ? PartialSourceMessage(
                    selected.Snapshot.Result.Diagnostics.SafeOpaqueOversizedRecordsSkipped,
                    selected.Snapshot.Result.Diagnostics.SafeNullPaddingRecordsSkipped)
                : null);
        var checkpoint = CreateCheckpoint(
            selected.FilePath, selected.Snapshot.Stat, selected.Snapshot.SourceIdentity,
            selected.Snapshot.Result.StableByteLength, selected.Snapshot.BoundaryHash,
            selected.Snapshot.Result.State,
            selected.Snapshot.Result.Diagnostics.SafeOpaqueOversizedRecordsSkipped,
            selected.Snapshot.Result.Diagnostics.SafeNullPaddingRecordsSkipped,
            selected.Snapshot.Stat.Size - selected.Snapshot.Result.StableByteLength);
        store.ReplaceCanonicalRollout(new ReplaceCanonicalRolloutInput(
            selectedMetadata,
            UsageInputs(selected.Snapshot.Result),
            new CanonicalSourceInput(
                source.FilePath,
                source.SizeBytes,
                source.ModifiedAtEpochMs,
                source.ByteOffset,
                source.PrefixHash,
                source.PrefixStatus,
                source.LastScannedAtEpochMs,
                source.LastError),
            observedAt,
            conflictPath,
            checkpoint));
        RememberPromotion(rolloutId, source);
        var conflictKey = NormalizeKey(conflictPath);
        if (_sourcesByPath.TryGetValue(conflictKey, out var conflictSource)
            && string.Equals(conflictSource.RolloutId, rolloutId, StringComparison.Ordinal))
        {
            _sourcesByPath[conflictKey] = conflictSource with
            {
                CanonicalStatus = CanonicalStatus.Candidate,
                LastError = null,
            };
            _partialSourceKeys.Remove(conflictKey);
        }
        RememberRuntime(selected.FilePath, selected.Snapshot.Result, selected.Snapshot.BoundaryHash,
            selected.Snapshot.SourceIdentity, selected.Snapshot.Stat.ModifiedAtEpochMs);
        ClearSourceFailure(conflictKey);
        return new ConflictRecoveryResult(
            true,
            selected.Relation == SignatureRelationship.Extension);
    }

    private async Task EnsureFullRecoveryIndexAsync(
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        if (_fullRecoveryIndexBuilt) return;
        foreach (var filePath in _latestInventoryPaths)
        {
            var key = NormalizeKey(filePath);
            if (_sourcesByPath.TryGetValue(key, out var knownSource) && knownSource.RolloutId is not null) continue;
            var parsed = await GetConfirmedRecoverySnapshotAsync(
                filePath, null, yields, cancellationToken).ConfigureAwait(false);
            if (parsed is null) continue;
            if (!_fullRecoveryIndexByRollout.TryGetValue(parsed.Result.Metadata.RolloutId, out var candidates))
            {
                candidates = [];
                _fullRecoveryIndexByRollout.Add(parsed.Result.Metadata.RolloutId, candidates);
            }
            candidates.Add(new RecoverySeed(filePath, parsed));
        }
        _fullRecoveryIndexBuilt = true;
        if (_testHooks?.AfterFullRecoveryIndexBuiltAsync is { } hook)
            await hook(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecoveryCandidate?> RevalidateRecoveryCandidateAsync(
        string filePath,
        RolloutMetadata metadata,
        IReadOnlyList<string> existingSignatures,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        _recoverySnapshotsByPath.Remove(NormalizeKey(filePath));
        var parsed = await GetConfirmedRecoverySnapshotAsync(
            filePath, null, yields, cancellationToken).ConfigureAwait(false);
        if (parsed is null || !SameMetadata(metadata, parsed.Result.Metadata)) return null;
        var relation = SignatureRelation(
            existingSignatures,
            parsed.Result.Events.Select(EventSemanticSignature).ToArray());
        return relation is SignatureRelationship.Equal or SignatureRelationship.Extension
            ? new RecoveryCandidate(filePath, parsed, relation)
            : null;
    }

    private async Task<ParsedSnapshot?> GetConfirmedRecoverySnapshotAsync(
        string filePath,
        RecoverySeed? seed,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        var key = NormalizeKey(filePath);
        if (_recoverySnapshotsByPath.TryGetValue(key, out var cached))
        {
            if (seed is null || (cached.Snapshot is not null
                && string.Equals(cached.Snapshot.ContentHash, seed.Snapshot.ContentHash, StringComparison.Ordinal)))
                return cached.Snapshot;
            _recoverySnapshotsByPath.Remove(key);
        }

        try
        {
            var first = seed?.Snapshot
                ?? await ReadStableFullSnapshotAsync(filePath, yields, cancellationToken).ConfigureAwait(false);
            var confirmed = await ConfirmStableSnapshotAsync(filePath, first, yields, cancellationToken).ConfigureAwait(false);
            var parsed = confirmed as ParsedSnapshot;
            _recoverySnapshotsByPath[key] = new RecoverySnapshotCacheEntry(parsed);
            _testHooks?.AfterConfirmedRecoverySnapshot?.Invoke(filePath);
            return parsed;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _recoverySnapshotsByPath[key] = new RecoverySnapshotCacheEntry(null);
            AddDiagnostic(filePath, "conflict-recovery-candidate-rejected", error.Message, DiagnosticSeverity.Warning);
            return null;
        }
    }

    private static void AddRecoveryCandidate(
        ICollection<RecoveryCandidate> candidates,
        string filePath,
        ParsedSnapshot? parsed,
        RolloutMetadata metadata,
        IReadOnlyList<string> existingSignatures)
    {
        if (parsed is null || !SameMetadata(metadata, parsed.Result.Metadata)) return;
        var relation = SignatureRelation(
            existingSignatures,
            parsed.Result.Events.Select(EventSemanticSignature).ToArray());
        if (relation is SignatureRelationship.Equal or SignatureRelationship.Extension)
            candidates.Add(new RecoveryCandidate(filePath, parsed, relation));
    }

    private void ClearRecoveryInventoryCache()
    {
        _recoverySnapshotsByPath.Clear();
        _fullRecoveryIndexByRollout.Clear();
        _fullRecoveryIndexBuilt = false;
    }

    private async Task<FullSnapshot> ReadStableFullSnapshotAsync(
        string filePath,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        _testHooks?.FullSnapshotRead?.Invoke(filePath);
        var before = GetFileStat(filePath);
        if (before.Size > int.MaxValue) throw new IOException("Rollout source is too large to parse in memory.");
        var buffer = new byte[checked((int)before.Size)];
        await using var stream = OpenReadOnlyShared(filePath);
        if (stream.Length != before.Size) throw new IOException("Source changed before reading a full snapshot.");
        var sourceIdentity = _sourceIdentityReader.Read(stream, filePath, before.Size, before.ModifiedAtEpochMs);
        await ReadStreamCooperativelyAsync(stream, buffer, yields, cancellationToken).ConfigureAwait(false);
        _fullReconcileBytesRead = checked(_fullReconcileBytesRead + buffer.Length);
        var after = GetFileStat(filePath);
        if (before != after || stream.Length != before.Size) throw new IOException("Source changed while reading a full snapshot.");
        var result = await ParseAsync(buffer, FallbackRolloutId(filePath), null, yields, cancellationToken).ConfigureAwait(false);
        var contentHash = await CooperativeSha256Async(buffer, yields, cancellationToken).ConfigureAwait(false);
        var unsafeContent = result.Diagnostics.MalformedLines > 0 || result.Diagnostics.NonObjectLines > 0
            || result.Diagnostics.HasUnsafeOversizedRecords;
        var hashLength = unsafeContent
            ? buffer.Length : result.StableByteLength;
        var boundaryHash = ComputeBoundaryHash(buffer, hashLength);
        return unsafeContent
            ? new UnsafeSnapshot(after, sourceIdentity, contentHash, boundaryHash, $"Stable JSONL content is malformed: {filePath}")
            : new ParsedSnapshot(after, sourceIdentity, contentHash, boundaryHash, result);
    }

    private async Task<FullSnapshot> ConfirmStableSnapshotAsync(
        string filePath,
        FullSnapshot first,
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        if (_testHooks?.BeforeConfirmationSnapshotAsync is { } hook)
            await hook(filePath, cancellationToken).ConfigureAwait(false);
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

    private async Task<bool> RefreshSessionIndexAsync(
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        SessionIndexParseResult parsed;
        var isMissing = false;
        try
        {
            _ = File.GetAttributes(_sessionIndexPath);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            isMissing = true;
        }

        if (isMissing)
        {
            parsed = new SessionIndexParseResult(new Dictionary<string, string>(StringComparer.Ordinal), 0);
        }
        else
        {
            if (!IsResolvedObservedSessionIndex())
                throw new IOException("Session index resolves through a reparse point outside collector scope.");
            var snapshot = await ReadStableSessionIndexSnapshotAsync(yields, cancellationToken).ConfigureAwait(false);
            parsed = SessionIndexParser.Parse(snapshot);
        }

        if (parsed.IsAuthoritative)
        {
            _sessionIndexTitles.Clear();
            foreach (var (conversationId, title) in parsed.ThreadTitles)
                _sessionIndexTitles.Add(conversationId, title);
        }
        else
        {
            foreach (var (conversationId, title) in parsed.ThreadTitles)
                _sessionIndexTitles[conversationId] = title;
            AddDiagnostic(
                _sessionIndexPath,
                "session-index-invalid-records",
                $"Ignored {parsed.InvalidRecords} invalid session index records; existing titles were retained for unresolved entries.",
                DiagnosticSeverity.Warning);
        }

        return RequireStore().SynchronizeMainThreadTitles(
            parsed.ThreadTitles,
            parsed.IsAuthoritative,
            NowEpochMs());
    }

    private async Task<byte[]> ReadStableSessionIndexSnapshotAsync(
        InventoryYieldTracker? yields,
        CancellationToken cancellationToken)
    {
        var before = GetFileStat(_sessionIndexPath);
        if (before.Size > 64L * 1024 * 1024)
            throw new IOException("Session index exceeds the 64 MiB bounded snapshot limit.");
        var buffer = new byte[checked((int)before.Size)];
        await using var stream = OpenReadOnlyShared(_sessionIndexPath);
        if (stream.Length != before.Size) throw new IOException("Session index changed before reading a stable snapshot.");
        await ReadStreamCooperativelyAsync(stream, buffer, yields, cancellationToken).ConfigureAwait(false);
        var after = GetFileStat(_sessionIndexPath);
        if (before != after || stream.Length != before.Size)
            throw new IOException("Session index changed while reading a stable snapshot.");
        return buffer;
    }

    private RolloutMetadata ApplyThreadPickerMetadata(RolloutMetadata metadata)
    {
        if (metadata.ThreadType != ThreadType.Main || metadata.IsRealtimeVoice)
            return metadata;
        var title = _sessionIndexTitles.TryGetValue(metadata.ConversationId, out var indexedTitle)
            ? indexedTitle
            : string.Empty;
        return metadata with { ThreadTitle = title };
    }

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
            _testHooks?.SourceBytesRead?.Invoke(length);
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
        if (known?.RolloutId is { } rolloutId && rolloutId != parsed.Result.Metadata.RolloutId
            && !(GetCanonical(rolloutId) is { } designatedCanonical
                && PathsEqual(designatedCanonical, known.FilePath)
                && string.Equals(
                    rolloutId,
                    RolloutFileIdentity.LegacyFallbackRolloutId(known.FilePath),
                    StringComparison.Ordinal)
                && RolloutFileIdentity.TryGetTrailingUuidV7(known.FilePath, out var filenameRolloutId)
                && string.Equals(
                    filenameRolloutId,
                    parsed.Result.Metadata.RolloutId,
                    StringComparison.Ordinal)))
            throw new InvalidDataException($"Known source rollout changed from {rolloutId} to {parsed.Result.Metadata.RolloutId}.");
        var viable = known?.CanonicalStatus != CanonicalStatus.Conflict
            || (known?.RolloutId is { } knownRollout && GetCanonical(knownRollout) is { } canonical && PathsEqual(canonical, known.FilePath));
        return new RevisionCandidate(known?.FilePath ?? filePath, known?.RolloutId ?? parsed.Result.Metadata.RolloutId,
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

    private void RememberRuntime(
        string filePath,
        RolloutChunkParseResult result,
        string boundaryHash,
        SourceIdentity sourceIdentity,
        long observedModifiedAtEpochMs) =>
        _runtimeByPath[NormalizeKey(filePath)] = new SourceRuntime(
            result.Metadata.RolloutId,
            result.StableByteLength,
            boundaryHash,
            result.State,
            result.Diagnostics.SafeOpaqueOversizedRecordsSkipped,
            result.Diagnostics.SafeNullPaddingRecordsSkipped,
            sourceIdentity,
            observedModifiedAtEpochMs);

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
        if (source.IsPresent && IsPartialSourceError(source.LastError)) _partialSourceKeys.Add(key);
        else _partialSourceKeys.Remove(key);
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
        _partialSourceKeys.Remove(key);
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
        var key = NormalizeKey(filePath);
        _sourcesByPath[key] = updated;
        _partialSourceKeys.Remove(key);
        RecordConflict(filePath, code, message, known.RolloutId);
        _runtimeByPath.Remove(key);
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
            var updated = known with
            {
                IsPresent = true,
                LastScannedAtEpochMs = NowEpochMs(),
                LastError = IsPartialSourceError(known.LastError) ? known.LastError : error.Message,
            };
            RequireStore().UpsertSourceFile(ToInput(updated));
            _sourcesByPath[key] = updated;
        }
        var now = NowEpochMs();
        var signature = $"{error.GetType().FullName}:{error.Message}";
        if (!_failureDiagnostics.TryGetValue(key, out var previous)
            || !string.Equals(previous.Signature, signature, StringComparison.Ordinal)
            || now - previous.LastRecordedEpochMs >= RepeatedFailureDiagnosticInterval.TotalMilliseconds)
        {
            AddDiagnostic(filePath, "source-read-retry", error.Message, DiagnosticSeverity.Warning);
            _failureDiagnostics[key] = new FailureDiagnosticState(signature, now);
        }
    }

    private void RecordEnumerationFailure(string directory, Exception error) =>
        AddDiagnostic(directory, "inventory-enumeration-failed", error.Message, DiagnosticSeverity.Warning);

    private void AddDiagnostic(string? path, string code, string message, DiagnosticSeverity severity) =>
        RequireStore().AddDiagnostic(new CollectorDiagnosticInput(
            _runId, path, severity, code, message, null, NowEpochMs()));

    private void AddDiagnostics(string filePath, RolloutParseDiagnostics value)
    {
        _diagnostics.MalformedLines += value.MalformedLines + value.NonObjectLines
            + value.OversizedRecords.Count(item => item.Disposition != OversizedRecordDisposition.SafeOpaqueSkipped);
        _diagnostics.SafeOpaqueOversizedRecordsSkipped += value.SafeOpaqueOversizedRecordsSkipped;
        if (value.SafeOpaqueOversizedRecordsSkipped > 0)
            AddDiagnostic(filePath, "safe-opaque-oversized-skipped",
                $"Safely skipped {value.SafeOpaqueOversizedRecordsSkipped} oversized opaque JSONL records after full syntax validation.",
                DiagnosticSeverity.Info);
        _diagnostics.SafeNullPaddingRecordsSkipped += value.SafeNullPaddingRecordsSkipped;
        if (value.SafeNullPaddingRecordsSkipped > 0)
            AddDiagnostic(filePath, "safe-null-padding-skipped",
                $"Safely skipped {value.SafeNullPaddingRecordsSkipped} complete all-NUL JSONL padding records.",
                DiagnosticSeverity.Warning);
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

    private void CompleteRecentMainThreadsQuery(QueryRecentMainThreadsCommand query)
    {
        try
        {
            EnsureStarted();
            query.Completion.TrySetResult(RequireStore().QueryRecentMainThreads(query.MaximumCount));
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
                case QueryRecentMainThreadsCommand query when query.CancellationToken.IsCancellationRequested:
                    query.Completion.TrySetCanceled(query.CancellationToken);
                    break;
                case QueryRecentMainThreadsCommand query:
                    CompleteRecentMainThreadsQuery(query);
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
        var phase = conflicts > 0 && _phase is CollectorPhase.Watching or CollectorPhase.Partial or CollectorPhase.Retrying
            ? CollectorPhase.Degraded
            : _phase == CollectorPhase.Retrying && !HasScheduledRecoverableRetries()
                ? CollectorPhase.Degraded
                : _phase;
        return new CollectorStatus(
            phase,
            _options.DatabasePath,
            _runStartedEpochMs == 0 ? null : FromEpoch(_runStartedEpochMs),
            _lastSuccessfulInventoryEpochMs is { } inventory ? FromEpoch(inventory) : null,
            _lastHeartbeatEpochMs is { } heartbeat ? FromEpoch(heartbeat) : null,
            store?.CountPresentSources() ?? 0,
            store?.CountPresentRealtimeVoiceSessions() ?? 0,
            _pendingPaths.Count + _watcherInbox.Count + _retryStates.Count,
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
                _diagnostics.CooperativeYieldCount,
                _partialSourceKeys.Count,
                _diagnostics.SafeOpaqueOversizedRecordsSkipped,
                _diagnostics.SafeNullPaddingRecordsSkipped),
            _usageRevision);
    }

    private void AdvanceUsageRevision(bool usageChanged)
    {
        if (usageChanged) _usageRevision = checked(_usageRevision + 1);
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

    private bool IsLexicallyObservedSessionIndex(string filePath) =>
        PathsEqual(Path.GetFullPath(filePath), _sessionIndexPath);

    private bool IsResolvedObservedSessionIndex()
    {
        try
        {
            if ((File.GetAttributes(_options.CodexHome) & FileAttributes.ReparsePoint) != 0) return false;
            if (!File.Exists(_sessionIndexPath)) return true;
            return (File.GetAttributes(_sessionIndexPath) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
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

    private static RolloutCheckpointInput CreateCheckpoint(
        string filePath,
        SourceStat stat,
        SourceIdentity sourceIdentity,
        long stableCompleteOffset,
        string boundaryHash,
        RolloutParserState state,
        int safeOpaqueOversizedRecords,
        int safeNullPaddingRecords,
        long trailingPartialBytes)
    {
        var parserStateJson = RolloutParserStateCodec.Serialize(state);
        var parserStateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parserStateJson)))
            .ToLowerInvariant();
        return new RolloutCheckpointInput(
            filePath,
            state.Metadata.RolloutId,
            RolloutParserStateCodec.FormatRevision,
            ParserRevision,
            sourceIdentity,
            stat.Size,
            stat.ModifiedAtEpochMs,
            stableCompleteOffset,
            boundaryHash,
            parserStateJson,
            parserStateHash,
            trailingPartialBytes,
            safeOpaqueOversizedRecords,
            safeNullPaddingRecords,
            NowEpochMs());
    }

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
        && left.AgentNickname == right.AgentNickname && left.IsRealtimeVoice == right.IsRealtimeVoice;

    private static bool SameDashboardUsageMetadata(RolloutMetadata? left, RolloutMetadata right) =>
        left is not null && left.RolloutId == right.RolloutId && left.ConversationId == right.ConversationId
        && left.ParentThreadId == right.ParentThreadId && left.ThreadType == right.ThreadType
        && left.AgentRole == right.AgentRole && left.AgentPath == right.AgentPath
        && left.AgentNickname == right.AgentNickname;

    private static bool SameThreadPickerMetadata(RolloutMetadata? left, RolloutMetadata right) =>
        left is not null && left.ThreadTitle == right.ThreadTitle
        && left.LastActivityEpochMs == right.LastActivityEpochMs;

    private static void RejectInternalDamage(string filePath, RolloutChunkParseResult result)
    {
        if (result.Diagnostics.MalformedLines > 0 || result.Diagnostics.NonObjectLines > 0
            || result.Diagnostics.HasUnsafeOversizedRecords)
            throw new InvalidDataException($"Stable JSONL content is malformed: {filePath}");
    }

    private void ClearSourceFailure(string key)
    {
        if (_failureDiagnostics.ContainsKey(key)
            && _sourcesByPath.TryGetValue(key, out var known)
            && known.LastError is not null
            && !IsPartialSourceError(known.LastError))
        {
            var updated = known with { LastError = null, LastScannedAtEpochMs = NowEpochMs() };
            RequireStore().UpsertSourceFile(ToInput(updated));
            _sourcesByPath[key] = updated;
        }
        _retryStates.Remove(key);
        _failureDiagnostics.Remove(key);
    }

    private static bool IsPartialSourceError(string? value) =>
        value?.StartsWith(PartialSourceErrorPrefix, StringComparison.Ordinal) == true;

    private static string PartialSourceMessage(int oversizedRecords, int nullPaddingRecords) =>
        $"{PartialSourceErrorPrefix} safely skipped {oversizedRecords} opaque oversized records and {nullPaddingRecords} all-NUL padding records";

    private static string ComputeBoundaryHash(byte[] buffer, int stableByteLength)
    {
        var start = Math.Max(0, stableByteLength - BoundaryWindowBytes);
        return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(start, stableByteLength - start))).ToLowerInvariant();
    }

    private static string FallbackRolloutId(string filePath) => RolloutFileIdentity.FallbackRolloutId(filePath);

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

    private sealed record RetryPathCommand(string FilePath, int Generation) : CollectorCommand;

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

    private sealed record QueryRecentMainThreadsCommand(
        int MaximumCount,
        TaskCompletionSource<IReadOnlyList<MainThreadOption>> Completion,
        CancellationToken CancellationToken) : CollectorCommand
    {
        public override void CancelCompletion() => Completion.TrySetCanceled();
    }

    private sealed record SourceRuntime(
        string RolloutId,
        long ByteOffset,
        string BoundaryHash,
        RolloutParserState State,
        int SafeOpaqueOversizedRecordsSkipped,
        int SafeNullPaddingRecordsSkipped,
        SourceIdentity SourceIdentity,
        long ObservedModifiedAtEpochMs);

    private sealed record CheckpointRehydrateResult(SourceRuntime? Runtime, string? Reason, long BytesRead)
    {
        public static CheckpointRehydrateResult Miss(string reason) => new(null, reason, 0);
    }

    private sealed record ReverseTokenReconciliationResult(bool Succeeded, string? Reason, long BytesRead)
    {
        public static ReverseTokenReconciliationResult Failure(string reason) => new(false, reason, 0);
    }

    private readonly record struct ReverseLineBoundaryResult(
        long LineStart,
        long PreviousLineEnd,
        long BytesRead,
        bool BudgetExceeded);

    private enum ReverseLineDisposition
    {
        NonToken,
        Token,
        Malformed,
    }

    private sealed record ReverseLineParseResult(ReverseLineDisposition Disposition, ReverseTokenRecord? Token);

    private sealed record ReverseTokenRecord(
        string CumulativeSnapshot,
        long TimestampEpochMs,
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        long ReasoningOutputTokens);

    private sealed class RetryState
    {
        public int ConsecutiveFailures { get; set; }
        public int Generation { get; set; }
        public long NextAllowedRetryTimestamp { get; set; }
        public bool Scheduled { get; set; }
        public bool InFlight { get; set; }
    }

    private sealed record FailureDiagnosticState(string Signature, long LastRecordedEpochMs);

    private readonly record struct SourceStat(long Size, long ModifiedAtEpochMs);

    private sealed record AppendSnapshot(
        SourceStat Stat,
        SourceIdentity SourceIdentity,
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

    private abstract record FullSnapshot(
        SourceStat Stat,
        SourceIdentity SourceIdentity,
        string ContentHash,
        string BoundaryHash);

    private sealed record ParsedSnapshot(
        SourceStat Stat,
        SourceIdentity SourceIdentity,
        string ContentHash,
        string BoundaryHash,
        RolloutChunkParseResult Result) : FullSnapshot(Stat, SourceIdentity, ContentHash, BoundaryHash);

    private sealed record UnsafeSnapshot(
        SourceStat Stat,
        SourceIdentity SourceIdentity,
        string ContentHash,
        string BoundaryHash,
        string Message) : FullSnapshot(Stat, SourceIdentity, ContentHash, BoundaryHash);

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

    private sealed record ConflictRecoveryResult(bool Recovered, bool UsageChanged);

    private sealed record RecoverySeed(string FilePath, ParsedSnapshot Snapshot);

    private sealed record RecoveryCandidate(
        string FilePath,
        ParsedSnapshot Snapshot,
        SignatureRelationship Relation);

    private sealed record RecoverySnapshotCacheEntry(ParsedSnapshot? Snapshot);

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
        public long SafeOpaqueOversizedRecordsSkipped { get; set; }
        public long SafeNullPaddingRecordsSkipped { get; set; }
    }

}

internal sealed record CollectorTestHooks(
    Func<string, CancellationToken, ValueTask>? AfterStableAppendSnapshotCapturedAsync = null,
    Func<CancellationToken, ValueTask>? BeforeInventoryEnumerationAsync = null,
    Action? BeforeQuery = null,
    Action? BeforeInteractiveDispatch = null,
    Action? AfterInventoryCompleted = null,
    Action<string>? AfterConfirmedRecoverySnapshot = null,
    Func<CancellationToken, ValueTask>? AfterFullRecoveryIndexBuiltAsync = null,
    Func<long>? GetMonotonicTimestamp = null,
    long MonotonicTimestampFrequency = 0,
    ISourceIdentityReader? SourceIdentityReader = null,
    Action<long>? SourceBytesRead = null,
    Action<string>? FullSnapshotRead = null,
    Func<string, CancellationToken, ValueTask>? BeforeConfirmationSnapshotAsync = null);
