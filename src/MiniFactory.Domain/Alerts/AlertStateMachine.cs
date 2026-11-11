using MiniFactory.Domain.Common;

namespace MiniFactory.Domain.Alerts;

/// <summary>告警狀態機（architecture.md §9.5）：Open → Resolved，Resolved 為終態。</summary>
public static class AlertStateMachine
{
    private static readonly TransitionTable<AlertStatus> Table = new("alert", new Dictionary<AlertStatus, AlertStatus[]>
    {
        [AlertStatus.Open] = [AlertStatus.Resolved],
    });

    public static bool CanTransition(AlertStatus from, AlertStatus to) => Table.CanTransition(from, to);

    public static bool IsTerminal(AlertStatus status) => Table.IsTerminal(status);

    public static Result<AlertStatus> TryTransition(AlertStatus from, AlertStatus to) => Table.TryTransition(from, to);
}
