using System.Security.Cryptography;
using System.Text;
using MiniFactory.Api.Errors;

namespace MiniFactory.Api.Security;

/// <summary>X-Api-Key 的種類（api.md §1.1）：端點只接受指定的那一把，拿另一把一律 401。</summary>
public enum ApiKeyKind
{
    /// <summary>前端操作：建單、設定情境、告警結案。</summary>
    Operator,

    /// <summary>設備回報（POST /api/equipment-events，Phase 2 加入）。</summary>
    Simulator,
}

/// <summary>端點 metadata：此端點需要哪一種 API Key（由 <see cref="ApiKeyMiddleware"/> 檢查）。</summary>
public sealed record ApiKeyRequirement(ApiKeyKind Kind);

/// <summary>
/// 安全設定。Key 只從設定 / 環境變數取得（<c>Security__OperatorApiKey</c>、<c>Security__SimulatorApiKey</c>），
/// 不寫進版控，也不寫進日誌。
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public string? OperatorApiKey { get; set; }

    public string? SimulatorApiKey { get; set; }

    /// <summary>Key 的最小長度（字元）。32 字元的隨機字串（例如 <c>openssl rand -hex 16</c>）才足以抵抗線上猜測。</summary>
    public const int MinKeyLength = 32;

    /// <summary>Key 至少要有幾種不同字元：擋掉 32 個相同字元、<c>1234…</c> 之類的低亂度字串。</summary>
    public const int MinDistinctChars = 10;

    // 占位值關鍵字：比對前先轉小寫並移除 - _ 與空白，所以 Change-Me、CHANGE_ME、change me 都會命中
    private static readonly string[] PlaceholderTokens =
    [
        "changeme", "changeit", "replaceme", "replacethis", "yourkey", "yourapikey", "placeholder", "example",
    ];

    /// <summary>
    /// 啟動時檢查；設定錯誤直接讓程序失敗，而不是帶著一個沒保護（或可被猜中）的 API 上線。
    /// 錯誤訊息只說明是哪個設定、違反什麼規則，<b>絕不包含 Key 的內容，也不帶出目前長度</b>。
    /// 呼叫點只有 <see cref="ApiKeyValidator"/> 的建構子（見該類別說明）。
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OperatorApiKey))
        {
            throw new InvalidOperationException("缺少設定 Security:OperatorApiKey（環境變數 Security__OperatorApiKey）。");
        }

        ValidateKey("Security:OperatorApiKey", OperatorApiKey);

        // Simulator Key 留給 equipment-events（Phase 2）；沒設定時，需要 Simulator Key 的端點一律 401
        if (string.IsNullOrWhiteSpace(SimulatorApiKey))
        {
            return;
        }

        ValidateKey("Security:SimulatorApiKey", SimulatorApiKey);

        if (string.Equals(OperatorApiKey, SimulatorApiKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Security:OperatorApiKey 與 Security:SimulatorApiKey 不可相同（兩把 Key 必須分開）。");
        }
    }

    private static void ValidateKey(string settingName, string key)
    {
        // HTTP 標頭值會被去除前後空白，含前後空白的 Key 永遠驗不過，只會造成難以排查的 401
        if (!string.Equals(key, key.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{settingName} 不可含前後空白（HTTP 標頭會去除前後空白，這把 Key 永遠驗證不過）。");
        }

        if (key.Length < MinKeyLength)
        {
            throw new InvalidOperationException($"{settingName} 長度不足：至少需要 {MinKeyLength} 個字元。請改用隨機產生的長字串。");
        }

        var normalized = new string(key.Where(c => c is not ('-' or '_' or ' ')).ToArray());
        if (PlaceholderTokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{settingName} 看起來是範例用的占位值（例如 change-me），請改用隨機產生的 Key。");
        }

        if (key.Distinct().Count() < MinDistinctChars)
        {
            throw new InvalidOperationException($"{settingName} 亂度不足：至少需要 {MinDistinctChars} 種不同字元。請改用隨機產生的 Key。");
        }
    }
}

/// <summary>
/// 以固定時間比較驗證 API Key（<see cref="CryptographicOperations.FixedTimeEquals"/>）。
/// 先各自做 SHA-256 再比對：兩邊長度固定為 32 bytes，比較時間不會洩漏 Key 的長度或前綴相符程度。
/// <para>
/// <see cref="SecurityOptions.Validate"/> 只在這裡的建構子呼叫一次（options 工廠不呼叫）：
/// 驗證器是唯一真正使用 Key 的地方，把檢查放在它的建構子，任何建構路徑（DI、測試、日後其他入口）都不可能
/// 拿到未通過檢查的 Key；啟動時由 <c>StartupConfigurationCheck</c> 解析驗證器來快速失敗。
/// </para>
/// </summary>
public sealed class ApiKeyValidator
{
    private readonly byte[]? _operatorHash;
    private readonly byte[]? _simulatorHash;

    public ApiKeyValidator(SecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _operatorHash = HashOrNull(options.OperatorApiKey);
        _simulatorHash = HashOrNull(options.SimulatorApiKey);
    }

    public bool IsValid(ApiKeyKind kind, string? provided)
    {
        var expected = kind == ApiKeyKind.Operator ? _operatorHash : _simulatorHash;
        if (expected is null || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), expected);
    }

    private static byte[]? HashOrNull(string? key)
        => string.IsNullOrWhiteSpace(key) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

public static class ApiKeyEndpointExtensions
{
    public const string HeaderName = "X-Api-Key";

    /// <summary>
    /// 標記端點需要 API Key。實際檢查在 <see cref="ApiKeyMiddleware"/>（讀端點 metadata）。
    /// 不用 IEndpointFilter 的原因：endpoint filter 在參數繫結<b>之後</b>才執行，格式錯誤的 body 會先得到 400，
    /// 沒帶 Key 的請求應該一律先得到 401。
    /// </summary>
    public static TBuilder RequireApiKey<TBuilder>(this TBuilder builder, ApiKeyKind kind)
        where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new ApiKeyRequirement(kind));
}

/// <summary>驗證端點 metadata 指定的 X-Api-Key；失敗回 401 Problem Details（不記錄 Key 內容）。</summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, ApiKeyValidator validator, ILogger<ApiKeyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requirement = context.GetEndpoint()?.Metadata.GetMetadata<ApiKeyRequirement>();
        if (requirement is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // 多個 X-Api-Key header 值視為無效，不去猜哪一個才對
        var values = context.Request.Headers[ApiKeyEndpointExtensions.HeaderName];
        var provided = values.Count == 1 ? values[0] : null;

        if (!validator.IsValid(requirement.Kind, provided))
        {
            // 記錄來源 IP 供事後追查暴力猜測；絕不記錄 Key 內容（含嘗試的值）
            logger.LogWarning(
                "API Key 驗證失敗 Method={Method} Path={Path} Kind={Kind} KeyProvided={KeyProvided} RemoteIp={RemoteIp}",
                context.Request.Method, context.Request.Path, requirement.Kind, !string.IsNullOrEmpty(provided),
                context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ProblemResponseWriter.WriteAsync(
                context,
                ProblemFactory.Create(context, ProblemTypes.Unauthorized, "缺少或不正確的 X-Api-Key"),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
