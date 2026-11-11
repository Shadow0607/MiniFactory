using MiniFactory.Application.Orders;
using MiniFactory.Infrastructure.Persistence;
using MiniFactory.Infrastructure.Persistence.Data;
using MiniFactory.IntegrationTests.Support;
using MySqlConnector;

namespace MiniFactory.IntegrationTests.Persistence;

/// <summary>B4：訂單詳情的多個查詢經 DbSession 的唯讀一致性快照交易；B5：未篩選的訂單列表不帶 IN 子句。</summary>
[Collection(MariaDbCollection.Name)]
public class ReadOnlySnapshotTests(MariaDbFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MySqlDataSource DataSource(string cs) => new MySqlDataSourceBuilder(DbConnectionString.Normalize(cs)).Build();

    private static Task InsertInbox(string cs, Guid id)
        => Db.ExecAsync(cs, "INSERT INTO inbox_messages (consumer_name, message_id, processed_at) VALUES ('snap', @id, @now)", new { id, now = Db.T0 });

    [Fact]
    public async Task Snapshot_QueriesInsideSeeTheSamePointInTime_EvenIfOthersCommitBetweenThem()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        await InsertInbox(cs, Guid.CreateVersion7());

        await using (await session.BeginReadOnlySnapshotAsync(Ct))
        {
            Assert.True(session.HasActiveTransaction);
            var before = await session.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM inbox_messages", null, Ct);

            await InsertInbox(cs, Guid.CreateVersion7()); // 另一個連線在兩個查詢之間提交

            var during = await session.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM inbox_messages", null, Ct);
            Assert.Equal(1, before);
            Assert.Equal(before, during); // 同一快照：看不到後來提交的資料
        }

        Assert.False(session.HasActiveTransaction);
        // 離開快照後回到 autocommit，看得到最新資料
        Assert.Equal(2, await session.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM inbox_messages", null, Ct));
    }

    [Fact]
    public async Task Snapshot_IsReadOnly_AndRepeatableRead()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);

        await using (await session.BeginReadOnlySnapshotAsync(Ct))
        {
            var ex = await Assert.ThrowsAsync<MySqlException>(() => session.ExecuteAsync(
                "INSERT INTO inbox_messages (consumer_name, message_id, processed_at) VALUES ('x', UUID(), UTC_TIMESTAMP(6))", null, Ct));
            Assert.Equal(1792, (int)ex.ErrorCode); // ER_CANT_EXECUTE_IN_READ_ONLY_TRANSACTION

            // @@tx_isolation 是 session 預設值（SET TRANSACTION 只影響下一個交易），要看實際交易的隔離等級得查 INNODB_TRX
            Assert.Equal("REPEATABLE READ", await session.ExecuteScalarAsync<string>(
                "SELECT trx_isolation_level FROM information_schema.INNODB_TRX WHERE trx_mysql_thread_id = CONNECTION_ID()", null, Ct));
        }
    }

    [Fact]
    public async Task Snapshot_DoesNotChangeIsolationOfLaterTransactionsOnTheSameConnection()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        var uow = new UnitOfWork(session);

        await using (await session.BeginReadOnlySnapshotAsync(Ct))
        {
        }

        await using var tx = await uow.BeginAsync(Ct);
        Assert.Equal("READ-COMMITTED", await session.ExecuteScalarAsync<string>("SELECT @@tx_isolation", null, Ct));
        Assert.Equal(0, await session.ExecuteScalarAsync<long>("SELECT @@tx_read_only", null, Ct));
    }

    [Fact]
    public async Task Snapshot_NestedSnapshot_IsRejected()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);

        await using var snapshot = await session.BeginReadOnlySnapshotAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.BeginReadOnlySnapshotAsync(Ct));
    }

    [Fact]
    public async Task Snapshot_BeginUnitOfWorkInsideSnapshot_IsRejected_AndSnapshotStaysIntact()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        var uow = new UnitOfWork(session);
        await InsertInbox(cs, Guid.CreateVersion7());

        await using (await session.BeginReadOnlySnapshotAsync(Ct))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await uow.BeginAsync(Ct));

            // 被拒絕後快照仍有效：仍是同一個時間點、仍是唯讀，也沒有留下寫入交易
            Assert.Null(session.Transaction);
            Assert.True(session.HasActiveTransaction);
            await InsertInbox(cs, Guid.CreateVersion7());
            Assert.Equal(1, await session.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM inbox_messages", null, Ct));
        }

        Assert.False(session.HasActiveTransaction);
    }

    [Fact]
    public async Task Snapshot_BeginSnapshotInsideWriteTransaction_IsRejected_AndWriteTransactionStillRollsBack()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        var uow = new UnitOfWork(session);

        await using (await uow.BeginAsync(Ct))
        {
            await session.ExecuteAsync(
                "INSERT INTO inbox_messages (consumer_name, message_id, processed_at) VALUES ('tx', UUID(), UTC_TIMESTAMP(6))", null, Ct);

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.BeginReadOnlySnapshotAsync(Ct));
            // 未 Commit 離開：寫入必須回滾
        }

        Assert.Equal(0, await Db.ScalarAsync<long>(cs, "SELECT COUNT(*) FROM inbox_messages"));
    }

    [Fact]
    public async Task GetOrder_UsesSnapshot_ThenReturnsSessionToAutocommit_AndWorksWithDmlOnlyAccount()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        var database = new MySqlConnectionStringBuilder(cs).Database;
        await Db.ExecAsync(cs, $"GRANT SELECT, INSERT, UPDATE, DELETE ON `{database}`.* TO '{MariaDbFixture.AppUser}'@'%'");
        var (orderId, jobId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Db.InsertOrderAsync(cs, orderId);
        await Db.InsertJobAsync(cs, jobId, orderId);

        var appCs = new MySqlConnectionStringBuilder(fixture.AppConnectionString(database)).ConnectionString;
        await using var dataSource = DataSource(appCs);
        await using var session = new DbSession(dataSource);

        var detail = await new DashboardQueries(session).GetOrderAsync(orderId, Ct);

        Assert.NotNull(detail);
        Assert.Equal(orderId, detail!.Id);
        Assert.Single(detail.Jobs);
        Assert.False(session.HasActiveTransaction); // 查完就結束快照交易
        Assert.Null(await new DashboardQueries(session).GetOrderAsync(Guid.CreateVersion7(), Ct)); // 不存在的訂單同樣正確收尾
        Assert.False(session.HasActiveTransaction);
    }

    // ---------------------------------------------------------------- B5

    [Fact]
    public void ListOrdersSql_WithoutStatusFilter_HasNoInClause_AndFilteredVersionDoes()
    {
        Assert.DoesNotContain("@statuses", DashboardQueries.OrderCountAll, StringComparison.Ordinal);
        Assert.DoesNotContain("@statuses", DashboardQueries.OrderSelectAll, StringComparison.Ordinal);
        Assert.DoesNotContain(" IN ", DashboardQueries.OrderSelectAll, StringComparison.Ordinal);

        Assert.Contains("o.status IN @statuses", DashboardQueries.OrderCountByStatus, StringComparison.Ordinal);
        Assert.Contains("o.status IN @statuses", DashboardQueries.OrderSelectByStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListOrders_WithoutFilter_ReturnsEveryStatus_AndFilterStillNarrows()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        foreach (var status in new[] { "Created", "InProgress", "AwaitingConfirmation", "Completed", "NeedsReview" })
        {
            var id = Guid.CreateVersion7();
            await Db.InsertOrderAsync(cs, id, "ORD-" + status);
            var completedAt = status is "Completed" or "NeedsReview" ? "UTC_TIMESTAMP(6)" : "NULL";
            await Db.ExecAsync(cs, $"UPDATE production_orders SET status = @status, completed_at = {completedAt} WHERE id = @id", new { status, id });
        }

        var queries = new DashboardQueries(session);
        var all = await queries.ListOrdersAsync(new OrderListFilter([], null), 1, 50, Ct);
        var prefixed = await queries.ListOrdersAsync(new OrderListFilter([], "ORD-Cr"), 1, 50, Ct);
        var narrowed = await queries.ListOrdersAsync(
            new OrderListFilter([MiniFactory.Domain.Orders.OrderStatus.Completed, MiniFactory.Domain.Orders.OrderStatus.NeedsReview], null), 1, 50, Ct);

        Assert.Equal(5, all.TotalCount);
        Assert.Equal(5, all.Items.Count);
        Assert.Equal(1, prefixed.TotalCount); // 只有 ORD-Created
        Assert.Equal(2, narrowed.TotalCount);
    }
}
