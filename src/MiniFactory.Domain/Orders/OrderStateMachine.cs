using MiniFactory.Domain.Common;

namespace MiniFactory.Domain.Orders;

/// <summary>訂單狀態機（architecture.md §9.1）。</summary>
public static class OrderStateMachine
{
    private static readonly TransitionTable<OrderStatus> Table = new("order", new Dictionary<OrderStatus, OrderStatus[]>
    {
        [OrderStatus.Created] = [OrderStatus.InProgress, OrderStatus.AwaitingConfirmation, OrderStatus.NeedsReview],
        [OrderStatus.InProgress] = [OrderStatus.AwaitingConfirmation, OrderStatus.Completed, OrderStatus.NeedsReview],
        [OrderStatus.AwaitingConfirmation] = [OrderStatus.InProgress, OrderStatus.Completed, OrderStatus.NeedsReview],
        // Completed / NeedsReview 為終態
    });

    public static bool CanTransition(OrderStatus from, OrderStatus to) => Table.CanTransition(from, to);

    public static bool IsTerminal(OrderStatus status) => Table.IsTerminal(status);

    public static IReadOnlyCollection<OrderStatus> NextStates(OrderStatus status) => Table.NextStates(status);

    /// <summary>驗證轉換；合法回傳目標狀態，非法回傳 order.invalid_transition。</summary>
    public static Result<OrderStatus> TryTransition(OrderStatus from, OrderStatus to) => Table.TryTransition(from, to);
}
