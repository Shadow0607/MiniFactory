using MiniFactory.Domain.Alerts;

namespace MiniFactory.UnitTests.Domain;

public class AlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 2, 15, 30, TimeSpan.Zero);

    private static Alert OpenAlert(AlertReason reason = AlertReason.EquipmentOffline, Guid? equipmentId = null)
    {
        var eq = equipmentId ?? Guid.CreateVersion7();
        return Alert.Create(Guid.CreateVersion7(), reason, eq.ToString(), "msg", null, null, eq, Now).Value;
    }

    [Theory]
    [InlineData(AlertReason.EquipmentOffline, "EQUIPMENT_OFFLINE", AlertSeverity.Critical, true)]
    [InlineData(AlertReason.EquipmentError, "EQUIPMENT_ERROR", AlertSeverity.Warning, true)]
    [InlineData(AlertReason.JobUnconfirmed, "JOB_UNCONFIRMED", AlertSeverity.Warning, true)]
    [InlineData(AlertReason.JobNeedsReview, "JOB_NEEDS_REVIEW", AlertSeverity.Critical, false)]
    [InlineData(AlertReason.OutboxPublishFailing, "OUTBOX_PUBLISH_FAILING", AlertSeverity.Warning, true)]
    public void Reason_MapsToCodeSeverityAndAutoResolve(AlertReason reason, string code, AlertSeverity severity, bool autoResolvable)
    {
        Assert.Equal(code, reason.ToCode());
        Assert.Equal(severity, reason.DefaultSeverity());
        Assert.Equal(autoResolvable, reason.IsAutoResolvable());
    }

    [Fact]
    public void Create_Valid_IsOpenWithDedupKeyAndDefaultSeverity()
    {
        var equipmentId = Guid.CreateVersion7();

        var alert = OpenAlert(AlertReason.EquipmentOffline, equipmentId);

        Assert.Equal(AlertStatus.Open, alert.Status);
        Assert.Equal("EQUIPMENT_OFFLINE", alert.Reason);
        Assert.Equal($"EQUIPMENT_OFFLINE:{equipmentId}", alert.DedupKey);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Null(alert.ResolvedAt);
        Assert.Null(alert.ResolvedBy);
    }

    [Fact]
    public void Create_ExplicitSeverity_OverridesDefault()
    {
        var alert = Alert.Create(Guid.CreateVersion7(), AlertReason.EquipmentError, "x", null, null, null, null, Now, AlertSeverity.Info).Value;

        Assert.Equal(AlertSeverity.Info, alert.Severity);
    }

    [Fact]
    public void Create_SameReasonAndSubject_ProducesSameDedupKey()
    {
        var a = Alert.Create(Guid.CreateVersion7(), AlertReason.JobUnconfirmed, "job-1", null, null, null, null, Now).Value;
        var b = Alert.Create(Guid.CreateVersion7(), AlertReason.JobUnconfirmed, " job-1 ", null, null, null, null, Now).Value;

        Assert.Equal(a.DedupKey, b.DedupKey);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_MissingSubject_Fails(string? subject)
        => Assert.Equal("alert.dedup_subject_required",
            Alert.Create(Guid.CreateVersion7(), AlertReason.EquipmentOffline, subject, null, null, null, null, Now).Error!.Code);

    [Fact]
    public void Create_Invalid_Fails()
    {
        Assert.Equal("alert.id_required",
            Alert.Create(Guid.Empty, AlertReason.EquipmentOffline, "x", null, null, null, null, Now).Error!.Code);
        Assert.Equal("alert.reason_invalid",
            Alert.Create(Guid.CreateVersion7(), (AlertReason)99, "x", null, null, null, null, Now).Error!.Code);
        Assert.Equal("alert.message_too_long",
            Alert.Create(Guid.CreateVersion7(), AlertReason.EquipmentOffline, "x", new string('m', Alert.MaxMessageLength + 1), null, null, null, Now).Error!.Code);
        Assert.Equal("alert.dedup_key_too_long",
            Alert.Create(Guid.CreateVersion7(), AlertReason.EquipmentOffline, new string('s', Alert.MaxDedupKeyLength), null, null, null, null, Now).Error!.Code);
        Assert.Equal("alert.severity_invalid",
            Alert.Create(Guid.CreateVersion7(), AlertReason.EquipmentOffline, "x", null, null, null, null, Now, (AlertSeverity)99).Error!.Code);
    }

    [Theory]
    [InlineData(ResolvedBy.System)]
    [InlineData(ResolvedBy.Operator)]
    public void Resolve_Open_RecordsWhoWhenAndNote(ResolvedBy by)
    {
        var alert = OpenAlert();
        var at = Now.AddMinutes(5);

        var result = alert.Resolve(by, "  checked on site  ", at);

        Assert.True(result.IsSuccess);
        Assert.Equal(AlertStatus.Resolved, alert.Status);
        Assert.Equal(by, alert.ResolvedBy);
        Assert.Equal(at, alert.ResolvedAt);
        Assert.Equal("checked on site", alert.ResolutionNote);
    }

    [Fact]
    public void Resolve_BlankNote_StoresNull()
    {
        var alert = OpenAlert();

        alert.Resolve(ResolvedBy.Operator, "   ", Now);

        Assert.Null(alert.ResolutionNote);
    }

    [Fact]
    public void Resolve_Twice_FailsAndKeepsFirstResolution()
    {
        var alert = OpenAlert();
        var first = Now.AddMinutes(1);
        alert.Resolve(ResolvedBy.System, null, first);

        var second = alert.Resolve(ResolvedBy.Operator, "later", Now.AddMinutes(2));

        Assert.Equal("alert.invalid_transition", second.Error!.Code);
        Assert.Equal(ResolvedBy.System, alert.ResolvedBy);
        Assert.Equal(first, alert.ResolvedAt);
        Assert.Null(alert.ResolutionNote);
    }

    [Fact]
    public void Resolve_NoteTooLong_Fails_AndStaysOpen()
    {
        var alert = OpenAlert();

        var result = alert.Resolve(ResolvedBy.Operator, new string('n', Alert.MaxMessageLength + 1), Now);

        Assert.Equal("alert.note_too_long", result.Error!.Code);
        Assert.Equal(AlertStatus.Open, alert.Status);
    }

    [Fact]
    public void Resolve_UndefinedResolvedBy_Fails()
        => Assert.Equal("alert.resolved_by_invalid", OpenAlert().Resolve((ResolvedBy)99, null, Now).Error!.Code);
}
