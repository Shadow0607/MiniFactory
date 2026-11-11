using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;

namespace MiniFactory.Infrastructure.Migrations;

/// <summary>一次遷移的結果。<see cref="ExecutedScripts"/> 為空代表資料庫已是最新（重跑無變更）。</summary>
public sealed record MigrationResult(
    bool Successful,
    IReadOnlyList<string> ExecutedScripts,
    string? ErrorMessage,
    Exception? Error);

/// <summary>
/// MES（database <c>minifactory</c>）的 DbUp 遷移（ADR-018）。
/// 腳本為本組件的 Embedded Resource（<c>Migrations/Scripts/NNNN_動詞_對象.sql</c>），依檔名順序執行，
/// 已執行者記錄在 journal 表 <c>schemaversions</c>。
/// 部署時 journal 放在獨立的 database（<see cref="JournalSchemaVariable"/>，預設部署為 <c>minifactory_migrations</c>），
/// 讓執行期帳號 mf_app 完全碰不到它：MariaDB 的權限只能累加、無法「db 層級授權但排除某張表」，
/// 所以 journal 不能與業務表放在同一個 database（否則 mf_app 的 DML 權限會涵蓋它，能竄改遷移紀錄）。
/// journal 位置由呼叫端以 <see cref="JournalLocation"/> 明確指定（沒有預設值）；
/// 「放在目標 database 內」只供整合測試使用（每個測試資料庫各自獨立），Migrator 不允許。
/// </summary>
public static class DbUpMigrator
{
    /// <summary>腳本資源名稱前綴（組件預設命名空間 + 資料夾）。</summary>
    public const string ScriptsNamespace = "MiniFactory.Infrastructure.Migrations.Scripts.";

    /// <summary>Migrator 讀取 journal 所在 database 名稱的環境變數。</summary>
    public const string JournalSchemaVariable = "MIGRATIONS_JOURNAL_SCHEMA";

    /// <summary>DbUp journal 表名（DbUp 預設）。</summary>
    public const string JournalTable = "schemaversions";

    /// <summary>依序執行尚未執行的腳本。<paramref name="log"/> 為 null 時不輸出。</summary>
    /// <param name="journal">journal 表的存放位置（必填，由呼叫端明確選擇）。</param>
    public static MigrationResult Migrate(string connectionString, JournalLocation journal, IUpgradeLog? log = null)
        => Migrate(connectionString, typeof(DbUpMigrator).Assembly, ScriptsNamespace, journal, log);

    /// <summary>列出內嵌的腳本名稱（已排序），供測試與除錯。</summary>
    public static IReadOnlyList<string> ListScripts()
        => typeof(DbUpMigrator).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ScriptsNamespace, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    public static MigrationResult Migrate(
        string connectionString, Assembly assembly, string scriptsNamespace, JournalLocation journal, IUpgradeLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = DeployChanges.To
            .MySqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                assembly,
                name => name.StartsWith(scriptsNamespace, StringComparison.Ordinal)
                        && name.EndsWith(".sql", StringComparison.Ordinal))
            .WithVariablesDisabled() // ADR-018：不做 $var$ 變數替換，避免誤替換
            .WithExecutionTimeout(TimeSpan.FromMinutes(2));

        if (journal.Schema is not null)
        {
            builder = builder.JournalToMySqlTable(journal.Schema, JournalTable);
        }

        builder = log is null ? builder.LogToNowhere() : builder.LogTo(log);

        var result = builder.Build().PerformUpgrade();
        return new MigrationResult(
            result.Successful,
            result.Scripts.Select(s => s.Name).ToList(),
            result.Error?.Message,
            result.Error);
    }
}
