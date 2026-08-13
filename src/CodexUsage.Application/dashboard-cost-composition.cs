using System.Globalization;
using CodexUsage.Domain;

namespace CodexUsage.Application;

public static class DashboardCostComposition
{
    public static IReadOnlyList<CostSlice> From(
        UsageSummary summary,
        decimal overallTotalCost,
        string entityLabel)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityLabel);

        var cost = summary.Cost;
        var pricingStatus = DashboardCostPresentation.PricingStatusFrom(summary);
        return
        [
            CreateSlice(DashboardCostCategory.UncachedInput, entityLabel, cost.UncachedInput,
                summary.UncachedInputTokens, cost.Total, overallTotalCost, pricingStatus, "PrimaryBrush"),
            CreateSlice(DashboardCostCategory.CachedInput, entityLabel, cost.CachedInput,
                summary.CachedInputTokens, cost.Total, overallTotalCost, pricingStatus, "SuccessBrush"),
            CreateSlice(DashboardCostCategory.ReasoningOutput, entityLabel, cost.ReasoningOutput,
                summary.ReasoningOutputTokens, cost.Total, overallTotalCost, pricingStatus, "WarningBrush"),
            CreateSlice(DashboardCostCategory.OtherOutput, entityLabel, cost.OtherOutput,
                summary.OtherOutputTokens, cost.Total, overallTotalCost, pricingStatus, "PurpleBrush"),
        ];
    }

    private static CostSlice CreateSlice(
        DashboardCostCategory category,
        string entityLabel,
        decimal costAmount,
        long tokenCount,
        decimal entityTotalCost,
        decimal overallTotalCost,
        CostPricingStatus pricingStatus,
        string brushKey)
    {
        var displayedCost = decimal.Max(0, costAmount);
        var canShowCostShare = pricingStatus is not CostPricingStatus.Unpriced;
        var entityShare = canShowCostShare && entityTotalCost > 0
            ? decimal.ToDouble(displayedCost / entityTotalCost * 100)
            : 0;
        var overallShare = canShowCostShare && overallTotalCost > 0
            ? decimal.ToDouble(displayedCost / overallTotalCost * 100)
            : 0;

        return new CostSlice(
            category,
            entityLabel,
            displayedCost,
            entityShare,
            overallShare,
            Math.Max(0, tokenCount),
            pricingStatus,
            brushKey);
    }
}

public readonly record struct DashboardCostPresentation(
    string Cost,
    string Share,
    CostPricingStatus PricingStatus)
{
    public static DashboardCostPresentation From(UsageSummary summary, decimal overallTotalCost)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var pricingStatus = PricingStatusFrom(summary);
        if (pricingStatus is CostPricingStatus.Unpriced)
            return new("未定价", "—", pricingStatus);

        var cost = decimal.Max(0, summary.Cost.Total);
        var share = overallTotalCost > 0
            ? decimal.ToDouble(cost / overallTotalCost * 100)
            : 0;
        return new(
            DashboardCostCategoryPresentation.FormatCost(cost, pricingStatus),
            DashboardCostCategoryPresentation.FormatPercentage(share, pricingStatus),
            pricingStatus);
    }

    public static CostPricingStatus PricingStatusFrom(UsageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return summary.UnpricedTokens switch
        {
            <= 0 => CostPricingStatus.Priced,
            _ when summary.Cost.Total > 0 => CostPricingStatus.PartiallyPriced,
            _ => CostPricingStatus.Unpriced,
        };
    }
}

public static class DashboardCostCategoryPresentation
{
    public static string Label(DashboardCostCategory category) => category switch
    {
        DashboardCostCategory.UncachedInput => "无缓存输入",
        DashboardCostCategory.CachedInput => "缓存输入",
        DashboardCostCategory.ReasoningOutput => "思考输出",
        DashboardCostCategory.OtherOutput => "其他输出",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    public static string FormatCost(decimal cost, CostPricingStatus pricingStatus) =>
        pricingStatus is CostPricingStatus.Unpriced ? "未定价" : $"${cost:N1}";

    public static string FormatPercentage(double percentage, CostPricingStatus pricingStatus) =>
        pricingStatus is CostPricingStatus.Unpriced ? "—" : $"{percentage:F1}%";

    public static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000d:F1}B",
        >= 1_000_000 => $"{tokens / 1_000_000d:F1}M",
        >= 1_000 => $"{tokens / 1_000d:F1}K",
        _ => tokens.ToString("N0", CultureInfo.CurrentCulture),
    };
}

public sealed record SubagentUsageAggregate(int ThreadCount, UsageSummary Summary);

public static class DashboardSubagentAggregation
{
    public static SubagentUsageAggregate? From(IEnumerable<RoleUsageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var subagentRows = rows.Where(row => row.ThreadType is ThreadType.Subagent).ToArray();
        if (subagentRows.Length == 0) return null;

        return new(
            subagentRows.Sum(static row => row.ThreadCount),
            new UsageSummary(
                subagentRows.Sum(static row => row.Summary.Calls),
                subagentRows.Sum(static row => row.Summary.InputTokens),
                subagentRows.Sum(static row => row.Summary.CachedInputTokens),
                subagentRows.Sum(static row => row.Summary.UncachedInputTokens),
                subagentRows.Sum(static row => row.Summary.OutputTokens),
                subagentRows.Sum(static row => row.Summary.ReasoningOutputTokens),
                subagentRows.Sum(static row => row.Summary.OtherOutputTokens),
                subagentRows.Sum(static row => row.Summary.CanonicalTotalTokens),
                subagentRows.Sum(static row => row.Summary.UnpricedTokens),
                new CostBreakdown(
                    subagentRows.Sum(static row => row.Summary.Cost.UncachedInput),
                    subagentRows.Sum(static row => row.Summary.Cost.CachedInput),
                    subagentRows.Sum(static row => row.Summary.Cost.ReasoningOutput),
                    subagentRows.Sum(static row => row.Summary.Cost.OtherOutput),
                    subagentRows.Sum(static row => row.Summary.Cost.Total),
                    subagentRows.All(static row => row.Summary.Cost.Priced))));
    }
}
