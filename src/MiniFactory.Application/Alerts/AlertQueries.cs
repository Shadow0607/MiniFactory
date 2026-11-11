using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Common;
using MiniFactory.Domain.Alerts;

namespace MiniFactory.Application.Alerts;

public sealed record AlertDto(
    Guid Id,
    string Severity,
    string Reason,
    string? Message,
    string Status,
    Guid? RelatedOrderId,
    Guid? RelatedJobId,
    Guid? RelatedEquipmentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy,
    string? ResolutionNote);

public sealed record AlertListFilter(AlertStatus? Status, AlertSeverity? Severity, Guid? OrderId, Guid? EquipmentId);

/// <param name="OpenCount">導覽列未處理告警數，不受分頁 / 篩選影響（api.md §5.1）。</param>
public sealed record AlertListResponse(IReadOnlyList<AlertDto> Items, int Page, int PageSize, long TotalCount, long OpenCount);

public sealed class AlertQueries(IDashboardQueries queries)
{
    public async Task<AlertListResponse> ListAsync(
        string? status,
        string? severity,
        Guid? orderId,
        Guid? equipmentId,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var parsedStatus = errors.ParseEnum<AlertStatus>("status", status);
        var parsedSeverity = errors.ParseEnum<AlertSeverity>("severity", severity);
        var (p, s) = Paging.Normalize(page, pageSize, errors);
        errors.ThrowIfAny();

        return await queries.ListAlertsAsync(
            new AlertListFilter(parsedStatus, parsedSeverity, orderId, equipmentId), p, s, cancellationToken).ConfigureAwait(false);
    }
}
