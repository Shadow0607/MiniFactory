using System.Text.RegularExpressions;
using MiniFactory.Application.Common;
using MiniFactory.Domain.Orders;

namespace MiniFactory.Application.Orders;

/// <summary>
/// 建單輸入驗證（api.md §3.1）。錯誤欄位名稱用 JSON 的 camelCase，header 用 header 名稱，
/// 對應 400 回應的 <c>errors</c> 物件。「產線是否存在」需要查資料庫，由 handler 處理。
/// </summary>
public static partial class CreateOrderValidator
{
    public const int MaxIdempotencyKeyLength = 128;

    // 【待確認：orderNo 格式】依 api.md 先採 A-Z a-z 0-9 - _
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex OrderNoPattern();

    /// <param name="maxQuantity">quantity 上限（含）。必填：由呼叫端傳入設定值（<see cref="OrderLimitsOptions.MaxQuantity"/>），避免漏傳而悄悄套用預設值。</param>
    public static ValidationErrors Validate(
        CreateOrderRequest? request,
        string? idempotencyKey,
        int maxQuantity)
    {
        var errors = new ValidationErrors();

        if (idempotencyKey is not null && !IsValidIdempotencyKey(idempotencyKey))
        {
            errors.Add("Idempotency-Key", $"Idempotency-Key 必須是 1 到 {MaxIdempotencyKeyLength} 個可見 ASCII 字元（不含空白）");
        }

        if (request is null)
        {
            errors.Add("body", "必須提供請求內容");
            return errors;
        }

        var orderNo = request.OrderNo?.Trim();
        if (string.IsNullOrEmpty(orderNo))
        {
            errors.Add("orderNo", "orderNo 為必填");
        }
        else if (orderNo.Length > ProductionOrder.MaxOrderNoLength)
        {
            errors.Add("orderNo", $"orderNo 長度不可超過 {ProductionOrder.MaxOrderNoLength} 字");
        }
        else if (!OrderNoPattern().IsMatch(orderNo))
        {
            errors.Add("orderNo", "orderNo 只能包含英文字母、數字、- 與 _");
        }

        var productCode = request.ProductCode?.Trim();
        if (string.IsNullOrEmpty(productCode))
        {
            errors.Add("productCode", "productCode 為必填");
        }
        else if (productCode.Length > ProductionOrder.MaxProductCodeLength)
        {
            errors.Add("productCode", $"productCode 長度不可超過 {ProductionOrder.MaxProductCodeLength} 字");
        }

        if (request.Quantity is null)
        {
            errors.Add("quantity", "quantity 為必填");
        }
        else if (request.Quantity <= 0)
        {
            errors.Add("quantity", "quantity 必須大於 0");
        }
        else if (request.Quantity > maxQuantity)
        {
            errors.Add("quantity", $"quantity 不可超過 {maxQuantity}");
        }

        var lineId = request.LineId?.Trim();
        if (string.IsNullOrEmpty(lineId))
        {
            errors.Add("lineId", "lineId 為必填");
        }
        else if (lineId.Length > ProductionOrder.MaxLineIdLength)
        {
            errors.Add("lineId", $"lineId 長度不可超過 {ProductionOrder.MaxLineIdLength} 字");
        }

        return errors;
    }

    /// <summary>1～128 個可見 ASCII（0x21～0x7E，不含空白與控制字元）。</summary>
    public static bool IsValidIdempotencyKey(string key)
        => key.Length is >= 1 and <= MaxIdempotencyKeyLength && key.All(c => c is >= '\u0021' and <= '\u007E');
}
