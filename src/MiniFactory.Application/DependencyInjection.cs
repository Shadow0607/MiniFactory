using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MiniFactory.Application.Alerts;
using MiniFactory.Application.Equipments;
using MiniFactory.Application.Orders;

namespace MiniFactory.Application;

public static class DependencyInjection
{
    /// <summary>
    /// 註冊 Application 層的 use case。需要呼叫端另外提供 <see cref="IdempotencyOptions"/>（單例）；
    /// 未提供時使用預設值（保留 24 小時）。
    /// </summary>
    public static IServiceCollection AddMiniFactoryApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new IdempotencyOptions());
        services.TryAddSingleton(new OrderLimitsOptions());
        services.AddScoped<CreateOrderHandler>();
        services.AddScoped<OrderQueries>();
        services.AddScoped<EquipmentQueries>();
        services.AddScoped<AlertQueries>();
        return services;
    }
}
