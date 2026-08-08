using CodexUsage.Domain;

namespace CodexUsage.Infrastructure;

public enum PrefixStatus
{
    Unknown,
    Matches,
    Diverged,
}

public enum CanonicalStatus
{
    Candidate,
    Canonical,
    Conflict,
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public enum CollectorRunStatus
{
    Running,
    Succeeded,
    Failed,
}

public sealed record UsageEventInput(
    long TokenEventOrdinal,
    long TimestampEpochMs,
    string Model,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    string EventSignature);

public sealed record SourceFileInput(
    string FilePath,
    string? RolloutId,
    long SizeBytes,
    long ModifiedAtEpochMs,
    long ByteOffset,
    string PrefixHash,
    PrefixStatus PrefixStatus,
    CanonicalStatus CanonicalStatus,
    bool IsPresent,
    long LastScannedAtEpochMs,
    string? LastError);

public sealed record CandidateSourceInput(
    string FilePath,
    long SizeBytes,
    long ModifiedAtEpochMs,
    long ByteOffset,
    string PrefixHash,
    PrefixStatus PrefixStatus,
    CanonicalStatus CanonicalStatus,
    bool IsPresent,
    long LastScannedAtEpochMs,
    string? LastError);

public sealed record RecoverableCanonicalSourceInput(
    string FilePath,
    long SizeBytes,
    long ModifiedAtEpochMs,
    long ByteOffset,
    string PrefixHash,
    long LastScannedAtEpochMs);

public sealed record AppendRolloutSourceInput(
    RolloutMetadata Metadata,
    IReadOnlyList<UsageEventInput> Events,
    CandidateSourceInput Source,
    long ObservedAtEpochMs,
    RolloutCheckpointInput? Checkpoint = null);

public sealed record CanonicalSourceInput(
    string FilePath,
    long SizeBytes,
    long ModifiedAtEpochMs,
    long ByteOffset,
    string PrefixHash,
    PrefixStatus PrefixStatus,
    long LastScannedAtEpochMs,
    string? LastError);

public sealed record ReplaceCanonicalRolloutInput(
    RolloutMetadata Metadata,
    IReadOnlyList<UsageEventInput> Events,
    CanonicalSourceInput Source,
    long ObservedAtEpochMs,
    string? ResolvedConflictSourcePath,
    RolloutCheckpointInput? Checkpoint = null);

public sealed record RekeyLegacyCanonicalRolloutInput(
    string LegacyRolloutId,
    RolloutMetadata Metadata,
    IReadOnlyList<UsageEventInput> Events,
    CanonicalSourceInput Source,
    long ObservedAtEpochMs,
    RolloutCheckpointInput Checkpoint);

public sealed record RecoverDivergedCanonicalSourceInput(
    RolloutMetadata Metadata,
    IReadOnlyList<UsageEventInput> Events,
    RecoverableCanonicalSourceInput Source,
    long ObservedAtEpochMs,
    RolloutCheckpointInput? Checkpoint = null);

public enum SourceIdentityKind
{
    WindowsFileId,
    ConservativeStat,
}

public sealed record SourceIdentity(SourceIdentityKind Kind, string Value);

public sealed record RolloutCheckpointInput(
    string FilePath,
    string RolloutId,
    int CheckpointFormatRevision,
    int ParserRevision,
    SourceIdentity SourceIdentity,
    long ObservedSizeBytes,
    long ObservedModifiedAtEpochMs,
    long StableCompleteOffset,
    string BoundaryHash,
    string ParserStateJson,
    string ParserStateHash,
    long TrailingPartialBytes,
    int SafeOpaqueOversizedRecords,
    int SafeNullPaddingRecords,
    long LastVerifiedAtEpochMs);

public sealed record RolloutCheckpointRecord(
    string FilePath,
    string RolloutId,
    int CheckpointFormatRevision,
    int ParserRevision,
    SourceIdentity SourceIdentity,
    long ObservedSizeBytes,
    long ObservedModifiedAtEpochMs,
    long StableCompleteOffset,
    string BoundaryHash,
    string ParserStateJson,
    string ParserStateHash,
    long TrailingPartialBytes,
    int SafeOpaqueOversizedRecords,
    int SafeNullPaddingRecords,
    long LastVerifiedAtEpochMs);

public sealed record RolloutEventCursor(long EventCount, long NextTokenEventOrdinal);

public sealed record RolloutLedgerTail(
    long TokenEventOrdinal,
    long TimestampEpochMs,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens);

public sealed record AppendEventsResult(long Inserted, long IgnoredAsDuplicate);

public sealed record SourceFileRecord(
    string FilePath,
    string? RolloutId,
    long SizeBytes,
    long ModifiedAtEpochMs,
    long ByteOffset,
    string PrefixHash,
    PrefixStatus PrefixStatus,
    CanonicalStatus CanonicalStatus,
    bool IsPresent,
    long LastScannedAtEpochMs,
    string? LastError);

public sealed record StoredUsageEvent(
    DateTimeOffset TimestampUtc,
    string ConversationId,
    string RolloutId,
    string ParentThreadId,
    ThreadType ThreadType,
    string AgentRole,
    string AgentPath,
    string AgentNickname,
    string Model,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TokenEventOrdinal,
    long TimestampEpochMs,
    string EventSignature);

public sealed record UsageEventQuery(
    long StartEpochMs,
    long EndEpochMs,
    IReadOnlyList<string>? Models = null,
    IReadOnlyList<string>? AgentRoles = null,
    IReadOnlyList<ThreadType>? ThreadTypes = null,
    string? MainThreadConversationId = null);

public sealed record CollectorDiagnosticInput(
    string? RunId,
    string? SourceFilePath,
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? DetailsJson,
    long CreatedAtEpochMs);

public sealed record SourceConflictInput(
    string? RunId,
    string SourceFilePath,
    string Code,
    string Message,
    string? DetailsJson,
    long ObservedAtEpochMs);

public sealed record CollectorRunStartInput(string RunId, string Trigger, long StartedAtEpochMs);

public sealed record CollectorRunHeartbeatInput(
    string RunId,
    long HeartbeatAtEpochMs,
    IReadOnlyDictionary<string, string>? State = null);

public sealed record CollectorRunFinishInput(
    string RunId,
    CollectorRunStatus Status,
    long CompletedAtEpochMs,
    long FilesScanned,
    long EventsAdded,
    long DiagnosticsCount,
    string? ErrorMessage);

public sealed record CollectorRunRecord(
    string RunId,
    string Trigger,
    CollectorRunStatus Status,
    long StartedAtEpochMs,
    long HeartbeatAtEpochMs,
    long? CompletedAtEpochMs,
    long FilesScanned,
    long EventsAdded,
    long DiagnosticsCount,
    string? ErrorMessage);

public sealed record CheckpointResult(long Busy, long LogFrames, long CheckpointedFrames);
