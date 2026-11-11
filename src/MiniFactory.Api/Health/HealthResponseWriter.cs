using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace MiniFactory.Api.Health;

/// <summary>/health/ready 的回應格式（api.md §9）：整體狀態、檢查時間、各元件狀態與耗時。</summary>
public static class HealthResponseWriter
{
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();
        var options = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        var body = new ReadyResponse(
            report.Status.ToString(),
            timeProvider.GetUtcNow(),
            report.Entries.ToDictionary(
                e => e.Key,
                e => new ComponentResponse(e.Value.Status.ToString(), (long)e.Value.Duration.TotalMilliseconds, e.Value.Description),
                StringComparer.Ordinal));

        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, options, context.RequestAborted).ConfigureAwait(false);
    }

    private sealed record ReadyResponse(string Status, DateTimeOffset CheckedAt, IReadOnlyDictionary<string, ComponentResponse> Components);

    private sealed record ComponentResponse(
        string Status,
        long DurationMs,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description);
}
