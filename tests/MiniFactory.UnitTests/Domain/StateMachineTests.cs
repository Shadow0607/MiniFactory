using MiniFactory.Domain.Alerts;
using MiniFactory.Domain.Equipments;
using MiniFactory.Domain.Jobs;
using MiniFactory.Domain.Orders;

namespace MiniFactory.UnitTests.Domain;

// 每個狀態機：展開所有 (from, to) 組合，合法者必須通過、其餘（含相同狀態）必須被拒絕。

public class OrderStateMachineTests
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Allowed = new()
    {
        [OrderStatus.Created] = [OrderStatus.InProgress, OrderStatus.AwaitingConfirmation, OrderStatus.NeedsReview],
        [OrderStatus.InProgress] = [OrderStatus.AwaitingConfirmation, OrderStatus.Completed, OrderStatus.NeedsReview],
        [OrderStatus.AwaitingConfirmation] = [OrderStatus.InProgress, OrderStatus.Completed, OrderStatus.NeedsReview],
    };

    public static TheoryData<OrderStatus, OrderStatus, bool> Pairs => StateMachineTestData.AllPairs(Allowed);

    [Theory]
    [MemberData(nameof(Pairs))]
    public void CanTransition_AllPairs_MatchesDocumentedTable(OrderStatus from, OrderStatus to, bool expected)
    {
        Assert.Equal(expected, OrderStateMachine.CanTransition(from, to));

        var result = OrderStateMachine.TryTransition(from, to);
        Assert.Equal(expected, result.IsSuccess);
        if (expected)
        {
            Assert.Equal(to, result.Value);
        }
        else
        {
            Assert.Equal("order.invalid_transition", result.Error!.Code);
        }
    }

    [Theory]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.NeedsReview)]
    public void Terminal_HasNoOutgoingTransition(OrderStatus status)
    {
        Assert.True(OrderStateMachine.IsTerminal(status));
        Assert.Empty(OrderStateMachine.NextStates(status));
    }

    [Theory]
    [InlineData(OrderStatus.Created)]
    [InlineData(OrderStatus.InProgress)]
    [InlineData(OrderStatus.AwaitingConfirmation)]
    public void NonTerminal_HasOutgoingTransition(OrderStatus status)
        => Assert.False(OrderStateMachine.IsTerminal(status));

    [Fact]
    public void Failure_Value_ThrowsInvalidOperation()
    {
        var result = OrderStateMachine.TryTransition(OrderStatus.Completed, OrderStatus.InProgress);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }
}

public class JobStateMachineTests
{
    private static readonly Dictionary<JobStatus, JobStatus[]> Allowed = new()
    {
        [JobStatus.Pending] = [JobStatus.Ready],
        [JobStatus.Ready] = [JobStatus.Dispatched],
        [JobStatus.Dispatched] =
        [
            JobStatus.Running, JobStatus.Completed, JobStatus.AwaitingConfirmation, JobStatus.Ready, JobStatus.NeedsReview,
        ],
        [JobStatus.Running] =
        [
            JobStatus.Completed, JobStatus.AwaitingConfirmation, JobStatus.Ready, JobStatus.NeedsReview,
        ],
        [JobStatus.AwaitingConfirmation] =
        [
            JobStatus.Running, JobStatus.Completed, JobStatus.Ready, JobStatus.NeedsReview,
        ],
    };

    public static TheoryData<JobStatus, JobStatus, bool> Pairs => StateMachineTestData.AllPairs(Allowed);

    [Theory]
    [MemberData(nameof(Pairs))]
    public void CanTransition_AllPairs_MatchesDocumentedTable(JobStatus from, JobStatus to, bool expected)
    {
        Assert.Equal(expected, JobStateMachine.CanTransition(from, to));

        var result = JobStateMachine.TryTransition(from, to);
        Assert.Equal(expected, result.IsSuccess);
        if (!expected)
        {
            Assert.Equal("job.invalid_transition", result.Error!.Code);
        }
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.NeedsReview)]
    public void Terminal_CannotGoBack(JobStatus status)
    {
        Assert.True(JobStateMachine.IsTerminal(status));
        foreach (var to in Enum.GetValues<JobStatus>())
        {
            Assert.False(JobStateMachine.CanTransition(status, to));
        }
    }

    [Theory]
    [InlineData(JobStatus.Pending, false)]
    [InlineData(JobStatus.Ready, false)]
    [InlineData(JobStatus.Dispatched, true)]
    [InlineData(JobStatus.Running, true)]
    [InlineData(JobStatus.AwaitingConfirmation, true)]
    [InlineData(JobStatus.Completed, false)]
    [InlineData(JobStatus.NeedsReview, false)]
    public void HoldsEquipment_OnlyWhileActive(JobStatus status, bool expected)
        => Assert.Equal(expected, JobStateMachine.HoldsEquipment(status));
}

public class ExecutionStateMachineTests
{
    private static readonly Dictionary<ExecutionStatus, ExecutionStatus[]> Allowed = new()
    {
        [ExecutionStatus.Pending] = [ExecutionStatus.Dispatched],
        [ExecutionStatus.Dispatched] =
        [
            ExecutionStatus.Accepted, ExecutionStatus.Running, ExecutionStatus.Succeeded,
            ExecutionStatus.Failed, ExecutionStatus.Rejected, ExecutionStatus.Unknown,
        ],
        [ExecutionStatus.Accepted] =
        [
            ExecutionStatus.Running, ExecutionStatus.Succeeded, ExecutionStatus.Failed, ExecutionStatus.Unknown,
        ],
        [ExecutionStatus.Running] = [ExecutionStatus.Succeeded, ExecutionStatus.Failed, ExecutionStatus.Unknown],
        [ExecutionStatus.Unknown] =
        [
            ExecutionStatus.Accepted, ExecutionStatus.Running, ExecutionStatus.Succeeded,
            ExecutionStatus.Failed, ExecutionStatus.Rejected, ExecutionStatus.Abandoned,
        ],
    };

    public static TheoryData<ExecutionStatus, ExecutionStatus, bool> Pairs => StateMachineTestData.AllPairs(Allowed);

    [Theory]
    [MemberData(nameof(Pairs))]
    public void CanTransition_AllPairs_MatchesDocumentedTable(ExecutionStatus from, ExecutionStatus to, bool expected)
    {
        Assert.Equal(expected, ExecutionStateMachine.CanTransition(from, to));

        var result = ExecutionStateMachine.TryTransition(from, to);
        Assert.Equal(expected, result.IsSuccess);
        if (!expected)
        {
            Assert.Equal("execution.invalid_transition", result.Error!.Code);
        }
    }

    [Theory]
    [InlineData(ExecutionStatus.Succeeded)]
    [InlineData(ExecutionStatus.Failed)]
    [InlineData(ExecutionStatus.Rejected)]
    [InlineData(ExecutionStatus.Abandoned)]
    public void Terminal_CannotGoBack(ExecutionStatus status)
    {
        Assert.True(ExecutionStateMachine.IsTerminal(status));
        Assert.Empty(ExecutionStateMachine.NextStates(status));
    }

    [Theory]
    [InlineData(ExecutionStatus.Pending, false)]
    [InlineData(ExecutionStatus.Dispatched, false)]
    [InlineData(ExecutionStatus.Accepted, false)]
    [InlineData(ExecutionStatus.Running, false)]
    [InlineData(ExecutionStatus.Unknown, false)] // 結果不明：必須沿用同一 commandId
    [InlineData(ExecutionStatus.Succeeded, false)]
    [InlineData(ExecutionStatus.Failed, true)]
    [InlineData(ExecutionStatus.Rejected, true)]
    [InlineData(ExecutionStatus.Abandoned, false)] // 命令可能已執行，需人工確認
    public void AllowsNewAttempt_OnlyForConfirmedNotExecuted(ExecutionStatus status, bool expected)
        => Assert.Equal(expected, ExecutionStateMachine.AllowsNewAttempt(status));
}

public class EquipmentStateMachineTests
{
    private static readonly Dictionary<EquipmentStatus, EquipmentStatus[]> Allowed = new()
    {
        [EquipmentStatus.Idle] = [EquipmentStatus.Running, EquipmentStatus.Error, EquipmentStatus.Offline],
        [EquipmentStatus.Running] = [EquipmentStatus.Idle, EquipmentStatus.Error, EquipmentStatus.Offline],
        [EquipmentStatus.Error] = [EquipmentStatus.Idle, EquipmentStatus.Offline],
        [EquipmentStatus.Offline] = [EquipmentStatus.Idle, EquipmentStatus.Running, EquipmentStatus.Error],
    };

    public static TheoryData<EquipmentStatus, EquipmentStatus, bool> Pairs => StateMachineTestData.AllPairs(Allowed);

    [Theory]
    [MemberData(nameof(Pairs))]
    public void CanTransition_AllPairs_MatchesDocumentedTable(EquipmentStatus from, EquipmentStatus to, bool expected)
    {
        Assert.Equal(expected, EquipmentStateMachine.CanTransition(from, to));

        var result = EquipmentStateMachine.TryTransition(from, to);
        Assert.Equal(expected, result.IsSuccess);
        if (!expected)
        {
            Assert.Equal("equipment.invalid_transition", result.Error!.Code);
        }
    }

    [Fact]
    public void ErrorToRunning_IsRejected_MustRecoverViaIdle()
        => Assert.False(EquipmentStateMachine.CanTransition(EquipmentStatus.Error, EquipmentStatus.Running));
}

public class AlertStateMachineTests
{
    private static readonly Dictionary<AlertStatus, AlertStatus[]> Allowed = new()
    {
        [AlertStatus.Open] = [AlertStatus.Resolved],
    };

    public static TheoryData<AlertStatus, AlertStatus, bool> Pairs => StateMachineTestData.AllPairs(Allowed);

    [Theory]
    [MemberData(nameof(Pairs))]
    public void CanTransition_AllPairs_MatchesDocumentedTable(AlertStatus from, AlertStatus to, bool expected)
    {
        Assert.Equal(expected, AlertStateMachine.CanTransition(from, to));

        var result = AlertStateMachine.TryTransition(from, to);
        Assert.Equal(expected, result.IsSuccess);
        if (!expected)
        {
            Assert.Equal("alert.invalid_transition", result.Error!.Code);
        }
    }

    [Fact]
    public void Resolved_IsTerminal() => Assert.True(AlertStateMachine.IsTerminal(AlertStatus.Resolved));
}
