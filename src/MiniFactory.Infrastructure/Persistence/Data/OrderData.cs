using MiniFactory.Application.Abstractions;
using MiniFactory.Domain.Orders;

namespace MiniFactory.Infrastructure.Persistence.Data;

/// <summary>訂單寫入（Dapper）。一律經 <see cref="DbSession"/>，所以自動帶入目前交易、enum 轉字串、錯誤碼翻譯。</summary>
public sealed class OrderData(DbSession session) : IOrderData
{
    // 時間以 UtcDateTime 傳入（DATETIME(6) 存 UTC；SQL 不使用 NOW()，architecture.md §10.7.3）

    public async Task InsertOrderAsync(ProductionOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        await session.ExecuteAsync(
            """
            INSERT INTO production_orders
                (id, order_no, product_code, quantity, line_id, status, version, correlation_id, trace_parent,
                 created_at, updated_at, completed_at)
            VALUES
                (@Id, @OrderNo, @ProductCode, @Quantity, @LineId, @Status, @Version, @CorrelationId, @TraceParent,
                 @CreatedAt, @UpdatedAt, @CompletedAt)
            """,
            new
            {
                order.Id,
                order.OrderNo,
                order.ProductCode,
                order.Quantity,
                order.LineId,
                order.Status,
                order.Version,
                order.CorrelationId,
                order.TraceParent,
                CreatedAt = order.CreatedAt.UtcDateTime,
                UpdatedAt = order.UpdatedAt.UtcDateTime,
                CompletedAt = order.CompletedAt?.UtcDateTime,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task InsertJobsAsync(ProductionOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        foreach (var job in order.Jobs.OrderBy(j => j.Sequence))
        {
            await session.ExecuteAsync(
                """
                INSERT INTO production_jobs
                    (id, order_id, `sequence`, operation, status, assigned_equipment_id, current_attempt_no, version,
                     created_at, updated_at, started_at, completed_at)
                VALUES
                    (@Id, @OrderId, @Sequence, @Operation, @Status, @AssignedEquipmentId, @CurrentAttemptNo, @Version,
                     @CreatedAt, @UpdatedAt, @StartedAt, @CompletedAt)
                """,
                new
                {
                    job.Id,
                    job.OrderId,
                    job.Sequence,
                    job.Operation,
                    job.Status,
                    job.AssignedEquipmentId,
                    job.CurrentAttemptNo,
                    job.Version,
                    CreatedAt = job.CreatedAt.UtcDateTime,
                    UpdatedAt = job.UpdatedAt.UtcDateTime,
                    StartedAt = job.StartedAt?.UtcDateTime,
                    CompletedAt = job.CompletedAt?.UtcDateTime,
                },
                cancellationToken).ConfigureAwait(false);

            // execution 依賴 job（外鍵），所以放在同一個 job 之後
            foreach (var execution in job.Executions)
            {
                await session.ExecuteAsync(
                    """
                    INSERT INTO job_executions
                        (id, job_id, attempt_no, command_id, status, equipment_id, send_count, last_sent_at, deadline_at,
                         accepted_at, started_at, completed_at, error_code, result_event_id, version, created_at, updated_at)
                    VALUES
                        (@Id, @JobId, @AttemptNo, @CommandId, @Status, @EquipmentId, @SendCount, @LastSentAt, @DeadlineAt,
                         @AcceptedAt, @StartedAt, @CompletedAt, @ErrorCode, @ResultEventId, @Version, @CreatedAt, @UpdatedAt)
                    """,
                    new
                    {
                        execution.Id,
                        execution.JobId,
                        execution.AttemptNo,
                        execution.CommandId,
                        execution.Status,
                        execution.EquipmentId,
                        execution.SendCount,
                        LastSentAt = execution.LastSentAt?.UtcDateTime,
                        DeadlineAt = execution.DeadlineAt?.UtcDateTime,
                        AcceptedAt = execution.AcceptedAt?.UtcDateTime,
                        StartedAt = execution.StartedAt?.UtcDateTime,
                        CompletedAt = execution.CompletedAt?.UtcDateTime,
                        execution.ErrorCode,
                        execution.ResultEventId,
                        execution.Version,
                        CreatedAt = execution.CreatedAt.UtcDateTime,
                        UpdatedAt = execution.UpdatedAt.UtcDateTime,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
