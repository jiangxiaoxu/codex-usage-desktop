using System.Collections.Immutable;
using CodexUsage.Domain;
using Xunit;

namespace CodexUsage.Domain.Tests;

public sealed class UsageAccountingTests
{
    private static readonly UsageEvent Event = new(
        "2026-07-15T01:00:00.000Z", 0, "conversation", "rollout", "", ThreadType.Main, "main", "/root", "",
        "gpt-5.6-sol", 1_000_000, 800_000, 100_000, 70_000);

    private static readonly FilterSpec Filter = new(
        DateTimeOffset.Parse("2026-07-15T00:00:00.000Z"), DateTimeOffset.Parse("2026-07-16T00:00:00.000Z"), null, null, "");

    [Fact]
    public void ReasoningOutputIsSubsetAndRatesNeverApplyLongContextPremium()
    {
        var cost = UsageAccounting.CostFor(Event);
        Assert.Equal(1m, cost.UncachedInput);
        Assert.Equal(0.4m, cost.CachedInput);
        Assert.Equal(2.1m, cost.ReasoningOutput);
        Assert.Equal(0.9m, cost.OtherOutput);
        Assert.Equal(4.4m, cost.Total);
        Assert.Equal(1_100_000, UsageAccounting.Summarize([Event]).CanonicalTotalTokens);

        Assert.Equal(2.5m, UsageAccounting.CostFor(Event with { Model = "gpt-5.4", CachedInputTokens = 0 }).UncachedInput);
        Assert.Equal(1.2m, UsageAccounting.CostFor(Event with { Model = "gpt-5.4-mini", CachedInputTokens = 0 }).Total);
        Assert.Equal(0.325m, UsageAccounting.CostFor(Event with { Model = "gpt-5.4-nano", CachedInputTokens = 0 }).Total);
        Assert.Equal(1.360005m, UsageAccounting.CostFor(Event with { Model = "gpt-5.5", InputTokens = 272_001, CachedInputTokens = 0 }).UncachedInput);
        Assert.Equal(cost, UsageAccounting.CostFor(Event with { Model = "gpt-5.6" }));
    }

    [Theory]
    [InlineData("gpt-5.4-mini", "gpt-5.4-mini")]
    [InlineData("gpt-5.5", "gpt-5.5")]
    [InlineData("gpt-5.6-preview", "gpt-5.6-preview")]
    [InlineData("unknown", "Unknown attribution")]
    [InlineData("Unknown", "Others")]
    [InlineData("gpt-5.60-preview", "Others")]
    [InlineData("GPT-5.6-sol", "Others")]
    public void ModelCategoryMatchesExactCaseSensitiveFamilyRules(string model, string expected) =>
        Assert.Equal(expected, UsageAccounting.ModelCategory(model));

    [Fact]
    public void OthersIsIntentionallyZeroPricedButUnknownAndUnsupportedVariantsAreUnpriced()
    {
        var other = Event with { Model = "o3" };
        var unknown = Event with { Model = "unknown" };
        var variant = Event with { Model = "gpt-5.6-preview" };
        Assert.True(UsageAccounting.CostFor(other).Priced);
        Assert.False(UsageAccounting.CostFor(unknown).Priced);
        Assert.False(UsageAccounting.CostFor(variant).Priced);
        Assert.Equal(2_200_000, UsageAccounting.Summarize([other, unknown, variant]).UnpricedTokens);
    }

    [Fact]
    public void FiltersDistinguishNullFromEmptyAndNormalizeOnlyMainRoles()
    {
        var events = new[]
        {
            Event,
            Event with { RolloutId = "worker", ThreadType = ThreadType.Subagent, AgentRole = "worker", Model = "gpt-5.6-terra" },
            Event with { RolloutId = "main-worker", ThreadType = ThreadType.Main, AgentRole = "worker", Model = "gpt-5.6-terra" },
            Event with { RolloutId = "sub-main", ThreadType = ThreadType.Subagent, AgentRole = "main", Model = "gpt-5.6-luna" },
        };
        Assert.Equal(4, UsageAccounting.Query(events, ScanDiagnostics.Empty, Filter).Summary.Calls);
        Assert.Equal(0, UsageAccounting.Query(events, ScanDiagnostics.Empty, Filter with { Models = ImmutableArray<string>.Empty }).Summary.Calls);
        Assert.Equal(0, UsageAccounting.Query(events, ScanDiagnostics.Empty, Filter with { Subjects = ImmutableArray<SubjectFilter>.Empty }).Summary.Calls);
        var selected = Filter with
        {
            Subjects = [new(ThreadType.Main, "root"), new(ThreadType.Subagent, "worker")],
        };
        Assert.Equal(3, UsageAccounting.Query(events, ScanDiagnostics.Empty, selected).Summary.Calls);
        Assert.Equal("root", UsageAccounting.NormalizedAgentRole(ThreadType.Main, "worker"));
        Assert.Equal("main", UsageAccounting.NormalizedAgentRole(ThreadType.Subagent, "main"));
    }

    [Fact]
    public void FacetsUseOnlyDateRangeAndIgnoreActiveSelections()
    {
        var events = new[]
        {
            Event,
            Event with { RolloutId = "other", Model = "o3" },
            Event with { RolloutId = "unknown", Model = "unknown" },
            Event with { RolloutId = "outside", TimestampUtc = "2026-07-16T00:00:00.000Z", Model = "gpt-5.4" },
        };
        var result = UsageAccounting.Query(events, ScanDiagnostics.Empty, Filter with { Models = ["gpt-5.6-sol"] });
        Assert.Single(result.ByModel);
        Assert.Equal(3, result.Facets.Models.Length);
        Assert.DoesNotContain(result.Facets.Models, value => value.Model == "gpt-5.4");
        Assert.Contains(result.Facets.Models, value => value.Model == "Others");
        Assert.Contains(result.Facets.Models, value => value.Model == "Unknown attribution");
    }

    [Fact]
    public void CsvPreservesSourceModelAndProtectsSpreadsheetFormulaFields()
    {
        var csv = UsageAccounting.CsvRows([
            Event,
            Event with { RolloutId = "=formula", TokenEventOrdinal = 1, Model = "o3" },
        ], Filter);
        Assert.StartsWith("\uFEFFtimestamp_sgt", csv, StringComparison.Ordinal);
        Assert.Contains("\"2026-07-15T09:00:00+08:00\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"gpt-5.6-sol\",\"gpt-5.6-sol\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"Others\",\"o3\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"'=formula\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"4.4\"", csv, StringComparison.Ordinal);
    }
}
