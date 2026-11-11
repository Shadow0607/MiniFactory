namespace MiniFactory.Domain.Events;

/// <summary>事件型別字串（= RabbitMQ routing key；architecture.md §12）。</summary>
public static class EventTypes
{
    public const string OrderCreated = "OrderCreated";
    public const string JobDispatchRequested = "JobDispatchRequested";
    public const string JobStarted = "JobStarted";
    public const string JobCompleted = "JobCompleted";
    public const string JobFailed = "JobFailed";
    public const string EquipmentStatusChanged = "EquipmentStatusChanged";
    public const string AlertRaised = "AlertRaised";

    // 以下 4 種為架構文件新增型別（已確認）
    public const string JobStatusChanged = "JobStatusChanged";
    public const string OrderStatusChanged = "OrderStatusChanged";
    public const string CommandResent = "CommandResent";
    public const string AlertResolved = "AlertResolved";

    /// <summary>
    /// 設備回報（inbound）專用：Simulator → API 的心跳。不是領域事件，因此不在 <see cref="All"/>，
    /// 也不能包成 EventEnvelope 發到 RabbitMQ。
    /// </summary>
    public const string Heartbeat = "Heartbeat";

    /// <summary>所有可經 Outbox / RabbitMQ 發送的領域事件型別。</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        OrderCreated, JobDispatchRequested, JobStarted, JobCompleted, JobFailed,
        EquipmentStatusChanged, AlertRaised,
        JobStatusChanged, OrderStatusChanged, CommandResent, AlertResolved,
    };
}
