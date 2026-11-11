using System.Text.Json;
using MiniFactory.Application.Abstractions;
using MiniFactory.Application.Alerts;
using MiniFactory.Application.Common;
using MiniFactory.Application.Equipments;
using MiniFactory.Application.Errors;
using MiniFactory.Application.Orders;
using MiniFactory.Domain.Alerts;
using MiniFactory.Domain.Orders;

namespace MiniFactory.UnitTests.Application;

public class JsonDefaultsTests
{
    private record TimeBox(DateTimeOffset At, DateTimeOffset? MaybeAt, DateTime Legacy);

    [Fact]
    public void DateTimeOffset_IsWrittenAsUtcWithZSuffix_NotPlusZeroOffset()
    {
        var json = JsonSerializer.Serialize(
            new TimeBox(new DateTimeOffset(2026, 9, 21, 2, 15, 30, 123, TimeSpan.Zero), null, new DateTime(2026, 9, 21, 2, 15, 30, 123, DateTimeKind.Utc)),
            JsonDefaults.Options);

        Assert.Contains("\"at\":\"2026-09-21T02:15:30.123Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"legacy\":\"2026-09-21T02:15:30.123Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maybeAt\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DateTimeOffset_WithNonUtcOffset_IsConvertedToUtc()
    {
        var local = new DateTimeOffset(2026, 9, 21, 10, 15, 30, 123, TimeSpan.FromHours(8));

        var json = JsonSerializer.Serialize(new TimeBox(local, local, local.DateTime), JsonDefaults.Options);

        Assert.Contains("\"at\":\"2026-09-21T02:15:30.123Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maybeAt\":\"2026-09-21T02:15:30.123Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DateTimeOffset_RoundTrips()
    {
        var original = new DateTimeOffset(2026, 9, 21, 2, 15, 30, 123, TimeSpan.Zero);

        var json = JsonSerializer.Serialize(new TimeBox(original, original, original.UtcDateTime), JsonDefaults.Options);
        var back = JsonSerializer.Deserialize<TimeBox>(json, JsonDefaults.Options);

        Assert.Equal(original, back!.At);
    }

    [Fact]
    public void Enums_AreWrittenAsStrings_AndNamesAreCamelCase()
    {
        var json = JsonSerializer.Serialize(
            new CreateOrderResponse(Guid.Empty, "X", OrderStatus.Created, DispatchStatus.Queued, [], DateTimeOffset.UnixEpoch),
            JsonDefaults.Options);

        Assert.Contains("\"status\":\"Created\"", json, StringComparison.Ordinal);
        Assert.Contains("\"dispatchStatus\":\"Queued\"", json, StringComparison.Ordinal);
        Assert.Contains("\"orderId\"", json, StringComparison.Ordinal);
    }
}

public class PagingTests
{
    [Fact]
    public void Normalize_Defaults()
    {
        var errors = new ValidationErrors();

        var (page, size) = Paging.Normalize(null, null, errors);

        Assert.Equal((1, 20), (page, size));
        Assert.True(errors.IsEmpty);
    }

    [Theory]
    [InlineData(0, 20, "page")]
    [InlineData(-3, 20, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 101, "pageSize")]
    public void Normalize_OutOfRange_ReportsField(int page, int pageSize, string field)
    {
        var errors = new ValidationErrors();

        Paging.Normalize(page, pageSize, errors);

        Assert.Contains(field, errors.ToDictionary().Keys);
    }

    [Fact]
    public void Normalize_PageSizeAtLimit100_IsAccepted()
    {
        var errors = new ValidationErrors();

        var (_, size) = Paging.Normalize(1, 100, errors);

        Assert.Equal(100, size);
        Assert.True(errors.IsEmpty);
    }

    [Fact]
    public void Offset_DoesNotOverflowForHugePageNumbers()
        => Assert.Equal((long)(int.MaxValue - 1) * 100, Paging.Offset(int.MaxValue, 100));
}

public class QueryValidationTests
{
    private sealed class CapturingQueries : IDashboardQueries
    {
        public OrderListFilter? OrderFilter { get; private set; }

        public AlertListFilter? AlertFilter { get; private set; }

        public (int Page, int PageSize)? Paged { get; private set; }

        public Task<PagedResult<OrderSummaryDto>> ListOrdersAsync(OrderListFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            OrderFilter = filter;
            Paged = (page, pageSize);
            return Task.FromResult(new PagedResult<OrderSummaryDto>([], page, pageSize, 0));
        }

        public Task<OrderDetailDto?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.FromResult<OrderDetailDto?>(null);

        public Task<bool> OrderExistsAsync(Guid orderId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<IReadOnlyList<OrderEventDto>> ListOrderEventsAsync(Guid orderId, Guid? afterEventId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OrderEventDto>>([]);

        public Task<IReadOnlyList<EquipmentDto>> ListEquipmentAsync(string? lineId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EquipmentDto>>([]);

        public Task<AlertListResponse> ListAlertsAsync(AlertListFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            AlertFilter = filter;
            Paged = (page, pageSize);
            return Task.FromResult(new AlertListResponse([], page, pageSize, 0, 0));
        }
    }

    [Fact]
    public async Task ListOrders_MultipleStatuses_ParsedCaseInsensitively()
    {
        var queries = new CapturingQueries();

        await new OrderQueries(queries).ListAsync(["InProgress", "awaitingconfirmation", "InProgress"], "ORD-", 2, 50, TestContext.Current.CancellationToken);

        Assert.Equal([OrderStatus.InProgress, OrderStatus.AwaitingConfirmation], queries.OrderFilter!.Statuses);
        Assert.Equal("ORD-", queries.OrderFilter.OrderNoPrefix);
        Assert.Equal((2, 50), queries.Paged);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("InProgress,AwaitingConfirmation")] // Enum.TryParse 會位元 OR 合併成另一個已定義的值
    [InlineData("Created,InProgress")]
    [InlineData("InProgress,")]
    [InlineData("Created, InProgress")]
    public async Task ListOrders_UnknownStatus_ThrowsValidationFailed(string status)
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => new OrderQueries(new CapturingQueries()).ListAsync([status], null, null, null, TestContext.Current.CancellationToken));

        Assert.Contains("status", ex.Errors.Keys);
    }

    [Fact]
    public void ParseEnum_CommaSeparatedNames_AreRejected_NotBitwiseOrCombined()
    {
        var errors = new ValidationErrors();

        var result = errors.ParseEnum<OrderStatus>("status", "InProgress,AwaitingConfirmation");

        Assert.Null(result);
        Assert.Contains("status", errors.ToDictionary().Keys);
    }

    [Theory]
    [InlineData("Completed", OrderStatus.Completed)]
    [InlineData("completed", OrderStatus.Completed)]
    [InlineData("  NEEDSREVIEW ", OrderStatus.NeedsReview)]
    public void ParseEnum_ExactName_IgnoringCase_IsAccepted(string raw, OrderStatus expected)
    {
        var errors = new ValidationErrors();

        Assert.Equal(expected, errors.ParseEnum<OrderStatus>("status", raw));
        Assert.True(errors.IsEmpty);
    }

    [Fact]
    public async Task ListOrders_PageSizeOver100_ThrowsValidationFailed()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => new OrderQueries(new CapturingQueries()).ListAsync(null, null, 1, 101, TestContext.Current.CancellationToken));

        Assert.Contains("pageSize", ex.Errors.Keys);
    }

    [Fact]
    public async Task GetOrder_Missing_ThrowsNotFound()
        => await Assert.ThrowsAsync<NotFoundException>(
            () => new OrderQueries(new CapturingQueries()).GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetOrderEvents_MissingOrder_ThrowsNotFound()
        => await Assert.ThrowsAsync<NotFoundException>(
            () => new OrderQueries(new CapturingQueries()).GetEventsAsync(Guid.NewGuid(), null, TestContext.Current.CancellationToken));

    [Fact]
    public async Task ListAlerts_StatusOpenAndPageSize1_ParsedForNavBadge()
    {
        var queries = new CapturingQueries();

        await new AlertQueries(queries).ListAsync("Open", null, null, null, null, 1, TestContext.Current.CancellationToken);

        Assert.Equal(AlertStatus.Open, queries.AlertFilter!.Status);
        Assert.Null(queries.AlertFilter.Severity);
        Assert.Equal((1, 1), queries.Paged);
    }

    [Fact]
    public async Task ListAlerts_InvalidStatusAndSeverity_ReportsBoth()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => new AlertQueries(new CapturingQueries()).ListAsync("Closed", "Fatal", null, null, null, null, TestContext.Current.CancellationToken));

        Assert.Contains("status", ex.Errors.Keys);
        Assert.Contains("severity", ex.Errors.Keys);
    }
}
