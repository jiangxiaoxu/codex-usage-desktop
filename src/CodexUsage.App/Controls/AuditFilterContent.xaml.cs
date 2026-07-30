using CodexUsage.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
}
