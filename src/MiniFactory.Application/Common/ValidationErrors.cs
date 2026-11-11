using MiniFactory.Application.Errors;

namespace MiniFactory.Application.Common;

/// <summary>收集欄位驗證錯誤（欄位名稱 → 訊息陣列），對應 api.md §2 的 <c>errors</c> 物件。</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public int Count => _errors.Count;

    public bool IsEmpty => _errors.Count == 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            list = [];
            _errors[field] = list;
        }

        list.Add(message);
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary()
        => _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);

    /// <summary>有錯誤時拋出 <see cref="ValidationFailedException"/>（API 轉 400 validation-failed）。</summary>
    public void ThrowIfAny()
    {
        if (!IsEmpty)
        {
            throw new ValidationFailedException(ToDictionary());
        }
    }

    /// <summary>
    /// 字串列舉值解析：只接受列舉名稱的精確比對（不分大小寫）。
    /// 不用 <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>：它會把逗號分隔的名稱以位元 OR 合併
    /// （例如 <c>InProgress,AwaitingConfirmation</c> 合併成另一個已定義的值），數字字串也會被接受。
    /// 含逗號、數字、空白夾雜的值一律不合法。回傳規範值；不合法回 null 並記錄錯誤。
    /// </summary>
    public T? ParseEnum<T>(string field, string? raw) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        foreach (var name in Enum.GetNames<T>())
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<T>(name);
            }
        }

        Add(field, $"{field} 的值 '{text}' 不合法，可用值：{string.Join(", ", Enum.GetNames<T>())}");
        return null;
    }
}
