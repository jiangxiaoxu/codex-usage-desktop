using System.Collections.Immutable;
using CodexUsage.Domain;
using Xunit;

namespace CodexUsage.Domain.Tests;

public sealed class ModelActivityAccountingTests
{
    private static readonly FilterSpec Filter = new(
        DateTimeOffset.Parse("2026-10-03T00:00:00Z"), DateTimeOffset.Parse("2026-10-04T00:00:00Z"), null, null);

    private static ModelActivitySample Sample(ServiceTier tier, long bytes, long milliseconds) => new(
        "conversation", "rollout", "", ThreadType.Main, "main", "/root", "", "2026-10-03T01:00:00Z",
        0, "thread", "turn", Guid.NewGuid().ToString(), ModelActivityKind.TextOutput, "gpt-6.1-sol",
        ReasoningEffort.XHigh, tier, 1_000, 1_000 + milliseconds, bytes, bytes, 1);

    [Fact]
    public void WeightedRateAndMedianUseDifferentSampleWeightsAndPredictTheSameVolume()
    {
        var result = ModelActivityAccounting.Query([
            Sample(ServiceTier.Standard, 1_000, 1_000),
            Sample(ServiceTier.Standard, 6_000, 3_000),
            Sample(ServiceTier.Fast, 3_500, 1_000),
        ], Filter);
        var row = Assert.Single(result.Rows);

        Assert.Equal(2, row.Standard.SampleCount);
        Assert.Equal(1_750m, row.Standard.WeightedBytesPerSecond);
        Assert.Equal(1_500m, row.Standard.MedianBytesPerSecond);
        Assert.Equal(3_500m, row.Fast.MedianBytesPerSecond);
        Assert.Equal(2m, row.FastToStandardSpeedMultiplier);
        Assert.Equal(2m, result.EstimatedStandardActivitySeconds);
        Assert.Equal(1m, result.EstimatedSavedSeconds);
        Assert.Equal(2m, result.FastToStandardTimeMultiplier);
        Assert.Equal(50m, row.EstimatedTimeReductionPercent);
        Assert.Null(result.ReasoningEstimatedTimeReductionPercent);
    }

    [Fact]
    public void SavedModelActivityAddsReasoningAndTextAcrossParallelThreadsAndRetainsSlowdown()
    {
        var result = ModelActivityAccounting.Query([
            Sample(ServiceTier.Standard, 1_000, 1_000),
            Sample(ServiceTier.Fast, 1_000, 2_000),
            Sample(ServiceTier.Standard, 2_000, 4_000) with { Kind = ModelActivityKind.Reasoning },
            Sample(ServiceTier.Fast, 2_000, 1_000) with
            {
                Kind = ModelActivityKind.Reasoning, ThreadId = "parallel-child", ThreadType = ThreadType.Subagent,
            },
        ], Filter);

        Assert.Equal(-1m, result.Rows.Single(row => row.Kind == ModelActivityKind.TextOutput).EstimatedSavedSeconds);
        Assert.Equal(-100m, result.Rows.Single(row => row.Kind == ModelActivityKind.TextOutput).EstimatedTimeReductionPercent);
        Assert.Equal(3m, result.Rows.Single(row => row.Kind == ModelActivityKind.Reasoning).EstimatedSavedSeconds);
        Assert.Equal(3m, result.FastActivitySeconds);
        Assert.Equal(5m, result.EstimatedStandardActivitySeconds);
        Assert.Equal(2m, result.EstimatedSavedSeconds);
        Assert.Equal(2, result.ComparableFastSampleCount);
        Assert.Equal(75m, result.ReasoningEstimatedTimeReductionPercent);
    }

    [Fact]
    public void ReasoningReductionUsesExpectedTimeWeightsAndRetainsNegativeGroups()
    {
        var result = ModelActivityAccounting.Query([
            Sample(ServiceTier.Standard, 1_000, 1_000) with { Kind = ModelActivityKind.Reasoning },
            Sample(ServiceTier.Fast, 1_000, 2_000) with { Kind = ModelActivityKind.Reasoning },
            Sample(ServiceTier.Standard, 2_000, 4_000) with { Kind = ModelActivityKind.Reasoning, Effort = ReasoningEffort.High },
            Sample(ServiceTier.Fast, 2_000, 1_000) with { Kind = ModelActivityKind.Reasoning, Effort = ReasoningEffort.High },
        ], Filter);

        Assert.Equal(40m, result.ReasoningEstimatedTimeReductionPercent);
        Assert.Equal(-100m, result.Rows.Single(row => row.Effort == ReasoningEffort.XHigh).EstimatedTimeReductionPercent);
        Assert.Equal(75m, result.Rows.Single(row => row.Effort == ReasoningEffort.High).EstimatedTimeReductionPercent);
    }

    [Fact]
    public void BaselinesRequireExactModelEffortKindAndEstimatorRevision()
    {
        var standard = Sample(ServiceTier.Standard, 1_000, 1_000) with { Model = "model-a" };
        var fast = Sample(ServiceTier.Fast, 1_000, 500) with { Model = "model-a" };
        var result = ModelActivityAccounting.Query([
            standard, fast,
            fast with { Model = "model-b" },
            fast with { Effort = ReasoningEffort.High },
            fast with { Kind = ModelActivityKind.Reasoning },
            fast with { EstimatorRevision = 2 },
        ], Filter with { Models = [UsageAccounting.OtherModelCategory] });

        Assert.Equal(5, result.FastSampleCount);
        Assert.Equal(1, result.ComparableFastSampleCount);
        Assert.Equal(2.5m, result.FastActivitySeconds);
        Assert.Equal(0.5m, result.ComparableFastActivitySeconds);
        Assert.Equal(0.5m, result.EstimatedSavedSeconds);
        Assert.Equal(4, result.Rows.Count(row => row.EstimatedSavedSeconds is null));
    }

    [Theory]
    [InlineData("unknown", ReasoningEffort.XHigh)]
    [InlineData("gpt-6.1-sol", ReasoningEffort.Unknown)]
    public void UnknownAttributionNeverProvidesAComparableBaseline(string model, ReasoningEffort effort)
    {
        var result = ModelActivityAccounting.Query([
            Sample(ServiceTier.Standard, 1_000, 1_000) with { Model = model, Effort = effort },
            Sample(ServiceTier.Fast, 1_000, 500) with { Model = model, Effort = effort },
        ], Filter);

        Assert.Equal(1, result.FastSampleCount);
        Assert.Equal(0, result.ComparableFastSampleCount);
        Assert.Null(Assert.Single(result.Rows).FastToStandardSpeedMultiplier);
        Assert.Null(result.EstimatedSavedSeconds);
        Assert.Null(result.FastToStandardTimeMultiplier);
        Assert.Null(result.ReasoningEstimatedTimeReductionPercent);
    }

    [Fact]
    public void UnknownTierActivityIsVisibleButCannotReplaceStandardBaseline()
    {
        var result = ModelActivityAccounting.Query([
            Sample(ServiceTier.Unknown, 1_000, 1_000),
            Sample(ServiceTier.Fast, 1_000, 500),
        ], Filter);

        var row = Assert.Single(result.Rows);
        Assert.Equal(1, result.UnknownServiceTierSampleCount);
        Assert.Equal(1_000m, row.Unknown.WeightedBytesPerSecond);
        Assert.Equal(0, row.Standard.SampleCount);
        Assert.Null(row.Standard.WeightedBytesPerSecond);
        Assert.Null(result.EstimatedSavedSeconds);
    }

    [Fact]
    public void TimeModelAndNormalizedSubjectFiltersApplyBeforeBaselineConstruction()
    {
        var standard = Sample(ServiceTier.Standard, 1_000, 1_000);
        var fast = Sample(ServiceTier.Fast, 1_000, 500);
        var result = ModelActivityAccounting.Query([
            standard, fast,
            standard with { TimestampUtc = "2026-10-04T00:00:00Z" },
            standard with { TimestampUtc = "2026-10-02T23:59:59Z" },
            standard with { ThreadType = ThreadType.Subagent, AgentRole = "worker" },
            standard with { Model = "gpt-6-sol" },
            standard with { TimestampUtc = "invalid" },
        ], Filter with
        {
            Models = ["gpt-6.1-sol"],
            Subjects = [new SubjectFilter(ThreadType.Main, "root")],
        });

        var row = Assert.Single(result.Rows);
        Assert.Equal(1, row.Standard.SampleCount);
        Assert.Equal(1, row.Fast.SampleCount);
        Assert.Equal(0.5m, result.EstimatedSavedSeconds);
        Assert.Empty(ModelActivityAccounting.Query([standard], Filter with { Models = ImmutableArray<string>.Empty }).Rows);
    }

    [Fact]
    public void MissingVolumeAndInvalidDurationsAreUnavailableRatherThanZeroSpeedSamples()
    {
        var sample = Sample(ServiceTier.Fast, 1_000, 500);
        var result = ModelActivityAccounting.Query([
            sample with { EstimatedContentBytes = 0 },
            sample with { CompletedAtEpochMs = sample.StartedAtEpochMs },
            sample with { CompletedAtEpochMs = sample.StartedAtEpochMs - 1 },
            sample with { StartedAtEpochMs = -1 },
            sample with { CompletedAtEpochMs = long.MaxValue },
        ], Filter);

        Assert.Empty(result.Rows);
        Assert.Equal(0, result.FastSampleCount);
        Assert.Null(result.EstimatedSavedSeconds);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(866, 0)]
    [InlineData(867, 0)]
    [InlineData(868, 1)]
    [InlineData(1_000, 100)]
    [InlineData(long.MaxValue, 6_917_529_027_641_081_205)]
    public void ReasoningVolumeUsesVersionedCodexEstimateWithoutOverflow(long rawBytes, long expected)
    {
        Assert.Equal(expected, ModelActivityVolumeEstimator.EstimateReasoningContentBytes(rawBytes));
    }
}
