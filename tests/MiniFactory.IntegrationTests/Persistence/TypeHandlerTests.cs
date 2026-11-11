using System.Data;
using Dapper;
using MiniFactory.Domain.Alerts;
using MiniFactory.Domain.Equipments;
using MiniFactory.Domain.Jobs;
using MiniFactory.Domain.Orders;
using MiniFactory.Infrastructure.Persistence;
using MiniFactory.Infrastructure.Persistence.TypeHandlers;
using MiniFactory.IntegrationTests.Support;
using MySqlConnector;

namespace MiniFactory.IntegrationTests.Persistence;

/// <summary>Dapper TypeHandlers（architecture.md §10.7.7）：enum ↔ 字串、DateTime UTC、snake_case 映射。</summary>
[Collection(MariaDbCollection.Name)]
public class TypeHandlerTests(MariaDbFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void EnumHandler_Writes_NameAsString()
    {
        var parameter = new MySqlParameter();
        new EnumStringTypeHandler<JobStatus>().SetValue(parameter, JobStatus.AwaitingConfirmation);

        Assert.Equal("AwaitingConfirmation", parameter.Value);
        Assert.Equal(DbType.String, parameter.DbType);
    }

    [Fact]
    public void EnumHandler_Parse_RejectsUnknownValueAndNonString()
    {
        var handler = new EnumStringTypeHandler<OrderStatus>();

        Assert.Equal(OrderStatus.NeedsReview, handler.Parse("NeedsReview"));
        Assert.Throws<DataException>(() => handler.Parse("needsreview"));   // 區分大小寫，與 CHECK 一致
        Assert.Throws<DataException>(() => handler.Parse("Nope"));
        Assert.Throws<DataException>(() => handler.Parse("3"));             // 數字字串不可被當成 enum 數值
        Assert.Throws<DataException>(() => handler.Parse(3));
    }

    [Fact]
    public void AlertReasonHandler_UsesUpperSnakeCaseCodes_ForEveryReason()
    {
        var handler = new AlertReasonTypeHandler();
        foreach (var reason in Enum.GetValues<AlertReason>())
        {
            var parameter = new MySqlParameter();
            handler.SetValue(parameter, reason);
            Assert.Equal(reason.ToCode(), parameter.Value);
            Assert.Equal(reason, handler.Parse(reason.ToCode()));
        }

        Assert.Throws<DataException>(() => handler.Parse("EquipmentOffline"));
    }

    [Fact]
    public void DateTimeHandler_Local_IsConvertedToUtc_AndUnspecifiedIsTreatedAsUtc()
    {
        var handler = new UtcDateTimeTypeHandler();
        var local = new DateTime(2026, 11, 11, 16, 0, 0, DateTimeKind.Local);

        var p1 = new MySqlParameter();
        handler.SetValue(p1, local);
        Assert.Equal(local.ToUniversalTime(), (DateTime)p1.Value!);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)p1.Value!).Kind);

        var p2 = new MySqlParameter();
        handler.SetValue(p2, new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal(DateTimeKind.Utc, ((DateTime)p2.Value!).Kind);
        Assert.Equal(8, ((DateTime)p2.Value!).Hour);
    }

    private sealed class EquipmentRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public EquipmentStatus Status { get; set; }

        public ProcessOperation Capability { get; set; }

        public DateTime StatusChangedAt { get; set; }

        public DateTime? LastSeenAt { get; set; }

        public uint Version { get; set; }
    }

    [Fact]
    public async Task DbSession_RoundTrip_EnumsAsStrings_UtcMicroseconds_AndSnakeCaseMapping()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = new MySqlDataSourceBuilder(DbConnectionString.Normalize(cs)).Build();
        await using var session = new DbSession(dataSource);
        var id = Guid.CreateVersion7();
        // 含微秒的 Local 時間：驗證寫入轉成 UTC，且 DATETIME(6) 微秒不被截斷
        var local = new DateTime(2026, 11, 11, 16, 30, 15, DateTimeKind.Local).AddTicks(1234560);
        var expectedUtc = local.ToUniversalTime();

        // 參數直接帶 enum：DbSession 負責轉成名稱字串（Dapper 本身會把 enum 轉成整數）
        await session.ExecuteAsync("""
            INSERT INTO equipment (id, line_id, name, capability, status, status_changed_at, last_seen_at, created_at, updated_at)
            VALUES (@id, 'Line-02', 'TH-1', @capability, @status, @t, @seen, @t, @t)
            """,
            new { id, capability = ProcessOperation.Inspection, status = EquipmentStatus.Running, t = local, seen = (DateTime?)null },
            Ct);

        var raw = await Db.QueryAsync<(string Status, string Capability)>(cs, "SELECT status, capability FROM equipment WHERE id = @id", new { id });
        Assert.Equal(("Running", "Inspection"), Assert.Single(raw));

        // 欄位 snake_case ↔ 屬性 PascalCase（不寫別名）；時間讀出標成 UTC 且完整保留微秒
        var row = await session.QuerySingleOrDefaultAsync<EquipmentRow>(
            "SELECT id, name, status, capability, status_changed_at, last_seen_at, version FROM equipment WHERE id = @id",
            new { id }, Ct);
        Assert.NotNull(row);
        Assert.Equal(id, row.Id);
        Assert.Equal(EquipmentStatus.Running, row.Status);
        Assert.Equal(ProcessOperation.Inspection, row.Capability);
        Assert.Equal(DateTimeKind.Utc, row.StatusChangedAt.Kind);
        Assert.Equal(expectedUtc, row.StatusChangedAt);
        Assert.Null(row.LastSeenAt);
        Assert.Equal(0u, row.Version);
    }

    [Fact]
    public async Task DbSession_EnumCollectionForInClause_IsExpandedAsStrings()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = new MySqlDataSourceBuilder(DbConnectionString.Normalize(cs)).Build();
        await using var session = new DbSession(dataSource);

        // 種子的三台設備皆 Offline
        var hit = await session.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM equipment WHERE status IN @statuses",
            new { statuses = new[] { EquipmentStatus.Offline, EquipmentStatus.Error } }, Ct);
        var miss = await session.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM equipment WHERE status IN @statuses",
            new { statuses = new List<EquipmentStatus> { EquipmentStatus.Idle, EquipmentStatus.Running } }, Ct);
        var ids = await session.QueryAsync<Guid>("SELECT id FROM equipment WHERE id IN @ids",
            new { ids = new[] { Guid.Parse("01a1c7fc-2938-7775-9d83-762b1a40f2ed"), Guid.NewGuid() } }, Ct);

        Assert.Equal(3, hit);
        Assert.Equal(0, miss);
        Assert.Single(ids); // Guid 集合維持 Dapper 原生展開
    }

    [Fact]
    public async Task DbSession_AlertEnums_AreWrittenAsDomainCodes_AndReadBackStrictly()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = new MySqlDataSourceBuilder(DbConnectionString.Normalize(cs)).Build();
        await using var session = new DbSession(dataSource);
        var id = Guid.CreateVersion7();

        await session.ExecuteAsync("""
            INSERT INTO alerts (id, severity, reason, status, dedup_key, created_at, updated_at)
            VALUES (@id, @severity, @reason, @status, 'k', @now, @now)
            """,
            new { id, severity = AlertSeverity.Critical, reason = AlertReason.OutboxPublishFailing, status = AlertStatus.Open, now = Db.T0 }, Ct);

        var raw = Assert.Single(await Db.QueryAsync<(string Severity, string Reason, string Status)>(cs, "SELECT severity, reason, status FROM alerts WHERE id = @id", new { id }));
        Assert.Equal(("Critical", "OUTBOX_PUBLISH_FAILING", "Open"), raw);
    }

    [Fact]
    public async Task RawDapper_DateTimeHandler_ConvertsLocalToUtc_WhenTypeMapIsRemoved()
    {
        // 即使不經 DbSession，直接用 Dapper 的 DateTime 參數也會走 handler（Register 已移除 Dapper 內建的 DateTime 對應）
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var conn = await Db.OpenAsync(new MySqlConnectionStringBuilder(cs) { DateTimeKind = MySqlDateTimeKind.Utc }.ConnectionString);
        var local = new DateTime(2026, 11, 11, 16, 0, 0, DateTimeKind.Local);

        var echoed = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT @t", new { t = local }, cancellationToken: Ct));

        Assert.Equal(local.ToUniversalTime(), echoed);
        Assert.Equal(DateTimeKind.Utc, echoed.Kind);
    }
}
