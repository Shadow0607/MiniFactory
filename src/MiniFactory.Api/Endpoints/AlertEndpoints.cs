using Microsoft.AspNetCore.Mvc;
using MiniFactory.Api.RateLimiting;
using MiniFactory.Application.Alerts;

namespace MiniFactory.Api.Endpoints;

public static class AlertEndpoints
{
    /// <summary>告警列表（api.md §5.1）。POST /api/alerts/{id}/resolve 屬 Phase 4。</summary>
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/alerts", async (
                [FromQuery] string? status,
                [FromQuery] string? severity,
                [FromQuery] Guid? orderId,
                [FromQuery] Guid? equipmentId,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                AlertQueries queries,
                CancellationToken cancellationToken)
            => Results.Ok(await queries.ListAsync(status, severity, orderId, equipmentId, page, pageSize, cancellationToken)))
            .RequireRateLimiting(RateLimitingSetup.PublicRead)
            .WithTags("Alerts")
            .WithName("ListAlerts");

        return app;
    }
}
