using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexUsage.App.Controls;

public sealed partial class CostRow : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(CostRow),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail),
        typeof(string),
        typeof(CostRow),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(CostRow),
        new PropertyMetadata(0d));

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth),
        typeof(double),
        typeof(CostRow),
        new PropertyMetadata(100d));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush),
        typeof(Brush),
        typeof(CostRow),
        new PropertyMetadata(null));

    public static readonly DependencyProperty DetailVisibilityProperty = DependencyProperty.Register(
        nameof(DetailVisibility),
        typeof(Visibility),
        typeof(CostRow),
        new PropertyMetadata(Visibility.Visible));

    public CostRow()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double LabelWidth
    {
        get => (double)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Visibility DetailVisibility
    {
        get => (Visibility)GetValue(DetailVisibilityProperty);
        set => SetValue(DetailVisibilityProperty, value);
    }
}
