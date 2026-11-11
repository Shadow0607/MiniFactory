using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Messaging;
using MiniFactory.Application.Persistence;
using MiniFactory.Domain.Orders;

namespace MiniFactory.UnitTests.Application;

// Application 只相依介面，所以單元測試用手寫的記憶體假物件（不引入 mock 套件）。
// SQL 本身的正確性由整合測試（Testcontainers MariaDB）驗證。

/// <summary>固定時間，可手動前進。</summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>記錄所有呼叫順序，方便斷言「idempotency 在訂單之前」「失敗時沒有提交」。</summary>
public sealed class CallLog
{
    public List<string> Calls { get; } = [];
}

public sealed class FakeUnitOfWork(CallLog log) : IUnitOfWork
{
    public int Begun { get; private set; }

    public int Committed { get; private set; }

    public int RolledBack { get; private set; }

    public ValueTask<IUnitOfWorkScope> BeginAsync(CancellationToken cancellationToken = default)
    {
        Begun++;
        log.Calls.Add("begin");
        return ValueTask.FromResult<IUnitOfWorkScope>(new Scope(this, log));
    }

    private sealed class Scope(FakeUnitOfWork owner, CallLog log) : IUnitOfWorkScope
    {
        private bool _finished;

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            _finished = true;
            owner.Committed++;
            log.Calls.Add("commit");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_finished)
            {
                owner.RolledBack++;
                log.Calls.Add("rollback");
            }

            return ValueTask.CompletedTask;
        }
    }
}

public sealed class PassThroughTransactionRunner : ITransactionRunner
{
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default) => work(cancellationToken);

    public Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default) => work(cancellationToken);
}

public sealed class FakeOrderData(CallLog log) : IOrderData
{
    public List<ProductionOrder> Orders { get; } = [];

    public bool ThrowDuplicateOrderNo { get; set; }

    public Task InsertOrderAsync(ProductionOrder order, CancellationToken cancellationToken = default)
    {
        log.Calls.Add("order");
        if (ThrowDuplicateOrderNo)
        {
            throw new UniqueConstraintViolationException(1062, "Duplicate entry for key 'ux_production_orders_order_no'");
        }

        Orders.Add(order);
        return Task.CompletedTask;
    }

    public Task InsertJobsAsync(ProductionOrder order, CancellationToken cancellationToken = default)
    {
        log.Calls.Add("jobs");
        return Task.CompletedTask;
    }
}

public sealed class FakeMessagingData(CallLog log) : IMessagingData
{
    public Dictionary<(string Scope, string Key), IdempotencyRecord> Keys { get; } = [];

    public List<DomainEventRecord> Events { get; } = [];

    public Task InsertIdempotencyKeyAsync(IdempotencyRecord record, CancellationToken cancellationToken = default)
    {
        log.Calls.Add("idempotency");
        if (Keys.ContainsKey((record.Scope, record.Key)))
        {
            throw new UniqueConstraintViolationException(1062, "Duplicate entry for key 'PRIMARY'");
        }

        Keys[(record.Scope, record.Key)] = record;
        return Task.CompletedTask;
    }

    public Task<IdempotencyRecord?> GetIdempotencyKeyAsync(string scope, string key, CancellationToken cancellationToken = default)
        => Task.FromResult(Keys.GetValueOrDefault((scope, key)));

    public Task<bool> DeleteExpiredIdempotencyKeyAsync(string scope, string key, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (Keys.TryGetValue((scope, key), out var existing) && existing.ExpiresAt <= now)
        {
            Keys.Remove((scope, key));
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task AppendOrderEventAsync(DomainEventRecord record, CancellationToken cancellationToken = default)
    {
        log.Calls.Add("event:" + record.EventType);
        Events.Add(record);
        return Task.CompletedTask;
    }
}

public sealed class FakeEquipmentData(params string[] lines) : IEquipmentData
{
    public Task<string?> FindLineIdAsync(string lineId, CancellationToken cancellationToken = default)
        => Task.FromResult(lines.FirstOrDefault(l => string.Equals(l, lineId, StringComparison.OrdinalIgnoreCase)));
}

public sealed class FakeBrokerStatus(bool connected) : IBrokerStatus
{
    public bool IsConnected { get; set; } = connected;
}
