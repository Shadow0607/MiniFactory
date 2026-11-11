namespace MiniFactory.Domain.Jobs;

/// <summary>執行紀錄狀態（database.md §3.3 CHECK）。</summary>
public enum ExecutionStatus
{
    Pending,
    Dispatched,
    Accepted,
    Running,
    Unknown,
    Succeeded,
    Failed,
    Rejected,
    Abandoned,
}
