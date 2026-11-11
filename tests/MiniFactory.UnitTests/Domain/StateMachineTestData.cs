namespace MiniFactory.UnitTests.Domain;

/// <summary>
/// 狀態機測試資料：以「文件上的合法轉換表」獨立重述一次（與實作分開寫），
/// 並展開成所有 (from, to) 組合，逐一驗證合法與非法。
/// </summary>
internal static class StateMachineTestData
{
    public static TheoryData<T, T, bool> AllPairs<T>(IReadOnlyDictionary<T, T[]> allowed) where T : struct, Enum
    {
        var data = new TheoryData<T, T, bool>();
        foreach (var from in Enum.GetValues<T>())
        {
            foreach (var to in Enum.GetValues<T>())
            {
                var isAllowed = allowed.TryGetValue(from, out var next) && next.Contains(to);
                data.Add(from, to, isAllowed);
            }
        }

        return data;
    }

    public static TheoryData<T> AllValues<T>(IEnumerable<T> values) where T : struct, Enum
    {
        var data = new TheoryData<T>();
        foreach (var value in values)
        {
            data.Add(value);
        }

        return data;
    }
}
