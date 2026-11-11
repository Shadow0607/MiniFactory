using System.Diagnostics;

namespace MiniFactory.Api.Middleware;

/// <summary>
/// Correlation ID（architecture.md §14.4）：讀 <c>X-Correlation-Id</c>，沒帶或格式不合就產生新的 UUID；
/// 回應一律回寫該 header，並放進日誌範圍（<c>CorrelationId</c>）與目前 Activity 的 tag。
/// 外來的值只接受 1～64 個安全字元，避免日誌注入與超出 <c>correlation_id</c> 欄位長度。
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const int MaxLength = 64;

    private const string ItemKey = "MiniFactory.CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Resolve(context.Request.Headers[HeaderName]);
        context.Items[ItemKey] = correlationId;

        // 例外處理器會清除回應 header，所以要在回應開始寫出時才設定
        context.Response.OnStarting(static state =>
        {
            var (ctx, id) = ((HttpContext, string))state;
            ctx.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        }, (context, correlationId));

        Activity.Current?.SetTag("correlation_id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    /// <summary>取得本次請求的 Correlation ID（需經過本 middleware）。</summary>
    public static string Get(HttpContext context)
        => context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;

    internal static string Resolve(Microsoft.Extensions.Primitives.StringValues header)
    {
        if (header.Count == 1 && header[0] is { } candidate && IsValid(candidate))
        {
            return candidate;
        }

        return Guid.NewGuid().ToString();
    }

    internal static bool IsValid(string value)
        => value.Length is >= 1 and <= MaxLength
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
