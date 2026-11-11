using System.Text.Json;
using System.Text.Json.Serialization;
using MiniFactory.Domain.Alerts;
using MiniFactory.Domain.Equipments;
using MiniFactory.Domain.Events;
using MiniFactory.Domain.Jobs;
using MiniFactory.Domain.Orders;

namespace MiniFactory.UnitTests.Domain;

public class EventEnvelopeTests
{
    private static readonly DateTimeOffset Utc = new(2026, 9, 21, 2, 15, 30, 123, TimeSpan.Zero);

    private static JobDispatchRequestedPayload Payload()
        => new(Guid.CreateVersion7(), null, Guid.CreateVersion7(), 1);

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Create_SetsEventTypeFromPayload_AndCurrentVersion()
    {
        var eventId = Guid.CreateVersion7();

        var result = EventEnvelope.Create(eventId, Payload(), Utc, "corr-1");

        Assert.True(result.IsSuccess);
        var envelope = result.Value;
        Assert.Equal(eventId, envelope.EventId);
        Assert.Equal("JobDispatchRequested", envelope.EventType);
        Assert.Equal(1, envelope.EventVersion);
        Assert.Equal(Utc, envelope.OccurredAt);
        Assert.Equal("corr-1", envelope.CorrelationId);
    }

    [Fact]
    public void Create_EmptyEventId_Fails()
        => Assert.Equal("event.event_id_required", EventEnvelope.Create(Guid.Empty, Payload(), Utc, "c").Error!.Code);

    [Fact]
    public void Create_NonUtcOccurredAt_Fails()
    {
        var local = new DateTimeOffset(2026, 9, 21, 10, 15, 30, TimeSpan.FromHours(8));

        Assert.Equal("event.occurred_at_not_utc", EventEnvelope.Create(Guid.CreateVersion7(), Payload(), local, "c").Error!.Code);
    }

    [Fact]
    public void Create_CorrelationIdTooLong_Fails()
    {
        var tooLong = new string('c', EventEnvelope<JobDispatchRequestedPayload>.MaxCorrelationIdLength + 1);

        Assert.Equal("event.correlation_id_too_long", EventEnvelope.Create(Guid.CreateVersion7(), Payload(), Utc, tooLong).Error!.Code);
    }

    [Fact]
    public void Create_NullCorrelationId_IsAllowed()
        => Assert.True(EventEnvelope.Create(Guid.CreateVersion7(), Payload(), Utc, null).IsSuccess);

    [Fact]
    public void Validate_UnknownEventType_Fails()
    {
        var envelope = new EventEnvelope<object>(Guid.CreateVersion7(), "SomethingElse", 1, Utc, null, new object());

        Assert.Equal("event.type_unknown", envelope.Validate().Error!.Code);
    }

    [Fact]
    public void Validate_HeartbeatIsNotADomainEvent()
    {
        var envelope = new EventEnvelope<object>(Guid.CreateVersion7(), EventTypes.Heartbeat, 1, Utc, null, new object());

        Assert.Equal("event.type_unknown", envelope.Validate().Error!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Validate_UnsupportedVersion_Fails(int version)
    {
        var envelope = new EventEnvelope<JobDispatchRequestedPayload>(
            Guid.CreateVersion7(), EventTypes.JobDispatchRequested, version, Utc, null, Payload());

        Assert.Equal("event.version_unsupported", envelope.Validate().Error!.Code);
    }

    [Fact]
    public void Validate_NullPayload_Fails()
    {
        var envelope = new EventEnvelope<JobDispatchRequestedPayload>(
            Guid.CreateVersion7(), EventTypes.JobDispatchRequested, 1, Utc, null, null!);

        Assert.Equal("event.payload_required", envelope.Validate().Error!.Code);
    }

    [Fact]
    public void Json_Shape_MatchesDocumentedEnvelope()
    {
        var eventId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var commandId = Guid.CreateVersion7();
        var envelope = EventEnvelope.Create(eventId, new JobDispatchRequestedPayload(jobId, null, commandId, 1), Utc, "c0ffee").Value;

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(envelope, JsonOptions()));
        var root = doc.RootElement;

        Assert.Equal(
            ["eventId", "eventType", "eventVersion", "occurredAt", "correlationId", "payload"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(eventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal("JobDispatchRequested", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("eventVersion").GetInt32());
        Assert.Equal(Utc, root.GetProperty("occurredAt").GetDateTimeOffset());
        // 注意：System.Text.Json 預設把 UTC 的 DateTimeOffset 輸出成 "+00:00" 而非 "Z"；
        // 文件要求 ISO-8601 "Z"，序列化格式由 Infrastructure / Api 的 JSON converter 負責。

        var payload = root.GetProperty("payload");
        Assert.Equal(["jobId", "equipmentId", "commandId", "attemptNo"], payload.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("equipmentId").ValueKind);
    }

    [Fact]
    public void Json_RoundTrip_PreservesEnvelope()
    {
        var original = EventEnvelope.Create(
            Guid.CreateVersion7(),
            new JobStatusChangedPayload(Guid.CreateVersion7(), JobStatus.Ready, JobStatus.Dispatched, Guid.CreateVersion7(), null),
            Utc, "c").Value;

        var json = JsonSerializer.Serialize(original, JsonOptions());
        var back = JsonSerializer.Deserialize<EventEnvelope<JobStatusChangedPayload>>(json, JsonOptions())!;

        Assert.Equal(original, back);
        Assert.True(back.Validate().IsSuccess);
        Assert.Contains("\"fromStatus\":\"Ready\"", json); // enum 以字串輸出
    }

    // 每個 payload 型別：EventType 都在 EventTypes.All，且彼此不重複（防止配錯型別字串）
    private static readonly string[] AllPayloadEventTypes =
    [
        OrderCreatedPayload.EventType, JobDispatchRequestedPayload.EventType, JobStartedPayload.EventType,
        JobCompletedPayload.EventType, JobFailedPayload.EventType, EquipmentStatusChangedPayload.EventType,
        AlertRaisedPayload.EventType, JobStatusChangedPayload.EventType, OrderStatusChangedPayload.EventType,
        CommandResentPayload.EventType, AlertResolvedPayload.EventType,
    ];

    public static TheoryData<string> PayloadEventTypes => [.. AllPayloadEventTypes];

    [Theory]
    [MemberData(nameof(PayloadEventTypes))]
    public void PayloadEventType_IsRegistered_AndSupported(string eventType)
    {
        Assert.Contains(eventType, EventTypes.All);
        Assert.True(EventVersions.IsSupported(eventType, 1));
    }

    [Fact]
    public void EventTypes_All_HasElevenUniqueTypes_ExactlyThePayloadTypes()
    {
        var payloadTypes = AllPayloadEventTypes;

        Assert.Equal(11, EventTypes.All.Count);
        Assert.Equal(11, payloadTypes.Distinct().Count());
        Assert.True(EventTypes.All.SetEquals(payloadTypes));
    }

    [Theory]
    [InlineData("JobDispatchRequested", 2, false)]
    [InlineData("JobDispatchRequested", 0, false)]
    [InlineData("Unknown", 1, false)]
    [InlineData(null, 1, false)]
    [InlineData("Heartbeat", 1, false)]
    [InlineData("JobCompleted", 1, true)]
    public void IsSupported_RequiresKnownTypeAndCurrentVersion(string? type, int version, bool expected)
        => Assert.Equal(expected, EventVersions.IsSupported(type, version));

    [Fact]
    public void Payloads_CarryDocumentedFields()
    {
        var id = Guid.CreateVersion7();

        // 編譯期即驗證欄位名稱與型別對應 architecture.md §12 的表格
        _ = new OrderCreatedPayload(id, "ORD-1", [id]);
        _ = new JobStartedPayload(id, id, Utc);
        _ = new JobCompletedPayload(id, id, Utc, null);
        _ = new JobFailedPayload(id, id, "EQUIPMENT_OFFLINE", 1);
        _ = new EquipmentStatusChangedPayload(id, EquipmentStatus.Idle, EquipmentStatus.Offline, Utc);
        _ = new AlertRaisedPayload(id, AlertSeverity.Critical, "EQUIPMENT_OFFLINE", null, id);
        _ = new OrderStatusChangedPayload(id, OrderStatus.Created, OrderStatus.InProgress);
        _ = new CommandResentPayload(id, id, 1, 2);
        _ = new AlertResolvedPayload(id, "JOB_NEEDS_REVIEW", Utc, ResolvedBy.Operator);

        Assert.Null(new JobCompletedPayload(id, id, Utc, null).NextJobId); // 最後一道 nextJobId 為 null
    }
}
