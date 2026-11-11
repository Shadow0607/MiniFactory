using System.Net;
using System.Text.Json;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>POST /api/orders：回應格式、同交易寫入、驗證（400）、授權（401）、Correlation ID。</summary>
[Collection(MariaDbCollection.Name)]
public class CreateOrderApiTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{
    // ------------------------------------------------------------ 成功

    [Fact]
    public async Task CreateOrder_Valid_Returns202WithApiSpecShape()
    {
        var orderNo = NewOrderNo();

        using var response = await PostOrderAsync(ValidBody(orderNo));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        var orderId = body.GetProperty("orderId").GetGuid();

        Assert.Equal(orderNo, body.GetProperty("orderNo").GetString());
        Assert.Equal("Created", body.GetProperty("status").GetString());
        // 本階段 API 尚未持有 RabbitMQ 連線，事件只寫入 Outbox → Pending
        Assert.Equal("Pending", body.GetProperty("dispatchStatus").GetString());
        Assert.Equal(3, body.GetProperty("jobIds").GetArrayLength());
        Assert.Equal($"/api/orders/{orderId}", response.Headers.Location?.OriginalString);
        Assert.False(HasHeader(response, "Idempotent-Replayed"));
        AssertAllTimestampsAreUtcZ(body);
        Assert.EndsWith("Z", body.GetProperty("createdAt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateOrder_Valid_WritesOrderJobsExecutionEventsAndOutboxInSameTransaction()
    {
        var orderNo = NewOrderNo();
        using var response = await PostOrderAsync(ValidBody(orderNo, quantity: 120), correlationId: "corr-create-1");
        var body = await ReadJsonAsync(response);
        var orderId = body.GetProperty("orderId").GetGuid();
        var jobIds = body.GetProperty("jobIds").EnumerateArray().Select(e => e.GetGuid()).ToArray();

        var order = (await Db.QueryAsync<(string status, int quantity, string line, string correlation, string? trace)>(
            Cs, "SELECT status, quantity, line_id, correlation_id, trace_parent FROM production_orders WHERE id = @orderId", new { orderId })).Single();
        Assert.Equal("Created", order.status);
        Assert.Equal(120, order.quantity);
        Assert.Equal("Line-01", order.line);
        Assert.Equal("corr-create-1", order.correlation);
        Assert.NotNull(order.trace);
        Assert.Matches(TraceParentFormat(), order.trace!);

        var jobs = await Db.QueryAsync<(Guid id, int seq, string operation, string status)>(
            Cs, "SELECT id, `sequence`, operation, status FROM production_jobs WHERE order_id = @orderId ORDER BY `sequence`", new { orderId });
        Assert.Equal(jobIds, jobs.Select(j => j.id));
        Assert.Equal(["Machining", "Assembly", "Inspection"], jobs.Select(j => j.operation));
        Assert.Equal(["Ready", "Pending", "Pending"], jobs.Select(j => j.status));

        // 第 1 道已開啟 attempt 1，commandId 在派工前就持久化
        var executions = await Db.QueryAsync<(Guid jobId, int attempt, string status)>(
            Cs, "SELECT job_id, attempt_no, status FROM job_executions WHERE job_id IN @jobIds", new { jobIds });
        var execution = Assert.Single(executions);
        Assert.Equal((jobIds[0], 1, "Pending"), execution);

        // order_events 與 outbox 以同一個 eventId 成對寫入，且尚未發送
        var events = await Db.QueryAsync<(Guid id, string type)>(
            Cs, "SELECT id, event_type FROM order_events WHERE order_id = @orderId ORDER BY occurred_at, id", new { orderId });
        Assert.Equal(["OrderCreated", "JobDispatchRequested"], events.Select(e => e.type));

        var outbox = await Db.QueryAsync<(Guid id, string type, string? correlation, string? trace, DateTime? publishedAt)>(
            Cs, "SELECT id, event_type, correlation_id, trace_parent, published_at FROM outbox_messages WHERE id IN @ids ORDER BY occurred_at, id",
            new { ids = events.Select(e => e.id).ToArray() });
        Assert.Equal(events.Select(e => e.id), outbox.Select(o => o.id));
        Assert.All(outbox, o =>
        {
            Assert.Equal("corr-create-1", o.correlation);
            Assert.Equal(order.trace, o.trace);
            Assert.Null(o.publishedAt); // 本階段只寫不發送
        });
    }

    [Fact]
    public async Task CreateOrder_WithoutIdempotencyKey_Works_AndLeavesNoIdempotencyRecord()
    {
        using var response = await PostOrderAsync(ValidBody());
        var orderId = (await ReadJsonAsync(response)).GetProperty("orderId").GetGuid();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE resource_id = @orderId", new { orderId }));
    }

    [Fact]
    public async Task CreateOrder_LineIdIsStoredInCanonicalCase()
    {
        using var response = await PostOrderAsync(ValidBody(lineId: "line-01"));
        var orderId = (await ReadJsonAsync(response)).GetProperty("orderId").GetGuid();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Line-01", await Db.ScalarAsync<string>(Cs, "SELECT line_id FROM production_orders WHERE id = @orderId", new { orderId }));
    }

    [Fact]
    public async Task CreateOrder_JsonBodyWithDifferentFieldOrderAndCasing_IsAccepted()
    {
        var orderNo = NewOrderNo();
        var raw = $$"""{ "LINEID": "Line-01", "Quantity": 5, "productCode": "Motor-A", "orderNo": "{{orderNo}}", "extra": true }""";

        using var response = await PostRawAsync(raw);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // ------------------------------------------------------------ Correlation ID

    [Fact]
    public async Task Response_EchoesProvidedCorrelationId()
    {
        using var response = await PostOrderAsync(ValidBody(), correlationId: "my-trace.id:123");

        Assert.Equal("my-trace.id:123", Header(response, "X-Correlation-Id"));
    }

    [Fact]
    public async Task Response_GeneratesCorrelationIdWhenMissing()
    {
        using var response = await PostOrderAsync(ValidBody());

        Assert.True(Guid.TryParse(Header(response, "X-Correlation-Id"), out _), "未帶時應產生 UUID");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("bad<script>")]
    public async Task Response_ReplacesUnsafeCorrelationId(string unsafeValue)
    {
        using var response = await PostOrderAsync(ValidBody(), correlationId: unsafeValue);

        var echoed = Header(response, "X-Correlation-Id");
        Assert.NotEqual(unsafeValue, echoed);
        Assert.True(Guid.TryParse(echoed, out _));
    }

    [Fact]
    public async Task Response_ReplacesTooLongCorrelationId()
    {
        using var response = await PostOrderAsync(ValidBody(), correlationId: new string('a', 65));

        Assert.True(Guid.TryParse(Header(response, "X-Correlation-Id"), out _));
    }

    [Fact]
    public async Task ErrorResponses_AlsoCarryCorrelationId()
    {
        using var response = await PostOrderAsync(new { }, correlationId: "corr-err");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("corr-err", Header(response, "X-Correlation-Id"));
    }

    // ------------------------------------------------------------ 400

    [Fact]
    public async Task CreateOrder_EmptyBody_Returns400ValidationProblemWithErrorsPerField()
    {
        using var response = await PostOrderAsync(new { });

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        Assert.Equal("One or more validation errors occurred", problem.GetProperty("title").GetString());
        var errors = problem.GetProperty("errors");
        foreach (var field in new[] { "orderNo", "productCode", "quantity", "lineId" })
        {
            Assert.Equal(JsonValueKind.Array, errors.GetProperty(field).ValueKind);
        }
    }

    [Fact]
    public async Task CreateOrder_NonPositiveQuantity_Returns400WithSpecMessage()
    {
        using var response = await PostOrderAsync(ValidBody(quantity: 0));

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        Assert.Equal("quantity 必須大於 0", problem.GetProperty("errors").GetProperty("quantity")[0].GetString());
    }

    [Fact]
    public async Task CreateOrder_UnknownLine_Returns400WithSpecMessage()
    {
        using var response = await PostOrderAsync(ValidBody(lineId: "Line-99"));

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        Assert.Equal("產線 'Line-99' 不存在", problem.GetProperty("errors").GetProperty("lineId")[0].GetString());
    }

    [Fact]
    public async Task CreateOrder_InvalidOrderNoCharacters_Returns400()
    {
        using var response = await PostOrderAsync(ValidBody("ORD 001/x"));

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty("orderNo", out _));
    }

    [Theory]
    [InlineData("""{ "orderNo": "X-1", "productCode": "M", "quantity": "abc", "lineId": "Line-01" }""")]
    [InlineData("""{ "orderNo": "X-1", "productCode": "M", "quantity": 1.5, "lineId": "Line-01" }""")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public async Task CreateOrder_MalformedJsonOrWrongTypes_Returns400ProblemDetails(string raw)
    {
        using var response = await PostRawAsync(raw);

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        Assert.True(problem.TryGetProperty("errors", out _));
        // 不洩漏內部型別名稱 / 堆疊
        Assert.DoesNotContain("System.", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateOrder_NoBody_Returns400()
    {
        using var response = await PostRawAsync(null);

        await AssertProblemAsync(response, 400, "validation-failed");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("中文key")]
    public async Task CreateOrder_InvalidIdempotencyKey_Returns400OnHeader(string key)
    {
        using var response = await PostOrderAsync(ValidBody(), key);

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
    }

    [Fact]
    public async Task CreateOrder_IdempotencyKeyLongerThan128_Returns400()
    {
        using var response = await PostOrderAsync(ValidBody(), new string('k', 129));

        await AssertProblemAsync(response, 400, "validation-failed");
    }

    [Fact]
    public async Task CreateOrder_IdempotencyKeyOfExactly128_IsAccepted()
    {
        using var response = await PostOrderAsync(ValidBody(), new string('k', 100) + Guid.NewGuid().ToString("N")[..28]);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_InvalidInput_WritesNothing()
    {
        var before = await CountAsync("SELECT (SELECT COUNT(*) FROM production_orders) + (SELECT COUNT(*) FROM outbox_messages) + (SELECT COUNT(*) FROM idempotency_keys)");

        using var response = await PostOrderAsync(ValidBody(quantity: -5), "k-invalid-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await CountAsync("SELECT (SELECT COUNT(*) FROM production_orders) + (SELECT COUNT(*) FROM outbox_messages) + (SELECT COUNT(*) FROM idempotency_keys)"));
    }

    // ------------------------------------------------------------ 401

    [Fact]
    public async Task CreateOrder_WithoutApiKey_Returns401()
    {
        var before = await CountAsync("SELECT COUNT(*) FROM production_orders");

        using var response = await PostOrderAsync(ValidBody(), apiKey: null);

        await AssertProblemAsync(response, 401, "unauthorized");
        Assert.Equal(before, await CountAsync("SELECT COUNT(*) FROM production_orders"));
    }

    [Fact]
    public async Task CreateOrder_WithWrongApiKey_Returns401()
    {
        using var response = await PostOrderAsync(ValidBody(), apiKey: "definitely-wrong");

        await AssertProblemAsync(response, 401, "unauthorized");
    }

    [Fact]
    public async Task CreateOrder_WithSimulatorKey_Returns401()
    {
        // 端點只接受指定的那一把 Key（api.md §1.1）
        using var response = await PostOrderAsync(ValidBody(), apiKey: ApiFixture.SimulatorKey);

        await AssertProblemAsync(response, 401, "unauthorized");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(ApiFixture.OperatorKey + "x")]
    [InlineData("x" + ApiFixture.OperatorKey)]
    [InlineData("TEST-OPERATOR-KEY-3F9A1C")] // 大小寫不同也不行
    public async Task CreateOrder_WithNearMissApiKeys_Returns401(string key)
    {
        using var response = await PostOrderAsync(ValidBody(), apiKey: key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_UnauthorizedRequestWithMalformedBody_Returns401Not400()
    {
        // 授權在參數繫結之前：沒帶 Key 的人不該能探測 body 驗證規則
        using var response = await PostRawAsync("{ not json", apiKey: null);

        await AssertProblemAsync(response, 401, "unauthorized");
    }

    [Fact]
    public async Task CreateOrder_MultipleApiKeyHeaders_Returns401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(ValidBody(), CamelCase), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Api-Key", [ApiFixture.OperatorKey, ApiFixture.OperatorKey]);

        using var response = await Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unauthorized_ResponseDoesNotEchoTheProvidedKey()
    {
        const string probe = "probe-key-should-never-appear";

        using var response = await PostOrderAsync(ValidBody(), apiKey: probe);

        Assert.DoesNotContain(probe, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(probe, string.Join(";", response.Headers.SelectMany(h => h.Value)), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 其他路由 / 方法

    [Fact]
    public async Task UnknownRoute_Returns404Problem()
    {
        using var response = await Client.GetAsync("/api/does-not-exist", Ct);

        await AssertProblemAsync(response, 404, "not-found");
    }

    [Fact]
    public async Task GetEndpoints_DoNotRequireApiKey()
    {
        foreach (var url in new[] { "/api/orders", "/api/equipment", "/api/alerts", "/health/live" })
        {
            using var response = await Client.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
