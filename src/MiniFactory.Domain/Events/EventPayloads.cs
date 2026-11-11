using MiniFactory.Domain.Alerts;
using MiniFactory.Domain.Equipments;
using MiniFactory.Domain.Jobs;
using MiniFactory.Domain.Orders;

namespace MiniFactory.Domain.Events;

/// <summary>事件 payload 標記介面；EventType 由 payload 型別本身決定，避免型別字串與 payload 配錯。</summary>
public interface IEventPayload
{
    static abstract string EventType { get; }
}

// 欄位依 architecture.md §12 表格。只加欄位不改型別（eventVersion 第一版固定 1）。

public sealed record OrderCreatedPayload(Guid OrderId, string OrderNo, IReadOnlyList<Guid> JobIds) : IEventPayload
{
    public static string EventType => EventTypes.OrderCreated;
}

/// <param name="EquipmentId">第一版固定 null，由 Worker 挑選設備。</param>
public sealed record JobDispatchRequestedPayload(Guid JobId, Guid? EquipmentId, Guid CommandId, int AttemptNo) : IEventPayload
{
    public static string EventType => EventTypes.JobDispatchRequested;
}

public sealed record JobStartedPayload(Guid JobId, Guid CommandId, DateTimeOffset StartedAt) : IEventPayload
{
    public static string EventType => EventTypes.JobStarted;
}

/// <param name="NextJobId">最後一道工序為 null。</param>
public sealed record JobCompletedPayload(Guid JobId, Guid CommandId, DateTimeOffset CompletedAt, Guid? NextJobId) : IEventPayload
{
    public static string EventType => EventTypes.JobCompleted;
}

public sealed record JobFailedPayload(Guid JobId, Guid CommandId, string ErrorCode, int AttemptNo) : IEventPayload
{
    public static string EventType => EventTypes.JobFailed;
}

public sealed record EquipmentStatusChangedPayload(
    Guid EquipmentId,
    EquipmentStatus FromStatus,
    EquipmentStatus ToStatus,
    DateTimeOffset OccurredAt) : IEventPayload
{
    public static string EventType => EventTypes.EquipmentStatusChanged;
}

/// <param name="Reason">原因代碼，見 <see cref="AlertReasonExtensions.ToCode"/>。</param>
public sealed record AlertRaisedPayload(
    Guid AlertId,
    AlertSeverity Severity,
    string Reason,
    Guid? RelatedOrderId,
    Guid? RelatedEquipmentId) : IEventPayload
{
    public static string EventType => EventTypes.AlertRaised;
}

public sealed record JobStatusChangedPayload(
    Guid JobId,
    JobStatus FromStatus,
    JobStatus ToStatus,
    Guid? EquipmentId,
    string? Reason) : IEventPayload
{
    public static string EventType => EventTypes.JobStatusChanged;
}

public sealed record OrderStatusChangedPayload(Guid OrderId, OrderStatus FromStatus, OrderStatus ToStatus) : IEventPayload
{
    public static string EventType => EventTypes.OrderStatusChanged;
}

public sealed record CommandResentPayload(Guid JobId, Guid CommandId, int AttemptNo, int SendCount) : IEventPayload
{
    public static string EventType => EventTypes.CommandResent;
}

/// <summary>系統自動結案與人工結案共用，以 ResolvedBy 區分。</summary>
public sealed record AlertResolvedPayload(Guid AlertId, string Reason, DateTimeOffset ResolvedAt, ResolvedBy ResolvedBy) : IEventPayload
{
    public static string EventType => EventTypes.AlertResolved;
}
