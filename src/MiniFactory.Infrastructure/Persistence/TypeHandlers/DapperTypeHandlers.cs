using System.Data;
using Dapper;
using MiniFactory.Domain.Alerts;
using MiniFactory.Domain.Equipments;
using MiniFactory.Domain.Jobs;
using MiniFactory.Domain.Orders;

namespace MiniFactory.Infrastructure.Persistence.TypeHandlers;

/// <summary>
/// 註冊 Dapper 的型別對應（architecture.md §10.7.7）。程序啟動時呼叫一次 <see cref="Register"/>（可重複呼叫）。
/// <list type="bullet">
/// <item>欄位 snake_case ↔ 屬性 PascalCase（<c>MatchNamesWithUnderscores</c>）。</item>
/// <item>狀態 enum ↔ <c>VARCHAR</c> 字串。<b>注意</b>：Dapper 在查詢參數端會先把 enum 轉成整數、不會呼叫 handler，
/// 所以「寫入」由 <see cref="DbParameters"/>（<see cref="DbSession"/> 自動套用）負責；這裡的 handler 負責「讀取」時嚴格解析（拒絕未知值與數字字串）。</item>
/// <item>DateTime：寫入一律 UTC、讀出標成 UTC。</item>
/// <item>Guid ↔ MariaDB 原生 UUID：已驗證 MySqlConnector + Dapper 預設行為即可正確讀寫（含 WHERE 參數、IN 清單、可為 null），因此不註冊 Guid handler（UuidRoundTripTests 持續守護）。</item>
/// </list>
/// </summary>
public static class DapperTypeHandlers
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            DefaultTypeMap.MatchNamesWithUnderscores = true;

            // DateTime 是 Dapper 內建對應的型別，必須先移除內建對應，自訂 handler 才會被呼叫
            SqlMapper.RemoveTypeMap(typeof(DateTime));
            SqlMapper.RemoveTypeMap(typeof(DateTime?));
            SqlMapper.AddTypeHandler(new UtcDateTimeTypeHandler());

            // 列舉名稱即 DB 儲存字串，與 CHECK 約束一致（StatusValuesTests / 整合測試比對）
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<OrderStatus>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<JobStatus>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<ExecutionStatus>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<EquipmentStatus>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<ProcessOperation>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<StatusChangeSource>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<AlertSeverity>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<AlertStatus>());
            SqlMapper.AddTypeHandler(new EnumStringTypeHandler<ResolvedBy>());

            // AlertReason 在 DB 是 UPPER_SNAKE_CASE 代碼（EQUIPMENT_OFFLINE），不是列舉名稱
            SqlMapper.AddTypeHandler(new AlertReasonTypeHandler());

            _registered = true;
        }
    }
}

/// <summary>enum ↔ 字串。讀取時拒絕未知字串（DB 有 CHECK，出現未知值代表遷移與程式不同步，應大聲失敗）。</summary>
public sealed class EnumStringTypeHandler<TEnum> : SqlMapper.TypeHandler<TEnum> where TEnum : struct, Enum
{
    // 只接受列舉「名稱」；Enum.TryParse 會把 "3" 這種數字字串也當成合法值，所以改用名稱字典
    private static readonly Dictionary<string, TEnum> ByName =
        Enum.GetValues<TEnum>().ToDictionary(v => v.ToString(), StringComparer.Ordinal);

    public override void SetValue(IDbDataParameter parameter, TEnum value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString();
    }

    public override TEnum Parse(object value)
    {
        var text = value as string ?? throw new DataException($"欄位值無法轉為 {typeof(TEnum).Name}：預期字串，實際為 {value?.GetType().Name ?? "null"}。");
        return ByName.TryGetValue(text, out var result)
            ? result
            : throw new DataException($"未知的 {typeof(TEnum).Name} 值：'{text}'。");
    }
}

/// <summary>AlertReason ↔ <see cref="AlertReasonExtensions.ToCode"/> 代碼。</summary>
public sealed class AlertReasonTypeHandler : SqlMapper.TypeHandler<AlertReason>
{
    private static readonly Dictionary<string, AlertReason> ByCode =
        Enum.GetValues<AlertReason>().ToDictionary(r => r.ToCode(), StringComparer.Ordinal);

    public override void SetValue(IDbDataParameter parameter, AlertReason value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToCode();
    }

    public override AlertReason Parse(object value)
        => value is string code && ByCode.TryGetValue(code, out var reason)
            ? reason
            : throw new DataException($"未知的 AlertReason 代碼：'{value}'。");
}

/// <summary>
/// DateTime：DB 存 UTC（DATETIME(6)）。寫入時 Local 轉 UTC、Unspecified 視為已是 UTC；讀出標成 <see cref="DateTimeKind.Utc"/>。
/// 時間值本來就應由 <c>TimeProvider.GetUtcNow()</c> 提供，這裡只是最後一道防線。
/// </summary>
public sealed class UtcDateTimeTypeHandler : SqlMapper.TypeHandler<DateTime>
{
    public override void SetValue(IDbDataParameter parameter, DateTime value)
    {
        parameter.DbType = DbType.DateTime2;
        parameter.Value = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    public override DateTime Parse(object value) => value switch
    {
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        DateTimeOffset dto => dto.UtcDateTime,
        _ => throw new DataException($"欄位值無法轉為 DateTime：{value.GetType().Name}。"),
    };
}
