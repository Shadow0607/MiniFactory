using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Messaging;

namespace MiniFactory.Infrastructure.Persistence.Data;

/// <summary>idempotency_keys、order_events、outbox_messages 的寫入（Dapper，經 <see cref="DbSession"/>）。</summary>
public sealed class MessagingData(DbSession session) : IMessagingData
{
    public async Task InsertIdempotencyKeyAsync(IdempotencyRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await session.ExecuteAsync(
            """
            INSERT INTO idempotency_keys
                (scope, idempotency_key, request_hash, response_status_code, response_body, resource_id, created_at, expires_at)
            VALUES
                (@Scope, @Key, @RequestHash, @ResponseStatusCode, @ResponseBody, @ResourceId, @CreatedAt, @ExpiresAt)
            """,
            new
            {
                record.Scope,
                record.Key,
                record.RequestHash,
                record.ResponseStatusCode,
                record.ResponseBody,
                record.ResourceId,
                CreatedAt = record.CreatedAt.UtcDateTime,
                ExpiresAt = record.ExpiresAt.UtcDateTime,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdempotencyRecord?> GetIdempotencyKeyAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        var row = await session.QuerySingleOrDefaultAsync<IdempotencyRow>(
            """
            SELECT scope, idempotency_key, request_hash, response_status_code, response_body, resource_id, created_at, expires_at
            FROM idempotency_keys
            WHERE scope = @scope AND idempotency_key = @key
            """,
            new { scope, key },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? null
            : new IdempotencyRecord(
                row.Scope,
                row.IdempotencyKey,
                row.RequestHash,
                row.ResponseStatusCode,
                row.ResponseBody,
                row.ResourceId,
                new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc)));
    }

    public async Task<bool> DeleteExpiredIdempotencyKeyAsync(string scope, string key, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var affected = await session.ExecuteAsync(
            "DELETE FROM idempotency_keys WHERE scope = @scope AND idempotency_key = @key AND expires_at <= @now",
            new { scope, key, now = now.UtcDateTime },
            cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task AppendOrderEventAsync(DomainEventRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var parameters = new
        {
            record.EventId,
            record.OrderId,
            record.EventType,
            record.EventVersion,
            Payload = record.PayloadJson,
            OccurredAt = record.OccurredAt.UtcDateTime,
            record.AggregateType,
            record.AggregateId,
            record.CorrelationId,
            record.TraceParent,
            record.TraceState,
        };

        // 同一個 eventId 同時作為 order_events.id 與 outbox_messages.id（architecture.md §8）
        await session.ExecuteAsync(
            """
            INSERT INTO order_events (id, order_id, event_type, event_version, payload, occurred_at)
            VALUES (@EventId, @OrderId, @EventType, @EventVersion, @Payload, @OccurredAt)
            """,
            parameters,
            cancellationToken).ConfigureAwait(false);

        await session.ExecuteAsync(
            """
            INSERT INTO outbox_messages
                (id, event_type, event_version, payload, aggregate_type, aggregate_id, correlation_id,
                 trace_parent, trace_state, occurred_at, attempt_count)
            VALUES
                (@EventId, @EventType, @EventVersion, @Payload, @AggregateType, @AggregateId, @CorrelationId,
                 @TraceParent, @TraceState, @OccurredAt, 0)
            """,
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class IdempotencyRow
    {
        public string Scope { get; set; } = string.Empty;

        public string IdempotencyKey { get; set; } = string.Empty;

        public string RequestHash { get; set; } = string.Empty;

        public int ResponseStatusCode { get; set; }

        public string ResponseBody { get; set; } = string.Empty;

        public Guid? ResourceId { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime ExpiresAt { get; set; }
    }
}
