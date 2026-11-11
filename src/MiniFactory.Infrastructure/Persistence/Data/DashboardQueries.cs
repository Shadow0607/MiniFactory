using System.Text.Json;
using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Alerts;
using MiniFactory.Application.Common;
using MiniFactory.Application.Equipments;
using MiniFactory.Application.Errors;
using MiniFactory.Application.Orders;
using MiniFactory.Domain.Orders;

namespace MiniFactory.Infrastructure.Persistence.Data;

/// <summary>
/// Dashboard 唯讀查詢（Dapper，經 <see cref="DbSession"/>）。所有 SQL 皆為參數化常數字串；
/// 「選填篩選」用 <c>(@p IS NULL OR col = @p)</c> 表示，不做字串拼接。
/// 查詢列用私有 row 類別接收（snake_case 欄位自動對應 PascalCase 屬性），再轉成 Application 的 DTO。
/// </summary>
public sealed class DashboardQueries(DbSession session) : IDashboardQueries
{
    // 訂單列表的 SQL 全是編譯期常數：有狀態篩選與無篩選各一組。
    // 無篩選時不帶 IN 子句（不必為了「全部」而塞入 5 個狀態值，也讓查詢計畫單純）。
    internal const string OrderCountAll =
        """
        SELECT COUNT(*) FROM production_orders o
        WHERE (@like IS NULL OR o.order_no LIKE @like)
        """;

    internal const string OrderCountByStatus =
        """
        SELECT COUNT(*) FROM production_orders o
        WHERE o.status IN @statuses AND (@like IS NULL OR o.order_no LIKE @like)
        """;

    private const string OrderSelectHead =
        """
        SELECT o.id, o.order_no, o.product_code, o.quantity, o.line_id, o.status, o.version, o.created_at, o.updated_at,
               (SELECT COUNT(*) FROM production_jobs j WHERE j.order_id = o.id AND j.status = 'Completed') AS completed_jobs,
               (SELECT COUNT(*) FROM production_jobs j WHERE j.order_id = o.id) AS total_jobs
        FROM production_orders o

        """;

    private const string OrderSelectTail =
        """

        ORDER BY o.created_at DESC, o.id DESC
        LIMIT @limit OFFSET @offset
        """;

    internal const string OrderSelectAll = OrderSelectHead + "WHERE (@like IS NULL OR o.order_no LIKE @like)" + OrderSelectTail;

    internal const string OrderSelectByStatus = OrderSelectHead + "WHERE o.status IN @statuses AND (@like IS NULL OR o.order_no LIKE @like)" + OrderSelectTail;

    // ------------------------------------------------------------------ 訂單

    public async Task<PagedResult<OrderSummaryDto>> ListOrdersAsync(
        OrderListFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var like = filter.OrderNoPrefix is null ? null : EscapeLike(filter.OrderNoPrefix) + "%";
        var limit = pageSize;
        var offset = Paging.Offset(page, pageSize);

        string countSql, selectSql;
        object parameters;
        if (filter.Statuses.Count == 0)
        {
            (countSql, selectSql) = (OrderCountAll, OrderSelectAll);
            parameters = new { like, limit, offset };
        }
        else
        {
            (countSql, selectSql) = (OrderCountByStatus, OrderSelectByStatus);
            parameters = new { statuses = filter.Statuses.Select(s => s.ToString()).ToArray(), like, limit, offset };
        }

        var total = await session.ExecuteScalarAsync<long>(countSql, parameters, cancellationToken).ConfigureAwait(false);
        var rows = await session.QueryAsync<OrderSummaryRow>(selectSql, parameters, cancellationToken).ConfigureAwait(false);

        var items = rows.Select(r => new OrderSummaryDto(
            r.Id, r.OrderNo, r.ProductCode, r.Quantity, r.LineId, r.Status,
            r.CompletedJobs, r.TotalJobs, r.Version, Utc(r.CreatedAt), Utc(r.UpdatedAt))).ToArray();
        return new PagedResult<OrderSummaryDto>(items, page, pageSize, total);
    }

    public async Task<bool> OrderExistsAsync(Guid orderId, CancellationToken cancellationToken = default)
        => await session.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM production_orders WHERE id = @orderId", new { orderId }, cancellationToken).ConfigureAwait(false) > 0;

    public async Task<OrderDetailDto?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        // 訂單詳情由三個查詢（訂單 / Job / execution）拼成。預設 autocommit 下每個查詢各看各的「當下」，
        // 查詢之間若有狀態更新提交，會拼出不一致的畫面（例如 Job 已 Completed 但訂單仍 InProgress）。
        // 以唯讀一致性快照交易包住，三個查詢讀同一個時間點；範圍結束即結束交易。
        await using var snapshot = await session.BeginReadOnlySnapshotAsync(cancellationToken).ConfigureAwait(false);

        var order = await session.QuerySingleOrDefaultAsync<OrderRow>(
            """
            SELECT id, order_no, product_code, quantity, line_id, status, version, trace_parent, created_at, updated_at, completed_at
            FROM production_orders WHERE id = @orderId
            """,
            new { orderId },
            cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return null;
        }

        var jobRows = (await session.QueryAsync<JobRow>(
            """
            SELECT j.id, j.`sequence`, j.operation, j.status, j.assigned_equipment_id, e.name AS assigned_equipment_name,
                   j.current_attempt_no, j.started_at, j.completed_at
            FROM production_jobs j
            LEFT JOIN equipment e ON e.id = j.assigned_equipment_id
            WHERE j.order_id = @orderId
            ORDER BY j.`sequence`
            """,
            new { orderId },
            cancellationToken).ConfigureAwait(false)).ToList();

        var executionRows = (await session.QueryAsync<ExecutionRow>(
            """
            SELECT x.job_id, x.attempt_no, x.command_id, x.status, x.equipment_id, e.name AS equipment_name,
                   x.send_count, x.last_sent_at, x.started_at, x.completed_at, x.error_code
            FROM job_executions x
            JOIN production_jobs j ON j.id = x.job_id
            LEFT JOIN equipment e ON e.id = x.equipment_id
            WHERE j.order_id = @orderId
            ORDER BY x.attempt_no
            """,
            new { orderId },
            cancellationToken).ConfigureAwait(false)).ToList();

        var executionsByJob = executionRows
            .GroupBy(x => x.JobId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ExecutionDto>)g.OrderBy(x => x.AttemptNo).Select(ToDto).ToArray());

        var jobs = jobRows.Select(j => new JobDto(
            j.Id,
            j.Sequence,
            j.Operation,
            j.Status,
            j.AssignedEquipmentId is { } assignedId ? new EquipmentRefDto(assignedId, j.AssignedEquipmentName ?? string.Empty) : null,
            j.CurrentAttemptNo,
            UtcOrNull(j.StartedAt),
            UtcOrNull(j.CompletedAt),
            executionsByJob.TryGetValue(j.Id, out var list) ? list : [])).ToArray();

        return new OrderDetailDto(
            order.Id, order.OrderNo, order.ProductCode, order.Quantity, order.LineId, order.Status, order.Version,
            ExtractTraceId(order.TraceParent), Utc(order.CreatedAt), Utc(order.UpdatedAt), UtcOrNull(order.CompletedAt), jobs);
    }

    public async Task<IReadOnlyList<OrderEventDto>> ListOrderEventsAsync(
        Guid orderId, Guid? afterEventId, CancellationToken cancellationToken = default)
    {
        var hasAfter = false;
        var afterAt = DateTime.UnixEpoch;
        var afterId = Guid.Empty;

        if (afterEventId is { } cursor)
        {
            // 以 (occurred_at, id) 排序，所以游標要先換成該事件的時間
            var cursorTime = await session.ExecuteScalarAsync<DateTime?>(
                "SELECT occurred_at FROM order_events WHERE id = @cursor AND order_id = @orderId",
                new { cursor, orderId },
                cancellationToken).ConfigureAwait(false);
            if (cursorTime is null)
            {
                throw new NotFoundException("事件", cursor.ToString());
            }

            hasAfter = true;
            afterAt = cursorTime.Value;
            afterId = cursor;
        }

        var rows = await session.QueryAsync<EventRow>(
            """
            SELECT id, event_type, event_version, payload, occurred_at
            FROM order_events
            WHERE order_id = @orderId
              AND (@hasAfter = 0 OR occurred_at > @afterAt OR (occurred_at = @afterAt AND id > @afterId))
            ORDER BY occurred_at, id
            """,
            new { orderId, hasAfter = hasAfter ? 1 : 0, afterAt, afterId },
            cancellationToken).ConfigureAwait(false);

        return rows.Select(r =>
        {
            using var document = JsonDocument.Parse(r.Payload);
            return new OrderEventDto(r.Id, r.EventType, r.EventVersion, Utc(r.OccurredAt), document.RootElement.Clone());
        }).ToArray();
    }

    // ------------------------------------------------------------------ 設備

    public async Task<IReadOnlyList<EquipmentDto>> ListEquipmentAsync(string? lineId, CancellationToken cancellationToken = default)
    {
        var rows = await session.QueryAsync<EquipmentRow>(
            """
            SELECT id, name, line_id, capability, status, last_seen_at, status_changed_at, reserved_job_id, version
            FROM equipment
            WHERE (@lineId IS NULL OR line_id = @lineId)
            ORDER BY line_id, name
            """,
            new { lineId },
            cancellationToken).ConfigureAwait(false);

        return rows.Select(r => new EquipmentDto(
            r.Id, r.Name, r.LineId, r.Capability, r.Status,
            UtcOrNull(r.LastSeenAt), UtcOrNull(r.StatusChangedAt), r.ReservedJobId, r.Version)).ToArray();
    }

    // ------------------------------------------------------------------ 告警

    public async Task<AlertListResponse> ListAlertsAsync(
        AlertListFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var parameters = new
        {
            status = filter.Status,
            severity = filter.Severity,
            orderId = filter.OrderId,
            equipmentId = filter.EquipmentId,
            limit = pageSize,
            offset = Paging.Offset(page, pageSize),
        };

        var total = await session.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM alerts
            WHERE (@status IS NULL OR status = @status)
              AND (@severity IS NULL OR severity = @severity)
              AND (@orderId IS NULL OR order_id = @orderId)
              AND (@equipmentId IS NULL OR equipment_id = @equipmentId)
            """,
            parameters,
            cancellationToken).ConfigureAwait(false);

        var rows = await session.QueryAsync<AlertRow>(
            """
            SELECT id, severity, reason, message, status, order_id, job_id, equipment_id,
                   created_at, resolved_at, resolved_by, resolution_note
            FROM alerts
            WHERE (@status IS NULL OR status = @status)
              AND (@severity IS NULL OR severity = @severity)
              AND (@orderId IS NULL OR order_id = @orderId)
              AND (@equipmentId IS NULL OR equipment_id = @equipmentId)
            ORDER BY created_at DESC, id DESC
            LIMIT @limit OFFSET @offset
            """,
            parameters,
            cancellationToken).ConfigureAwait(false);

        // 導覽列徽章：不受篩選與分頁影響
        var openCount = await session.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM alerts WHERE status = 'Open'", null, cancellationToken).ConfigureAwait(false);

        var items = rows.Select(r => new AlertDto(
            r.Id, r.Severity, r.Reason, r.Message, r.Status, r.OrderId, r.JobId, r.EquipmentId,
            Utc(r.CreatedAt), UtcOrNull(r.ResolvedAt), r.ResolvedBy, r.ResolutionNote)).ToArray();
        return new AlertListResponse(items, page, pageSize, total, openCount);
    }

    // ------------------------------------------------------------------ 輔助

    private static ExecutionDto ToDto(ExecutionRow x) => new(
        x.AttemptNo,
        x.CommandId,
        x.Status,
        x.EquipmentId is { } id ? new EquipmentRefDto(id, x.EquipmentName ?? string.Empty) : null,
        x.SendCount,
        UtcOrNull(x.LastSentAt),
        UtcOrNull(x.StartedAt),
        UtcOrNull(x.CompletedAt),
        x.ErrorCode);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? UtcOrNull(DateTime? value) => value is { } v ? Utc(v) : null;

    /// <summary>W3C traceparent（<c>00-{32 hex trace-id}-{16 hex span-id}-{flags}</c>）取 trace-id；格式不符回 null。</summary>
    internal static string? ExtractTraceId(string? traceParent)
    {
        // 00-{32 hex}-{16 hex}-{2 hex} 共 55 字元
        if (traceParent is not { Length: 55 } || traceParent[2] != '-' || traceParent[35] != '-')
        {
            return null;
        }

        var traceId = traceParent.AsSpan(3, 32);
        foreach (var c in traceId)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return traceId.ToString().ToLowerInvariant();
    }

    /// <summary>跳脫 LIKE 萬用字元（MariaDB 預設跳脫字元為反斜線）。訂單編號允許 <c>_</c>，不跳脫會被當成任意單一字元。</summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);

    // ---- row 類別（欄位 snake_case ↔ 屬性 PascalCase 由 Dapper 的 MatchNamesWithUnderscores 處理） ----

    private sealed class OrderSummaryRow
    {
        public Guid Id { get; set; }

        public string OrderNo { get; set; } = string.Empty;

        public string ProductCode { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public string LineId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public int Version { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        public int CompletedJobs { get; set; }

        public int TotalJobs { get; set; }
    }

    private sealed class OrderRow
    {
        public Guid Id { get; set; }

        public string OrderNo { get; set; } = string.Empty;

        public string ProductCode { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public string LineId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public int Version { get; set; }

        public string? TraceParent { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        public DateTime? CompletedAt { get; set; }
    }

    private sealed class JobRow
    {
        public Guid Id { get; set; }

        public int Sequence { get; set; }

        public string Operation { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public Guid? AssignedEquipmentId { get; set; }

        public string? AssignedEquipmentName { get; set; }

        public int CurrentAttemptNo { get; set; }

        public DateTime? StartedAt { get; set; }

        public DateTime? CompletedAt { get; set; }
    }

    private sealed class ExecutionRow
    {
        public Guid JobId { get; set; }

        public int AttemptNo { get; set; }

        public Guid CommandId { get; set; }

        public string Status { get; set; } = string.Empty;

        public Guid? EquipmentId { get; set; }

        public string? EquipmentName { get; set; }

        public int SendCount { get; set; }

        public DateTime? LastSentAt { get; set; }

        public DateTime? StartedAt { get; set; }

        public DateTime? CompletedAt { get; set; }

        public string? ErrorCode { get; set; }
    }

    private sealed class EventRow
    {
        public Guid Id { get; set; }

        public string EventType { get; set; } = string.Empty;

        public int EventVersion { get; set; }

        public string Payload { get; set; } = "{}";

        public DateTime OccurredAt { get; set; }
    }

    private sealed class EquipmentRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string LineId { get; set; } = string.Empty;

        public string Capability { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public DateTime? LastSeenAt { get; set; }

        public DateTime? StatusChangedAt { get; set; }

        public Guid? ReservedJobId { get; set; }

        public int Version { get; set; }
    }

    private sealed class AlertRow
    {
        public Guid Id { get; set; }

        public string Severity { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;

        public string? Message { get; set; }

        public string Status { get; set; } = string.Empty;

        public Guid? OrderId { get; set; }

        public Guid? JobId { get; set; }

        public Guid? EquipmentId { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? ResolvedAt { get; set; }

        public string? ResolvedBy { get; set; }

        public string? ResolutionNote { get; set; }
    }
}
