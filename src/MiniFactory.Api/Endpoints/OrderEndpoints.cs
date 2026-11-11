using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MiniFactory.Api.Middleware;
using MiniFactory.Api.RateLimiting;
using MiniFactory.Api.Security;
using MiniFactory.Application.Common;
using MiniFactory.Application.Orders;

namespace MiniFactory.Api.Endpoints;

public static class OrderEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotentReplayedHeader = "Idempotent-Replayed";

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders").WithTags("Orders");

        // 建單：Operator Key
        group.MapPost(string.Empty, CreateAsync)
            .RequireApiKey(ApiKeyKind.Operator)
            .RequireRateLimiting(RateLimitingSetup.DemoWrite)
            .WithName("CreateOrder");

        // 以下 GET 不需 API Key（api.md §1.1）
        group.MapGet(string.Empty, ListAsync).RequireRateLimiting(RateLimitingSetup.PublicRead).WithName("ListOrders");
        group.MapGet("/{id:guid}", GetAsync).RequireRateLimiting(RateLimitingSetup.PublicRead).WithName("GetOrder");
        group.MapGet("/{id:guid}/events", GetEventsAsync).RequireRateLimiting(RateLimitingSetup.PublicRead).WithName("GetOrderEvents");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        CreateOrderRequest? body,
        CreateOrderHandler handler,
        CancellationToken cancellationToken)
    {
        var (traceParent, traceState) = CurrentTraceContext();
        var command = new CreateOrderCommand(
            body,
            ReadIdempotencyKey(http.Request),
            CorrelationIdMiddleware.Get(http),
            traceParent,
            traceState);

        var result = await handler.HandleAsync(command, cancellationToken);

        if (result.Replayed)
        {
            http.Response.Headers[IdempotentReplayedHeader] = "true";
        }

        return Results.Accepted($"/api/orders/{result.Response.OrderId}", result.Response);
    }

    private static async Task<IResult> ListAsync(
        [FromQuery(Name = "status")] string[]? status,
        [FromQuery] string? orderNo,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        OrderQueries queries,
        CancellationToken cancellationToken)
        => Results.Ok(await queries.ListAsync(status, orderNo, page, pageSize, cancellationToken));

    private static async Task<IResult> GetAsync(Guid id, OrderQueries queries, CancellationToken cancellationToken)
        => Results.Ok(await queries.GetAsync(id, cancellationToken));

    private static async Task<IResult> GetEventsAsync(
        Guid id,
        [FromQuery] Guid? after,
        OrderQueries queries,
        CancellationToken cancellationToken)
        => Results.Ok(await queries.GetEventsAsync(id, after, cancellationToken));

    /// <summary>未帶 header 回 null；帶了多個值視為驗證失敗（不猜哪一個才對）。</summary>
    private static string? ReadIdempotencyKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(IdempotencyKeyHeader, out var values))
        {
            return null;
        }

        if (values.Count != 1)
        {
            var errors = new ValidationErrors();
            errors.Add(IdempotencyKeyHeader, "Idempotency-Key 只能提供一個值");
            errors.ThrowIfAny();
        }

        return values[0];
    }

    /// <summary>
    /// API 自己的 W3C traceparent / tracestate，存進訂單與 outbox，之後 Worker 以此串起同一條 Trace（architecture.md §14.5）。
    /// 外部請求帶入的 traceparent / tracestate 不可信，已由 <see cref="UntrustedTraceContextMiddleware"/> 換成新的 root，這裡取到的一定是 API 自己的。
    /// </summary>
    private static (string? TraceParent, string? TraceState) CurrentTraceContext()
    {
        // traceparent 固定 55 字元（欄位 VARCHAR(55)）；tracestate 欄位上限 256，超過就不存
        if (Activity.Current is not { IdFormat: ActivityIdFormat.W3C, Id: { Length: 55 } id } activity)
        {
            return (null, null);
        }

        var state = activity.TraceStateString;
        return (id, state is { Length: <= 256 } ? state : null);
    }
}
