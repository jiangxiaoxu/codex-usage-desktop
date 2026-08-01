namespace CodexUsage.Application;

public readonly record struct DashboardViewportRestoreTicket(
    long Generation,
    long UserInteractionGeneration,
    double VerticalOffset);

/// <summary>
/// Coordinates a single pending restoration for dashboard data refreshes.
/// A later refresh supersedes an earlier one, and any user scroll intent
/// observed after capture invalidates the restoration.
/// </summary>
public sealed class DashboardViewportRestoreCoordinator
{
    private long _latestGeneration;
    private long _userInteractionGeneration;
    private DashboardViewportRestoreTicket? _pending;

    public DashboardViewportRestoreTicket? PrepareForDataRefresh(
        bool hasStructuralChanges,
        Func<double> verticalOffsetProvider)
    {
        ArgumentNullException.ThrowIfNull(verticalOffsetProvider);

        if (!hasStructuralChanges)
        {
            return null;
        }

        var verticalOffset = verticalOffsetProvider();
        var logicalVerticalOffset = _pending is { } pending
            && pending.UserInteractionGeneration == _userInteractionGeneration
            ? pending.VerticalOffset
            : NormalizeVerticalOffset(verticalOffset);
        long generation;
        checked
        {
            generation = ++_latestGeneration;
        }
        var ticket = new DashboardViewportRestoreTicket(
            generation,
            _userInteractionGeneration,
            logicalVerticalOffset);
        _pending = ticket;
        return ticket;
    }

    public void RecordUserInteraction()
    {
        checked
        {
            ++_userInteractionGeneration;
        }
    }

    public void InvalidatePendingRestoration()
    {
        checked
        {
            ++_latestGeneration;
        }
        _pending = null;
    }

    public bool TryConsumeLatest(
        DashboardViewportRestoreTicket ticket,
        out double verticalOffset)
    {
        if (_pending is not { } pending
            || pending.Generation != ticket.Generation
            || ticket.Generation != _latestGeneration)
        {
            verticalOffset = 0;
            return false;
        }

        _pending = null;
        if (ticket.UserInteractionGeneration != _userInteractionGeneration)
        {
            verticalOffset = 0;
            return false;
        }

        verticalOffset = ticket.VerticalOffset;
        return true;
    }

    private static double NormalizeVerticalOffset(double verticalOffset) => double.IsFinite(verticalOffset)
        ? Math.Max(0, verticalOffset)
        : 0;
}

public readonly record struct DashboardViewportRefreshTransition(
    bool SubscribeLayoutUpdated,
    bool UnsubscribeLayoutUpdated,
    double? VerticalOffsetToRestore);

/// <summary>
/// Owns viewport restoration across snapshot application, layout and user input.
/// Same-structure data refreshes are deliberate no-ops so they cannot disturb a
/// pending structural refresh or read from the UI scroll viewer.
/// </summary>
public sealed class DashboardViewportRefreshLifecycle
{
    private readonly DashboardViewportRestoreCoordinator _restoration = new();
    private CapturedViewportRestore? _captured;
    private DashboardViewportRestoreTicket? _pending;
    private bool _layoutUpdatedSubscribed;

    public DashboardViewportRefreshTransition BeginSnapshotApplication(
        DashboardSnapshotApplicationEventArgs application,
        Func<double> verticalOffsetProvider)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(verticalOffsetProvider);

        if (application.Purpose != DashboardSnapshotApplyPurpose.DataRefresh)
            return Cancel();

        if (!application.HasStructuralChanges)
            return default;

        var unsubscribeLayoutUpdated = _layoutUpdatedSubscribed;
        _layoutUpdatedSubscribed = false;
        _pending = null;
        var ticket = _restoration.PrepareForDataRefresh(true, verticalOffsetProvider)
            ?? throw new InvalidOperationException("Structural dashboard refresh did not capture a viewport ticket.");
        _captured = new CapturedViewportRestore(application.ApplicationGeneration, ticket);
        return new(false, unsubscribeLayoutUpdated, null);
    }

    public DashboardViewportRefreshTransition CompleteSnapshotApplication(
        DashboardSnapshotApplicationEventArgs application)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (application.Purpose != DashboardSnapshotApplyPurpose.DataRefresh)
            return Cancel();

        if (!application.HasStructuralChanges)
            return default;

        if (_captured is not { } captured
            || captured.ApplicationGeneration != application.ApplicationGeneration)
        {
            return default;
        }

        _captured = null;
        _pending = captured.Ticket;
        if (_layoutUpdatedSubscribed) return default;

        _layoutUpdatedSubscribed = true;
        return new(true, false, null);
    }

    public DashboardViewportRefreshTransition CompleteLayout(double scrollableHeight)
    {
        if (!_layoutUpdatedSubscribed)
            return default;

        _layoutUpdatedSubscribed = false;
        var ticket = _pending;
        _pending = null;
        if (ticket is not { } pending
            || !_restoration.TryConsumeLatest(pending, out var capturedOffset))
        {
            return new(false, true, null);
        }

        var usableScrollableHeight = double.IsFinite(scrollableHeight)
            ? Math.Max(0, scrollableHeight)
            : 0;
        return new(
            false,
            true,
            Math.Clamp(capturedOffset, 0, usableScrollableHeight));
    }

    public DashboardViewportRefreshTransition RecordUserInteraction()
    {
        _restoration.RecordUserInteraction();
        _captured = null;
        _pending = null;
        if (!_layoutUpdatedSubscribed) return default;

        _layoutUpdatedSubscribed = false;
        return new(false, true, null);
    }

    public DashboardViewportRefreshTransition Cancel()
    {
        _restoration.InvalidatePendingRestoration();
        _captured = null;
        _pending = null;
        if (!_layoutUpdatedSubscribed) return default;

        _layoutUpdatedSubscribed = false;
        return new(false, true, null);
    }

    private readonly record struct CapturedViewportRestore(
        long ApplicationGeneration,
        DashboardViewportRestoreTicket Ticket);
}
