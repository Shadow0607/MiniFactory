using MiniFactory.Application.Abstractions;

namespace MiniFactory.Infrastructure.Persistence.Data;

public sealed class EquipmentData(DbSession session) : IEquipmentData
{
    public async Task<string?> FindLineIdAsync(string lineId, CancellationToken cancellationToken = default)
        => await session.QuerySingleOrDefaultAsync<string>(
            // line_id 的 collation 不分大小寫；DISTINCT + LIMIT 讓同一產線多台設備也只回一筆
            "SELECT DISTINCT line_id FROM equipment WHERE line_id = @lineId ORDER BY line_id LIMIT 1",
            new { lineId },
            cancellationToken).ConfigureAwait(false);
}
