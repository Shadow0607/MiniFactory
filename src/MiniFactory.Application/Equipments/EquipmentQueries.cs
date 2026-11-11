using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Common;
using MiniFactory.Domain.Equipments;

namespace MiniFactory.Application.Equipments;

/// <summary>GET /api/equipment 的單筆設備（api.md §4.1）。</summary>
public sealed record EquipmentDto(
    Guid Id,
    string Name,
    string LineId,
    string Capability,
    string Status,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? StatusChangedAt,
    Guid? ReservedJobId,
    int Version);

/// <param name="Stale">資料來自 Redis 快照（資料庫不可用時）。尚未接上 Redis，目前恆為 false。</param>
public sealed record EquipmentListResponse(IReadOnlyList<EquipmentDto> Items, bool Stale, DateTimeOffset GeneratedAt);

public sealed class EquipmentQueries(IDashboardQueries queries, TimeProvider timeProvider)
{
    public async Task<EquipmentListResponse> ListAsync(string? lineId, CancellationToken cancellationToken = default)
    {
        var line = string.IsNullOrWhiteSpace(lineId) ? null : lineId.Trim();
        if (line is { Length: > Equipment.MaxLineIdLength })
        {
            var errors = new ValidationErrors();
            errors.Add("lineId", $"lineId 長度不可超過 {Equipment.MaxLineIdLength} 字");
            errors.ThrowIfAny();
        }

        var items = await queries.ListEquipmentAsync(line, cancellationToken).ConfigureAwait(false);

        // 【Phase 2 之後】資料庫不可用且 Redis 有快照時，改回 Stale = true 的快照（architecture.md §14.8）
        return new EquipmentListResponse(items, Stale: false, timeProvider.GetUtcNow());
    }
}
