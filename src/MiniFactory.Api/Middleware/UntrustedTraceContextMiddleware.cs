using System.Diagnostics;

namespace MiniFactory.Api.Middleware;

/// <summary>
/// API 對外入口不信任請求帶入的 <c>traceparent</c> / <c>tracestate</c>（api.md §1）：
/// ASP.NET Core 的 Hosting 會把這兩個 header 當成父層建立請求的 Activity，任何人都能偽造 trace id，
/// 進而污染存進資料庫的 trace_parent（之後 Worker 以它串起整條 Trace）、日誌與 Jaeger 的查詢結果。
/// 偵測到請求 Activity 的父層是外部（<see cref="Activity.HasRemoteParent"/>，或有 ParentId 但沒有行程內的 Parent）時，改由專屬的
/// <see cref="ApiActivitySource"/> 開一個全新的 root Activity（新的 trace id、不帶 tracestate），
/// 之後所有程式碼（Serilog、存入 DB 的 trace context、錯誤回應的 traceId）看到的都是 API 自己產生的 trace。
/// 沒有外部父層的請求不處理。
/// <para>
/// 已知限制【待確認：Phase 2 接上 OpenTelemetry 時一併處理】：
/// (1) Hosting 在本中介層之前就已寫出的 "Request starting" 日誌，其 scope 仍帶外部的 trace id；
/// (2) Hosting 自己的 Activity（<c>IHttpActivityFeature.Activity</c>）仍以外部 trace 為父層，
/// OpenTelemetry AspNetCore instrumentation 的伺服器端 span 會是它而不是這裡的 root。
/// </para>
/// </summary>
public sealed class UntrustedTraceContextMiddleware(RequestDelegate next)
{
    private const string ActivityName = "MiniFactory.Api.Request";

    /// <summary>
    /// 外部父層判斷。實測 Hosting 以 traceparent 標頭建立的 Activity，<see cref="Activity.HasRemoteParent"/> 不一定為 true
    /// （由標頭解析出的 ActivityContext 未標 IsRemote），所以補上「有 ParentId 但沒有行程內 Parent 物件」的條件：
    /// 這代表父層來自行程外（標頭），而不是同行程的上層 Activity。
    /// </summary>
    private static bool HasExternalParent(Activity activity)
        => activity.HasRemoteParent || (activity.Parent is null && activity.ParentId is not null);

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = Activity.Current;
        if (incoming is null || !HasExternalParent(incoming))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // 先清除 Current：StartActivity 收到 default(ActivityContext) 時視為「未指定父層」，
        // 會退回使用 Activity.Current 當父層（實測外部 trace id 因此被沿用）；清掉之後才會成為全新的 root。
        Activity.Current = null;

        // StartActivity 在「沒有任何 listener 監聽此 source」時會回傳 null，正常情況下 Program 已註冊
        // ApiActivityListenerService 所以不會發生；萬一為 null（例如以不含該服務的方式組裝主機），
        // 退回 new Activity：同樣會成為 root，只是不經過 ActivitySource。
        var root = ApiActivitySource.Source.StartActivity(ActivityName, ActivityKind.Server, default(ActivityContext));
        if (root is null)
        {
            root = new Activity(ActivityName);
            root.Start();
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            root.Stop();
            Activity.Current = incoming;
        }
    }
}
