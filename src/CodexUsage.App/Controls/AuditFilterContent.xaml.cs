using System.Collections.Specialized;
using System.ComponentModel;
using CodexUsage.App.ViewModels;
using CodexUsage.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexUsage.App.Controls;

public sealed partial class AuditFilterContent : UserControl
{
    private const double WideLayoutMinimumWidth = 1280;
    private readonly List<DashboardPresentationItem> _subscribedFilterOptions = [];
    private bool _isLoaded;
    private DashboardViewModel? _subscribedViewModel;

    public AuditFilterContent()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            AttachFilterOptionSubscriptions();
            UpdateLayoutState();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            DetachFilterOptionSubscriptions();
        };
        DataContextChanged += (_, _) =>
        {
            DetachFilterOptionSubscriptions();
            if (_isLoaded) AttachFilterOptionSubscriptions();
            else UpdateSelectionLabels();
        };
        SizeChanged += (_, _) => UpdateLayoutState();
    }

    private void OnResetFilters(object sender, RoutedEventArgs args)
    {
        if (DataContext is DashboardViewModel viewModel)
        {
            viewModel.ResetFilters();
        }
    }

    private void OnClearMainThreadFilter(object sender, RoutedEventArgs args)
    {
        if (DataContext is DashboardViewModel viewModel)
        {
            viewModel.ClearMainThreadFilter();
        }
    }

    private void OnMainThreadSuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (DataContext is not DashboardViewModel viewModel
            || args.SelectedItem is not MainThreadFilterOption option)
        {
            return;
        }

        viewModel.SelectMainThreadOption(option);
    }

    private void OnMainThreadSuggestionsGotFocus(object sender, RoutedEventArgs args)
    {
        if (sender is AutoSuggestBox suggestionBox
            && DataContext is DashboardViewModel { MainThreadOptions.Count: > 0 })
        {
            suggestionBox.IsSuggestionListOpen = true;
        }
    }

    internal bool IsMainThreadSuggestionVisualSource(object? source) =>
        source is DependencyObject element
        && IsWithinMainThreadSuggestionsVisualTree(element);

    internal void CloseMainThreadSuggestions()
    {
        WideMainThreadSuggestions.IsSuggestionListOpen = false;
        CompactMainThreadSuggestions.IsSuggestionListOpen = false;
    }

    internal bool IsMainThreadSuggestionFocusedElement(object? focusedElement) =>
        focusedElement is DependencyObject element
        && IsWithinMainThreadSuggestionsVisualTree(element);

    private bool IsWithinMainThreadSuggestionsVisualTree(DependencyObject element)
    {
        for (DependencyObject? current = element;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, WideMainThreadSuggestions)
                || ReferenceEquals(current, CompactMainThreadSuggestions))
            {
                return true;
            }
        }

        return false;
    }

    private void OnSelectAllModels(object sender, RoutedEventArgs args)
    {
        if (DataContext is DashboardViewModel viewModel)
        {
            viewModel.SelectAllModels();
            UpdateSelectionLabels();
        }
    }

    private void OnSelectAllAgents(object sender, RoutedEventArgs args)
    {
        if (DataContext is DashboardViewModel viewModel)
        {
            viewModel.SelectAllAgents();
            UpdateSelectionLabels();
        }
    }

    private void OnModelOptionInvoked(object sender, RoutedEventArgs args) => UpdateSelectionLabels();

    private void OnAgentOptionInvoked(object sender, RoutedEventArgs args) => UpdateSelectionLabels();

    private async void OnOpenCustomDateRange(object sender, RoutedEventArgs args)
    {
        if (DataContext is not DashboardViewModel viewModel) return;

        var todaySgt = DateTimeOffset.UtcNow
            .ToOffset(DashboardCustomRange.SgtOffset)
            .Date;
        var startPicker = new CalendarDatePicker
        {
            Header = "开始日期 (SGT)",
            Date = viewModel.CustomStartDateSgt ?? new DateTimeOffset(
                todaySgt.AddDays(-1),
                DashboardCustomRange.SgtOffset),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var endPicker = new CalendarDatePicker
        {
            Header = "结束日期 (SGT, 不含)",
            Date = viewModel.CustomEndDateSgt ?? new DateTimeOffset(
                todaySgt.AddDays(1),
                DashboardCustomRange.SgtOffset),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var validationText = new TextBlock
        {
            FontSize = 13,
            Foreground = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["DangerBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var content = new StackPanel
        {
            MinWidth = 320,
            Spacing = 12,
        };
        content.Children.Add(new TextBlock
        {
            FontSize = 13,
            Text = "日期按 SGT 00:00 解析. 结束日期为不包含的查询边界.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(startPicker);
        content.Children.Add(endPicker);
        content.Children.Add(validationText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "自定义日期范围",
            Content = content,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, clickArgs) =>
        {
            if (viewModel.TryApplyCustomDateRange(
                    startPicker.Date,
                    endPicker.Date,
                    out var validationMessage))
            {
                return;
            }

            clickArgs.Cancel = true;
            validationText.Text = validationMessage;
        };

        await dialog.ShowAsync();
    }

    private void AttachFilterOptionSubscriptions()
    {
        if (_subscribedViewModel is not null || DataContext is not DashboardViewModel viewModel)
        {
            UpdateSelectionLabels();
            return;
        }

        _subscribedViewModel = viewModel;
        viewModel.ModelOptions.CollectionChanged += OnFilterOptionsCollectionChanged;
        viewModel.AgentOptions.CollectionChanged += OnFilterOptionsCollectionChanged;
        SubscribeToFilterOptions(viewModel);
        UpdateSelectionLabels();
    }

    private void DetachFilterOptionSubscriptions()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ModelOptions.CollectionChanged -= OnFilterOptionsCollectionChanged;
            _subscribedViewModel.AgentOptions.CollectionChanged -= OnFilterOptionsCollectionChanged;
        }

        foreach (var option in _subscribedFilterOptions)
        {
            option.PropertyChanged -= OnFilterOptionPropertyChanged;
        }

        _subscribedFilterOptions.Clear();
        _subscribedViewModel = null;
    }

    private void OnFilterOptionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_subscribedViewModel is null) return;

        foreach (var option in _subscribedFilterOptions)
        {
            option.PropertyChanged -= OnFilterOptionPropertyChanged;
        }

        _subscribedFilterOptions.Clear();
        SubscribeToFilterOptions(_subscribedViewModel);
        UpdateSelectionLabels();
    }

    private void SubscribeToFilterOptions(DashboardViewModel viewModel)
    {
        foreach (var option in viewModel.ModelOptions)
        {
            option.PropertyChanged += OnFilterOptionPropertyChanged;
            _subscribedFilterOptions.Add(option);
        }

        foreach (var option in viewModel.AgentOptions)
        {
            option.PropertyChanged += OnFilterOptionPropertyChanged;
            _subscribedFilterOptions.Add(option);
        }
    }

    private void OnFilterOptionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ModelFilterOption.IsSelected))
        {
            UpdateSelectionLabels();
        }
    }

    private void UpdateSelectionLabels()
    {
        var viewModel = _subscribedViewModel ?? DataContext as DashboardViewModel;
        var selectedModelCount = viewModel?.ModelOptions.Count(option => option.IsSelected) ?? 0;
        var selectedAgentCount = viewModel?.AgentOptions.Count(option => option.IsSelected) ?? 0;
        var modelText = $"{selectedModelCount} 已选";
        var agentText = $"{selectedAgentCount} 已选";

        WideModelSelectionText.Text = modelText;
        CompactModelSelectionText.Text = modelText;
        WideAgentSelectionText.Text = agentText;
        CompactAgentSelectionText.Text = agentText;
        AutomationProperties.SetName(WideModelSelectionButton, $"模型筛选, {modelText}");
        AutomationProperties.SetName(CompactModelSelectionButton, $"模型筛选, {modelText}");
        AutomationProperties.SetName(WideAgentSelectionButton, $"执行主体筛选, {agentText}");
        AutomationProperties.SetName(CompactAgentSelectionButton, $"执行主体筛选, {agentText}");
    }

    private void UpdateLayoutState()
    {
        var isWide = ActualWidth >= WideLayoutMinimumWidth;
        WideLayout.Visibility = isWide ? Visibility.Visible : Visibility.Collapsed;
        CompactLayout.Visibility = isWide ? Visibility.Collapsed : Visibility.Visible;
    }
}
