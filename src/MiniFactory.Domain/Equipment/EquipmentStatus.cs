namespace MiniFactory.Domain.Equipments;

// 命名空間用複數 Equipments：避免與 Equipment 類別同名造成 MiniFactory.Domain.Equipment 解析歧義

/// <summary>設備狀態（database.md §3.4 CHECK）。</summary>
public enum EquipmentStatus
{
    Idle,
    Running,
    Error,
    Offline,
}
