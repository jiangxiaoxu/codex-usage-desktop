namespace CodexUsage.Application;

public enum DashboardSnapshotApplyPurpose
{
    InitialLoad,
    DataRefresh,
    UserFilter,
}

public sealed class DashboardSnapshotApplicationEventArgs(
    long applicationGeneration,
    DashboardSnapshotApplyPurpose purpose,
    bool hasStructuralChanges = false) : EventArgs
{
    public long ApplicationGeneration { get; } = applicationGeneration;
    public DashboardSnapshotApplyPurpose Purpose { get; } = purpose;
    public bool HasStructuralChanges { get; } = hasStructuralChanges;
    public bool RequiresVerticalViewportRestore => Purpose == DashboardSnapshotApplyPurpose.DataRefresh
        && HasStructuralChanges;
}

public sealed class DashboardSnapshotApplicationLifecycle
{
    private long _nextGeneration;

    public DashboardSnapshotApplicationEventArgs Begin(
        DashboardSnapshotApplyPurpose purpose,
        bool hasStructuralChanges) => new(
        checked(++_nextGeneration),
        purpose,
        hasStructuralChanges);

    public static DashboardSnapshotApplicationEventArgs Complete(
        DashboardSnapshotApplicationEventArgs application,
        DashboardPresentationApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(application);
        return new(
            application.ApplicationGeneration,
            application.Purpose,
            result.HasStructuralChanges);
    }
}
