namespace MiniFactory.Application.Common;

/// <summary>分頁回應（api.md §1）：<c>{ items, page, pageSize, totalCount }</c>。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

/// <summary>分頁參數驗證：預設 page=1、pageSize=20，pageSize 上限 100（api.md §1）。</summary>
public static class Paging
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>驗證並回傳實際使用的值；不合法時把訊息加入 <paramref name="errors"/>。</summary>
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize, ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var p = page ?? DefaultPage;
        var s = pageSize ?? DefaultPageSize;

        if (p < 1)
        {
            errors.Add("page", "page 必須大於或等於 1");
            p = DefaultPage;
        }

        if (s < 1 || s > MaxPageSize)
        {
            errors.Add("pageSize", $"pageSize 必須介於 1 到 {MaxPageSize}");
            s = DefaultPageSize;
        }

        return (p, s);
    }

    /// <summary>OFFSET 以 long 計算，避免 page 極大時 int 溢位；超過 int 上限的頁數視為無資料。</summary>
    public static long Offset(int page, int pageSize) => (long)(page - 1) * pageSize;
}
