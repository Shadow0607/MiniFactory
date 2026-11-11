using MiniFactory.IntegrationTests.Support;
using MySqlConnector;

namespace MiniFactory.IntegrationTests.Behavior;

/// <summary>
/// architecture.md §18 步驟 1.2 驗收、database.md §7 第 9 項：
/// mf_app 無 DDL；mf_sim 看不到 minifactory；各帳號互相隔離。測的是 deploy/mariadb/initdb 的實際腳本。
/// </summary>
[Collection(MariaDbCollection.Name)]
public class RoleSeparationTests(MariaDbFixture fixture)
{
    // 1044 = 資料庫存取被拒、1142 = 表的指令被拒、1045 = 帳密錯誤
    private const int AccessDeniedToDatabase = 1044;
    private const int CommandDeniedToTable = 1142;
    private const int AccessDeniedForUser = 1045;

    private static async Task<int> ErrorCodeAsync(string cs, string? sql)
    {
        var ex = await Assert.ThrowsAsync<MySqlException>(async () =>
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            if (sql is not null)
            {
                await using var cmd = new MySqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        });
        return (int)ex.ErrorCode;
    }

    [Theory]
    [InlineData("CREATE TABLE ddl_probe (id INT)")]
    [InlineData("ALTER TABLE equipment ADD COLUMN ddl_probe INT NULL")]
    [InlineData("CREATE INDEX ix_ddl_probe ON equipment (updated_at)")]
    [InlineData("DROP TABLE inbox_messages")]
    [InlineData("TRUNCATE TABLE inbox_messages")]
    [InlineData("CREATE DATABASE ddl_probe")]
    public async Task MfApp_CannotExecuteDdl(string ddl)
    {
        var code = await ErrorCodeAsync(fixture.AppConnectionString(), ddl);
        Assert.Contains(code, new[] { CommandDeniedToTable, AccessDeniedToDatabase });
    }

    [Fact]
    public async Task MfApp_CanDoDmlOnMinifactory()
    {
        var cs = fixture.AppConnectionString();
        var id = Guid.CreateVersion7();

        await Db.ExecAsync(cs, "INSERT INTO inbox_messages (consumer_name, message_id, processed_at) VALUES ('role-test', @id, @now)", new { id, now = Db.T0 });
        Assert.Equal(1, await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM inbox_messages WHERE message_id = @id", new { id }));
        Assert.Equal(1, await Db.ExecAsync(cs, "UPDATE inbox_messages SET processed_at = @now WHERE message_id = @id", new { id, now = Db.T0.AddHours(1) }));
        Assert.Equal(1, await Db.ExecAsync(cs, "DELETE FROM inbox_messages WHERE message_id = @id", new { id }));
        Assert.Equal(3, await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM equipment"));
    }

    [Fact]
    public async Task MfApp_CannotAccessSimulatorDatabase()
    {
        Assert.Equal(AccessDeniedToDatabase, await ErrorCodeAsync(fixture.AppConnectionString("minifactory_sim"), null));
        Assert.Equal(CommandDeniedToTable, await ErrorCodeAsync(fixture.AppConnectionString(), "SELECT * FROM minifactory_sim.simulator_commands"));
    }

    [Fact]
    public async Task MfSim_CannotSeeMinifactory()
    {
        // 直接連到 minifactory：被拒
        Assert.Equal(AccessDeniedToDatabase, await ErrorCodeAsync(fixture.SimConnectionString("minifactory"), null));
        // 跨 database 查詢：被拒
        Assert.Equal(CommandDeniedToTable, await ErrorCodeAsync(fixture.SimConnectionString(), "SELECT * FROM minifactory.equipment"));
        Assert.Equal(CommandDeniedToTable, await ErrorCodeAsync(fixture.SimConnectionString(), "INSERT INTO minifactory.inbox_messages (consumer_name, message_id, processed_at) VALUES ('x', UUID(), UTC_TIMESTAMP(6))"));

        // SHOW DATABASES 只看得到自己的 database
        var visible = await Db.QueryAsync<string>(fixture.SimConnectionString(), "SHOW DATABASES");
        Assert.Contains("minifactory_sim", visible);
        Assert.DoesNotContain("minifactory", visible);
        Assert.DoesNotContain("mysql", visible);

        // information_schema 也看不到 MES 的表
        Assert.Equal(0, await Db.ScalarAsync<int>(fixture.SimConnectionString(),
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'minifactory'"));
    }

    [Fact]
    public async Task MfSim_CanDoDdlAndDmlOnItsOwnDatabase()
    {
        var cs = fixture.SimConnectionString();
        await Db.ExecAsync(cs, "CREATE TABLE IF NOT EXISTS sim_role_probe (id INT NOT NULL, PRIMARY KEY (id)) ENGINE=InnoDB");
        await Db.ExecAsync(cs, "INSERT INTO sim_role_probe (id) VALUES (1) ON DUPLICATE KEY UPDATE id = id");
        Assert.Equal(1, await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sim_role_probe"));
        await Db.ExecAsync(cs, "DROP TABLE sim_role_probe");
    }

    [Fact]
    public async Task MfMigrator_HasDdlOnMinifactory_ButNothingOnSimulatorDatabase()
    {
        var cs = fixture.MigratorConnectionString();
        await Db.ExecAsync(cs, "CREATE TABLE IF NOT EXISTS migrator_role_probe (id INT NOT NULL, PRIMARY KEY (id)) ENGINE=InnoDB");
        await Db.ExecAsync(cs, "DROP TABLE migrator_role_probe");

        Assert.Equal(AccessDeniedToDatabase, await ErrorCodeAsync(fixture.MigratorConnectionString("minifactory_sim"), null));
    }

    // ---- DbUp journal（S10）：mf_app / mf_sim 不得讀寫 schemaversions ----

    [Theory]
    [InlineData("SELECT COUNT(*) FROM minifactory_migrations.schemaversions")]
    [InlineData("DELETE FROM minifactory_migrations.schemaversions")]
    [InlineData("UPDATE minifactory_migrations.schemaversions SET scriptname = 'x'")]
    [InlineData("INSERT INTO minifactory_migrations.schemaversions (scriptname, applied) VALUES ('fake.sql', UTC_TIMESTAMP())")]
    [InlineData("DROP TABLE minifactory_migrations.schemaversions")]
    [InlineData("TRUNCATE TABLE minifactory_migrations.schemaversions")]
    public async Task MfApp_CannotTouchMigrationJournal(string sql)
    {
        var code = await ErrorCodeAsync(fixture.AppConnectionString(), sql);
        Assert.Equal(CommandDeniedToTable, code);
    }

    [Fact]
    public async Task MfApp_CannotConnectToJournalDatabase_AndDoesNotSeeIt()
    {
        Assert.Equal(AccessDeniedToDatabase, await ErrorCodeAsync(fixture.AppConnectionString(MariaDbFixture.JournalDatabase), null));
        Assert.DoesNotContain(MariaDbFixture.JournalDatabase, await Db.QueryAsync<string>(fixture.AppConnectionString(), "SHOW DATABASES"));
        Assert.DoesNotContain(MariaDbFixture.JournalDatabase, await Db.QueryAsync<string>(fixture.SimConnectionString(), "SHOW DATABASES"));
        Assert.Equal(CommandDeniedToTable, await ErrorCodeAsync(
            fixture.SimConnectionString(), "SELECT COUNT(*) FROM minifactory_migrations.schemaversions"));
    }

    [Fact]
    public async Task MfMigrator_CanReadAndWriteJournal()
    {
        var cs = fixture.MigratorConnectionString();
        Assert.True(await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM minifactory_migrations.schemaversions") > 0);

        // 能寫入（DbUp 每執行一支腳本就寫一列）；測完還原
        await Db.ExecAsync(cs, "INSERT INTO minifactory_migrations.schemaversions (scriptname, applied) VALUES ('role-probe.sql', UTC_TIMESTAMP())");
        Assert.Equal(1, await Db.ExecAsync(cs, "DELETE FROM minifactory_migrations.schemaversions WHERE scriptname = 'role-probe.sql'"));
    }

    [Fact]
    public async Task Grants_MatchLeastPrivilegeDesign()
    {
        var root = fixture.RootConnectionString();
        async Task<string[]> GrantsAsync(string user)
            => (await Db.QueryAsync<string>(root, $"SHOW GRANTS FOR '{user}'@'%'")).ToArray();

        var app = string.Join("\n", await GrantsAsync(MariaDbFixture.AppUser));
        var migrator = string.Join("\n", await GrantsAsync(MariaDbFixture.MigratorUser));
        var sim = string.Join("\n", await GrantsAsync(MariaDbFixture.SimUser));

        Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE ON `minifactory`.*", app);
        Assert.DoesNotContain("minifactory_sim", app);
        Assert.DoesNotContain("minifactory_migrations", app); // 不得修改 DbUp journal
        Assert.DoesNotContain("ALL PRIVILEGES", app);

        Assert.Contains("GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, DROP, REFERENCES, INDEX, ALTER ON `minifactory`.*", migrator);
        Assert.Contains("ON `minifactory_migrations`.*", migrator); // journal 由 migrator 維護
        Assert.DoesNotContain("minifactory_sim", migrator);
        Assert.DoesNotContain("GRANT OPTION", migrator);

        Assert.Contains("ON `minifactory_sim`.*", sim);
        Assert.DoesNotContain("ON `minifactory`.*", sim);
        Assert.DoesNotContain("minifactory_migrations", sim);
        Assert.DoesNotContain("GRANT OPTION", sim);
    }

    [Fact]
    public async Task WrongPassword_IsRejected()
    {
        var bad = new MySqlConnectionStringBuilder(fixture.AppConnectionString()) { Password = "wrong" }.ConnectionString;
        Assert.Equal(AccessDeniedForUser, await ErrorCodeAsync(bad, null));
    }
}
