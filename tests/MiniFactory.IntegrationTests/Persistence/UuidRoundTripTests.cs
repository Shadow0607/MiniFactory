using Dapper;
using MiniFactory.IntegrationTests.Support;
using MySqlConnector;

namespace MiniFactory.IntegrationTests.Persistence;

/// <summary>
/// Phase 1.4 的第一項驗證（architecture.md §2.5、database.md §1「UUID 退路」）：
/// MariaDB 原生 UUID 欄位 ↔ .NET Guid，經 MySqlConnector（預設 GuidFormat）與 Dapper（無自訂 Guid handler）讀寫。
/// 任何一項失敗，就要依文件改用 CHAR(36) ascii_bin + GuidFormat=Char36。
/// </summary>
[Collection(MariaDbCollection.Name)]
public class UuidRoundTripTests(MariaDbFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Row
    {
        public Guid Id { get; set; }

        public string Note { get; set; } = "";

        public Guid? OtherId { get; set; }
    }

    private async Task<string> CreateProbeTableAsync()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync("mf_uuid_");
        await Db.ExecAsync(cs, "CREATE TABLE probe (id UUID NOT NULL, note VARCHAR(20) NOT NULL, other_id UUID NULL, PRIMARY KEY (id)) ENGINE=InnoDB");
        return cs;
    }

    [Fact]
    public async Task Persistence_UuidRoundTrip_GuidEqualsAfterReadBack()
    {
        var cs = await CreateProbeTableAsync();
        var id = Guid.CreateVersion7();

        // 欄位真的是原生 UUID 型別，而不是被悄悄當成 CHAR
        var dataType = await Db.ScalarAsync<string>(cs,
            "SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'probe' AND COLUMN_NAME = 'id'");
        Assert.Equal("uuid", dataType);

        // 1) 以 Guid 參數寫入（Dapper）
        await using var conn = await Db.OpenAsync(cs);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO probe (id, note, other_id) VALUES (@id, 'dapper', NULL)", new { id }, cancellationToken: Ct));

        // 2) 以 Guid 參數查詢 WHERE，並以 Dapper 映射到 Guid 屬性（snake_case 欄位名，含 Guid? NULL）
        var row = await conn.QuerySingleAsync<Row>(new CommandDefinition(
            "SELECT id AS Id, note AS Note, other_id AS OtherId FROM probe WHERE id = @id", new { id }, cancellationToken: Ct));
        Assert.Equal(id, row.Id);
        Assert.Null(row.OtherId);

        // 3) 原始 ADO.NET：參數寫入、GetGuid 讀回、欄位型別名稱
        var raw = Guid.CreateVersion7();
        await using (var insert = new MySqlCommand("INSERT INTO probe (id, note) VALUES (@id, 'raw')", conn))
        {
            insert.Parameters.AddWithValue("@id", raw);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(Ct));
        }

        await using (var select = new MySqlCommand("SELECT id FROM probe WHERE id = @id", conn))
        {
            select.Parameters.AddWithValue("@id", raw);
            await using var reader = await select.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal(raw, reader.GetGuid(0));
            Assert.Equal(typeof(Guid), reader.GetFieldType(0));
        }

        // 4) IN 清單展開
        var ids = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM probe WHERE id IN @ids", new { ids = new[] { id, raw, Guid.NewGuid() } }, cancellationToken: Ct))).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains(id, ids);
        Assert.Contains(raw, ids);

        // 5) Guid? 寫入非 NULL 值並讀回
        var other = Guid.CreateVersion7();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE probe SET other_id = @other WHERE id = @id", new { id, other }, cancellationToken: Ct));
        var withOther = await conn.QuerySingleAsync<Row>(new CommandDefinition(
            "SELECT id AS Id, note AS Note, other_id AS OtherId FROM probe WHERE id = @id", new { id }, cancellationToken: Ct));
        Assert.Equal(other, withOther.OtherId);
    }

    [Fact]
    public async Task Persistence_UuidV7_DisplaysAsStandardLowercaseStringAndSortsByCreationOrder()
    {
        var cs = await CreateProbeTableAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            created.Add(Guid.CreateVersion7());
            await Task.Delay(3, Ct); // 確保毫秒時間戳遞增
        }

        // 亂序寫入
        foreach (var id in created.OrderByDescending(g => g))
        {
            await Db.ExecAsync(cs, "INSERT INTO probe (id, note) VALUES (@id, 'v7')", new { id });
        }

        // 查詢結果顯示為標準 8-4-4-4-12 小寫字串（Demo 直接查 DB 可讀）
        var text = await Db.QueryAsync<string>(cs, "SELECT CAST(id AS CHAR) FROM probe ORDER BY id");
        Assert.All(text, t => Assert.Equal(36, t.Length));
        Assert.Equal(created.Select(g => g.ToString("D")), text);

        // ORDER BY id 的順序與建立順序一致（UUIDv7 時間排序在 MariaDB UUID 型別下保持）
        var sorted = await Db.QueryAsync<Guid>(cs, "SELECT id FROM probe ORDER BY id");
        Assert.Equal(created, sorted);
    }

    [Fact]
    public async Task Persistence_Uuid_StringLiteralAndGuidParameterAreInterchangeable()
    {
        var cs = await CreateProbeTableAsync();
        var id = Guid.CreateVersion7();
        await Db.ExecAsync(cs, $"INSERT INTO probe (id, note) VALUES ('{id:D}', 'literal')");

        // 手寫字串寫入，用 Guid 參數查得到（Demo 手動 INSERT / 排錯時的前提）
        var found = await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM probe WHERE id = @id", new { id });
        Assert.Equal(1, found);
    }
}
