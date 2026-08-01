using CodexUsage.Domain;

namespace CodexUsage.Application;

public static class DashboardCostComposition
{
    public static IReadOnlyList<CostSlice> From(CostBreakdown cost)
    {
        ArgumentNullException.ThrowIfNull(cost);

        return
        [
            CreateSlice("无缓存输入", cost.UncachedInput, cost.Total, "PrimaryBrush"),
            CreateSlice("缓存输入", cost.CachedInput, cost.Total, "SuccessBrush"),
            CreateSlice("思考输出", cost.ReasoningOutput, cost.Total, "WarningBrush"),
            CreateSlice("其他输出", cost.OtherOutput, cost.Total, "PurpleBrush"),
        ];
    }

    private static CostSlice CreateSlice(string label, decimal value, decimal total, string brushKey)
    {
        var displayedValue = decimal.Max(0, value);
        var percentage = total > 0 ? decimal.ToDouble(displayedValue / total * 100) : 0;
        return new CostSlice(
            label,
            percentage,
            $"{percentage:F1}%",
            brushKey);
    }
}
