using System.Text.RegularExpressions;
using MiniFactory.Domain.Alerts;
using MiniFactory.Domain.Equipments;
using MiniFactory.Domain.Jobs;
using MiniFactory.Domain.Orders;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Migrations;

/// <summary>
/// 以 information_schema 逐項比對 database.md 的資料表、索引、約束、生成欄位、欄位型別
/// （database.md §7 第 1、2、6、7 項；architecture.md §18 步驟 1.4 驗收）。
/// 預期清單刻意手寫一份，與遷移腳本是兩個獨立來源：任何一邊漂移都會被抓到。
/// </summary>
[Collection(MariaDbCollection.Name)]
public partial class SchemaTests(MariaDbFixture fixture)
{
    // ---------- database.md 預期結構 ----------

    private static readonly string[] MesTables =
    [
        "production_orders", "production_jobs", "job_executions", "equipment", "equipment_status_history",
        "order_events", "alerts", "outbox_messages", "inbox_messages", "idempotency_keys",
    ];

    private static readonly string[] SimTables = ["simulator_commands"];

    /// <summary>格式：表|索引名|U 或 N|欄位（依序）。PRIMARY 一併列出。</summary>
    private static readonly string[] MesIndexes =
    [
        "production_orders|PRIMARY|U|id",
        "production_orders|ux_production_orders_order_no|U|order_no",
        "production_orders|ix_production_orders_created_at|N|created_at",
        "production_orders|ix_production_orders_status|N|status",

        "production_jobs|PRIMARY|U|id",
        "production_jobs|ux_production_jobs_order_id_sequence|U|order_id,sequence",
        "production_jobs|ux_production_jobs_assigned_equipment_id|U|assigned_equipment_id",
        "production_jobs|ix_production_jobs_status|N|status",

        "job_executions|PRIMARY|U|id",
        "job_executions|ux_job_executions_command_id|U|command_id",
        "job_executions|ux_job_executions_job_id_attempt_no|U|job_id,attempt_no",
        "job_executions|ix_job_executions_status_deadline|N|status,deadline_at",
        // database.md 未列 equipment_id 的索引；外鍵需要索引，MariaDB 以外鍵名稱自動建立
        "job_executions|fk_job_executions_equipment|N|equipment_id",

        "equipment|PRIMARY|U|id",
        "equipment|ux_equipment_name|U|name",
        "equipment|ux_equipment_reserved_job_id|U|reserved_job_id",
        "equipment|ix_equipment_line_id_capability|N|line_id,capability",
        "equipment|ix_equipment_status_last_seen_at|N|status,last_seen_at",

        "equipment_status_history|PRIMARY|U|id",
        "equipment_status_history|ix_equipment_status_history_equipment_id_occurred_at|N|equipment_id,occurred_at",
        "equipment_status_history|ix_equipment_status_history_occurred_at|N|occurred_at",

        "order_events|PRIMARY|U|id",
        "order_events|ix_order_events_order_id_occurred_at|N|order_id,occurred_at,id",
        "order_events|ix_order_events_occurred_at|N|occurred_at",

        "alerts|PRIMARY|U|id",
        "alerts|ux_alerts_open_dedup_key|U|open_dedup_key",
        "alerts|ix_alerts_status_created_at|N|status,created_at",
        "alerts|ix_alerts_order_id|N|order_id",
        "alerts|ix_alerts_equipment_id|N|equipment_id",
        // database.md 未列 job_id 的索引；同上，由外鍵自動建立
        "alerts|fk_alerts_production_jobs|N|job_id",

        "outbox_messages|PRIMARY|U|id",
        "outbox_messages|ix_outbox_messages_pending|N|published_at,dead_lettered_at,occurred_at,id",

        "inbox_messages|PRIMARY|U|consumer_name,message_id",
        "inbox_messages|ix_inbox_messages_processed_at|N|processed_at",

        "idempotency_keys|PRIMARY|U|scope,idempotency_key",
        "idempotency_keys|ix_idempotency_keys_expires_at|N|expires_at",
    ];

    private static readonly string[] SimIndexes =
    [
        "simulator_commands|PRIMARY|U|command_id",
        "simulator_commands|ux_simulator_commands_result_event_id|U|result_event_id",
        "simulator_commands|ix_simulator_commands_equipment_id_status|N|equipment_id,status",
        "simulator_commands|ix_simulator_commands_unreported|N|reported_at,completed_at",
    ];

    /// <summary>格式：外鍵名|子表.欄位|父表.id。一律 RESTRICT。</summary>
    private static readonly string[] ForeignKeys =
    [
        "fk_production_jobs_production_orders|production_jobs.order_id|production_orders.id",
        "fk_job_executions_production_jobs|job_executions.job_id|production_jobs.id",
        "fk_job_executions_equipment|job_executions.equipment_id|equipment.id",
        "fk_equipment_status_history_equipment|equipment_status_history.equipment_id|equipment.id",
        "fk_order_events_production_orders|order_events.order_id|production_orders.id",
        "fk_alerts_production_orders|alerts.order_id|production_orders.id",
        "fk_alerts_production_jobs|alerts.job_id|production_jobs.id",
        "fk_alerts_equipment|alerts.equipment_id|equipment.id",
        // 循環外鍵（建表後另以腳本補上）
        "fk_equipment_production_jobs|equipment.reserved_job_id|production_jobs.id",
        "fk_production_jobs_equipment|production_jobs.assigned_equipment_id|equipment.id",
    ];

    /// <summary>database.md 命名的 CHECK 約束（表.約束名）。</summary>
    private static readonly string[] MesChecks =
    [
        "production_orders.ck_production_orders_quantity", "production_orders.ck_production_orders_status",
        "production_jobs.ck_production_jobs_sequence", "production_jobs.ck_production_jobs_operation", "production_jobs.ck_production_jobs_status",
        "job_executions.ck_job_executions_attempt_no", "job_executions.ck_job_executions_status",
        "equipment.ck_equipment_capability", "equipment.ck_equipment_status",
        "equipment_status_history.ck_equipment_status_history_to_status", "equipment_status_history.ck_equipment_status_history_source",
        "order_events.ck_order_events_payload",
        "alerts.ck_alerts_severity", "alerts.ck_alerts_status", "alerts.ck_alerts_resolved_by",
        "alerts.ck_alerts_resolved_at", "alerts.ck_alerts_resolved_by_set",
        "outbox_messages.ck_outbox_messages_payload",
        "idempotency_keys.ck_idempotency_keys_response_body",
    ];

    private static readonly string[] SimChecks =
    [
        "simulator_commands.ck_simulator_commands_status", "simulator_commands.ck_simulator_commands_request_payload", "simulator_commands.ck_simulator_commands_result",
    ];

    /// <summary>所有應為原生 UUID 型別的欄位。</summary>
    private static readonly string[] MesUuidColumns =
    [
        "production_orders.id",
        "production_jobs.id", "production_jobs.order_id", "production_jobs.assigned_equipment_id",
        "job_executions.id", "job_executions.job_id", "job_executions.command_id", "job_executions.equipment_id", "job_executions.result_event_id",
        "equipment.id", "equipment.reserved_job_id",
        "equipment_status_history.equipment_id",
        "order_events.id", "order_events.order_id",
        "alerts.id", "alerts.order_id", "alerts.job_id", "alerts.equipment_id",
        "outbox_messages.id", "outbox_messages.aggregate_id",
        "inbox_messages.message_id",
        "idempotency_keys.resource_id",
    ];

    private static readonly string[] SimUuidColumns =
    [
        "simulator_commands.command_id", "simulator_commands.equipment_id", "simulator_commands.job_id", "simulator_commands.result_event_id",
    ];

    // ---------- 查詢 helper ----------

    private string Mes => fixture.MigratorConnectionString();

    private string Sim => fixture.SimConnectionString();

    private static async Task<List<string>> TablesAsync(string cs)
        => await Db.QueryAsync<string>(cs, "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> 'schemaversions' ORDER BY TABLE_NAME");

    private static async Task<List<string>> IndexesAsync(string cs)
        => await Db.QueryAsync<string>(cs, """
            SELECT CONCAT(TABLE_NAME, '|', INDEX_NAME, '|', IF(NON_UNIQUE = 0, 'U', 'N'), '|', GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ','))
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> 'schemaversions'
            GROUP BY TABLE_NAME, INDEX_NAME, NON_UNIQUE
            """);

    private static async Task<List<string>> ChecksAsync(string cs)
        => await Db.QueryAsync<string>(cs, "SELECT CONCAT(TABLE_NAME, '.', CONSTRAINT_NAME) FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE()");

    private static async Task<List<string>> UuidColumnsAsync(string cs)
        => await Db.QueryAsync<string>(cs, "SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND DATA_TYPE = 'uuid'");

    // ---------- 測試 ----------

    [Fact]
    public async Task Tables_Exist_AreInnoDbUtf8mb4UnicodeCi()
    {
        Assert.Equal(MesTables.Order(), await TablesAsync(Mes));
        Assert.Equal(SimTables.Order(), await TablesAsync(Sim));

        foreach (var cs in new[] { Mes, Sim })
        {
            var options = await Db.QueryAsync<(string Name, string Engine, string Collation)>(cs, """
                SELECT TABLE_NAME, ENGINE, TABLE_COLLATION FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE'
                """);
            Assert.All(options, o =>
            {
                Assert.Equal("InnoDB", o.Engine);
                Assert.Equal("utf8mb4_unicode_ci", o.Collation);
            });
        }

        // database 本身的預設字元集（initdb 建立）
        var dbOptions = await Db.QueryAsync<(string Name, string Charset, string Collation)>(fixture.RootConnectionString(),
            "SELECT SCHEMA_NAME, DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME IN ('minifactory', 'minifactory_sim')");
        Assert.Equal(2, dbOptions.Count);
        Assert.All(dbOptions, d =>
        {
            Assert.Equal("utf8mb4", d.Charset);
            Assert.Equal("utf8mb4_unicode_ci", d.Collation);
        });
    }

    [Fact]
    public async Task Indexes_MatchDatabaseMd_ExactlyWithPrimaryAndUniqueConstraints()
    {
        Assert.Equal(MesIndexes.Order(), (await IndexesAsync(Mes)).Order());
        Assert.Equal(SimIndexes.Order(), (await IndexesAsync(Sim)).Order());
    }

    [Fact]
    public async Task ForeignKeys_AreAllRestrict_IncludingCircularOnes()
    {
        var actual = await Db.QueryAsync<string>(Mes, """
            SELECT CONCAT(k.CONSTRAINT_NAME, '|', k.TABLE_NAME, '.', k.COLUMN_NAME, '|', k.REFERENCED_TABLE_NAME, '.', k.REFERENCED_COLUMN_NAME)
            FROM information_schema.KEY_COLUMN_USAGE k
            WHERE k.CONSTRAINT_SCHEMA = DATABASE() AND k.REFERENCED_TABLE_NAME IS NOT NULL
            """);
        Assert.Equal(ForeignKeys.Order(), actual.Order());

        var rules = await Db.QueryAsync<(string Name, string Update, string Delete)>(Mes,
            "SELECT CONSTRAINT_NAME, UPDATE_RULE, DELETE_RULE FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE()");
        Assert.Equal(ForeignKeys.Length, rules.Count);
        Assert.All(rules, r =>
        {
            Assert.Equal("RESTRICT", r.Update);
            Assert.Equal("RESTRICT", r.Delete);
        });

        // Simulator 與 MES 之間沒有任何外鍵（不同 database、邏輯對應）
        Assert.Equal(0, await Db.ScalarAsync<int>(Sim, "SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE()"));
    }

    [Fact]
    public async Task CheckConstraints_AllNamedChecksExist()
    {
        var mes = await ChecksAsync(Mes);
        var sim = await ChecksAsync(Sim);

        Assert.Empty(MesChecks.Except(mes));
        Assert.Empty(SimChecks.Except(sim));
    }

    [Fact]
    public async Task CheckConstraints_StatusValueLists_MatchDomainEnums()
    {
        // (表.約束名) → 該 enum 的名稱集合；CHECK 裡的字串清單必須與 enum 完全一致
        var map = new Dictionary<string, string[]>
        {
            ["production_orders.ck_production_orders_status"] = Names<OrderStatus>(),
            ["production_jobs.ck_production_jobs_status"] = Names<JobStatus>(),
            ["production_jobs.ck_production_jobs_operation"] = Names<ProcessOperation>(),
            ["job_executions.ck_job_executions_status"] = Names<ExecutionStatus>(),
            ["equipment.ck_equipment_status"] = Names<EquipmentStatus>(),
            ["equipment.ck_equipment_capability"] = Names<ProcessOperation>(),
            ["equipment_status_history.ck_equipment_status_history_to_status"] = Names<EquipmentStatus>(),
            ["equipment_status_history.ck_equipment_status_history_source"] = Names<StatusChangeSource>(),
            ["alerts.ck_alerts_severity"] = Names<AlertSeverity>(),
            ["alerts.ck_alerts_status"] = Names<AlertStatus>(),
            ["alerts.ck_alerts_resolved_by"] = Names<ResolvedBy>(),
        };

        var clauses = (await Db.QueryAsync<(string Key, string Clause)>(Mes,
                "SELECT CONCAT(TABLE_NAME, '.', CONSTRAINT_NAME), CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE()"))
            .ToDictionary(x => x.Key, x => x.Clause);

        foreach (var (key, expected) in map)
        {
            Assert.True(clauses.TryGetValue(key, out var clause), $"缺少 CHECK 約束 {key}");
            var values = QuotedValue().Matches(clause!).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
            Assert.True(expected.SequenceEqual(values), $"{key} 的字串清單 [{string.Join(",", values)}] 與 enum [{string.Join(",", expected)}] 不一致");
        }

        // Simulator 命令狀態
        var simClause = await Db.ScalarAsync<string>(Sim,
            "SELECT CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'ck_simulator_commands_status'");
        Assert.Equal(
            new[] { "Accepted", "Running", "Succeeded", "Failed", "Rejected" }.Order(StringComparer.Ordinal),
            QuotedValue().Matches(simClause!).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal));
    }

    private static string[] Names<T>() where T : struct, Enum => Enum.GetNames<T>().Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex QuotedValue();

    [Fact]
    public async Task GeneratedColumn_OpenDedupKey_IsStoredAndDerivedFromStatus()
    {
        var column = (await Db.QueryAsync<(string Extra, string Expression, string Nullable, string Type)>(Mes, """
            SELECT EXTRA, GENERATION_EXPRESSION, IS_NULLABLE, COLUMN_TYPE FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'alerts' AND COLUMN_NAME = 'open_dedup_key'
            """)).Single();
        Assert.Contains("STORED", column.Extra, StringComparison.OrdinalIgnoreCase); // PERSISTENT = STORED
        Assert.Contains("Open", column.Expression);
        Assert.Contains("dedup_key", column.Expression);
        Assert.Equal("YES", column.Nullable);
        Assert.Equal("varchar(200)", column.Type);

        // 全庫只有這一個生成欄位
        Assert.Equal(1, await Db.ScalarAsync<int>(Mes,
            "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND (GENERATION_EXPRESSION IS NOT NULL AND GENERATION_EXPRESSION <> '')"));
    }

    [Fact]
    public async Task UuidColumns_AreNativeUuidType_ExactlyTheListedOnes()
    {
        Assert.Equal(MesUuidColumns.Order(), (await UuidColumnsAsync(Mes)).Order());
        Assert.Equal(SimUuidColumns.Order(), (await UuidColumnsAsync(Sim)).Order());
    }

    [Fact]
    public async Task DateTimeColumns_AreDatetime6_WithoutDefaults()
    {
        foreach (var cs in new[] { Mes, Sim })
        {
            var columns = await Db.QueryAsync<(string Column, string Type, string? Default)>(cs, """
                SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME), DATA_TYPE, COLUMN_DEFAULT
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> 'schemaversions' AND COLUMN_NAME LIKE '%\_at'
                """);
            Assert.NotEmpty(columns);
            Assert.All(columns, c =>
            {
                Assert.True(c.Type == "datetime", $"{c.Column} 應為 DATETIME，實際 {c.Type}");
                // 時間值一律由應用程式傳入，DB 不設預設值（database.md §1）。nullable 欄位預設 NULL 以字串 "NULL" 回報
                Assert.True(c.Default is null or "NULL", $"{c.Column} 不應有預設值，實際 {c.Default}");
            });

            var precisions = await Db.QueryAsync<(string Column, ulong? Precision)>(cs, """
                SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME), DATETIME_PRECISION
                FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> 'schemaversions' AND DATA_TYPE = 'datetime'
                """);
            Assert.All(precisions, p => Assert.Equal(6UL, p.Precision));
        }
    }

    [Fact]
    public async Task VersionColumns_ExistOnOptimisticConcurrencyTables_AsUnsignedIntDefaultZero()
    {
        var tables = new[] { "production_orders", "production_jobs", "job_executions", "equipment" };
        var rows = await Db.QueryAsync<(string Table, string Type, string? Default)>(Mes, """
            SELECT TABLE_NAME, COLUMN_TYPE, COLUMN_DEFAULT FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND COLUMN_NAME = 'version'
            """);

        Assert.Equal(tables.Order(), rows.Select(r => r.Table).Order());
        Assert.All(rows, r =>
        {
            Assert.Equal("int(10) unsigned", r.Type);
            Assert.Equal("0", r.Default);
        });
    }
}
