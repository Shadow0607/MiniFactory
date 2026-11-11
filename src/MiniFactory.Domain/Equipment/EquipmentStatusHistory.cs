namespace MiniFactory.Domain.Equipments;

/// <summary>狀態來源（database.md §3.5 CHECK）。</summary>
public enum StatusChangeSource
{
    Report,
    Heartbeat,
    HeartbeatTimeout,
}

/// <summary>設備狀態歷史；只新增不修改。Id 由 DB identity 產生。</summary>
public sealed class EquipmentStatusHistory
{
    // 供 ORM 使用
    private EquipmentStatusHistory()
    {
    }

    public EquipmentStatusHistory(Guid equipmentId, EquipmentStatus? fromStatus, EquipmentStatus toStatus, StatusChangeSource source, DateTimeOffset occurredAt)
    {
        if (equipmentId == Guid.Empty)
        {
            throw new ArgumentException("Equipment id is required.", nameof(equipmentId));
        }

        EquipmentId = equipmentId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Source = source;
        OccurredAt = occurredAt;
    }

    public long Id { get; private set; }

    public Guid EquipmentId { get; private set; }

    /// <summary>初始紀錄為 null。</summary>
    public EquipmentStatus? FromStatus { get; private set; }

    public EquipmentStatus ToStatus { get; private set; }

    public StatusChangeSource Source { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
}
