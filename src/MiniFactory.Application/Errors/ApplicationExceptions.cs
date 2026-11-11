namespace MiniFactory.Application.Errors;

// Application 層的「預期內」業務失敗，交由 API 的全域例外處理器轉成 Problem Details（api.md §2.1）。

/// <summary>欄位驗證失敗 → 400 validation-failed。</summary>
public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("One or more validation errors occurred.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>資源不存在 → 404 not-found。</summary>
public sealed class NotFoundException(string resource, string identifier)
    : Exception($"{resource} '{identifier}' 不存在")
{
    public string Resource { get; } = resource;

    public string Identifier { get; } = identifier;
}

/// <summary>同 Idempotency-Key 但請求內容不同 → 409 idempotency-conflict。</summary>
public sealed class IdempotencyConflictException(string idempotencyKey)
    : Exception($"Idempotency-Key '{idempotencyKey}' 已存在但內容不同")
{
    public string IdempotencyKey { get; } = idempotencyKey;
}

/// <summary>order_no 唯一約束觸發 → 409 order-no-conflict。</summary>
public sealed class OrderNoConflictException(string orderNo)
    : Exception($"訂單編號 '{orderNo}' 已存在")
{
    public string OrderNo { get; } = orderNo;
}
