using MiniFactory.Domain.Common;

namespace MiniFactory.Domain.Events;

/// <summary>事件版本策略：第一版固定 1；只加欄位不改型別；消費端遇到未知版本轉 parking queue。</summary>
public static class EventVersions
{
    public const int Current = 1;

    /// <summary>消費端是否認得此事件（型別與版本都支援才處理）。</summary>
    public static bool IsSupported(string? eventType, int eventVersion)
        => eventType is not null && EventTypes.All.Contains(eventType) && eventVersion == Current;
}

/// <summary>
/// 事件 envelope（Outbox、RabbitMQ body、order_events 共用；architecture.md §12）。
/// JSON 欄位：eventId、eventType、eventVersion、occurredAt、correlationId、payload。
/// </summary>
/// <param name="EventId">全域唯一；同時是 outbox_messages.id、AMQP MessageId、去重鍵。</param>
/// <param name="OccurredAt">UTC 時間（offset 必須為 0）。</param>
public sealed record EventEnvelope<TPayload>(
    Guid EventId,
    string EventType,
    int EventVersion,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    TPayload Payload)
{
    public const int MaxCorrelationIdLength = 64;

    /// <summary>
    /// 檢查 envelope 規則：eventId 非空、eventType 為已知領域事件、eventVersion 支援、
    /// occurredAt 為 UTC、correlationId 不超長、payload 非 null。
    /// 反序列化後的 envelope 也應先呼叫此方法再處理。
    /// </summary>
    public Result Validate()
    {
        if (EventId == Guid.Empty)
        {
            return Fail("event.event_id_required", "eventId is required.");
        }

        if (string.IsNullOrWhiteSpace(EventType) || !EventTypes.All.Contains(EventType))
        {
            return Fail("event.type_unknown", $"Unknown eventType '{EventType}'.");
        }

        if (EventVersion != EventVersions.Current)
        {
            return Fail("event.version_unsupported", $"Unsupported eventVersion {EventVersion} for {EventType}.");
        }

        if (OccurredAt.Offset != TimeSpan.Zero)
        {
            return Fail("event.occurred_at_not_utc", "occurredAt must be UTC.");
        }

        if (CorrelationId is { Length: > MaxCorrelationIdLength })
        {
            return Fail("event.correlation_id_too_long", $"correlationId exceeds {MaxCorrelationIdLength} characters.");
        }

        if (Payload is null)
        {
            return Fail("event.payload_required", "payload is required.");
        }

        return Result.Success();

        static Result Fail(string code, string message) => Result.Failure(DomainError.Validation(code, message));
    }
}

/// <summary>建立 envelope 的工廠；eventType 由 payload 型別決定、eventVersion 取目前版本。</summary>
public static class EventEnvelope
{
    public static Result<EventEnvelope<TPayload>> Create<TPayload>(
        Guid eventId,
        TPayload payload,
        DateTimeOffset occurredAt,
        string? correlationId)
        where TPayload : IEventPayload
    {
        var envelope = new EventEnvelope<TPayload>(
            eventId, TPayload.EventType, EventVersions.Current, occurredAt, correlationId, payload);

        var validation = envelope.Validate();
        return validation.IsSuccess
            ? Result.Success(envelope)
            : Result.Failure<EventEnvelope<TPayload>>(validation.Error!);
    }
}
