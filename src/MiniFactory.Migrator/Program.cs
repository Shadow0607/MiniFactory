using System.Text;
using DbUp.Engine.Output;
using MiniFactory.Infrastructure.Migrations;
using MySqlConnector;

// 結束碼：0 成功（含「已是最新」）、1 遷移失敗、2 設定錯誤
const string ConnectionStringVariable = "ConnectionStrings__MiniFactory";

Console.OutputEncoding = new UTF8Encoding(false);

var raw = Environment.GetEnvironmentVariable(ConnectionStringVariable);
if (string.IsNullOrWhiteSpace(raw))
{
    Console.Error.WriteLine($"缺少環境變數 {ConnectionStringVariable}（應為 mf_migrator 帳號對 minifactory 的連線字串）。");
    return 2;
}

// 注意：不套用 DbConnectionString.Normalize。DbUp 的 journal 以 DateTime.Now（Local）寫入 applied 欄位，
// 連線字串若設 DateTimeKind=Utc，MySqlConnector 會拒絕 Local 時間而使遷移失敗。
MySqlConnectionStringBuilder info;
try
{
    info = new MySqlConnectionStringBuilder(raw);
}
catch (ArgumentException)
{
    // 不輸出連線字串內容，避免洩漏帳密
    Console.Error.WriteLine($"環境變數 {ConnectionStringVariable} 格式不正確。");
    return 2;
}

// journal 表所在的 database（部署時為 minifactory_migrations，與業務 database 分開，mf_app 碰不到）。
// 必須明確設定：未設定或空白就失敗，不退回放進目標 database（否則 mf_app 的 DML 權限會涵蓋遷移紀錄）。
if (!JournalLocation.TryFromEnvironmentValue(Environment.GetEnvironmentVariable(DbUpMigrator.JournalSchemaVariable), out var journal, out var journalError))
{
    Console.Error.WriteLine(journalError);
    return 2;
}

var connectionString = info.ConnectionString;
Console.WriteLine($"MiniFactory migrator：開始遷移 database '{info.Database}'（{info.Server}:{info.Port}，帳號 {info.UserID}）。");
Console.WriteLine($"遷移紀錄（{DbUpMigrator.JournalTable}）存放於 database '{journal.Schema}'。");

var result = DbUpMigrator.Migrate(connectionString, journal, new ConsoleUpgradeLog());
if (!result.Successful)
{
    Console.Error.WriteLine($"遷移失敗：{result.ErrorMessage}");
    return 1;
}

Console.WriteLine(result.ExecutedScripts.Count == 0
    ? "資料庫已是最新，沒有需要執行的腳本。"
    : $"遷移完成，本次執行 {result.ExecutedScripts.Count} 支腳本。");
return 0;
