using System.Text.Json;
using MiniFactory.Domain.Orders;

namespace MiniFactory.Application.Orders;

/// <summary>建單請求 body（api.md §3.1）。數值欄位用可為 null 的型別，才能分辨「沒填」與「填 0」。</summary>
public sealed record CreateOrderRequest(string? OrderNo, string? ProductCode, int? Quantity, string? LineId);

/// <param name="Request">請求內容；沒有 body 時為 null（驗證會回 400）。</param>
/// <param name="IdempotencyKey">選填；通過驗證前不可信任。</param>
public sealed record CreateOrderCommand(
    CreateOrderRequest? Request,
    string? IdempotencyKey,
    string CorrelationId,
    string? TraceParent,
    string? TraceState);

/// <summary>建單派工事件的發送狀態（api.md §3.1）。</summary>
public enum DispatchStatus
{
    /// <summary>RabbitMQ 連線正常，事件將由 Outbox 發送。</summary>
    Queued,

    /// <summary>RabbitMQ 不可用，事件暫存 Outbox，恢復後補送。</summary>
    Pending,
}

/// <summary>202 回應 body；同時也是 Idempotency 儲存的「第一次回應」。</summary>
public sealed record CreateOrderResponse(
    Guid OrderId,
    string OrderNo,
    OrderStatus Status,
    DispatchStatus DispatchStatus,
    IReadOnlyList<Guid> JobIds,
    DateTimeOffset CreatedAt);

/// <param name="Replayed">true 代表同 Key 同內容的重送，內容是第一次儲存的回應。</param>
public sealed record CreateOrderResult(CreateOrderResponse Response, bool Replayed);

// ---- 查詢 ----

public sealed record OrderListFilter(IReadOnlyList<OrderStatus> Statuses, string? OrderNoPrefix);

public sealed record OrderSummaryDto(
    Guid Id,
    string OrderNo,
    string ProductCode,
    int Quantity,
    string LineId,
    string Status,
    int CompletedJobs,
    int TotalJobs,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EquipmentRefDto(Guid Id, string Name);

public sealed record ExecutionDto(
    int AttemptNo,
    Guid CommandId,
    string Status,
    EquipmentRefDto? Equipment,
    int SendCount,
    DateTimeOffset? LastSentAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode);

public sealed record JobDto(
    Guid Id,
    int Sequence,
    string Operation,
    string Status,
    EquipmentRefDto? AssignedEquipment,
    int CurrentAttemptNo,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<ExecutionDto> Executions);

/// <param name="TraceId">建單 Trace 的 trace-id（32 hex），前端組 Jaeger 連結用。</param>
public sealed record OrderDetailDto(
    Guid Id,
    string OrderNo,
    string ProductCode,
    int Quantity,
    string LineId,
    string Status,
    int Version,
    string? TraceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<JobDto> Jobs);

public sealed record OrderEventDto(Guid EventId, string EventType, int EventVersion, DateTimeOffset OccurredAt, JsonElement Payload);

public sealed record OrderEventsResponse(Guid OrderId, IReadOnlyList<OrderEventDto> Items);
