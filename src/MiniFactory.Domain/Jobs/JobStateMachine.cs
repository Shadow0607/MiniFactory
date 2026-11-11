using MiniFactory.Domain.Common;

namespace MiniFactory.Domain.Jobs;

/// <summary>Job 狀態機（architecture.md §9.2）。</summary>
public static class JobStateMachine
{
    private static readonly TransitionTable<JobStatus> Table = new("job", new Dictionary<JobStatus, JobStatus[]>
    {
        [JobStatus.Pending] = [JobStatus.Ready],
        [JobStatus.Ready] = [JobStatus.Dispatched],
        [JobStatus.Dispatched] =
        [
            JobStatus.Running, JobStatus.Completed, JobStatus.AwaitingConfirmation,
            JobStatus.Ready, JobStatus.NeedsReview,
        ],
        [JobStatus.Running] =
        [
            JobStatus.Completed, JobStatus.AwaitingConfirmation, JobStatus.Ready, JobStatus.NeedsReview,
        ],
        [JobStatus.AwaitingConfirmation] =
        [
            JobStatus.Running, JobStatus.Completed, JobStatus.Ready, JobStatus.NeedsReview,
        ],
        // Completed / NeedsReview 為終態：晚到或重複回報不可倒退
    });

    public static bool CanTransition(JobStatus from, JobStatus to) => Table.CanTransition(from, to);

    public static bool IsTerminal(JobStatus status) => Table.IsTerminal(status);

    public static IReadOnlyCollection<JobStatus> NextStates(JobStatus status) => Table.NextStates(status);

    public static Result<JobStatus> TryTransition(JobStatus from, JobStatus to) => Table.TryTransition(from, to);

    /// <summary>
    /// 此狀態下 Job 是否持有設備預留。預留在 Job 離開
    /// Dispatched / Running / AwaitingConfirmation 時才釋放（§9.2）。
    /// </summary>
    public static bool HoldsEquipment(JobStatus status)
        => status is JobStatus.Dispatched or JobStatus.Running or JobStatus.AwaitingConfirmation;
}
