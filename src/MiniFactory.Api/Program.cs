using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MiniFactory.Api;
using MiniFactory.Api.Endpoints;
using MiniFactory.Api.Errors;
using MiniFactory.Api.Health;
using MiniFactory.Api.Middleware;
using MiniFactory.Api.RateLimiting;
using MiniFactory.Api.Security;
using MiniFactory.Application;
using MiniFactory.Application.Common;
using MiniFactory.Application.Orders;
using MiniFactory.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// ---- 日誌（architecture.md §14.3）：Serilog → Console，Compact JSON；TraceId / SpanId 由 Serilog 從 Activity 取得 ----
builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(services.GetRequiredService<IConfiguration>())
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "MiniFactory.Api")
    .WriteTo.Console(new CompactJsonFormatter()));

// ---- JSON（api.md §1）：camelCase、enum 字串、時間一律 UTC 並以 Z 結尾 ----
builder.Services.ConfigureHttpJsonOptions(options => JsonDefaults.Apply(options.SerializerOptions));

// ---- 錯誤處理（architecture.md §14.2） ----
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsCustomizer.Customize);
builder.Services.AddExceptionHandler<ExceptionToProblemHandler>();

// 參數繫結失敗（壞掉的 JSON、型別不符的 query）改為丟出例外，交給全域處理器輸出 Problem Details；
// 預設只有 Development 會這樣做，其他環境會回沒有本文的 400
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

// ---- 設定：一律延遲到第一次使用時才讀（測試覆寫的設定值此時才完整載入） ----
// SecurityOptions 的 Validate() 只在 ApiKeyValidator 建構子呼叫（避免兩處重複檢查）
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions());
builder.Services.AddSingleton<ApiKeyValidator>();
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Idempotency").Get<IdempotencyOptions>() ?? new IdempotencyOptions());

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IConfiguration>().GetSection(OrderLimitsOptions.SectionName).Get<OrderLimitsOptions>() ?? new OrderLimitsOptions();
    options.Validate();
    return options;
});

// ---- 限流（預留，預設不啟用）與請求本文大小上限 ----
builder.Services.AddMiniFactoryRateLimiting();
builder.Services.AddMiniFactoryRequestLimits();

// ---- 應用程式與資料存取 ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMiniFactoryApplication();
builder.Services.AddMiniFactoryPersistence(sp =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("MiniFactory") is { Length: > 0 } connectionString
        && !string.IsNullOrWhiteSpace(connectionString)
        ? connectionString
        : throw new InvalidOperationException("缺少連線字串 ConnectionStrings:MiniFactory（環境變數 ConnectionStrings__MiniFactory）。"));

// ---- 健康檢查：目前只有 MariaDB（RabbitMQ / Redis 於 Phase 2 接上連線後加入） ----
builder.Services.AddHealthChecks().AddCheck<MariaDbHealthCheck>("mariadb", failureStatus: HealthStatus.Unhealthy);

// 讓專屬 ActivitySource 一定有 listener（沒有 listener 時 StartActivity 回傳 null）；Phase 2 的 OpenTelemetry 要 AddSource(ApiActivitySource.Name)
builder.Services.AddHostedService<ApiActivityListenerService>();

// 啟動時就檢查必要設定，缺少時程序直接失敗
builder.Services.AddHostedService<StartupConfigurationCheck>();

var app = builder.Build();

// 順序：不信任外部 trace context（最先，之後所有日誌與錯誤回應都用 API 自己的 trace）→ Correlation →
// 請求日誌 → 例外處理 → 狀態碼頁 → 路由 → 限流（需要路由的端點 metadata；在 API Key 之前，才擋得住暴力猜測 Key）→ API Key → 端點
app.UseMiddleware<UntrustedTraceContextMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options =>
{
    // 健康檢查每幾秒就會被呼叫，降到 Verbose 避免洗版
    options.GetLevel = (httpContext, _, exception) =>
        exception is null && httpContext.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
});
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseRateLimiter();
app.UseMiddleware<ApiKeyMiddleware>();

// 存活檢查：只代表程序可回應，不檢查相依服務（Compose healthcheck 使用）
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

// 就緒檢查：任一元件 Unhealthy → 503（預設對應），回應格式見 api.md §9
app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.WriteAsync });

app.MapOrderEndpoints();
app.MapEquipmentEndpoints();
app.MapAlertEndpoints();

app.Run();
