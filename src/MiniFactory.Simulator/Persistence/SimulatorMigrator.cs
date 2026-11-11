using DbUp;
using DbUp.Engine.Output;

namespace MiniFactory.Simulator.Persistence;

/// <summary>Simulator 遷移結果。<see cref="ExecutedScripts"/> 為空代表已是最新。</summary>
public sealed record SimulatorMigrationResult(bool Successful, IReadOnlyList<string> ExecutedScripts, string? ErrorMessage, Exception? Error);

/// <summary>
/// Simulator 自己的 DbUp 遷移（architecture.md §16：Simulator 啟動時自行套用 database <c>minifactory_sim</c> 的腳本，
/// 使用 <c>mf_sim</c> 帳號，journal 表在 minifactory_sim）。
/// 刻意不與 Infrastructure 共用程式碼：Simulator 是獨立系統，無專案參考。
/// </summary>
public static class SimulatorMigrator
{
    public const string ScriptsNamespace = "MiniFactory.Simulator.Persistence.Migrations.Scripts.";

    public static SimulatorMigrationResult Migrate(string connectionString, IUpgradeLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = DeployChanges.To
            .MySqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(SimulatorMigrator).Assembly,
                name => name.StartsWith(ScriptsNamespace, StringComparison.Ordinal)
                        && name.EndsWith(".sql", StringComparison.Ordinal))
            .WithVariablesDisabled()
            .WithExecutionTimeout(TimeSpan.FromMinutes(2));

        builder = log is null ? builder.LogToNowhere() : builder.LogTo(log);

        var result = builder.Build().PerformUpgrade();
        return new SimulatorMigrationResult(result.Successful, result.Scripts.Select(s => s.Name).ToList(), result.Error?.Message, result.Error);
    }
}
