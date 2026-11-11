using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MiniFactory.Application.Orders;

/// <summary>
/// 建單請求摘要（architecture.md §10.5）：把正規化後的欄位以<b>固定順序</b>序列化，再取 SHA-256（小寫 hex，64 字元）。
/// 不對原始 body 雜湊，所以空白、欄位順序、多餘欄位不影響結果；任何欄位值改變都會得到不同摘要。
/// 正規化：orderNo / productCode / lineId 去除前後空白；quantity 為整數。
/// </summary>
public static class IdempotencyRequestHasher
{
    public static string Compute(string orderNo, string productCode, int quantity, string lineId)
    {
        ArgumentNullException.ThrowIfNull(orderNo);
        ArgumentNullException.ThrowIfNull(productCode);
        ArgumentNullException.ThrowIfNull(lineId);

        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("orderNo", orderNo.Trim());
            writer.WriteString("productCode", productCode.Trim());
            writer.WriteNumber("quantity", quantity);
            writer.WriteString("lineId", lineId.Trim());
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>從請求計算摘要；請求必須已通過驗證（欄位皆有值）。</summary>
    public static string Compute(CreateOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Compute(request.OrderNo!, request.ProductCode!, request.Quantity!.Value, request.LineId!);
    }
}
