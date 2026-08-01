using CodexUsage.Application;
using Xunit;

namespace CodexUsage.Application.Tests;

public sealed class ReleaseUpdateCheckScheduleTests
{
    [Fact]
    public void StartRequestsExactlyOneImmediateCheck()
    {
        var schedule = new ReleaseUpdateCheckSchedule(TimeSpan.FromHours(6));
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

        Assert.True(schedule.Start(now));
        Assert.False(schedule.Start(now));
    }

    [Fact]
    public void SixHourBoundaryRequestsOneFollowUpCheckAndKeepsTheScheduleAnchored()
    {
        var schedule = new ReleaseUpdateCheckSchedule(TimeSpan.FromHours(6));
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        _ = schedule.Start(now);

        Assert.False(schedule.IsDue(now.AddHours(5).AddMinutes(59).AddSeconds(59)));
        Assert.True(schedule.IsDue(now.AddHours(6)));
        Assert.False(schedule.IsDue(now.AddHours(6)));
        Assert.True(schedule.IsDue(now.AddHours(12)));
    }

    [Fact]
    public void LateWakeUpCoalescesMissedIntervalsWithoutReentry()
    {
        var schedule = new ReleaseUpdateCheckSchedule(TimeSpan.FromHours(6));
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        _ = schedule.Start(now);

        Assert.True(schedule.IsDue(now.AddHours(19)));
        Assert.False(schedule.IsDue(now.AddHours(19)));
        Assert.True(schedule.IsDue(now.AddHours(24)));
    }

    [Fact]
    public void CancelPreventsFutureScheduledChecks()
    {
        var schedule = new ReleaseUpdateCheckSchedule(TimeSpan.FromHours(6));
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        _ = schedule.Start(now);

        schedule.Cancel();

        Assert.False(schedule.IsDue(now.AddHours(6)));
        Assert.False(schedule.Start(now.AddHours(6)));
    }

    [Fact]
    public void CoordinatorPreventsOverlapAndRecoversAfterACompletedFailure()
    {
        var coordinator = new ReleaseUpdateCheckCoordinator();

        Assert.True(coordinator.TryBegin());
        Assert.False(coordinator.TryBegin());
        coordinator.Complete();
        Assert.True(coordinator.TryBegin());
    }

    [Fact]
    public void CoordinatorCancellationPreventsLaterChecks()
    {
        var coordinator = new ReleaseUpdateCheckCoordinator();

        coordinator.Cancel();

        Assert.False(coordinator.TryBegin());
    }

    [Fact]
    public void DownloadCoordinatorDisablesRapidDuplicateDownloads()
    {
        var coordinator = new ReleaseUpdateDownloadCoordinator();

        Assert.True(coordinator.TryBegin(out var first));
        Assert.False(coordinator.TryBegin(out _));
        Assert.True(coordinator.IsInFlight);
        coordinator.Complete(first);
        Assert.False(coordinator.IsInFlight);
    }

    [Fact]
    public void NewCheckInvalidatesAnOlderDownloadGeneration()
    {
        var coordinator = new ReleaseUpdateDownloadCoordinator();
        Assert.True(coordinator.TryBegin(out var oldDownload));

        coordinator.Invalidate();

        Assert.False(coordinator.IsCurrent(oldDownload));
        coordinator.Complete(oldDownload);
        Assert.True(coordinator.TryBegin(out var newDownload));
        Assert.True(coordinator.IsCurrent(newDownload));
    }

    [Fact]
    public void InstallerLaunchCoordinatorPreventsDuplicateConfirmationFlows()
    {
        var coordinator = new ReleaseUpdateInstallerLaunchCoordinator();

        Assert.True(coordinator.TryBegin());
        Assert.True(coordinator.IsInFlight);
        Assert.False(coordinator.TryBegin());
        coordinator.Complete();
        Assert.False(coordinator.IsInFlight);
        Assert.True(coordinator.TryBegin());
    }

}
