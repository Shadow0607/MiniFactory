namespace MiniFactory.Domain.Jobs;

/// <summary>Job 狀態（database.md §3.2 CHECK）。</summary>
public enum JobStatus
{
    Pending,
    Ready,
    Dispatched,
    Running,
    AwaitingConfirmation,
    Completed,
    NeedsReview,
}
