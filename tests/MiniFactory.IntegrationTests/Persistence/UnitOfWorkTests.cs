using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Persistence;
using MiniFactory.Infrastructure;
using MiniFactory.Infrastructure.Persistence;
using MiniFactory.IntegrationTests.Support;
using MySqlConnector;

namespace MiniFactory.IntegrationTests.Persistence;

/// <summary>DbSession / UnitOfWork / TransactionRunner / MySqlErrorTranslator 對真實 MariaDB 的行為。</summary>
[Collection(MariaDbCollection.Name)]
public class UnitOfWorkTests(MariaDbFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MySqlDataSource DataSource(string cs) => new MySqlDataSourceBuilder(DbConnectionString.Normalize(cs)).Build();

    private static TransactionRunner Runner(int maxAttempts = 3)
        => new(new TransactionRetryOptions { MaxAttempts = maxAttempts, BaseDelay = TimeSpan.FromMilliseconds(5) }, NullLogger<TransactionRunner>.Instance);

    [Fact]
    public async Task UnitOfWork_Commit_PersistsAllWritesTogether()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        var uow = new UnitOfWork(session);
        var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        await using (var tx = await uow.BeginAsync(Ct))
        {
            await session.ExecuteAsync("INSERT INTO inbox_messages (consumer_name, message_id, processed_at) VALUES ('c', @a, @now)", new { a, now = Db.T0 }, Ct);
            await session.ExecuteAsync("INSERT INTO inbox_messages (consumer_name, message_id, processed_at) VALUES ('c', @b, @now)", new { b, now = Db.T0 }, Ct);
            await tx.CommitAsync(Ct);
        }

        Assert.False(session.HasActiveTransaction);
        Assert.Equal(2, await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM inbox_messages WHERE consumer_name = 'c'"));
    }

    [Fact]
    public async Task UnitOfWork_DisposeWithoutCommit_RollsBackEverything()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        var uow = new UnitOfWork(session);

        await using (var tx = await uow.BeginAsync(Ct))
        {
            await session.ExecuteAsync("INSERT INTO inbox_messages (consumer_name, message_id, processed_at) VALUES ('c', @id, @now)", new { id = Guid.NewGuid(), now = Db.T0 }, Ct);
        }

        Assert.Equal(0, await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM inbox_messages"));
    }

    [Fact]
    public async Task UnitOfWork_UsesReadCommitted_AndRejectsNestedTransaction()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        var uow = new UnitOfWork(session);

        await using var tx = await uow.BeginAsync(Ct);
        var level = await session.ExecuteScalarAsync<string>("SELECT @@tx_isolation", cancellationToken: Ct);
        Assert.Equal("READ-COMMITTED", level);
        Assert.Equal(IsolationLevel.ReadCommitted, session.Transaction!.IsolationLevel);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await uow.BeginAsync(Ct));
    }

    [Fact]
    public async Task UseAffectedRows_ConditionalUpdate_ReturnsChangedRowCount()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);

        // 種子設備 Machine-A 為 Offline、version 0：第一次條件更新成功（1），版本已變所以第二次同條件更新為 0
        const string sql = "UPDATE equipment SET status = 'Idle', version = version + 1, updated_at = @now WHERE name = 'Machine-A' AND version = @v";
        Assert.Equal(1, await session.ExecuteAsync(sql, new { now = Db.T0, v = 0 }, Ct));
        Assert.Equal(0, await session.ExecuteAsync(sql, new { now = Db.T0, v = 0 }, Ct));
    }

    [Fact]
    public async Task Translator_DuplicateKey_BecomesUniqueConstraintViolation()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        const string insert = """
            INSERT INTO production_orders (id, order_no, product_code, quantity, line_id, status, version, correlation_id, created_at, updated_at)
            VALUES (@id, @orderNo, 'Motor-A', 10, 'Line-01', 'Created', 0, 'corr', @now, @now)
            """;
        await session.ExecuteAsync(insert, new { id = Guid.CreateVersion7(), orderNo = "ORD-DUP", now = Db.T0 }, Ct);

        // 不分大小寫（utf8mb4_unicode_ci）也視為重複
        var ex = await Assert.ThrowsAsync<UniqueConstraintViolationException>(() =>
            session.ExecuteAsync(insert, new { id = Guid.CreateVersion7(), orderNo = "ord-dup", now = Db.T0 }, Ct));
        Assert.Equal(1062, ex.ErrorCode);
    }

    [Fact]
    public async Task Translator_ForeignKeyAndCheckViolations_AreMappedByErrorCode()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);

        // 1452：子列指向不存在的父列
        var fk = await Assert.ThrowsAsync<ReferenceViolationException>(() => session.ExecuteAsync(
            "INSERT INTO production_jobs (id, order_id, sequence, operation, status, created_at, updated_at) VALUES (@id, @orderId, 1, 'Machining', 'Pending', @now, @now)",
            new { id = Guid.NewGuid(), orderId = Guid.NewGuid(), now = Db.T0 }, Ct));
        Assert.Equal(1452, fk.ErrorCode);

        // 4025：CHECK 約束（狀態字串不合法）
        var check = await Assert.ThrowsAsync<ConstraintViolationException>(() => session.ExecuteAsync(
            "UPDATE equipment SET status = 'Broken' WHERE name = 'Machine-A'", cancellationToken: Ct));
        Assert.Equal(4025, check.ErrorCode);

        // 1451：刪除仍被參照的父列
        var orderId = Guid.CreateVersion7();
        await Db.InsertOrderAsync(cs, orderId);
        await Db.InsertJobAsync(cs, Guid.CreateVersion7(), orderId);
        var parent = await Assert.ThrowsAsync<ReferenceViolationException>(() => session.ExecuteAsync(
            "DELETE FROM production_orders WHERE id = @orderId", new { orderId }, Ct));
        Assert.Equal(1451, parent.ErrorCode);
    }

    [Fact]
    public async Task Translator_UnreachableServer_BecomesDatabaseUnavailable()
    {
        // 埠 1 沒有服務：立即連線被拒
        var cs = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = 1,
            UserID = "x",
            Password = "x",
            ConnectionTimeout = 3,
        }.ConnectionString;
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);

        var ex = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => session.ExecuteAsync("SELECT 1", cancellationToken: Ct));
        Assert.IsAssignableFrom<MySqlException>(ex.InnerException);
    }

    [Fact]
    public async Task Translator_UnmappedError_IsLeftAsMySqlException()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);

        // 1146 表不存在：不在翻譯清單，維持原始例外往上拋
        var ex = await Assert.ThrowsAsync<MySqlException>(() => session.ExecuteAsync("SELECT * FROM no_such_table", cancellationToken: Ct));
        Assert.Equal(1146, (int)ex.ErrorCode);
    }

    [Fact]
    public async Task Runner_LockWaitTimeout_1205_IsRetriedAndSucceedsOnceLockIsReleased()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);

        // 另一個連線持有 Machine-A 那一列的鎖
        await using var blocker = await Db.OpenAsync(cs);
        await using var blockerTx = await blocker.BeginTransactionAsync(Ct);
        await using (var cmd = new MySqlCommand("UPDATE equipment SET version = version + 1 WHERE name = 'Machine-A'", blocker, blockerTx))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        var attempts = 0;
        await using var session = new DbSession(dataSource);
        var uow = new UnitOfWork(session);

        await Runner().RunAsync(async ct =>
        {
            attempts++;
            if (attempts == 2)
            {
                await blockerTx.RollbackAsync(ct); // 第二次嘗試前釋放鎖
            }

            await using var tx = await uow.BeginAsync(ct);
            await session.ExecuteAsync("SET SESSION innodb_lock_wait_timeout = 1", cancellationToken: ct);
            await session.ExecuteAsync("UPDATE equipment SET version = version + 10 WHERE name = 'Machine-A'", cancellationToken: ct);
            await tx.CommitAsync(ct);
        }, Ct);

        Assert.Equal(2, attempts);
        // 第一次嘗試（1205）的寫入沒有殘留；只有第二次的 +10
        Assert.Equal(10, await Db.ScalarAsync<int>(cs, "SELECT version FROM equipment WHERE name = 'Machine-A'"));
    }

    [Fact]
    public async Task Runner_ExhaustedRetries_ThrowsTransientAfterMaxAttempts()
    {
        var attempts = 0;
        var ex = await Assert.ThrowsAsync<TransientDbException>(() => Runner(3).RunAsync<int>(_ =>
        {
            attempts++;
            throw new TransientDbException(TransientDbFailure.Deadlock, 1213, "deadlock");
        }, Ct));

        Assert.Equal(3, attempts);
        Assert.Equal(TransientDbFailure.Deadlock, ex.Failure);
    }

    [Fact]
    public async Task Runner_NonTransientErrors_AreNotRetried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => Runner(3).RunAsync<int>(_ =>
        {
            attempts++;
            throw new UniqueConstraintViolationException(1062, "dup");
        }, Ct));
        Assert.Equal(1, attempts);

        attempts = 0;
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => Runner(3).RunAsync<int>(_ =>
        {
            attempts++;
            throw new DatabaseUnavailableException(2003, "down");
        }, Ct));
        Assert.Equal(1, attempts); // 連線失敗不自動重試（architecture.md §10.6）
    }

    [Fact]
    public async Task DependencyInjection_ResolvesUnitOfWorkAndRunner_AndRunsAStatement()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMiniFactoryPersistence(cs);
        await using var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var runner = scope.ServiceProvider.GetRequiredService<ITransactionRunner>();
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();

        var count = await runner.RunAsync(async ct =>
        {
            await using var tx = await uow.BeginAsync(ct);
            var n = await session.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM equipment", cancellationToken: ct);
            await tx.CommitAsync(ct);
            return n;
        }, Ct);

        Assert.Equal(3, count);
    }
}
