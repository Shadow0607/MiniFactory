namespace MiniFactory.Application.Abstractions;

/// <summary>
/// 訊息代理（RabbitMQ）連線狀態，決定建單回應的 <c>dispatchStatus</c>（architecture.md §8）：
/// 連線正常 → Queued；斷線 → Pending（事件暫存 Outbox，恢復後補送）。
/// </summary>
public interface IBrokerStatus
{
    bool IsConnected { get; }
}
