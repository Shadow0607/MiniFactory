using MiniFactory.Domain.Common;

namespace MiniFactory.Domain.Jobs;

/// <summary>訂單中的一道工序（database.md §3.2）。</summary>
public sealed class ProductionJob
{
    private readonly List<JobExecution> _executions = [];

    // 供 ORM 使用
    private ProductionJob()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public int Sequence { get; private set; }

    public ProcessOperation Operation { get; private set; }

    public JobStatus Status { get; private set; }

    /// <summary>預留時寫入，離開 Dispatched/Running/AwaitingConfirmation 後清空。</summary>
    public Guid? AssignedEquipmentId { get; private set; }

    public int CurrentAttemptNo { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public IReadOnlyList<JobExecution> Executions => _executions;

    /// <summary>建立 Job。第一道為 Ready，其餘為 Pending（architecture.md §8）。</summary>
    public static Result<ProductionJob> Create(Guid id, Guid orderId, ProcessStep step, JobStatus initialStatus, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (id == Guid.Empty || orderId == Guid.Empty)
        {
            return Result.Failure<ProductionJob>(DomainError.Validation("job.id_required", "Job id and order id are required."));
        }

        if (initialStatus is not (JobStatus.Pending or JobStatus.Ready))
        {
            return Result.Failure<ProductionJob>(DomainError.Validation(
                "job.initial_status_invalid", "A job can only be created as Pending or Ready."));
        }

        return Result.Success(new ProductionJob
        {
            Id = id,
            OrderId = orderId,
            Sequence = step.Sequence,
            Operation = step.Operation,
            Status = initialStatus,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    /// <summary>
    /// 在 Ready 狀態下開啟新的 attempt（含新的 commandId）。
    /// 已存在的 Job 確認 Failed/Rejected 後的重試走此方法，會遞增 version（樂觀並行）。
    /// 建單時的第 1 次 attempt 屬於「建立」本身，改走 <see cref="OpenInitialAttempt"/>。
    /// </summary>
    public Result<JobExecution> OpenAttempt(Guid executionId, Guid commandId, DateTimeOffset now)
        => OpenAttemptCore(executionId, commandId, now, touch: true);

    /// <summary>
    /// 建單當下開啟第 1 次 attempt：Job 尚未寫入資料庫，建立不算一次修改，所以 version 維持 0
    /// （與其他新建實體一致：新建實體的 version 一律為 0）。僅供 <see cref="Orders.ProductionOrder"/> 建立流程使用。
    /// </summary>
    internal Result<JobExecution> OpenInitialAttempt(Guid executionId, Guid commandId, DateTimeOffset now)
        => OpenAttemptCore(executionId, commandId, now, touch: false);

    private Result<JobExecution> OpenAttemptCore(Guid executionId, Guid commandId, DateTimeOffset now, bool touch)
    {
        if (Status != JobStatus.Ready)
        {
            return Result.Failure<JobExecution>(DomainError.Validation(
                "job.attempt_requires_ready", $"Cannot open an attempt while job is {Status}."));
        }

        var created = JobExecution.Create(executionId, Id, CurrentAttemptNo + 1, commandId, now);
        if (created.IsFailure)
        {
            return created;
        }

        _executions.Add(created.Value);
        CurrentAttemptNo = created.Value.AttemptNo;
        if (touch)
        {
            Touch(now);
        }

        return created;
    }

    /// <summary>Ready → Dispatched：原子預留設備成功後呼叫，並記錄指派的設備。</summary>
    public Result Reserve(Guid equipmentId, DateTimeOffset now)
    {
        if (equipmentId == Guid.Empty)
        {
            return Result.Failure(DomainError.Validation("job.equipment_id_required", "Equipment id is required."));
        }

        var transition = JobStateMachine.TryTransition(Status, JobStatus.Dispatched);
        if (transition.IsFailure)
        {
            return Result.Failure(transition.Error!);
        }

        Status = JobStatus.Dispatched;
        AssignedEquipmentId = equipmentId;
        Touch(now);
        return Result.Success();
    }

    /// <summary>
    /// 其他狀態轉換。Dispatched 必須經 <see cref="Reserve"/>（確保 Dispatched 一定有指派設備）。
    /// 離開持有設備的狀態時自動清空 AssignedEquipmentId。
    /// </summary>
    public Result TransitionTo(JobStatus to, DateTimeOffset now)
    {
        if (to == JobStatus.Dispatched)
        {
            return Result.Failure(DomainError.Validation(
                "job.reserve_required", "Use Reserve to move a job to Dispatched."));
        }

        var transition = JobStateMachine.TryTransition(Status, to);
        if (transition.IsFailure)
        {
            return Result.Failure(transition.Error!);
        }

        Status = to;
        if (to == JobStatus.Running)
        {
            StartedAt ??= now;
        }

        if (to == JobStatus.Completed)
        {
            CompletedAt = now;
        }

        if (!JobStateMachine.HoldsEquipment(to))
        {
            AssignedEquipmentId = null;
        }

        Touch(now);
        return Result.Success();
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
