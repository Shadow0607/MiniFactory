namespace MiniFactory.Application.Orders;

/// <summary>建單 Idempotency 設定（architecture.md §14.10：<c>Idempotency:RetentionHours</c> 預設 24）。</summary>
public sealed class IdempotencyOptions
{
    public int RetentionHours { get; set; } = 24;
}
