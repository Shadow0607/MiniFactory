using MiniFactory.Domain.Jobs;

namespace MiniFactory.UnitTests.Domain;

public class JobExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 2, 15, 30, TimeSpan.Zero);
    private static readonly Guid EquipmentId = Guid.CreateVersion7();

    private static JobExecution Pending()
        => JobExecution.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, Guid.CreateVersion7(), Now).Value;

    private static JobExecution Dispatched()
    {
        var e = Pending();
        Assert.True(e.MarkDispatched(EquipmentId, Now.AddSeconds(2), Now).IsSuccess);
        return e;
    }

    [Fact]
    public void Create_Valid_IsPendingWithPersistedCommandId()
    {
        var commandId = Guid.CreateVersion7();

        var e = JobExecution.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, commandId, Now).Value;

        Assert.Equal(ExecutionStatus.Pending, e.Status);
        Assert.Equal(commandId, e.CommandId);
        Assert.Equal(0, e.SendCount);
    }

    [Theory]
    [InlineData(0, "execution.attempt_no_invalid")]
    [InlineData(-1, "execution.attempt_no_invalid")]
    public void Create_InvalidAttemptNo_Fails(int attemptNo, string code)
    {
        var result = JobExecution.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), attemptNo, Guid.CreateVersion7(), Now);

        Assert.Equal(code, result.Error!.Code);
    }

    [Fact]
    public void Create_EmptyIds_Fail()
    {
        Assert.Equal("execution.id_required",
            JobExecution.Create(Guid.Empty, Guid.CreateVersion7(), 1, Guid.CreateVersion7(), Now).Error!.Code);
        Assert.Equal("execution.command_id_required",
            JobExecution.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, Guid.Empty, Now).Error!.Code);
    }

    [Fact]
    public void MarkDispatched_FromPending_RecordsEquipmentAndDeadline()
    {
        var e = Pending();
        var deadline = Now.AddSeconds(3);

        Assert.True(e.MarkDispatched(EquipmentId, deadline, Now).IsSuccess);

        Assert.Equal(ExecutionStatus.Dispatched, e.Status);
        Assert.Equal(EquipmentId, e.EquipmentId);
        Assert.Equal(deadline, e.DeadlineAt);
    }

    [Fact]
    public void MarkDispatched_Twice_Fails()
        => Assert.Equal("execution.invalid_transition", Dispatched().MarkDispatched(EquipmentId, Now, Now).Error!.Code);

    [Fact]
    public void MarkDispatched_EmptyEquipment_Fails()
        => Assert.Equal("execution.equipment_id_required", Pending().MarkDispatched(Guid.Empty, Now, Now).Error!.Code);

    [Fact]
    public void RecordSend_IncrementsSendCount_KeepsCommandId()
    {
        var e = Dispatched();
        var commandId = e.CommandId;

        Assert.True(e.RecordSend(Now.AddSeconds(1), Now.AddSeconds(3)).IsSuccess);
        Assert.True(e.RecordSend(Now.AddSeconds(4), Now.AddSeconds(6)).IsSuccess);

        Assert.Equal(2, e.SendCount);
        Assert.Equal(Now.AddSeconds(4), e.LastSentAt);
        Assert.Equal(Now.AddSeconds(6), e.DeadlineAt);
        Assert.Equal(commandId, e.CommandId); // 重送沿用同一 commandId
    }

    [Fact]
    public void RecordSend_WhenPending_Fails()
        => Assert.Equal("execution.send_not_allowed", Pending().RecordSend(Now, Now).Error!.Code);

    [Fact]
    public void RecordSend_WhenTerminal_Fails()
    {
        var e = Dispatched();
        e.TransitionTo(ExecutionStatus.Succeeded, Now);

        Assert.Equal("execution.send_not_allowed", e.RecordSend(Now, Now).Error!.Code);
    }

    [Fact]
    public void TransitionTo_Accepted_SetsAcceptedAt()
    {
        var e = Dispatched();
        var at = Now.AddSeconds(1);

        Assert.True(e.TransitionTo(ExecutionStatus.Accepted, at).IsSuccess);

        Assert.Equal(at, e.AcceptedAt);
    }

    [Fact]
    public void TransitionTo_Running_SetsStartedAtOnce()
    {
        var e = Dispatched();
        e.TransitionTo(ExecutionStatus.Running, Now.AddSeconds(1));
        e.TransitionTo(ExecutionStatus.Unknown, Now.AddSeconds(2));
        e.TransitionTo(ExecutionStatus.Running, Now.AddSeconds(3));

        Assert.Equal(Now.AddSeconds(1), e.StartedAt);
    }

    [Theory]
    [InlineData(ExecutionStatus.Succeeded)]
    [InlineData(ExecutionStatus.Failed)]
    [InlineData(ExecutionStatus.Rejected)]
    public void TransitionTo_ConfirmedResult_SetsCompletedAt_ClearsDeadline_StoresErrorAndEventId(ExecutionStatus terminal)
    {
        var e = Dispatched();
        var eventId = Guid.CreateVersion7();
        var at = Now.AddSeconds(9);

        Assert.True(e.TransitionTo(terminal, at, "EQUIPMENT_BUSY", eventId).IsSuccess);

        Assert.Equal(at, e.CompletedAt);
        Assert.Null(e.DeadlineAt);
        Assert.Equal("EQUIPMENT_BUSY", e.ErrorCode);
        Assert.Equal(eventId, e.ResultEventId);
        Assert.True(e.IsTerminal);
    }

    [Fact]
    public void TransitionTo_Abandoned_DoesNotSetCompletedAt()
    {
        var e = Dispatched();
        e.TransitionTo(ExecutionStatus.Unknown, Now);

        Assert.True(e.TransitionTo(ExecutionStatus.Abandoned, Now.AddMinutes(10)).IsSuccess);

        Assert.Null(e.CompletedAt); // 結果不明，不是「完成」
        Assert.True(e.IsTerminal);
    }

    [Fact]
    public void TransitionTo_Terminal_CannotGoBack_AndKeepsFirstResult()
    {
        var e = Dispatched();
        e.TransitionTo(ExecutionStatus.Failed, Now, "SIMULATOR_RESTARTED");

        var late = e.TransitionTo(ExecutionStatus.Succeeded, Now.AddSeconds(5));

        Assert.Equal("execution.invalid_transition", late.Error!.Code);
        Assert.Equal(ExecutionStatus.Failed, e.Status);
        Assert.Equal("SIMULATOR_RESTARTED", e.ErrorCode);
    }

    [Fact]
    public void TransitionTo_Dispatched_MustUseMarkDispatched()
        => Assert.Equal("execution.dispatch_required", Pending().TransitionTo(ExecutionStatus.Dispatched, Now).Error!.Code);

    [Fact]
    public void TransitionTo_ErrorCodeTooLong_Fails_WithoutChange()
    {
        var e = Dispatched();

        var result = e.TransitionTo(ExecutionStatus.Failed, Now, new string('X', JobExecution.MaxErrorCodeLength + 1));

        Assert.Equal("execution.error_code_too_long", result.Error!.Code);
        Assert.Equal(ExecutionStatus.Dispatched, e.Status);
    }

    [Fact]
    public void SetDeadline_WhenActive_Updates_WhenTerminal_Fails()
    {
        var e = Dispatched();
        var deadline = Now.AddSeconds(30);

        Assert.True(e.SetDeadline(deadline, Now).IsSuccess);
        Assert.Equal(deadline, e.DeadlineAt);

        e.TransitionTo(ExecutionStatus.Succeeded, Now);
        Assert.Equal("execution.deadline_not_allowed", e.SetDeadline(deadline, Now).Error!.Code);
    }
}
