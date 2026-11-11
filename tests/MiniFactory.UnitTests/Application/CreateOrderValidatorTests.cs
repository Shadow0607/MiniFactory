using MiniFactory.Application.Common;
using MiniFactory.Application.Orders;

namespace MiniFactory.UnitTests.Application;

public class CreateOrderValidatorTests
{
    // maxQuantity 在正式程式碼是必填；測試預設用 OrderLimitsOptions.DefaultMaxQuantity，需要其他值時明確傳入
    private static ValidationErrors Check(
        CreateOrderRequest? request, string? idempotencyKey, int maxQuantity = OrderLimitsOptions.DefaultMaxQuantity)
        => CreateOrderValidator.Validate(request, idempotencyKey, maxQuantity);

    private static CreateOrderRequest Valid() => new("ORD-20260921-001", "Motor-A", 500, "Line-01");

    [Fact]
    public void Validate_ValidRequest_NoErrors()
        => Assert.True(Check(Valid(), "create-order-ord-20260921-001").IsEmpty);

    [Fact]
    public void Validate_NoIdempotencyKey_IsAllowed()
        => Assert.True(Check(Valid(), null).IsEmpty);

    [Fact]
    public void Validate_NullBody_ReportsBody()
        => Assert.Contains("body", Check(null, null).ToDictionary().Keys);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ORD 001")] // 空白
    [InlineData("ORD/001")]
    [InlineData("訂單001")]
    public void Validate_InvalidOrderNo_ReportsOrderNo(string? orderNo)
    {
        var errors = Check(Valid() with { OrderNo = orderNo }, null).ToDictionary();

        Assert.Contains("orderNo", errors.Keys);
    }

    [Fact]
    public void Validate_OrderNoTooLong_ReportsOrderNo()
    {
        var errors = Check(Valid() with { OrderNo = new string('A', 51) }, null).ToDictionary();

        Assert.Contains("orderNo", errors.Keys);
    }

    [Fact]
    public void Validate_OrderNoAtMaxLength_IsAccepted()
        => Assert.True(Check(Valid() with { OrderNo = new string('A', 50) }, null).IsEmpty);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_InvalidQuantity_ReportsQuantity(int? quantity)
    {
        var errors = Check(Valid() with { Quantity = quantity }, null).ToDictionary();

        Assert.Contains("quantity", errors.Keys);
    }

    [Fact]
    public void Validate_ZeroQuantity_MessageMatchesApiSpecExample()
    {
        var errors = Check(Valid() with { Quantity = 0 }, null).ToDictionary();

        Assert.Equal("quantity 必須大於 0", Assert.Single(errors["quantity"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_MissingProductCodeOrLineId_Reported(string? value)
    {
        var errors = Check(Valid() with { ProductCode = value, LineId = value }, null).ToDictionary();

        Assert.Contains("productCode", errors.Keys);
        Assert.Contains("lineId", errors.Keys);
    }

    [Fact]
    public void Validate_TooLongProductCodeAndLineId_Reported()
    {
        var errors = Check(
            Valid() with { ProductCode = new string('P', 51), LineId = new string('L', 33) }, null).ToDictionary();

        Assert.Contains("productCode", errors.Keys);
        Assert.Contains("lineId", errors.Keys);
    }

    [Fact]
    public void Validate_AllFieldsInvalid_ReportsEveryField()
    {
        var errors = Check(new CreateOrderRequest(null, null, null, null), null).ToDictionary();

        Assert.Equal(["lineId", "orderNo", "productCode", "quantity"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("create-order-ord-20260921-001")]
    [InlineData("!~")] // 0x21 與 0x7E，可見 ASCII 的邊界
    public void IsValidIdempotencyKey_Valid(string key)
        => Assert.True(CreateOrderValidator.IsValidIdempotencyKey(key));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("tab\there")]
    [InlineData("中文")]
    [InlineData("ctrl\u0001")]
    [InlineData("del\u007F")]
    public void IsValidIdempotencyKey_Invalid(string key)
        => Assert.False(CreateOrderValidator.IsValidIdempotencyKey(key));

    [Fact]
    public void IsValidIdempotencyKey_Length128Ok_129Rejected()
    {
        Assert.True(CreateOrderValidator.IsValidIdempotencyKey(new string('k', 128)));
        Assert.False(CreateOrderValidator.IsValidIdempotencyKey(new string('k', 129)));
    }

    [Fact]
    public void Validate_InvalidIdempotencyKey_ReportsHeaderName()
    {
        var errors = Check(Valid(), "bad key").ToDictionary();

        Assert.Contains("Idempotency-Key", errors.Keys);
    }

    [Fact]
    public void Quantity_AtDefaultMax_IsValid_AndOneOverIsRejected()
    {
        Assert.True(Check(Valid() with { Quantity = OrderLimitsOptions.DefaultMaxQuantity }, null).IsEmpty);

        var errors = Check(Valid() with { Quantity = OrderLimitsOptions.DefaultMaxQuantity + 1 }, null).ToDictionary();

        Assert.Contains("quantity", errors.Keys);
    }

    [Fact]
    public void Quantity_UsesConfiguredMax()
    {
        Assert.True(Check(Valid() with { Quantity = 10 }, null, maxQuantity: 10).IsEmpty);
        Assert.Contains("quantity", Check(Valid() with { Quantity = 11 }, null, maxQuantity: 10).ToDictionary().Keys);
    }

    [Fact]
    public void OrderLimitsOptions_RejectsNonPositiveMax()
    {
        Assert.Throws<InvalidOperationException>(() => new OrderLimitsOptions { MaxQuantity = 0 }.Validate());
        new OrderLimitsOptions().Validate();
    }
}
