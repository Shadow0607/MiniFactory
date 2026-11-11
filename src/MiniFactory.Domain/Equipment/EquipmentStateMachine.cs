using MiniFactory.Domain.Common;

namespace MiniFactory.Domain.Equipments;

/// <summary>設備狀態機（architecture.md §9.4）。設備沒有終態；相同狀態不算轉換。</summary>
public static class EquipmentStateMachine
{
    private static readonly TransitionTable<EquipmentStatus> Table = new("equipment", new Dictionary<EquipmentStatus, EquipmentStatus[]>
    {
        [EquipmentStatus.Idle] = [EquipmentStatus.Running, EquipmentStatus.Error, EquipmentStatus.Offline],
        [EquipmentStatus.Running] = [EquipmentStatus.Idle, EquipmentStatus.Error, EquipmentStatus.Offline],
        [EquipmentStatus.Error] = [EquipmentStatus.Idle, EquipmentStatus.Offline],
        [EquipmentStatus.Offline] = [EquipmentStatus.Idle, EquipmentStatus.Running, EquipmentStatus.Error],
    });

    public static bool CanTransition(EquipmentStatus from, EquipmentStatus to) => Table.CanTransition(from, to);

    public static IReadOnlyCollection<EquipmentStatus> NextStates(EquipmentStatus status) => Table.NextStates(status);

    public static Result<EquipmentStatus> TryTransition(EquipmentStatus from, EquipmentStatus to)
        => Table.TryTransition(from, to);
}
