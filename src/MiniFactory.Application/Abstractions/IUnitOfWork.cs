namespace MiniFactory.Application.Abstractions;

/// <summary>
/// Unit of Work（architecture.md §10.7.1）。
/// <see cref="BeginAsync"/> 以 READ COMMITTED 開啟交易；同一 DI scope 內所有資料存取類別共用同一個交易，
/// 所以業務表、order_events、outbox_messages、inbox_messages、idempotency_keys 會在同一交易提交或回滾。
/// SignalR 推播、Redis 寫入、HTTP 呼叫一律放在 <see cref="IUnitOfWorkScope.CommitAsync"/> 之後。
/// </summary>
public interface IUnitOfWork
{
    /// <summary>開始交易。同一個 scope 內不支援巢狀交易（已有進行中交易時拋 <see cref="InvalidOperationException"/>）。</summary>
    ValueTask<IUnitOfWorkScope> BeginAsync(CancellationToken cancellationToken = default);
}

/// <summary>一次交易的範圍。<see cref="IAsyncDisposable.DisposeAsync"/> 時若尚未提交則回滾。</summary>
public interface IUnitOfWorkScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}
