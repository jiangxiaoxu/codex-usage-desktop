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

public enum DashboardCostCategory
{
    UncachedInput,
    CachedInput,
    ReasoningOutput,
    OtherOutput,
}

public enum CostPricingStatus
{
    Priced,
    PartiallyPriced,
    Unpriced,
}

public sealed class CostSlice(
    DashboardCostCategory category,
    string entityLabel,
    decimal costAmount,
    double entitySharePercentage,
    double overallSharePercentage,
    long tokenCount,
    CostPricingStatus pricingStatus,
    string brushKey) : DashboardPresentationItem
{
    private string _entityLabel = entityLabel;
    private decimal _costAmount = costAmount;
    private double _entitySharePercentage = entitySharePercentage;
    private double _overallSharePercentage = overallSharePercentage;
    private long _tokenCount = tokenCount;
    private CostPricingStatus _pricingStatus = pricingStatus;

    public DashboardCostCategory Category { get; } = category;
    public string Label => DashboardCostCategoryPresentation.Label(Category);
    public string EntityLabel { get => _entityLabel; private set => SetValue(ref _entityLabel, value); }
    public decimal CostAmount { get => _costAmount; private set => SetValue(ref _costAmount, value); }
    public string Cost => DashboardCostCategoryPresentation.FormatCost(CostAmount, PricingStatus);
    public double EntitySharePercentage { get => _entitySharePercentage; private set => SetValue(ref _entitySharePercentage, value); }
    public string EntityShare => DashboardCostCategoryPresentation.FormatPercentage(EntitySharePercentage, PricingStatus);
    public double OverallSharePercentage { get => _overallSharePercentage; private set => SetValue(ref _overallSharePercentage, value); }
    public string OverallShare => DashboardCostCategoryPresentation.FormatPercentage(OverallSharePercentage, PricingStatus);
    public long TokenCount { get => _tokenCount; private set => SetValue(ref _tokenCount, value); }
    public string Tokens => DashboardCostCategoryPresentation.FormatTokens(TokenCount);
    public CostPricingStatus PricingStatus { get => _pricingStatus; private set => SetValue(ref _pricingStatus, value); }
    public bool IsPriced => PricingStatus is not CostPricingStatus.Unpriced;
    public string BrushKey { get; } = brushKey;
    public double Percentage => EntitySharePercentage;
    public string ToolTipText => $"{EntityLabel} · {Label}\nToken 数  {Tokens}\n占该实体费用  {EntityShare}\n占当前筛选总费用  {OverallShare}\n费用  {Cost}";

    public void UpdateFrom(CostSlice source)
    {
        if (Category != source.Category)
            throw new ArgumentException("Cost slices can only update from the same category.", nameof(source));
        if (!string.Equals(BrushKey, source.BrushKey, StringComparison.Ordinal))
            throw new ArgumentException("Cost slices can only update from the same brush.", nameof(source));

        EntityLabel = source.EntityLabel;
        CostAmount = source.CostAmount;
        EntitySharePercentage = source.EntitySharePercentage;
        OverallSharePercentage = source.OverallSharePercentage;
        TokenCount = source.TokenCount;
        PricingStatus = source.PricingStatus;
        RaisePropertyChanged(nameof(Cost));
        RaisePropertyChanged(nameof(EntityShare));
        RaisePropertyChanged(nameof(OverallShare));
        RaisePropertyChanged(nameof(Tokens));
        RaisePropertyChanged(nameof(IsPriced));
        RaisePropertyChanged(nameof(Percentage));
        RaisePropertyChanged(nameof(ToolTipText));
    }
}

public sealed class ModelUsageRow(
    string model,
    long canonicalTotalTokens,
    string cost,
    string share,
    IReadOnlyList<CostSlice> costSlices) : DashboardPresentationItem
{
    private long _canonicalTotalTokens = canonicalTotalTokens;
    private string _cost = cost;
    private string _share = share;
    private IReadOnlyList<CostSlice> _costSlices = [.. costSlices];

    public string Model { get; } = model;
    public long CanonicalTotalTokens { get => _canonicalTotalTokens; private set => SetValue(ref _canonicalTotalTokens, value); }
    public string TotalTokens => DashboardCostCategoryPresentation.FormatTokens(CanonicalTotalTokens);
    public string TotalTokensAccessibilityName => $"总计 Token 数 {TotalTokens}, 费用占比 {Share}";
    public string Cost { get => _cost; private set => SetValue(ref _cost, value); }
    public string Share { get => _share; private set => SetValue(ref _share, value); }
    public IReadOnlyList<CostSlice> CostSlices { get => _costSlices; private set => SetValue(ref _costSlices, value); }

    public void UpdateFrom(ModelUsageRow source)
    {
        var totalTokensChanged = CanonicalTotalTokens != source.CanonicalTotalTokens;
        var shareChanged = !string.Equals(Share, source.Share, StringComparison.Ordinal);
        CanonicalTotalTokens = source.CanonicalTotalTokens;
        Cost = source.Cost;
        Share = source.Share;
        CostSlices = [.. source.CostSlices];
        if (totalTokensChanged) RaisePropertyChanged(nameof(TotalTokens));
        if (totalTokensChanged || shareChanged) RaisePropertyChanged(nameof(TotalTokensAccessibilityName));
    }
}

public enum SubjectUsageRowKind
{
    Role,
    SubagentAggregate,
}

public sealed class SubjectUsageRow(
    SubjectUsageRowKind kind,
    string key,
    string threadType,
    string role,
    long canonicalTotalTokens,
    string cost,
    string share,
    IReadOnlyList<CostSlice> costSlices) : DashboardPresentationItem
{
    private string _threadType = threadType;
    private string _role = role;
    private long _canonicalTotalTokens = canonicalTotalTokens;
    private string _cost = cost;
    private string _share = share;
    private IReadOnlyList<CostSlice> _costSlices = [.. costSlices];

    public SubjectUsageRowKind Kind { get; } = kind;
    public string Key { get; } = key;
    public string ThreadType { get => _threadType; private set => SetValue(ref _threadType, value); }
    public string Role { get => _role; private set => SetValue(ref _role, value); }
    public string DisplayName => Kind switch
    {
        SubjectUsageRowKind.SubagentAggregate => "子代理合计",
        _ when string.Equals(ThreadType, "子代理", StringComparison.Ordinal) => Role,
        _ => $"{ThreadType} · {Role}",
    };
    public long CanonicalTotalTokens { get => _canonicalTotalTokens; private set => SetValue(ref _canonicalTotalTokens, value); }
    public string TotalTokens => DashboardCostCategoryPresentation.FormatTokens(CanonicalTotalTokens);
    public string TotalTokensAccessibilityName => $"总计 Token 数 {TotalTokens}, 费用占比 {Share}";
    public string Cost { get => _cost; private set => SetValue(ref _cost, value); }
    public string Share { get => _share; private set => SetValue(ref _share, value); }
    public IReadOnlyList<CostSlice> CostSlices { get => _costSlices; private set => SetValue(ref _costSlices, value); }

    public void UpdateFrom(SubjectUsageRow source)
    {
        if (Kind != source.Kind || !string.Equals(Key, source.Key, StringComparison.Ordinal))
            throw new ArgumentException("Subject rows can only update from the same identity.", nameof(source));

        var displayNameChanged = ThreadType != source.ThreadType || Role != source.Role;
        var totalTokensChanged = CanonicalTotalTokens != source.CanonicalTotalTokens;
        var shareChanged = !string.Equals(Share, source.Share, StringComparison.Ordinal);
        ThreadType = source.ThreadType;
        Role = source.Role;
        if (displayNameChanged) RaisePropertyChanged(nameof(DisplayName));
        CanonicalTotalTokens = source.CanonicalTotalTokens;
        Cost = source.Cost;
        Share = source.Share;
        CostSlices = [.. source.CostSlices];
        if (totalTokensChanged) RaisePropertyChanged(nameof(TotalTokens));
        if (totalTokensChanged || shareChanged) RaisePropertyChanged(nameof(TotalTokensAccessibilityName));
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
    private const int DisplayConversationIdPrefixLength = 12;

    private string _projectName = option.ProjectName;
    private string _title = option.Title;
    private DateTimeOffset _lastActivityUtc = option.LastActivityUtc;

    public string ConversationId { get; } = option.ConversationId;
    public string ProjectName => _projectName;
    public string Title => _title;
    public DateTimeOffset LastActivityUtc => _lastActivityUtc;
    public string DisplayLabel => $"{ProjectName} - {ConversationId[..Math.Min(ConversationId.Length, DisplayConversationIdPrefixLength)]} - {(Title.Length > 0 ? Title : "未命名线程")}";

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
            new(DashboardCostCategory.UncachedInput, "总费用", 0, 0, 0, 0, CostPricingStatus.Priced, "PrimaryBrush"),
            new(DashboardCostCategory.CachedInput, "总费用", 0, 0, 0, 0, CostPricingStatus.Priced, "SuccessBrush"),
            new(DashboardCostCategory.ReasoningOutput, "总费用", 0, 0, 0, 0, CostPricingStatus.Priced, "WarningBrush"),
            new(DashboardCostCategory.OtherOutput, "总费用", 0, 0, 0, 0, CostPricingStatus.Priced, "PurpleBrush"),
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
            || WouldSynchronizeRows(CostSlices, input.CostSlices, static value => value.Category)
            || WouldSynchronizeRows(Models, input.Models, static value => value.Model)
            || WouldSynchronizeRows(Subjects, input.Subjects, static value => value.Key)
            || WouldSynchronizeRows(Diagnostics, input.Diagnostics, static value => value.Label)
            || WouldSynchronizeRows(ModelOptions, input.ModelOptions, static value => value.Model)
            || WouldSynchronizeRows(AgentOptions, input.AgentOptions, static value => value.Subject);
    }

    public DashboardPresentationApplyResult Apply(DashboardPresentationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var hasStructuralChanges = false;
        hasStructuralChanges |= SynchronizeRows(Metrics, input.Metrics, static value => value.Label, static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
        hasStructuralChanges |= SynchronizeRows(CostSlices, input.CostSlices, static value => value.Category, static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
        hasStructuralChanges |= SynchronizeRows(Models, input.Models, static value => value.Model, static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
        hasStructuralChanges |= SynchronizeRows(Subjects, input.Subjects, static value => value.Key, static (current, incoming) => current.UpdateFrom(incoming)).HasStructuralChanges;
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
