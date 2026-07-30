using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexUsage.Domain;

namespace CodexUsage.App.Models;

public sealed record MetricCard(string Label, string Value);

public sealed record CostSlice(string Label, double Percentage, string Detail, string BrushKey);

public sealed record ModelUsageRow(
    string Model,
    string TotalTokens,
    string CachedInput,
    string Output,
    string Cost,
    string Share);

public sealed record RunStatistic(string Label, string Value);

public sealed record SubjectUsageRow(
    string ThreadType,
    string Role,
    string TotalTokens,
    string Output,
    string Cost,
    string Share);

public sealed record DiagnosticRow(string Label, string Value, string Detail);

public sealed class ModelFilterOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public ModelFilterOption(string model, bool isSelected = true)
    {
        Model = model;
        _isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Model { get; }

    public string Label => Model;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class SubjectFilterOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public SubjectFilterOption(SubjectFilter subject, bool isSelected = true)
    {
        Subject = subject;
        _isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SubjectFilter Subject { get; }

    public string Label => $"{UsageAccounting.ThreadTypeText(Subject.ThreadType)} · {Subject.AgentRole}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
