using System.Data;
using Dapper;
using MiniFactory.Application.Persistence;
using MySqlConnector;

namespace MiniFactory.Infrastructure.Persistence;

/// <summary>
/// 每個 DI scope 一個：延遲開啟 <see cref="MySqlConnection"/>，持有目前的 <see cref="MySqlTransaction"/>
/// （architecture.md §10.7.1）。所有資料存取類別都透過它執行 SQL，所以同一 UoW 內自然同交易。
/// 執行 SQL 時把 <see cref="MySqlException"/> 轉成 Application 例外，讓呼叫端依「哪一個語句失敗」判斷約束。
/// 參數經 <see cref="DbParameters.Prepare"/> 正規化（enum 轉字串、DateTime 轉 UTC）。
/// 非 thread-safe：一個 scope 同時只能有一個執行緒使用（與 ADO.NET 連線的限制相同）。
/// </summary>
public sealed class DbSession(MySqlDataSource dataSource) : IAsyncDisposable
{
    private MySqlConnection? _connection;

    /// <summary>目前進行中的交易；沒有則為 null（此時每個語句各自 auto-commit）。</summary>
    public MySqlTransaction? Transaction { get; private set; }

    public bool HasActiveTransaction => Transaction is not null || _readOnlySnapshotActive;

    // 以原生 SQL 開啟的唯讀快照交易（沒有對應的 MySqlTransaction 物件）
    private bool _readOnlySnapshotActive;

    /// <summary>取得（必要時開啟）連線。連線失敗會轉成 <c>DatabaseUnavailableException</c>。</summary>
    public async ValueTask<MySqlConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        try
        {
            _connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        catch (MySqlException ex) when (MySqlErrorTranslator.Translate(ex) is not null)
        {
            throw MySqlErrorTranslator.Translate(ex)!;
        }
    }

    internal async Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        // 唯讀快照也算進行中的交易：MariaDB 在交易中執行 START TRANSACTION 會隱含提交目前交易，
        // 若放行，快照結束的 COMMIT 會把寫入交易提前提交、之後的 ROLLBACK 失效
        if (HasActiveTransaction)
        {
            throw new InvalidOperationException("DbSession 已有進行中的交易或唯讀快照，不支援巢狀交易。");
        }

        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (MySqlErrorTranslator.Translate(ex) is not null)
        {
            throw MySqlErrorTranslator.Translate(ex)!;
        }
    }

    /// <summary>
    /// 開啟唯讀一致性快照交易（REPEATABLE READ + <c>WITH CONSISTENT SNAPSHOT</c> + <c>READ ONLY</c>）。
    /// 用途：一個畫面需要多個查詢拼成同一份資料時（例如訂單詳情的訂單 / Job / execution），
    /// 在預設 autocommit 下每個查詢各自看到「當下」的資料，查詢之間若有寫入提交，會拼出不一致的畫面
    /// （例如 Job 已 Completed 但訂單仍是 InProgress）。快照交易讓這些查詢都讀同一個時間點。
    /// READ ONLY 讓伺服器拒絕任何寫入，並省去分配交易 ID 的成本。範圍結束時結束交易（沒有東西需要提交）。
    /// 不可與一般寫入交易並存（不支援巢狀）。
    /// </summary>
    public async ValueTask<IAsyncDisposable> BeginReadOnlySnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (HasActiveTransaction)
        {
            throw new InvalidOperationException("DbSession 已有進行中的交易，不支援巢狀交易。");
        }

        // 兩個語句合併成同一次往返（MySqlConnector 預設允許單一 command 多語句）。
        // 注意：SET TRANSACTION（不加 SESSION）只影響「下一個」交易，不會改變連線的預設隔離等級
        await ExecuteAsync(
            "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ; START TRANSACTION WITH CONSISTENT SNAPSHOT, READ ONLY",
            null, cancellationToken).ConfigureAwait(false);
        _readOnlySnapshotActive = true;
        return new SnapshotScope(this);
    }

    private async ValueTask EndReadOnlySnapshotAsync()
    {
        if (!_readOnlySnapshotActive)
        {
            return;
        }

        _readOnlySnapshotActive = false;
        try
        {
            // 唯讀交易沒有東西可提交；COMMIT 與 ROLLBACK 效果相同，用 COMMIT 較便宜
            await ExecuteAsync("COMMIT").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DatabaseUnavailableException or TransientDbException
                                       or MySqlException or InvalidOperationException or IOException)
        {
            // 連線已斷：伺服器端交易必定已結束；丟棄連線，下次使用時重新取得
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
    }

    private sealed class SnapshotScope(DbSession session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => session.EndReadOnlySnapshotAsync();
    }

    internal async Task CommitAsync(CancellationToken cancellationToken)
    {
        var tx = Transaction ?? throw new InvalidOperationException("沒有進行中的交易可提交。");
        try
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (MySqlErrorTranslator.Translate(ex) is not null)
        {
            throw MySqlErrorTranslator.Translate(ex)!;
        }
        finally
        {
            await EndTransactionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>回滾並結束交易；回滾本身失敗（例如連線已斷）時吞掉，因為此時伺服器端必定已回滾。</summary>
    internal async Task RollbackAsync()
    {
        var tx = Transaction;
        if (tx is null)
        {
            return;
        }

        try
        {
            await tx.RollbackAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException or IOException)
        {
            // 連線已斷或伺服器已回滾（例如 1213）：不需處理
        }
        finally
        {
            await EndTransactionAsync().ConfigureAwait(false);
        }
    }

    private async Task EndTransactionAsync()
    {
        var tx = Transaction;
        Transaction = null;
        if (tx is not null)
        {
            await tx.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- Dapper 包裝：自動帶入目前交易，並翻譯錯誤碼 ----

    public Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken cancellationToken = default)
        => RunAsync((c, t) => c.ExecuteAsync(new CommandDefinition(sql, DbParameters.Prepare(param), t, cancellationToken: cancellationToken)), cancellationToken);

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken cancellationToken = default)
        => RunAsync((c, t) => c.QueryAsync<T>(new CommandDefinition(sql, DbParameters.Prepare(param), t, cancellationToken: cancellationToken)), cancellationToken);

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null, CancellationToken cancellationToken = default)
        => RunAsync((c, t) => c.QuerySingleOrDefaultAsync<T>(new CommandDefinition(sql, DbParameters.Prepare(param), t, cancellationToken: cancellationToken)), cancellationToken);

    public Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null, CancellationToken cancellationToken = default)
        => RunAsync((c, t) => c.ExecuteScalarAsync<T>(new CommandDefinition(sql, DbParameters.Prepare(param), t, cancellationToken: cancellationToken)), cancellationToken);

    private async Task<TResult> RunAsync<TResult>(
        Func<MySqlConnection, MySqlTransaction?, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(connection, Transaction).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (MySqlErrorTranslator.Translate(ex) is not null)
        {
            throw MySqlErrorTranslator.Translate(ex)!;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RollbackAsync().ConfigureAwait(false);
        await EndReadOnlySnapshotAsync().ConfigureAwait(false);
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}
