using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MiniFactory.Api.Security;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>
/// R7 防漏標：列舉 API 的所有 endpoint，凡是需要 API Key 的都必須同時掛上限流 policy。
/// 限流在 API Key 中介層之前執行，才擋得住暴力猜測 Key；Phase 2 新增端點（例如 equipment-events）時若只標
/// RequireApiKey 而漏掉 RequireRateLimiting，這個測試會失敗。不需要資料庫。
/// </summary>
public class EndpointMetadataGuardTests
{
    private const string AnyDb = "Server=127.0.0.1;Port=1;Database=x;User ID=u;Password=p";

    private static async Task<List<RouteEndpoint>> ListEndpointsAsync()
    {
        await using var factory = new MiniFactoryApiFactory(AnyDb);
        return factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    [Fact]
    public async Task EveryEndpointRequiringApiKey_AlsoHasRateLimiting()
    {
        var endpoints = await ListEndpointsAsync();
        var withApiKey = endpoints.Where(e => e.Metadata.GetMetadata<ApiKeyRequirement>() is not null).ToList();

        // 目前至少有建單；避免列舉不到端點時測試空轉通過
        Assert.NotEmpty(withApiKey);
        var missing = withApiKey
            .Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>() is null)
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {e.RoutePattern.RawText}")
            .ToList();

        Assert.True(missing.Count == 0, $"需要 API Key 但沒有 RequireRateLimiting 的端點：{string.Join("; ", missing)}");
    }

    [Fact]
    public async Task EveryBusinessEndpoint_UnderApiPrefix_HasRateLimiting()
    {
        // 更嚴格的一層：/api/* 的所有端點（含不需 API Key 的查詢）都要有限流 metadata；/health/* 不在此列
        var endpoints = await ListEndpointsAsync();
        var apiEndpoints = endpoints.Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true).ToList();

        Assert.NotEmpty(apiEndpoints);
        var missing = apiEndpoints
            .Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.True(missing.Count == 0, $"/api/* 端點缺少 RequireRateLimiting：{string.Join("; ", missing)}");
    }
}
