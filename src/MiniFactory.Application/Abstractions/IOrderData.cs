using MiniFactory.Domain.Orders;

namespace MiniFactory.Application.Abstractions;

/// <summary>訂單聚合的寫入（architecture.md §3）。必須在 <see cref="IUnitOfWork"/> 範圍內呼叫以共用交易。</summary>
public interface IOrderData
{
    /// <summary>
    /// 新增訂單本身。違反 order_no 唯一約束時拋出 <c>UniqueConstraintViolationException</c>
    /// （主鍵為應用程式產生的 UUIDv7，不會重複，所以這裡的 1062 一定是 order_no）。
    /// </summary>
    Task InsertOrderAsync(ProductionOrder order, CancellationToken cancellationToken = default);

    /// <summary>新增訂單的所有 Job 與其 execution（建單時只有第 1 道有 attempt 1）。</summary>
    Task InsertJobsAsync(ProductionOrder order, CancellationToken cancellationToken = default);
}
