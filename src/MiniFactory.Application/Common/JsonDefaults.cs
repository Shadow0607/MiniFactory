using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniFactory.Application.Common;

/// <summary>
/// 全系統共用的 JSON 設定（api.md §1）：camelCase、enum 以字串、時間一律 UTC 並以 <c>Z</c> 結尾。
/// API 回應、Idempotency 儲存的回應本文、事件 payload 都使用同一組設定，確保重送時內容與第一次一致。
/// </summary>
public static class JsonDefaults
{
    /// <summary>已凍結的共用選項（Application 內部序列化 payload / 回應快照用）。</summary>
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>把共用設定套用到既有選項（ASP.NET Core 的 <c>JsonOptions.SerializerOptions</c>）。</summary>
    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.Converters.Add(new UtcDateTimeConverter());
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Apply(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>
/// <see cref="DateTimeOffset"/> 一律輸出 UTC、毫秒精度、<c>Z</c> 結尾（<c>2026-09-21T02:15:30.123Z</c>）。
/// System.Text.Json 預設會輸出 <c>+00:00</c>，前端與 api.md 都要求 <c>Z</c>。
/// </summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}

/// <summary><see cref="DateTime"/>：Local 轉 UTC；Unspecified 視為已是 UTC（資料庫讀出值）。輸出格式同上。</summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTime.SpecifyKind(reader.GetDateTimeOffset().UtcDateTime, DateTimeKind.Utc);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        writer.WriteStringValue(utc.ToString(UtcDateTimeOffsetConverter.Format, CultureInfo.InvariantCulture));
    }
}
