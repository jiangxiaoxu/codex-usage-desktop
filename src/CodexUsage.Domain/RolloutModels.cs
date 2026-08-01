using System.Collections.Immutable;

namespace CodexUsage.Domain;

public sealed record RolloutMetadata(
    string ConversationId,
    string RolloutId,
    string ParentThreadId,
    ThreadType ThreadType,
    string AgentRole,
    string AgentPath,
    string AgentNickname,
    bool IsRealtimeVoice);

public sealed record ParsedRolloutUsageEvent(
    string ConversationId,
    string RolloutId,
    string ParentThreadId,
    ThreadType ThreadType,
    string AgentRole,
    string AgentPath,
    string AgentNickname,
    string TimestampUtc,
    long TokenEventOrdinal,
    string TurnId,
    string Model,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    string CumulativeSnapshot,
    string DeterministicSignature)
{
    public UsageEvent ToUsageEvent() => new(
        TimestampUtc, TokenEventOrdinal, ConversationId, RolloutId, ParentThreadId, ThreadType,
        AgentRole, AgentPath, AgentNickname, Model, InputTokens, CachedInputTokens, OutputTokens, ReasoningOutputTokens);
}

public sealed record RolloutParseDiagnostics(
    int BlankLines,
    int SafeNullPaddingRecordsSkipped,
    int MalformedLines,
    int NonObjectLines,
    ImmutableArray<OversizedRecordDiagnostic> OversizedRecords,
    int InvalidTokenUsageLines,
    int DuplicateSnapshotsSkipped,
    int ZeroBreakdownSnapshotsSkipped,
    int InvalidTokenRelationshipsSkipped,
    int InvalidTimestampsSkipped)
{
    public int SafeOpaqueOversizedRecordsSkipped => OversizedRecords.Count(value =>
        value.Disposition == OversizedRecordDisposition.SafeOpaqueSkipped);

    public bool HasUnsafeOversizedRecords => OversizedRecords.Any(value =>
        value.Disposition != OversizedRecordDisposition.SafeOpaqueSkipped);
}

public enum OversizedRecordDisposition
{
    SafeOpaqueSkipped,
    UnsafeCritical,
    UnsafeUnclassified,
    Malformed,
}

public enum OversizedRecordKind
{
    Unknown,
    SessionMetadata,
    TurnContext,
    InterAgentCommunicationMetadata,
    TokenCount,
    EventMessageContext,
    ResponseItemAgentMessage,
    ResponseItemOpaque,
    Compacted,
    ImageGenerationEnd,
    McpToolCallEnd,
}

public sealed record OversizedRecordDiagnostic(
    int StableLineNumber,
    int ByteLength,
    OversizedRecordDisposition Disposition,
    OversizedRecordKind Kind);

public enum ForkReplayStatus
{
    Inactive,
    AwaitingMainLiveTurn,
    Unproven,
    AwaitingTaskStarted,
    AwaitingTurnContext,
    AwaitingTrigger,
    AwaitingRecipient,
}

public sealed record RolloutForkReplayState(
    ForkReplayStatus Status,
    long? ForkBoundaryEpochMilliseconds = null,
    string? TurnId = null,
    string? Model = null)
{
    public static RolloutForkReplayState Inactive { get; } = new(ForkReplayStatus.Inactive);
}

public sealed record RolloutParserState(
    bool HasMetadata,
    RolloutMetadata Metadata,
    ImmutableDictionary<string, string> TurnModels,
    string CurrentTurnId,
    bool CurrentTurnModelOverridden,
    string CurrentModel,
    RolloutForkReplayState ForkReplay,
    string? PreviousSnapshot,
    long NextTokenEventOrdinal,
    ImmutableSortedSet<string> UnresolvedTurnIds,
    ImmutableSortedSet<string> ProvisionalTurnIds);

public sealed record RolloutChunkParseResult(
    RolloutMetadata Metadata,
    ImmutableArray<ParsedRolloutUsageEvent> Events,
    RolloutParseDiagnostics Diagnostics,
    RolloutParserState State,
    int StableLineCount,
    int StableByteLength,
    bool TrailingPartialLine);

public sealed record RolloutParseResult(
    RolloutMetadata Metadata,
    ImmutableArray<ParsedRolloutUsageEvent> Events,
    RolloutParseDiagnostics Diagnostics,
    int StableLineCount,
    bool TrailingPartialLine);

public sealed record CooperativeParseOptions(
    int MaxBytesPerSlice,
    int MaxRecordsPerSlice,
    TimeSpan MaxTimePerSlice,
    int MaximumRecordBytes,
    Func<CancellationToken, ValueTask> YieldControl);
