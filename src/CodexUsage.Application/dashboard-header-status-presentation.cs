using CodexUsage.Infrastructure.Collection;

namespace CodexUsage.Application;

public enum DashboardHeaderStatusTone
{
    Accent,
    Success,
    Warning,
    Danger,
    Muted,
}

public readonly record struct DashboardHeaderStatusPresentation(
    string Glyph,
    DashboardHeaderStatusTone Tone)
{
    public static DashboardHeaderStatusPresentation From(CollectorPhase phase) => phase switch
    {
        CollectorPhase.Watching => new("\uE73E", DashboardHeaderStatusTone.Success),
        CollectorPhase.Partial => new("\uE7BA", DashboardHeaderStatusTone.Warning),
        CollectorPhase.Syncing => new("\uE895", DashboardHeaderStatusTone.Accent),
        CollectorPhase.Degraded => new("\uE7BA", DashboardHeaderStatusTone.Danger),
        CollectorPhase.Stopped => new("\uE711", DashboardHeaderStatusTone.Muted),
        _ => new("\uE895", DashboardHeaderStatusTone.Muted),
    };
}
