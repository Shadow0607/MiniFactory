using System.Net;
using System.Text.Json;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>GET /api/orders、/api/orders/{id}、/api/orders/{id}/events 的回應格式（api.md §3.2～3.4）。</summary>
[Collection(MariaDbCollection.Name)]
public class OrderQueryApiTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{
    // ------------------------------------------------------------ 列表

    [Fact]
    public async Task ListOrders_ReturnsPagedShapeMatchingApiSpec()
    {
        var prefix = "LST" + Guid.NewGuid().ToString("N")[..8];
        var id = await CreateOrderAsync(prefix + "-1", quantity: 42);

        var body = await GetJsonAsync($"/api/orders?orderNo={prefix}");

        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        var item = Assert.Single(body.GetProperty("items").EnumerateArray());

        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal(prefix + "-1", item.GetProperty("orderNo").GetString());
        Assert.Equal("Motor-A", item.GetProperty("productCode").GetString());
        Assert.Equal(42, item.GetProperty("quantity").GetInt32());
        Assert.Equal("Line-01", item.GetProperty("lineId").GetString());
        Assert.Equal("Created", item.GetProperty("status").GetString());
        Assert.Equal(0, item.GetProperty("completedJobs").GetInt32());
        Assert.Equal(3, item.GetProperty("totalJobs").GetInt32());
        Assert.Equal(JsonValueKind.Number, item.GetProperty("version").ValueKind);
        Assert.EndsWith("Z", item.GetProperty("createdAt").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", item.GetProperty("updatedAt").GetString(), StringComparison.Ordinal);
        AssertAllTimestampsAreUtcZ(body);
    }

    [Fact]
    public async Task ListOrders_SortedByCreatedAtDescending()
    {
        var prefix = "SRT" + Guid.NewGuid().ToString("N")[..8];
        var first = await CreateOrderAsync(prefix + "-1");
        await Task.Delay(20, Ct);
        var second = await CreateOrderAsync(prefix + "-2");
        await Task.Delay(20, Ct);
        var third = await CreateOrderAsync(prefix + "-3");

        var body = await GetJsonAsync($"/api/orders?orderNo={prefix}");

        Assert.Equal([third, second, first], body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task ListOrders_Pagination_SplitsResultsAndReportsTotal()
    {
        var prefix = "PAG" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 1; i <= 3; i++)
        {
            await CreateOrderAsync($"{prefix}-{i}");
            await Task.Delay(10, Ct);
        }

        var page1 = await GetJsonAsync($"/api/orders?orderNo={prefix}&page=1&pageSize=2");
        var page2 = await GetJsonAsync($"/api/orders?orderNo={prefix}&page=2&pageSize=2");
        var beyond = await GetJsonAsync($"/api/orders?orderNo={prefix}&page=9&pageSize=2");

        Assert.Equal(3, page1.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page2.GetProperty("page").GetInt32());
        Assert.Equal(2, page2.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, beyond.GetProperty("items").GetArrayLength());
        Assert.Equal(3, beyond.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ListOrders_StatusFilter_SupportsRepeatedValues()
    {
        var prefix = "STA" + Guid.NewGuid().ToString("N")[..8];
        var a = await CreateOrderAsync(prefix + "-A");
        var b = await CreateOrderAsync(prefix + "-B");
        var c = await CreateOrderAsync(prefix + "-C");
        await Db.ExecAsync(Cs, "UPDATE production_orders SET status = 'InProgress' WHERE id = @a", new { a });
        await Db.ExecAsync(Cs, "UPDATE production_orders SET status = 'AwaitingConfirmation' WHERE id = @b", new { b });

        var created = await GetJsonAsync($"/api/orders?orderNo={prefix}&status=Created");
        var two = await GetJsonAsync($"/api/orders?orderNo={prefix}&status=InProgress&status=AwaitingConfirmation");
        var none = await GetJsonAsync($"/api/orders?orderNo={prefix}&status=Completed");

        Assert.Equal([c], created.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(
            new[] { a, b }.Order(),
            two.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Order());
        Assert.Equal(0, none.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ListOrders_CompletedJobsCountsOnlyCompletedJobs()
    {
        var prefix = "CNT" + Guid.NewGuid().ToString("N")[..8];
        var id = await CreateOrderAsync(prefix + "-1");
        await Db.ExecAsync(Cs, "UPDATE production_jobs SET status = 'Completed' WHERE order_id = @id AND `sequence` = 1", new { id });

        var body = await GetJsonAsync($"/api/orders?orderNo={prefix}");

        var item = body.GetProperty("items")[0];
        Assert.Equal(1, item.GetProperty("completedJobs").GetInt32());
        Assert.Equal(3, item.GetProperty("totalJobs").GetInt32());
    }

    [Fact]
    public async Task ListOrders_OrderNoFilterIsPrefixSearch_AndUnderscoreIsLiteral()
    {
        var token = Guid.NewGuid().ToString("N")[..6];
        await CreateOrderAsync($"PFX_{token}-1");
        await CreateOrderAsync($"PFXA{token}-1"); // 若 _ 被當成萬用字元，"PFX_" 會誤匹配這筆
        await CreateOrderAsync($"ZZ-PFX_{token}"); // 不是前綴

        var body = await GetJsonAsync($"/api/orders?orderNo=PFX_{token}");

        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal($"PFX_{token}-1", item.GetProperty("orderNo").GetString());
    }

    [Fact]
    public async Task ListOrders_PercentInOrderNo_IsLiteralNotWildcard()
    {
        var body = await GetJsonAsync("/api/orders?orderNo=%25");

        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("status=Bogus", "status")]
    [InlineData("status=1", "status")]
    [InlineData("status=InProgress,AwaitingConfirmation", "status")] // 逗號不可被當成位元 OR 合併
    [InlineData("status=InProgress%2CAwaitingConfirmation", "status")]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task ListOrders_InvalidQuery_Returns400WithFieldErrors(string query, string field)
    {
        using var response = await Client.GetAsync("/api/orders?" + query, Ct);

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task ListOrders_NonNumericPage_Returns400ProblemDetails()
    {
        using var response = await Client.GetAsync("/api/orders?page=abc", Ct);

        await AssertProblemAsync(response, 400, "validation-failed");
    }

    [Fact]
    public async Task ListOrders_PageSizeAtLimit100_IsAccepted()
    {
        var body = await GetJsonAsync("/api/orders?pageSize=100");

        Assert.Equal(100, body.GetProperty("pageSize").GetInt32());
    }

    // ------------------------------------------------------------ 詳情

    [Fact]
    public async Task GetOrder_ReturnsThreeJobsAndFirstAttemptMatchingApiSpec()
    {
        var orderNo = NewOrderNo("DET");
        var id = await CreateOrderAsync(orderNo, quantity: 77);

        var body = await GetJsonAsync($"/api/orders/{id}");

        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(orderNo, body.GetProperty("orderNo").GetString());
        Assert.Equal("Motor-A", body.GetProperty("productCode").GetString());
        Assert.Equal(77, body.GetProperty("quantity").GetInt32());
        Assert.Equal("Line-01", body.GetProperty("lineId").GetString());
        Assert.Equal("Created", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Number, body.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("completedAt").ValueKind);
        AssertAllTimestampsAreUtcZ(body);

        // traceId = 建單 Trace 的 trace-id（32 hex），與資料庫 trace_parent 一致
        var traceId = body.GetProperty("traceId").GetString();
        Assert.Matches("^[0-9a-f]{32}$", traceId!);
        var traceParent = await Db.ScalarAsync<string>(Cs, "SELECT trace_parent FROM production_orders WHERE id = @id", new { id });
        Assert.Equal(traceParent!.Split('-')[1], traceId);

        var jobs = body.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.Equal(3, jobs.Length);
        Assert.Equal([1, 2, 3], jobs.Select(j => j.GetProperty("sequence").GetInt32()));
        Assert.Equal(["Machining", "Assembly", "Inspection"], jobs.Select(j => j.GetProperty("operation").GetString()));
        Assert.Equal(["Ready", "Pending", "Pending"], jobs.Select(j => j.GetProperty("status").GetString()));
        Assert.Equal([1, 0, 0], jobs.Select(j => j.GetProperty("currentAttemptNo").GetInt32()));
        Assert.All(jobs, j =>
        {
            Assert.Equal(JsonValueKind.Null, j.GetProperty("assignedEquipment").ValueKind);
            Assert.Equal(JsonValueKind.Null, j.GetProperty("startedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, j.GetProperty("completedAt").ValueKind);
        });

        var execution = Assert.Single(jobs[0].GetProperty("executions").EnumerateArray());
        Assert.Equal(1, execution.GetProperty("attemptNo").GetInt32());
        Assert.NotEqual(Guid.Empty, execution.GetProperty("commandId").GetGuid());
        Assert.Equal("Pending", execution.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, execution.GetProperty("equipment").ValueKind);
        Assert.Equal(0, execution.GetProperty("sendCount").GetInt32());
        foreach (var nullable in new[] { "lastSentAt", "startedAt", "completedAt", "errorCode" })
        {
            Assert.Equal(JsonValueKind.Null, execution.GetProperty(nullable).ValueKind);
        }

        Assert.Equal(0, jobs[1].GetProperty("executions").GetArrayLength());
        Assert.Equal(0, jobs[2].GetProperty("executions").GetArrayLength());
    }

    [Fact]
    public async Task GetOrder_WithAssignedEquipmentAndExecutions_ShowsEquipmentRefsAndOrdersAttempts()
    {
        var id = await CreateOrderAsync();
        var jobId = await Db.ScalarAsync<Guid>(Cs, "SELECT id FROM production_jobs WHERE order_id = @id AND `sequence` = 1", new { id });
        var machineA = await Db.ScalarAsync<Guid>(Cs, "SELECT id FROM equipment WHERE name = 'Machine-A'");
        var now = Db.T0;

        // 模擬 Worker 之後的狀態：attempt 1 失敗、attempt 2 執行中並指派到 Machine-A
        await Db.ExecAsync(Cs, "UPDATE job_executions SET status = 'Failed', equipment_id = @machineA, error_code = 'SPINDLE_OVERHEAT', send_count = 2, last_sent_at = @now WHERE job_id = @jobId", new { machineA, jobId, now });
        await Db.ExecAsync(Cs, """
            INSERT INTO job_executions (id, job_id, attempt_no, command_id, status, equipment_id, send_count, started_at, version, created_at, updated_at)
            VALUES (@eid, @jobId, 2, @cmd, 'Running', @machineA, 1, @now, 0, @now, @now)
            """, new { eid = Guid.CreateVersion7(), jobId, cmd = Guid.CreateVersion7(), machineA, now });
        await Db.ExecAsync(Cs, "UPDATE production_jobs SET status = 'Running', assigned_equipment_id = @machineA, current_attempt_no = 2, started_at = @now WHERE id = @jobId", new { machineA, jobId, now });

        var body = await GetJsonAsync($"/api/orders/{id}");

        var job = body.GetProperty("jobs")[0];
        Assert.Equal("Running", job.GetProperty("status").GetString());
        Assert.Equal(machineA, job.GetProperty("assignedEquipment").GetProperty("id").GetGuid());
        Assert.Equal("Machine-A", job.GetProperty("assignedEquipment").GetProperty("name").GetString());
        Assert.Equal("2026-11-11T08:00:00.000Z", job.GetProperty("startedAt").GetString());

        var executions = job.GetProperty("executions").EnumerateArray().ToArray();
        Assert.Equal([1, 2], executions.Select(e => e.GetProperty("attemptNo").GetInt32()));
        Assert.Equal("Failed", executions[0].GetProperty("status").GetString());
        Assert.Equal("SPINDLE_OVERHEAT", executions[0].GetProperty("errorCode").GetString());
        Assert.Equal(2, executions[0].GetProperty("sendCount").GetInt32());
        Assert.Equal("Machine-A", executions[0].GetProperty("equipment").GetProperty("name").GetString());
        Assert.Equal("Running", executions[1].GetProperty("status").GetString());
        AssertAllTimestampsAreUtcZ(body);
    }

    [Fact]
    public async Task GetOrder_Unknown_Returns404Problem()
    {
        using var response = await Client.GetAsync($"/api/orders/{Guid.NewGuid()}", Ct);

        var problem = await AssertProblemAsync(response, 404, "not-found");
        Assert.Equal("Resource not found", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetOrder_NotAGuid_Returns404Problem()
    {
        using var response = await Client.GetAsync("/api/orders/not-a-guid", Ct);

        await AssertProblemAsync(response, 404, "not-found");
    }

    // ------------------------------------------------------------ 事件時間線

    [Fact]
    public async Task GetOrderEvents_ReturnsTimelineInOrder_MatchingApiSpec()
    {
        var id = await CreateOrderAsync();

        var body = await GetJsonAsync($"/api/orders/{id}/events");

        Assert.Equal(id, body.GetProperty("orderId").GetGuid());
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["OrderCreated", "JobDispatchRequested"], items.Select(i => i.GetProperty("eventType").GetString()));
        Assert.All(items, i =>
        {
            Assert.NotEqual(Guid.Empty, i.GetProperty("eventId").GetGuid());
            Assert.Equal(1, i.GetProperty("eventVersion").GetInt32());
            Assert.EndsWith("Z", i.GetProperty("occurredAt").GetString(), StringComparison.Ordinal);
        });
        AssertAllTimestampsAreUtcZ(body);

        var jobIds = (await GetJsonAsync($"/api/orders/{id}")).GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("id").GetGuid()).ToArray();

        var created = items[0].GetProperty("payload");
        Assert.Equal(id, created.GetProperty("orderId").GetGuid());
        Assert.Equal(jobIds, created.GetProperty("jobIds").EnumerateArray().Select(e => e.GetGuid()));

        var dispatch = items[1].GetProperty("payload");
        Assert.Equal(jobIds[0], dispatch.GetProperty("jobId").GetGuid());
        Assert.Equal(JsonValueKind.Null, dispatch.GetProperty("equipmentId").ValueKind);
        Assert.Equal(1, dispatch.GetProperty("attemptNo").GetInt32());
        var commandId = await Db.ScalarAsync<Guid>(Cs, "SELECT command_id FROM job_executions WHERE job_id = @job", new { job = jobIds[0] });
        Assert.Equal(commandId, dispatch.GetProperty("commandId").GetGuid());
    }

    [Fact]
    public async Task GetOrderEvents_After_ReturnsOnlyLaterEvents()
    {
        var id = await CreateOrderAsync();
        var all = (await GetJsonAsync($"/api/orders/{id}/events")).GetProperty("items").EnumerateArray().ToArray();

        var afterFirst = await GetJsonAsync($"/api/orders/{id}/events?after={all[0].GetProperty("eventId").GetGuid()}");
        var afterLast = await GetJsonAsync($"/api/orders/{id}/events?after={all[1].GetProperty("eventId").GetGuid()}");

        Assert.Equal(
            [all[1].GetProperty("eventId").GetGuid()],
            afterFirst.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("eventId").GetGuid()));
        Assert.Equal(0, afterLast.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetOrderEvents_AfterIsExclusive_AndRespectsSameTimestampTieBreakById()
    {
        var id = await CreateOrderAsync();
        var t = Db.T0;
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToArray();
        foreach (var eventId in ids)
        {
            await Db.ExecAsync(Cs, "INSERT INTO order_events (id, order_id, event_type, event_version, payload, occurred_at) VALUES (@eventId, @id, 'CommandResent', 1, '{}', @t)", new { eventId, id, t });
        }

        var all = (await GetJsonAsync($"/api/orders/{id}/events")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("eventId").GetGuid()).ToArray();

        // 相同 occurredAt 的 4 筆以 id 排序；任一筆當游標，結果必須恰為排在它之後的那幾筆
        var sameTime = all.Where(e => ids.Contains(e)).ToArray();
        Assert.Equal(4, sameTime.Length);
        for (var i = 0; i < sameTime.Length; i++)
        {
            var body = await GetJsonAsync($"/api/orders/{id}/events?after={sameTime[i]}");
            var returned = body.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("eventId").GetGuid()).ToArray();
            Assert.Equal(all.Skip(Array.IndexOf(all, sameTime[i]) + 1), returned);
        }
    }

    [Fact]
    public async Task GetOrderEvents_AfterUnknownEvent_Returns404()
    {
        var id = await CreateOrderAsync();

        using var response = await Client.GetAsync($"/api/orders/{id}/events?after={Guid.NewGuid()}", Ct);

        await AssertProblemAsync(response, 404, "not-found");
    }

    [Fact]
    public async Task GetOrderEvents_AfterEventOfAnotherOrder_Returns404()
    {
        var a = await CreateOrderAsync();
        var b = await CreateOrderAsync();
        var eventOfB = (await GetJsonAsync($"/api/orders/{b}/events")).GetProperty("items")[0].GetProperty("eventId").GetGuid();

        using var response = await Client.GetAsync($"/api/orders/{a}/events?after={eventOfB}", Ct);

        await AssertProblemAsync(response, 404, "not-found");
    }

    [Fact]
    public async Task GetOrderEvents_UnknownOrder_Returns404()
    {
        using var response = await Client.GetAsync($"/api/orders/{Guid.NewGuid()}/events", Ct);

        await AssertProblemAsync(response, 404, "not-found");
    }

    [Fact]
    public async Task GetOrderEvents_InvalidAfter_Returns400()
    {
        var id = await CreateOrderAsync();

        using var response = await Client.GetAsync($"/api/orders/{id}/events?after=nope", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, 400, "validation-failed");
    }
}
