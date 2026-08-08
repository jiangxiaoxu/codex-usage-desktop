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

    [Fact]
    public void TerraAndLunaUseCurrentRatesForAllUsage()
    {
        var terra = UsageAccounting.CostFor(Event with { Model = "gpt-5.6-terra" });
        Assert.Equal(0.4m, terra.UncachedInput);
        Assert.Equal(0.16m, terra.CachedInput);
        Assert.Equal(0.84m, terra.ReasoningOutput);
        Assert.Equal(0.36m, terra.OtherOutput);
        Assert.Equal(1.76m, terra.Total);

        var luna = UsageAccounting.CostFor(Event with { Model = "gpt-5.6-luna" });
        Assert.Equal(0.04m, luna.UncachedInput);
        Assert.Equal(0.016m, luna.CachedInput);
        Assert.Equal(0.084m, luna.ReasoningOutput);
        Assert.Equal(0.036m, luna.OtherOutput);
        Assert.Equal(0.176m, luna.Total);

        Assert.Equal(0.544002m, UsageAccounting.CostFor(Event with
        {
            Model = "gpt-5.6-terra",
            InputTokens = 272_001,
            CachedInputTokens = 0,
        }).UncachedInput);
        Assert.Equal(0.0544002m, UsageAccounting.CostFor(Event with
        {
            Model = "gpt-5.6-luna",
            InputTokens = 272_001,
            CachedInputTokens = 0,
        }).UncachedInput);
    }

    [Theory]
    [InlineData("gpt-5.4-mini", "gpt-5.4-mini")]
    [InlineData("gpt-5.5", "gpt-5.5")]
    [InlineData("gpt-5.6-preview", "gpt-5.6-preview")]
    [InlineData("codex-auto-review", "codex-auto-review")]
    [InlineData("codex-auto-review-preview", "Others")]
    [InlineData("unknown", "Unknown attribution")]
    [InlineData("Unknown", "Others")]
    [InlineData("gpt-5.60-preview", "Others")]
    [InlineData("GPT-5.6-sol", "Others")]
    public void ModelCategoryMatchesExactCaseSensitiveFamilyRules(string model, string expected) =>
        Assert.Equal(expected, UsageAccounting.ModelCategory(model));

    [Fact]
    public void OthersIsIntentionallyZeroPricedButAutoReviewUnknownAndUnsupportedVariantsAreUnpriced()
    {
        var other = Event with { Model = "o3" };
        var autoReview = Event with { Model = "codex-auto-review" };
        var unknown = Event with { Model = "unknown" };
        var variant = Event with { Model = "gpt-5.6-preview" };
        Assert.True(UsageAccounting.CostFor(other).Priced);
        Assert.Equal(CostBreakdown.UnpricedZero, UsageAccounting.CostFor(autoReview));
        Assert.False(UsageAccounting.CostFor(unknown).Priced);
        Assert.False(UsageAccounting.CostFor(variant).Priced);
        Assert.Equal(2_200_000, UsageAccounting.Summarize([other, unknown, variant]).UnpricedTokens);
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
        Assert.Equal(4, result.Facets.Models.Length);
        Assert.DoesNotContain(result.Facets.Models, value => value.Model == "gpt-5.4");
        Assert.Contains(result.Facets.Models, value => value.Model == "Others");
        Assert.Contains(result.Facets.Models, value =>
            value.Model == "codex-auto-review" && value.CanonicalTotalTokens == 1_100_000 && value.TotalCost == 0);
        Assert.Contains(result.Facets.Models, value => value.Model == "Unknown attribution");

        var autoReviewOnly = UsageAccounting.Query(
            events,
            ScanDiagnostics.Empty,
            Filter with { Models = ["codex-auto-review"] });
        Assert.Single(autoReviewOnly.ByModel);
        Assert.Equal("codex-auto-review", autoReviewOnly.ByModel[0].Key[0]);
        Assert.Equal(1_100_000, autoReviewOnly.Summary.CanonicalTotalTokens);
        Assert.Equal(0, autoReviewOnly.Summary.Cost.Total);
    }

}
