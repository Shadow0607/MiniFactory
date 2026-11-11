using System.Net;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Api;

// ---- 啟用限流的 fixture：每個測試類別各自一個主機，計數器互不干擾 ----

public sealed class RateLimitWriteFixture(MariaDbFixture db) : ApiFixture(db)
{
    protected override IReadOnlyDictionary<string, string?>? Settings => new Dictionary<string, string?>
    {
        ["RateLimiting:Enabled"] = "true",
        ["RateLimiting:DemoWrite:PermitLimit"] = "2",
        ["RateLimiting:DemoWrite:WindowSeconds"] = "60",
    };
}

public sealed class RateLimitReadFixture(MariaDbFixture db) : ApiFixture(db)
{
    protected override IReadOnlyDictionary<string, string?>? Settings => new Dictionary<string, string?>
    {
        ["RateLimiting:Enabled"] = "true",
        ["RateLimiting:PublicRead:PermitLimit"] = "3",
        ["RateLimiting:PublicRead:WindowSeconds"] = "60",
    };
}

/// <summary>S3：預設（Enabled=false）完全不限制，即使超過預設額度也不影響。</summary>
[Collection(MariaDbCollection.Name)]
public class RateLimitingDisabledTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Disabled_WriteEndpoint_IsNeverLimited_EvenBeyondDefaultQuota()
    {
        // 預設額度（若啟用）為每分鐘 10 次；送 14 次都應成功
        for (var i = 0; i < 14; i++)
        {
            using var response = await PostOrderAsync(ValidBody());
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
    }

    [Fact]
    public async Task Disabled_ReadEndpoints_AreNeverLimited()
    {
        for (var i = 0; i < 130; i++) // 預設額度（若啟用）為 120
        {
            using var response = await Client.GetAsync("/api/equipment", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}

/// <summary>S3：Enabled=true 時，寫入端點超量回 429 Problem Details（附 Retry-After）。</summary>
[Collection(MariaDbCollection.Name)]
public class RateLimitingWriteEnabledTests(RateLimitWriteFixture api) : ApiTestBase(api), IClassFixture<RateLimitWriteFixture>
{
    [Fact]
    public async Task Enabled_WriteEndpoint_OverQuota_Returns429ProblemDetails_AndOtherEndpointsAreUnaffected()
    {
        using var first = await PostOrderAsync(ValidBody());
        using var second = await PostOrderAsync(ValidBody());
        using var third = await PostOrderAsync(ValidBody());

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        var problem = await AssertProblemAsync(third, 429, "http-429");
        Assert.Equal("Too Many Requests", problem.GetProperty("title").GetString());
        Assert.True(int.Parse(Header(third, "Retry-After"), System.Globalization.CultureInfo.InvariantCulture) >= 1);

        // 限流在 API Key 檢查之前：沒帶 Key 的暴力猜測同樣被擋
        using var noKey = await PostOrderAsync(ValidBody(), apiKey: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, noKey.StatusCode);

        // 查詢端點使用另一個 policy（此 fixture 未調低額度），健康檢查不套用任何 policy
        using var read = await Client.GetAsync("/api/equipment", Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var live = await Client.GetAsync("/health/live", Ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}

/// <summary>S3：Enabled=true 時，查詢端點超量回 429。</summary>
[Collection(MariaDbCollection.Name)]
public class RateLimitingReadEnabledTests(RateLimitReadFixture api) : ApiTestBase(api), IClassFixture<RateLimitReadFixture>
{
    [Fact]
    public async Task Enabled_ReadEndpoint_OverQuota_Returns429ProblemDetails()
    {
        for (var i = 0; i < 3; i++)
        {
            using var ok = await Client.GetAsync("/api/orders", Ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await Client.GetAsync("/api/orders", Ct);

        await AssertProblemAsync(limited, 429, "http-429");
        Assert.True(HasHeader(limited, "Retry-After"));

        // 同一個 policy 涵蓋所有查詢端點；健康檢查不受影響
        using var alerts = await Client.GetAsync("/api/alerts", Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, alerts.StatusCode);
        using var live = await Client.GetAsync("/health/live", Ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
