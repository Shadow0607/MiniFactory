using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Migrations;

/// <summary>
/// database.md §7 第 3 項：用 EXPLAIN 確認主要查詢走預期索引、無 filesort。
/// 先灌入有代表性的資料量（絕大多數為已完成 / 已發布，只有少數待處理）並 ANALYZE，
/// 否則空表時優化器的選擇沒有參考價值。
/// </summary>
[Collection(MariaDbCollection.Name)]
public class ExplainTests(MariaDbFixture fixture)
{
    private sealed class Plan
    {
        public string? Table { get; set; }

        public string? Type { get; set; }

        public string? Key { get; set; }

        public string? Extra { get; set; }

        public object? Rows { get; set; }

        public long RowCount => Rows is null ? 0 : Convert.ToInt64(Rows, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<Plan>> ExplainAsync(string cs, string sql, object? param = null)
    {
        var rows = await Db.QueryAsync<Plan>(cs, "EXPLAIN " + sql, param);
        return rows;
    }

    private static void AssertPlan(IReadOnlyList<Plan> plans, string expectedKey)
    {
        var plan = Assert.Single(plans);
        var text = $"table={plan.Table} type={plan.Type} key={plan.Key} rows={plan.Rows} extra={plan.Extra}";
        Assert.True(plan.Key == expectedKey, $"預期使用索引 {expectedKey}，實際：{text}");
        Assert.True(plan.Extra is null || !plan.Extra.Contains("filesort", StringComparison.OrdinalIgnoreCase), $"不應有 filesort：{text}");
    }

    [Fact]
    public async Task Explain_OutboxPendingQuery_UsesPendingIndexWithoutFilesort()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        // 20000 筆，其中 15 筆待發送（published_at 與 dead_lettered_at 皆 NULL）、5 筆 dead letter，其餘已發布
        await Db.ExecAsync(cs, """
            INSERT INTO outbox_messages (id, event_type, payload, aggregate_type, aggregate_id, occurred_at, published_at, dead_lettered_at)
            SELECT UUID(), 'JobDispatchRequested', '{}', 'Job', UUID(),
                   TIMESTAMPADD(SECOND, seq, '2026-11-01 00:00:00'),
                   IF(seq > 20, TIMESTAMPADD(SECOND, seq + 1, '2026-11-01 00:00:00'), NULL),
                   IF(seq BETWEEN 16 AND 20, '2026-11-01 00:00:00', NULL)
            FROM seq_1_to_20000
            """);
        await Db.ExecAsync(cs, "ANALYZE TABLE outbox_messages");

        // database.md 3.8 的 Publisher 發送查詢（不含 FOR UPDATE SKIP LOCKED，EXPLAIN 結果相同）
        var plans = await ExplainAsync(cs, """
            SELECT id, event_type, event_version, payload, correlation_id, trace_parent, trace_state, occurred_at
            FROM outbox_messages
            WHERE published_at IS NULL AND dead_lettered_at IS NULL
            ORDER BY occurred_at, id
            LIMIT 200
            """);
        AssertPlan(plans, "ix_outbox_messages_pending");

        var pending = await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM outbox_messages WHERE published_at IS NULL AND dead_lettered_at IS NULL");
        Assert.Equal(15, pending);
    }

    [Fact]
    public async Task Explain_ReconcilerScan_UsesStatusDeadlineIndex()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        var (orderId, jobId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Db.InsertOrderAsync(cs, orderId);
        await Db.InsertJobAsync(cs, jobId, orderId);

        // 8000 筆 execution，絕大多數 Succeeded；12 筆進行中
        await Db.ExecAsync(cs, """
            INSERT INTO job_executions (id, job_id, attempt_no, command_id, status, deadline_at, created_at, updated_at)
            SELECT UUID(), @jobId, seq, UUID(),
                   CASE WHEN seq <= 3 THEN 'Dispatched' WHEN seq <= 6 THEN 'Accepted' WHEN seq <= 9 THEN 'Running' WHEN seq <= 12 THEN 'Unknown' ELSE 'Succeeded' END,
                   TIMESTAMPADD(SECOND, seq, '2026-11-01 00:00:00'), @now, @now
            FROM seq_1_to_8000
            """, new { jobId, now = Db.T0 });
        await Db.ExecAsync(cs, "ANALYZE TABLE job_executions");

        // Reconciler：進行中的 4 個狀態且 deadline 已到
        var plans = await ExplainAsync(cs, """
            SELECT id FROM job_executions
            WHERE status IN ('Dispatched','Accepted','Running','Unknown') AND deadline_at < @now
            ORDER BY deadline_at
            LIMIT 100
            """, new { now = Db.T0 });
        var plan = Assert.Single(plans);
        Assert.True(plan.Key == "ix_job_executions_status_deadline", $"key={plan.Key} type={plan.Type} rows={plan.Rows} extra={plan.Extra}");
        // 只觸及進行中的列，而不是掃整張表
        Assert.True(plan.RowCount < 100, $"預估掃描列數應遠小於 8000，實際 {plan.Rows}");
    }

    [Fact]
    public async Task Explain_AlertListAndHeartbeatScan_UseTheirIndexes()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        // 6000 筆告警，其中 20 筆 Open（每筆 dedup_key 不同）
        await Db.ExecAsync(cs, """
            INSERT INTO alerts (id, severity, reason, status, dedup_key, created_at, resolved_at, resolved_by, updated_at)
            SELECT UUID(), 'Warning', 'EQUIPMENT_OFFLINE', IF(seq <= 20, 'Open', 'Resolved'), CONCAT('k', seq),
                   TIMESTAMPADD(SECOND, seq, '2026-11-01 00:00:00'),
                   IF(seq <= 20, NULL, '2026-11-02 00:00:00'), IF(seq <= 20, NULL, 'System'), '2026-11-02 00:00:00'
            FROM seq_1_to_6000
            """);
        await Db.ExecAsync(cs, "ANALYZE TABLE alerts");

        // 告警列表（未結案，最新在前）
        var alerts = await ExplainAsync(cs, "SELECT id FROM alerts WHERE status = 'Open' ORDER BY created_at DESC LIMIT 50");
        AssertPlan(alerts, "ix_alerts_status_created_at");

        // 心跳逾時掃描：設備表只有數筆，主要為一致性；至少要能用到 (status, last_seen_at) 或全表掃描
        var equipment = await ExplainAsync(cs, "SELECT id FROM equipment WHERE status <> 'Offline' AND last_seen_at < @t", new { t = Db.T0 });
        Assert.Single(equipment);
    }

    [Fact]
    public async Task Explain_OrderEventsTimeline_UsesOrderIdOccurredAtIndexWithoutFilesort()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        // 1000 張訂單、每張 10 筆事件（共 10000 筆），查詢其中一張的時間線
        await Db.ExecAsync(cs, """
            INSERT INTO production_orders (id, order_no, product_code, quantity, line_id, status, version, correlation_id, created_at, updated_at)
            SELECT UUID(), CONCAT('ORD-', seq), 'Motor-A', 10, 'Line-01', 'Created', 0, 'corr', '2026-11-01 00:00:00', '2026-11-01 00:00:00'
            FROM seq_1_to_1000
            """);
        await Db.ExecAsync(cs, """
            INSERT INTO order_events (id, order_id, event_type, payload, occurred_at)
            SELECT UUID(), o.id, 'OrderCreated', '{}', TIMESTAMPADD(SECOND, e.seq, '2026-11-01 00:00:00')
            FROM production_orders o CROSS JOIN seq_1_to_10 e
            """);
        await Db.ExecAsync(cs, "ANALYZE TABLE order_events");
        var orderId = await Db.ScalarAsync<Guid>(cs, "SELECT id FROM production_orders WHERE order_no = 'ORD-500'");

        var plans = await ExplainAsync(cs, "SELECT id, event_type, payload FROM order_events WHERE order_id = @orderId ORDER BY occurred_at, id", new { orderId });
        AssertPlan(plans, "ix_order_events_order_id_occurred_at");
    }
}
