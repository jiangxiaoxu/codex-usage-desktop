using System.Collections.Specialized;
using System.ComponentModel;
using CodexUsage.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexUsage.App.Controls;

public sealed class CostCompositionBar : Panel
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items),
        typeof(IReadOnlyList<CostSlice>),
        typeof(CostCompositionBar),
        new PropertyMetadata(null, OnItemsChanged));

    private readonly List<CostSlice> _subscribedItems = [];
    private INotifyCollectionChanged? _collection;

    public IReadOnlyList<CostSlice>? Items
    {
        get => (IReadOnlyList<CostSlice>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(0, availableSize.Height));
        }

        return new Windows.Foundation.Size(0, double.IsNaN(Height) ? 8 : Height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        var weights = _subscribedItems
            .Select(item => double.IsFinite(item.Percentage) ? Math.Max(0, item.Percentage) : 0)
            .ToArray();
        var totalWeight = weights.Sum();
        var offset = 0d;

        for (var index = 0; index < Children.Count; index++)
        {
            var width = totalWeight > 0
                ? index == Children.Count - 1
                    ? Math.Max(0, finalSize.Width - offset)
                    : finalSize.Width * weights[index] / totalWeight
                : 0;
            Children[index].Arrange(new Windows.Foundation.Rect(offset, 0, width, finalSize.Height));
            offset += width;
        }

        return finalSize;
    }

    private static void OnItemsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((CostCompositionBar)dependencyObject).ResetItems();
    }

    private void ResetItems()
    {
        if (_collection is not null) _collection.CollectionChanged -= OnCollectionChanged;
        foreach (var item in _subscribedItems) item.PropertyChanged -= OnItemPropertyChanged;

        _subscribedItems.Clear();
        Children.Clear();
        _collection = Items as INotifyCollectionChanged;
        if (_collection is not null) _collection.CollectionChanged += OnCollectionChanged;

        if (Items is not null)
        {
            foreach (var item in Items)
            {
                _subscribedItems.Add(item);
                item.PropertyChanged += OnItemPropertyChanged;
                Children.Add(new Border { Background = ResolveBrush(item.BrushKey) });
            }
        }

        InvalidateMeasure();
        InvalidateArrange();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => ResetItems();

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(CostSlice.Percentage)) InvalidateArrange();
    }

    private static Brush? ResolveBrush(string key) => Microsoft.UI.Xaml.Application.Current.Resources[key] as Brush;
}
