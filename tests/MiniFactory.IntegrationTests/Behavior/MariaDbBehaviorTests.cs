using Microsoft.Extensions.Logging.Abstractions;
using MiniFactory.Application.Persistence;
using MiniFactory.Infrastructure.Persistence;
using MiniFactory.IntegrationTests.Support;
using MySqlConnector;

namespace MiniFactory.IntegrationTests.Behavior;

/// <summary>failure-scenarios.md 情境 13：MariaDB 專屬行為。</summary>
[Collection(MariaDbCollection.Name)]
public class MariaDbBehaviorTests(MariaDbFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MySqlDataSource DataSource(string cs) => new MySqlDataSourceBuilder(DbConnectionString.Normalize(cs)).Build();

    [Fact]
    public async Task UnitOfWork_DeadlockDetected_RetriesWholeTransaction()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        var (e1, e2) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Db.InsertEquipmentAsync(cs, e1, "DL-1");
        await Db.InsertEquipmentAsync(cs, e2, "DL-2");
        await using var dataSource = DataSource(cs);

        // 兩個交易以相反順序更新兩列：第一次嘗試時雙方各鎖住第一列後才繼續，必然形成死結
        var ready = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var attempts = new int[2];

        async Task RunAsync(int me, Guid first, Guid second)
        {
            await using var session = new DbSession(dataSource);
            var uow = new UnitOfWork(session);
            var runner = new TransactionRunner(
                new TransactionRetryOptions { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(20) },
                NullLogger<TransactionRunner>.Instance);

            await runner.RunAsync(async ct =>
            {
                var attempt = Interlocked.Increment(ref attempts[me]);
                await using var tx = await uow.BeginAsync(ct);
                const string sql = "UPDATE equipment SET version = version + 1, updated_at = @now WHERE id = @id";
                await session.ExecuteAsync(sql, new { id = first, now = Db.T0 }, ct);
                if (attempt == 1)
                {
                    ready[me].SetResult();
                    await ready[1 - me].Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                }

                await session.ExecuteAsync(sql, new { id = second, now = Db.T0 }, ct);
                await tx.CommitAsync(ct);
            }, Ct);
        }

        await Task.WhenAll(RunAsync(0, e1, e2), RunAsync(1, e2, e1));

        // 恰好一方被選為死結犧牲者（1213）並整段重跑一次：總共 3 次嘗試
        Assert.Equal(3, attempts.Sum());
        Assert.Contains(attempts, a => a == 2);
        // 兩個交易各自完整提交一次：每列剛好被更新兩次，沒有部分寫入、也沒有重複寫入
        Assert.Equal(2, await Db.ScalarAsync<int>(cs, "SELECT version FROM equipment WHERE id = @id", new { id = e1 }));
        Assert.Equal(2, await Db.ScalarAsync<int>(cs, "SELECT version FROM equipment WHERE id = @id", new { id = e2 }));
    }

    [Fact]
    public async Task UnitOfWork_DuplicateKeyInsideTransaction_TransactionRemainsUsable()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        var uow = new UnitOfWork(session);
        var (first, dup, other) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        const string insert = "INSERT INTO inbox_messages (consumer_name, message_id, processed_at) VALUES (@consumer, @id, @now)";

        await using (var tx = await uow.BeginAsync(Ct))
        {
            await session.ExecuteAsync(insert, new { consumer = "worker.dispatch", id = first, now = Db.T0 }, Ct);
            await session.ExecuteAsync(insert, new { consumer = "worker.dispatch", id = dup, now = Db.T0 }, Ct);

            // 重複的 inbox 訊息 → 1062，只回滾該語句
            await Assert.ThrowsAsync<UniqueConstraintViolationException>(() =>
                session.ExecuteAsync(insert, new { consumer = "worker.dispatch", id = dup, now = Db.T0 }, Ct));

            // 同一交易繼續寫其他資料並提交
            await session.ExecuteAsync(insert, new { consumer = "worker.dispatch", id = other, now = Db.T0 }, Ct);
            await tx.CommitAsync(Ct);
        }

        // 1062 之前與之後的寫入都成功提交；重複那一筆只有一份
        var ids = await Db.QueryAsync<Guid>(cs, "SELECT message_id FROM inbox_messages WHERE consumer_name = 'worker.dispatch'");
        Assert.Equal(3, ids.Count);
        Assert.Contains(first, ids);
        Assert.Contains(dup, ids);
        Assert.Contains(other, ids);
    }

    [Fact]
    public async Task Alerts_GeneratedOpenDedupKey_AllowsOnlyOneOpenPerKey()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);
        var (a1, a2, a3) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var key = "EQUIPMENT_OFFLINE:" + Guid.NewGuid();

        const string insert = """
            INSERT INTO alerts (id, severity, reason, status, dedup_key, created_at, updated_at)
            VALUES (@id, 'Critical', 'EQUIPMENT_OFFLINE', 'Open', @key, @now, @now)
            """;
        await session.ExecuteAsync(insert, new { id = a1, key, now = Db.T0 }, Ct);

        // 同 dedup_key 第二筆 Open → 1062（生成欄位 open_dedup_key 的唯一索引）
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => session.ExecuteAsync(insert, new { id = a2, key, now = Db.T0 }, Ct));
        Assert.Equal(key, await Db.ScalarAsync<string>(cs, "SELECT open_dedup_key FROM alerts WHERE id = @a1", new { a1 }));

        // 人工結案（database.md 3.7 的條件更新）：affected rows = 1；open_dedup_key 變 NULL
        const string resolve = """
            UPDATE alerts SET status = 'Resolved', resolved_at = @now, resolved_by = 'Operator', resolution_note = @note, updated_at = @now
            WHERE id = @id AND status = 'Open'
            """;
        Assert.Equal(1, await session.ExecuteAsync(resolve, new { id = a1, note = "ok", now = Db.T0 }, Ct));
        Assert.Null(await Db.ScalarAsync<string>(cs, "SELECT open_dedup_key FROM alerts WHERE id = @a1", new { a1 }));
        // 重複結案：條件不符，affected rows = 0（呼叫端改為冪等回 200）
        Assert.Equal(0, await session.ExecuteAsync(resolve, new { id = a1, note = "again", now = Db.T0 }, Ct));

        // 結案後同原因可再建立新的 Open；再結案後，多筆 Resolved 並存
        await session.ExecuteAsync(insert, new { id = a3, key, now = Db.T0 }, Ct);
        Assert.Equal(1, await session.ExecuteAsync(resolve, new { id = a3, note = "ok", now = Db.T0 }, Ct));
        Assert.Equal(2, await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM alerts WHERE dedup_key = @key AND status = 'Resolved'", new { key }));
    }

    [Fact]
    public async Task Alerts_CheckConstraints_RejectInconsistentResolvedState()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);

        // Open 卻帶 resolved_at、Resolved 卻沒有 resolved_by：皆違反 CHECK（4025）
        await Assert.ThrowsAsync<ConstraintViolationException>(() => session.ExecuteAsync("""
            INSERT INTO alerts (id, severity, reason, status, dedup_key, created_at, resolved_at, updated_at)
            VALUES (@id, 'Info', 'X', 'Open', 'k1', @now, @now, @now)
            """, new { id = Guid.NewGuid(), now = Db.T0 }, Ct));
        await Assert.ThrowsAsync<ConstraintViolationException>(() => session.ExecuteAsync("""
            INSERT INTO alerts (id, severity, reason, status, dedup_key, created_at, resolved_at, updated_at)
            VALUES (@id, 'Info', 'X', 'Resolved', 'k2', @now, @now, @now)
            """, new { id = Guid.NewGuid(), now = Db.T0 }, Ct));
    }

    [Fact]
    public async Task Equipment_UniqueNullableColumns_AllowMultipleNulls()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = DataSource(cs);
        await using var session = new DbSession(dataSource);

        var orderId = Guid.CreateVersion7();
        await Db.InsertOrderAsync(cs, orderId);

        // 多個 Job 的 assigned_equipment_id 皆為 NULL → 可並存
        var jobs = Enumerable.Range(1, 3).Select(_ => Guid.CreateVersion7()).ToArray();
        for (var i = 0; i < jobs.Length; i++)
        {
            await Db.InsertJobAsync(cs, jobs[i], orderId, sequence: i + 1);
        }

        // 多台設備的 reserved_job_id 皆為 NULL → 可並存
        var (e1, e2) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Db.InsertEquipmentAsync(cs, e1, "UQ-1");
        await Db.InsertEquipmentAsync(cs, e2, "UQ-2");
        Assert.Equal(3, await Db.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM production_jobs WHERE assigned_equipment_id IS NULL"));

        // 兩個 Job 指向同一台設備 → 第二筆 1062（預留最終保證）
        Assert.Equal(1, await session.ExecuteAsync("UPDATE production_jobs SET assigned_equipment_id = @e WHERE id = @j", new { e = e1, j = jobs[0] }, Ct));
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(() =>
            session.ExecuteAsync("UPDATE production_jobs SET assigned_equipment_id = @e WHERE id = @j", new { e = e1, j = jobs[1] }, Ct));

        // 兩台設備預留同一個 Job → 第二筆 1062
        Assert.Equal(1, await session.ExecuteAsync("UPDATE equipment SET reserved_job_id = @j WHERE id = @e", new { e = e1, j = jobs[2] }, Ct));
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(() =>
            session.ExecuteAsync("UPDATE equipment SET reserved_job_id = @j WHERE id = @e", new { e = e2, j = jobs[2] }, Ct));

        // 釋放（清為 NULL）後，同一個 Job 可被另一台設備預留
        Assert.Equal(1, await session.ExecuteAsync("UPDATE equipment SET reserved_job_id = NULL WHERE id = @e", new { e = e1 }, Ct));
        Assert.Equal(1, await session.ExecuteAsync("UPDATE equipment SET reserved_job_id = @j WHERE id = @e", new { e = e2, j = jobs[2] }, Ct));
    }

    [Fact]
    public async Task Simulator_UniqueResultEventId_AllowsMultipleNulls()
    {
        // minifactory_sim 以 mf_sim 帳號寫入：result_event_id 允許多個 NULL，同值第二筆 1062
        await using var dataSource = DataSource(fixture.SimConnectionString());
        await using var session = new DbSession(dataSource);

        const string insert = """
            INSERT INTO simulator_commands (command_id, equipment_id, job_id, attempt_no, operation, request_payload, status, result_event_id, accepted_at, updated_at)
            VALUES (@commandId, @equipmentId, @jobId, 1, 'Machining', '{}', 'Accepted', @resultEventId, @now, @now)
            """;
        object P(Guid? resultEventId) => new { commandId = Guid.CreateVersion7(), equipmentId = Guid.NewGuid(), jobId = Guid.NewGuid(), resultEventId, now = Db.T0 };

        await session.ExecuteAsync(insert, P(null), Ct);
        await session.ExecuteAsync(insert, P(null), Ct);

        var eventId = Guid.CreateVersion7();
        await session.ExecuteAsync(insert, P(eventId), Ct);
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => session.ExecuteAsync(insert, P(eventId), Ct));

        // command_id（PK）重複同樣是 1062：Simulator 據此判斷為既有命令
        var id = Guid.CreateVersion7();
        const string insertSame = """
            INSERT INTO simulator_commands (command_id, equipment_id, job_id, attempt_no, operation, request_payload, status, accepted_at, updated_at)
            VALUES (@id, @e, @j, 1, 'Machining', '{}', 'Accepted', @now, @now)
            """;
        var p = new { id, e = Guid.NewGuid(), j = Guid.NewGuid(), now = Db.T0 };
        await session.ExecuteAsync(insertSame, p, Ct);
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => session.ExecuteAsync(insertSame, p, Ct));
    }

    [Fact]
    public async Task Outbox_SkipLocked_ConcurrentBatchesDoNotOverlap()
    {
        var cs = await fixture.CreateMigratedDatabaseAsync();
        for (var i = 0; i < 6; i++)
        {
            await Db.ExecAsync(cs, """
                INSERT INTO outbox_messages (id, event_type, payload, aggregate_type, aggregate_id, occurred_at)
                VALUES (@id, 'JobDispatchRequested', '{}', 'Job', @agg, @at)
                """, new { id = Guid.CreateVersion7(), agg = Guid.NewGuid(), at = Db.T0.AddSeconds(i) });
        }

        const string pick = """
            SELECT id FROM outbox_messages
            WHERE published_at IS NULL AND dead_lettered_at IS NULL
            ORDER BY occurred_at, id
            LIMIT 3
            FOR UPDATE SKIP LOCKED
            """;

        // 兩個 Publisher 在 READ COMMITTED 交易內各取一批：互相跳過對方鎖住的列，不重複也不等待
        await using var c1 = await Db.OpenAsync(cs);
        await using var c2 = await Db.OpenAsync(cs);
        await using var t1 = await c1.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
        await using var t2 = await c2.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);

        var batch1 = await ReadIdsAsync(c1, t1, pick);
        var batch2 = await ReadIdsAsync(c2, t2, pick);

        Assert.Equal(3, batch1.Count);
        Assert.Equal(3, batch2.Count);
        Assert.Empty(batch1.Intersect(batch2));
        await t1.RollbackAsync(Ct);
        await t2.RollbackAsync(Ct);
    }

    private static async Task<List<Guid>> ReadIdsAsync(MySqlConnection conn, MySqlTransaction tx, string sql)
    {
        var ids = new List<Guid>();
        await using var cmd = new MySqlCommand(sql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
