using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CodexUsage.App.Controls;

public enum ChipWrapLineDistribution
{
    Compact,
    EqualCells,
}

public sealed class ChipWrapPanel : Panel
{
    private double _minimumEqualCellsArrangeWidth;

    public static readonly DependencyProperty LineDistributionProperty = DependencyProperty.Register(
        nameof(LineDistribution),
        typeof(ChipWrapLineDistribution),
        typeof(ChipWrapPanel),
        new PropertyMetadata(ChipWrapLineDistribution.Compact));

    public double HorizontalSpacing { get; set; } = 8;

    public double VerticalSpacing { get; set; } = 8;

    public ChipWrapLineDistribution LineDistribution
    {
        get => (ChipWrapLineDistribution)GetValue(LineDistributionProperty);
        set => SetValue(LineDistributionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var availableWidth = GetAvailableWidth(availableSize.Width);
        var measuredWidth = 0d;
        var measuredHeight = 0d;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        var lines = CreateLines(availableWidth);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            measuredWidth = Math.Max(measuredWidth, line.Width);
            measuredHeight += line.Height;

            if (index < lines.Count - 1)
            {
                measuredHeight += VerticalSpacing;
            }
        }

        var usesEqualCells = LineDistribution == ChipWrapLineDistribution.EqualCells && lines.Count > 0;
        var width = usesEqualCells
            ? double.IsInfinity(availableWidth)
                ? GetEqualCellsRequiredWidth(lines[0].Children.Count, lines[0].MaximumChildWidth)
                : availableWidth
            : double.IsInfinity(availableWidth)
                ? measuredWidth
                : Math.Min(measuredWidth, availableWidth);

        _minimumEqualCellsArrangeWidth = usesEqualCells ? width : 0;

        return new Size(width, measuredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var finalWidth = GetAvailableWidth(finalSize.Width);
        var availableWidth = LineDistribution == ChipWrapLineDistribution.EqualCells
            ? Math.Max(finalWidth, _minimumEqualCellsArrangeWidth)
            : finalWidth;
        var y = 0d;
        var lines = CreateLines(availableWidth);

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var distributeLine = UsesEqualCells(availableWidth)
                && line.MaximumChildWidth <= availableWidth;

            if (distributeLine)
            {
                var cellWidth = (availableWidth - ((line.Children.Count - 1) * HorizontalSpacing)) / line.Children.Count;
                var x = 0d;

                foreach (var child in line.Children)
                {
                    var childX = x + ((cellWidth - child.Size.Width) / 2);
                    child.Element.Arrange(new Rect(childX, y, child.Size.Width, child.Size.Height));
                    x += cellWidth + HorizontalSpacing;
                }
            }
            else
            {
                var x = 0d;
                foreach (var child in line.Children)
                {
                    child.Element.Arrange(new Rect(x, y, child.Size.Width, child.Size.Height));
                    x += child.Size.Width + HorizontalSpacing;
                }
            }

            if (lineIndex < lines.Count - 1)
            {
                y += line.Height + VerticalSpacing;
            }
        }

        return finalSize;
    }

    private static double GetAvailableWidth(double width)
    {
        return double.IsInfinity(width)
            ? double.PositiveInfinity
            : Math.Max(0, width);
    }

    private IReadOnlyList<LayoutLine> CreateLines(double availableWidth)
    {
        var lines = new List<LayoutLine>();
        LayoutLine? currentLine = null;

        foreach (var child in Children)
        {
            var childSize = child.DesiredSize;
            var requiresNewLine = currentLine is not null
                && currentLine.Children.Count > 0
                && RequiresNewLine(currentLine, childSize, availableWidth);

            if (currentLine is null || requiresNewLine)
            {
                currentLine = new LayoutLine();
                lines.Add(currentLine);
            }

            currentLine.Add(child, childSize, HorizontalSpacing);
        }

        return lines;
    }

    private bool UsesEqualCells(double availableWidth)
    {
        return LineDistribution == ChipWrapLineDistribution.EqualCells
            && !double.IsInfinity(availableWidth);
    }

    private bool RequiresNewLine(LayoutLine currentLine, Size childSize, double availableWidth)
    {
        if (!UsesEqualCells(availableWidth))
        {
            return currentLine.Width + HorizontalSpacing + childSize.Width > availableWidth;
        }

        var candidateCount = currentLine.Children.Count + 1;
        var candidateMaximumWidth = Math.Max(currentLine.MaximumChildWidth, childSize.Width);
        var candidateWidth = GetEqualCellsRequiredWidth(candidateCount, candidateMaximumWidth);
        return candidateWidth > availableWidth;
    }

    private double GetEqualCellsRequiredWidth(int count, double maximumChildWidth)
    {
        return (count * maximumChildWidth) + ((count - 1) * HorizontalSpacing);
    }

    private sealed class LayoutLine
    {
        public List<MeasuredChild> Children { get; } = [];

        public double Width { get; private set; }

        public double Height { get; private set; }

        public double MaximumChildWidth { get; private set; }

        public void Add(UIElement element, Size size, double horizontalSpacing)
        {
            if (Children.Count > 0)
            {
                Width += horizontalSpacing;
            }

            Children.Add(new MeasuredChild(element, size));
            Width += size.Width;
            Height = Math.Max(Height, size.Height);
            MaximumChildWidth = Math.Max(MaximumChildWidth, size.Width);
        }
    }

    private readonly record struct MeasuredChild(UIElement Element, Size Size);
}
