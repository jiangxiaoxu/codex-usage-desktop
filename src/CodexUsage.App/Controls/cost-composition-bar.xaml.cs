using System.Collections.Specialized;
using System.ComponentModel;
using CodexUsage.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

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

    public static readonly DependencyProperty IsDetailsExpandedProperty = DependencyProperty.Register(
        nameof(IsDetailsExpanded),
        typeof(bool),
        typeof(CostCompositionBar),
        new PropertyMetadata(false, OnIsDetailsExpandedChanged));

    private readonly List<CostSlice> _subscribedItems = [];
    private readonly List<SegmentVisual> _segmentVisuals = [];
    private INotifyCollectionChanged? _collection;
    private bool _hasItems;

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

    public bool IsDetailsExpanded
    {
        get => (bool)GetValue(IsDetailsExpandedProperty);
        set => SetValue(IsDetailsExpandedProperty, value);
    }

    private static void OnItemsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((CostCompositionBar)dependencyObject).SynchronizeItems();

    private static void OnVisualHeightChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((CostCompositionBar)dependencyObject).ApplyVisualHeight();

    private static void OnIsDetailsExpandedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((CostCompositionBar)dependencyObject).SynchronizeDetailsExpandedState();

    private void SynchronizeItems()
    {
        UpdateSubscriptions();
        var allItems = Items?.ToArray() ?? [];
        _hasItems = allItems.Length > 0;
        UpdateTrackAccessibility(allItems);

        _segmentVisuals.Clear();
        SegmentsHost.Children.Clear();
        SegmentsHost.ColumnDefinitions.Clear();
        LegendHost.Children.Clear();
        DetailsHost.Children.Clear();
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

        if (allItems.Length == 0)
        {
            DetailsHost.Children.Add(CreateEmptyDetailsState());
        }

        foreach (var item in allItems)
        {
            LegendHost.Children.Add(CreateLegendChip(item));
            DetailsHost.Children.Add(CreateDetailCard(item));
        }

        ApplyVisualHeight();
        SynchronizeDetailsExpandedState();
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
        var canDismissExpandedEmptyState = IsDetailsExpanded;
        var isAvailable = _hasItems || canDismissExpandedEmptyState;
        var detail = string.Join(
            Environment.NewLine,
            items.Select(static item =>
                $"{item.EntityLabel} · {item.Label}; 费用 {item.Cost}; 实体 {item.EntityShare}; 筛选 {item.OverallShare}; tokens {item.Tokens}"));

        InteractionSurface.IsEnabled = isAvailable;
        InteractionSurface.IsTabStop = isAvailable;
        AutomationProperties.SetAccessibilityView(InteractionSurface, AccessibilityView.Content);
        AutomationProperties.SetName(
            InteractionSurface,
            _hasItems ? "切换费用构成详情" : isAvailable ? "收起空的费用构成详情" : "费用构成详情不可用");
        AutomationProperties.SetHelpText(
            InteractionSurface,
            _hasItems
                ? $"点击或按 Space/Enter 展开或收起稳定的费用构成详情。按 Escape 收起详情。{Environment.NewLine}{detail}"
                : "当前没有费用构成数据。");
    }

    private void SynchronizeDetailsExpandedState()
    {
        InteractionSurface.IsChecked = IsDetailsExpanded;
        DetailsPanel.Visibility = IsDetailsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InteractionSurface_OnChecked(object sender, RoutedEventArgs args)
    {
        IsDetailsExpanded = true;
        UpdateTrackAccessibility(Items?.ToArray() ?? []);
    }

    private void InteractionSurface_OnUnchecked(object sender, RoutedEventArgs args)
    {
        IsDetailsExpanded = false;
        UpdateTrackAccessibility(Items?.ToArray() ?? []);
    }

    private void InteractionSurface_OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape || !IsDetailsExpanded) return;

        IsDetailsExpanded = false;
        if (_hasItems) InteractionSurface.Focus(FocusState.Keyboard);
        args.Handled = true;
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

    private static Border CreateDetailCard(CostSlice item)
    {
        var card = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Background = Microsoft.UI.Xaml.Application.Current.Resources["ElevatedSurfaceBrush"] as Brush,
            BorderBrush = Microsoft.UI.Xaml.Application.Current.Resources["CardBorderBrush"] as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
        };
        AutomationProperties.SetAccessibilityView(card, AccessibilityView.Content);
        AutomationProperties.SetName(
            card,
            $"{item.EntityLabel} · {item.Label}; 费用 {item.Cost}; 实体 {item.EntityShare}; 筛选 {item.OverallShare}; tokens {item.Tokens}");

        var content = new Grid
        {
            RowSpacing = 4,
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var color = new Border
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ResolveCategoryBrush(item.Category),
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(color, AccessibilityView.Raw);

        var category = new TextBlock
        {
            Style = Microsoft.UI.Xaml.Application.Current.Resources["TableCellTextStyle"] as Style,
            Text = item.Label,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetAccessibilityView(category, AccessibilityView.Raw);
        Grid.SetColumn(category, 2);

        var cost = new TextBlock
        {
            Style = Microsoft.UI.Xaml.Application.Current.Resources["PriceCellTextStyle"] as Style,
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = item.Cost,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetAccessibilityView(cost, AccessibilityView.Raw);
        Grid.SetColumn(cost, 3);

        var detail = new TextBlock
        {
            Style = Microsoft.UI.Xaml.Application.Current.Resources["CaptionStyle"] as Style,
            Text = $"实体 {item.EntityShare} · 筛选 {item.OverallShare} · tokens {item.Tokens}",
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetAccessibilityView(detail, AccessibilityView.Content);
        AutomationProperties.SetName(
            detail,
            $"{item.EntityLabel} · {item.Label}; 费用 {item.Cost}; 实体 {item.EntityShare}; 筛选 {item.OverallShare}; tokens {item.Tokens}");
        Grid.SetRow(detail, 1);
        Grid.SetColumnSpan(detail, 4);

        content.Children.Add(color);
        content.Children.Add(category);
        content.Children.Add(cost);
        content.Children.Add(detail);
        card.Child = content;
        return card;
    }

    private static TextBlock CreateEmptyDetailsState()
    {
        var emptyState = new TextBlock
        {
            Style = Microsoft.UI.Xaml.Application.Current.Resources["CaptionStyle"] as Style,
            Text = "当前没有费用构成数据。",
        };
        AutomationProperties.SetAccessibilityView(emptyState, AccessibilityView.Content);
        AutomationProperties.SetName(emptyState, "当前没有费用构成数据。");
        return emptyState;
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
