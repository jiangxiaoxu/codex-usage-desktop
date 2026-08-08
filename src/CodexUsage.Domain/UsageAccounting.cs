using System.Collections.Immutable;
using System.Globalization;

namespace CodexUsage.Domain;

public static class UsageAccounting
{
    public const string OtherModelCategory = "Others";
    public const string AutoReviewModelCategory = "codex-auto-review";
    public const string UnknownAttributionCategory = "Unknown attribution";
    private const decimal Million = 1_000_000m;
    private static readonly string[] SupportedFamilies = ["gpt-5.6", "gpt-5.5", "gpt-5.4"];
    private static readonly IReadOnlyDictionary<string, ModelRate> Rates = new Dictionary<string, ModelRate>(StringComparer.Ordinal)
    {
        ["gpt-5.6"] = new(5m, 0.5m, 30m),
        ["gpt-5.6-sol"] = new(5m, 0.5m, 30m),
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
        if (sourceModel == AutoReviewModelCategory) return AutoReviewModelCategory;
        return SupportedFamilies.Any(family => sourceModel == family || sourceModel.StartsWith(family + "-", StringComparison.Ordinal))
            ? sourceModel
            : OtherModelCategory;
    }

    public static string NormalizedAgentRole(ThreadType threadType, string observedRole) =>
        threadType == ThreadType.Main ? "root" : observedRole;

    public static CostBreakdown CostFor(UsageEvent usageEvent)
    {
        var category = ModelCategory(usageEvent.Model);
        if (category == OtherModelCategory) return CostBreakdown.PricedZero;
        if (category is AutoReviewModelCategory or UnknownAttributionCategory || !Rates.TryGetValue(usageEvent.Model, out var rate))
            return CostBreakdown.UnpricedZero;

        var uncached = (usageEvent.InputTokens - usageEvent.CachedInputTokens) * rate.Input / Million;
        var cached = usageEvent.CachedInputTokens * rate.CachedInput / Million;
        var reasoning = usageEvent.ReasoningOutputTokens * rate.Output / Million;
        var other = (usageEvent.OutputTokens - usageEvent.ReasoningOutputTokens) * rate.Output / Million;
        return new(uncached, cached, reasoning, other, uncached + cached + reasoning + other, true);
    }

    public static UsageSummary Summarize(IEnumerable<UsageEvent> events)
    {
        var calls = 0;
        long input = 0, cached = 0, output = 0, reasoning = 0, unpriced = 0;
        decimal uncachedCost = 0, cachedCost = 0, reasoningCost = 0, otherCost = 0, totalCost = 0;
        foreach (var usageEvent in events)
        {
            var cost = CostFor(usageEvent);
            var canonical = checked(usageEvent.InputTokens + usageEvent.OutputTokens);
            calls = checked(calls + 1);
            input = checked(input + usageEvent.InputTokens);
            cached = checked(cached + usageEvent.CachedInputTokens);
            output = checked(output + usageEvent.OutputTokens);
            reasoning = checked(reasoning + usageEvent.ReasoningOutputTokens);
            if (!cost.Priced) unpriced = checked(unpriced + canonical);
            uncachedCost += cost.UncachedInput;
            cachedCost += cost.CachedInput;
            reasoningCost += cost.ReasoningOutput;
            otherCost += cost.OtherOutput;
            totalCost += cost.Total;
        }

        return new(
            calls, input, cached, checked(input - cached), output, reasoning, checked(output - reasoning),
            checked(input + output), unpriced,
            new(uncachedCost, cachedCost, reasoningCost, otherCost, totalCost, true));
    }

    public static bool MatchesFilter(UsageEvent usageEvent, FilterSpec filter)
    {
        if (!TryTimestamp(usageEvent.TimestampUtc, out var timestamp) || timestamp < filter.StartUtc || timestamp >= filter.EndUtc)
            return false;
        if (filter.Models is { } models && !models.Contains(ModelCategory(usageEvent.Model), StringComparer.Ordinal))
            return false;
        var role = NormalizedAgentRole(usageEvent.ThreadType, usageEvent.AgentRole);
        if (filter.Subjects is { } subjects && !subjects.Any(subject => subject.ThreadType == usageEvent.ThreadType && subject.AgentRole == role))
            return false;
        return true;
    }

    public static QueryResult Query(IEnumerable<UsageEvent> events, ScanDiagnostics diagnostics, FilterSpec filter)
    {
        var dateScoped = events.Where(value => TryTimestamp(value.TimestampUtc, out var timestamp) && timestamp >= filter.StartUtc && timestamp < filter.EndUtc).ToArray();
        var selected = dateScoped.Where(value => MatchesFilter(value, filter)).ToArray();
        return new(
            Summarize(selected),
            Group(selected, value => [ModelCategory(value.Model)]),
            Group(selected, value => [ThreadTypeText(value.ThreadType), NormalizedAgentRole(value.ThreadType, value.AgentRole)]),
            Group(selected, value => [ThreadTypeText(value.ThreadType), NormalizedAgentRole(value.ThreadType, value.AgentRole), value.AgentPath, ModelCategory(value.Model)]),
            BuildFacets(dateScoped),
            diagnostics);
    }

    private static QueryFacets BuildFacets(IEnumerable<UsageEvent> events)
    {
        var models = events.GroupBy(value => ModelCategory(value.Model), StringComparer.Ordinal)
            .Select(group => new ModelFacetOption(group.Key, group.Sum(value => checked(value.InputTokens + value.OutputTokens)), group.Sum(value => CostFor(value).Total)))
            .OrderBy(value => value.Model, StringComparer.Ordinal).ToImmutableArray();
        var subjects = events.GroupBy(value => new SubjectFilter(value.ThreadType, NormalizedAgentRole(value.ThreadType, value.AgentRole)))
            .Select(group => new SubjectFacetOption(group.Key, group.Sum(value => checked(value.InputTokens + value.OutputTokens)), group.Sum(value => CostFor(value).Total)))
            .OrderBy(value => ThreadTypeText(value.Subject.ThreadType), StringComparer.Ordinal)
            .ThenBy(value => value.Subject.AgentRole, StringComparer.Ordinal).ToImmutableArray();
        return new(models, subjects);
    }

    private static ImmutableArray<GroupRow> Group(IEnumerable<UsageEvent> events, Func<UsageEvent, string[]> keySelector) =>
        events.GroupBy(value => string.Join("\u001f", keySelector(value)), StringComparer.Ordinal)
            .Select(group => new GroupRow(keySelector(group.First()).ToImmutableArray(), Summarize(group)))
            .OrderByDescending(value => value.Summary.Cost.Total).ToImmutableArray();

    private static bool TryTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);

    public static string ThreadTypeText(ThreadType threadType) => threadType switch
    {
        ThreadType.Main => "main",
        ThreadType.Subagent => "subagent",
        _ => "unknown",
    };

    private sealed record ModelRate(decimal Input, decimal CachedInput, decimal Output);
}
