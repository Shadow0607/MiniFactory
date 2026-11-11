using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using MiniFactory.Api.Errors;

namespace MiniFactory.Api.RateLimiting;

/// <summary>單一限流 policy 的額度：固定視窗內每個來源 IP 最多 <see cref="PermitLimit"/> 個請求。</summary>
public sealed class RateLimitPolicyOptions
{
    public int PermitLimit { get; set; }

    public int WindowSeconds { get; set; }
}

/// <summary>
/// 限流設定（設定區段 <c>RateLimiting</c>，architecture.md §19.2）。
/// <see cref="Enabled"/> 預設 false：不限制（區網 / 本機 Demo）；公開展示前再開啟。
/// 額度為預設值，【待確認：公開展示的實際限流額度】。
/// </summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; }

    /// <summary>寫入端點（建單等）：預設每個 IP 每分鐘 10 次。</summary>
    public RateLimitPolicyOptions DemoWrite { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    /// <summary>查詢端點：預設每個 IP 每分鐘 120 次（Dashboard 輪詢需要較寬鬆）。</summary>
    public RateLimitPolicyOptions PublicRead { get; set; } = new() { PermitLimit = 120, WindowSeconds = 60 };

    public void Validate()
    {
        foreach (var (name, policy) in new[] { (nameof(DemoWrite), DemoWrite), (nameof(PublicRead), PublicRead) })
        {
            if (policy.PermitLimit < 1 || policy.WindowSeconds < 1)
            {
                throw new InvalidOperationException($"{SectionName}:{name} 的 PermitLimit 與 WindowSeconds 必須大於 0。");
            }
        }
    }
}

public static class RateLimitingSetup
{
    /// <summary>寫入端點使用的 policy 名稱。</summary>
    public const string DemoWrite = "demo-write";

    /// <summary>查詢端點使用的 policy 名稱。</summary>
    public const string PublicRead = "public-read";

    public static IServiceCollection AddMiniFactoryRateLimiting(this IServiceCollection services)
    {
        // 與其他設定一樣延遲到第一次使用時才讀（測試覆寫的設定值此時才完整載入）
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IConfiguration>().GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>()
                          ?? new RateLimitingOptions();
            options.Validate();
            return options;
        });

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(DemoWrite, context => Partition(context, DemoWrite, o => o.DemoWrite));
            limiter.AddPolicy(PublicRead, context => Partition(context, PublicRead, o => o.PublicRead));
            limiter.OnRejected = static async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await ProblemResponseWriter.WriteAsync(
                    http,
                    ProblemFactory.Create(http, ProblemTypes.ForHttpStatus(StatusCodes.Status429TooManyRequests), "請求過於頻繁，請稍後再試"),
                    cancellationToken).ConfigureAwait(false);
            };
        });

        return services;
    }

    private static RateLimitPartition<string> Partition(HttpContext context, string policy, Func<RateLimitingOptions, RateLimitPolicyOptions> select)
    {
        var options = context.RequestServices.GetRequiredService<RateLimitingOptions>();
        if (!options.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("disabled");
        }

        // 以來源 IP 分區；各 policy 各自計數
        // 【待確認：部署在反向代理後面時，需先設定 ForwardedHeaders 才能取得真實用戶端 IP】
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var quota = select(options);
        return RateLimitPartition.GetFixedWindowLimiter($"{policy}:{ip}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = quota.PermitLimit,
            Window = TimeSpan.FromSeconds(quota.WindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}
