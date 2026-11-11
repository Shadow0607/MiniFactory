using System.Net;
using System.Text;
using MiniFactory.Api;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>
/// S7：請求本文上限（預設 64 KB，可由 Http:MaxRequestBodyBytes 設定），超過回 413 Problem Details。
/// 上限由 Kestrel 執行，TestServer 不會套用，所以這裡啟動真正的 Kestrel（回送位址的隨機埠）。
/// </summary>
[Collection(MariaDbCollection.Name)]
public class RequestBodyLimitTests(MariaDbFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Json(int padding)
        => $$"""{"orderNo":"ORD-LIMIT","productCode":"Motor-A","quantity":5,"lineId":"Line-01","note":"{{new string('x', padding)}}"}""";

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Api-Key", ApiFixture.OperatorKey);
        return await client.SendAsync(request, Ct);
    }

    private async Task<MiniFactoryApiFactory> StartKestrelAsync(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var cs = await db.CreateMigratedDatabaseAsync();
        var factory = new MiniFactoryApiFactory(cs, settings: settings);
        factory.UseKestrel(0);
        factory.StartServer();
        return factory;
    }

    [Fact]
    public void DefaultLimit_Is64KB()
        => Assert.Equal(64 * 1024, RequestLimits.DefaultMaxRequestBodyBytes);

    [Fact]
    public async Task Body_JustOver64KB_Returns413ProblemDetails_AndSmallBodyStillWorks()
    {
        await using var factory = await StartKestrelAsync();
        using var client = factory.CreateClient();

        using var tooLarge = await PostAsync(client, Json(65 * 1024));
        using var normal = await PostAsync(client, Json(0));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal("application/problem+json", tooLarge.Content.Headers.ContentType?.MediaType);
        var text = await tooLarge.Content.ReadAsStringAsync(Ct);
        Assert.Contains("https://minifactory.dev/errors/http-413", text, StringComparison.Ordinal);
        Assert.Contains("traceId", text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Accepted, normal.StatusCode);
    }

    [Fact]
    public async Task Body_Under64KB_IsNotRejectedForSize()
    {
        await using var factory = await StartKestrelAsync();
        using var client = factory.CreateClient();

        using var response = await PostAsync(client, Json(60 * 1024)); // 未超過上限：額外欄位被忽略，照常建單

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Limit_IsConfigurable()
    {
        await using var factory = await StartKestrelAsync(new Dictionary<string, string?> { ["Http:MaxRequestBodyBytes"] = "512" });
        using var client = factory.CreateClient();

        using var tooLarge = await PostAsync(client, Json(600));
        using var ok = await PostAsync(client, Json(0));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
    }
}
