namespace MiniFactory.Infrastructure.Migrations;

/// <summary>
/// DbUp journal 表（schemaversions）的存放位置。呼叫端必須明確選擇，沒有預設值：
/// 部署環境若漏設環境變數而悄悄把 journal 放進業務 database，mf_app 的 DML 權限就會涵蓋它（能竄改遷移紀錄）。
/// </summary>
public readonly record struct JournalLocation
{
    private JournalLocation(string? schema) => Schema = schema;

    /// <summary>獨立 database 名稱；null 代表放在連線字串指定的目標 database 內。</summary>
    public string? Schema { get; }

    /// <summary>放在目標 database 內。僅供整合測試（每個測試資料庫各自獨立）或明確知情的程式碼使用，Migrator 不允許。</summary>
    public static JournalLocation InTargetDatabase { get; } = new(null);

    /// <summary>放在獨立的 database（部署為 <c>minifactory_migrations</c>）。</summary>
    public static JournalLocation InSchema(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        return new JournalLocation(schema.Trim());
    }

    /// <summary>
    /// 由環境變數值解析（Migrator 使用）：未設定或空白視為設定錯誤，不退回「目標 database」。
    /// </summary>
    public static bool TryFromEnvironmentValue(string? value, out JournalLocation location, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            location = default;
            error = $"缺少環境變數 {DbUpMigrator.JournalSchemaVariable}（遷移紀錄 {DbUpMigrator.JournalTable} 所在的獨立 database，部署為 minifactory_migrations）。"
                    + "為避免遷移紀錄被放進業務 database，未設定時不會執行遷移。";
            return false;
        }

        location = InSchema(value);
        error = null;
        return true;
    }
}
