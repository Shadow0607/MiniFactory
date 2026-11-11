using Dapper;
using MySqlConnector;

namespace MiniFactory.IntegrationTests.Support;

/// <summary>測試用的簡短 helper：開連線、執行 SQL。</summary>
public static class Db
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<MySqlConnection> OpenAsync(string connectionString)
    {
        var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        return conn;
    }

    public static async Task<int> ExecAsync(string connectionString, string sql, object? param = null)
    {
        await using var conn = await OpenAsync(connectionString);
        return await conn.ExecuteAsync(new CommandDefinition(sql, param, cancellationToken: Ct));
    }

    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql, object? param = null)
    {
        await using var conn = await OpenAsync(connectionString);
        return await conn.ExecuteScalarAsync<T>(new CommandDefinition(sql, param, cancellationToken: Ct));
    }

    public static async Task<List<T>> QueryAsync<T>(string connectionString, string sql, object? param = null)
    {
        await using var conn = await OpenAsync(connectionString);
        return (await conn.QueryAsync<T>(new CommandDefinition(sql, param, cancellationToken: Ct))).ToList();
    }

    /// <summary>固定的測試時間（避免依賴系統時鐘）。</summary>
    public static readonly DateTime T0 = new(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);

    // ---- 建立最小合法資料列（滿足 NOT NULL 與 CHECK） ----

    public static Task InsertOrderAsync(string cs, Guid id, string? orderNo = null)
        => ExecAsync(cs, """
            INSERT INTO production_orders (id, order_no, product_code, quantity, line_id, status, version, correlation_id, created_at, updated_at)
            VALUES (@id, @orderNo, 'Motor-A', 10, 'Line-01', 'Created', 0, 'corr', @now, @now)
            """, new { id, orderNo = orderNo ?? "ORD-" + id.ToString("N")[..20], now = T0 });

    public static Task InsertJobAsync(string cs, Guid id, Guid orderId, int sequence = 1, Guid? assignedEquipmentId = null)
        => ExecAsync(cs, """
            INSERT INTO production_jobs (id, order_id, sequence, operation, status, assigned_equipment_id, created_at, updated_at)
            VALUES (@id, @orderId, @sequence, 'Machining', 'Pending', @assignedEquipmentId, @now, @now)
            """, new { id, orderId, sequence, assignedEquipmentId, now = T0 });

    public static Task InsertEquipmentAsync(string cs, Guid id, string name, Guid? reservedJobId = null)
        => ExecAsync(cs, """
            INSERT INTO equipment (id, line_id, name, capability, status, status_changed_at, reserved_job_id, created_at, updated_at)
            VALUES (@id, 'Line-02', @name, 'Machining', 'Idle', @now, @reservedJobId, @now, @now)
            """, new { id, name, reservedJobId, now = T0 });

    public static Task InsertAlertAsync(string cs, Guid id, string dedupKey, string status = "Open")
        => ExecAsync(cs, """
            INSERT INTO alerts (id, severity, reason, status, dedup_key, created_at, resolved_at, resolved_by, updated_at)
            VALUES (@id, 'Warning', 'EQUIPMENT_OFFLINE', @status, @dedupKey, @now,
                    IF(@status = 'Resolved', @now, NULL), IF(@status = 'Resolved', 'System', NULL), @now)
            """, new { id, dedupKey, status, now = T0 });
}
