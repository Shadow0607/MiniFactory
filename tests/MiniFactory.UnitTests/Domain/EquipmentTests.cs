using MiniFactory.Domain.Equipments;
using MiniFactory.Domain.Jobs;

namespace MiniFactory.UnitTests.Domain;

public class EquipmentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 2, 0, 0, TimeSpan.Zero);

    private static Equipment NewEquipment(ProcessOperation capability = ProcessOperation.Machining)
        => Equipment.Create(Guid.CreateVersion7(), "Line-01", "Machine-A", capability, T0).Value;

    private static Equipment IdleEquipment()
    {
        var e = NewEquipment();
        Assert.Equal(EquipmentReportOutcome.StatusChanged,
            e.ApplyReport(EquipmentStatus.Idle, T0.AddSeconds(5), T0.AddSeconds(5)).Value);
        return e;
    }

    [Fact]
    public void Create_StartsOffline()
    {
        var e = NewEquipment();

        Assert.Equal(EquipmentStatus.Offline, e.Status);
        Assert.Null(e.ReservedJobId);
        Assert.Null(e.LastSeenAt);
    }

    [Theory]
    [InlineData("", "Machine-A", "equipment.line_id_invalid")]
    [InlineData("Line-01", null, "equipment.name_invalid")]
    [InlineData("Line-01", "  ", "equipment.name_invalid")]
    public void Create_InvalidInput_Fails(string? line, string? name, string code)
        => Assert.Equal(code, Equipment.Create(Guid.CreateVersion7(), line, name, ProcessOperation.Machining, T0).Error!.Code);

    [Fact]
    public void Create_EmptyIdOrUndefinedCapability_Fails()
    {
        Assert.Equal("equipment.id_required",
            Equipment.Create(Guid.Empty, "Line-01", "M", ProcessOperation.Machining, T0).Error!.Code);
        Assert.Equal("equipment.capability_invalid",
            Equipment.Create(Guid.CreateVersion7(), "Line-01", "M", (ProcessOperation)99, T0).Error!.Code);
    }

    [Fact]
    public void CanServe_MatchesLineAndCapability()
    {
        var e = NewEquipment(ProcessOperation.Assembly);

        Assert.True(e.CanServe("Line-01", ProcessOperation.Assembly));
        Assert.False(e.CanServe("Line-02", ProcessOperation.Assembly));
        Assert.False(e.CanServe("Line-01", ProcessOperation.Machining));
    }

    [Fact]
    public void ApplyReport_NewerDifferentStatus_ChangesStatus()
    {
        var e = NewEquipment();
        var occurred = T0.AddSeconds(5);
        var received = T0.AddSeconds(6);

        var result = e.ApplyReport(EquipmentStatus.Idle, occurred, received);

        Assert.Equal(EquipmentReportOutcome.StatusChanged, result.Value);
        Assert.Equal(EquipmentStatus.Idle, e.Status);
        Assert.Equal(occurred, e.StatusChangedAt);
        Assert.Equal(received, e.LastSeenAt);
        Assert.Equal(1, e.Version);
    }

    [Fact]
    public void ApplyReport_SameStatus_OnlyRefreshesLastSeen()
    {
        var e = IdleEquipment();
        var changedAt = e.StatusChangedAt;
        var version = e.Version;

        var result = e.ApplyReport(EquipmentStatus.Idle, T0.AddSeconds(10), T0.AddSeconds(10));

        Assert.Equal(EquipmentReportOutcome.Refreshed, result.Value);
        Assert.Equal(T0.AddSeconds(10), e.LastSeenAt);
        Assert.Equal(changedAt, e.StatusChangedAt);
        Assert.Equal(version, e.Version);
    }

    [Theory]
    [InlineData(0)]  // 與目前狀態時間相同
    [InlineData(-3)] // 比目前狀態舊
    public void ApplyReport_NotNewerThanStatusChangedAt_IsIgnored(int offsetSeconds)
    {
        var e = IdleEquipment();
        var lastSeen = e.LastSeenAt;

        var result = e.ApplyReport(EquipmentStatus.Error, e.StatusChangedAt.AddSeconds(offsetSeconds), T0.AddSeconds(20));

        Assert.Equal(EquipmentReportOutcome.IgnoredStale, result.Value);
        Assert.Equal(EquipmentStatus.Idle, e.Status);
        Assert.Equal(lastSeen, e.LastSeenAt);
    }

    [Fact]
    public void ApplyReport_IllegalTransition_FailsWithoutChange()
    {
        var e = IdleEquipment();
        e.ApplyReport(EquipmentStatus.Error, T0.AddSeconds(10), T0.AddSeconds(10));

        var result = e.ApplyReport(EquipmentStatus.Running, T0.AddSeconds(20), T0.AddSeconds(20)); // Error → Running 非法

        Assert.True(result.IsFailure);
        Assert.Equal("equipment.invalid_transition", result.Error!.Code);
        Assert.Equal(EquipmentStatus.Error, e.Status);
    }

    [Fact]
    public void MarkOffline_FromIdle_SetsOfflineAndStatusChangedAt()
    {
        var e = IdleEquipment();
        var at = T0.AddSeconds(60);

        Assert.True(e.MarkOffline(at).IsSuccess);

        Assert.Equal(EquipmentStatus.Offline, e.Status);
        Assert.Equal(at, e.StatusChangedAt);
    }

    [Fact]
    public void MarkOffline_WhenAlreadyOffline_Fails()
        => Assert.Equal("equipment.invalid_transition", NewEquipment().MarkOffline(T0).Error!.Code);

    [Fact]
    public void Recovery_ReportOlderThanOfflineMark_IsIgnored_NewerRecovers()
    {
        var e = IdleEquipment();
        e.MarkOffline(T0.AddSeconds(60));

        var stale = e.ApplyReport(EquipmentStatus.Idle, T0.AddSeconds(58), T0.AddSeconds(61)); // 逾時前送出、晚到的心跳
        Assert.Equal(EquipmentReportOutcome.IgnoredStale, stale.Value);
        Assert.Equal(EquipmentStatus.Offline, e.Status);

        var recovered = e.ApplyReport(EquipmentStatus.Idle, T0.AddSeconds(70), T0.AddSeconds(70));
        Assert.Equal(EquipmentReportOutcome.StatusChanged, recovered.Value);
        Assert.Equal(EquipmentStatus.Idle, e.Status);
    }

    [Fact]
    public void Reserve_WhenIdleAndFree_Succeeds()
    {
        var e = IdleEquipment();
        var jobId = Guid.CreateVersion7();

        Assert.True(e.IsAvailableForReservation);
        Assert.True(e.Reserve(jobId, T0.AddSeconds(10)).IsSuccess);

        Assert.Equal(jobId, e.ReservedJobId);
        Assert.False(e.IsAvailableForReservation);
    }

    [Fact]
    public void Reserve_WhenAlreadyReserved_FailsAndKeepsFirstReservation()
    {
        var e = IdleEquipment();
        var first = Guid.CreateVersion7();
        e.Reserve(first, T0.AddSeconds(10));

        var result = e.Reserve(Guid.CreateVersion7(), T0.AddSeconds(11)); // 第二個 Worker 搶同一台

        Assert.Equal("equipment.unavailable", result.Error!.Code);
        Assert.Equal(first, e.ReservedJobId);
    }

    [Theory]
    [InlineData(EquipmentStatus.Running)]
    [InlineData(EquipmentStatus.Error)]
    [InlineData(EquipmentStatus.Offline)]
    public void Reserve_WhenNotIdle_Fails(EquipmentStatus status)
    {
        var e = IdleEquipment();
        if (status == EquipmentStatus.Offline)
        {
            e.MarkOffline(T0.AddSeconds(10));
        }
        else
        {
            e.ApplyReport(status, T0.AddSeconds(10), T0.AddSeconds(10));
        }

        Assert.Equal("equipment.unavailable", e.Reserve(Guid.CreateVersion7(), T0.AddSeconds(11)).Error!.Code);
        Assert.Null(e.ReservedJobId);
    }

    [Fact]
    public void Reserve_EmptyJobId_Fails()
        => Assert.Equal("equipment.job_id_required", IdleEquipment().Reserve(Guid.Empty, T0).Error!.Code);

    [Fact]
    public void Release_ByReservingJob_Succeeds_ByOtherJob_Fails()
    {
        var e = IdleEquipment();
        var jobId = Guid.CreateVersion7();
        e.Reserve(jobId, T0.AddSeconds(10));

        Assert.Equal("equipment.not_reserved_by_job", e.Release(Guid.CreateVersion7(), T0.AddSeconds(11)).Error!.Code);
        Assert.Equal(jobId, e.ReservedJobId);

        Assert.True(e.Release(jobId, T0.AddSeconds(12)).IsSuccess);
        Assert.Null(e.ReservedJobId);
        Assert.True(e.IsAvailableForReservation);
    }

    [Fact]
    public void StatusHistory_RecordsTransition()
    {
        var id = Guid.CreateVersion7();

        var history = new EquipmentStatusHistory(id, null, EquipmentStatus.Idle, StatusChangeSource.Heartbeat, T0);

        Assert.Equal(id, history.EquipmentId);
        Assert.Null(history.FromStatus);
        Assert.Equal(EquipmentStatus.Idle, history.ToStatus);
        Assert.Equal(StatusChangeSource.Heartbeat, history.Source);
    }

    [Fact]
    public void StatusHistory_EmptyEquipmentId_Throws()
        => Assert.Throws<ArgumentException>(() =>
            new EquipmentStatusHistory(Guid.Empty, null, EquipmentStatus.Idle, StatusChangeSource.Report, T0));
}
