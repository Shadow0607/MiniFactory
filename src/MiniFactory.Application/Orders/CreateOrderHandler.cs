using System.Text.Json;
using Microsoft.Extensions.Logging;
using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Common;
using MiniFactory.Application.Errors;
using MiniFactory.Application.Messaging;
using MiniFactory.Application.Persistence;
using MiniFactory.Domain.Events;
using MiniFactory.Domain.Jobs;
using MiniFactory.Domain.Orders;

namespace MiniFactory.Application.Orders;

/// <summary>
/// 建單（architecture.md §8、§10.5）。單一交易內依序寫入：idempotency_keys（有帶 Key 時，第一個語句）→ 訂單 →
/// 3 個 Job 與第 1 道的 execution → order_events + outbox（OrderCreated、JobDispatchRequested）。
/// 本階段 outbox 只寫不發送（發送屬 Phase 2）。
/// </summary>
public sealed class CreateOrderHandler(
    IUnitOfWork unitOfWork,
    ITransactionRunner transactionRunner,
    IOrderData orderData,
    IMessagingData messagingData,
    IEquipmentData equipmentData,
    IBrokerStatus brokerStatus,
    IdempotencyOptions idempotencyOptions,
    OrderLimitsOptions orderLimits,
    TimeProvider timeProvider,
    ILogger<CreateOrderHandler> logger)
{
    /// <summary>idempotency_keys.scope（第一版只有建單）。</summary>
    public const string IdempotencyScope = "orders.create";

    private const int AcceptedStatusCode = 202;

    // 過期紀錄被別人搶先刪除再重建等競爭情形，重試數次即可
    private const int MaxIdempotencyAttempts = 3;

    public async Task<CreateOrderResult> HandleAsync(CreateOrderCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        CreateOrderValidator.Validate(command.Request, command.IdempotencyKey, orderLimits.MaxQuantity).ThrowIfAny();

        var request = command.Request!; // 已通過驗證，必有值
        var orderNo = request.OrderNo!.Trim();
        var productCode = request.ProductCode!.Trim();
        var quantity = request.Quantity!.Value;
        var requestedLine = request.LineId!.Trim();

        // 產線必須存在於 equipment；以資料庫中的正規寫法儲存（Line-01）
        var lineId = await equipmentData.FindLineIdAsync(requestedLine, cancellationToken).ConfigureAwait(false);
        if (lineId is null)
        {
            var lineErrors = new ValidationErrors();
            lineErrors.Add("lineId", $"產線 '{requestedLine}' 不存在");
            lineErrors.ThrowIfAny();
        }

        // 摘要用 DB 查回的正規 lineId（Line-01），不用使用者輸入的寫法：
        // 同一個 Key 下 line-01 與 Line-01 指向同一條產線，是同一個請求，不應誤判為內容不同（409）。
        // orderNo 維持區分大小寫（order_no 的唯一性比對區分大小寫，ORD-1 與 ord-1 是不同訂單）。
        var requestHash = IdempotencyRequestHasher.Compute(orderNo, productCode, quantity, lineId!);

        // 死結 / 鎖等待逾時時整段重跑（每次重跑都產生新的 ID，前一次嘗試已整個回滾）
        return await transactionRunner.RunAsync(
            ct => CreateOnceAsync(command, orderNo, productCode, quantity, lineId!, requestHash, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CreateOrderResult> CreateOnceAsync(
        CreateOrderCommand command,
        string orderNo,
        string productCode,
        int quantity,
        string lineId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        Guid NewId() => Guid.CreateVersion7(now);

        var created = ProductionOrder.Create(
            NewId(), orderNo, productCode, quantity, lineId,
            command.CorrelationId, command.TraceParent, ProcessRoute.Default, NewId, now);
        if (created.IsFailure)
        {
            // 輸入已先驗證過，Domain 仍拒絕代表兩邊規則不同步：視為程式錯誤
            throw new InvalidOperationException($"Domain 拒絕建立訂單：{created.Error}");
        }

        var order = created.Value;
        var response = new CreateOrderResponse(
            order.Id,
            order.OrderNo,
            order.Status,
            brokerStatus.IsConnected ? DispatchStatus.Queued : DispatchStatus.Pending,
            order.Jobs.OrderBy(j => j.Sequence).Select(j => j.Id).ToArray(),
            order.CreatedAt);

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        if (command.IdempotencyKey is not null)
        {
            var replay = await ReserveIdempotencyKeyAsync(command.IdempotencyKey, requestHash, response, now, cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                // 離開範圍時未提交 → 回滾，不留下任何東西
                logger.LogInformation("建單重送（Idempotency-Key 命中），回放第一次的結果 OrderId={OrderId}", replay.OrderId);
                return new CreateOrderResult(replay, Replayed: true);
            }
        }

        try
        {
            await orderData.InsertOrderAsync(order, cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException ex)
        {
            // 主鍵由應用程式產生 UUIDv7，不會重複，所以這裡的 1062 一定是 order_no
            logger.LogInformation("訂單編號重複 OrderNo={OrderNo}（{ServerMessage}）", orderNo, ex.ServerMessage);
            throw new OrderNoConflictException(orderNo);
        }

        await orderData.InsertJobsAsync(order, cancellationToken).ConfigureAwait(false);
        await AppendCreatedEventsAsync(order, command, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "建單成功 OrderId={OrderId} OrderNo={OrderNo} DispatchStatus={DispatchStatus}",
            order.Id, order.OrderNo, response.DispatchStatus);
        return new CreateOrderResult(response, Replayed: false);
    }

    /// <summary>
    /// 交易的第一個語句：插入 idempotency_keys。成功 → 回傳 null（繼續建單）；
    /// 1062 → 讀出既有紀錄：未過期且摘要相同 → 回傳儲存的原回應；摘要不同 → 409；已過期 → 刪除後視為新請求。
    /// </summary>
    private async Task<CreateOrderResponse?> ReserveIdempotencyKeyAsync(
        string key,
        string requestHash,
        CreateOrderResponse response,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var record = new IdempotencyRecord(
            IdempotencyScope,
            key,
            requestHash,
            AcceptedStatusCode,
            JsonSerializer.Serialize(response, JsonDefaults.Options),
            response.OrderId,
            now,
            now.AddHours(idempotencyOptions.RetentionHours));

        for (var attempt = 1; attempt <= MaxIdempotencyAttempts; attempt++)
        {
            try
            {
                await messagingData.InsertIdempotencyKeyAsync(record, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (UniqueConstraintViolationException)
            {
                // InnoDB 1062 只回滾該語句，交易仍可繼續使用
            }

            var existing = await messagingData.GetIdempotencyKeyAsync(IdempotencyScope, key, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                continue; // 對方在我們讀取前被清理 / 回滾，重新嘗試插入
            }

            if (existing.ExpiresAt <= now)
            {
                // 過期後同 Key 視為新請求（仍受 order_no 唯一約束保護）
                await messagingData.DeleteExpiredIdempotencyKeyAsync(IdempotencyScope, key, now, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                logger.LogWarning("Idempotency-Key 重複使用但內容不同 ExistingOrderId={OrderId}", existing.ResourceId);
                throw new IdempotencyConflictException(key);
            }

            return JsonSerializer.Deserialize<CreateOrderResponse>(existing.ResponseBody, JsonDefaults.Options)
                ?? throw new InvalidOperationException("Idempotency 儲存的回應本文無法解析。");
        }

        throw new InvalidOperationException("Idempotency-Key 競爭過於激烈，重試後仍無法取得或建立紀錄。");
    }

    /// <summary>
    /// OrderCreated 與 JobDispatchRequested（第 1 道）。兩筆事件的 occurredAt 相差 1 微秒：
    /// order_events / outbox 都依 (occurred_at, id) 排序，同一毫秒內的 UUIDv7 後綴不保證遞增，
    /// 不加偏移的話時間線上 JobDispatchRequested 可能排在 OrderCreated 前面。
    /// </summary>
    private async Task AppendCreatedEventsAsync(ProductionOrder order, CreateOrderCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var jobs = order.Jobs.OrderBy(j => j.Sequence).ToArray();
        var firstJob = jobs[0];
        var firstExecution = firstJob.Executions.Single();

        var orderCreated = DomainEventRecord.Create(
            Guid.CreateVersion7(now),
            new OrderCreatedPayload(order.Id, order.OrderNo, jobs.Select(j => j.Id).ToArray()),
            now,
            aggregateType: "Order",
            aggregateId: order.Id,
            orderId: order.Id,
            command.CorrelationId,
            command.TraceParent,
            command.TraceState);

        var dispatchRequested = DomainEventRecord.Create(
            Guid.CreateVersion7(now),
            new JobDispatchRequestedPayload(firstJob.Id, EquipmentId: null, firstExecution.CommandId, firstExecution.AttemptNo),
            now.AddTicks(10),
            aggregateType: "Job",
            aggregateId: firstJob.Id,
            orderId: order.Id,
            command.CorrelationId,
            command.TraceParent,
            command.TraceState);

        await messagingData.AppendOrderEventAsync(orderCreated, cancellationToken).ConfigureAwait(false);
        await messagingData.AppendOrderEventAsync(dispatchRequested, cancellationToken).ConfigureAwait(false);
    }
}
