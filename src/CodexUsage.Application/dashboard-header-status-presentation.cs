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
    string Text,
    string Glyph,
    DashboardHeaderStatusTone Tone)
{
    public static DashboardHeaderStatusPresentation From(CollectorPhase phase) => phase switch
    {
        CollectorPhase.Watching => new("正在监测", "\uE73E", DashboardHeaderStatusTone.Success),
        CollectorPhase.Partial => new("正在监测 · 部分数据可能不完整", "\uE7BA", DashboardHeaderStatusTone.Warning),
        CollectorPhase.Syncing => new("正在同步", "\uE895", DashboardHeaderStatusTone.Accent),
        CollectorPhase.Retrying => new("正在更新数据", "\uE72C", DashboardHeaderStatusTone.Accent),
        CollectorPhase.Degraded => new("需要关注", "\uE7BA", DashboardHeaderStatusTone.Danger),
        CollectorPhase.Stopped => new("已暂停", "\uE711", DashboardHeaderStatusTone.Muted),
        _ => new("正在启动", "\uE895", DashboardHeaderStatusTone.Muted),
    };
}
