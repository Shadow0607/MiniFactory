using MiniFactory.Domain.Common;
using MiniFactory.Domain.Orders;

namespace MiniFactory.UnitTests.Domain;

public class DomainIsolationTests
{
    [Fact]
    public void DomainAssembly_ReferencesOnlyFrameworkAssemblies()
    {
        var referenced = typeof(ProductionOrder).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToArray();

        // Domain 不得相依 EF Core 或任何基礎設施套件：只能參考 BCL / 共用框架
        Assert.All(referenced, name =>
            Assert.True(
                name == "mscorlib" || name == "netstandard" || name.StartsWith("System", StringComparison.Ordinal),
                $"Domain must not reference '{name}'."));
    }

    [Fact]
    public void Result_Success_HasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Result_Failure_CarriesError()
    {
        var error = DomainError.Validation("x.code", "message");

        var result = Result.Failure(error);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
        Assert.Equal("x.code: message", error.ToString());
    }

    [Fact]
    public void ResultOfT_Success_ExposesValue()
        => Assert.Equal(42, Result.Success(42).Value);

    [Fact]
    public void ResultOfT_Failure_ValueThrows()
    {
        var result = Result.Failure<int>(DomainError.Validation("c", "m"));

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Failure_NullError_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => Result.Failure<int>(null!));
    }
}
