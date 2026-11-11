using MiniFactory.Domain.Alerts;
using MiniFactory.Domain.Equipments;
using MiniFactory.Domain.Jobs;
using MiniFactory.Domain.Orders;

namespace MiniFactory.UnitTests.Domain;

/// <summary>
/// 列舉名稱即 DB 儲存字串。此處固定 database.md 的 CHECK 清單，
/// 任何一邊（enum 或文件）單獨改動都會讓測試失敗，提醒同步遷移。
/// </summary>
public class StatusValuesTests
{
    private static void AssertNames<T>(params string[] expected) where T : struct, Enum
        => Assert.Equal(expected.Order(StringComparer.Ordinal), Enum.GetNames<T>().Order(StringComparer.Ordinal));

    [Fact]
    public void OrderStatus_MatchesCheckConstraint()
        => AssertNames<OrderStatus>("Created", "InProgress", "AwaitingConfirmation", "Completed", "NeedsReview");

    [Fact]
    public void JobStatus_MatchesCheckConstraint()
        => AssertNames<JobStatus>("Pending", "Ready", "Dispatched", "Running", "AwaitingConfirmation", "Completed", "NeedsReview");

    [Fact]
    public void ExecutionStatus_MatchesCheckConstraint()
        => AssertNames<ExecutionStatus>("Pending", "Dispatched", "Accepted", "Running", "Unknown", "Succeeded", "Failed", "Rejected", "Abandoned");

    [Fact]
    public void EquipmentStatus_MatchesCheckConstraint()
        => AssertNames<EquipmentStatus>("Idle", "Running", "Error", "Offline");

    [Fact]
    public void ProcessOperation_MatchesCheckConstraint()
        => AssertNames<ProcessOperation>("Machining", "Assembly", "Inspection");

    [Fact]
    public void StatusChangeSource_MatchesCheckConstraint()
        => AssertNames<StatusChangeSource>("Report", "Heartbeat", "HeartbeatTimeout");

    [Fact]
    public void AlertSeverity_MatchesCheckConstraint()
        => AssertNames<AlertSeverity>("Info", "Warning", "Critical");

    [Fact]
    public void AlertStatus_MatchesCheckConstraint()
        => AssertNames<AlertStatus>("Open", "Resolved");

    [Fact]
    public void ResolvedBy_MatchesCheckConstraint()
        => AssertNames<ResolvedBy>("System", "Operator");
}
