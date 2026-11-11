using Microsoft.AspNetCore.Mvc;
using MiniFactory.Api.RateLimiting;
using MiniFactory.Application.Equipments;

namespace MiniFactory.Api.Endpoints;

public static class EquipmentEndpoints
{
    /// <summary>設備狀態（api.md §4.1）。所有 GET 不需 API Key（區網內開放）。</summary>
    public static IEndpointRouteBuilder MapEquipmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/equipment", async (
                [FromQuery] string? lineId,
                EquipmentQueries queries,
                CancellationToken cancellationToken)
            => Results.Ok(await queries.ListAsync(lineId, cancellationToken)))
            .RequireRateLimiting(RateLimitingSetup.PublicRead)
            .WithTags("Equipment")
            .WithName("ListEquipment");

        return app;
    }
}
