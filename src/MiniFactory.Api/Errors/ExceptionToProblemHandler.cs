using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MiniFactory.Application.Errors;
using MiniFactory.Application.Persistence;

namespace MiniFactory.Api.Errors;

/// <summary>
/// 全域例外處理（architecture.md §14.2、§10.7.6）：把例外轉成 RFC 9457 Problem Details。
/// 業務例外對應 4xx；資料庫連線 / 死結重試用盡 → 503 + Retry-After；其餘一律 500，
/// 回應不含例外訊息與堆疊（完整例外只寫進日誌）。
/// </summary>
public sealed class ExceptionToProblemHandler(ILogger<ExceptionToProblemHandler> logger) : IExceptionHandler
{
    /// <summary>DB 暫時不可用時建議的重試等待秒數（api.md §2.1）。</summary>
    public const int RetryAfterSeconds = 5;

    // 用戶端中斷連線（nginx 慣例的 499）；沒人讀得到回應，也不是伺服器錯誤
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        var problem = Map(httpContext, exception);
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        await ProblemResponseWriter.WriteAsync(httpContext, problem, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private ProblemDetails Map(HttpContext context, Exception exception)
    {
        switch (exception)
        {
            case ValidationFailedException validation:
                return ProblemFactory.CreateValidation(context, validation.Errors);

            case NotFoundException notFound:
                return ProblemFactory.Create(context, ProblemTypes.NotFound, notFound.Message);

            case IdempotencyConflictException conflict:
                return ProblemFactory.Create(context, ProblemTypes.IdempotencyConflict, conflict.Message);

            case OrderNoConflictException orderNo:
            {
                var problem = ProblemFactory.Create(context, ProblemTypes.OrderNoConflict, orderNo.Message);
                problem.Extensions["orderNo"] = orderNo.OrderNo;
                return problem;
            }

            case UniqueConstraintViolationException unique:
                // 未被特定處理的唯一約束：正常流程不應出現，出現代表需補特定處理（api.md §2.1）
                logger.LogWarning(unique, "未處理的唯一約束衝突（1062）：{ServerMessage}", unique.ServerMessage);
                return ProblemFactory.Create(context, ProblemTypes.Conflict, "資料與既有資料衝突");

            case DatabaseUnavailableException or TransientDbException:
            {
                logger.LogWarning(exception, "資料庫暫時無法使用，回應 503");
                context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return ProblemFactory.Create(context, ProblemTypes.ServiceUnavailable, "資料庫暫時無法使用，請稍後再試");
            }

            case BadHttpRequestException bad:
            {
                // 框架 / Kestrel 的原始訊息可能含內部型別名稱、欄位路徑或實作細節，只寫日誌；回應一律用固定中文說明
                logger.LogWarning(bad, "請求格式錯誤（HTTP {StatusCode}）：{Method} {Path}", bad.StatusCode, context.Request.Method, context.Request.Path);

                if (bad.StatusCode == StatusCodes.Status400BadRequest)
                {
                    return ProblemFactory.CreateValidation(context, new Dictionary<string, string[]>
                    {
                        [bad.InnerException is JsonException ? "body" : "request"] =
                            [bad.InnerException is JsonException ? "請求內容不是合法的 JSON，或欄位型別不符" : "請求格式不正確"],
                    });
                }

                // 413（本文過大）等：沒有專屬業務類型，使用通用的 http-{status}
                return ProblemFactory.Create(
                    context,
                    ProblemTypes.ForHttpStatus(bad.StatusCode),
                    bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                        ? "請求內容超過允許的大小上限"
                        : "請求無法處理");
            }

            default:
                logger.LogError(exception, "未預期的例外：{Method} {Path}", context.Request.Method, context.Request.Path);
                return ProblemFactory.Create(context, ProblemTypes.InternalError, "伺服器發生未預期的錯誤，請提供 traceId 聯絡管理員");
        }
    }
}
