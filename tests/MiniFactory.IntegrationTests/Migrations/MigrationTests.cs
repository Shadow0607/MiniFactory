using System.Text.RegularExpressions;
using MiniFactory.Infrastructure.Migrations;
using MiniFactory.IntegrationTests.Support;
using MiniFactory.Simulator.Persistence;

namespace MiniFactory.IntegrationTests.Migrations;

/// <summary>database.md §7 第 8 項、failure-scenarios.md 情境 13：從空資料庫遷移、可重跑。</summary>
[Collection(MariaDbCollection.Name)]
public partial class MigrationTests(MariaDbFixture fixture)
{
    [GeneratedRegex(@"^MiniFactory\.(Infrastructure|Simulator)\.(Migrations|Persistence\.Migrations)\.Scripts\.(\d{4})_[a-z0-9_]+\.sql$")]
    private static partial Regex ScriptNamePattern();

    [Fact]
    public void Scripts_AreNamedNnnnVerbObject_WithConsecutiveUniqueNumbers()
    {
        var mes = DbUpMigrator.ListScripts();
        Assert.Equal(31, mes.Count);

        var numbers = mes.Select(n =>
        {
            var m = ScriptNamePattern().Match(n);
            Assert.True(m.Success, $"腳本名稱不符 NNNN_動詞_對象.sql：{n}");
            return int.Parse(m.Groups[3].Value);
        }).ToList();
        Assert.Equal(Enumerable.Range(1, mes.Count), numbers);
    }

    [Fact]
    public void Migrations_FromEmptyDatabase_AppliedEveryScriptInOrderViaMfMigrator()
    {
        // fixture 以 mf_migrator 對全新容器的 minifactory 做了第一次遷移
        Assert.True(fixture.FirstMesMigration.Successful, fixture.FirstMesMigration.ErrorMessage);
        Assert.Equal(DbUpMigrator.ListScripts(), fixture.FirstMesMigration.ExecutedScripts);

        // Simulator 以 mf_sim 對 minifactory_sim 做了第一次遷移
        Assert.True(fixture.FirstSimulatorMigration.Successful, fixture.FirstSimulatorMigration.ErrorMessage);
        Assert.Equal(2, fixture.FirstSimulatorMigration.ExecutedScripts.Count);
    }

    [Fact]
    public void Migrations_SecondRun_ExecutesNothing()
    {
        var mes = DbUpMigrator.Migrate(fixture.MigratorConnectionString(), JournalLocation.InSchema(MariaDbFixture.JournalDatabase));
        Assert.True(mes.Successful, mes.ErrorMessage);
        Assert.Empty(mes.ExecutedScripts);

        var sim = SimulatorMigrator.Migrate(fixture.SimConnectionString());
        Assert.True(sim.Successful, sim.ErrorMessage);
        Assert.Empty(sim.ExecutedScripts);
    }

    [Fact]
    public async Task Migrations_Journal_MesInSeparateDatabase_SimulatorInItsOwn_AndRecordsEveryScript()
    {
        // MES 的 journal 在獨立的 minifactory_migrations（mf_app 碰不到），業務 database 內沒有 schemaversions
        var mesCount = await Db.ScalarAsync<int>(fixture.MigratorConnectionString(), "SELECT COUNT(*) FROM minifactory_migrations.schemaversions");
        var mesTablesInBusinessDb = await Db.ScalarAsync<int>(
            fixture.MigratorConnectionString(),
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'minifactory' AND TABLE_NAME = 'schemaversions'");
        var simCount = await Db.ScalarAsync<int>(fixture.SimConnectionString(), "SELECT COUNT(*) FROM schemaversions");

        Assert.Equal(31, mesCount);
        Assert.Equal(0, mesTablesInBusinessDb);
        Assert.Equal(2, simCount);
    }

    [Fact]
    public async Task Migrations_WithSeparateJournalSchema_RunTwice_SecondRunHasNoChanges()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();
        var journal = await fixture.CreateEmptyDatabaseAsync("mf_j_");
        var journalName = new MySqlConnector.MySqlConnectionStringBuilder(journal).Database;

        var first = DbUpMigrator.Migrate(cs, JournalLocation.InSchema(journalName));
        Assert.True(first.Successful, first.ErrorMessage);
        Assert.Equal(31, first.ExecutedScripts.Count);

        var second = DbUpMigrator.Migrate(cs, JournalLocation.InSchema(journalName));
        Assert.True(second.Successful, second.ErrorMessage);
        Assert.Empty(second.ExecutedScripts);
        Assert.Equal(31, await Db.ScalarAsync<int>(journal, "SELECT COUNT(*) FROM schemaversions"));
    }

    [Fact]
    public async Task Migrations_FreshRootDatabase_RunTwice_SecondRunHasNoChanges()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();

        var first = DbUpMigrator.Migrate(cs, JournalLocation.InTargetDatabase);
        Assert.True(first.Successful, first.ErrorMessage);
        Assert.Equal(31, first.ExecutedScripts.Count);

        var second = DbUpMigrator.Migrate(cs, JournalLocation.InTargetDatabase);
        Assert.True(second.Successful, second.ErrorMessage);
        Assert.Empty(second.ExecutedScripts);
    }

    [Fact]
    public async Task Migrations_ScriptsThemselvesAreRerunnable_WhenJournalIsLost()
    {
        // 模擬「DDL 隱含 COMMIT、腳本中途失敗後重跑」：清掉 journal 後整批重跑，
        // 因為每支腳本都用 IF NOT EXISTS / ON DUPLICATE KEY，結構與種子資料都不變
        var cs = await fixture.CreateMigratedDatabaseAsync();
        var before = await Db.ScalarAsync<string>(cs, "SELECT GROUP_CONCAT(table_name ORDER BY table_name) FROM information_schema.TABLES WHERE table_schema = DATABASE()");

        await Db.ExecAsync(cs, "DELETE FROM schemaversions WHERE scriptname LIKE '%.sql'");
        var rerun = DbUpMigrator.Migrate(cs, JournalLocation.InTargetDatabase);

        Assert.True(rerun.Successful, rerun.ErrorMessage);
        Assert.Equal(31, rerun.ExecutedScripts.Count);
        Assert.Equal(3, await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM equipment"));
        Assert.Equal(before, await Db.ScalarAsync<string>(cs, "SELECT GROUP_CONCAT(table_name ORDER BY table_name) FROM information_schema.TABLES WHERE table_schema = DATABASE()"));
    }

    [Fact]
    public async Task Seed_Equipment_Line01_MachineABC_WithFixedIds()
    {
        var rows = await Db.QueryAsync<(Guid Id, string Name, string Capability, string Status, string LineId)>(
            fixture.MigratorConnectionString(),
            "SELECT id, name, capability, status, line_id FROM equipment ORDER BY name");

        Assert.Equal(
            [
                (Guid.Parse("01a1c7fc-2938-7775-9d83-762b1a40f2ed"), "Machine-A", "Machining", "Offline", "Line-01"),
                (Guid.Parse("01a1c7fc-2943-77c3-88ed-d3e28c659891"), "Machine-B", "Assembly", "Offline", "Line-01"),
                (Guid.Parse("01a1c7fc-294d-7f72-a080-cd8cbc0c6912"), "Machine-C", "Inspection", "Offline", "Line-01"),
            ],
            rows);
    }
}
