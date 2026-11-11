using MiniFactory.Domain.Common;

namespace MiniFactory.Domain.Alerts;

/// <summary>告警（database.md §3.7）。同一 DedupKey 同時只會有一筆 Open（由 DB 部分唯一索引保證）。</summary>
public sealed class Alert
{
    public const int MaxMessageLength = 500;
    public const int MaxDedupKeyLength = 200;

    // 供 ORM 使用
    private Alert()
    {
    }

    public Guid Id { get; private set; }

    public Guid? OrderId { get; private set; }

    public Guid? JobId { get; private set; }

    public Guid? EquipmentId { get; private set; }

    public AlertSeverity Severity { get; private set; }

    /// <summary>原因代碼（如 EQUIPMENT_OFFLINE）；不加 CHECK，方便擴充。</summary>
    public string Reason { get; private set; } = string.Empty;

    public string? Message { get; private set; }

    public AlertStatus Status { get; private set; }

    public string DedupKey { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public ResolvedBy? ResolvedBy { get; private set; }

    public string? ResolutionNote { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>組出去重鍵，例：EQUIPMENT_OFFLINE:{equipmentId}。</summary>
    public static string BuildDedupKey(AlertReason reason, string subject) => $"{reason.ToCode()}:{subject}";

    /// <param name="dedupSubject">去重主體（設備 ID、Job ID 等），與原因組成 DedupKey。</param>
    /// <param name="severity">未指定時採用該原因的預設嚴重度。</param>
    public static Result<Alert> Create(
        Guid id,
        AlertReason reason,
        string? dedupSubject,
        string? message,
        Guid? orderId,
        Guid? jobId,
        Guid? equipmentId,
        DateTimeOffset now,
        AlertSeverity? severity = null)
    {
        if (id == Guid.Empty)
        {
            return Result.Failure<Alert>(DomainError.Validation("alert.id_required", "Alert id is required."));
        }

        if (!Enum.IsDefined(reason))
        {
            return Result.Failure<Alert>(DomainError.Validation("alert.reason_invalid", "Alert reason is not defined."));
        }

        var subject = dedupSubject?.Trim();
        if (string.IsNullOrEmpty(subject))
        {
            return Result.Failure<Alert>(DomainError.Validation("alert.dedup_subject_required", "Dedup subject is required."));
        }

        var dedupKey = BuildDedupKey(reason, subject);
        if (dedupKey.Length > MaxDedupKeyLength)
        {
            return Result.Failure<Alert>(DomainError.Validation("alert.dedup_key_too_long", $"Dedup key exceeds {MaxDedupKeyLength} characters."));
        }

        if (message is { Length: > MaxMessageLength })
        {
            return Result.Failure<Alert>(DomainError.Validation("alert.message_too_long", $"Message exceeds {MaxMessageLength} characters."));
        }

        if (severity is { } s && !Enum.IsDefined(s))
        {
            return Result.Failure<Alert>(DomainError.Validation("alert.severity_invalid", "Alert severity is not defined."));
        }

        return Result.Success(new Alert
        {
            Id = id,
            OrderId = orderId,
            JobId = jobId,
            EquipmentId = equipmentId,
            Severity = severity ?? reason.DefaultSeverity(),
            Reason = reason.ToCode(),
            Message = message,
            Status = AlertStatus.Open,
            DedupKey = dedupKey,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    /// <summary>
    /// 結案（系統自動或人工）。已結案者回傳 alert.invalid_transition，
    /// 呼叫端（Application）視為冪等並保留第一次的結案資訊。結案只關閉告警，不影響 Job / Order / 設備。
    /// </summary>
    public Result Resolve(ResolvedBy by, string? note, DateTimeOffset now)
    {
        if (!Enum.IsDefined(by))
        {
            return Result.Failure(DomainError.Validation("alert.resolved_by_invalid", "ResolvedBy is not defined."));
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is { Length: > MaxMessageLength })
        {
            return Result.Failure(DomainError.Validation("alert.note_too_long", $"Resolution note exceeds {MaxMessageLength} characters."));
        }

        var transition = AlertStateMachine.TryTransition(Status, AlertStatus.Resolved);
        if (transition.IsFailure)
        {
            return Result.Failure(transition.Error!);
        }

        Status = AlertStatus.Resolved;
        ResolvedAt = now;
        ResolvedBy = by;
        ResolutionNote = trimmedNote;
        UpdatedAt = now;
        return Result.Success();
    }
}
