using System.Net;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>failure-scenarios.md 情境 1：建立訂單後 API 回應逾時，使用者用同一 Key 重送。</summary>
[Collection(MariaDbCollection.Name)]
public class IdempotentOrderCreationTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{
    [Fact]
    public async Task CreateOrder_RetryWithSameIdempotencyKey_ReturnsSameOrderId()
    {
        var orderNo = NewOrderNo();
        var body = ValidBody(orderNo);

        using var first = await PostOrderAsync(body, "create-order-" + orderNo.ToLowerInvariant());
        using var second = await PostOrderAsync(body, "create-order-" + orderNo.ToLowerInvariant());

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        var firstText = await first.Content.ReadAsStringAsync(Ct);
        var secondText = await second.Content.ReadAsStringAsync(Ct);
        Assert.Equal(firstText, secondText); // 重送回的是第一次的 body，一字不差（含 dispatchStatus）

        var firstJson = await ReadJsonAsync(first);
        var secondJson = await ReadJsonAsync(second);
        Assert.Equal(firstJson.GetProperty("orderId").GetGuid(), secondJson.GetProperty("orderId").GetGuid());

        Assert.False(HasHeader(first, "Idempotent-Replayed"));
        Assert.Equal("true", Header(second, "Idempotent-Replayed"));
        Assert.Equal($"/api/orders/{firstJson.GetProperty("orderId").GetGuid()}", second.Headers.Location?.OriginalString);

        // 資料庫：一張訂單、3 個 Job、OrderCreated outbox 一筆
        var orderId = firstJson.GetProperty("orderId").GetGuid();
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM production_orders WHERE order_no = @orderNo", new { orderNo }));
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM production_jobs WHERE order_id = @orderId", new { orderId }));
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM outbox_messages WHERE aggregate_id = @orderId AND event_type = 'OrderCreated'", new { orderId }));
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(*) FROM idempotency_keys WHERE resource_id = @orderId", new { orderId }));
    }

    [Fact]
    public async Task CreateOrder_ConcurrentRequestsWithSameIdempotencyKey_CreatesSingleOrder()
    {
        var orderNo = NewOrderNo();
        var key = "concurrent-" + orderNo;
        var body = ValidBody(orderNo);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => PostOrderAsync(body, key), Ct)));
        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));

            var orderIds = new HashSet<Guid>();
            foreach (var response in responses)
            {
                orderIds.Add((await ReadJsonAsync(response)).GetProperty("orderId").GetGuid());
            }

            Assert.Single(orderIds);
            Assert.Equal(1, responses.Count(r => !HasHeader(r, "Idempotent-Replayed"))); // 只有一個真正建單
            Assert.Equal(9, responses.Count(r => Header(r, "Idempotent-Replayed") == "true"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM production_orders WHERE order_no = @orderNo", new { orderNo }));
            Assert.Equal(3, await CountAsync(
                "SELECT COUNT(*) FROM production_jobs j JOIN production_orders o ON o.id = j.order_id WHERE o.order_no = @orderNo", new { orderNo }));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task CreateOrder_SameIdempotencyKeyDifferentPayload_Returns409IdempotencyConflict()
    {
        var orderNo = NewOrderNo();
        var key = "create-order-" + orderNo.ToLowerInvariant();
        using var first = await PostOrderAsync(ValidBody(orderNo, quantity: 500), key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        using var second = await PostOrderAsync(ValidBody(orderNo, quantity: 600), key);

        var problem = await AssertProblemAsync(second, 409, "idempotency-conflict");
        Assert.Contains(key, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal("/api/orders", problem.GetProperty("instance").GetString());
        Assert.False(HasHeader(second, "Idempotent-Replayed"));

        // 資料庫數量仍為 500
        Assert.Equal(500, await CountAsync("SELECT quantity FROM production_orders WHERE order_no = @orderNo", new { orderNo }));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM production_orders WHERE order_no = @orderNo", new { orderNo }));
    }

    [Fact]
    public async Task CreateOrder_DuplicateOrderNoWithDifferentKey_Returns409OrderNoConflict()
    {
        var orderNo = NewOrderNo();
        using var first = await PostOrderAsync(ValidBody(orderNo), "key-A-" + orderNo);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var outboxBefore = await CountAsync("SELECT COUNT(*) FROM outbox_messages");

        using var second = await PostOrderAsync(ValidBody(orderNo), "key-B-" + orderNo);

        var problem = await AssertProblemAsync(second, 409, "order-no-conflict"); // 與 idempotency-conflict 的 type 不同
        Assert.Equal(orderNo, problem.GetProperty("orderNo").GetString());
        Assert.NotEqual("https://minifactory.dev/errors/idempotency-conflict", problem.GetProperty("type").GetString());

        // 失敗的請求整個回滾：沒有多出 outbox，也沒有留下 key-B 的 idempotency 紀錄（之後換個 orderNo 可用同 Key 重試）
        Assert.Equal(outboxBefore, await CountAsync("SELECT COUNT(*) FROM outbox_messages"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @k", new { k = "key-B-" + orderNo }));

        using var retry = await PostOrderAsync(ValidBody(NewOrderNo()), "key-B-" + orderNo);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_DuplicateOrderNoWithoutKey_Returns409OrderNoConflict()
    {
        var orderNo = NewOrderNo();
        using var first = await PostOrderAsync(ValidBody(orderNo));
        using var second = await PostOrderAsync(ValidBody(orderNo));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        await AssertProblemAsync(second, 409, "order-no-conflict");
    }

    [Fact]
    public async Task CreateOrder_OrderNoDiffersOnlyByCase_Returns409OrderNoConflict()
    {
        // order_no 欄位使用 utf8mb4_unicode_ci（database.md §3.1）：ORD-1 與 ord-1 視為重複
        var orderNo = NewOrderNo();
        using var first = await PostOrderAsync(ValidBody(orderNo.ToUpperInvariant()));
        using var second = await PostOrderAsync(ValidBody(orderNo.ToLowerInvariant()));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        await AssertProblemAsync(second, 409, "order-no-conflict");
    }

    [Fact]
    public async Task CreateOrder_SameKeyAfterRetentionExpired_IsTreatedAsNewRequest()
    {
        var orderNo = NewOrderNo();
        var key = "expire-" + orderNo;
        using var first = await PostOrderAsync(ValidBody(orderNo), key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        // 模擬 24 小時保留期已過：把紀錄的 expires_at 改到過去
        await Db.ExecAsync(Cs, "UPDATE idempotency_keys SET expires_at = @past WHERE idempotency_key = @key", new { past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), key });

        // 內容不同也不會衝突，視為新請求（仍受 order_no 唯一約束保護）
        using var second = await PostOrderAsync(ValidBody(NewOrderNo(), quantity: 7), key);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.False(HasHeader(second, "Idempotent-Replayed"));
        Assert.NotEqual(
            (await ReadJsonAsync(first)).GetProperty("orderId").GetGuid(),
            (await ReadJsonAsync(second)).GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task CreateOrder_IdempotencyKeyIsCaseSensitive()
    {
        var orderNo = NewOrderNo();
        using var first = await PostOrderAsync(ValidBody(orderNo), "Case-" + orderNo);
        using var second = await PostOrderAsync(ValidBody(NewOrderNo()), "case-" + orderNo);

        // 不同大小寫是不同的 Key（ascii_bin），所以第二個是獨立的新訂單，不是衝突
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.False(HasHeader(second, "Idempotent-Replayed"));
    }
}
