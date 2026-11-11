using System.Security.Cryptography;
using System.Text;
using MiniFactory.Application.Orders;

namespace MiniFactory.UnitTests.Application;

/// <summary>failure-scenarios.md §4：Idempotency 請求摘要比對（對欄位順序 / 空白不敏感、對值變化敏感）。</summary>
public class IdempotencyRequestHasherTests
{
    private const string OrderNo = "ORD-20260921-001";

    [Fact]
    public void Compute_ReturnsLowercaseSha256Hex()
    {
        var hash = IdempotencyRequestHasher.Compute(OrderNo, "Motor-A", 500, "Line-01");

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Compute_MatchesHashOfCanonicalJson()
    {
        // 獨立重述 architecture.md §10.5 的正規化格式：固定順序的 JSON，再取 SHA-256
        const string canonical = """{"orderNo":"ORD-20260921-001","productCode":"Motor-A","quantity":500,"lineId":"Line-01"}""";
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        Assert.Equal(expected, IdempotencyRequestHasher.Compute(OrderNo, "Motor-A", 500, "Line-01"));
    }

    [Fact]
    public void Compute_SameContent_SameHash()
        => Assert.Equal(
            IdempotencyRequestHasher.Compute(OrderNo, "Motor-A", 500, "Line-01"),
            IdempotencyRequestHasher.Compute(OrderNo, "Motor-A", 500, "Line-01"));

    [Fact]
    public void Compute_SurroundingWhitespace_IsIgnored()
        => Assert.Equal(
            IdempotencyRequestHasher.Compute(OrderNo, "Motor-A", 500, "Line-01"),
            IdempotencyRequestHasher.Compute("  ORD-20260921-001 ", "\tMotor-A", 500, "Line-01  "));

    [Fact]
    public void Compute_FieldOrderInOriginalBody_DoesNotMatter()
    {
        // 摘要來自「解析後的欄位」而不是原始 body，所以 JSON 欄位順序、縮排、多餘欄位都不影響
        var a = new CreateOrderRequest(OrderNo, "Motor-A", 500, "Line-01");
        var b = new CreateOrderRequest(LineId: "Line-01", Quantity: 500, ProductCode: "Motor-A", OrderNo: OrderNo);

        Assert.Equal(IdempotencyRequestHasher.Compute(a), IdempotencyRequestHasher.Compute(b));
    }

    [Theory]
    [InlineData("ORD-20260921-002", "Motor-A", 500, "Line-01")]
    [InlineData(OrderNo, "Motor-B", 500, "Line-01")]
    [InlineData(OrderNo, "Motor-A", 600, "Line-01")]
    [InlineData(OrderNo, "Motor-A", 500, "Line-02")]
    public void Compute_AnyFieldChanged_ChangesHash(string orderNo, string productCode, int quantity, string lineId)
        => Assert.NotEqual(
            IdempotencyRequestHasher.Compute(OrderNo, "Motor-A", 500, "Line-01"),
            IdempotencyRequestHasher.Compute(orderNo, productCode, quantity, lineId));

    [Fact]
    public void Compute_ValuesSwappedBetweenFields_ChangesHash()
        => Assert.NotEqual(
            IdempotencyRequestHasher.Compute("A", "B", 1, "L"),
            IdempotencyRequestHasher.Compute("B", "A", 1, "L"));

    [Fact]
    public void Compute_CaseDifference_ChangesHash()
        // 摘要比對區分大小寫：同 Key 換了大小寫，視為內容不同
        => Assert.NotEqual(
            IdempotencyRequestHasher.Compute("ORD-1", "Motor-A", 1, "Line-01"),
            IdempotencyRequestHasher.Compute("ord-1", "Motor-A", 1, "Line-01"));

    [Fact]
    public void Compute_SpecialCharacters_AreEscapedSoFieldsCannotBleedIntoEachOther()
        => Assert.NotEqual(
            IdempotencyRequestHasher.Compute("A\",\"productCode\":\"B", "C", 1, "L"),
            IdempotencyRequestHasher.Compute("A", "B\",\"productCode\":\"C", 1, "L"));
}
