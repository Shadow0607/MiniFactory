using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Dapper;
using MiniFactory.Domain.Alerts;

namespace MiniFactory.Infrastructure.Persistence;

/// <summary>
/// 參數正規化：Dapper 的 TypeHandler 無法改變 enum 參數的寫入方式（Dapper 會先把 enum 轉成底層整數，
/// 不會呼叫 handler），所以 <see cref="DbSession"/> 在送出前統一轉換：
/// <list type="bullet">
/// <item>enum → 名稱字串（<see cref="AlertReason"/> → UPPER_SNAKE_CASE 代碼），與 CHECK 約束一致。</item>
/// <item>enum 集合（<c>IN @statuses</c>）→ 字串集合。</item>
/// <item>DateTime：Local 轉 UTC、Unspecified 視為已是 UTC（architecture.md §10.7.3 的最後一道防線）。</item>
/// </list>
/// 其餘值原樣交給 Dapper。傳入 <see cref="SqlMapper.IDynamicParameters"/> 時不處理（呼叫端自行負責）。
/// </summary>
public static class DbParameters
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    public static object? Prepare(object? param)
    {
        if (param is null or SqlMapper.IDynamicParameters)
        {
            return param;
        }

        var parameters = new DynamicParameters();
        foreach (var property in PropertyCache.GetOrAdd(param.GetType(), GetReadableProperties))
        {
            parameters.Add(property.Name, Convert(property.GetValue(param)));
        }

        return parameters;
    }

    private static PropertyInfo[] GetReadableProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

    private static object? Convert(object? value) => value switch
    {
        null => null,
        AlertReason reason => reason.ToCode(),
        Enum e => e.ToString(),
        DateTime dt => ToUtc(dt),
        string or byte[] => value,
        IEnumerable sequence => ConvertSequence(sequence, value),
        _ => value,
    };

    private static object ConvertSequence(IEnumerable sequence, object original)
    {
        // 只有元素是 enum / DateTime 時才改寫；Guid、數字、字串集合維持原狀（Dapper 原生展開）
        var converted = new List<object?>();
        var changed = false;
        foreach (var item in sequence)
        {
            var c = Convert(item);
            changed |= !ReferenceEquals(c, item) && (item is Enum or DateTime);
            converted.Add(c);
        }

        return changed ? converted : original;
    }

    private static DateTime ToUtc(DateTime value)
        => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
