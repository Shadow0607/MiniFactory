using DotNet.Testcontainers.Configurations;
using MiniFactory.Infrastructure.Migrations;
using MiniFactory.Infrastructure.Persistence;
using MiniFactory.Infrastructure.Persistence.TypeHandlers;
using MiniFactory.Simulator.Persistence;
using MySqlConnector;
using Testcontainers.MariaDb;

namespace MiniFactory.IntegrationTests.Support;

/// <summary>
/// 整個整合測試共用一個 mariadb:10.11.11 容器（與正式環境 NAS 版本一致）。
/// 容器啟動時掛載 repo 內真正的 initdb 腳本，所以「三個最小權限帳號」測的就是實際部署用的腳本；
/// 啟動後依實際部署路徑執行遷移：MES 用 mf_migrator、Simulator 用 mf_sim。
/// </summary>
public sealed class MariaDbFixture : IAsyncLifetime
{
    public const string Image = "mariadb:10.11.11";
    public const string MesDatabase = "minifactory";
    public const string SimDatabase = "minifactory_sim";

    /// <summary>DbUp journal（schemaversions）專用 database；只有 mf_migrator 有權限，mf_app / mf_sim 完全碰不到。</summary>
    public const string JournalDatabase = "minifactory_migrations";

    // 刻意含單引號與反斜線，驗證 initdb 腳本的 SQL 跳脫與連線字串處理
    public const string AppUser = "mf_app";
    public const string MigratorUser = "mf_migrator";
    public const string SimUser = "mf_sim";

    private readonly string _rootPassword = "root-" + Guid.NewGuid().ToString("N");
    private readonly string _appPassword = "app'pw\\" + Guid.NewGuid().ToString("N");
    private readonly string _migratorPassword = "mig-" + Guid.NewGuid().ToString("N");
    private readonly string _simPassword = "sim-" + Guid.NewGuid().ToString("N");

    private MariaDbContainer? _container;

    static MariaDbFixture() => DapperTypeHandlers.Register();

    public MigrationResult FirstMesMigration { get; private set; } = default!;

    public SimulatorMigrationResult FirstSimulatorMigration { get; private set; } = default!;

    public async ValueTask InitializeAsync()
    {
        var initdb = new FileInfo(Path.Combine(RepoPaths.Root, "deploy", "mariadb", "initdb", "01-databases-and-users.sh"));
        Assert.True(initdb.Exists, $"找不到 initdb 腳本：{initdb.FullName}");

        _container = new MariaDbBuilder("mariadb:10.11.11")
            .WithPassword(_rootPassword)
            .WithEnvironment("MARIADB_ROOT_PASSWORD", _rootPassword)
            .WithEnvironment("MF_APP_USER", AppUser)
            .WithEnvironment("MF_APP_PASSWORD", _appPassword)
            .WithEnvironment("MF_MIGRATOR_USER", MigratorUser)
            .WithEnvironment("MF_MIGRATOR_PASSWORD", _migratorPassword)
            .WithEnvironment("MF_SIM_USER", SimUser)
            .WithEnvironment("MF_SIM_PASSWORD", _simPassword)
            .WithResourceMapping(
                File.ReadAllBytes(initdb.FullName),
                "/docker-entrypoint-initdb.d/01-databases-and-users.sh",
                fileMode: UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute
                          | UnixFileModes.GroupRead | UnixFileModes.GroupExecute
                          | UnixFileModes.OtherRead | UnixFileModes.OtherExecute)
            .WithCommand(
                "--character-set-server=utf8mb4",
                "--collation-server=utf8mb4_unicode_ci",
                "--transaction-isolation=READ-COMMITTED",
                "--innodb-lock-wait-timeout=10")
            .Build();

        await _container.StartAsync(TestContext.Current.CancellationToken);

        // 模組內建的就緒檢查可能在 entrypoint 的暫時伺服器階段就通過；
        // initdb 腳本跑完、真正的伺服器開啟 TCP 後 mf_migrator 才連得上，以此作為就緒條件
        await WaitUntilAsync(async () =>
        {
            await using var conn = new MySqlConnection(MigratorConnectionString());
            await conn.OpenAsync(TestContext.Current.CancellationToken);
        });

        FirstMesMigration = DbUpMigrator.Migrate(MigratorConnectionString(), JournalLocation.InSchema(JournalDatabase));
        FirstSimulatorMigration = SimulatorMigrator.Migrate(SimConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private string Host => _container!.Hostname;

    private uint Port => _container!.GetMappedPublicPort(3306);

    private string Build(string user, string password, string? database) => new MySqlConnectionStringBuilder
    {
        Server = Host,
        Port = Port,
        UserID = user,
        Password = password,
        Database = database ?? string.Empty,
        ConnectionTimeout = 15,
        Pooling = true,
    }.ConnectionString;

    /// <summary>root：只用於建立測試用 database 與檢視 information_schema。</summary>
    public string RootConnectionString(string? database = null) => Build("root", _rootPassword, database);

    public string MigratorConnectionString() => Build(MigratorUser, _migratorPassword, MesDatabase);

    public string AppConnectionString(string? database = MesDatabase) => Build(AppUser, _appPassword, database);

    public string SimConnectionString(string? database = SimDatabase) => Build(SimUser, _simPassword, database);

    public string MigratorConnectionString(string? database) => Build(MigratorUser, _migratorPassword, database);

    /// <summary>
    /// 建立一個全新的 database 並跑完 MES 全部遷移（root 帳號），回傳指向它的 root 連線字串。
    /// 供需要乾淨資料的測試使用，彼此隔離。
    /// </summary>
    public async Task<string> CreateMigratedDatabaseAsync()
    {
        var name = "mf_t_" + Guid.NewGuid().ToString("N")[..12];
        await using (var conn = new MySqlConnection(RootConnectionString()))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new MySqlCommand(
                $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", conn);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var cs = RootConnectionString(name);
        var result = DbUpMigrator.Migrate(cs, JournalLocation.InTargetDatabase);
        Assert.True(result.Successful, result.ErrorMessage);
        return cs;
    }

    /// <summary>建立空的 database（不遷移），回傳 root 連線字串。</summary>
    public async Task<string> CreateEmptyDatabaseAsync(string prefix = "mf_e_")
    {
        var name = prefix + Guid.NewGuid().ToString("N")[..12];
        await using var conn = new MySqlConnection(RootConnectionString());
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new MySqlCommand(
            $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", conn);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return RootConnectionString(name);
    }

    private static async Task WaitUntilAsync(Func<Task> probe)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await probe();
                return;
            }
            catch (Exception ex) when (ex is MySqlException or IOException or TimeoutException)
            {
                last = ex;
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }
        }

        throw new TimeoutException("MariaDB 容器在 90 秒內未就緒。", last);
    }
}

[CollectionDefinition(Name)]
public sealed class MariaDbCollection : ICollectionFixture<MariaDbFixture>
{
    public const string Name = "MariaDB";
}
