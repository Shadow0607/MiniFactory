using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace MiniFactory.Api;

/// <summary>請求大小上限（api.md §1：本文 64 KB）。</summary>
public static class RequestLimits
{
    /// <summary>設定鍵 <c>Http:MaxRequestBodyBytes</c>（環境變數 <c>Http__MaxRequestBodyBytes</c>）。</summary>
    public const string ConfigKey = "Http:MaxRequestBodyBytes";

    /// <summary>預設 64 KB。建單本文只有幾十個字元，Kestrel 預設的約 28.6 MB 沒有必要。</summary>
    public const long DefaultMaxRequestBodyBytes = 64 * 1024;

    /// <summary>
    /// 套用到 Kestrel。設定值延遲到伺服器建立時才讀；超過上限時 Kestrel 在讀取本文時丟出 413，
    /// 由全域例外處理 / 狀態碼頁轉成 Problem Details。
    /// </summary>
    public static IServiceCollection AddMiniFactoryRequestLimits(this IServiceCollection services)
    {
        services.AddOptions<KestrelServerOptions>().Configure<IConfiguration>((kestrel, configuration) =>
        {
            var configured = configuration.GetValue<long?>(ConfigKey) ?? DefaultMaxRequestBodyBytes;
            if (configured < 1)
            {
                throw new InvalidOperationException($"{ConfigKey} 必須大於 0。");
            }

            kestrel.Limits.MaxRequestBodySize = configured;
        });

        return services;
    }
}
