using System.Text.Json;
using MiniFactory.IntegrationTests.Support;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>GET /api/equipment（api.md §4.1）。</summary>
[Collection(MariaDbCollection.Name)]
public class EquipmentApiTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{

    [Fact]
    public async Task GetEquipment_ReturnsSeededMachinesMatchingApiSpec()
    {
        var body = await GetJsonAsync("/api/equipment");

        Assert.False(body.GetProperty("stale").GetBoolean());
        Assert.EndsWith("Z", body.GetProperty("generatedAt").GetString(), StringComparison.Ordinal);
        AssertAllTimestampsAreUtcZ(body);

        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["Machine-A", "Machine-B", "Machine-C"], items.Select(i => i.GetProperty("name").GetString()));
        Assert.Equal(["Machining", "Assembly", "Inspection"], items.Select(i => i.GetProperty("capability").GetString()));
        Assert.All(items, item =>
        {
            Assert.Equal("Line-01", item.GetProperty("lineId").GetString());
            Assert.Equal("Offline", item.GetProperty("status").GetString()); // seed 初始為 Offline
            Assert.NotEqual(Guid.Empty, item.GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("lastSeenAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("reservedJobId").ValueKind);
            Assert.Equal(JsonValueKind.Number, item.GetProperty("version").ValueKind);
            Assert.EndsWith("Z", item.GetProperty("statusChangedAt").GetString(), StringComparison.Ordinal);
        });

        // 回傳的 ID 與資料庫 seed 一致（Simulator 設定檔共用同一組 ID）
        var seededIds = await Db.QueryAsync<Guid>(Cs, "SELECT id FROM equipment ORDER BY name");
        Assert.Equal(seededIds, items.Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task GetEquipment_FilterByLineId()
    {
        var line01 = await GetJsonAsync("/api/equipment?lineId=Line-01");
        var lowerCase = await GetJsonAsync("/api/equipment?lineId=line-01");
        var unknown = await GetJsonAsync("/api/equipment?lineId=Line-99");

        Assert.Equal(3, line01.GetProperty("items").GetArrayLength());
        Assert.Equal(3, lowerCase.GetProperty("items").GetArrayLength());
        Assert.Equal(0, unknown.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetEquipment_ReflectsReservationAndStatusFromDatabase()
    {
        var id = await CreateOrderAsync();
        var jobId = await Db.ScalarAsync<Guid>(Cs, "SELECT id FROM production_jobs WHERE order_id = @id AND `sequence` = 1", new { id });
        await Db.ExecAsync(Cs,
            "UPDATE equipment SET status = 'Running', reserved_job_id = @jobId, last_seen_at = @t, status_changed_at = @t, version = 5 WHERE name = 'Machine-C'",
            new { jobId, t = Db.T0.AddMinutes(1) });

        try
        {
            var body = await GetJsonAsync("/api/equipment");

            var machineC = body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Machine-C");
            Assert.Equal("Running", machineC.GetProperty("status").GetString());
            Assert.Equal(jobId, machineC.GetProperty("reservedJobId").GetGuid());
            Assert.Equal("2026-11-11T08:01:00.000Z", machineC.GetProperty("lastSeenAt").GetString());
            Assert.Equal(5, machineC.GetProperty("version").GetInt32());
        }
        finally
        {
            // 還原 seed 狀態，不影響同類別其他測試
            await Db.ExecAsync(Cs,
                "UPDATE equipment SET status = 'Offline', reserved_job_id = NULL, last_seen_at = NULL, version = 0 WHERE name = 'Machine-C'");
        }
    }
}

/// <summary>沒有任何告警的乾淨資料庫（每個測試類別有自己的資料庫）。</summary>
[Collection(MariaDbCollection.Name)]
public class AlertsEmptyApiTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{
    [Fact]
    public async Task GetAlerts_NoAlerts_ReturnsEmptyItemsAndZeroOpenCount()
    {
        var body = await GetJsonAsync("/api/alerts?status=Open&pageSize=1");

        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, body.GetProperty("openCount").GetInt32());
    }

}

/// <summary>totalCount / openCount 是全域計數，需要專屬的資料庫，所以獨立成一個類別。</summary>
[Collection(MariaDbCollection.Name)]
public class AlertOpenCountApiTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{
    [Fact]
    public async Task GetAlerts_ReturnsPagedShapeWithOpenCount()
    {
        var open1 = Guid.CreateVersion7();
        var open2 = Guid.CreateVersion7();
        var resolved = Guid.CreateVersion7();
        await Db.InsertAlertAsync(Cs, open1, "k-" + open1);
        await Db.InsertAlertAsync(Cs, open2, "k-" + open2);
        await Db.InsertAlertAsync(Cs, resolved, "k-" + resolved, "Resolved");

        var all = await GetJsonAsync("/api/alerts");
        var openOnly = await GetJsonAsync("/api/alerts?status=Open&pageSize=1"); // 前端導覽列徽章的呼叫方式
        var resolvedOnly = await GetJsonAsync("/api/alerts?status=Resolved");

        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, all.GetProperty("openCount").GetInt32());
        Assert.Equal(1, all.GetProperty("page").GetInt32());
        Assert.Equal(20, all.GetProperty("pageSize").GetInt32());

        Assert.Equal(1, openOnly.GetProperty("items").GetArrayLength());
        Assert.Equal(2, openOnly.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, openOnly.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, openOnly.GetProperty("openCount").GetInt32());

        // openCount 不受篩選影響
        Assert.Equal(1, resolvedOnly.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, resolvedOnly.GetProperty("openCount").GetInt32());
        AssertAllTimestampsAreUtcZ(all);
    }

}

/// <summary>篩選 / 排序 / 欄位格式；各測試用自己的唯一資料，不依賴全域計數。</summary>
[Collection(MariaDbCollection.Name)]
public class AlertListApiTests(ApiFixture api) : ApiTestBase(api), IClassFixture<ApiFixture>
{
    [Fact]
    public async Task GetAlerts_ItemShapeMatchesApiSpec()
    {
        var open = Guid.CreateVersion7();
        var resolved = Guid.CreateVersion7();
        await Db.InsertAlertAsync(Cs, open, "shape-" + open);
        await Db.InsertAlertAsync(Cs, resolved, "shape-" + resolved, "Resolved");

        var body = await GetJsonAsync("/api/alerts?pageSize=100");

        var items = body.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
        var openItem = items[open];
        Assert.Equal("Warning", openItem.GetProperty("severity").GetString());
        Assert.Equal("EQUIPMENT_OFFLINE", openItem.GetProperty("reason").GetString());
        Assert.Equal("Open", openItem.GetProperty("status").GetString());
        foreach (var nullable in new[] { "message", "relatedOrderId", "relatedJobId", "relatedEquipmentId", "resolvedAt", "resolvedBy", "resolutionNote" })
        {
            Assert.True(openItem.TryGetProperty(nullable, out var value), $"缺少欄位 {nullable}");
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }

        Assert.EndsWith("Z", openItem.GetProperty("createdAt").GetString(), StringComparison.Ordinal);

        var resolvedItem = items[resolved];
        Assert.Equal("Resolved", resolvedItem.GetProperty("status").GetString());
        Assert.Equal("System", resolvedItem.GetProperty("resolvedBy").GetString());
        Assert.EndsWith("Z", resolvedItem.GetProperty("resolvedAt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAlerts_RelatedIdsAndFilters_WorkAndAreCombinable()
    {
        var orderId = await CreateOrderAsync();
        var jobId = await Db.ScalarAsync<Guid>(Cs, "SELECT id FROM production_jobs WHERE order_id = @orderId AND `sequence` = 1", new { orderId });
        var machineB = await Db.ScalarAsync<Guid>(Cs, "SELECT id FROM equipment WHERE name = 'Machine-B'");
        var related = Guid.CreateVersion7();
        var unrelated = Guid.CreateVersion7();
        await Db.ExecAsync(Cs, """
            INSERT INTO alerts (id, order_id, job_id, equipment_id, severity, reason, message, status, dedup_key, created_at, updated_at)
            VALUES (@related, @orderId, @jobId, @machineB, 'Critical', 'JOB_NEEDS_REVIEW', '工序待確認', 'Open', @dedup, @t, @t)
            """, new { related, orderId, jobId, machineB, dedup = "rel-" + related, t = Db.T0 });
        await Db.InsertAlertAsync(Cs, unrelated, "unrel-" + unrelated);

        var byOrder = await GetJsonAsync($"/api/alerts?orderId={orderId}");
        var byEquipment = await GetJsonAsync($"/api/alerts?equipmentId={machineB}");
        var bySeverity = await GetJsonAsync($"/api/alerts?severity=Critical&equipmentId={machineB}");
        var noMatch = await GetJsonAsync($"/api/alerts?severity=Info&orderId={orderId}");

        var item = Assert.Single(byOrder.GetProperty("items").EnumerateArray());
        Assert.Equal(related, item.GetProperty("id").GetGuid());
        Assert.Equal(orderId, item.GetProperty("relatedOrderId").GetGuid());
        Assert.Equal(jobId, item.GetProperty("relatedJobId").GetGuid());
        Assert.Equal(machineB, item.GetProperty("relatedEquipmentId").GetGuid());
        Assert.Equal("工序待確認", item.GetProperty("message").GetString());
        Assert.Equal("JOB_NEEDS_REVIEW", item.GetProperty("reason").GetString());

        Assert.Equal(1, byEquipment.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, bySeverity.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, noMatch.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task GetAlerts_SortedByCreatedAtDescending()
    {
        var older = Guid.CreateVersion7();
        var newer = Guid.CreateVersion7();
        await Db.ExecAsync(Cs, """
            INSERT INTO alerts (id, severity, reason, status, dedup_key, created_at, updated_at) VALUES
            (@older, 'Info', 'EQUIPMENT_ERROR', 'Open', @k1, '2030-01-01 00:00:00.000000', '2030-01-01 00:00:00.000000'),
            (@newer, 'Info', 'EQUIPMENT_ERROR', 'Open', @k2, '2030-01-02 00:00:00.000000', '2030-01-02 00:00:00.000000')
            """, new { older, newer, k1 = "o-" + older, k2 = "n-" + newer });

        var body = await GetJsonAsync("/api/alerts?severity=Info&pageSize=2");

        Assert.Equal([newer, older], body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Theory]
    [InlineData("status=Closed", "status")]
    [InlineData("severity=Fatal", "severity")]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=500", "pageSize")]
    public async Task GetAlerts_InvalidQuery_Returns400(string query, string field)
    {
        using var response = await Client.GetAsync("/api/alerts?" + query, Ct);

        var problem = await AssertProblemAsync(response, 400, "validation-failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task GetAlerts_InvalidGuidFilter_Returns400()
    {
        using var response = await Client.GetAsync("/api/alerts?orderId=nope", Ct);

        await AssertProblemAsync(response, 400, "validation-failed");
    }
}
