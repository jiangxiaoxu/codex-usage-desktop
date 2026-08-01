using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CodexUsage.App.Controls;

public sealed class ChipWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;

    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var availableWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width);
        var lineWidth = 0d;
        var lineHeight = 0d;
        var measuredWidth = 0d;
        var measuredHeight = 0d;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var childSize = child.DesiredSize;
            var requiredWidth = lineWidth == 0
                ? childSize.Width
                : lineWidth + HorizontalSpacing + childSize.Width;

            if (lineWidth > 0 && requiredWidth > availableWidth)
            {
                measuredWidth = Math.Max(measuredWidth, lineWidth);
                measuredHeight += lineHeight + VerticalSpacing;
                lineWidth = childSize.Width;
                lineHeight = childSize.Height;
                continue;
            }

            lineWidth = requiredWidth;
            lineHeight = Math.Max(lineHeight, childSize.Height);
        }

        measuredWidth = Math.Max(measuredWidth, lineWidth);
        measuredHeight += lineHeight;
        return new Size(
            double.IsInfinity(availableWidth) ? measuredWidth : Math.Min(measuredWidth, availableWidth),
            measuredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var lineHeight = 0d;

        foreach (var child in Children)
        {
            var childSize = child.DesiredSize;
            var requiredWidth = x == 0
                ? childSize.Width
                : x + HorizontalSpacing + childSize.Width;

            if (x > 0 && requiredWidth > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }

            child.Arrange(new Rect(x, y, childSize.Width, childSize.Height));
            x += childSize.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, childSize.Height);
        }

        return finalSize;
    }
}
