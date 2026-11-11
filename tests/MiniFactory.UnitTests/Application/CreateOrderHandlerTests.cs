using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MiniFactory.Application.Common;
using MiniFactory.Application.Errors;
using MiniFactory.Application.Orders;
using MiniFactory.Domain.Events;
using MiniFactory.Domain.Orders;

namespace MiniFactory.UnitTests.Application;

public class CreateOrderHandlerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private readonly CallLog _log = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeBrokerStatus _broker = new(connected: false);
    private readonly FakeUnitOfWork _uow;
    private readonly FakeOrderData _orders;
    private readonly FakeMessagingData _messaging;
    private readonly CreateOrderHandler _handler;

    public CreateOrderHandlerTests()
    {
        _uow = new FakeUnitOfWork(_log);
        _orders = new FakeOrderData(_log);
        _messaging = new FakeMessagingData(_log);
        _handler = new CreateOrderHandler(
            _uow,
            new PassThroughTransactionRunner(),
            _orders,
            _messaging,
            new FakeEquipmentData("Line-01"),
            _broker,
            new IdempotencyOptions { RetentionHours = 24 },
            new OrderLimitsOptions(),
            _time,
            NullLogger<CreateOrderHandler>.Instance);
    }

    private static CreateOrderRequest Request(string orderNo = "ORD-20260921-001", int quantity = 500)
        => new(orderNo, "Motor-A", quantity, "Line-01");

    private static CreateOrderCommand Command(CreateOrderRequest? request, string? key = null)
        => new(request, key, "corr-1", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", null);

    // ------------------------------------------------------------ 成功路徑

    [Fact]
    public async Task Create_WithoutKey_WritesOrderThreeJobsAndTwoEventsInOneTransaction()
    {
        var result = await _handler.HandleAsync(Command(Request()), TestContext.Current.CancellationToken);

        Assert.False(result.Replayed);
        var order = Assert.Single(_orders.Orders);
        Assert.Equal(3, order.Jobs.Count);
        Assert.Equal(order.Id, result.Response.OrderId);
        Assert.Equal(order.Jobs.OrderBy(j => j.Sequence).Select(j => j.Id), result.Response.JobIds);
        Assert.Equal(OrderStatus.Created, result.Response.Status);

        // 單一交易：begin → 訂單 → Job → 兩個事件 → commit（沒有 idempotency，因為沒帶 Key）
        Assert.Equal(
            ["begin", "order", "jobs", "event:OrderCreated", "event:JobDispatchRequested", "commit"],
            _log.Calls);
        Assert.Equal(1, _uow.Begun);
        Assert.Equal(1, _uow.Committed);
        Assert.Empty(_messaging.Keys);
    }

    [Fact]
    public async Task Create_WithKey_InsertsIdempotencyKeyAsFirstStatement()
    {
        await _handler.HandleAsync(Command(Request(), "create-order-1"), TestContext.Current.CancellationToken);

        Assert.Equal(["begin", "idempotency", "order", "jobs", "event:OrderCreated", "event:JobDispatchRequested", "commit"], _log.Calls);
    }

    [Fact]
    public async Task Create_WithKey_StoresRequestHashAndResponseAndExpiryAfterRetentionHours()
    {
        var result = await _handler.HandleAsync(Command(Request(), "create-order-1"), TestContext.Current.CancellationToken);

        var record = Assert.Single(_messaging.Keys.Values);
        Assert.Equal(CreateOrderHandler.IdempotencyScope, record.Scope);
        Assert.Equal("create-order-1", record.Key);
        Assert.Equal(IdempotencyRequestHasher.Compute(Request()), record.RequestHash);
        Assert.Equal(202, record.ResponseStatusCode);
        Assert.Equal(result.Response.OrderId, record.ResourceId);
        Assert.Equal(Start.AddHours(24), record.ExpiresAt);

        var stored = JsonSerializer.Deserialize<CreateOrderResponse>(record.ResponseBody, JsonDefaults.Options);
        Assert.Equal(result.Response.OrderId, stored!.OrderId);
        Assert.Equal(result.Response.JobIds, stored.JobIds);
    }

    [Fact]
    public async Task Create_StoredResponseBody_UsesCamelCaseStringEnumsAndZTimestamps()
    {
        await _handler.HandleAsync(Command(Request(), "create-order-1"), TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(_messaging.Keys.Values.Single().ResponseBody);
        var root = json.RootElement;
        Assert.Equal("Created", root.GetProperty("status").GetString());
        Assert.Equal("Pending", root.GetProperty("dispatchStatus").GetString());
        Assert.EndsWith("Z", root.GetProperty("createdAt").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, root.GetProperty("jobIds").GetArrayLength());
    }

    [Theory]
    [InlineData(false, DispatchStatus.Pending)]
    [InlineData(true, DispatchStatus.Queued)]
    public async Task Create_DispatchStatus_FollowsBrokerConnection(bool connected, DispatchStatus expected)
    {
        _broker.IsConnected = connected;

        var result = await _handler.HandleAsync(Command(Request()), TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Response.DispatchStatus);
    }

    [Fact]
    public async Task Create_Events_OrderCreatedBeforeJobDispatchRequested_WithSameOrderAndCorrelation()
    {
        var result = await _handler.HandleAsync(Command(Request(), "k"), TestContext.Current.CancellationToken);

        Assert.Equal(2, _messaging.Events.Count);
        var created = _messaging.Events[0];
        var dispatch = _messaging.Events[1];

        Assert.Equal(EventTypes.OrderCreated, created.EventType);
        Assert.Equal(EventTypes.JobDispatchRequested, dispatch.EventType);
        Assert.True(created.OccurredAt < dispatch.OccurredAt, "時間線依 occurredAt 排序，OrderCreated 必須在前");
        Assert.NotEqual(created.EventId, dispatch.EventId);
        Assert.All(_messaging.Events, e =>
        {
            Assert.Equal(result.Response.OrderId, e.OrderId);
            Assert.Equal("corr-1", e.CorrelationId);
            Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", e.TraceParent);
            Assert.Equal(EventVersions.Current, e.EventVersion);
        });

        using var createdPayload = JsonDocument.Parse(created.PayloadJson);
        Assert.Equal(result.Response.OrderId, createdPayload.RootElement.GetProperty("orderId").GetGuid());
        Assert.Equal(3, createdPayload.RootElement.GetProperty("jobIds").GetArrayLength());

        using var dispatchPayload = JsonDocument.Parse(dispatch.PayloadJson);
        Assert.Equal(result.Response.JobIds[0], dispatchPayload.RootElement.GetProperty("jobId").GetGuid());
        Assert.Equal(JsonValueKind.Null, dispatchPayload.RootElement.GetProperty("equipmentId").ValueKind);
        Assert.Equal(1, dispatchPayload.RootElement.GetProperty("attemptNo").GetInt32());
        Assert.NotEqual(Guid.Empty, dispatchPayload.RootElement.GetProperty("commandId").GetGuid());
    }

    [Fact]
    public async Task Create_NormalizesWhitespaceAndLineCasing()
    {
        await _handler.HandleAsync(
            Command(new CreateOrderRequest("  ORD-1 ", " Motor-A ", 5, "line-01 ")), TestContext.Current.CancellationToken);

        var order = Assert.Single(_orders.Orders);
        Assert.Equal("ORD-1", order.OrderNo);
        Assert.Equal("Motor-A", order.ProductCode);
        Assert.Equal("Line-01", order.LineId); // 以資料庫中的正規寫法儲存
    }

    // ------------------------------------------------------------ Idempotency（情境 1）

    [Fact]
    public async Task CreateOrder_RetryWithSameIdempotencyKey_ReturnsSameOrderId()
    {
        var first = await _handler.HandleAsync(Command(Request(), "k1"), TestContext.Current.CancellationToken);
        _log.Calls.Clear();

        var second = await _handler.HandleAsync(Command(Request(), "k1"), TestContext.Current.CancellationToken);

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Response.OrderId, second.Response.OrderId);
        Assert.Equal(first.Response.OrderNo, second.Response.OrderNo);
        Assert.Equal(first.Response.Status, second.Response.Status);
        Assert.Equal(first.Response.DispatchStatus, second.Response.DispatchStatus);
        Assert.Equal(first.Response.JobIds, second.Response.JobIds);
        Assert.Equal(first.Response.CreatedAt, second.Response.CreatedAt);
        Assert.Single(_orders.Orders);
        Assert.Equal(2, _messaging.Events.Count);

        // 重送：只嘗試插入 idempotency（遇 1062）後回滾，沒有任何訂單 / 事件寫入
        Assert.Equal(["begin", "idempotency", "rollback"], _log.Calls);
    }

    [Fact]
    public async Task CreateOrder_RetryAfterBrokerStatusChanged_ReplaysFirstDispatchStatus()
    {
        var first = await _handler.HandleAsync(Command(Request(), "k1"), TestContext.Current.CancellationToken);
        _broker.IsConnected = true;

        var second = await _handler.HandleAsync(Command(Request(), "k1"), TestContext.Current.CancellationToken);

        Assert.Equal(DispatchStatus.Pending, first.Response.DispatchStatus);
        Assert.Equal(DispatchStatus.Pending, second.Response.DispatchStatus); // 與第一次完全相同
    }

    [Fact]
    public async Task CreateOrder_RetryWithWhitespaceDifferences_IsStillTheSameRequest()
    {
        var first = await _handler.HandleAsync(Command(Request(), "k1"), TestContext.Current.CancellationToken);

        var second = await _handler.HandleAsync(
            Command(new CreateOrderRequest(" ORD-20260921-001 ", "Motor-A ", 500, "Line-01"), "k1"), TestContext.Current.CancellationToken);

        Assert.True(second.Replayed);
        Assert.Equal(first.Response.OrderId, second.Response.OrderId);
    }

    [Fact]
    public async Task CreateOrder_SameKeyWithDifferentLineIdCasing_IsTheSameRequest()
    {
        var first = await _handler.HandleAsync(
            Command(new CreateOrderRequest("ORD-1", "Motor-A", 5, "line-01"), "k1"), TestContext.Current.CancellationToken);

        // line-01 與 Line-01 在資料庫中是同一條產線（不分大小寫），摘要以正規化後的 lineId 計算，不可誤判為 409
        var second = await _handler.HandleAsync(
            Command(new CreateOrderRequest("ORD-1", "Motor-A", 5, "Line-01"), "k1"), TestContext.Current.CancellationToken);

        Assert.True(second.Replayed);
        Assert.Equal(first.Response.OrderId, second.Response.OrderId);
        Assert.Equal(IdempotencyRequestHasher.Compute("ORD-1", "Motor-A", 5, "Line-01"), _messaging.Keys.Values.Single().RequestHash);
    }

    [Fact]
    public async Task CreateOrder_SameKeyWithDifferentOrderNoCasing_StillConflicts()
    {
        await _handler.HandleAsync(Command(Request("ORD-abc"), "k1"), TestContext.Current.CancellationToken);

        // orderNo 維持區分大小寫：ORD-ABC 是另一個訂單編號
        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => _handler.HandleAsync(Command(Request("ORD-ABC"), "k1"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateOrder_SameIdempotencyKeyDifferentPayload_ThrowsIdempotencyConflict()
    {
        await _handler.HandleAsync(Command(Request(quantity: 500), "k1"), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => _handler.HandleAsync(Command(Request(quantity: 600), "k1"), TestContext.Current.CancellationToken));

        Assert.Equal("k1", ex.IdempotencyKey);
        Assert.Single(_orders.Orders);
        Assert.Equal(500, _orders.Orders[0].Quantity);
    }

    [Fact]
    public async Task CreateOrder_ExpiredIdempotencyKey_IsTreatedAsNewRequest()
    {
        var first = await _handler.HandleAsync(Command(Request("ORD-A"), "k1"), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromHours(25));

        // 過期後同 Key 視為新請求（內容不同也不衝突）
        var second = await _handler.HandleAsync(Command(Request("ORD-B", 7), "k1"), TestContext.Current.CancellationToken);

        Assert.False(second.Replayed);
        Assert.NotEqual(first.Response.OrderId, second.Response.OrderId);
        Assert.Equal(2, _orders.Orders.Count);
        Assert.Equal(second.Response.OrderId, _messaging.Keys.Values.Single().ResourceId);
    }

    [Fact]
    public async Task CreateOrder_KeyWithinRetention_StillReplaysJustBeforeExpiry()
    {
        var first = await _handler.HandleAsync(Command(Request(), "k1"), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));

        var second = await _handler.HandleAsync(Command(Request(), "k1"), TestContext.Current.CancellationToken);

        Assert.True(second.Replayed);
        Assert.Equal(first.Response.OrderId, second.Response.OrderId);
    }

    // ------------------------------------------------------------ order_no 唯一

    [Fact]
    public async Task CreateOrder_DuplicateOrderNo_ThrowsOrderNoConflictAndDoesNotCommit()
    {
        _orders.ThrowDuplicateOrderNo = true;

        var ex = await Assert.ThrowsAsync<OrderNoConflictException>(
            () => _handler.HandleAsync(Command(Request("ORD-DUP"), "k1"), TestContext.Current.CancellationToken));

        Assert.Equal("ORD-DUP", ex.OrderNo);
        Assert.Equal(0, _uow.Committed);
        Assert.Equal(1, _uow.RolledBack); // idempotency 紀錄隨交易一起回滾，之後可用同 Key 重試
        Assert.Empty(_messaging.Events);
    }

    // ------------------------------------------------------------ 驗證

    [Fact]
    public async Task CreateOrder_InvalidInput_ThrowsValidationFailedBeforeTouchingDatabase()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => _handler.HandleAsync(Command(Request(quantity: 0)), TestContext.Current.CancellationToken));

        Assert.Contains("quantity", ex.Errors.Keys);
        Assert.Empty(_log.Calls);
    }

    [Fact]
    public async Task CreateOrder_AppliesConfiguredMaxQuantity_NotTheDefault()
    {
        // 設定值（10）不等於預設值（100000）：超過設定值被拒、等於設定值通過，證明 handler 傳的是設定值
        var handler = new CreateOrderHandler(
            _uow,
            new PassThroughTransactionRunner(),
            _orders,
            _messaging,
            new FakeEquipmentData("Line-01"),
            _broker,
            new IdempotencyOptions { RetentionHours = 24 },
            new OrderLimitsOptions { MaxQuantity = 10 },
            _time,
            NullLogger<CreateOrderHandler>.Instance);
        Assert.NotEqual(OrderLimitsOptions.DefaultMaxQuantity, 10);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => handler.HandleAsync(Command(Request(quantity: 11)), TestContext.Current.CancellationToken));
        Assert.Contains("quantity", ex.Errors.Keys);
        Assert.Empty(_log.Calls);

        var ok = await handler.HandleAsync(Command(Request(quantity: 10)), TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Created, ok.Response.Status);
    }

    [Fact]
    public async Task CreateOrder_UnknownLine_ThrowsValidationFailedOnLineId()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => _handler.HandleAsync(
                Command(new CreateOrderRequest("ORD-1", "Motor-A", 1, "Line-99")), TestContext.Current.CancellationToken));

        Assert.Equal("產線 'Line-99' 不存在", Assert.Single(ex.Errors["lineId"]));
        Assert.Empty(_log.Calls);
    }

    [Fact]
    public async Task CreateOrder_InvalidIdempotencyKey_ThrowsValidationFailed()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => _handler.HandleAsync(Command(Request(), "bad key"), TestContext.Current.CancellationToken));

        Assert.Contains("Idempotency-Key", ex.Errors.Keys);
    }

    // ------------------------------------------------------------ quantity 上限

    [Fact]
    public async Task Create_QuantityAtLimit_IsAccepted_AndOverLimitThrowsValidationFailed()
    {
        var atLimit = await _handler.HandleAsync(
            Command(Request("ORD-LIMIT", OrderLimitsOptions.DefaultMaxQuantity)), TestContext.Current.CancellationToken);
        Assert.False(atLimit.Replayed);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => _handler.HandleAsync(
                Command(Request("ORD-OVER", OrderLimitsOptions.DefaultMaxQuantity + 1)), TestContext.Current.CancellationToken));

        Assert.Contains("quantity", ex.Errors.Keys);
        Assert.Single(_orders.Orders);
    }
}
