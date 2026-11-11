using MiniFactory.Domain.Common;

namespace MiniFactory.Domain.Jobs;

/// <summary>
/// Job 的一次執行嘗試（database.md §3.3）。CommandId 在建立時即產生並持久化，
/// 之後任何重送都沿用同一個 CommandId；只有確認 Failed/Rejected 才由 Job 開新 attempt。
/// </summary>
public sealed class JobExecution
{
    public const int MaxErrorCodeLength = 64;

    // 供 ORM 使用
    private JobExecution()
    {
    }

    public Guid Id { get; private set; }

    public Guid JobId { get; private set; }

    public int AttemptNo { get; private set; }

    public Guid CommandId { get; private set; }

    public ExecutionStatus Status { get; private set; }

    public Guid? EquipmentId { get; private set; }

    public int SendCount { get; private set; }

    public DateTimeOffset? LastSentAt { get; private set; }

    public DateTimeOffset? DeadlineAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? ErrorCode { get; private set; }

    public Guid? ResultEventId { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsTerminal => ExecutionStateMachine.IsTerminal(Status);

    public static Result<JobExecution> Create(Guid id, Guid jobId, int attemptNo, Guid commandId, DateTimeOffset now)
    {
        if (id == Guid.Empty || jobId == Guid.Empty)
        {
            return Result.Failure<JobExecution>(DomainError.Validation("execution.id_required", "Execution id and job id are required."));
        }

        if (commandId == Guid.Empty)
        {
            return Result.Failure<JobExecution>(DomainError.Validation("execution.command_id_required", "Command id is required."));
        }

        if (attemptNo < 1)
        {
            return Result.Failure<JobExecution>(DomainError.Validation("execution.attempt_no_invalid", "Attempt number must be >= 1."));
        }

        return Result.Success(new JobExecution
        {
            Id = id,
            JobId = jobId,
            AttemptNo = attemptNo,
            CommandId = commandId,
            Status = ExecutionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    /// <summary>Pending → Dispatched：已預留設備，記錄設備與 ack 期限。</summary>
    public Result MarkDispatched(Guid equipmentId, DateTimeOffset deadlineAt, DateTimeOffset now)
    {
        if (equipmentId == Guid.Empty)
        {
            return Result.Failure(DomainError.Validation("execution.equipment_id_required", "Equipment id is required."));
        }

        var transition = ExecutionStateMachine.TryTransition(Status, ExecutionStatus.Dispatched);
        if (transition.IsFailure)
        {
            return Result.Failure(transition.Error!);
        }

        Status = ExecutionStatus.Dispatched;
        EquipmentId = equipmentId;
        DeadlineAt = deadlineAt;
        Touch(now);
        return Result.Success();
    }

    /// <summary>
    /// 記錄一次命令送出（同一 CommandId 的首送或重送）。
    /// 只有已派出且未終結的執行才能送出命令。
    /// </summary>
    public Result RecordSend(DateTimeOffset now, DateTimeOffset nextDeadlineAt)
    {
        if (Status == ExecutionStatus.Pending || IsTerminal)
        {
            return Result.Failure(DomainError.Validation(
                "execution.send_not_allowed", $"Cannot send command while execution is {Status}."));
        }

        SendCount++;
        LastSentAt = now;
        DeadlineAt = nextDeadlineAt;
        Touch(now);
        return Result.Success();
    }

    /// <summary>調整下一次檢查（重送 / 查詢）時間；終態不可再設定。</summary>
    public Result SetDeadline(DateTimeOffset? deadlineAt, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return Result.Failure(DomainError.Validation(
                "execution.deadline_not_allowed", $"Cannot set deadline while execution is {Status}."));
        }

        DeadlineAt = deadlineAt;
        Touch(now);
        return Result.Success();
    }

    /// <summary>
    /// 一般狀態轉換（Accepted / Running / Unknown / 終態）。Pending → Dispatched 請用 <see cref="MarkDispatched"/>。
    /// </summary>
    public Result TransitionTo(ExecutionStatus to, DateTimeOffset now, string? errorCode = null, Guid? resultEventId = null)
    {
        if (to == ExecutionStatus.Dispatched)
        {
            return Result.Failure(DomainError.Validation(
                "execution.dispatch_required", "Use MarkDispatched to move an execution to Dispatched."));
        }

        if (errorCode is { Length: > MaxErrorCodeLength })
        {
            return Result.Failure(DomainError.Validation(
                "execution.error_code_too_long", $"Error code exceeds {MaxErrorCodeLength} characters."));
        }

        var transition = ExecutionStateMachine.TryTransition(Status, to);
        if (transition.IsFailure)
        {
            return Result.Failure(transition.Error!);
        }

        Status = to;
        switch (to)
        {
            case ExecutionStatus.Accepted:
                AcceptedAt ??= now;
                break;
            case ExecutionStatus.Running:
                StartedAt ??= now;
                break;
            case ExecutionStatus.Succeeded or ExecutionStatus.Failed or ExecutionStatus.Rejected:
                CompletedAt = now;
                break;
        }

        if (errorCode is not null)
        {
            ErrorCode = errorCode;
        }

        if (resultEventId is not null)
        {
            ResultEventId = resultEventId;
        }

        // 終態不再需要被對帳掃描
        if (IsTerminal)
        {
            DeadlineAt = null;
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
