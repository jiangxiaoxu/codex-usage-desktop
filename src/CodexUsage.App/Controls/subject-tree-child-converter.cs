using CodexUsage.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace CodexUsage.App.Controls;

public sealed class SubjectTreeChildConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isSubagent = value is SubjectUsageRow
        {
            Kind: SubjectUsageRowKind.Role,
            ThreadType: "子代理",
        };
        return string.Equals(parameter as string, "guide", StringComparison.Ordinal)
            ? isSubagent ? Visibility.Visible : Visibility.Collapsed
            : isSubagent ? new Thickness(28, 0, 0, 0) : new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
