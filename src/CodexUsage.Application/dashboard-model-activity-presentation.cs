using System.Globalization;
using CodexUsage.Domain;

namespace CodexUsage.Application;

public sealed class ModelActivityRow(ModelActivityComparisonRow row) : DashboardPresentationItem
{
    private string _standard = Rate(row.Standard);
    private string _fast = Rate(row.Fast);
    private string _unknown = $"模式未知: {row.Unknown.SampleCount:N0} 条";
    private string _timeReduction = Percent(row.EstimatedTimeReductionPercent, "无法估算");

    public string Model { get; } = row.Model;
    public ReasoningEffort Effort { get; } = row.Effort;
    public ModelActivityKind Kind { get; } = row.Kind;
    public int EstimatorRevision { get; } = row.EstimatorRevision;
    public string Label => $"{Model} / {(Effort == ReasoningEffort.Unknown ? "推理强度未知" : Effort.ToString().ToLowerInvariant())} / {(Kind == ModelActivityKind.Reasoning ? "推理" : "正文")}";
    public string Standard { get => _standard; private set => SetValue(ref _standard, value); }
    public string Fast { get => _fast; private set => SetValue(ref _fast, value); }
    public string Unknown { get => _unknown; private set => SetValue(ref _unknown, value); }
    public string TimeReduction { get => _timeReduction; private set => SetValue(ref _timeReduction, value); }

    public void UpdateFrom(ModelActivityRow source)
    {
        Standard = source.Standard;
        Fast = source.Fast;
        Unknown = source.Unknown;
        TimeReduction = source.TimeReduction;
    }

    public static string Percent(decimal? value, string unavailable) => value is { } percent
        ? $"{percent.ToString("N1", CultureInfo.CurrentCulture)}%"
        : unavailable;

    private static string Rate(ModelActivityRateSummary summary) =>
        $"加权 {RateValue(summary.WeightedBytesPerSecond)} / 中位 {RateValue(summary.MedianBytesPerSecond)} KiB/s; {summary.SampleCount:N0} 条";

    private static string RateValue(decimal? bytesPerSecond) => bytesPerSecond is { } value
        ? (value / 1024m).ToString("N2", CultureInfo.CurrentCulture)
        : "-";
}
