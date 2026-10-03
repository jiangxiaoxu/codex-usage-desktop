using System.Collections.Immutable;
using System.Globalization;

namespace CodexUsage.Domain;

public static class ModelActivityAccounting
{
    public static ModelActivityQueryResult Query(
        IEnumerable<ModelActivitySample> samples, FilterSpec filter)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(filter);
        var selected = samples.Where(sample => IsValid(sample) && MatchesFilter(sample, filter)).ToArray();

        var rows = selected.GroupBy(sample => (sample.Model, sample.Effort, sample.Kind, sample.EstimatorRevision))
            .Select(group => Compare(group.Key.Model, group.Key.Effort, group.Key.Kind, group.Key.EstimatorRevision, group))
            .OrderBy(row => row.Model, StringComparer.Ordinal)
            .ThenBy(row => row.Effort)
            .ThenBy(row => row.Kind)
            .ThenBy(row => row.EstimatorRevision)
            .ToImmutableArray();
        var comparable = rows.Where(row => row.ComparableFastSampleCount > 0).ToArray();
        var comparableFastSeconds = comparable.Sum(row => row.Fast.ActivitySeconds);
        decimal? expectedSeconds = comparable.Length > 0
            ? comparable.Sum(row => row.EstimatedStandardActivitySeconds!.Value)
            : null;
        var comparableReasoning = comparable.Where(row => row.Kind == ModelActivityKind.Reasoning).ToArray();
        var expectedReasoningSeconds = comparableReasoning.Sum(row => row.EstimatedStandardActivitySeconds!.Value);
        decimal? reasoningReductionPercent = expectedReasoningSeconds > 0
            ? comparableReasoning.Sum(row => row.EstimatedSavedSeconds!.Value) / expectedReasoningSeconds * 100m
            : null;

        return new(
            rows,
            rows.Sum(row => row.Fast.SampleCount),
            rows.Sum(row => row.Fast.ActivitySeconds),
            comparable.Sum(row => row.ComparableFastSampleCount),
            comparableFastSeconds,
            rows.Sum(row => row.Unknown.SampleCount),
            expectedSeconds,
            expectedSeconds - comparableFastSeconds,
            comparableFastSeconds > 0 ? expectedSeconds / comparableFastSeconds : null,
            reasoningReductionPercent);
    }

    private static ModelActivityComparisonRow Compare(
        string model, ReasoningEffort effort, ModelActivityKind kind, int estimatorRevision,
        IEnumerable<ModelActivitySample> samples)
    {
        var values = samples.ToArray();
        var standard = Summarize(values.Where(sample => sample.ServiceTier == ServiceTier.Standard));
        var fast = Summarize(values.Where(sample => sample.ServiceTier == ServiceTier.Fast));
        var unknown = Summarize(values.Where(sample => sample.ServiceTier == ServiceTier.Unknown));
        var comparable = !string.IsNullOrWhiteSpace(model) && model != "unknown"
            && effort != ReasoningEffort.Unknown
            && standard.WeightedBytesPerSecond is > 0
            && fast.SampleCount > 0;
        decimal? expectedSeconds = comparable
            ? fast.EstimatedContentBytes / standard.WeightedBytesPerSecond!.Value
            : null;
        return new(model, effort, kind, estimatorRevision, standard, fast, unknown,
            comparable ? fast.WeightedBytesPerSecond / standard.WeightedBytesPerSecond : null,
            expectedSeconds, expectedSeconds - fast.ActivitySeconds,
            comparable ? fast.SampleCount : 0,
            expectedSeconds > 0 ? (expectedSeconds - fast.ActivitySeconds) / expectedSeconds * 100m : null);
    }

    private static ModelActivityRateSummary Summarize(IEnumerable<ModelActivitySample> samples)
    {
        var values = samples.ToArray();
        if (values.Length == 0) return ModelActivityRateSummary.Empty;
        var bytes = values.Sum(sample => sample.EstimatedContentBytes);
        var seconds = values.Sum(ActivitySeconds);
        var rates = values.Select(sample => sample.EstimatedContentBytes / ActivitySeconds(sample)).Order().ToArray();
        var middle = rates.Length / 2;
        var median = rates.Length % 2 == 0 ? (rates[middle - 1] + rates[middle]) / 2 : rates[middle];
        return new(values.Length, bytes, seconds, bytes / seconds, median);
    }

    private static decimal ActivitySeconds(ModelActivitySample sample) =>
        ((decimal)sample.CompletedAtEpochMs - sample.StartedAtEpochMs) / 1000m;

    private static bool IsValid(ModelActivitySample sample) =>
        sample.StartedAtEpochMs >= 0 && sample.CompletedAtEpochMs > sample.StartedAtEpochMs
        && sample.CompletedAtEpochMs <= 253_402_300_799_999
        && sample.RawPayloadBytes > 0 && sample.EstimatedContentBytes > 0
        && sample.EstimatedContentBytes <= sample.RawPayloadBytes && sample.EstimatorRevision > 0
        && Enum.IsDefined(sample.Kind) && Enum.IsDefined(sample.Effort) && Enum.IsDefined(sample.ServiceTier);

    private static bool MatchesFilter(ModelActivitySample sample, FilterSpec filter)
    {
        if (!DateTimeOffset.TryParse(sample.TimestampUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            || timestamp < filter.StartUtc || timestamp >= filter.EndUtc)
            return false;
        if (filter.Models is { } models && !models.Contains(UsageAccounting.ModelCategory(sample.Model), StringComparer.Ordinal))
            return false;
        var role = UsageAccounting.NormalizedAgentRole(sample.ThreadType, sample.AgentRole);
        return filter.Subjects is not { } subjects
            || subjects.Any(subject => subject.ThreadType == sample.ThreadType && subject.AgentRole == role);
    }
}
