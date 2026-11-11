using System.Diagnostics;
using MiniFactory.Infrastructure.Migrations;

namespace MiniFactory.IntegrationTests.Migrations;

/// <summary>
/// Migrator 的設定錯誤必須以結束碼 2 失敗：尤其 MIGRATIONS_JOURNAL_SCHEMA 未設定或空白時，
/// 不可悄悄把 journal 放進業務 database（mf_app 的 DML 權限會涵蓋遷移紀錄）。
/// 這些情境在連線前就失敗，不需要資料庫。
/// </summary>
public class MigratorExitCodeTests
{
    // 格式合法但不會被連線的字串（journal 檢查在連線前）；密碼為假值
    private const string DummyConnectionString = "Server=127.0.0.1;Port=1;Database=minifactory;User ID=nobody;Password=not-a-real-password";

    [Fact]
    public void JournalLocation_TryFromEnvironmentValue_RejectsNullEmptyAndWhitespace()
    {
        foreach (var value in new string?[] { null, "", "   ", "\t" })
        {
            Assert.False(JournalLocation.TryFromEnvironmentValue(value, out _, out var error));
            Assert.Contains(DbUpMigrator.JournalSchemaVariable, error);
        }
    }

    [Fact]
    public void JournalLocation_TryFromEnvironmentValue_AcceptsNameAndTrims()
    {
        Assert.True(JournalLocation.TryFromEnvironmentValue(" minifactory_migrations ", out var location, out var error));
        Assert.Null(error);
        Assert.Equal("minifactory_migrations", location.Schema);
        Assert.Null(JournalLocation.InTargetDatabase.Schema);
        Assert.Throws<ArgumentException>(() => JournalLocation.InSchema(" "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Migrator_WithoutJournalSchema_ExitsWithCode2_AndExplains(string? journalSchema)
    {
        var (exitCode, stderr) = RunMigrator(DummyConnectionString, journalSchema);

        Assert.Equal(2, exitCode);
        Assert.Contains(DbUpMigrator.JournalSchemaVariable, stderr);
        Assert.DoesNotContain("not-a-real-password", stderr);
    }

    [Fact]
    public void Migrator_WithoutConnectionString_ExitsWithCode2()
    {
        var (exitCode, stderr) = RunMigrator(null, "minifactory_migrations");

        Assert.Equal(2, exitCode);
        Assert.Contains("ConnectionStrings__MiniFactory", stderr);
    }

    private static (int ExitCode, string Stderr) RunMigrator(string? connectionString, string? journalSchema)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "MiniFactory.Migrator.dll");
        Assert.True(File.Exists(dll), $"找不到 {dll}（測試專案應有 Migrator 的專案參考）");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.Environment.Remove("ConnectionStrings__MiniFactory");
        psi.Environment.Remove(DbUpMigrator.JournalSchemaVariable);
        if (connectionString is not null)
        {
            psi.Environment["ConnectionStrings__MiniFactory"] = connectionString;
        }

        if (journalSchema is not null)
        {
            psi.Environment[DbUpMigrator.JournalSchemaVariable] = journalSchema;
        }

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "Migrator 未在 60 秒內結束");
        return (process.ExitCode, stderrTask.GetAwaiter().GetResult());
    }
}
