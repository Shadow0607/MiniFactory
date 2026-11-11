using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Alerts;
using MiniFactory.Application.Common;
using MiniFactory.Application.Equipments;
using MiniFactory.Application.Orders;
using MiniFactory.IntegrationTests.Support;
using MySqlConnector;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>/health/*（api.md §9）。</summary>
[Collection(MariaDbCollection.Name)]
public class HealthApiTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{
    [Fact]
    public async Task HealthLive_Returns200_WithoutCheckingDependencies()
    {
        using var response = await Client.GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_WhenMariaDbIsUp_Returns200WithComponentStatus()
    {
        using var response = await Client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.EndsWith("Z", body.GetProperty("checkedAt").GetString(), StringComparison.Ordinal);
        AssertAllTimestampsAreUtcZ(body);

        var mariadb = body.GetProperty("components").GetProperty("mariadb");
        Assert.Equal("Healthy", mariadb.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Number, mariadb.GetProperty("durationMs").ValueKind);
        Assert.False(mariadb.TryGetProperty("description", out _)); // 健康時不輸出 description
    }
}

/// <summary>相依服務（資料庫）不可用、未預期例外時的錯誤回應（architecture.md §10.7.6、§14.2）。</summary>
public class DatabaseDownTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 埠 1 沒有人在聽：連線會立刻被拒絕
    private const string UnreachableDb = "Server=127.0.0.1;Port=1;Database=minifactory;User ID=secret_user;Password=secret_pw_123;ConnectionTimeout=2";

    [Fact]
    public async Task HealthReady_WhenMariaDbIsDown_Returns503WithoutLeakingConnectionDetails()
    {
        await using var factory = new MiniFactoryApiFactory(UnreachableDb);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("Unhealthy", body.RootElement.GetProperty("status").GetString());
        var mariadb = body.RootElement.GetProperty("components").GetProperty("mariadb");
        Assert.Equal("Unhealthy", mariadb.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(mariadb.GetProperty("description").GetString()));

        // description 只放錯誤類別（api.md §9），不含主機 / 帳號 / 密碼
        Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthLive_WhenMariaDbIsDown_StillReturns200()
    {
        await using var factory = new MiniFactoryApiFactory(UnreachableDb);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/orders")]
    [InlineData("/api/equipment")]
    [InlineData("/api/alerts")]
    public async Task Get_WhenMariaDbIsDown_Returns503WithRetryAfter(string url)
    {
        await using var factory = new MiniFactoryApiFactory(UnreachableDb);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("https://minifactory.dev/errors/service-unavailable", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(503, body.RootElement.GetProperty("status").GetInt32());
        Assert.True(body.RootElement.TryGetProperty("traceId", out _));
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateOrder_WhenMariaDbIsDown_Returns503WithRetryAfter()
    {
        await using var factory = new MiniFactoryApiFactory(UnreachableDb);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent("""{"orderNo":"ORD-1","productCode":"M","quantity":1,"lineId":"Line-01"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Api-Key", ApiFixture.OperatorKey);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
    }

    // ------------------------------------------------------------ 500

    private sealed class ExplodingQueries : IDashboardQueries
    {
        private static Exception Boom() => new InvalidOperationException("internal detail: password=hunter2 at Secret.Class.Method()");

        public Task<PagedResult<OrderSummaryDto>> ListOrdersAsync(OrderListFilter filter, int page, int pageSize, CancellationToken cancellationToken = default) => throw Boom();

        public Task<OrderDetailDto?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default) => throw Boom();

        public Task<bool> OrderExistsAsync(Guid orderId, CancellationToken cancellationToken = default) => throw Boom();

        public Task<IReadOnlyList<OrderEventDto>> ListOrderEventsAsync(Guid orderId, Guid? afterEventId, CancellationToken cancellationToken = default) => throw Boom();

        public Task<IReadOnlyList<EquipmentDto>> ListEquipmentAsync(string? lineId, CancellationToken cancellationToken = default) => throw Boom();

        public Task<AlertListResponse> ListAlertsAsync(AlertListFilter filter, int page, int pageSize, CancellationToken cancellationToken = default) => throw Boom();
    }

    [Fact]
    public async Task UnexpectedException_Returns500ProblemWithoutStackOrMessage()
    {
        await using var factory = new MiniFactoryApiFactory(
            UnreachableDb, configureServices: services => services.AddScoped<IDashboardQueries, ExplodingQueries>());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/equipment", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("https://minifactory.dev/errors/internal-error", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("An unexpected error occurred", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(500, body.RootElement.GetProperty("status").GetInt32());
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", body.RootElement.GetProperty("traceId").GetString()!);

        // 不外洩例外訊息、堆疊、型別名稱
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret.Class", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
    }
}

/// <summary>啟動時的設定檢查：缺少必要設定就讓程序失敗。</summary>
public class StartupConfigurationTests
{
    private const string AnyDb = "Server=127.0.0.1;Port=1;Database=x;User ID=u;Password=p";

    private static string AllMessages(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    [Fact]
    public async Task Startup_WithoutOperatorApiKey_Fails()
    {
        await using var factory = new MiniFactoryApiFactory(AnyDb, operatorKey: null);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("OperatorApiKey", AllMessages(ex), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_WithIdenticalOperatorAndSimulatorKeys_Fails()
    {
        await using var factory = new MiniFactoryApiFactory(AnyDb, operatorKey: ApiFixture.OperatorKey, simulatorKey: ApiFixture.OperatorKey);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("不可相同", AllMessages(ex), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_WithoutSimulatorKey_Works_AndOperatorKeyStillWorks()
    {
        await using var factory = new MiniFactoryApiFactory(AnyDb, simulatorKey: null);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("short-key")]
    [InlineData("change-me-change-me-change-me-change-me")]
    [InlineData("11111111111111111111111111111111")] // 32 個相同字元
    [InlineData("test-operator-key-3f9a1c7e5b2d4a60 ")] // 含尾端空白
    [InlineData(" test-operator-key-3f9a1c7e5b2d4a60")] // 含前端空白
    public async Task Startup_WithWeakOperatorApiKey_Fails_WithoutPrintingTheKey(string weakKey)
    {
        await using var factory = new MiniFactoryApiFactory(AnyDb, operatorKey: weakKey);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var messages = AllMessages(ex);
        Assert.Contains("Security:OperatorApiKey", messages, StringComparison.Ordinal);
        Assert.DoesNotContain(weakKey, messages, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_WithWeakSimulatorApiKey_Fails()
    {
        await using var factory = new MiniFactoryApiFactory(AnyDb, simulatorKey: "replace-me-replace-me-replace-me-123");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Security:SimulatorApiKey", AllMessages(ex), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_WithNonPositiveMaxQuantity_Fails()
    {
        await using var factory = new MiniFactoryApiFactory(
            AnyDb, settings: new Dictionary<string, string?> { ["Orders:MaxQuantity"] = "0" });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Orders:MaxQuantity", AllMessages(ex), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_WithoutConnectionString_Fails()
    {
        await using var factory = new MiniFactoryApiFactory(string.Empty);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings", AllMessages(ex), StringComparison.Ordinal);
    }
}

/// <summary>以 DML-only 的 mf_app 帳號（正式部署用的最小權限）跑完整流程，確認 SQL 不需要 DDL 權限。</summary>
[Collection(MariaDbCollection.Name)]
public class LeastPrivilegeApiTests(MariaDbFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAndReadOrder_WithDmlOnlyAppAccount_Works()
    {
        var rootCs = await fixture.CreateMigratedDatabaseAsync();
        var database = new MySqlConnectionStringBuilder(rootCs).Database;
        await Db.ExecAsync(rootCs, $"GRANT SELECT, INSERT, UPDATE, DELETE ON `{database}`.* TO '{MariaDbFixture.AppUser}'@'%'");

        await using var factory = new MiniFactoryApiFactory(fixture.AppConnectionString(database));
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent("""{"orderNo":"ORD-LP-1","productCode":"M","quantity":3,"lineId":"Line-01"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Api-Key", ApiFixture.OperatorKey);
        request.Headers.Add("Idempotency-Key", "lp-1");

        using var created = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var orderId = body.RootElement.GetProperty("orderId").GetGuid();

        using var detail = await client.GetAsync($"/api/orders/{orderId}", Ct);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var events = await client.GetAsync($"/api/orders/{orderId}/events", Ct);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        using var ready = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}
