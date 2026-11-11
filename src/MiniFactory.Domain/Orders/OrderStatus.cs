namespace MiniFactory.Domain.Orders;

/// <summary>訂單狀態（列舉名稱即 DB 儲存字串，見 database.md §3.1 CHECK）。</summary>
public enum OrderStatus
{
    Created,
    InProgress,
    AwaitingConfirmation,
    Completed,
    NeedsReview,
}
