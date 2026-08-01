using System.Collections.Immutable;

namespace CodexUsage.Application;

public enum ShellResourceCleanupStep
{
    RemoveTrayIcon,
    RestoreWindowProcedure,
    DestroyOwnedIcon,
}

public static class ShellResourceCleanupPlan
{
    public static ImmutableArray<ShellResourceCleanupStep> OrderedSteps(
        bool trayIconAdded,
        bool windowProcedureInstalled,
        bool iconHandleOwned)
    {
        var steps = ImmutableArray.CreateBuilder<ShellResourceCleanupStep>(3);
        if (trayIconAdded) steps.Add(ShellResourceCleanupStep.RemoveTrayIcon);
        if (windowProcedureInstalled) steps.Add(ShellResourceCleanupStep.RestoreWindowProcedure);
        if (iconHandleOwned) steps.Add(ShellResourceCleanupStep.DestroyOwnedIcon);
        return steps.ToImmutable();
    }
}
