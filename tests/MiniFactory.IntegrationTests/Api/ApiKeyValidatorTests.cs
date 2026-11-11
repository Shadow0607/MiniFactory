using MiniFactory.Api.Security;

namespace MiniFactory.IntegrationTests.Api;

/// <summary>X-Api-Key 比對規則（不需要資料庫）。</summary>
public class ApiKeyValidatorTests
{
    // 啟動檢查要求 Key 至少 32 字元，所以測試用的 Key 也要夠長
    private const string OpKey = "op-key-0123456789abcdef0123456789";
    private const string SimKey = "sim-key-0123456789abcdef0123456789";

    private static ApiKeyValidator Create(string? operatorKey = OpKey, string? simulatorKey = SimKey)
        => new(new SecurityOptions { OperatorApiKey = operatorKey, SimulatorApiKey = simulatorKey });

    [Fact]
    public void IsValid_ExactKey_Accepted()
    {
        var validator = Create();

        Assert.True(validator.IsValid(ApiKeyKind.Operator, OpKey));
        Assert.True(validator.IsValid(ApiKeyKind.Simulator, SimKey));
    }

    [Fact]
    public void IsValid_KeyOfTheOtherKind_Rejected()
    {
        var validator = Create();

        Assert.False(validator.IsValid(ApiKeyKind.Operator, SimKey));
        Assert.False(validator.IsValid(ApiKeyKind.Simulator, OpKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("op-key-0123456789abcdef012345678")]
    [InlineData("op-key-0123456789abcdef0123456789x")]
    [InlineData("OP-KEY-0123456789ABCDEF0123456789")]
    [InlineData(" op-key-0123456789abcdef0123456789")]
    public void IsValid_WrongOrMissingKey_Rejected(string? provided)
        => Assert.False(Create().IsValid(ApiKeyKind.Operator, provided));

    [Fact]
    public void IsValid_SimulatorKeyNotConfigured_NothingMatchesSimulatorKind()
    {
        var validator = Create(simulatorKey: null);

        Assert.False(validator.IsValid(ApiKeyKind.Simulator, string.Empty));
        Assert.False(validator.IsValid(ApiKeyKind.Simulator, "anything"));
        Assert.True(validator.IsValid(ApiKeyKind.Operator, OpKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithoutOperatorKey_Throws(string? operatorKey)
        => Assert.Throws<InvalidOperationException>(() => Create(operatorKey));

    [Fact]
    public void Constructor_WithSameKeys_Throws()
        => Assert.Throws<InvalidOperationException>(() => Create(OpKey, OpKey));

    // ---- 啟動檢查：長度與占位值（S1） ----

    [Fact]
    public void Constructor_KeyShorterThan32_ThrowsWithoutLeakingKey()
    {
        var shortKey = "short-secret-value-31-chars-xxx"; // 31 字元
        Assert.Equal(31, shortKey.Length);

        var ex = Assert.Throws<InvalidOperationException>(() => Create(shortKey));

        Assert.Contains("Security:OperatorApiKey", ex.Message, StringComparison.Ordinal);
        Assert.Contains("32", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(shortKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("31", ex.Message, StringComparison.Ordinal); // 不帶出目前長度
    }

    [Fact]
    public void Constructor_KeyOfExactly32Chars_IsAccepted()
        => Assert.NotNull(Create("abcdefghij" + new string('7', 22)));

    [Fact]
    public void Constructor_ShortSimulatorKey_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(simulatorKey: "too-short"));

        Assert.Contains("Security:SimulatorApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("change-me-change-me-change-me-change-me")]
    [InlineData("CHANGE-ME-CHANGE-ME-CHANGE-ME-CHANGE-ME")]
    [InlineData("changemechangemechangemechangeme0000")]
    [InlineData("replace-me-replace-me-replace-me-replace-me")]
    [InlineData("Replace_Me_with_a_real_key_0123456789")]
    [InlineData("0123456789abcdef-Change_Me-0123456789abcdef")]
    public void Constructor_PlaceholderKey_ThrowsWithoutLeakingKey(string placeholder)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(placeholder));

        Assert.Contains("占位", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(placeholder, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_PlaceholderSimulatorKey_Throws()
        => Assert.Throws<InvalidOperationException>(() => Create(simulatorKey: "change-me-change-me-change-me-change-me"));

    // ---- R4：前後空白、亂度下限 ----

    [Theory]
    [InlineData(" op-key-0123456789abcdef0123456789")]
    [InlineData("op-key-0123456789abcdef0123456789 ")]
    [InlineData("op-key-0123456789abcdef0123456789\n")]
    [InlineData("\top-key-0123456789abcdef0123456789")]
    public void Constructor_KeyWithLeadingOrTrailingWhitespace_ThrowsWithoutLeakingKey(string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(key));

        Assert.Contains("Security:OperatorApiKey", ex.Message, StringComparison.Ordinal);
        Assert.Contains("空白", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key.Trim(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_SimulatorKeyWithTrailingWhitespace_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Create(simulatorKey: SimKey + " "));

        Assert.Contains("Security:SimulatorApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_KeyWithInnerSpaces_IsAccepted()
        => Assert.NotNull(Create("op key 0123456789abcdef 0123456789"));

    [Theory]
    [InlineData("11111111111111111111111111111111")] // 32 個相同字元
    [InlineData("abababababababababababababababab")] // 2 種
    [InlineData("12345678901234567890123456789012")] // 10 種但剛好夠：另見下方邊界測試，這裡只驗證可接受
    public void Constructor_LowEntropyKey_RejectedOnlyBelowTheDistinctCharFloor(string key)
    {
        if (key.Distinct().Count() >= SecurityOptions.MinDistinctChars)
        {
            Assert.NotNull(Create(key));
            return;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => Create(key));
        Assert.Contains("亂度", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_DistinctCharBoundary_NineRejected_TenAccepted()
    {
        var nine = "abcdefghi" + new string('a', 23);
        var ten = "abcdefghij" + new string('a', 22);
        Assert.Equal(32, nine.Length);
        Assert.Equal(32, ten.Length);

        Assert.Throws<InvalidOperationException>(() => Create(nine));
        Assert.NotNull(Create(ten));
    }

    [Fact]
    public void Constructor_LowEntropySimulatorKey_Throws()
        => Assert.Throws<InvalidOperationException>(() => Create(simulatorKey: new string('z', 32)));
}
