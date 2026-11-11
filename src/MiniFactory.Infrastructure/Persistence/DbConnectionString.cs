using MySqlConnector;

namespace MiniFactory.Infrastructure.Persistence;

/// <summary>
/// 套用 architecture.md §10.7.2 要求的連線選項。帳密由呼叫端從環境變數 / 設定提供，這裡不碰機密。
/// </summary>
public static class DbConnectionString
{
    /// <summary>
    /// 強制設定：<c>UseAffectedRows=true</c>（條件更新以實際改變的列數判斷）、<c>DateTimeKind=Utc</c>（DATETIME(6) 讀出標成 UTC）。
    /// 原生 UUID 欄位與 Guid 的對應是否需要額外 GuidFormat，見 UuidRoundTripTests 的驗證結論。
    /// </summary>
    public static string Normalize(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            UseAffectedRows = true,
            DateTimeKind = MySqlDateTimeKind.Utc,
        };
        return builder.ConnectionString;
    }
}
