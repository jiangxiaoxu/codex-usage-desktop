using System.Collections.Specialized;
using System.ComponentModel;
using CodexUsage.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexUsage.App.Controls;

public sealed partial class CostCompositionBar : UserControl
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items),
        typeof(IReadOnlyList<CostSlice>),
        typeof(CostCompositionBar),
        new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty VisualHeightProperty = DependencyProperty.Register(
        nameof(VisualHeight),
        typeof(double),
        typeof(CostCompositionBar),
        new PropertyMetadata(8d, OnVisualHeightChanged));

    private readonly List<CostSlice> _subscribedItems = [];
    private readonly List<SegmentVisual> _segmentVisuals = [];
    private INotifyCollectionChanged? _collection;

    public CostCompositionBar()
    {
        InitializeComponent();
        ApplyVisualHeight();
    }

    public IReadOnlyList<CostSlice>? Items
    {
        get => (IReadOnlyList<CostSlice>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double VisualHeight
    {
        get => (double)GetValue(VisualHeightProperty);
        set => SetValue(VisualHeightProperty, value);
    }

    private static void OnItemsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((CostCompositionBar)dependencyObject).SynchronizeItems();

    private static void OnVisualHeightChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((CostCompositionBar)dependencyObject).ApplyVisualHeight();

    private void SynchronizeItems()
    {
        UpdateSubscriptions();
        var allItems = Items?.ToArray() ?? [];
        UpdateTrackAccessibility(allItems);

        _segmentVisuals.Clear();
        SegmentsHost.Children.Clear();
        SegmentsHost.ColumnDefinitions.Clear();
        LegendHost.Children.Clear();
        foreach (var item in allItems.Where(IsSelectable))
        {
            var percentage = Math.Max(0.0001, item.Percentage);
            SegmentsHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(percentage, GridUnitType.Star),
            });

            var color = new Border
            {
                Height = NormalizeVisualHeight(),
                VerticalAlignment = VerticalAlignment.Center,
                Background = ResolveCategoryBrush(item.Category),
            };
            Grid.SetColumn(color, _segmentVisuals.Count);
            SegmentsHost.Children.Add(color);
            _segmentVisuals.Add(new SegmentVisual(item, color));
        }

        foreach (var item in allItems)
        {
            LegendHost.Children.Add(CreateLegendChip(item));
        }

        ApplyVisualHeight();
    }

    private void UpdateSubscriptions()
    {
        if (_collection is not null) _collection.CollectionChanged -= OnCollectionChanged;
        foreach (var item in _subscribedItems) item.PropertyChanged -= OnItemPropertyChanged;

        _subscribedItems.Clear();
        _collection = Items as INotifyCollectionChanged;
        if (_collection is not null) _collection.CollectionChanged += OnCollectionChanged;
        if (Items is null) return;

        foreach (var item in Items)
        {
            _subscribedItems.Add(item);
            item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => SynchronizeItems();

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args) => SynchronizeItems();

    private void UpdateTrackAccessibility(IReadOnlyList<CostSlice> items)
    {
        var hasItems = items.Count > 0;
        var detail = hasItems
            ? string.Join(Environment.NewLine + Environment.NewLine, items.Select(static item => item.ToolTipText))
            : "当前没有费用构成数据。";

        InteractionSurface.IsHitTestVisible = hasItems;
        InteractionSurface.IsTabStop = hasItems;
        AutomationProperties.SetAccessibilityView(InteractionSurface, AccessibilityView.Content);
        AutomationProperties.SetName(InteractionSurface, "费用构成");
        AutomationProperties.SetHelpText(InteractionSurface, detail);
        ToolTipService.SetToolTip(InteractionSurface, detail);
    }

    private void ApplyVisualHeight()
    {
        NeutralTrack.Height = NormalizeVisualHeight();
        foreach (var segment in _segmentVisuals)
        {
            segment.Color.Height = NormalizeVisualHeight();
        }
    }

    private static bool IsSelectable(CostSlice item) => item.IsPriced && item.CostAmount > 0;

    private static Grid CreateLegendChip(CostSlice item)
    {
        var chip = new Grid
        {
            ColumnSpacing = 7,
        };
        chip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(chip, $"{item.Label} {item.EntityShare}");
        AutomationProperties.SetAccessibilityView(chip, AccessibilityView.Content);
        ToolTipService.SetToolTip(chip, item.ToolTipText);

        var color = new Border
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ResolveCategoryBrush(item.Category),
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(color, AccessibilityView.Raw);

        var label = new TextBlock
        {
            Style = Microsoft.UI.Xaml.Application.Current.Resources["CaptionStyle"] as Style,
            Text = item.Label,
        };
        AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
        Grid.SetColumn(label, 1);

        var percentage = new TextBlock
        {
            Style = Microsoft.UI.Xaml.Application.Current.Resources["CaptionStyle"] as Style,
            Text = item.EntityShare,
        };
        AutomationProperties.SetAccessibilityView(percentage, AccessibilityView.Raw);
        Grid.SetColumn(percentage, 2);

        chip.Children.Add(color);
        chip.Children.Add(label);
        chip.Children.Add(percentage);
        return chip;
    }

    private double NormalizeVisualHeight() => Math.Clamp(VisualHeight, 8, 10);

    private static Brush? ResolveCategoryBrush(DashboardCostCategory category)
    {
        var key = category switch
        {
            DashboardCostCategory.UncachedInput => "CostUncachedBrush",
            DashboardCostCategory.CachedInput => "CostCachedBrush",
            DashboardCostCategory.ReasoningOutput => "CostReasoningBrush",
            DashboardCostCategory.OtherOutput => "CostOtherBrush",
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
        };
        return Microsoft.UI.Xaml.Application.Current.Resources[key] as Brush;
    }

    private sealed record SegmentVisual(CostSlice Item, Border Color);
}
