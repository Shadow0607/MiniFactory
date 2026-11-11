using MiniFactory.Application.Abstractions;

namespace MiniFactory.Infrastructure.Messaging;

/// <summary>
/// Phase 1 的 <see cref="IBrokerStatus"/>：API 尚未持有 RabbitMQ 連線（Phase 2.3 才加入），
/// 事件只寫進 Outbox、沒有人發送，因此誠實回報「未連線」→ 建單回應 <c>dispatchStatus: Pending</c>。
/// 接上 RabbitMQ 後以真正的連線狀態實作取代。
/// </summary>
public sealed class NotConnectedBrokerStatus : IBrokerStatus
{
    public bool IsConnected => false;
}
