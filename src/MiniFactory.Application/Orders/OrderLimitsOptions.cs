namespace MiniFactory.Application.Orders;

/// <summary>建單輸入的上限設定（設定區段 <c>Orders</c>）。</summary>
public sealed class OrderLimitsOptions
{
    public const string SectionName = "Orders";

    /// <summary>【待確認：業務上限】quantity 的預設上限；防止極端數字寫入資料庫與畫面。實際值待業務確認。</summary>
    public const int DefaultMaxQuantity = 100_000;

    /// <summary>單張訂單 quantity 上限（含）。設定鍵 <c>Orders:MaxQuantity</c>，環境變數 <c>Orders__MaxQuantity</c>。</summary>
    public int MaxQuantity { get; set; } = DefaultMaxQuantity;

    /// <summary>啟動時檢查；設定值不合理直接讓程序失敗。</summary>
    public void Validate()
    {
        if (MaxQuantity < 1)
        {
            throw new InvalidOperationException("Orders:MaxQuantity 必須大於 0。");
        }
    }
}
