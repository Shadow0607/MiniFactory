using System.Data;
using MiniFactory.Application.Abstractions;

namespace MiniFactory.Infrastructure.Persistence;

/// <summary>以 READ COMMITTED 開啟交易（architecture.md §10.6、§10.7.4）。每個 DI scope 一個。</summary>
public sealed class UnitOfWork(DbSession session) : IUnitOfWork
{
    public async ValueTask<IUnitOfWorkScope> BeginAsync(CancellationToken cancellationToken = default)
    {
        await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        return new Scope(session);
    }

    private sealed class Scope(DbSession session) : IUnitOfWorkScope
    {
        private bool _finished;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (_finished)
            {
                throw new InvalidOperationException("此交易範圍已結束。");
            }

            _finished = true;
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            await session.RollbackAsync().ConfigureAwait(false);
        }
    }
}
