using MiniFactory.Domain.Common;

namespace MiniFactory.Domain.Jobs;

/// <summary>執行紀錄狀態機（architecture.md §9.3）。</summary>
public static class ExecutionStateMachine
{
    private static readonly TransitionTable<ExecutionStatus> Table = new("execution", new Dictionary<ExecutionStatus, ExecutionStatus[]>
    {
        [ExecutionStatus.Pending] = [ExecutionStatus.Dispatched],
        [ExecutionStatus.Dispatched] =
        [
            ExecutionStatus.Accepted, ExecutionStatus.Running, ExecutionStatus.Succeeded,
            ExecutionStatus.Failed, ExecutionStatus.Rejected, ExecutionStatus.Unknown,
        ],
        [ExecutionStatus.Accepted] =
        [
            ExecutionStatus.Running, ExecutionStatus.Succeeded, ExecutionStatus.Failed, ExecutionStatus.Unknown,
        ],
        [ExecutionStatus.Running] =
        [
            ExecutionStatus.Succeeded, ExecutionStatus.Failed, ExecutionStatus.Unknown,
        ],
        [ExecutionStatus.Unknown] =
        [
            ExecutionStatus.Accepted, ExecutionStatus.Running, ExecutionStatus.Succeeded,
            ExecutionStatus.Failed, ExecutionStatus.Rejected, ExecutionStatus.Abandoned,
        ],
        // Succeeded / Failed / Rejected / Abandoned 為終態
    });

    public static bool CanTransition(ExecutionStatus from, ExecutionStatus to) => Table.CanTransition(from, to);

    public static bool IsTerminal(ExecutionStatus status) => Table.IsTerminal(status);

    public static IReadOnlyCollection<ExecutionStatus> NextStates(ExecutionStatus status) => Table.NextStates(status);

    public static Result<ExecutionStatus> TryTransition(ExecutionStatus from, ExecutionStatus to)
        => Table.TryTransition(from, to);

    /// <summary>
    /// 「確認未執行或已安全終止」= Failed 或 Rejected，只有這兩種才可建立新 attempt（新 commandId）。
    /// 其餘情況（含 Unknown、Abandoned）重送必須沿用同一 commandId。
    /// </summary>
    public static bool AllowsNewAttempt(ExecutionStatus status)
        => status is ExecutionStatus.Failed or ExecutionStatus.Rejected;
}
