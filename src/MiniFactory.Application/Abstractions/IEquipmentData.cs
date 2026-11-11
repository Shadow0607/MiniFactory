namespace MiniFactory.Application.Abstractions;

/// <summary>設備資料的寫入 / 單筆查詢。</summary>
public interface IEquipmentData
{
    /// <summary>
    /// 確認產線存在於 equipment 並回傳資料庫中的正規寫法（比對不分大小寫，例如輸入 line-01 → Line-01）；
    /// 不存在回傳 null。
    /// </summary>
    Task<string?> FindLineIdAsync(string lineId, CancellationToken cancellationToken = default);
}
