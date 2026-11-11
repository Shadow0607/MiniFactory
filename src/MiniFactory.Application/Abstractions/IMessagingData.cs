using MiniFactory.Application.Messaging;

namespace MiniFactory.Application.Abstractions;

/// <summary>Idempotency、order_events 與 outbox 的寫入（architecture.md §10.5、§10.7.1）。</summary>
public interface IMessagingData
{
    /// <summary>
    /// 新增 Idempotency 紀錄。同 scope + key 已存在時拋出 <c>UniqueConstraintViolationException</c>
    /// （InnoDB 會先等待另一個未提交的交易結束）。
    /// </summary>
    Task InsertIdempotencyKeyAsync(IdempotencyRecord record, CancellationToken cancellationToken = default);

    Task<IdempotencyRecord?> GetIdempotencyKeyAsync(string scope, string key, CancellationToken cancellationToken = default);

    /// <summary>刪除已過期的紀錄（條件式，不會誤刪未過期者）；回傳是否刪到。</summary>
    Task<bool> DeleteExpiredIdempotencyKeyAsync(string scope, string key, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>同一交易寫入 order_events（時間線）與 outbox_messages；同一 eventId 同時作為兩表的 id。</summary>
    Task AppendOrderEventAsync(DomainEventRecord record, CancellationToken cancellationToken = default);
}
