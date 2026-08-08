using CodexUsage.App.ViewModels;
using CodexUsage.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexUsage.App.Controls;

public sealed partial class AuditFilterContent : UserControl
{
    public AuditFilterContent()
    {
        InitializeComponent();
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

    private void OnSelectAllModels(object sender, RoutedEventArgs args)
    {
        if (DataContext is DashboardViewModel viewModel)
        {
            viewModel.SelectAllModels();
        }
    }

    private void OnSelectAllAgents(object sender, RoutedEventArgs args)
    {
        if (DataContext is DashboardViewModel viewModel)
        {
            viewModel.SelectAllAgents();
        }
    }

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
}
