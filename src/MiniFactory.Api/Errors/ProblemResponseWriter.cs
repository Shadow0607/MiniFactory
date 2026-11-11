using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace MiniFactory.Api.Errors;

/// <summary>
/// 把 Problem Details 寫進回應。優先走 <see cref="IProblemDetailsService"/>（遵守內容協商與 Customize）；
/// 用戶端的 Accept 不接受 JSON 時 <c>TryWriteAsync</c> 會回 false，此時仍直接寫出 <c>application/problem+json</c>
/// （錯誤格式不能因內容協商而消失）。呼叫端須先設定好 <c>Response.StatusCode</c>。
/// </summary>
public static class ProblemResponseWriter
{
    public static async Task WriteAsync(HttpContext context, ProblemDetails problem, CancellationToken cancellationToken = default)
    {
        var written = await context.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
        }).ConfigureAwait(false);

        if (written)
        {
            return;
        }

        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            problem.GetType(),
            context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions,
            cancellationToken).ConfigureAwait(false);
    }
}
