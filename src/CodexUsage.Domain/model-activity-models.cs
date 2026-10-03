using System.Collections.Immutable;

namespace CodexUsage.Domain;

public enum ModelActivityKind
{
    Reasoning,
    TextOutput,
}

public enum ReasoningEffort
{
    Unknown,
    None,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

public sealed record ParsedModelActivitySample(
    string ConversationId,
    string RolloutId,
    string ParentThreadId,
    ThreadType ThreadType,
    string AgentRole,
    string AgentPath,
    string AgentNickname,
    string TimestampUtc,
    long ActivityOrdinal,
    string ThreadId,
    string TurnId,
    string ItemId,
    ModelActivityKind Kind,
    string Model,
    ReasoningEffort Effort,
    ServiceTier ServiceTier,
    long StartedAtEpochMs,
    long CompletedAtEpochMs,
    long RawPayloadBytes,
    long EstimatedContentBytes,
    int EstimatorRevision,
    string DeterministicSignature)
{
    public ModelActivitySample ToModelActivitySample() => new(
        ConversationId, RolloutId, ParentThreadId, ThreadType, AgentRole, AgentPath, AgentNickname,
        TimestampUtc, ActivityOrdinal, ThreadId, TurnId, ItemId, Kind, Model, Effort, ServiceTier,
        StartedAtEpochMs, CompletedAtEpochMs, RawPayloadBytes, EstimatedContentBytes, EstimatorRevision);
}

public sealed record ModelActivitySample(
    string ConversationId,
    string RolloutId,
    string ParentThreadId,
    ThreadType ThreadType,
    string AgentRole,
    string AgentPath,
    string AgentNickname,
    string TimestampUtc,
    long ActivityOrdinal,
    string ThreadId,
    string TurnId,
    string ItemId,
    ModelActivityKind Kind,
    string Model,
    ReasoningEffort Effort,
    ServiceTier ServiceTier,
    long StartedAtEpochMs,
    long CompletedAtEpochMs,
    long RawPayloadBytes,
    long EstimatedContentBytes,
    int EstimatorRevision);

public sealed record ModelActivityRateSummary(
    int SampleCount,
    long EstimatedContentBytes,
    decimal ActivitySeconds,
    decimal? WeightedBytesPerSecond,
    decimal? MedianBytesPerSecond)
{
    public static ModelActivityRateSummary Empty { get; } = new(0, 0, 0, null, null);
}

public sealed record ModelActivityComparisonRow(
    string Model,
    ReasoningEffort Effort,
    ModelActivityKind Kind,
    int EstimatorRevision,
    ModelActivityRateSummary Standard,
    ModelActivityRateSummary Fast,
    ModelActivityRateSummary Unknown,
    decimal? FastToStandardSpeedMultiplier,
    decimal? EstimatedStandardActivitySeconds,
    decimal? EstimatedSavedSeconds,
    int ComparableFastSampleCount,
    decimal? EstimatedTimeReductionPercent);

public sealed record ModelActivityQueryResult(
    ImmutableArray<ModelActivityComparisonRow> Rows,
    int FastSampleCount,
    decimal FastActivitySeconds,
    int ComparableFastSampleCount,
    decimal ComparableFastActivitySeconds,
    int UnknownServiceTierSampleCount,
    decimal? EstimatedStandardActivitySeconds,
    decimal? EstimatedSavedSeconds,
    decimal? FastToStandardTimeMultiplier,
    decimal? ReasoningEstimatedTimeReductionPercent)
{
    public static ModelActivityQueryResult Empty { get; } = new([], 0, 0, 0, 0, 0, null, null, null, null);
}
