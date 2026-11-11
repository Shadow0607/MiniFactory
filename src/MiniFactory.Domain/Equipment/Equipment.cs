using MiniFactory.Domain.Common;
using MiniFactory.Domain.Jobs;

namespace MiniFactory.Domain.Equipments;

/// <summary>套用設備回報的結果。</summary>
public enum EquipmentReportOutcome
{
    /// <summary>回報時間不比目前狀態新，整筆忽略（避免舊回報覆蓋新狀態）。</summary>
    IgnoredStale,

    /// <summary>狀態相同，只更新 LastSeenAt。</summary>
    Refreshed,

    /// <summary>狀態實際改變（呼叫端需寫狀態歷史、事件、清快取）。</summary>
    StatusChanged,
}

/// <summary>
/// 設備（database.md §3.4）。Status 反映設備實際回報；是否被派工佔用另以 ReservedJobId 表示，兩者分開。
/// </summary>
public sealed class Equipment
{
    public const int MaxLineIdLength = 32;
    public const int MaxNameLength = 50;

    // 供 ORM 使用
    private Equipment()
    {
    }

    public Guid Id { get; private set; }

    public string LineId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public ProcessOperation Capability { get; private set; }

    public EquipmentStatus Status { get; private set; }

    public DateTimeOffset? LastSeenAt { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    public Guid? ReservedJobId { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>建立設備；初始為 Offline，收到第一次心跳才轉為實際狀態。</summary>
    public static Result<Equipment> Create(Guid id, string? lineId, string? name, ProcessOperation capability, DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            return Result.Failure<Equipment>(DomainError.Validation("equipment.id_required", "Equipment id is required."));
        }

        var line = lineId?.Trim();
        if (string.IsNullOrEmpty(line) || line.Length > MaxLineIdLength)
        {
            return Result.Failure<Equipment>(DomainError.Validation("equipment.line_id_invalid", $"Line id is required and at most {MaxLineIdLength} characters."));
        }

        var n = name?.Trim();
        if (string.IsNullOrEmpty(n) || n.Length > MaxNameLength)
        {
            return Result.Failure<Equipment>(DomainError.Validation("equipment.name_invalid", $"Name is required and at most {MaxNameLength} characters."));
        }

        if (!Enum.IsDefined(capability))
        {
            return Result.Failure<Equipment>(DomainError.Validation("equipment.capability_invalid", "Capability is not defined."));
        }

        return Result.Success(new Equipment
        {
            Id = id,
            LineId = line,
            Name = n,
            Capability = capability,
            Status = EquipmentStatus.Offline,
            StatusChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    /// <summary>是否適合執行指定產線與工序。</summary>
    public bool CanServe(string lineId, ProcessOperation operation)
        => string.Equals(LineId, lineId, StringComparison.Ordinal) && Capability == operation;

    /// <summary>可被預留：目前 Idle 且尚未被任何 Job 預留。</summary>
    public bool IsAvailableForReservation => Status == EquipmentStatus.Idle && ReservedJobId is null;

    /// <summary>
    /// 套用設備回報（含心跳）。occurredAt 必須晚於 StatusChangedAt 才會處理（§9.4），
    /// 否則整筆忽略。非法轉換（例如 Error → Running）回傳失敗且不改變任何欄位。
    /// </summary>
    /// <param name="occurredAt">設備端的事件時間。</param>
    /// <param name="now">本系統收到回報的時間，寫入 LastSeenAt。</param>
    public Result<EquipmentReportOutcome> ApplyReport(EquipmentStatus reported, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        if (occurredAt <= StatusChangedAt)
        {
            return Result.Success(EquipmentReportOutcome.IgnoredStale);
        }

        if (reported == Status)
        {
            LastSeenAt = now;
            UpdatedAt = now;
            return Result.Success(EquipmentReportOutcome.Refreshed);
        }

        var transition = EquipmentStateMachine.TryTransition(Status, reported);
        if (transition.IsFailure)
        {
            return Result.Failure<EquipmentReportOutcome>(transition.Error!);
        }

        Status = reported;
        StatusChangedAt = occurredAt;
        LastSeenAt = now;
        Touch(now);
        return Result.Success(EquipmentReportOutcome.StatusChanged);
    }

    /// <summary>心跳逾時：轉為 Offline（已是 Offline 則回傳失敗，不重複轉換）。</summary>
    public Result MarkOffline(DateTimeOffset now)
    {
        var transition = EquipmentStateMachine.TryTransition(Status, EquipmentStatus.Offline);
        if (transition.IsFailure)
        {
            return Result.Failure(transition.Error!);
        }

        Status = EquipmentStatus.Offline;
        StatusChangedAt = now;
        Touch(now);
        return Result.Success();
    }

    /// <summary>原子預留的 Domain 規則：必須 Idle 且未被預留。</summary>
    public Result Reserve(Guid jobId, DateTimeOffset now)
    {
        if (jobId == Guid.Empty)
        {
            return Result.Failure(DomainError.Validation("equipment.job_id_required", "Job id is required."));
        }

        if (!IsAvailableForReservation)
        {
            return Result.Failure(DomainError.Validation(
                "equipment.unavailable", $"Equipment {Name} is {Status} and reserved by {ReservedJobId?.ToString() ?? "nobody"}."));
        }

        ReservedJobId = jobId;
        Touch(now);
        return Result.Success();
    }

    /// <summary>釋放預留；只有預留者本人可以釋放。</summary>
    public Result Release(Guid jobId, DateTimeOffset now)
    {
        if (ReservedJobId != jobId)
        {
            return Result.Failure(DomainError.Validation(
                "equipment.not_reserved_by_job", $"Equipment {Name} is not reserved by job {jobId}."));
        }

        ReservedJobId = null;
        Touch(now);
        return Result.Success();
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
