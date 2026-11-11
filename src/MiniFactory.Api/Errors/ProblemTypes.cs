using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace MiniFactory.Api.Errors;

/// <summary>Problem Details 的 <c>type</c> 清單（api.md §2.1）。</summary>
public sealed record ProblemType(string Slug, int Status, string Title)
{
    public const string BaseUri = "https://minifactory.dev/errors/";

    public string Uri => BaseUri + Slug;
}

public static class ProblemTypes
{
    public static readonly ProblemType ValidationFailed = new("validation-failed", StatusCodes.Status400BadRequest, "One or more validation errors occurred");
    public static readonly ProblemType Unauthorized = new("unauthorized", StatusCodes.Status401Unauthorized, "Missing or invalid API key");
    public static readonly ProblemType NotFound = new("not-found", StatusCodes.Status404NotFound, "Resource not found");
    public static readonly ProblemType IdempotencyConflict = new("idempotency-conflict", StatusCodes.Status409Conflict, "Idempotency key reused with different payload");
    public static readonly ProblemType OrderNoConflict = new("order-no-conflict", StatusCodes.Status409Conflict, "Order number already exists");
    public static readonly ProblemType Conflict = new("conflict", StatusCodes.Status409Conflict, "Conflicting data");
    public static readonly ProblemType ServiceUnavailable = new("service-unavailable", StatusCodes.Status503ServiceUnavailable, "Dependent service unavailable");
    public static readonly ProblemType InternalError = new("internal-error", StatusCodes.Status500InternalServerError, "An unexpected error occurred");

    /// <summary>框架自己產生、沒有指定 type 的錯誤（例如找不到路由的 404）依狀態碼補上對應的 type；沒有對應者回 null。</summary>
    public static ProblemType? ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ValidationFailed,
        StatusCodes.Status401Unauthorized => Unauthorized,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status500InternalServerError => InternalError,
        StatusCodes.Status503ServiceUnavailable => ServiceUnavailable,
        _ => null,
    };

    /// <summary>
    /// 框架產生、沒有專屬業務類型的狀態碼（405、413、415、429 等）統一使用的 type：
    /// <c>https://minifactory.dev/errors/http-{status}</c>（api.md §2.1）。title 用標準狀態說明（例如 "Unsupported Media Type"）。
    /// </summary>
    public static ProblemType ForHttpStatus(int status, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            var phrase = ReasonPhrases.GetReasonPhrase(status);
            title = string.IsNullOrEmpty(phrase) ? "Request error" : phrase;
        }

        return new ProblemType($"http-{status}", status, title);
    }
}

/// <summary>建立符合 api.md §2 的 Problem Details：含 <c>type</c>、<c>instance</c>（請求路徑）與 <c>traceId</c>。</summary>
public static class ProblemFactory
{
    public const string TraceIdKey = "traceId";

    public static ProblemDetails Create(HttpContext context, ProblemType type, string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Type = type.Uri,
            Title = type.Title,
            Status = type.Status,
            Detail = detail,
            Instance = InstanceOf(context),
        };
        problem.Extensions[TraceIdKey] = TraceIdOf(context);
        return problem;
    }

    public static HttpValidationProblemDetails CreateValidation(HttpContext context, IReadOnlyDictionary<string, string[]> errors)
    {
        var problem = new HttpValidationProblemDetails(errors.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal))
        {
            Type = ProblemTypes.ValidationFailed.Uri,
            Title = ProblemTypes.ValidationFailed.Title,
            Status = ProblemTypes.ValidationFailed.Status,
            Instance = InstanceOf(context),
        };
        problem.Extensions[TraceIdKey] = TraceIdOf(context);
        return problem;
    }

    public static string InstanceOf(HttpContext context) => context.Request.PathBase + context.Request.Path;

    /// <summary>W3C traceparent 格式（00-{trace-id}-{span-id}-{flags}）；沒有 Activity 時退回 ASP.NET 的 TraceIdentifier。</summary>
    public static string TraceIdOf(HttpContext context)
        => Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } id } ? id : context.TraceIdentifier;
}
