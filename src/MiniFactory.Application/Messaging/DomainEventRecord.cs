using System.Text.Json;
using MiniFactory.Application.Common;
using MiniFactory.Domain.Events;

namespace MiniFactory.Application.Messaging;

/// <summary>
/// 要寫入 order_events 與 outbox_messages 的一筆事件（兩表共用同一個 EventId）。
/// <see cref="PayloadJson"/> 只含 payload；eventType、eventVersion、occurredAt、correlationId 各有獨立欄位。
/// </summary>
public sealed record DomainEventRecord(
    Guid EventId,
    string EventType,
    int EventVersion,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    string AggregateType,
    Guid AggregateId,
    Guid OrderId,
    string? CorrelationId,
    string? TraceParent,
    string? TraceState)
{
    /// <summary>
    /// 由 payload 建立事件紀錄，並經過 <see cref="EventEnvelope{TPayload}.Validate"/>
    /// （eventType / eventVersion / UTC / correlationId 長度）。
    /// </summary>
    public static DomainEventRecord Create<TPayload>(
        Guid eventId,
        TPayload payload,
        DateTimeOffset occurredAt,
        string aggregateType,
        Guid aggregateId,
        Guid orderId,
        string? correlationId,
        string? traceParent,
        string? traceState)
        where TPayload : IEventPayload
    {
        var envelope = EventEnvelope.Create(eventId, payload, occurredAt.ToUniversalTime(), correlationId);
        if (envelope.IsFailure)
        {
            // 事件由程式碼自行組裝，驗證失敗代表程式錯誤，不是使用者輸入問題
            throw new InvalidOperationException($"事件 envelope 不合法：{envelope.Error}");
        }

        var e = envelope.Value;
        return new DomainEventRecord(
            e.EventId,
            e.EventType,
            e.EventVersion,
            JsonSerializer.Serialize(payload, JsonDefaults.Options),
            e.OccurredAt,
            aggregateType,
            aggregateId,
            orderId,
            e.CorrelationId,
            traceParent,
            traceState);
    }
}

/// <summary>idempotency_keys 的一列（database.md §3.10）。</summary>
public sealed record IdempotencyRecord(
    string Scope,
    string Key,
    string RequestHash,
    int ResponseStatusCode,
    string ResponseBody,
    Guid? ResourceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
