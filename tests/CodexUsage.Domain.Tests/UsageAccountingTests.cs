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
        DateTimeOffset.Parse("2026-07-15T00:00:00.000Z"), DateTimeOffset.Parse("2026-07-16T00:00:00.000Z"), null, null);

    [Fact]
    public void LongContextPricingAppliesToTheFullRequestWithoutDoubleChargingReasoning()
    {
        var cost = UsageAccounting.CostFor(Event);
        Assert.Equal(2m, cost.UncachedInput);
        Assert.Equal(0.8m, cost.CachedInput);
        Assert.Equal(3.15m, cost.ReasoningOutput);
        Assert.Equal(1.35m, cost.OtherOutput);
        Assert.Equal(7.3m, cost.Total);
        Assert.Equal(4.4m, cost.BaselineTotal);
        Assert.Equal(2.9m, cost.LongContextPremium);
        Assert.Equal(73m / 44m, cost.ActualToBaselineMultiplier);
        Assert.Equal(1_100_000, UsageAccounting.Summarize([Event]).CanonicalTotalTokens);

        Assert.Equal(5m, UsageAccounting.CostFor(Event with { Model = "gpt-5.4", CachedInputTokens = 0 }).UncachedInput);
        Assert.Equal(cost, UsageAccounting.CostFor(Event with { Model = "gpt-5.6" }));
    }

    [Theory]
    [InlineData("gpt-5.4-mini", 1.2)]
    [InlineData("gpt-5.4-nano", 0.325)]
    public void LongContextPricingDoesNotApplyToModelsWithoutLongContextRates(string model, decimal expectedTotal)
    {
        var cost = UsageAccounting.CostFor(Event with { Model = model, CachedInputTokens = 0 });

        Assert.Equal(expectedTotal, cost.Total);
        Assert.Equal(cost.Total, cost.BaselineTotal);
        Assert.Equal(0m, cost.LongContextPremium);
        Assert.Equal(1m, cost.ActualToBaselineMultiplier);
    }

    [Fact]
    public void LongContextPricingStartsOnlyAboveTheInputThreshold()
    {
        var atThreshold = UsageAccounting.CostFor(Event with
        {
            Model = "gpt-5.5",
            InputTokens = UsageAccounting.LongContextInputTokenThreshold,
            CachedInputTokens = 0,
        });
        var aboveThreshold = UsageAccounting.CostFor(Event with
        {
            Model = "gpt-5.5",
            InputTokens = UsageAccounting.LongContextInputTokenThreshold + 1,
            CachedInputTokens = 0,
        });

        Assert.Equal(4.36m, atThreshold.Total);
        Assert.Equal(atThreshold.Total, atThreshold.BaselineTotal);
        Assert.Equal(0m, atThreshold.LongContextPremium);
        Assert.Equal(1m, atThreshold.ActualToBaselineMultiplier);
        Assert.Equal(2.72001m, aboveThreshold.UncachedInput);
        Assert.Equal(3.15m, aboveThreshold.ReasoningOutput);
        Assert.Equal(1.35m, aboveThreshold.OtherOutput);
        Assert.Equal(7.22001m, aboveThreshold.Total);
        Assert.Equal(4.360005m, aboveThreshold.BaselineTotal);
        Assert.Equal(2.860005m, aboveThreshold.LongContextPremium);
        Assert.Equal(aboveThreshold.Total / aboveThreshold.BaselineTotal, aboveThreshold.ActualToBaselineMultiplier);
    }

    [Fact]
    public void TerraAndLunaUseLongContextRatesForAllPricedUsage()
    {
        var terra = UsageAccounting.CostFor(Event with { Model = "gpt-5.6-terra" });
        Assert.Equal(0.8m, terra.UncachedInput);
        Assert.Equal(0.32m, terra.CachedInput);
        Assert.Equal(1.26m, terra.ReasoningOutput);
        Assert.Equal(0.54m, terra.OtherOutput);
        Assert.Equal(2.92m, terra.Total);
        Assert.Equal(1.76m, terra.BaselineTotal);
        Assert.Equal(1.16m, terra.LongContextPremium);

        var luna = UsageAccounting.CostFor(Event with { Model = "gpt-5.6-luna" });
        Assert.Equal(0.08m, luna.UncachedInput);
        Assert.Equal(0.032m, luna.CachedInput);
        Assert.Equal(0.126m, luna.ReasoningOutput);
        Assert.Equal(0.054m, luna.OtherOutput);
        Assert.Equal(0.292m, luna.Total);
        Assert.Equal(0.176m, luna.BaselineTotal);
        Assert.Equal(0.116m, luna.LongContextPremium);

        Assert.Equal(1.088004m, UsageAccounting.CostFor(Event with
        {
            Model = "gpt-5.6-terra",
            InputTokens = 272_001,
            CachedInputTokens = 0,
        }).UncachedInput);
        Assert.Equal(0.1088004m, UsageAccounting.CostFor(Event with
        {
            Model = "gpt-5.6-luna",
            InputTokens = 272_001,
            CachedInputTokens = 0,
        }).UncachedInput);
    }

    [Fact]
    public void SummaryAggregatesMixedRequestsWithoutPricingOthersOrUnknownAttribution()
    {
        var shortRequest = Event with
        {
            InputTokens = UsageAccounting.LongContextInputTokenThreshold,
            CachedInputTokens = 0,
        };
        var mixed = UsageAccounting.Summarize(
        [
            Event,
            shortRequest,
            Event with { Model = "o3" },
            Event with { Model = "unknown" },
        ]);
        var zeroBaseline = UsageAccounting.Summarize(
        [
            Event with { Model = "o3" },
            Event with { Model = "unknown" },
        ]);

        Assert.Equal(8.76m, mixed.Cost.BaselineTotal);
        Assert.Equal(11.66m, mixed.Cost.Total);
        Assert.Equal(2.9m, mixed.Cost.LongContextPremium);
        Assert.Equal(mixed.Cost.Total / mixed.Cost.BaselineTotal, mixed.Cost.ActualToBaselineMultiplier);
        Assert.Equal(2_200_000, mixed.UnpricedTokens);
        Assert.Equal(0m, zeroBaseline.Cost.BaselineTotal);
        Assert.Equal(0m, zeroBaseline.Cost.Total);
        Assert.Equal(0m, zeroBaseline.Cost.LongContextPremium);
        Assert.Null(zeroBaseline.Cost.ActualToBaselineMultiplier);
        Assert.Equal(2_200_000, zeroBaseline.UnpricedTokens);
    }

    [Theory]
    [InlineData("gpt-5.4-mini", "gpt-5.4-mini")]
    [InlineData("gpt-5.5", "gpt-5.5")]
    [InlineData("gpt-5.6-preview", "gpt-5.6-preview")]
    [InlineData("codex-auto-review", "Others")]
    [InlineData("codex-auto-review-preview", "Others")]
    [InlineData("unknown", "Unknown attribution")]
    [InlineData("Unknown", "Others")]
    [InlineData("gpt-5.60-preview", "Others")]
    [InlineData("GPT-5.6-sol", "Others")]
    public void ModelCategoryMatchesExactCaseSensitiveFamilyRules(string model, string expected) =>
        Assert.Equal(expected, UsageAccounting.ModelCategory(model));

    [Fact]
    public void OthersAutoReviewUnknownAndUnsupportedVariantsAreUnpriced()
    {
        var other = Event with { Model = "o3" };
        var autoReview = Event with { Model = "codex-auto-review" };
        var unknown = Event with { Model = "unknown" };
        var variant = Event with { Model = "gpt-5.6-preview" };
        Assert.False(UsageAccounting.CostFor(other).Priced);
        Assert.Equal(CostBreakdown.UnpricedZero, UsageAccounting.CostFor(autoReview));
        Assert.False(UsageAccounting.CostFor(unknown).Priced);
        Assert.False(UsageAccounting.CostFor(variant).Priced);
        Assert.Equal(3_300_000, UsageAccounting.Summarize([other, unknown, variant]).UnpricedTokens);
        Assert.Equal(1_100_000, UsageAccounting.Summarize([autoReview]).CanonicalTotalTokens);
        Assert.Equal(1_100_000, UsageAccounting.Summarize([autoReview]).UnpricedTokens);
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
    public void RoleRowsCountDistinctSelectedThreadIdentifiers()
    {
        var events = new[]
        {
            Event with { ConversationId = "main-conversation", RolloutId = "main-rollout-1" },
            Event with { ConversationId = "main-conversation", RolloutId = "main-rollout-2" },
            Event with { ConversationId = "filtered-main", RolloutId = "main-rollout-3", Model = "gpt-5.6-terra" },
            Event with { ConversationId = "parent", RolloutId = "child-a", ThreadType = ThreadType.Subagent, AgentRole = "worker" },
            Event with { ConversationId = "parent", RolloutId = "child-a", ThreadType = ThreadType.Subagent, AgentRole = "worker" },
            Event with { ConversationId = "parent", RolloutId = "child-b", ThreadType = ThreadType.Subagent, AgentRole = "worker" },
            Event with { ConversationId = "parent", RolloutId = "unknown-child", ThreadType = ThreadType.Unknown, AgentRole = "unknown" },
            Event with { ConversationId = "parent", RolloutId = "unknown-child", ThreadType = ThreadType.Unknown, AgentRole = "unknown" },
        };

        var result = UsageAccounting.Query(events, ScanDiagnostics.Empty, Filter with { Models = ["gpt-5.6-sol"] });
        var main = Assert.Single(result.ByRole.Where(row => row.ThreadType == ThreadType.Main));
        var worker = Assert.Single(result.ByRole.Where(row => row.ThreadType == ThreadType.Subagent && row.AgentRole == "worker"));
        var unknown = Assert.Single(result.ByRole.Where(row => row.ThreadType == ThreadType.Unknown));

        Assert.Equal(1, main.ThreadCount);
        Assert.Equal(2, main.Summary.Calls);
        Assert.Equal(2, worker.ThreadCount);
        Assert.Equal(3, worker.Summary.Calls);
        Assert.Equal(1, unknown.ThreadCount);
        Assert.Equal(2, unknown.Summary.Calls);
    }

    [Fact]
    public void FacetsUseOnlyDateRangeAndIgnoreActiveSelections()
    {
        var events = new[]
        {
            Event,
            Event with { RolloutId = "other", Model = "o3" },
            Event with { RolloutId = "auto-review", Model = "codex-auto-review" },
            Event with { RolloutId = "unknown", Model = "unknown" },
            Event with { RolloutId = "outside", TimestampUtc = "2026-07-16T00:00:00.000Z", Model = "gpt-5.4" },
        };
        var result = UsageAccounting.Query(events, ScanDiagnostics.Empty, Filter with { Models = ["gpt-5.6-sol"] });
        Assert.Single(result.ByModel);
        Assert.Equal(3, result.Facets.Models.Length);
        Assert.DoesNotContain(result.Facets.Models, value => value.Model == "gpt-5.4");
        Assert.Contains(result.Facets.Models, value =>
            value.Model == "Others" && value.CanonicalTotalTokens == 2_200_000 && value.TotalCost == 0);
        Assert.Contains(result.Facets.Models, value => value.Model == "Unknown attribution");

        var othersOnly = UsageAccounting.Query(
            events,
            ScanDiagnostics.Empty,
            Filter with { Models = ["Others"] });
        Assert.Single(othersOnly.ByModel);
        Assert.Equal("Others", othersOnly.ByModel[0].Key[0]);
        Assert.Equal(2_200_000, othersOnly.Summary.CanonicalTotalTokens);
        Assert.Equal(0, othersOnly.Summary.Cost.Total);
        Assert.Equal(2_200_000, othersOnly.Summary.UnpricedTokens);
    }

}
