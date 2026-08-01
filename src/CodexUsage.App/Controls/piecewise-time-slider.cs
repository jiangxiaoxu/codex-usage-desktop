using CodexUsage.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace CodexUsage.App.Controls;

public sealed class PiecewiseTimeSlider : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(PiecewiseTimeSlider),
        new PropertyMetadata(1d, OnValueChanged));

    private readonly Grid _inputSurface;
    private readonly Border _fill;
    private readonly Thumb _thumb;
    private readonly TranslateTransform _thumbTransform;
    private double _dragPhysicalX;
    private bool _isCoercingValue;

    public PiecewiseTimeSlider()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        MinHeight = 40;
        AutomationProperties.SetAutomationId(this, "TimeRangeSlider");

        var track = new Border
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ResourceBrush("ProgressTrackBrush"),
            CornerRadius = new CornerRadius(2),
        };
        _fill = new Border
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ResourceBrush("PrimaryBrush"),
            CornerRadius = new CornerRadius(2),
        };
        _thumbTransform = new TranslateTransform();
        _thumb = new Thumb
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ResourceBrush("PrimaryBrush"),
            BorderBrush = ResourceBrush("PrimaryTextBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            IsTabStop = false,
            RenderTransform = _thumbTransform,
        };
        _inputSurface = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _inputSurface.Children.Add(track);
        _inputSurface.Children.Add(_fill);
        _inputSurface.Children.Add(_thumb);
        Content = _inputSurface;

        SizeChanged += (_, _) => UpdateVisualPosition();
        _inputSurface.Tapped += OnTrackTapped;
        _thumb.DragStarted += OnDragStarted;
        _thumb.DragDelta += OnDragDelta;
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            SetValue(ValueProperty, Math.Clamp(value, DashboardTimeRangeScale.MinimumPosition, DashboardTimeRangeScale.MaximumPosition));
        }
    }

    protected override void OnKeyDown(KeyRoutedEventArgs args)
    {
        if (!TryGetAdjustment(args.Key, out var adjustment))
        {
            base.OnKeyDown(args);
            return;
        }

        var hours = DashboardTimeRangeScale.PositionToHours(Value);
        Value = DashboardTimeRangeScale.HoursToPosition(DashboardTimeRangeScale.AdjustHours(hours, adjustment));
        args.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PiecewiseTimeSliderAutomationPeer(this);

    private static void OnValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (PiecewiseTimeSlider)dependencyObject;
        var rawValue = (double)args.NewValue;
        var previousValue = (double)args.OldValue;
        if (control._isCoercingValue)
        {
            control.UpdateVisualPosition();
            return;
        }

        var coercion = DashboardTimeRangeScale.CoercePositionInput(rawValue, previousValue);
        if (coercion.RequiresCorrection)
        {
            control._isCoercingValue = true;
            try
            {
                control.SetValue(ValueProperty, coercion.Position);
            }
            finally
            {
                control._isCoercingValue = false;
            }
            return;
        }

        control.UpdateVisualPosition();
        if (double.IsFinite(previousValue)
            && FrameworkElementAutomationPeer.FromElement(control) is PiecewiseTimeSliderAutomationPeer peer)
        {
            peer.RaiseValueChanged(
                DashboardTimeRangeScale.PositionToHours((double)args.OldValue),
                DashboardTimeRangeScale.PositionToHours(rawValue));
        }
    }

    private void OnTrackTapped(object sender, TappedRoutedEventArgs args)
    {
        Focus(FocusState.Pointer);
        Value = XToPosition(args.GetPosition(_inputSurface).X);
        args.Handled = true;
    }

    private void OnDragStarted(object sender, DragStartedEventArgs args)
    {
        Focus(FocusState.Pointer);
        _dragPhysicalX = PositionToPhysicalX(Value);
    }

    private void OnDragDelta(object sender, DragDeltaEventArgs args)
    {
        _dragPhysicalX = Math.Clamp(_dragPhysicalX + args.HorizontalChange, 0, TrackWidth);
        Value = XToPosition(_dragPhysicalX + (_thumb.ActualWidth / 2));
    }

    private void UpdateVisualPosition()
    {
        if (_inputSurface.ActualWidth <= 0) return;
        var physicalX = PositionToPhysicalX(Value);
        var logicalX = DashboardTimeRangeGeometry.PositionToPhysicalX(Value, TrackWidth, rightToLeft: false);
        _thumbTransform.X = physicalX;
        _fill.Width = logicalX + (_thumb.ActualWidth / 2);
        _fill.HorizontalAlignment = FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
    }

    private double PositionToPhysicalX(double position) => DashboardTimeRangeGeometry.PositionToPhysicalX(
        position,
        TrackWidth,
        FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft);

    private double XToPosition(double pointerX)
    {
        return DashboardTimeRangeGeometry.PhysicalXToPosition(
            pointerX - (_thumb.ActualWidth / 2),
            TrackWidth,
            FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft);
    }

    private double TrackWidth => Math.Max(_inputSurface.ActualWidth - _thumb.ActualWidth, 1);

    private bool TryGetAdjustment(VirtualKey key, out DashboardTimeRangeAdjustment adjustment)
    {
        adjustment = key switch
        {
            VirtualKey.Left => DashboardTimeRangeInput.DirectionalAdjustment(
                DashboardDirectionalKey.Left,
                FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft),
            VirtualKey.Right => DashboardTimeRangeInput.DirectionalAdjustment(
                DashboardDirectionalKey.Right,
                FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft),
            VirtualKey.Up => DashboardTimeRangeInput.DirectionalAdjustment(DashboardDirectionalKey.Up, rightToLeft: false),
            VirtualKey.Down => DashboardTimeRangeInput.DirectionalAdjustment(DashboardDirectionalKey.Down, rightToLeft: false),
            VirtualKey.PageDown => DashboardTimeRangeAdjustment.PreviousAnchor,
            VirtualKey.PageUp => DashboardTimeRangeAdjustment.NextAnchor,
            VirtualKey.Home => DashboardTimeRangeAdjustment.Minimum,
            VirtualKey.End => DashboardTimeRangeAdjustment.Maximum,
            _ => default,
        };
        return key is VirtualKey.Left
            or VirtualKey.Down
            or VirtualKey.Right
            or VirtualKey.Up
            or VirtualKey.PageDown
            or VirtualKey.PageUp
            or VirtualKey.Home
            or VirtualKey.End;
    }

    private static Brush ResourceBrush(string key) =>
        (Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];
}

internal sealed class PiecewiseTimeSliderAutomationPeer(PiecewiseTimeSlider owner)
    : FrameworkElementAutomationPeer(owner), IRangeValueProvider
{
    private PiecewiseTimeSlider SliderOwner => (PiecewiseTimeSlider)Owner;

    public bool IsReadOnly => !SliderOwner.IsEnabled;
    public double LargeChange => 0;
    public double Maximum => 336;
    public double Minimum => 0.5;
    public double SmallChange => 0.5;
    public double Value => DashboardTimeRangeScale.PositionToHours(SliderOwner.Value);

    public void SetValue(double value)
    {
        if (!SliderOwner.IsEnabled) throw new ElementNotEnabledException();
        if (!double.IsFinite(value) || value < Minimum || value > Maximum)
            throw new ArgumentOutOfRangeException(nameof(value));
        SliderOwner.Value = DashboardTimeRangeScale.HoursToPosition(value);
    }

    internal void RaiseValueChanged(double oldValue, double newValue)
    {
        if (oldValue != newValue)
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);
    }

    protected override string GetClassNameCore() => nameof(PiecewiseTimeSlider);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);
}
