using System.Net;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>B3：新建訂單的所有實體 version 一律為 0（含第 1 道 Job 與其 execution），architecture.md §6 定案。</summary>
[Collection(MariaDbCollection.Name)]
public class InitialVersionTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{
    [Fact]
    public async Task B3_CreateOrder_AllEntitiesStartAtVersionZero()
    {
        using var response = await PostOrderAsync(ValidBody());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var orderId = (await ReadJsonAsync(response)).GetProperty("orderId").GetGuid();

        Assert.Equal(0, await CountAsync("SELECT version FROM production_orders WHERE id = @orderId", new { orderId }));

        // 3 道 Job（含 Ready 的第 1 道）逐一為 0；以「version <> 0 的筆數」檢查
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM production_jobs WHERE order_id = @orderId", new { orderId }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM production_jobs WHERE order_id = @orderId AND version <> 0", new { orderId }));

        // 第 1 道 Job 已開啟 attempt 1，execution 也必須為 0
        var executions = await CountAsync(
            "SELECT COUNT(*) FROM job_executions e JOIN production_jobs j ON j.id = e.job_id WHERE j.order_id = @orderId", new { orderId });
        Assert.True(executions >= 1);
        Assert.Equal(0, await CountAsync(
            "SELECT COUNT(*) FROM job_executions e JOIN production_jobs j ON j.id = e.job_id WHERE j.order_id = @orderId AND e.version <> 0", new { orderId }));
    }
}
