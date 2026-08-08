using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexUsage.Domain;

namespace CodexUsage.Application;

public abstract class DashboardPresentationItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetValue<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MetricCard(string label, string value) : DashboardPresentationItem
{
    private string _value = value;

    public string Label { get; } = label;
    public string Value { get => _value; private set => SetValue(ref _value, value); }

    public void UpdateFrom(MetricCard source) => Value = source.Value;
}

public sealed class CostSlice(string label, double percentage, string detail, string brushKey) : DashboardPresentationItem
{
    private double _percentage = percentage;
    private string _detail = detail;

    public string Label { get; } = label;
    public double Percentage { get => _percentage; private set => SetValue(ref _percentage, value); }
    public string Detail { get => _detail; private set => SetValue(ref _detail, value); }
    public string BrushKey { get; } = brushKey;

    public void UpdateFrom(CostSlice source)
    {
        Percentage = source.Percentage;
        Detail = source.Detail;
    }
}

public sealed class ModelUsageRow(
    string model,
    string totalTokens,
    string uncachedInput,
    string cachedInput,
    string output,
    string reasoningOutput,
    string cost,
    string share) : DashboardPresentationItem
{
    private string _totalTokens = totalTokens;
    private string _uncachedInput = uncachedInput;
    private string _cachedInput = cachedInput;
    private string _output = output;
    private string _reasoningOutput = reasoningOutput;
    private string _cost = cost;
    private string _share = share;

    public string Model { get; } = model;
    public string TotalTokens { get => _totalTokens; private set => SetValue(ref _totalTokens, value); }
    public string UncachedInput { get => _uncachedInput; private set => SetValue(ref _uncachedInput, value); }
    public string CachedInput { get => _cachedInput; private set => SetValue(ref _cachedInput, value); }
    public string Output { get => _output; private set => SetValue(ref _output, value); }
    public string ReasoningOutput { get => _reasoningOutput; private set => SetValue(ref _reasoningOutput, value); }
    public string Cost { get => _cost; private set => SetValue(ref _cost, value); }
    public string Share { get => _share; private set => SetValue(ref _share, value); }

    public void UpdateFrom(ModelUsageRow source)
    {
        TotalTokens = source.TotalTokens;
        UncachedInput = source.UncachedInput;
        CachedInput = source.CachedInput;
        Output = source.Output;
        ReasoningOutput = source.ReasoningOutput;
        Cost = source.Cost;
        Share = source.Share;
    }
}

public sealed class SubjectUsageRow(
    string threadType,
    string role,
    string threadCount,
    string totalTokens,
    string uncachedInput,
    string cachedInput,
    string output,
    string reasoningOutput,
    string cost,
    string share) : DashboardPresentationItem
{
    private string _threadCount = threadCount;
    private string _totalTokens = totalTokens;
    private string _uncachedInput = uncachedInput;
    private string _cachedInput = cachedInput;
    private string _output = output;
    private string _reasoningOutput = reasoningOutput;
    private string _cost = cost;
    private string _share = share;

    public string ThreadType { get; } = threadType;
    public string Role { get; } = role;
    public string ThreadCount { get => _threadCount; private set => SetValue(ref _threadCount, value); }
    public string TotalTokens { get => _totalTokens; private set => SetValue(ref _totalTokens, value); }
    public string UncachedInput { get => _uncachedInput; private set => SetValue(ref _uncachedInput, value); }
    public string CachedInput { get => _cachedInput; private set => SetValue(ref _cachedInput, value); }
    public string Output { get => _output; private set => SetValue(ref _output, value); }
    public string ReasoningOutput { get => _reasoningOutput; private set => SetValue(ref _reasoningOutput, value); }
    public string Cost { get => _cost; private set => SetValue(ref _cost, value); }
    public string Share { get => _share; private set => SetValue(ref _share, value); }

    public void UpdateFrom(SubjectUsageRow source)
    {
        ThreadCount = source.ThreadCount;
        TotalTokens = source.TotalTokens;
        UncachedInput = source.UncachedInput;
        CachedInput = source.CachedInput;
        Output = source.Output;
        ReasoningOutput = source.ReasoningOutput;
        Cost = source.Cost;
        Share = source.Share;
    }
}

public sealed class DiagnosticRow(string label, string value, string detail) : DashboardPresentationItem
{
    private string _value = value;
    private string _detail = detail;

    public string Label { get; } = label;
    public string Value { get => _value; private set => SetValue(ref _value, value); }
    public string Detail { get => _detail; private set => SetValue(ref _detail, value); }

    public void UpdateFrom(DiagnosticRow source)
    {
        Value = source.Value;
        Detail = source.Detail;
    }
}

public sealed class ModelFilterOption(string model, bool isSelected = true) : DashboardPresentationItem
{
    private bool _isSelected = isSelected;

    public string Model { get; } = model;
    public string Label => Model;
    public string SelectionGlyph => IsSelected ? "\uE73E" : string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetValue(ref _isSelected, value)) return;
            OnSelectionGlyphChanged();
        }
    }

    public void UpdateFrom(ModelFilterOption _)
    {
    }

    private void OnSelectionGlyphChanged() => RaisePropertyChanged(nameof(SelectionGlyph));
}

public sealed class SubjectFilterOption(SubjectFilter subject, bool isSelected = true) : DashboardPresentationItem
{
    private bool _isSelected = isSelected;

    public SubjectFilter Subject { get; } = subject;
    public string Label => Subject.AgentRole;
    public string SelectionGlyph => IsSelected ? "\uE73E" : string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetValue(ref _isSelected, value)) return;
            OnSelectionGlyphChanged();
        }
    }

    public void UpdateFrom(SubjectFilterOption _)
    {
    }

    private void OnSelectionGlyphChanged() => RaisePropertyChanged(nameof(SelectionGlyph));
}

public sealed class MainThreadFilterOption(MainThreadOption option) : DashboardPresentationItem
{
    private string _projectName = option.ProjectName;
    private string _title = option.Title;
    private DateTimeOffset _lastActivityUtc = option.LastActivityUtc;

    public string ConversationId { get; } = option.ConversationId;
    public string ProjectName => _projectName;
    public string Title => _title;
    public DateTimeOffset LastActivityUtc => _lastActivityUtc;
    public string DisplayLabel => $"{ProjectName} - {ConversationId[..Math.Min(ConversationId.Length, 8)]} - {(Title.Length > 0 ? Title : "未命名线程")}";

    public void UpdateFrom(MainThreadFilterOption source)
    {
        if (SetValue(ref _projectName, source.ProjectName)) RaisePropertyChanged(nameof(DisplayLabel));
        if (SetValue(ref _title, source.Title)) RaisePropertyChanged(nameof(DisplayLabel));
        SetValue(ref _lastActivityUtc, source.LastActivityUtc);
    }
}

public sealed record DashboardPresentationInput(
    IReadOnlyList<MetricCard> Metrics,
    IReadOnlyList<CostSlice> CostSlices,
    IReadOnlyList<ModelUsageRow> Models,
    IReadOnlyList<SubjectUsageRow> Subjects,
    IReadOnlyList<DiagnosticRow> Diagnostics,
    IReadOnlyList<ModelFilterOption> ModelOptions,
    IReadOnlyList<SubjectFilterOption> AgentOptions);

public readonly record struct DashboardPresentationApplyResult(bool HasStructuralChanges);

public sealed class DashboardPresentationCollections
{
    public DashboardPresentationCollections()
    {
        Metrics = new ObservableCollection<MetricCard>
        {
            new("总 tokens", "0"),
            new("输入", "0"),
            new("输出", "0"),
            new("未定价", "0"),
            new("费用", "$0.0"),
        };
        CostSlices = new ObservableCollection<CostSlice>
        {
            new("无缓存输入", 0, "0.0%", "PrimaryBrush"),
            new("缓存输入", 0, "0.0%", "SuccessBrush"),
            new("思考输出", 0, "0.0%", "WarningBrush"),
            new("其他输出", 0, "0.0%", "PurpleBrush"),
        };
        Models = [];
        Subjects = [];
        Diagnostics = [];
        ModelOptions = [];
        AgentOptions = [];
    }

    public ObservableCollection<MetricCard> Metrics { get; }
    public ObservableCollection<CostSlice> CostSlices { get; }
    public ObservableCollection<ModelUsageRow> Models { get; }
    public ObservableCollection<SubjectUsageRow> Subjects { get; }
    public ObservableCollection<DiagnosticRow> Diagnostics { get; }
    public ObservableCollection<ModelFilterOption> ModelOptions { get; }
    public ObservableCollection<SubjectFilterOption> AgentOptions { get; }

    public bool WouldApplyHaveStructuralChanges(DashboardPresentationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return WouldSynchronizeRows(Metrics, input.Metrics, static value => value.Label)
            || WouldSynchronizeRows(CostSlices, input.CostSlices, static value => value.Label)
            || WouldSynchronizeRows(Models, input.Models, static value => value.Model)
            || WouldSynchronizeRows(Subjects, input.Subjects, static value => (value.ThreadType, value.Role))
            || WouldSynchronizeRows(Diagnostics, input.Diagnostics, static value => value.Label)
            || WouldSynchronizeRows(ModelOptions, input.ModelOptions, static value => value.Model)
            || WouldSynchronizeRows(AgentOptions, input.AgentOptions, static value => value.Subject);
    }

    public DashboardPresentationApplyResult Apply(DashboardPresentationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var hasStructuralChanges = false;
        hasStructuralChanges |= SynchronizeRows(Metrics, input.Metrics, static value => value.Label, static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
        hasStructuralChanges |= SynchronizeRows(CostSlices, input.CostSlices, static value => value.Label, static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
        hasStructuralChanges |= SynchronizeRows(Models, input.Models, static value => value.Model, static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
        hasStructuralChanges |= SynchronizeRows(Subjects, input.Subjects, static value => (value.ThreadType, value.Role), static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
        hasStructuralChanges |= SynchronizeRows(Diagnostics, input.Diagnostics, static value => value.Label, static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
        hasStructuralChanges |= SynchronizeRows(ModelOptions, input.ModelOptions, static value => value.Model, static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
        hasStructuralChanges |= SynchronizeRows(AgentOptions, input.AgentOptions, static value => value.Subject, static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
        return new DashboardPresentationApplyResult(hasStructuralChanges);
    }

    public void UpdateDiagnosticsSubset(IReadOnlyList<DiagnosticRow> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        foreach (var incoming in replacement)
        {
            var current = Diagnostics.FirstOrDefault(value => string.Equals(
                value.Label,
                incoming.Label,
                StringComparison.Ordinal));
            if (current is null) Diagnostics.Add(incoming);
            else current.UpdateFrom(incoming);
        }
    }

    private static DashboardCollectionSynchronizationResult SynchronizeRows<TItem, TKey>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TItem> replacement,
        Func<TItem, TKey> keySelector,
        Action<TItem, TItem> update)
        where TKey : notnull => DashboardCollectionReconciler.Synchronize(target, replacement, keySelector, update);

    private static bool WouldSynchronizeRows<TItem, TKey>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TItem> replacement,
        Func<TItem, TKey> keySelector)
        where TKey : notnull => DashboardCollectionReconciler.WouldRequireStructuralChanges(
            target,
            replacement,
            keySelector);
}
