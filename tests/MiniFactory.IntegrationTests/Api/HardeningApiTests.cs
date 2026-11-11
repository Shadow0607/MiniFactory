using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>
/// 驗收後修正項目的 API 層測試（預設設定：不限流、請求本文上限為預設值）：
/// B2 Idempotency 摘要用正規化 lineId、B7 通用 type、B8 401 在 Accept 不接受 JSON 時仍有本文、
/// S2 quantity 上限、S5 不信任外部 traceparent、S6 固定中文錯誤說明。
/// </summary>
[Collection(MariaDbCollection.Name)]
public class HardeningApiTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{
    private const string ForgedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string ForgedTraceParent = "00-" + ForgedTraceId + "-00f067aa0ba902b7-01";

    // ------------------------------------------------------------------ B2

    [Fact]
    public async Task B2_CreateOrder_SameKeyWithDifferentLineIdCasing_ReplaysInsteadOf409()
    {
        var orderNo = NewOrderNo();
        var key = "b2-" + Guid.NewGuid().ToString("N");

        using var first = await PostOrderAsync(ValidBody(orderNo, lineId: "line-01"), key);
        using var second = await PostOrderAsync(ValidBody(orderNo, lineId: "Line-01"), key);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("true", Header(second, "Idempotent-Replayed"));
        Assert.Equal(
            (await ReadJsonAsync(first)).GetProperty("orderId").GetGuid(),
            (await ReadJsonAsync(second)).GetProperty("orderId").GetGuid());
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM production_orders WHERE order_no = @orderNo", new { orderNo }));
    }

    [Fact]
    public async Task B2_CreateOrder_SameKeyWithDifferentOrderNoCasing_StillConflicts()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var key = "b2c-" + Guid.NewGuid().ToString("N");

        using var first = await PostOrderAsync(ValidBody("ORD-" + suffix), key);
        using var second = await PostOrderAsync(ValidBody("ord-" + suffix), key);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        await AssertProblemAsync(second, 409, "idempotency-conflict");
    }

    // ------------------------------------------------------------------ B7

    [Fact]
    public async Task B7_MethodNotAllowed_UsesGenericHttpStatusType()
    {
        using var response = await Client.PutAsync("/api/orders", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);

        var problem = await AssertProblemAsync(response, 405, "http-405");
        Assert.Equal("Method Not Allowed", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task B7_UnsupportedMediaType_UsesGenericHttpStatusType()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent("orderNo=1", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add("X-Api-Key", ApiFixture.OperatorKey);

        using var response = await Client.SendAsync(request, Ct);

        var problem = await AssertProblemAsync(response, 415, "http-415");
        Assert.Equal("Unsupported Media Type", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task B7_StatusCodesWithDedicatedTypes_KeepTheirOwnType()
    {
        using var response = await Client.GetAsync("/api/does-not-exist", Ct);

        await AssertProblemAsync(response, 404, "not-found");
    }

    // ------------------------------------------------------------------ B8

    [Fact]
    public async Task B8_Unauthorized_WhenClientDoesNotAcceptJson_StillWritesProblemBody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));

        using var response = await Client.SendAsync(request, Ct);

        var problem = await AssertProblemAsync(response, 401, "unauthorized");
        Assert.Equal("缺少或不正確的 X-Api-Key", problem.GetProperty("detail").GetString());
    }

    // ------------------------------------------------------------------ S2

    [Fact]
    public async Task S2_Quantity_AtMaximum_IsAccepted_AndOverMaximumReturns400()
    {
        using var atLimit = await PostOrderAsync(ValidBody(quantity: 100_000));
        Assert.Equal(HttpStatusCode.Accepted, atLimit.StatusCode);

        using var over = await PostOrderAsync(ValidBody(quantity: 100_001));

        var problem = await AssertProblemAsync(over, 400, "validation-failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty("quantity", out var messages));
        Assert.Contains("100000", messages[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S2_Quantity_IntMaxValue_Returns400()
    {
        using var response = await PostOrderAsync(ValidBody(quantity: int.MaxValue));

        await AssertProblemAsync(response, 400, "validation-failed");
    }

    // ------------------------------------------------------------------ S5

    [Fact]
    public async Task S5_CreateOrder_ForgedTraceparent_IsNotStored()
    {
        var orderNo = NewOrderNo();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent(JsonSerializer.Serialize(ValidBody(orderNo), CamelCase), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Api-Key", ApiFixture.OperatorKey);
        request.Headers.Add("traceparent", ForgedTraceParent);
        request.Headers.Add("tracestate", "evil=injected");

        using var response = await Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var orderId = (await ReadJsonAsync(response)).GetProperty("orderId").GetGuid();

        var stored = await Db.ScalarAsync<string>(Cs, "SELECT trace_parent FROM production_orders WHERE id = @orderId", new { orderId });
        Assert.NotNull(stored);
        Assert.Matches(TraceParentFormat(), stored!);
        Assert.NotEqual(ForgedTraceId, stored![3..35]);
        Assert.DoesNotContain("00f067aa0ba902b7", stored, StringComparison.Ordinal);

        // outbox：同樣是 API 自己的 trace，也不帶外部 tracestate
        var outbox = await Db.QueryAsync<(string? TraceParent, string? TraceState)>(
            Cs,
            """
            SELECT trace_parent, trace_state FROM outbox_messages
            WHERE aggregate_id = @orderId OR aggregate_id IN (SELECT id FROM production_jobs WHERE order_id = @orderId)
            """,
            new { orderId });
        Assert.NotEmpty(outbox);
        Assert.All(outbox, o =>
        {
            Assert.NotEqual(ForgedTraceId, o.TraceParent![3..35]);
            Assert.Null(o.TraceState);
        });
    }

    [Fact]
    public async Task S5_ErrorResponse_TraceId_IsNotTheForgedOne()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders?page=0");
        request.Headers.Add("traceparent", ForgedTraceParent);

        using var response = await Client.SendAsync(request, Ct);

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        Assert.DoesNotContain(ForgedTraceId, problem.GetProperty("traceId").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S5_RequestsWithoutTraceparent_StillGetTheirOwnTrace()
    {
        var orderId = await CreateOrderAsync();

        var stored = await Db.ScalarAsync<string>(Cs, "SELECT trace_parent FROM production_orders WHERE id = @orderId", new { orderId });
        Assert.Matches(TraceParentFormat(), stored!);
    }

    // ------------------------------------------------------------------ S6

    [Theory]
    [InlineData("/api/orders?page=abc")]
    [InlineData("/api/orders?pageSize=1.5")]
    [InlineData("/api/alerts?orderId=not-a-guid")]
    public async Task S6_BindingFailure_ReturnsFixedChineseMessage_NotTheFrameworkText(string url)
    {
        using var response = await Client.GetAsync(url, Ct);

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        var message = problem.GetProperty("errors").GetProperty("request")[0].GetString();
        Assert.Equal("請求格式不正確", message);
        var text = problem.GetRawText();
        Assert.DoesNotContain("Failed to bind", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Int32", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Guid", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S6_MalformedJson_ReturnsFixedChineseMessage()
    {
        using var response = await PostRawAsync("{ not json");

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        Assert.Equal("請求內容不是合法的 JSON，或欄位型別不符", problem.GetProperty("errors").GetProperty("body")[0].GetString());
    }
}
