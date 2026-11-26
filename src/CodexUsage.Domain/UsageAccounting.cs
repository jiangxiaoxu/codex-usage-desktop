using System.Collections.Immutable;

namespace CodexUsage.Domain;

public static class UsageAccounting
{
    public const long LongContextInputTokenThreshold = 272_000;
    public const decimal FastModeCostMultiplier = 2.5m;
    public const decimal UltrafastModeCostMultiplier = 8m;
    public const string OtherModelCategory = "Others";
    public const string UnknownAttributionCategory = "Unknown attribution";
    private const decimal Million = 1_000_000m;
    private static readonly string[] SupportedFamilies =
        ["gpt-6.2-astra", "gpt-6.2-sol", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6", "gpt-5.5", "gpt-5.4"];
    private static readonly IReadOnlySet<string> LongContextEligibleModels = new HashSet<string>(StringComparer.Ordinal)
    {
        "gpt-6.1-terra",
        "gpt-6-astra",
        "gpt-6-sol",
        "gpt-6-luna",
        "gpt-5.6",
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna",
        "gpt-5.5",
        "gpt-5.4",
    };
    private static readonly IReadOnlyDictionary<string, ModelRate> Rates = new Dictionary<string, ModelRate>(StringComparer.Ordinal)
    {
        // Standard API prices for GPT-6.2, in USD per 1M tokens.
        ["gpt-6.2-astra"] = new(8m, 0.4m, 40m),
        ["gpt-6.2-sol"] = new(2m, 0.1m, 10m),
        // Standard API prices for GPT-6.1, in USD per 1M tokens.
        ["gpt-6.1-sol"] = new(2m, 0.1m, 10m),
        ["gpt-6.1-terra"] = new(1m, 0.05m, 5m),
        // Standard API prices for GPT-6, in USD per 1M tokens.
        ["gpt-6-astra"] = new(10m, 1m, 50m),
        ["gpt-6-sol"] = new(2m, 0.2m, 10m),
        ["gpt-6-luna"] = new(0.1m, 0.01m, 0.5m),
        ["gpt-5.6"] = new(5m, 0.5m, 30m),
        ["gpt-5.6-sol"] = new(5m, 0.5m, 30m),
        // Standard API prices from the 2026-07-30 price-performance announcement, in USD per 1M tokens.
        ["gpt-5.6-terra"] = new(2m, 0.2m, 12m),
        ["gpt-5.6-luna"] = new(0.2m, 0.02m, 1.2m),
        ["gpt-5.5"] = new(5m, 0.5m, 30m),
        ["gpt-5.4"] = new(2.5m, 0.25m, 15m),
        ["gpt-5.4-mini"] = new(0.75m, 0.075m, 4.5m),
        ["gpt-5.4-nano"] = new(0.2m, 0.02m, 1.25m),
    };

    public static string ModelCategory(string sourceModel)
    {
        ArgumentNullException.ThrowIfNull(sourceModel);
        if (sourceModel == "unknown") return UnknownAttributionCategory;
        foreach (var family in SupportedFamilies)
        {
            if (sourceModel == family || (sourceModel.Length > family.Length && sourceModel[family.Length] == '-'
                && sourceModel.StartsWith(family, StringComparison.Ordinal))) return sourceModel;
        }
        return OtherModelCategory;
    }

    public static string NormalizedAgentRole(ThreadType threadType, string observedRole) =>
        threadType == ThreadType.Main ? "root" : observedRole;

    public static CostBreakdown CostFor(UsageEvent usageEvent) => CostFor(usageEvent, ModelCategory(usageEvent.Model));

    private static CostBreakdown CostFor(UsageEvent usageEvent, string category)
    {
        if (category is OtherModelCategory or UnknownAttributionCategory || !Rates.TryGetValue(usageEvent.Model, out var rate))
            return CostBreakdown.UnpricedZero;

        var isGpt62 = usageEvent.Model is "gpt-6.2-astra" or "gpt-6.2-sol";
        decimal? speedMultiplier = usageEvent.ServiceTier switch
        {
            ServiceTier.Fast => isGpt62 ? 2m : FastModeCostMultiplier,
            ServiceTier.Ultrafast when isGpt62 => 6m,
            ServiceTier.Ultrafast when usageEvent.Model is "gpt-6-astra" or "gpt-6.1-sol" => UltrafastModeCostMultiplier,
            ServiceTier.Ultrafast => null,
            _ => 1m,
        };
        if (speedMultiplier is not { } fastMultiplier)
            return CostBreakdown.UnpricedZero;

        var baselineUncached = (usageEvent.InputTokens - usageEvent.CachedInputTokens) * rate.Input / Million;
        var baselineCached = usageEvent.CachedInputTokens * rate.CachedInput / Million;
        var baselineReasoning = usageEvent.ReasoningOutputTokens * rate.Output / Million;
        var baselineOther = (usageEvent.OutputTokens - usageEvent.ReasoningOutputTokens) * rate.Output / Million;
        var baselineTotal = baselineUncached + baselineCached + baselineReasoning + baselineOther;
        var isLongContext = usageEvent.InputTokens > LongContextInputTokenThreshold
            && LongContextEligibleModels.Contains(usageEvent.Model);
        var inputMultiplier = isLongContext ? 2m : 1m;
        var outputMultiplier = isLongContext ? 1.5m : 1m;
        var longAdjustedTotal = (baselineUncached + baselineCached) * inputMultiplier
            + (baselineReasoning + baselineOther) * outputMultiplier;
        var uncached = baselineUncached * inputMultiplier * fastMultiplier;
        var cached = baselineCached * inputMultiplier * fastMultiplier;
        var reasoning = baselineReasoning * outputMultiplier * fastMultiplier;
        var other = baselineOther * outputMultiplier * fastMultiplier;
        var total = uncached + cached + reasoning + other;
        return new(uncached, cached, reasoning, other, total, baselineTotal,
            longAdjustedTotal - baselineTotal, total - longAdjustedTotal, true);
    }

    public static UsageSummary Summarize(IEnumerable<UsageEvent> events)
    {
        var summary = new SummaryAccumulator();
        foreach (var usageEvent in events)
            summary.Add(usageEvent, CostFor(usageEvent), checked(usageEvent.InputTokens + usageEvent.OutputTokens));
        return summary.Build();
    }

    public static QueryResult Query(
        IEnumerable<UsageEvent> events,
        ScanDiagnostics diagnostics,
        FilterSpec filter,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modelFilter = filter.Models?.ToHashSet(StringComparer.Ordinal);
        var subjectFilter = filter.Subjects?.Select(subject => (subject.ThreadType, subject.AgentRole)).ToHashSet();
        var summary = new SummaryAccumulator();
        var modelSummaries = new OrderedDictionary<string, SummaryAccumulator>(StringComparer.Ordinal);
        var roleSummaries = new OrderedDictionary<(ThreadType ThreadType, string AgentRole), (SummaryAccumulator Summary, HashSet<string> Threads)>();
        var modelFacets = new Dictionary<string, FacetAccumulator>(StringComparer.Ordinal);
        var subjectFacets = new Dictionary<(ThreadType ThreadType, string AgentRole), FacetAccumulator>();

        foreach (var usageEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (usageEvent.TimestampUtc < filter.StartUtc || usageEvent.TimestampUtc >= filter.EndUtc) continue;

            var model = ModelCategory(usageEvent.Model);
            var subject = (usageEvent.ThreadType, AgentRole: NormalizedAgentRole(usageEvent.ThreadType, usageEvent.AgentRole));
            var cost = CostFor(usageEvent, model);
            var canonical = checked(usageEvent.InputTokens + usageEvent.OutputTokens);

            // Facet totals describe the date scope before model and subject filtering.
            if (!modelFacets.TryGetValue(model, out var modelFacet))
                modelFacets[model] = modelFacet = new();
            if (!subjectFacets.TryGetValue(subject, out var subjectFacet))
                subjectFacets[subject] = subjectFacet = new();
            modelFacet.Add(canonical, cost.Total);
            subjectFacet.Add(canonical, cost.Total);

            if (modelFilter is not null && !modelFilter.Contains(model)) continue;
            if (subjectFilter is not null && !subjectFilter.Contains(subject)) continue;

            if (!modelSummaries.TryGetValue(model, out var modelSummary))
                modelSummaries[model] = modelSummary = new();
            if (!roleSummaries.TryGetValue(subject, out var role))
                roleSummaries[subject] = role = (new(), new(StringComparer.Ordinal));
            summary.Add(usageEvent, cost, canonical);
            modelSummary.Add(usageEvent, cost, canonical);
            role.Summary.Add(usageEvent, cost, canonical);
            role.Threads.Add(usageEvent.ThreadType == ThreadType.Main ? usageEvent.ConversationId : usageEvent.RolloutId);
        }

        var result = new QueryResult(
            summary.Build(),
            modelSummaries.Select(value => new GroupRow([value.Key], value.Value.Build()))
                .OrderByDescending(value => value.Summary.Cost.Total).ToImmutableArray(),
            roleSummaries.Select(value => new RoleUsageRow(value.Key.ThreadType, value.Key.AgentRole,
                    value.Value.Threads.Count, value.Value.Summary.Build()))
                .OrderByDescending(value => value.Summary.Cost.Total).ToImmutableArray(),
            new(
                modelFacets.OrderBy(value => value.Key, StringComparer.Ordinal)
                    .Select(value => new ModelFacetOption(value.Key, value.Value.Tokens, value.Value.Cost)).ToImmutableArray(),
                subjectFacets.OrderBy(value => ThreadTypeText(value.Key.ThreadType), StringComparer.Ordinal)
                    .ThenBy(value => value.Key.AgentRole, StringComparer.Ordinal)
                    .Select(value => new SubjectFacetOption(new(value.Key.ThreadType, value.Key.AgentRole),
                        value.Value.Tokens, value.Value.Cost)).ToImmutableArray()),
            diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public static string ThreadTypeText(ThreadType threadType) => threadType switch
    {
        ThreadType.Main => "main",
        ThreadType.Subagent => "subagent",
        ThreadType.GuardianReview => "guardian_review",
        _ => "unknown",
    };

    private sealed record ModelRate(decimal Input, decimal CachedInput, decimal Output);

    private sealed class FacetAccumulator
    {
        public long Tokens { get; private set; }
        public decimal Cost { get; private set; }

        public void Add(long tokens, decimal cost)
        {
            Tokens = checked(Tokens + tokens);
            Cost += cost;
        }
    }

    private sealed class SummaryAccumulator
    {
        private int _calls, _fastCalls, _unknownServiceTierCalls;
        private long _input, _cached, _output, _reasoning, _unpriced, _fastTokens, _unknownServiceTierTokens;
        private decimal _uncachedCost, _cachedCost, _reasoningCost, _otherCost, _totalCost, _baselineCost, _longContextPremium, _fastModePremium;

        public void Add(UsageEvent usageEvent, CostBreakdown cost, long canonical)
        {
            checked
            {
                _calls++;
                _input += usageEvent.InputTokens;
                _cached += usageEvent.CachedInputTokens;
                _output += usageEvent.OutputTokens;
                _reasoning += usageEvent.ReasoningOutputTokens;
                if (!cost.Priced) _unpriced += canonical;
                if (usageEvent.ServiceTier is ServiceTier.Fast or ServiceTier.Ultrafast)
                {
                    _fastCalls++;
                    _fastTokens += canonical;
                }
                else if (usageEvent.ServiceTier == ServiceTier.Unknown)
                {
                    _unknownServiceTierCalls++;
                    _unknownServiceTierTokens += canonical;
                }
            }
            _uncachedCost += cost.UncachedInput;
            _cachedCost += cost.CachedInput;
            _reasoningCost += cost.ReasoningOutput;
            _otherCost += cost.OtherOutput;
            _totalCost += cost.Total;
            _baselineCost += cost.BaselineTotal;
            _longContextPremium += cost.LongContextPremium;
            _fastModePremium += cost.FastModePremium;
        }

        public UsageSummary Build() => new(
            _calls, _input, _cached, checked(_input - _cached), _output, _reasoning, checked(_output - _reasoning),
            checked(_input + _output), _unpriced,
            new(_uncachedCost, _cachedCost, _reasoningCost, _otherCost, _totalCost, _baselineCost, _longContextPremium, _fastModePremium, true),
            _fastCalls, _fastTokens, _unknownServiceTierCalls, _unknownServiceTierTokens);
    }
}
