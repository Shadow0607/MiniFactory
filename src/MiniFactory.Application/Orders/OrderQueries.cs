using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Common;
using MiniFactory.Application.Errors;
using MiniFactory.Domain.Orders;

namespace MiniFactory.Application.Orders;

/// <summary>訂單相關查詢：驗證查詢參數後交給 <see cref="IDashboardQueries"/>（api.md §3.2～3.4）。</summary>
public sealed class OrderQueries(IDashboardQueries queries)
{
    public const int MaxOrderNoPrefixLength = ProductionOrder.MaxOrderNoLength;

    /// <param name="statuses">可多值：<c>status=InProgress&amp;status=AwaitingConfirmation</c>。</param>
    public async Task<PagedResult<OrderSummaryDto>> ListAsync(
        IReadOnlyCollection<string>? statuses,
        string? orderNoPrefix,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var parsedStatuses = new List<OrderStatus>();
        foreach (var raw in statuses ?? [])
        {
            if (errors.ParseEnum<OrderStatus>("status", raw) is { } status && !parsedStatuses.Contains(status))
            {
                parsedStatuses.Add(status);
            }
        }

        var prefix = string.IsNullOrWhiteSpace(orderNoPrefix) ? null : orderNoPrefix.Trim();
        if (prefix is { Length: > MaxOrderNoPrefixLength })
        {
            errors.Add("orderNo", $"orderNo 長度不可超過 {MaxOrderNoPrefixLength} 字");
        }

        var (p, s) = Paging.Normalize(page, pageSize, errors);
        errors.ThrowIfAny();

        return await queries.ListOrdersAsync(new OrderListFilter(parsedStatuses, prefix), p, s, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OrderDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => await queries.GetOrderAsync(id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("訂單", id.ToString());

    /// <param name="after">增量查詢：只回傳排在該事件之後的事件。</param>
    public async Task<OrderEventsResponse> GetEventsAsync(Guid orderId, Guid? after, CancellationToken cancellationToken = default)
    {
        if (!await queries.OrderExistsAsync(orderId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("訂單", orderId.ToString());
        }

        var items = await queries.ListOrderEventsAsync(orderId, after, cancellationToken).ConfigureAwait(false);
        return new OrderEventsResponse(orderId, items);
    }
}
