using Microsoft.AspNetCore.Mvc;
using MiniFactory.Api.Errors;
using MiniFactory.Api.RateLimiting;
using MiniFactory.Api.Security;
using MiniFactory.Application.Orders;
using MySqlConnector;

namespace MiniFactory.Api;

/// <summary>
/// 供 <c>WebApplicationFactory&lt;T&gt;</c> 定位 API 組件。不直接用 <c>Program</c>：
/// 每個執行檔專案的頂層陳述式都會產生全域的 <c>Program</c>，整合測試同時參考多個專案時會有型別衝突。
/// </summary>
public sealed class ApiAssemblyMarker;

/// <summary>
/// 補齊框架自行產生的 Problem Details（例如找不到路由的 404、UseStatusCodePages 的空本文錯誤）：
/// 沒有指定 type 時依狀態碼補上 api.md §2.1 的 type 與 title，並一律加上 instance 與 traceId。
/// 自己建立的 Problem（ProblemFactory）已完整，這裡不會覆蓋。
/// </summary>
public static class ProblemDetailsCustomizer
{
    public static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;
        problem.Status = status;

        // 框架預設的 type 是 https://tools.ietf.org/html/rfc... 或 null
        if (problem.Type is null || problem.Type.StartsWith("https://tools.ietf.org/", StringComparison.Ordinal))
        {
            // 有專屬業務類型的狀態碼（400/401/404/500/503）用專屬 type；
            // 其餘（405、413、415、429…）統一使用通用的 http-{status}，保留框架給的 title
            var known = ProblemTypes.ForStatus(status) ?? ProblemTypes.ForHttpStatus(status, problem.Title);
            problem.Type = known.Uri;
            problem.Title = known.Title;
        }

        problem.Instance ??= ProblemFactory.InstanceOf(context.HttpContext);
        problem.Extensions.TryAdd(ProblemFactory.TraceIdKey, ProblemFactory.TraceIdOf(context.HttpContext));
    }
}

/// <summary>
/// 啟動時檢查必要設定（API Key、連線字串）。缺少就讓程序啟動失敗，
/// 而不是等到第一個請求才發現，或帶著沒有保護的 API 上線。
/// </summary>
public sealed class StartupConfigurationCheck(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<ApiKeyValidator>(); // 建構子內含 SecurityOptions.Validate()
        _ = services.GetRequiredService<OrderLimitsOptions>();
        _ = services.GetRequiredService<RateLimitingOptions>();
        _ = services.GetRequiredService<MySqlDataSource>(); // 連線字串缺少 / 格式錯誤時在此失敗（不會真的連線）
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
