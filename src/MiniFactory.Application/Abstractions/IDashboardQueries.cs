using MiniFactory.Application.Alerts;
using MiniFactory.Application.Common;
using MiniFactory.Application.Equipments;
using MiniFactory.Application.Orders;

namespace MiniFactory.Application.Abstractions;

/// <summary>Dashboard 用的唯讀查詢（architecture.md §3）。參數已由 Application 驗證完畢。</summary>
public interface IDashboardQueries
{
    Task<PagedResult<OrderSummaryDto>> ListOrdersAsync(OrderListFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>訂單不存在回傳 null。</summary>
    Task<OrderDetailDto?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<bool> OrderExistsAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 事件時間線，依 occurredAt、id 遞增。<paramref name="afterEventId"/> 有值時只回傳排在該事件之後的事件；
    /// 該事件不存在於此訂單時拋出 <see cref="Errors.NotFoundException"/>。
    /// </summary>
    Task<IReadOnlyList<OrderEventDto>> ListOrderEventsAsync(Guid orderId, Guid? afterEventId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EquipmentDto>> ListEquipmentAsync(string? lineId, CancellationToken cancellationToken = default);

    Task<AlertListResponse> ListAlertsAsync(AlertListFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}
