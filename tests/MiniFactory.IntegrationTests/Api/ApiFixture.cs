using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MiniFactory.Api;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>
/// 以 WebApplicationFactory 啟動真正的 API，連到共用 MariaDB 容器中<b>專屬於這個測試類別</b>的全新資料庫
/// （已跑完全部遷移與 equipment seed），所以各測試類別的資料互不干擾。
/// API Key 為測試專用的假值。
/// </summary>
public class ApiFixture(MariaDbFixture db) : IAsyncLifetime
{
    public const string OperatorKey = "test-operator-key-3f9a1c7e5b2d4a60";
    public const string SimulatorKey = "test-simulator-key-8b2e7d1f9c3a5e40";

    private MiniFactoryApiFactory? _factory;

    public string ConnectionString { get; private set; } = string.Empty;

    public HttpClient Client { get; private set; } = default!;

    public MiniFactoryApiFactory Factory => _factory!;

    /// <summary>子類別可覆寫，提供額外的設定值（例如啟用限流）。</summary>
    protected virtual IReadOnlyDictionary<string, string?>? Settings => null;

    public async ValueTask InitializeAsync()
    {
        ConnectionString = await db.CreateMigratedDatabaseAsync();
        _factory = new MiniFactoryApiFactory(ConnectionString, settings: Settings);
        Client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }
}

/// <summary>測試用 API 主機。設定值以 UseSetting 覆寫，不碰任何環境變數。</summary>
public sealed class MiniFactoryApiFactory(
    string connectionString,
    string? operatorKey = ApiFixture.OperatorKey,
    string? simulatorKey = ApiFixture.SimulatorKey,
    Action<IServiceCollection>? configureServices = null,
    IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<ApiAssemblyMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:MiniFactory", connectionString);
        if (operatorKey is not null)
        {
            builder.UseSetting("Security:OperatorApiKey", operatorKey);
        }

        if (simulatorKey is not null)
        {
            builder.UseSetting("Security:SimulatorApiKey", simulatorKey);
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        // 測試輸出不需要每個請求一行 JSON
        builder.UseSetting("Serilog:MinimumLevel:Default", "Error");

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }
}

/// <summary>API 測試共用的請求 / 斷言輔助。</summary>
public abstract partial class ApiTestBase(ApiFixture api)
{
    protected static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected HttpClient Client => api.Client;

    protected string Cs => api.ConnectionString;

    protected static string NewOrderNo(string prefix = "ORD") => $"{prefix}-{Guid.NewGuid():N}"[..28];

    protected static object ValidBody(string? orderNo = null, int quantity = 500, string lineId = "Line-01")
        => new { orderNo = orderNo ?? NewOrderNo(), productCode = "Motor-A", quantity, lineId };

    /// <summary>送出建單請求；<paramref name="apiKey"/> 傳 null 代表不帶 X-Api-Key。</summary>
    protected Task<HttpResponseMessage> PostOrderAsync(
        object body,
        string? idempotencyKey = null,
        string? apiKey = ApiFixture.OperatorKey,
        string? correlationId = null)
        => PostRawAsync(JsonSerializer.Serialize(body, CamelCase), idempotencyKey, apiKey, correlationId);

    protected async Task<HttpResponseMessage> PostRawAsync(
        string? rawBody,
        string? idempotencyKey = null,
        string? apiKey = ApiFixture.OperatorKey,
        string? correlationId = null,
        HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
        if (rawBody is not null)
        {
            request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
        }

        if (apiKey is not null)
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (correlationId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        }

        return await (client ?? Client).SendAsync(request, Ct);
    }

    protected static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    protected async Task<JsonElement> GetJsonAsync(string url, int expectedStatus = 200)
    {
        using var response = await Client.GetAsync(url, Ct);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        return await ReadJsonAsync(response);
    }

    /// <summary>建立一張訂單並回傳 orderId（失敗就讓測試失敗）。</summary>
    protected async Task<Guid> CreateOrderAsync(string? orderNo = null, string? idempotencyKey = null, int quantity = 500)
    {
        using var response = await PostOrderAsync(ValidBody(orderNo, quantity), idempotencyKey);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("orderId").GetGuid();
    }

    protected Task<long> CountAsync(string sql, object? param = null) => Db.ScalarAsync<long>(Cs, sql, param);

    // ---- 斷言 ----

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")]
    private static partial Regex UtcTimestamp();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T")]
    private static partial Regex LooksLikeTimestamp();

    [GeneratedRegex(@"^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$")]
    protected static partial Regex TraceParentFormat();

    /// <summary>JSON 內所有看起來像時間的字串都必須是 UTC、毫秒、以 Z 結尾（不可是 +00:00）。</summary>
    protected static void AssertAllTimestampsAreUtcZ(JsonElement element, string path = "$")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString()!;
                if (LooksLikeTimestamp().IsMatch(text))
                {
                    Assert.True(UtcTimestamp().IsMatch(text), $"{path} 的時間 '{text}' 不是 UTC 且以 Z 結尾的 yyyy-MM-ddTHH:mm:ss.fffZ 格式");
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AssertAllTimestampsAreUtcZ(property.Value, $"{path}.{property.Name}");
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    AssertAllTimestampsAreUtcZ(item, $"{path}[{index++}]");
                }

                break;
        }
    }

    /// <summary>驗證 Problem Details 的共同欄位並回傳本文（api.md §2）。</summary>
    protected static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, int status, string typeSlug)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await ReadJsonAsync(response);
        Assert.Equal($"https://minifactory.dev/errors/{typeSlug}", body.GetProperty("type").GetString());
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.Matches(TraceParentFormat(), body.GetProperty("traceId").GetString()!);
        Assert.True(body.TryGetProperty("instance", out _));
        return body;
    }

    protected static string Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;

    protected static bool HasHeader(HttpResponseMessage response, string name) => response.Headers.Contains(name);
}
