using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexUsage.Domain;

public static partial class RolloutParser
{
    public const int CooperativeHardMaximumRecordBytes = 1024 * 1024;
    private const int MaximumSanitizedCarryBytes = 128 * 1024;
    private const long JavaScriptMaxSafeInteger = 9_007_199_254_740_991;

    public static RolloutParseResult Parse(string input, string fallbackRolloutId)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Parse(Encoding.UTF8.GetBytes(input), fallbackRolloutId);
    }

    public static RolloutParseResult Parse(ReadOnlyMemory<byte> input, string fallbackRolloutId)
    {
        var chunk = ParseChunk(input, fallbackRolloutId);
        return new(chunk.Metadata, chunk.Events, chunk.Diagnostics, chunk.StableLineCount, chunk.TrailingPartialLine);
    }

    public static RolloutChunkParseResult ParseChunk(string input, string fallbackRolloutId, RolloutParserState? priorState = null) =>
        ParseChunk(Encoding.UTF8.GetBytes(input), fallbackRolloutId, priorState);

    public static RolloutChunkParseResult ParseChunk(ReadOnlyMemory<byte> input, string fallbackRolloutId, RolloutParserState? priorState = null)
    {
        ArgumentNullException.ThrowIfNull(fallbackRolloutId);
        var stable = GetStableInput(input.Span);
        var accumulator = new ParserAccumulator(fallbackRolloutId, priorState);
        var cursor = 0;
        var stableLineCount = 0;
        while (cursor < stable.ByteLength)
        {
            var lineStart = cursor;
            var relativeNewline = input.Span[cursor..stable.ByteLength].IndexOf((byte)'\n');
            cursor = relativeNewline < 0 ? stable.ByteLength : cursor + relativeNewline + 1;
            var contentEnd = ContentEnd(input.Span, lineStart, cursor);
            var record = input[lineStart..contentEnd];
            if (IsAllNullPadding(record.Span))
                accumulator.SkipNullPaddingRecord();
            else if (record.Length > CooperativeHardMaximumRecordBytes)
                accumulator.ClassifyOversizedRecord(record, stableLineCount + 1);
            else
                accumulator.ProcessRecord(record);
            stableLineCount++;
        }
        return accumulator.Complete(stableLineCount, stable.ByteLength, stable.TrailingPartialLine);
    }

    public static async ValueTask<RolloutChunkParseResult> ParseChunkCooperativelyAsync(
        ReadOnlyMemory<byte> input,
        string fallbackRolloutId,
        CooperativeParseOptions options,
        RolloutParserState? priorState = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fallbackRolloutId);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        var stable = GetStableInput(input.Span);
        var accumulator = new ParserAccumulator(fallbackRolloutId, priorState);
        var cursor = 0;
        var stableLineCount = 0;
        var recordsInSlice = 0;
        long bytesInSlice = 0;
        var sliceStarted = Stopwatch.GetTimestamp();

        async ValueTask YieldAsync()
        {
            await options.YieldControl(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            recordsInSlice = 0;
            bytesInSlice = 0;
            sliceStarted = Stopwatch.GetTimestamp();
        }

        while (cursor < stable.ByteLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lineStart = cursor;
            var searchStart = cursor;
            while (true)
            {
                var searchEnd = (int)Math.Min((long)searchStart + options.MaxBytesPerSlice, stable.ByteLength);
                var relativeNewline = input.Span[searchStart..searchEnd].IndexOf((byte)'\n');
                if (relativeNewline >= 0)
                {
                    cursor = searchStart + relativeNewline + 1;
                    break;
                }
                if (searchEnd >= stable.ByteLength)
                {
                    cursor = stable.ByteLength;
                    break;
                }
                searchStart = searchEnd;
                await YieldAsync().ConfigureAwait(false);
            }

            var contentEnd = ContentEnd(input.Span, lineStart, cursor);
            var recordBytes = contentEnd - lineStart;
            if (IsAllNullPadding(input.Span[lineStart..contentEnd]))
            {
                accumulator.SkipNullPaddingRecord();
            }
            else if (recordBytes > options.MaximumRecordBytes)
            {
                accumulator.AddOversizedDiagnostic(await InspectOversizedRecordCooperativelyAsync(
                    input[lineStart..contentEnd], stableLineCount + 1, options, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                accumulator.ProcessRecord(input[lineStart..contentEnd]);
                cancellationToken.ThrowIfCancellationRequested();
            }
            stableLineCount++;
            recordsInSlice++;
            bytesInSlice = checked(bytesInSlice + cursor - lineStart);
            if (cursor < stable.ByteLength
                && (recordsInSlice >= options.MaxRecordsPerSlice
                    || bytesInSlice >= options.MaxBytesPerSlice
                    || Stopwatch.GetElapsedTime(sliceStarted) >= options.MaxTimePerSlice))
            {
                await YieldAsync().ConfigureAwait(false);
            }
        }

        return await accumulator.CompleteCooperativelyAsync(
            stableLineCount, stable.ByteLength, stable.TrailingPartialLine, options, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateOptions(CooperativeParseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.YieldControl);
        if (options.MaxBytesPerSlice <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxBytesPerSlice must be positive.");
        if (options.MaxRecordsPerSlice <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxRecordsPerSlice must be positive.");
        if (options.MaxTimePerSlice <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "MaxTimePerSlice must be positive.");
        if (options.MaximumRecordBytes is <= 0 or > CooperativeHardMaximumRecordBytes)
            throw new ArgumentOutOfRangeException(nameof(options), $"MaximumRecordBytes must be between 1 and {CooperativeHardMaximumRecordBytes}.");
    }

    private static int ContentEnd(ReadOnlySpan<byte> input, int lineStart, int cursor)
    {
        var contentEnd = cursor > lineStart && input[cursor - 1] == (byte)'\n' ? cursor - 1 : cursor;
        return contentEnd > lineStart && input[contentEnd - 1] == (byte)'\r' ? contentEnd - 1 : contentEnd;
    }

    private sealed class ParserAccumulator
    {
        private readonly RolloutParserState? _priorState;
        private readonly MutableDiagnostics _diagnostics = new();
        private readonly ImmutableDictionary<string, string>.Builder _turnModels;
        private readonly List<TokenCandidate> _candidates = [];
        private bool _hasMetadata;
        private RolloutMetadata _metadata;
        private string _currentTurnId;
        private bool _currentTurnModelOverridden;
        private string _currentModel;
        private RolloutForkReplayState _forkReplay;
        private string? _previousSnapshot;

        public ParserAccumulator(string fallbackRolloutId, RolloutParserState? priorState)
        {
            _priorState = priorState;
            _turnModels = priorState?.TurnModels.ToBuilder() ?? ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            _hasMetadata = priorState?.HasMetadata ?? false;
            _metadata = priorState?.Metadata ?? MetadataFrom(null, fallbackRolloutId);
            _currentTurnId = priorState?.CurrentTurnId ?? string.Empty;
            _currentTurnModelOverridden = priorState?.CurrentTurnModelOverridden ?? false;
            _currentModel = priorState?.CurrentModel ?? "unknown";
            _forkReplay = priorState?.ForkReplay ?? RolloutForkReplayState.Inactive;
            _previousSnapshot = priorState?.PreviousSnapshot;
        }

        public void ClassifyOversizedRecord(ReadOnlyMemory<byte> rawLine, int stableLineNumber) =>
            AddOversizedDiagnostic(InspectOversizedRecord(rawLine.Span, stableLineNumber));

        public void AddOversizedDiagnostic(OversizedRecordDiagnostic diagnostic) =>
            _diagnostics.OversizedRecords.Add(diagnostic);

        public void SkipNullPaddingRecord() => _diagnostics.SafeNullPaddingRecordsSkipped++;

        public void ProcessRecord(ReadOnlyMemory<byte> rawLine)
        {
            if (IsBlank(rawLine.Span))
            {
                _diagnostics.BlankLines++;
                return;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(rawLine);
            }
            catch (JsonException)
            {
                _diagnostics.MalformedLines++;
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    _diagnostics.NonObjectLines++;
                    return;
                }
                var eventType = GetNonEmptyString(root, "type");
                var hasPayload = TryGetObject(root, "payload", out var payload);
                RecordActivity(root);

                if (eventType == "session_meta" && !_hasMetadata && hasPayload)
                {
                    _hasMetadata = true;
                    _metadata = MetadataFrom(payload, _metadata.RolloutId) with
                    {
                        ThreadTitle = _metadata.ThreadTitle,
                        LastActivityEpochMs = Math.Max(
                            _metadata.LastActivityEpochMs,
                            ActivityEpochMilliseconds(root)),
                    };
                    if (_metadata.IsRealtimeVoice)
                    {
                        _candidates.Clear();
                        _previousSnapshot = null;
                    }
                    _forkReplay = ForkReplayFrom(payload, _metadata, root);
                    return;
                }

                if (eventType == "turn_context" && hasPayload)
                {
                    ProcessTurnContext(payload);
                    return;
                }

                if (eventType == "inter_agent_communication_metadata" && hasPayload && GetBoolean(payload, "trigger_turn") == true)
                {
                    if (_forkReplay.Status == ForkReplayStatus.AwaitingTrigger)
                        _forkReplay = new(ForkReplayStatus.AwaitingRecipient, TurnId: _forkReplay.TurnId, Model: _forkReplay.Model);
                    return;
                }

                if (eventType == "response_item" && hasPayload && GetNonEmptyString(payload, "type") == "agent_message"
                    && _forkReplay.Status == ForkReplayStatus.AwaitingRecipient)
                {
                    ProcessForkRecipient(payload);
                    return;
                }

                if (eventType != "event_msg" || !hasPayload) return;
                ProcessEventMessage(root, payload);
            }
        }

        private static bool IsBlank(ReadOnlySpan<byte> value)
        {
            foreach (var item in value)
            {
                if (item is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                    return item >= 0x80 && string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(value));
            }
            return true;
        }

        private void ProcessTurnContext(JsonElement payload)
        {
            var turnId = GetNonEmptyString(payload, "turn_id");
            var model = GetNonEmptyString(payload, "model");
            if (_forkReplay.Status == ForkReplayStatus.AwaitingTurnContext)
            {
                if (turnId == _forkReplay.TurnId)
                    _forkReplay = new(ForkReplayStatus.AwaitingTrigger, TurnId: _forkReplay.TurnId, Model: model);
                return;
            }
            if (_forkReplay.Status != ForkReplayStatus.Inactive) return;
            if (turnId is null) return;
            if (turnId != _currentTurnId) _currentTurnModelOverridden = false;
            _currentTurnId = turnId;
            if (model is not null) _turnModels[turnId] = model;
        }

        private void ProcessForkRecipient(JsonElement payload)
        {
            string? internalTurnId = null;
            if (TryGetObject(payload, "internal_chat_message_metadata_passthrough", out var internalMetadata))
                internalTurnId = GetNonEmptyString(internalMetadata, "turn_id");
            if (GetNonEmptyString(payload, "recipient") != _metadata.AgentPath
                || (internalTurnId is not null && internalTurnId != _forkReplay.TurnId)) return;
            _currentTurnId = _forkReplay.TurnId!;
            if (_forkReplay.Model is not null) _turnModels[_currentTurnId] = _forkReplay.Model;
            _forkReplay = RolloutForkReplayState.Inactive;
        }

        private void ProcessEventMessage(JsonElement root, JsonElement payload)
        {
            var payloadType = GetNonEmptyString(payload, "type");
            if (payloadType == "user_message")
            {
                return;
            }

            if (payloadType == "thread_settings_applied")
            {
                if (TryGetObject(payload, "thread_settings", out var settings) && GetNonEmptyString(settings, "model") is { } model)
                {
                    _currentModel = model;
                    if (_currentTurnId.Length > 0) _currentTurnModelOverridden = true;
                }
                return;
            }

            if (payloadType == "task_started")
            {
                ProcessTaskStarted(payload);
                return;
            }

            if (payloadType == "task_complete")
            {
                var turnId = GetNonEmptyString(payload, "turn_id");
                if (turnId is null || turnId == _currentTurnId)
                {
                    _currentTurnId = string.Empty;
                    _currentTurnModelOverridden = false;
                }
                return;
            }

            if (payloadType == "token_count" && _forkReplay.Status == ForkReplayStatus.Inactive)
                ProcessTokenCount(root, payload);
        }

        private void RecordActivity(JsonElement root)
        {
            var activity = ActivityEpochMilliseconds(root);
            if (activity > _metadata.LastActivityEpochMs)
                _metadata = _metadata with { LastActivityEpochMs = activity };
        }

        private void ProcessTaskStarted(JsonElement payload)
        {
            var turnId = GetNonEmptyString(payload, "turn_id");
            if (_forkReplay.Status == ForkReplayStatus.AwaitingMainLiveTurn)
            {
                if (!IsLiveMainForkTurn(payload, _forkReplay.ForkBoundaryEpochMilliseconds)) return;
                _forkReplay = RolloutForkReplayState.Inactive;
            }
            else if (_forkReplay.Status == ForkReplayStatus.Unproven)
            {
                return;
            }
            else if (_forkReplay.Status != ForkReplayStatus.Inactive)
            {
                if (turnId is not null) _forkReplay = new(ForkReplayStatus.AwaitingTurnContext, TurnId: turnId);
                return;
            }
            if (turnId is not null) _currentTurnId = turnId;
            _currentTurnModelOverridden = false;
        }

        private void ProcessTokenCount(JsonElement root, JsonElement payload)
        {
            if (_metadata.IsRealtimeVoice) return;
            TokenTuple? usage = null, total = null;
            if (TryGetObject(payload, "info", out var info))
            {
                if (TryGetObject(info, "last_token_usage", out var last)) usage = TokenTupleFrom(last);
                if (TryGetObject(info, "total_token_usage", out var cumulative)) total = TokenTupleFrom(cumulative);
            }
            if (usage is null || total is null)
            {
                _diagnostics.InvalidTokenUsageLines++;
                return;
            }
            var snapshot = total.Snapshot;
            if (usage.InputTokens == 0 && usage.CachedInputTokens == 0 && usage.OutputTokens == 0 && usage.ReasoningOutputTokens == 0)
            {
                _diagnostics.ZeroBreakdownSnapshotsSkipped++;
                return;
            }
            if (usage.CachedInputTokens > usage.InputTokens || usage.ReasoningOutputTokens > usage.OutputTokens)
            {
                _diagnostics.InvalidTokenRelationshipsSkipped++;
                return;
            }
            var timestamp = GetNonEmptyString(root, "timestamp");
            if (timestamp is null || !TryTimestamp(timestamp, out _))
            {
                _diagnostics.InvalidTimestampsSkipped++;
                return;
            }
            if (snapshot == _previousSnapshot)
            {
                _diagnostics.DuplicateSnapshotsSkipped++;
                return;
            }
            _previousSnapshot = snapshot;
            var candidateTurnId = GetNonEmptyString(payload, "turn_id") ?? _currentTurnId;
            var activeSetting = _currentTurnModelOverridden && candidateTurnId == _currentTurnId && _currentModel != "unknown";
            var fallbackSetting = !activeSetting && !_turnModels.ContainsKey(candidateTurnId) && _currentModel != "unknown";
            _candidates.Add(new(timestamp, candidateTurnId, activeSetting ? _currentModel : fallbackSetting ? _currentModel : null,
                activeSetting ? ModelSource.ActiveTurnSetting : fallbackSetting ? ModelSource.SettingsFallback : ModelSource.None,
                usage, snapshot));
        }

        public RolloutChunkParseResult Complete(int stableLineCount, int stableByteLength, bool trailingPartialLine)
        {
            var events = ImmutableArray.CreateBuilder<ParsedRolloutUsageEvent>(_candidates.Count);
            for (var index = 0; index < _candidates.Count; index++) events.Add(Materialize(_candidates[index], index));
            return BuildResult(events.ToImmutable(), stableLineCount, stableByteLength, trailingPartialLine);
        }

        public async ValueTask<RolloutChunkParseResult> CompleteCooperativelyAsync(
            int stableLineCount,
            int stableByteLength,
            bool trailingPartialLine,
            CooperativeParseOptions options,
            CancellationToken cancellationToken)
        {
            var events = ImmutableArray.CreateBuilder<ParsedRolloutUsageEvent>(_candidates.Count);
            var recordsInSlice = 0;
            var sliceStarted = Stopwatch.GetTimestamp();

            async ValueTask CheckpointAsync(bool hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();
                recordsInSlice++;
                if (!hasMore
                    || (recordsInSlice < options.MaxRecordsPerSlice && Stopwatch.GetElapsedTime(sliceStarted) < options.MaxTimePerSlice)) return;
                await options.YieldControl(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                recordsInSlice = 0;
                sliceStarted = Stopwatch.GetTimestamp();
            }

            for (var index = 0; index < _candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                events.Add(Materialize(_candidates[index], index));
                await CheckpointAsync(index + 1 < _candidates.Count).ConfigureAwait(false);
            }

            var immutableEvents = events.ToImmutable();
            var unresolved = (_priorState?.UnresolvedTurnIds ?? ImmutableSortedSet<string>.Empty).ToBuilder();
            var provisional = (_priorState?.ProvisionalTurnIds ?? ImmutableSortedSet<string>.Empty).ToBuilder();
            recordsInSlice = 0;
            sliceStarted = Stopwatch.GetTimestamp();
            for (var index = 0; index < immutableEvents.Length; index++)
            {
                if (immutableEvents[index].Model == "unknown") unresolved.Add(immutableEvents[index].TurnId);
                await CheckpointAsync(index + 1 < immutableEvents.Length || _candidates.Count > 0 || _turnModels.Count > 0).ConfigureAwait(false);
            }
            for (var index = 0; index < _candidates.Count; index++)
            {
                var candidate = _candidates[index];
                if (candidate.Source == ModelSource.SettingsFallback && candidate.TurnId.Length > 0 && !_turnModels.ContainsKey(candidate.TurnId))
                    provisional.Add(candidate.TurnId);
                await CheckpointAsync(index + 1 < _candidates.Count || _turnModels.Count > 0).ConfigureAwait(false);
            }
            var turnIndex = 0;
            foreach (var turnId in _turnModels.Keys)
            {
                unresolved.Remove(turnId);
                provisional.Remove(turnId);
                turnIndex++;
                await CheckpointAsync(turnIndex < _turnModels.Count).ConfigureAwait(false);
            }
            var firstOrdinal = _priorState?.NextTokenEventOrdinal ?? 0;
            var state = new RolloutParserState(
                _hasMetadata, _metadata, _turnModels.ToImmutable(), _currentTurnId, _currentTurnModelOverridden, _currentModel,
                _forkReplay, _previousSnapshot, checked(firstOrdinal + immutableEvents.Length), unresolved.ToImmutable(), provisional.ToImmutable());
            return new(_metadata, immutableEvents, _diagnostics.ToImmutable(), state, stableLineCount, stableByteLength, trailingPartialLine);
        }

        private ParsedRolloutUsageEvent Materialize(TokenCandidate candidate, int candidateIndex)
        {
            var model = candidate.Source == ModelSource.ActiveTurnSetting
                ? candidate.Model!
                : _turnModels.TryGetValue(candidate.TurnId, out var exactModel)
                    ? exactModel
                    : candidate.Source == ModelSource.SettingsFallback ? candidate.Model! : "unknown";
            var ordinal = checked((_priorState?.NextTokenEventOrdinal ?? 0) + candidateIndex);
            var signature = JsonSerializer.Serialize(new object[]
            {
                candidate.Timestamp, candidate.TurnId, candidate.Usage.InputTokens, candidate.Usage.CachedInputTokens,
                candidate.Usage.OutputTokens, candidate.Usage.ReasoningOutputTokens, candidate.Snapshot,
            });
            return new(
                _metadata.ConversationId, _metadata.RolloutId, _metadata.ParentThreadId, _metadata.ThreadType, _metadata.AgentRole,
                _metadata.AgentPath, _metadata.AgentNickname, candidate.Timestamp, ordinal, candidate.TurnId, model,
                candidate.Usage.InputTokens, candidate.Usage.CachedInputTokens, candidate.Usage.OutputTokens,
                candidate.Usage.ReasoningOutputTokens, candidate.Snapshot, signature);
        }

        private RolloutChunkParseResult BuildResult(
            ImmutableArray<ParsedRolloutUsageEvent> events,
            int stableLineCount,
            int stableByteLength,
            bool trailingPartialLine)
        {
            var unresolved = (_priorState?.UnresolvedTurnIds ?? ImmutableSortedSet<string>.Empty).ToBuilder();
            var provisional = (_priorState?.ProvisionalTurnIds ?? ImmutableSortedSet<string>.Empty).ToBuilder();
            foreach (var usageEvent in events)
                if (usageEvent.Model == "unknown") unresolved.Add(usageEvent.TurnId);
            foreach (var candidate in _candidates)
                if (candidate.Source == ModelSource.SettingsFallback && candidate.TurnId.Length > 0 && !_turnModels.ContainsKey(candidate.TurnId))
                    provisional.Add(candidate.TurnId);
            foreach (var turnId in _turnModels.Keys)
            {
                unresolved.Remove(turnId);
                provisional.Remove(turnId);
            }
            var firstOrdinal = _priorState?.NextTokenEventOrdinal ?? 0;
            var state = new RolloutParserState(
                _hasMetadata, _metadata, _turnModels.ToImmutable(), _currentTurnId, _currentTurnModelOverridden, _currentModel,
                _forkReplay, _previousSnapshot, checked(firstOrdinal + events.Length), unresolved.ToImmutable(), provisional.ToImmutable());
            return new(_metadata, events, _diagnostics.ToImmutable(), state, stableLineCount, stableByteLength, trailingPartialLine);
        }
    }

    private static OversizedRecordDiagnostic InspectOversizedRecord(
        ReadOnlySpan<byte> rawLine,
        int stableLineNumber)
    {
        var inspector = new OversizedRecordInspector(stableLineNumber, rawLine.Length);
        try
        {
            var reader = new Utf8JsonReader(rawLine, isFinalBlock: true, state: default);
            while (reader.Read()) inspector.ProcessToken(ref reader);
        }
        catch (JsonException)
        {
            return inspector.Malformed();
        }
        return inspector.Complete();
    }

    private static async ValueTask<OversizedRecordDiagnostic> InspectOversizedRecordCooperativelyAsync(
        ReadOnlyMemory<byte> rawLine,
        int stableLineNumber,
        CooperativeParseOptions options,
        CancellationToken cancellationToken)
    {
        var inspector = new OversizedRecordInspector(stableLineNumber, rawLine.Length);
        var sanitizer = new OversizedJsonSanitizer();
        var readerState = new JsonReaderState();
        var sanitized = new ArrayBufferWriter<byte>(Math.Min(options.MaxBytesPerSlice, 64 * 1024));
        var cursor = 0;
        while (cursor < rawLine.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var byteBoundary = (int)Math.Min(rawLine.Length, cursor + (long)options.MaxBytesPerSlice);
            var sliceStarted = Stopwatch.GetTimestamp();
            var initialSanitizedBytes = sanitized.WrittenCount;
            while (cursor < byteBoundary
                && (sanitized.WrittenCount < options.MaxBytesPerSlice
                    || sanitized.WrittenCount == initialSanitizedBytes)
                && Stopwatch.GetElapsedTime(sliceStarted) < options.MaxTimePerSlice)
            {
                if (!sanitizer.Consume(rawLine.Span, ref cursor, byteBoundary, sanitized)) return inspector.Malformed();
            }

            if (cursor < rawLine.Length && sanitized.WrittenCount >= options.MaxBytesPerSlice)
            {
                var slice = InspectOversizedRecordSlice(
                    sanitized.WrittenMemory, false, readerState, inspector);
                if (slice.Malformed) return inspector.Malformed();
                readerState = slice.ReaderState;
                var carryLength = sanitized.WrittenCount - slice.BytesConsumed;
                if (carryLength > MaximumSanitizedCarryBytes) return inspector.Malformed();
                var next = new ArrayBufferWriter<byte>(Math.Max(
                    Math.Min(options.MaxBytesPerSlice, 64 * 1024),
                    carryLength));
                sanitized.WrittenMemory[slice.BytesConsumed..].CopyTo(next.GetMemory(carryLength));
                next.Advance(carryLength);
                sanitized = next;
            }

            if (cursor < rawLine.Length)
            {
                await options.YieldControl(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        if (!sanitizer.Complete(sanitized)) return inspector.Malformed();
        var finalSlice = InspectOversizedRecordSlice(sanitized.WrittenMemory, true, readerState, inspector);
        if (finalSlice.Malformed || finalSlice.BytesConsumed != sanitized.WrittenCount) return inspector.Malformed();

        return inspector.Complete();
    }

    private static OversizedRecordSliceResult InspectOversizedRecordSlice(
        ReadOnlyMemory<byte> input,
        bool finalBlock,
        JsonReaderState readerState,
        OversizedRecordInspector inspector)
    {
        try
        {
            var reader = new Utf8JsonReader(input.Span, finalBlock, readerState);
            while (reader.Read()) inspector.ProcessToken(ref reader);
            return new(checked((int)reader.BytesConsumed), reader.CurrentState, false);
        }
        catch (JsonException)
        {
            return new(0, readerState, true);
        }
    }

    private static OversizedEventType ReadOversizedEventType(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("session_meta"u8)) return OversizedEventType.SessionMeta;
        if (reader.ValueTextEquals("turn_context"u8)) return OversizedEventType.TurnContext;
        if (reader.ValueTextEquals("inter_agent_communication_metadata"u8))
            return OversizedEventType.InterAgentCommunicationMetadata;
        if (reader.ValueTextEquals("response_item"u8)) return OversizedEventType.ResponseItem;
        if (reader.ValueTextEquals("event_msg"u8)) return OversizedEventType.EventMessage;
        if (reader.ValueTextEquals("compacted"u8)) return OversizedEventType.Compacted;
        return OversizedEventType.Other;
    }

    private static OversizedPayloadType ReadOversizedPayloadType(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("agent_message"u8)) return OversizedPayloadType.AgentMessage;
        if (reader.ValueTextEquals("token_count"u8)) return OversizedPayloadType.TokenCount;
        if (reader.ValueTextEquals("thread_settings_applied"u8)
            || reader.ValueTextEquals("task_started"u8)
            || reader.ValueTextEquals("task_complete"u8)) return OversizedPayloadType.EventContext;
        if (reader.ValueTextEquals("image_generation_end"u8)) return OversizedPayloadType.ImageGenerationEnd;
        if (reader.ValueTextEquals("mcp_tool_call_end"u8)) return OversizedPayloadType.McpToolCallEnd;
        if (reader.ValueTextEquals("custom_tool_call"u8)
            || reader.ValueTextEquals("custom_tool_call_output"u8)
            || reader.ValueTextEquals("function_call"u8)
            || reader.ValueTextEquals("function_call_output"u8)
            || reader.ValueTextEquals("local_shell_call"u8)
            || reader.ValueTextEquals("message"u8)
            || reader.ValueTextEquals("reasoning"u8)
            || reader.ValueTextEquals("tool_search_call"u8)
            || reader.ValueTextEquals("tool_search_output"u8)
            || reader.ValueTextEquals("web_search_call"u8)
            || reader.ValueTextEquals("image_generation_call"u8)
            || reader.ValueTextEquals("compaction"u8)
            || reader.ValueTextEquals("compaction_summary"u8)
            || reader.ValueTextEquals("context_compaction"u8)) return OversizedPayloadType.OpaqueResponseItem;
        return OversizedPayloadType.Other;
    }

    private static RolloutMetadata MetadataFrom(JsonElement? payload, string fallbackRolloutId)
    {
        JsonElement spawn = default;
        var hasSpawn = payload is { } metadata
            && TryGetObject(metadata, "source", out var source)
            && TryGetObject(source, "subagent", out var subagent)
            && TryGetObject(subagent, "thread_spawn", out spawn);
        var threadSource = payload is { } value ? GetNonEmptyString(value, "thread_source") : null;
        var threadType = threadSource == "subagent" || hasSpawn
            ? ThreadType.Subagent
            : threadSource is null or "user" or "realtime_voice" ? ThreadType.Main : ThreadType.Unknown;
        var rolloutId = payload is { } p ? GetNonEmptyString(p, "id") ?? fallbackRolloutId : fallbackRolloutId;
        string? Top(string name) => payload is { } topPayload ? GetNonEmptyString(topPayload, name) : null;
        string Field(string nestedName, string topName, string fallback) =>
            hasSpawn ? GetNonEmptyString(spawn, nestedName) ?? Top(topName) ?? fallback : Top(topName) ?? fallback;
        return new(
            payload is { } data ? GetNonEmptyString(data, "session_id") ?? rolloutId : rolloutId,
            rolloutId,
            Field("parent_thread_id", "parent_thread_id", string.Empty),
            threadType,
            threadType == ThreadType.Main ? "main" : Field("agent_role", "agent_role", "unknown"),
            threadType == ThreadType.Main ? "/root" : Field("agent_path", "agent_path", "/root"),
            Field("agent_nickname", "agent_nickname", string.Empty),
            threadSource == "realtime_voice",
            ProjectNameFromCwd(Top("cwd")),
            string.Empty,
            0);
    }

    private static string ProjectNameFromCwd(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "Codex";
        var trimmed = cwd.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0) return "Codex";
        var separator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        var projectName = separator < 0 ? trimmed : trimmed[(separator + 1)..];
        return string.IsNullOrWhiteSpace(projectName) ? "Codex" : projectName;
    }

    private static long ActivityEpochMilliseconds(JsonElement root) =>
        GetNonEmptyString(root, "timestamp") is { } timestamp && TryTimestamp(timestamp, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : 0;

    private static RolloutForkReplayState ForkReplayFrom(JsonElement payload, RolloutMetadata metadata, JsonElement root)
    {
        if (GetNonEmptyString(payload, "forked_from_id") is null) return RolloutForkReplayState.Inactive;
        if (metadata.ThreadType == ThreadType.Subagent) return new(ForkReplayStatus.AwaitingTaskStarted);
        if (metadata.ThreadType != ThreadType.Main) return new(ForkReplayStatus.Unproven);
        long? boundary = GetNonEmptyString(root, "timestamp") is { } timestamp && TryTimestamp(timestamp, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : UuidV7EpochMilliseconds(metadata.RolloutId);
        return new(ForkReplayStatus.AwaitingMainLiveTurn, boundary);
    }

    private static bool IsLiveMainForkTurn(JsonElement payload, long? boundaryMilliseconds)
    {
        if (boundaryMilliseconds is null) return false;
        var startedAt = GetNonNegativeSafeInteger(payload, "started_at");
        var turnMilliseconds = UuidV7EpochMilliseconds(GetNonEmptyString(payload, "turn_id"));
        if (startedAt is null) return turnMilliseconds is not null && turnMilliseconds > boundaryMilliseconds;
        var boundarySeconds = boundaryMilliseconds / 1000;
        if (startedAt == boundarySeconds) return turnMilliseconds is not null && turnMilliseconds > boundaryMilliseconds;
        var liveByStartedAt = startedAt > boundarySeconds;
        if (turnMilliseconds is not null && (turnMilliseconds > boundaryMilliseconds) != liveByStartedAt) return false;
        return liveByStartedAt;
    }

    private static TokenTuple? TokenTupleFrom(JsonElement value)
    {
        var input = GetNonNegativeSafeInteger(value, "input_tokens");
        var cached = GetNonNegativeSafeInteger(value, "cached_input_tokens");
        var output = GetNonNegativeSafeInteger(value, "output_tokens");
        var reasoning = GetNonNegativeSafeInteger(value, "reasoning_output_tokens");
        var total = GetNonNegativeSafeInteger(value, "total_tokens");
        return input is not null && cached is not null && output is not null && reasoning is not null && total is not null
            ? new(input.Value, cached.Value, output.Value, reasoning.Value, total.Value)
            : null;
    }

    private static long? GetNonNegativeSafeInteger(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var number)) return null;
        return number is >= 0 and <= JavaScriptMaxSafeInteger ? number : null;
    }

    private static string? GetNonEmptyString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text ? text : null;

    private static bool? GetBoolean(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static bool TryGetObject(JsonElement value, string name, out JsonElement result)
    {
        if (value.TryGetProperty(name, out result) && result.ValueKind == JsonValueKind.Object) return true;
        result = default;
        return false;
    }

    private static bool TryTimestamp(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var match = TimestampPattern().Match(value);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[1].Value, out var year) || !int.TryParse(match.Groups[2].Value, out var month)
            || !int.TryParse(match.Groups[3].Value, out var day) || !int.TryParse(match.Groups[4].Value, out var hour)
            || !int.TryParse(match.Groups[5].Value, out var minute) || !int.TryParse(match.Groups[6].Value, out var second)) return false;
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59) return false;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
    }

    private static long? UuidV7EpochMilliseconds(string? value)
    {
        if (value is null || !UuidV7Pattern().IsMatch(value)) return null;
        var hex = value[..8] + value.Substring(9, 4);
        return long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var milliseconds) ? milliseconds : null;
    }

    private static StableInput GetStableInput(ReadOnlySpan<byte> input)
    {
        var trailing = input.Length > 0 && input[^1] != (byte)'\n';
        if (!trailing) return new(input.Length, false);
        var newline = input.LastIndexOf((byte)'\n');
        return new(newline + 1, true);
    }

    private static bool IsAllNullPadding(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty) return false;
        foreach (var value in input)
            if (value != 0) return false;
        return true;
    }

    [GeneratedRegex("^(\\d{4})-(\\d{2})-(\\d{2})T(\\d{2}):(\\d{2}):(\\d{2})(?:\\.\\d+)?(?:Z|[+-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UuidV7Pattern();

    private sealed record StableInput(int ByteLength, bool TrailingPartialLine);
    private sealed record OversizedRecordSliceResult(
        int BytesConsumed,
        JsonReaderState ReaderState,
        bool Malformed);
    private sealed record TokenTuple(long InputTokens, long CachedInputTokens, long OutputTokens, long ReasoningOutputTokens, long TotalTokens)
    {
        public string Snapshot => $"{InputTokens}:{CachedInputTokens}:{OutputTokens}:{ReasoningOutputTokens}:{TotalTokens}";
    }
    private enum ModelSource { None, ActiveTurnSetting, SettingsFallback }
    private sealed record TokenCandidate(string Timestamp, string TurnId, string? Model, ModelSource Source, TokenTuple Usage, string Snapshot);
    private sealed class MutableDiagnostics
    {
        public int BlankLines { get; set; }
        public int SafeNullPaddingRecordsSkipped { get; set; }
        public int MalformedLines { get; set; }
        public int NonObjectLines { get; set; }
        public List<OversizedRecordDiagnostic> OversizedRecords { get; } = [];
        public int InvalidTokenUsageLines { get; set; }
        public int DuplicateSnapshotsSkipped { get; set; }
        public int ZeroBreakdownSnapshotsSkipped { get; set; }
        public int InvalidTokenRelationshipsSkipped { get; set; }
        public int InvalidTimestampsSkipped { get; set; }
        public RolloutParseDiagnostics ToImmutable() => new(BlankLines, SafeNullPaddingRecordsSkipped,
            MalformedLines, NonObjectLines, [.. OversizedRecords],
            InvalidTokenUsageLines, DuplicateSnapshotsSkipped, ZeroBreakdownSnapshotsSkipped,
            InvalidTokenRelationshipsSkipped, InvalidTimestampsSkipped);
    }

    private sealed class OversizedRecordInspector(int stableLineNumber, int recordByteLength)
    {
        private OversizedEventType _eventType = OversizedEventType.Unknown;
        private OversizedPayloadType _payloadType = OversizedPayloadType.Unknown;
        private bool _rootTypeSeen;
        private bool _payloadSeen;
        private bool _payloadWasObject;
        private int? _payloadObjectDepth;
        private bool _payloadTypeSeen;
        private bool _structureAmbiguous;
        private bool _rootObject;
        private OversizedPendingProperty _pending;

        public void ProcessToken(ref Utf8JsonReader reader)
        {
            if (!_rootObject)
            {
                if (reader.TokenType != JsonTokenType.StartObject || reader.CurrentDepth != 0)
                    _structureAmbiguous = true;
                _rootObject = true;
                return;
            }

            if (reader.TokenType == JsonTokenType.EndObject
                && _payloadObjectDepth == reader.CurrentDepth)
            {
                _payloadObjectDepth = null;
                _pending = OversizedPendingProperty.None;
                return;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _pending = reader.CurrentDepth switch
                {
                    1 when reader.ValueTextEquals("type"u8) => OversizedPendingProperty.RootType,
                    1 when reader.ValueTextEquals("payload"u8) => OversizedPendingProperty.Payload,
                    var depth when _payloadObjectDepth is { } payloadDepth
                        && depth == payloadDepth + 1
                        && reader.ValueTextEquals("type"u8) => OversizedPendingProperty.PayloadType,
                    _ => OversizedPendingProperty.None,
                };
                return;
            }

            switch (_pending)
            {
                case OversizedPendingProperty.RootType:
                    if (_rootTypeSeen || reader.TokenType != JsonTokenType.String)
                        _eventType = OversizedEventType.Ambiguous;
                    else
                        _eventType = ReadOversizedEventType(ref reader);
                    _rootTypeSeen = true;
                    break;
                case OversizedPendingProperty.Payload:
                    if (_payloadSeen || reader.TokenType != JsonTokenType.StartObject)
                        _structureAmbiguous = true;
                    else
                    {
                        _payloadWasObject = true;
                        _payloadObjectDepth = reader.CurrentDepth;
                    }
                    _payloadSeen = true;
                    break;
                case OversizedPendingProperty.PayloadType:
                    if (_payloadTypeSeen || reader.TokenType != JsonTokenType.String)
                        _payloadType = OversizedPayloadType.Ambiguous;
                    else
                        _payloadType = ReadOversizedPayloadType(ref reader);
                    _payloadTypeSeen = true;
                    break;
            }
            _pending = OversizedPendingProperty.None;
        }

        public OversizedRecordDiagnostic Complete()
        {
            if (_structureAmbiguous)
                return Create(OversizedRecordDisposition.UnsafeUnclassified, OversizedRecordKind.Unknown);
            return (_eventType, _payloadType, _payloadWasObject) switch
            {
                (OversizedEventType.SessionMeta, _, _) =>
                    Create(OversizedRecordDisposition.UnsafeCritical, OversizedRecordKind.SessionMetadata),
                (OversizedEventType.TurnContext, _, _) =>
                    Create(OversizedRecordDisposition.UnsafeCritical, OversizedRecordKind.TurnContext),
                (OversizedEventType.InterAgentCommunicationMetadata, _, _) =>
                    Create(OversizedRecordDisposition.UnsafeCritical, OversizedRecordKind.InterAgentCommunicationMetadata),
                (OversizedEventType.ResponseItem, OversizedPayloadType.AgentMessage, true) =>
                    Create(OversizedRecordDisposition.UnsafeCritical, OversizedRecordKind.ResponseItemAgentMessage),
                (OversizedEventType.ResponseItem, OversizedPayloadType.OpaqueResponseItem, true) =>
                    Create(OversizedRecordDisposition.SafeOpaqueSkipped, OversizedRecordKind.ResponseItemOpaque),
                (OversizedEventType.EventMessage, OversizedPayloadType.TokenCount, true) =>
                    Create(OversizedRecordDisposition.UnsafeCritical, OversizedRecordKind.TokenCount),
                (OversizedEventType.EventMessage, OversizedPayloadType.EventContext, true) =>
                    Create(OversizedRecordDisposition.UnsafeCritical, OversizedRecordKind.EventMessageContext),
                (OversizedEventType.EventMessage, OversizedPayloadType.ImageGenerationEnd, true) =>
                    Create(OversizedRecordDisposition.SafeOpaqueSkipped, OversizedRecordKind.ImageGenerationEnd),
                (OversizedEventType.EventMessage, OversizedPayloadType.McpToolCallEnd, true) =>
                    Create(OversizedRecordDisposition.SafeOpaqueSkipped, OversizedRecordKind.McpToolCallEnd),
                (OversizedEventType.Compacted, _, _) =>
                    Create(OversizedRecordDisposition.SafeOpaqueSkipped, OversizedRecordKind.Compacted),
                _ => Create(OversizedRecordDisposition.UnsafeUnclassified, OversizedRecordKind.Unknown),
            };
        }

        public OversizedRecordDiagnostic Malformed() =>
            Create(OversizedRecordDisposition.Malformed, OversizedRecordKind.Unknown);

        private OversizedRecordDiagnostic Create(
            OversizedRecordDisposition disposition,
            OversizedRecordKind kind) => new(stableLineNumber, recordByteLength, disposition, kind);
    }

    private enum OversizedPendingProperty { None, RootType, Payload, PayloadType }
    private enum OversizedEventType
    {
        Unknown,
        Ambiguous,
        Other,
        SessionMeta,
        TurnContext,
        InterAgentCommunicationMetadata,
        ResponseItem,
        EventMessage,
        Compacted,
    }
    private enum OversizedPayloadType
    {
        Unknown,
        Ambiguous,
        Other,
        AgentMessage,
        TokenCount,
        EventContext,
        OpaqueResponseItem,
        ImageGenerationEnd,
        McpToolCallEnd,
    }
}
