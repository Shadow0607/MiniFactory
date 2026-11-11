using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MiniFactory.Application.Abstractions;
using MiniFactory.Infrastructure.Messaging;
using MiniFactory.Infrastructure.Persistence;
using MiniFactory.Infrastructure.Persistence.Data;
using MiniFactory.Infrastructure.Persistence.TypeHandlers;
using MySqlConnector;

namespace MiniFactory.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// 註冊資料存取基礎：<see cref="MySqlDataSource"/>（單例，內含連線池）、scoped 的 <see cref="DbSession"/> /
    /// <see cref="IUnitOfWork"/>，以及 <see cref="ITransactionRunner"/>；並註冊各聚合的資料存取類別（scoped）。
    /// 連線字串由呼叫端從設定 / 環境變數取得。
    /// </summary>
    public static IServiceCollection AddMiniFactoryPersistence(
        this IServiceCollection services,
        string connectionString,
        Action<TransactionRetryOptions>? configureRetry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return services.AddMiniFactoryPersistence(_ => connectionString, configureRetry);
    }

    /// <summary>
    /// 同上，但連線字串延遲到第一次需要資料庫時才取得（此時設定已完整載入，例如整合測試覆寫的設定值）。
    /// </summary>
    public static IServiceCollection AddMiniFactoryPersistence(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionStringFactory,
        Action<TransactionRetryOptions>? configureRetry = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionStringFactory);
        DapperTypeHandlers.Register();

        services.TryAddSingleton(sp =>
            new MySqlDataSourceBuilder(DbConnectionString.Normalize(connectionStringFactory(sp))).Build());

        var retry = new TransactionRetryOptions();
        configureRetry?.Invoke(retry);
        services.TryAddSingleton(retry);

        services.TryAddScoped<DbSession>();
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddScoped<ITransactionRunner, TransactionRunner>();

        // 各聚合的資料存取類別：共用同一個 scoped DbSession，所以同一 UoW 內自然同交易（architecture.md §10.7.1）
        services.TryAddScoped<IOrderData, OrderData>();
        services.TryAddScoped<IMessagingData, MessagingData>();
        services.TryAddScoped<IEquipmentData, EquipmentData>();
        services.TryAddScoped<IDashboardQueries, DashboardQueries>();

        // Phase 2.3 接上 RabbitMQ 後改註冊真正的實作
        services.TryAddSingleton<IBrokerStatus, NotConnectedBrokerStatus>();
        return services;
    }
}
