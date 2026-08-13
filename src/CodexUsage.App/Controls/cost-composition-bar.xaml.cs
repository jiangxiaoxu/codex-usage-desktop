using System.Collections.Specialized;
using System.ComponentModel;
using CodexUsage.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    public static readonly DependencyProperty DetailsAlwaysVisibleProperty = DependencyProperty.Register(
        nameof(DetailsAlwaysVisible),
        typeof(bool),
        typeof(CostCompositionBar),
        new PropertyMetadata(false, OnDetailsAlwaysVisibleChanged));

    private readonly List<CostSlice> _subscribedItems = [];
    private readonly List<SegmentVisual> _segmentVisuals = [];
    private INotifyCollectionChanged? _collection;
    private bool _isPointerOver;
    private bool _isFocused;
    private bool _canExpand;

    public CostCompositionBar()
    {
        InitializeComponent();
        InteractionSurface.PointerEntered += OnInteractionSurfacePointerEntered;
        InteractionSurface.PointerExited += OnInteractionSurfacePointerExited;
        InteractionSurface.GotFocus += OnInteractionSurfaceGotFocus;
        InteractionSurface.LostFocus += OnInteractionSurfaceLostFocus;
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

    public bool DetailsAlwaysVisible
    {
        get => (bool)GetValue(DetailsAlwaysVisibleProperty);
        set => SetValue(DetailsAlwaysVisibleProperty, value);
    }

    private static void OnItemsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((CostCompositionBar)dependencyObject).SynchronizeItems();

    private static void OnVisualHeightChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((CostCompositionBar)dependencyObject).ApplyVisualHeight();

    private static void OnDetailsAlwaysVisibleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((CostCompositionBar)dependencyObject).UpdateDetailVisibility();

    private void SynchronizeItems()
    {
        UpdateSubscriptions();
        var allItems = Items?.ToArray() ?? [];
        _canExpand = allItems.Any(IsSelectable);
        UpdateInteractionSurface();

        _segmentVisuals.Clear();
        SegmentsHost.Children.Clear();
        SegmentsHost.ColumnDefinitions.Clear();
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

        ApplyVisualHeight();
        InlineDetail.Text = DetailText(allItems);
        UpdateDetailVisibility();
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

    private void OnInteractionSurfacePointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _isPointerOver = true;
        UpdateDetailVisibility();
    }

    private void OnInteractionSurfacePointerExited(object sender, PointerRoutedEventArgs args)
    {
        _isPointerOver = false;
        UpdateDetailVisibility();
    }

    private void OnInteractionSurfaceGotFocus(object sender, RoutedEventArgs args)
    {
        _isFocused = true;
        UpdateDetailVisibility();
    }

    private void OnInteractionSurfaceLostFocus(object sender, RoutedEventArgs args)
    {
        _isFocused = false;
        UpdateDetailVisibility();
    }

    private void UpdateInteractionSurface()
    {
        var isInteractive = !DetailsAlwaysVisible && _canExpand;
        InteractionSurface.IsHitTestVisible = isInteractive;
        InteractionSurface.IsTabStop = isInteractive;
        AutomationProperties.SetName(InteractionSurface, "费用构成");
        AutomationProperties.SetHelpText(
            InteractionSurface,
            isInteractive
                ? "悬停或键盘聚焦可读取费用构成百分比。"
                : "当前没有可计费的费用构成。");
    }

    private void UpdateDetailVisibility()
    {
        var isExpanded = DetailsAlwaysVisible || (_canExpand && (_isPointerOver || _isFocused));
        InlineDetail.Opacity = isExpanded ? 1 : 0;
        AutomationProperties.SetAccessibilityView(
            InlineDetail,
            isExpanded ? AccessibilityView.Content : AccessibilityView.Raw);
        UpdateInteractionSurface();
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

    private static string DetailText(IEnumerable<CostSlice> items) =>
        string.Join(
            " · ",
            items.Select(item => $"{item.Label} {item.EntityShare}"));

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
