using System.Collections.Immutable;

namespace CodexUsage.Domain;

public enum ThreadType
{
    Main,
    Subagent,
    GuardianReview,
    Unknown,
}

public sealed record SubjectFilter(ThreadType ThreadType, string AgentRole);

public sealed record UsageEvent(
    string TimestampUtc,
    long TokenEventOrdinal,
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
    long ReasoningOutputTokens);

public sealed record ScanDiagnostics(
    int FilesScanned,
    int MalformedLines,
    int DuplicateSnapshotsSkipped,
    int ZeroBreakdownSnapshotsSkipped,
    int InvalidTokenRelationshipsSkipped)
{
    public static ScanDiagnostics Empty { get; } = new(0, 0, 0, 0, 0);
}

public sealed record FilterSpec(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    ImmutableArray<string>? Models,
    ImmutableArray<SubjectFilter>? Subjects);

public sealed record CostBreakdown(
    decimal UncachedInput,
    decimal CachedInput,
    decimal ReasoningOutput,
    decimal OtherOutput,
    decimal Total,
    decimal BaselineTotal,
    decimal LongContextPremium,
    bool Priced)
{
    public decimal? ActualToBaselineMultiplier => BaselineTotal > 0 ? Total / BaselineTotal : null;

    public static CostBreakdown PricedZero { get; } = new(0, 0, 0, 0, 0, 0, 0, true);
    public static CostBreakdown UnpricedZero { get; } = new(0, 0, 0, 0, 0, 0, 0, false);
}

public sealed record UsageSummary(
    int Calls,
    long InputTokens,
    long CachedInputTokens,
    long UncachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long OtherOutputTokens,
    long CanonicalTotalTokens,
    long UnpricedTokens,
    CostBreakdown Cost);

public sealed record GroupRow(ImmutableArray<string> Key, UsageSummary Summary);
public sealed record RoleUsageRow(ThreadType ThreadType, string AgentRole, int ThreadCount, UsageSummary Summary);
public sealed record ModelFacetOption(string Model, long CanonicalTotalTokens, decimal TotalCost);
public sealed record SubjectFacetOption(SubjectFilter Subject, long CanonicalTotalTokens, decimal TotalCost);
public sealed record QueryFacets(ImmutableArray<ModelFacetOption> Models, ImmutableArray<SubjectFacetOption> Subjects);
public sealed record QueryResult(
    UsageSummary Summary,
    ImmutableArray<GroupRow> ByModel,
    ImmutableArray<RoleUsageRow> ByRole,
    ImmutableArray<GroupRow> ByAgent,
    QueryFacets Facets,
    ScanDiagnostics Diagnostics);
