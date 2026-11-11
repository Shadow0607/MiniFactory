using MiniFactory.Domain.Jobs;

namespace MiniFactory.UnitTests.Domain;

public class ProductionJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 2, 15, 30, TimeSpan.Zero);
    private static readonly Guid EquipmentId = Guid.CreateVersion7();

    private static ProductionJob NewJob(JobStatus status = JobStatus.Ready)
        => ProductionJob.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), new ProcessStep(1, ProcessOperation.Machining), status, Now).Value;

    private static ProductionJob DispatchedJob()
    {
        var job = NewJob();
        Assert.True(job.Reserve(EquipmentId, Now).IsSuccess);
        return job;
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Ready)]
    public void Create_AllowsPendingAndReady(JobStatus status)
        => Assert.Equal(status, NewJob(status).Status);

    [Theory]
    [InlineData(JobStatus.Dispatched)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Completed)]
    public void Create_OtherInitialStatus_Fails(JobStatus status)
    {
        var result = ProductionJob.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), new ProcessStep(1, ProcessOperation.Machining), status, Now);

        Assert.Equal("job.initial_status_invalid", result.Error!.Code);
    }

    [Fact]
    public void OpenAttempt_WhenReady_CreatesIncrementingAttemptsWithNewCommandIds()
    {
        var job = NewJob();

        var first = job.OpenAttempt(Guid.CreateVersion7(), Guid.CreateVersion7(), Now).Value;
        var second = job.OpenAttempt(Guid.CreateVersion7(), Guid.CreateVersion7(), Now).Value;

        Assert.Equal(1, first.AttemptNo);
        Assert.Equal(2, second.AttemptNo);
        Assert.Equal(2, job.CurrentAttemptNo);
        Assert.NotEqual(first.CommandId, second.CommandId);
        Assert.Equal(2, job.Executions.Count);
    }

    [Fact]
    public void OpenAttempt_OnExistingJob_IncrementsVersion()
    {
        var job = NewJob();
        var before = job.Version;

        job.OpenAttempt(Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddSeconds(5));

        Assert.Equal(before + 1, job.Version);
    }

    [Fact]
    public void OpenAttempt_WhenPending_Fails()
    {
        var result = NewJob(JobStatus.Pending).OpenAttempt(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        Assert.Equal("job.attempt_requires_ready", result.Error!.Code);
    }

    [Fact]
    public void OpenAttempt_WhenDispatched_Fails()
    {
        var result = DispatchedJob().OpenAttempt(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        Assert.Equal("job.attempt_requires_ready", result.Error!.Code);
    }

    [Fact]
    public void OpenAttempt_WhenCompleted_Fails()
    {
        var job = DispatchedJob();
        job.TransitionTo(JobStatus.Completed, Now);

        var result = job.OpenAttempt(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        Assert.Equal("job.attempt_requires_ready", result.Error!.Code);
    }

    [Fact]
    public void OpenAttempt_EmptyCommandId_Fails_AndDoesNotAdvanceAttempt()
    {
        var job = NewJob();

        var result = job.OpenAttempt(Guid.CreateVersion7(), Guid.Empty, Now);

        Assert.True(result.IsFailure);
        Assert.Equal(0, job.CurrentAttemptNo);
        Assert.Empty(job.Executions);
    }

    [Fact]
    public void Reserve_FromReady_SetsDispatchedAndAssignedEquipment()
    {
        var job = NewJob();

        Assert.True(job.Reserve(EquipmentId, Now).IsSuccess);

        Assert.Equal(JobStatus.Dispatched, job.Status);
        Assert.Equal(EquipmentId, job.AssignedEquipmentId);
    }

    [Fact]
    public void Reserve_WhenPending_Fails()
    {
        var job = NewJob(JobStatus.Pending);

        var result = job.Reserve(EquipmentId, Now);

        Assert.Equal("job.invalid_transition", result.Error!.Code);
        Assert.Null(job.AssignedEquipmentId);
    }

    [Fact]
    public void Reserve_WhenAlreadyDispatched_Fails_AndKeepsOriginalAssignment()
    {
        var job = DispatchedJob();

        var result = job.Reserve(Guid.CreateVersion7(), Now);

        Assert.Equal("job.invalid_transition", result.Error!.Code);
        Assert.Equal(EquipmentId, job.AssignedEquipmentId);
    }

    [Fact]
    public void Reserve_EmptyEquipmentId_Fails()
        => Assert.Equal("job.equipment_id_required", NewJob().Reserve(Guid.Empty, Now).Error!.Code);

    [Fact]
    public void TransitionTo_Dispatched_MustUseReserve()
    {
        var job = NewJob();

        var result = job.TransitionTo(JobStatus.Dispatched, Now);

        Assert.Equal("job.reserve_required", result.Error!.Code);
        Assert.Equal(JobStatus.Ready, job.Status);
    }

    [Fact]
    public void TransitionTo_Running_SetsStartedAtOnce_AndKeepsEquipment()
    {
        var job = DispatchedJob();
        var t1 = Now.AddSeconds(5);

        Assert.True(job.TransitionTo(JobStatus.Running, t1).IsSuccess);
        Assert.Equal(t1, job.StartedAt);
        Assert.Equal(EquipmentId, job.AssignedEquipmentId);

        // 對帳後 AwaitingConfirmation → Running 不會覆寫首次開始時間
        Assert.True(job.TransitionTo(JobStatus.AwaitingConfirmation, t1.AddSeconds(1)).IsSuccess);
        Assert.Equal(EquipmentId, job.AssignedEquipmentId); // 待確認期間保留預留
        Assert.True(job.TransitionTo(JobStatus.Running, t1.AddSeconds(2)).IsSuccess);
        Assert.Equal(t1, job.StartedAt);
    }

    [Fact]
    public void TransitionTo_Completed_SetsCompletedAt_ReleasesEquipment()
    {
        var job = DispatchedJob();
        var at = Now.AddSeconds(10);

        Assert.True(job.TransitionTo(JobStatus.Completed, at).IsSuccess); // 亂序：JobCompleted 先到

        Assert.Equal(at, job.CompletedAt);
        Assert.Null(job.AssignedEquipmentId);
    }

    [Fact]
    public void TransitionTo_BackToReady_ReleasesEquipment_AndAllowsNewAttempt()
    {
        var job = NewJob();
        var first = job.OpenAttempt(Guid.CreateVersion7(), Guid.CreateVersion7(), Now).Value;
        job.Reserve(EquipmentId, Now);

        Assert.True(job.TransitionTo(JobStatus.Ready, Now).IsSuccess);
        var second = job.OpenAttempt(Guid.CreateVersion7(), Guid.CreateVersion7(), Now).Value;

        Assert.Null(job.AssignedEquipmentId);
        Assert.Equal(2, second.AttemptNo);
        Assert.NotEqual(first.CommandId, second.CommandId);
    }

    [Fact]
    public void TransitionTo_NeedsReview_ReleasesEquipment_AndIsTerminal()
    {
        var job = DispatchedJob();

        Assert.True(job.TransitionTo(JobStatus.NeedsReview, Now).IsSuccess);

        Assert.Null(job.AssignedEquipmentId);
        Assert.Null(job.CompletedAt);
        Assert.True(job.TransitionTo(JobStatus.Running, Now).IsFailure);
    }

    [Fact]
    public void TransitionTo_Illegal_DoesNotChangeStateOrVersion()
    {
        var job = NewJob(JobStatus.Pending);
        var version = job.Version;

        var result = job.TransitionTo(JobStatus.Completed, Now);

        Assert.Equal("job.invalid_transition", result.Error!.Code);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(version, job.Version);
    }

    [Fact]
    public void TransitionTo_PendingToReady_IsAllowedOnlyOnce()
    {
        var job = NewJob(JobStatus.Pending);

        Assert.True(job.TransitionTo(JobStatus.Ready, Now).IsSuccess);
        Assert.True(job.TransitionTo(JobStatus.Ready, Now).IsFailure); // 重複開放下一工序會被拒絕
    }
}
