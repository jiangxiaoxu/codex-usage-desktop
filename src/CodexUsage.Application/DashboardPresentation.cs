using System.Collections.Immutable;
using CodexUsage.Domain;
using CodexUsage.Infrastructure.Collection;

namespace CodexUsage.Application;

public static class DashboardTimeRangeScale
{
    private static readonly double[] HoursAnchors = [0.5, 12, 24, 168, 336];

    public const double MinimumPosition = 0;
    public const double MaximumPosition = 4;
    public const double DefaultPosition = 1;
    public const double HoursStep = 0.5;

    public static double PositionToHours(double position)
    {
        var clampedPosition = Math.Clamp(position, MinimumPosition, MaximumPosition);
        var lowerIndex = Math.Min((int)Math.Floor(clampedPosition), HoursAnchors.Length - 2);
        var segmentProgress = clampedPosition - lowerIndex;
        var lowerHours = HoursAnchors[lowerIndex];
        var upperHours = HoursAnchors[lowerIndex + 1];
        return NormalizeHours(lowerHours + ((upperHours - lowerHours) * segmentProgress));
    }

    public static DashboardTimeRangePositionCoercion CoercePositionInput(double requested, double previous)
    {
        var fallback = double.IsFinite(previous)
            ? Math.Clamp(previous, MinimumPosition, MaximumPosition)
            : DefaultPosition;
        if (!double.IsFinite(requested)) return new(fallback, RequiresCorrection: true);

        var position = Math.Clamp(requested, MinimumPosition, MaximumPosition);
        return new(position, RequiresCorrection: position != requested);
    }

    public static DashboardTimeRangeSelection SelectionFromPosition(double position)
    {
        var hours = PositionToHours(position);
        return new(hours, HoursToPosition(hours));
    }

    public static double HoursToPosition(double hours)
    {
        var normalizedHours = NormalizeHours(hours);
        for (var upperIndex = 1; upperIndex < HoursAnchors.Length; upperIndex++)
        {
            var upperHours = HoursAnchors[upperIndex];
            if (normalizedHours > upperHours) continue;

            var lowerIndex = upperIndex - 1;
            var lowerHours = HoursAnchors[lowerIndex];
            return lowerIndex + ((normalizedHours - lowerHours) / (upperHours - lowerHours));
        }

        return MaximumPosition;
    }

    public static double NormalizeHours(double hours)
    {
        var clampedHours = Math.Clamp(hours, HoursAnchors[0], HoursAnchors[^1]);
        return Math.Round(clampedHours / HoursStep, MidpointRounding.AwayFromZero) * HoursStep;
    }

    public static string FormatHours(double hours)
    {
        var normalizedHours = NormalizeHours(hours);
        return normalizedHours < 24
            ? $"{normalizedHours:0.#}小时"
            : $"{normalizedHours / 24:0.#}天";
    }

    public static double AdjustHours(double hours, DashboardTimeRangeAdjustment adjustment)
    {
        var normalizedHours = NormalizeHours(hours);
        return adjustment switch
        {
            DashboardTimeRangeAdjustment.Decrease => NormalizeHours(normalizedHours - HoursStep),
            DashboardTimeRangeAdjustment.Increase => NormalizeHours(normalizedHours + HoursStep),
            DashboardTimeRangeAdjustment.PreviousAnchor => PreviousAnchor(normalizedHours),
            DashboardTimeRangeAdjustment.NextAnchor => NextAnchor(normalizedHours),
            DashboardTimeRangeAdjustment.Minimum => HoursAnchors[0],
            DashboardTimeRangeAdjustment.Maximum => HoursAnchors[^1],
            _ => throw new ArgumentOutOfRangeException(nameof(adjustment), adjustment, null),
        };
    }

    private static double PreviousAnchor(double hours)
    {
        for (var index = HoursAnchors.Length - 1; index >= 0; index--)
        {
            if (HoursAnchors[index] < hours) return HoursAnchors[index];
        }

        return HoursAnchors[0];
    }

    private static double NextAnchor(double hours)
    {
        foreach (var anchor in HoursAnchors)
        {
            if (anchor > hours) return anchor;
        }

        return HoursAnchors[^1];
    }
}

public enum DashboardTimeRangeAdjustment
{
    Decrease,
    Increase,
    PreviousAnchor,
    NextAnchor,
    Minimum,
    Maximum,
}

public enum DashboardDirectionalKey
{
    Left,
    Right,
    Up,
    Down,
}

public static class DashboardTimeRangeInput
{
    public static DashboardTimeRangeAdjustment DirectionalAdjustment(
        DashboardDirectionalKey key,
        bool rightToLeft) => key switch
        {
            DashboardDirectionalKey.Left when rightToLeft => DashboardTimeRangeAdjustment.Increase,
            DashboardDirectionalKey.Left => DashboardTimeRangeAdjustment.Decrease,
            DashboardDirectionalKey.Right when rightToLeft => DashboardTimeRangeAdjustment.Decrease,
            DashboardDirectionalKey.Right => DashboardTimeRangeAdjustment.Increase,
            DashboardDirectionalKey.Up => DashboardTimeRangeAdjustment.Increase,
            DashboardDirectionalKey.Down => DashboardTimeRangeAdjustment.Decrease,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };
}

public static class DashboardTimeRangeGeometry
{
    public static double PositionToPhysicalX(double position, double trackWidth, bool rightToLeft)
    {
        var width = Math.Max(trackWidth, 1);
        var logicalX = Math.Clamp(
            position,
            DashboardTimeRangeScale.MinimumPosition,
            DashboardTimeRangeScale.MaximumPosition)
            / DashboardTimeRangeScale.MaximumPosition
            * width;
        return rightToLeft ? width - logicalX : logicalX;
    }

    public static double PhysicalXToPosition(double physicalX, double trackWidth, bool rightToLeft)
    {
        var width = Math.Max(trackWidth, 1);
        var clampedPhysicalX = Math.Clamp(physicalX, 0, width);
        var logicalX = rightToLeft ? width - clampedPhysicalX : clampedPhysicalX;
        return logicalX / width * DashboardTimeRangeScale.MaximumPosition;
    }
}

public readonly record struct DashboardTimeRangeSelection(double Hours, double Position);

public readonly record struct DashboardTimeRangePositionCoercion(double Position, bool RequiresCorrection);

public readonly record struct DashboardTimeRangeTransition(
    DashboardTimeRangeSelection Selection,
    bool HoursChanged,
    bool ClearCustomRange,
    bool QueryRequired)
{
    public static DashboardTimeRangeTransition FromUserPosition(
        double currentHours,
        double inputPosition,
        bool hasCustomRange)
    {
        var selection = DashboardTimeRangeScale.SelectionFromPosition(inputPosition);
        var hoursChanged = selection.Hours != DashboardTimeRangeScale.NormalizeHours(currentHours);
        return new(selection, hoursChanged, hasCustomRange, hoursChanged || hasCustomRange);
    }

    public static DashboardTimeRangeTransition FromProgrammaticHours(
        double currentHours,
        double requestedHours)
    {
        var hours = DashboardTimeRangeScale.NormalizeHours(requestedHours);
        var selection = new DashboardTimeRangeSelection(
            hours,
            DashboardTimeRangeScale.HoursToPosition(hours));
        return new(
            selection,
            hours != DashboardTimeRangeScale.NormalizeHours(currentHours),
            ClearCustomRange: false,
            QueryRequired: false);
    }
}

public sealed record DashboardCustomRange(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public static TimeSpan SgtOffset { get; } = TimeSpan.FromHours(8);

    public DateTimeOffset StartDateSgt => StartUtc.ToOffset(SgtOffset);

    public DateTimeOffset EndDateSgt => EndUtc.ToOffset(SgtOffset);

    public string Label => $"{StartDateSgt:MM/dd} - {EndDateSgt:MM/dd}";

    public static bool TryCreateFromSgtDates(
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        out DashboardCustomRange? range,
        out string validationMessage)
    {
        if (startDate is null || endDate is null)
        {
            range = null;
            validationMessage = "请选择开始和结束日期.";
            return false;
        }

        var startSgt = AtSgtMidnight(startDate.Value);
        var endSgt = AtSgtMidnight(endDate.Value);
        if (endSgt <= startSgt)
        {
            range = null;
            validationMessage = "结束日期必须晚于开始日期.";
            return false;
        }

        range = new(startSgt.ToUniversalTime(), endSgt.ToUniversalTime());
        validationMessage = string.Empty;
        return true;
    }

    private static DateTimeOffset AtSgtMidnight(DateTimeOffset date) => new(
        date.Year,
        date.Month,
        date.Day,
        0,
        0,
        0,
        SgtOffset);
}

public sealed record CoveragePresentation(
    string Text,
    double ContinuousOpacity,
    double GapOpacity)
{
    public static CoveragePresentation From(
        ObservationCoverage coverage,
        ObservationGap? gap) => coverage switch
        {
            ObservationCoverage.Continuous => new("持续观测中", 1, 0),
            ObservationCoverage.Gap when gap is not null => new(
                $"{gap.StartUtc.ToOffset(DashboardCustomRange.SgtOffset):HH:mm:ss} - {gap.EndUtc.ToOffset(DashboardCustomRange.SgtOffset):HH:mm:ss} 未观测",
                0,
                1),
            ObservationCoverage.Gap => new("存在观察缺口", 0, 1),
            _ => new("已建立本次运行基线", 0, 0),
        };
}

public readonly record struct DashboardLongContextRatePresentation(string LongContextRate, string Share)
{
    public static DashboardLongContextRatePresentation From(
        decimal cost,
        decimal totalCost,
        decimal? actualToBaselineMultiplier,
        bool priced)
    {
        var rate = actualToBaselineMultiplier is { } multiplier ? $"×{multiplier:N2}" : "—";
        if (!priced && actualToBaselineMultiplier is null) return new(rate, "—");

        var share = totalCost > 0 ? $"{cost / totalCost:P1}" : "0.0%";
        return new(rate, share);
    }
}

public static class DashboardSubjectOrdering
{
    public static ImmutableArray<RoleUsageRow> SortByDescendingCost(IEnumerable<RoleUsageRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows
            .OrderByDescending(row => row.Summary.Cost.Total)
            .ThenBy(row => SemanticOrder(UsageAccounting.ThreadTypeText(row.ThreadType), row.AgentRole))
            .ThenBy(row => UsageAccounting.ThreadTypeText(row.ThreadType), StringComparer.Ordinal)
            .ThenBy(row => row.AgentRole, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public static int SemanticOrder(string threadType, string role) => (threadType, role) switch
    {
        ("main", "root") => 0,
        ("guardian_review", "guardian") => 1,
        ("subagent", "worker") => 2,
        ("subagent", "reviewer") => 3,
        ("subagent", "explorer") => 4,
        ("subagent", "scout") => 5,
        ("subagent", "awaiter") => 6,
        ("subagent", "scoped_worker") => 7,
        ("subagent", "simple_worker") => 8,
        ("subagent", "unknown") => 9,
        _ => 10,
    };
}

public static class DashboardAccessibilityLayout
{
    private const double MinimumTextScaleFactor = 1;
    private const double MaximumTextScaleFactor = 2.25;

    public static double TableRowHeight(double textScaleFactor)
    {
        var scale = double.IsFinite(textScaleFactor)
            ? Math.Clamp(textScaleFactor, MinimumTextScaleFactor, MaximumTextScaleFactor)
            : MinimumTextScaleFactor;
        return Math.Ceiling(32 + (24 * scale));
    }
}

public readonly record struct DashboardPixelSize(int Width, int Height);

public static class DashboardWindowSizing
{
    private const uint DefaultDpi = 96;

    public static DashboardPixelSize MinimumTrackClientPixels(
        int effectiveWidth,
        int effectiveHeight,
        uint dpi)
    {
        if (effectiveWidth <= 0) throw new ArgumentOutOfRangeException(nameof(effectiveWidth));
        if (effectiveHeight <= 0) throw new ArgumentOutOfRangeException(nameof(effectiveHeight));

        var effectiveDpi = dpi == 0 ? DefaultDpi : dpi;
        return new(
            checked((int)Math.Ceiling(effectiveWidth * effectiveDpi / (double)DefaultDpi)),
            checked((int)Math.Ceiling(effectiveHeight * effectiveDpi / (double)DefaultDpi)));
    }
}
