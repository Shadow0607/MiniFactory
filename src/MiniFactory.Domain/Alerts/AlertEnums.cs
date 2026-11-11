namespace MiniFactory.Domain.Alerts;

/// <summary>告警嚴重度（database.md §3.7；值域【待確認】）。</summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>告警狀態（architecture.md §9.5）。</summary>
public enum AlertStatus
{
    Open,
    Resolved,
}

/// <summary>結案者：系統自動結案或操作員人工結案。</summary>
public enum ResolvedBy
{
    System,
    Operator,
}

/// <summary>告警原因（architecture.md §9.5 表）。DB 與事件中以 <see cref="AlertReasonExtensions.ToCode"/> 的字串儲存。</summary>
public enum AlertReason
{
    EquipmentOffline,
    EquipmentError,
    JobUnconfirmed,
    JobNeedsReview,
    OutboxPublishFailing,
}

public static class AlertReasonExtensions
{
    /// <summary>DB / 事件使用的 UPPER_SNAKE_CASE 代碼。</summary>
    public static string ToCode(this AlertReason reason) => reason switch
    {
        AlertReason.EquipmentOffline => "EQUIPMENT_OFFLINE",
        AlertReason.EquipmentError => "EQUIPMENT_ERROR",
        AlertReason.JobUnconfirmed => "JOB_UNCONFIRMED",
        AlertReason.JobNeedsReview => "JOB_NEEDS_REVIEW",
        AlertReason.OutboxPublishFailing => "OUTBOX_PUBLISH_FAILING",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>各原因的預設嚴重度。</summary>
    public static AlertSeverity DefaultSeverity(this AlertReason reason) => reason switch
    {
        AlertReason.EquipmentOffline => AlertSeverity.Critical,
        AlertReason.EquipmentError => AlertSeverity.Warning,
        AlertReason.JobUnconfirmed => AlertSeverity.Warning,
        AlertReason.JobNeedsReview => AlertSeverity.Critical,
        AlertReason.OutboxPublishFailing => AlertSeverity.Warning,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>該原因是否會由系統自動結案（JOB_NEEDS_REVIEW 不會，只能人工結案）。</summary>
    public static bool IsAutoResolvable(this AlertReason reason) => reason != AlertReason.JobNeedsReview;
}
